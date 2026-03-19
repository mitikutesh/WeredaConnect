# AI Agents - Role Definitions

## Purpose
Defines the roles, ownership, and boundaries of AI agents working on WeredaConnect.

## MANDATORY for All Agents

Every agent must read `.ai/completeness-rules.md` before every task. It defines:
- **Rule 1**: All 3 user roles (Farmer, Trucker, Buyer) must be verified
- **Rule 2**: Mobile + Web platform pairs must stay in sync
- **Rule 3**: Cross-module contracts (server ↔ mobile ↔ admin) must align
- **Rule 4**: Tests must follow code changes
- **Rule 5**: Core marketplace flow must not break

## Claude Code Agents

Implemented in `.claude/agents/`. Read each agent file for full scope, constraints, and patterns.

| Agent | File | Owns | Key Constraint |
|-------|------|------|----------------|
| Mobile | `.claude/agents/mobile.md` | `mobile/**` | Offline-first, Isar, BLoC, all 3 roles, mobile+web parity |
| Server | `.claude/agents/server.md` | `server/**` | AOT compliance, role-based query filtering, cross-module impact |
| Infrastructure | `.claude/agents/infra.md` | `infra/**` | Cost optimization, ARM64, me-south-1 region |
| Reviewer | `.claude/agents/reviewer.md` | Read-only | Role + cross-platform checks are CRITICAL FAIL gates |

## Governance Roles

Decision-making responsibilities (not separate Claude Code agents):

| Role | Scope | Focus |
|------|-------|-------|
| Architect | `specs/**`, `.ai/**` | System design, API contracts, tech evaluation |
| Localization | `mobile/lib/shared/l10n/**` | Amharic, Afan Oromo, English translations |
| Security | `qa/**` | Threat modeling, data privacy, OWASP |
| Testing | `*/test/**`, `*/tests/**` | Unit, integration, E2E, offline scenarios |

## Rules

- File ownership boundaries: `.ai/ownership.md`
- Completeness rules (MANDATORY): `.ai/completeness-rules.md`
- Agent conflicts escalate to human developer. Document disagreements in `specs/decisions/`.

---

**Last Updated**: 2026-03-19
**Version**: 2.2.0
