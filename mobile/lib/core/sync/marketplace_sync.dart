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

  /// Convenience: pull both crops and trips.
  Future<void> pullAll();
}
