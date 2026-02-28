# AI Quick Start

Read this first before making any changes. It provides critical constraints, project navigation, and common patterns.

---

## Critical Constraints

These cause build failures or bad architecture if ignored:

- **Offline-first**: Isar local DB → sync to server. Never assume connectivity.
- **AOT mandatory**: No reflection, no EF Core, no Moq, no Newtonsoft.Json. Use `System.Text.Json` source generators. See `server/AOT_COMPLIANCE.md`.
- **Target device**: Android 8+ (API 26), 2GB RAM, ARM64.
- **Performance**: <3s load on 3G, sync batch <5s, cold start <100ms (currently 95ms).
- **App size**: <50MB APK. Images: WebP, max 800px.
- **Localization**: Every UI string must exist in English + Amharic + Afan Oromo. Use `AppLocalizations.of(context)!.keyName`, never hardcode strings.
- **Versions**: Flutter 3.38.9, Dart 3.10.8, .NET 10 (Native AOT), Node 20, CDK 2.170.x.

---

## Project Structure

```
mobile/lib/
├── core/database/schemas/    # Isar schemas (6 entities)
├── core/sync/                # Sync engine (queue, executor, monitor, conflict resolver)
├── core/network/             # Dio HTTP client + interceptors
├── features/[name]/          # Feature modules (BLoC + screens + repos)
│   ├── data/repositories/    # Offline-first repository
│   ├── domain/               # Business logic, entities
│   └── presentation/         # Screens, widgets, BLoC
└── shared/l10n/              # .arb localization files (en, am, om)

server/src/
├── Functions/                # Lambda handlers (one per endpoint)
├── Models/                   # DTOs with JsonSerializerContext
├── Data/                     # DynamoDB repositories (manual marshalling)
├── Services/                 # Business logic
├── Auth/                     # JWT validation
└── AppJsonContext.cs          # AOT-safe JSON source generator

infra/lib/
├── stacks/                   # CDK stack definitions
└── constructs/               # Reusable Lambda/DynamoDB constructs

specs/contracts/               # API contracts (source of truth for cross-module interfaces)
```

---

## Task-Specific Navigation

| Task | Read First | Then | Reference |
|------|-----------|------|-----------|
| **Add mobile feature** | `specs/contracts/` for API | `.ai/standards.md` for patterns | `mobile/TESTING.md` |
| **Add server endpoint** | `server/AOT_COMPLIANCE.md` | `specs/contracts/` for contract | `server/TESTING.md` |
| **Add/update API contract** | `specs/contracts/README.md` | Existing contracts for format | `.ai/ownership.md` |
| **Fix sync/offline bug** | `specs/contracts/sync-protocol.md` | `mobile/lib/core/sync/` source | `.agents/workflows.md` (bug fix) |
| **Add localization** | `mobile/lib/shared/l10n/app_en.arb` | Run `flutter gen-l10n` | All 3 `.arb` files |
| **Add infra** (table, Lambda) | `.ai/tech_stack.md` | `infra/lib/` existing patterns | `infra/TESTING.md` |
| **Deploy** | `.agents/deployment_checklist.md` | `docs/developer/deployment.md` | — |
| **Resume work / new session** | `STATE.md` → `TASK.md` | This file | `CLAUDE.md` |

---

## Key Patterns (with source references)

Follow these existing patterns when adding new code:

1. **Offline-first repository** — Local Isar first, remote fallback when online, cache results.
   See: `mobile/lib/features/crop_listing/data/repositories/crop_listing_repository.dart`

2. **Sync queue** — Enqueue operations to Isar with idempotency keys, process in batches.
   See: `mobile/lib/core/sync/sync_queue.dart`

3. **Connectivity monitor** — Stream-based, debounced, verifies internet access.
   See: `mobile/lib/core/sync/connectivity_monitor.dart`

4. **Conflict resolution** — Server-wins strategy, auto-update local records.
   See: `mobile/lib/core/sync/conflict_resolver.dart`

5. **Idempotent API handler** — Check idempotency key before processing, 7-day TTL.
   See: `server/src/Functions/SyncFunction.cs`

6. **AOT-safe JSON** — `JsonSerializerContext` source generator pattern.
   See: `server/src/AppJsonContext.cs`, `server/src/Models/CropDtos.cs`

7. **Lambda handler** — Parse request, validate, call service, return `LambdaResponse`.
   See: `server/src/Functions/CropFunction.cs`

8. **BLoC pattern** — Events → BLoC → States, with offline-first repository.
   See: `mobile/lib/features/crop_listing/presentation/` (bloc + screens)

---

## Documentation Index

| Topic | Source of Truth |
|-------|----------------|
| Project principles & decision framework | `CLAUDE.md` |
| Session state & resumption | `STATE.md` |
| Active tasks & roadmap | `TASK.md` |
| File ownership & scope rules | `.ai/ownership.md` |
| Coding standards & commit rules | `.ai/standards.md` |
| Tech versions & banned libs | `.ai/tech_stack.md` |
| Testing philosophy | `.ai/testing.md` |
| Workflows & checklists | `.agents/workflows.md` |
| Pre-deployment validation | `.agents/deployment_checklist.md` |
| Ethiopian context & constraints | `docs/local_reality/README.md` |
| Product definition (PRD) | `docs/project-background.md` |
| API contracts | `specs/contracts/` |
| Architecture decisions | `specs/decisions/` |
| AOT compliance (server) | `server/AOT_COMPLIANCE.md` |
| Product roadmap | `specs/features/ROADMAP.md` |
| Feature backlog | `specs/features/BACKLOG.md` |

---

## Common Mistakes

Errors AI agents repeatedly make on this project:

- **Using `Newtonsoft.Json` or reflection-based serializers** → breaks AOT. Use `System.Text.Json` with `JsonSerializerContext`.
- **Using `Moq` for testing** → reflection-based. Use `NSubstitute`.
- **Forgetting localization** → every user-facing string needs all 3 languages in `.arb` files.
- **Assuming network connectivity** → always implement offline path first, test in airplane mode.
- **Hardcoding crop types** → use the enum from `specs/contracts/crop-listing-api.md`.
- **Single-table DynamoDB design** → project uses multi-table (one per entity).
- **Using `Entity Framework Core`** → not AOT-compatible. Use `AWSSDK.DynamoDBv2` with manual marshalling.
- **Skipping idempotency keys** → all sync operations must have composite keys: `{userId}:{entityType}:{entityId}:{action}:{timestamp}`.

---

**Last Updated**: 2026-02-16
