import 'dart:io';

import 'package:path_provider/path_provider.dart';
import 'package:record/record.dart';

/// Audio recording service for voice notes.
///
/// Records AAC-LC at 32kbps in M4A container, max 60 seconds (~240KB).
/// Modeled on [PhotoManager] for consistency:
/// - UUID validation before path construction (path traversal prevention)
/// - App-private storage directory
/// - Canonical path validation on delete
class AudioRecorderService {
  final AudioRecorder _recorder;

  /// UUID v4 validation pattern (lowercase hex).
  static final RegExp _uuidRegex = RegExp(
    r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$',
  );

  static const int maxDurationSeconds = 60;
  static const int audioBitrate = 32000;
  static const String _audioDir = 'audio';

  AudioRecorderService({AudioRecorder? recorder})
      : _recorder = recorder ?? AudioRecorder();

  /// Start recording a voice note.
  ///
  /// Validates [entityId] as UUID to prevent path traversal.
  /// Saves to `{appDir}/audio/{userId}/{entityId}.m4a`.
  Future<AudioResult> startRecording({
    required String userId,
    required String entityId,
  }) async {
    if (!_uuidRegex.hasMatch(entityId)) {
      return const AudioResult.error('Invalid entity ID format');
    }
    if (!_uuidRegex.hasMatch(userId)) {
      return const AudioResult.error('Invalid user ID format');
    }

    try {
      if (await _recorder.hasPermission()) {
        final audioDir = await _getAudioDir(userId);
        final filePath = '${audioDir.path}/$entityId.m4a';

        // AAC-LC is the default encoder in RecordConfig.
        await _recorder.start(
          const RecordConfig(
            bitRate: audioBitrate,
            sampleRate: 22050,
            numChannels: 1,
          ),
          path: filePath,
        );

        return AudioResult.success(
          localPath: filePath,
          durationMs: 0,
          fileSizeBytes: 0,
        );
      } else {
        return const AudioResult.error('Microphone permission denied');
      }
    } on Exception catch (e) {
      return AudioResult.error('Recording failed: $e');
    }
  }

  /// Stop the current recording.
  ///
  /// Returns [AudioResult] with the local path, duration, and file size.
  Future<AudioResult> stopRecording() async {
    try {
      final path = await _recorder.stop();
      if (path == null) {
        return const AudioResult.error('Recording stop returned no path');
      }

      final file = File(path);
      if (!file.existsSync()) {
        return const AudioResult.error('Recording file not found');
      }

      final fileSizeBytes = await file.length();

      return AudioResult.success(
        localPath: path,
        durationMs: 0, // Duration tracked by UI timer
        fileSizeBytes: fileSizeBytes,
      );
    } on Exception catch (e) {
      return AudioResult.error('Stop recording failed: $e');
    }
  }

  /// Check if currently recording.
  Future<bool> isRecording() async {
    return _recorder.isRecording();
  }

  /// Check if microphone permission is granted.
  Future<bool> hasPermission() async {
    return _recorder.hasPermission();
  }

  /// Get local audio file path if it exists.
  Future<String?> getLocalAudioPath({
    required String userId,
    required String entityId,
  }) async {
    if (!_uuidRegex.hasMatch(entityId) || !_uuidRegex.hasMatch(userId)) {
      return null;
    }

    final audioDir = await _getAudioDir(userId);
    final filePath = '${audioDir.path}/$entityId.m4a';
    final file = File(filePath);
    return file.existsSync() ? filePath : null;
  }

  /// Delete a local audio file.
  ///
  /// Validates that the resolved canonical path is within the app's
  /// audio directory before deletion to prevent arbitrary file deletion.
  Future<void> deleteLocalAudio(String path) async {
    if (path.isEmpty) return;
    final appDir = await getApplicationDocumentsDirectory();
    final audioPrefix = '${appDir.path}/$_audioDir/';
    final canonicalPath = File(path).absolute.path;
    if (!canonicalPath.startsWith(audioPrefix)) {
      return;
    }
    final file = File(canonicalPath);
    if (file.existsSync()) {
      await file.delete();
    }
  }

  /// Release recorder resources.
  void dispose() {
    _recorder.dispose();
  }

  /// Get the audio storage directory for a user, creating it if needed.
  Future<Directory> _getAudioDir(String userId) async {
    final appDir = await getApplicationDocumentsDirectory();
    final audioDir = Directory('${appDir.path}/$_audioDir/$userId');
    if (!audioDir.existsSync()) {
      await audioDir.create(recursive: true);
    }
    return audioDir;
  }
}

/// Result of an audio recording operation.
class AudioResult {
  final bool success;
  final String? localPath;
  final int? durationMs;
  final int? fileSizeBytes;
  final String? errorMessage;

  const AudioResult.success({
    required this.localPath,
    required this.durationMs,
    required this.fileSizeBytes,
  })  : success = true,
        errorMessage = null;

  const AudioResult.error(this.errorMessage)
      : success = false,
        localPath = null,
        durationMs = null,
        fileSizeBytes = null;
}
