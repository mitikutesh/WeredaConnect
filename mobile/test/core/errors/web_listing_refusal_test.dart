import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/web_crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/web_trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

import '../../helpers/crop_photo_fixtures.dart';
import '../../helpers/wait_for.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// FUP-51 on web: a crop listing or trip create refused because its text
/// holds a phone number, or because the account is under review, fails with
/// the reason in en, am and om. Web reads live data, so nothing local is left
/// to undo: the create screen stays open with the explanation.
void main() {
  const cropText = 'CROP_CONTACT_IN_TEXT: Remove the phone number from '
      'description. Contact opens after payment is held in escrow.';
  const tripText = 'TRIP_CONTACT_IN_TEXT: Remove the phone number from '
      'vehiclePlate. Contact opens after payment is held in escrow.';
  const held = 'FRAUD_ACCOUNT_HELD: Your account is under review. Some '
      'actions are paused. Contact support.';

  late _MockApiClient api;

  setUp(() {
    api = _MockApiClient();
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

  Future<void> expectExplained(AppErrorCode code, AppErrorCode expected) async {
    expect(code, expected);
    for (final locale in ['en', 'am', 'om']) {
      final l10n = await AppLocalizations.delegate.load(Locale(locale));
      final text = localizeErrorCode(code, l10n);
      expect(text, isNot(l10n.errorOccurred), reason: locale);
      expect(
        text,
        expected == AppErrorCode.contactInText
            ? l10n.errorContactInText
            : l10n.fraudAccountHeld,
        reason: locale,
      );
    }
  }

  for (final (message, expected) in [
    (cropText, AppErrorCode.contactInText),
    (held, AppErrorCode.accountHeld),
  ]) {
    test('farmer: a crop create refused with ${message.split(':').first}',
        () async {
      refuseWith(message);
      final bloc = CropListingBloc(
        repository: WebCropListingRepository(
          api: api,
          photoUploader: FakeCropPhotoUploader(),
        ),
      );
      addTearDown(bloc.close);

      bloc.add(
        CreateCropListing(
          farmerId: 'farmer-1',
          cropType: 'teff',
          quantity: 10,
          pricePerKg: 45,
          latitude: 9.6,
          longitude: 39.5,
          locationName: 'Debre Birhan',
          description: 'Call 0911234567',
          photos: testCropPhotos(),
        ),
      );
      await waitFor(() => bloc.state is CropListingError);

      await expectExplained((bloc.state as CropListingError).code, expected);
    });
  }

  for (final (message, expected) in [
    (tripText, AppErrorCode.contactInText),
    (held, AppErrorCode.accountHeld),
  ]) {
    test('trucker: a trip create refused with ${message.split(':').first}',
        () async {
      refuseWith(message);
      final bloc = TripListingBloc(
        repository: WebTripListingRepository(api: api),
      );
      addTearDown(bloc.close);

      bloc.add(
        CreateTrip(
          truckerId: 'trucker-1',
          truckerName: 'Trucker',
          truckerPhone: '+251900100002',
          fromWoreda: 'Adama',
          toWoreda: 'Addis Ababa',
          departureDate: DateTime.now().add(const Duration(days: 3)),
          capacity: 40,
          pricePerQuintal: 120,
          vehiclePlate: '0911234567',
        ),
      );
      await waitFor(() => bloc.state is TripListingError);

      await expectExplained((bloc.state as TripListingError).code, expected);
    });
  }
}
