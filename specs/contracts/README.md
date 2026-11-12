# Boundary Contracts

## Purpose
This directory contains **API contracts** between modules (mobile, server, infra). Contracts define the shared interface so each scope can develop independently without breaking the other.

---

## Why Contracts Exist

The #1 source of cross-module conflicts: one agent changes an API while another assumes the old format. Contracts make the interface explicit and version-controlled.

---

## Rules

1. **Architect scope owns all contracts.** Only the Architect modifies files in this directory.
2. **Mobile and Backend read contracts, not each other's source.** When implementing an API call, reference the contract — not the other module's code.
3. **Propose changes via TASK.md.** If a contract needs updating, create a task tagged `@Architect` with the proposed change and rationale.
4. **Version bumps require both sides.** When a contract changes, both the Mobile and Backend tasks must reference the new version before implementation begins.
5. **Breaking changes need migration notes.** If a contract change breaks existing clients, document the migration path in the contract file itself.

---

## Contract Format

Each contract file follows this structure:

```markdown
# [Contract Name]

## Version
[semver, e.g., 1.0.0]

## Parties
[Which scopes use this contract]

## Request Format
[Schema or example]

## Response Format
[Schema or example]

## Error Codes
[Enumerated error responses]

## Invariants
[Rules that must always hold]

## Changelog
[Version history with dates]
```

---

## Current Contracts

| Contract | File | Version | Parties |
|----------|------|---------|---------|
| Sync Protocol | `sync-protocol.md` | 1.58.0 | Mobile, Backend |
| Authentication API | `auth-api.md` | 2.16.0 | Mobile, Backend, Infrastructure |
| User Entity (profile read and edit: `GET /user/profile`, the `/sync` `user` entity) | `user-entity.md` | 2.2.0 | Mobile, Web, Backend, Admin, Infrastructure |
| Rating API | `rating-api.md` | 1.5.0 | Mobile, Web, Backend, Infrastructure, Admin |
| Reputation API (penalty events, tiers, appeals, gates; events, aggregate, reads, appeal, gates and admin endpoints BUILT on the server; app BUILT behind the flags, ESC-31) | `reputation-api.md` | 1.8.0 | Mobile, Web, Backend, Admin, Infrastructure |
| Order Lifecycle (status levels & escrow semantics) | `order-lifecycle.md` | 2.16.0 | Mobile, Backend, Admin |
| Escrow Protocol (Telebirr escrow, payouts, disputes, money alarms) | `escrow-protocol.md` | 2.18.0 | Mobile, Web, Backend, Admin, Infrastructure, Telebirr Gateway |
| Delivery Verification API | `delivery-verification-api.md` | 2.8.0 | Mobile, Web, Backend, Infrastructure, Admin |
| Account Deletion | `account-deletion.md` | 1.7.1 | Mobile, Backend, Infrastructure, Admin |
| Crop Listing API | `crop-listing-api.md` | 1.13.0 | Mobile, Web, Backend, Infrastructure |
| Trip Listing API | `trip-listing-api.md` | 2.6.1 | Mobile, Backend |
| Buyer Match API | `match-api.md` | 1.5.0 | Mobile, Backend |
| Match Events (`match_event` sync entity) | `match-events.md` | n/a | Mobile, Backend |
| Push Alerts API (device tokens, `buyer_intent` sync, `GET /intents`) | `push-alerts-api.md` | 1.5.0 | Mobile, Web, Backend, Infrastructure |
| Notifications API (order, delivery, money and reputation inbox + push) | `notifications-api.md` | 1.5.1 | Mobile, Web, Backend, Infrastructure |
| Live Trip (`location_ping` check-ins, track, admin trail, `truck_nearby`) | `live-trip-api.md` | 1.1.1 | Mobile, Web, Backend, Infrastructure, Admin |
| In-App Messaging (context-scoped chat) | `messaging.md` | 1.5.0 | Mobile, Backend, Admin |
| Data Models | `data-models.md` | 1.15.0 | Mobile, Backend |
| Listing Reports (`listing_report` sync entity, admin queue) | `listing-report-api.md` | 1.0.0 | Mobile, Web, Backend, Admin, Infrastructure |

---

## Proposing a New Contract

1. Create a `TASK.md` entry: `@Architect` — propose contract for [feature]
2. Include: parties involved, data flowing between them, error scenarios
3. Architect drafts the contract and tags Mobile/Backend for review
4. Human developer approves the final version

---

**Last Updated**: 2026-11-13
**Version**: 1.0.0
