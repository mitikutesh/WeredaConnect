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
