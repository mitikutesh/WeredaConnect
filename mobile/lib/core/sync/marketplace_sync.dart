import 'package:wereda_connect/core/models/order.dart';

/// Downloads the shared marketplace — all active crop listings and all open
/// trips — from the server into local storage.
///
/// Why this exists: the offline sync engine ([SyncExecutor]) only *uploads* a
/// user's own writes; it never downloads other users' data. A Buyer creates
/// no crops or trips of their own, so without a pull their local Isar stays
/// empty and "Browse all crops/trips" and "Find matches" return nothing on
/// mobile (CLAUDE.md: a Buyer MUST see ALL crops + ALL trips).
///
/// Platform split (mirrors [BaseMatchingService] / repositories):
/// - Mobile: [IsarMarketplaceSync] caches the API response into Isar so the
///   existing offline-first Isar queries have data to read.
/// - Web: there is no Isar — the web repositories/matching service query the
///   API directly on every read — so web passes `null` (no pull needed).
abstract class MarketplaceSync {
  /// Fetch all active crop listings from the server into local storage.
  Future<void> pullCrops();

  /// Fetch all open trips from the server into local storage.
  Future<void> pullTrips();

  /// FUP-374: fetch the trucker's own trips, whatever their status
  /// (`GET /trips?truckerId=`, no status filter; the server answers a
  /// trucker with their own trips only and leaves out cancelled ones), into
  /// local storage. The status the server sets on a trucker's trip (`matched`
  /// once a funding fills it, `delivered`, or `open` / `matched` again after
  /// an admin's no-show re-open revived a trip the order cron closed) reaches
  /// the phone this way; uploads never bring it. A row with a trip upload
  /// still on its way is left alone, and a row is never moved to an older
  /// server version. Never reads another trucker's trips: it is not the
  /// buyer's market pull ([pullTrips]).
  ///
  /// FUP-385: true when it stored a trip that is new to the phone or that
  /// the server changed, so a screen that showed the stored trips first
  /// reads them again; false when nothing changed or the pull was throttled.
  /// A call while one for the same trucker is on its way gets that one's
  /// answer. Errors (offline) propagate.
  Future<bool> pullOwnTrips({required String truckerId});

  /// Fetch the current user's deliveries from the server into local storage.
  ///
  /// Deliveries are created/updated by whichever party acts (trucker confirms
  /// pickup, buyer confirms delivery). The offline sync engine only *uploads* a
  /// device's own writes, so without this pull a buyer never sees the
  /// trucker-driven status changes on their own device — and a farmer, who
  /// never authors a delivery at all, sees nothing. Scoped server-side to the
  /// caller by querying BOTH `?farmerId=` (FarmerIdCreatedAtIndex) and
  /// `?mine=true` (the buyer via ParticipantIndex) and merging — so the caller
  /// gets their deliveries whether they are the farmer or the buyer, without
  /// this layer needing to know the role.
  Future<void> pullDeliveries();

  /// Fetch the current user's orders from the server into local storage.
  ///
  /// An order is created by a buyer but must also appear on the farmer's device
  /// (so they can accept/reject) and the assigned trucker's device. The offline
  /// sync engine only *uploads* a device's own writes, so without this pull a
  /// farmer never sees an incoming buyer order on mobile. Scoped to the caller's
  /// role-appropriate id (`GET /orders?farmerId=`/`buyerId=`/`truckerId=`).
  Future<void> pullOrders({required String userId, String? role});

  /// Fetch one order from the server (`GET /orders/{id}`) into local storage
  /// and return the server's copy (ESC-44: the live checkout follows the
  /// server's status). Returns null, without a request, while a local change
  /// of the order is still waiting to upload: the server copy would be older.
  /// Never moves the local row to an older version. Errors (offline, 404)
  /// propagate.
  Future<Order?> pullOrder(String orderId);

  /// Convenience: pull both crops and trips.
  Future<void> pullAll();
}
