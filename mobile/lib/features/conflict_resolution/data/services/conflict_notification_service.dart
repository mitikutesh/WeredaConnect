import 'dart:async';

import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';

/// Stream-based service that broadcasts conflict events to the UI layer.
///
/// Injected into SyncExecutor as a callback. When the executor resolves
/// a conflict, it calls [notify] which pushes the event onto the stream.
/// ConflictBloc subscribes to this stream.
///
/// Includes a replay buffer (security review finding #1): events fired
/// before ConflictBloc subscribes are buffered and replayed on first
/// subscription, preventing silent event loss during app startup.
///
/// Lifecycle: created in main.dart, shared between SyncExecutor and ConflictBloc.
class ConflictNotificationService {
  final StreamController<ConflictEvent> _controller =
      StreamController<ConflictEvent>.broadcast();

  final List<ConflictEvent> _pendingEvents = [];
  bool _hasSubscriber = false;

  ConflictNotificationService() {
    // Reset the subscriber flag when the last listener cancels (e.g. the
    // ConflictBloc is closed on provider rebuild or re-login). Without this,
    // events fired by SyncExecutor while no bloc is listening would be added
    // to the broadcast stream and silently dropped instead of buffered for
    // replay to the next subscriber.
    _controller.onCancel = () => _hasSubscriber = false;
  }

  /// Stream that ConflictBloc subscribes to.
  ///
  /// On first access, replays any buffered events that arrived before
  /// a subscriber was present (fixes startup race condition).
  Stream<ConflictEvent> get onConflict {
    _hasSubscriber = true;
    // Replay any buffered events on first subscription.
    // Scheduled as a microtask so replay happens AFTER .listen() is called
    // on the returned broadcast stream.
    if (_pendingEvents.isNotEmpty) {
      final pending = List<ConflictEvent>.of(_pendingEvents);
      _pendingEvents.clear();
      Future.microtask(() {
        for (final event in pending) {
          if (!_controller.isClosed) {
            _controller.add(event);
          }
        }
      });
    }
    return _controller.stream;
  }

  /// Called by SyncExecutor when a conflict is resolved.
  ///
  /// This method should be called AFTER ConflictResolver.resolve()
  /// succeeds, so the local data is already updated when the UI shows.
  void notify(ConflictEvent event) {
    if (_controller.isClosed) return;
    if (_hasSubscriber) {
      _controller.add(event);
    } else {
      _pendingEvents.add(event);
    }
  }

  /// Release resources.
  void dispose() {
    _pendingEvents.clear();
    _controller.close();
  }
}
