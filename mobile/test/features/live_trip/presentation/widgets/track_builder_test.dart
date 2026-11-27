import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:provider/provider.dart';

import 'package:wereda_connect/features/live_trip/domain/models/track_result.dart';
import 'package:wereda_connect/features/live_trip/domain/repositories/base_track_repository.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_builder.dart';

import '../../live_trip_fixtures.dart';
import '../../live_trip_harness.dart';

/// LT-11: the track is read only while its screen is the one shown and the
/// app is in the foreground; the stored snapshot shows at once.
void main() {
  late FakeTrackRepository tracks;
  final navigator = GlobalKey<NavigatorState>();

  setUp(() {
    tracks = FakeTrackRepository(track: corridorTrack());
  });

  Future<void> pumpBuilder(WidgetTester tester) async {
    await tester.pumpWidget(
      Provider<BaseTrackRepository>.value(
        value: tracks,
        child: MaterialApp(
          navigatorKey: navigator,
          home: Scaffold(
            body: TrackBuilder(
              deliveryId: 'del-101',
              userId: buyerId,
              builder: (context, view) => Text(
                view.loading
                    ? 'loading'
                    : '${view.track?.sharing.wire} ${view.failure?.name}',
              ),
            ),
          ),
        ),
      ),
    );
    await settleReads(tester);
  }

  testWidgets('reads at once when shown, as the viewer', (tester) async {
    await pumpBuilder(tester);
    expect(tracks.fetches, 1);
    expect(tracks.readers, [buyerId]);
    expect(find.text('live null'), findsOneWidget);
  });

  testWidgets('reads again after 5 minutes while shown', (tester) async {
    await pumpBuilder(tester);
    await tester.pump(const Duration(minutes: 5, seconds: 1));
    await settleReads(tester);
    expect(tracks.fetches, 2);
  });

  testWidgets('a screen pushed on top stops the reads, popping resumes',
      (tester) async {
    await pumpBuilder(tester);
    navigator.currentState!.push(
      MaterialPageRoute<void>(builder: (_) => const Text('on top')),
    );
    await tester.pumpAndSettle();
    await tester.pump(const Duration(minutes: 20));
    await settleReads(tester);
    expect(tracks.fetches, 1);

    // Shown again: the next read follows within the 5 minute interval.
    navigator.currentState!.pop();
    await tester.pumpAndSettle();
    await tester.pump(const Duration(minutes: 5, seconds: 1));
    await settleReads(tester);
    expect(tracks.fetches, 2);
  });

  testWidgets('the app in the background stops the reads', (tester) async {
    await pumpBuilder(tester);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.paused);
    await tester.pump(const Duration(minutes: 20));
    await settleReads(tester);
    expect(tracks.fetches, 1);

    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    await tester.pump(const Duration(minutes: 5, seconds: 1));
    await settleReads(tester);
    expect(tracks.fetches, 2);
  });

  testWidgets('offline shows the stored snapshot, never a blank',
      (tester) async {
    tracks
      ..offline = true
      ..stored = TrackSnapshot(track: corridorTrack(), fetchedAt: at(8, 50));
    await pumpBuilder(tester);
    expect(find.text('live offline'), findsOneWidget);
  });

  testWidgets('leaving the screen ends the polling', (tester) async {
    await pumpBuilder(tester);
    await tester.pumpWidget(const SizedBox());
    await tester.pump(const Duration(minutes: 20));
    expect(tracks.fetches, 1);
  });

  testWidgets('without a track repository it builds with no answer',
      (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        home: TrackBuilder(
          deliveryId: 'del-101',
          userId: buyerId,
          builder: (context, view) => Text(view.loading ? 'no track' : 'track'),
        ),
      ),
    );
    await settleReads(tester);
    expect(find.text('no track'), findsOneWidget);
  });
}
