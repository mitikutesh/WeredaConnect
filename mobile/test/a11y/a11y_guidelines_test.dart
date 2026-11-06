import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/screens/login_screen.dart';
import 'package:wereda_connect/features/auth/screens/registration/role_details_screen.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/screens/home_screen.dart';
import 'package:wereda_connect/screens/orders_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/navigation/role_nav_bar.dart';
import 'package:wereda_connect/shared/navigation/role_navigation.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockBuyerIntentBloc extends Mock implements BuyerIntentBloc {}

class MockCropListingBloc extends Mock implements CropListingBloc {}

class MockInboxBloc extends Mock implements InboxBloc {}

class MockOrderBloc extends Mock implements OrderBloc {}

class MockSyncExecutor extends Mock implements SyncExecutor {}

class MockTripListingBloc extends Mock implements TripListingBloc {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeBuyerIntentEvent extends Fake implements BuyerIntentEvent {}

class FakeCropListingEvent extends Fake implements CropListingEvent {}

class FakeInboxEvent extends Fake implements InboxEvent {}

class FakeOrderEvent extends Fake implements OrderEvent {}

const _tokens = AuthTokens(
  accessToken: 'a',
  refreshToken: 'r',
  idToken: 'i',
);

/// Accessibility guard rails for the main screens (audit Fix 10): every
/// tappable thing is at least 48 by 48 and has a label, in English,
/// Amharic and Afaan Oromoo, at the default text size and at 200%, with no
/// overflow (an overflow fails the test as an exception). Contrast is
/// checked on the theme tokens (app_theme_contrast_test.dart): the
/// rendered-text contrast guideline is too flaky for CI.
void main() {
  late MockAuthBloc authBloc;
  late MockBuyerIntentBloc buyerIntentBloc;
  late MockCropListingBloc cropListingBloc;
  late MockInboxBloc inboxBloc;
  late MockOrderBloc orderBloc;
  late MockSyncExecutor syncExecutor;
  late MockTripListingBloc tripListingBloc;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeBuyerIntentEvent());
    registerFallbackValue(FakeCropListingEvent());
    registerFallbackValue(FakeInboxEvent());
    registerFallbackValue(FakeOrderEvent());
  });

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    authBloc = MockAuthBloc();
    buyerIntentBloc = MockBuyerIntentBloc();
    cropListingBloc = MockCropListingBloc();
    inboxBloc = MockInboxBloc();
    orderBloc = MockOrderBloc();
    syncExecutor = MockSyncExecutor();
    tripListingBloc = MockTripListingBloc();

    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => buyerIntentBloc.state).thenReturn(const BuyerIntentInitial());
    when(() => buyerIntentBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => cropListingBloc.state).thenReturn(
      const CropListingsLoaded(listings: []),
    );
    when(() => cropListingBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => inboxBloc.state).thenReturn(const InboxInitial());
    when(() => inboxBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.state).thenReturn(const OrdersLoaded(orders: []));
    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orderBloc.add(any())).thenReturn(null);
    when(() => syncExecutor.onSyncResult).thenAnswer(
      (_) => const Stream.empty(),
    );
    when(() => syncExecutor.isSyncing).thenReturn(false);
    when(() => tripListingBloc.state).thenReturn(const TripListingInitial());
    when(() => tripListingBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  tearDown(() => Intl.defaultLocale = null);

  void signIn(UserRole role) {
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(
        userId: '${role.name}-001',
        tokens: _tokens,
        role: role,
        userName: 'Almaz Bekele',
        woreda: 'Meki',
      ),
    );
  }

  /// [home] in the app's providers and delegates, on a phone.
  Widget app(Widget home, Locale locale) {
    return ChangeNotifierProvider<LanguageProvider>(
      create: (_) => LanguageProvider(),
      child: Provider<SyncExecutor>.value(
        value: syncExecutor,
        child: MultiBlocProvider(
          providers: [
            BlocProvider<AuthBloc>.value(value: authBloc),
            BlocProvider<BuyerIntentBloc>.value(value: buyerIntentBloc),
            BlocProvider<CropListingBloc>.value(value: cropListingBloc),
            BlocProvider<InboxBloc>.value(value: inboxBloc),
            BlocProvider<OrderBloc>.value(value: orderBloc),
            BlocProvider<TripListingBloc>.value(value: tripListingBloc),
          ],
          child: MaterialApp(
            locale: locale,
            localizationsDelegates: const [
              AppLocalizations.delegate,
              FallbackMaterialLocalizationsDelegate(),
              GlobalWidgetsLocalizations.delegate,
              FallbackCupertinoLocalizationsDelegate(),
            ],
            supportedLocales: AppLocalizations.supportedLocales,
            home: home,
          ),
        ),
      ),
    );
  }

  /// A role's Home as the phone shell draws it: the bar and the create
  /// button around the tab.
  Widget roleHome(UserRole role) => Scaffold(
        body: const HomeScreen(isOnline: false),
        floatingActionButton: RoleCreateButton(role: role, onPressed: () {}),
        floatingActionButtonLocation: FloatingActionButtonLocation.centerFloat,
        bottomNavigationBar: RoleNavBar(
          role: role,
          tabs: tabsForRole(role),
          currentIndex: 0,
          onSelected: (_) {},
        ),
      );

  Future<void> check(
    WidgetTester tester,
    Widget home,
    Locale locale,
    double textScale, {
    Size size = const Size(390, 844),
  }) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1.0;
    tester.platformDispatcher.textScaleFactorTestValue = textScale;
    addTearDown(tester.view.reset);
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
    final semantics = tester.ensureSemantics();

    await tester.pumpWidget(app(home, locale));
    await tester.pumpAndSettle();

    await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
    await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
    semantics.dispose();
  }

  const locales = [Locale('en'), Locale('am'), Locale('om')];
  const scales = [1.0, 2.0];

  for (final locale in locales) {
    for (final scale in scales) {
      final variant = '${locale.languageCode} at ${scale}x';

      for (final role in UserRole.values) {
        testWidgets('${role.name} Home, $variant', (tester) async {
          signIn(role);
          await check(tester, roleHome(role), locale, scale);
        });

        testWidgets('${role.name} bar and create button, $variant',
            (tester) async {
          signIn(role);
          await check(
            tester,
            Scaffold(
              floatingActionButton:
                  RoleCreateButton(role: role, onPressed: () {}),
              floatingActionButtonLocation:
                  FloatingActionButtonLocation.centerFloat,
              bottomNavigationBar: RoleNavBar(
                role: role,
                tabs: tabsForRole(role),
                currentIndex: 0,
                onSelected: (_) {},
              ),
            ),
            locale,
            scale,
          );
        });
      }

      testWidgets('Orders list (buyer), $variant', (tester) async {
        signIn(UserRole.buyer);
        await check(
          tester,
          const Scaffold(body: OrdersTabScreen()),
          locale,
          scale,
        );
      });

      testWidgets('Login form, $variant', (tester) async {
        when(() => authBloc.state).thenReturn(const AuthUnauthenticated());
        await check(tester, const LoginScreen(), locale, scale);
      });

      // FUP-118: Retake and Remove under the trucker's license photo, on a
      // 360 dp phone.
      testWidgets('Trucker license photo actions, $variant', (tester) async {
        await check(
          tester,
          Scaffold(
            body: RoleDetailsScreen(
              role: UserRole.trucker,
              drivingLicensePath: '/fake/license.jpg',
              onDrivingLicenseChanged: (_) {},
              onBuyerTypeChanged: (_) {},
              onCompanyNameChanged: (_) {},
            ),
          ),
          locale,
          scale,
          size: const Size(360, 740),
        );
      });
    }
  }
}
