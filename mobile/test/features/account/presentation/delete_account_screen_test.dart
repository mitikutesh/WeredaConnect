import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/account/data/repositories/account_deletion_repository.dart';
import 'package:wereda_connect/features/account/presentation/screens/delete_account_screen.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

class MockAccountDeletionRepository extends Mock
    implements AccountDeletionRepository {}

class MockAuthBloc extends Mock implements AuthBloc {}

class FakeAuthEvent extends Fake implements AuthEvent {}

const _me = 'farmer-1';
const _phone = '+251911234567';
const _token = 'id-token-of-farmer-1';

/// The screen runs its production wiring against a mocked AuthBloc: the
/// deleted account, the sign-out and the account check all go through it.
void main() {
  late MockAccountDeletionRepository repository;
  late MockAuthBloc auth;
  late List<AuthEvent> authEvents;
  late bool signedInAsMe;
  late bool online;
  late int passwordPrompts;
  late bool passwordConfirmed;

  setUpAll(() => registerFallbackValue(FakeAuthEvent()));

  setUp(() {
    repository = MockAccountDeletionRepository();
    auth = MockAuthBloc();
    authEvents = [];
    signedInAsMe = true;
    when(() => auth.state).thenReturn(
      const AuthAuthenticated(
        userId: _me,
        tokens: AuthTokens(accessToken: 'a', refreshToken: 'r', idToken: 'i'),
        role: UserRole.farmer,
        phoneNumber: _phone,
      ),
    );
    when(() => auth.stream).thenAnswer((_) => const Stream.empty());
    when(() => auth.add(any())).thenAnswer(
      (invocation) =>
          authEvents.add(invocation.positionalArguments.first as AuthEvent),
    );
    when(() => auth.bearerFor(any())).thenAnswer(
      (invocation) async =>
          signedInAsMe && invocation.positionalArguments.first == _me
              ? _token
              : null,
    );
    online = true;
    passwordPrompts = 0;
    passwordConfirmed = true;
  });

  /// How many times the AuthBloc was told this account is deleted.
  int deletedWipes() => authEvents
      .where(
        (e) => e == const AuthAccountDeleted(userId: _me, phoneNumber: _phone),
      )
      .length;

  void answer(List<DeleteAccountResult> results) {
    final queue = [...results];
    when(() => repository.deleteAccount(userId: _me, bearer: _token))
        .thenAnswer((_) async => queue.removeAt(0));
  }

  Future<void> pump(
    WidgetTester tester, {
    Locale locale = const Locale('en'),
  }) async {
    tester.view.physicalSize = const Size(800, 2400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      BlocProvider<AuthBloc>.value(
        value: auth,
        child: MaterialApp(
          // The app's delegates: om has no Material localizations of its own.
          localizationsDelegates: const [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            GlobalWidgetsLocalizations.delegate,
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          locale: locale,
          home: DeleteAccountScreen(
            repository: repository,
            isOnline: () async => online,
            confirmPassword: (_) async {
              passwordPrompts++;
              return passwordConfirmed;
            },
          ),
        ),
      ),
    );
    // The fallback Material localizations load asynchronously.
    await tester.pumpAndSettle();
  }

  final button = find.byKey(const Key('delete_account_button'));
  final confirm = find.byKey(const Key('delete_account_confirm'));

  Future<void> deleteAndConfirm(WidgetTester tester) async {
    await tester.tap(button);
    await tester.pumpAndSettle();
    await tester.tap(confirm);
    await tester.pumpAndSettle();
  }

  testWidgets('explains what is deleted and what is kept', (tester) async {
    await pump(tester);

    expect(find.text('Delete account'), findsOneWidget);
    // The headings are the boards' uppercase section labels.
    expect(find.text('WHAT IS DELETED'), findsOneWidget);
    expect(find.text('Your profile, name and phone number'), findsOneWidget);
    expect(find.text('The link to your Fayda ID'), findsOneWidget);
    expect(
      find.text('Your crop listings, trips and buying intents'),
      findsOneWidget,
    );
    expect(find.text('Your messages'), findsOneWidget);
    expect(find.text('WHAT IS KEPT, WITHOUT YOUR NAME'), findsOneWidget);
    expect(find.text('Orders, deliveries and payment records'), findsOneWidget);
    expect(find.text('Delete my account'), findsOneWidget);
  });

  for (final code in ['am', 'om']) {
    testWidgets('$code: the screen is translated', (tester) async {
      await pump(tester, locale: Locale(code));
      final l10n = lookupAppLocalizations(Locale(code));

      expect(find.text(l10n.deleteAccountWarning), findsOneWidget);
      expect(find.text('What is deleted'), findsNothing);
    });
  }

  testWidgets('cancelling the confirm dialog sends nothing', (tester) async {
    await pump(tester);

    await tester.tap(button);
    await tester.pumpAndSettle();
    expect(find.text('Delete your account?'), findsOneWidget);
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();

    verifyNever(() => repository.deleteAccount(userId: _me, bearer: _token));
    expect(deletedWipes(), 0);
  });

  testWidgets('confirmed: deletes, starts the wipe and says so',
      (tester) async {
    answer([const DeleteAccountDone()]);
    await pump(tester);

    await deleteAndConfirm(tester);

    verify(() => repository.deleteAccount(userId: _me, bearer: _token))
        .called(1);
    expect(deletedWipes(), 1);
    expect(find.text('Your account was deleted.'), findsOneWidget);
  });

  testWidgets('shows progress while the request runs', (tester) async {
    final pending = Completer<DeleteAccountResult>();
    when(() => repository.deleteAccount(userId: _me, bearer: _token))
        .thenAnswer((_) => pending.future);
    await pump(tester);

    await tester.tap(button);
    await tester.pumpAndSettle();
    await tester.tap(confirm);
    await tester.pump();
    await tester.pump();

    expect(find.text('Deleting your account...'), findsOneWidget);
    expect(tester.widget<FilledButton>(button).onPressed, isNull);

    pending.complete(const DeleteAccountDone());
    await tester.pumpAndSettle();
  });

  testWidgets('blocked: one line per blocker type, support for payment review',
      (tester) async {
    answer([
      const DeleteAccountBlocked(
        blockers: [
          AccountDeletionBlocker(type: 'order', id: 'o1'),
          AccountDeletionBlocker(type: 'order', id: 'o2'),
          AccountDeletionBlocker(type: 'escrow', id: 'e1'),
          AccountDeletionBlocker(type: 'delivery', id: 'd1'),
          AccountDeletionBlocker(type: 'dispute', id: 'o3'),
          AccountDeletionBlocker(type: 'payment_review', id: 'o4'),
          AccountDeletionBlocker(type: 'held_payout', id: 'x1'),
          AccountDeletionBlocker(type: 'loan', id: 'x2'),
        ],
        blockerCount: 11,
      ),
    ]);
    await pump(tester);

    await deleteAndConfirm(tester);

    expect(find.text('You cannot delete your account yet'), findsOneWidget);
    expect(find.text('Active orders: 2'), findsOneWidget);
    expect(find.text('Payments held in escrow: 1'), findsOneWidget);
    expect(find.text('Open deliveries: 1'), findsOneWidget);
    expect(find.text('Open disputes: 1'), findsOneWidget);
    expect(
      find.text(
        'Payments waiting for a refund: 1. Contact support to finish the refund.',
      ),
      findsOneWidget,
    );
    // Types this version does not know share one generic line.
    expect(find.text('Other active items: 2'), findsOneWidget);
    expect(find.text('And 3 more.'), findsOneWidget);
    expect(deletedWipes(), 0);
  });

  testWidgets('offline: explains it and retries without asking again',
      (tester) async {
    online = false;
    answer([const DeleteAccountDone()]);
    await pump(tester);

    await deleteAndConfirm(tester);

    expect(
      find.text(
        'You are offline. Connect to the internet to delete your account.',
      ),
      findsOneWidget,
    );
    verifyNever(() => repository.deleteAccount(userId: _me, bearer: _token));

    online = true;
    await tester.tap(find.text('Retry'));
    await tester.pumpAndSettle();

    expect(find.text('Delete your account?'), findsNothing);
    verify(() => repository.deleteAccount(userId: _me, bearer: _token))
        .called(1);
    expect(deletedWipes(), 1);
  });

  testWidgets('network error: never a success, offers a retry', (tester) async {
    answer([const DeleteAccountFailed(AccountDeletionFailure.network)]);
    await pump(tester);

    await deleteAndConfirm(tester);

    expect(
      find.textContaining('we cannot tell if your account was deleted'),
      findsOneWidget,
    );
    expect(find.text('Your account was deleted.'), findsNothing);
    expect(find.text('Retry'), findsOneWidget);
    expect(deletedWipes(), 0);
  });

  testWidgets('step-up: asks for the password and sends the request again',
      (tester) async {
    answer([const DeleteAccountStepUpRequired(), const DeleteAccountDone()]);
    await pump(tester);

    await deleteAndConfirm(tester);

    expect(passwordPrompts, 1);
    verify(() => repository.deleteAccount(userId: _me, bearer: _token))
        .called(2);
    expect(deletedWipes(), 1);
  });

  testWidgets('step-up cancelled: nothing more is sent', (tester) async {
    passwordConfirmed = false;
    answer([const DeleteAccountStepUpRequired()]);
    await pump(tester);

    await deleteAndConfirm(tester);

    expect(passwordPrompts, 1);
    verify(() => repository.deleteAccount(userId: _me, bearer: _token))
        .called(1);
    expect(deletedWipes(), 0);
    expect(tester.widget<FilledButton>(button).onPressed, isNotNull);
  });

  testWidgets('suspended: says contact support and stays disabled',
      (tester) async {
    answer([const DeleteAccountFailed(AccountDeletionFailure.suspended)]);
    await pump(tester);

    await deleteAndConfirm(tester);

    expect(find.textContaining('Contact support'), findsOneWidget);
    expect(tester.widget<FilledButton>(button).onPressed, isNull);
  });

  // Review item 4: the production wiring, not an injected callback.
  testWidgets('200: tells the AuthBloc exactly this account is deleted',
      (tester) async {
    answer([const DeleteAccountDone()]);
    await pump(tester);

    await deleteAndConfirm(tester);

    expect(authEvents, [
      const AuthAccountDeleted(userId: _me, phoneNumber: _phone),
    ]);
  });

  testWidgets('AUTH_013: signs out, wipes nothing', (tester) async {
    answer([const DeleteAccountFailed(AccountDeletionFailure.accountNotFound)]);
    await pump(tester);

    await deleteAndConfirm(tester);

    expect(authEvents, [const AuthLogoutRequested()]);
  });

  testWidgets(
      'another account holds the stored session: says so, sends '
      'nothing, cannot retry', (tester) async {
    signedInAsMe = false;
    answer([const DeleteAccountDone()]);
    await pump(tester);

    await deleteAndConfirm(tester);

    verify(() => auth.bearerFor(_me)).called(1);
    expect(
      find.text(
        'Another account is now signed in here, so nothing was deleted. '
        'Reload the page or reopen the app, then try again.',
      ),
      findsOneWidget,
    );
    expect(tester.widget<FilledButton>(button).onPressed, isNull);
    verifyNever(
      () => repository.deleteAccount(
        userId: any(named: 'userId'),
        bearer: any(named: 'bearer'),
      ),
    );
    expect(authEvents, isEmpty);
  });
}
