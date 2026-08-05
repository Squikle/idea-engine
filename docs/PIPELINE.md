# PIPELINE.md — what runs where: code vs AI, models, storage

> One page to answer "what model is behind each step, what's deterministic code,
> and what data does it leave behind?" Companion to [ARCHITECTURE.md](ARCHITECTURE.md)
> (shape) and [DEVELOPMENT.md](DEVELOPMENT.md) (change rules). Models/prices are
> configuration defaults (`*Options.cs`, overridable via appsettings/env); this
> table is the intent, options classes are the truth.

## Stage map

| # | Stage (ledger name) | Code or AI | Model (default) | What it does | What it stores |
|---|---|---|---|---|---|
| 1 | ingest | **code** | — | 11 source adapters (HN, RedditRSS+archaeology, 4chan, Lemmy, Bluesky, YouTube+Shorts, GDELT, Product Hunt launches, App Store charts, StackExchange unanswered, + /mine) on 3h cycles + backfill; eBay Browse probe joins research for physical ideas when keys exist | `raw_items`, `pipeline_runs` row per adapter run |
| 2 | prefilter | **code** | — | heuristics: length, spam patterns, dupe URL/id checks, language | flags on `raw_items` (skipped items keep the reason) |
| 3 | triage | AI | `openai/gpt-5-nano` | batch-scores raw items for pain/idea potential 0–1; only survivors advance | score fields on `raw_items`, `ai_ledger` per call |
| 4 | signals (+glance) | AI | `openai/gpt-5-nano` | clusters survivors into named pain signals; glance = one-line human summary | `signals` (name, summary, item links, strength) |
| 5 | ideation (builder) | AI | `anthropic/claude-sonnet-5` | turns signal blends (incl. long-tail) into structured ideas through 1–2 rotating playbook lenses (13 lenses) | `ideas` (thesis/category/effort/target/monetization/variants), `ai_ledger` |
| 5b | ideation (skeptic) | AI | `deepseek/deepseek-v4-pro` | adversarial review, verdict advance/kill + kill reasons + category scores | `ideas.SkepticJson`, `ideas.ScoresJson`, status candidate/dismissed |
| 5c | relate | AI | `openai/gpt-5-nano` | links new idea to duplicates/variants/related among existing ideas | `ideas.RelatedJson` (both sides) |
| 6 | jobs | **code** | — | durable queue for drop/research/dig: checkpoints, watchdog timeouts, budget-hold, cancel, retry | `jobs` (kind, payload JSON, state, checkpoints, progress msg id) |
| 7 | research | AI + code | `anthropic/claude-sonnet-5` | plan → Brave searches (code) → page reads (code, 15s box) → advocate case → judge; multi-round until closure; builds ON TOP of previous reports + latest appeal + notes | `research_reports` (full ReportJson + EngineVersion), `research_artifacts` (SERPs, pages, advocate, raw synthesis), `ai_ledger` |
| 8 | appeal | AI | `anthropic/claude-opus-4.6` | reviews verdict-vs-evidence; auto-fires on no-go with ⭐≥50% AND on operator-drop skeptic kills (pre-research); can overturn status | `ideas.AppealJson`, status change, `ai_ledger` |
| 9 | dig | AI | `anthropic/claude-sonnet-5` | operator-directed topic excavation, spawns ideas | new `ideas` rows (origin=dig), `ai_ledger` |
| 10 | reeval (/sweep) | code + AI | heuristics + `openai/gpt-5-nano` | re-examines killed ideas under newest reasoning milestones; nano screen → research queue | status flips, queued jobs, `ai_ledger` |
| 11 | audit | AI | `openai/gpt-5-nano` | weekly leak check: promising signals/ideas that fell through cracks | report to Telegram, `ai_ledger` |
| 12 | advise | AI | `anthropic/claude-sonnet-5` | meta-review of pipeline health, suggestions | `journal/advice.md` via owner |
| 13 | mine | AI | `anthropic/claude-sonnet-5` | asks the model's trained memory for concrete pains via rotating angles or operator fantasies; reply-chat continues; 1 auto-run/day | anchor `raw_items` (source=15) + `signals`, `ai_ledger` |
| 14 | partner | AI | `anthropic/claude-opus-4.6` | ≤10-line blunt take: effort vs payoff, portfolio comparison, BUILD/PARK/FIX/KILL (button + /partner) | `ideas.PartnerJson`, `ai_ledger` |
| 15 | hand | AI + **code** | `anthropic/claude-opus-4.6` | right-hand chat over the whole db: read intents executed by code, write proposals audit-gated (owner taps Apply), code is the only executor | `app_state` session + pending, `ai_ledger` |
| — | budget | **code** | — | BudgetGuard: stage daily caps → global daily ($5+bumps) → monthly ($60) → per-call ceiling ($0.15; per-stage overrides, appeal $0.45) | `ai_ledger` (every call: stage, model, tokens in/out, USD, day), `app_state` (bumps) |
| — | delivery | **code** | — | Telegram bot: commands, inline keyboards, reply-chained chunking, pinned status board, autopilot (10:00 ideate / 21:00 digest Ontario) | message ids in `jobs`/`app_state` |

## Who decides what (judgment boundaries)

- **Code never judges content.** It gates on budget, dedup, length, timeouts.
- **nano judges cheaply and reversibly** (triage scores, screens, relations) — errors
  here cost a lost item, not a wrong verdict; /sweep exists to catch its false kills.
- **Verdicts belong to the debate + judge (research) and the court of appeal.**
  Individual judgment with absolute floors — never relative ranking (owner law).
- **The owner overrides everything**: /kill /promote /verify /note /research /appeal.

## Retro data you can chart today

- `ai_ledger` — every AI call ever: stage × model × tokens × cost × day. Cost curves,
  cost-per-idea, cost-per-surviving-idea.
- `pipeline_runs` — every ingest/triage batch: items in/out, errors, duration. Source
  yield over time; adapter health.
- `raw_items` — full raw corpus incl. triage scores and skip reasons. Re-triageable
  retroactively when prompts improve.
- `signals` — pain clusters with strength + member links.
- `ideas` — full judgment trail per idea: skeptic JSON, category scores, appeal JSON,
  notes, relations, playbook lens, origin, raw operator pitch (from v0.27.0), costs.
- `research_reports` — one row per research RUN (multi-research appends, EngineVersion
  stamped): verdict, confidence, competitors, Q&A with evidence URLs, searches/rounds/
  pages counters. Verdict-flip analysis across engine versions is possible.
- `jobs` — every drop/research/dig with timestamps and outcomes: queue latency,
  failure rates, watchdog kills.
- `research_artifacts` (v0.28.0) — the scaffolding, kept: per-query SERPs
  (title/url/snippet), page-read excerpts, the advocate's full case, and raw synthesis
  output when parsing failed (ReportId null = failed run). ~50–150KB of text per run;
  jsonb, SQL-minable. Enables retro niche mining ("which SERPs kept surfacing the same
  underserved audience?"), combining strong sides of related ideas, and judgment audits
  — without re-paying for AI.
- **Still not stored**: full fetched page bodies (only the excerpt the judge saw) and
  the judge's chain-of-thought (providers don't return it).

## Where to look next

- Change rules and owner laws: [DEVELOPMENT.md](DEVELOPMENT.md)
- Current status + blockers: [STATE.md](STATE.md)
- Ops (run, migrate, keys): [RUNBOOK.md](RUNBOOK.md)
- Reasoning history (what changed in judgment logic when): `src/IdeaEngine.Core/Common/ReasoningMilestones.cs`
