import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_generate_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockQrService extends Mock implements QrVerificationService {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

class FakeDelivery extends Fake implements Delivery {}

Delivery _delivery() => Delivery()
  ..deliveryId = 'delivery-12345678'
  ..tripId = 't1'
  ..listingId = 'l1'
  ..farmerId = 'f1'
  ..farmerName = deletedUserSentinel
  ..truckerId = 'tr1'
  ..truckerName = 'Kebede'
  ..buyerId = 'b1'
  ..buyerName = 'Almaz'
  ..qrToken = 'qr'
  ..status = DeliveryStatus.pending
  ..createdAt = DateTime(2026)
  ..updatedAt = DateTime(2026)
  ..version = 1;

/// ACC-03: a deleted farmer's name on the QR screen is the localized
/// "Deleted user".
void main() {
  setUpAll(() => registerFallbackValue(FakeDelivery()));

  testWidgets('am: the deleted farmer reads in Amharic', (tester) async {
    final qr = MockQrService();
    final repository = MockDeliveryRepository();
    when(() => qr.generateQrToken(any()))
        .thenAnswer((_) async => ('token', 'nonce'));
    when(
      () => repository.persistQrNonce(
        deliveryId: any(named: 'deliveryId'),
        qrNonce: any(named: 'qrNonce'),
        userId: any(named: 'userId'),
      ),
    ).thenAnswer((_) async {});

    await tester.pumpWidget(
      MaterialApp(
        locale: const Locale('am'),
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: QrGenerateScreen(
          delivery: _delivery(),
          qrService: qr,
          repository: repository,
          userId: 'f1',
        ),
      ),
    );
    await tester.pumpAndSettle();

    final am = lookupAppLocalizations(const Locale('am'));
    expect(find.text(am.deletedUser), findsOneWidget);
    expect(find.text(deletedUserSentinel), findsNothing);
  });
}
