import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/crop_listing/domain/repositories/base_crop_listing_repository.dart';
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

class _MockCropRepository extends Mock implements BaseCropListingRepository {}

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

const _waitingText =
    'Your last change to this listing has not been sent yet. Edit it again '
    'once you are online and it has gone out.';

/// SIM-56: the farmer edits or deletes their own active listing from its
/// detail; nobody else sees either action.
void main() {
  late _MockCropListingBloc cropBloc;
  late StreamController<CropListingState> cropStates;
  late _MockOrderBloc orderBloc;
  late _MockOrderRepository orders;
  late _MockCropRepository cropRepo;
  late RatingBloc ratingBloc;

  /// What the screens sent to the bloc; each carries its own completer,
  /// which the test answers.
  late List<CropListingEvent> sent;

  setUpAll(() => registerFallbackValue(_FakeCropListingEvent()));

  setUp(() {
    cropBloc = _MockCropListingBloc();
    cropStates = StreamController<CropListingState>.broadcast();
    when(() => cropBloc.state).thenReturn(const CropListingInitial());
    when(() => cropBloc.stream).thenAnswer((_) => cropStates.stream);
    when(() => cropBloc.close()).thenAnswer((_) async {});
    sent = [];
    when(() => cropBloc.add(any())).thenAnswer(
      (invocation) =>
          sent.add(invocation.positionalArguments.single as CropListingEvent),
    );
    cropRepo = _MockCropRepository();
    when(() => cropBloc.repository).thenReturn(cropRepo);
    // Nothing waits in the queue, and the copy read again before an edit is
    // the one shown (each test that needs more says so).
    when(() => cropRepo.hasChangeWaiting(any())).thenAnswer((_) async => false);
    when(() => cropRepo.getListingById(any()))
        .thenAnswer((_) async => _listing());
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

    DeleteCropListing sentDelete() => sent.single as DeleteCropListing;

    testWidgets('cancel deletes nothing', (tester) async {
      await open(tester);
      await tester.tap(find.byKey(delete));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      expect(sent, isEmpty);
      expect(find.byType(CropListingDetailScreen), findsOneWidget);
    });

    testWidgets('confirmed: the delete goes out and the detail closes',
        (tester) async {
      await open(tester);
      await confirm(tester);

      expect(sentDelete().listingId, 'listing-1');
      expect(sentDelete().farmerId, 'farmer-001');

      sentDelete().done!.complete();
      await tester.pumpAndSettle();

      expect(find.byType(CropListingDetailScreen), findsNothing);
      expect(find.text('home'), findsOneWidget);
    });

    testWidgets("another screen's list load is not taken for the delete",
        (tester) async {
      await open(tester);
      await confirm(tester);

      // The app-wide bloc answers some other screen's load meanwhile.
      cropStates
        ..add(const CropListingsLoaded(listings: []))
        ..add(const CropListingError(message: 'offline'));
      // The delete is still running (its spinner turns): no settle here.
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(find.byType(CropListingDetailScreen), findsOneWidget);
      expect(find.byType(SnackBar), findsNothing);

      sentDelete().done!.complete();
      await tester.pumpAndSettle();
      expect(find.byType(CropListingDetailScreen), findsNothing);
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

      expect(sent, isEmpty);
      expect(
        find.text('This listing has open orders. Handle them first.'),
        findsOneWidget,
      );
      expect(find.byType(CropListingDetailScreen), findsOneWidget);
    });

    testWidgets('orders that cannot be read stop it (fail closed)',
        (tester) async {
      when(() => orders.getOrdersByFarmer('farmer-001'))
          .thenThrow(const NetworkException());
      await open(tester);
      await confirm(tester);
      await tester.pumpAndSettle();

      expect(sent, isEmpty);
      expect(
        find.text(
          "Could not check this listing's orders. Try again when you are "
          'online.',
        ),
        findsOneWidget,
      );
      expect(find.byType(CropListingDetailScreen), findsOneWidget);
      // The buttons work again.
      final button = tester.widget<OutlinedButton>(find.byKey(delete));
      expect(button.onPressed, isNotNull);
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

      sentDelete().done!.complete();
      await tester.pumpAndSettle();
      expect(find.byType(CropListingDetailScreen), findsNothing);
    });

    testWidgets('a refusal is said and the detail stays', (tester) async {
      await open(tester);
      await confirm(tester);

      sentDelete().done!.completeError(
            const SyncRejectedException('CROP_HAS_ACTIVE_ORDERS: open orders'),
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
    Future<void> openEdit(WidgetTester tester) async {
      await open(tester);
      await tester.tap(find.byKey(edit));
      await tester.pumpAndSettle();
    }

    UpdateCropListing sentEdit() => sent.single as UpdateCropListing;

    testWidgets('opens filled in, sends only the change, shows the saved copy',
        (tester) async {
      await openEdit(tester);

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

      final edit = sentEdit();
      expect(edit.listing.listingId, 'listing-1');
      // The base is the version the farmer was looking at.
      expect(edit.listing.version, 1);
      expect(edit.farmerId, 'farmer-001');
      expect(edit.pricePerKg, 52.5);
      // Untouched: not sent, so an order's stock taken meanwhile is safe.
      expect(edit.quantity, isNull);
      expect(edit.description, isNull);

      edit.done!.complete(_listing(pricePerKg: 52.5)..version = 2);
      await tester.pumpAndSettle();

      expect(find.byType(EditCropListingScreen), findsNothing);
      expect(find.byType(CropListingDetailScreen), findsOneWidget);
      expect(find.text('52.50'), findsOneWidget);
    });

    testWidgets(
        'changed since it opened: nothing saved, the detail reads the '
        'listing again and says so', (tester) async {
      var reads = 0;
      when(() => cropRepo.getListingById('listing-1')).thenAnswer(
        (_) async => reads++ == 0
            ? _listing()
            : (_listing(quantity: 6, pricePerKg: 60.5)..version = 3),
      );
      await openEdit(tester);
      await tester.enterText(
        find.byKey(const Key('edit_listing_price')),
        '52.5',
      );
      await tester.tap(find.byKey(const Key('edit_listing_save')));
      await tester.pump();

      sentEdit().done!.completeError(
            const SyncConflictException('Version conflict.'),
          );
      await tester.pumpAndSettle();

      expect(find.byType(EditCropListingScreen), findsNothing);
      expect(
        find.text(
          'This listing changed since you opened it, maybe an order took '
          'some. Check it and edit again.',
        ),
        findsOneWidget,
      );
      // The detail shows the listing as it is now.
      expect(find.text('60.50'), findsOneWidget);

      // The next edit starts from that copy.
      sent.clear();
      await tester.tap(find.byKey(edit));
      await tester.pumpAndSettle();
      expect(find.text('6'), findsOneWidget);
      expect(find.text('60.5'), findsOneWidget);
      await tester.enterText(
        find.byKey(const Key('edit_listing_price')),
        '61',
      );
      await tester.tap(find.byKey(const Key('edit_listing_save')));
      await tester.pump();
      expect(sentEdit().listing.version, 3);
      sentEdit().done!.complete(_listing(quantity: 6, pricePerKg: 61));
      await tester.pumpAndSettle();
    });

    testWidgets('opens from the listing as it is now, read again first',
        (tester) async {
      // The phone's copy took the server's after a sync conflict while the
      // detail showed the older one.
      when(() => cropRepo.getListingById('listing-1')).thenAnswer(
        (_) async => _listing(quantity: 5)..version = 4,
      );
      await openEdit(tester);

      expect(find.text('5'), findsOneWidget);
      await tester.enterText(
        find.byKey(const Key('edit_listing_price')),
        '50',
      );
      await tester.tap(find.byKey(const Key('edit_listing_save')));
      await tester.pump();

      expect(sentEdit().listing.version, 4);
      expect(sentEdit().listing.quantity, 5);
      expect(sentEdit().quantity, isNull);
      sentEdit().done!.complete(_listing(quantity: 5, pricePerKg: 50));
      await tester.pumpAndSettle();
    });

    testWidgets('an earlier change still waiting: no form, the farmer is told',
        (tester) async {
      when(() => cropRepo.hasChangeWaiting('listing-1'))
          .thenAnswer((_) async => true);
      await openEdit(tester);

      expect(find.byType(EditCropListingScreen), findsNothing);
      expect(find.text(_waitingText), findsOneWidget);
      verifyNever(() => cropRepo.getListingById(any()));
    });

    testWidgets('a save refused because a change waits says so and stays',
        (tester) async {
      await openEdit(tester);
      await tester.enterText(
        find.byKey(const Key('edit_listing_price')),
        '50',
      );
      await tester.tap(find.byKey(const Key('edit_listing_save')));
      await tester.pump();

      sentEdit().done!.completeError(const ListingChangeWaitingException());
      await tester.pumpAndSettle();

      expect(find.byType(EditCropListingScreen), findsOneWidget);
      expect(find.text(_waitingText), findsOneWidget);
    });

    testWidgets(
        'changed since it opened and the new copy cannot be read (offline '
        'on web): the offline message, not "changed"', (tester) async {
      var reads = 0;
      when(() => cropRepo.getListingById('listing-1'))
          .thenAnswer((_) async => reads++ == 0 ? _listing() : null);
      await openEdit(tester);
      await tester.enterText(
        find.byKey(const Key('edit_listing_price')),
        '52.5',
      );
      await tester.tap(find.byKey(const Key('edit_listing_save')));
      await tester.pump();

      sentEdit().done!.completeError(
            const SyncConflictException('Version conflict.'),
          );
      await tester.pumpAndSettle();

      expect(find.byType(EditCropListingScreen), findsNothing);
      expect(find.text('No internet connection'), findsOneWidget);
      expect(find.textContaining('This listing changed'), findsNothing);
      // Still the copy it showed.
      expect(find.text('45'), findsOneWidget);
    });

    testWidgets('nothing changed: Save closes the form, nothing sent',
        (tester) async {
      await openEdit(tester);
      await tester.tap(find.byKey(const Key('edit_listing_save')));
      await tester.pumpAndSettle();

      expect(sent, isEmpty);
      expect(find.byType(EditCropListingScreen), findsNothing);
      expect(find.byType(CropListingDetailScreen), findsOneWidget);
    });

    testWidgets('a phone number in the description is refused', (tester) async {
      await openEdit(tester);

      await tester.enterText(
        find.byKey(const Key('edit_listing_description')),
        'Call 0911 22 33 44',
      );
      await tester.tap(find.byKey(const Key('edit_listing_save')));
      await tester.pumpAndSettle();

      expect(sent, isEmpty);
      expect(find.byType(EditCropListingScreen), findsOneWidget);
    });

    testWidgets('a zero quantity is refused', (tester) async {
      await openEdit(tester);

      await tester.enterText(
        find.byKey(const Key('edit_listing_quantity')),
        '0',
      );
      await tester.tap(find.byKey(const Key('edit_listing_save')));
      await tester.pumpAndSettle();

      expect(sent, isEmpty);
    });

    testWidgets(
        "a refused save stays on the form; another screen's error "
        'is not taken for it', (tester) async {
      await openEdit(tester);
      await tester.enterText(
        find.byKey(const Key('edit_listing_description')),
        'Red teff',
      );
      await tester.tap(find.byKey(const Key('edit_listing_save')));
      await tester.pump();

      cropStates.add(const CropListingError(message: 'list reload failed'));
      // The save is still running (its spinner turns): no settle here.
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 100));
      expect(find.byType(SnackBar), findsNothing);

      sentEdit().done!.completeError(
            const SyncRejectedException(
              'CROP_CONTACT_IN_TEXT: Remove the phone number.',
            ),
          );
      await tester.pumpAndSettle();

      expect(find.byType(EditCropListingScreen), findsOneWidget);
      expect(find.byType(SnackBar), findsOneWidget);
      final save = tester.widget<FilledButton>(
        find.byKey(const Key('edit_listing_save')),
      );
      expect(save.onPressed, isNotNull);
    });
  });
}
