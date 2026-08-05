# AGENTS.md — start here (any model, any context)

idea-engine: a personal product-opportunity pipeline. It collects public internet
signals, triages them with cheap AI, fabricates and adversarially judges product ideas
with stronger AI, and delivers everything to the owner through a Telegram bot. One
owner (Mykhailo), garage-scale focus (1–3 person products), strict cost discipline.

## Mandatory read order (15 minutes, no exceptions)

1. **docs/STATE.md** — where the project actually is, blockers, conventions.
2. **docs/DEVELOPMENT.md** — the change protocol and OWNER LAWS. Violating these is
   the only real way to fail here.
3. **docs/PIPELINE.md** — stage map: what's code, what's AI, which model, what's stored.
4. Skim `git log --oneline -20` and CHANGELOG.md for what changed recently.

Reference when needed: docs/RUNBOOK.md (run/migrate/keys), docs/ARCHITECTURE.md
(target shape), docs/COMPETITORS.md (why this exists), docs/PHONE-RIG.md (future
capture rig), `src/IdeaEngine.Core/Common/ReasoningMilestones.cs` (judgment history).

## The owner laws you will actually hit

- Every user-visible change → CHANGELOG entry + semver bump in `Directory.Build.props`
  (the worker auto-announces new versions to Telegram on startup).
- No silent skips, no silent kills: everything skipped shows a reason and a force hint.
- Scores are EARNED through judgment — never hand-edit them; use appeals.
- Judgment-logic changes → append `ReasoningMilestones` (drives ⌛ staleness + /sweep).
- Uncensored prompts: no moralizing, edgy niches valid; the owner filters manually.
- Secrets live in `.env` only. Never in code, config, chats, or commits.
- Outward-facing text (API applications, support tickets) gets the humanizer pass:
  first person, hobbyist voice, no AI parallelisms.

## Where things go

| You're adding… | It goes… |
|---|---|
| a new source | `src/IdeaEngine.Infrastructure/Sources/<Name>/` — implement `ISourceAdapter`, add `SourceKind` enum value (NEVER renumber), register HttpClient+DI in `ServiceCollectionExtensions`, options section, follow the source checklist in DEVELOPMENT.md |
| a new bot command | `TelegramCommandService`: dispatch switch + `BotCommand` list + `BuildHelp()`; emit id-hints via `Ui.Cmd(name,id)` (compact tappable `/idea5` form) |
| a new AI stage | options class (Model/prices/DailyUsdCap/ReasoningEffort + `WithModel` clone) + resolve through `ModelRegistry` (stage name!) + `BudgetGuard.CheckAsync` + `ai_ledger` rows + add stage to `/models` catalog in the Worker |
| a long-running operation | a durable job kind: payload record in `JobService`, executor in `JobRunnerHostedService` (HEAVY lane = research/dig/drop, LIGHT = appeal/partner), timeout entry, budget-stop via `HandleStopAsync` |
| schema changes | entity + DbContext mapping (jsonb for JSON columns) + `set -a; source .env; set +a; dotnet ef migrations add <Name> --project src/IdeaEngine.Infrastructure` + `dotnet ef database update --project src/IdeaEngine.Infrastructure` |
| a runtime-tunable setting | `SettingsCatalog` (Infrastructure/Autopilot) — /config, the right hand and autopilot all read that single whitelist |
| judgment/prompt changes | the prompt file + `ReasoningMilestones` + changelog; verdict cards must stay honest (both numbers, full arguments, no "…" truncation — the notifier chunks) |

## The ship ritual (every change)

```bash
dotnet build           # warnings are errors
dotnet test            # must be green; add tests for behavior changes
# changelog entry + version bump in Directory.Build.props
pkill -9 -f IdeaEngine.Worker; sleep 2
: > logs/worker-console.log     # TRUNCATE FIRST - stale logs have fooled us before
# DETACHED launch (macOS has no setsid binary; nohup+disown still dies with the
# launching process group when an agent tool-call tears down - real incident):
python3 -c "import subprocess,os; subprocess.Popen(['dotnet','run','--project','src/IdeaEngine.Worker'], stdout=open('logs/worker-console.log','ab'), stderr=subprocess.STDOUT, start_new_session=True, cwd=os.getcwd())"
sleep 30
grep -q "<new version> started" logs/worker-console.log && ! grep -q FTL logs/worker-console.log
# verify the process survives YOUR OWN call boundary: ps in a SEPARATE call
git add -A && git commit && git push
```

## Hard-won lessons (cost real incidents)

- `grep | tail` exits 0 even when grep finds nothing — never gate a deploy on it.
  Always `grep -q` + truncated-fresh logs + version-stamped startup line.
- Resilience options validate at startup: `MaxRetryAttempts = 0` crashes the host;
  disable retries via `ShouldHandle = _ => PredicateResult.False()`.
- LLM JSON breaks in creative ways (trailing prose, raw control chars, missing `}`).
  `LlmJson.TryParse` has layered repairs + a nano re-emit fallback in research; store
  raw output as a `synthesis_raw` artifact when parsing fails — evidence over guesses.
- GDELT (and others) penalty-box aggressive clients: one 429 → stop the whole pass,
  zero retries, long pacing.
- Telegram hard-limits 4096 chars: ALL sends go through chunking (`MessageChunker`);
  never `_bot.SendMessage` raw for variable-length content.
- Worker launches MUST use `start_new_session=True` (python Popen) - `nohup ... & disown`
  from an agent's bash call dies with the call's process group (silent bot downtime).
- The AI-brain/code-executor split (right hand) is a LAW: models emit intents, only
  code touches the database, writes always show an audit card first.

## Run, debug, test

```bash
docker compose up -d db                     # Postgres 17 + pgvector on localhost:5433
set -a; source .env; set +a                 # env for migrations/CLI (worker loads .env itself)
dotnet build && dotnet test                 # 154 tests, warnings are errors
dotnet test --filter "FullyQualifiedName~LlmJson"   # one test class
# start the worker: see the ship ritual above (DETACHED launch, then verify)
tail -f logs/worker-console.log             # live log (Serilog console, local time)
grep -E "✓ |ERR|WRN|started" logs/worker-console.log   # health at a glance
docker compose exec -T db psql -U ideaengine -d ideaengine   # SQL console
# useful tables: ideas, signals, raw_items, research_reports, research_artifacts,
#                jobs, ai_ledger, pipeline_runs, app_state
```

Debugging flow issues: `pipeline_runs` has one row per stage execution (ingest:X,
ideate:session with sampled/cited ids in notes); `ai_ledger` explains every dollar;
failed LLM parses leave `synthesis_raw` artifacts. The bot itself is the best probe:
/status, /queue, /costs, /config, /models, /signals, and the right hand can query
anything conversationally.

## Porting to another host (Raspberry Pi / VPS / Linux)

The app is a single .NET worker + Postgres; nothing is macOS-specific.

1. Install .NET 10 SDK (arm64 builds exist for Pi 4/5; 2GB RAM is enough, the heavy
   lifting happens at OpenRouter) + docker compose, or a native Postgres 17 with the
   pgvector extension.
2. Copy the repo + `.env` (that file IS the identity: Telegram token, OpenRouter key,
   Brave, YouTube, Bluesky, optional eBay/Etsy/Pinterest).
3. `docker compose up -d db && set -a; source .env; set +a && dotnet ef database update
   --project src/IdeaEngine.Infrastructure` (fresh host = all migrations apply cleanly).
4. Timezone: autopilot schedules in America/Toronto via TimeZoneInfo — ensure tzdata
   is installed (Linux: `apt install tzdata`); no code change needed.
5. Run under systemd instead of nohup (survives reboots):

```ini
# /etc/systemd/system/idea-engine.service
[Unit]
Description=idea-engine worker
After=network-online.target docker.service
[Service]
WorkingDirectory=/home/pi/idea-engine
ExecStart=/usr/bin/dotnet run --project src/IdeaEngine.Worker
Restart=on-failure
RestartSec=10
[Install]
WantedBy=multi-user.target
```

6. Only one instance may run globally — two pollers on one Telegram token fight
   (409 conflicts). Stop the old host before starting the new one.
7. Budget/keys/announcements continue unchanged: state lives in Postgres + .env,
   so a host move is: stop worker → pg_dump/restore (or move the docker volume) →
   copy .env → migrate → start.

## Environment

macOS, .NET 10, Postgres 17 + pgvector in docker (`docker compose up -d db`,
localhost:5433). Secrets in `.env`. The bot is `@squ_idea_engine_bot`; the owner's
chat id is wired. All model calls go through OpenRouter with layered budget caps —
never add a direct provider dependency.
