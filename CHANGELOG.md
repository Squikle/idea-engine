# Changelog

Versioning: semver `MAJOR.MINOR.PATCH` — MAJOR: architectural/breaking shifts ·
MINOR: new capability (source, stage, command) · PATCH: fixes/tuning.
The version lives in `Directory.Build.props` and shows in the startup banner.
Every release gets a short point-by-point entry here, newest first.

## 0.4.0 — 2026-08-02

- `/ideate [n]`: AI ideation sessions (1-10). Builder (claude-sonnet-5) proposes ONE
  idea grounded in collected signals with mandatory citations; Skeptic
  (deepseek-v4-pro, cross-vendor on purpose) attacks it; verdict stores the idea as
  candidate or dismissed - both kept forever
- `/advise`: meta session - AI reviews our own pipeline (sources, stages, stats)
  and proposes overlooked sources/improvements; stored as `meta` ideas
- `/ideas`: recent ideas, live and killed
- `ideas` table (evidence, skeptic review, scores, per-idea cost)
- BudgetGuard - financial firewall consulted before EVERY AI call: per-stage daily
  caps + global daily cap ($5) + global monthly cap ($60) + per-call worst-case
  ceiling ($0.15); block reasons surfaced in Telegram
- Unvetted ideas are never advanced: skeptic failure = dismissed with reason
- Generic OpenRouterChatClient; LLM-tuned resilience (default 10s attempt timeout
  would have killed long sonnet calls)
- Triage moved onto BudgetGuard; stop reasons in pause notifications

## 0.3.0 — 2026-08-02

- Triage stage: gpt-5-nano via OpenRouter extracts product-opportunity signals
  (pain/wish/demand/trend/complaint + commercial sentiment, novelty, confidence)
- `signals` table + prefilter (junk gate before any tokens are spent)
- Budget guard: per-stage daily USD cap, every call in `ai_ledger`
- Bot: `/analyze` (drain queue now), `/signals`, `/costs`
- TriageCoordinator: manual and scheduled analysis can't double-process
- Fix: reasoning-token budget too small truncated JSON (0-signals incident);
  now `MaxCompletionTokens=3000` + `ReasoningEffort=low` + parse diagnostics
- Log noise: Polly/HttpClient resilience chatter silenced

## 0.2.1 — 2026-08-02

- Fix: Reddit-format User-Agent rejected by strict header validation, which
  killed DI resolution of all new adapters (`TryAddWithoutValidation` now)
- Startup Telegram ping trimmed of fluff, then replaced by the status board

## 0.2.0 — 2026-08-02

- Sources: 4chan (official JSON API), Bluesky (pain-phrase search; dormant -
  endpoint needs auth), Lemmy (lemmy.world), Reddit via RSS (interim,
  feed-position score proxy) - joins HackerNews from 0.1.0
- Pinned live status message: edited in place through Collecting/Idle,
  flips to OFFLINE on shutdown/crash (AppDomain + host exit hooks)
- Bot commands: `/status` `/top` `/collect [source]` `/config` `/help`
- IngestionCoordinator: single-flight cycles shared by schedule and commands
- Ingestion cycle reports to Telegram (counts + top-5 highlights)

## 0.1.0 — 2026-08-01

- Solution skeleton: Core / Infrastructure / Worker + tests, warnings-as-errors
- Postgres 17 + pgvector via docker compose; EF Core schema:
  `raw_items`, `pipeline_runs`, `ai_ledger`
- HackerNews adapter (Algolia: front page + Ask HN + comments)
- Ingestion service: dedup by external id + content hash
- Serilog (console + rolling file), .env loader, Telegram notifier
- Docs: ARCHITECTURE (mermaid), RUNBOOK, STATE, ADR-0001..0004
