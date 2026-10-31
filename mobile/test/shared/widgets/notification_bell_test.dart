import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/app_notification.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/notifications/domain/base_notification_repository.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/notification_bell.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

/// An inbox with fixed entries that records pulls and reads.
class _FakeInbox extends BaseNotificationRepository {
  _FakeInbox(this.entries);

  final List<AppNotification> entries;
  final pulls = <String>[];
  var markedRead = 0;
  final _changes = StreamController<void>.broadcast();

  @override
  Stream<void> get changes => _changes.stream;

  @override
  Future<void> pull(String userId, {bool force = false}) async =>
      pulls.add(userId);

  @override
  Future<List<AppNotification>> recent(String userId, {int limit = 50}) async =>
      entries.where((e) => e.userId == userId).toList();

  @override
  Future<int> unreadCount(String userId) async =>
      entries.where((e) => e.userId == userId && !e.read).length;

  Future<void> close() => _changes.close();

  @override
  Future<void> markAllRead(String userId) async {
    markedRead++;
    for (final e in entries) {
      e.read = true;
    }
  }
}

const _tokens = AuthTokens(
  accessToken: 'access',
  refreshToken: 'refresh',
  idToken: 'id',
);

/// The tab headers' bell: the signed-in user's inbox in a white square.
void main() {
  late MockAuthBloc authBloc;

  setUp(() {
    authBloc = MockAuthBloc();
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'buyer-001',
        tokens: _tokens,
        role: UserRole.buyer,
      ),
    );
  });

  Future<_FakeInbox> pumpBell(WidgetTester tester) async {
    final inbox = _FakeInbox([
      AppNotification()
        ..notificationId = 'n1'
        ..userId = 'buyer-001'
        ..type = 'order_request'
        ..entity = 'order'
        ..entityId = 'ord-1'
        ..orderId = 'ord-1'
        ..params = jsonEncode({'buyerName': 'Abebe', 'cropType': 'teff'})
        ..createdAt = DateTime.now().toUtc(),
    ]);
    addTearDown(inbox.close);
    await tester.pumpWidget(
      Provider<BaseNotificationRepository>.value(
        value: inbox,
        child: BlocProvider<AuthBloc>.value(
          value: authBloc,
          child: const MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: Scaffold(body: Center(child: HeaderBell())),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    return inbox;
  }

  testWidgets('reads the signed-in user\'s inbox in a square button',
      (tester) async {
    final inbox = await pumpBell(tester);

    expect(inbox.pulls, ['buyer-001']);
    expect(
      find.ancestor(
        of: find.byIcon(Icons.notifications_outlined),
        matching: find.byType(FwSquareButton),
      ),
      findsOneWidget,
    );
  });

  testWidgets('opens the inbox and marks it seen', (tester) async {
    final inbox = await pumpBell(tester);

    await tester.tap(find.byIcon(Icons.notifications_outlined));
    await tester.pumpAndSettle();

    final en = lookupAppLocalizations(const Locale('en'));
    expect(find.text(en.notifOrderRequestTitle), findsOneWidget);
    expect(inbox.markedRead, 1);
  });

  // The boards' headers draw no Messages or Profile icon and the phone bar
  // has no tab for them, so the bell's sheet opens both.
  testWidgets('the sheet opens Messages and Profile', (tester) async {
    await pumpBell(tester);

    await tester.tap(find.byIcon(Icons.notifications_outlined));
    await tester.pumpAndSettle();

    final en = lookupAppLocalizations(const Locale('en'));
    expect(
      find.descendant(
        of: find.byKey(const Key('notif_open_messages')),
        matching: find.text(en.messages),
      ),
      findsOneWidget,
    );
    expect(
      find.descendant(
        of: find.byKey(const Key('notif_open_profile')),
        matching: find.text(en.profile),
      ),
      findsOneWidget,
    );
  });
}
