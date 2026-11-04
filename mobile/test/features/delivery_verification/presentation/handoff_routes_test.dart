import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/handoff_routes.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_generate_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

Delivery _delivery() => Delivery()
  ..deliveryId = 'delivery-12345678'
  ..tripId = 'trip-1'
  ..listingId = 'l-1'
  ..farmerId = 'f-1'
  ..farmerName = 'Almaz'
  ..truckerId = 'trucker-1'
  ..truckerName = 'Dawit'
  ..buyerId = 'b-1'
  ..buyerName = 'Selam'
  ..qrToken = 'qr'
  ..status = DeliveryStatus.inTransit
  ..createdAt = DateTime.utc(2026, 10, 30)
  ..updatedAt = DateTime.utc(2026, 10, 30)
  ..version = 1;

/// T-3: the trucker's drop-off QR opens from what the phone kept at pickup.
void main() {
  final l10n = lookupAppLocalizations(const Locale('en'));

  Future<void> open(
    WidgetTester tester,
    DropoffHandoffStore store, {
    String userId = 'trucker-1',
  }) async {
    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: Scaffold(
          body: Builder(
            builder: (context) => TextButton(
              onPressed: () => openDropoffQr(
                context,
                _delivery(),
                userId: userId,
                store: store,
              ),
              child: const Text('go'),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('go'));
    await tester.pumpAndSettle();
  }

  testWidgets('the kept handoff opens the drop-off QR', (tester) async {
    final store = MemoryDropoffHandoffStore();
    await store.keep(
      'trucker-1',
      'delivery-12345678',
      const KeptHandoff(token: 'tok', code: '757555978'),
    );

    await open(tester, store);

    expect(find.byType(QrGenerateScreen), findsOneWidget);
    expect(find.text(l10n.deliveryDropoffQrTitle), findsOneWidget);
  });

  testWidgets('without one it says why, and opens nothing', (tester) async {
    await open(tester, MemoryDropoffHandoffStore());

    expect(find.byType(QrGenerateScreen), findsNothing);
    expect(find.text(l10n.deliveryDropoffQrMissing), findsOneWidget);
  });

  testWidgets('nothing opens for anyone but the delivery\'s trucker',
      (tester) async {
    final store = MemoryDropoffHandoffStore();
    // Kept under another account on this phone: still not shown.
    for (final userId in ['buyer-1', 'trucker-1']) {
      await store.keep(
        userId,
        'delivery-12345678',
        const KeptHandoff(code: '757555978'),
      );
    }

    for (final userId in ['buyer-1', 'f-1', '']) {
      await open(tester, store, userId: userId);

      expect(find.byType(QrGenerateScreen), findsNothing, reason: userId);
      expect(find.text(l10n.deliveryDropoffQrMissing), findsNothing);
    }
  });
}
