import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/handoff_routes.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_generate_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockRepository extends Mock implements BaseDeliveryRepository {}

class _MockQrService extends Mock implements QrVerificationService {}

class _FakeDelivery extends Fake implements Delivery {}

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

/// The trucker's drop-off QR: the trucker's own code kept since the pickup
/// (ESC-35), one made when none is kept, or a farmer's token an older
/// version kept (T-3).
void main() {
  final l10n = lookupAppLocalizations(const Locale('en'));
  late _MockRepository repository;
  late _MockQrService qr;

  setUpAll(() => registerFallbackValue(_FakeDelivery()));

  setUp(() {
    repository = _MockRepository();
    qr = _MockQrService();
    when(() => qr.generateQrToken(any(), dropoff: true))
        .thenAnswer((_) async => ('new-token', 'd.new-nonce'));
    when(
      () => repository.persistDropoffQrNonce(
        deliveryId: any(named: 'deliveryId'),
        nonce: any(named: 'nonce'),
        truckerId: any(named: 'truckerId'),
        renew: any(named: 'renew'),
      ),
    ).thenAnswer((_) async => DropoffCodeRegistration.sent);
  });

  void verifyRegistered(String nonce, {bool renew = false}) => verify(
        () => repository.persistDropoffQrNonce(
          deliveryId: 'delivery-12345678',
          nonce: nonce,
          truckerId: 'trucker-1',
          renew: renew,
        ),
      ).called(1);

  void verifyNothingRegistered() => verifyNever(
        () => repository.persistDropoffQrNonce(
          deliveryId: any(named: 'deliveryId'),
          nonce: any(named: 'nonce'),
          truckerId: any(named: 'truckerId'),
          renew: any(named: 'renew'),
        ),
      );

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
                repository: repository,
                qrService: qr,
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

  testWidgets('the trucker\'s kept code opens and is sent again',
      (tester) async {
    final store = MemoryDropoffHandoffStore();
    await store.keep(
      'trucker-1',
      'delivery-12345678',
      const KeptHandoff(token: 'tok', code: '757555978', nonce: 'd.kept'),
    );

    await open(tester, store);

    expect(find.byType(QrGenerateScreen), findsOneWidget);
    expect(find.text(l10n.deliveryDropoffQrTitle), findsOneWidget);
    expect(find.text(l10n.deliveryDropoffQrOwnNote), findsOneWidget);
    verifyRegistered('d.kept');
    verifyNever(() => qr.generateQrToken(any(), dropoff: true));
  });

  testWidgets(
      'none kept: no code is made on open; New drop-off code, once '
      'confirmed, makes one that replaces any other', (tester) async {
    final store = MemoryDropoffHandoffStore();

    await open(tester, store);

    // A second phone or browser must not void the code the buyer may hold.
    expect(find.byType(QrGenerateScreen), findsOneWidget);
    expect(find.byKey(const Key('dropoff_none_here')), findsOneWidget);
    verifyNever(() => qr.generateQrToken(any(), dropoff: true));
    verifyNothingRegistered();

    await tester.tap(find.byKey(const Key('dropoff_new_code')));
    await tester.pumpAndSettle();
    expect(find.text(l10n.deliveryDropoffNewCodeConfirmBody), findsOneWidget);
    await tester.tap(find.byKey(const Key('dropoff_new_code_confirm')));
    await tester.pumpAndSettle();

    expect(find.text(l10n.deliveryDropoffQrOwnNote), findsOneWidget);
    verifyRegistered('d.new-nonce', renew: true);
    final kept = await store.read('trucker-1', 'delivery-12345678');
    expect(kept?.nonce, 'd.new-nonce');
  });

  testWidgets('a farmer\'s token an older version kept still shows, unsent',
      (tester) async {
    final store = MemoryDropoffHandoffStore();
    await store.keep(
      'trucker-1',
      'delivery-12345678',
      const KeptHandoff(token: 'farmer-token', code: '757555978'),
    );

    await open(tester, store);

    expect(find.byType(QrGenerateScreen), findsOneWidget);
    expect(find.text(l10n.deliveryDropoffQrNote), findsOneWidget);
    verifyNothingRegistered();
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
    }
    verifyNever(() => qr.generateQrToken(any(), dropoff: true));
    verifyNothingRegistered();
  });
}
