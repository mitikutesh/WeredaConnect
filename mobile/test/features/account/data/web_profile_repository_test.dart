import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/account/data/repositories/web_profile_repository.dart';
import 'package:wereda_connect/features/account/domain/profile_edit.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// SIM-56: web has no queue, so a profile edit goes straight to /sync, and
/// the read is GET /user/profile (shared with the phone).
void main() {
  late _MockApiClient api;
  late WebProfileRepository repository;
  Map<String, dynamic>? sent;

  setUp(() {
    api = _MockApiClient();
    repository = WebProfileRepository(api: api);
    sent = null;
  });

  void answerSync(Map<String, dynamic> result) {
    when(
      () => api.post<Map<String, dynamic>>(
        '/sync',
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer((invocation) async {
      sent = invocation.namedArguments[#data] as Map<String, dynamic>;
      return {
        'results': [result],
      };
    });
  }

  test('sends one user update with the changed fields only', () async {
    answerSync({'operationId': 'op', 'status': 'success', 'serverVersion': 4});

    final outcome = await repository.save(
      userId: 'farmer-1',
      changes: const ProfileChanges(displayName: 'Abebe K.', woreda: 'Meki'),
    );

    expect(outcome, ProfileSaveOutcome.saved);
    final op = (sent!['operations'] as List).single as Map<String, dynamic>;
    expect(op['entityType'], 'user');
    expect(op['entityId'], 'farmer-1');
    expect(op['action'], 'update');
    expect(op['idempotencyKey'], startsWith('farmer-1:user:farmer-1:update:'));
    expect(op['data'], {'displayName': 'Abebe K.', 'woreda': 'Meki'});
  });

  test('a refusal throws with the server message', () async {
    answerSync({
      'status': 'error',
      'message': 'AUTH_027: Remove the phone number from displayName. '
          'Contact opens after payment is held in escrow.',
    });

    await expectLater(
      repository.save(
        userId: 'farmer-1',
        changes: const ProfileChanges(displayName: 'Call 0911223344'),
      ),
      throwsA(
        isA<SyncRejectedException>().having(
          (e) => contactInTextFields(e.message),
          'fields',
          ['displayName'],
        ),
      ),
    );
  });

  test('web never holds an unsent edit', () async {
    expect(await repository.hasUnsentEdit('farmer-1'), isFalse);
  });

  group('fetch', () {
    test("reads the caller's profile", () async {
      when(() => api.get<Map<String, dynamic>>('/user/profile')).thenAnswer(
        (_) async => {
          'user': {
            'userId': 'buyer-1',
            'displayName': 'Almaz',
            'woreda': 'Meki',
            'buyerType': 'company',
            'companyName': 'Meki Traders',
          },
        },
      );

      expect(
        await repository.fetch('buyer-1'),
        const ProfileValues(
          displayName: 'Almaz',
          woreda: 'Meki',
          buyerType: 'company',
          companyName: 'Meki Traders',
        ),
      );
    });

    test("another account's answer is no answer", () async {
      when(() => api.get<Map<String, dynamic>>('/user/profile')).thenAnswer(
        (_) async => {
          'user': {'userId': 'buyer-2', 'woreda': 'Meki'},
        },
      );

      expect(await repository.fetch('buyer-1'), isNull);
    });

    test('a failed read is null, never an error', () async {
      when(() => api.get<Map<String, dynamic>>('/user/profile'))
          .thenThrow(const NetworkException('offline'));

      expect(await repository.fetch('buyer-1'), isNull);
    });
  });
}
