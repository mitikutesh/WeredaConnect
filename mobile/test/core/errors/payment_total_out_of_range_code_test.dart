import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// FUP-22: the live gateway's refusal of a payment total one Telebirr payment
/// cannot take has its own code (`ORDER_PAYMENT_TOTAL_OUT_OF_RANGE`,
/// sync-protocol.md 1.24.0). The client matches the code, and for one more
/// release the English text older servers send under `ORDER_VALIDATION`.
void main() {
  const newMessage = 'ORDER_PAYMENT_TOTAL_OUT_OF_RANGE: This order total '
      '(50.00 ETB) is outside what one Telebirr payment can take '
      '(100 to 500000 ETB).';
  const oldMessage = 'ORDER_VALIDATION: This order total (50.00 ETB) is '
      'outside what one Telebirr payment can take (100 to 500000 ETB).';

  group('isPaymentTotalOutOfRangeMessage', () {
    test('matches the dedicated code, whatever text follows it', () {
      expect(isPaymentTotalOutOfRangeMessage(newMessage), isTrue);
      expect(
        isPaymentTotalOutOfRangeMessage(
          '$orderPaymentTotalOutOfRangeCode: anything',
        ),
        isTrue,
      );
    });

    test('still matches the old servers\' ORDER_VALIDATION text', () {
      expect(isPaymentTotalOutOfRangeMessage(oldMessage), isTrue);
    });

    test('does not match other refusals', () {
      for (final message in [
        null,
        '',
        'ORDER_VALIDATION: Invalid status transition.',
        'ORDER_UNAUTHORIZED: outside what one Telebirr payment can take',
        'ORDER_PAYMENT_METHOD_UNAVAILABLE: Bank transfer (cbe_transfer) is '
            'not available yet. Pay with Telebirr.',
        'ORDER_KYC_REQUIRED: buyer_fayda: Verify',
        'This order total is outside what one Telebirr payment can take',
      ]) {
        expect(
          isPaymentTotalOutOfRangeMessage(message),
          isFalse,
          reason: '$message',
        );
      }
    });

    test('is permanent and reverted like every ORDER_* refusal', () {
      expect(isRevertableRejectionMessage(newMessage), isTrue);
      expect(isKycRequiredMessage(newMessage), isFalse);
    });
  });

  group('web: /sync refusal to the payment screen', () {
    late _MockApiClient api;
    late WebOrderRepository repo;

    setUp(() {
      api = _MockApiClient();
      repo = WebOrderRepository(api: api);
      when(() => api.get<Map<String, dynamic>>('/orders/ord-1')).thenAnswer(
        (_) async => {
          'order': {
            'orderId': 'ord-1',
            'listingId': 'listing-1',
            'buyerId': 'buyer-1',
            'farmerId': 'farmer-1',
            'buyerName': 'Buyer',
            'farmerName': 'Farmer',
            'cropType': 'teff',
            'quantity': 1.0,
            'pricePerUnit': 50.0,
            'totalPrice': 50.0,
            'status': 'pending_payment',
            'createdAt': '2026-10-23T08:00:00Z',
            'version': 2,
          },
        },
      );
    });

    void refuseWith(String message) {
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'results': [
            {'operationId': 'op-1', 'status': 'error', 'message': message},
          ],
        },
      );
    }

    Future<List<OrderState>> submit() async {
      final bloc = OrderBloc(repository: repo);
      final states = <OrderState>[];
      final sub = bloc.stream.listen(states.add);
      bloc.add(
        const SubmitPayment(orderId: 'ord-1', paymentMethod: 'telebirr'),
      );
      final deadline = DateTime.now().add(const Duration(seconds: 5));
      while (states.length < 2 && DateTime.now().isBefore(deadline)) {
        await Future<void>.delayed(const Duration(milliseconds: 5));
      }
      await sub.cancel();
      await bloc.close();
      return states;
    }

    for (final (label, message) in [
      ('the dedicated code', newMessage),
      ('an old server\'s ORDER_VALIDATION text', oldMessage),
    ]) {
      test('$label ends in paymentTotalOutOfRange, never success', () async {
        refuseWith(message);

        await expectLater(
          repo.submitPayment('ord-1', null, paymentMethod: 'telebirr'),
          throwsA(
            isA<SyncRejectedException>()
                .having((e) => e.message, 'message', message),
          ),
        );

        final states = await submit();
        expect(states, hasLength(2));
        expect(states.first, isA<OrderLoading>());
        expect(
          states.last,
          isA<OrderError>().having(
            (s) => s.code,
            'code',
            AppErrorCode.paymentTotalOutOfRange,
          ),
        );
      });
    }
  });

  group('mobile: the sync queue rejection snackbar', () {
    test('an order payment refused with the code has its own text', () {
      for (final message in [newMessage, oldMessage]) {
        expect(
          syncRejectionErrorCode(
            entityType: 'order',
            action: 'update',
            message: message,
          ),
          AppErrorCode.paymentTotalOutOfRange,
          reason: message,
        );
      }
    });

    test('a refused create still reads as not placed', () {
      expect(
        syncRejectionErrorCode(
          entityType: 'order',
          action: 'create',
          message: newMessage,
        ),
        AppErrorCode.orderNotPlaced,
      );
    });
  });

  test('the code is explained in en, am and om', () async {
    for (final locale in ['en', 'am', 'om']) {
      final l10n = await AppLocalizations.delegate.load(Locale(locale));
      final text = localizeError(const SyncRejectedException(newMessage), l10n);
      expect(text, l10n.paymentTotalOutOfRange, reason: locale);
      expect(text, isNot(l10n.errorOrderRefused), reason: locale);
    }
  });
}
