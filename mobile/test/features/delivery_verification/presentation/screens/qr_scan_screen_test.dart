import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile_scanner/mobile_scanner.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/security/local_auth_gate.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_validation_exception.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/handoff_code.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/verification_type.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_scan_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class MockQrService extends Mock implements QrVerificationService {}

class MockGpsService extends Mock implements GpsCaptureService {}

class MockPhotoManager extends Mock implements PhotoManager {}

class MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

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
  late MockDeliveryRepository repository;
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
    repository = MockDeliveryRepository();
    when(() => bloc.repository).thenReturn(repository);
    when(
      () => repository.persistDropoffQrNonce(
        deliveryId: any(named: 'deliveryId'),
        nonce: any(named: 'nonce'),
        truckerId: any(named: 'truckerId'),
        renew: any(named: 'renew'),
      ),
    ).thenAnswer((_) async => DropoffCodeRegistration.sent);
    qr = MockQrService();
    when(() => qr.generateQrToken(any(), dropoff: true))
        .thenAnswer((_) async => ('drop-token', 'd.trucker-nonce'));
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
    PhotoManager? photoManager,
    Delivery? subject,
  }) async {
    final screen = QrScanScreen(
      delivery: subject ?? delivery,
      verificationType: type,
      qrService: qr,
      gpsService: gps,
      photoManager: photoManager ?? MockPhotoManager(),
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
    VerificationType type, {
    PhotoManager? photoManager,
    Delivery? subject,
  }) async {
    await pumpScreen(
      tester,
      type,
      photoManager: photoManager,
      subject: subject,
    );
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

  // FRD-12: the photo taken at the handoff rides on the confirm (the phone's
  // file, which the sync queue uploads; on web the key of the photo that
  // went up already).
  testWidgets('the handoff photo goes with the pickup confirm', (tester) async {
    const path =
        '/data/user/0/app/files/photos/delivery-001-abcd-efgh/pickup-1.webp';
    final photos = MockPhotoManager();
    when(
      () => photos.captureAndCompress(
        deliveryId: any(named: 'deliveryId'),
        type: PhotoType.pickup,
      ),
    ).thenAnswer(
      (_) async =>
          const PhotoResult.success(localPath: path, fileSizeBytes: 90000),
    );
    await scanValidCode(tester, VerificationType.pickup, photoManager: photos);

    final addPhoto = find.text('Add Photo');
    await tester.ensureVisible(addPhoto);
    await tester.tap(addPhoto);
    await tester.pumpAndSettle();
    await tapConfirm(tester, 'Confirm Pickup');

    final event = verify(() => bloc.add(captureAny())).captured.single;
    expect((event as ConfirmPickup).photoRef, path);
  });

  testWidgets('a photo that failed says so and the confirm goes without it',
      (tester) async {
    final photos = MockPhotoManager();
    when(
      () => photos.captureAndCompress(
        deliveryId: any(named: 'deliveryId'),
        type: PhotoType.pickup,
      ),
    ).thenAnswer((_) async => const PhotoResult.error('Photo too large'));
    await scanValidCode(tester, VerificationType.pickup, photoManager: photos);

    final addPhoto = find.text('Add Photo');
    await tester.ensureVisible(addPhoto);
    await tester.tap(addPhoto);
    await tester.pump();

    expect(find.text(l10n.deliveryPhotoFailed), findsOneWidget);
    await tapConfirm(tester, 'Confirm Pickup');
    final event = verify(() => bloc.add(captureAny())).captured.single;
    expect((event as ConfirmPickup).photoRef, isNull);
  });

  testWidgets('a closed camera says nothing', (tester) async {
    final photos = MockPhotoManager();
    when(
      () => photos.captureAndCompress(
        deliveryId: any(named: 'deliveryId'),
        type: PhotoType.pickup,
      ),
    ).thenAnswer((_) async => const PhotoResult.cancelled());
    await scanValidCode(tester, VerificationType.pickup, photoManager: photos);

    final addPhoto = find.text('Add Photo');
    await tester.ensureVisible(addPhoto);
    await tester.tap(addPhoto);
    await tester.pump();

    expect(find.byType(SnackBar), findsNothing);
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

  group('the trucker\'s own drop-off code, made at pickup (ESC-35)', () {
    final dropoffCode = HandoffCode.derive(
      nonce: 'd.trucker-nonce',
      deliveryId: delivery.deliveryId,
    );

    // Made at pickup, it never replaces a code the server keeps (no renew).
    void verifyRegistered() => verify(
          () => repository.persistDropoffQrNonce(
            deliveryId: delivery.deliveryId,
            nonce: 'd.trucker-nonce',
            truckerId: 'trucker-001',
          ),
        ).called(1);

    testWidgets('a scanned pickup makes, keeps and registers it',
        (tester) async {
      await scanValidCode(tester, VerificationType.pickup);
      await tapConfirm(tester, 'Confirm Pickup');

      states.add(const PickupConfirmed());
      await tester.pumpAndSettle();

      final kept = await handoffs.read('trucker-001', delivery.deliveryId);
      expect(kept?.token, 'drop-token');
      expect(kept?.nonce, 'd.trucker-nonce');
      expect(kept?.code, dropoffCode);
      // Not the farmer's token, which the buyer's confirm no longer takes.
      expect(kept?.token, isNot('qr-token'));
      verifyRegistered();
    });

    testWidgets('a typed pickup makes one too', (tester) async {
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
      expect(kept?.nonce, 'd.trucker-nonce');
      expect(kept?.code, dropoffCode);
      verifyRegistered();
      expect(find.text(l10n.qrPickupSavedCheckOnline), findsOneWidget);
    });

    testWidgets('a code that cannot be made never fails the pickup',
        (tester) async {
      when(() => qr.generateQrToken(any(), dropoff: true))
          .thenThrow(StateError('no device key'));
      await pumpScreen(tester, VerificationType.pickup, pushed: true);
      tester.widget<MobileScanner>(find.byType(MobileScanner)).onDetect!(
        const BarcodeCapture(barcodes: [Barcode(rawValue: 'qr-token')]),
      );
      await tester.pumpAndSettle();
      await tapConfirm(tester, 'Confirm Pickup');

      states.add(const PickupConfirmed());
      await tester.pumpAndSettle();

      // The screen closed as for any confirmed pickup.
      expect(find.byType(QrScanScreen), findsNothing);
      expect(await handoffs.read('trucker-001', delivery.deliveryId), isNull);
    });

    testWidgets('a buyer confirm makes nothing', (tester) async {
      await scanValidCode(tester, VerificationType.delivery);
      await tapConfirm(tester, 'Confirm Delivery');

      states.add(const DeliveryConfirmed());
      await tester.pumpAndSettle();

      expect(await handoffs.read('trucker-001', delivery.deliveryId), isNull);
      verifyNever(() => qr.generateQrToken(any(), dropoff: true));
    });
  });

  group('the buyer at a trucker\'s drop-off (ESC-35)', () {
    Future<void> failWith(
      WidgetTester tester,
      String code,
      String message, {
      VerificationType type = VerificationType.delivery,
    }) async {
      await scanValidCode(tester, type);
      await tapConfirm(
        tester,
        type == VerificationType.delivery
            ? 'Confirm Delivery'
            : 'Confirm Pickup',
      );
      states.add(DeliveryError(message: message, code: code));
      await tester.pumpAndSettle();
    }

    testWidgets('a wait for the trucker\'s code names the trucker',
        (tester) async {
      await failWith(
        tester,
        DeliveryErrorCode.qrPending,
        "DELIVERY_NONCE_PENDING: Waiting for the trucker's drop-off QR code to "
        'sync. Will retry automatically.',
      );

      expect(find.text(l10n.deliveryErrorDropoffQrPending), findsOneWidget);
      expect(find.text(l10n.deliveryErrorQrPending), findsNothing);
    });

    for (final message in [
      "DELIVERY_NONCE_PENDING: Waiting for the farmer's QR code to sync. "
          'Will retry automatically.',
      'DELIVERY_NONCE_PENDING: The payment check could not be completed. '
          'Will retry automatically.',
    ]) {
      testWidgets('any other wait keeps its own text: $message',
          (tester) async {
        await failWith(tester, DeliveryErrorCode.qrPending, message);

        expect(find.text(l10n.deliveryErrorQrPending), findsOneWidget);
        expect(find.text(l10n.deliveryErrorDropoffQrPending), findsNothing);
      });
    }

    testWidgets('the trucker\'s lock is renewed by the trucker',
        (tester) async {
      await failWith(
        tester,
        DeliveryErrorCode.codeLocked,
        'DELIVERY_CODE_LOCKED: Too many wrong codes for this drop-off. Scan '
        "the trucker's drop-off QR, or ask the trucker for a new code.",
      );

      expect(find.text(l10n.deliveryErrorDropoffCodeLocked), findsOneWidget);
    });

    testWidgets('a store without typed codes keeps the shared lock text',
        (tester) async {
      await failWith(
        tester,
        DeliveryErrorCode.codeLocked,
        'DELIVERY_CODE_LOCKED: Typed codes cannot be checked for this '
        'delivery. Scan the QR code.',
      );

      expect(find.text(l10n.deliveryErrorCodeLocked), findsOneWidget);
      expect(find.text(l10n.deliveryErrorDropoffCodeLocked), findsNothing);
    });

    testWidgets('the pickup keeps the farmer\'s wait text', (tester) async {
      await failWith(
        tester,
        DeliveryErrorCode.qrPending,
        "DELIVERY_NONCE_PENDING: Waiting for the trucker's drop-off QR code to "
        'sync. Will retry automatically.',
        type: VerificationType.pickup,
      );

      expect(find.text(l10n.deliveryErrorQrPending), findsOneWidget);
    });
  });

  group('location stamped at the scan (FRD-11)', () {
    Future<void> scanWithoutSettling(WidgetTester tester) async {
      await pumpScreen(tester, VerificationType.pickup);
      tester.widget<MobileScanner>(find.byType(MobileScanner)).onDetect!(
        const BarcodeCapture(barcodes: [Barcode(rawValue: 'qr-token')]),
      );
      // Not pumpAndSettle: the location spinner keeps animating.
      await tester.pump();
      await tester.pump();
    }

    bool confirmEnabled(WidgetTester tester) =>
        tester
            .widget<FilledButton>(find.byKey(const Key('qr_confirm')))
            .onPressed !=
        null;

    ConfirmPickup sentPickup() =>
        verify(() => bloc.add(captureAny())).captured.single as ConfirmPickup;

    testWidgets('Confirm waits for the fix, then sends it with the pickup',
        (tester) async {
      final fix = Completer<GpsCaptureResult>();
      when(() => gps.captureCurrentPosition()).thenAnswer((_) => fix.future);
      await scanWithoutSettling(tester);

      expect(confirmEnabled(tester), isFalse);
      await tester.tap(find.byKey(const Key('qr_confirm')));
      await tester.pump();
      verifyNever(() => bloc.add(any()));

      fix.complete(
        const GpsCaptureResult.success(
          latitude: 9.0,
          longitude: 38.7,
          accuracy: 12,
        ),
      );
      await tester.pumpAndSettle();
      expect(confirmEnabled(tester), isTrue);
      await tapConfirm(tester, 'Confirm Pickup');

      final pickup = sentPickup();
      expect(pickup.gpsPickup?.latitude, 9.0);
      expect(pickup.gpsPickup?.longitude, 38.7);
      expect(pickup.scannedQrNonce, 'nonce-1');
    });

    testWidgets('no fix: a scanned pickup still confirms without one',
        (tester) async {
      await scanValidCode(tester, VerificationType.pickup);

      expect(confirmEnabled(tester), isTrue);
      await tapConfirm(tester, 'Confirm Pickup');

      final pickup = sentPickup();
      expect(pickup.gpsPickup, isNull);
      expect(pickup.scannedQrNonce, 'nonce-1');
    });

    testWidgets('a capture that fails ends the wait', (tester) async {
      when(() => gps.captureCurrentPosition())
          .thenAnswer((_) async => throw Exception('permission busy'));
      await scanValidCode(tester, VerificationType.pickup);

      expect(confirmEnabled(tester), isTrue);
      expect(find.text(l10n.deliveryGpsUnavailable), findsOneWidget);
    });

    testWidgets('a capture that never ends stops the wait at the cap',
        (tester) async {
      final never = Completer<GpsCaptureResult>();
      when(() => gps.captureCurrentPosition()).thenAnswer((_) => never.future);
      await scanWithoutSettling(tester);
      expect(confirmEnabled(tester), isFalse);

      await tester.pump(QrScanScreen.gpsWaitCap);
      await tester.pumpAndSettle();

      expect(confirmEnabled(tester), isTrue);
      expect(find.text(l10n.deliveryGpsUnavailable), findsOneWidget);
    });
  });

  // The server refuses a self-pickup confirm without a fix, so the screen
  // waits for one, says why there is none and offers a retry.
  group('a self-pickup waits for a fix (FUP-138)', () {
    const panel = Key('qr_location_needed');
    const retryKey = Key('qr_retry_gps');
    const settingsKey = Key('qr_location_settings');

    Delivery selfPickup() => _delivery()
      ..deliveryType = DeliveryConstants.selfPickupDeliveryType
      ..truckerId = ''
      ..truckerName = ''
      ..status = DeliveryStatus.pending;

    void noFix(String reason) => when(() => gps.captureCurrentPosition())
        .thenAnswer((_) async => GpsCaptureResult.unavailable(reason));

    void fixComes() => when(() => gps.captureCurrentPosition()).thenAnswer(
          (_) async => const GpsCaptureResult.success(
            latitude: 9.0,
            longitude: 38.7,
            accuracy: 12,
          ),
        );

    Future<void> scanSelfPickup(WidgetTester tester) => scanValidCode(
          tester,
          VerificationType.delivery,
          subject: selfPickup(),
        );

    bool confirmEnabled(WidgetTester tester) =>
        tester
            .widget<FilledButton>(find.byKey(const Key('qr_confirm')))
            .onPressed !=
        null;

    String? reasonText(WidgetTester tester) =>
        tester.widget<Text>(find.byKey(const Key('qr_location_reason'))).data;

    Future<void> tapRetry(WidgetTester tester) async {
      await tester.ensureVisible(find.byKey(retryKey));
      await tester.tap(find.byKey(retryKey));
      await tester.pumpAndSettle();
    }

    final reasons = {
      GpsCaptureResult.locationOffReason: l10n.checkInNeedsLocation,
      GpsCaptureResult.permissionDeniedReason: l10n.deliveryGpsPermissionDenied,
      GpsCaptureResult.permanentlyDeniedReason: l10n.checkInLocationBlocked,
      GpsCaptureResult.noSignalReason: l10n.qrLocationNoSignal,
    };

    for (final MapEntry(key: reason, value: text) in reasons.entries) {
      testWidgets('no fix ($reason): Confirm stays off and says why',
          (tester) async {
        noFix(reason);
        await scanSelfPickup(tester);

        expect(find.text(l10n.qrSelfPickupNeedsGps), findsOneWidget);
        expect(reasonText(tester), text);
        expect(find.byKey(retryKey), findsOneWidget);
        expect(
          find.byKey(settingsKey),
          reason == GpsCaptureResult.permanentlyDeniedReason
              ? findsOneWidget
              : findsNothing,
          reason: 'only a permission denied for good needs the settings',
        );
        expect(find.text(l10n.deliveryGpsUnavailable), findsNothing);
        expect(confirmEnabled(tester), isFalse);

        await tester.tap(find.byKey(const Key('qr_confirm')));
        await tester.pump();
        expect(gate.prompts, 0);
        verifyNever(() => bloc.add(any()));
      });
    }

    testWidgets('denied for good: Open settings opens the app settings',
        (tester) async {
      noFix(GpsCaptureResult.permanentlyDeniedReason);
      when(() => gps.openAppSettings()).thenAnswer((_) async => true);
      await scanSelfPickup(tester);

      await tester.ensureVisible(find.byKey(settingsKey));
      await tester.tap(find.byKey(settingsKey));
      await tester.pump();

      verify(() => gps.openAppSettings()).called(1);
      verifyNever(() => bloc.add(any()));
    });

    testWidgets('Try location again, then Confirm sends the fix',
        (tester) async {
      noFix(GpsCaptureResult.locationOffReason);
      await scanSelfPickup(tester);
      expect(confirmEnabled(tester), isFalse);

      fixComes();
      await tapRetry(tester);

      expect(find.byKey(panel), findsNothing);
      expect(confirmEnabled(tester), isTrue);
      await tapConfirm(tester, l10n.deliveryConfirmDelivery);

      expect(gate.prompts, 1, reason: 'the device lock still comes first');
      final sent = verify(() => bloc.add(captureAny())).captured.single
          as ConfirmDelivery;
      expect(sent.gpsDropoff?.latitude, 9.0);
      expect(sent.gpsDropoff?.longitude, 38.7);
      expect(sent.scannedQrNonce, 'nonce-1');
      verify(() => gps.captureCurrentPosition()).called(2);
    });

    testWidgets('a retry still without a fix says the new reason',
        (tester) async {
      noFix(GpsCaptureResult.permissionDeniedReason);
      await scanSelfPickup(tester);
      expect(reasonText(tester), l10n.deliveryGpsPermissionDenied);

      noFix(GpsCaptureResult.noSignalReason);
      await tapRetry(tester);

      expect(reasonText(tester), l10n.qrLocationNoSignal);
      expect(confirmEnabled(tester), isFalse);
    });

    testWidgets('a capture that never ends: at the cap Confirm stays off',
        (tester) async {
      final never = Completer<GpsCaptureResult>();
      when(() => gps.captureCurrentPosition()).thenAnswer((_) => never.future);
      await pumpScreen(
        tester,
        VerificationType.delivery,
        subject: selfPickup(),
      );
      tester.widget<MobileScanner>(find.byType(MobileScanner)).onDetect!(
        const BarcodeCapture(barcodes: [Barcode(rawValue: 'qr-token')]),
      );
      // Not pumpAndSettle: the location spinner keeps animating.
      await tester.pump();
      await tester.pump();
      expect(confirmEnabled(tester), isFalse);

      await tester.pump(QrScanScreen.gpsWaitCap);
      await tester.pumpAndSettle();

      expect(confirmEnabled(tester), isFalse);
      expect(reasonText(tester), l10n.qrLocationNoSignal);
    });

    testWidgets('a typed self-pickup code says why too', (tester) async {
      noFix(GpsCaptureResult.permissionDeniedReason);
      await pumpScreen(
        tester,
        VerificationType.delivery,
        subject: selfPickup(),
      );
      await tester.tap(find.byKey(const Key('qr_type_code')));
      await tester.pumpAndSettle();
      expect(find.text(l10n.qrTypeCodeSelfPickupHint), findsOneWidget);
      await tester.enterText(find.byKey(const Key('qr_code_field')), typedCode);
      await tester.tap(find.byKey(const Key('qr_code_check')));
      await tester.pumpAndSettle();

      expect(reasonText(tester), l10n.deliveryGpsPermissionDenied);
      expect(find.text(l10n.qrTypedCodeNeedsGps), findsNothing);
      expect(confirmEnabled(tester), isFalse);
    });

    group('on web', () {
      Future<void> pumpWebView(
        WidgetTester tester,
        GpsUnavailableCause cause, {
        List<String>? taps,
      }) =>
          tester.pumpWidget(
            MaterialApp(
              localizationsDelegates: AppLocalizations.localizationsDelegates,
              supportedLocales: AppLocalizations.supportedLocales,
              locale: const Locale('en'),
              home: Scaffold(
                body: QrLocationNeededView(
                  cause: cause,
                  onRetry: () => taps?.add('retry'),
                ),
              ),
            ),
          );

      testWidgets('denied for good names the site settings, no button',
          (tester) async {
        final taps = <String>[];
        await pumpWebView(
          tester,
          GpsUnavailableCause.deniedForever,
          taps: taps,
        );

        expect(find.text(l10n.qrSelfPickupNeedsGps), findsOneWidget);
        expect(find.text(l10n.checkInLocationBlockedWeb), findsOneWidget);
        expect(find.text(l10n.checkInLocationBlocked), findsNothing);
        expect(find.byKey(settingsKey), findsNothing);
        await tester.tap(find.byKey(retryKey));
        expect(taps, ['retry']);
      });

      for (final (cause, text) in [
        (
          GpsUnavailableCause.permissionDenied,
          l10n.deliveryGpsPermissionDenied
        ),
        (GpsUnavailableCause.noSignal, l10n.qrLocationNoSignal),
      ]) {
        testWidgets('$cause reads as on the phone', (tester) async {
          await pumpWebView(tester, cause);

          expect(find.text(text), findsOneWidget);
          expect(find.byKey(settingsKey), findsNothing);
          expect(find.byKey(retryKey), findsOneWidget);
        });
      }
    });
  });

  // Only the self-pickup changed: a scanned trucker pickup or drop-off still
  // confirms without a fix (GPS is optional evidence there).
  group('the other legs keep GPS optional (FUP-138)', () {
    testWidgets('a buyer at a trucker\'s drop-off confirms without a fix',
        (tester) async {
      when(() => gps.captureCurrentPosition()).thenAnswer(
        (_) async => const GpsCaptureResult.unavailable(
          GpsCaptureResult.permanentlyDeniedReason,
        ),
      );
      await scanValidCode(tester, VerificationType.delivery);

      expect(find.byKey(const Key('qr_location_needed')), findsNothing);
      expect(find.text(l10n.deliveryGpsUnavailable), findsOneWidget);
      await tapConfirm(tester, l10n.deliveryConfirmDelivery);

      final sent = verify(() => bloc.add(captureAny())).captured.single
          as ConfirmDelivery;
      expect(sent.gpsDropoff, isNull);
      expect(sent.scannedQrNonce, 'nonce-1');
    });

    testWidgets('a trucker\'s pickup confirms without a fix', (tester) async {
      when(() => gps.captureCurrentPosition()).thenAnswer(
        (_) async => const GpsCaptureResult.unavailable(
          GpsCaptureResult.locationOffReason,
        ),
      );
      await scanValidCode(tester, VerificationType.pickup);

      expect(find.byKey(const Key('qr_location_needed')), findsNothing);
      await tapConfirm(tester, l10n.deliveryConfirmPickup);

      final sent =
          verify(() => bloc.add(captureAny())).captured.single as ConfirmPickup;
      expect(sent.gpsPickup, isNull);
    });
  });
}
