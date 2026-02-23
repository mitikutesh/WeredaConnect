import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

CropListing _makeListing({
  String cropType = 'teff',
  double quantity = 50.0,
  double pricePerKg = 85.0,
  String locationName = 'Bishoftu',
  String description = 'Test description',
  ListingStatus status = ListingStatus.active,
}) {
  return CropListing()
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
}) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    locale: const Locale('en'),
    home: CropListingDetailScreen(
      listing: listing,
      currentUserId: currentUserId,
      currentUserRole: currentUserRole,
    ),
  );
}

void main() {
  group('CropListingDetailScreen', () {
    testWidgets('renders crop type name and icon in header', (tester) async {
      final listing = _makeListing(cropType: 'teff');

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      // Teff uses Icons.grass
      expect(find.byIcon(Icons.grass), findsOneWidget);
      expect(find.text('Teff'), findsOneWidget);
    });

    testWidgets('renders Active status chip', (tester) async {
      final listing = _makeListing(status: ListingStatus.active);

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
      final listing = _makeListing(quantity: 50.0);

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      expect(find.text('50.0 quintals'), findsOneWidget);
      expect(find.text('Quantity'), findsOneWidget);
    });

    testWidgets('renders price with ETB/kg', (tester) async {
      final listing = _makeListing(pricePerKg: 85.0);

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      expect(find.text('85.00 ETB/kg'), findsOneWidget);
      expect(find.text('Price'), findsOneWidget);
    });

    testWidgets('renders location', (tester) async {
      final listing = _makeListing(locationName: 'Bishoftu');

      await tester.pumpWidget(_buildScreen(listing: listing));
      await tester.pumpAndSettle();

      expect(find.text('Bishoftu'), findsOneWidget);
      expect(find.text('Location'), findsOneWidget);
    });

    testWidgets('renders created date', (tester) async {
      final listing = _makeListing();
      final expectedDate =
          DateFormat.yMMMd().add_jm().format(DateTime(2026, 2, 20));

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

      await tester.pumpWidget(_buildScreen(
        listing: listing,
        currentUserRole: UserRole.trucker,
      ));
      await tester.pumpAndSettle();

      // ElevatedButton.icon renders the label and icon
      expect(find.text('Create Delivery'), findsOneWidget);
      expect(find.byIcon(Icons.local_shipping), findsOneWidget);
    });

    testWidgets('buyer role shows Contact Farmer button', (tester) async {
      final listing = _makeListing();

      await tester.pumpWidget(_buildScreen(
        listing: listing,
        currentUserRole: UserRole.buyer,
      ));
      await tester.pumpAndSettle();

      // OutlinedButton.icon renders the label and icon
      expect(find.text('Contact Farmer'), findsOneWidget);
      expect(find.byIcon(Icons.chat_outlined), findsOneWidget);
    });

    testWidgets('farmer role shows no action buttons', (tester) async {
      final listing = _makeListing();

      await tester.pumpWidget(_buildScreen(
        listing: listing,
        currentUserRole: UserRole.farmer,
      ));
      await tester.pumpAndSettle();

      expect(find.text('Create Delivery'), findsNothing);
      expect(find.text('Contact Farmer'), findsNothing);
    });
  });
}
