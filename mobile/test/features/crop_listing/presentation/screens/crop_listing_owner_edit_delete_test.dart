import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/rating.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/edit_crop_listing_screen.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockCropListingBloc extends Mock implements CropListingBloc {}

class _MockOrderBloc extends Mock implements OrderBloc {}

class _MockOrderRepository extends Mock implements BaseOrderRepository {}

class _MockRatingRepository extends Mock implements BaseRatingRepository {}

class _FakeCropListingEvent extends Fake implements CropListingEvent {}

CropListing _listing({
  ListingStatus status = ListingStatus.active,
  double quantity = 12.345,
  double pricePerKg = 45,
}) =>
    CropListing()
      ..listingId = 'listing-1'
      ..farmerId = 'farmer-001'
      ..farmerName = 'Almaz'
      ..cropType = 'teff'
      ..quantity = quantity
      ..pricePerKg = pricePerKg
      ..location = (LatLng()
        ..latitude = 9.0
        ..longitude = 38.7)
      ..locationName = 'Bishoftu'
      ..description = 'White teff'
      ..status = status
      ..createdAt = DateTime(2026, 11)
      ..updatedAt = DateTime(2026, 11)
      ..version = 1;

Order _order(OrderStatus status, {String listingId = 'listing-1'}) => Order()
  ..orderId = 'order-${status.name}'
  ..listingId = listingId
  ..buyerId = 'buyer-1'
  ..farmerId = 'farmer-001'
  ..cropType = 'teff'
  ..quantity = 100
  ..pricePerUnit = 45
  ..totalPrice = 4500
  ..status = status
  ..createdAt = DateTime(2026, 11)
  ..version = 1;

/// SIM-56: the farmer edits or deletes their own active listing from its
/// detail; nobody else sees either action.
void main() {
  late _MockCropListingBloc cropBloc;
  late StreamController<CropListingState> cropStates;
  late _MockOrderBloc orderBloc;
  late _MockOrderRepository orders;
  late RatingBloc ratingBloc;

  setUpAll(() => registerFallbackValue(_FakeCropListingEvent()));

  setUp(() {
    cropBloc = _MockCropListingBloc();
    cropStates = StreamController<CropListingState>.broadcast();
    when(() => cropBloc.state).thenReturn(const CropListingInitial());
    when(() => cropBloc.stream).thenAnswer((_) => cropStates.stream);
    when(() => cropBloc.close()).thenAnswer((_) async {});
    orderBloc = _MockOrderBloc();
    orders = _MockOrderRepository();
    when(() => orderBloc.repository).thenReturn(orders);
    when(() => orderBloc.state).thenReturn(const OrderInitial());
    when(() => orderBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => orders.getOrdersByFarmer('farmer-001'))
        .thenAnswer((_) async => const []);
    final ratings = _MockRatingRepository();
    when(() => ratings.getRatingSummary(any()))
        .thenAnswer((_) async => RatingSummary.empty);
    ratingBloc = RatingBloc(repository: ratings);
  });

  tearDown(() async {
    await cropStates.close();
    await ratingBloc.close();
  });

  /// The detail pushed over a home page, so a pop shows "home".
  Widget app({
    required CropListing listing,
    String userId = 'farmer-001',
    UserRole role = UserRole.farmer,
  }) =>
      MultiBlocProvider(
        providers: [
          BlocProvider<CropListingBloc>.value(value: cropBloc),
          BlocProvider<OrderBloc>.value(value: orderBloc),
          BlocProvider<RatingBloc>.value(value: ratingBloc),
        ],
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: Builder(
            builder: (context) => Scaffold(
              body: Center(
                child: TextButton(
                  onPressed: () => Navigator.of(context).push(
                    MaterialPageRoute<void>(
                      builder: (_) => CropListingDetailScreen(
                        listing: listing,
                        currentUserId: userId,
                        currentUserRole: role,
                      ),
                    ),
                  ),
                  child: const Text('home'),
                ),
              ),
            ),
          ),
        ),
      );

  Future<void> open(
    WidgetTester tester, {
    CropListing? listing,
    String userId = 'farmer-001',
    UserRole role = UserRole.farmer,
  }) async {
    await tester.pumpWidget(
      app(listing: listing ?? _listing(), userId: userId, role: role),
    );
    await tester.tap(find.text('home'));
    await tester.pumpAndSettle();
  }

  const edit = Key('crop_detail_edit');
  const delete = Key('crop_detail_delete');

  group('who sees Edit and Delete', () {
    testWidgets('the owner of an active listing', (tester) async {
      await open(tester);

      expect(find.byKey(edit), findsOneWidget);
      expect(find.byKey(delete), findsOneWidget);
    });

    testWidgets('not another farmer', (tester) async {
      await open(tester, userId: 'farmer-002');

      expect(find.byKey(edit), findsNothing);
      expect(find.byKey(delete), findsNothing);
    });

    for (final role in [UserRole.buyer, UserRole.trucker]) {
      testWidgets('not a ${role.name}', (tester) async {
        await open(tester, userId: '${role.name}-1', role: role);

        expect(find.byKey(edit), findsNothing);
        expect(find.byKey(delete), findsNothing);
      });
    }

    for (final status in [
      ListingStatus.reserved,
      ListingStatus.sold,
      ListingStatus.expired,
    ]) {
      testWidgets('not on a ${status.name} listing', (tester) async {
        await open(tester, listing: _listing(status: status));

        expect(find.byKey(edit), findsNothing);
        expect(find.byKey(delete), findsNothing);
      });
    }
  });

  group('Delete', () {
    Future<void> confirm(WidgetTester tester) async {
      await tester.tap(find.byKey(delete));
      await tester.pumpAndSettle();
      expect(find.text('Delete this listing?'), findsOneWidget);
      await tester.tap(find.byKey(const Key('crop_detail_delete_confirm')));
      await tester.pump();
      await tester.pump();
    }

    testWidgets('cancel deletes nothing', (tester) async {
      await open(tester);
      await tester.tap(find.byKey(delete));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      verifyNever(() => cropBloc.add(any()));
      expect(find.byType(CropListingDetailScreen), findsOneWidget);
    });

    testWidgets('confirmed: the delete goes out and the detail closes',
        (tester) async {
      await open(tester);
      await confirm(tester);

      final sent = verify(() => cropBloc.add(captureAny())).captured.single
          as DeleteCropListing;
      expect(sent.listingId, 'listing-1');
      expect(sent.farmerId, 'farmer-001');

      cropStates.add(const CropListingsLoaded(listings: []));
      await tester.pumpAndSettle();

      expect(find.byType(CropListingDetailScreen), findsNothing);
      expect(find.text('home'), findsOneWidget);
    });

    testWidgets('a paid order on the listing stops it before it goes out',
        (tester) async {
      when(() => orders.getOrdersByFarmer('farmer-001')).thenAnswer(
        (_) async => [
          _order(OrderStatus.funded),
          _order(OrderStatus.settled, listingId: 'other'),
        ],
      );
      await open(tester);
      await confirm(tester);
      await tester.pumpAndSettle();

      verifyNever(() => cropBloc.add(any()));
      expect(
        find.text('This listing has open orders. Handle them first.'),
        findsOneWidget,
      );
      expect(find.byType(CropListingDetailScreen), findsOneWidget);
    });

    testWidgets('orders that no longer need it do not stop it', (tester) async {
      when(() => orders.getOrdersByFarmer('farmer-001')).thenAnswer(
        (_) async => [
          _order(OrderStatus.cancelled),
          _order(OrderStatus.inTransit),
          _order(OrderStatus.pendingPayment),
        ],
      );
      await open(tester);
      await confirm(tester);

      verify(() => cropBloc.add(any(that: isA<DeleteCropListing>()))).called(1);
      cropStates.add(const CropListingsLoaded(listings: []));
      await tester.pumpAndSettle();
      expect(find.byType(CropListingDetailScreen), findsNothing);
    });

    testWidgets('a refusal is said and the detail stays', (tester) async {
      await open(tester);
      await confirm(tester);

      cropStates.add(
        const CropListingError(
          message: 'CROP_HAS_ACTIVE_ORDERS: open orders',
          code: AppErrorCode.cropHasActiveOrders,
        ),
      );
      await tester.pumpAndSettle();

      expect(
        find.text('This listing has open orders. Handle them first.'),
        findsOneWidget,
      );
      expect(find.byType(CropListingDetailScreen), findsOneWidget);
    });
  });

  group('Edit', () {
    testWidgets('opens filled in, saves the edit and shows it', (tester) async {
      await open(tester);
      await tester.tap(find.byKey(edit));
      await tester.pumpAndSettle();

      expect(find.byType(EditCropListingScreen), findsOneWidget);
      expect(find.text('Edit listing'), findsOneWidget);
      // At most two decimals in the field.
      expect(find.text('12.35'), findsOneWidget);
      expect(find.text('45'), findsOneWidget);
      expect(find.text('White teff'), findsOneWidget);

      await tester.enterText(
        find.byKey(const Key('edit_listing_price')),
        '52.5',
      );
      await tester.tap(find.byKey(const Key('edit_listing_save')));
      await tester.pump();

      final sent = verify(() => cropBloc.add(captureAny())).captured.single
          as UpdateCropListing;
      expect(sent.listingId, 'listing-1');
      expect(sent.farmerId, 'farmer-001');
      expect(sent.pricePerKg, 52.5);
      // The untouched quantity keeps its decimals (the free stock).
      expect(sent.quantity, 12.345);
      expect(sent.description, 'White teff');

      cropStates.add(
        CropListingUpdated(listing: _listing(pricePerKg: 52.5)),
      );
      await tester.pumpAndSettle();

      expect(find.byType(EditCropListingScreen), findsNothing);
      expect(find.byType(CropListingDetailScreen), findsOneWidget);
      expect(find.text('52.50'), findsOneWidget);
    });

    testWidgets('a phone number in the description is refused', (tester) async {
      await open(tester);
      await tester.tap(find.byKey(edit));
      await tester.pumpAndSettle();

      await tester.enterText(
        find.byKey(const Key('edit_listing_description')),
        'Call 0911 22 33 44',
      );
      await tester.tap(find.byKey(const Key('edit_listing_save')));
      await tester.pumpAndSettle();

      verifyNever(() => cropBloc.add(any()));
      expect(find.byType(EditCropListingScreen), findsOneWidget);
    });

    testWidgets('a zero quantity is refused', (tester) async {
      await open(tester);
      await tester.tap(find.byKey(edit));
      await tester.pumpAndSettle();

      await tester.enterText(
        find.byKey(const Key('edit_listing_quantity')),
        '0',
      );
      await tester.tap(find.byKey(const Key('edit_listing_save')));
      await tester.pumpAndSettle();

      verifyNever(() => cropBloc.add(any()));
    });

    testWidgets('a refused save stays on the form', (tester) async {
      await open(tester);
      await tester.tap(find.byKey(edit));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('edit_listing_save')));
      await tester.pump();

      cropStates.add(
        const CropListingError(
          message: 'CROP_CONTACT_IN_TEXT: Remove the phone number.',
          code: AppErrorCode.contactInText,
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byType(EditCropListingScreen), findsOneWidget);
      final save = tester.widget<FilledButton>(
        find.byKey(const Key('edit_listing_save')),
      );
      expect(save.onPressed, isNotNull);
    });
  });
}
