# Changelog

Versioning: semver `MAJOR.MINOR.PATCH` — MAJOR: architectural/breaking shifts ·
MINOR: new capability (source, stage, command) · PATCH: fixes/tuning.
The version lives in `Directory.Build.props` and shows in the startup banner.
Every release gets a short point-by-point entry here, newest first.

## 0.25.1 — 2026-08-02

- 🗒 marker on /ideas lines when an idea carries your notes (re-research reminder)
- /notes — all ideas with notes (count + latest clip); /notes 12 — full history
  with timestamps
- /find lego sorting — fuzzy idea search: pg_trgm similarity (typo-tolerant,
  instant, $0) + nano semantic fallback when the cheap pass finds nothing
- New playbook: apifirst 🔌 - sell shovels in a gold rush, stay upstream/man-in-
  the-middle (Airbnb owns no apartments, Uber no cars); package data/tools as
  API/MCP for AI agents and vibe-coders; expensive incumbent APIs are demand
  proof. Judge now weighs an upstream variant for every idea (+ ReasoningMilestone)
- Resumed drop jobs check the verdict from the DB - a skeptic-killed drop can't
  sneak into research through the restart path either

## 0.25.0 — 2026-08-02

- WATCHDOG: every job now has a hard runtime box (research 12m · drop 18m ·
  dig 10m, configurable) - a single stalled network call can never freeze the
  queue again (the job-#37 incident); timeouts fail the job with a 🔁 retry card
- Cancel works on RUNNING jobs: /cancel N and ✖️ buttons signal the job's own
  token - stops at the next step (spent tokens are lost, job → canceled)
- PageFetcher tarpit fix: whole fetch (headers AND body) boxed to 15s -
  ResponseHeadersRead + slow-drip servers could previously hang the body read
  forever with no timeout
- State-machine fix: done/failed transitions only apply to RUNNING jobs -
  held/failed/canceled can no longer be silently overwritten to "done"
- Drop chain stops wasting money on corpses: skeptic-killed drops skip web
  research with an override hint (/research N) instead of auto-validating a
  dismissed idea

## 0.24.1 — 2026-08-02

- "model output unparseable" is dead vocabulary. Every AI failure now says WHAT
  and WHAT TO DO: HTTP status + provider message ("HTTP 402 Insufficient
  credits — top up at openrouter.ai → Credits"), truncation ("cut at N tokens,
  finish=length"), or a preview of the non-JSON reply. Root cause of today's
  incident: the OpenRouter prepaid balance ran dry and the client reported the
  402 refusals as parse failures
- Credit exhaustion (402) is now its own flow: jobs go ⏸ held with a dedicated
  card explaining it's the WALLET, not our caps (no bump button - bumping can't
  help), with a "topped up — resume all" button; re-probes every 4h regardless
- Truncated replies auto-retry once with a 1.5x token budget (shaping, research
  synthesis, dig mapping)
- Error completions never pollute the ledger (0-token rows suppressed)
- /costs shows the live OpenRouter wallet: $ left of $ deposited, with a
  top-up warning under $2

## 0.24.0 — 2026-08-02

- Budget stops no longer fail jobs: they go ⏸ HELD and AUTO-RESUME at cap reset
  (00:00 UTC ≈ 20:00 Ontario) - the "wall of failed jobs after the cap" incident
  can't repeat; manual mass-retry is dead
- ONE coalesced cap card per day (instead of a card per starved job) with
  💸 +$5 & resume all / ▶️ resume without bump
- Bumping (+$5 button or /bump) now releases ALL held jobs automatically and
  reports how many resumed
- /queue job control: ⏸ held section with resume time, ✖️ cancel buttons per
  waiting/held job, 🔁 retry-all-failed and ▶️ resume-all rows; /cancel 7
  command (running jobs finish - no mid-flight kill by design)
- Non-budget failures keep the per-job retry card (those are real errors)

## 0.23.0 — 2026-08-02

- Status board desync fixed structurally: dig ⛏ / sweep 🔄 / audit 🧾 now report
  as tracks with live state, last result and next scheduled run (audit weekly,
  sweep monthly) - and the board AUTO-RENDERS any track that ever reports, so a
  future operation can never be forgotten again
- docs/DEVELOPMENT.md: the change-protocol handbook for any developer or AI
  model continuing without this chat's context - golden rules, run/verify
  commands, per-change sync checklists (command/stage/long-op/source/verdict
  logic), the sync map of "what forgets what", cost philosophy, owner
  preferences that are law
- docs/STATE.md refreshed to current reality (shipped vs not-yet) and now
  points resuming agents at the handbook first

## 0.22.1 — 2026-08-02

- /ideas default (top) now includes ☠️ ideas killed by ESTIMATE only (never
  researched = just the skeptic's opinion - research or /sweep can revive them);
  evidence-based kills stay in the ☠️ tab
- Uncertain tier arrows: 🤔↑ (⭐≥55%) · 🤔→ (40-54%) · 🤔↓ (<40%) - the wide
  bucket's inner spread is scannable at a glance
- 10 ideas per page (was 8)
- Judge decisiveness rules: "maybe" only for genuinely split evidence, scores
  must use the full 0-1 range instead of camping at 0.5 - fewer lazy
  "uncertain" verdicts going forward

## 0.22.0 — 2026-08-02

- /sweep - the verdict-improvement pass (monthly auto too). Three-stage funnel,
  cost scales with certainty:
  0) FREE heuristics over every researched idea AND the never-researched
     backlog: missed reasoning upgrades, open questions, kill-despite-score
     tension, notes added after the verdict, appeal-flagged-shallow, age/origin
  1) one batched nano call (~$0.001) screens the shortlist: rerun / targeted / leave
  2) top 2 auto-queue as research jobs; the rest get a one-tap 🔎 button
- Reasoning versioning: research reports now record the engine version that
  produced them; a reasoning-milestone changelog (debate, arbitrage, page
  reading, individual judgment…) tells exactly which upgrades a verdict missed
- Re-research builds ON TOP: previous verdict, still-open questions, and the
  missed upgrades are injected into context - settled findings are not redone
- Long-tail justice in ideation: every signal pool now blends 20 random tail
  signals (below the top-80 cut) so a growing corpus can't bury old decent
  signals forever

## 0.21.0 — 2026-08-02

- New playbook: aiwave 🤖 - ride the AI capability wave. New model releases and
  rising AI startups are timing signals; wrap existing smart models into new
  form factors (PC-controlling agents a la OpenClaw, always-on background
  right-hand assistants, model X for niche Y). Auto-rotates into ideation,
  feeds advocate pivots; /ideate 3 aiwave to force it
- Two matching seeds added to the local idea inbox (#13 background right-hand
  agent, #14 new-model launch radar)

## 0.20.1 — 2026-08-02

- Reddit 429 handling fixed properly: subs are shuffled every cycle (under a
  mid-list rate limit the same tail subs used to starve forever), a 429 now
  waits 45s and retries once, and a second 429 opens a circuit - remaining
  subs defer to the next run with ONE honest summary line instead of a wall
  of per-sub failures. Nothing is lost: hot feeds persist for hours, order
  reshuffles, dedup is free
- Non-429 feed errors log the actual HTTP status; Polly timeout exceptions
  no longer take down the whole source for the cycle

## 0.20.0 — 2026-08-02

- ARCHAEOLOGY MODE: collection now mines the archives, not just fresh posts -
  HN pulls top stories from a random historical window each cycle (8mo-12yr
  back, 150+ pts), Reddit rotates 3 subs/cycle through all-time-top feeds,
  Lemmy samples random all-time-top pages (300+ score). Old threads with dense
  comments are opportunity gold; dedup makes re-scans free
- NO MORE TOURNAMENT: auto-research queues EVERY fresh candidate above an
  absolute 30% floor as its own durable job (up to 8/day) - relative ranking
  can never bury an idea; skipped/deferred ones are listed with reasons and a
  force hint
- Idea relations (semantic memory): every /drop gets a nano-model comparison
  against recent ideas; duplicate/variant/related links stored on BOTH sides,
  shown as 🧬 Related on cards - re-drops enrich instead of fragment
- Links never truncated: URLs render as short host-labeled hyperlinks
  everywhere (answers now carry [source] links); clipping can't cut a link
- Cleaner logs: no more "Research #N ·" prefix spam (header owns identity,
  which now shows the IDEA TITLE for research and drop jobs); step markers
  (1/4 plan → 2/4 search → 3/4 advocate → 4/4 judge)
- More keyboards: /idea card has Verify/Research/Appeal/Promote/Kill buttons;
  dig maps and /audit get "🔎 Research all N"; /ideas and cards carry action
  footers (all commands usable with the id)
- /status shows the effective daily cap incl. today's bump (base + bumped);
  /bump reply spells out new totals
- /ideas prints full titles (no mid-word wraps); 🔹 bullets replace "?" walls
- New playbook: datavalue 🧲 (captcha/PokemonGo pattern - free product,
  valuable byproduct)

## 0.19.0 — 2026-08-02

- /dig <topic> - niche excavation as a durable job: plans 4-6 sub-branches
  (products, apps, services, content, arbitrage angle), searches each, returns
  a 🟢🟡🔴 saturation map and spawns 0-4 candidate ideas (origin "dig") with a
  ready /research batch line; "no gaps worth spawning" is an honest outcome;
  works for pain roots too (/dig procrastination); new "dig" stage, $1/day cap
- /audit + weekly auto-audit: finds ideas that never reached research (with a
  ready batch command), failed jobs, unreviewed verdicts and silent auto-kills;
  nano reflection paragraph appended to journal/advice.md for the architect

## 0.18.0 — 2026-08-02

- Playbooks: operator wisdom as system behavior - 11 lenses (psych, absurd,
  nostalgia, toolabuse, arbitrage, copycat, reputation, content, gamify,
  gamble, pain); every ideation session auto-samples 1-2, /ideate 3 nostalgia
  forces one, /playbooks lists them; used lenses stored on ideas and shown
  in results and cards
- ARBITRAGE lens corrected per operator: platform (Apple Watch, niche OS),
  country/language (Ukraine/Canada first), audience, and missing-companion
  gaps of PROVEN products; hard rule wired into skeptic AND judge - "a
  competitor exists" is not a kill when the target dimension is uncaptured,
  and competition_gap stays HIGH in that case
- New idea categories: reputation (virality/stars metric) and content
  (audience metric) with right-goal judging rules in skeptic and judge
- Advocate receives the full lens list to fuel pivots
- journal/idea-inbox.md rewritten: concrete droppable pitches only (12),
  lenses are not inbox items

## 0.17.0 — 2026-08-02

- Debate research: an ⚖️ ADVOCATE pass now builds the strongest case FOR every
  idea (competitor gaps + at least 2 concrete pivots) before the judge
  synthesizes - the judge weighs advocate vs skeptic honestly and adopts pivots
  that survive evidence (fixes the reject-by-default bias)
- /note 5 your argument - argue with the machine: notes are stored on the idea,
  shown on the card, and the next /research must address each explicitly
- /appeal 5 - opus-class court of appeal reviews verdict-vs-evidence for depth
  and fairness, can overturn status (overturned ideas return to review inbox)
- Auto-appeal: no-go verdicts with ⭐score ≥50% trigger the second opinion
  automatically (new "appeal" stage, $1.50/day cap)

## 0.16.0 — 2026-08-02

- Research output shows BOTH numbers everywhere: ⭐N% opportunity strength +
  evidence N% research solidity (report header, progress final line)
- Human verification: /verify N, ✅ button on every research report (plus
  🔁 re-research, 🔥 promote, ☠️ kill buttons); /ideas defaults to "top
  unreviewed" (verified + dead hidden), new ✅ tab for reviewed ideas
- /research 20 21 24 — queue several ideas in one command
- /bump command + 💸 +$5 button on /status (raises today's caps; monthly stays)
- /queue now REPLIES to the live log of the running job (tap the quote to jump)
  and says so; ack cards remain the anchor for each job's own log
- journal/ is local-only now (idea inbox + advice log never reach GitHub);
  seeded journal/idea-inbox.md with 9 example ideas from the improvement session

## 0.15.0 — 2026-08-02

- Queue UX: /drop and /research post an ack card (job #, queue position); the
  live progress log arrives as a REPLY to that ack - tap the quote to jump
- /queue command: running (with runtime), waiting (positions), failed (last 3)
  with 🔁 retry buttons, done-in-24h count
- Budget-cap stops now post an actionable card: 🔁 Retry job + 💸 +$5 today
  (bumps today's stage AND global daily caps; monthly ceiling stays hard);
  bumping auto-re-queues the stopped job
- Stopped jobs are marked failed (retryable) instead of silently done; failure
  cards link the idea (/idea N) so a job is never just a number
- /ideas shows "⏳ N job(s) in flight — /queue" while work is pending

## 0.14.0 — 2026-08-02

- Durable job queue: /drop and /research are persisted jobs now - they survive
  restarts, re-queue automatically on startup (♻️ recovery notice), and resume
  from checkpoints (a shaped idea never re-runs shaping; at most one stage repeats)
- /ideas got inline keyboards: filter tabs (All · Top · 🔥 · 🤔 · 🌱 · ☠️) and
  page arrows - nothing hidden behind "+N more" anymore
- /ideas default = full list by number; /ideas top = best first
- /help explains scores: ⭐ research-graded vs ≈ skeptic estimate; score =
  ingredients, status = decision (fatal flaws kill regardless of score)

## 0.13.4 — 2026-08-02

- Ideation result lines now carry the idea id (🟢 #14 [saas/e2] …) so /idea and
  /research work straight from the message
- No more mid-word cutoffs: word-boundary clipping with ellipsis across research
  reports, ideation lines, status details
- Oversized messages (over Telegram's 4096 limit) split at line boundaries and
  all chunks arrive - previously they were rejected and silently dropped; plus
  plain-text fallback on HTML rejection for every notification

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
