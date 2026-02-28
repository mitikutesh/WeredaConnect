# WeredaConnect Documentation

## Start Here

| Goal | Documentation |
|------|---------------|
| **Understand the product (PRD)** | [Project Background](./project-background.md) |
| **Transaction flow details** | [Transaction Flow](../specs/transaction-flow.md) |
| **Ethiopian context** | [Local Reality](./local_reality/README.md) |
| **Product roadmap** | [Roadmap](../specs/features/ROADMAP.md) |

## Role-Specific Guides

| Role | Guide |
|------|-------|
| **Farmer** | [Farmer Guide](./Farmer.md) — screen-by-screen walkthrough |
| **Trucker** | [Trucker Guide](./Trucker.md) — delivery verification flow |
| **Buyer** | [Buyer Guide](./Buyer.md) — escrow payment and confirmation |

## Developer Guides

| Goal | Documentation |
|------|---------------|
| **Set up dev environment** | [Getting Started](./developer/getting-started.md) |
| **Day-to-day development** | [Development Workflow](./developer/dev-workflow.md) |
| **Learn the API** | [API Reference](./developer/api-reference.md) |
| **Write and run tests** | [Testing Guidelines](./developer/testing-guidelines.md) |
| **Deploy to AWS** | [Deployment Guide](./developer/deployment.md) |
| **E2E local testing** | [E2E Local Testing](./developer/e2e-local-testing.md) |
| **Android emulation** | [Android Emulation](./developer/android-emulation.md) |

## Architecture & Design

| Goal | Documentation |
|------|---------------|
| **System architecture** | [System Architecture](./architecture/system-architecture.md) |
| **Data flow (offline-first)** | [Data Flow](./architecture/data-flow.md) |
| **Escrow architecture** | [Escrow Architecture](./architecture/f13-mobile-escrow-architecture.md) |
| **API contracts** | [Contracts](../specs/contracts/) |
| **Architecture decisions** | [ADRs](../specs/decisions/) |

## Security & QA

| Goal | Documentation |
|------|---------------|
| **Threat model** | [Threat Model](../qa/threat_models/THREAT_MODEL_v1.0.md) |
| **Security audit (Feb 2026)** | [Security Audit](./security/security-audit-2026-02-22.md) |
| **Escrow security** | [Escrow Security](./security/f13-escrow-security-hardening.md) |
| **QA overview** | [QA README](../qa/README.md) |

## UX & Design

| Goal | Documentation |
|------|---------------|
| **Onboarding redesign** | [Onboarding](./ux/onboarding-redesign.md) |
| **UX audit issues** | [UX Audit](./ux/ux-audit-issues.md) |
| **Welcome page** | [Welcome Screen](./ux/welcome-page-improvements.md) |

## Research & Marketing

| Goal | Documentation |
|------|---------------|
| **Feasibility report** | [Feasibility](./Research/Ethiopia_Farming_App_Feasibility_Report.md) |
| **Marketing strategy** | [Marketing](./Research/Marketing_Strategy_Deep_Dive.md) |
| **Ad video script** | [Video Script](./ad-video-script.md) |
| **Troubleshooting** | [Troubleshooting](./user/troubleshooting.md) |

## Other

| Goal | Documentation |
|------|---------------|
| **Translation review** | [Afan Oromo Review](./l10n/translation-review-needed.md) |
| **2GB RAM optimization** | [Quick Reference](./mobile/optimization/QUICK_REFERENCE_2GB_RAM.md) |
| **Review fixes checklist** | [Review Fixes](./review/REVIEW-FIXES.md) |
| **Archived reports** | [Archive](./archive/) |

## Directory Structure

```
docs/
├── project-background.md    # Product Requirements Document (start here)
├── Farmer.md                # Farmer role guide (screen-by-screen)
├── Buyer.md                 # Buyer role guide (escrow + delivery)
├── Trucker.md               # Trucker role guide (pickup + transit)
├── ad-video-script.md       # Marketing video script
├── architecture/            # System design & technical architecture
├── developer/               # Developer guides & API references
├── l10n/                    # Localization review notes
├── local_reality/           # Ethiopian context & constraints
├── mobile/optimization/     # 2GB RAM optimization guide
├── Research/                # Market research & feasibility
├── review/                  # Active code review fix lists
├── runbooks/                # Operational runbooks
├── security/                # Security audits & hardening docs
├── user/                    # Troubleshooting guide
├── ux/                      # UX designs and audit issues
└── archive/                 # Historical reports and completed tasks
```

---

**Last Updated**: 2026-02-28
