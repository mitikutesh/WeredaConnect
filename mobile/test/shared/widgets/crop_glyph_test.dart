import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/crop_glyph.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

/// The crop glyphs of the page M boards (CreateCropListingM, CreateIntentM,
/// MarketM): an ear for the cereals, a pod for the pulses and sesame, a
/// bean for coffee; the sprout for anything else.
void main() {
  Widget app(Widget child) => MaterialApp(
        theme: AppTheme.light(role: UserRole.farmer),
        home: Scaffold(body: Center(child: child)),
      );

  group('cropGlyphShape', () {
    test('every crop the app lists has the glyph its board draws', () {
      const expected = {
        CropType.teff: CropGlyphShape.ear,
        CropType.wheat: CropGlyphShape.ear,
        CropType.barley: CropGlyphShape.ear,
        CropType.maize: CropGlyphShape.ear,
        CropType.sorghum: CropGlyphShape.ear,
        CropType.chickpea: CropGlyphShape.pod,
        CropType.lentils: CropGlyphShape.pod,
        CropType.sesame: CropGlyphShape.pod,
        CropType.coffee: CropGlyphShape.bean,
      };
      for (final type in CropType.values) {
        expect(cropGlyphShape(type.name), expected[type], reason: type.name);
      }
    });

    test('reads the key in any case, null for an unknown crop', () {
      expect(cropGlyphShape(' Wheat '), CropGlyphShape.ear);
      expect(cropGlyphShape('COFFEE'), CropGlyphShape.bean);
      expect(cropGlyphShape('avocado'), isNull);
      expect(cropGlyphShape(null), isNull);
    });
  });

  group('the drawings', () {
    test('stay inside the 24 px box the boards draw them in', () {
      const box = Rect.fromLTWH(0, 0, 24, 24);
      for (final shape in CropGlyphShape.values) {
        final bounds = cropGlyphBounds(shape);
        expect(box.contains(bounds.topLeft), isTrue, reason: '$shape');
        expect(box.contains(bounds.bottomRight), isTrue, reason: '$shape');
      }
    });

    test('are the boards own strokes', () {
      // The ear: the stalk from 3 to 21, kernels 4 px to each side.
      expect(
        cropGlyphBounds(CropGlyphShape.ear),
        const Rect.fromLTRB(8, 3, 16, 21),
      );
      // The pod: corner to corner of its 16 px square.
      expect(
        cropGlyphBounds(CropGlyphShape.pod),
        const Rect.fromLTRB(4, 4, 20, 20),
      );
      // The bean: a 13 by 18 oval leaning 30 degrees about the centre of
      // the box, still taller than wide.
      final bean = cropGlyphBounds(CropGlyphShape.bean);
      expect(bean.center.dx, closeTo(12, 0.01));
      expect(bean.center.dy, closeTo(12, 0.01));
      expect(bean.height, greaterThan(bean.width));
    });
  });

  group('sketchPath relative words', () {
    test('a relative curve and close, numbers split by their sign', () {
      // A kernel of the ear: from (12, 9) out to (8, 5) and back.
      final kernel = sketchPath('M12 9c-2.6 0-4-1.4-4-4 2.6 0 4 1.4 4 4z');
      expect(kernel.getBounds(), const Rect.fromLTRB(8, 5, 12, 9));
    });

    test('an absolute curve followed by a relative one', () {
      final pod = sketchPath('M4 20C4 11 11 4 20 4c0 9-7 16-16 16z');
      expect(pod.getBounds(), const Rect.fromLTRB(4, 4, 20, 20));
    });

    test('relative lines and a smooth curve end where the board ends', () {
      final back = sketchPath('M15 5l-7 7 7 7');
      expect(back.getBounds(), const Rect.fromLTRB(8, 5, 15, 19));
      // The bean's crease: (15.8, 4.8) to (8.2, 19.2).
      final crease =
          sketchPath('M15.8 4.8c-3.4 2.4-1 5.2-3.6 7.2s-0.6 4.8-4 7.2');
      final bounds = crease.getBounds();
      expect(bounds.top, closeTo(4.8, 0.001));
      expect(bounds.bottom, closeTo(19.2, 0.001));
      expect(bounds.right, closeTo(15.8, 0.001));
      expect(bounds.left, closeTo(8.2, 0.05));
    });

    test('relative h and v', () {
      final box = sketchPath('M3 7h11v9H3z');
      expect(box.getBounds(), const Rect.fromLTRB(3, 7, 14, 16));
    });
  });

  group('CropGlyph', () {
    testWidgets('draws at its size, with no semantics of its own',
        (tester) async {
      final handle = tester.ensureSemantics();
      await tester.pumpWidget(app(const CropGlyph(cropKey: 'coffee')));
      expect(tester.getSize(find.byType(CropGlyph)), const Size.square(20));
      expect(find.byType(FwSprout), findsNothing);
      expect(tester.getSemantics(find.byType(CropGlyph)).label, isEmpty);
      handle.dispose();
    });

    testWidgets('a crop without a board glyph keeps the sprout',
        (tester) async {
      await tester.pumpWidget(
        app(const CropGlyph(cropKey: 'avocado', size: 24)),
      );
      expect(find.byType(FwSprout), findsOneWidget);
      expect(tester.getSize(find.byType(FwSprout)), const Size.square(24));
    });
  });
}
