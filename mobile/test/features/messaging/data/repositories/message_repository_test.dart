import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/conversation_read.dart';
import 'package:wereda_connect/core/database/schemas/message.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/messaging/data/repositories/message_repository.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';

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

    test('sendMessage writes locally and enqueues a message create op', () async {
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
