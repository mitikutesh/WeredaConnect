import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';

/// Counts pull invocations so we can assert which BLoC events trigger a
/// marketplace pull. This guards the HARD CLAUDE.md role rule: only Buyer
/// marketplace-browse events may pull ALL crops/trips; owner-scoped
/// (Farmer/Trucker) events MUST NOT, or they would leak others' data.
class _SpyMarketplaceSync implements MarketplaceSync {
  int crops = 0;
  int trips = 0;
  int all = 0;
  int deliveries = 0;

  @override
  Future<void> pullCrops() async => crops++;

  @override
  Future<void> pullTrips() async => trips++;

  @override
  Future<void> pullDeliveries() async => deliveries++;

  int orders = 0;

  @override
  Future<void> pullOrders({required String userId, String? role}) async =>
      orders++;

  @override
  Future<void> pullAll() async => all++;
}

Future<void> _settle() =>
    Future<void>.delayed(const Duration(milliseconds: 80));

void main() {
  late Isar isar;
  late Directory tempDir;
  late _SpyMarketplaceSync spy;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('pull_gating_test_');
    isar = await Isar.open(
      [CropListingSchema, TripSchema, SyncOperationSchema],
      directory: tempDir.path,
      name: 'test_pull_gating_${DateTime.now().microsecondsSinceEpoch}',
    );
    spy = _SpyMarketplaceSync();
  });

  tearDown(() async {
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  group('CropListingBloc marketplace-pull gating', () {
    CropListingBloc build() => CropListingBloc(
          repository: CropListingRepository(
            isar: isar,
            syncQueue: SyncQueue(isar),
          ),
          marketplaceSync: spy,
        );

    test('LoadActiveListings (Buyer marketplace) pulls crops', () async {
      final bloc = build();
      bloc.add(const LoadActiveListings());
      await _settle();
      expect(spy.crops, 1);
      await bloc.close();
    });

    test('LoadCropListings (Farmer own listings) does NOT pull', () async {
      final bloc = build();
      bloc.add(const LoadCropListings(farmerId: 'farmer-1'));
      await _settle();
      expect(spy.crops, 0, reason: 'a Farmer must not pull the whole market');
      await bloc.close();
    });
  });

  group('TripListingBloc marketplace-pull gating', () {
    TripListingBloc build() => TripListingBloc(
          repository: TripListingRepository(
            isar: isar,
            syncQueue: SyncQueue(isar),
          ),
          marketplaceSync: spy,
        );

    test('LoadOpenTrips (Buyer marketplace) pulls trips', () async {
      final bloc = build();
      bloc.add(const LoadOpenTrips());
      await _settle();
      expect(spy.trips, 1);
      await bloc.close();
    });

    test('LoadTrips (Trucker own trips) does NOT pull', () async {
      final bloc = build();
      bloc.add(const LoadTrips(truckerId: 'trucker-1'));
      await _settle();
      expect(spy.trips, 0, reason: 'a Trucker must not pull the whole market');
      await bloc.close();
    });

    test('FilterTrips (Trucker own trips) does NOT pull', () async {
      final bloc = build();
      bloc.add(const FilterTrips(truckerId: 'trucker-1', fromWoreda: 'Adama'));
      await _settle();
      expect(spy.trips, 0);
      await bloc.close();
    });
  });
}
