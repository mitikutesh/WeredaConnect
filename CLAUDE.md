# 📜 CLAUDE.md - Constitution (Root of Trust)

## Purpose
This document serves as the **constitutional foundation** for AI-assisted development on the WeredaConnect project. It defines the core principles, constraints, and decision-making framework that all AI agents must follow.

## Project Identity

**WeredaConnect** is an offline-first agritech logistics platform designed for Ethiopian rural communities, connecting farmers, transport providers, and urban wholesalers. It focuses on:
- **Offline-first architecture** (3G/intermittent connectivity)
- **Voice-to-text in local languages** (Amharic, Afan Oromo, English for low-literacy users)
- **Rural accessibility** (low-end Android devices, $50 phones)
- **Data efficiency** (expensive mobile data costs)
- **Logistics marketplace** (farmers → truckers → buyers with Telebirr escrow)

## Core Principles

### 1. **Local Reality First**
- All technical decisions must account for Ethiopian infrastructure constraints
- Optimize for offline functionality and minimal data usage
- Test on low-end devices (2GB RAM, Android 8+)

### 2. **Cultural Authenticity**
- Localization is not just translation—adapt UX for local context
- Use agricultural/market terminology familiar to rural users
- Respect local communication patterns and hierarchies

### 3. **Solo Developer Sustainability**
- Prioritize simplicity over cleverness
- Maintain clear documentation for context switching
- Automate repetitive tasks to preserve cognitive energy

### 4. **Production Readiness**
- Every feature must work offline before going online
- Security and privacy are non-negotiable
- Performance budgets: <3s load time on 3G, <50MB app size

## Technical Constraints

### Mobile (Flutter)
- **Offline-first**: Isar database for local storage
- **Sync engine**: Conflict resolution with server
- **Languages**: Amharic, Afan Oromo, English (l10n atoms)
- **Target**: Android 8+ (API 26+), 2GB RAM minimum

### Server (.NET 10)
- **Native AOT**: Serverless, cold-start optimized
- **Compliance**: See `server/AOT_COMPLIANCE.md` for banned libraries
- **Architecture**: Event-driven, idempotent operations

### Infrastructure (AWS CDK)
- **Cost-conscious**: Optimize for low-traffic scenarios
- **Resilient**: Handle intermittent client connections
- **Observable**: CloudWatch metrics for debugging

## AI Agent Guidelines

### Before Making Changes
1. **Read** `.ai/quick-start.md` — constraints, navigation, patterns
2. **Read** `STATE.md` → `TASK.md` — current context and priorities
3. **Read** the relevant contract in `specs/contracts/` if working on an API

### Implementation Protocol (Mandatory)

Follow this pipeline for **every** code change — no exceptions. Do NOT skip steps or combine them.

**Phase 1 — Analyze** (before writing any code):
1. Run impact analysis: identify all files that will need changes, including platform pairs (`*_web.dart`, `core/models/web/`), cross-module dependencies (server ↔ mobile ↔ admin), and affected tests.
2. Present the impact summary to the user:
   - Files to modify (grouped by module)
   - Platform counterparts that need matching updates
   - Cross-module impacts (e.g., "server DTO changed → mobile model + admin type")
   - Risk level (LOW / MEDIUM / HIGH)
3. **Wait for user confirmation before proceeding.** Do not write any code until the user approves the plan.

**Phase 2 — Implement** (after user confirms):
4. Make the changes as approved. Always update platform counterparts together — never leave a pair out of sync.

**Phase 3 — Validate** (after implementation):
5. Run `/validate` — cross-platform pair checks, cross-module impact, builds for all affected modules.
6. Run `/review` — quality criteria (offline-first, AOT, l10n, security, cross-platform, cross-module).
7. Run `/test` — tests for all affected modules.

**Phase 4 — Fix loop** (if issues found):
8. If any step in Phase 3 fails, fix the issues, then re-run **all three** (validate → review → test) until clean.
9. Report final results to the user.

**Exceptions**: Trivial changes (typo fixes, comment edits, formatting-only) may skip Phase 1 impact analysis, but must still run Phase 3 validation.

### When Proposing Solutions
- **Ask** before installing dependencies
- **Explain** trade-offs in Ethiopian context
- **Validate** against offline-first requirements
- **Document** decisions in appropriate spec files

### Scope & Ownership

Before editing files, check `.ai/ownership.md` for scope boundaries. Use `specs/contracts/` (not source code) as the single source of truth for cross-module interfaces.

### Git Commit Messages

Follow [Conventional Commits](https://www.conventionalcommits.org/). Full rules and examples in `.ai/standards.md`.

### Session Handoffs
- **Update** `STATE.md` before token limits
- **Run** `scripts/checkpoint.sh` to automate handoff
- **Commit** work-in-progress with clear messages following the convention above

## Build & Validation

After every code change, validate that the build succeeds before considering the work done.

### Formatting (mandatory)
Always format code after writing or editing. PostToolUse hooks auto-format in the main session, but **team agents must format manually**:
```bash
# Dart files
dart format <file_path>
# C# files
dotnet format server/WeredaConnect.Server.sln --include <file_path>
```

### Server (.NET)
```bash
cd server && dotnet build --no-restore
dotnet test --no-restore
```

### Mobile (Flutter)
```bash
cd mobile && flutter analyze
flutter test
```

### Infrastructure (CDK)
```bash
cd infra && npx cdk synth --quiet
```

Run only the commands relevant to the module(s) you changed. If a build or test fails, fix the issue before proceeding.

## Decision Framework

When faced with technical choices, prioritize in this order:

1. **Does it work offline?** (Non-negotiable)
2. **Is it culturally appropriate?** (User trust)
3. **Can it run on low-end devices?** (Accessibility)
4. **Is it maintainable solo?** (Sustainability)
5. **Does it minimize data usage?** (Cost to users)

## Forbidden Patterns

❌ **Never** assume reliable internet connectivity  
❌ **Never** use reflection-heavy libraries in .NET AOT  
❌ **Never** create files without explicit user request  
❌ **Never** commit/deploy without user approval  
❌ **Never** ignore localization requirements  

## Claude Code Infrastructure

### Agents (`.claude/agents/`)

Specialized agents for team-based development:

| Agent | Scope | Use When |
|---|---|---|
| `mobile` | `mobile/**` | Flutter/Dart work — offline-first repos, BLoC, l10n, Isar |
| `server` | `server/**` | .NET AOT work — Lambda handlers, DynamoDB, JSON source gen |
| `infra` | `infra/**` | AWS CDK work — stacks, constructs, cost optimization |
| `reviewer` | Read-only | QA review — validates offline-first, AOT, l10n, security |

### Slash Commands (`.claude/commands/`)

| Command | What It Does |
|---|---|
| `/implement` | Full pipeline: impact analysis → confirm → implement → validate → review → test |
| `/impact` | Analyze blast radius of a file/feature change before modifying it |
| `/validate` | Build checks + cross-platform pair checks + cross-module impact detection |
| `/review` | Review recent changes against quality criteria |
| `/test` | Run tests for all modules that have changes |
| `/plan` | Create implementation spec in `specs/features/` before building |
| `/checkpoint` | Run session handoff — updates STATE.md, prompts for commit |

### Hook-Enforced Guardrails

These are enforced automatically — you don't need to remember them:

**PreToolUse** (all tools):
- Blocks `rm -rf` on critical directories (`mobile/`, `server/`, `infra/`, `.ai/`, `specs/`, `.claude/`)
- Blocks writing to `.env` files (use `.env.example`)
- Blocks `git push --force` to `main`/`master`

**PostToolUse** (Edit/Write):
- Auto-formats `.dart` and `.cs` files after every edit
- Validates `.cs` files for AOT compliance — blocks Newtonsoft.Json, Moq, EF Core, reflection APIs (exit code 2 forces self-correction)

## Success Metrics

A feature is "done" when:
- ✅ Works offline with graceful sync
- ✅ Tested on 2GB RAM Android device
- ✅ Localized in all three languages
- ✅ Documented in `specs/` with context
- ✅ Passes security threat model review

**Last Updated**: 2026-02-28
**Version**: 2.1.0

