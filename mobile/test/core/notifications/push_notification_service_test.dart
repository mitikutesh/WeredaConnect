import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/notifications/push_notification_service.dart';

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
}
