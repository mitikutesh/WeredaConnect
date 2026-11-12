import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/web_escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/escrow_error_l10n.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockApiClient extends Mock implements ApiClient {}

const _refundScheduled =
    'A refund to the buyer is scheduled for this escrow; it can no longer be '
    'disputed.';

/// FUP-70 and FUP-77: which escrow failures are permanent, the code a failure
/// is stored under, and the server's own code on 502 and 503 (web repository;
/// the mobile twin is covered in escrow_repository_test.dart).
void main() {
  group('isPermanentEscrowRefusal', () {
    test('400 and 422 are permanent, with or without a code', () {
      for (final e in <ApiException>[
        const ValidationException('bad'),
        const ValidationException('bad', 400, 'ESCROW_001'),
        const ServerException('refused', 422),
        const ServerException('refused', 422, 'ESCROW_039'),
      ]) {
        expect(isPermanentEscrowRefusal(e), isTrue, reason: '$e ${e.code}');
      }
    });

    test('another 4xx is permanent only with an escrow code', () {
      expect(
        isPermanentEscrowRefusal(
          const AuthException('not the buyer', 403, 'ESCROW_026'),
        ),
        isTrue,
      );
      // A 404 is never permanent: the delivery of a payment queued offline
      // may not have reached the server yet.
      expect(
        isPermanentEscrowRefusal(
          const NotFoundException('no delivery', 404, 'ESCROW_008'),
        ),
        isFalse,
      );
      expect(
        isPermanentEscrowRefusal(const AuthException('forbidden', 403)),
        isFalse,
      );
      expect(
        isPermanentEscrowRefusal(const NotFoundException('gone')),
        isFalse,
      );
    });

    test('401, 408, 429, 5xx and network errors stay retryable', () {
      for (final e in <ApiException>[
        const AuthException('expired', 401, 'ESCROW_005'),
        const ServerException('timeout', 408, 'ESCROW_012'),
        const RateLimitException('slow down', 429, 'ESCROW_011'),
        const ServerException('boom', 500, 'ESCROW_012'),
        const ServerException('gateway', 502, 'ESCROW_013'),
        const ServerException('read failed', 503, 'ESCROW_012'),
        const NetworkException(),
        const TimeoutException('slow'),
      ]) {
        expect(isPermanentEscrowRefusal(e), isFalse, reason: '$e ${e.code}');
      }
    });
  });

  group('escrowFailureCodeOf', () {
    test('the server code wins on every status', () {
      expect(
        escrowFailureCodeOf(const ServerException('x', 422, 'ESCROW_039')),
        'ESCROW_039',
      );
      expect(
        escrowFailureCodeOf(const ServerException('x', 503, 'ESCROW_012')),
        'ESCROW_012',
      );
      expect(
        escrowFailureCodeOf(
          const AuthException('held', 403, fraudAccountHeldCode),
        ),
        fraudAccountHeldCode,
      );
    });

    test('without a server code the kind of failure picks one', () {
      expect(
        escrowFailureCodeOf(const ServerException('Bad Gateway', 502)),
        'ESCROW_013',
      );
      expect(
        escrowFailureCodeOf(const ServerException('Unavailable', 503)),
        'ESCROW_014',
      );
      expect(escrowFailureCodeOf(const NetworkException()), 'ESCROW_OFFLINE');
      // Online but slow: not the offline text.
      expect(
        escrowFailureCodeOf(const TimeoutException('slow')),
        'ESCROW_CLIENT',
      );
      expect(escrowFailureCodeOf(const RateLimitException()), 'ESCROW_011');
      expect(
        escrowFailureCodeOf(const AuthException('expired')),
        'ESCROW_005',
      );
      expect(
        escrowFailureCodeOf(const ServerException('boom', 500)),
        'ESCROW_CLIENT',
      );
      expect(escrowFailureCodeOf(const FormatException('x')), 'ESCROW_CLIENT');
    });

    test('a code from another family is not kept', () {
      expect(
        escrowFailureCodeOf(const ServerException('x', 500, 'ORDER_001')),
        'ESCROW_CLIENT',
      );
    });

    test('every stored code has a localized text, never the raw code',
        () async {
      for (final locale in ['en', 'am', 'om']) {
        final l10n = await AppLocalizations.delegate.load(Locale(locale));
        for (final code in [
          'ESCROW_OFFLINE',
          'ESCROW_011',
          'ESCROW_013',
          'ESCROW_014',
          'ESCROW_005',
          'ESCROW_CLIENT',
          'ESCROW_039',
        ]) {
          final text = localizeEscrowError(l10n, code, 'raw server text');
          expect(text.trim(), isNotEmpty, reason: '$locale $code');
          expect(text, isNot(contains('ESCROW_')), reason: '$locale $code');
          expect(text, isNot('raw server text'), reason: '$locale $code');
        }
      }
    });
  });

  group('a dispute refused because a refund is scheduled (FUP-77)', () {
    test('escrowCodeOf reads it from the server text', () {
      expect(
        escrowCodeOf(
          const ConflictException(
            _refundScheduled,
            409,
            'ESCROW_INVALID_TRANSITION',
          ),
          'ESCROW_009',
        ),
        escrowDisputeRefundScheduledCode,
      );
    });

    test('any other invalid transition keeps the shared code', () {
      expect(
        escrowCodeOf(
          const ConflictException(
            'A settlement is in progress for this escrow; it can no longer '
                'be disputed.',
            409,
            'ESCROW_INVALID_TRANSITION',
          ),
          'ESCROW_009',
        ),
        'ESCROW_INVALID_TRANSITION',
      );
      // The wording alone, under another code, changes nothing.
      expect(
        escrowCodeOf(
          const ConflictException(_refundScheduled, 409, 'ESCROW_038'),
          'ESCROW_009',
        ),
        'ESCROW_038',
      );
    });

    test('has its own text in en, am and om', () async {
      for (final locale in ['en', 'am', 'om']) {
        final l10n = await AppLocalizations.delegate.load(Locale(locale));
        final text =
            localizeEscrowError(l10n, escrowDisputeRefundScheduledCode, 'raw');
        expect(text, l10n.escrowErrorDisputeRefundScheduled, reason: locale);
        expect(text.trim(), isNotEmpty, reason: locale);
        expect(text, isNot(l10n.escrowErrorInvalidTransition), reason: locale);
        expect(text, isNot(l10n.escrowErrorGeneric), reason: locale);
      }
    });
  });

  group('WebEscrowRepository', () {
    late _MockApiClient api;
    late WebEscrowRepository repo;

    setUp(() {
      api = _MockApiClient();
      repo = WebEscrowRepository(api: api);
    });

    void answer(String path, Object error) {
      when(
        () => api.post<Map<String, dynamic>>(
          path,
          data: any<dynamic>(named: 'data'),
        ),
      ).thenThrow(error);
    }

    Future<EscrowError> initiate() async => await repo.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000,
        ) as EscrowError;

    Future<EscrowError> dispute() async => await repo.raiseDispute(
          escrowId: 'esc-001',
          disputedBy: 'farmer-001',
          reason: 'damaged_goods',
        ) as EscrowError;

    test('503 ESCROW_012 keeps the server code, not the Telebirr text',
        () async {
      answer(
        '/escrow/dispute',
        const ServerException('read failed', 503, 'ESCROW_012'),
      );

      expect((await dispute()).code, 'ESCROW_012');
    });

    test('502 and 503 with a server code keep it on initiate', () async {
      for (final (status, code) in [
        (502, 'ESCROW_012'),
        (502, 'ESCROW_013'),
        (503, 'ESCROW_012'),
        (503, 'ESCROW_014'),
      ]) {
        answer('/escrow/initiate', ServerException('x', status, code));

        expect((await initiate()).code, code, reason: '$status $code');
      }
    });

    test('502 and 503 without a code fall back to the gateway codes', () async {
      answer('/escrow/initiate', const ServerException('Bad Gateway', 502));
      expect((await initiate()).code, 'ESCROW_013');

      answer('/escrow/initiate', const ServerException('Unavailable', 503));
      expect((await initiate()).code, 'ESCROW_014');

      // A code from another family is not an escrow code.
      answer('/escrow/initiate', const ServerException('x', 503, 'SYNC_001'));
      expect((await initiate()).code, 'ESCROW_014');
    });

    test('another 5xx without a code still reads ESCROW_SERVER', () async {
      answer('/escrow/initiate', const ServerException('boom', 500));

      expect((await initiate()).code, 'ESCROW_SERVER');
    });

    test('a dispute on a scheduled refund gets the refund code', () async {
      answer(
        '/escrow/dispute',
        const ConflictException(
          _refundScheduled,
          409,
          'ESCROW_INVALID_TRANSITION',
        ),
      );

      expect((await dispute()).code, escrowDisputeRefundScheduledCode);
    });
  });
}
