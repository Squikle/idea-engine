# idea-engine

Personal product-opportunity discovery pipeline. Continuously reads public internet
sources (Reddit, Hacker News, 4chan; later news/YouTube/marketplaces/trends), extracts
real problems people have, synthesizes product ideas (SaaS, apps, 3D-printables,
wearables, hardware), validates the promising ones with AI + web research, and delivers
a curated daily digest to Telegram. Quality over quantity: if nothing clears the
evidence bar, it says so.

## Status

Phase 0 (skeleton). See [docs/STATE.md](docs/STATE.md) for the live status,
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for design, [docs/RUNBOOK.md](docs/RUNBOOK.md)
for setup/operations, and [docs/adr/](docs/adr/) for why things are the way they are.

## Quickstart (dev, macOS/Linux)

```bash
cp .env.example .env          # then edit: set POSTGRES_PASSWORD at minimum
docker compose up -d db       # Postgres 17 + pgvector on localhost:5433
dotnet run --project src/IdeaEngine.Worker
```

Tests:

```bash
dotnet test
```

Fully containerized (used for Pi/VPS deployment later):

```bash
docker compose --profile app up -d --build
```

## Layout

```
src/IdeaEngine.Core/            domain + pipeline contracts (no external deps)
src/IdeaEngine.Infrastructure/  adapters: sources, LLM providers, db, telegram
src/IdeaEngine.Worker/          host, DI, scheduling
tests/                          unit tests (+ integration & prompt evals from Phase 1)
docs/                           architecture, runbook, state, ADRs
```

## Secrets

Real keys live only in `.env` (gitignored) or `dotnet user-secrets`. Never in the repo,
never pasted into AI chats — see [docs/RUNBOOK.md#secrets](docs/RUNBOOK.md#secrets).
