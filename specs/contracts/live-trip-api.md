# Live Trip API Contract

## Version
1.0.2

## Parties
- **Mobile / Web** (client): the trucker's phone writes check-ins through `POST /sync`; the buyer's, farmer's and trucker's apps read the track; the admin app reads the trail.
- **Backend** (server): validates and stores check-ins, serves the track and the trail, runs the side effects.
- **Infra**: the `{prefix}-trip-locations` table and the API routes.

Design: `specs/features/live-trip.md`. Related: `sync-protocol.md` (the entity type), `delivery-verification-api.md` (statuses, `gpsPickup`), `notifications-api.md` (`truck_nearby`), `order-lifecycle.md` (contact per party, `dropoffNote`).

---

## 1. Check-ins: `location_ping` via `POST /sync`

A create-only entity. One operation per check-in or sharing event.

```json
{
  "operationId": "uuid-v4",
  "idempotencyKey": "trucker-789:location_ping:trip-101#1793090520000:create:1793090520000",
  "entityType": "location_ping",
  "entityId": "trip-101#1793090520000",
  "action": "create",
  "clientVersion": 1,
  "data": {
    "tripId": "trip-101",
    "kind": "fix",
    "source": "hourly",
    "capturedAt": "2026-10-27T08:42:00.000Z",
    "latitude": 8.7523,
    "longitude": 38.9785,
    "accuracyM": 35,
    "mocked": false
  },
  "timestamp": "2026-10-27T08:42:00.000Z"
}
```

| Field | Type | Required | Rule |
|---|---|---|---|
| `tripId` | string | yes | the trip the truck is driving; `entityId` is `{tripId}#{capturedAt in epoch ms}` |
| `kind` | enum | yes | `fix`, `paused`, `resumed`, `declined` |
| `source` | enum | `fix` only | `hourly`, `scan`, `near_stop`, `manual`, `arrived`, `start` |
| `capturedAt` | ISO 8601 UTC | yes | when the phone took the fix or the driver acted; at most 2 minutes in the future, at most 7 days old |
| `latitude`, `longitude` | number | `fix` only | latitude 3 to 15, longitude 32.5 to 48.5 |
| `accuracyM` | number | `fix` only | 0 to 5,000 |
| `mocked` | boolean | no | the platform flagged a mock location |
| `pausedUntil` | ISO 8601 UTC | `paused` only | after `capturedAt`, at most 18 hours later (an overnight pause from mid afternoon fits) |

Accepted when the caller is the trip's trucker and the trip has a delivery that is `pickedUp` or `inTransit`, or that left those statuses after `capturedAt`: a `delivered` or `disputed` delivery left at its `confirmedAt`, a `cancelled` delivery that was picked up (it has a `pickedUpAt`) at its `cancelledAt`. A delivery cancelled before pickup counts for nothing. The deliveries are read from the deliveries' `TripIdIndex`. At most 20 `fix` operations per trip per clock hour (UTC) of `capturedAt`; events do not count.

**Results** (the `sync-protocol.md` statuses): `success` (stored, or an idempotent repeat of the same `entityId` and `kind`; a repeated idempotency key answers `duplicate`, as for every entity), or `error` with a `message` that starts with one of the codes below (`CODE: text`). All of them are permanent (the client drops the operation and does not retry) except `LOCATION_NOT_ACTIVE`, which answers `deferred` with a `message` starting `LOCATION_NOT_ACTIVE:`: the trip's first pickup may still be waiting in a queue (for example a pickup held back until its QR handoff secret syncs), so the client re-queues the ping without spending a retry. The window is checked before the trip and its deliveries are read, so once the ping is older than 7 days it answers `LOCATION_STALE` like any other, whatever the deliveries say.

| Code | When |
|---|---|
| `LOCATION_VALIDATION` | a missing or out-of-range field, an unknown `kind` or `source`, an `entityId` that does not match `tripId` and `capturedAt` |
| `LOCATION_NOT_TRUCKER` | the caller is not the trip's trucker (or the trip does not exist) |
| `LOCATION_NOT_ACTIVE` | status `deferred`, not `error`. No delivery is `pickedUp` or `inTransit` or left those statuses after `capturedAt`, and either a delivery on the trip is still `pending` (another leg may still be picked up, so sharing is not ended) or no delivery has been picked up (none, or only ones cancelled before pickup). Also sent instead of `LOCATION_SHARING_ENDED` when an earlier delivery operation of the same sync batch succeeded, because the deliveries' `TripIdIndex` may not show that write yet; the next sync decides |
| `LOCATION_SHARING_ENDED` | every delivery on the trip that was picked up left `pickedUp` and `inTransit` before `capturedAt`, none is still `pending`, and no earlier delivery operation of the same sync batch succeeded; the client stops sharing |
| `LOCATION_RATE_LIMITED` | more than 20 fixes for the trip in that clock hour |
| `LOCATION_STALE` | `capturedAt` is older than 7 days or more than 2 minutes in the future |

`update` and `delete` are refused with `LOCATION_VALIDATION`.

### Storage

`{prefix}-trip-locations`: PK `tripId` (S), SK `{capturedAt}#{kind}` (S). Attributes: `truckerId`, `latitude`, `longitude` (rounded to 4 decimals), `accuracyM`, `source`, `mocked`, `pausedUntil`, `receivedAt`, `expiresAt` (epoch seconds, `capturedAt` + 60 days, the table's TTL attribute). No GSI.

---

## 2. Track: `GET /deliveries/{deliveryId}/track`

**Auth**: Cognito ID token. Only the delivery's `buyerId`, `farmerId` or `truckerId`. Anyone else, an unknown delivery and a soft-deleted one: `404` with the standard error body `{"error":{"code":"DELIVERY_NOT_FOUND","message":"Delivery not found."}}`. The route is `GET /deliveries/{id}/track` on the mobile JWT authorizer, served by the deliveries Lambda.

**Response** `200`:

```json
{
  "deliveryId": "del-101",
  "sharing": "live",
  "pausedUntil": null,
  "asOf": "2026-10-27T09:10:00.000Z",
  "lastFix": { "latitude": 8.752, "longitude": 38.979, "capturedAt": "2026-10-27T08:42:00.000Z", "receivedAt": "2026-10-27T08:42:03.000Z", "accuracyM": 35 },
  "trail": [
    { "latitude": 8.150, "longitude": 38.820, "capturedAt": "2026-10-27T05:05:00.000Z", "receivedAt": "2026-10-27T05:05:02.000Z", "source": "scan" }
  ],
  "pauses": [ { "from": "2026-10-27T07:55:00.000Z", "until": "2026-10-27T08:25:00.000Z" } ],
  "pickup": { "latitude": 8.150, "longitude": 38.820, "at": "2026-10-27T05:05:00.000Z" },
  "dropoff": { "latitude": 9.03, "longitude": 38.74, "note": "Merkato, near Anwar Mosque" },
  "otherStops": [ { "kind": "dropoff", "latitude": 8.80, "longitude": 38.90, "done": false } ],
  "eta": { "from": "2026-10-27T10:20:00.000Z", "to": "2026-10-27T11:00:00.000Z" },
  "remainingKm": 52.7,
  "summary": null
}
```

| Field | Rule |
|---|---|
| `sharing` | `not_started` (before the delivery's `pickedUpAt`, or `pickedUp` without one), `live` (fixes flowing), `paused` (latest event is `paused` and `pausedUntil` is in the future), `declined` (latest event is `declined`), `ended` (the delivery is `delivered`, `cancelled` or `disputed`). "Latest event" is the trip's latest `paused`, `resumed` or `declined`, also from before this delivery's pickup (the driver answers the consent once per trip); fixes do not change it (scans still send fixes while paused or declined) |
| `pausedUntil` | the running pause's end, only when `paused` |
| `lastFix`, `trail` | only fixes from the delivery's `pickedUpAt` to `confirmedAt` (or now plus 2 minutes, the phone clock skew the ingest accepts), both ends included, in capture order; `trail` holds every fix of the window, `lastFix` is the latest; coordinates at 3 decimals; `null` and `[]` when `not_started` or `ended` |
| `pauses` | each `paused` event runs to its `pausedUntil` or to the next event, whichever comes first; only pauses that reach past pickup, cut to start no earlier than `pickedUpAt`; `[]` when `not_started` or `ended` |
| `receivedAt` | lets the client tag "sent late" (received more than 15 minutes after capture) |
| `pickup` | the delivery's `gpsPickup` (else the listing location) at 3 decimals, and `pickedUpAt` |
| `dropoff` | the buyer's registered place, else the trip's destination place; `note` is the order's `dropoffNote` (LT-12) or `null`. As built (LT-06) the server has no coordinates for a registered place (users carry a woreda name only), so the point is the trip's `destinationLatitude` and `destinationLongitude`, the reference the delivered GPS check uses; without them `latitude` and `longitude` are `null`, and so are `eta` and `remainingKm`. No client writes those trip attributes yet, so clients must expect a `null` point |
| `otherStops` | the other orders on the trip, never named: their pickup (its `gpsPickup`, else its listing location) and drop-off (its `gpsDropoff` once made, else the trip's destination point) at 2 decimals and whether each is done (pickup: picked up; drop-off: `delivered` or `disputed`); exactly `kind`, `latitude`, `longitude`, `done`. A cancelled delivery has no stops; a stop without coordinates is left out; `[]` when `not_started` or `ended` |
| `eta`, `remainingKm` | §6.2 of the design; `null` when `not_started`, `declined` with no fix, `ended`, or without a drop-off point. As built: leg speeds are great circle distance over time; while `paused` the travel time counts from `pausedUntil` (the truck stands still until then); after rounding to 5 minutes `from` is moved up to the first 5 minute mark at or after now, and `to` is kept at least 10 minutes after `from`; `remainingKm` (the great circle × 1.3) at 1 decimal |
| `summary` | only when `ended`: `{ "distanceKm", "durationMin", "fixes" }` over the window from `pickedUpAt` to `confirmedAt` (`cancelledAt` for `cancelled`): the sum of the great circle legs between its fixes at 1 decimal, whole minutes, the fix count; all `0` for a delivery never picked up. When `ended` no field carries coordinates: `pickup` keeps only `at`, `dropoff` has `null` coordinates |

**Errors**: `401` without a valid token; `404` as above; `500` with code `TRACK_UNAVAILABLE` in the same body on a read failure (the client keeps its last snapshot).

Clients poll at most every 5 minutes while a track screen is visible and keep the last response per delivery for offline display.

---

## 3. Trail for admins: `GET /admin/deliveries/{deliveryId}/trail`

Admin group only (`403` otherwise). Every `fix` and event for the delivery's trip from `pickedUpAt` to `confirmedAt`, at 4 decimals, plus legs: `{ "fromCapturedAt", "toCapturedAt", "distanceKm", "minutes", "kmh" }` and pauses. Used by the dispute evidence tab (LT-13).

---

## 4. Push: `truck_nearby`

Sent once per delivery to its buyer, when an accepted fix is within 5 km of the drop-off point while the delivery is `pickedUp` or `inTransit`. The server stamps `nearbyNotifiedAt` on the delivery first (conditional write), so a retry never sends twice.

| Param | Value |
|---|---|
| `trucker` | the trucker's display name |
| `distanceKm` | rounded to one decimal |
| `minutes` | the lower end of the arrival window, in minutes from now, rounded to 5 |

Push and inbox text in en, am and om; no clock times in the text. `data.type` `order`, `data.event` `truck_nearby`, `tag` `order-<orderId>`.

---

## 5. Invariants

1. Live location is never written to trip rows, crop rows, match results, messages or any browse response.
2. A party never receives coordinates from before their delivery's pickup or after its delivery.
3. Other orders on the same trip are never identified in a track response.
4. Check-ins never move money. Only the existing QR handoffs, admin verdicts and timers do.
5. Side effects never fail an ingest; each is idempotent (conditional writes).

---

## Changelog

| Version | Date | Change |
|---|---|---|
| 1.0.2 | 2026-10-28 | Section 2 as built (LT-06): the route `GET /deliveries/{id}/track`; a soft-deleted delivery is `404`; the latest event is the trip's; the window end allows the 2 minute phone clock skew; how pauses are cut; the drop-off point is the trip's destination point (no coordinates exist for a registered place), `null` without it; which points the other stops use, `[]` before pickup and after delivery; while `paused` the arrival window counts from `pausedUntil`, the start moves to the first 5 minute mark at or after now and the window stays at least 10 minutes wide; the summary's window, rounding and zeros; no coordinates in any field when `ended`. |
| 1.0.1 | 2026-10-28 | Section 1 as built (LT-05): when a delivery left the road (`confirmedAt`, or `cancelledAt` after pickup); `LOCATION_NOT_ACTIVE` (deferred) also while another delivery on the trip is still `pending`, and in place of `LOCATION_SHARING_ENDED` after a delivery write earlier in the same batch; the window is checked first, so a ping older than 7 days answers `LOCATION_STALE`; the fix limit counts the clock hour (UTC) of `capturedAt`. |
| 1.0.0 | 2026-10-28 | First version (EPIC LT): `location_ping`, track, admin trail, `truck_nearby`. |
