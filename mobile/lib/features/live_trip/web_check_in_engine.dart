import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/data/repositories/web_delivery_repository.dart';
import 'package:wereda_connect/features/live_trip/check_in_engine_binding.dart';
import 'package:wereda_connect/features/live_trip/check_in_notice.dart';
import 'package:wereda_connect/features/live_trip/data/geolocator_location_source.dart';
import 'package:wereda_connect/features/live_trip/data/prefs_sharing_state_store.dart';
import 'package:wereda_connect/features/live_trip/data/repository_trip_progress_reader.dart';
import 'package:wereda_connect/features/live_trip/data/web_location_ping_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/web_trip_listing_repository.dart';
import 'package:wereda_connect/shared/providers/language_provider.dart';

/// The web check-in engine (LT-08), bound to the account and the page's
/// visibility: no queue and no background, so a trucker on web checks in
/// while the page is visible and with the manual actions. Both web entry
/// points (`main.dart` and `main_web.dart`) build it here so they stay alike.
CheckInEngine buildWebCheckInEngine({
  required ApiClient api,
  required AuthBloc authBloc,
  required LanguageProvider languageProvider,
}) {
  final engine = CheckInEngine(
    pings: WebLocationPingRepository(api: api),
    location: GeolocatorLocationSource(supportsBackground: false),
    trips: RepositoryTripProgressReader(
      deliveries: WebDeliveryRepository(api: api),
      trips: WebTripListingRepository(api: api),
    ),
    states: PrefsSharingStateStore(),
    notice: () => checkInNoticeFor(languageProvider.locale),
  );
  CheckInEngineBinding(engine: engine, authBloc: authBloc).attach();
  return engine;
}
