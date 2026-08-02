# Architecture

> **This document describes the *target* architecture.** What is actually implemented
> at any moment is tracked in [STATE.md](STATE.md). Diagrams are updated as part of each
> phase's definition of done.

## 1. System context

```mermaid
flowchart LR
    subgraph Sources["External sources (read-only)"]
        R[Reddit API]
        HN[HN Algolia API]
        CH[4chan JSON API]
        T2["Phase 3: GDELT / YouTube /<br/>Product Hunt / Etsy / eBay / Trends"]
    end

    subgraph Host["Worker (.NET 10, Docker) — Mac → Pi → VPS"]
        W[idea-engine worker]
    end

    subgraph AI["AI providers (per-stage routing)"]
        OR[OpenRouter<br/>triage + screening]
        DV[Direct vendor API<br/>deep validation, batch 50%]
        ONNX[Local ONNX embeddings<br/>free, on-device]
    end

    BR[Brave Search API]
    PG[(PostgreSQL 17<br/>+ pgvector)]
    TG[Telegram bot<br/>long polling]
    HC[healthchecks.io<br/>dead-man's switch]

    Sources --> W
    W <--> PG
    W --> OR & DV & ONNX
    W --> BR
    W <--> TG
    W -.ping.-> HC
```

## 2. Pipeline (the value loop)

```mermaid
flowchart TB
    A[Ingest<br/>source adapters, scheduled] --> B[Normalize + prefilter<br/>heuristics only: score/length/dedup hash<br/>$0 — kills ~60-70% of volume]
    B --> C[Embed + near-dup detection<br/>local ONNX MiniLM → pgvector]
    C --> D[Triage — cheap LLM<br/>extract signals: pain / wish / demand<br/>commercial sentiment, novelty]
    D --> E[Cluster into topics<br/>pgvector similarity + daily timeseries]
    E --> F[Synthesize idea candidates<br/>group related signals]
    F --> G[Screen — mid LLM<br/>kill saturated/impossible/valueless]
    G --> H[Deep validation — strong LLM<br/>agentic: Brave search + page fetch<br/>competitor scan, effort, monetization,<br/>UA/CA fit, confidence + evidence links]
    H --> I{Score gates<br/>≥3 signals, ≥2 source types,<br/>competitor gap, confidence}
    I -- pass --> J[Telegram: hot alert / daily digest]
    I -- fail --> K[Parked / dismissed<br/>kept forever]
    K -. monthly retrospective:<br/>re-score when topic accelerates .-> H
    J --> L[Your feedback 👍/👎/🚀<br/>tunes thresholds over time]
```

**Stage handoff:** every stage reads its input from Postgres and writes its output back
(status columns + `FOR UPDATE SKIP LOCKED`). No in-memory queues across stages → crash-safe
on a Pi, resumable, and stages can later become separate processes without redesign
([ADR-0001](adr/0001-modular-monolith-postgres-queue.md)).

## 3. Solution structure & dependency rule

```mermaid
flowchart BT
    Core["IdeaEngine.Core<br/><i>domain, contracts, scoring, prompts</i><br/>no external dependencies"]
    Infra["IdeaEngine.Infrastructure<br/><i>EF Core + pgvector, source adapters,<br/>LLM router, Brave, ONNX, Telegram</i>"]
    Worker["IdeaEngine.Worker<br/><i>composition root, hosted services, scheduler</i>"]
    Tests["tests/*"]

    Infra --> Core
    Worker --> Infra
    Worker --> Core
    Tests --> Core
    Tests --> Infra
```

Rule: **dependencies point inward.** `Core` never references `Infrastructure`.
Everything external (an API, a database, a model provider) hides behind a `Core` interface
(`ISourceAdapter`, `IChatClient` routing, repositories), so parts stay movable.

## 4. Target data model

```mermaid
erDiagram
    sources ||--o{ raw_items : yields
    raw_items ||--o{ signals : "triage extracts"
    signals }o--|| topics : "clustered into"
    topics ||--o{ ideas : inspires
    ideas ||--o{ validations : "screen + deep"
    ideas ||--o{ deliveries : "sent via telegram"
    deliveries ||--o{ feedback : "user votes"

    raw_items {
        bigint id PK
        int source_kind
        text external_id UK "unique per source"
        text title
        text body
        text community "subreddit/board/channel"
        bigint score
        int comment_count
        text content_hash "cheap dedup"
        vector embedding "384d MiniLM"
        jsonb raw_payload "full original, compressed"
        text status "new|filtered|triaged|archived"
        timestamptz created_at
        timestamptz fetched_at
    }

    signals {
        bigint id PK
        bigint raw_item_id FK
        text kind "pain|wish|complaint|trend|demand"
        text summary
        text commercial_sentiment "wrist-strap rule applied"
        real novelty
        real confidence
        text model
        timestamptz created_at
    }

    topics {
        bigint id PK
        text label
        vector centroid
        jsonb daily_stats "timeseries: counts, engagement"
        timestamptz first_seen
        timestamptz last_seen
    }

    ideas {
        bigint id PK
        bigint topic_id FK
        text title
        text thesis
        text category "saas|app|3dprint|hardware|wearable|service|content"
        int effort_scale "1-5"
        jsonb evidence "signal ids + urls"
        jsonb scores "demand,gap,feasibility,monetization,distribution,risk"
        text status "candidate|screened|validated|hot|parked|dismissed"
        timestamptz created_at
        timestamptz updated_at
    }

    validations {
        bigint id PK
        bigint idea_id FK
        text stage "screen|deep"
        text verdict
        jsonb competitor_findings
        jsonb score_breakdown
        text model
        numeric cost_usd
        timestamptz created_at
    }

    ai_ledger {
        bigint id PK
        date day
        text stage
        text model
        bigint tokens_in
        bigint tokens_out
        numeric cost_usd
    }

    pipeline_runs {
        bigint id PK
        text stage
        timestamptz started_at
        timestamptz finished_at
        int items_in
        int items_out
        int errors
        numeric cost_usd
        text notes
    }
```

Storage posture: keep everything useful (raw payloads compressed, all signals/ideas forever).
Only compliance pruning applies (Reddit deleted-content sync,
[ADR-0004](adr/0004-source-tiering-and-compliance.md)). Budget: tens of GB/year is fine.

## 5. AI model routing

```mermaid
flowchart LR
    subgraph Router["ModelRouter (config-driven, per stage)"]
        direction TB
        RT["triage → cheap<br/>(default: gpt-5-nano via OpenRouter)"]
        RS["screen → mid<br/>(default: gpt-5-mini / deepseek-v4-flash)"]
        RV["validate → strong + batch<br/>(bake-off: sonnet-5 vs deepseek-v4-pro vs gpt-5.6-terra)"]
        RE["embed → local ONNX<br/>(fallback: openai 3-small)"]
    end
    EH["Eval harness<br/>golden set, cross-vendor labels<br/>re-run monthly → promote/demote models"]
    BG["Budget guard<br/>ai_ledger + daily caps per stage<br/>80% warn → 100% pause"]

    EH -. informs .-> Router
    Router --> BG
```

Details: [ADR-0002](adr/0002-llm-provider-strategy.md).

## 6. Deployment

```mermaid
flowchart LR
    subgraph Dev["Mac (now)"]
        D1["dotnet run + compose db"]
    end
    subgraph Pi["Raspberry Pi 4 (next)"]
        P1["compose: app + db profiles<br/>arm64 image, USB SSD,<br/>nightly pg_dump + rclone offsite"]
    end
    subgraph VPS["VPS (if/when needed)"]
        V1["same compose file<br/>restore from dump"]
    end
    Dev -->|"docker buildx (multi-arch)"| Pi -->|"pg_dump → restore"| VPS
```

Same image, same compose file everywhere; only `.env` and hardware change.
Migration = dump, restore, `docker compose up`.

## 7. Cross-cutting

- **Observability** ([ADR-0003](adr/0003-observability-lean-stack.md)): Serilog console+file,
  Telegram error alerts (deduplicated), `pipeline_runs` summaries surfaced via `/status`,
  healthchecks.io dead-man ping. `ActivitySource` instrumentation from day one → OTel-ready.
- **Resilience:** Polly retry/backoff per external API; circuit breaker per source;
  one source failing never stops the pipeline.
- **Budgets:** hard daily USD caps per stage, enforced before each call batch.
- **Config:** `appsettings.json` (structure) + environment variables from `.env` (secrets).
