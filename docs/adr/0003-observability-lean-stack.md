# ADR-0003: Observability — lean stack, no ELK

**Status:** accepted · **Date:** 2026-08-01

## Context

The system runs unattended on a Pi; silent failure means "no Telegram messages and no
idea why". Full ELK/Graylog (or hosted equivalents) costs RAM/money far beyond the value
for a single-user app.

## Decision

- **Serilog** structured logs → console + rolling files (14 days).
- **Telegram admin alerts** for warnings/errors — deduplicated + rate-limited (same error
  at most once/hour) so a flapping source can't spam.
- **`pipeline_runs` table** — every stage run records counts/durations/errors/cost;
  surfaced via `/status` command and a one-line health footer in the daily digest.
- **Dead-man's switch:** healthchecks.io free-tier ping each cycle; if the whole host
  dies, an external service emails.
- **`ActivitySource` instrumentation now**, exporters later: if we ever want Grafana/
  Tempo/Loki, it bolts on without touching pipeline code.
- Dev-only: optional Seq container profile for comfortable log browsing.

## Consequences

- (+) ~zero extra RAM/cost; failure visibility where the operator already is (Telegram).
- (−) No fancy log search in prod — acceptable; files + SQL cover the realistic cases.
