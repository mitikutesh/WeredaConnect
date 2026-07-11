import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/conversation_read.dart';
import 'package:wereda_connect/core/database/schemas/message.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/messaging/data/repositories/message_repository.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

void main() {
  group('MessageRepository', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue syncQueue;
    late MessageRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('message_repo_test_');
      isar = await Isar.open(
        [MessageSchema, ConversationReadSchema, SyncOperationSchema],
        directory: tempDir.path,
        name: 'test_message_repo_${DateTime.now().millisecondsSinceEpoch}',
      );
      syncQueue = SyncQueue(isar);
      // No ApiClient → pullMessages is a no-op; local store only.
      repository = MessageRepository(isar: isar, syncQueue: syncQueue);
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    test('conversationIdFor is deterministic and direction-independent', () {
      final a = BaseMessageRepository.conversationIdFor(
        contextType: 'crop',
        contextId: 'crop-1',
        userA: 'buyer-1',
        userB: 'farmer-1',
      );
      final b = BaseMessageRepository.conversationIdFor(
        contextType: 'crop',
        contextId: 'crop-1',
        userA: 'farmer-1',
        userB: 'buyer-1',
      );
      expect(a, b);
      expect(a, 'crop:crop-1:buyer-1:farmer-1');
    });

    test('sendMessage writes locally and enqueues a message create op',
        () async {
      final message = await repository.sendMessage(
        contextType: 'crop',
        contextId: 'crop-1',
        senderId: 'buyer-1',
        recipientId: 'farmer-1',
        body: 'Is this still available?',
        senderName: 'Buyer One',
        recipientName: 'Farmer One',
        recipientPhone: '+251900000002',
      );

      expect(message.conversationId, 'crop:crop-1:buyer-1:farmer-1');
      expect(message.senderId, 'buyer-1');

      final fromDb = await isar.messages
          .filter()
          .messageIdEqualTo(message.messageId)
          .findFirst();
      expect(fromDb, isNotNull);
      expect(fromDb!.body, 'Is this still available?');

      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 1);
      expect(pending.first.entityType, 'message');
      expect(pending.first.action, 'create');
      expect(pending.first.entityId, message.messageId);
      // Name/phone must NOT be in the synced payload (server denormalizes them).
      expect(pending.first.data.contains('senderName'), isFalse);
      expect(pending.first.data.contains('recipientPhone'), isFalse);
    });

    test('loadThread returns messages oldest-first', () async {
      await repository.sendMessage(
        contextType: 'crop',
        contextId: 'crop-1',
        senderId: 'buyer-1',
        recipientId: 'farmer-1',
        body: 'first',
      );
      await repository.sendMessage(
        contextType: 'crop',
        contextId: 'crop-1',
        senderId: 'buyer-1',
        recipientId: 'farmer-1',
        body: 'second',
      );

      final conversationId = BaseMessageRepository.conversationIdFor(
        contextType: 'crop',
        contextId: 'crop-1',
        userA: 'buyer-1',
        userB: 'farmer-1',
      );
      final thread = await repository.loadThread(conversationId);
      expect(thread.map((m) => m.body).toList(), ['first', 'second']);
    });

    test('inbound message is unread until the conversation is marked read',
        () async {
      final conversationId = BaseMessageRepository.conversationIdFor(
        contextType: 'crop',
        contextId: 'crop-1',
        userA: 'buyer-1',
        userB: 'farmer-1',
      );
      // Simulate a received (inbound) message from the counterpart.
      final inbound = Message()
        ..messageId = 'in-1'
        ..conversationId = conversationId
        ..contextType = 'crop'
        ..contextId = 'crop-1'
        ..senderId = 'farmer-1'
        ..recipientId = 'buyer-1'
        ..body = 'Yes, still available'
        ..createdAt = DateTime.utc(2026, 7, 9, 10)
        ..version = 1;
      await isar.writeTxn(() async => isar.messages.put(inbound));

      var conversations = await repository.loadConversations('buyer-1');
      expect(conversations.length, 1);
      expect(conversations.first.unreadCount, 1);

      await repository.markConversationRead(conversationId);
      conversations = await repository.loadConversations('buyer-1');
      expect(conversations.first.unreadCount, 0);
    });

    test('markConversationRead is idempotent once the marker covers the thread',
        () async {
      final conversationId = BaseMessageRepository.conversationIdFor(
        contextType: 'crop',
        contextId: 'crop-1',
        userA: 'buyer-1',
        userB: 'farmer-1',
      );
      final inbound = Message()
        ..messageId = 'in-1'
        ..conversationId = conversationId
        ..contextType = 'crop'
        ..contextId = 'crop-1'
        ..senderId = 'farmer-1'
        ..recipientId = 'buyer-1'
        ..body = 'hello'
        ..createdAt = DateTime.utc(2026, 7, 9, 10)
        ..version = 1;
      await isar.writeTxn(() async => isar.messages.put(inbound));

      await repository.markConversationRead(conversationId);
      final first = await isar.conversationReads
          .filter()
          .conversationIdEqualTo(conversationId)
          .findFirst();
      expect(first, isNotNull);

      // A second call must not write again: the thread bloc marks read on
      // every refresh AND refreshes on every store change, so a repeated
      // write would spin refresh → write → refresh forever.
      await repository.markConversationRead(conversationId);
      final second = await isar.conversationReads
          .filter()
          .conversationIdEqualTo(conversationId)
          .findFirst();
      expect(second!.lastReadAt, first!.lastReadAt);
    });

    test(
        'markConversationRead clears unread even when the message timestamp '
        'is ahead of the device clock', () async {
      final conversationId = BaseMessageRepository.conversationIdFor(
        contextType: 'crop',
        contextId: 'crop-1',
        userA: 'buyer-1',
        userB: 'farmer-1',
      );
      // Server-stamped createdAt an hour ahead of this device's clock.
      final inbound = Message()
        ..messageId = 'in-skew'
        ..conversationId = conversationId
        ..contextType = 'crop'
        ..contextId = 'crop-1'
        ..senderId = 'farmer-1'
        ..recipientId = 'buyer-1'
        ..body = 'from the future'
        ..createdAt = DateTime.now().toUtc().add(const Duration(hours: 1))
        ..version = 1;
      await isar.writeTxn(() async => isar.messages.put(inbound));

      await repository.markConversationRead(conversationId);
      final conversations = await repository.loadConversations('buyer-1');
      expect(conversations.single.unreadCount, 0);
    });

    test(
        'pullMessages emits inbound arrivals on watchInbound — '
        'but not on first hydration and not for own messages', () async {
      final api = _MockApiClient();
      final pulling = MessageRepository(
        isar: isar,
        syncQueue: syncQueue,
        apiClient: api,
      );

      Map<String, dynamic> wireMessage(
        String id,
        String body,
        DateTime at, {
        String senderId = 'farmer-1',
        String recipientId = 'buyer-1',
      }) =>
          {
            'messageId': id,
            'conversationId': 'crop:crop-1:buyer-1:farmer-1',
            'contextType': 'crop',
            'contextId': 'crop-1',
            'senderId': senderId,
            'recipientId': recipientId,
            'body': body,
            'createdAt': at.toIso8601String(),
            'version': 1,
          };

      final arrivals = <List<Message>>[];
      final subscription = pulling.watchInbound().listen(arrivals.add);
      addTearDown(subscription.cancel);

      // First hydration of an empty store: stored, but no arrival banner.
      when(
        () => api.get<Map<String, dynamic>>(
          '/messages',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer(
        (_) async => {
          'messages': [
            wireMessage('in-1', 'hello', DateTime.utc(2026, 7, 9, 10)),
          ],
        },
      );
      expect(await pulling.pullMessages('buyer-1'), 1);
      await Future<void>.delayed(Duration.zero);
      expect(arrivals, isEmpty);

      // A later pull discovering a new inbound message IS an arrival.
      when(
        () => api.get<Map<String, dynamic>>(
          '/messages',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer(
        (_) async => {
          'messages': [
            wireMessage('in-2', 'again', DateTime.utc(2026, 7, 9, 11)),
          ],
        },
      );
      expect(await pulling.pullMessages('buyer-1'), 1);
      await Future<void>.delayed(Duration.zero);
      expect(arrivals.length, 1);
      expect(arrivals.single.single.messageId, 'in-2');

      // Messages I sent from another device are not "arrivals" for me.
      when(
        () => api.get<Map<String, dynamic>>(
          '/messages',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer(
        (_) async => {
          'messages': [
            wireMessage(
              'out-1',
              'mine',
              DateTime.utc(2026, 7, 9, 12),
              senderId: 'buyer-1',
              recipientId: 'farmer-1',
            ),
          ],
        },
      );
      expect(await pulling.pullMessages('buyer-1'), 1);
      await Future<void>.delayed(Duration.zero);
      expect(arrivals.length, 1);
    });

    test('loadConversations groups distinct contexts separately', () async {
      await repository.sendMessage(
        contextType: 'crop',
        contextId: 'crop-1',
        senderId: 'buyer-1',
        recipientId: 'farmer-1',
        body: 'about the crop',
      );
      await repository.sendMessage(
        contextType: 'trip',
        contextId: 'trip-9',
        senderId: 'buyer-1',
        recipientId: 'trucker-1',
        body: 'about the trip',
      );

      final conversations = await repository.loadConversations('buyer-1');
      expect(conversations.length, 2);
    });
  });
}
