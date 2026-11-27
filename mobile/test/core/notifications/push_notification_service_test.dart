import 'dart:async';

import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/notifications/push_notification_service.dart';
import 'package:wereda_connect/features/messaging/domain/notified_conversation.dart';

/// A chat push as MessageStreamFunction sends it: `buyer-1` wrote to
/// `farmer-1` on order `ord-7f3a`.
const _chatPush = <String, dynamic>{
  'type': 'message',
  'conversationId': 'order:ord-7f3a:buyer-1:farmer-1',
  'messageId': 'm-1',
  'senderId': 'buyer-1',
  'contextType': 'order',
  'contextId': 'ord-7f3a',
};

const _orderPush = <String, dynamic>{
  'type': 'order',
  'event': 'transport_request',
  'orderId': 'ord-7f3a',
};

void main() {
  group('PushNotificationService.isMessagePush', () {
    test('accepts a chat-message payload (MessageStreamFunction contract)', () {
      expect(
        PushNotificationService.isMessagePush({
          'type': 'message',
          'conversationId': 'crop:c1:a:b',
          'messageId': 'm1',
          'senderId': 'a',
          'contextType': 'crop',
          'contextId': 'c1',
        }),
        isTrue,
      );
    });

    test('rejects a buyer-match payload (shares the same FCM pipe)', () {
      expect(
        PushNotificationService.isMessagePush({
          'intentId': 'i1',
          'cropId': 'c1',
        }),
        isFalse,
      );
    });

    test('rejects missing or non-string type values', () {
      expect(PushNotificationService.isMessagePush({}), isFalse);
      expect(PushNotificationService.isMessagePush({'type': 42}), isFalse);
      expect(
        PushNotificationService.isMessagePush({'type': 'crop_match'}),
        isFalse,
      );
    });
  });

  // ACC-03: the deletion wipe calls this whether or not push is set up.
  test('deleteToken completes quietly when Firebase is not set up', () async {
    TestWidgetsFlutterBinding.ensureInitialized();

    await expectLater(PushNotificationService.deleteToken(), completes);
  });

  group('ESC-21 order pushes', () {
    const orderPush = {
      'type': 'order',
      'event': 'transport_request',
      'orderId': 'ord-7f3a',
      'entityId': 'ord-7f3a',
      'entity': 'order',
      'notificationId': '2026-10-26T08:15:02Z#transport_request#ord-7f3a',
    };

    test('an order event is an order push, not a chat push', () {
      expect(PushNotificationService.isOrderPush(orderPush), isTrue);
      expect(PushNotificationService.isMessagePush(orderPush), isFalse);
      expect(
        PushNotificationService.isOrderPush({'type': 'message'}),
        isFalse,
      );
    });

    test('a tap opens the order it names', () {
      expect(PushNotificationService.orderIdToOpen(orderPush), 'ord-7f3a');
    });

    test('nothing to open without an order, or for a chat push', () {
      expect(
        PushNotificationService.orderIdToOpen(
          {...orderPush}..remove('orderId'),
        ),
        isNull,
      );
      expect(
        PushNotificationService.orderIdToOpen({...orderPush, 'orderId': ''}),
        isNull,
      );
      expect(
        PushNotificationService.orderIdToOpen(
          {'type': 'message', 'orderId': 'ord-1'},
        ),
        isNull,
      );
      // Not a plain id: never placed in a path.
      for (final id in ['../escrow/history', 'a/b', 'ord 1', 'x' * 65]) {
        expect(
          PushNotificationService.orderIdToOpen({...orderPush, 'orderId': id}),
          isNull,
          reason: id,
        );
      }
    });
  });

  group('FUP-90 chat push payload', () {
    test('a chat push names the conversation to open', () {
      final conversation = PushNotificationService.conversationToOpen(
        _chatPush,
      );

      expect(conversation, isNotNull);
      expect(conversation!.conversationId, 'order:ord-7f3a:buyer-1:farmer-1');
      expect(conversation.contextType, 'order');
      expect(conversation.contextId, 'ord-7f3a');
      expect(conversation.senderId, 'buyer-1');
    });

    test('nothing to open without the thread ids', () {
      for (final key in [
        'conversationId',
        'contextType',
        'contextId',
        'senderId',
      ]) {
        expect(
          PushNotificationService.conversationToOpen(
            {..._chatPush}..remove(key),
          ),
          isNull,
          reason: key,
        );
      }
      expect(
        PushNotificationService.conversationToOpen(
          {..._chatPush, 'conversationId': ''},
        ),
        isNull,
      );
    });

    test('an order push or a match push opens no conversation', () {
      expect(PushNotificationService.conversationToOpen(_orderPush), isNull);
      expect(
        PushNotificationService.conversationToOpen(
          {'intentId': 'i1', 'cropId': 'c1'},
        ),
        isNull,
      );
      // The thread ids on a payload that is not a chat push.
      expect(
        PushNotificationService.conversationToOpen(
          {..._chatPush, 'type': 'order'},
        ),
        isNull,
      );
    });

    test('junk opens nothing', () {
      expect(PushNotificationService.conversationToOpen({}), isNull);
      expect(
        PushNotificationService.conversationToOpen(
          {..._chatPush, 'contextType': 'listing'},
        ),
        isNull,
      );
      expect(
        PushNotificationService.conversationToOpen(
          {..._chatPush, 'conversationId': 42},
        ),
        isNull,
      );
      for (final id in ['../users/me', 'a/b', 'buyer 1', 'x' * 65]) {
        expect(
          PushNotificationService.conversationToOpen(
            {..._chatPush, 'contextId': id},
          ),
          isNull,
          reason: 'contextId $id',
        );
        expect(
          PushNotificationService.conversationToOpen(
            {..._chatPush, 'senderId': id},
          ),
          isNull,
          reason: 'senderId $id',
        );
      }
    });

    test('only the recipient may open it', () {
      final conversation =
          PushNotificationService.conversationToOpen(_chatPush)!;

      expect(conversation.isFor('farmer-1'), isTrue);
      // The sender, another account on a shared phone (FUP-121), nobody.
      expect(conversation.isFor('buyer-1'), isFalse);
      expect(conversation.isFor('trucker-1'), isFalse);
      expect(conversation.isFor(''), isFalse);
    });

    test('a thread id that does not match its context opens for nobody', () {
      final forged = PushNotificationService.conversationToOpen({
        ..._chatPush,
        'conversationId': 'order:ord-other:buyer-1:farmer-1',
      })!;
      final otherPair = PushNotificationService.conversationToOpen({
        ..._chatPush,
        'conversationId': 'order:ord-7f3a:buyer-1:trucker-1',
      })!;

      expect(forged.isFor('farmer-1'), isFalse);
      expect(otherPair.isFor('farmer-1'), isFalse);
      expect(otherPair.isFor('trucker-1'), isTrue);
    });
  });

  group('FUP-90 taps', () {
    // FirebaseMessaging.onMessageOpenedApp is a broadcast stream.
    late StreamController<RemoteMessage> taps;

    setUp(() async {
      await PushNotificationService.resetTapsForTest();
      taps = StreamController<RemoteMessage>.broadcast();
    });

    tearDown(() async {
      await PushNotificationService.resetTapsForTest();
      await taps.close();
    });

    test('a tap from the background opens its conversation once', () async {
      final conversations = <NotifiedConversation>[];
      final orders = <String>[];
      await PushNotificationService.listenForTaps(
        opened: taps.stream,
        initialMessage: () async => null,
        onOpenOrder: orders.add,
        onOpenConversation: conversations.add,
      );

      taps.add(const RemoteMessage(data: _chatPush));
      await pumpEventQueue();

      expect(conversations, hasLength(1));
      expect(
        conversations.single.conversationId,
        'order:ord-7f3a:buyer-1:farmer-1',
      );
      expect(orders, isEmpty);
    });

    test('the tap that cold-started the app opens its conversation once',
        () async {
      var reads = 0;
      Future<RemoteMessage?> initialMessage() async {
        reads++;
        return const RemoteMessage(data: _chatPush);
      }

      final conversations = <NotifiedConversation>[];
      await PushNotificationService.listenForTaps(
        opened: taps.stream,
        initialMessage: initialMessage,
        onOpenConversation: conversations.add,
      );
      expect(conversations, hasLength(1));
      expect(conversations.single.senderId, 'buyer-1');

      // A later sign-in in the same launch does not open it again.
      await PushNotificationService.listenForTaps(
        opened: taps.stream,
        initialMessage: initialMessage,
        onOpenConversation: conversations.add,
      );
      expect(conversations, hasLength(1));
      expect(reads, 1);
    });

    test('a later sign-in replaces the listener: one open per tap', () async {
      final first = <NotifiedConversation>[];
      final second = <NotifiedConversation>[];
      await PushNotificationService.listenForTaps(
        opened: taps.stream,
        initialMessage: () async => null,
        onOpenConversation: first.add,
      );
      await PushNotificationService.listenForTaps(
        opened: taps.stream,
        initialMessage: () async => null,
        onOpenConversation: second.add,
      );

      taps.add(const RemoteMessage(data: _chatPush));
      await pumpEventQueue();

      expect(first, isEmpty);
      expect(second, hasLength(1));
    });

    test('an order tap still opens its order, a match tap opens nothing',
        () async {
      final conversations = <NotifiedConversation>[];
      final orders = <String>[];
      await PushNotificationService.listenForTaps(
        opened: taps.stream,
        initialMessage: () async => const RemoteMessage(data: _orderPush),
        onOpenOrder: orders.add,
        onOpenConversation: conversations.add,
      );
      expect(orders, ['ord-7f3a']);

      taps
        ..add(const RemoteMessage(data: {'intentId': 'i1', 'cropId': 'c1'}))
        ..add(const RemoteMessage(data: _orderPush));
      await pumpEventQueue();

      expect(orders, ['ord-7f3a', 'ord-7f3a']);
      expect(conversations, isEmpty);
    });
  });
}
