# ADR-0002: LLM provider strategy — routed, eval-driven, two-tier accounts

**Status:** accepted · **Date:** 2026-08-01

## Context

AI is the dominant running cost. Model prices/quality shift monthly (e.g. Sonnet 5 intro
pricing ends 2026-08-31; DeepSeek V4 line updated 2026-07-31). The assistant building
this project is an Anthropic model — vendor recommendations must be structurally
de-biased, not taken on trust. Volumes differ per stage by ~100x.

## Decision

1. **Abstraction:** all calls go through `Microsoft.Extensions.AI` (`IChatClient`) behind
   a config-driven **ModelRouter** (per-stage provider+model+params). Swapping a model is
   a config change.
2. **Accounts:** OpenRouter (single key, ~5.5% fee) for high-frequency cheap stages and
   for eval breadth; a **direct vendor account** for the deep-validation stage to use
   that vendor's **Batch API (−50%)** — validation is overnight-friendly.
3. **Selection by measurement:** golden set labeled by cross-vendor consensus (two strong
   models from different vendors; disagreements adjudicated by the owner). Cheap models
   ranked by F1-per-dollar; validation models by blind owner ranking on pilot ideas.
   Re-run monthly; router updated accordingly.
4. **Budget guard:** `ai_ledger` records every call; hard daily USD caps per stage
   (warn 80%, pause 100%) + provider-side key limits as backstop.

## Consequences

- (+) No vendor lock-in; price drops are captured by config, not rewrites.
- (+) Bias controlled by process (measurements + owner adjudication), not intentions.
- (−) Two account types to manage; batch pipeline adds async complexity in Phase 2.
- Starting defaults (provisional until first eval): triage=gpt-5-nano, screen=gpt-5-mini
  or deepseek-v4-flash, validate=bake-off (sonnet-5 / deepseek-v4-pro / gpt-5.6-terra),
  embeddings=local ONNX MiniLM.
