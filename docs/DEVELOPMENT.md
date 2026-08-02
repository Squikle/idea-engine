# DEVELOPMENT.md — the change protocol

> **Audience: any developer or AI model continuing this project without prior context.**
> Read [STATE.md](STATE.md) first (what exists), then this file (how to change it safely).
> The #1 failure mode of this codebase is not bugs — it's **desync**: adding a capability
> and forgetting one of the surfaces that must reflect it. Every section below ends with a
> sync checklist. Run it mechanically.

## Golden rules

1. **Every user-visible change**: entry in `CHANGELOG.md` (newest first, terse bullets) +
   version bump in `Directory.Build.props` (semver: capability=minor, fix/tuning=patch).
   The worker auto-announces the new section to Telegram on next start.
2. **Warnings are errors.** `dotnet build` must be 0-error, `dotnet test` all-green before
   any commit. Analyzer complaints are fixed, not suppressed (exception: generated
   migrations are exempt via `Migrations/.editorconfig`).
3. **Money moves only through BudgetGuard.** No AI call without a prior
   `BudgetGuard.CheckAsync` and a post-hoc `ai_ledger` row. No exceptions.
4. **Never ask the owner to paste secrets into chat.** `.env` variable names only;
   verification via `set -a; source .env; set +a` + a live call that prints no values.
5. **The owner's chat is the UI.** Telegram HTML, `Ui` emoji vocabulary, `TextClip`/`Linkify`
   for clipping (never cut a word or a URL), percentages not fractions, headers carry the
   subject (idea title), no per-line prefix spam, keyboards wherever an obvious action exists.
6. **Uncensored policy**: models must never kill/avoid ideas on morality/edginess; legal
   nuance is recorded as information. Operator filters manually. (See prompts.)
7. **Outward-facing form texts** (API applications etc.): humanize — first person, no
   em-dashes, no AI-parallelisms, hobbyist voice (owner's standing rule).

## How to run / verify

```bash
docker compose up -d db                      # postgres+pgvector on localhost:5433
set -a; source .env; set +a                  # export env INCLUDING for child processes
dotnet build && dotnet test                  # must be green
nohup dotnet run --project src/IdeaEngine.Worker > logs/worker-console.log 2>&1 & disown
grep -E "started|ERR|FTL" logs/worker-console.log | head   # sanity
pkill -INT -f IdeaEngine.Worker              # graceful stop (status flips OFFLINE)
```

Migrations: `dotnet ef migrations add <Name> --project src/IdeaEngine.Infrastructure`
then (env exported!) `dotnet ef database update --project src/IdeaEngine.Infrastructure`.

## Architecture in 60 seconds

Modular monolith, 3 projects. `Core` = contracts/scoring/no deps. `Infrastructure` =
adapters (sources, OpenRouter LLM, Brave, Telegram, EF+pgvector), services (triage,
ideation, research, dig, audit, reeval), durable `jobs` queue. `Worker` = hosted services
(ingestion scheduler, triage poller, autopilot, job runner, retention, telegram commands)
+ composition root. Stage handoff via Postgres statuses (`FOR UPDATE SKIP LOCKED` style
claiming); in-process single-flight via coordinator semaphores. Pipeline:

```
sources → raw_items → prefilter → triage(nano) → signals → ideate(builder+skeptic, playbook lenses)
→ ideas → research(plan→search→advocate→judge, rounds, page reads) → verdict/status
→ appeal(opus, auto on suspicious kills) → owner (verify/kill/promote/note → re-research)
Cross-cutting: /dig (niche→spawned ideas), /sweep (re-eval old verdicts), /audit (leak check),
relations (nano links dup/variant/related), daily digest, budget firewall.
```

## Checklist: adding a bot command

- [ ] Route in `TelegramCommandService.HandleUpdateAsync` switch
- [ ] Implementation (return string → auto-reply; return `string.Empty` when you send
      directly, e.g. with a keyboard; long ops = fire-and-forget `Task.Run` + progress log)
- [ ] `SetMyCommands` menu entry + `/help` text (keep the flow map coherent)
- [ ] Buttons? add callback data `verb|arg` + handler in `HandleCallbackAsync`
- [ ] Only `_adminChatId` is honored — never widen
- [ ] CHANGELOG + version

## Checklist: adding an AI stage (any new LLM call site)

- [ ] Options class + `IdeaEngine:Ai:<Stage>` section in appsettings (model, prices/MTok, cap)
- [ ] `BudgetGuard.CheckAsync(stage, cap, worstCall, plannedSpend)` BEFORE calls
- [ ] `ai_ledger` row per call (tokens, cost, stage, model) — `/costs` picks it up automatically
- [ ] Parse via `LlmJson.TryParse` (fence-tolerant) with one re-ask on garbage; never crash
- [ ] Stop reasons surface to the owner (⛔ card with 🔁 retry + 💸 +$5 when cap-related)
- [ ] Prompt rules: JSON-only reply, garage-scale lens, uncensored policy, right-goal judging
- [ ] CHANGELOG + version

## Checklist: adding a long-running operation

- [ ] Durable? → job kind in `JobRunnerHostedService` switch + payload record in `JobService`
      + enqueue from command (ack card pattern: "queuing…" → edit with job#/position)
- [ ] Progress: `IProgressNotifier.StartAsync` (reply to ack `OriginMessageId`), append-only
      steps `n/m stage…`, `SetHeaderAsync` once the subject (title) is known,
      `SaveProgressIdAsync` so `/queue` can link the live log
- [ ] **Status track**: `Tracks` constant + `BeginAsync/UpdateAsync/EndAsync` around the run
      (the board auto-renders any reporting track — but core ops belong in `Tracks.All`);
      scheduled ops also `ScheduleAsync(next)` (see `PublishSchedulesAsync`)
- [ ] `/queue` `JobLabel` case for the payload
- [ ] Failure → `FailWithButtonsAsync` (marks job failed=retryable, actionable card)
- [ ] Single-flight if concurrent runs are unsafe (coordinator semaphore or `OperationGates`)
- [ ] CHANGELOG + version

## Checklist: adding a source

- [ ] `SourceKind` enum member (**never renumber existing**), `SourceKindParser` alias
- [ ] Adapter: `ISourceAdapter`, partial-failure tolerant (log+skip, never throw through),
      politeness delays, descriptive UA, rate-limit aware (429 → backoff + circuit,
      see RedditRss), `Options.Since` respected for fresh paths
- [ ] **Backfill path** where the API allows (archives are gold; dedup makes re-scans free);
      backfill items exempt from `Since`
- [ ] DI: options section + typed HttpClient + `AddStandardResilienceHandler` + register as
      `ISourceAdapter`
- [ ] Keys: `.env.example` placeholder + RUNBOOK signup steps + `IsConfigured` no-op skip
- [ ] CHANGELOG + version. `/collect <alias>` works automatically once registered

## Checklist: touching idea/verdict logic

- [ ] If it changes HOW verdicts are made (prompts, scoring, stages): **append a
      `ReasoningMilestones` entry** — this is what `/sweep` uses to find stale verdicts.
      Forgetting this silently breaks re-evaluation.
- [ ] New idea fields: entity + DbContext config + migration; jsonb read via
      `LlmJson.SafeDeserialize`; render on `/idea` card AND anywhere relevant (`/ideas` line?)
- [ ] Status strings are contracts: `candidate|uncertain|validated(legacy)|hot|dismissed`;
      `Verified` is an ORTHOGONAL filter flag (reviewed-by-owner), never a verdict
- [ ] Score semantics: ⭐ = weighted categories × evidence confidence (research overrides
      skeptic); one formula, `IdeaScores.Compute` — never invent a second number

## Sync map (what forgets what)

| You changed… | Don't forget… |
|---|---|
| bot command | menu, /help, callbacks |
| AI stage | appsettings, BudgetGuard, ledger, stop-cards |
| long op | track on board, /queue label, progress, retry card |
| source | enum, parser alias, .env.example, RUNBOOK, backfill |
| verdict logic | **ReasoningMilestones**, /help score explainer |
| idea fields | migration, card, list, sweep heuristics? |
| schedules | `PublishSchedulesAsync`, /status |
| anything user-visible | CHANGELOG + version bump |

## Cost philosophy

Caps: per-stage daily → global daily ($5 base + owner bumps) → monthly hard wall ($60) →
per-call sanity ceiling. Cheap models for volume (nano: triage/glance/relations/screens),
sonnet-class for synthesis (ideation/research/dig), opus only for appeals. Multi-stage
funnels: free heuristics → batched nano → expensive only for survivors (see ReevalService —
copy that pattern). Owner's stance: quality over cost at the final stages, never miss
opportunities to save pennies at the top of the funnel.

## Owner preferences that are LAW

- Transparency: nothing silently skipped — skips are listed with reasons + force hints
- Individual judgment: no relative ranking may bury an idea (absolute floors only)
- Both numbers always: ⭐ opportunity + evidence confidence
- Killed-by-estimate ≠ killed-by-evidence (revivable, shown in default /ideas)
- Every long op: visible, queued, resumable after restart (checkpoints), retryable
- Archives/backlogs are re-minable (thresholds change → /sweep exists)
