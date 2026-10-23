import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/messaging/domain/conversation_context_resolver.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/conversation_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockInboxBloc extends Mock implements InboxBloc {}

class MockMessageRepository extends Mock implements BaseMessageRepository {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

class FakeInboxEvent extends Fake implements InboxEvent {}

class FakeInboxState extends Fake implements InboxState {}

const _tokens = AuthTokens(
  accessToken: 'a',
  refreshToken: 'r',
  idToken: 'i',
);

/// Serves one order for every order and delivery thread.
class _FakeResolver extends ConversationContextResolver {
  Order? order;

  @override
  Future<ConversationContextInfo?> lookup(
    String contextType,
    String contextId,
  ) async =>
      null;

  @override
  Future<Order?> lookupContactOrder(
    String contextType,
    String contextId,
  ) async =>
      order;
}

Order _order(OrderStatus status) => Order()
  ..orderId = 'order-1'
  ..listingId = 'crop-1'
  ..buyerId = 'buyer-1'
  ..farmerId = 'farmer-1'
  ..buyerName = 'Buyer'
  ..farmerName = 'Farmer'
  ..cropType = 'teff'
  ..quantity = 100
  ..pricePerUnit = 50
  ..totalPrice = 5000
  ..status = status
  ..createdAt = DateTime.utc(2026, 10)
  ..version = 1
  ..contactOpen = status == OrderStatus.inTransit
  ..farmerPhone = '+251911000002';

/// ESC-48: a thread is open only while its order's contact is open.
void main() {
  late MockAuthBloc authBloc;
  late MockInboxBloc inboxBloc;
  late MockMessageRepository repository;
  late _FakeResolver resolver;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
    registerFallbackValue(FakeInboxEvent());
    registerFallbackValue(FakeInboxState());
  });

  setUp(() {
    authBloc = MockAuthBloc();
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'buyer-1',
        tokens: _tokens,
        role: UserRole.buyer,
        userName: 'Buyer',
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());

    repository = MockMessageRepository();
    when(() => repository.watchChanges())
        .thenAnswer((_) => const Stream.empty());
    when(() => repository.loadThread(any())).thenAnswer((_) async => []);
    when(() => repository.markConversationRead(any())).thenAnswer((_) async {});

    resolver = _FakeResolver();
    inboxBloc = MockInboxBloc();
    when(() => inboxBloc.repository).thenReturn(repository);
    when(() => inboxBloc.contextResolver).thenReturn(resolver);
    when(() => inboxBloc.state).thenReturn(const InboxInitial());
    when(() => inboxBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  Widget buildScreen(
    String contextType,
    String contextId, {
    String counterpartName = 'Farmer',
    Locale locale = const Locale('en'),
  }) =>
      MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<InboxBloc>.value(value: inboxBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: locale,
          home: ConversationScreen(
            contextType: contextType,
            contextId: contextId,
            counterpartId: 'farmer-1',
            counterpartName: counterpartName,
          ),
        ),
      );

  const note = 'Contact opens after payment is held in escrow.';

  testWidgets('a crop thread is read-only: the note, no composer, no Call',
      (tester) async {
    await tester.pumpWidget(buildScreen('crop', 'crop-1'));
    await tester.pumpAndSettle();

    expect(find.text(note), findsOneWidget);
    expect(find.byType(TextField), findsNothing);
    expect(find.byIcon(Icons.call_outlined), findsNothing);
  });

  testWidgets('an open order thread has the composer and Call with the number',
      (tester) async {
    resolver.order = _order(OrderStatus.inTransit);

    await tester.pumpWidget(buildScreen('order', 'order-1'));
    await tester.pumpAndSettle();

    expect(find.text(note), findsNothing);
    expect(find.byType(TextField), findsOneWidget);
    await tester.tap(find.byIcon(Icons.call_outlined));
    await tester.pumpAndSettle();
    expect(find.text('+251911000002'), findsOneWidget);
  });

  const acceptNote = 'Contact opens once the farmer accepts the order.';
  const closedNote = 'Contact is closed for this conversation.';

  // The note says when contact opens, or that it will not open again.
  for (final (status, expected) in [
    (OrderStatus.created, note),
    (OrderStatus.paymentFailed, note),
    (OrderStatus.funded, acceptNote),
    (OrderStatus.cancelled, closedNote),
    (OrderStatus.rejected, closedNote),
    (OrderStatus.disputeResolved, closedNote),
  ]) {
    testWidgets('a ${status.name} order thread is read-only with its note',
        (tester) async {
      resolver.order = _order(status);

      await tester.pumpWidget(buildScreen('order', 'order-1'));
      await tester.pumpAndSettle();

      expect(find.text(expected), findsOneWidget);
      for (final other in {note, acceptNote, closedNote}..remove(expected)) {
        expect(find.text(other), findsNothing, reason: other);
      }
      expect(find.byType(TextField), findsNothing);
      expect(find.byIcon(Icons.call_outlined), findsNothing);
    });
  }

  testWidgets('an open status refunded by an admin override says closed',
      (tester) async {
    resolver.order = _order(OrderStatus.delivered)..contactOpen = false;

    await tester.pumpWidget(buildScreen('order', 'order-1'));
    await tester.pumpAndSettle();

    expect(find.text(closedNote), findsOneWidget);
    expect(find.byType(TextField), findsNothing);
  });

  testWidgets('an order not on the device keeps the composer, no Call',
      (tester) async {
    await tester.pumpWidget(buildScreen('order', 'order-1'));
    await tester.pumpAndSettle();

    expect(find.text(note), findsNothing);
    expect(find.byType(TextField), findsOneWidget);
    expect(find.byIcon(Icons.call_outlined), findsNothing);
  });

  // ACC-03: a deleted party's name is the localized "Deleted user".
  testWidgets('am: a deleted counterpart reads in Amharic', (tester) async {
    await tester.pumpWidget(
      buildScreen(
        'order',
        'order-1',
        counterpartName: deletedUserSentinel,
        locale: const Locale('am'),
      ),
    );
    await tester.pumpAndSettle();

    final am = lookupAppLocalizations(const Locale('am'));
    expect(find.text(am.deletedUser), findsOneWidget);
    expect(find.text(deletedUserSentinel), findsNothing);
  });
}
