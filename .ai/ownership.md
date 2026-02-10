# Module Ownership Map

## Purpose
This file defines **which scope owns which files**. Every agent must check this before editing any file. If a file is outside your scope, create a task in `TASK.md` for the owning scope instead of modifying it yourself.

---

## Ownership Table

| Scope | Owned Paths | Read-Only Access |
|-------|-------------|------------------|
| **Mobile** | `mobile/**` (except `mobile/lib/shared/l10n/`) | `specs/`, `docs/`, `.ai/` |
| **Server** | `server/**` | `specs/`, `docs/`, `.ai/` |
| **Infra** | `infra/**` | `specs/`, `docs/`, `.ai/` |
| **Localization** | `mobile/lib/shared/l10n/**` | `docs/local_reality/` |
| **Testing** | `*/test/**`, `*/tests/**`, `*/integration_test/**` | All source (read-only) |
| **Security** | `qa/**` | All (read-only) |
| **Architect** | `specs/**`, `docs/architecture/**`, `.ai/**` | All (read-only) |

---

## Shared Files (Special Rules)

These files are shared across scopes and require extra care:

| File | Who Can Modify | Protocol |
|------|---------------|----------|
| `CLAUDE.md` | Architect only | Propose changes via `TASK.md`, human approves |
| `STATE.md` | Any scope | Update before session handoff (append, don't overwrite) |
| `TASK.md` | Any scope | Claim with `@Scope` tag before editing tasks (see `.agents/task-protocol.md`) |
| `.agents/*.md` | Architect only | Propose changes via `TASK.md` |
| `specs/contracts/*.md` | Architect only | Mobile/Server propose changes via `TASK.md` |

---

## Rules

1. **Check before you edit.** Look up the file path in the table above. If it's not in your owned paths, stop.
2. **Read-only means read-only.** You can reference any file in your read-only list but never modify it.
3. **Request, don't reach.** If you need a change in another scope's files, create a `TASK.md` entry tagged with the owning scope.
4. **Tests follow source.** The Testing scope owns test directories, but test files should mirror the source structure of the scope being tested.
5. **Contracts are neutral ground.** `specs/contracts/` defines the interface between scopes. Only the Architect scope modifies contracts; Mobile and Server propose changes.

---

## Conflict Resolution

If two scopes believe they own the same file:
1. Check this table — it's the source of truth
2. If the table doesn't cover it, escalate to the human developer
3. Document the resolution by updating this table

---

**Last Updated**: 2026-02-09
**Version**: 1.0.0
