# ADR-0004: Source tiering, wide-topic policy, and compliance

**Status:** accepted · **Date:** 2026-08-01

## Context

Opportunities hide in niche communities (cosplay props, 3D printing, EDC…), not only in
business forums — collection must be wide and cheap. But some desired sources (TikTok,
Instagram Reels, Facebook Marketplace) prohibit scraping, and Reddit's Data API terms
require deleting user-deleted content.

## Decision

**Tier 1 (MVP):** Reddit (OAuth free tier), Hacker News (Algolia API, free), 4chan
(official read-only JSON API, free; aggressive prefilter).
**Tier 2 (Phase 3):** GDELT (news firehose + outlet whitelist), YouTube Data API
(trending + comments — the ToS-compliant substitute for TikTok/Reels signal),
Product Hunt (launch trends + existence checks), Etsy + eBay APIs (physical/3D-print
demand — substitute for FB Marketplace signal), Google Trends (official alpha if
granted; else defer/SerpAPI), arXiv/PubMed (science corroboration).
**Parked, with reasons:** TikTok/Reels/FB Marketplace — ToS-violating scraping, brittle,
proxy costs, account bans; revisit only via compliant paid aggregators with explicit
risk acceptance.

**Wide-topic policy:** subreddit/board lists are configuration, deliberately spanning
hobby/maker/craft niches; triage prompts are category-agnostic (SaaS to LED earrings).
Phase 3 adds community discovery (suggest new subreddits when a topic accelerates).

**Compliance:** weekly job re-checks stored Reddit items and hard-deletes user-deleted
content (raw text + quotes). Derived aggregates and our own idea write-ups are retained.
Commercial use of Reddit data beyond personal research requires their commercial terms —
flag before any productization of the digests themselves.

## Consequences

- (+) Wide, legal, cheap collection; substitutes preserve the intended signals.
- (−) No TikTok-native virality signal for now; YouTube comments are the proxy.
- (−) Weekly deletion sync consumes a small slice of the Reddit rate budget.

## Update 2026-08-01: Reddit approval gate + RSS interim

Findings while onboarding:

- Reddit's **Responsible Builder Policy** (June 2026) requires *explicit approval* for all
  Data API access; self-serve app creation at `prefs/apps` is rejected with a policy
  pointer. External readers file the developer request ticket
  (`ticket_form_id=14868593862164`, type: developer). Unauthenticated `.json` endpoints:
  HTTP 403 (verified empirically).
- **Interim source:** Reddit RSS (`/r/<sub>/hot.rss`) returns HTTP 200 and remains an
  official public syndication feed. Adapter constraints: gentle cadence (each sub every
  2–4 h, ≥2 s between requests, descriptive UA), no scores available → **feed position
  used as popularity proxy**. Replaced by the OAuth adapter when approval lands (same
  `ISourceAdapter` contract).
- Additional policy obligations adopted: **no AI training** on Reddit data (we do
  inference-only analysis); **no inference of sensitive user characteristics** — the
  pipeline analyzes content/topics and must never profile individual users (encode in
  triage prompt guardrails).
