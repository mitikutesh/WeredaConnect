import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/models/message.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/messaging/data/repositories/web_message_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('WebMessageRepository', () {
    late _MockApiClient api;
    late WebMessageRepository repository;

    setUp(() {
      SharedPreferences.setMockInitialValues({});
      api = _MockApiClient();
      repository = WebMessageRepository(api: api);
    });

    Map<String, dynamic> wireMessage(
      String id,
      DateTime at, {
      String senderId = 'farmer-1',
      String recipientId = 'buyer-1',
      String body = 'hello',
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

    void stubMessages(List<Map<String, dynamic>> messages) {
      when(
        () => api.get<Map<String, dynamic>>(
          '/messages',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer((_) async => {'messages': messages});
    }

    test(
        'unread counts now work on web: loadConversations counts inbound, '
        'markConversationRead clears and persists in prefs', () async {
      stubMessages([wireMessage('in-1', DateTime.utc(2026, 7, 9, 10))]);

      final conversations = await repository.loadConversations('buyer-1');
      expect(conversations.length, 1);
      expect(conversations.single.unreadCount, 1);

      final conversationId = conversations.single.conversationId;
      await repository.markConversationRead(conversationId);

      final after = await repository.loadConversations('buyer-1');
      expect(after.single.unreadCount, 0);
      expect(await repository.totalUnread('buyer-1'), 0);

      // The marker survives in localStorage-backed prefs.
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('msg_read_$conversationId'), isNotNull);
    });

    test('markConversationRead is idempotent (no watch event when covered)',
        () async {
      stubMessages([wireMessage('in-1', DateTime.utc(2026, 7, 9, 10))]);
      await repository.loadConversations('buyer-1');
      const conversationId = 'crop:crop-1:buyer-1:farmer-1';

      await repository.markConversationRead(conversationId);

      final events = <void>[];
      final subscription = repository.watchChanges().listen(events.add);
      addTearDown(subscription.cancel);

      // Marker already covers the newest cached message → must not emit
      // (an emit here would spin the thread bloc's refresh loop).
      await repository.markConversationRead(conversationId);
      await Future<void>.delayed(Duration.zero);
      expect(events, isEmpty);
    });

    test(
        'pullMessages fetches deltas and emits arrivals only after '
        'first hydration', () async {
      final arrivals = <List<Message>>[];
      final subscription = repository.watchInbound().listen(arrivals.add);
      addTearDown(subscription.cancel);

      // First hydration: bootstraps history, no arrival.
      stubMessages([wireMessage('in-1', DateTime.utc(2026, 7, 9, 10))]);
      expect(await repository.pullMessages('buyer-1'), 1);
      await Future<void>.delayed(Duration.zero);
      expect(arrivals, isEmpty);

      // Later pull with a new inbound message: delta cursor + arrival.
      stubMessages([wireMessage('in-2', DateTime.utc(2026, 7, 9, 11))]);
      expect(await repository.pullMessages('buyer-1'), 1);
      await Future<void>.delayed(Duration.zero);
      expect(arrivals.length, 1);
      expect(arrivals.single.single.messageId, 'in-2');
      verify(
        () => api.get<Map<String, dynamic>>(
          '/messages',
          queryParameters: {'since': '2026-07-09T10:00:00.000Z'},
        ),
      ).called(1);
    });

    test('pullMessages swallows network failures (offline poll)', () async {
      when(
        () => api.get<Map<String, dynamic>>(
          '/messages',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenThrow(Exception('offline'));
      expect(await repository.pullMessages('buyer-1'), 0);
    });
  });
}
