import 'dart:async';

import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/features/messaging/domain/conversation_context_resolver.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';

/// BLoC for the conversations inbox + the global unread badge.
///
/// On [LoadInbox] it pulls from the server (best-effort) then reads the local
/// store; a local-store change (send, or a pulled inbound message) re-reads
/// without a network round-trip. Safe to keep app-global — it only reads.
///
/// The poll timer is lifecycle-aware: [InboxPaused]/[InboxResumed] stop and
/// restart it so a backgrounded app doesn't burn 3G data, and an FCM push can
/// dispatch [InboxPollRequested] to make foreground arrival instant.
class InboxBloc extends Bloc<InboxEvent, InboxState> {
  /// How often to poll the server for new messages while the app is open.
  /// One tiny GET per minute keeps chat feeling live without hurting 3G data
  /// budgets; background delivery is FCM's job, not the poll's.
  static const Duration defaultPollInterval = Duration(seconds: 60);

  /// Skip the resume catch-up pull when the last pull is this recent —
  /// notification taps and permission dialogs cycle the lifecycle without
  /// the inbox actually being stale.
  static const Duration defaultResumeThrottle = Duration(seconds: 10);

  final BaseMessageRepository _repository;

  BaseMessageRepository get repository => _repository;

  /// Resolves what each thread is about (linked crop/trip). Exposed here the
  /// same way [repository] is, so thread screens and inbox tiles share one
  /// cached instance. Null in tests / when context labels aren't wired.
  final ConversationContextResolver? contextResolver;

  /// Overridable for tests; production callers use the defaults.
  final Duration pollInterval;
  final Duration resumeThrottle;

  StreamSubscription<void>? _changes;
  Timer? _pollTimer;
  String? _userId;

  /// True while a server pull is in flight. A pull requested mid-flight sets
  /// [_pullPending] instead of racing (same cursor → duplicate GETs); the
  /// in-flight pull then runs exactly one trailing pull, so a message landing
  /// mid-pull is still picked up.
  bool _pulling = false;
  bool _pullPending = false;
  DateTime? _lastPullAt;

  InboxBloc({
    required BaseMessageRepository repository,
    this.contextResolver,
    this.pollInterval = defaultPollInterval,
    this.resumeThrottle = defaultResumeThrottle,
  })  : _repository = repository,
        super(const InboxInitial()) {
    on<LoadInbox>(_onLoad);
    on<InboxRefreshRequested>(_onRefresh);
    on<InboxPollRequested>(_onPoll);
    on<InboxPaused>(_onPaused);
    on<InboxResumed>(_onResumed);
    on<InboxReset>(_onReset);

    _changes = _repository.watchChanges().listen((_) {
      if (_userId != null) add(const InboxRefreshRequested());
    });
  }

  void _startPolling() {
    _pollTimer ??= Timer.periodic(
      pollInterval,
      (_) => add(const InboxPollRequested()),
    );
  }

  void _stopPolling() {
    _pollTimer?.cancel();
    _pollTimer = null;
  }

  Future<void> _onLoad(LoadInbox event, Emitter<InboxState> emit) async {
    _userId = event.userId;
    _startPolling();
    emit(const InboxLoading());
    await _load(emit, pull: true);
  }

  Future<void> _onRefresh(
    InboxRefreshRequested event,
    Emitter<InboxState> emit,
  ) async {
    await _load(emit, pull: false);
  }

  Future<void> _onPoll(
    InboxPollRequested event,
    Emitter<InboxState> emit,
  ) async {
    await _load(emit, pull: true, silent: true);
  }

  void _onPaused(InboxPaused event, Emitter<InboxState> emit) {
    _stopPolling();
  }

  Future<void> _onResumed(
    InboxResumed event,
    Emitter<InboxState> emit,
  ) async {
    if (_userId == null) return; // Never loaded — nothing to resume.
    _startPolling();
    final last = _lastPullAt;
    if (last != null && DateTime.now().difference(last) < resumeThrottle) {
      return; // Fresh enough — don't double-pull on resume blips.
    }
    await _load(emit, pull: true, silent: true);
  }

  void _onReset(InboxReset event, Emitter<InboxState> emit) {
    _stopPolling();
    _userId = null;
    _pullPending = false;
    emit(const InboxInitial());
  }

  Future<void> _load(
    Emitter<InboxState> emit, {
    required bool pull,
    bool silent = false,
  }) async {
    final userId = _userId;
    if (userId == null) return;
    try {
      if (pull) {
        if (_pulling) {
          // Coalesce: the in-flight pull will run one trailing pull for us
          // (its cursor predates whatever triggered this request).
          _pullPending = true;
          return;
        }
        _pulling = true;
        try {
          do {
            _pullPending = false;
            await _repository.pullMessages(userId); // swallows offline
          } while (_pullPending && _userId == userId);
          _lastPullAt = DateTime.now();
        } finally {
          _pulling = false;
        }
        // A reset (sign-out) or user switch mid-pull means this run must not
        // read or emit for the user it started with.
        if (_userId != userId) return;
      }
      final conversations = await _repository.loadConversations(userId);
      final totalUnread =
          conversations.fold<int>(0, (sum, c) => sum + c.unreadCount);
      emit(InboxLoaded(conversations: conversations, totalUnread: totalUnread));
    } on Object catch (e) {
      // A failed background poll must not blank a healthy inbox.
      if (!silent) {
        emit(InboxError(message: e.toString(), code: mapErrorToCode(e)));
      }
    }
  }

  @override
  Future<void> close() {
    _changes?.cancel();
    _stopPolling();
    return super.close();
  }
}
