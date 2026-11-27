import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/message.dart';
import 'package:wereda_connect/features/messaging/domain/notified_conversation.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';
import 'package:wereda_connect/features/messaging/presentation/open_notified_conversation.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/conversation_screen.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/messages_inbox_screen.dart';
import 'package:wereda_connect/features/messaging/presentation/widgets/message_arrival_listener.dart';

class MockMessageRepository extends Mock implements BaseMessageRepository {}

const _buyer = 'buyer-1';
const _farmer = 'farmer-1';
const _conversationId = 'order:ord-7f3a:buyer-1:farmer-1';

/// The buyer wrote to the farmer on order `ord-7f3a`.
const _tapped = NotifiedConversation(
  conversationId: _conversationId,
  contextType: 'order',
  contextId: 'ord-7f3a',
  senderId: _buyer,
);

Message _message({
  required String id,
  required String from,
  required String to,
  required int minute,
  String? senderName,
  String? recipientName,
}) =>
    Message()
      ..messageId = id
      ..conversationId = _conversationId
      ..contextType = 'order'
      ..contextId = 'ord-7f3a'
      ..senderId = from
      ..recipientId = to
      ..senderName = senderName
      ..recipientName = recipientName
      ..body = 'hello'
      ..createdAt = DateTime.utc(2026, 11, 6, 9, minute);

void main() {
  late MockMessageRepository repository;

  setUp(() {
    repository = MockMessageRepository();
    ActiveConversation.id = null;
  });

  tearDown(() => ActiveConversation.id = null);

  test('another account on the phone opens nothing and reads nothing',
      () async {
    for (final userId in ['trucker-1', _buyer, '']) {
      final screen = await notifiedConversationScreen(
        repository: repository,
        userId: userId,
        conversation: _tapped,
      );
      expect(screen, isNull, reason: userId);
    }
    verifyZeroInteractions(repository);
  });

  test('a thread on the device opens at once and asks the inbox to pull',
      () async {
    when(() => repository.loadThread(_conversationId)).thenAnswer(
      (_) async => [
        _message(
          id: 'm-0',
          from: _buyer,
          to: _farmer,
          senderName: 'Abebe',
          minute: 1,
        ),
        // The farmer's own reply, stored before the server named anyone.
        _message(id: 'm-1', from: _farmer, to: _buyer, minute: 2),
      ],
    );
    var pulls = 0;

    final screen = await notifiedConversationScreen(
      repository: repository,
      userId: _farmer,
      conversation: _tapped,
      requestPull: () => pulls++,
    );

    expect(screen, isA<ConversationScreen>());
    final thread = screen! as ConversationScreen;
    expect(thread.contextType, 'order');
    expect(thread.contextId, 'ord-7f3a');
    expect(thread.counterpartId, _buyer);
    expect(thread.counterpartName, 'Abebe');
    expect(pulls, 1);
    verifyNever(() => repository.pullMessages(any()));
  });

  test('a first message is pulled before its thread opens, without a banner',
      () async {
    final stored = <Message>[];
    String? onScreenDuringPull;
    when(() => repository.loadThread(_conversationId))
        .thenAnswer((_) async => List.of(stored));
    when(() => repository.pullMessages(_farmer)).thenAnswer((_) async {
      onScreenDuringPull = ActiveConversation.id;
      stored.add(
        _message(
          id: 'm-1',
          from: _buyer,
          to: _farmer,
          senderName: 'Abebe',
          recipientName: 'Chaltu',
          minute: 1,
        ),
      );
      return 1;
    });
    ActiveConversation.id = 'crop:c1:buyer-9:farmer-1';

    final screen = await notifiedConversationScreen(
      repository: repository,
      userId: _farmer,
      conversation: _tapped,
    );

    final thread = screen! as ConversationScreen;
    expect(thread.counterpartId, _buyer);
    expect(thread.counterpartName, 'Abebe');
    verify(() => repository.pullMessages(_farmer)).called(1);
    // The arrival the pull finds is not announced over the opening thread,
    // and the thread that was on screen counts again afterwards.
    expect(onScreenDuringPull, _conversationId);
    expect(ActiveConversation.id, 'crop:c1:buyer-9:farmer-1');
  });

  test('offline with nothing of the thread opens the messages list', () async {
    when(() => repository.loadThread(_conversationId))
        .thenAnswer((_) async => []);
    // The repository swallows offline and stores nothing.
    when(() => repository.pullMessages(_farmer)).thenAnswer((_) async => 0);

    final screen = await notifiedConversationScreen(
      repository: repository,
      userId: _farmer,
      conversation: _tapped,
    );

    expect(screen, isA<MessagesInboxScreen>());
    expect(ActiveConversation.id, isNull);
  });

  test('a pull that hangs on a slow network stops at the bound', () async {
    when(() => repository.loadThread(_conversationId))
        .thenAnswer((_) async => []);
    final hung = Completer<int>();
    when(() => repository.pullMessages(_farmer)).thenAnswer((_) => hung.future);

    final screen = await notifiedConversationScreen(
      repository: repository,
      userId: _farmer,
      conversation: _tapped,
      pullTimeout: const Duration(milliseconds: 1),
    );

    expect(screen, isA<MessagesInboxScreen>());
    expect(ActiveConversation.id, isNull);
  });
}
