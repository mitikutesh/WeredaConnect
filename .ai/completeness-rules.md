# Completeness Rules — MANDATORY

**Every agent and command MUST check these rules before reporting work as done.**
**Violations are automatic FAIL — no exceptions.**

---

## Rule 1: All Three Roles

WeredaConnect has **3 user roles** with different permissions. Every change to role-dependent code must be verified for ALL 3:

| Role | Creates | Sees Own | Sees All |
|------|---------|----------|----------|
| **Farmer** | Crop listings | Own crops only | — |
| **Trucker** | Trip listings | Own trips only | — |
| **Buyer** | Purchase intents | Own intents | ALL crops + ALL trips |

**When this applies:**
- Navigation, dashboards, tab bars, drawer menus
- Browse/list/search screens for crops, trips, orders, deliveries
- BLoC/cubit data-fetching logic
- Server query/filter logic in repositories and handlers
- API endpoint authorization
- UI conditionals based on `user.role`

**How to verify:**
1. Search changed files for role references: `farmer`, `trucker`, `buyer`, `UserRole`, `role ==`
2. If ANY role-specific logic changed, check the other two roles
3. Test with all 3 test users: Farmer (`+251900100001`), Trucker (`+251900100002`), Buyer (`+251900100003`)

**Common mistakes:**
- Fixing a nav bug for Farmer but not checking Trucker/Buyer
- Changing a dashboard widget that only renders for one role
- Adding a new screen without role-based access control
- Modifying a server query that filters by `userId` — Buyer queries must NOT filter by userId for crops/trips

---

## Rule 2: Mobile + Web Together

Mobile and web share the same Dart codebase (`mobile/`) but have **separate implementations** for platform-specific features. Any change to one platform must verify the other.

**Platform pair mappings** (canonical list — update HERE, not elsewhere):

| Mobile File | Web File | Shared Interface |
|------------|----------|-----------------|
| `core/auth/auth_storage_mobile.dart` | `core/auth/auth_storage_web.dart` | `auth_storage.dart` |
| `core/auth/offline_auth_cache.dart` | `core/auth/offline_auth_cache_web.dart` | — |
| `core/database/isar_service.dart` | `core/database/isar_service_web.dart` | — |
| `core/sync/sync_executor.dart` | `core/sync/sync_executor_web.dart` | — |
| `core/sync/sync_queue.dart` | `core/sync/sync_queue_web.dart` | — |
| `core/network/cert_pinning_io.dart` | `core/network/cert_pinning_stub.dart` | — |
| `core/auth/fayda_callback_links_stub.dart` | `core/auth/fayda_callback_links_web.dart` | `fayda_callback_links.dart` |
| `core/database/schemas/*.dart` | `core/models/web/*.dart` | `core/models/*.dart` (barrel) |

**When this applies:**
- ANY change to a file in the left or right column above
- ANY change to a shared interface/abstract that both implement
- ANY change to `core/models/*.dart` barrel files
- ANY new method, field, or signature change in platform-specific code
- ANY import of `dart:io`, `dart:html`, or `package:isar`

**How to verify:**
1. Check if the changed file has a counterpart in the table above
2. If a shared interface changed, verify BOTH implementations satisfy it
3. If a new method was added to one side, add a matching stub/implementation to the other
4. Run web build to catch web-incompatible imports: `cd mobile && flutter build web --no-tree-shake-icons 2>&1 | tail -20`

**Web-specific constraints:**
- Isar does NOT work on web — web stubs must use in-memory or no-op implementations
- `dart:io` is not available on web — use conditional imports
- `flutter_secure_storage` has different behavior on web (localStorage, less secure)

**Common mistakes:**
- Adding a method to `sync_queue.dart` but not `sync_queue_web.dart`
- Changing a schema field without updating the web model counterpart
- Using `dart:io` in shared code without a web-safe conditional import
- Changing a BLoC that feeds both mobile and web screens without testing web

---

## Rule 3: Cross-Module Sync

Server, mobile, and admin share data contracts. Changes to one module may break the others.

| If you change... | Also check... |
|-----------------|--------------|
| Server DTO (`server/src/Models/`) | Mobile model + admin TypeScript type |
| Server endpoint (`server/src/Functions/`) | Mobile API client + admin API client |
| API contract (`specs/contracts/`) | Server handler + mobile client + admin client |
| Mobile model (`mobile/lib/**/models/`) | Server DTO shape + web counterpart |
| Localization key (any `.arb` file) | All 3 languages: `app_en.arb`, `app_am.arb`, `app_om.arb` |

---

## Rule 4: Tests Follow Code

Every production code change must have corresponding test updates.

- Search `mobile/test/` and `server/tests/` for files that test the changed code
- If a model changes, update serialization/deserialization tests
- If a BLoC/handler changes, update its unit tests
- If no test exists yet, **flag it to the user** — do not silently skip

---

## Rule 5: Don't Break the Core Flow

The core marketplace flow must always work end-to-end:

```
Farmer creates crop listing
  → Buyer browses ALL crop listings
  → Buyer creates order
  → Trucker creates trip listing
  → Buyer browses ALL trip listings
  → Delivery created (farmer → trucker → buyer)
  → Escrow payment (buyer pays → farmer receives)
```

Before reporting "done", mentally trace the change through this flow. If ANY step could break, verify it.

---

## Self-Check Before "Done"

Copy this checklist mentally before reporting completion:

- [ ] All 3 user roles covered (Farmer, Trucker, Buyer)?
- [ ] Both platforms verified (mobile + web)?
- [ ] Platform pair counterparts updated (if applicable)?
- [ ] Cross-module contracts still aligned (server ↔ mobile ↔ admin)?
- [ ] All 3 language files updated (if UI strings changed)?
- [ ] Affected tests updated and passing?
- [ ] Core marketplace flow still works end-to-end?
- [ ] No `dart:io` or Isar in shared/web code paths?

---

**Last Updated**: 2026-03-19
