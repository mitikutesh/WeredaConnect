# Conflict Resolution UI Architecture

## Version
1.0.0 | 2026-02-21

## Status
Implemented (F-15 — logic complete, UI ~30%)

---

## 1. Overview

WeredaConnect's sync engine resolves conflicts using a "server wins" strategy. F-15 adds a **notification-based UI** that surfaces conflict events to users, showing what the server changed. The resolution is automatic — the UI is purely informational, building trust in the offline-first system.

### Ethiopian Context
- Conflicts are rare (most users work solo on one device)
- When they occur (shared devices, family members), users need reassurance
- Low-literacy users need simple, visual diffs — not JSON
- The UI must work offline (conflict data is already local)

---

## 2. Architecture

```
SyncExecutor._processResponse()
    │
    ├── calls ConflictResolver.resolve()           ← existing (updates Isar)
    │
    └── calls ConflictNotificationService.notify() ← NEW (broadcasts to UI)
            │
            ▼
    ConflictNotificationService (Stream<ConflictEvent>)
            │
            ▼
    ConflictBloc (subscribes to stream)
            │
            ├── emits ConflictPending(event)
            │
            ▼
    BlocListener in AuthGate
            │
            └── shows ConflictResolutionScreen as modal bottom sheet
```

---

## 3. Data Model — ConflictEvent

```dart
class ConflictEvent extends Equatable {
  final SyncOperation operation;          // The conflicting operation
  final Map<String, dynamic> serverData;  // Server's authoritative data
  final int serverVersion;                // Applied version number
  final String entityType;                // crop_listing, delivery, trip
  final String entityId;                  // Entity UUID
  final DateTime resolvedAt;              // When conflict was resolved
}
```

---

## 4. ConflictNotificationService

Stream-based service created in `main.dart`, shared between SyncExecutor and ConflictBloc.

```dart
class ConflictNotificationService {
  final StreamController<ConflictEvent> _controller =
      StreamController<ConflictEvent>.broadcast();

  Stream<ConflictEvent> get onConflict => _controller.stream;
  void notify(ConflictEvent event) { ... }
  void dispose() { ... }
}
```

**Integration**: Called by SyncExecutor AFTER ConflictResolver.resolve() succeeds, so local data is already updated when the UI shows.

---

## 5. ConflictBloc

### Events
| Event | Purpose |
|-------|---------|
| `ConflictDetected(conflict)` | Sync conflict auto-resolved |
| `AcceptServerVersion` | User taps "OK, Got It" |
| `DismissConflict` | User dismisses dialog |

### States
| State | Meaning |
|-------|---------|
| `ConflictInitial` | No active conflict |
| `ConflictPending(conflict)` | Conflict pending user acknowledgement |
| `ConflictResolved` | User acknowledged (transient → Initial) |
| `ConflictError(message)` | Processing error |

### State Flow
```
ConflictInitial → ConflictPending → ConflictResolved → ConflictInitial
```

---

## 6. UI — ConflictResolutionScreen

A **modal bottom sheet** (not full screen) showing:

1. **Header**: sync_problem icon + "Data Updated" title
2. **Explanation**: "Another device or the server updated this record"
3. **Entity badge**: Type icon + "Crop Listing #abc123"
4. **Field summary**: Key fields from server data (entity-type-specific)
5. **Primary button**: "OK, Got It" (FilledButton)
6. **Secondary button**: "Dismiss" (TextButton)

### Entity-Specific Field Display

| Entity | Fields Shown |
|--------|-------------|
| crop_listing | Crop Type, Quantity, Price/kg, Status |
| delivery | Status, Trip ID |
| trip | Route (from → to), Status, Capacity |

---

## 7. UX Design Patterns

### Notification Banner (Future Enhancement)
- Non-intrusive amber banner at top of screen
- Auto-dismisses after 8 seconds
- "View Changes" action to see details
- Maximum one banner at a time

### Conflict Detail Cards
- Stacked layout (not side-by-side — 360px screens too narrow)
- **Amber** = user's version (warm, personal)
- **Green** = server's version (positive, resolved)
- Checkmark icon on applied version

### Terminology (Low-Literacy Friendly)

| Technical Term | User-Facing Term |
|---------------|-----------------|
| Conflict | "Updated on another device" |
| Server version | "Current version" |
| Local version | "Your version" |
| Resolved | "Applied" |
| Entity | Specific name: "crop listing", "trip", "delivery" |

---

## 8. Navigation Wiring

BlocListener added in `AuthGate` wrapping `MainNavigation`:

```dart
if (authState is AuthAuthenticated) {
  return BlocListener<ConflictBloc, ConflictState>(
    listener: (context, state) {
      if (state is ConflictPending) {
        showModalBottomSheet<void>(
          context: context,
          isScrollControlled: true,
          isDismissible: true,
          builder: (_) => BlocProvider.value(
            value: context.read<ConflictBloc>(),
            child: ConflictResolutionScreen(conflict: state.conflict),
          ),
        );
      }
    },
    child: const MainNavigation(),
  );
}
```

**Why AuthGate?** Ensures conflicts only shown to authenticated users, and the listener fires regardless of which tab is active.

---

## 9. Design Decisions

| Decision | Rationale |
|----------|-----------|
| Modal bottom sheet (not full screen) | Informational only — less jarring |
| Notify after resolve (not before) | Server wins is automatic — local data already consistent |
| Separate notification service | Clean separation: SyncExecutor → service → BLoC |
| Listen in AuthGate | Natural "authenticated shell" boundary |
| Amber + Green (not Red + Green) | Red implies error; amber is warm/neutral |
| "Got It" (not "Accept") | Acknowledgment, not choice (server already won) |
| No undo/revert | Server-wins strategy; users can re-edit if needed |

---

## 10. File Structure

```
mobile/lib/features/conflict_resolution/
├── data/
│   ├── models/
│   │   └── conflict_event.dart
│   └── services/
│       └── conflict_notification_service.dart
└── presentation/
    ├── bloc/
    │   ├── conflict_bloc.dart
    │   ├── conflict_event.dart        # BLoC events (not data model)
    │   └── conflict_state.dart
    ├── screens/
    │   └── conflict_resolution_screen.dart
    └── widgets/
        ├── conflict_empty_state.dart
        ├── conflict_detail_card.dart
        ├── conflict_notification_banner.dart
        └── conflict_field_diff.dart
```

**Files modified** (not created):
- `sync_executor.dart` — ConflictNotificationService param + notify call
- `auth_gate.dart` — BlocListener for ConflictBloc
- `main.dart` — register service + BLoC
- `app_en.arb` / `app_am.arb` / `app_om.arb` — 9 l10n keys

---

## 11. L10n Keys

| Key | English |
|-----|---------|
| `conflictTitle` | Data Updated |
| `conflictExplanation` | Another device or the server updated this record... |
| `conflictAccept` | OK, Got It |
| `conflictDismiss` | Dismiss |
| `conflictEntityCropListing` | Crop Listing |
| `conflictEntityDelivery` | Delivery |
| `conflictEntityTrip` | Trip |
| `conflictServerValues` | Updated values from server: |
| `conflictNoDetails` | No details available. |

---

**Cross-references**:
- Detailed design: `specs/decisions/f15-conflict-resolution-ui.md`
- UX design: `specs/decisions/f15-ux-design.md`
- Security review: `specs/decisions/f15-security-review.md`

**Last Updated**: 2026-02-21
