import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/rating.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// No-op rating repository so the detail screen's farmer card can build in
/// tests without hitting a backend ("no ratings yet" unless [summary] says).
class _FakeRatingRepository implements BaseRatingRepository {
  _FakeRatingRepository([this.summary = RatingSummary.empty]);

  final RatingSummary summary;

  @override
  Future<Rating> createRating({
    required String orderId,
    required String raterId,
    required String rateeId,
    required String rateeRole,
    required int stars,
    String? comment,
  }) async =>
      Rating();

  @override
  Future<bool> hasRated({
    required String orderId,
    required String rateeId,
  }) async =>
      false;

  @override
  Future<List<Rating>> getRatingsForRatee(String rateeId) async => const [];

  @override
  Future<RatingSummary> getRatingSummary(String rateeId) async => summary;
}

CropListing _makeListing({
  String cropType = 'teff',
  double quantity = 50.0,
  double pricePerKg = 85.0,
  String locationName = 'Bishoftu',
  String description = 'Test description',
  ListingStatus status = ListingStatus.active,
  String farmerName = '',
}) {
  return CropListing()
    ..farmerName = farmerName
    ..listingId = 'test-listing'
    ..farmerId = 'farmer-001'
    ..cropType = cropType
    ..quantity = quantity
    ..pricePerKg = pricePerKg
    ..location = (LatLng()
      ..latitude = 9.0
      ..longitude = 38.7)
    ..locationName = locationName
    ..description = description
    ..audioNoteRef = null
    ..status = status
    ..createdAt = DateTime(2026, 2, 20)
    ..updatedAt = DateTime(2026, 2, 20)
    ..version = 1;
}

Widget _buildScreen({
  required CropListing listing,
  String currentUserId = 'user-001',
  UserRole currentUserRole = UserRole.farmer,
  RatingSummary rating = RatingSummary.empty,
}) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    locale: const Locale('en'),
    home: BlocProvider<RatingBloc>(
      create: (_) => RatingBloc(repository: _FakeRatingRepository(rating)),
      child: CropListingDetailScreen(
        listing: listing,
        currentUserId: currentUserId,
        currentUserRole: currentUserRole,
      ),
    ),
  );
}

void main() {
  group('CropListingDetailScreen', () {
    testWidgets('renders crop type name and icon in header', (tester) async {
      final listing = _makeListing();

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      // The crop's name is the page title (no icon tile on the board).
      expect(find.byIcon(Icons.grass), findsNothing);
      expect(find.text('Teff'), findsWidgets);
    });

    testWidgets('renders Active status chip', (tester) async {
      final listing = _makeListing();

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      expect(find.text('Active'), findsOneWidget);
    });

    testWidgets('renders Draft status chip', (tester) async {
      final listing = _makeListing(status: ListingStatus.draft);

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      expect(find.text('Draft'), findsOneWidget);
    });

    testWidgets('renders quantity with quintals', (tester) async {
      final listing = _makeListing();

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      // Crop detail board: "50 quintals left · listed Feb 20" under the
      // crop, no separate quantity row.
      expect(find.textContaining('50 quintals left'), findsOneWidget);
      expect(find.text('Quantity'), findsNothing);
    });

    testWidgets('renders price with ETB/kg', (tester) async {
      final listing = _makeListing();

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      // The price large on the right with its unit under it.
      expect(find.text('85'), findsOneWidget);
      expect(find.text('ETB/kg'), findsOneWidget);
      expect(find.text('Price'), findsNothing);
    });

    testWidgets('renders location', (tester) async {
      final listing = _makeListing();

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      expect(find.text('Bishoftu'), findsOneWidget);
    });

    testWidgets('renders created date', (tester) async {
      final listing = _makeListing();
      // The day it was listed sits under the crop ("listed Feb 20").
      final eat = instantInEthiopia(DateTime(2026, 2, 20));
      final expectedDate = 'listed ${DateFormat.MMMd().format(eat)}';

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      expect(find.textContaining(expectedDate), findsOneWidget);
      expect(find.text('Created'), findsNothing);
    });

    testWidgets('hides description row when description is empty',
        (tester) async {
      final listing = _makeListing(description: '');

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      // The "Description" label should not appear
      expect(find.text('Description'), findsNothing);
    });

    testWidgets('shows description row when description is non-empty',
        (tester) async {
      final listing = _makeListing(description: 'Premium quality grain');

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      expect(find.text('Description'), findsOneWidget);
      expect(find.text('Premium quality grain'), findsOneWidget);
    });

    testWidgets('trucker role shows Create Delivery button', (tester) async {
      final listing = _makeListing();

      await tester.pumpWidget(
        _buildScreen(
          listing: listing,
          currentUserRole: UserRole.trucker,
        ),
      );
      await tester.pumpAndSettle();

      // ElevatedButton.icon renders the label and icon
      expect(find.text('Create Delivery'), findsOneWidget);
      expect(find.byIcon(Icons.local_shipping), findsOneWidget);
    });

    testWidgets(
        'buyer role gets no contact before an order, only the escrow note '
        '(ESC-48)', (tester) async {
      final listing = _makeListing()..farmerPhone = '+251911000111';

      await tester.pumpWidget(
        _buildScreen(
          listing: listing,
          currentUserRole: UserRole.buyer,
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Contact Farmer'), findsNothing);
      expect(find.byIcon(Icons.chat_outlined), findsNothing);
      expect(find.textContaining('911000111'), findsNothing);
      expect(
        find.text('Contact opens after payment is held in escrow.'),
        findsOneWidget,
      );
    });

    testWidgets('farmer role shows no action buttons', (tester) async {
      final listing = _makeListing();

      await tester.pumpWidget(
        _buildScreen(
          listing: listing,
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Create Delivery'), findsNothing);
      expect(find.text('Contact Farmer'), findsNothing);
      expect(
        find.text('Contact opens after payment is held in escrow.'),
        findsNothing,
      );
    });

    // Crop detail board: who sells it, with their rating.
    testWidgets('the farmer card shows initials, name and no ratings yet',
        (tester) async {
      await tester.pumpWidget(
        _buildScreen(
          listing: _makeListing(farmerName: 'Abebe Kebede'),
          currentUserRole: UserRole.buyer,
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('AK'), findsOneWidget);
      expect(find.text('Abebe Kebede'), findsOneWidget);
      expect(find.text('Farmer Rating: No ratings yet'), findsOneWidget);
    });

    testWidgets('the farmer card shows the rating when there is one',
        (tester) async {
      await tester.pumpWidget(
        _buildScreen(
          listing: _makeListing(farmerName: 'Abebe Kebede'),
          currentUserRole: UserRole.buyer,
          rating: const RatingSummary(average: 4.8, count: 12),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.text('Farmer Rating 4.8 · 12 ratings'), findsOneWidget);
    });

    testWidgets('no farmer card without a farmer name', (tester) async {
      await tester.pumpWidget(_buildScreen(listing: _makeListing()));
      await tester.pumpAndSettle();

      expect(find.textContaining('Farmer Rating'), findsNothing);
    });

    // SIM-33: a record holding the farmer's id as the name, and a junk
    // place ("105"), show neither.
    testWidgets('no farmer card for an id name, no junk place', (tester) async {
      const id = '3f2b8c1e-9a4d-4e6f-8b7a-1c2d3e4f5a6b';
      await tester.pumpWidget(
        _buildScreen(
          listing: _makeListing(farmerName: id, locationName: '105'),
          currentUserRole: UserRole.buyer,
        ),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('Farmer Rating'), findsNothing);
      expect(find.textContaining(id), findsNothing);
      expect(find.text('105'), findsNothing);
      expect(find.byIcon(Icons.location_on_outlined), findsNothing);
    });

    // FUP-67: buyers see every listing and may report one; a farmer sees only
    // their own and a trucker only uses a listing, so neither gets the button.
    group('report this listing', () {
      testWidgets('a buyer sees the report button', (tester) async {
        await tester.pumpWidget(
          _buildScreen(
            listing: _makeListing(),
            currentUserId: 'buyer-001',
            currentUserRole: UserRole.buyer,
          ),
        );
        await tester.pumpAndSettle();
        await tester.scrollUntilVisible(
          find.byKey(const Key('report_listing_button')),
          200,
        );

        expect(find.text('Report this listing'), findsOneWidget);
      });

      for (final role in [UserRole.farmer, UserRole.trucker]) {
        testWidgets('a ${role.name} sees no report button', (tester) async {
          await tester.pumpWidget(
            _buildScreen(
              listing: _makeListing(),
              currentUserId: '${role.name}-002',
              currentUserRole: role,
            ),
          );
          await tester.pumpAndSettle();

          expect(find.byKey(const Key('report_listing_button')), findsNothing);
        });
      }

      testWidgets('the owner never gets a report button', (tester) async {
        await tester.pumpWidget(
          _buildScreen(
            listing: _makeListing(),
            // The listing's farmer (the default role is farmer).
            currentUserId: 'farmer-001',
          ),
        );
        await tester.pumpAndSettle();

        expect(find.byKey(const Key('report_listing_button')), findsNothing);
      });
    });
  });
}
