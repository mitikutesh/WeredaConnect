import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/security/app_lock_service.dart';
import 'package:wereda_connect/core/security/local_auth_gate.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/screens/settings_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockSecureStorage extends Mock implements FlutterSecureStorage {}

class FakeGate implements LocalAuthGate {
  @override
  Future<LocalAuthOutcome> authenticate(String reason) async =>
      LocalAuthOutcome.authenticated;

  @override
  Future<bool> isAvailable() async => false;
}

AuthAuthenticated _signedIn(UserRole role) => AuthAuthenticated(
      userId: 'user-1',
      tokens:
          const AuthTokens(accessToken: 'a', refreshToken: 'r', idToken: 'i'),
      role: role,
      userName: 'Abebe Kebede',
      phoneNumber: '+251911234567',
    );

User _user(UserRole role, {required bool verified}) => User()
  ..userId = 'user-1'
  ..phoneNumber = '+251911234567'
  ..name = 'Abebe Kebede'
  ..role = role
  ..woreda = 'Adama'
  ..isVerified = verified
  ..faydaVerified = verified
  ..createdAt = DateTime.utc(2026, 9)
  ..updatedAt = DateTime.utc(2026, 9)
  ..version = 1;

/// Profile and Settings have no board; they speak the boards' language
/// (page header with eyebrow, white card groups under uppercase labels)
/// in one composition at every width, for every role.
void main() {
  late MockAuthBloc bloc;
  late AppLockService lock;

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    bloc = MockAuthBloc();
    when(() => bloc.stream).thenAnswer((_) => const Stream.empty());
    final storage = MockSecureStorage();
    when(() => storage.read(key: any(named: 'key')))
        .thenAnswer((_) async => null);
    lock = AppLockService(gate: FakeGate(), storage: storage);
  });

  void useWindow(WidgetTester tester, Size size) {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
  }

  Widget app(Widget home) => MultiProvider(
        providers: [
          ChangeNotifierProvider(create: (_) => LanguageProvider()),
          ChangeNotifierProvider(create: (_) => SettingsProvider()),
        ],
        child: BlocProvider<AuthBloc>.value(
          value: bloc,
          child: MaterialApp(
            theme: AppTheme.light(),
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: home,
          ),
        ),
      );

  Future<void> pumpProfile(
    WidgetTester tester,
    UserRole role, {
    bool verified = false,
    Size size = const Size(390, 1400),
  }) async {
    useWindow(tester, size);
    when(() => bloc.state).thenReturn(_signedIn(role));
    await tester.pumpWidget(
      app(
        ProfileScreen(
          loadUser: (_) async => _user(role, verified: verified),
          appLockService: lock,
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  group('Profile', () {
    for (final (role, label) in [
      (UserRole.farmer, 'Farmer'),
      (UserRole.trucker, 'Trucker'),
      (UserRole.buyer, 'Buyer'),
    ]) {
      testWidgets('$label: page header, identity card and card groups',
          (tester) async {
        await pumpProfile(tester, role);

        expect(find.byType(AppBar), findsNothing);
        expect(find.byType(FwPageHeader), findsOneWidget);
        // The role is the eyebrow and the pill on the identity card.
        expect(find.text(label), findsNWidgets(2));
        expect(find.text('Profile'), findsOneWidget);
        expect(find.text('Abebe Kebede'), findsOneWidget);
        // The groups and their labels.
        expect(find.text('ACCOUNT'), findsOneWidget);
        expect(find.text('MONEY'), findsOneWidget);
        expect(find.text('APP'), findsOneWidget);
        expect(find.byType(FwCard), findsNWidgets(6));
        // Every role keeps the same entries.
        expect(find.byKey(const Key('profile_verify_fayda')), findsOneWidget);
        expect(find.byKey(const Key('profile_delete_account')), findsOneWidget);
        expect(find.text('Logout'), findsOneWidget);
      });
    }

    testWidgets('a verified user shows the Verified chip, not the Fayda row',
        (tester) async {
      await pumpProfile(tester, UserRole.farmer, verified: true);

      expect(find.byType(StatusChip), findsOneWidget);
      expect(find.text('Verified'), findsOneWidget);
      expect(find.byKey(const Key('profile_verify_fayda')), findsNothing);
    });

    testWidgets('a 1280 px window centres the same column, no grid',
        (tester) async {
      await pumpProfile(
        tester,
        UserRole.buyer,
        size: const Size(1280, 1400),
      );

      final header = tester.getRect(find.byType(FwPageHeader));
      expect(header.width, lessThanOrEqualTo(FwContentWidth.defaultMaxWidth));
      expect((header.left - (1280 - header.right)).abs(), lessThan(2));
      // The groups stack in one column.
      final cards = find.byType(FwCard);
      final lefts = <double>{
        for (final e in cards.evaluate())
          tester.getRect(find.byWidget(e.widget)).left.roundToDouble(),
      };
      expect(lefts, hasLength(1));
    });
  });

  group('Settings', () {
    testWidgets('page header and card groups, same at 1280 px', (tester) async {
      for (final width in [390.0, 1280.0]) {
        useWindow(tester, Size(width, 1200));
        when(() => bloc.state).thenReturn(_signedIn(UserRole.farmer));
        await tester.pumpWidget(app(const SettingsScreen()));
        await tester.pumpAndSettle();

        expect(find.byType(AppBar), findsNothing, reason: '$width');
        expect(find.text('Settings'), findsOneWidget, reason: '$width');
        expect(find.text('LANGUAGE AND TIME'), findsOneWidget);
        expect(find.text('DATA'), findsOneWidget);
        expect(find.text('About'), findsOneWidget);
        expect(find.byKey(const Key('settings_clock_tile')), findsOneWidget);
        expect(find.text('Data Usage'), findsOneWidget);
        final header = tester.getRect(find.byType(FwPageHeader));
        expect(
          header.width,
          lessThanOrEqualTo(FwContentWidth.defaultMaxWidth),
          reason: '$width',
        );
      }
    });
  });
}
