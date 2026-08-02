# RUNBOOK — setup & operations

## Prerequisites

- .NET SDK 10.x (`dotnet --list-sdks`)
- Docker Desktop (or docker engine + compose v2)
- git

## First run (dev)

```bash
cp .env.example .env    # edit: set POSTGRES_PASSWORD to something random
docker compose up -d db
dotnet run --project src/IdeaEngine.Worker
```

Expected: startup banner in console, log file under `logs/`. Stop with Ctrl-C.

Verify db: `docker compose ps` → `idea-engine-db (healthy)`.

## Secrets

- Runtime secrets live in `.env` (docker) — gitignored. Local-dev alternative:
  `dotnet user-secrets` (Worker project already has a `UserSecretsId`).
- **Never** commit `.env`, never paste key values into AI chat sessions. When an AI
  session needs a key configured, it will tell you the variable name; you edit `.env`
  yourself.
- If a key ever leaks (pasted somewhere, committed accidentally): revoke + reissue at the
  provider, update `.env`, restart.

## Accounts (one-time setup, ~20 min total)

### 1. Telegram bot (~5 min, free)

1. In Telegram, open **@BotFather** → `/newbot`.
2. Name: anything (e.g. `Idea Engine`). Username: must end in `bot` (e.g. `myk_idea_engine_bot`).
3. BotFather replies with an **HTTP API token** → put it in `.env` as `TELEGRAM_BOT_TOKEN`.
4. Open your new bot's chat and send it any message (e.g. `hi`) — this lets us discover
   your chat id.
5. Get your chat id:
   ```bash
   source .env && curl -s "https://api.telegram.org/bot${TELEGRAM_BOT_TOKEN}/getUpdates" | grep -o '"chat":{"id":[0-9-]*' | head -1
   ```
   Put the number in `.env` as `TELEGRAM_ADMIN_CHAT_ID`.
6. Optional hygiene: `/setjoingroups` → Disable (bot is personal, DM-only for now).

### 2. OpenRouter (~5 min, pay-as-you-go)

1. Sign up at `openrouter.ai` (Google/GitHub login works).
2. **Credits** → add $10 to start (goes a long way at triage prices).
3. **Keys** → Create key, name `idea-engine` → `.env` as `OPENROUTER_API_KEY`.
4. Recommended: set a monthly spend limit on the key ($30) as a second safety net
   beneath our in-app budget guard.

### 3. Reddit script app (~5 min, free tier)

1. Log in to Reddit → `https://www.reddit.com/prefs/apps` → **create another app…**
2. Type: **script**. Name: `idea-engine`. Redirect URI: `http://localhost:8080` (unused
   but required).
3. After creation: the string under the app name is the **client id**; the **secret** is
   labeled. → `.env` as `REDDIT_CLIENT_ID` / `REDDIT_CLIENT_SECRET`.
4. Set `REDDIT_USERNAME` in `.env` (used in the mandatory descriptive User-Agent:
   `macos:idea-engine:vX.Y (by /u/<you>)`).
5. Free tier: 100 queries/min per client — our usage stays far below.

### 4. Brave Search API (~5 min, free tier)

1. Register at `https://api-dashboard.search.brave.com` (credit card required for
   identity even on free plan; not charged).
2. Subscribe to the **Search** plan — $5 free credits monthly ≈ 1,000 searches.
3. Create API key → `.env` as `BRAVE_API_KEY`.

## Operations

### Logs

- Console (live) + `logs/idea-engine-YYYYMMDD.log` (14-day retention).
- Phase 1+: errors are also pushed to your Telegram (deduplicated, rate-limited).

### Database

```bash
docker compose up -d db                  # start
docker compose exec db psql -U ideaengine -d ideaengine   # shell
docker compose down                      # stop (data persists in named volume)
docker volume rm idea-engine_pgdata      # DESTROY all data (careful)
```

### Migrations (EF Core, local tool)

```bash
dotnet ef migrations add <Name> --project src/IdeaEngine.Infrastructure

# Apply. NOTE: plain `source .env` is NOT enough - it creates unexported shell vars
# that child processes (dotnet) never see. Use set -a to auto-export:
set -a; source .env; set +a
dotnet ef database update --project src/IdeaEngine.Infrastructure
```

Generated migration files are analyzer-exempt via `src/IdeaEngine.Infrastructure/Migrations/.editorconfig`.

### Backup / restore (activated in Phase 4; design ready)

- Nightly `pg_dump | gzip` to `./backups` (7 daily / 4 weekly rotation) via a compose
  `backup` profile; offsite push with rclone (Backblaze B2 or Google Drive).
- Restore procedure will be documented AND rehearsed here when activated.

### Deploy to Raspberry Pi / VPS (Phase 4; design ready)

- Same compose file; build multi-arch: `docker buildx build --platform linux/arm64,linux/amd64 ...`
- Pi specifics: 64-bit OS, USB SSD strongly recommended (SD cards die under Postgres),
  `user:` mapping for the bind-mounted `logs/` dir.

## Troubleshooting

| Symptom | Check |
|---|---|
| Worker exits immediately | `logs/` tail; usually bad `.env`/appsettings |
| `db` unhealthy | `docker compose logs db`; port 5433 collision → change `DB_PORT` |
| No Telegram messages (Phase 1+) | token/chat id in `.env`; bot must have received ≥1 message from you |
| AI spend spike (Phase 1+) | `/costs` in Telegram; budget guard pauses stages at 100% daily cap |
