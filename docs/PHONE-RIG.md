# PHONE-RIG.md — short-video complaint harvester (TikTok / Reels / Shorts)

> **Audience:** a developer or AI model implementing this WITHOUT the original chat
> context. Read [STATE.md](STATE.md) and [DEVELOPMENT.md](DEVELOPMENT.md) first.
> Status: **designed, not implemented**. The owner approved this path over paid
> aggregators (~$30-100/mo deferred until the app proves profitable).

## 1. Why this exists

Short-video comments are the densest complaint stream on the internet ("why does no
app do X", "I hate that Y"), but TikTok/Instagram have no usable public APIs and
video-frame AI analysis is 10-50x our text budget. The rig sidesteps both: a real
phone auto-scrolls the apps while a controller captures ONLY TEXT — captions,
hashtags, author, comments — and feeds it into idea-engine's normal pipeline as one
more source. Quantity over polish; dedup and triage do the filtering downstream.

ToS note: this automates a personal account on personal hardware at human-ish pace.
It is gray. Keep volumes modest, use burner accounts, expect occasional bans.

## 2. Owner's available hardware & constraints

- Old **Android** device(s) ← **the capture device. Android is mandatory for v1**
  (adb gives free automation + UI-text dumps). 
- Old **iPhone** — NOT for v1: no adb equivalent; automation needs a Mac running
  XCUITest/Appium with a paid-ish toolchain, and UI text extraction is far weaker.
  Revisit only if the Android path dies.
- **Raspberry Pi 4** ← the 24/7 controller/sink (drives the phone, stores JSONL,
  ships it to idea-engine).
- Future: idea-engine moves to a VPS → rig must not assume same-host; the hand-off
  contract below is file/HTTP based on purpose.

## 3. Architecture

```
[Android phone]  ──USB (adb)──  [Raspberry Pi controller]        [idea-engine worker]
 TikTok/IG/YT apps               python driver:                    FileDropAdapter (Phase B)
 burner account                  - swipe/scroll via adb            watches DROP_DIR/*.jsonl
 screen always-on                - uiautomator XML dumps           → raw_items(Source=PhoneRig)
