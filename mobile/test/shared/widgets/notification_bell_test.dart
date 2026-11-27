import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter/gestures.dart';
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
import 'package:wereda_connect/features/reputation/data/reputation_repository.dart';
import 'package:wereda_connect/features/reputation/presentation/screens/my_reputation_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/notification_bell.dart';

import '../../features/reputation/reputation_fixtures.dart';

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

  testWidgets('something new: the orange dot in a ring of the paper grey',
      (tester) async {
    await pumpBell(tester);

    final dot = tester.widget<Container>(
      find.byKey(const Key('notification_bell_dot')),
    );
    final decoration = dot.decoration! as BoxDecoration;
    expect(decoration.color, AppTheme.orange);
    expect(decoration.shape, BoxShape.circle);
    expect(decoration.border!.top.color, AppTheme.paper);
    final button = tester.widget<FwSquareButton>(find.byType(FwSquareButton));
    expect(button.background, AppTheme.paper);

    // Opening the sheet marks the inbox seen: the dot goes.
    await tester.tap(find.byIcon(Icons.notifications_outlined));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('notification_bell_dot')), findsNothing);
  });

  testWidgets('opens the inbox and marks it seen', (tester) async {
    final inbox = await pumpBell(tester);

    await tester.tap(find.byIcon(Icons.notifications_outlined));
    await tester.pumpAndSettle();

    final en = lookupAppLocalizations(const Locale('en'));
    expect(find.text(en.notifOrderRequestTitle), findsOneWidget);
    expect(inbox.markedRead, 1);
  });

  // Messages and Profile open from the header's account button (or the web
  // sidebar), so the bell's sheet holds notifications only.
  testWidgets('the sheet has no Messages or Profile shortcut', (tester) async {
    await pumpBell(tester);

    await tester.tap(find.byIcon(Icons.notifications_outlined));
    await tester.pumpAndSettle();

    final en = lookupAppLocalizations(const Locale('en'));
    expect(find.text(en.notifications), findsOneWidget);
    expect(find.text(en.messages), findsNothing);
    expect(find.text(en.profile), findsNothing);
  });

  // ESC-31: an entry about the user's own reputation names no order;
  // tapping it opens My reputation for the signed-in user.
  testWidgets('a reputation entry opens My reputation', (tester) async {
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'farmer-001',
        tokens: _tokens,
        role: UserRole.farmer,
      ),
    );
    final reputation = MockReputationRepository();
    when(() => reputation.loadOwn('farmer-001', force: any(named: 'force')))
        .thenAnswer(
      (_) async => snapshot(ownReputation(tier: 'warning', points: 4)),
    );
    when(() => reputation.loadEvents('farmer-001'))
        .thenAnswer((_) async => eventsResult(const []));
    final inbox = _FakeInbox([
      AppNotification()
        ..notificationId = 'n-rep'
        ..userId = 'farmer-001'
        ..type = 'penalty_recorded'
        ..entity = 'user'
        ..entityId = 'farmer-001'
        ..params = jsonEncode({'code': 'LATE_CANCEL', 'points': 4})
        ..createdAt = DateTime.now().toUtc(),
    ]);
    addTearDown(inbox.close);
    await tester.pumpWidget(
      MultiProvider(
        providers: [
          Provider<BaseNotificationRepository>.value(value: inbox),
          Provider<ReputationRepository>.value(value: reputation),
        ],
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

    await tester.tap(find.byIcon(Icons.notifications_outlined));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Penalty points added'));
    await tester.pumpAndSettle();

    expect(find.byType(MyReputationScreen), findsOneWidget);
    expect(
      tester.widget<MyReputationScreen>(find.byType(MyReputationScreen)).userId,
      'farmer-001',
    );
    expect(find.text('4 penalty points'), findsOneWidget);
  });

  /// The bell of the signed-in [userId] over [entries], with My reputation's
  /// repository, its sheet open (FUP-195).
  Future<void> pumpReputationBell(
    WidgetTester tester, {
    required String userId,
    required UserRole role,
    required List<AppNotification> entries,
  }) async {
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(userId: userId, tokens: _tokens, role: role),
    );
    final reputation = MockReputationRepository();
    when(() => reputation.loadOwn(userId, force: any(named: 'force')))
        .thenAnswer(
      (_) async => snapshot(ownReputation()),
    );
    when(() => reputation.loadEvents(userId))
        .thenAnswer((_) async => eventsResult(const []));
    final inbox = _FakeInbox(entries);
    addTearDown(inbox.close);
    await tester.pumpWidget(
      MultiProvider(
        providers: [
          Provider<BaseNotificationRepository>.value(value: inbox),
          Provider<ReputationRepository>.value(value: reputation),
        ],
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
    await tester.tap(find.byIcon(Icons.notifications_outlined));
    await tester.pumpAndSettle();
  }

  // FUP-195: an admin's decision opens My reputation, as tier_changed does.
  testWidgets('a decision on a record opens My reputation', (tester) async {
    await pumpReputationBell(
      tester,
      userId: 'trucker-001',
      role: UserRole.trucker,
      entries: [
        AppNotification()
          ..notificationId = 'n-decided'
          ..userId = 'trucker-001'
          ..type = 'appeal_decided'
          ..entity = 'reputation'
          ..entityId = 'OVERDUE#ord-1'
          ..params = jsonEncode({
            'eventId': 'OVERDUE#ord-1',
            'code': 'DELIVERY_OVERDUE',
            'decision': 'excused',
            'note': 'The road was closed by the flood.',
          })
          ..createdAt = DateTime.now().toUtc(),
      ],
    );

    expect(find.text('Penalty points removed'), findsOneWidget);
    expect(
      find.textContaining('Note from our team: The road was closed'),
      findsOneWidget,
    );
    await tester.tap(find.text('Penalty points removed'));
    await tester.pumpAndSettle();

    expect(find.byType(MyReputationScreen), findsOneWidget);
    expect(
      tester.widget<MyReputationScreen>(find.byType(MyReputationScreen)).userId,
      'trucker-001',
    );
  });

  // How a build older than a type reads it: the generic line, and a tap on
  // an entry without an order only closes the sheet.
  testWidgets('a type this build does not know opens nothing', (tester) async {
    await pumpReputationBell(
      tester,
      userId: 'farmer-001',
      role: UserRole.farmer,
      entries: [
        AppNotification()
          ..notificationId = 'n-new'
          ..userId = 'farmer-001'
          ..type = 'something_new'
          ..entity = 'reputation'
          ..entityId = 'CANCEL#ord-1'
          ..params = jsonEncode({'decision': 'excused'})
          ..createdAt = DateTime.now().toUtc(),
      ],
    );

    final en = lookupAppLocalizations(const Locale('en'));
    expect(find.text(en.notifGenericTitle), findsOneWidget);
    await tester.tap(find.text(en.notifGenericTitle));
    await tester.pumpAndSettle();

    expect(find.byType(MyReputationScreen), findsNothing);
    expect(find.text(en.notifGenericTitle), findsNothing);
    expect(tester.takeException(), isNull);
  });

  double sheetHeight(WidgetTester tester) =>
      tester.getSize(find.byType(DraggableScrollableSheet)).height;

  double windowHeight(WidgetTester tester) =>
      tester.view.physicalSize.height / tester.view.devicePixelRatio;

  testWidgets('a phone opens the sheet low', (tester) async {
    await pumpBell(tester);

    await tester.tap(find.byIcon(Icons.notifications_outlined));
    await tester.pumpAndSettle();

    expect(sheetHeight(tester), closeTo(windowHeight(tester) * 0.4, 1));
  });

  // No mouse can drag a sheet by default, so on a desktop browser it stayed
  // at 40% with the rest of the list below the window.
  testWidgets('a desktop opens the sheet tall', (tester) async {
    debugDefaultTargetPlatformOverride = TargetPlatform.macOS;
    try {
      await pumpBell(tester);

      await tester.tap(find.byIcon(Icons.notifications_outlined));
      await tester.pumpAndSettle();

      expect(sheetHeight(tester), closeTo(windowHeight(tester) * 0.9, 1));
    } finally {
      debugDefaultTargetPlatformOverride = null;
    }
  });

  testWidgets('a mouse drag grows the sheet', (tester) async {
    await pumpBell(tester);
    await tester.tap(find.byIcon(Icons.notifications_outlined));
    await tester.pumpAndSettle();
    final before = sheetHeight(tester);

    final en = lookupAppLocalizations(const Locale('en'));
    await tester.drag(
      find.text(en.notifOrderRequestTitle),
      const Offset(0, -150),
      kind: PointerDeviceKind.mouse,
    );
    await tester.pumpAndSettle();

    expect(sheetHeight(tester), greaterThan(before + 100));
  });
}
