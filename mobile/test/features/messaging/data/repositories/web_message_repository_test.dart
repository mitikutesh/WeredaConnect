import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/message.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
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

    test('a user switch in the same session drops the previous cache',
        () async {
      stubMessages([wireMessage('in-1', DateTime.utc(2026, 7, 9, 10))]);
      final first = await repository.loadConversations('buyer-1');
      expect(first.length, 1);

      // Same browser session, different account: buyer-1's conversations
      // must not leak into farmer-9's inbox, and the cache must re-hydrate.
      stubMessages(const []);
      final second = await repository.loadConversations('farmer-9');
      expect(second, isEmpty);
    });

    test(
        'read marker covers messages whose server timestamp is ahead of '
        'the client clock', () async {
      // Server-stamped createdAt an hour ahead of this machine's clock.
      final skewed = DateTime.now().toUtc().add(const Duration(hours: 1));
      stubMessages([wireMessage('in-1', skewed)]);

      final before = await repository.loadConversations('buyer-1');
      expect(before.single.unreadCount, 1);

      await repository.markConversationRead(before.single.conversationId);
      final after = await repository.loadConversations('buyer-1');
      expect(after.single.unreadCount, 0);
    });

    test(
        'pullMessages counts new outbound messages (other device) without '
        'emitting arrivals', () async {
      final arrivals = <List<Message>>[];
      final subscription = repository.watchInbound().listen(arrivals.add);
      addTearDown(subscription.cancel);

      // Hydrate first so the next pull is not first-hydration.
      stubMessages([wireMessage('in-1', DateTime.utc(2026, 7, 9, 10))]);
      await repository.pullMessages('buyer-1');

      // A message I sent from another device: new to the cache (counted),
      // but not an arrival for me.
      stubMessages([
        wireMessage(
          'out-1',
          DateTime.utc(2026, 7, 9, 11),
          senderId: 'buyer-1',
          recipientId: 'farmer-1',
        ),
      ]);
      expect(await repository.pullMessages('buyer-1'), 1);
      await Future<void>.delayed(Duration.zero);
      expect(arrivals, isEmpty);
    });
    // ESC-48: no phone number is kept on a message, sent or pulled.
    test('a pulled message keeps no phone even if the server sent one',
        () async {
      stubMessages([
        wireMessage('in-1', DateTime.utc(2026, 7, 9, 10))
          ..['senderPhone'] = '+251911000002'
          ..['recipientPhone'] = '+251911000001',
      ]);

      await repository.pullMessages('buyer-1');
      final thread =
          await repository.loadThread('crop:crop-1:buyer-1:farmer-1');

      expect(thread.single.senderPhone, isNull);
      expect(thread.single.recipientPhone, isNull);
    });

    test('a send refused because contact is not open is not cached', () async {
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'results': [
            {
              'operationId': 'op',
              'status': 'error',
              'message': 'MESSAGE_CONTACT_LOCKED: Contact opens after payment '
                  'is held in escrow and the farmer accepts the order.',
            },
          ],
        },
      );
      stubMessages([]);

      final error = await repository
          .sendMessage(
            contextType: 'crop',
            contextId: 'crop-1',
            senderId: 'buyer-1',
            recipientId: 'farmer-1',
            body: 'hello',
          )
          .then<Object?>((_) => null, onError: (Object e) => e);

      expect(error, isA<SyncRejectedException>());
      expect(mapErrorToCode(error!), AppErrorCode.contactLocked);
      expect(
        await repository.loadThread('crop:crop-1:buyer-1:farmer-1'),
        isEmpty,
      );
    });

    // ACC-03: a deleted account's read markers leave the browser; other
    // accounts' markers and unrelated keys stay.
    test('forgetReadMarkers drops only the deleted user\'s markers', () async {
      SharedPreferences.setMockInitialValues({
        'msg_read_order:o1:buyer-1:farmer-1': '2026-10-01T00:00:00.000Z',
        'msg_read_crop:c1:buyer-1:trucker-1': '2026-10-01T00:00:00.000Z',
        'msg_read_order:o2:buyer-2:farmer-2': '2026-10-01T00:00:00.000Z',
        'language_code': 'am',
      });

      await WebMessageRepository.forgetReadMarkers('buyer-1');

      final prefs = await SharedPreferences.getInstance();
      expect(
        prefs.getKeys(),
        {'msg_read_order:o2:buyer-2:farmer-2', 'language_code'},
      );
    });
  });
}
