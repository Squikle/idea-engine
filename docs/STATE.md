# STATE — implementation status & conventions

> **Purpose:** the single resume-point. Any human or AI session picking this project up
> reads this file first, then [ARCHITECTURE.md](ARCHITECTURE.md) (target design) and
> [RUNBOOK.md](RUNBOOK.md) (how to run things). Keep this file terse and current —
> updating it is part of every phase's definition of done.

## Phase status

- [x] **Phase 0 — Skeleton**: solution, projects, Serilog worker, compose (pgvector),
      Dockerfile, docs, first unit tests. _Completed 2026-08-01._
- [ ] **Phase 1 — MVP loop**: Reddit + HN + 4chan adapters → prefilter → triage (cheap LLM)
      → store → daily Telegram digest. Eval harness v1 + golden set. Budget guard.
- [ ] **Phase 2 — Ideas & validation**: clustering, synthesis, screen stage, agentic deep
      validation (Brave + fetcher), scoring gates, hot alerts, idea commands, bake-off.
- [ ] **Phase 3 — Breadth & trends**: GDELT, YouTube, Product Hunt, Etsy/eBay adapters;
      topic timeseries; weekly trend report; monthly retrospective re-scorer;
      Reddit deletion-compliance job.
- [ ] **Phase 4 — Pi deployment**: arm64 deploy, backups (pg_dump + rclone), watchdog,
      load report + VPS recommendation.
- [ ] **Phase 5 — Later**: web dashboard, validation-service integrations, promo experiments.

## What exists right now

| Piece | State |
|---|---|
| `IdeaEngine.Core` | `Sources/` contracts (`ISourceAdapter`, `RawItem`, `RawComment`, `SourceKind`, `SourceFetchOptions`), `Common/ContentHasher` |
| `IdeaEngine.Infrastructure` | empty shell (fills in Phase 1) |
| `IdeaEngine.Worker` | Serilog two-stage init, `StartupSummaryService` heartbeat, appsettings |
| `tests/IdeaEngine.Tests` | `ContentHasherTests` (6 tests) |
| Docker | `docker-compose.yml` (db: pgvector/pg17 on localhost:5433; app: profile `app`), multi-arch `Dockerfile` |
| Secrets | `.env` (gitignored) from `.env.example`; API keys not yet issued |

## Pending decisions / blockers

- **Reddit Data API approval** — Reddit requires explicit approval since June 2026
  (see ADR-0004 update). Developer request ticket to be/was submitted by owner;
  until granted, Phase 1 uses the Reddit **RSS adapter** (feed position = score proxy).
- **Account setup by owner** — Telegram ✅ (bot live, chat id wired), OpenRouter ✅
  (key valid; ⚠ expires 2026-08-09, owner to remove expiry), Reddit ⏳ (approval gate),
  Brave ⏳.
- **Deep-validation model** — decided by bake-off in Phase 2 (see ADR-0002), not by default.
- **EF Core vs Dapper** — decide at Phase 1 start; leaning EF Core + Npgsql + pgvector plugin
  (migrations + productivity; raw SQL escape hatch where hot).

## Conventions

- **Commits:** Conventional Commits (`feat:`, `fix:`, `chore:`, `docs:`, `test:`, `refactor:`).
- **Versioning:** semver in `Directory.Build.props` + point-by-point entry in
  `CHANGELOG.md` for every user-visible change. Bump before the release commit.
- **Style:** file-scoped namespaces, primary constructors where natural, `var` freely,
  warnings are errors. `.editorconfig` is authoritative.
- **Tests:** every behavioral change ships with tests; `dotnet test` must be green before
  commit. Prompt/model changes must keep the eval harness green (Phase 1+).
- **Docs:** ADR for every significant decision (short, immutable). ARCHITECTURE diagrams
  updated when the shape changes. This file updated every phase.
- **Secrets:** only in `.env`/user-secrets. Never in code, config committed to git, or AI chats.
- **XML doc comments:** on public contracts where intent isn't obvious from the name. No ceremony.

## For AI agents resuming work

1. Read this file, then `git log --oneline -20`.
2. `docker compose up -d db && dotnet build && dotnet test` — must be green before starting.
3. Work in small commits; update this file + relevant docs before finishing a phase.
4. Never ask the owner to paste secret values into chat; reference `.env` variable names.
