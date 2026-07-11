import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/web_crop_listing_repository.dart';

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
}
