import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/models/order_dropoff_check_in.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockApiClient extends Mock implements ApiClient {}

const _deliveryId = '6f1c2a4e-1111-4b2c-9d3e-0a1b2c3d4e5f';
const _photoKey = 'uploads/photos/$_deliveryId/arrival-1700000.jpg';
const _fix =
    HandoffCheckInFix(latitude: 9.031, longitude: 38.74, accuracyM: 20);

/// ESC-36 on web (FUP-336): the order reads the drop-off judgement; the
/// arrival photo the browser uploaded is attached first, then the drop-off
/// check-in goes at once with exactly the server's shape; each refusal comes
/// back with its own drop-off code and text.
void main() {
  final en = lookupAppLocalizations(const Locale('en'));
  late _MockApiClient api;
  late WebOrderRepository repo;
  late List<Map<String, dynamic>> batches;
  late Map<String, Map<String, dynamic>> answers;

  Map<String, dynamic> orderDto({
    String? judgement = 'pending',
    Duration deadlineIn = const Duration(days: 1),
    Map<String, dynamic>? checkIns,
  }) =>
      {
        'orderId': 'ord-1',
        'listingId': 'listing-1',
        'buyerId': 'buyer-1',
        'farmerId': 'farmer-1',
        'buyerName': 'Buyer',
        'farmerName': 'Farmer',
        'cropType': 'teff',
        'quantity': 500.0,
        'pricePerUnit': 60.0,
        'status': 'in_transit',
        'createdAt': '2026-10-30T08:00:00Z',
        'version': 6,
        'fulfillment': 'trucker',
        'tripId': 'trip-1',
        'truckerId': 'trucker-1',
        if (judgement != null) 'dropoffJudgement': judgement,
        'dropoffDeadlineAt':
            DateTime.now().toUtc().add(deadlineIn).toIso8601String(),
        if (checkIns != null) 'checkIns': checkIns,
      };

  Map<String, dynamic> opOf(Map<String, dynamic> batch) =>
      (batch['operations'] as List).single as Map<String, dynamic>;

  void serverHas(Map<String, dynamic> order) =>
      when(() => api.get<Map<String, dynamic>>('/orders/ord-1'))
          .thenAnswer((_) async => {'order': order});

  Future<bool> checkIn({String userId = 'trucker-1', String? photo}) =>
      repo.checkInAtDropoff(
        'ord-1',
        userId,
        deliveryId: _deliveryId,
        fix: _fix,
        arrivalPhotoRef: photo,
      );

  setUp(() {
    api = _MockApiClient();
    repo = WebOrderRepository(api: api);
    batches = [];
    answers = {};
    when(
      () => api.post<Map<String, dynamic>>(
        '/sync',
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer((invocation) async {
      final batch = invocation.namedArguments[#data] as Map<String, dynamic>;
      batches.add(batch);
      final op = opOf(batch);
      return {
        'results': [
          {
            'operationId': op['operationId'],
            ...answers[op['entityType']] ??
                const {'status': 'success', 'serverVersion': 6},
          },
        ],
      };
    });
  });

  test('reads the drop-off judgement, T8, the reason and the check-in',
      () async {
    serverHas(
      orderDto(
        judgement: 'silent_buyer',
        checkIns: {
          'arrived_dropoff': {
            'receivedAt': '2026-11-22T06:10:00Z',
            'distanceChecked': false,
            'late': false,
          },
        },
      )
        ..['dropoffJudgedAt'] = '2026-11-24T06:11:00Z'
        ..['disputeReason'] = 'silent_buyer',
    );

    final order = (await repo.getOrderById('ord-1'))!;

    expect(order.dropoffJudgement, DropoffJudgementOutcome.silentBuyer);
    expect(order.dropoffJudgedAt, DateTime.utc(2026, 11, 24, 6, 11));
    expect(order.dropoffDeadlineAt, isNotNull);
    expect(order.disputeReason, 'silent_buyer');
    expect(dropoffCheckInOf(order)!.distanceChecked, isFalse);
  });

  test('the photo is attached first, then the check-in goes', () async {
    serverHas(orderDto());

    expect(await checkIn(photo: _photoKey), isTrue);

    expect(batches, hasLength(2));
    final photo = opOf(batches[0]);
    expect(photo['entityType'], 'delivery_photo');
    expect(photo['entityId'], '$_deliveryId#arrival');
    expect(photo['action'], 'create');
    expect(photo['data'], {'photoRef': _photoKey});
    final op = opOf(batches[1]);
    expect(op['entityType'], 'handoff_check_in');
    expect(op['entityId'], 'ord-1#arrived_dropoff');
    expect(op['action'], 'create');
    final data = op['data'] as Map<String, dynamic>;
    expect(data.keys.toSet(), {
      'orderId',
      'kind',
      'capturedAt',
      'latitude',
      'longitude',
      'accuracyM',
    });
    expect(data['kind'], 'arrived_dropoff');
    expect(data['longitude'], 38.74);
  });

  test('a delivery with its photo sends the check-in alone', () async {
    serverHas(orderDto());

    await checkIn();

    expect(
      batches.single['operations'].single['entityType'],
      'handoff_check_in',
    );
  });

  test('a refused or deferred attach still lets the server decide', () async {
    serverHas(orderDto());
    for (final answer in [
      {
        'status': 'error',
        'message': 'DELIVERY_PHOTO_EXISTS: This handoff already has its photo.',
      },
      {
        'status': 'deferred',
        'message': 'DELIVERY_PHOTO_PENDING: Photo storage is not reachable.',
      },
    ]) {
      batches.clear();
      answers['delivery_photo'] = answer;

      expect(await checkIn(photo: _photoKey), isTrue);

      expect(
        [for (final batch in batches) opOf(batch)['entityType']],
        ['delivery_photo', 'handoff_check_in'],
      );
    }
  });

  test('a repeat sends nothing; the guard refuses with drop-off texts',
      () async {
    serverHas(
      orderDto(
        checkIns: {
          'arrived_dropoff': {
            'receivedAt': '2026-11-22T06:10:00Z',
            'distanceChecked': true,
            'late': false,
          },
        },
      ),
    );
    expect(await checkIn(photo: _photoKey), isFalse);

    serverHas(orderDto());
    await expectLater(
      checkIn(userId: 'buyer-1'),
      throwsA(
        isA<CheckInNotAllowedException>().having(
          (e) => e.code,
          'code',
          AppErrorCode.dropoffCheckInNotParty,
        ),
      ),
    );
    serverHas(orderDto(deadlineIn: const Duration(minutes: -1)));
    await expectLater(
      checkIn(),
      throwsA(
        isA<CheckInNotAllowedException>().having(
          (e) => e.code,
          'code',
          AppErrorCode.dropoffCheckInClosed,
        ),
      ),
    );
    expect(batches, isEmpty);
  });

  group('each refusal has its drop-off code and text', () {
    const tooFar = 'CHECKIN_TOO_FAR: You are 7.2 km from the drop-off '
        '(at most 5 km).';
    final cases = <String, (AppErrorCode, String)>{
      'CHECKIN_PHOTO_REQUIRED: Take the arrival photo first.': (
        AppErrorCode.checkInPhotoRequired,
        en.errorCheckInPhotoRequired,
      ),
      tooFar: (
        AppErrorCode.dropoffCheckInTooFar,
        en.errorDropoffCheckInTooFar,
      ),
      '$tooFar The attempt was kept for review.': (
        AppErrorCode.dropoffCheckInTooFarKept,
        en.errorDropoffCheckInTooFarKept,
      ),
      'CHECKIN_TOO_LATE: The delivery deadline passed. It was kept for '
          'review.': (
        AppErrorCode.dropoffCheckInTooLate,
        en.errorDropoffCheckInTooLate,
      ),
      'CHECKIN_NOT_OPEN: This delivery is no longer on the road.': (
        AppErrorCode.dropoffCheckInNotOpen,
        en.errorDropoffCheckInNotOpen,
      ),
      "CHECKIN_NOT_PARTY: Only the order's trucker checks in.": (
        AppErrorCode.dropoffCheckInNotParty,
        en.errorDropoffCheckInNotParty,
      ),
    };

    for (final MapEntry(key: message, value: (code, text)) in cases.entries) {
      test(message.substring(0, message.indexOf(':')), () async {
        serverHas(orderDto());
        answers['handoff_check_in'] = {'status': 'error', 'message': message};

        final error = await checkIn().then<Object?>(
          (_) => null,
          onError: (Object e) => e,
        );

        expect(error, isA<SyncRejectedException>());
        final mapped = checkInErrorCode(
          (error! as SyncRejectedException).message,
          kind: CheckInKind.arrivedDropoff,
        );
        expect(mapped, code);
        expect(localizeErrorCode(mapped, en), text);
      });
    }
  });
}
