import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/rating/data/repositories/web_rating_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// ESC-12: a /sync conflict is a failure on web; for a rating it means the
/// server already holds it (one per order and ratee).
void main() {
  late _MockApiClient api;
  late WebRatingRepository repository;

  setUp(() {
    api = _MockApiClient();
    repository = WebRatingRepository(api: api);
  });

  void answerSync(Map<String, dynamic> result) {
    when(
      () => api.post<Map<String, dynamic>>(
        '/sync',
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer(
      (_) async => {
        'results': [result],
      },
    );
  }

  Future<void> rate() => repository.createRating(
        orderId: 'order-1',
        raterId: 'buyer-1',
        rateeId: 'farmer-1',
        rateeRole: 'farmer',
        stars: 5,
      );

  test('a conflict means it is already rated', () async {
    answerSync({
      'status': 'conflict',
      'serverVersion': 1,
      'message': 'Rating already exists.',
    });

    await expectLater(rate(), throwsA(isA<AlreadyRatedException>()));
    expect(
      mapErrorToCode(const AlreadyRatedException()),
      AppErrorCode.alreadyRated,
    );
  });

  test('a refusal still throws the server rejection', () async {
    answerSync({
      'status': 'error',
      'message': 'RATING_VALIDATION: stars must be 1 to 5.',
    });

    await expectLater(rate(), throwsA(isA<SyncRejectedException>()));
  });

  test('a success returns the rating', () async {
    answerSync({'status': 'success', 'serverVersion': 1});

    await expectLater(rate(), completes);
  });
}
