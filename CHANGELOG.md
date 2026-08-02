# Changelog

Versioning: semver `MAJOR.MINOR.PATCH` — MAJOR: architectural/breaking shifts ·
MINOR: new capability (source, stage, command) · PATCH: fixes/tuning.
The version lives in `Directory.Build.props` and shows in the startup banner.
Every release gets a short point-by-point entry here, newest first.

## 0.13.3 — 2026-08-02

- /ideas grouped by stage with headers (🔥 Hot / 🤔 Uncertain / 🌱 New / ☠️ Killed,
  dead collapsed to 3) - scores sort within a group, so a killed 43% no longer
  outranks a living 26%
- Emoji rework: 🤔 uncertain (was 🟨), 🌱 new (was 🟡) - distinct at a glance
- /drop now queues behind a running ideation instead of asking to retry
- Builder prompts demand plain 3-6 word titles ("product photo split tester"),
  brand-name puns banned

## 0.13.2 — 2026-08-02

- /idea card is now a chronological 🛤 Journey: 1️⃣ Skeptic gate (labeled "AI
  opinion, no web evidence yet", verdict as "voted kill/advance") → 2️⃣ Web
  research marked "FINAL verdict (evidence-based, overrides the gate)" -
  answers "why does skeptic show after research" for good
- Score categories render as an aligned monospace 2x2 grid instead of one
  inline string
- ⚔️ disagreement line moved to the end of the journey where it belongs

## 0.13.1 — 2026-08-02

- Progress messages are now append-only step logs: every stage adds a bullet
  instead of replacing the text - each command leaves a readable history
  (long logs trim oldest steps, header kept)
- /ideas layout discipline: one pattern per line - status · monospace
  `#id score` column (aligned regardless of emoji widths) · title;
  operator ideas marked with a plain "— yours" suffix instead of the 🧑 marker

## 0.13.0 — 2026-08-02

- ONE score per idea, everywhere: ⭐ Score = weighted categories (💰 demand 35%,
  💵 willingness-to-pay 30%, 🔨 solo-buildability 20%, 🏪 competition gap 15%)
  × evidence confidence. Category values come from web research when it exists
  (evidence beats opinion), else from the skeptic - the source is always labeled
- /ideas: ⭐54% research-scored vs ≈54% skeptic estimate; /idea shows the full
  category breakdown line
- Competitors always surfaced with names AND urls: on the /idea card (up to 6,
  persistent), in research reports (up to 6), and the synthesis prompt now
  treats omitting a found competitor as a failed report

## 0.12.0 — 2026-08-02

- Verdict clarity: research "maybe" now maps to status `uncertain` 🟨 (was
  "validated" ✅ - a green mark on a coin-flip was misleading); existing rows
  migrated
- /idea card tells the pipeline story in order: Research (final) first, then
  Skeptic labeled "pre-research"; when they disagree an explicit ⚔️ line says
  "skeptic killed it, research kept it alive - treat as unproven" with the
  decision commands inline
- All confidences and ratings shown as percentages (90%, not 0.90)
- /kill 5 and /promote 5: operator override - your verdict beats any AI verdict

## 0.11.0 — 2026-08-02

- DEEP RESEARCH: closure-driven loop - unanswered questions trigger follow-up
  rounds (up to 3) that search the questions verbatim AND read the top result
  pages (not just snippets); remaining gaps reported as "🕳 Still open", never
  papered over. Single-pass shallow research was a scope shortcut, now gone
- Fix: /idea card showed the skeptic's original questions as "To research" even
  after research answered them - now shows answered count + genuinely open ones
- Multi-track status board: collect/analyze/ideate/research/digest each keep
  their own always-visible line (live detail when active, last result + next
  run when idle) - concurrent processes no longer overwrite each other
- /status: full funnel view - items new→queued→analyzed (junk/failed), signals
  total/24h, ideas by stage with await-research count, spend vs daily+monthly
  caps, recent runs

## 0.10.0 — 2026-08-02

- Bluesky source LIVE: app-password session auth (search requires it since 2026);
  pain-phrase mining active
- YouTube source LIVE: US+CA trending + top comments via official Data API
  (~50 of 10,000 free daily quota units per cycle); /collect youtube
- Chat facelift: consistent emoji vocabulary for fast scanning - 🟢/🔴 worker,
  📥 collect, 🧠 analyze, 💡 ideas, 🔎 research, 🧭 advisor, 💸 spend;
  signal kinds 🩹✨💰📈😤; idea statuses 🔥✅🟡☠️; verdicts 🟢🟡🔴;
  bolded ids, itemized sections, breathing room
- Reddit RSS: slower cadence (5s/feed) + single retry - stops the 429 hammering;
  Polly retry noise silenced from logs and alerts

## 0.9.0 — 2026-08-02

- AUTOPILOT: the machine now produces ideas on its own - daily ideation at 10:00
  Ontario (3 sessions) with auto-research of the best fresh candidate (rating
  gate, no money on weak ideas), and the daily digest at 21:00 Ontario
- Bootstrap: on startup with zero product ideas, ideation runs immediately
- Daily digest: collected per source, +signals with top-3 by value, ideas
  live/killed, research verdicts, 24h spend by stage - honest "nothing cleared
  the bar" when empty
- Ontario local time everywhere (status board, reports, schedules; DST-safe;
  IdeaEngine:TimeZone configurable)
- /ideas shows product ideas only - meta proposals moved to the journal count
- Error alerts: Warning+ log events reach Telegram, deduplicated 1/hour
- Retention/compliance job: Reddit content copies stripped after 30 days
  (ADR-0004 posture), old pipeline runs pruned after 90
- ResearchCoordinator: manual /research, /drop chain and autopilot share one
  single-flight research slot

## 0.8.0 — 2026-08-02

- Live progress: /ideate /drop /research /advise now show ONE message edited in
  place through the steps (shaping → skeptic → searching 3/6 → synthesizing),
  no step spam
- Advice journal: /advise proposals append to journal/advice.md - a read-only
  log the architect reads and turns into changes on request
- Fixed /help crash (raw angle brackets vs Telegram HTML) and reformatted it
- Patchnote messages properly formatted (unwrapped bullets, code spans)
- Any HTML-rejected reply auto-retries as plain text - formatting can never
  kill a command again

## 0.7.0 — 2026-08-02

- `/research <id>` - the final validation stage closing the loop: plans 4-8 web
  queries from the skeptic's open questions → Brave web search (free tier,
  1 req/s) → grounded synthesis with mandatory "not found in results" honesty
- Verdict moves the idea: go → HOT, maybe → validated, no-go → dismissed;
  full report (competitors with links, Q&A, differentiation, risks, next steps)
  stored in research_reports and delivered to Telegram
- `/idea <id>` shows the latest research verdict; `/ideas` shows HOT/ok markers
- New "research" stage under BudgetGuard ($2/day cap); ~$0.05-0.10 per report
- `/drop <pitch>` - submit YOUR OWN idea: shaped into structure (with 3-6 variant
  applications of the mechanic), skeptic-reviewed, then auto-chained into web
  research - the full incubation pass in one command (~3 min, ~$0.15)
- Research reports now include an MVP test (cheapest riskiest-assumption
  experiment, lean-startup style) and stronger related variants
- Ideas carry origin (ai | operator) and variants

## 0.6.0 — 2026-08-02

- `/best` now shows ≤10-word glance lines instead of long article-ish summaries:
  generated by the cheapest model (nano) in one batched call, cached per signal
  forever (`signals.glance`), so repeat views cost nothing
- New "glance" AI stage with its own daily cap ($0.10) under the BudgetGuard;
  cold /best ≈ $0.0002, warm /best = $0
- Fallback: if the glance call fails or is capped, the full summary is shown

## 0.5.0 — 2026-08-02

- Startup patchnotes: on version change the bot messages the new CHANGELOG section
  (embedded in the binary, works in Docker; last version tracked in app_state)
- Scoring module: signal value = confidence x novelty-boost x commercial-sentiment
  weight (wrist-strap rule numerically); idea rating = weighted skeptic scores
  (demand/WTP/feasibility/differentiation) discounted by skeptic confidence
- Triage drain notifications: "+N signals" with top-3 by value after every drain
- `/best [n]`: top-valued signals of the last 7 days, tagged with ideas they fed
- `/idea <id>`: full trace card - thesis, skeptic verdict, research questions,
  cited evidence with links
- `/ideas`: ids + ratings, candidates first
- Ideation grounding: garage-scale rubric (1-3 people, apartment-buildable; no
  lawyer-department/factory/logistics-network ideas)
- Uncensored policy: models must not self-censor or kill on morality/legality -
  legal nuance is recorded as information; the operator filters manually

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
