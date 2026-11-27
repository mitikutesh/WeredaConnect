import 'dart:io';

import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
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

  // A desktop browser has no swipe: the strip needs arrows to get past the
  // first photo.
  testWidgets('the gallery arrows move the strip', (tester) async {
    await tester.pumpWidget(
      _app(
        CropPhotoGallery(
          listing: _listing([localPhoto('a'), localPhoto('b')]),
          loadPhotos: (_) async => [],
        ),
      ),
    );
    expect(find.byTooltip('Previous photo'), findsNothing);

    await tester.tap(find.byTooltip('Next photo'));
    await tester.pumpAndSettle();

    expect(find.byTooltip('Next photo'), findsNothing);
    expect(find.byTooltip('Previous photo'), findsOneWidget);
  });

  testWidgets('a mouse drag swipes the strip', (tester) async {
    await tester.pumpWidget(
      _app(
        CropPhotoGallery(
          listing: _listing([localPhoto('a'), localPhoto('b')]),
          loadPhotos: (_) async => [],
        ),
      ),
    );

    await tester.drag(
      find.byKey(const Key('crop_photo_gallery')),
      const Offset(-500, 0),
      kind: PointerDeviceKind.mouse,
    );
    await tester.pumpAndSettle();

    expect(find.byTooltip('Previous photo'), findsOneWidget);
  });

  testWidgets('one photo has no arrows', (tester) async {
    await tester.pumpWidget(
      _app(
        CropPhotoGallery(
          listing: _listing([localPhoto('a')]),
          loadPhotos: (_) async => [],
        ),
      ),
    );

    expect(find.byTooltip('Next photo'), findsNothing);
    expect(find.byTooltip('Previous photo'), findsNothing);
  });

  group('viewer', () {
    Future<void> pumpViewer(WidgetTester tester) => tester.pumpWidget(
          MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: CropPhotoViewer(
              photos: [localPhoto('a'), localPhoto('b'), localPhoto('c')],
            ),
          ),
        );

    testWidgets('the arrows step through every photo', (tester) async {
      await pumpViewer(tester);
      expect(find.text('Photo 1 of 3'), findsOneWidget);
      expect(find.byTooltip('Previous photo'), findsNothing);

      await tester.tap(find.byTooltip('Next photo'));
      await tester.pumpAndSettle();
      expect(find.text('Photo 2 of 3'), findsOneWidget);

      await tester.tap(find.byTooltip('Next photo'));
      await tester.pumpAndSettle();
      expect(find.text('Photo 3 of 3'), findsOneWidget);
      expect(find.byTooltip('Next photo'), findsNothing);

      await tester.tap(find.byTooltip('Previous photo'));
      await tester.pumpAndSettle();
      expect(find.text('Photo 2 of 3'), findsOneWidget);
    });

    testWidgets('the arrow keys move between photos', (tester) async {
      await pumpViewer(tester);

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
      await tester.pumpAndSettle();
      expect(find.text('Photo 1 of 3'), findsOneWidget);

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowRight);
      await tester.pumpAndSettle();
      expect(find.text('Photo 2 of 3'), findsOneWidget);

      await tester.sendKeyEvent(LogicalKeyboardKey.arrowLeft);
      await tester.pumpAndSettle();
      expect(find.text('Photo 1 of 3'), findsOneWidget);
    });
  });
}
