import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/reputation/data/reputation_cache.dart';
import 'package:wereda_connect/features/reputation/data/reputation_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

class _MockDio extends Mock implements Dio {}

Map<String, dynamic> _me({
  String tier = 'warning',
  int points = 4,
  bool penaltiesEnabled = true,
}) =>
    {
      'reputation': <String, dynamic>{
        'userId': 'f-1',
        'role': 'farmer',
        'tier': tier,
        'activePoints': points,
        'thresholds': {'warning': 3, 'low': 6, 'restricted': 10},
        'ratingAverage': 4.8,
        'ratingCount': 12,
        'ratingDeduction': 0.4,
        'effectiveRating': 4.4,
        'reliabilityPct': 83,
        'completedOrders90d': 10,
        'faultCancels90d': 1,
        'noShows90d': 1,
        'nextPointExpiryAt': '2027-01-20T09:00:00.000Z',
        'penaltiesEnabled': penaltiesEnabled,
        'asOf': '2026-11-07T09:00:00.000Z',
      },
    };

Map<String, dynamic> _event(String id, {bool canAppeal = true}) => {
      'eventId': id,
      'code': 'LATE_CANCEL',
      'points': 4,
      'earnedBack': 1,
      'remainingPoints': 3,
      'status': 'active',
      'occurredAt': '2026-11-01T06:00:00.000Z',
      'recordedAt': '2026-11-01T06:00:04.000Z',
      'pointsExpireAt': '2027-01-30T06:00:00.000Z',
      'orderId': 'ord-1',
      'appeal': {
        'status': 'none',
        'canAppeal': canAppeal,
        if (canAppeal) 'deadlineAt': '2026-11-08T06:00:04.000Z',
      },
    };

/// ESC-31: the signed-in farmer's or trucker's own reputation
/// (reputation-api.md sections 2, 4, 5, 6 and 13).
void main() {
  late _MockApiClient api;
  late _MockDio storage;
  late DateTime now;
  late ReputationRepository repository;

  setUpAll(() {
    registerFallbackValue(Options());
  });

  setUp(() {
    SharedPreferences.setMockInitialValues(<String, Object>{});
    api = _MockApiClient();
    storage = _MockDio();
    now = DateTime.utc(2026, 11, 7, 9);
    repository = ReputationRepository(
      api: api,
      cache: ReputationCache(),
      storage: storage,
      now: () => now,
    );
  });

  void answerMe(Map<String, dynamic> body) =>
      when(() => api.get<Map<String, dynamic>>('/reputation/me'))
          .thenAnswer((_) async => body);

  void failMe([Object? error]) =>
      when(() => api.get<Map<String, dynamic>>('/reputation/me'))
          .thenThrow(error ?? const NetworkException());

  void answerEvents(Map<String, dynamic> body, {String? token}) => when(
        () => api.get<Map<String, dynamic>>(
          '/reputation/me/events',
          queryParameters: {
            'limit': '20',
            if (token != null) 'nextToken': token,
          },
        ),
      ).thenAnswer((_) async => body);

  group('loadOwn', () {
    test('reads the answer and its fields', () async {
      answerMe(_me());

      final own = await repository.loadOwn('f-1');

      expect(own.stale, isFalse);
      expect(own.fetchedAt, now);
      final r = own.reputation;
      expect(r.tier, 'warning');
      expect(r.activePoints, 4);
      expect(r.lowThreshold, 6);
      expect(r.restrictedThreshold, 10);
      expect(r.ratingAverage, 4.8);
      expect(r.ratingCount, 12);
      expect(r.ratingDeduction, 0.4);
      expect(r.effectiveRating, 4.4);
      expect(r.totalOrders90d, 12);
      expect(r.penaltiesEnabled, isTrue);
      expect(r.nextPointExpiryAt, DateTime.utc(2027, 1, 20, 9));
      expect(r.restriction, isNull);
      expect(r.override, isNull);
    });

    test('asks the server at most every 30 minutes', () async {
      answerMe(_me());
      await repository.loadOwn('f-1');

      now = now.add(const Duration(minutes: 29));
      final again = await repository.loadOwn('f-1');
      expect(again.stale, isFalse);
      expect(again.fetchedAt, DateTime.utc(2026, 11, 7, 9));
      verify(() => api.get<Map<String, dynamic>>('/reputation/me')).called(1);

      now = now.add(const Duration(minutes: 2));
      answerMe(_me(points: 5));
      final later = await repository.loadOwn('f-1');
      expect(later.reputation.activePoints, 5);
      verify(() => api.get<Map<String, dynamic>>('/reputation/me')).called(1);
    });

    test('force asks even when the saved answer is fresh', () async {
      answerMe(_me());
      await repository.loadOwn('f-1');
      answerMe(_me(points: 0, tier: 'good'));

      final forced = await repository.loadOwn('f-1', force: true);

      expect(forced.reputation.tier, 'good');
      verify(() => api.get<Map<String, dynamic>>('/reputation/me')).called(2);
    });

    test('offline: the saved answer, marked stale, with its date', () async {
      answerMe(_me());
      await repository.loadOwn('f-1');
      now = now.add(const Duration(hours: 5));
      failMe();

      final own = await repository.loadOwn('f-1');

      expect(own.stale, isTrue);
      expect(own.fetchedAt, DateTime.utc(2026, 11, 7, 9));
      expect(own.reputation.activePoints, 4);
    });

    test('offline with nothing saved: the failure comes through', () async {
      failMe();
      await expectLater(
        repository.loadOwn('f-1'),
        throwsA(isA<NetworkException>()),
      );
    });

    test("another user's saved answer is never read", () async {
      answerMe(_me());
      await repository.loadOwn('f-1');
      failMe();

      await expectLater(
        repository.loadOwn('f-2'),
        throwsA(isA<NetworkException>()),
      );
    });

    test('a restriction and an admin tag are read', () async {
      final body = _me(tier: 'restricted', points: 11);
      (body['reputation'] as Map<String, dynamic>).addAll({
        'restriction': {
          'since': '2026-11-07T09:00:00.000Z',
          'until': '2026-11-21T09:00:00.000Z',
          'cause': 'points',
        },
        'override': {
          'tag': 'low',
          'until': '2026-12-01T00:00:00.000Z',
          'reason': 'Reported by three buyers.',
        },
      });
      answerMe(body);

      final r = (await repository.loadOwn('f-1')).reputation;

      expect(r.restriction!.cause, 'points');
      expect(r.restriction!.until, DateTime.utc(2026, 11, 21, 9));
      expect(r.override!.tag, 'low');
      expect(r.override!.reason, 'Reported by three buyers.');
    });

    test('a new user: no rating fields, penalties off', () async {
      answerMe({
        'reputation': {
          'userId': 'f-1',
          'role': 'farmer',
          'tier': 'good',
          'activePoints': 0,
          'thresholds': {'warning': 3, 'low': 6, 'restricted': 10},
          'ratingCount': 0,
          'completedOrders90d': 0,
          'faultCancels90d': 0,
          'noShows90d': 0,
          'penaltiesEnabled': false,
          'asOf': '2026-11-12T06:06:41.673Z',
        },
      });

      final r = (await repository.loadOwn('f-1')).reputation;

      expect(r.hasRating, isFalse);
      expect(r.ratingAverage, isNull);
      expect(r.reliabilityPct, isNull);
      expect(r.penaltiesEnabled, isFalse);
      expect(r.totalOrders90d, 0);
    });

    test('an answer without a reputation is a failure, not a crash', () async {
      answerMe({'unexpected': true});
      await expectLater(
        repository.loadOwn('f-1'),
        throwsA(isA<ServerException>()),
      );
    });
  });

  group('loadEvents', () {
    test('asks a modest page and parses it', () async {
      answerEvents({
        'events': [
          _event('CANCEL#ord-1'),
          {
            'eventId': 'REQUEST#ord-2',
            'code': 'RESPONSE_EXPIRED',
            'points': 0,
            'zeroReason': 'not_reachable',
            'earnedBack': 0,
            'remainingPoints': 0,
            'status': 'active',
            'occurredAt': '2026-10-20T12:00:00.000Z',
            'appeal': {'status': 'none', 'canAppeal': false},
          },
          // A row without an id is skipped.
          {'code': 'LATE_CANCEL'},
        ],
        'nextToken': 'tok-2',
      });

      final result = await repository.loadEvents('f-1');

      expect(result.stale, isFalse);
      expect(result.page.nextToken, 'tok-2');
      expect(result.page.events, hasLength(2));
      final first = result.page.events.first;
      expect(first.eventId, 'CANCEL#ord-1');
      expect(first.points, 4);
      expect(first.remainingPoints, 3);
      expect(first.pointsExpireAt, DateTime.utc(2027, 1, 30, 6));
      expect(first.appeal.canAppeal, isTrue);
      expect(first.appeal.deadlineAt, DateTime.utc(2026, 11, 8, 6, 0, 4));
      final second = result.page.events.last;
      expect(second.zeroReason, 'not_reachable');
      expect(second.appeal.canAppeal, isFalse);
    });

    test('the next page sends the token', () async {
      answerEvents(
        {
          'events': [_event('CANCEL#ord-9')],
        },
        token: 'tok-2',
      );

      final result = await repository.loadEvents('f-1', nextToken: 'tok-2');

      expect(result.page.events.single.eventId, 'CANCEL#ord-9');
      expect(result.page.nextToken, isNull);
    });

    test('offline: the saved first page, stale, with no next page', () async {
      answerEvents({
        'events': [_event('CANCEL#ord-1')],
        'nextToken': 'tok-2',
      });
      await repository.loadEvents('f-1');
      when(
        () => api.get<Map<String, dynamic>>(
          '/reputation/me/events',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenThrow(const NetworkException());

      final result = await repository.loadEvents('f-1');

      expect(result.stale, isTrue);
      expect(result.page.events.single.eventId, 'CANCEL#ord-1');
      expect(result.page.nextToken, isNull);
    });

    test('offline with nothing saved, or on a later page: the failure',
        () async {
      when(
        () => api.get<Map<String, dynamic>>(
          '/reputation/me/events',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenThrow(const NetworkException());

      await expectLater(
        repository.loadEvents('f-1'),
        throwsA(isA<NetworkException>()),
      );
      await expectLater(
        repository.loadEvents('f-1', nextToken: 'tok-2'),
        throwsA(isA<NetworkException>()),
      );
    });
  });

  group('submitAppeal', () {
    const path = '/reputation/events/CANCEL%23ord-1/appeal';

    void answerAppeal([Map<String, dynamic>? body]) => when(
          () => api.post<Map<String, dynamic>>(
            path,
            data: any<dynamic>(named: 'data'),
          ),
        ).thenAnswer(
          (_) async =>
              body ??
              {
                'appeal': {
                  'status': 'pending',
                  'appealedAt': '2026-11-02T08:00:00.000Z',
                  'hasPhoto': false,
                },
                'event': {
                  'eventId': 'CANCEL#ord-1',
                  'code': 'LATE_CANCEL',
                  'points': 4,
                  'status': 'active',
                  'appeal': {'status': 'pending', 'canAppeal': false},
                },
                'alreadyAppealed': false,
              },
        );

    void failAppeal(int status, String code) => when(
          () => api.post<Map<String, dynamic>>(
            path,
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(ServerException('refused', status, code));

    Map<String, dynamic> sentBody() => verify(
          () => api.post<Map<String, dynamic>>(
            path,
            data: captureAny<dynamic>(named: 'data'),
          ),
        ).captured.single as Map<String, dynamic>;

    final jpeg = Uint8List.fromList([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);

    test('sends the trimmed text to the encoded event id', () async {
      answerAppeal();

      final appeal = await repository.submitAppeal(
        userId: 'f-1',
        eventId: 'CANCEL#ord-1',
        text: '  The road was closed by the flood.  ',
      );

      expect(appeal.status, 'pending');
      expect(appeal.canAppeal, isFalse);
      expect(sentBody(), {'text': 'The road was closed by the flood.'});
    });

    test('a sent appeal forgets its draft and marks the saved page', () async {
      answerEvents({
        'events': [_event('CANCEL#ord-1'), _event('CANCEL#ord-2')],
      });
      await repository.loadEvents('f-1');
      await repository.saveAppealDraft('f-1', 'CANCEL#ord-1', 'The road');
      expect(
        await repository.readAppealDraft('f-1', 'CANCEL#ord-1'),
        'The road',
      );
      answerAppeal();

      await repository.submitAppeal(
        userId: 'f-1',
        eventId: 'CANCEL#ord-1',
        text: 'The road was closed.',
      );

      expect(await repository.readAppealDraft('f-1', 'CANCEL#ord-1'), isNull);
      // Offline afterwards, the saved page no longer offers the appeal.
      when(
        () => api.get<Map<String, dynamic>>(
          '/reputation/me/events',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenThrow(const NetworkException());
      final saved = (await repository.loadEvents('f-1')).page.events;
      expect(saved.first.appeal.status, 'pending');
      expect(saved.first.appeal.canAppeal, isFalse);
      expect(saved.last.appeal.canAppeal, isTrue);
    });

    test('with a photo: upload URL, PUT, then the appeal with its ref',
        () async {
      when(
        () => api.post<Map<String, dynamic>>(
          '/reputation/appeals/photo-url',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'uploadUrl': 'https://bucket.example/put',
          'photoRef': 'uploads/appeals/f-1/abc.jpg',
          'expiresInSeconds': 900,
        },
      );
      when(
        () => storage.put<void>(
          any(),
          data: any<dynamic>(named: 'data'),
          options: any(named: 'options'),
        ),
      ).thenAnswer(
        (_) async => Response<void>(
          requestOptions: RequestOptions(path: '/put'),
          statusCode: 200,
        ),
      );
      answerAppeal();

      await repository.submitAppeal(
        userId: 'f-1',
        eventId: 'CANCEL#ord-1',
        text: 'See the photo.',
        photo: jpeg,
      );

      final urlBody = verify(
        () => api.post<Map<String, dynamic>>(
          '/reputation/appeals/photo-url',
          data: captureAny<dynamic>(named: 'data'),
        ),
      ).captured.single as Map<String, dynamic>;
      expect(urlBody, {'sizeBytes': jpeg.length, 'contentType': 'image/jpeg'});
      final put = verify(
        () => storage.put<void>(
          'https://bucket.example/put',
          data: any<dynamic>(named: 'data'),
          options: captureAny(named: 'options'),
        ),
      ).captured.single as Options;
      expect(put.headers?[Headers.contentTypeHeader], 'image/jpeg');
      expect(sentBody(), {
        'text': 'See the photo.',
        'photoRef': 'uploads/appeals/f-1/abc.jpg',
      });
    });

    test('a photo over 200 KB or of another type is refused before any call',
        () async {
      for (final photo in [
        Uint8List.fromList(
          [0xFF, 0xD8, 0xFF, ...List.filled(204800, 0)],
        ),
        // PNG.
        Uint8List.fromList([0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4]),
        Uint8List(0),
      ]) {
        await expectLater(
          repository.submitAppeal(
            userId: 'f-1',
            eventId: 'CANCEL#ord-1',
            text: 'x',
            photo: photo,
          ),
          throwsA(
            isA<AppealException>()
                .having((e) => e.failure, 'failure', AppealFailure.photo),
          ),
        );
      }
      verifyNever(
        () => api.post<Map<String, dynamic>>(
          any(),
          data: any<dynamic>(named: 'data'),
        ),
      );
    });

    test('a retry after the appeal failed reuses the uploaded photo', () async {
      when(
        () => api.post<Map<String, dynamic>>(
          '/reputation/appeals/photo-url',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'uploadUrl': 'https://bucket.example/put',
          'photoRef': 'uploads/appeals/f-1/abc.jpg',
        },
      );
      when(
        () => storage.put<void>(
          any(),
          data: any<dynamic>(named: 'data'),
          options: any(named: 'options'),
        ),
      ).thenAnswer(
        (_) async => Response<void>(
          requestOptions: RequestOptions(path: '/put'),
          statusCode: 200,
        ),
      );
      failAppeal(500, 'REPUTATION_SERVER_ERROR');
      Future<void> send(Uint8List photo) => repository
          .submitAppeal(
            userId: 'f-1',
            eventId: 'CANCEL#ord-1',
            text: 'See the photo.',
            photo: photo,
          )
          .then((_) {});

      await expectLater(send(jpeg), throwsA(isA<AppealException>()));
      answerAppeal();
      await send(jpeg);

      // One upload for the two tries, and the retry carried its ref.
      verify(
        () => api.post<Map<String, dynamic>>(
          '/reputation/appeals/photo-url',
          data: any<dynamic>(named: 'data'),
        ),
      ).called(1);
      verify(
        () => storage.put<void>(
          any(),
          data: any<dynamic>(named: 'data'),
          options: any(named: 'options'),
        ),
      ).called(1);
      final bodies = verify(
        () => api.post<Map<String, dynamic>>(
          path,
          data: captureAny<dynamic>(named: 'data'),
        ),
      ).captured;
      expect(bodies, hasLength(2));
      expect(
        (bodies.last as Map)['photoRef'],
        'uploads/appeals/f-1/abc.jpg',
      );

      // Another photo, or the same after a sent appeal, is uploaded.
      await send(Uint8List.fromList([0xFF, 0xD8, 0xFF, 9, 9]));
      verify(
        () => storage.put<void>(
          any(),
          data: any<dynamic>(named: 'data'),
          options: any(named: 'options'),
        ),
      ).called(1);
    });

    test('a failed upload sends no appeal', () async {
      when(
        () => api.post<Map<String, dynamic>>(
          '/reputation/appeals/photo-url',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'uploadUrl': 'https://bucket.example/put',
          'photoRef': 'uploads/appeals/f-1/abc.jpg',
        },
      );
      when(
        () => storage.put<void>(
          any(),
          data: any<dynamic>(named: 'data'),
          options: any(named: 'options'),
        ),
      ).thenThrow(
        DioException(
          requestOptions: RequestOptions(path: '/put'),
          type: DioExceptionType.connectionError,
        ),
      );

      await expectLater(
        repository.submitAppeal(
          userId: 'f-1',
          eventId: 'CANCEL#ord-1',
          text: 'x',
          photo: jpeg,
        ),
        throwsA(
          isA<AppealException>()
              .having((e) => e.failure, 'failure', AppealFailure.notSent),
        ),
      );
      verifyNever(
        () => api.post<Map<String, dynamic>>(
          path,
          data: any<dynamic>(named: 'data'),
        ),
      );
    });

    for (final (status, code, failure) in [
      (409, 'REPUTATION_APPEAL_CLOSED', AppealFailure.closed),
      (404, 'REPUTATION_EVENT_NOT_FOUND', AppealFailure.closed),
      (409, 'REPUTATION_APPEAL_EXISTS', AppealFailure.exists),
      (503, 'REPUTATION_UNAVAILABLE', AppealFailure.unavailable),
      (500, 'REPUTATION_SERVER_ERROR', AppealFailure.notSent),
      // Not connection problems: no "check your connection".
      (400, 'REPUTATION_VALIDATION', AppealFailure.refused),
      (403, 'AUTH_025', AppealFailure.refused),
      (410, 'ACCOUNT_004', AppealFailure.refused),
    ]) {
      test('$code is ${failure.name}, and the draft stays', () async {
        await repository.saveAppealDraft('f-1', 'CANCEL#ord-1', 'The road');
        failAppeal(status, code);

        await expectLater(
          repository.submitAppeal(
            userId: 'f-1',
            eventId: 'CANCEL#ord-1',
            text: 'The road',
          ),
          throwsA(
            isA<AppealException>().having((e) => e.failure, 'failure', failure),
          ),
        );
        expect(
          await repository.readAppealDraft('f-1', 'CANCEL#ord-1'),
          'The road',
        );
      });
    }

    test('offline is not sent', () async {
      when(
        () => api.post<Map<String, dynamic>>(
          path,
          data: any<dynamic>(named: 'data'),
        ),
      ).thenThrow(const NetworkException());

      await expectLater(
        repository.submitAppeal(
          userId: 'f-1',
          eventId: 'CANCEL#ord-1',
          text: 'The road',
        ),
        throwsA(
          isA<AppealException>()
              .having((e) => e.failure, 'failure', AppealFailure.notSent),
        ),
      );
    });

    test('an empty or too long text never reaches the server', () async {
      for (final text in ['   ', 'x' * 501]) {
        await expectLater(
          repository.submitAppeal(
            userId: 'f-1',
            eventId: 'CANCEL#ord-1',
            text: text,
          ),
          throwsA(isA<AppealException>()),
        );
      }
      verifyNever(
        () => api.post<Map<String, dynamic>>(
          any(),
          data: any<dynamic>(named: 'data'),
        ),
      );
    });

    test('the same appeal again answers as taken (a retry is safe)', () async {
      answerAppeal({
        'appeal': {'status': 'pending', 'hasPhoto': false},
        'event': {
          'eventId': 'CANCEL#ord-1',
          'appeal': {'status': 'pending', 'canAppeal': false},
        },
        'alreadyAppealed': true,
      });

      final appeal = await repository.submitAppeal(
        userId: 'f-1',
        eventId: 'CANCEL#ord-1',
        text: 'The road',
      );
      expect(appeal.status, 'pending');
    });
  });

  group('the own rating summary kept for Profile', () {
    test('saved with its time, read back, and forgotten on logout', () async {
      expect(await repository.readOwnRatingSummary('f-1'), isNull);

      await repository.saveOwnRatingSummary('f-1', average: 4.6, count: 12);

      final saved = await repository.readOwnRatingSummary('f-1');
      expect(saved!.average, 4.6);
      expect(saved.count, 12);
      expect(saved.fetchedAt, now);
      expect(await repository.readOwnRatingSummary('f-2'), isNull);

      await ReputationCache().forgetUser('f-1');
      expect(await repository.readOwnRatingSummary('f-1'), isNull);
    });
  });

  group('fetchOwnRatingSummary (the server read Profile trusts)', () {
    void answerRatings(Map<String, dynamic> body) => when(
          () => api.get<Map<String, dynamic>>(
            '/ratings',
            queryParameters: {'rateeId': 'f-1', 'limit': '1'},
          ),
        ).thenAnswer((_) async => body);

    test('the summary as the server answered it, for the own id only',
        () async {
      answerRatings({
        'ratings': <Object>[],
        'summary': {'average': 4.6, 'count': 12, 'rateeRole': 'farmer'},
      });

      final summary = await repository.fetchOwnRatingSummary('f-1');

      expect(summary.average, 4.6);
      expect(summary.count, 12);
      verify(
        () => api.get<Map<String, dynamic>>(
          '/ratings',
          queryParameters: {'rateeId': 'f-1', 'limit': '1'},
        ),
      ).called(1);
    });

    test('a real "no ratings" answer is count 0', () async {
      answerRatings({
        'ratings': <Object>[],
        'summary': {'average': 0, 'count': 0},
      });

      final summary = await repository.fetchOwnRatingSummary('f-1');

      expect(summary.count, 0);
      expect(summary.average, 0);
    });

    // The rating repositories answer "empty" for these; this read must not,
    // or Profile would say "None yet" to a rated user on a bad connection.
    for (final error in const <ApiException>[
      NetworkException(),
      TimeoutException(),
      ServerException('Internal error', 503),
      RateLimitException(),
    ]) {
      test('${error.runtimeType} is thrown, never an empty summary', () async {
        when(
          () => api.get<Map<String, dynamic>>(
            '/ratings',
            queryParameters: any(named: 'queryParameters'),
          ),
        ).thenThrow(error);

        await expectLater(
          repository.fetchOwnRatingSummary('f-1'),
          throwsA(same(error)),
        );
      });
    }

    for (final body in <Map<String, dynamic>>[
      {},
      {'ratings': <Object>[], 'summary': null},
      {
        'summary': {'average': 4.6},
      },
      {
        'summary': {'average': 4.6, 'count': '12'},
      },
      {
        'summary': {'count': 0},
      },
    ]) {
      test('an answer without a readable summary throws: $body', () async {
        answerRatings(body);

        await expectLater(
          repository.fetchOwnRatingSummary('f-1'),
          throwsA(isA<ServerException>()),
        );
      });
    }
  });

  group('photoContentTypeOf', () {
    test('JPEG, WebP and anything else', () {
      expect(
        ReputationRepository.photoContentTypeOf(
          Uint8List.fromList([0xFF, 0xD8, 0xFF, 0xE1]),
        ),
        'image/jpeg',
      );
      expect(
        ReputationRepository.photoContentTypeOf(
          Uint8List.fromList('RIFF0000WEBPVP8 '.codeUnits),
        ),
        'image/webp',
      );
      expect(
        ReputationRepository.photoContentTypeOf(
          Uint8List.fromList([0x89, 0x50, 0x4E, 0x47]),
        ),
        isNull,
      );
      expect(ReputationRepository.photoContentTypeOf(Uint8List(0)), isNull);
    });
  });

  group('ReputationCache', () {
    test('logout forgets this user only: answer, events and drafts', () async {
      final cache = ReputationCache();
      for (final user in ['f-1', 'f-2']) {
        await cache.writeOwn(
          user,
          _me()['reputation'] as Map<String, dynamic>,
          now,
        );
        await cache.writeEvents(user, {'events': <dynamic>[]}, now);
        await cache.writeDraft(user, 'CANCEL#ord-1', 'draft of $user');
      }

      await cache.forgetUser('f-1');

      expect(await cache.readOwn('f-1'), isNull);
      expect(await cache.readEvents('f-1'), isNull);
      expect(await cache.readDraft('f-1', 'CANCEL#ord-1'), isNull);
      expect(await cache.readOwn('f-2'), isNotNull);
      expect(await cache.readEvents('f-2'), isNotNull);
      expect(await cache.readDraft('f-2', 'CANCEL#ord-1'), 'draft of f-2');
    });

    test('an entry that no longer parses reads as nothing and is removed',
        () async {
      SharedPreferences.setMockInitialValues({'reputation_v1:f-1:me': 'junk{'});
      final cache = ReputationCache();

      expect(await cache.readOwn('f-1'), isNull);
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString('reputation_v1:f-1:me'), isNull);
    });

    test('a draft is cut at 500 characters, an empty one forgotten', () async {
      final cache = ReputationCache();
      await cache.writeDraft('f-1', 'e', 'x' * 600);
      expect((await cache.readDraft('f-1', 'e'))!.length, 500);
      await cache.writeDraft('f-1', 'e', '  ');
      expect(await cache.readDraft('f-1', 'e'), isNull);
    });

    test('a failing store never fails the caller', () async {
      final cache = ReputationCache(
        preferences: () async => throw StateError('prefs broken'),
      );

      expect(await cache.readOwn('f-1'), isNull);
      expect(await cache.readDraft('f-1', 'e'), isNull);
      await cache.writeOwn('f-1', const {}, now);
      await cache.writeDraft('f-1', 'e', 'x');
      await cache.forgetUser('f-1');
    });
  });
}
