# F-15: Conflict Resolution UI

**Status**: COMPLETE
**Completed**: 2026-02-21
**Branch**: `telebirr-mock`

---

## Summary

Offline sync conflict resolution UI for WeredaConnect. Uses server-wins strategy and shows users a non-intrusive notification when their data was auto-resolved, with a detail screen showing what changed.

---

## Deliverables

### Core Logic
- [x] `ConflictResolver` — server-wins resolution with Isar writes (330 lines)
- [x] `ConflictNotificationService` — broadcast stream with replay buffer
- [x] `ConflictBloc` — events: ConflictDetected, AcceptServerVersion, DismissConflict

### UI Components
- [x] `ConflictResolutionScreen` — modal bottom sheet (entity badge, field diffs, buttons)
- [x] `ConflictNotificationBanner` — MaterialBanner (amber bg, sync icon, "View Changes" action)
- [x] `ConflictDetailCard` — entity header + field diffs + "Server version applied" badge
- [x] `ConflictFieldDiff` — green background row showing current (server) value
- [x] `ConflictEmptyState` — checkmark icon + "Everything is up to date"

### Wiring
- [x] Banner displayed via `ScaffoldMessenger.showMaterialBanner()` in `MainNavigation`
- [x] `BlocListener<ConflictBloc>` triggers on `ConflictPending` state
- [x] "View Changes" navigates to `ConflictResolutionScreen` as full route
- [x] 8-second auto-dismiss via `onVisible` callback
- [x] `SyncExecutor` calls `ConflictNotificationService.notify()` after resolve

### Localization
- [x] 16 l10n keys in 3 languages (English, Amharic, Afan Oromo)

### Security
- [x] Replay buffer prevents silent event loss during app startup (Finding #1)
- [x] Length guard on `substring(0, 8)` in 3 locations (Finding #2)
- [x] All 7 security findings verified (2 required + 5 advisory)

### Tests (49 total)
- [x] `conflict_resolver_test.dart` — resolver logic
- [x] `conflict_notification_service_test.dart` — replay buffer, dispose, stream
- [x] `conflict_bloc_test.dart` — state transitions, stream subscription
- [x] `conflict_resolution_screen_test.dart` — rendering, interactions, entity types
- [x] `conflict_notification_banner_test.dart` — 6 tests (message variants, callbacks, styling)
- [x] `conflict_detail_card_test.dart` — 9 tests (3 entity types, badge, edge cases)
- [x] `conflict_field_diff_test.dart` — 7 tests (rendering, styling, long values)
- [x] `conflict_empty_state_test.dart` — 4 tests (icon, title, subtitle, centering)

---

## Key Files

| Purpose | Path |
|---------|------|
| Architecture Spec | `specs/decisions/f15-conflict-resolution-ui.md` |
| UX Design Spec | `specs/decisions/f15-ux-design.md` |
| Security Review | `specs/decisions/f15-security-review.md` |
| Resolver | `mobile/lib/core/sync/conflict_resolver.dart` |
| Notification Service | `mobile/lib/features/conflict_resolution/data/services/conflict_notification_service.dart` |
| ConflictEvent Model | `mobile/lib/features/conflict_resolution/data/models/conflict_event.dart` |
| BLoC | `mobile/lib/features/conflict_resolution/presentation/bloc/conflict_bloc.dart` |
| Screen | `mobile/lib/features/conflict_resolution/presentation/screens/conflict_resolution_screen.dart` |
| Widgets | `mobile/lib/features/conflict_resolution/presentation/widgets/` |
| Banner Wiring | `mobile/lib/screens/main_navigation.dart` (BlocListener, lines 161-208) |
| Tests | `mobile/test/features/conflict_resolution/` |

---

## Bug Fix During Completion

**Replay buffer microtask fix**: `ConflictNotificationService.onConflict` getter was replaying buffered events synchronously before `.listen()` was called on the broadcast stream, causing events to be silently lost. Fixed by wrapping replay in `Future.microtask()` so events are delivered after the caller attaches their listener.

---

## Design Decisions

1. **MaterialBanner over SnackBar** — persists at top of screen, matches UX spec for non-intrusive notification
2. **Banner in MainNavigation, not AuthGate** — Scaffold context is required for ScaffoldMessenger; MainNavigation owns the Scaffold
3. **Inline MaterialBanner, not widget import** — `ConflictNotificationBanner` returns a `MaterialBanner` wrapped in a `StatelessWidget`, but `showMaterialBanner()` requires a `MaterialBanner` directly; built inline using same visual pattern
4. **Server-wins, no user choice** — simplest for solo farmers; conflicts are rare in Ethiopian rural context
