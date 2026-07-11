import 'dart:async';

import 'package:shared_preferences/shared_preferences.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/message.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/messaging/data/conversation_grouping.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';

/// Web-only message repository. All operations use HTTP via [ApiClient],
/// with a session-scoped in-memory cache so polling only fetches deltas.
/// Read state persists in browser localStorage (via shared_preferences), so
/// web shows the same unread badges/dots as mobile.
class WebMessageRepository extends BaseMessageRepository {
  static const _uuid = Uuid();

  /// localStorage key prefix for per-conversation last-read markers. The
  /// conversationId embeds both participant ids, so keys never collide
  /// across accounts sharing a browser.
  static const _readKeyPrefix = 'msg_read_';

  final ApiClient _api;

  /// Session cache of every message seen, keyed by messageId. Reset on page
  /// reload — the first pull re-hydrates from the server.
  final Map<String, Message> _cache = {};
  bool _hydrated = false;

  final _changesController = StreamController<void>.broadcast();
  final _inboundController = StreamController<List<Message>>.broadcast();

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

    _cache[messageId] = message;
    // Sending counts as reading up to now (mirrors the native repository).
    await markConversationRead(conversationId);
    return message;
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
      var changed = false;
      for (final m in messages) {
        if (m.messageId.isEmpty) continue;
        changed = changed || !_cache.containsKey(m.messageId);
        _cache[m.messageId] = m;
      }
      // A thread fetch can discover messages the poll hasn't seen yet — let
      // the inbox refresh its previews/unread from the updated cache.
      if (changed) _changesController.add(null);
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
    // First call must hydrate from the server; a failure here should surface
    // (an empty inbox and a dead connection are different states). Once
    // hydrated, serve the cache — pullMessages keeps it fresh.
    if (!_hydrated) {
      await _fetchInto(currentUserId, since: '');
      _hydrated = true;
    }
    return buildConversationSummaries(
      _cache.values.toList(),
      currentUserId,
      lastRead: await _lastReadMap(),
    );
  }

  @override
  Future<int> totalUnread(String currentUserId) async {
    final summaries = await loadConversations(currentUserId);
    return summaries.fold<int>(0, (sum, c) => sum + c.unreadCount);
  }

  @override
  Future<void> markConversationRead(String conversationId) async {
    // Idempotent like the native repo: if the stored marker already covers
    // the newest cached message, don't write — an unconditional write would
    // fire watchChanges and spin the thread bloc's refresh loop forever.
    DateTime? newest;
    for (final m in _cache.values) {
      if (m.conversationId != conversationId) continue;
      if (newest == null || m.createdAt.isAfter(newest)) newest = m.createdAt;
    }
    if (newest == null) return;
    final prefs = await SharedPreferences.getInstance();
    final existing = DateTime.tryParse(
      prefs.getString('$_readKeyPrefix$conversationId') ?? '',
    );
    if (existing != null && !newest.isAfter(existing.toUtc())) return;
    await prefs.setString(
      '$_readKeyPrefix$conversationId',
      DateTime.now().toUtc().toIso8601String(),
    );
    _changesController.add(null);
  }

  @override
  Future<int> pullMessages(String currentUserId) async {
    final firstHydration = !_hydrated;
    // Stateless cursor: everything newer than the newest cached message
    // (server compares createdAt strictly greater; empty = no lower bound).
    DateTime? newest;
    for (final m in _cache.values) {
      if (newest == null || m.createdAt.isAfter(newest)) newest = m.createdAt;
    }
    final since =
        firstHydration ? '' : (newest?.toUtc().toIso8601String() ?? '');

    try {
      final newInbound = await _fetchInto(currentUserId, since: since);
      _hydrated = true;
      // Bootstrapping the session cache is history, not "arrivals".
      if (!firstHydration && newInbound.isNotEmpty) {
        _inboundController.add(newInbound);
      }
      return newInbound.length;
    } on Object {
      // Best-effort like the native pull: offline polling must not throw.
      return 0;
    }
  }

  /// Fetch messages newer than [since] and merge them into the cache.
  /// Returns the newly-seen messages addressed to [currentUserId].
  /// Throws on network/API failure — callers decide whether that surfaces.
  Future<List<Message>> _fetchInto(
    String currentUserId, {
    required String since,
  }) async {
    final data = await _api.get<Map<String, dynamic>>(
      '/messages',
      queryParameters: {'since': since},
    );
    final list = (data['messages'] as List?) ?? const [];
    final newInbound = <Message>[];
    var changed = false;
    for (final raw in list) {
      final parsed = parseMessage(raw as Map<String, dynamic>);
      if (parsed.messageId.isEmpty) continue;
      if (!_cache.containsKey(parsed.messageId)) {
        changed = true;
        if (parsed.recipientId == currentUserId) newInbound.add(parsed);
      }
      _cache[parsed.messageId] = parsed;
    }
    if (changed) _changesController.add(null);
    return newInbound;
  }

  Future<Map<String, DateTime>> _lastReadMap() async {
    final prefs = await SharedPreferences.getInstance();
    final map = <String, DateTime>{};
    for (final key in prefs.getKeys()) {
      if (!key.startsWith(_readKeyPrefix)) continue;
      final at = DateTime.tryParse(prefs.getString(key) ?? '');
      if (at != null) map[key.substring(_readKeyPrefix.length)] = at.toUtc();
    }
    return map;
  }

  @override
  Stream<void> watchChanges() => _changesController.stream;

  @override
  Stream<List<Message>> watchInbound() => _inboundController.stream;
}
