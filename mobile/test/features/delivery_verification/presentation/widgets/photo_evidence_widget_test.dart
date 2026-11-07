import 'dart:io';
import 'dart:typed_data';

import 'package:cached_network_image/cached_network_image.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/photo_evidence_widget.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

const _deliveryId = '550e8400-e29b-41d4-a716-446655440000';
const _key = 'uploads/photos/$_deliveryId/dropoff-1700000000000.jpg';

/// FRD-12: the evidence tile shows a handoff photo from wherever it is: the
/// phone's own file, the bytes the browser just took, or the server through
/// the presigned URL the delivery's parties get.
void main() {
  late List<(String, PhotoType)> loads;

  Future<void> pump(
    WidgetTester tester, {
    String? photoRef,
    Uint8List? previewBytes,
    Future<String?> Function()? answer,
    bool canCapture = false,
  }) async {
    loads = [];
    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: PhotoEvidenceWidget(
            photoRef: photoRef,
            previewBytes: previewBytes,
            photoType: PhotoType.dropoff,
            deliveryId: _deliveryId,
            canCapture: canCapture,
            photoUrlLoader: (deliveryId, type) {
              loads.add((deliveryId, type));
              return (answer ?? () async => null)();
            },
          ),
        ),
      ),
    );
  }

  testWidgets('a server photo is shown through its presigned URL',
      (tester) async {
    await pump(
      tester,
      photoRef: _key,
      answer: () async => 'https://uploads.s3.test/signed-dropoff',
    );
    await tester.pump();

    expect(loads, [(_deliveryId, PhotoType.dropoff)]);
    final image = tester.widget<CachedNetworkImage>(
      find.byType(CachedNetworkImage),
    );
    expect(image.imageUrl, 'https://uploads.s3.test/signed-dropoff');
    // Cached by the key: a fresh URL on the next visit is the same photo.
    expect(image.cacheKey, _key);
  });

  testWidgets('offline, a server photo says it shows when online',
      (tester) async {
    await pump(
      tester,
      photoRef: _key,
      answer: () async => throw const SocketException('offline'),
    );
    await tester.pump();

    expect(find.text('Shows when online'), findsOneWidget);
    expect(find.byType(CachedNetworkImage), findsNothing);
  });

  testWidgets('no URL from the server says it shows when online',
      (tester) async {
    await pump(tester, photoRef: _key);
    await tester.pump();

    expect(find.text('Shows when online'), findsOneWidget);
  });

  testWidgets('a photo the browser just took is shown from its bytes',
      (tester) async {
    await pump(tester, photoRef: _key, previewBytes: Uint8List(16));

    expect(find.byType(Image), findsOneWidget);
    expect(loads, isEmpty);
  });

  testWidgets('a local file that is gone shows the empty tile, not the server',
      (tester) async {
    await pump(
      tester,
      photoRef: '/data/user/0/app/files/photos/$_deliveryId/dropoff-1.webp',
    );

    expect(find.text('No photo'), findsOneWidget);
    expect(loads, isEmpty);
  });

  testWidgets('a key of another delivery is never loaded', (tester) async {
    await pump(
      tester,
      photoRef: 'uploads/photos/660e8400-e29b-41d4-a716-446655440099/'
          'dropoff-1700000000000.jpg',
    );

    expect(loads, isEmpty);
    expect(find.text('No photo'), findsOneWidget);
  });

  testWidgets('without a photo the scan screen offers to add one',
      (tester) async {
    await pump(tester, canCapture: true);

    expect(find.text('Add Photo'), findsOneWidget);
  });
}
