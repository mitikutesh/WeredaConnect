# Buyer Intent-Matching — Roadmap & Deferred Work

**Status**: Living document
**Last updated**: 2026-07-07
**Related**: [`specs/contracts/match-api.md`](../contracts/match-api.md), [`specs/contracts/match-events.md`](../contracts/match-events.md)

This records the state of the buyer intent-matching algorithm after the
2026-07-07 improvement pass, and the remaining work that is **gated** on
external prerequisites or accumulated data. It exists so the deferred items are
tracked as intentional follow-on work — not forgotten, and not shipped
half-built.

## What shipped (2026-07-07)

The matcher stays a transparent, hand-tuned weighted sum (the right choice for
the catalogue size, low-end devices, and solo maintenance — confirmed against
how ride-hailing/marketplace and agritech players rank). Improvements landed:

- **UX**: "why this matched" reason chips + "similar crop" label; intent
  delete / mark-fulfilled wired; orders fulfil their intent; optional budget;
  inline validation; error-vs-empty states; quintal/kg consistency.
- **Scale/correctness**: removed the newest-1000 candidate cliff (price ceiling
  pushed into the GSI query + 20-page walk); cheaper stream fan-out (one push
  per buyer per crop); persisted delivery coords so push and `/match` score
  identically; aligned online/offline tie-breaks; `totalMatches` so badges
  don't saturate at the page limit; full-substitute-map + ranking parity tests.
- **New signals**: farmer **reliability** (listing-conversion track record,
  server-injected, neutral default) folded into the weighted sum; buyer
  **`neededByDate`** captured and consumed as trip date-fit.
- **Foundations**: **match-event logging** (`match_event` sync entity →
  `MatchEvents` table, 90-day TTL) — the training corpus for a future learned
  ranker.

Current weights (server `MatchScoring` ≡ mobile `MatchScorer`, parity-tested):
type 0.30 · price 0.20 · geo 0.18 · quantity 0.12 · freshness 0.10 ·
reliability 0.10.

## Deferred / gated work

### 1. FCM push — client half (GATED: operator)
Server already scores new crops against active intents and publishes FCM via SNS
(`CropMatchStreamFunction`). The mobile client half — `firebase_messaging`
dependency, device-token registration/upload, and a deep link from a
notification tap into `MatchResultsScreen(intentId)` — is **not built** because
adding the `google-services` Gradle plugin breaks the Android build without a
`google-services.json`, which only the operator can provide.
**Prerequisite**: Firebase project + `google-services.json` + an SNS platform
application (see `.ai/push-notifications-setup.md`). **Then**: wire the client
per that runbook. Until then, re-engagement is the in-app "N new" badge on
pull-to-refresh.

### 2. Bandit weight tuning (GATED: data)
Replace hand-tuned weights with Thompson sampling over a small set of weight
presets: the server tunes on logged conversions and ships the best weight
vector; devices stay deterministic and explainable.
**Prerequisite**: enough `MatchEvents` conversion data to distinguish presets
(order of thousands of interactions). **Consumes**: the Tier-3 logging that just
started accruing.

### 3. Learned ranker via m2cgen (GATED: data)
Train a GBDT/logistic ranker on `MatchEvents` (positives = order_created,
negatives = shown-not-opened), export **dependency-free C# *and* Dart from one
model** via m2cgen — which also structurally eliminates the two-hand-written-
scorers drift risk. AOT-safe (no runtime/reflection); keep the weighted sum as
the explanation fallback.
**Prerequisite**: ~tens of thousands of logged interactions; an offline
training/export job. Revisit after #2.

### 4. Geohash / H3 supply-discovery index (DEFERRED: design)
A geohash GSI on crops would speed *area-browse* ("crops near a point, any
type"). It is **intentionally not** added to the matching path: matching is
type-driven (PK=`cropType`) and geo is a **soft** ranking signal, so a geohash
*hard* pre-filter would wrongly drop far-but-valid matches and change matching
semantics. Add it only when an area-browse feature needs it — with its own query
consumer — not for matching.

### 5. Reliability from order outcomes (FUTURE: richer signal)
Reliability today is a listing-conversion proxy (fraction of a farmer's prior
listings that reached `sold`), computed escrow-free at crop-create time. A
richer signal — accept rate, on-time completion — would come from the
order/escrow lifecycle. Deferred because it must not couple the matcher to the
money path; revisit as a separate, carefully-reviewed change.

## Explicitly rejected (do not pursue)
P2P / DHT discovery (needs peers online simultaneously; escrow + role-visibility
need a central authority — and "replicate to device, match on-device" is
already done), ONNX on-device inference (runtime/reflection unfriendly to AOT),
learned embeddings (opaque, heavy; the small crop taxonomy + substitute table
already encode similarity), and stable-matching/Hungarian assignment (only
relevant if the product moves from buyer-pull browsing to system-push
assignment of scarce contested supply).
