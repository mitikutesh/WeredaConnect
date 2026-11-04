import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_pickup_scans.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockAuthBloc extends Mock implements AuthBloc {}

class _MockDeliveryBloc extends Mock implements DeliveryBloc {}

class _MockDeliveryRepository extends Mock implements BaseDeliveryRepository {}

const _tokens = AuthTokens(
  accessToken: 'a',
  refreshToken: 'r',
  idToken: 'i',
);

Trip _trip() => Trip()
  ..tripId = 'trip-001'
  ..truckerId = 'trucker-001'
  ..truckerName = 'Dawit'
  ..truckerPhone = ''
  ..fromWoreda = 'Meki'
  ..toWoreda = 'Addis Ababa'
  ..departureDate = DateTime.utc(2026, 11, 4)
  ..capacity = 50
  ..pricePerQuintal = 100
  ..status = TripStatus.matched
  ..matchedListings = <String>[]
  ..createdAt = DateTime.utc(2026, 10)
  ..updatedAt = DateTime.utc(2026, 10)
  ..version = 1;

Delivery _delivery(
  String id, {
  DeliveryStatus status = DeliveryStatus.pending,
  String trucker = 'trucker-001',
  String farmer = 'Almaz Bekele',
}) =>
    Delivery()
      ..deliveryId = id
      ..tripId = 'trip-001'
      ..listingId = 'l-$id'
      ..farmerId = 'f-$id'
      ..farmerName = farmer
      ..truckerId = trucker
      ..truckerName = 'Dawit'
      ..buyerId = 'b-1'
      ..buyerName = 'Selam'
      ..qrToken = 'qr'
      ..status = status
      ..createdAt = DateTime.utc(2026, 10, 30)
      ..updatedAt = DateTime.utc(2026, 10, 30)
      ..version = 1;

/// T-2: the trip owner scans each waiting pickup from the trip's page,
/// through the same scan as trip mode.
void main() {
  late _MockAuthBloc auth;
  late _MockDeliveryBloc deliveries;
  late _MockDeliveryRepository repository;
  final l10n = lookupAppLocalizations(const Locale('en'));

  setUp(() {
    auth = _MockAuthBloc();
    deliveries = _MockDeliveryBloc();
    repository = _MockDeliveryRepository();
    when(() => auth.stream).thenAnswer((_) => const Stream.empty());
    when(() => deliveries.stream).thenAnswer((_) => const Stream.empty());
    when(() => deliveries.state).thenReturn(const DeliveryInitial());
    when(() => deliveries.repository).thenReturn(repository);
  });

  void signIn(String userId, UserRole role) =>
      when(() => auth.state).thenReturn(
        AuthAuthenticated(userId: userId, tokens: _tokens, role: role),
      );

  Future<void> pump(
    WidgetTester tester, {
    Future<void> Function(BuildContext, Delivery)? onScan,
  }) async {
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: auth),
          BlocProvider<DeliveryBloc>.value(value: deliveries),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: Scaffold(
            body: TripPickupScans(trip: _trip(), onScanPickup: onScan),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('one 56 dp scan per waiting pickup of the owner', (tester) async {
    signIn('trucker-001', UserRole.trucker);
    when(
      () => repository.getDeliveriesByTrip('trip-001', userId: 'trucker-001'),
    ).thenAnswer(
      (_) async => [
        _delivery('d-1'),
        _delivery('d-2', farmer: 'Tolosa Gemechu'),
        _delivery('d-3', status: DeliveryStatus.pickedUp, farmer: 'Done'),
        _delivery('d-4', trucker: 'trucker-002', farmer: 'Other'),
      ],
    );
    final scanned = <String>[];

    await pump(tester, onScan: (_, d) async => scanned.add(d.deliveryId));

    expect(find.text(l10n.tripScanPickupQrFor('Almaz Bekele')), findsOneWidget);
    expect(
      find.text(l10n.tripScanPickupQrFor('Tolosa Gemechu')),
      findsOneWidget,
    );
    expect(find.text(l10n.tripScanPickupQrFor('Done')), findsNothing);
    expect(find.text(l10n.tripScanPickupQrFor('Other')), findsNothing);
    expect(
      tester.getSize(find.byKey(const Key('trip_scan_pickup_d-1'))).height,
      greaterThanOrEqualTo(56),
    );

    await tester.tap(find.byKey(const Key('trip_scan_pickup_d-2')));
    await tester.pumpAndSettle();
    expect(scanned, ['d-2']);
    // Read again after the scan: a confirmed pickup leaves the list.
    verify(
      () => repository.getDeliveriesByTrip('trip-001', userId: 'trucker-001'),
    ).called(2);
  });

  testWidgets('nothing for anyone but the trip owner', (tester) async {
    signIn('buyer-001', UserRole.buyer);

    await pump(tester);

    expect(find.byType(FilledButton), findsNothing);
    verifyNever(
      () => repository.getDeliveriesByTrip(any(), userId: any(named: 'userId')),
    );
  });

  testWidgets('nothing while the deliveries cannot be read', (tester) async {
    signIn('trucker-001', UserRole.trucker);
    when(
      () => repository.getDeliveriesByTrip('trip-001', userId: 'trucker-001'),
    ).thenThrow(StateError('offline'));

    await pump(tester);

    expect(find.byType(FilledButton), findsNothing);
  });
}
