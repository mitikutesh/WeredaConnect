// ignore_for_file: avoid_print
import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:isar/isar.dart';
import 'package:path_provider/path_provider.dart';

import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/sync/conflict_resolver.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';

/// Manual stress test for offline sync flow.
///
/// This test runs 100 full offline-online cycles to stress-test the sync engine.
/// It's designed to be run manually (not in CI) to validate system stability
/// under prolonged use.
///
/// WARNING: This test takes a long time to run (~30-60 minutes depending on
/// network conditions). Only run when you need comprehensive stress testing.
///
/// Usage:
/// ```bash
/// flutter test integration_test/manual_stress_test.dart
/// ```
///
/// Performance targets:
/// - All 100 cycles should complete successfully
/// - Memory usage should remain stable (no leaks)
/// - Average sync latency should stay <5s per operation
/// - No failed operations after 100 cycles
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  late Isar isar;
  late SyncQueue syncQueue;
  late CropListingRepository repository;
  late Dio dio;
  late SyncExecutor syncExecutor;
  late ConnectivityMonitor connectivityMonitor;
  late ConflictResolver conflictResolver;
  late FakeConnectivitySource fakeConnectivity;

  const testUserId = 'stress-test-user';
  const testClientId = 'stress-test-device';

  setUpAll(() async {
    final dir = await getTemporaryDirectory();
    isar = await Isar.open(
      [CropListingSchema, SyncOperationSchema],
      directory: dir.path,
      name: 'wereda_connect_stress_test',
    );

    syncQueue = SyncQueue(isar);
    repository = CropListingRepository(
      isar: isar,
      syncQueue: syncQueue,
    );

    dio = Dio(
      BaseOptions(
        baseUrl: Environment.config.apiBaseUrl,
        connectTimeout: const Duration(seconds: 30),
        receiveTimeout: const Duration(seconds: 30),
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'application/json',
        },
      ),
    );

    fakeConnectivity = FakeConnectivitySource();
    connectivityMonitor = ConnectivityMonitor(
      source: fakeConnectivity,
      dio: dio,
    );

    conflictResolver = ConflictResolver(isar);

    syncExecutor = SyncExecutor(
      syncQueue: syncQueue,
      connectivityMonitor: connectivityMonitor,
      dio: dio,
      clientId: testClientId,
      conflictResolver: conflictResolver,
    );
  });

  tearDownAll(() async {
    syncExecutor.dispose();
    connectivityMonitor.dispose();
    await isar.close(deleteFromDisk: true);
  });

  test(
    '100 offline-online cycles stress test',
    () async {
      const cycleCount = 100;
      final results = <_CycleResult>[];
      var totalOperations = 0;
      var totalSucceeded = 0;
      var totalFailed = 0;

      print('🔥 Starting stress test: $cycleCount cycles');
      print('This will take ~30-60 minutes. Please be patient...\n');

      final overallStartTime = DateTime.now();

      for (var i = 1; i <= cycleCount; i++) {
        final cycleStartTime = DateTime.now();

        // OFFLINE PHASE: Create 1-3 random listings
        fakeConnectivity.setConnected(false);
        final operationsInCycle = 1 + (i % 3); // 1, 2, or 3 operations

        for (var j = 0; j < operationsInCycle; j++) {
          await repository.createListing(
            farmerId: testUserId,
            cropType: _randomCropType(i, j),
            quantity: (i % 10 + 1).toDouble(),
            pricePerKg: 40.0 + (i % 20).toDouble(),
            latitude: 9.0 + (i * 0.001),
            longitude: 38.0 + (j * 0.001),
            description: 'Stress test cycle $i operation $j',
          );
        }

        // Verify operations are queued
        final queuedCount = await syncQueue.getPendingCount();
        if (queuedCount < operationsInCycle) {
          print('❌ Cycle $i: Failed to queue all operations');
          break;
        }

        // ONLINE PHASE: Sync
        fakeConnectivity.setConnected(true);
        final syncResult = await syncExecutor.syncNow();

        // Record results
        final cycleEndTime = DateTime.now();
        final cycleDuration = cycleEndTime.difference(cycleStartTime);

        final cycleResult = _CycleResult(
          cycleNumber: i,
          operationCount: operationsInCycle,
          succeeded: syncResult.succeeded,
          failed: syncResult.failed,
          duration: cycleDuration,
        );
        results.add(cycleResult);

        totalOperations += operationsInCycle;
        totalSucceeded += syncResult.succeeded;
        totalFailed += syncResult.failed;

        // Progress update every 10 cycles
        if (i % 10 == 0) {
          print('✅ Completed $i/$cycleCount cycles');
          print('   Total operations: $totalOperations');
          print(
            '   Success rate: ${(totalSucceeded / totalOperations * 100).toStringAsFixed(1)}%',
          );
          print(
            '   Average cycle time: ${_averageDuration(results).inSeconds}s\n',
          );
        }

        // Clean up synced operations periodically
        if (i % 25 == 0) {
          await syncQueue.purgeSynced();
        }
      }

      final overallEndTime = DateTime.now();
      final totalDuration = overallEndTime.difference(overallStartTime);

      // Print comprehensive results
      print('\n${'=' * 60}');
      print('🎯 STRESS TEST RESULTS');
      print('=' * 60);
      print('Total cycles: $cycleCount');
      print('Total operations: $totalOperations');
      print('Total succeeded: $totalSucceeded');
      print('Total failed: $totalFailed');
      print(
        'Success rate: ${(totalSucceeded / totalOperations * 100).toStringAsFixed(2)}%',
      );
      print(
        'Total duration: ${totalDuration.inMinutes}m ${totalDuration.inSeconds % 60}s',
      );
      print('Average cycle time: ${_averageDuration(results).inSeconds}s');
      print(
        'Average operations per cycle: ${(totalOperations / cycleCount).toStringAsFixed(2)}',
      );
      print('=' * 60);

      // Performance analysis
      print('\n📊 PERFORMANCE BREAKDOWN:');
      final fastCycles = results.where((r) => r.duration.inSeconds < 3).length;
      final mediumCycles = results
          .where((r) => r.duration.inSeconds >= 3 && r.duration.inSeconds < 5)
          .length;
      final slowCycles = results.where((r) => r.duration.inSeconds >= 5).length;

      print(
        'Fast cycles (<3s): $fastCycles (${(fastCycles / cycleCount * 100).toStringAsFixed(1)}%)',
      );
      print(
        'Medium cycles (3-5s): $mediumCycles (${(mediumCycles / cycleCount * 100).toStringAsFixed(1)}%)',
      );
      print(
        'Slow cycles (>5s): $slowCycles (${(slowCycles / cycleCount * 100).toStringAsFixed(1)}%)',
      );

      // Memory check (approximate)
      final finalQueueSize = await syncQueue.getPendingCount();
      final failedOps = await syncQueue.getFailedCount();

      print('\n💾 MEMORY & QUEUE STATUS:');
      print('Pending operations: $finalQueueSize');
      print('Failed operations: $failedOps');
      print('Total crop listings: ${await isar.cropListings.count()}');
      print('Total sync operations: ${await isar.syncOperations.count()}');

      // ASSERTIONS
      expect(totalFailed, equals(0), reason: 'All operations should succeed');
      expect(
        finalQueueSize,
        equals(0),
        reason: 'Queue should be empty after sync',
      );
      expect(failedOps, equals(0), reason: 'No permanently failed operations');

      // Success rate should be 100% (or very close)
      final successRate = totalSucceeded / totalOperations;
      expect(
        successRate,
        greaterThan(0.95),
        reason: 'Success rate should be >95%',
      );

      print('\n✅ STRESS TEST PASSED!');
    },
    timeout: const Timeout(
      Duration(
        hours: 2,
      ),
    ),
  );
}

/// Result of a single offline-online cycle.
class _CycleResult {
  final int cycleNumber;
  final int operationCount;
  final int succeeded;
  final int failed;
  final Duration duration;

  _CycleResult({
    required this.cycleNumber,
    required this.operationCount,
    required this.succeeded,
    required this.failed,
    required this.duration,
  });
}

/// Calculate average duration from results.
Duration _averageDuration(List<_CycleResult> results) {
  if (results.isEmpty) return Duration.zero;
  final totalMs =
      results.fold<int>(0, (sum, r) => sum + r.duration.inMilliseconds);
  return Duration(milliseconds: totalMs ~/ results.length);
}

/// Generate random crop type for variation.
String _randomCropType(int cycle, int operation) {
  const cropTypes = ['teff', 'wheat', 'barley', 'maize', 'chickpea'];
  return cropTypes[(cycle + operation) % cropTypes.length];
}

/// Fake connectivity source for testing.
class FakeConnectivitySource implements ConnectivitySource {
  final StreamController<List<ConnectivityResult>> _controller =
      StreamController<List<ConnectivityResult>>.broadcast();

  List<ConnectivityResult> _currentState = [ConnectivityResult.none];

  @override
  Stream<List<ConnectivityResult>> get onConnectivityChanged =>
      _controller.stream;

  @override
  Future<List<ConnectivityResult>> checkConnectivity() async {
    return _currentState;
  }

  void setConnected(bool connected) {
    _currentState =
        connected ? [ConnectivityResult.mobile] : [ConnectivityResult.none];
    _controller.add(_currentState);
  }

  void dispose() {
    _controller.close();
  }
}
