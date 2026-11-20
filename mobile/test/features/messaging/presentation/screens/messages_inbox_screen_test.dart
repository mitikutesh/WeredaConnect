import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/messaging/domain/conversation_context_resolver.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/messages_inbox_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockInboxBloc extends Mock implements InboxBloc {}

class FakeInboxEvent extends Fake implements InboxEvent {}

class _NoContext extends ConversationContextResolver {
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
      null;
}

const _counterpartId = '3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b';

ConversationSummary _summary(String id, String? name) => ConversationSummary(
      conversationId: id,
      contextType: 'order',
      contextId: 'order-1',
      counterpartId: _counterpartId,
      counterpartName: name,
      lastMessageBody: 'Selam',
      lastMessageAt: DateTime.utc(2026, 11, 1, 9),
      lastMessageFromMe: false,
      unreadCount: 0,
    );

/// FUP-130: an inbox row never shows the counterpart's id as their name.
void main() {
  late MockAuthBloc authBloc;
  late MockInboxBloc inboxBloc;

  setUpAll(() => registerFallbackValue(FakeInboxEvent()));

  setUp(() {
    authBloc = MockAuthBloc();
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'buyer-1',
        tokens: AuthTokens(accessToken: 'a', refreshToken: 'r', idToken: 'i'),
        role: UserRole.buyer,
        userName: 'Buyer',
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());

    inboxBloc = MockInboxBloc();
    when(() => inboxBloc.contextResolver).thenReturn(_NoContext());
    when(() => inboxBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  Widget buildScreen(List<ConversationSummary> rows, Locale locale) {
    when(() => inboxBloc.state)
        .thenReturn(InboxLoaded(conversations: rows, totalUnread: 0));
    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<InboxBloc>.value(value: inboxBloc),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: locale,
        home: const MessagesInboxScreen(),
      ),
    );
  }

  // om is covered in party_name_test.dart (this test app has no Material
  // strings for om).
  for (final code in ['en', 'am']) {
    testWidgets('$code: a row without a real name reads "User", never the id',
        (tester) async {
      final l10n = lookupAppLocalizations(Locale(code));

      await tester.pumpWidget(
        buildScreen(
          [
            _summary('c1', null),
            _summary('c2', '  '),
            _summary('c3', _counterpartId),
          ],
          Locale(code),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text(l10n.unnamedUser), findsNWidgets(3));
      expect(find.text('?'), findsNWidgets(3));
      expect(find.textContaining(_counterpartId), findsNothing);
    });
  }

  testWidgets('a real name is shown with its initials', (tester) async {
    await tester.pumpWidget(
      buildScreen([_summary('c1', ' Abebe Kebede ')], const Locale('en')),
    );
    await tester.pumpAndSettle();

    expect(find.text('Abebe Kebede'), findsOneWidget);
    expect(find.text('AK'), findsOneWidget);
    expect(find.text('User'), findsNothing);
  });

  testWidgets(
      'the threads are rows of one raised card; unread is the orange pill',
      (tester) async {
    await tester.pumpWidget(
      buildScreen(
        [
          ConversationSummary(
            conversationId: 'c1',
            contextType: 'order',
            contextId: 'order-1',
            counterpartId: _counterpartId,
            counterpartName: 'Abebe Kebede',
            lastMessageBody: 'Selam',
            lastMessageAt: DateTime.utc(2026, 11, 1, 9),
            lastMessageFromMe: false,
            unreadCount: 3,
          ),
          _summary('c2', 'Dawit Tesfaye'),
        ],
        const Locale('en'),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.byType(AppBar), findsNothing);
    expect(find.byType(FwPageHeader), findsOneWidget);
    final card = tester.widget<FwCard>(find.byType(FwCard));
    expect(card.raised, isTrue);

    final pill = tester.widget<FwPill>(
      find.byKey(const Key('inbox_unread_pill')),
    );
    expect(pill.label, '3');
    expect(pill.background, AppTheme.orange);
    expect(pill.foreground, AppTheme.ink);
  });

  testWidgets('no conversations: the role drawing, the title and the hint',
      (tester) async {
    await tester.pumpWidget(buildScreen(const [], const Locale('en')));
    await tester.pumpAndSettle();

    final l10n = lookupAppLocalizations(const Locale('en'));
    expect(find.byType(RoleSketch), findsWidgets);
    expect(find.text(l10n.noConversationsYet), findsOneWidget);
    expect(find.text(l10n.noConversationsHint), findsOneWidget);
    expect(find.byType(FwCard), findsNothing);
  });
}
