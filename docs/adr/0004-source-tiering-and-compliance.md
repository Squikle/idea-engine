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
