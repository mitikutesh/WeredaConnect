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
}
