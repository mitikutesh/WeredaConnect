import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/widgets/security_prompt_listener.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/payment_instructions_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockApiClient extends Mock implements ApiClient {}

class _MockAuthBloc extends Mock implements AuthBloc {}

// Server texts (KycPolicy.cs), with the reason token of sync-protocol 1.5.0.
const _buyerFayda = 'ORDER_KYC_REQUIRED: buyer_fayda: '
    'Verify your Fayda ID to pay more than 10,000 ETB.';
const _buyerPhone =
    'ORDER_KYC_REQUIRED: buyer_phone: Verify your phone number before paying.';
const _farmerFayda = 'ORDER_KYC_REQUIRED: farmer_fayda: '
    'The farmer must verify their Fayda ID for orders above 10,000 ETB.';
const _farmerPhone = 'ORDER_KYC_REQUIRED: farmer_phone: '
    "The farmer's phone number is not verified yet.";
const _unavailable = 'ORDER_KYC_REQUIRED: unavailable: '
    'Identity verification is unavailable right now. Please try again later.';

// A reason token a later server may add; this build does not know it.
const _unknownToken = 'ORDER_KYC_REQUIRED: trucker_fayda: '
    'The trucker must verify their Fayda ID.';
const _unknownReasonText = 'an identity check is still missing';

// Servers older than 1.5.0 sent no token: the wording names the party.
const _legacyFarmerFayda =
    'ORDER_KYC_REQUIRED: The farmer must verify their Fayda ID.';
const _legacyFarmerPhone =
    "ORDER_KYC_REQUIRED: The farmer's phone number is not verified yet.";
const _legacyBuyer = 'ORDER_KYC_REQUIRED: Verify your Fayda ID.';
const _bare = 'ORDER_KYC_REQUIRED';

// English texts the buyer sees (app_en.arb).
const _farmerFaydaText = 'need the farmer to have a verified Fayda ID';
const _farmerPhoneText = "The farmer's phone number is not verified yet";
const _unavailableText = 'could not check identity verification';
const _askBuyerToVerify = 'Verify with Fayda';

/// SIM-49: a payment the KYC gate refused tells the buyer to verify with
/// Fayda only when the buyer is the one who must. When the farmer must (or
/// the check was unavailable) every surface names the farmer (or says try
/// later) and offers the buyer no Fayda action, on web (/sync answers the
/// payment screen directly) and on mobile (the sync queue raises the event).
void main() {
  group('the error code says whose verification is missing', () {
    for (final (message, code) in [
      (_buyerFayda, AppErrorCode.kycRequired),
      // FUP-83: the phone refusal has its own code and text.
      (_buyerPhone, AppErrorCode.kycPhoneRequired),
      // FUP-83: a token this build does not know only explains.
      (_unknownToken, AppErrorCode.kycUnknownReason),
      (_farmerFayda, AppErrorCode.kycFarmerRequired),
      (_farmerPhone, AppErrorCode.kycFarmerRequired),
      (_unavailable, AppErrorCode.kycUnavailable),
      (_legacyFarmerFayda, AppErrorCode.kycFarmerRequired),
      (_legacyFarmerPhone, AppErrorCode.kycFarmerRequired),
      (_legacyBuyer, AppErrorCode.kycRequired),
      // Nothing names a party: the contract's default is the buyer.
      (_bare, AppErrorCode.kycRequired),
    ]) {
      test('$message -> ${code.name}', () {
        expect(mapErrorToCode(SyncRejectedException(message)), code);
      });
    }

    test('only the KYC codes count as a KYC refusal', () {
      const kyc = {
        AppErrorCode.kycRequired,
        AppErrorCode.kycPhoneRequired,
        AppErrorCode.kycUnknownReason,
        AppErrorCode.kycFarmerRequired,
        AppErrorCode.kycUnavailable,
      };
      for (final code in AppErrorCode.values) {
        expect(isKycErrorCode(code), kyc.contains(code), reason: code.name);
      }
    });
  });

  group('the localized text', () {
    for (final locale in AppLocalizations.supportedLocales) {
      test('${locale.languageCode}: only the buyer code asks the buyer',
          () async {
        final l10n = await AppLocalizations.delegate.load(locale);

        final farmer = localizeErrorCode(AppErrorCode.kycFarmerRequired, l10n);
        expect(farmer, l10n.kycFarmerNotVerifiedMessage);
        expect(farmer.trim(), isNotEmpty);
        expect(farmer, isNot(l10n.kycRequiredFaydaMessage));
        expect(
          localizeErrorCode(AppErrorCode.kycUnavailable, l10n),
          l10n.kycUnavailableMessage,
        );
        expect(
          localizeErrorCode(AppErrorCode.kycRequired, l10n),
          l10n.kycRequiredFaydaMessage,
        );
        // FUP-83: the phone text is exact, the unknown reason has its own.
        final phone = localizeErrorCode(AppErrorCode.kycPhoneRequired, l10n);
        expect(phone, l10n.kycRequiredPhoneMessage);
        expect(phone, isNot(l10n.kycRequiredFaydaMessage));
        final unknown = localizeErrorCode(AppErrorCode.kycUnknownReason, l10n);
        expect(unknown, l10n.kycUnknownReasonMessage);
        expect(unknown.trim(), isNotEmpty);
        expect(unknown, isNot(l10n.kycRequiredFaydaMessage));
        expect(unknown, isNot(l10n.kycUnavailableMessage));
      });
    }

    test('en: the phone text names the phone, not the 10,000 ETB rule',
        () async {
      final l10n = await AppLocalizations.delegate.load(const Locale('en'));
      final phone = localizeErrorCode(AppErrorCode.kycPhoneRequired, l10n);

      expect(phone, contains('phone number is not verified'));
      expect(phone, isNot(contains('10,000')));
    });

    test('en: the unknown reason text does not ask the buyer to verify',
        () async {
      final l10n = await AppLocalizations.delegate.load(const Locale('en'));
      final unknown = localizeErrorCode(AppErrorCode.kycUnknownReason, l10n);

      expect(unknown, contains(_unknownReasonText));
      expect(unknown, isNot(contains(_askBuyerToVerify)));
    });
  });

  group('the reason token (FUP-83)', () {
    test('known tokens, legacy wording and the bare code read as before', () {
      expect(KycReason.fromMessage(_buyerFayda), KycReason.buyerFayda);
      expect(KycReason.fromMessage(_buyerPhone), KycReason.buyerPhone);
      expect(KycReason.fromMessage(_farmerFayda), KycReason.farmerFayda);
      expect(KycReason.fromMessage(_farmerPhone), KycReason.farmerPhone);
      expect(KycReason.fromMessage(_unavailable), KycReason.unavailable);
      expect(KycReason.fromMessage(_legacyFarmerFayda), KycReason.farmerFayda);
      expect(KycReason.fromMessage(_legacyFarmerPhone), KycReason.farmerPhone);
      expect(KycReason.fromMessage(_legacyBuyer), KycReason.buyerFayda);
      expect(KycReason.fromMessage(_bare), KycReason.buyerFayda);
    });

    test('an unknown token is unknown, whatever its text says', () {
      for (final message in [
        _unknownToken,
        'ORDER_KYC_REQUIRED: trucker_phone: Verify your phone number.',
        'ORDER_KYC_REQUIRED: buyer_address: Verify your Fayda ID.',
        'sync refused: ORDER_KYC_REQUIRED: agent2_fayda: The farmer must.',
      ]) {
        final reason = KycReason.fromMessage(message);
        expect(reason, KycReason.unknown, reason: message);
        expect(reason.buyerCanFix, isFalse, reason: message);
      }
    });

    test('prose before a colon is not a token', () {
      expect(
        KycReason.fromMessage(
          'ORDER_KYC_REQUIRED: Verify your Fayda ID: it is needed above '
          '10,000 ETB.',
        ),
        KycReason.buyerFayda,
      );
    });

    test('en: the farmer text names the farmer, not Fayda for the buyer',
        () async {
      final l10n = await AppLocalizations.delegate.load(const Locale('en'));
      final farmer = localizeErrorCode(AppErrorCode.kycFarmerRequired, l10n);

      expect(farmer, contains('farmer'));
      expect(farmer, isNot(contains(_askBuyerToVerify)));
    });
  });

  group('web: the payment screen gets the refusal from /sync', () {
    late _MockApiClient api;
    late int syncPosts;

    setUp(() {
      api = _MockApiClient();
      syncPosts = 0;
    });

    Order order() => Order()
      ..orderId = 'ord-1'
      ..listingId = 'listing-1'
      ..buyerId = 'buyer-1'
      ..farmerId = 'farmer-1'
      ..cropType = 'teff'
      ..quantity = 1000
      ..pricePerUnit = 94.5
      ..totalPrice = 94500
      ..status = OrderStatus.created
      ..createdAt = DateTime(2026, 10, 23)
      ..version = 1;

    /// Pays through the real web repository and bloc; /sync refuses with
    /// [message]. Returns the bloc so the shared error state can be read.
    Future<OrderBloc> payAndGetRefused(
      WidgetTester tester,
      String message,
    ) async {
      when(() => api.get<Map<String, dynamic>>('/orders/ord-1')).thenAnswer(
        (_) async => {
          'order': {
            'orderId': 'ord-1',
            'listingId': 'listing-1',
            'buyerId': 'buyer-1',
            'farmerId': 'farmer-1',
            'cropType': 'teff',
            'quantity': 1000.0,
            'pricePerUnit': 94.5,
            'totalPrice': 94500.0,
            'status': 'created',
            'createdAt': '2026-10-23T10:00:00Z',
            'version': 1,
          },
        },
      );
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer((_) async {
        syncPosts++;
        return {
          'results': [
            {'status': 'error', 'message': message},
          ],
        };
      });
      final bloc = OrderBloc(repository: WebOrderRepository(api: api));
      addTearDown(bloc.close);

      await tester.pumpWidget(
        BlocProvider<OrderBloc>.value(
          value: bloc,
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: PaymentInstructionsScreen(
              order: order(),
              telebirrLive: false,
              kycVerificationScreen: (_) => const Text('fayda screen'),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      await tester.ensureVisible(find.text('Submit Payment'));
      await tester.tap(find.text('Submit Payment'));
      await tester.pumpAndSettle();
      expect(syncPosts, 1);
      return bloc;
    }

    for (final (label, message, text, code) in [
      (
        'farmer Fayda',
        _farmerFayda,
        _farmerFaydaText,
        AppErrorCode.kycFarmerRequired
      ),
      (
        'farmer phone',
        _farmerPhone,
        _farmerPhoneText,
        AppErrorCode.kycFarmerRequired
      ),
      (
        'legacy farmer wording',
        _legacyFarmerFayda,
        _farmerFaydaText,
        AppErrorCode.kycFarmerRequired,
      ),
      (
        'check unavailable',
        _unavailable,
        _unavailableText,
        AppErrorCode.kycUnavailable
      ),
      (
        'unknown reason token',
        _unknownToken,
        _unknownReasonText,
        AppErrorCode.kycUnknownReason,
      ),
    ]) {
      testWidgets('$label: explains, no Fayda for the buyer, nothing resent',
          (tester) async {
        final bloc = await payAndGetRefused(tester, message);

        expect(find.textContaining(text), findsOneWidget);
        expect(find.byKey(const Key('kyc_verify_button')), findsNothing);
        expect(find.textContaining(_askBuyerToVerify), findsNothing);

        // A surface that renders the shared bloc's error (the Orders tab
        // under this screen) names the same party.
        final state = bloc.state;
        expect(state, isA<OrderError>());
        expect((state as OrderError).code, code);

        await tester.tap(find.byKey(const Key('kyc_ok_button')));
        await tester.pumpAndSettle();

        expect(syncPosts, 1);
        expect(find.byType(AlertDialog), findsNothing);
        expect(find.textContaining(_askBuyerToVerify), findsNothing);
      });
    }

    testWidgets('buyer Fayda: the buyer is offered Fayda', (tester) async {
      final bloc = await payAndGetRefused(tester, _buyerFayda);

      expect(find.byKey(const Key('kyc_verify_button')), findsOneWidget);
      expect(
        find.textContaining('Payments above 10,000 ETB need a verified Fayda'),
        findsOneWidget,
      );
      expect((bloc.state as OrderError).code, AppErrorCode.kycRequired);

      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();
      expect(syncPosts, 1);
    });
  });

  group('mobile: the sync queue raises the refusal in the background', () {
    late _MockAuthBloc auth;
    late SecurityEvents events;

    setUp(() {
      auth = _MockAuthBloc();
      events = SecurityEvents.forTesting();
      when(() => auth.state).thenReturn(
        const AuthAuthenticated(
          userId: 'buyer-1',
          tokens: AuthTokens(
            accessToken: 'a1',
            refreshToken: 'r1',
            idToken: 'i1',
          ),
          role: UserRole.buyer,
          userName: 'Abebe',
          phoneNumber: '+251911234567',
        ),
      );
      when(() => auth.stream).thenAnswer((_) => const Stream.empty());
    });

    /// Mounts the listener, raises the refusal the sync queue would, and
    /// returns the payments it sent again.
    Future<List<KycRequired>> refuse(
      WidgetTester tester,
      String message,
    ) async {
      final resubmitted = <KycRequired>[];
      await tester.pumpWidget(
        BlocProvider<AuthBloc>.value(
          value: auth,
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: Scaffold(
              body: SecurityPromptListener(
                events: events,
                resubmitPayment: (_, event) async => resubmitted.add(event),
                verificationScreen: (_) => const Text('fayda screen'),
                child: const Text('app'),
              ),
            ),
          ),
        ),
      );
      events.emit(
        KycRequired(
          orderId: 'o-1',
          message: message,
          paymentMethod: 'cbe_transfer',
          paymentReference: 'MOCK-CBE-1',
        ),
      );
      await tester.pumpAndSettle();
      return resubmitted;
    }

    for (final (label, message, text) in [
      ('farmer Fayda', _farmerFayda, _farmerFaydaText),
      ('farmer phone', _farmerPhone, _farmerPhoneText),
      ('legacy farmer wording', _legacyFarmerPhone, _farmerPhoneText),
      ('check unavailable', _unavailable, _unavailableText),
      ('unknown reason token', _unknownToken, _unknownReasonText),
    ]) {
      testWidgets('$label: explains, no Fayda for the buyer, nothing resent',
          (tester) async {
        final resubmitted = await refuse(tester, message);

        expect(find.textContaining(text), findsOneWidget);
        expect(find.byKey(const Key('kyc_verify_button')), findsNothing);
        expect(find.textContaining(_askBuyerToVerify), findsNothing);

        await tester.tap(find.byKey(const Key('kyc_ok_button')));
        await tester.pumpAndSettle();

        expect(resubmitted, isEmpty);
        expect(find.byType(AlertDialog), findsNothing);
        expect(find.textContaining(_askBuyerToVerify), findsNothing);
      });
    }

    testWidgets('buyer phone: the buyer is offered Fayda', (tester) async {
      final resubmitted = await refuse(tester, _buyerPhone);

      expect(find.byKey(const Key('kyc_verify_button')), findsOneWidget);
      expect(
        find.textContaining('Your phone number is not verified yet'),
        findsOneWidget,
      );

      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();
      expect(resubmitted, isEmpty);
    });
  });
}
