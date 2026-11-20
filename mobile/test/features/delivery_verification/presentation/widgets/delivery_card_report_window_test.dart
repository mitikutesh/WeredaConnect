import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/dashed_border.dart';

Delivery _delivered({required DateTime confirmedAt}) => Delivery()
  ..deliveryId = 'del-12345678'
  ..tripId = 't1'
  ..listingId = 'l1'
  ..farmerId = 'f1'
  ..farmerName = 'Almaz'
  ..truckerId = 'tr1'
  ..truckerName = 'Dawit'
  ..buyerId = 'b1'
  ..buyerName = 'Selam'
  ..qrToken = 'qr'
  ..status = DeliveryStatus.delivered
  ..confirmedAt = confirmedAt
  ..createdAt = DateTime.utc(2026)
  ..updatedAt = DateTime.utc(2026)
  ..version = 1;

/// Web · Buyer deliveries board: a delivered row tells the buyer how long
/// they can still report a problem (48 h after delivery).
void main() {
  Future<void> pump(WidgetTester tester, Delivery delivery, String userId) =>
      tester.pumpWidget(
        MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(
            body: DeliveryCard(delivery: delivery, currentUserId: userId),
          ),
        ),
      );

  final recent = DateTime.now().toUtc().subtract(const Duration(hours: 2));

  testWidgets('the buyer sees until when to report a problem', (tester) async {
    await pump(tester, _delivered(confirmedAt: recent), 'b1');

    expect(find.textContaining('Report a problem until'), findsOneWidget);
  });

  testWidgets('the farmer does not get the buyer line', (tester) async {
    await pump(tester, _delivered(confirmedAt: recent), 'f1');

    expect(find.textContaining('Report a problem until'), findsNothing);
  });

  // Design canvas page M, the Buyer deliveries board: the delivered card
  // is sand, with the orange "Release pending" pill and what (not) to do.
  testWidgets('the buyer: Release pending and nothing to do, then Open',
      (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: DeliveryCard(
            delivery: _delivered(confirmedAt: recent),
            currentUserId: 'b1',
            onTap: () {},
          ),
        ),
      ),
    );

    expect(find.byKey(const Key('delivery_card_delivered')), findsOneWidget);
    expect(find.text('Release pending'), findsOneWidget);
    expect(
      find.textContaining(
        'Nothing for you to do. The payment is released after ',
      ),
      findsOneWidget,
    );
    expect(find.textContaining('unless you report a problem.'), findsOneWidget);
    expect(find.text('Open'), findsOneWidget);
  });

  testWidgets('the farmer and the trucker: paid after the window',
      (tester) async {
    for (final viewer in ['f1', 'tr1']) {
      await pump(tester, _delivered(confirmedAt: recent), viewer);

      expect(find.text('Release pending'), findsOneWidget);
      expect(
        find.textContaining('Nothing for you to do. You are paid after '),
        findsOneWidget,
      );
      expect(
        find.textContaining('unless the buyer reports a problem.'),
        findsOneWidget,
      );
    }
  });

  testWidgets('waiting for pickup: a dashed card with the day and the note',
      (tester) async {
    final waiting = _delivered(confirmedAt: recent)
      ..status = DeliveryStatus.pending
      ..confirmedAt = null
      ..cropType = 'teff'
      ..quantity = 1500
      ..pickupPlace = 'Bichena'
      ..pickupPlannedAt = DateTime.utc(2026, 10, 30, 6);
    await pump(tester, waiting, 'b1');

    expect(find.byKey(const Key('delivery_card_waiting')), findsOneWidget);
    expect(find.byType(DashedBorderBox), findsOneWidget);
    expect(find.text('Teff · 1,500 kg'), findsOneWidget);
    expect(find.text('Fri 30 Oct'), findsOneWidget);
    // The buyer's waiting card names the farmer alone, never the driver.
    expect(find.text('Almaz, Bichena'), findsOneWidget);
    expect(
      find.text("The map appears once the driver scans the farmer's QR."),
      findsOneWidget,
    );

    // The note is the buyer's: the trucker has no map to wait for.
    await pump(tester, waiting, 'tr1');
    expect(find.textContaining('The map appears'), findsNothing);
  });

  testWidgets('after 48 h the line is gone', (tester) async {
    final old = DateTime.now().toUtc().subtract(const Duration(hours: 49));
    await pump(tester, _delivered(confirmedAt: old), 'b1');

    expect(find.textContaining('Report a problem until'), findsNothing);
    expect(find.text('Release pending'), findsNothing);
    expect(find.textContaining('Nothing for you to do'), findsNothing);
  });
}
