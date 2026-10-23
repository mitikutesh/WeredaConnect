import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/web_crop_listing_repository.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockApiClient extends Mock implements ApiClient {}

void main() {
  group('WebCropListingRepository.getListingById', () {
    late _MockApiClient api;
    late WebCropListingRepository repository;

    setUp(() {
      api = _MockApiClient();
      repository = WebCropListingRepository(api: api);
    });

    test('parses the {"crop": {...}} envelope (API field names)', () async {
      when(() => api.get<Map<String, dynamic>>('/crops/crop-1')).thenAnswer(
        (_) async => {
          'crop': {
            'cropId': 'crop-1',
            'farmerId': 'farmer-1',
            'cropType': 'teff',
            'quantityKg': 100,
            'pricePerKg': 55,
            'status': 'available',
            'createdAt': '2026-07-11T00:00:00.000Z',
            'updatedAt': '2026-07-11T00:00:00.000Z',
            'version': 1,
          },
        },
      );

      final listing = await repository.getListingById('crop-1');
      expect(listing, isNotNull);
      expect(listing!.listingId, 'crop-1');
      expect(listing.cropType, 'teff');
      expect(listing.quantity, 100);
    });

    test('returns null on failure (offline / 403 / 404)', () async {
      when(() => api.get<Map<String, dynamic>>('/crops/crop-x'))
          .thenThrow(Exception('403 CROP_FORBIDDEN'));
      expect(await repository.getListingById('crop-x'), isNull);
    });
  });

  group('WebCropListingRepository.getActiveListings search (SIM-51)', () {
    late _MockApiClient api;
    late WebCropListingRepository repository;

    Map<String, dynamic> crop({
      required String id,
      required String cropType,
      required String farmerName,
      String? locationName,
      String? woreda,
    }) =>
        {
          'cropId': id,
          'farmerId': 'farmer-$id',
          'farmerName': farmerName,
          'cropType': cropType,
          'quantityKg': 100,
          'pricePerKg': 50,
          if (locationName != null) 'locationName': locationName,
          if (woreda != null) 'woreda': woreda,
          'status': 'available',
          'createdAt': '2026-09-28T00:00:00.000Z',
          'updatedAt': '2026-09-28T00:00:00.000Z',
          'version': 1,
        };

    setUp(() {
      api = _MockApiClient();
      repository = WebCropListingRepository(api: api);
      when(
        () => api.get<Map<String, dynamic>>(
          '/crops',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer(
        (_) async => {
          'crops': [
            crop(
              id: 'c1',
              cropType: 'teff',
              farmerName: 'Abebe Kebede',
              locationName: 'Bishoftu',
            ),
            // Older rows carry only "woreda"; the parser falls back to it.
            crop(
              id: 'c2',
              cropType: 'wheat',
              farmerName: 'Almaz Tesfaye',
              woreda: 'Adama',
            ),
          ],
        },
      );
    });

    Future<List<String>> ids(String? query) async =>
        (await repository.getActiveListings(searchQuery: query))
            .map((l) => l.listingId)
            .toList();

    test('matches farmer name, Amharic crop name and woreda place', () async {
      final am = await AppLocalizations.delegate.load(const Locale('am'));
      expect(await ids('abebe'), ['c1']);
      expect(await ids(am.cropTypeTeff), ['c1']);
      expect(await ids('wheat'), ['c2']);
      expect(await ids('adama'), ['c2']);
      expect(await ids('almaz adama'), ['c2']);
    });

    test('a query that hits nothing returns an empty list', () async {
      expect(await ids('quinoa'), isEmpty);
      expect(await ids('teff adama'), isEmpty);
    });

    test('no search returns every listing', () async {
      expect(await ids(null), ['c1', 'c2']);
      expect(await ids(''), ['c1', 'c2']);
    });

    test('the search stays client-side: no search key is sent', () async {
      await repository.getActiveListings(searchQuery: 'abebe');
      final captured = verify(
        () => api.get<Map<String, dynamic>>(
          '/crops',
          queryParameters: captureAny(named: 'queryParameters'),
        ),
      ).captured;
      expect(captured, hasLength(1));
      final params = captured.single as Map<String, dynamic>;
      expect(params, {'status': 'available', 'limit': '100'});
    });
  });

  // ESC-12: the delete used to go out without a version, so the server's
  // version check always conflicted and nothing was deleted; a conflict now
  // fails on web, so the delete reads the real version first.
  group('WebCropListingRepository.deleteListing', () {
    late _MockApiClient api;
    late WebCropListingRepository repository;
    Map<String, dynamic>? sentBody;

    setUp(() {
      api = _MockApiClient();
      repository = WebCropListingRepository(api: api);
      sentBody = null;
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer((invocation) async {
        sentBody = invocation.namedArguments[#data] as Map<String, dynamic>;
        return {
          'results': [
            {'status': 'success'},
          ],
        };
      });
    });

    void answerRead(int version) {
      when(() => api.get<Map<String, dynamic>>('/crops/crop-1')).thenAnswer(
        (_) async => {
          'crop': {
            'cropId': 'crop-1',
            'farmerId': 'farmer-1',
            'cropType': 'teff',
            'status': 'available',
            'version': version,
          },
        },
      );
    }

    test('sends the version the server holds', () async {
      answerRead(3);

      await repository.deleteListing(listingId: 'crop-1', farmerId: 'farmer-1');

      final op =
          (sentBody!['operations'] as List).first as Map<String, dynamic>;
      expect(op['action'], 'delete');
      expect(op['data'], {'farmerId': 'farmer-1', 'version': 3});
    });

    test('a failed read throws and sends nothing (no guessed version)',
        () async {
      when(() => api.get<Map<String, dynamic>>('/crops/crop-1'))
          .thenThrow(const NetworkException());

      await expectLater(
        repository.deleteListing(listingId: 'crop-1', farmerId: 'farmer-1'),
        throwsA(isA<NetworkException>()),
      );
      expect(sentBody, isNull);
    });

    test('an empty read throws NotFoundException', () async {
      when(() => api.get<Map<String, dynamic>>('/crops/crop-1'))
          .thenAnswer((_) async => <String, dynamic>{});

      await expectLater(
        repository.deleteListing(listingId: 'crop-1', farmerId: 'farmer-1'),
        throwsA(isA<NotFoundException>()),
      );
      expect(sentBody, isNull);
    });

    test('a version race throws SyncConflictException', () async {
      answerRead(3);
      when(
        () => api.post<Map<String, dynamic>>(
          '/sync',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          'results': [
            {'status': 'conflict', 'message': 'Version conflict on delete.'},
          ],
        },
      );

      await expectLater(
        repository.deleteListing(listingId: 'crop-1', farmerId: 'farmer-1'),
        throwsA(isA<SyncConflictException>()),
      );
    });
  });

  // ESC-12 part B (sync-protocol.md 1.17.0): web has no queue, so a refusal
  // is a failure that carries the server message for its own text.
  group('WebCropListingRepository part B refusals (FUP-01)', () {
    late _MockApiClient api;
    late WebCropListingRepository repository;

    setUp(() {
      api = _MockApiClient();
      repository = WebCropListingRepository(api: api);
      when(() => api.get<Map<String, dynamic>>('/crops/crop-1')).thenAnswer(
        (_) async => {
          'crop': {'cropId': 'crop-1', 'farmerId': 'farmer-1', 'version': 3},
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
            {'status': 'error', 'message': message},
          ],
        },
      );
    }

    for (final message in [
      'REPUTATION_RESTRICTED: restricted',
      'APP_UPDATE_REQUIRED: minimum 2.0.0',
    ]) {
      test('create: ${message.split(':').first} throws with the message',
          () async {
        refuseWith(message);

        await expectLater(
          repository.createListing(
            farmerId: 'farmer-1',
            cropType: 'teff',
            quantity: 10,
            pricePerKg: 45,
            latitude: 9.6,
            longitude: 39.5,
            locationName: 'Debre Birhan',
            description: '',
          ),
          throwsA(
            isA<SyncRejectedException>()
                .having((e) => e.message, 'message', message),
          ),
        );
      });
    }

    for (final message in [
      'CROP_HAS_ACTIVE_ORDERS: open orders',
      'APP_UPDATE_REQUIRED: minimum 2.0.0',
    ]) {
      test('delete: ${message.split(':').first} throws with the message',
          () async {
        refuseWith(message);

        await expectLater(
          repository.deleteListing(listingId: 'crop-1', farmerId: 'farmer-1'),
          throwsA(
            isA<SyncRejectedException>()
                .having((e) => e.message, 'message', message),
          ),
        );
      });
    }
  });
}
