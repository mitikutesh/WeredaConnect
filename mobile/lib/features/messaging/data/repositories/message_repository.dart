import 'dart:async';
import 'dart:convert';

import 'package:async/async.dart';
import 'package:flutter/foundation.dart';
import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/conversation_read.dart';
import 'package:wereda_connect/core/models/message.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/messaging/data/conversation_grouping.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';

/// Offline-first repository for chat messages.
///
/// Writes go to local Isar first (offline-first), then enqueue a `message`
/// sync operation. Name/phone are stored locally for immediate display but are
/// NOT synced — the server denormalizes them authoritatively. Inbound messages
/// are pulled from the server into Isar via [pullMessages]; unread state is
/// tracked per-device in a local [ConversationRead] store.
class MessageRepository extends BaseMessageRepository {
  final Isar _isar;
  final SyncQueue _syncQueue;

  /// Optional API client for the sync-down pull. Null in tests / pure offline
  /// setups (the inbox then shows only locally-authored messages).
  final ApiClient? _apiClient;

  static const _uuid = Uuid();

  final _inboundController = StreamController<List<Message>>.broadcast();

  MessageRepository({
    required Isar isar,
    required SyncQueue syncQueue,
    ApiClient? apiClient,
  })  : _isar = isar,
        _syncQueue = syncQueue,
        _apiClient = apiClient;

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

    final message = Message()
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

    await _isar.writeTxn(() async {
      await _isar.messages.put(message);
      // Sending counts as reading up to now.
      await _setLastReadAt(conversationId, now);
    });

    // The local write is authoritative (offline-first); a failed enqueue must
    // not surface as a send failure — the sync layer reconciles later.
    try {
      await _syncQueue.enqueue(
        userId: senderId,
        entityType: 'message',
        entityId: messageId,
        action: 'create',
        data: jsonEncode({
          'messageId': messageId,
          'conversationId': conversationId,
          'contextType': contextType,
          'contextId': contextId,
          'senderId': senderId,
          'recipientId': recipientId,
          'body': trimmed,
          'createdAt': now.toIso8601String(),
        }),
        operationId: _uuid.v4(),
      );
    } on Object catch (e) {
      debugPrint('[MessageRepository] saved locally but enqueue failed: $e');
    }

    return message;
  }

  @override
  Future<List<Message>> loadThread(String conversationId) {
    return _isar.messages
        .filter()
        .conversationIdEqualTo(conversationId)
        .sortByCreatedAt()
        .findAll();
  }

  @override
  Future<List<ConversationSummary>> loadConversations(
    String currentUserId,
  ) async {
    final messages = await _isar.messages
        .filter()
        .senderIdEqualTo(currentUserId)
        .or()
        .recipientIdEqualTo(currentUserId)
        .findAll();

    final lastRead = await _lastReadMap();
    return buildConversationSummaries(
      messages,
      currentUserId,
      lastRead: lastRead,
    );
  }

  @override
  Future<int> totalUnread(String currentUserId) async {
    final summaries = await loadConversations(currentUserId);
    return summaries.fold<int>(0, (sum, c) => sum + c.unreadCount);
  }

  @override
  Future<void> markConversationRead(String conversationId) async {
    // Idempotent: skip the write when the marker already covers every message
    // we hold. The thread bloc marks read on every refresh AND refreshes on
    // every store change — an unconditional write here would fire
    // watchChanges and spin refresh → write → refresh forever.
    final newest = await _isar.messages
        .filter()
        .conversationIdEqualTo(conversationId)
        .sortByCreatedAtDesc()
        .findFirst();
    if (newest == null) return;
    final existing = await _isar.conversationReads
        .filter()
        .conversationIdEqualTo(conversationId)
        .findFirst();
    if (existing != null && !newest.createdAt.isAfter(existing.lastReadAt)) {
      return;
    }
    // The marker must cover the newest message even when the device clock
    // lags the server timestamps — a plain now() could stay behind forever,
    // so unread would never clear and the idempotence guard above would stop
    // holding (write → watch event → refresh → write ...).
    final nowUtc = DateTime.now().toUtc();
    final at = newest.createdAt.isAfter(nowUtc) ? newest.createdAt : nowUtc;
    await _isar.writeTxn(() async {
      await _setLastReadAt(conversationId, at);
    });
  }

  @override
  Future<int> pullMessages(String currentUserId) async {
    final api = _apiClient;
    if (api == null) return 0;

    // Stateless cursor: pull everything newer than the newest message we
    // already hold (server compares createdAt strictly greater than `since`).
    final newestLocal =
        await _isar.messages.where().sortByCreatedAtDesc().findFirst();
    // On an empty local store, send since='' (not omitted): the server's /messages
    // endpoint requires either conversationId or since, and treats an empty since as
    // "no lower bound". Omitting it entirely 400s, so a recipient who never chatted
    // could never bootstrap their first inbound message. Mirrors the web repository.
    final since = newestLocal?.createdAt.toUtc().toIso8601String() ?? '';

    try {
      final data = await api.get<Map<String, dynamic>>(
        '/messages',
        queryParameters: {'since': since},
      );
      final list = (data['messages'] as List?) ?? const [];
      if (list.isEmpty) return 0;

      var newCount = 0;
      final newInbound = <Message>[];
      await _isar.writeTxn(() async {
        for (final raw in list) {
          final parsed = parseMessage(raw as Map<String, dynamic>);
          if (parsed.messageId.isEmpty) continue;
          final existing = await _isar.messages
              .filter()
              .messageIdEqualTo(parsed.messageId)
              .findFirst();
          if (existing != null) {
            parsed.id =
                existing.id; // idempotent upsert (messages are immutable)
          } else {
            newCount++;
            if (parsed.recipientId == currentUserId) newInbound.add(parsed);
          }
          await _isar.messages.put(parsed);
        }
      });
      // First hydration of an empty store bootstraps history — not "arrivals".
      if (newestLocal != null && newInbound.isNotEmpty) {
        _inboundController.add(newInbound);
      }
      return newCount;
    } on Object catch (e) {
      debugPrint('[MessageRepository] pullMessages failed: $e');
      return 0;
    }
  }

  @override
  Stream<List<Message>> watchInbound() => _inboundController.stream;

  @override
  Stream<void> watchChanges() => StreamGroup.merge<void>([
        // Watch both messages AND read-state: marking a conversation read updates
        // conversationReads (not messages), and the inbox unread badge must refresh
        // when that happens, not only when a new message arrives.
        _isar.messages.watchLazy(),
        _isar.conversationReads.watchLazy(),
      ]);

  // ── read-state helpers ──────────────────────────────────────────────

  /// Upsert the last-read marker. MUST be called inside a write transaction.
  Future<void> _setLastReadAt(String conversationId, DateTime at) async {
    final existing = await _isar.conversationReads
        .filter()
        .conversationIdEqualTo(conversationId)
        .findFirst();
    final marker = (existing ?? ConversationRead())
      ..conversationId = conversationId
      ..lastReadAt = at;
    // Only advance the marker forward.
    if (existing != null && existing.lastReadAt.isAfter(at)) return;
    await _isar.conversationReads.put(marker);
  }

  Future<Map<String, DateTime>> _lastReadMap() async {
    final all = await _isar.conversationReads.where().findAll();
    return {for (final r in all) r.conversationId: r.lastReadAt};
  }
}
