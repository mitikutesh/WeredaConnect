# F-13 Mobile Escrow Architecture — Implementation Design

**Version**: 1.0.0
**Date**: 2026-02-19
**Author**: Architect Agent
**Status**: Draft (awaiting implementation)

---

## Table of Contents

1. [Overview](#1-overview)
2. [File Structure](#2-file-structure)
3. [Data Layer](#3-data-layer)
4. [BLoC Design](#4-bloc-design)
5. [Screen Designs](#5-screen-designs)
6. [Offline Strategy](#6-offline-strategy)
7. [API Integration](#7-api-integration)
8. [Localization Keys](#8-localization-keys)
9. [Security Requirements](#9-security-requirements)

---

## 1. Overview

### What This Document Covers

The mobile (Flutter) side of F-13 Telebirr Escrow: data models, BLoC state management, screens, offline intent queuing, API integration, l10n, and security. The server side is already implemented (mock Telebirr gateway, escrow service, DynamoDB, Lambda).

### Key Architectural Decisions

| Decision | Rationale |
|----------|-----------|
| **Server-authoritative mutations** | Money movement requires Telebirr + server. Escrow mutations go through dedicated `/escrow/*` endpoints, NOT the sync queue (`POST /sync`). |
| **Offline intent queue** | Payment INTENT can be queued in Isar when offline. Actual fund movement requires connectivity. |
| **Separate PaymentIntent schema** | Keeps the intent queue (local-only, transient) separate from the Payment cache (mirrors server state). |
| **Online-first repository** | Unlike trip_listing/delivery_verification (which use SyncQueue), escrow repository calls API directly when online, falls back to intent queue when offline. |

### Reference Implementations

- **trip_listing**: BLoC pattern, repository with SyncQueue, screen/widget conventions
- **delivery_verification**: Complex BLoC with 11 events/states, dispute flow, QR verification services, photo evidence

---

## 2. File Structure

### New Files to Create

```
mobile/lib/features/escrow_payment/
├── data/
│   ├── models/
│   │   └── payment_intent.dart              # Isar schema for offline intent queue
│   ├── repositories/
│   │   └── escrow_repository.dart           # Online-first repo with intent fallback
│   └── services/
│       └── escrow_sync_service.dart         # Processes queued intents on connectivity
├── presentation/
│   ├── bloc/
│   │   ├── escrow_bloc.dart                 # BLoC implementation
│   │   ├── escrow_event.dart                # All events
│   │   └── escrow_state.dart                # All states
│   ├── screens/
│   │   ├── initiate_payment_screen.dart     # Buyer initiates escrow
│   │   ├── escrow_status_screen.dart        # View single escrow details
│   │   ├── payment_history_screen.dart      # List of user's escrow transactions
│   │   └── raise_dispute_screen.dart        # Raise dispute on escrowed payment
│   └── widgets/
│       ├── escrow_status_badge.dart         # Color-coded status icon + label
│       ├── amount_display.dart              # ETB-formatted amount (large, accessible)
│       ├── escrow_card.dart                 # List card for payment history
│       └── commission_breakdown.dart        # Shows amount, commission, farmer payout
```

### Existing Files to Modify

| File | Change |
|------|--------|
| `mobile/lib/core/database/schemas/payment.dart` | Add escrow-specific fields: `commission`, `commissionRate`, `farmerPayout`, `expiresAt`, `disputeWindowEndsAt`, `releasedAt`, `refundedAt`, `completedAt`, `disputeReason`, `disputeDescription`, `updatedAt`; add `initiated`, `completed`, `cancelled` to `PaymentStatus` enum |
| `mobile/lib/core/database/isar_service.dart` | Register `PaymentIntentSchema` in `Isar.open()` schema list |
| `mobile/lib/shared/l10n/app_en.arb` | Add ~55 escrow l10n keys |
| `mobile/lib/shared/l10n/app_am.arb` | Add ~55 Amharic translations |
| `mobile/lib/shared/l10n/app_om.arb` | Add ~55 Afan Oromo translations |
| `mobile/lib/core/network/api_config.dart` | Add escrow endpoint constants |

---

## 3. Data Layer

### 3.1 Payment Isar Schema (Updated)

Update `mobile/lib/core/database/schemas/payment.dart`:

```dart
@collection
class Payment {
  Id id = Isar.autoIncrement;

  @Index(unique: true)
  late String paymentId;          // Maps to server escrowId

  @Index()
  late String deliveryId;

  late String farmerId;
  late String buyerId;

  /// Total amount in ETB
  late double amount;

  /// Platform commission in ETB: max(amount * commissionRate, 5.0)
  late double commission;

  /// Commission rate (server-configured, default 0.025)
  late double commissionRate;

  /// Net amount farmer receives: amount - commission
  late double farmerPayout;

  /// Telebirr transaction reference (null until Telebirr confirms hold)
  String? telebirrRef;

  @Index()
  @Enumerated(EnumType.name)
  late PaymentStatus status;

  /// UTC timestamp when escrow was created
  late DateTime createdAt;

  /// UTC timestamp of last server state change
  late DateTime updatedAt;

  /// Auto-refund deadline (createdAt + 7 days)
  DateTime? expiresAt;

  /// Dispute deadline (deliveryConfirmedAt + 48 hours)
  DateTime? disputeWindowEndsAt;

  /// When funds were released to farmer
  DateTime? releasedAt;

  /// When funds were refunded to buyer
  DateTime? refundedAt;

  /// When transaction fully settled
  DateTime? completedAt;

  /// Dispute reason enum value (if disputed)
  String? disputeReason;

  /// Dispute free-text description
  String? disputeDescription;

  /// Optimistic concurrency control
  late int version;
}

enum PaymentStatus {
  /// Payment intent submitted; Telebirr hold pending
  initiated,

  /// Funds held in platform Telebirr account
  escrowed,

  /// Funds released to farmer (minus commission)
  released,

  /// Transaction fully settled
  completed,

  /// Buyer cancelled before funds were held
  cancelled,

  /// Dispute raised within 48h of delivery
  disputed,

  /// Funds returned to buyer
  refunded,
}
```

**Migration note**: The existing Payment schema only has `escrowed`, `released`, `disputed`, `refunded`. Adding `initiated`, `completed`, `cancelled` is a non-breaking Isar schema change (new enum values are additive). The new fields (`commission`, `farmerPayout`, etc.) default to 0 / null for any existing records.

### 3.2 PaymentIntent Isar Schema (New)

Create `mobile/lib/features/escrow_payment/data/models/payment_intent.dart`:

```dart
@collection
class PaymentIntent {
  Id id = Isar.autoIncrement;

  /// Client-generated idempotency key.
  /// Format: {buyerId}:{deliveryId}:initiate:{timestamp}
  @Index(unique: true)
  late String idempotencyKey;

  late String deliveryId;
  late String buyerId;
  late String farmerId;

  /// Amount in ETB (100-500,000)
  late double amount;

  @Index()
  @Enumerated(EnumType.name)
  late IntentStatus status;

  /// When the intent was created (UTC)
  late DateTime createdAt;

  /// Number of sync attempts (for backoff)
  int retryCount = 0;

  /// Last sync attempt time (for exponential backoff)
  DateTime? lastAttemptAt;

  /// Error message from last failed attempt
  String? lastError;
}

enum IntentStatus {
  /// Queued locally, waiting for connectivity
  pendingSync,

  /// Currently being sent to server
  syncing,

  /// Server confirmed, escrow created
  completed,

  /// Server rejected (validation error, etc.)
  failed,

  /// User cancelled before sync
  cancelled,
}
```

**Registration**: Add `PaymentIntentSchema` to the Isar.open() call in `isar_service.dart`.

### 3.3 DisputeIntent — Not a Separate Schema

Dispute intents are simpler (no financial data to queue), so they are handled inline in the repository: if offline when raising a dispute, show an error message asking the user to retry when online. Disputes are time-sensitive (48h window) and the server enforces the window — queueing offline could lead to expired disputes being sent. This is a deliberate tradeoff.

### 3.4 EscrowRepository

Create `mobile/lib/features/escrow_payment/data/repositories/escrow_repository.dart`:

```dart
class EscrowRepository {
  final Isar _isar;
  final ApiClient _apiClient;
  final ConnectivityMonitor _connectivityMonitor;

  EscrowRepository({
    required Isar isar,
    required ApiClient apiClient,
    required ConnectivityMonitor connectivityMonitor,
  }) : _isar = isar,
       _apiClient = apiClient,
       _connectivityMonitor = connectivityMonitor;

  // ── Mutations (online-first, intent fallback) ──

  /// Initiate escrow payment.
  /// Online: POST /escrow/initiate → cache response in Payment.
  /// Offline: Save PaymentIntent with pendingSync status.
  Future<EscrowResult> initiatePayment({
    required String deliveryId,
    required String buyerId,
    required String farmerId,
    required double amount,
  });

  /// Raise dispute on an escrowed payment.
  /// Online-only: POST /escrow/dispute → update local Payment.
  /// Offline: Return error (disputes are time-sensitive).
  Future<EscrowResult> raiseDispute({
    required String escrowId,
    required String disputedBy,
    required String reason,
    String? description,
    List<String>? evidenceRefs,
  });

  /// Cancel a pending_sync intent before it syncs.
  /// Local-only operation: updates PaymentIntent status.
  Future<void> cancelIntent(String idempotencyKey);

  // ── Reads (local cache with remote refresh) ──

  /// Get single escrow by ID. Local first, remote refresh if online.
  Future<Payment?> getEscrow(String escrowId);

  /// Get escrow by delivery ID (1:1 relationship).
  Future<Payment?> getEscrowByDelivery(String deliveryId);

  /// Get payment history. Local first, remote refresh if online.
  Future<List<Payment>> getHistory({
    String? statusFilter,
    String? roleFilter,
  });

  /// Refresh single escrow from server. Updates local cache.
  Future<Payment?> refreshEscrow(String escrowId);

  /// Refresh payment history from server. Updates local cache.
  Future<List<Payment>> refreshHistory({
    String? statusFilter,
    String? roleFilter,
    int limit = 20,
    String? nextToken,
  });

  // ── Intent queue management ──

  /// Get all pending intents for display.
  Future<List<PaymentIntent>> getPendingIntents();

  // ── Private helpers ──

  /// Generate idempotency key: {buyerId}:{deliveryId}:initiate:{timestamp}
  String _generateIdempotencyKey(String buyerId, String deliveryId);

  /// Validate amount (100-500,000 ETB), client-side pre-check.
  void _validateAmount(double amount);

  /// Map server JSON response to local Payment entity.
  Payment _mapServerResponse(Map<String, dynamic> json);
}
```

**Result type**:

```dart
sealed class EscrowResult {
  const EscrowResult();
}

class EscrowSuccess extends EscrowResult {
  final Payment payment;
  const EscrowSuccess(this.payment);
}

class EscrowQueued extends EscrowResult {
  final PaymentIntent intent;
  const EscrowQueued(this.intent);
}

class EscrowError extends EscrowResult {
  final String code;    // ESCROW_* error code
  final String message; // User-facing message
  const EscrowError(this.code, this.message);
}
```

### 3.5 EscrowSyncService

Create `mobile/lib/features/escrow_payment/data/services/escrow_sync_service.dart`:

```dart
/// Processes queued PaymentIntents when connectivity returns.
/// Registered as a listener on ConnectivityMonitor.
class EscrowSyncService {
  final EscrowRepository _repository;
  final Isar _isar;
  final ApiClient _apiClient;
  final ConnectivityMonitor _connectivityMonitor;

  StreamSubscription? _connectivitySubscription;

  /// Start listening for connectivity changes.
  void startListening();

  /// Stop listening (dispose).
  void dispose();

  /// Process all pending intents. Called on connectivity change.
  /// - Picks up intents with status=pendingSync
  /// - Sends each to POST /escrow/initiate
  /// - Updates intent status (completed/failed)
  /// - Creates local Payment cache on success
  /// - Exponential backoff on failure (5s, 15s, 45s)
  /// - Max 3 retries per intent before marking failed
  Future<void> processQueuedIntents();
}
```

**Backoff strategy**:
- Retry 1: immediate
- Retry 2: 5 seconds delay
- Retry 3: 15 seconds delay
- After 3 failures: mark intent as `failed`, user must manually retry

---

## 4. BLoC Design

### 4.1 Events

Create `mobile/lib/features/escrow_payment/presentation/bloc/escrow_event.dart`:

```dart
abstract class EscrowEvent extends Equatable {
  const EscrowEvent();
  @override
  List<Object?> get props => [];
}

/// Buyer initiates payment for a delivery.
/// Triggers online API call or offline intent queue.
class InitiatePayment extends EscrowEvent {
  final String deliveryId;
  final String buyerId;
  final String farmerId;
  final double amount;
  const InitiatePayment({
    required this.deliveryId,
    required this.buyerId,
    required this.farmerId,
    required this.amount,
  });
  @override
  List<Object?> get props => [deliveryId, buyerId, farmerId, amount];
}

/// Load a single escrow for the status screen.
class LoadEscrowDetail extends EscrowEvent {
  final String escrowId;
  const LoadEscrowDetail({required this.escrowId});
  @override
  List<Object?> get props => [escrowId];
}

/// Load escrow by delivery ID (from delivery detail screen).
class LoadEscrowByDelivery extends EscrowEvent {
  final String deliveryId;
  const LoadEscrowByDelivery({required this.deliveryId});
  @override
  List<Object?> get props => [deliveryId];
}

/// Load user's payment history.
class LoadPaymentHistory extends EscrowEvent {
  final String? statusFilter;
  final String? roleFilter;
  const LoadPaymentHistory({this.statusFilter, this.roleFilter});
  @override
  List<Object?> get props => [statusFilter, roleFilter];
}

/// Refresh single escrow from server.
class RefreshEscrow extends EscrowEvent {
  final String escrowId;
  const RefreshEscrow({required this.escrowId});
  @override
  List<Object?> get props => [escrowId];
}

/// Refresh payment history from server (pull-to-refresh).
class RefreshPaymentHistory extends EscrowEvent {
  final String? statusFilter;
  final String? roleFilter;
  const RefreshPaymentHistory({this.statusFilter, this.roleFilter});
  @override
  List<Object?> get props => [statusFilter, roleFilter];
}

/// Buyer or farmer raises a dispute on an escrowed payment.
class RaisePaymentDispute extends EscrowEvent {
  final String escrowId;
  final String disputedBy;
  final String reason;
  final String? description;
  final List<String>? evidenceRefs;
  const RaisePaymentDispute({
    required this.escrowId,
    required this.disputedBy,
    required this.reason,
    this.description,
    this.evidenceRefs,
  });
  @override
  List<Object?> get props =>
      [escrowId, disputedBy, reason, description, evidenceRefs];
}

/// Cancel a queued payment intent (before it syncs).
class CancelPaymentIntent extends EscrowEvent {
  final String idempotencyKey;
  const CancelPaymentIntent({required this.idempotencyKey});
  @override
  List<Object?> get props => [idempotencyKey];
}

/// Filter payment history by status.
class FilterPaymentHistory extends EscrowEvent {
  final String? statusFilter;
  final String? roleFilter;
  const FilterPaymentHistory({this.statusFilter, this.roleFilter});
  @override
  List<Object?> get props => [statusFilter, roleFilter];
}

/// Load pending (queued) payment intents.
class LoadPendingIntents extends EscrowEvent {
  const LoadPendingIntents();
}
```

### 4.2 States

Create `mobile/lib/features/escrow_payment/presentation/bloc/escrow_state.dart`:

```dart
abstract class EscrowState extends Equatable {
  const EscrowState();
  @override
  List<Object?> get props => [];
}

/// Initial state before any data is loaded.
class EscrowInitial extends EscrowState {
  const EscrowInitial();
}

/// Loading state during data operations.
class EscrowLoading extends EscrowState {
  const EscrowLoading();
}

/// Single escrow loaded (for status/detail screen).
class EscrowDetailLoaded extends EscrowState {
  final Payment payment;
  const EscrowDetailLoaded({required this.payment});
  @override
  List<Object?> get props => [payment];
}

/// Payment history loaded.
class PaymentHistoryLoaded extends EscrowState {
  final List<Payment> payments;
  final List<PaymentIntent> pendingIntents;
  final String? statusFilter;
  final String? roleFilter;
  const PaymentHistoryLoaded({
    required this.payments,
    this.pendingIntents = const [],
    this.statusFilter,
    this.roleFilter,
  });
  @override
  List<Object?> get props =>
      [payments, pendingIntents, statusFilter, roleFilter];
}

/// Payment initiation succeeded (server confirmed).
class PaymentInitiated extends EscrowState {
  final Payment payment;
  const PaymentInitiated({required this.payment});
  @override
  List<Object?> get props => [payment];
}

/// Payment intent queued (offline, will sync later).
class PaymentQueued extends EscrowState {
  final PaymentIntent intent;
  const PaymentQueued({required this.intent});
  @override
  List<Object?> get props => [intent];
}

/// Dispute raised successfully.
class DisputeRaisedSuccess extends EscrowState {
  final Payment payment;
  const DisputeRaisedSuccess({required this.payment});
  @override
  List<Object?> get props => [payment];
}

/// Payment intent cancelled successfully.
class IntentCancelled extends EscrowState {
  const IntentCancelled();
}

/// Error state.
class EscrowError extends EscrowState {
  final String code;
  final String message;
  const EscrowError({required this.code, required this.message});
  @override
  List<Object?> get props => [code, message];
}
```

### 4.3 State Transitions

```
                        ┌──────────────┐
                        │ EscrowInitial│
                        └──────┬───────┘
                               │
              ┌────────────────┼────────────────┐
              │                │                │
     InitiatePayment    LoadEscrowDetail   LoadPaymentHistory
              │                │                │
              ▼                ▼                ▼
       EscrowLoading    EscrowLoading    EscrowLoading
              │                │                │
         ┌────┴────┐          │           ┌────┴────┐
         │         │          │           │         │
    [online]  [offline]       │      [success]  [error]
         │         │          │           │         │
         ▼         ▼          ▼           ▼         ▼
  PaymentInitiated PaymentQueued  EscrowDetailLoaded  EscrowError
                                  PaymentHistoryLoaded

  RaisePaymentDispute ──► EscrowLoading ──► DisputeRaisedSuccess
                                        ──► EscrowError (offline/server)

  CancelPaymentIntent ──► IntentCancelled

  RefreshEscrow ──► EscrowDetailLoaded (silent refresh, no loading state)
  RefreshPaymentHistory ──► PaymentHistoryLoaded (silent refresh)
```

### 4.4 BLoC Implementation

Create `mobile/lib/features/escrow_payment/presentation/bloc/escrow_bloc.dart`:

```dart
class EscrowBloc extends Bloc<EscrowEvent, EscrowState> {
  final EscrowRepository _repository;

  EscrowBloc({required EscrowRepository repository})
      : _repository = repository,
        super(const EscrowInitial()) {
    on<InitiatePayment>(_onInitiatePayment);
    on<LoadEscrowDetail>(_onLoadEscrowDetail);
    on<LoadEscrowByDelivery>(_onLoadEscrowByDelivery);
    on<LoadPaymentHistory>(_onLoadPaymentHistory);
    on<RefreshEscrow>(_onRefreshEscrow);
    on<RefreshPaymentHistory>(_onRefreshPaymentHistory);
    on<RaisePaymentDispute>(_onRaiseDispute);
    on<CancelPaymentIntent>(_onCancelIntent);
    on<FilterPaymentHistory>(_onFilterHistory);
    on<LoadPendingIntents>(_onLoadPendingIntents);
  }

  Future<void> _onInitiatePayment(
    InitiatePayment event, Emitter<EscrowState> emit,
  ) async {
    emit(const EscrowLoading());
    try {
      final result = await _repository.initiatePayment(
        deliveryId: event.deliveryId,
        buyerId: event.buyerId,
        farmerId: event.farmerId,
        amount: event.amount,
      );
      switch (result) {
        case EscrowSuccess(:final payment):
          emit(PaymentInitiated(payment: payment));
        case EscrowQueued(:final intent):
          emit(PaymentQueued(intent: intent));
        case EscrowError(:final code, :final message):
          emit(EscrowError(code: code, message: message));
      }
    } catch (e) {
      emit(EscrowError(code: 'ESCROW_CLIENT', message: e.toString()));
    }
  }

  // ... remaining handlers follow same pattern:
  //   emit(Loading) → call repository → emit(Success/Error)
}
```

---

## 5. Screen Designs

### 5.1 InitiatePaymentScreen

**Route**: Navigated from delivery detail screen (buyer role, delivery in `pending` or `pickedUp` state).

**Input params**: `deliveryId`, `buyerId`, `farmerId`, `farmerName`, `amount` (pre-calculated from listing).

**Layout**:

```
┌──────────────────────────────┐
│ ← Pay for Delivery           │  AppBar
├──────────────────────────────┤
│                              │
│  ┌────────────────────────┐  │
│  │ 🛡️  Escrow Protection  │  │  Shield icon, trust signal
│  │ Your payment is held   │  │
│  │ safely until delivery  │  │
│  │ is confirmed.          │  │
│  └────────────────────────┘  │
│                              │
│  Delivery #abc123de          │  First 8 chars
│  To: Alemayehu Bekele        │  Farmer name
│                              │
│  ┌────────────────────────┐  │
│  │ Total Amount            │  │
│  │   ETB 15,000.00        │  │  Large, bold
│  ├────────────────────────┤  │
│  │ Commission (2.5%)       │  │
│  │   ETB 375.00           │  │  Smaller, grey
│  ├────────────────────────┤  │
│  │ Farmer Receives         │  │
│  │   ETB 14,625.00        │  │  Green
│  └────────────────────────┘  │
│                              │
│  ⚠️ Fayda ID required for   │  Only shown if amount > 10,000
│  amounts above ETB 10,000    │
│                              │
│  ┌────────────────────────┐  │
│  │                        │  │
│  │  Confirm Payment ✓     │  │  Large green button
│  │                        │  │
│  └────────────────────────┘  │
│                              │
│  ⓘ Funds held 7 days max.   │  Info text, small
│    Refunded if not delivered.│
└──────────────────────────────┘
```

**Interactions**:
1. Tap "Confirm Payment" → confirmation dialog: "Pay ETB 15,000 to Alemayehu Bekele?"
2. On confirm → `InitiatePayment` event → loading spinner on button
3. On `PaymentInitiated` → success snackbar + navigate to EscrowStatusScreen
4. On `PaymentQueued` → info snackbar "Payment queued. Will process when online." + pop
5. On `EscrowError` → error snackbar with localized message
6. Fayda warning shown only when `amount > 10000`, as advisory (server enforces)

**Accessibility**:
- Amount display: 28sp font, bold, high contrast
- Green confirm button: 56dp height, full width
- Commission breakdown: card with clear visual separation
- Shield icon: 48dp, trust-building for first-time users

### 5.2 EscrowStatusScreen

**Route**: Navigated from payment history (tap card) or after initiating payment.

**Input params**: `escrowId` (or `deliveryId` for lookup).

**Layout**:

```
┌──────────────────────────────┐
│ ← Payment Status             │  AppBar with refresh action
├──────────────────────────────┤
│                              │
│  ┌────────────────────────┐  │
│  │  🟡 ESCROWED           │  │  Color-coded status badge
│  │  Funds held safely     │  │  (large, centered)
│  └────────────────────────┘  │
│                              │
│  ──── Amount ────            │
│  ┌────────────────────────┐  │
│  │ Total:    ETB 15,000   │  │  CommissionBreakdown widget
│  │ Fee:      ETB 375      │  │
│  │ Farmer:   ETB 14,625   │  │
│  └────────────────────────┘  │
│                              │
│  ──── Details ────           │
│  📦 Delivery  #abc123de     │
│  👤 Farmer    Alemayehu B.  │
│  📅 Created   Feb 17, 2026  │
│  ⏰ Expires   Feb 24, 2026  │
│  🔗 Telebirr TBR-202602... │  (only if telebirrRef exists)
│                              │
│  ──── Timeline ────          │
│  ● Initiated  Feb 17 10:00  │  Status timeline
│  ● Escrowed   Feb 17 10:01  │  (current step highlighted)
│  ○ Released                  │
│  ○ Completed                 │
│                              │
│  ──── Dispute ────           │  (only if status==disputed)
│  ⚠️ Disputed by buyer       │
│  Reason: damaged_goods       │
│  "Half the teff bags were    │
│   wet and moldy."            │
│                              │
│  ┌────────────────────────┐  │  (only if status==escrowed
│  │  ⚠️ Raise Dispute      │  │   AND within 48h window)
│  └────────────────────────┘  │
│                              │
│  ⓘ Last updated 5 min ago   │  Stale indicator if > 1 hour
└──────────────────────────────┘
```

**Interactions**:
1. Pull-to-refresh → `RefreshEscrow` event → silent refresh
2. "Raise Dispute" button → navigate to RaiseDisputeScreen
3. Stale data warning if `updatedAt` > 1 hour ago
4. Auto-refresh on screen focus (via `RouteAware` or `WidgetsBindingObserver`)

**Status badge colors** (matching delivery_verification pattern):

| Status | Color | Icon | Label |
|--------|-------|------|-------|
| `initiated` | Grey | hourglass_empty | Payment Pending |
| `escrowed` | Amber/Yellow | shield | Funds Held Safely |
| `released` | Blue | send | Funds Released |
| `completed` | Green | check_circle | Payment Complete |
| `cancelled` | Grey | cancel | Cancelled |
| `disputed` | Red/Orange | warning | Under Dispute |
| `refunded` | Teal | replay | Refunded |

### 5.3 PaymentHistoryScreen

**Route**: Navigated from main navigation (payments tab or profile section).

**Layout**:

```
┌──────────────────────────────┐
│ My Payments                  │  AppBar
├──────────────────────────────┤
│ ┌──────────────────────────┐ │
│ │ [All] [Buyer] [Farmer]   │ │  Role filter chips
│ └──────────────────────────┘ │
│ ┌──────────────────────────┐ │
│ │[All][Escrowed][Released] │ │  Status filter chips
│ │[Disputed][Refunded]      │ │  (horizontal scroll)
│ └──────────────────────────┘ │
│                              │
│ ── Pending (2) ──            │  Section: queued intents
│ ┌────────────────────────┐   │  (only shown if intents exist)
│ │ 🔄 ETB 5,000           │   │
│ │ Del. #xyz789 · Queued   │   │
│ │              [Cancel ✕] │   │
│ └────────────────────────┘   │
│                              │
│ ── History ──                │  Section: server-confirmed
│ ┌────────────────────────┐   │
│ │ 🟢 ETB 15,000          │   │  EscrowCard widget
│ │ Alemayehu B. · Feb 17  │   │
│ │ Completed ✓             │   │
│ └────────────────────────┘   │
│ ┌────────────────────────┐   │
│ │ 🟡 ETB 8,500           │   │
│ │ Kebede T. · Feb 19     │   │
│ │ Escrowed 🛡️            │   │
│ └────────────────────────┘   │
│                              │
│         Pull to refresh ↓    │
└──────────────────────────────┘
```

**Interactions**:
1. Role filter chips → `FilterPaymentHistory` event
2. Status filter chips → `FilterPaymentHistory` event
3. Tap escrow card → navigate to EscrowStatusScreen
4. Tap cancel on pending intent → confirmation dialog → `CancelPaymentIntent` event
5. Pull-to-refresh → `RefreshPaymentHistory` event
6. On first load → `LoadPaymentHistory` + `LoadPendingIntents`

**Empty states**:
- No payments: wallet icon + "No payments yet"
- No results for filter: filter icon + "No matching payments"

### 5.4 RaiseDisputeScreen

**Route**: Navigated from EscrowStatusScreen (escrow in `escrowed` state, within 48h of delivery).

**Input params**: `escrowId`, `deliveryConfirmedAt`, `disputeWindowEndsAt`, `amount`.

**Layout**:

```
┌──────────────────────────────┐
│ ← Raise Dispute              │  AppBar
├──────────────────────────────┤
│                              │
│  Payment: ETB 15,000         │  Reference info
│  Delivery: #abc123de         │
│                              │
│  ⏰ Time remaining: 23h 45m  │  Countdown (orange)
│  ── or ──                    │
│  ❌ Dispute window expired   │  Red banner (disabled form)
│                              │
│  ──── Reason ────            │
│  ┌────────────────────────┐  │
│  │ ○ Damaged goods        │  │
│  │ ○ Wrong quantity       │  │  Radio buttons
│  │ ○ Wrong crop           │  │  (large touch targets)
│  │ ○ Not delivered        │  │
│  │ ○ Quality mismatch     │  │
│  │ ○ Other                │  │
│  └────────────────────────┘  │
│                              │
│  ──── Description ────       │  (optional, max 1000 chars)
│  ┌────────────────────────┐  │
│  │ Describe the issue...  │  │  TextFormField
│  │                        │  │  (multiline, 3-5 lines)
│  └────────────────────────┘  │
│  0/1000                      │  Character counter
│                              │
│  ┌────────────────────────┐  │
│  │  Submit Dispute ⚠️     │  │  Orange button
│  └────────────────────────┘  │
│                              │
│  ⓘ Disputes are reviewed by │  Info text
│  the platform within 48h.   │
└──────────────────────────────┘
```

**Interactions**:
1. Select reason (required) → enables submit button
2. Type description (optional, 0-1000 chars)
3. Tap "Submit Dispute" → confirmation dialog: "This will freeze funds pending review. Continue?"
4. On confirm → `RaisePaymentDispute` event → loading spinner
5. On `DisputeRaisedSuccess` → success snackbar + pop to EscrowStatusScreen
6. On `EscrowError` → error snackbar
7. If offline → error snackbar "Internet required to raise disputes"
8. If window expired → form disabled, red banner, submit button greyed out

**Evidence**: Photo evidence upload is deferred to a future iteration. The `evidenceRefs` parameter is accepted by the BLoC but the UI does not expose it yet. When added, it will use the same PhotoEvidenceWidget from delivery_verification.

### 5.5 Navigation Map

```
DeliveryDetailScreen
  │ (buyer, delivery pending/pickedUp)
  ├──► InitiatePaymentScreen
  │      │ (success)
  │      └──► EscrowStatusScreen
  │
  │ (view existing escrow for this delivery)
  └──► EscrowStatusScreen
         │ (escrowed, within 48h window)
         └──► RaiseDisputeScreen

MainNavigation / ProfileScreen
  └──► PaymentHistoryScreen
         │ (tap card)
         └──► EscrowStatusScreen
```

---

## 6. Offline Strategy

### 6.1 Intent Queue Flow

```
┌─────────────────────────────────────────────────────────┐
│                    ONLINE PATH                            │
│                                                           │
│  User taps "Pay" → Validate → POST /escrow/initiate      │
│                                       │                   │
│                              ┌────────┴────────┐         │
│                              │                 │         │
│                          [201 Created]    [4xx/5xx]      │
│                              │                 │         │
│                         Save Payment      Show Error     │
│                         to Isar cache     to user        │
│                              │                           │
│                         Emit PaymentInitiated            │
└─────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────┐
│                    OFFLINE PATH                            │
│                                                           │
│  User taps "Pay" → Validate → No connectivity detected    │
│                                       │                   │
│                              Save PaymentIntent           │
│                              to Isar (pendingSync)        │
│                                       │                   │
│                              Emit PaymentQueued           │
│                              "Payment queued"             │
│                                       │                   │
│                          ┌────────────┴──────────┐       │
│                          │                       │       │
│                    [Connectivity              [User      │
│                     restored]                cancels]    │
│                          │                       │       │
│                   EscrowSyncService         Mark intent  │
│                   picks up intent           cancelled    │
│                          │                               │
│                  POST /escrow/initiate                    │
│                          │                               │
│                  ┌───────┴──────┐                        │
│                  │              │                        │
│              [success]     [failure]                     │
│                  │              │                        │
│             Update intent  Retry with                    │
│             → completed    backoff (max 3)               │
│             Cache Payment  then mark failed              │
│             Show notification                            │
└─────────────────────────────────────────────────────────┘
```

### 6.2 Cache Refresh Strategy

| Data | Cache Location | Refresh Trigger | Stale Threshold |
|------|---------------|-----------------|-----------------|
| Single escrow | Payment (Isar) | Screen focus, pull-to-refresh, 5-min timer | 1 hour (show "may be outdated") |
| Payment history | Payment[] (Isar) | Screen load, pull-to-refresh | 1 hour |
| Pending intents | PaymentIntent (Isar) | Real-time (local only) | N/A (always fresh) |

### 6.3 Conflict Resolution

Escrow is server-authoritative. If a local cache and server disagree:
- **Server wins**: Server response always overwrites local Payment cache.
- **Version tracking**: `Payment.version` is updated from server response; local version is never incremented by the client.
- **Stale reads are safe**: Showing an outdated status (e.g., "escrowed" when server has "released") is acceptable — it's informational, not actionable. The next refresh corrects it.

### 6.4 What Works Offline

| Action | Offline Behavior |
|--------|-----------------|
| View escrow status | Shows cached data with "may be outdated" if stale |
| View payment history | Shows cached list from last refresh |
| Initiate payment | Intent queued locally, synced when online |
| Raise dispute | **Not supported offline** — disputes are time-sensitive (48h window), server must validate |
| Cancel queued intent | Supported — local-only operation |

---

## 7. API Integration

### 7.1 Endpoint Mapping

Add to `api_config.dart`:

```dart
// Escrow endpoints
static const String escrowInitiate = '/escrow/initiate';
static const String escrowDispute = '/escrow/dispute';
static const String escrowStatus = '/escrow';        // + /{id}
static const String escrowHistory = '/escrow/history';
```

### 7.2 Request/Response Mapping

#### POST /escrow/initiate

```dart
// Request
final response = await _apiClient.post<Map<String, dynamic>>(
  ApiConfig.escrowInitiate,
  data: {
    'deliveryId': deliveryId,
    'buyerId': buyerId,
    'farmerId': farmerId,
    'amount': amount,
    'currency': 'ETB',
    'idempotencyKey': idempotencyKey,
  },
);

// Response → Payment
Payment _mapInitiateResponse(Map<String, dynamic> json) => Payment()
  ..paymentId = json['escrowId']
  ..deliveryId = json['deliveryId']
  ..buyerId = json['buyerId']
  ..farmerId = json['farmerId']
  ..amount = (json['amount'] as num).toDouble()
  ..commission = (json['commission'] as num).toDouble()
  ..commissionRate = 0.025  // server doesn't return rate in initiate response
  ..farmerPayout = (json['farmerPayout'] as num).toDouble()
  ..telebirrRef = json['telebirrRef']
  ..status = PaymentStatus.values.byName(json['status'])
  ..createdAt = DateTime.parse(json['createdAt'])
  ..updatedAt = DateTime.parse(json['createdAt'])
  ..expiresAt = DateTime.parse(json['expiresAt'])
  ..version = 1;
```

#### GET /escrow/{id}

```dart
final response = await _apiClient.get<Map<String, dynamic>>(
  '${ApiConfig.escrowStatus}/$escrowId',
);

// Response → Payment (full mapping including dispute fields)
Payment _mapDetailResponse(Map<String, dynamic> json) => Payment()
  ..paymentId = json['escrowId']
  ..deliveryId = json['deliveryId']
  ..buyerId = json['buyerId']
  ..farmerId = json['farmerId']
  ..amount = (json['amount'] as num).toDouble()
  ..commission = (json['commission'] as num).toDouble()
  ..commissionRate = (json['commissionRate'] as num).toDouble()
  ..farmerPayout = (json['farmerPayout'] as num).toDouble()
  ..telebirrRef = json['telebirrRef']
  ..status = PaymentStatus.values.byName(json['status'])
  ..createdAt = DateTime.parse(json['createdAt'])
  ..updatedAt = DateTime.parse(json['updatedAt'])
  ..expiresAt = DateTime.parse(json['expiresAt'])
  ..disputeWindowEndsAt = json['disputeWindowEndsAt'] != null
      ? DateTime.parse(json['disputeWindowEndsAt'])
      : null
  ..releasedAt = json['releasedAt'] != null
      ? DateTime.parse(json['releasedAt'])
      : null
  ..refundedAt = json['refundedAt'] != null
      ? DateTime.parse(json['refundedAt'])
      : null
  ..completedAt = json['completedAt'] != null
      ? DateTime.parse(json['completedAt'])
      : null
  ..disputeReason = json['dispute']?['reason']
  ..disputeDescription = json['dispute']?['description']
  ..version = json['version'];
```

#### GET /escrow/history

```dart
final response = await _apiClient.get<Map<String, dynamic>>(
  ApiConfig.escrowHistory,
  queryParameters: {
    if (statusFilter != null) 'status': statusFilter,
    if (roleFilter != null) 'role': roleFilter,
    'limit': limit.toString(),
    if (nextToken != null) 'nextToken': nextToken,
  },
);

// Response → List<Payment> (from escrows array)
final escrows = (response['escrows'] as List)
    .map((json) => _mapSummaryResponse(json))
    .toList();
```

#### POST /escrow/dispute

```dart
final idempotencyKey =
    '$disputedBy:$escrowId:dispute:${DateTime.now().millisecondsSinceEpoch}';

final response = await _apiClient.post<Map<String, dynamic>>(
  ApiConfig.escrowDispute,
  data: {
    'escrowId': escrowId,
    'disputedBy': disputedBy,
    'reason': reason,
    if (description != null) 'description': description,
    if (evidenceRefs != null) 'evidenceRefs': evidenceRefs,
    'idempotencyKey': idempotencyKey,
  },
);
```

### 7.3 Error Handling

Map server ESCROW_* error codes to user-facing messages via l10n:

| Error Code | l10n Key | User Message (EN) |
|------------|----------|-------------------|
| `ESCROW_001` | `escrowErrorInvalidAmount` | "Amount must be between ETB 100 and ETB 500,000." |
| `ESCROW_002` | `escrowErrorInvalidDelivery` | "Invalid delivery reference." |
| `ESCROW_003` | `escrowErrorInvalidIdempotency` | "Payment request error. Please try again." |
| `ESCROW_005` | `escrowErrorAuthRequired` | "Please log in to continue." |
| `ESCROW_006` | `escrowErrorNotAuthorized` | "You are not authorized for this action." |
| `ESCROW_007` | `escrowErrorFaydaRequired` | "Fayda ID verification required for amounts above ETB 10,000." |
| `ESCROW_008` | `escrowErrorDeliveryNotFound` | "Delivery not found." |
| `ESCROW_009` | `escrowErrorAlreadyExists` | "Payment already exists for this delivery." |
| `ESCROW_010` | `escrowErrorInvalidDeliveryState` | "Delivery is not in a valid state for payment." |
| `ESCROW_013` | `escrowErrorGateway` | "Telebirr service error. Please try again." |
| `ESCROW_014` | `escrowErrorServiceUnavailable` | "Telebirr is currently unavailable. Please try later." |
| `ESCROW_016` | `escrowErrorNotFound` | "Payment not found." |
| `ESCROW_017` | `escrowErrorInvalidDisputeReason` | "Invalid dispute reason." |
| `ESCROW_018` | `escrowErrorDisputeWindowExpired` | "Dispute window has expired (48 hours)." |
| `ESCROW_019` | `escrowErrorDisputeNotParty` | "You are not a party to this payment." |
| `ESCROW_020` | `escrowErrorDisputeExists` | "A dispute already exists for this payment." |
| `ESCROW_INVALID_TRANSITION` | `escrowErrorInvalidTransition` | "This action is not available for the current payment status." |

**ApiException mapping** (in repository):

```dart
try {
  final response = await _apiClient.post(...);
  // ...
} on ValidationException catch (e) {
  final error = e.response?.data?['error'];
  return EscrowError(
    error?['code'] ?? 'ESCROW_CLIENT',
    error?['message'] ?? 'Validation error.',
  );
} on AuthException {
  return const EscrowError('ESCROW_005', 'Please log in.');
} on NetworkException {
  // Fall through to offline intent queue
} on TimeoutException {
  // Fall through to offline intent queue
}
```

### 7.4 Idempotency Key Generation

```dart
String _generateIdempotencyKey(String actor, String entityId, String action) {
  final timestamp = DateTime.now().toUtc().millisecondsSinceEpoch;
  return '$actor:$entityId:$action:$timestamp';
}
```

Idempotency keys are generated once and stored with the PaymentIntent. If the intent is retried (after failure), the same key is reused — this is critical for preventing double-charges on retry.

---

## 8. Localization Keys

### 8.1 Complete Key List (~55 keys)

Add to `app_en.arb`, `app_am.arb`, `app_om.arb`:

#### Screen Titles & Navigation
| Key | English Value |
|-----|---------------|
| `escrowPayments` | "Payments" |
| `escrowPayForDelivery` | "Pay for Delivery" |
| `escrowPaymentStatus` | "Payment Status" |
| `escrowPaymentHistory` | "My Payments" |
| `escrowRaiseDispute` | "Raise Dispute" |

#### Status Labels
| Key | English Value |
|-----|---------------|
| `escrowStatusInitiated` | "Payment Pending" |
| `escrowStatusEscrowed` | "Funds Held Safely" |
| `escrowStatusReleased` | "Funds Released" |
| `escrowStatusCompleted` | "Payment Complete" |
| `escrowStatusCancelled` | "Cancelled" |
| `escrowStatusDisputed` | "Under Dispute" |
| `escrowStatusRefunded` | "Refunded" |

#### Payment Initiation
| Key | English Value |
|-----|---------------|
| `escrowProtectionInfo` | "Your payment is held safely until delivery is confirmed." |
| `escrowTotalAmount` | "Total Amount" |
| `escrowCommission` | "Commission ({rate}%)" |
| `escrowFarmerReceives` | "Farmer Receives" |
| `escrowConfirmPayment` | "Confirm Payment" |
| `escrowConfirmPaymentDialog` | "Pay ETB {amount} to {farmerName}?" |
| `escrowFaydaWarning` | "Fayda ID verification required for amounts above ETB 10,000." |
| `escrowFundsHeldInfo` | "Funds held for 7 days maximum. Refunded if not delivered." |
| `escrowPaymentQueued` | "Payment queued. Will process when you have internet." |
| `escrowPaymentSuccess` | "Payment initiated successfully." |

#### Payment Status
| Key | English Value |
|-----|---------------|
| `escrowDeliveryLabel` | "Delivery" |
| `escrowFarmerLabel` | "Farmer" |
| `escrowBuyerLabel` | "Buyer" |
| `escrowCreatedLabel` | "Created" |
| `escrowExpiresLabel` | "Expires" |
| `escrowTelebirrRefLabel` | "Telebirr Ref" |
| `escrowMayBeOutdated` | "Status may be outdated. Pull to refresh." |
| `escrowLastUpdated` | "Last updated {time}" |

#### Payment History
| Key | English Value |
|-----|---------------|
| `escrowFilterAll` | "All" |
| `escrowFilterBuyer` | "As Buyer" |
| `escrowFilterFarmer` | "As Farmer" |
| `escrowPendingSection` | "Pending ({count})" |
| `escrowHistorySection` | "History" |
| `escrowNoPayments` | "No payments yet." |
| `escrowNoMatchingPayments` | "No matching payments." |
| `escrowCancelIntent` | "Cancel" |
| `escrowCancelIntentDialog` | "Cancel this queued payment?" |
| `escrowIntentCancelled` | "Queued payment cancelled." |

#### Dispute
| Key | English Value |
|-----|---------------|
| `escrowDisputeTimeRemaining` | "Time remaining: {hours}h {minutes}m" |
| `escrowDisputeWindowExpired` | "Dispute window expired." |
| `escrowDisputeReasonLabel` | "Reason" |
| `escrowDisputeDescriptionLabel` | "Description (optional)" |
| `escrowDisputeDescriptionHint` | "Describe the issue..." |
| `escrowDisputeSubmit` | "Submit Dispute" |
| `escrowDisputeConfirmDialog` | "This will freeze funds pending review. Continue?" |
| `escrowDisputeSuccess` | "Dispute registered. Platform will review within 48 hours." |
| `escrowDisputeReviewInfo` | "Disputes are reviewed by the platform within 48 hours." |
| `escrowDisputeRequiresInternet` | "Internet connection required to raise disputes." |

#### Dispute Reasons
| Key | English Value |
|-----|---------------|
| `escrowReasonDamagedGoods` | "Damaged goods" |
| `escrowReasonWrongQuantity` | "Wrong quantity" |
| `escrowReasonWrongCrop` | "Wrong crop type" |
| `escrowReasonNotDelivered` | "Not delivered" |
| `escrowReasonQualityMismatch` | "Quality mismatch" |
| `escrowReasonOther` | "Other" |

#### Errors
| Key | English Value |
|-----|---------------|
| `escrowErrorInvalidAmount` | "Amount must be between ETB 100 and ETB 500,000." |
| `escrowErrorFaydaRequired` | "Fayda ID verification required for amounts above ETB 10,000." |
| `escrowErrorAlreadyExists` | "Payment already exists for this delivery." |
| `escrowErrorGateway` | "Telebirr service error. Please try again." |
| `escrowErrorServiceUnavailable` | "Telebirr is currently unavailable. Please try later." |
| `escrowErrorGeneric` | "Something went wrong. Please try again." |

#### Currency Formatting
| Key | English Value |
|-----|---------------|
| `escrowEtbAmount` | "ETB {amount}" |

### 8.2 Key Naming Convention

Following the existing pattern:
- Prefix: `escrow` (feature namespace)
- Sub-category after prefix: `Status`, `Error`, `Reason`, `Filter`, etc.
- camelCase throughout
- Placeholder syntax: `{variableName}` with `@key.placeholders` metadata

---

## 9. Security Requirements

Based on the security review (`docs/security/r4-escrow-security-review.md`), these are the mobile-side security requirements:

### 9.1 Critical (from MUST FIX items)

| Req | Server Issue | Mobile Implication |
|-----|-------------|-------------------|
| **M1**: Idempotency race | Server needs conditional write | Mobile must store idempotency key BEFORE calling API and reuse it on retry. Never generate a new key for the same intent. |
| **M2**: Release/refund auth | Server needs authz check | Mobile never calls `/escrow/release` or `/escrow/refund` — these are system/admin only. UI should not expose these actions. |
| **M3**: Optimistic concurrency | Server needs condition expression | Mobile must send `version` with dispute requests if server adds version checks. For now, mobile trusts server response and overwrites local cache. |
| **M4**: Fayda ID threshold | Server needs enforcement | Mobile shows advisory warning for amounts > 10,000 ETB. Server is the enforcement point. Client-side check is UX only, not security. |

### 9.2 Mobile-Specific Security

| Concern | Mitigation |
|---------|-----------|
| **Sensitive data on screen** | Show minimal payment data. Never display full Telebirr account numbers. Mask phone numbers in history view. |
| **Shared device risk** | Payment confirmation requires explicit dialog tap. No auto-pay. Consider biometric/PIN confirmation in future iteration. |
| **Idempotency key leakage** | Keys contain user IDs and timestamps — store in Isar only, never log. Clear completed intents after 24 hours. |
| **Local cache tampering** | Payment cache is read-only from server perspective. Tampering local Isar data has no effect on server state — all mutations go through authenticated API. |
| **Amount validation** | Client-side validation (100-500,000 ETB) is UX only. Server enforces the real limits. Never trust client-submitted commission or farmerPayout values. |
| **Intent queue manipulation** | If a malicious user modifies a queued intent's amount before sync, the server re-validates. The idempotency key contains the original timestamp, so a modified intent with the same key would be detected as a duplicate (returning the original cached result). |

### 9.3 PII Handling

| Data | Mobile Handling |
|------|----------------|
| Phone numbers | Masked in UI (show last 4 digits). Never logged. |
| Fayda IDs | Never stored on device. Advisory check only. |
| Transaction amounts | Displayed to authorized user only (buyer or farmer on the escrow). |
| Telebirr references | Shown only on detail screen. Truncated in list view. |

### 9.4 Audit Trail (Client-Side)

The mobile client does NOT maintain its own audit trail. All audit entries are server-side (EscrowAuditTable). The client contributes to the audit trail by:
- Including `idempotencyKey` in every mutation request
- The server logs the client IP address (from API Gateway)
- The server records the actor (from JWT `sub` claim)

---

## Appendix A: Commission Calculation Reference

```
commission = max(amount * 0.025, 5.00)   // 2.5% with 5 ETB floor
farmerPayout = amount - commission

Examples:
  ETB 100     → commission: ETB 5.00   (floor applied)
  ETB 500     → commission: ETB 12.50
  ETB 15,000  → commission: ETB 375.00
  ETB 500,000 → commission: ETB 12,500.00
```

Mobile calculates commission for display purposes only (InitiatePaymentScreen). The server is authoritative for actual commission values.

## Appendix B: Server DTO → Mobile Model Field Mapping

| Server DTO Field (EscrowDto) | Mobile Payment Field |
|------------------------------|---------------------|
| `escrowId` | `paymentId` |
| `deliveryId` | `deliveryId` |
| `buyerId` | `buyerId` |
| `farmerId` | `farmerId` |
| `amount` | `amount` |
| `commission` | `commission` |
| `commissionRate` | `commissionRate` |
| `farmerPayout` | `farmerPayout` |
| `status` | `status` (enum mapping) |
| `telebirrRef` | `telebirrRef` |
| `createdAt` | `createdAt` |
| `updatedAt` | `updatedAt` |
| `expiresAt` | `expiresAt` |
| `disputeWindowEndsAt` | `disputeWindowEndsAt` |
| `releasedAt` | `releasedAt` |
| `refundedAt` | `refundedAt` |
| `completedAt` | `completedAt` |
| `dispute.reason` | `disputeReason` |
| `dispute.description` | `disputeDescription` |
| `version` | `version` |

**Fields NOT stored locally**: `telebirrHoldRef`, `telebirrReleaseRef`, `telebirrRefundRef`, `buyerFaydaVerified`, `farmerFaydaVerified`, `dispute.disputeId`, `dispute.evidenceRefs`, `dispute.resolution`, `dispute.resolvedBy`, `dispute.resolvedAt`. These are server-internal or admin-only fields.

## Appendix C: Isar Schema Registration

Update `isar_service.dart`:

```dart
_isar = await Isar.open(
  [
    UserSchema,
    CropListingSchema,
    TripSchema,
    DeliverySchema,
    PaymentSchema,          // Updated with new fields
    PaymentIntentSchema,    // NEW
    SyncOperationSchema,
  ],
  directory: dir.path,
  name: 'wereda_connect',
);
```

---

**Last Updated**: 2026-02-19
**Version**: 1.0.0
