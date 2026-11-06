import 'package:flutter/widgets.dart';

import 'package:wereda_connect/core/models/message.dart';
import 'package:wereda_connect/features/messaging/domain/notified_conversation.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/conversation_screen.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/messages_inbox_screen.dart';
import 'package:wereda_connect/features/messaging/presentation/widgets/message_arrival_listener.dart';

/// FUP-90: the screen a tapped message notification opens for [userId], or
/// null when the notification is not theirs (another account signed in on a
/// shared phone, FUP-121). The caller pushes it on the root navigator.
///
/// A thread the device already holds opens at once, and [requestPull] asks
/// the inbox to fetch the tapped message, which the open thread shows as it
/// lands. A thread the device holds nothing of (its first message) is pulled
/// first, best-effort and bounded by [pullTimeout], so the screen can name
/// the sender. When the device still holds none of it (offline, or a thread
/// the server does not let this user read) the messages list opens instead.
Future<Widget?> notifiedConversationScreen({
  required BaseMessageRepository repository,
  required String userId,
  required NotifiedConversation conversation,
  void Function()? requestPull,
  Duration pullTimeout = const Duration(seconds: 8),
}) async {
  if (!conversation.isFor(userId)) return null;
  var thread = await repository.loadThread(conversation.conversationId);
  if (thread.isNotEmpty) {
    requestPull?.call();
  } else {
    thread = await _pullThread(repository, userId, conversation, pullTimeout);
  }
  if (thread.isEmpty) return const MessagesInboxScreen();
  return ConversationScreen(
    contextType: conversation.contextType,
    contextId: conversation.contextId,
    counterpartId: conversation.senderId,
    counterpartName: _counterpartName(thread, conversation.senderId),
  );
}

/// Pulls the user's messages and reads the thread again. The pull announces
/// what it finds as an arrival, so the thread counts as on screen meanwhile:
/// no banner pops over the thread the user is about to see.
Future<List<Message>> _pullThread(
  BaseMessageRepository repository,
  String userId,
  NotifiedConversation conversation,
  Duration pullTimeout,
) async {
  final onScreen = ActiveConversation.id;
  ActiveConversation.id = conversation.conversationId;
  try {
    await repository.pullMessages(userId).timeout(pullTimeout);
  } on Object {
    // Offline or slow: decided on what the device holds.
  } finally {
    if (ActiveConversation.id == conversation.conversationId) {
      ActiveConversation.id = onScreen;
    }
  }
  return repository.loadThread(conversation.conversationId);
}

/// The counterpart's name as the newest message that carries it has it (the
/// server stamps both names on every message it stores).
String? _counterpartName(List<Message> thread, String counterpartId) {
  for (final m in thread.reversed) {
    final name = m.senderId == counterpartId ? m.senderName : m.recipientName;
    if (name != null && name.trim().isNotEmpty) return name;
  }
  return null;
}
