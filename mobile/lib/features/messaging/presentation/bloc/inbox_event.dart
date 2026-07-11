import 'package:equatable/equatable.dart';

/// Base class for inbox events.
abstract class InboxEvent extends Equatable {
  const InboxEvent();

  @override
  List<Object?> get props => [];
}

/// Load the current user's conversations (pulls from the server first).
class LoadInbox extends InboxEvent {
  final String userId;

  const LoadInbox(this.userId);

  @override
  List<Object?> get props => [userId];
}

/// Re-read conversations from the local store (fired when it changes; no pull).
class InboxRefreshRequested extends InboxEvent {
  const InboxRefreshRequested();
}

/// Periodic background poll: pull new messages from the server, refresh
/// silently, and never surface an error (offline polls keep the last state).
class InboxPollRequested extends InboxEvent {
  const InboxPollRequested();
}

/// The app moved to the background (or the web tab was hidden) — stop the
/// poll timer so an idle backgrounded app stops burning 3G data.
class InboxPaused extends InboxEvent {
  const InboxPaused();
}

/// The app returned to the foreground (or the web tab regained visibility) —
/// restart the poll timer and catch up immediately (throttled, so resume
/// blips like notification taps don't double-pull).
class InboxResumed extends InboxEvent {
  const InboxResumed();
}

/// The user signed out — stop polling and drop per-user state. Without this
/// the app-global bloc keeps polling as the previous user forever.
class InboxReset extends InboxEvent {
  const InboxReset();
}
