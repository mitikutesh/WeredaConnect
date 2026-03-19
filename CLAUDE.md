# CLAUDE.md - Constitution (Root of Trust)

**WeredaConnect** — Offline-first agritech logistics platform for Ethiopian rural communities. Farmers, truckers, buyers with Telebirr escrow payments. 3 languages: Amharic, Afan Oromo, English.

---

## CRITICAL CONTEXT (always in scope — do not skip)

### Role-Based Visibility Rules (HARD REQUIREMENT)

| Role | Can Create | Sees Own Only | Sees ALL |
|------|------------|---------------|----------|
| **Farmer** | Crop listings | Own crops only | — |
| **Trucker** | Trip listings | Own trips only | — |
| **Buyer** | Purchase intents (NOT crops/trips) | Own intents | ALL crops + ALL trips from everyone |

- Farmer MUST NOT see other farmers' crops. Trucker MUST NOT see other truckers' trips.
- Buyer MUST see ALL crops and ALL trips from everyone.
- Enforce in: server queries, mobile browse screens, BLoC data-fetching, API design.
- **A violation is CRITICAL — automatic FAIL.**

### Platform Pairs (Mobile + Web must stay in sync)

| Mobile File | Web File | Shared Interface |
|------------|----------|-----------------|
| `core/auth/auth_storage_mobile.dart` | `core/auth/auth_storage_web.dart` | `auth_storage.dart` |
| `core/auth/offline_auth_cache.dart` | `core/auth/offline_auth_cache_web.dart` | — |
| `core/database/isar_service.dart` | `core/database/isar_service_web.dart` | — |
| `core/sync/sync_executor.dart` | `core/sync/sync_executor_web.dart` | — |
| `core/sync/sync_queue.dart` | `core/sync/sync_queue_web.dart` | — |
| `core/network/cert_pinning_io.dart` | `core/network/cert_pinning_stub.dart` | — |
| `core/database/schemas/*.dart` | `core/models/web/*.dart` | `core/models/*.dart` (barrel) |

Canonical source: `.ai/completeness-rules.md` Rule 2 (verification steps + web constraints)

- If you change one side, update the other. Isar does NOT work on web — web stubs use in-memory/no-op.
- Run web build to catch breakage: `cd mobile && flutter build web --no-tree-shake-icons 2>&1 | tail -20`

### Core Marketplace Flow (must never break)

```
Farmer creates crop listing → Buyer browses ALL listings → Buyer creates order
→ Trucker creates trip → Buyer browses ALL trips → Delivery created
→ Escrow payment (buyer pays → farmer receives after delivery confirmed)
```

### Test Users (dev environment)

| Role | Phone | Password |
|------|-------|----------|
| Farmer | `+251900100001` | `TestPass1dev` |
| Trucker | `+251900100002` | `TestPass1dev` |
| Buyer | `+251900100003` | `TestPass1dev` |

API: `https://b8xqjg4myb.execute-api.me-south-1.amazonaws.com`
Login: `POST /auth/login` with `{"phoneNumber": "...", "password": "TestPass1dev"}`
Full details: `.ai/test-users.md`

### AOT Compliance (Server — violations break the build)

**Banned**: Newtonsoft.Json, Entity Framework Core, Moq, AutoMapper, Autofac assembly scanning, all reflection APIs.
**Required**: `System.Text.Json` with `JsonSerializerContext` source generators. Register types in `server/src/AppJsonContext.cs`.
Full list: `server/AOT_COMPLIANCE.md`

---

## Context Loading — What to Read for Each Task

Read this table BEFORE starting work. Load only what you need — don't read everything.

| Task Type | Read These Files |
|-----------|-----------------|
| **Any code change** | `.ai/completeness-rules.md` (MANDATORY) |
| **Mobile feature/fix** | `.ai/standards.md`, `specs/contracts/` for relevant API |
| **Server endpoint/fix** | `server/AOT_COMPLIANCE.md`, `specs/contracts/` for relevant API |
| **Infrastructure change** | `.ai/tech_stack.md`, `infra/lib/stacks/` for existing patterns |
| **Sync/offline bug** | `specs/contracts/sync-protocol.md`, `mobile/lib/core/sync/` |
| **Localization** | `mobile/lib/shared/l10n/app_en.arb` (check existing keys first) |
| **API contract change** | `specs/contracts/README.md`, then the specific contract |
| **Deployment** | `.ai/deployment.md`, `.ai/deployment-checklist.md` |
| **Security audit** | Run `/harden`, read `TASK.md` TIER 3.5 |
| **Resume / new session** | `STATE.md` → `TASK.md` → `.ai/quick-start.md` |
| **Full codebase orientation** | `.ai/quick-start.md` (navigation hub for everything) |

---

## Completeness Rules (MANDATORY — automatic FAIL on violation)

**Full rules with tables and verification steps: `.ai/completeness-rules.md`**

1. **All 3 Roles**: If you change ANY role-dependent code (dashboards, navigation, browse screens, queries, BLoCs), verify Farmer, Trucker, AND Buyer.
2. **Mobile + Web Together**: If you change a platform-paired file (see table above), update its counterpart. Run web build.
3. **Cross-Module Sync**: Server DTO changes → check mobile models + admin types. Contract changes → check all modules. L10n → all 3 languages.
4. **Tests Follow Code**: Every production code change needs test updates. Search `mobile/test/` and `server/tests/`. Flag missing tests.
5. **Don't Break the Core Flow**: Trace your change through the marketplace flow above. If ANY step could break, verify it.

### Self-Check Before "Done"

- [ ] All 3 roles verified (Farmer, Trucker, Buyer)?
- [ ] Both platforms verified (mobile + web build passes)?
- [ ] Platform pair counterparts updated?
- [ ] Cross-module contracts aligned (server ↔ mobile ↔ admin)?
- [ ] All 3 language files updated (if UI strings changed)?
- [ ] Affected tests updated and passing?
- [ ] Core marketplace flow still works end-to-end?
- [ ] No `dart:io` or Isar in shared/web code paths?

---

## Core Principles

1. **Does it work offline?** (Non-negotiable)
2. **Is it culturally appropriate?** (User trust)
3. **Can it run on low-end devices?** (Accessibility — 2GB RAM, Android 8+)
4. **Is it maintainable solo?** (Sustainability)
5. **Does it minimize data usage?** (Cost to users — <3s on 3G, <50MB APK)

---

## Technical Constraints

### Mobile (Flutter 3.38.9, Dart 3.10.8)
- **Offline-first**: Isar database for local storage, sync queue for deferred operations
- **BLoC pattern**: Events → BLoC → States
- **Localization**: All UI strings in `AppLocalizations.of(context)!.keyName`, 3 `.arb` files
- **Target**: Android 8+ (API 26+), 2GB RAM, ARM64

### Server (.NET 10 Native AOT)
- **Native AOT**: No reflection — see AOT table above
- **JSON**: `System.Text.Json` with source generators only
- **Testing**: xUnit + NSubstitute + FluentAssertions (never Moq)
- **DynamoDB**: Manual marshalling, no ORM
- **Idempotency keys**: `{userId}:{entityType}:{entityId}:{action}:{timestamp}`
- **Cold start**: <100ms target

### Infrastructure (AWS CDK 2.170.x, Node 20)
- **Region**: `me-south-1` (Bahrain — closest to Ethiopia)
- **Lambda**: ARM64 Graviton, 512MB, 10s timeout, reserved concurrency
- **DynamoDB**: On-demand billing (12 tables)
- **Never**: NAT Gateways, RDS, auto-scaling without cost caps

---

## Implementation Protocol (Mandatory)

Follow this pipeline for **every** code change. Use `/implement` to automate it.

**Phase 1 — Analyze**: Identify ALL files, platform pairs, role impacts, cross-module effects. Present impact summary. **Wait for user confirmation.**

**Phase 2 — Implement**: Make changes. Update platform counterparts together. Verify all 3 roles.

**Phase 3 — Validate**: Run `/validate` → `/review` → `/test`. All must pass.

**Phase 4 — Fix loop**: If any fail, fix and re-run all three until clean.

**Exceptions**: Trivial changes (typos, comments, formatting) may skip Phase 1, but must still run Phase 3.

---

## Build & Validation

```bash
# Server
cd server && dotnet build --no-restore && dotnet test --no-restore

# Mobile
cd mobile && flutter analyze && flutter test

# Mobile Web (if shared code changed)
cd mobile && flutter build web --no-tree-shake-icons 2>&1 | tail -20

# Infrastructure
cd infra && npx cdk synth --quiet

# Formatting (auto-handled by PostToolUse hooks in main session)
dart format <file_path>
dotnet format server/WeredaConnect.Server.sln --include <file_path>
```

---

## Git Commit Messages

[Conventional Commits](https://www.conventionalcommits.org/). Full rules: `.ai/standards.md`.

Format: `<type>(<scope>): <subject>`
Types: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`, `ci`, `perf`, `build`
Scopes: `mobile`, `server`, `infra`, `docs`, `l10n`, `sync`, `escrow`, `auth`

---

## Forbidden Patterns

❌ **Never** assume reliable internet connectivity
❌ **Never** use reflection-heavy libraries in .NET AOT
❌ **Never** create files without explicit user request
❌ **Never** commit/deploy without user approval
❌ **Never** ignore localization requirements
❌ **Never** fix a bug for one user role without checking all 3 roles
❌ **Never** change mobile code without checking if web needs the same change
❌ **Never** change production code without updating corresponding tests

---

## Claude Code Infrastructure

### Agents (`.claude/agents/`)

| Agent | Scope | Use When |
|---|---|---|
| `mobile` | `mobile/**` | Flutter/Dart — offline-first, BLoC, l10n, Isar |
| `server` | `server/**` | .NET AOT — Lambda handlers, DynamoDB, JSON source gen |
| `infra` | `infra/**` | AWS CDK — stacks, constructs, cost optimization |
| `reviewer` | Read-only | QA — role visibility + cross-platform are CRITICAL gates |

### Slash Commands (`.claude/commands/`)

| Command | What It Does |
|---|---|
| `/implement` | Full pipeline: impact → confirm → implement → validate → review → test |
| `/impact` | Analyze blast radius before modifying code |
| `/validate` | Build + cross-platform + cross-role + cross-module checks |
| `/review` | Quality review (role + platform = CRITICAL gates) |
| `/test` | Run tests for changed modules |
| `/harden` | Security audit: pentest findings, OWASP, role access, vulnerabilities |
| `/monitor` | CloudWatch alarms, Lambda errors, DynamoDB throttles, API health |
| `/backup` | DynamoDB backups: PITR status, create, list, restore |
| `/deploy` | Deploy to AWS (dev/staging/prod) |
| `/plan` | Create feature spec in `specs/features/` |
| `/checkpoint` | Session handoff — updates STATE.md |
| `/l10n` | Localization strings across 3 languages |
| `/error-mapping` | Error handling audit between backend and frontend |

### Hook-Enforced Guardrails (automatic)

**PreToolUse**: Blocks `rm -rf` on critical dirs, `.env` writes, `git push --force` to main/master.
**PostToolUse**: Auto-formats `.dart`/`.cs` files. Validates `.cs` AOT compliance (exit 2 = self-correct).

### Scope & Ownership

Before editing files, check `.ai/ownership.md`. Use `specs/contracts/` as the single source of truth for cross-module interfaces.

### Session Handoffs

Update `STATE.md` → run `scripts/checkpoint.sh` → commit WIP.

---

## Reference Index

| Topic | File |
|-------|------|
| Completeness rules (MANDATORY) | `.ai/completeness-rules.md` |
| Agentic SDLC & orchestration | `.ai/sdlc.md` |
| Agent roles & boundaries | `.ai/agents.md` |
| Navigation hub & patterns | `.ai/quick-start.md` |
| Coding standards & commit rules | `.ai/standards.md` |
| File ownership & scope | `.ai/ownership.md` |
| Tech versions & banned libs | `.ai/tech_stack.md` |
| Test users & credentials | `.ai/test-users.md` |
| Testing philosophy | `.ai/testing.md` |
| Deployment & API reference | `.ai/deployment.md` |
| Pre-deploy checklist | `.ai/deployment-checklist.md` |
| Workflows & branch naming | `.ai/workflows.md` |
| AOT compliance (server) | `server/AOT_COMPLIANCE.md` |
| API contracts | `specs/contracts/` |
| Session state | `STATE.md` |
| Task list & roadmap | `TASK.md` |

**Last Updated**: 2026-03-19
**Version**: 2.3.0
