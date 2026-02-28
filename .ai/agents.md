# AI Agents - Role Definitions

## Purpose
Defines the roles, ownership, and boundaries of AI agents working on WeredaConnect.

## Claude Code Agents

The following agents are implemented as Claude Code agents in `.claude/agents/` and can be used for team-based development:

| Agent | File | Owns | Key Constraints |
|-------|------|------|-----------------|
| Mobile | `.claude/agents/mobile.md` | `mobile/**` | Offline-first, Isar, BLoC, l10n, 2GB RAM target |
| Server | `.claude/agents/server.md` | `server/**` | AOT compliance, no reflection, System.Text.Json source gen |
| Infrastructure | `.claude/agents/infra.md` | `infra/**` | Cost optimization, ARM64, me-south-1 region |
| Reviewer | `.claude/agents/reviewer.md` | Read-only | Cannot write files, validates quality criteria |

## Governance Roles

These roles define decision-making responsibilities but are not separate Claude Code agents:

| Role | Scope | Focus |
|------|-------|-------|
| Architect | `specs/**`, `docs/architecture/**`, `.ai/**` | System design, API contracts, tech evaluation |
| UX Designer | `specs/ux/**` | User flows, wireframes, rural Ethiopian UX |
| Localization | `mobile/lib/shared/l10n/**` | Amharic, Afan Oromo, English translations |
| Security | `qa/**` | Threat modeling, data privacy, OWASP |
| Testing | `*/test/**`, `*/tests/**` | Unit, integration, E2E, offline scenarios |
| Product Owner | `TASK.md`, `specs/features/` | Roadmap sequencing, feature prioritization |

## Rules

- For file ownership boundaries, see `.ai/ownership.md`.
- For detailed PO decision framework, see `specs/features/ROADMAP.md`.
- Agent conflicts escalate to human developer. Document disagreements in `specs/decisions/`.

---

**Last Updated**: 2026-02-28
**Version**: 2.1.0
