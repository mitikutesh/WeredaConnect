# F-15: Conflict Resolution UI — Architecture Design

**Status**: Proposed
**Date**: 2026-02-21
**Deciders**: Architect Agent
**Feature**: Sync Conflict Resolution UI (User-facing)

---

## Context

WeredaConnect's sync engine currently resolves conflicts silently using a "server wins" strategy (see `ConflictResolver`). When the server returns `status: 'conflict'` for a sync operation, `SyncExecutor._processResponse()` calls `ConflictResolver.resolve()` which overwrites the local Isar entity with server data.

This is invisible to the user. F-15 introduces a **notification-based UI** so users can see when a conflict occurred and what changed — building trust in the offline-first system. The current phase still auto-resolves (server wins), but surfaces the conflict event to the user with a dialog showing what the server changed.

### Ethiopian Context
- Conflicts are rare (most users work solo on one device)
- When they occur (shared devices, family members), users need reassurance their data is safe
- Low-literacy users need simple, visual diffs — not JSON
- The UI must work offline (conflict data is already local)

---

## Architecture Overview

```
SyncExecutor._processResponse()
    │
    ├── calls ConflictResolver.resolve()     ← existing (updates Isar)
    │
    └── calls ConflictNotificationService.notify()  ← NEW (broadcasts to UI)
            │
            ▼
    ConflictNotificationService (Stream<ConflictEvent>)
            │
            ▼
    ConflictBloc (subscribes to stream)
            │
            ├── emits ConflictPending(event) state
            │
            ▼
    BlocListener in AuthGate
            │
            └── pushes ConflictResolutionScreen as modal dialog
```

---

## 1. ConflictEvent (Data Model)

**File**: `mobile/lib/features/conflict_resolution/data/models/conflict_event.dart`

```dart
import 'package:equatable/equatable.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';

/// Represents a sync conflict that was auto-resolved by the server.
///
/// Carries enough data for the UI to show the user what changed.
/// The conflict has ALREADY been resolved (server wins) by the time
/// this event reaches the UI — this is purely informational.
class ConflictEvent extends Equatable {
  /// The sync operation that conflicted.
  final SyncOperation operation;

  /// The server's authoritative data that was applied locally.
  final Map<String, dynamic> serverData;

  /// The server's version number that was applied.
  final int serverVersion;

  /// Entity type for display (crop_listing, delivery, trip).
  final String entityType;

  /// Entity ID for lookup.
  final String entityId;

  /// UTC timestamp when the conflict was resolved.
  final DateTime resolvedAt;

  const ConflictEvent({
    required this.operation,
    required this.serverData,
    required this.serverVersion,
    required this.entityType,
    required this.entityId,
    required this.resolvedAt,
  });

  @override
  List<Object?> get props => [
        operation,
        serverData,
        serverVersion,
        entityType,
        entityId,
        resolvedAt,
      ];
}
```

---

## 2. ConflictNotificationService

**File**: `mobile/lib/features/conflict_resolution/data/services/conflict_notification_service.dart`

```dart
import 'dart:async';

import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';

/// Stream-based service that broadcasts conflict events to the UI layer.
///
/// Injected into SyncExecutor as a callback. When the executor resolves
/// a conflict, it calls [notify] which pushes the event onto the stream.
/// ConflictBloc subscribes to this stream.
///
/// Lifecycle: created in main.dart, shared between SyncExecutor and ConflictBloc.
class ConflictNotificationService {
  final StreamController<ConflictEvent> _controller =
      StreamController<ConflictEvent>.broadcast();

  /// Stream that ConflictBloc subscribes to.
  Stream<ConflictEvent> get onConflict => _controller.stream;

  /// Called by SyncExecutor when a conflict is resolved.
  ///
  /// This method should be called AFTER ConflictResolver.resolve()
  /// succeeds, so the local data is already updated when the UI shows.
  void notify(ConflictEvent event) {
    if (!_controller.isClosed) {
      _controller.add(event);
    }
  }

  /// Release resources.
  void dispose() {
    _controller.close();
  }
}
```

### Integration with SyncExecutor

The `ConflictNotificationService` must be passed to `SyncExecutor` as an optional dependency. Modify `SyncExecutor` to accept and use it:

**Changes to `mobile/lib/core/sync/sync_executor.dart`**:

1. Add constructor parameter:
```dart
final ConflictNotificationService? _conflictNotificationService;

SyncExecutor({
  // ... existing params ...
  ConflictNotificationService? conflictNotificationService,
}) : // ... existing initializers ...
     _conflictNotificationService = conflictNotificationService;
```

2. In `_processResponse()`, after the `case 'conflict':` block successfully resolves, add notification:
```dart
case 'conflict':
  if (serverData != null) {
    if (_conflictResolver != null) {
      try {
        await _conflictResolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: serverVersion ?? 0,
        );

        // NEW: Notify UI about the resolved conflict
        _conflictNotificationService?.notify(
          ConflictEvent(
            operation: operation,
            serverData: serverData,
            serverVersion: serverVersion ?? 0,
            entityType: operation.entityType,
            entityId: operation.entityId,
            resolvedAt: DateTime.now().toUtc(),
          ),
        );
      } on ConflictResolutionException {
        await _syncQueue.markAsFailed(operationId);
        failed++;
        continue;
      }
    }
    // ... rest unchanged ...
  }
```

**Import to add**:
```dart
import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';
import 'package:wereda_connect/features/conflict_resolution/data/services/conflict_notification_service.dart';
```

---

## 3. ConflictBloc

### Events

**File**: `mobile/lib/features/conflict_resolution/presentation/bloc/conflict_event.dart`

> Note: This file name collides with the data model `ConflictEvent`. To avoid confusion, the BLoC event classes use `Conflict` prefix + action verb naming.

```dart
import 'package:equatable/equatable.dart';
import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart'
    as model;

/// Base class for conflict resolution BLoC events.
abstract class ConflictBlocEvent extends Equatable {
  const ConflictBlocEvent();

  @override
  List<Object?> get props => [];
}

/// A sync conflict was detected and auto-resolved.
/// Triggers the UI to show the conflict dialog.
class ConflictDetected extends ConflictBlocEvent {
  final model.ConflictEvent conflict;

  const ConflictDetected(this.conflict);

  @override
  List<Object?> get props => [conflict];
}

/// User acknowledged the conflict by tapping "OK" / "Accept Server Version".
/// Dismisses the dialog and returns to ConflictInitial.
class AcceptServerVersion extends ConflictBlocEvent {
  const AcceptServerVersion();
}

/// User dismissed the conflict dialog without explicit acceptance.
/// Same effect as AcceptServerVersion (server already won), but tracked
/// separately for analytics.
class DismissConflict extends ConflictBlocEvent {
  const DismissConflict();
}
```

### States

**File**: `mobile/lib/features/conflict_resolution/presentation/bloc/conflict_state.dart`

```dart
import 'package:equatable/equatable.dart';
import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';

/// Base class for conflict resolution BLoC states.
abstract class ConflictState extends Equatable {
  const ConflictState();

  @override
  List<Object?> get props => [];
}

/// No active conflict. Default state.
class ConflictInitial extends ConflictState {
  const ConflictInitial();
}

/// A conflict has been detected and is pending user acknowledgement.
/// The ConflictResolutionScreen should be displayed.
class ConflictPending extends ConflictState {
  final ConflictEvent conflict;

  const ConflictPending(this.conflict);

  @override
  List<Object?> get props => [conflict];
}

/// User has acknowledged the conflict. Dialog should be dismissed.
/// Transitions back to ConflictInitial after a brief moment.
class ConflictResolved extends ConflictState {
  const ConflictResolved();
}

/// An error occurred during conflict processing.
class ConflictError extends ConflictState {
  final String message;

  const ConflictError(this.message);

  @override
  List<Object?> get props => [message];
}
```

### Bloc

**File**: `mobile/lib/features/conflict_resolution/presentation/bloc/conflict_bloc.dart`

```dart
import 'dart:async';

import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/conflict_resolution/data/services/conflict_notification_service.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_event.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_state.dart';

/// BLoC that manages conflict resolution UI state.
///
/// Subscribes to [ConflictNotificationService] stream and emits
/// [ConflictPending] when a conflict is detected. The conflict has
/// already been resolved (server wins) — this BLoC only manages
/// the user notification flow.
///
/// State flow:
///   ConflictInitial → ConflictPending → ConflictResolved → ConflictInitial
class ConflictBloc extends Bloc<ConflictBlocEvent, ConflictState> {
  final ConflictNotificationService _notificationService;
  StreamSubscription<dynamic>? _conflictSubscription;

  ConflictBloc({
    required ConflictNotificationService notificationService,
  })  : _notificationService = notificationService,
        super(const ConflictInitial()) {
    on<ConflictDetected>(_onConflictDetected);
    on<AcceptServerVersion>(_onAcceptServerVersion);
    on<DismissConflict>(_onDismissConflict);

    // Subscribe to conflict notification stream
    _conflictSubscription = _notificationService.onConflict.listen(
      (event) => add(ConflictDetected(event)),
    );
  }

  void _onConflictDetected(
    ConflictDetected event,
    Emitter<ConflictState> emit,
  ) {
    emit(ConflictPending(event.conflict));
  }

  void _onAcceptServerVersion(
    AcceptServerVersion event,
    Emitter<ConflictState> emit,
  ) {
    emit(const ConflictResolved());
    emit(const ConflictInitial());
  }

  void _onDismissConflict(
    DismissConflict event,
    Emitter<ConflictState> emit,
  ) {
    emit(const ConflictResolved());
    emit(const ConflictInitial());
  }

  @override
  Future<void> close() {
    _conflictSubscription?.cancel();
    return super.close();
  }
}
```

---

## 4. ConflictResolutionScreen

**File**: `mobile/lib/features/conflict_resolution/presentation/screens/conflict_resolution_screen.dart`

### UI Design

A **modal bottom sheet** (not a full screen) that shows:

1. **Header**: Warning icon + "Data Updated by Server" title
2. **Entity info**: Entity type badge (Crop Listing / Delivery / Trip) + entity ID (short)
3. **Field-level diff**: A list of key fields showing the server values that were applied
4. **Action buttons**: "OK, Got It" (primary) and "Details" (secondary, optional)

### Key Widget Structure

```dart
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_event.dart'
    as bloc_event;
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Modal bottom sheet shown when a sync conflict is auto-resolved.
///
/// Informs the user that their local data was updated by the server.
/// The conflict is already resolved — this is purely informational.
///
/// Shows:
/// - Entity type (crop listing, delivery, trip)
/// - Key fields from server data
/// - "OK, Got It" button to dismiss
class ConflictResolutionScreen extends StatelessWidget {
  final ConflictEvent conflict;

  const ConflictResolutionScreen({required this.conflict, super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // Drag handle
            Center(
              child: Container(
                width: 40,
                height: 4,
                decoration: BoxDecoration(
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.3),
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const SizedBox(height: 16),

            // Header with warning icon
            Row(
              children: [
                Icon(
                  Icons.sync_problem,
                  color: theme.colorScheme.tertiary,
                  size: 28,
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    l10n.conflictTitle,
                    style: theme.textTheme.titleLarge,
                  ),
                ),
              ],
            ),
            const SizedBox(height: 8),

            // Explanation
            Text(
              l10n.conflictExplanation,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurface.withValues(alpha: 0.7),
              ),
            ),
            const SizedBox(height: 16),

            // Entity type badge
            _buildEntityBadge(context, l10n),
            const SizedBox(height: 16),

            // Field-level summary
            _buildFieldSummary(context, l10n),
            const SizedBox(height: 24),

            // Primary action
            FilledButton(
              onPressed: () {
                context.read<ConflictBloc>().add(
                      const bloc_event.AcceptServerVersion(),
                    );
                Navigator.of(context).pop();
              },
              child: Text(l10n.conflictAccept),
            ),
            const SizedBox(height: 8),

            // Secondary action
            TextButton(
              onPressed: () {
                context.read<ConflictBloc>().add(
                      const bloc_event.DismissConflict(),
                    );
                Navigator.of(context).pop();
              },
              child: Text(l10n.conflictDismiss),
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildEntityBadge(BuildContext context, AppLocalizations l10n) {
    final theme = Theme.of(context);
    final label = _entityTypeLabel(l10n);
    final shortId = conflict.entityId.length > 8
        ? conflict.entityId.substring(0, 8)
        : conflict.entityId;

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: BoxDecoration(
        color: theme.colorScheme.secondaryContainer,
        borderRadius: BorderRadius.circular(8),
      ),
      child: Row(
        children: [
          Icon(_entityTypeIcon(), size: 20,
              color: theme.colorScheme.onSecondaryContainer),
          const SizedBox(width: 8),
          Text(
            '$label #$shortId',
            style: theme.textTheme.labelLarge?.copyWith(
              color: theme.colorScheme.onSecondaryContainer,
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildFieldSummary(BuildContext context, AppLocalizations l10n) {
    final theme = Theme.of(context);
    final fields = _extractDisplayFields();

    if (fields.isEmpty) {
      return Text(
        l10n.conflictNoDetails,
        style: theme.textTheme.bodySmall,
      );
    }

    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: theme.colorScheme.surfaceContainerHighest,
        borderRadius: BorderRadius.circular(8),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            l10n.conflictServerValues,
            style: theme.textTheme.labelMedium?.copyWith(
              color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
            ),
          ),
          const SizedBox(height: 8),
          ...fields.entries.map(
            (entry) => Padding(
              padding: const EdgeInsets.only(bottom: 4),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  SizedBox(
                    width: 120,
                    child: Text(
                      entry.key,
                      style: theme.textTheme.bodySmall?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ),
                  Expanded(
                    child: Text(
                      entry.value,
                      style: theme.textTheme.bodySmall,
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  /// Extract human-readable field names and values from serverData
  /// based on entity type.
  Map<String, String> _extractDisplayFields() {
    final data = conflict.serverData;
    switch (conflict.entityType) {
      case 'crop_listing':
        return {
          'Crop Type': data['cropType']?.toString() ?? '-',
          'Quantity': data['quantity']?.toString() ?? '-',
          'Price/kg': data['pricePerKg']?.toString() ?? '-',
          'Status': data['status']?.toString() ?? '-',
        };
      case 'delivery':
        return {
          'Status': data['status']?.toString() ?? '-',
          'Trip': data['tripId']?.toString().substring(0, 8) ?? '-',
        };
      case 'trip':
        return {
          'Route': '${data['fromWoreda'] ?? '?'} → ${data['toWoreda'] ?? '?'}',
          'Status': data['status']?.toString() ?? '-',
          'Capacity': data['capacity']?.toString() ?? '-',
        };
      default:
        return {};
    }
  }

  String _entityTypeLabel(AppLocalizations l10n) {
    switch (conflict.entityType) {
      case 'crop_listing':
        return l10n.conflictEntityCropListing;
      case 'delivery':
        return l10n.conflictEntityDelivery;
      case 'trip':
        return l10n.conflictEntityTrip;
      default:
        return conflict.entityType;
    }
  }

  IconData _entityTypeIcon() {
    switch (conflict.entityType) {
      case 'crop_listing':
        return Icons.list_alt;
      case 'delivery':
        return Icons.inventory_2;
      case 'trip':
        return Icons.local_shipping;
      default:
        return Icons.sync_problem;
    }
  }
}
```

---

## 5. Navigation Wiring

### Where to Listen

Add a `BlocListener<ConflictBloc, ConflictState>` inside **`AuthGate`**, wrapping the `MainNavigation` return. This ensures:
- Conflicts are only shown to authenticated users
- The listener has access to `Navigator` context
- It fires regardless of which tab the user is on

### Implementation in `auth_gate.dart`

Wrap the `MainNavigation()` return value:

```dart
if (authState is AuthAuthenticated) {
  context.read<OnboardingBloc>().add(const MarkWelcomeSeen());
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

**Imports to add to `auth_gate.dart`**:
```dart
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_state.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/screens/conflict_resolution_screen.dart';
```

---

## 6. File Structure

```
mobile/lib/features/conflict_resolution/
├── data/
│   ├── models/
│   │   └── conflict_event.dart              # ConflictEvent data class
│   └── services/
│       └── conflict_notification_service.dart  # Stream-based notification service
└── presentation/
    ├── bloc/
    │   ├── conflict_bloc.dart               # ConflictBloc
    │   ├── conflict_event.dart              # ConflictBlocEvent classes
    │   └── conflict_state.dart              # ConflictState classes
    └── screens/
        └── conflict_resolution_screen.dart  # Modal bottom sheet UI
```

**Files modified** (not created):
- `mobile/lib/core/sync/sync_executor.dart` — add `ConflictNotificationService` param + notification call
- `mobile/lib/features/auth/screens/auth_gate.dart` — add `BlocListener` for `ConflictBloc`
- `mobile/lib/main.dart` — register `ConflictNotificationService` + `ConflictBloc`
- `mobile/lib/shared/l10n/app_en.arb` — add l10n keys
- `mobile/lib/shared/l10n/app_am.arb` — add Amharic translations
- `mobile/lib/shared/l10n/app_om.arb` — add Afan Oromo translations

---

## 7. L10n Keys

Add to `app_en.arb` (English values):

```json
"@@conflictResolutionSection": "=== Conflict Resolution Feature ===",

"conflictTitle": "Data Updated",
"@conflictTitle": {
  "description": "Title shown on conflict resolution modal when server overwrites local data"
},
"conflictExplanation": "Another device or the server updated this record. Your local copy has been updated to match.",
"@conflictExplanation": {
  "description": "Explanation of what happened during a sync conflict"
},
"conflictAccept": "OK, Got It",
"@conflictAccept": {
  "description": "Primary button to acknowledge the conflict resolution"
},
"conflictDismiss": "Dismiss",
"@conflictDismiss": {
  "description": "Secondary button to dismiss the conflict notification"
},
"conflictEntityCropListing": "Crop Listing",
"@conflictEntityCropListing": {
  "description": "Entity type label for crop listing in conflict dialog"
},
"conflictEntityDelivery": "Delivery",
"@conflictEntityDelivery": {
  "description": "Entity type label for delivery in conflict dialog"
},
"conflictEntityTrip": "Trip",
"@conflictEntityTrip": {
  "description": "Entity type label for trip in conflict dialog"
},
"conflictServerValues": "Updated values from server:",
"@conflictServerValues": {
  "description": "Section header showing server-side field values"
},
"conflictNoDetails": "No details available.",
"@conflictNoDetails": {
  "description": "Shown when conflict event has no displayable field changes"
}
```

**Summary of l10n keys** (9 keys):
| Key | English Value |
|-----|---------------|
| `conflictTitle` | Data Updated |
| `conflictExplanation` | Another device or the server updated this record. Your local copy has been updated to match. |
| `conflictAccept` | OK, Got It |
| `conflictDismiss` | Dismiss |
| `conflictEntityCropListing` | Crop Listing |
| `conflictEntityDelivery` | Delivery |
| `conflictEntityTrip` | Trip |
| `conflictServerValues` | Updated values from server: |
| `conflictNoDetails` | No details available. |

---

## 8. main.dart Changes

### Service Creation (before `runApp`)

Add after `escrowSyncService` creation:

```dart
// Create conflict notification service for sync conflict UI
final conflictNotificationService = ConflictNotificationService();
```

### Provider Registration

Add `ConflictNotificationService` as a `Provider` in the `WeredaConnectApp`'s `MultiProvider`:

```dart
// In WeredaConnectApp constructor, add parameter:
final ConflictNotificationService conflictNotificationService;

// In WeredaConnectApp.build(), add to MultiProvider.providers:
Provider<ConflictNotificationService>.value(
  value: conflictNotificationService,
  dispose: (_, service) => service.dispose(),
),
```

### BLoC Registration

Add `ConflictBloc` to the `MultiBlocProvider`:

```dart
BlocProvider<ConflictBloc>(
  create: (context) => ConflictBloc(
    notificationService: context.read<ConflictNotificationService>(),
  ),
),
```

### WeredaConnectApp Constructor Update

```dart
const WeredaConnectApp({
  required this.connectivityMonitor,
  required this.escrowSyncService,
  required this.conflictNotificationService,  // NEW
  super.key,
});
```

### runApp Call Update

```dart
runApp(
  MultiProvider(
    providers: [
      ChangeNotifierProvider.value(value: languageProvider),
      ChangeNotifierProvider.value(value: settingsProvider),
      ChangeNotifierProvider.value(value: dataUsageTracker),
    ],
    child: WeredaConnectApp(
      connectivityMonitor: connectivityMonitor,
      escrowSyncService: escrowSyncService,
      conflictNotificationService: conflictNotificationService,  // NEW
    ),
  ),
);
```

### Imports to Add to main.dart

```dart
import 'package:wereda_connect/features/conflict_resolution/data/services/conflict_notification_service.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
```

---

## 9. SyncExecutor Wiring (at initialization site)

Currently, `SyncExecutor` is created somewhere during app initialization (likely in a service locator or provider). Wherever it is created, pass the `conflictNotificationService`:

```dart
SyncExecutor(
  syncQueue: syncQueue,
  connectivityMonitor: connectivityMonitor,
  dio: ApiClient().dio,
  clientId: clientId,
  conflictResolver: ConflictResolver(IsarService.instance.db),
  conflictNotificationService: conflictNotificationService,  // NEW
);
```

> **Note for mobile-agent**: Search for existing `SyncExecutor` instantiation sites and add the `conflictNotificationService` parameter. If `SyncExecutor` is not yet instantiated in the app (only in tests), you will need to create it in `main.dart` or a service locator and pass it to `SyncWorker`.

---

## 10. Design Decisions & Trade-offs

### Why modal bottom sheet (not full screen)?
- Conflicts are informational — the resolution already happened
- A bottom sheet is less jarring than a full-screen interruption
- Users can swipe to dismiss (natural gesture)
- Works well on low-end devices (minimal rendering overhead)

### Why notify after resolve (not before)?
- The "server wins" strategy is automatic — user approval is not needed
- Showing the notification after resolution means the local data is already consistent
- If we ever add "manual merge" in the future, we'd change this to notify before resolve

### Why a separate ConflictNotificationService (not just stream on ConflictResolver)?
- ConflictResolver is a pure data layer class (Isar operations only)
- Notification is a cross-cutting concern between data and presentation
- The service can be replaced/mocked independently in tests
- Clean dependency inversion: SyncExecutor depends on service, not on UI

### Why listen in AuthGate (not MainNavigation)?
- Ensures conflicts are only shown when user is authenticated
- MainNavigation already has complex state; adding BlocListener there increases coupling
- AuthGate is the natural "authenticated shell" boundary

---

## 11. Testing Strategy

### Unit Tests
- `ConflictNotificationService`: verify stream broadcasts, dispose behavior
- `ConflictBloc`: verify state transitions for all events
- `ConflictEvent` model: verify equality

### Widget Tests
- `ConflictResolutionScreen`: verify renders entity type, fields, buttons
- Verify "OK, Got It" dispatches `AcceptServerVersion`
- Verify "Dismiss" dispatches `DismissConflict`

### Integration Tests
- Wire `ConflictNotificationService` → `ConflictBloc` → verify UI shows modal
- Simulate `SyncExecutor` conflict case → verify notification fires

---

## 12. Future Considerations (Out of Scope for F-15)

- **Manual merge UI**: Let users choose between local and server values per field
- **Conflict history**: Store resolved conflicts for audit trail
- **Push notification**: Alert user of conflicts even when app is backgrounded
- **Conflict prevention**: Optimistic locking with version checks before sync

---

**Last Updated**: 2026-02-21
