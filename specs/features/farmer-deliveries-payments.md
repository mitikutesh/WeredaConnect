# Feature: Role-Based Navigation Redesign — Deliveries & Payments Tabs

**Status**: APPROVED
**Author**: AI-assisted
**Date**: 2026-03-01
**Phase**: R4 enhancement (builds on existing F-12 + F-13)
**Validated**: 2026-03-01 (2 rounds — architecture, requirements, screen compatibility)

---

## What

Redesign the bottom navigation from a generic 4-tab layout to a **role-specific 5-tab layout** that gives farmers, truckers, and buyers direct access to Deliveries and Payments — replacing the generic "Market" and "Orders" tabs.

### Current Navigation (all roles share the same 4 tabs)

```
[ Home ]  [ Market ]  [+FAB]  [ Orders ]  [ Profile ]
```

- Market → `BrowseListingsScreen` (always loads ALL active listings, header says "Market")
- Orders → `OrdersScreen` (mixed deliveries + payments in single list)
- FAB → Create listing (farmer) / Create trip (trucker) / Browse (buyer)

### Proposed Navigation (role-specific 5 tabs)

**Farmer:**
```
[ Home ]  [ Listings ]  [ Deliveries ]  [ Payments ]  [ Profile ]
```

**Trucker:**
```
[ Home ]  [ Trips ]  [ Deliveries ]  [ Payments ]  [ Profile ]
```

**Buyer:**
```
[ Home ]  [ Market ]  [ Deliveries ]  [ Payments ]  [ Profile ]
```

Key changes:
1. **Remove** the center-docked FAB and `CircularNotchedRectangle` — use standard `BottomNavigationBar` with 5 items
2. **Tab 2** is role-specific: farmer's own listings, trucker's trips, buyer's marketplace
3. **Tabs 3 & 4** are Deliveries and Payments — separate, dedicated screens for all roles
4. **Create action** (+ Sell / + Post Trip) moves to a FAB inside the Listings/Trips screen itself
5. **Home screen** quick actions updated to match new navigation

---

## Why

Ethiopian farmers need a clear mental model: **"My crops" → "Where are they?" → "Where is my money?"**

The current navigation forces farmers to find their deliveries and payments inside a combined "Orders" screen, which mixes two fundamentally different concerns. For low-literacy users on $50 phones, each bottom tab should answer one clear question:

| Tab | Farmer asks | Trucker asks | Buyer asks |
|-----|-------------|-------------|------------|
| Home | "What's my overview?" | "What's happening?" | "What's new?" |
| Tab 2 | "What am I selling?" | "What trips do I have?" | "What can I buy?" |
| Deliveries | "Where are my crops?" | "What am I carrying?" | "Where is my order?" |
| Payments | "Where is my money?" | "How much did I earn?" | "What have I paid?" |
| Profile | "My settings" | "My settings" | "My settings" |

---

## Files to Modify

| File | Change |
|------|--------|
| `mobile/lib/screens/main_navigation.dart` | Redesign from 4-tab BottomAppBar+FAB to 5-tab role-specific BottomNavigationBar. Wire role-specific Tab 2 and new Deliveries/Payments tabs. Remove center-docked FAB. |
| `mobile/lib/screens/home_screen.dart` | Update quick actions per role (4 tiles each). Fix `onNavigateToTab` indices for 5-tab layout. Fix notification handler `_NotifAction.ordersTab` at line 1488 → navigate to Deliveries (index 2). |
| `mobile/lib/features/crop_listing/presentation/screens/browse_listings_screen.dart` | Add `ownerMode` parameter: when true (farmer), use `LoadCropListings(farmerId:)` instead of `LoadActiveListings()` and show header "My Listings"; when false (buyer), keep current "Market" behavior. Add FAB for create action (farmer: +Sell, buyer: none). |
| `mobile/lib/features/trip_listing/presentation/screens/browse_trips_screen.dart` | Add GreenHeader for visual consistency with other tabs. Add FAB for truckers (+Post Trip → `CreateTripScreen`). |
| `mobile/lib/features/escrow_payment/presentation/screens/payment_history_screen.dart` | Add `embedded` parameter (default false). When `embedded: true`, skip Scaffold/AppBar wrapper so it can be used as tab content inside `PaymentsTabScreen`. |
| `mobile/lib/shared/l10n/app_en.arb` | Add ~6 new l10n keys |
| `mobile/lib/shared/l10n/app_am.arb` | Add ~6 new l10n keys (Amharic) |
| `mobile/lib/shared/l10n/app_om.arb` | Add ~6 new l10n keys (Afan Oromo) |

### Files to Create

| File | Purpose |
|------|---------|
| `mobile/lib/screens/deliveries_tab_screen.dart` | New tab screen: GreenHeader + delivery list via DeliveryBloc + status filter chips + empty state. No Scaffold — designed as tab content. |
| `mobile/lib/screens/payments_tab_screen.dart` | New tab screen: GreenHeader + embedded `PaymentHistoryScreen(embedded: true)` content. No Scaffold — designed as tab content. |

### Files to Remove

| File | Reason |
|------|--------|
| `mobile/lib/screens/orders_screen.dart` | Replaced by dedicated Deliveries and Payments tabs. Remove import from `main_navigation.dart`. |

### Existing Screens Reused (no changes needed)

| Screen | Reused as |
|--------|-----------|
| `DeliveryDetailScreen` | Tap target from Deliveries tab |
| `EscrowStatusScreen` | Tap target from Payments tab |
| `ProfileScreen` | Tab 5 unchanged |
| `HomeScreen` | Tab 1 (modified but not replaced) |

---

## Detailed Changes

### 1. `MainNavigation` — 5-Tab Role-Specific Layout

Replace `BottomAppBar` + center-docked FAB with a standard `BottomNavigationBar`:

```dart
BottomNavigationBar(
  currentIndex: _currentIndex,
  onTap: (i) => setState(() => _currentIndex = i),
  type: BottomNavigationBarType.fixed,
  selectedItemColor: AppTheme.primaryGreen,
  unselectedItemColor: AppTheme.textMuted,
  items: _navItemsForRole(role, l10n),
)
```

Screen array varies by role:
```dart
List<Widget> _buildScreens(UserRole role) {
  return [
    HomeScreen(...),               // Tab 0 — all roles
    _tab2ForRole(role),            // Tab 1 — role-specific
    const DeliveriesTabScreen(),   // Tab 2 — all roles
    const PaymentsTabScreen(),     // Tab 3 — all roles
    const ProfileScreen(),         // Tab 4 — all roles
  ];
}

Widget _tab2ForRole(UserRole role) => switch (role) {
  UserRole.farmer => const BrowseListingsScreen(ownerMode: true),
  UserRole.trucker => const BrowseTripsScreen(),
  UserRole.buyer => const BrowseListingsScreen(ownerMode: false),
};
```

Nav items vary by role:
- **Farmer**: Home, Listings (list_alt), Deliveries (local_shipping), Payments (account_balance_wallet), Profile
- **Trucker**: Home, Trips (local_shipping), Deliveries (inventory_2), Payments (account_balance_wallet), Profile
- **Buyer**: Home, Market (storefront), Deliveries (inventory_2), Payments (account_balance_wallet), Profile

Remove: `FloatingActionButton`, `FloatingActionButtonLocation.centerDocked`, `CircularNotchedRectangle`, `SizedBox(width: 48)` spacer, `_homeRefreshKey` counter (refresh now handled within screens).

### 2. `HomeScreen` — Updated Quick Actions

**Farmer** (4 tiles, 2×2 grid):
```
[ Sell ]       [ My Listings ]
[ Deliveries ] [ Payments ]
```
- Sell → push `CreateCropListingScreen` + refresh on return
- My Listings → `onNavigateToTab(1)`
- Deliveries → `onNavigateToTab(2)`
- Payments → `onNavigateToTab(3)`

**Trucker** (4 tiles, 2×2 grid):
```
[ Post Trip ]  [ My Trips ]
[ Deliveries ] [ Payments ]
```
- Post Trip → push `CreateTripScreen` + refresh on return
- My Trips → `onNavigateToTab(1)`
- Deliveries → `onNavigateToTab(2)`
- Payments → `onNavigateToTab(3)`

**Buyer** (4 tiles, 2×2 grid):
```
[ Browse ]     [ Deliveries ]
[ Payments ]   [ Profile ]
```
- Browse Market → `onNavigateToTab(1)`
- Deliveries → `onNavigateToTab(2)`
- Payments → `onNavigateToTab(3)`
- Profile → `onNavigateToTab(4)`

Notification handler fix (`_NotifAction.ordersTab` at line 1488):
→ Change `onNavigateToTab?.call(2)` to navigate to Deliveries tab (index 2).

### 3. `BrowseListingsScreen` — Owner Mode Support

Add `ownerMode` parameter:

```dart
class BrowseListingsScreen extends StatefulWidget {
  final bool ownerMode;
  const BrowseListingsScreen({this.ownerMode = false, super.key});
```

When `ownerMode == true` (farmer):
- Header title: `l10n.myListings` instead of `l10n.market`
- Header subtitle: localized "Your crop listings"
- Load event: `LoadCropListings(farmerId: userId)` instead of `LoadActiveListings()`
- Add FAB: `FloatingActionButton` → push `CreateCropListingScreen`

When `ownerMode == false` (buyer):
- Keep current "Market" header and `LoadActiveListings()` behavior
- No FAB (buyers browse, don't create)

### 4. `BrowseTripsScreen` — GreenHeader + FAB

Current: starts with `TripOfflineBanner()` + filter row (no header).

Add:
- GreenHeader with title `l10n.myTrips` and subtitle
- FAB for truckers: `FloatingActionButton` → push `CreateTripScreen`

### 5. `PaymentHistoryScreen` — Embedded Mode

Add `embedded` parameter:
```dart
class PaymentHistoryScreen extends StatefulWidget {
  final bool embedded;
  const PaymentHistoryScreen({this.embedded = false, super.key});
```

When `embedded == true`: return the Column content directly (filter chips + list), skip Scaffold/AppBar.
When `embedded == false`: keep current Scaffold/AppBar behavior (for backward compatibility when navigated to directly).

### 6. `DeliveriesTabScreen` — New Tab

```
┌─────────────────────────────┐
│  Green Header               │
│  "My Deliveries"            │
│  "Track your crop pickups"  │
├─────────────────────────────┤
│  [All][Pending][Transit]... │
│  Delivery cards list        │
│  (filtered by user + role)  │
│                             │
│  Empty: "No deliveries yet" │
└─────────────────────────────┘
```

- Reuses `DeliveryBloc` (app-wide provider) with `LoadUserDeliveries` / `FilterUserDeliveries`
- Status filter chips: All / Pending / Picked Up / In Transit / Delivered
- Tap card → push `DeliveryDetailScreen`
- Empty state with icon + localized message

### 7. `PaymentsTabScreen` — New Tab

```
┌─────────────────────────────┐
│  Green Header               │
│  "My Payments"              │
│  "Track your earnings"      │
├─────────────────────────────┤
│  PaymentHistoryScreen       │
│  (embedded: true)           │
│                             │
│  Empty: "No payments yet"   │
└─────────────────────────────┘
```

- GreenHeader with title + subtitle
- Embeds `PaymentHistoryScreen(embedded: true)` for filter chips + payment list
- Reuses `EscrowBloc` (app-wide provider)

---

## Offline Behavior

All data reads are from local **Isar database** — no network required:
- `isar.deliverys.where().filter().farmerIdEqualTo(userId)`
- `isar.payments.where().filter().farmerIdEqualTo(userId)`
- `isar.cropListings.where().farmerIdEqualTo(userId)` (owner mode)

Same offline-first pattern as the existing app. No new network dependencies.

---

## Sync Strategy

No sync changes needed. Deliveries and payments already sync via:
- `SyncExecutor` → POST `/sync` for delivery state changes
- Dedicated `/escrow` endpoints for payment operations

The new/modified screens only read from Isar.

---

## Localization

### New l10n keys (~6)

| Key | English | Amharic | Afan Oromo |
|-----|---------|---------|------------|
| `myDeliveries` | My Deliveries | ማድረሻዎቼ | Geejjiba Koo |
| `myPayments` | My Payments | ክፍያዎቼ | Kaffaltii Koo |
| `trackDeliveries` | Track your crop pickups & deliveries | የሰብል ማጓጓዣዎን ይከታተሉ | Geejjiba midhaanii kee hordofi |
| `trackPayments` | Track your earnings & escrow | ገቢዎን እና ኤስክሮ ይከታተሉ | Galii fi eesciroo kee hordofi |
| `noDeliveries` | No deliveries yet | ገና ማድረሻ የለም | Hanga ammaatti geejjibni hin jiru |
| `noPayments` | No payments yet | ገና ክፍያ የለም | Hanga ammaatti kaffaltiin hin jiru |

### Existing keys reused

- `deliveries` → already exists
- `myListings` → already exists
- `myTrips` → already exists
- `escrow` → already exists
- `market` → already exists
- `home`, `profile` → already exist

---

## API Contract

**No server changes needed.** This is a mobile-only UI restructuring. All data is already available locally.

---

## Testing

| Scenario | Type | Description |
|----------|------|-------------|
| **Navigation — Farmer** | Widget test | 5 tabs: Home, Listings, Deliveries, Payments, Profile |
| **Navigation — Trucker** | Widget test | 5 tabs: Home, Trips, Deliveries, Payments, Profile |
| **Navigation — Buyer** | Widget test | 5 tabs: Home, Market, Deliveries, Payments, Profile |
| **Tab 2 farmer = owner mode** | Widget test | Farmer sees own listings (LoadCropListings), header says "My Listings" |
| **Tab 2 buyer = marketplace** | Widget test | Buyer sees all listings (LoadActiveListings), header says "Market" |
| **Deliveries tab loads** | Widget test | Shows delivery cards filtered by userId |
| **Payments tab loads** | Widget test | Shows payment cards filtered by userId |
| **Filter chips work** | Widget test | Status filters narrow the list correctly |
| **Tap delivery card** | Widget test | Navigates to DeliveryDetailScreen |
| **Tap payment card** | Widget test | Navigates to EscrowStatusScreen |
| **Offline: all tabs load** | Widget test | Mock Isar, verify no network calls in any tab |
| **Home quick actions** | Widget test | Each role sees correct 4 tiles with correct tab indices |
| **Empty states** | Widget test | "No deliveries yet" / "No payments yet" per tab |
| **No center FAB** | Widget test | Verify FAB removed from MainNavigation |
| **FAB in Listings (farmer)** | Widget test | FAB visible, navigates to CreateCropListingScreen |
| **FAB in Trips (trucker)** | Widget test | FAB visible, navigates to CreateTripScreen |

---

## Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| Breaking existing tab index references | MEDIUM | Only 2 files have hardcoded indices (main_navigation, home_screen). Grep-verified. |
| 5 nav items may feel crowded on small phones | LOW | Material Design supports up to 5 items natively. Use `BottomNavigationBarType.fixed`. |
| `BrowseListingsScreen` owner mode changes behavior | MEDIUM | `ownerMode` parameter keeps backward compatibility (default false). |
| `PaymentHistoryScreen` embedded mode | MEDIUM | `embedded` parameter keeps backward compatibility (default false). |
| `BrowseTripsScreen` visual change (adding GreenHeader) | LOW | Additive — filter row and list remain unchanged. |
| Existing tests reference 4-tab layout or OrdersScreen | LOW | Update affected widget tests. |

---

## Implementation Order

1. **Add l10n keys** to all 3 `.arb` files → run `flutter gen-l10n`
2. **Add `ownerMode` to `BrowseListingsScreen`** — role-specific header + BLoC event + FAB
3. **Add GreenHeader + FAB to `BrowseTripsScreen`** — visual consistency
4. **Add `embedded` mode to `PaymentHistoryScreen`** — skip Scaffold when embedded
5. **Create `DeliveriesTabScreen`** — GreenHeader + DeliveryBloc integration + filter chips
6. **Create `PaymentsTabScreen`** — GreenHeader + embedded PaymentHistoryScreen
7. **Redesign `MainNavigation`** — 5-tab BottomNavigationBar, role-specific Tab 2, remove center FAB
8. **Update `HomeScreen`** — role-specific quick actions with correct tab indices, fix notification handler
9. **Remove `OrdersScreen`** — delete file, remove import
10. **Run `flutter analyze`** + fix any breakage
