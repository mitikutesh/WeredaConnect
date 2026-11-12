import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/security/app_lock_service.dart';
import 'package:wereda_connect/core/security/local_auth_gate.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/features/reputation/data/reputation_repository.dart';
import 'package:wereda_connect/features/reputation/domain/own_rating.dart';
import 'package:wereda_connect/features/reputation/presentation/screens/my_reputation_screen.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';
import 'package:wereda_connect/shared/providers/settings_provider.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

import '../features/reputation/reputation_fixtures.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockSecureStorage extends Mock implements FlutterSecureStorage {}

class MockRatingRepository extends Mock implements BaseRatingRepository {}

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

User _user(UserRole role) => User()
  ..userId = 'user-1'
  ..phoneNumber = '+251911234567'
  ..name = 'Abebe Kebede'
  ..role = role
  ..woreda = 'Adama'
  ..isVerified = false
  ..faydaVerified = false
  ..createdAt = DateTime.utc(2026, 9)
  ..updatedAt = DateTime.utc(2026, 9)
  ..version = 1;

/// ESC-31: Profile shows the user's own rating instead of the dash, for
/// every role, and offers My reputation to a farmer or trucker once the
/// server says penalties are switched on. Only ever the user's own data.
void main() {
  late MockAuthBloc bloc;
  late AppLockService lock;
  late MockReputationRepository reputation;
  late MockRatingRepository ratings;

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    bloc = MockAuthBloc();
    when(() => bloc.stream).thenAnswer((_) => const Stream.empty());
    final storage = MockSecureStorage();
    when(() => storage.read(key: any(named: 'key')))
        .thenAnswer((_) async => null);
    lock = AppLockService(gate: FakeGate(), storage: storage);
    reputation = MockReputationRepository();
    ratings = MockRatingRepository();
    when(() => ratings.getRatingSummary(any()))
        .thenAnswer((_) async => RatingSummary.empty);
  });

  Future<void> pumpProfile(
    WidgetTester tester,
    UserRole role, {
    Future<OwnRating?> Function(String userId, UserRole role)? loadOwnRating,
    bool provideRepositories = true,
    bool settle = true,
  }) async {
    tester.view.physicalSize = const Size(390, 1500);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    when(() => bloc.state).thenReturn(_signedIn(role));
    await tester.pumpWidget(
      MultiProvider(
        providers: [
          ChangeNotifierProvider(create: (_) => LanguageProvider()),
          ChangeNotifierProvider(create: (_) => SettingsProvider()),
          if (provideRepositories)
            Provider<ReputationRepository>.value(value: reputation),
        ],
        child: MultiBlocProvider(
          providers: [
            BlocProvider<AuthBloc>.value(value: bloc),
            if (provideRepositories)
              BlocProvider<RatingBloc>(
                create: (_) => RatingBloc(repository: ratings),
              ),
          ],
          child: MaterialApp(
            theme: AppTheme.light(),
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: ProfileScreen(
              loadUser: (_) async => _user(role),
              appLockService: lock,
              loadOwnRating: loadOwnRating,
            ),
          ),
        ),
      ),
    );
    if (settle) await tester.pumpAndSettle();
  }

  String ratingTile(WidgetTester tester) => tester
      .widget<Text>(
        find
            .descendant(
              of: find.byKey(const Key('profile_rating')),
              matching: find.byType(Text),
            )
            .first,
      )
      .data!;

  final myReputationRow = find.byKey(const Key('profile_my_reputation'));

  for (final role in [UserRole.farmer, UserRole.trucker]) {
    group(role.name, () {
      testWidgets('the own average replaces the dash', (tester) async {
        when(() => reputation.loadOwn('user-1')).thenAnswer(
          (_) async => snapshot(
            ownReputation(penaltiesEnabled: false, deduction: 0.4),
          ),
        );
        await pumpProfile(tester, role);

        // The buyers' own average, as My reputation shows it.
        expect(ratingTile(tester), '4.8');
        expect(find.text('Rating'), findsOneWidget);
      });

      testWidgets('no ratings yet: a neutral word, not a dash', (tester) async {
        when(() => reputation.loadOwn('user-1')).thenAnswer(
          (_) async => snapshot(
            ownReputation(penaltiesEnabled: false, rated: false),
          ),
        );
        await pumpProfile(tester, role);

        expect(ratingTile(tester), 'None yet');
      });

      testWidgets('the dash stays while the rating is being read',
          (tester) async {
        final pending = Completer<OwnRating?>();
        await pumpProfile(
          tester,
          role,
          loadOwnRating: (_, __) => pending.future,
          settle: false,
        );
        await tester.pump();
        await tester.pump();

        expect(ratingTile(tester), '-');

        pending.complete(const OwnRating(average: 4.2, count: 3));
        await tester.pumpAndSettle();

        expect(ratingTile(tester), '4.2');
      });

      testWidgets(
          'reputation unreadable and nothing saved: the own rating '
          'aggregate is read instead', (tester) async {
        when(() => reputation.loadOwn('user-1'))
            .thenThrow(const NetworkException());
        when(() => ratings.getRatingSummary('user-1')).thenAnswer(
          (_) async => const RatingSummary(average: 4.6, count: 5),
        );
        await pumpProfile(tester, role);

        expect(ratingTile(tester), '4.6');
        // Only ever the user's own id.
        verify(() => ratings.getRatingSummary('user-1')).called(1);
        verifyNever(
          () => ratings.getRatingSummary(any(that: isNot('user-1'))),
        );
      });

      testWidgets('nothing readable at all: the dash, never a made-up word',
          (tester) async {
        when(() => reputation.loadOwn('user-1'))
            .thenThrow(const NetworkException());
        await pumpProfile(tester, role);

        expect(ratingTile(tester), '-');
        expect(myReputationRow, findsNothing);
      });

      testWidgets('penalties switched off: no My reputation row',
          (tester) async {
        when(() => reputation.loadOwn('user-1')).thenAnswer(
          (_) async => snapshot(ownReputation(penaltiesEnabled: false)),
        );
        await pumpProfile(tester, role);

        expect(myReputationRow, findsNothing);
        expect(find.text('My reputation'), findsNothing);
      });

      testWidgets('penalties switched on: the row opens My reputation',
          (tester) async {
        when(() => reputation.loadOwn('user-1', force: any(named: 'force')))
            .thenAnswer(
          (_) async => snapshot(ownReputation(tier: 'warning', points: 4)),
        );
        when(() => reputation.loadEvents('user-1'))
            .thenAnswer((_) async => eventsResult(const []));
        await pumpProfile(tester, role);

        expect(myReputationRow, findsOneWidget);
        expect(find.text('Rating, penalty points and appeals'), findsOneWidget);

        await tester.tap(myReputationRow);
        await tester.pumpAndSettle();

        expect(find.byType(MyReputationScreen), findsOneWidget);
        expect(
          tester
              .widget<MyReputationScreen>(find.byType(MyReputationScreen))
              .userId,
          'user-1',
        );
        expect(find.text('4 penalty points'), findsOneWidget);
      });
    });
  }

  group('buyer', () {
    testWidgets(
        'buyers are not rated: the neutral word, with no call for a '
        'reputation or a rating', (tester) async {
      await pumpProfile(tester, UserRole.buyer);

      expect(ratingTile(tester), 'None yet');
      expect(myReputationRow, findsNothing);
      verifyNever(() => reputation.loadOwn(any(), force: any(named: 'force')));
      verifyNever(() => ratings.getRatingSummary(any()));
    });
  });

  testWidgets('without the app repositories the profile still renders',
      (tester) async {
    await pumpProfile(tester, UserRole.farmer, provideRepositories: false);

    expect(ratingTile(tester), '-');
    expect(myReputationRow, findsNothing);
  });

  for (final locale in const [Locale('am'), Locale('om')]) {
    test('${locale.languageCode}: the neutral word is short and translated',
        () {
      final l10n = lookupAppLocalizations(locale);
      expect(l10n.profileRatingNone, isNot('None yet'));
      expect(l10n.profileRatingNone.length, lessThanOrEqualTo(12));
    });
  }
}
