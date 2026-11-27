import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';

/// A [LocalFileComparator] that accepts a golden differing in at most
/// [tolerance] of its pixels (0 to 1).
///
/// Goldens are made on a developer Mac and checked on the Linux CI runner,
/// whose rasterizer anti-aliases curves a little differently. A small
/// tolerance absorbs that edge noise while a large change (a missing halo,
/// a moved trail) still fails. A small marker can stay under it, so check
/// those on the paint calls too (the `paints` matcher).
class TolerantGoldenComparator extends LocalFileComparator {
  TolerantGoldenComparator(super.testFile, {required this.tolerance})
      : assert(tolerance >= 0 && tolerance <= 1);

  final double tolerance;

  /// Replaces the current comparator for the rest of the test file, keeping
  /// golden paths relative to [testFileName] in the current test directory.
  static void install(String testFileName, {double tolerance = 0.01}) {
    final current = goldenFileComparator;
    final basedir = current is LocalFileComparator
        ? current.basedir
        : Uri.base.resolve('test/');
    goldenFileComparator = TolerantGoldenComparator(
      basedir.resolve(testFileName),
      tolerance: tolerance,
    );
  }

  @override
  Future<bool> compare(Uint8List imageBytes, Uri golden) async {
    final result = await GoldenFileComparator.compareLists(
      imageBytes,
      await getGoldenBytes(golden),
    );
    if (result.passed || result.diffPercent <= tolerance) {
      result.dispose();
      return true;
    }
    final error = await generateFailureOutput(result, golden, basedir);
    result.dispose();
    throw FlutterError(error);
  }
}
