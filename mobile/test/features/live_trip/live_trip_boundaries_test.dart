import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// LT-11 boundaries, read from the source:
/// - live location never reaches trip rows, browse screens, crop listings,
///   matching or messages (live-trip-api.md section 5, invariant 1; spec D6):
///   none of them imports the live trip feature;
/// - the live trip feature runs on web: none of its own files imports
///   Isar or `dart:io`.
void main() {
  List<File> dartFiles(String dir) => Directory(dir)
      .listSync(recursive: true)
      .whereType<File>()
      .where((f) => f.path.endsWith('.dart'))
      .toList();

  test('browse, trips, crops, matching and messages never import it', () {
    final offenders = [
      for (final dir in [
        'lib/features/trip_listing',
        'lib/features/crop_listing',
        'lib/features/buyer',
        'lib/features/messaging',
      ])
        for (final file in dartFiles(dir))
          if (file.readAsStringSync().contains('features/live_trip/'))
            file.path,
      for (final path in ['lib/screens/market_screen.dart'])
        if (File(path).readAsStringSync().contains('features/live_trip/')) path,
    ];
    expect(offenders, isEmpty);
  });

  test('the live trip feature imports no Isar and no dart:io', () {
    final offenders = [
      for (final file in dartFiles('lib/features/live_trip'))
        if (RegExp(r"import 'dart:io'|package:isar")
            .hasMatch(file.readAsStringSync()))
          file.path,
    ];
    expect(offenders, isEmpty);
  });
}
