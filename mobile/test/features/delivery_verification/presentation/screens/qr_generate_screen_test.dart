import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:qr_flutter/qr_flutter.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_qr_issuer.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/handoff_code.dart';
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

  group('typed code (T-1)', () {
    late MockQrService qr;
    late MockDeliveryRepository repository;
    late MemoryDropoffHandoffStore handoffs;
    final en = lookupAppLocalizations(const Locale('en'));

    void answerDropoff(DropoffCodeRegistration registration) => when(
          () => repository.persistDropoffQrNonce(
            deliveryId: any(named: 'deliveryId'),
            nonce: any(named: 'nonce'),
            truckerId: any(named: 'truckerId'),
            renew: any(named: 'renew'),
          ),
        ).thenAnswer((_) async => registration);

    setUp(() {
      qr = MockQrService();
      repository = MockDeliveryRepository();
      handoffs = MemoryDropoffHandoffStore();
      var n = 0;
      when(() => qr.generateQrToken(any()))
          .thenAnswer((_) async => ('token-${++n}', 'nonce-$n'));
      var d = 0;
      when(() => qr.generateQrToken(any(), dropoff: true))
          .thenAnswer((_) async => ('drop-token-${++d}', 'd.nonce-$d'));
      when(
        () => repository.persistQrNonce(
          deliveryId: any(named: 'deliveryId'),
          qrNonce: any(named: 'qrNonce'),
          userId: any(named: 'userId'),
        ),
      ).thenAnswer((_) async {});
      answerDropoff(DropoffCodeRegistration.sent);
    });

    DropoffQrIssuer issuer() => DropoffQrIssuer(
          qrService: qr,
          repository: repository,
          store: handoffs,
        );

    void verifyDropoffSent(String nonce, int times, {bool renew = false}) =>
        verify(
          () => repository.persistDropoffQrNonce(
            deliveryId: 'delivery-12345678',
            nonce: nonce,
            truckerId: 'tr1',
            renew: renew,
          ),
        ).called(times);

    /// New drop-off code, confirmed.
    Future<void> newCode(WidgetTester tester) async {
      await tester.tap(find.byKey(const Key('dropoff_new_code')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('dropoff_new_code_confirm')));
      await tester.pumpAndSettle();
    }

    Future<void> pump(WidgetTester tester, Widget screen) async {
      await tester.pumpWidget(
        MaterialApp(
          locale: const Locale('en'),
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: screen,
        ),
      );
      await tester.pumpAndSettle();
    }

    String codeOf(String nonce) => HandoffCode.format(
          HandoffCode.derive(nonce: nonce, deliveryId: 'delivery-12345678'),
        );

    testWidgets(
        'the farmer reveals the code of the shown nonce, with a warning',
        (tester) async {
      await pump(
        tester,
        QrGenerateScreen(
          delivery: _delivery(),
          qrService: qr,
          repository: repository,
          userId: 'f1',
        ),
      );

      // Hidden until asked for.
      expect(find.text(codeOf('nonce-1')), findsNothing);
      await tester.tap(find.byKey(const Key('qr_show_code')));
      await tester.pumpAndSettle();
      expect(find.text(codeOf('nonce-1')), findsOneWidget);
      expect(find.text(en.qrShowCodeWarning), findsOneWidget);

      // Regenerate: a new nonce, so a new code.
      await tester.tap(find.text(en.deliveryRegenerateQr));
      await tester.pumpAndSettle();
      expect(find.text(codeOf('nonce-2')), findsOneWidget);
      expect(find.text(codeOf('nonce-1')), findsNothing);
    });

    testWidgets(
        'a farmer\'s token an older version kept shows as it is, until a '
        'new code', (tester) async {
      final delivery = _delivery()..status = DeliveryStatus.pickedUp;
      await pump(
        tester,
        QrGenerateScreen.dropoff(
          delivery: delivery,
          userId: 'tr1',
          dropoff: const KeptHandoff(token: 'kept-token', code: '757555978'),
          issuer: issuer(),
        ),
      );

      expect(find.text(en.deliveryDropoffQrTitle), findsOneWidget);
      expect(find.text(en.deliveryDropoffQrInstruction), findsOneWidget);
      expect(find.text(en.deliveryDropoffQrNote), findsOneWidget);
      expect(find.byType(QrImageView), findsOneWidget);
      // Nothing generated or sent for it, and the farmer's Regenerate is not
      // here.
      verifyNever(() => qr.generateQrToken(any()));
      verifyNever(() => qr.generateQrToken(any(), dropoff: true));
      verifyNever(
        () => repository.persistDropoffQrNonce(
          deliveryId: any(named: 'deliveryId'),
          nonce: any(named: 'nonce'),
          truckerId: any(named: 'truckerId'),
          renew: any(named: 'renew'),
        ),
      );
      expect(find.text(en.deliveryRegenerateQr), findsNothing);

      await tester.tap(find.byKey(const Key('qr_show_code')));
      await tester.pumpAndSettle();
      expect(find.text('757 555 978'), findsOneWidget);

      // New drop-off code: the trucker's own replaces it.
      await newCode(tester);
      expect(find.text(codeOf('d.nonce-1')), findsOneWidget);
      expect(find.text(en.deliveryDropoffQrOwnNote), findsOneWidget);
      verifyDropoffSent('d.nonce-1', 1, renew: true);
      final kept = await handoffs.read('tr1', 'delivery-12345678');
      expect(kept?.nonce, 'd.nonce-1');
    });

    testWidgets(
        'the trucker\'s own code is sent again on open; a new one replaces '
        'it (ESC-35)', (tester) async {
      await pump(
        tester,
        QrGenerateScreen.dropoff(
          delivery: _delivery()..status = DeliveryStatus.inTransit,
          userId: 'tr1',
          dropoff: KeptHandoff(
            token: 'own-token',
            code: HandoffCode.derive(
              nonce: 'd.kept',
              deliveryId: 'delivery-12345678',
            ),
            nonce: 'd.kept',
          ),
          issuer: issuer(),
        ),
      );

      expect(find.byType(QrImageView), findsOneWidget);
      expect(find.text(en.deliveryDropoffQrOwnNote), findsOneWidget);
      expect(find.byKey(const Key('dropoff_not_sent')), findsNothing);
      verifyDropoffSent('d.kept', 1);

      await tester.tap(find.byKey(const Key('qr_show_code')));
      await tester.pumpAndSettle();
      expect(find.text(codeOf('d.kept')), findsOneWidget);

      // Cancelled: nothing changes.
      await tester.tap(find.byKey(const Key('dropoff_new_code')));
      await tester.pumpAndSettle();
      await tester.tap(find.text(en.cancel));
      await tester.pumpAndSettle();
      expect(find.text(codeOf('d.kept')), findsOneWidget);
      verifyNever(() => qr.generateQrToken(any(), dropoff: true));

      await newCode(tester);
      expect(find.text(codeOf('d.nonce-1')), findsOneWidget);
      expect(find.text(codeOf('d.kept')), findsNothing);
      verifyDropoffSent('d.nonce-1', 1, renew: true);
    });

    testWidgets('a code the server did not get says so (offline web)',
        (tester) async {
      when(
        () => repository.persistDropoffQrNonce(
          deliveryId: any(named: 'deliveryId'),
          nonce: any(named: 'nonce'),
          truckerId: any(named: 'truckerId'),
          renew: any(named: 'renew'),
        ),
      ).thenThrow(const NetworkException('offline'));

      await pump(
        tester,
        QrGenerateScreen.dropoff(
          delivery: _delivery()..status = DeliveryStatus.pickedUp,
          userId: 'tr1',
          dropoff: null,
          issuer: issuer(),
        ),
      );
      await newCode(tester);

      // Made and shown anyway: the buyer's confirm waits for it.
      expect(find.byType(QrImageView), findsOneWidget);
      expect(find.byKey(const Key('dropoff_not_sent')), findsOneWidget);
      expect(find.text(en.deliveryDropoffQrNotSent), findsOneWidget);
    });

    testWidgets('a code the server did not keep says so (no pickup code)',
        (tester) async {
      answerDropoff(DropoffCodeRegistration.notStored);

      await pump(
        tester,
        QrGenerateScreen.dropoff(
          delivery: _delivery()..status = DeliveryStatus.pickedUp,
          userId: 'tr1',
          dropoff:
              const KeptHandoff(token: 'own', code: '757555978', nonce: 'd.k'),
          issuer: issuer(),
        ),
      );

      expect(find.byKey(const Key('dropoff_not_sent')), findsOneWidget);
    });

    testWidgets(
        'a code made since on another phone or browser: this one says it '
        'does not work', (tester) async {
      answerDropoff(DropoffCodeRegistration.superseded);

      await pump(
        tester,
        QrGenerateScreen.dropoff(
          delivery: _delivery()..status = DeliveryStatus.inTransit,
          userId: 'tr1',
          dropoff:
              const KeptHandoff(token: 'own', code: '757555978', nonce: 'd.k'),
          issuer: issuer(),
        ),
      );

      expect(find.byKey(const Key('dropoff_superseded')), findsOneWidget);
      expect(find.text(en.deliveryDropoffQrSuperseded), findsOneWidget);
      verifyDropoffSent('d.k', 1);
    });

    // A phone whose copy said pending may open the QR after the pickup
    // synced: the delivery's nonce is the drop-off's now, so none is made.
    testWidgets('after pickup the farmer makes no new QR', (tester) async {
      when(
        () => repository.getDeliveryById(
          'delivery-12345678',
          userId: 'f1',
        ),
      ).thenAnswer(
        (_) async => _delivery()..status = DeliveryStatus.pickedUp,
      );

      await pump(
        tester,
        QrGenerateScreen(
          delivery: _delivery(),
          qrService: qr,
          repository: repository,
          userId: 'f1',
        ),
      );

      expect(find.text(en.deliveryQrAlreadyPickedUp), findsOneWidget);
      expect(find.byType(QrImageView), findsNothing);
      expect(find.byKey(const Key('qr_show_code')), findsNothing);
      expect(find.text(en.deliveryRegenerateQr), findsNothing);
      verifyNever(() => qr.generateQrToken(any()));
      verifyNever(
        () => repository.persistQrNonce(
          deliveryId: any(named: 'deliveryId'),
          qrNonce: any(named: 'qrNonce'),
          userId: any(named: 'userId'),
        ),
      );
    });

    testWidgets('a delivery still pending gets its QR', (tester) async {
      when(
        () => repository.getDeliveryById(
          'delivery-12345678',
          userId: 'f1',
        ),
      ).thenAnswer((_) async => _delivery());

      await pump(
        tester,
        QrGenerateScreen(
          delivery: _delivery(),
          qrService: qr,
          repository: repository,
          userId: 'f1',
        ),
      );

      expect(find.byType(QrImageView), findsOneWidget);
      verify(() => qr.generateQrToken(any())).called(1);
    });

    testWidgets('a typed pickup has only its code to show at the drop-off',
        (tester) async {
      await pump(
        tester,
        QrGenerateScreen.dropoff(
          delivery: _delivery()..status = DeliveryStatus.inTransit,
          userId: 'tr1',
          dropoff: const KeptHandoff(code: '340404994'),
          issuer: issuer(),
        ),
      );

      expect(find.byType(QrImageView), findsNothing);
      expect(find.text(en.deliveryDropoffCodeOnly), findsOneWidget);
      expect(find.text('340 404 994'), findsOneWidget);
    });
  });
}
