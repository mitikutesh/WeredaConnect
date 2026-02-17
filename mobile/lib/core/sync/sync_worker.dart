import 'dart:async';

import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

/// Background sync worker that periodically processes the sync queue.
///
/// The SyncWorker orchestrates automatic syncing by:
/// 1. Running a periodic timer every [syncInterval] (15 minutes)
/// 2. Delegating actual sync processing to [SyncExecutor]
/// 3. Purging old synced operations daily to prevent database bloat
/// 4. Providing [onAppResumed] for immediate sync on app foreground
///
/// Ethiopian context:
/// - 15-minute intervals balance data freshness with battery/data cost
/// - Periodic purge keeps Isar database small on low-storage devices
/// - App resume trigger catches the common "user opens app after offline period"
///
/// Usage:
/// ```dart
/// final worker = SyncWorker(executor: syncExecutor, syncQueue: syncQueue);
/// worker.start(); // Begin periodic sync + connectivity listening
///
/// // When app comes to foreground (from WidgetsBindingObserver):
/// worker.onAppResumed();
///
/// // On shutdown:
/// worker.dispose();
/// ```
///
/// See: `specs/contracts/sync-protocol.md`
class SyncWorker {
  final SyncExecutor _executor;
  final SyncQueue _syncQueue;

  Timer? _periodicTimer;
  DateTime? _lastPurge;
  bool _isStarted = false;

  /// Interval between periodic sync attempts.
  /// 15 minutes balances freshness with battery/data usage on Ethiopian 3G.
  static const Duration syncInterval = Duration(minutes: 15);

  /// Interval between purge runs (remove synced operations older than 30 days).
  static const Duration purgeInterval = Duration(hours: 24);

  /// Creates a SyncWorker.
  ///
  /// Parameters:
  /// - [executor]: SyncExecutor that processes the queue
  /// - [syncQueue]: Isar-backed sync queue (for purging old operations)
  SyncWorker({
    required SyncExecutor executor,
    required SyncQueue syncQueue,
  })  : _executor = executor,
        _syncQueue = syncQueue;

  /// Whether the worker is currently running.
  bool get isStarted => _isStarted;

  /// Start the background sync worker.
  ///
  /// Begins the periodic sync timer and starts the executor's
  /// connectivity listener. Also triggers an immediate sync attempt.
  void start() {
    if (_isStarted) return;
    _isStarted = true;

    // Start listening to connectivity changes for auto-sync
    _executor.start();

    // Start periodic timer
    _periodicTimer?.cancel();
    _periodicTimer = Timer.periodic(syncInterval, (_) => _onPeriodicSync());

    // Trigger an immediate sync on start
    _executor.syncNow();
  }

  /// Called when the app returns to foreground.
  ///
  /// Triggers an immediate sync attempt. This catches the common scenario
  /// where a user goes offline, performs actions, then opens the app
  /// when back in a coverage area.
  ///
  /// Integration point (call from AppLifecycleObserver):
  /// ```dart
  /// @override
  /// void didChangeAppLifecycleState(AppLifecycleState state) {
  ///   if (state == AppLifecycleState.resumed) {
  ///     syncWorker.onAppResumed();
  ///   }
  /// }
  /// ```
  Future<SyncBatchResult> onAppResumed() async {
    return _executor.syncNow();
  }

  /// Periodic sync callback.
  ///
  /// Triggers a sync attempt and optionally purges old synced operations.
  Future<void> _onPeriodicSync() async {
    await _executor.syncNow();

    // Run daily purge of old synced operations
    if (_shouldPurge()) {
      await _syncQueue.purgeSynced();
      _lastPurge = DateTime.now().toUtc();
    }
  }

  /// Check if enough time has passed since the last purge.
  bool _shouldPurge() {
    if (_lastPurge == null) return true;
    return DateTime.now().toUtc().difference(_lastPurge!) >= purgeInterval;
  }

  /// Stop the background sync worker.
  ///
  /// Cancels the periodic timer and stops connectivity listening.
  /// Does not cancel any in-flight sync operations.
  void stop() {
    _periodicTimer?.cancel();
    _periodicTimer = null;
    _executor.stop();
    _isStarted = false;
  }

  /// Release all resources held by this worker.
  ///
  /// Stops the worker and disposes the executor.
  void dispose() {
    stop();
    _executor.dispose();
  }
}
