import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/security/local_auth_gate.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/handoff_code.dart';
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
  late MemoryDropoffHandoffStore handoffs;
  late StreamController<DeliveryState> states;
  final delivery = _delivery();
  final l10n = lookupAppLocalizations(const Locale('en'));
  final typedCode =
      HandoffCode.derive(nonce: 'nonce-1', deliveryId: delivery.deliveryId);

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
    states = StreamController<DeliveryState>.broadcast();
    when(() => bloc.state).thenReturn(const DeliveryInitial());
    when(() => bloc.stream).thenAnswer((_) => states.stream);
    handoffs = MemoryDropoffHandoffStore();
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

  tearDown(() => states.close());

  /// [pushed]: opened over a page, as the app does, so the screen can pop
  /// and a snackbar has a page to show on.
  Future<void> pumpScreen(
    WidgetTester tester,
    VerificationType type, {
    bool pushed = false,
  }) async {
    final screen = QrScanScreen(
      delivery: delivery,
      verificationType: type,
      qrService: qr,
      gpsService: gps,
      photoManager: MockPhotoManager(),
      localAuthGate: gate,
      handoffStore: handoffs,
    );
    await tester.pumpWidget(
      BlocProvider<DeliveryBloc>.value(
        value: bloc,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: pushed
              ? Scaffold(
                  body: Builder(
                    builder: (context) => TextButton(
                      onPressed: () => Navigator.of(context).push<void>(
                        MaterialPageRoute(builder: (_) => screen),
                      ),
                      child: const Text('open'),
                    ),
                  ),
                )
              : screen,
        ),
      ),
    );
    if (pushed) {
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
    }
    await tester.pump();
  }

  Future<void> scanValidCode(
    WidgetTester tester,
    VerificationType type,
  ) async {
    await pumpScreen(tester, type);
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

  testWidgets('a scan says the QR was read and is checked online, not passed',
      (tester) async {
    await scanValidCode(tester, VerificationType.pickup);

    expect(find.text(l10n.deliveryQrReadCheckedOnline), findsOneWidget);
    expect(find.text('Verification Passed'), findsNothing);
  });

  group('camera error (T-1)', () {
    Future<void> pumpError(
      WidgetTester tester, {
      required bool denied,
      bool web = false,
      List<String>? taps,
    }) async {
      await tester.pumpWidget(
        MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: Scaffold(
            body: QrCameraErrorView(
              permissionDenied: denied,
              onRetry: () => taps?.add('retry'),
              onTypeCode: () => taps?.add('type'),
              onOpenSettings: web ? null : () => taps?.add('settings'),
            ),
          ),
        ),
      );
    }

    testWidgets('a refused camera says how to allow it, with every way out',
        (tester) async {
      final taps = <String>[];
      await pumpError(tester, denied: true, taps: taps);

      expect(find.text(l10n.qrCameraDeniedTitle), findsOneWidget);
      expect(find.text(l10n.qrCameraDeniedText), findsOneWidget);
      await tester.tap(find.byKey(const Key('qr_camera_settings')));
      await tester.tap(find.byKey(const Key('qr_camera_retry')));
      await tester.tap(find.byKey(const Key('qr_camera_type_code')));
      expect(taps, ['settings', 'retry', 'type']);
    });

    testWidgets('on web the browser settings are named, no settings button',
        (tester) async {
      await pumpError(tester, denied: true, web: true);

      expect(find.text(l10n.qrCameraDeniedTextWeb), findsOneWidget);
      expect(find.byKey(const Key('qr_camera_settings')), findsNothing);
      expect(find.byKey(const Key('qr_camera_type_code')), findsOneWidget);
    });

    testWidgets('a missing or busy camera says so', (tester) async {
      await pumpError(tester, denied: false);

      expect(find.text(l10n.qrCameraUnavailableTitle), findsOneWidget);
      expect(find.text(l10n.qrCameraUnavailableText), findsOneWidget);
      expect(find.byKey(const Key('qr_camera_settings')), findsNothing);
    });

    testWidgets('the scanner builds the panel for its errors', (tester) async {
      await pumpScreen(tester, VerificationType.pickup);
      final builder =
          tester.widget<MobileScanner>(find.byType(MobileScanner)).errorBuilder;
      expect(builder, isNotNull);
      final context = tester.element(find.byType(MobileScanner));

      final denied = builder!(
        context,
        const MobileScannerException(
          errorCode: MobileScannerErrorCode.permissionDenied,
        ),
      );
      final busy = builder(
        context,
        const MobileScannerException(
          errorCode: MobileScannerErrorCode.genericError,
        ),
      );
      expect((denied as QrCameraErrorView).permissionDenied, isTrue);
      expect((busy as QrCameraErrorView).permissionDenied, isFalse);
    });
  });

  group('typed code (T-1)', () {
    Future<void> openTyping(WidgetTester tester, VerificationType type) async {
      await pumpScreen(tester, type);
      await tester.tap(find.byKey(const Key('qr_type_code')));
      await tester.pumpAndSettle();
    }

    Future<void> typeAndCheck(WidgetTester tester, String code) async {
      await tester.enterText(find.byKey(const Key('qr_code_field')), code);
      await tester.tap(find.byKey(const Key('qr_code_check')));
      await tester.pumpAndSettle();
    }

    testWidgets('the check digit catches typos and short codes offline',
        (tester) async {
      await openTyping(tester, VerificationType.pickup);
      expect(find.text(l10n.qrTypeCodePickupHint), findsOneWidget);

      await typeAndCheck(tester, typedCode.substring(0, 8));
      expect(find.text(l10n.qrTypeCodeIncomplete), findsOneWidget);

      final last = int.parse(typedCode[8]);
      final typo = '${typedCode.substring(0, 8)}${(last + 1) % 10}';
      await typeAndCheck(tester, typo);
      expect(find.text(l10n.qrTypeCodeTypo), findsOneWidget);
      verifyNever(() => bloc.add(any()));
    });

    testWidgets('only digits go into the field, at most nine', (tester) async {
      await openTyping(tester, VerificationType.pickup);

      await tester.enterText(
        find.byKey(const Key('qr_code_field')),
        'ab 757-555 9780 12',
      );
      await tester.pump();

      final field =
          tester.widget<TextField>(find.byKey(const Key('qr_code_field')));
      expect(field.controller!.text, '757555978');
    });

    testWidgets('a typed code needs a location before Confirm, then sends it',
        (tester) async {
      await openTyping(tester, VerificationType.pickup);
      await typeAndCheck(tester, typedCode);

      expect(find.text(l10n.deliveryCodeTypedCheckedOnline), findsOneWidget);
      expect(find.text(l10n.qrTypedCodeNeedsGps), findsOneWidget);
      final confirm = find.byKey(const Key('qr_confirm'));
      expect(tester.widget<FilledButton>(confirm).onPressed, isNull);

      when(() => gps.captureCurrentPosition()).thenAnswer(
        (_) async => const GpsCaptureResult.success(
          latitude: 9.0,
          longitude: 38.7,
          accuracy: 12,
        ),
      );
      await tester.ensureVisible(find.byKey(const Key('qr_retry_gps')));
      await tester.tap(find.byKey(const Key('qr_retry_gps')));
      await tester.pumpAndSettle();
      expect(tester.widget<FilledButton>(confirm).onPressed, isNotNull);

      await tapConfirm(tester, 'Confirm Pickup');

      final sent =
          verify(() => bloc.add(captureAny())).captured.single as ConfirmPickup;
      expect(sent.scannedQrCode, typedCode);
      expect(sent.scannedQrNonce, isNull);
      expect(sent.gpsPickup?.latitude, 9.0);
    });

    testWidgets('at a drop-off the buyer is told to type it with the crops',
        (tester) async {
      await openTyping(tester, VerificationType.delivery);

      expect(find.text(l10n.qrTypeCodeDropoffHint), findsOneWidget);
    });

    testWidgets('Scan QR goes back to the camera', (tester) async {
      await openTyping(tester, VerificationType.pickup);
      await tester.tap(find.byKey(const Key('qr_code_back_to_camera')));
      await tester.pumpAndSettle();

      expect(find.byType(MobileScanner), findsOneWidget);
      expect(find.byKey(const Key('qr_code_field')), findsNothing);
    });
  });

  group('drop-off handoff kept at pickup (T-3)', () {
    testWidgets('a scanned pickup keeps the token and its code',
        (tester) async {
      await scanValidCode(tester, VerificationType.pickup);
      await tapConfirm(tester, 'Confirm Pickup');

      states.add(const PickupConfirmed());
      await tester.pumpAndSettle();

      final kept = await handoffs.read('trucker-001', delivery.deliveryId);
      expect(kept?.token, 'qr-token');
      expect(kept?.code, typedCode);
    });

    testWidgets('a typed pickup keeps the code only', (tester) async {
      when(() => gps.captureCurrentPosition()).thenAnswer(
        (_) async => const GpsCaptureResult.success(
          latitude: 9.0,
          longitude: 38.7,
          accuracy: 12,
        ),
      );
      await pumpScreen(tester, VerificationType.pickup, pushed: true);
      await tester.tap(find.byKey(const Key('qr_type_code')));
      await tester.pumpAndSettle();
      await tester.enterText(find.byKey(const Key('qr_code_field')), typedCode);
      await tester.tap(find.byKey(const Key('qr_code_check')));
      await tester.pumpAndSettle();
      await tapConfirm(tester, 'Confirm Pickup');

      states.add(const PickupConfirmed());
      await tester.pumpAndSettle();

      final kept = await handoffs.read('trucker-001', delivery.deliveryId);
      expect(kept?.token, isNull);
      expect(kept?.code, typedCode);
      expect(find.text(l10n.qrPickupSavedCheckOnline), findsOneWidget);
    });

    testWidgets('a buyer confirm keeps nothing', (tester) async {
      await scanValidCode(tester, VerificationType.delivery);
      await tapConfirm(tester, 'Confirm Delivery');

      states.add(const DeliveryConfirmed());
      await tester.pumpAndSettle();

      expect(await handoffs.read('trucker-001', delivery.deliveryId), isNull);
    });
  });
}
