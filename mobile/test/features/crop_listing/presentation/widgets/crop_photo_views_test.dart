import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/features/crop_listing/presentation/widgets/crop_photo_views.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

import '../../../../helpers/crop_photo_fixtures.dart';

CropListing _listing(List<CropPhoto> photos) => CropListing()
  ..listingId = 'listing-1'
  ..farmerId = 'farmer-1'
  ..cropType = 'teff'
  ..quantity = 10
  ..pricePerKg = 45
  ..location = (LatLng()
    ..latitude = 9
    ..longitude = 38.7)
  ..locationName = 'Ada'
  ..description = ''
  ..photos = photos
  ..status = ListingStatus.active
  ..createdAt = DateTime.utc(2026, 10, 31)
  ..updatedAt = DateTime.utc(2026, 10, 31)
  ..version = 1;

Widget _app(Widget child) => MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      // The detail screen scrolls; so does this harness.
      home: Scaffold(body: SingleChildScrollView(child: child)),
    );

void main() {
  late Directory dir;

  setUp(() => dir = Directory.systemTemp.createTempSync('crop_photo_views_'));
  tearDown(() => dir.deleteSync(recursive: true));

  CropPhoto localPhoto(String id) {
    final file = File('${dir.path}/$id.png')..writeAsBytesSync(onePixelPng);
    return CropPhoto()
      ..photoId = id
      ..localPath = file.path;
  }

  testWidgets('a listing from before photos keeps the crop tile',
      (tester) async {
    await tester.pumpWidget(_app(CropPhotoThumb(listing: _listing([]))));

    expect(find.byType(Image), findsNothing);
    expect(find.byIcon(Icons.photo_library_outlined), findsNothing);
  });

  testWidgets('the card shows the cover and the photo count', (tester) async {
    await tester.pumpWidget(
      _app(
        CropPhotoThumb(
          listing:
              _listing([localPhoto('a'), localPhoto('b'), localPhoto('c')]),
        ),
      ),
    );

    expect(find.byType(Image), findsOneWidget);
    expect(find.text('3'), findsOneWidget);
    expect(find.bySemanticsLabel('3 photos'), findsOneWidget);
  });

  testWidgets('one photo has no count badge', (tester) async {
    await tester.pumpWidget(
      _app(CropPhotoThumb(listing: _listing([localPhoto('a')]))),
    );

    expect(find.byIcon(Icons.photo_library_outlined), findsNothing);
  });

  testWidgets('the gallery asks for the full photos of a server listing',
      (tester) async {
    final asked = <String>[];
    final serverPhotos = [
      CropPhoto()..photoId = 'a',
      CropPhoto()..photoId = 'b',
    ];

    await tester.pumpWidget(
      _app(
        CropPhotoGallery(
          listing: _listing(serverPhotos),
          loadPhotos: (id) async {
            asked.add(id);
            return [];
          },
        ),
      ),
    );
    await tester.pump();

    expect(asked, ['listing-1']);
    expect(find.byKey(const Key('crop_photo_gallery')), findsOneWidget);
    expect(find.bySemanticsLabel(RegExp('Photo 1 of 2')), findsOneWidget);
  });

  testWidgets('the farmer\'s own photos need no read', (tester) async {
    final asked = <String>[];

    await tester.pumpWidget(
      _app(
        CropPhotoGallery(
          listing: _listing([localPhoto('a'), localPhoto('b')]),
          loadPhotos: (id) async {
            asked.add(id);
            return [];
          },
        ),
      ),
    );
    await tester.pump();

    expect(asked, isEmpty);
  });

  testWidgets('no photos, no gallery', (tester) async {
    await tester.pumpWidget(
      _app(
        CropPhotoGallery(listing: _listing([]), loadPhotos: (_) async => []),
      ),
    );

    expect(find.byKey(const Key('crop_photo_gallery')), findsNothing);
  });
}
