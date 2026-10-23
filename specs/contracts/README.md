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
| Sync Protocol | `sync-protocol.md` | 1.20.0 | Mobile, Backend |
| Authentication API | `auth-api.md` | 2.9.0 | Mobile, Backend, Infrastructure |
| User Entity | `user-entity.md` | 1.1.0 | Mobile, Backend, Infrastructure |
| Rating API | `rating-api.md` | 1.1.0 | Mobile, Backend, Infrastructure |
| Order Lifecycle (status levels & escrow semantics) | `order-lifecycle.md` | 1.20.0 | Mobile, Backend, Admin |
| Escrow Protocol (Telebirr integration) | `escrow-protocol.md` | 1.19.0 | Mobile, Backend, Telebirr Gateway |
| Account Deletion | `account-deletion.md` | 1.0.0 | Mobile, Backend, Infrastructure, Admin |

---

## Proposing a New Contract

1. Create a `TASK.md` entry: `@Architect` — propose contract for [feature]
2. Include: parties involved, data flowing between them, error scenarios
3. Architect drafts the contract and tags Mobile/Backend for review
4. Human developer approves the final version

---

**Last Updated**: 2026-10-23
**Version**: 1.0.0
