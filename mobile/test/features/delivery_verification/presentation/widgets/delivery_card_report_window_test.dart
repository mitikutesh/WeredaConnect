import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

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

  testWidgets('after 48 h the line is gone', (tester) async {
    final old = DateTime.now().toUtc().subtract(const Duration(hours: 49));
    await pump(tester, _delivered(confirmedAt: old), 'b1');

    expect(find.textContaining('Report a problem until'), findsNothing);
  });
}
