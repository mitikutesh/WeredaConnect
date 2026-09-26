import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/security/local_auth_gate.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/verification_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_scan_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockQrService extends Mock implements QrVerificationService {}

class MockGpsService extends Mock implements GpsCaptureService {}

class MockPhotoManager extends Mock implements PhotoManager {}

class FakeDeliveryEvent extends Fake implements DeliveryEvent {}

class FakeDelivery extends Fake implements Delivery {}

class FakeGate implements LocalAuthGate {
  LocalAuthOutcome outcome = LocalAuthOutcome.authenticated;
  int prompts = 0;

  @override
  Future<LocalAuthOutcome> authenticate(String reason) async {
    prompts++;
    return outcome;
  }

  @override
  Future<bool> isAvailable() async => true;
}

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
  ..status = DeliveryStatus.pickedUp
  ..createdAt = DateTime(2026, 2, 20)
  ..updatedAt = DateTime(2026, 2, 20)
  ..version = 1;

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late MockDeliveryBloc bloc;
  late MockQrService qr;
  late MockGpsService gps;
  late FakeGate gate;
  final delivery = _delivery();

  setUpAll(() {
    registerFallbackValue(FakeDeliveryEvent());
    registerFallbackValue(FakeDelivery());
    // The camera plugin is not present in widget tests.
    for (final name in [
      'dev.steenbakker.mobile_scanner/scanner/method',
      'dev.steenbakker.mobile_scanner/scanner/event',
    ]) {
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
          .setMockMethodCallHandler(MethodChannel(name), (_) async => null);
    }
  });

  setUp(() {
    bloc = MockDeliveryBloc();
    when(() => bloc.state).thenReturn(const DeliveryInitial());
    when(() => bloc.stream).thenAnswer((_) => const Stream.empty());
    qr = MockQrService();
    when(() => qr.parseQrToken(any(), any())).thenReturn(
      const QrParseResult.valid(
        deliveryId: 'delivery-001-abcd-efgh',
        tripId: 'trip-001',
        farmerId: 'farmer-001',
        timestamp: 1,
        nonce: 'nonce-1',
      ),
    );
    gps = MockGpsService();
    when(() => gps.captureCurrentPosition()).thenAnswer(
      (_) async => const GpsCaptureResult.unavailable('test'),
    );
    gate = FakeGate();
  });

  Future<void> scanValidCode(
    WidgetTester tester,
    VerificationType type,
  ) async {
    await tester.pumpWidget(
      BlocProvider<DeliveryBloc>.value(
        value: bloc,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: QrScanScreen(
            delivery: delivery,
            verificationType: type,
            qrService: qr,
            gpsService: gps,
            photoManager: MockPhotoManager(),
            localAuthGate: gate,
          ),
        ),
      ),
    );
    await tester.pump();
    tester.widget<MobileScanner>(find.byType(MobileScanner)).onDetect!(
      const BarcodeCapture(barcodes: [Barcode(rawValue: 'qr-token')]),
    );
    await tester.pumpAndSettle();
  }

  Future<void> tapConfirm(WidgetTester tester, String label) async {
    final button = find.widgetWithText(FilledButton, label);
    await tester.ensureVisible(button);
    await tester.tap(button);
    // Not pumpAndSettle: the button spins until the bloc answers.
    for (var i = 0; i < 5; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
  }

  testWidgets('a failed device auth blocks the delivery confirm',
      (tester) async {
    gate.outcome = LocalAuthOutcome.failed;
    await scanValidCode(tester, VerificationType.delivery);

    await tapConfirm(tester, 'Confirm Delivery');

    expect(gate.prompts, 1);
    verifyNever(() => bloc.add(any()));
    expect(
      find.byType(SnackBar),
      findsOneWidget,
      reason: 'the user is told why nothing happened',
    );
  });

  testWidgets('a passed device auth sends the delivery confirm',
      (tester) async {
    await scanValidCode(tester, VerificationType.delivery);

    await tapConfirm(tester, 'Confirm Delivery');

    expect(gate.prompts, 1);
    verify(() => bloc.add(any(that: isA<ConfirmDelivery>()))).called(1);
  });

  testWidgets('pickup scanning is not gated by device auth', (tester) async {
    gate.outcome = LocalAuthOutcome.failed;
    await scanValidCode(tester, VerificationType.pickup);

    await tapConfirm(tester, 'Confirm Pickup');

    expect(gate.prompts, 0);
    verify(() => bloc.add(any(that: isA<ConfirmPickup>()))).called(1);
  });
}
