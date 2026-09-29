import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/auth/widgets/kyc_required_dialog.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

void main() {
  Widget host({required void Function(BuildContext) onTap}) => MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: Builder(
            builder: (context) => TextButton(
              onPressed: () => onTap(context),
              child: const Text('pay'),
            ),
          ),
        ),
      );

  testWidgets('explains the Fayda requirement; Cancel resolves false',
      (tester) async {
    bool? outcome;
    await tester.pumpWidget(
      host(onTap: (c) async => outcome = await showKycRequiredDialog(c)),
    );
    await tester.tap(find.text('pay'));
    await tester.pumpAndSettle();

    expect(find.text('Verification needed'), findsOneWidget);
    expect(
      find.textContaining('Payments above 10,000 ETB need a verified Fayda'),
      findsOneWidget,
    );

    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();
    expect(outcome, isFalse);
    expect(find.byType(AlertDialog), findsNothing);
  });

  testWidgets('uses the phone wording when the phone is not verified',
      (tester) async {
    await tester.pumpWidget(
      host(
        onTap: (c) => showKycRequiredDialog(c, reason: KycReason.buyerPhone),
      ),
    );
    await tester.tap(find.text('pay'));
    await tester.pumpAndSettle();

    expect(
      find.textContaining('Your phone number is not verified yet'),
      findsOneWidget,
    );
  });

  testWidgets('Verify opens verification and reports its result',
      (tester) async {
    bool? outcome;
    await tester.pumpWidget(
      host(
        onTap: (c) async => outcome = await showKycRequiredDialog(
          c,
          verificationScreen: (context) => Scaffold(
            body: TextButton(
              onPressed: () => Navigator.of(context).pop(true),
              child: const Text('fayda screen'),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('pay'));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('kyc_verify_button')));
    await tester.pumpAndSettle();

    expect(find.byType(AlertDialog), findsNothing);
    expect(find.text('fayda screen'), findsOneWidget);

    await tester.tap(find.text('fayda screen'));
    await tester.pumpAndSettle();
    expect(outcome, isTrue);
  });

  testWidgets('a failed or abandoned verification resolves false',
      (tester) async {
    bool? outcome;
    await tester.pumpWidget(
      host(
        onTap: (c) async => outcome = await showKycRequiredDialog(
          c,
          verificationScreen: (context) => Scaffold(
            body: TextButton(
              onPressed: () => Navigator.of(context).pop(false),
              child: const Text('fayda screen'),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('pay'));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('kyc_verify_button')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('fayda screen'));
    await tester.pumpAndSettle();

    expect(outcome, isFalse);
  });

  for (final (reason, text) in [
    (KycReason.farmerFayda, 'need the farmer to have a verified Fayda ID'),
    (KycReason.farmerPhone, "The farmer's phone number is not verified"),
    (KycReason.unavailable, 'could not check identity verification'),
  ]) {
    testWidgets('$reason: explains, offers no Fayda, resolves false',
        (tester) async {
      bool? outcome;
      var opened = false;
      await tester.pumpWidget(
        host(
          onTap: (c) async => outcome = await showKycRequiredDialog(
            c,
            reason: reason,
            verificationScreen: (_) {
              opened = true;
              return const SizedBox();
            },
          ),
        ),
      );
      await tester.tap(find.text('pay'));
      await tester.pumpAndSettle();

      expect(find.textContaining(text), findsOneWidget);
      expect(find.byKey(const Key('kyc_verify_button')), findsNothing);

      await tester.tap(find.byKey(const Key('kyc_ok_button')));
      await tester.pumpAndSettle();
      expect(outcome, isFalse);
      expect(opened, isFalse);
    });
  }
}
