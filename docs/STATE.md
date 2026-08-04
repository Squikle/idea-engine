# STATE — implementation status & conventions

> **Purpose:** the single resume-point. Any human or AI session picking this project up
> reads this file first, then [ARCHITECTURE.md](ARCHITECTURE.md) (target design) and
> [RUNBOOK.md](RUNBOOK.md) (how to run things). Keep this file terse and current —
> updating it is part of every phase's definition of done.

## Where the project actually is (2026-08-04, v0.31.0)

The phased plan below became reality faster than the phases: the full loop ships.

**Shipped:** 9 sources (HN, RedditRSS+archaeology, 4chan, Lemmy, Bluesky, YouTube
search + Shorts complaint-mining, GDELT news, /mine AI-memory mining;
Etsy/Pinterest/Reddit-OAuth pending third-party approvals) → triage (nano, budget-capped)
→ signals with glance lines → ideation (builder+skeptic, 13 playbook lenses, long-tail
signal blending) → durable jobs (/drop /research /dig, restart-safe checkpoints, queue UX,
retry/+$5 cards) → deep research (plan→search→advocate-vs-skeptic debate→judge, multi-round
closure, page reading, arbitrage valuation, builds ON TOP of previous reports) → appeals
(opus, auto on suspicious kills) → owner workflow (verify/kill/promote/note/re-research,
inline keyboards everywhere) → /sweep re-eval with reasoning versioning (ReasoningMilestones)
→ /audit leak checks → relations (nano dup/variant links) → concern-ledger distillation
(v0.29: concerns re-adjudicated every round, appeals adjust scores, ⌛ staleness markers)
→ partner seat + /origin + /models runtime switching (v0.30) → right-hand agentic chat
(brain proposes, code executes after audit approval) + /mine + AI-kill auto-appeals (v0.31)
→ autopilot (10:00 ideation ×5 sessions, 13:30 mine, 21:00 digest, Ontario time) → multi-track pinned
status board (auto-renders any reporting track) → budget firewall (stage/daily/monthly caps,
bumps, full ledger).

**Not yet:** embeddings/pgvector actually used (column exists), eval harness/golden set
(ADR-0002 promise), Pi/VPS deployment hardening (launchd/compose autostart, backups+rclone),
web dashboard/mind-map, Tier-2 shop sources (keys pending), Bluesky/YouTube quota tuning,
**phone capture rig** (TikTok/Reels text capture — design approved, build instructions in
[PHONE-RIG.md](PHONE-RIG.md), `SourceKind.PhoneRig=14` reserved, adapter not yet written).

**Watch:** GDELT rate-limiter penalty-boxes aggressively; adapter now uses 10s pacing,
zero retries, and a per-cycle circuit on the first 429. If `✓ Gdelt` keeps storing 0
across several cycles, check `raw_items where source=4` and consider longer pacing.

## Pending decisions / blockers

- **Reddit Data API approval** — Reddit requires explicit approval since June 2026
  (see ADR-0004 update). Developer request ticket to be/was submitted by owner;
  until granted, Phase 1 uses the Reddit **RSS adapter** (feed position = score proxy).
- **Etsy app approval pending** — keystring saved (correct shape) but returns
  "not active" until Etsy reviews the app. Adapter builds the day it activates.
- **Pinterest trial access pending** — app created, token GENERATED AND SAVED in
  .env, but every v5 endpoint (even user_account) returns "consumer type not
  supported" until the trial review completes. Re-test on approval email; Trends
  may additionally need standard access — request from the app page if so.
- **Parked:** Best Buy (rejects free-email signups). Optional queue: AliExpress
  affiliate, eBay dev keys.
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
- **Outward-facing form texts** (API access applications, developer forms, support
  tickets): always run through the humanizer pass - first person, no em dashes, no
  AI-parallelisms, hobbyist voice, honest. Owner's standing rule.

## For AI agents resuming work

1. Read this file, then **docs/DEVELOPMENT.md (the change protocol - mandatory)**,
   then [PIPELINE.md](PIPELINE.md) (stage map: code vs AI vs models vs storage),
   then `git log --oneline -20` and `CHANGELOG.md`.
2. `docker compose up -d db && dotnet build && dotnet test` — must be green before starting.
3. Work in small commits; update this file + relevant docs before finishing a phase.
4. Never ask the owner to paste secret values into chat; reference `.env` variable names.
