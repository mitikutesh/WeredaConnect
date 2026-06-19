import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/buyer_intent.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/buyer/data/repositories/buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';
import 'package:wereda_connect/features/buyer/domain/services/matching_service.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';

/// Fake [MarketplaceSync] that seeds Isar with one matching crop + trip,
/// standing in for the real API-backed pull. Verifies that the BLoC pulls the
/// marketplace before matching.
class _FakeMarketplaceSync implements MarketplaceSync {
  _FakeMarketplaceSync(this._isar);

  final Isar _isar;
  int pullAllCount = 0;

  @override
  Future<void> pullAll() async {
    pullAllCount++;
    await _seed();
  }

  @override
  Future<void> pullCrops() => _seed();

  @override
  Future<void> pullTrips() async {}

  Future<void> _seed() async {
    await _isar.writeTxn(() async {
      await _isar.cropListings.putByListingId(
        CropListing()
          ..listingId = 'crop-1'
          ..farmerId = 'farmer-1'
          ..farmerName = 'Mitiku'
          ..cropType = 'wheat'
          ..quantity = 10 // quintals
          ..pricePerKg = 100
          ..location = (LatLng()
            ..latitude = 0
            ..longitude = 0)
          ..locationName = 'Assosa'
          ..description = ''
          ..status = ListingStatus.active
          ..createdAt = DateTime.now().toUtc()
          ..updatedAt = DateTime.now().toUtc()
          ..version = 1,
      );
      await _isar.trips.putByTripId(
        Trip()
          ..tripId = 'trip-1'
          ..truckerId = 'trucker-1'
          ..truckerName = 'Geleta'
          ..truckerPhone = '+251931831670'
          ..fromWoreda = 'Assosa'
          ..toWoreda = 'AddisAbeba'
          ..departureDate = DateTime.now().toUtc().add(const Duration(days: 2))
          ..capacity = 100
          ..pricePerQuintal = 10
          ..status = TripStatus.open
          ..matchedListings = <String>[]
          ..createdAt = DateTime.now().toUtc()
          ..updatedAt = DateTime.now().toUtc()
          ..version = 1,
      );
    });
  }
}

/// Fake [MarketplaceSync] whose pull always fails (offline/transient) — used to
/// verify the C1 fix: a failed refresh must NOT overwrite cached match counts
/// and must flag the result as stale.
class _FailingMarketplaceSync implements MarketplaceSync {
  int pullAllCount = 0;

  @override
  Future<void> pullAll() async {
    pullAllCount++;
    throw Exception('offline');
  }

  @override
  Future<void> pullCrops() async => throw Exception('offline');

  @override
  Future<void> pullTrips() async => throw Exception('offline');
}

/// Fake [BaseMatchingService] whose match call always throws — stands in for the
/// web `/match` network call failing AFTER the intent has already been saved.
/// The BLoC must still emit [BuyerIntentCreated] (empty + stale), never
/// [BuyerIntentError], so the buyer is not told a saved intent "failed" (and
/// then confusingly finds it later).
class _ThrowingMatchingService implements BaseMatchingService {
  @override
  Future<List<CropMatchResult>> matchListingsForIntent(
    BuyerIntent intent,
  ) async {
    throw Exception('no internet');
  }

  @override
  Future<void> refreshMatchCounts(
    String buyerId,
    BaseBuyerIntentRepository repository,
  ) async {}
}

void main() {
  group('BuyerIntentBloc marketplace-backed matching', () {
    late Isar isar;
    late Directory tempDir;
    late BuyerIntentRepository repository;
    late MatchingService matchingService;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('buyer_match_test_');
      isar = await Isar.open(
        [BuyerIntentSchema, CropListingSchema, TripSchema],
        directory: tempDir.path,
        name: 'test_buyer_match_${DateTime.now().microsecondsSinceEpoch}',
      );
      repository = BuyerIntentRepository(isar: isar);
      matchingService = MatchingService(isar: isar);
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
    });

    Future<List<BuyerIntentState>> runCreate(BuyerIntentBloc bloc) async {
      final states = <BuyerIntentState>[];
      final sub = bloc.stream.listen(states.add);
      bloc.add(
        const CreateBuyerIntent(
          buyerId: 'buyer-1',
          cropType: 'wheat',
          quantityNeeded: 5,
          maxPricePerKg: 150,
          deliveryWoreda: 'AddisAbeba',
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 300));
      await sub.cancel();
      return states;
    }

    test('pulls the marketplace then returns matching crop + suggested trip',
        () async {
      final sync = _FakeMarketplaceSync(isar);
      final bloc = BuyerIntentBloc(
        repository: repository,
        matchingService: matchingService,
        marketplaceSync: sync,
      );

      final states = await runCreate(bloc);

      expect(sync.pullAllCount, 1, reason: 'pull must run before matching');
      final created = states.whereType<BuyerIntentCreated>().single;
      expect(created.matches, hasLength(1));
      expect(created.matches.first.listing.cropType, 'wheat');
      expect(created.matches.first.suggestedTrips, hasLength(1));
      // The persisted match-count cache is updated separately from the
      // in-memory intent object carried by the state.
      final persisted = await repository.getIntentById(created.intent.intentId);
      expect(persisted?.matchCount, 1);
      await bloc.close();
    });

    test(
        'RefreshAllMatches pulls, recomputes match counts, and emits '
        'ActiveIntentsLoaded with the refreshed count', () async {
      final sync = _FakeMarketplaceSync(isar);
      final bloc = BuyerIntentBloc(
        repository: repository,
        matchingService: matchingService,
        marketplaceSync: sync,
      );

      // An intent exists, but its cached count is stale (0) until a refresh.
      final intent = await repository.createIntent(
        buyerId: 'buyer-1',
        cropType: 'wheat',
        quantityNeeded: 5,
        maxPricePerKg: 150,
        deliveryWoreda: 'AddisAbeba',
      );
      expect(intent.matchCount, 0);

      final states = <BuyerIntentState>[];
      final sub = bloc.stream.listen(states.add);
      bloc.add(const RefreshAllMatches(buyerId: 'buyer-1'));
      await Future<void>.delayed(const Duration(milliseconds: 300));
      await sub.cancel();

      expect(sync.pullAllCount, 1);
      final loaded = states.whereType<ActiveIntentsLoaded>().last;
      expect(loaded.intents, hasLength(1));
      expect(
        loaded.intents.first.matchCount,
        1,
        reason: 'count recomputed against the freshly pulled marketplace',
      );
      await bloc.close();
    });

    test(
        'without a marketplace pull (null sync) and empty Isar, matching '
        'returns nothing — the original bug', () async {
      final bloc = BuyerIntentBloc(
        repository: repository,
        matchingService: matchingService,
        // marketplaceSync intentionally omitted (mirrors the broken state).
      );

      final states = await runCreate(bloc);

      final created = states.whereType<BuyerIntentCreated>().single;
      expect(created.matches, isEmpty);
      await bloc.close();
    });

    test(
        'C1: a failed pull on RefreshAllMatches preserves the cached count and '
        'flags the result stale', () async {
      // Seed an intent with a known good count, then view it (snapshot).
      final intent = await repository.createIntent(
        buyerId: 'buyer-1',
        cropType: 'wheat',
        quantityNeeded: 5,
        maxPricePerKg: 150,
        deliveryWoreda: 'AddisAbeba',
      );
      await repository.updateMatchCount(
        intent.intentId,
        3,
        DateTime.now().toUtc(),
      );
      await repository.updateLastViewed(intent.intentId);

      final failing = _FailingMarketplaceSync();
      final bloc = BuyerIntentBloc(
        repository: repository,
        matchingService: matchingService,
        marketplaceSync: failing,
      );

      final states = <BuyerIntentState>[];
      final sub = bloc.stream.listen(states.add);
      bloc.add(const RefreshAllMatches(buyerId: 'buyer-1'));
      await Future<void>.delayed(const Duration(milliseconds: 300));
      await sub.cancel();

      expect(failing.pullAllCount, 1);
      final loaded = states.whereType<ActiveIntentsLoaded>().last;
      expect(loaded.isStale, isTrue, reason: 'pull failed → stale results');
      expect(
        loaded.intents.first.matchCount,
        3,
        reason: 'a failed pull must NOT overwrite the cached count (with 0)',
      );
      await bloc.close();
    });

    test('C1: a failed pull on create still emits matches but flags them stale',
        () async {
      final failing = _FailingMarketplaceSync();
      final bloc = BuyerIntentBloc(
        repository: repository,
        matchingService: matchingService,
        marketplaceSync: failing,
      );

      final created =
          (await runCreate(bloc)).whereType<BuyerIntentCreated>().single;

      expect(failing.pullAllCount, 1);
      expect(created.isStale, isTrue);
      await bloc.close();
    });

    test(
        'matching failure after the intent is saved emits BuyerIntentCreated '
        '(empty + stale), never BuyerIntentError', () async {
      // Mirrors the web bug: createIntent succeeds, but the /match network call
      // throws. The buyer must still land on the results screen with the saved
      // intent, not see a "no internet" error for an intent that was saved.
      final bloc = BuyerIntentBloc(
        repository: repository,
        matchingService: _ThrowingMatchingService(),
        marketplaceSync: _FakeMarketplaceSync(isar),
      );

      final states = await runCreate(bloc);

      expect(
        states.whereType<BuyerIntentError>(),
        isEmpty,
        reason: 'a matching failure must not be surfaced as an error',
      );
      final created = states.whereType<BuyerIntentCreated>().single;
      expect(created.matches, isEmpty);
      expect(created.isStale, isTrue);

      // The intent was genuinely persisted despite the matching failure.
      final active = await repository.getActiveIntents('buyer-1');
      expect(active, hasLength(1));
      expect(active.first.intentId, created.intent.intentId);
      await bloc.close();
    });

    test('I5: FulfillIntent emits a terminal ActiveIntentsLoaded', () async {
      final intent = await repository.createIntent(
        buyerId: 'buyer-1',
        cropType: 'wheat',
        quantityNeeded: 5,
        maxPricePerKg: 150,
        deliveryWoreda: 'AddisAbeba',
      );
      final bloc = BuyerIntentBloc(
        repository: repository,
        matchingService: matchingService,
      );

      final states = <BuyerIntentState>[];
      final sub = bloc.stream.listen(states.add);
      bloc.add(FulfillIntent(intentId: intent.intentId, buyerId: 'buyer-1'));
      await Future<void>.delayed(const Duration(milliseconds: 200));
      await sub.cancel();

      final loaded = states.whereType<ActiveIntentsLoaded>().last;
      expect(
        loaded.intents,
        isEmpty,
        reason: 'a fulfilled intent leaves the active list',
      );
      await bloc.close();
    });

    test('I5: DeleteIntent emits a terminal ActiveIntentsLoaded', () async {
      final intent = await repository.createIntent(
        buyerId: 'buyer-1',
        cropType: 'wheat',
        quantityNeeded: 5,
        maxPricePerKg: 150,
        deliveryWoreda: 'AddisAbeba',
      );
      final bloc = BuyerIntentBloc(
        repository: repository,
        matchingService: matchingService,
      );

      final states = <BuyerIntentState>[];
      final sub = bloc.stream.listen(states.add);
      bloc.add(DeleteIntent(intentId: intent.intentId, buyerId: 'buyer-1'));
      await Future<void>.delayed(const Duration(milliseconds: 200));
      await sub.cancel();

      final loaded = states.whereType<ActiveIntentsLoaded>().last;
      expect(loaded.intents, isEmpty);
      await bloc.close();
    });
  });
}
