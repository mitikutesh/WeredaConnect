import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/features/account/data/repositories/account_deletion_repository.dart';
import 'package:wereda_connect/features/account/presentation/bloc/account_deletion_bloc.dart';
import 'package:wereda_connect/features/account/presentation/screens/delete_account_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

class MockAccountDeletionRepository extends Mock
    implements AccountDeletionRepository {}

void main() {
  late MockAccountDeletionRepository repository;
  late bool online;
  late int deleted;
  late int passwordPrompts;
  late bool passwordConfirmed;

  setUp(() {
    repository = MockAccountDeletionRepository();
    online = true;
    deleted = 0;
    passwordPrompts = 0;
    passwordConfirmed = true;
  });

  void answer(List<DeleteAccountResult> results) {
    final queue = [...results];
    when(() => repository.deleteAccount())
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
      MaterialApp(
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
          createBloc: (_) => AccountDeletionBloc(
            repository: repository,
            isOnline: () async => online,
            onDeleted: () => deleted++,
            onAccountMissing: () {},
            wait: (_) async {},
          ),
          confirmPassword: (_) async {
            passwordPrompts++;
            return passwordConfirmed;
          },
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
    expect(find.text('What is deleted'), findsOneWidget);
    expect(find.text('Your profile, name and phone number'), findsOneWidget);
    expect(find.text('The link to your Fayda ID'), findsOneWidget);
    expect(
      find.text('Your crop listings, trips and buying intents'),
      findsOneWidget,
    );
    expect(find.text('Your messages'), findsOneWidget);
    expect(find.text('What is kept, without your name'), findsOneWidget);
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

    verifyNever(() => repository.deleteAccount());
    expect(deleted, 0);
  });

  testWidgets('confirmed: deletes, starts the wipe and says so',
      (tester) async {
    answer([const DeleteAccountDone()]);
    await pump(tester);

    await deleteAndConfirm(tester);

    verify(() => repository.deleteAccount()).called(1);
    expect(deleted, 1);
    expect(find.text('Your account was deleted.'), findsOneWidget);
  });

  testWidgets('shows progress while the request runs', (tester) async {
    final pending = Completer<DeleteAccountResult>();
    when(() => repository.deleteAccount()).thenAnswer((_) => pending.future);
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
    expect(deleted, 0);
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
    verifyNever(() => repository.deleteAccount());

    online = true;
    await tester.tap(find.text('Retry'));
    await tester.pumpAndSettle();

    expect(find.text('Delete your account?'), findsNothing);
    verify(() => repository.deleteAccount()).called(1);
    expect(deleted, 1);
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
    expect(deleted, 0);
  });

  testWidgets('step-up: asks for the password and sends the request again',
      (tester) async {
    answer([const DeleteAccountStepUpRequired(), const DeleteAccountDone()]);
    await pump(tester);

    await deleteAndConfirm(tester);

    expect(passwordPrompts, 1);
    verify(() => repository.deleteAccount()).called(2);
    expect(deleted, 1);
  });

  testWidgets('step-up cancelled: nothing more is sent', (tester) async {
    passwordConfirmed = false;
    answer([const DeleteAccountStepUpRequired()]);
    await pump(tester);

    await deleteAndConfirm(tester);

    expect(passwordPrompts, 1);
    verify(() => repository.deleteAccount()).called(1);
    expect(deleted, 0);
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
}
