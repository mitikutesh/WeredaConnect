import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/web_crop_listing_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// FUP-58: a crop listing's createdAt comes back as UTC. A listing created at
/// 02:30 on 23 Oct in Ethiopia is 2026-10-22T23:30:00.000Z, which web kept in
/// UTC and showed as 22 Oct.
// intl renders a narrow no-break space (U+202F) before AM/PM; normalize
// it so the literal expectations below stay readable.
String _norm(String s) => s.replaceAll('\u202f', ' ').replaceAll('\u00a0', ' ');

void main() {
  const stored = '2026-10-22T23:30:00.000Z';

  Map<String, dynamic> cropDto() => {
        'cropId': 'crop-1',
        'farmerId': 'farmer-1',
        'farmerName': 'Farmer',
        'cropType': 'teff',
        'quantityKg': 100.0,
        'pricePerKg': 50.0,
        'latitude': 9.0,
        'longitude': 38.0,
        'locationName': 'Adama',
        'status': 'available',
        'createdAt': stored,
        'updatedAt': stored,
        'version': 1,
      };

  late _MockApiClient api;
  late WebCropListingRepository repository;

  setUp(() {
    api = _MockApiClient();
    repository = WebCropListingRepository(api: api);
  });

  test('createdAt shows the Ethiopia day and time', () async {
    when(() => api.get<Map<String, dynamic>>('/crops/crop-1'))
        .thenAnswer((_) async => {'crop': cropDto()});

    final listing = (await repository.getListingById('crop-1'))!;

    expect(listing.createdAt.isAtSameMomentAs(DateTime.parse(stored)), isTrue);

    // Crop card (date only) and crop detail (date and time).
    expect(
      DateFormat.yMMMd().format(dayInEthiopia(listing.createdAt)),
      'Oct 23, 2026',
    );
    expect(
      DateFormat.yMMMd().format(listing.createdAt.toUtc()),
      'Oct 22, 2026',
    );
    expect(
      _norm(DateFormat.yMMMd()
          .add_jm()
          .format(instantInEthiopia(listing.createdAt))),
      'Oct 23, 2026 2:30 AM',
    );
  });
}
