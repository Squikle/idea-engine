# COMPETITORS.md — is building idea-engine even worth it?

> Owner asked straight: "does what we're doing make sense, or does something better exist?"
> Verified 2026-08-04 (live fetches where noted). Update when the landscape shifts.

## The landscape

| Product | What it does | Overlap with us | State |
|---|---|---|---|
| **GummySearch** (verified) | Reddit pain-mining: discover/search/analyze communities for complaints and buying intent | Was the closest thing to our sourcing layer | **CLOSED 2025-11-30.** The closest competitor shut down — its users are orphaned; its playbook (pain taxonomies per community, "buys despite complaints" framing) is worth stealing |
| **ValidatorAI** (verified) | Free single-shot AI validation: paste idea → score + advice; behavioral stats across 300k founders; idea generator | Their "validate one idea in 60s" ≈ our skeptic gate, one-shot and shallower | Alive, pivoting into "founder behavior analytics" newsletters. No sourcing, no iteration, no evidence-grounded web research per idea |
| **DimeADozen** (unverified) | Paid AI validation reports (~$20-40/report): market size, competitors, GTM | ≈ one /research run, prettier, single-shot | Alive per marketing pages. No concern ledger, no re-adjudication, no owner notes |
| **Exploding Topics** (unverified) | Trend detection from search-volume deltas; $39+/mo | Complements rather than competes: trends ≠ pains | Alive. Their trick — surfacing *rate of change*, not absolutes — is adoptable in our scoring someday |
| Generic "idea generator" GPT wrappers | Prompt → 10 ideas | Zero grounding, zero memory | Commodity |

## What NOBODY else does (our actual moat, such as it is)

1. **Continuous multi-source ingestion → judgment pipeline.** They validate what YOU bring; we also go find the pains (8 live sources + /mine + archaeology backfill).
2. **Iterative distillation with memory.** Concern ledger re-adjudicated across rounds, appeals that correct scores, owner notes the judge MUST address, verdicts that build on prior reports. Every commercial tool is single-shot.
3. **Adversarial multi-model judgment.** Builder (sonnet) vs skeptic (deepseek) vs judge (sonnet+evidence) vs court (opus) — different vendors, so agreement means something.
4. **Owner-in-the-loop as a first-class mechanic** — notes, appeals, verify/kill/promote, right-hand chat with audit-gated writes. Their users read a report; ours argues back and the system digests it.
5. **Cost discipline**: full pipeline runs on ~$1-3/day; a DimeADozen habit would cost more for less depth.

## Honest weaknesses vs them

- ValidatorAI's 300k-founder behavioral dataset is something we can never have.
- Polished PDF reports (DimeADozen) look better than Telegram cards for sharing with others.
- Exploding Topics' search-volume time series beats our "evidence-age" heuristic for trend timing.

## Tricks adopted / to adopt

- [x] "Buys despite complaints" sentiment class (GummySearch) — already in signals.
- [x] Pain-angle taxonomy for /mine (GummySearch's community taxonomies, generalized).
- [ ] Trend-delta scoring: weight signals by growth rate, not just volume (Exploding Topics).
- [ ] "Customer feedback simulator" (ValidatorAI): a cheap pre-research stage where nano
      roleplays 5 target users reacting to the pitch. Candidate for a future playbook.

## Verdict

Worth continuing. The overlap zone (single-shot validation) is commoditized and cheap;
the compounding zone (continuous sourcing + iterative judgment + owner memory) is empty —
and the closest sourcing competitor just died. This is a personal deal-flow engine, not a
SaaS to sell; the alternative isn't "buy a tool", it's "do nothing and forget ideas in
notes apps".
