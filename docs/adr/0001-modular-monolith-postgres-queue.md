# ADR-0001: Modular monolith with Postgres-backed stage queue

**Status:** accepted · **Date:** 2026-08-01

## Context

The pipeline has ~8 sequential stages with wildly different costs (free heuristics vs
paid LLM calls). It must run 24/7 on a Raspberry Pi 4 (4GB), survive crashes/restarts
without losing work, and later scale to a VPS without redesign. Options considered:
microservices + message bus (RabbitMQ/Redis Streams), in-process channels only, or a
single process with durable state in Postgres.

## Decision

One deployable worker process. Stage handoff via Postgres: each entity carries a
`status` column; stage workers claim batches with `SELECT … FOR UPDATE SKIP LOCKED`.
In-process concurrency uses `System.Threading.Channels` *within* a stage only.
Project boundaries (Core/Infrastructure/Worker) enforce the dependency rule so stages
can be extracted into separate processes later if ever justified.

## Consequences

- (+) Crash-safe and resumable by construction; zero extra infrastructure on the Pi.
- (+) Full pipeline history queryable in SQL (feeds analytics for free).
- (+) Migration path preserved: a stage becomes a service by pointing it at the same DB.
- (−) Postgres does queue duty — fine at our volume (<10k items/day), revisit if 100x.
- (−) Polling latency between stages (seconds) — irrelevant for a daily-digest product.
