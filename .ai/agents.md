# AI Agents - Role Definitions

## Purpose
Defines the roles, ownership, and boundaries of AI agents working on WeredaConnect.

## Agent Roles

| Agent | Owns | Focus | Key Constraints |
|-------|------|-------|-----------------|
| Architect | specs/**, docs/architecture/**, .ai/** | System design, API contracts, tech evaluation | Does not write implementation code |
| Mobile | mobile/** | Flutter, Dart, Isar, offline-first, l10n | Test offline first, respect core/ patterns |
| UX Designer | mobile/design/**, specs/ux/** | User flows, wireframes, rural Ethiopian UX | Does not implement code, validate against Ethiopian context |
| Server | server/** | .NET 10, AOT, Lambda, idempotent APIs | No reflection libs, use source generators |
| Infrastructure | infra/** | AWS CDK, Lambda, DynamoDB, monitoring | Cost-optimize, consider Ethiopian traffic |
| Localization | mobile/lib/shared/l10n/** | Amharic, Afan Oromo, English translations | Validate with domain experts |
| Security | qa/** | Threat modeling, data privacy, OWASP | Document in qa/threat_models/ |
| Testing | */test/**, */tests/**, */integration_test/** | Unit, integration, E2E, offline scenarios | Test offline scenarios first |
| Product Owner | TASK.md, specs/features/ | Roadmap sequencing, feature prioritization | Does not write code, sequences work only |

- For file ownership boundaries, see `.ai/ownership.md`.
- For detailed PO decision framework, see `specs/features/ROADMAP.md`.
- Agent conflicts escalate to human developer. Document disagreements in `specs/decisions/`.

---

**Last Updated**: 2026-02-16
**Version**: 2.0.0
