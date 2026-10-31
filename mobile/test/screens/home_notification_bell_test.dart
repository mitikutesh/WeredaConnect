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
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/notifications/domain/base_notification_repository.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockCropListingBloc extends Mock implements CropListingBloc {}

class MockInboxBloc extends Mock implements InboxBloc {}

class MockSyncExecutor extends Mock implements SyncExecutor {}

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

/// ESC-21: the bell lists the server inbox on every platform.
void main() {
  late MockAuthBloc authBloc;
  late MockCropListingBloc cropListingBloc;
  late MockInboxBloc inboxBloc;
  late MockSyncExecutor syncExecutor;

  setUp(() {
    authBloc = MockAuthBloc();
    cropListingBloc = MockCropListingBloc();
    inboxBloc = MockInboxBloc();
    syncExecutor = MockSyncExecutor();
    when(() => inboxBloc.state).thenReturn(const InboxInitial());
    when(() => inboxBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'farmer-001',
        tokens: _tokens,
        role: UserRole.farmer,
      ),
    );
    when(() => cropListingBloc.state)
        .thenReturn(const CropListingsLoaded(listings: []));
    when(() => cropListingBloc.stream).thenAnswer(
      (_) => Stream.value(const CropListingsLoaded(listings: [])),
    );
    when(() => syncExecutor.onSyncResult)
        .thenAnswer((_) => const Stream.empty());
    when(() => syncExecutor.isSyncing).thenReturn(false);
  });

  testWidgets('pulls on open, shows the entry, marks it seen', (tester) async {
    tester.view.physicalSize = const Size(800, 2000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    final inbox = _FakeInbox([
      AppNotification()
        ..notificationId = 'n1'
        ..userId = 'farmer-001'
        ..type = 'order_request'
        ..entity = 'order'
        ..entityId = 'ord-1'
        ..orderId = 'ord-1'
        ..params = jsonEncode({'buyerName': 'Abebe', 'cropType': 'teff'})
        ..createdAt = DateTime.now().toUtc(),
    ]);
    addTearDown(inbox.close);

    await tester.pumpWidget(
      MultiProvider(
        providers: [
          Provider<SyncExecutor>.value(value: syncExecutor),
          Provider<BaseNotificationRepository>.value(value: inbox),
        ],
        child: MultiBlocProvider(
          providers: [
            BlocProvider<AuthBloc>.value(value: authBloc),
            BlocProvider<CropListingBloc>.value(value: cropListingBloc),
            BlocProvider<InboxBloc>.value(value: inboxBloc),
          ],
          child: const MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: Scaffold(body: HomeScreen(isOnline: false)),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();

    expect(inbox.pulls, ['farmer-001']);
    // Farmer · Home board: the bell is the header's only icon; Messages
    // and Profile open from its sheet.
    expect(find.byIcon(Icons.chat_bubble_outline), findsNothing);
    expect(find.byIcon(Icons.person_outline), findsNothing);
    await tester.tap(find.byIcon(Icons.notifications_outlined));
    await tester.pumpAndSettle();

    final en = lookupAppLocalizations(const Locale('en'));
    expect(find.text(en.notifOrderRequestTitle), findsOneWidget);
    expect(find.textContaining('Abebe paid'), findsOneWidget);
    expect(inbox.markedRead, 1);
    expect(find.byKey(const Key('notif_open_messages')), findsOneWidget);
    expect(find.byKey(const Key('notif_open_profile')), findsOneWidget);
  });
}
