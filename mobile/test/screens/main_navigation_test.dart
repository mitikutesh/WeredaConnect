import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_state.dart';
import 'package:wereda_connect/screens/main_navigation.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockConflictBloc extends Mock implements ConflictBloc {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

const _tokens = AuthTokens(
  accessToken: 'a',
  refreshToken: 'r',
  idToken: 'i',
);

/// LT-01: the labeled create FAB floats above the bottom bar instead of
/// docking into a gap it was far wider than, so it never covers a tab.
void main() {
  late MockAuthBloc authBloc;
  late MockConflictBloc conflictBloc;
  late MockConnectivityMonitor monitor;

  setUp(() {
    authBloc = MockAuthBloc();
    conflictBloc = MockConflictBloc();
    monitor = MockConnectivityMonitor();
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => conflictBloc.state).thenReturn(const ConflictInitial());
    when(() => conflictBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => monitor.isConnected()).thenAnswer((_) async => true);
    when(() => monitor.onConnectivityChanged)
        .thenAnswer((_) => const Stream.empty());
  });

  Widget buildApp(UserRole role, {Locale locale = const Locale('en')}) {
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(userId: 'u-1', tokens: _tokens, role: role),
    );
    return Provider<ConnectivityMonitor>.value(
      value: monitor,
      child: MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<ConflictBloc>.value(value: conflictBloc),
        ],
        child: MaterialApp(
          theme: AppTheme.light(),
          locale: locale,
          // The app's delegates: Material has no om, so it falls back.
          localizationsDelegates: const [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            GlobalWidgetsLocalizations.delegate,
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          home: MainNavigation(
            tabBodyBuilder: (tab) => Center(child: Text('body ${tab.name}')),
          ),
        ),
      ),
    );
  }

  void useSize(WidgetTester tester, Size size) {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
  }

  Finder tabItem(String label) =>
      find.ancestor(of: find.text(label), matching: find.byType(InkWell)).first;

  const fabLabels = {
    UserRole.farmer: 'Sell Crops',
    UserRole.trucker: 'Post Trip',
    UserRole.buyer: 'New Buying Intent',
  };

  const tabLabels = {
    RoleTab.home: 'Home',
    RoleTab.myCrops: 'My Crops',
    RoleTab.myTrips: 'My Trips',
    RoleTab.market: 'Market',
    RoleTab.orders: 'Orders',
    RoleTab.deliveries: 'Deliveries',
    RoleTab.payments: 'Payments',
  };

  for (final size in const [Size(360, 640), Size(390, 844)]) {
    final width = size.width.toInt();
    for (final role in UserRole.values) {
      testWidgets('$width dp ${role.name}: the FAB covers no tab',
          (tester) async {
        useSize(tester, size);
        await tester.pumpWidget(buildApp(role));
        await tester.pumpAndSettle();

        expect(find.text(fabLabels[role]!), findsOneWidget);
        final fab = tester.getRect(find.byType(FloatingActionButton));
        final bar = tester.getRect(find.byType(BottomAppBar));
        expect(fab.bottom, lessThanOrEqualTo(bar.top));
        expect(
          bar.top - fab.top,
          lessThanOrEqualTo(Breakpoints.fabClearance),
          reason: 'tab bodies pad by fabClearance; it must cover the FAB',
        );

        final tabs = tabsForRole(role);
        final rects = [
          for (final tab in tabs) tester.getRect(tabItem(tabLabels[tab]!)),
        ];
        for (var i = 0; i < tabs.length; i++) {
          expect(
            rects[i].overlaps(fab),
            isFalse,
            reason: '${tabs[i].name} hit area overlaps the FAB',
          );
          expect(rects[i].width, moreOrLessEquals(rects.first.width));
        }

        // Every tab still takes its own tap: none lands on the FAB.
        for (final tab in tabs.reversed) {
          await tester.tap(find.text(tabLabels[tab]!));
          await tester.pumpAndSettle();
          expect(find.text('body ${tab.name}'), findsOneWidget);
        }
      });
    }
  }

  for (final locale in const [Locale('am'), Locale('om')]) {
    for (final role in UserRole.values) {
      testWidgets(
          '360 dp ${role.name} in ${locale.languageCode}: '
          'labels fit their tab', (tester) async {
        useSize(tester, const Size(360, 640));
        await tester.pumpWidget(buildApp(role, locale: locale));
        await tester.pumpAndSettle();

        final context = tester.element(find.byType(MainNavigation));
        final l10n = AppLocalizations.of(context)!;
        for (final tab in tabsForRole(role)) {
          final label = tab.label(l10n);
          final slot = tester.getRect(tabItem(label));
          final text = tester.getRect(find.text(label));
          expect(slot.left, lessThanOrEqualTo(text.left));
          expect(text.right, lessThanOrEqualTo(slot.right));
        }
        expect(tester.takeException(), isNull);
      });
    }
  }
}
