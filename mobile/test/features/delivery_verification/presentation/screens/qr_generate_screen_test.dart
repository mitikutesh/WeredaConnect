import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:qr_flutter/qr_flutter.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
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
    final en = lookupAppLocalizations(const Locale('en'));

    setUp(() {
      qr = MockQrService();
      repository = MockDeliveryRepository();
      var n = 0;
      when(() => qr.generateQrToken(any()))
          .thenAnswer((_) async => ('token-${++n}', 'nonce-$n'));
      when(
        () => repository.persistQrNonce(
          deliveryId: any(named: 'deliveryId'),
          qrNonce: any(named: 'qrNonce'),
          userId: any(named: 'userId'),
        ),
      ).thenAnswer((_) async {});
    });

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

    testWidgets('the drop-off QR shows the kept token and sends nothing',
        (tester) async {
      final delivery = _delivery()..status = DeliveryStatus.pickedUp;
      await pump(
        tester,
        QrGenerateScreen.dropoff(
          delivery: delivery,
          userId: 'tr1',
          dropoff: const KeptHandoff(token: 'kept-token', code: '757555978'),
        ),
      );

      expect(find.text(en.deliveryDropoffQrTitle), findsOneWidget);
      expect(find.text(en.deliveryDropoffQrInstruction), findsOneWidget);
      expect(find.byType(QrImageView), findsOneWidget);
      // Nothing generated or persisted, and no Regenerate.
      verifyNever(() => qr.generateQrToken(any()));
      verifyNever(
        () => repository.persistQrNonce(
          deliveryId: any(named: 'deliveryId'),
          qrNonce: any(named: 'qrNonce'),
          userId: any(named: 'userId'),
        ),
      );
      expect(find.text(en.deliveryRegenerateQr), findsNothing);

      await tester.tap(find.byKey(const Key('qr_show_code')));
      await tester.pumpAndSettle();
      expect(find.text('757 555 978'), findsOneWidget);
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
        ),
      );

      expect(find.byType(QrImageView), findsNothing);
      expect(find.text(en.deliveryDropoffCodeOnly), findsOneWidget);
      expect(find.text('340 404 994'), findsOneWidget);
    });
  });
}
