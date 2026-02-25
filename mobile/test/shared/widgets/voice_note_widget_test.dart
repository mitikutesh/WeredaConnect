import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/audio_player_service.dart';
import 'package:wereda_connect/shared/services/audio_recorder_service.dart';
import 'package:wereda_connect/shared/widgets/voice_note_widget.dart';

// ---------------------------------------------------------------------------
// Mocks
// ---------------------------------------------------------------------------

class MockAudioRecorderService extends Mock implements AudioRecorderService {}

class MockAudioPlayerService extends Mock implements AudioPlayerService {}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const _testUserId = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';
const _testEntityId = 'f1e2d3c4-b5a6-7890-abcd-ef1234567890';

/// Returns a path to a real temporary file that [File.existsSync] reports as
/// existing. The widget's `didUpdateWidget` checks this.
late String _testAudioPath;

/// Wraps [VoiceNoteWidget] in a [MaterialApp] with localization delegates.
///
/// For tests that only need the initial state (empty / recording), use this
/// simpler helper. For tests that exercise recorded / playing / re-record /
/// delete states where the parent must feed the recorded path back into the
/// widget, use [buildHarnessWidget] instead.
Widget buildTestWidget({
  required MockAudioRecorderService recorderService,
  required MockAudioPlayerService playerService,
  String? audioFilePath,
  ValueChanged<String>? onRecorded,
  VoidCallback? onDeleted,
}) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    locale: const Locale('en'),
    home: Scaffold(
      body: VoiceNoteWidget(
        userId: _testUserId,
        entityId: _testEntityId,
        onRecorded: onRecorded ?? (_) {},
        onDeleted: onDeleted ?? () {},
        recorderService: recorderService,
        playerService: playerService,
        audioFilePath: audioFilePath,
      ),
    ),
  );
}

/// A stateful harness that feeds the recorded path back into [VoiceNoteWidget]
/// via [audioFilePath], mirroring how a real parent widget would behave.
///
/// This is necessary because [VoiceNoteWidget._playAudio], [_reRecord], and
/// [_deleteRecording] all reference [widget.audioFilePath].
class _VoiceNoteTestHarness extends StatefulWidget {
  final MockAudioRecorderService recorderService;
  final MockAudioPlayerService playerService;
  final ValueChanged<String>? onRecordedExtra;
  final VoidCallback? onDeletedExtra;

  const _VoiceNoteTestHarness({
    required this.recorderService,
    required this.playerService,
    this.onRecordedExtra,
    this.onDeletedExtra,
  });

  @override
  State<_VoiceNoteTestHarness> createState() => _VoiceNoteTestHarnessState();
}

class _VoiceNoteTestHarnessState extends State<_VoiceNoteTestHarness> {
  String? _audioFilePath;

  @override
  Widget build(BuildContext context) {
    return VoiceNoteWidget(
      userId: _testUserId,
      entityId: _testEntityId,
      audioFilePath: _audioFilePath,
      recorderService: widget.recorderService,
      playerService: widget.playerService,
      onRecorded: (path) {
        setState(() => _audioFilePath = path);
        widget.onRecordedExtra?.call(path);
      },
      onDeleted: () {
        setState(() => _audioFilePath = null);
        widget.onDeletedExtra?.call();
      },
    );
  }
}

/// Wraps the stateful harness in a [MaterialApp] + [Scaffold].
Widget buildHarnessWidget({
  required MockAudioRecorderService recorderService,
  required MockAudioPlayerService playerService,
  ValueChanged<String>? onRecorded,
  VoidCallback? onDeleted,
}) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    locale: const Locale('en'),
    home: Scaffold(
      body: _VoiceNoteTestHarness(
        recorderService: recorderService,
        playerService: playerService,
        onRecordedExtra: onRecorded,
        onDeletedExtra: onDeleted,
      ),
    ),
  );
}

void main() {
  late MockAudioRecorderService mockRecorder;
  late MockAudioPlayerService mockPlayer;

  late StreamController<bool> completedController;
  late StreamController<Duration> positionController;
  late StreamController<Duration?> durationController;

  late File tempAudioFile;

  setUp(() {
    mockRecorder = MockAudioRecorderService();
    mockPlayer = MockAudioPlayerService();

    completedController = StreamController<bool>.broadcast();
    positionController = StreamController<Duration>.broadcast();
    durationController = StreamController<Duration?>.broadcast();

    // Create a real temporary file so File.existsSync() returns true inside
    // the widget's didUpdateWidget.
    tempAudioFile = File(
      '${Directory.systemTemp.path}/voice_note_test_${DateTime.now().microsecondsSinceEpoch}.m4a',
    )..createSync();
    _testAudioPath = tempAudioFile.path;

    // Default stream stubs.
    when(() => mockPlayer.onCompleted)
        .thenAnswer((_) => completedController.stream);
    when(() => mockPlayer.onPositionChanged)
        .thenAnswer((_) => positionController.stream);
    when(() => mockPlayer.onDurationChanged)
        .thenAnswer((_) => durationController.stream);

    // Default dispose stubs.
    when(() => mockPlayer.dispose()).thenAnswer((_) async {});
    when(() => mockRecorder.dispose()).thenReturn(null);

    // Default stop / pause stubs.
    when(() => mockPlayer.stop()).thenAnswer((_) async {});
    when(() => mockPlayer.pause()).thenAnswer((_) async {});
  });

  tearDown(() {
    completedController.close();
    positionController.close();
    durationController.close();
    if (tempAudioFile.existsSync()) {
      tempAudioFile.deleteSync();
    }
  });

  // -------------------------------------------------------------------------
  // 1. Empty state
  // -------------------------------------------------------------------------
  group('Empty state', () {
    testWidgets('shows record button with mic icon', (tester) async {
      await tester.pumpWidget(
        buildTestWidget(
          recorderService: mockRecorder,
          playerService: mockPlayer,
        ),
      );
      await tester.pumpAndSettle();

      expect(find.byIcon(Icons.mic), findsOneWidget);
      expect(find.text('Record Audio Note'), findsOneWidget);
    });
  });

  // -------------------------------------------------------------------------
  // 2. Recording state
  // -------------------------------------------------------------------------
  group('Recording state', () {
    testWidgets(
        'shows stop button, timer, and animated red dot after startRecording',
        (tester) async {
      when(
        () => mockRecorder.startRecording(
          userId: _testUserId,
          entityId: _testEntityId,
        ),
      ).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 0,
          fileSizeBytes: 100,
        ),
      );

      await tester.pumpWidget(
        buildTestWidget(
          recorderService: mockRecorder,
          playerService: mockPlayer,
        ),
      );
      await tester.pumpAndSettle();

      // Tap the record button.
      await tester.tap(find.text('Record Audio Note'));
      await tester.pump();

      // Should be in recording state.
      expect(find.byIcon(Icons.stop), findsOneWidget);
      expect(find.text('Stop Recording'), findsOneWidget);
      expect(find.text('Recording...'), findsOneWidget);
      // Timer starts at 0.
      expect(find.text('0s'), findsOneWidget);
    });

    testWidgets('timer increments each second during recording',
        (tester) async {
      when(
        () => mockRecorder.startRecording(
          userId: _testUserId,
          entityId: _testEntityId,
        ),
      ).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 0,
          fileSizeBytes: 100,
        ),
      );

      await tester.pumpWidget(
        buildTestWidget(
          recorderService: mockRecorder,
          playerService: mockPlayer,
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Record Audio Note'));
      await tester.pump();

      // Advance 3 seconds.
      await tester.pump(const Duration(seconds: 1));
      expect(find.text('1s'), findsOneWidget);

      await tester.pump(const Duration(seconds: 1));
      expect(find.text('2s'), findsOneWidget);

      await tester.pump(const Duration(seconds: 1));
      expect(find.text('3s'), findsOneWidget);
    });
  });

  // -------------------------------------------------------------------------
  // 3. Recorded state
  // -------------------------------------------------------------------------
  group('Recorded state', () {
    testWidgets(
        'shows play button, duration, re-record button, and delete button',
        (tester) async {
      String? recordedPath;

      when(
        () => mockRecorder.startRecording(
          userId: _testUserId,
          entityId: _testEntityId,
        ),
      ).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 0,
          fileSizeBytes: 100,
        ),
      );

      when(() => mockRecorder.stopRecording()).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 5000,
          fileSizeBytes: 20000,
        ),
      );

      await tester.pumpWidget(
        buildHarnessWidget(
          recorderService: mockRecorder,
          playerService: mockPlayer,
          onRecorded: (path) => recordedPath = path,
        ),
      );
      await tester.pumpAndSettle();

      // Start recording.
      await tester.tap(find.text('Record Audio Note'));
      await tester.pump();

      // Let timer advance a few seconds so we see a duration.
      await tester.pump(const Duration(seconds: 3));

      // Stop recording.
      await tester.tap(find.text('Stop Recording'));
      await tester.pumpAndSettle();

      // Should be in recorded state.
      expect(find.byIcon(Icons.play_arrow), findsOneWidget);
      expect(find.text('Audio Note'), findsOneWidget);
      // Duration text (3s from the timer).
      expect(find.text('3s'), findsOneWidget);
      // Re-record button (mic icon).
      expect(find.byIcon(Icons.mic), findsOneWidget);
      // Delete button.
      expect(find.byIcon(Icons.delete_outline), findsOneWidget);

      // onRecorded callback should have been called.
      expect(recordedPath, _testAudioPath);
    });
  });

  // -------------------------------------------------------------------------
  // 4. Playing state
  // -------------------------------------------------------------------------
  group('Playing state', () {
    testWidgets('shows pause button, progress indicator, and re-record button',
        (tester) async {
      when(
        () => mockRecorder.startRecording(
          userId: _testUserId,
          entityId: _testEntityId,
        ),
      ).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 0,
          fileSizeBytes: 100,
        ),
      );

      when(() => mockRecorder.stopRecording()).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 5000,
          fileSizeBytes: 20000,
        ),
      );

      when(() => mockPlayer.play(_testAudioPath)).thenAnswer((_) async {});

      await tester.pumpWidget(
        buildHarnessWidget(
          recorderService: mockRecorder,
          playerService: mockPlayer,
        ),
      );
      await tester.pumpAndSettle();

      // Record then stop.
      await tester.tap(find.text('Record Audio Note'));
      await tester.pump();
      await tester.pump(const Duration(seconds: 2));
      await tester.tap(find.text('Stop Recording'));
      await tester.pumpAndSettle();

      // Now tap play.
      await tester.tap(find.byIcon(Icons.play_arrow));
      await tester.pump();

      // Should now be in playing state.
      expect(find.byIcon(Icons.pause), findsOneWidget);
      expect(find.byType(LinearProgressIndicator), findsOneWidget);
      // Re-record button (mic icon).
      expect(find.byIcon(Icons.mic), findsOneWidget);
    });

    testWidgets('returns to recorded state when playback completes',
        (tester) async {
      when(
        () => mockRecorder.startRecording(
          userId: _testUserId,
          entityId: _testEntityId,
        ),
      ).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 0,
          fileSizeBytes: 100,
        ),
      );

      when(() => mockRecorder.stopRecording()).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 5000,
          fileSizeBytes: 20000,
        ),
      );

      when(() => mockPlayer.play(_testAudioPath)).thenAnswer((_) async {});

      await tester.pumpWidget(
        buildHarnessWidget(
          recorderService: mockRecorder,
          playerService: mockPlayer,
        ),
      );
      await tester.pumpAndSettle();

      // Record then stop.
      await tester.tap(find.text('Record Audio Note'));
      await tester.pump();
      await tester.pump(const Duration(seconds: 2));
      await tester.tap(find.text('Stop Recording'));
      await tester.pumpAndSettle();

      // Play.
      await tester.tap(find.byIcon(Icons.play_arrow));
      await tester.pump();
      expect(find.byIcon(Icons.pause), findsOneWidget);

      // Simulate playback completion.
      completedController.add(true);
      await tester.pump();

      // Should return to recorded state with play button.
      expect(find.byIcon(Icons.play_arrow), findsOneWidget);
    });
  });

  // -------------------------------------------------------------------------
  // 5. Re-record flow
  // -------------------------------------------------------------------------
  group('Re-record flow', () {
    testWidgets(
        'tapping re-record stops playback, deletes file, and starts new recording',
        (tester) async {
      var deletedCalled = false;

      when(
        () => mockRecorder.startRecording(
          userId: _testUserId,
          entityId: _testEntityId,
        ),
      ).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 0,
          fileSizeBytes: 100,
        ),
      );

      when(() => mockRecorder.stopRecording()).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 5000,
          fileSizeBytes: 20000,
        ),
      );

      when(() => mockRecorder.deleteLocalAudio(_testAudioPath))
          .thenAnswer((_) async {});

      await tester.pumpWidget(
        buildHarnessWidget(
          recorderService: mockRecorder,
          playerService: mockPlayer,
          onDeleted: () => deletedCalled = true,
        ),
      );
      await tester.pumpAndSettle();

      // Record then stop.
      await tester.tap(find.text('Record Audio Note'));
      await tester.pump();
      await tester.pump(const Duration(seconds: 2));
      await tester.tap(find.text('Stop Recording'));
      await tester.pumpAndSettle();

      // Should now be in recorded state. Find the mic icon (re-record) button.
      final micButton = find.byIcon(Icons.mic);
      expect(micButton, findsOneWidget);

      // Tap re-record.
      await tester.tap(micButton);
      // _reRecord is async: it awaits player.stop(), recorder.deleteLocalAudio,
      // calls widget.onDeleted (harness setState), then awaits _startRecording.
      // Pump enough frames for all microtask continuations and rebuilds.
      await tester.pump();
      await tester.pump();
      await tester.pump();

      // Verify player was stopped.
      verify(() => mockPlayer.stop()).called(greaterThanOrEqualTo(1));

      // Verify file deletion was called with the original path.
      verify(() => mockRecorder.deleteLocalAudio(_testAudioPath)).called(1);

      // Verify onDeleted callback was invoked.
      expect(deletedCalled, isTrue);

      // Verify startRecording was called a second time for re-record.
      verify(
        () => mockRecorder.startRecording(
          userId: _testUserId,
          entityId: _testEntityId,
        ),
      ).called(2); // Once initially, once for re-record.
    });
  });

  // -------------------------------------------------------------------------
  // 6. Max duration snackbar
  // -------------------------------------------------------------------------
  group('Max duration snackbar', () {
    testWidgets('shows audioTooLong snackbar when recording reaches 30s',
        (tester) async {
      when(
        () => mockRecorder.startRecording(
          userId: _testUserId,
          entityId: _testEntityId,
        ),
      ).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 0,
          fileSizeBytes: 100,
        ),
      );

      when(() => mockRecorder.stopRecording()).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 60000,
          fileSizeBytes: 240000,
        ),
      );

      await tester.pumpWidget(
        buildTestWidget(
          recorderService: mockRecorder,
          playerService: mockPlayer,
        ),
      );
      await tester.pumpAndSettle();

      // Start recording.
      await tester.tap(find.text('Record Audio Note'));
      await tester.pump();

      // Advance 30 seconds to trigger max duration.
      for (var i = 0; i < 30; i++) {
        await tester.pump(const Duration(seconds: 1));
      }

      // The snackbar with audioTooLong message should appear.
      expect(
        find.text('Maximum recording length is 30 seconds'),
        findsOneWidget,
      );

      // stopRecording should have been called automatically.
      verify(() => mockRecorder.stopRecording()).called(1);
    });
  });

  // -------------------------------------------------------------------------
  // 7. Delete recording flow
  // -------------------------------------------------------------------------
  group('Delete recording flow', () {
    testWidgets('shows confirmation dialog and deletes file on confirm',
        (tester) async {
      var deletedCalled = false;

      when(
        () => mockRecorder.startRecording(
          userId: _testUserId,
          entityId: _testEntityId,
        ),
      ).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 0,
          fileSizeBytes: 100,
        ),
      );

      when(() => mockRecorder.stopRecording()).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 5000,
          fileSizeBytes: 20000,
        ),
      );

      when(() => mockRecorder.deleteLocalAudio(_testAudioPath))
          .thenAnswer((_) async {});

      await tester.pumpWidget(
        buildHarnessWidget(
          recorderService: mockRecorder,
          playerService: mockPlayer,
          onDeleted: () => deletedCalled = true,
        ),
      );
      await tester.pumpAndSettle();

      // Record then stop.
      await tester.tap(find.text('Record Audio Note'));
      await tester.pump();
      await tester.pump(const Duration(seconds: 2));
      await tester.tap(find.text('Stop Recording'));
      await tester.pumpAndSettle();

      // Tap the delete button.
      await tester.tap(find.byIcon(Icons.delete_outline));
      await tester.pumpAndSettle();

      // Confirmation dialog should appear.
      expect(find.text('Delete Recording'), findsOneWidget);
      expect(find.text('Delete this voice note?'), findsOneWidget);
      expect(find.text('Cancel'), findsOneWidget);
      expect(find.text('Delete'), findsOneWidget);

      // Confirm deletion.
      await tester.tap(find.text('Delete'));
      await tester.pumpAndSettle();

      // Verify file was deleted.
      verify(() => mockRecorder.deleteLocalAudio(_testAudioPath)).called(1);

      // Verify onDeleted callback.
      expect(deletedCalled, isTrue);

      // Should return to empty state.
      expect(find.text('Record Audio Note'), findsOneWidget);
      expect(find.byIcon(Icons.mic), findsOneWidget);
    });

    testWidgets('cancelling delete dialog keeps recorded state',
        (tester) async {
      var deletedCalled = false;

      when(
        () => mockRecorder.startRecording(
          userId: _testUserId,
          entityId: _testEntityId,
        ),
      ).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 0,
          fileSizeBytes: 100,
        ),
      );

      when(() => mockRecorder.stopRecording()).thenAnswer(
        (_) async => AudioResult.success(
          localPath: _testAudioPath,
          durationMs: 5000,
          fileSizeBytes: 20000,
        ),
      );

      await tester.pumpWidget(
        buildHarnessWidget(
          recorderService: mockRecorder,
          playerService: mockPlayer,
          onDeleted: () => deletedCalled = true,
        ),
      );
      await tester.pumpAndSettle();

      // Record then stop.
      await tester.tap(find.text('Record Audio Note'));
      await tester.pump();
      await tester.pump(const Duration(seconds: 2));
      await tester.tap(find.text('Stop Recording'));
      await tester.pumpAndSettle();

      // Tap the delete button.
      await tester.tap(find.byIcon(Icons.delete_outline));
      await tester.pumpAndSettle();

      // Cancel deletion.
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();

      // Should still be in recorded state.
      expect(find.byIcon(Icons.play_arrow), findsOneWidget);
      expect(find.text('Audio Note'), findsOneWidget);
      expect(deletedCalled, isFalse);

      // deleteLocalAudio should NOT have been called.
      verifyNever(() => mockRecorder.deleteLocalAudio(any()));
    });
  });
}
