import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/dispute_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockRepository extends Mock implements BaseDeliveryRepository {}

Delivery _delivery() => Delivery()
  ..deliveryId = 'delivery-001-abcd-efgh'
  ..tripId = 'trip-001'
  ..listingId = 'listing-001'
  ..farmerId = 'farmer-001'
  ..farmerName = 'Abebe Kebede'
  ..truckerId = 'trucker-001'
  ..truckerName = 'Tadesse Worku'
  ..buyerId = 'buyer-001'
  ..buyerName = 'Mekonnen Tadesse'
  ..qrToken = 'test-token'
  ..status = DeliveryStatus.delivered
  ..confirmedAt = DateTime.now().toUtc().subtract(const Duration(hours: 1))
  ..createdAt = DateTime(2026, 2, 20)
  ..updatedAt = DateTime(2026, 2, 20)
  ..version = 1;

const _stepUp = SyncRejectedException('AUTH_040: Confirm it is you.');
const _notSent = "Dispute not sent. Confirm it's you to send it.";

/// FUP-43: on web a delivery dispute answered AUTH_040 asks for the password
/// and sends the same dispute again. Any party of the delivery can dispute.
void main() {
  late _MockRepository repository;
  late List<Object> answers;
  late int prompts;
  late bool confirm;

  setUp(() {
    repository = _MockRepository();
    answers = [];
    prompts = 0;
    confirm = true;
    when(
      () => repository.raiseDispute(
        deliveryId: any(named: 'deliveryId'),
        userId: any(named: 'userId'),
        reason: any(named: 'reason'),
      ),
    ).thenAnswer((_) async {
      final answer = answers.isEmpty ? true : answers.removeAt(0);
      if (answer is Exception) throw answer;
    });
    when(
      () => repository.getDeliveriesByTrip(any(), userId: any(named: 'userId')),
    ).thenAnswer((_) async => <Delivery>[]);
  });

  Future<void> openAndSubmit(WidgetTester tester, String userId) async {
    // Created inside the test's zone, so pumps deliver its states.
    final bloc = DeliveryBloc(repository: repository);
    addTearDown(bloc.close);
    await tester.pumpWidget(
      BlocProvider<DeliveryBloc>.value(
        value: bloc,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: Builder(
            builder: (context) => Scaffold(
              body: TextButton(
                onPressed: () => Navigator.of(context).push(
                  MaterialPageRoute<void>(
                    builder: (_) => DisputeScreen(
                      delivery: _delivery(),
                      userId: userId,
                      confirmPassword: (_) async {
                        prompts++;
                        return confirm;
                      },
                    ),
                  ),
                ),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    await tester.enterText(find.byType(TextFormField), 'wet bags');
    await tester.tap(find.widgetWithText(FilledButton, 'Submit Dispute'));
    await tester.pumpAndSettle();
    // The "are you sure" dialog.
    await tester.tap(find.widgetWithText(TextButton, 'Submit Dispute'));
    await tester.pumpAndSettle();
  }

  int sent(String userId) => verify(
        () => repository.raiseDispute(
          deliveryId: 'delivery-001-abcd-efgh',
          userId: userId,
          reason: 'wet bags',
        ),
      ).callCount;

  for (final userId in ['buyer-001', 'farmer-001', 'trucker-001']) {
    testWidgets('$userId: the password prompt, then the same dispute again',
        (tester) async {
      answers.add(_stepUp);

      await openAndSubmit(tester, userId);

      expect(prompts, 1);
      expect(sent(userId), 2);
      // Sent: the screen closed, nothing says "not sent".
      expect(find.byType(DisputeScreen), findsNothing);
      expect(find.text(_notSent), findsNothing);
    });
  }

  testWidgets('a cancelled prompt leaves the dispute unsent and says so',
      (tester) async {
    answers.add(_stepUp);
    confirm = false;

    await openAndSubmit(tester, 'buyer-001');

    expect(prompts, 1);
    expect(sent('buyer-001'), 1);
    expect(find.byType(DisputeScreen), findsOneWidget);
    expect(find.text(_notSent), findsOneWidget);
    // Submit is offered again.
    final submit = find.widgetWithText(FilledButton, 'Submit Dispute');
    expect(tester.widget<FilledButton>(submit).onPressed, isNotNull);
  });

  testWidgets('a second AUTH_040 after the resend does not prompt again',
      (tester) async {
    answers.addAll([_stepUp, _stepUp]);

    await openAndSubmit(tester, 'buyer-001');

    expect(prompts, 1);
    expect(sent('buyer-001'), 2);
    expect(find.byType(DisputeScreen), findsOneWidget);
    expect(find.text(_notSent), findsOneWidget);
  });

  testWidgets('AUTH_041 keeps the generic error, without a prompt',
      (tester) async {
    answers.add(
      const SyncRejectedException('AUTH_041: Confirm it is you with Fayda.'),
    );

    await openAndSubmit(tester, 'buyer-001');

    expect(prompts, 0);
    expect(sent('buyer-001'), 1);
    expect(find.text('Delivery operation failed'), findsOneWidget);
  });

  testWidgets('a dispute that goes through at once never prompts',
      (tester) async {
    await openAndSubmit(tester, 'buyer-001');

    expect(prompts, 0);
    expect(sent('buyer-001'), 1);
    expect(find.byType(DisputeScreen), findsNothing);
  });
}
