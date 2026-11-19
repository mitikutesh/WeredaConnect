import 'dart:async';

import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

/// Background sync worker that periodically processes the sync queue.
///
/// The SyncWorker orchestrates automatic syncing by:
/// 1. Running a periodic timer every [syncInterval] (15 minutes)
/// 2. Delegating actual sync processing to [SyncExecutor]
/// 3. Purging old synced operations daily to prevent database bloat
/// 4. Providing [onAppResumed] for immediate sync on app foreground
/// 5. Trying again soon, a few times, when a sync could not reach the server
///    although the phone has a network and changes wait ([retryDelays])
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

  /// BUG-32: tells a failed health check (the phone has a network) from a
  /// phone that is offline. Null leaves the early retries off.
  final ConnectivityMonitor? _connectivityMonitor;
  final List<Duration> _retryDelays;

  Timer? _periodicTimer;
  DateTime? _lastPurge;
  bool _isStarted = false;

  StreamSubscription<SyncBatchResult>? _resultSubscription;
  Timer? _retryTimer;

  /// How many early retries were scheduled since the server was last
  /// reached, the phone was last offline, or the last periodic or resume
  /// sync.
  int _retryAttempt = 0;

  /// Interval between periodic sync attempts.
  /// 15 minutes balances freshness with battery/data usage on Ethiopian 3G.
  static const Duration syncInterval = Duration(minutes: 15);

  /// Interval between purge runs (remove synced operations older than 30 days).
  static const Duration purgeInterval = Duration(hours: 24);

  /// BUG-32: the waits before each early retry of a sync that could not
  /// reach the server (a failed health check, or a `/sync` request that
  /// timed out) while the phone reports a network and changes wait. Before,
  /// one failed health check right after the signal came back left the
  /// queue untouched until the next [syncInterval] tick.
  ///
  /// Data cost: an attempt that still cannot reach the server is one small
  /// health check, and five attempts spread over about nine minutes are the
  /// most between two periodic syncs. The retries stop at once when the
  /// queue is empty or the phone is offline (the connectivity listener
  /// starts a sync when the network comes back).
  static const List<Duration> retryDelays = [
    Duration(seconds: 10),
    Duration(seconds: 30),
    Duration(minutes: 1),
    Duration(minutes: 2),
    Duration(minutes: 5),
  ];

  /// Creates a SyncWorker.
  ///
  /// Parameters:
  /// - [executor]: SyncExecutor that processes the queue
  /// - [syncQueue]: Isar-backed sync queue (for purging old operations)
  /// - [connectivityMonitor]: turns the early retries on (BUG-32)
  /// - [retryDelays]: the waits before each early retry (tests)
  SyncWorker({
    required SyncExecutor executor,
    required SyncQueue syncQueue,
    ConnectivityMonitor? connectivityMonitor,
    List<Duration> retryDelays = SyncWorker.retryDelays,
  })  : _executor = executor,
        _syncQueue = syncQueue,
        _connectivityMonitor = connectivityMonitor,
        _retryDelays = retryDelays;

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

    // BUG-32: watch every sync's outcome for one that never reached the
    // server.
    _resultSubscription?.cancel();
    _resultSubscription = _executor.onSyncResult.listen(_onSyncResult);

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
    _retryAttempt = 0;
    return _executor.syncNow();
  }

  /// BUG-32: schedules an early retry after a sync that did not reach the
  /// server, and forgets the count once one did.
  void _onSyncResult(SyncBatchResult result) {
    switch (result.status) {
      case SyncBatchStatus.noInternet:
      case SyncBatchStatus.networkError:
        unawaited(_scheduleRetry());
      case SyncBatchStatus.skipped:
        // Another sync is running: its own result decides.
        break;
      case SyncBatchStatus.success:
      case SyncBatchStatus.partial:
      case SyncBatchStatus.empty:
      case SyncBatchStatus.authError:
      case SyncBatchStatus.rateLimited:
      case SyncBatchStatus.error:
      case SyncBatchStatus.accountDeleted:
        // The server answered: nothing to retry early.
        _cancelRetry();
    }
  }

  Future<void> _scheduleRetry() async {
    final monitor = _connectivityMonitor;
    if (monitor == null || (_retryTimer?.isActive ?? false)) return;
    if (_retryAttempt >= _retryDelays.length) return;
    try {
      // Offline: the connectivity listener syncs when the network is back.
      // Nothing waiting: nothing to send.
      if (!await monitor.isConnected() ||
          await _syncQueue.getPendingCount() == 0) {
        _cancelRetry();
        return;
      }
    } on Object catch (e) {
      debugPrint('[SYNC] retry check failed: $e');
      return;
    }
    if (!_isStarted || (_retryTimer?.isActive ?? false)) return;
    final delay = _retryDelays[_retryAttempt];
    _retryAttempt++;
    debugPrint(
      '[SYNC] server not reached, retry $_retryAttempt of '
      '${_retryDelays.length} in ${delay.inSeconds}s',
    );
    _retryTimer = Timer(delay, () {
      if (_isStarted) _executor.syncNow();
    });
  }

  void _cancelRetry() {
    _retryTimer?.cancel();
    _retryTimer = null;
    _retryAttempt = 0;
  }

  /// Periodic sync callback.
  ///
  /// Triggers a sync attempt and optionally purges old synced operations.
  Future<void> _onPeriodicSync() async {
    // A new round of early retries per periodic sync.
    _retryAttempt = 0;
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
    _resultSubscription?.cancel();
    _resultSubscription = null;
    _cancelRetry();
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
