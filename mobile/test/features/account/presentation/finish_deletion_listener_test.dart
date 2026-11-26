import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/account/data/repositories/account_deletion_repository.dart';
import 'package:wereda_connect/features/account/presentation/bloc/account_deletion_bloc.dart';
import 'package:wereda_connect/features/account/presentation/widgets/finish_deletion_listener.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockAccountDeletionRepository extends Mock
    implements AccountDeletionRepository {}

AuthAuthenticated _signedIn(String userId, UserRole role) => AuthAuthenticated(
      userId: userId,
      tokens: const AuthTokens(
        accessToken: 'a',
        refreshToken: 'r',
        idToken: 'i',
      ),
      role: role,
    );

void main() {
  late MockAuthBloc auth;
  late StreamController<AuthState> authStates;
  late StreamController<String> gone;
  late AuthState authState;
  late bool storedSessionIsMine;
  late DateTime now;

  setUp(() {
    auth = MockAuthBloc();
    authStates = StreamController<AuthState>.broadcast();
    gone = StreamController<String>.broadcast();
    authState = _signedIn('farmer-1', UserRole.farmer);
    storedSessionIsMine = true;
    now = DateTime(2026, 11, 27, 9);
    when(() => auth.state).thenAnswer((_) => authState);
    when(() => auth.stream).thenAnswer((_) => authStates.stream);
    when(() => auth.isSignedInAs(any())).thenAnswer(
      (invocation) async =>
          storedSessionIsMine &&
          authState is AuthAuthenticated &&
          (authState as AuthAuthenticated).userId ==
              invocation.positionalArguments.first,
    );
  });

  tearDown(() {
    // Not awaited: a broadcast stream's close waits for its listeners.
    unawaited(authStates.close());
    unawaited(gone.close());
  });

  final prompt = find.byKey(const Key('finish_deleting_prompt'));
  final finish = find.byKey(const Key('finish_deleting_button'));
  final deleteScreen = find.text('delete account screen');

  Future<void> pump(
    WidgetTester tester, {
    Locale locale = const Locale('en'),
  }) async {
    await tester.pumpWidget(
      BlocProvider<AuthBloc>.value(
        value: auth,
        child: MaterialApp(
          localizationsDelegates: const [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            GlobalWidgetsLocalizations.delegate,
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          locale: locale,
          home: FinishDeletionListener(
            accountGone: gone.stream,
            now: () => now,
            deleteScreen: (_) =>
                const Scaffold(body: Text('delete account screen')),
            child: const Scaffold(body: Text('home')),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Future<void> refuse(WidgetTester tester, String deletionState) async {
    gone.add(deletionState);
    await tester.pumpAndSettle();
  }

  for (final role in UserRole.values) {
    testWidgets(
        '${role.name}: an unfinished deletion offers Finish deleting, '
        'which opens Delete account', (tester) async {
      authState = _signedIn('${role.name}-1', role);
      await pump(tester);

      await refuse(tester, 'in_progress');

      expect(prompt, findsOneWidget);
      expect(find.text('Finish deleting your account?'), findsOneWidget);
      expect(find.text('Not now'), findsOneWidget);
      await tester.tap(finish);
      await tester.pumpAndSettle();

      expect(prompt, findsNothing);
      expect(deleteScreen, findsOneWidget);
    });
  }

  for (final code in ['am', 'om']) {
    testWidgets('$code: the prompt is translated', (tester) async {
      await pump(tester, locale: Locale(code));
      final l10n = lookupAppLocalizations(Locale(code));

      await refuse(tester, 'in_progress');

      expect(find.text(l10n.finishDeletingTitle), findsOneWidget);
      expect(find.text(l10n.finishDeletingButton), findsOneWidget);
      expect(find.text('Finish deleting your account?'), findsNothing);
    });
  }

  testWidgets('a deleted account is the wipe, not a prompt', (tester) async {
    await pump(tester);

    await refuse(tester, 'completed');
    await refuse(tester, 'cognito_pending');

    expect(prompt, findsNothing);
  });

  testWidgets('one prompt at a time', (tester) async {
    await pump(tester);

    gone
      ..add('in_progress')
      ..add('in_progress');
    await tester.pumpAndSettle();

    expect(prompt, findsOneWidget);
  });

  testWidgets('Not now quiets it for 10 minutes for this account',
      (tester) async {
    await pump(tester);

    await refuse(tester, 'in_progress');
    await tester.tap(find.text('Not now'));
    await tester.pumpAndSettle();
    expect(prompt, findsNothing);

    now = now.add(const Duration(minutes: 9));
    await refuse(tester, 'in_progress');
    expect(prompt, findsNothing);

    // Another account on the same phone is asked.
    authState = _signedIn('trucker-1', UserRole.trucker);
    await refuse(tester, 'in_progress');
    expect(prompt, findsOneWidget);
    await tester.tap(find.text('Not now'));
    await tester.pumpAndSettle();

    authState = _signedIn('farmer-1', UserRole.farmer);
    now = now.add(const Duration(minutes: 2));
    await refuse(tester, 'in_progress');
    expect(prompt, findsOneWidget);
  });

  testWidgets('signed out, or another account in the stored session: no prompt',
      (tester) async {
    authState = const AuthUnauthenticated();
    await pump(tester);
    await refuse(tester, 'in_progress');
    expect(prompt, findsNothing);

    authState = _signedIn('farmer-1', UserRole.farmer);
    storedSessionIsMine = false;
    await refuse(tester, 'in_progress');
    expect(prompt, findsNothing);
  });

  testWidgets('no prompt while a Delete account screen is open',
      (tester) async {
    await pump(tester);
    final screenBloc = AccountDeletionBloc(
      repository: MockAccountDeletionRepository(),
      userId: 'farmer-1',
      isOnline: () async => true,
      bearerFor: () async => null,
      onDeleted: () {},
      onAccountMissing: () {},
    );

    await refuse(tester, 'in_progress');
    expect(prompt, findsNothing);

    await tester.runAsync(screenBloc.close);
    await refuse(tester, 'in_progress');
    expect(prompt, findsOneWidget);
  });

  testWidgets('a sign-out closes the prompt, and the screen it opened',
      (tester) async {
    await pump(tester);

    await refuse(tester, 'in_progress');
    authState = const AuthUnauthenticated();
    authStates.add(authState);
    await tester.pumpAndSettle();
    expect(prompt, findsNothing);

    authState = _signedIn('farmer-1', UserRole.farmer);
    await refuse(tester, 'in_progress');
    await tester.tap(finish);
    await tester.pumpAndSettle();
    expect(deleteScreen, findsOneWidget);

    // The finished deletion wipes the account and signs out.
    authState = const AuthAccountWiped();
    authStates.add(authState);
    await tester.pumpAndSettle();
    expect(deleteScreen, findsNothing);
    expect(find.text('home'), findsOneWidget);
  });
}
