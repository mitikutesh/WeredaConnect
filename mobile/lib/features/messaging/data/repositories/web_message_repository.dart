import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/message.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/messaging/data/conversation_grouping.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';

/// Web-only message repository. All operations use HTTP via [ApiClient];
/// no Isar dependency, no local read-state (web relies on refresh, not push).
class WebMessageRepository extends BaseMessageRepository {
  static const _uuid = Uuid();
  final ApiClient _api;

  WebMessageRepository({required ApiClient api}) : _api = api;

  @override
  Future<Message> sendMessage({
    required String contextType,
    required String contextId,
    required String senderId,
    required String recipientId,
    required String body,
    String? senderName,
    String? senderPhone,
    String? recipientName,
    String? recipientPhone,
  }) async {
    final messageId = _uuid.v4();
    final conversationId = BaseMessageRepository.conversationIdFor(
      contextType: contextType,
      contextId: contextId,
      userA: senderId,
      userB: recipientId,
    );
    final now = DateTime.now().toUtc();
    final trimmed = body.trim();

    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'message',
            'action': 'create',
            'entityId': messageId,
            'timestamp': now.toIso8601String(),
            'idempotencyKey': 'web_msg_$messageId',
            'data': {
              'messageId': messageId,
              'conversationId': conversationId,
              'contextType': contextType,
              'contextId': contextId,
              'senderId': senderId,
              'recipientId': recipientId,
              'body': trimmed,
              'createdAt': now.toIso8601String(),
              // Name/phone intentionally omitted — server denormalizes them.
            },
          },
        ],
      },
    );
    ensureSyncSucceeded(response);

    return Message()
      ..messageId = messageId
      ..conversationId = conversationId
      ..contextType = contextType
      ..contextId = contextId
      ..senderId = senderId
      ..recipientId = recipientId
      ..senderName = senderName
      ..senderPhone = senderPhone
      ..recipientName = recipientName
      ..recipientPhone = recipientPhone
      ..body = trimmed
      ..createdAt = now
      ..version = 1;
  }

  @override
  Future<List<Message>> loadThread(String conversationId) async {
    try {
      final data = await _api.get<Map<String, dynamic>>(
        '/messages',
        queryParameters: {'conversationId': conversationId},
      );
      final list = (data['messages'] as List?) ?? const [];
      final messages =
          list.map((e) => parseMessage(e as Map<String, dynamic>)).toList();
      messages.sort((a, b) => a.createdAt.compareTo(b.createdAt));
      return messages;
    } on Object {
      // Surface the failure so the bloc shows an error state. Returning an empty
      // list here rendered a genuine API error as a false "No conversations yet" /
      // empty thread. A truly empty result still returns [] from the success path.
      rethrow;
    }
  }

  @override
  Future<List<ConversationSummary>> loadConversations(
    String currentUserId,
  ) async {
    try {
      // No dedicated "list conversations" endpoint — pull recent messages
      // to/from me and group client-side. `since` empty = no lower bound.
      final data = await _api.get<Map<String, dynamic>>(
        '/messages',
        queryParameters: {'since': ''},
      );
      final list = (data['messages'] as List?) ?? const [];
      final messages =
          list.map((e) => parseMessage(e as Map<String, dynamic>)).toList();
      // lastRead null → web shows no unread badges (no local read store).
      return buildConversationSummaries(messages, currentUserId);
    } on Object {
      // Surface the failure so the bloc shows an error state. Returning an empty
      // list here rendered a genuine API error as a false "No conversations yet" /
      // empty thread. A truly empty result still returns [] from the success path.
      rethrow;
    }
  }

  @override
  Future<int> totalUnread(String currentUserId) async => 0;

  @override
  Future<void> markConversationRead(String conversationId) async {}

  @override
  Future<int> pullMessages(String currentUserId) async => 0;

  @override
  Stream<void> watchChanges() => const Stream<void>.empty();
}
