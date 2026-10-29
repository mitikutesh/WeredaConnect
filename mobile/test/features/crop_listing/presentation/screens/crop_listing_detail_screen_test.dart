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

      // Teff uses Icons.grass
      expect(find.byIcon(Icons.grass), findsOneWidget);
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

      expect(find.text('50.0 quintals'), findsOneWidget);
      expect(find.text('Quantity'), findsOneWidget);
    });

    testWidgets('renders price with ETB/kg', (tester) async {
      final listing = _makeListing();

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      expect(find.text('85.00 ETB/kg'), findsOneWidget);
      expect(find.text('Price'), findsOneWidget);
    });

    testWidgets('renders location', (tester) async {
      final listing = _makeListing();

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      expect(find.text('Bishoftu'), findsOneWidget);
      expect(find.text('Location'), findsOneWidget);
    });

    testWidgets('renders created date', (tester) async {
      final listing = _makeListing();
      // LT-03: English keeps the 24-hour clock, in Ethiopia time.
      final eat = instantInEthiopia(DateTime(2026, 2, 20));
      final expectedDate =
          '${DateFormat.yMMMd().format(eat)} ${DateFormat.Hm().format(eat)}';

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      expect(find.text(expectedDate), findsOneWidget);
      expect(find.text('Created'), findsOneWidget);
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
  });
}
