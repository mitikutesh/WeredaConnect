import 'dart:async';

import 'package:audio_session/audio_session.dart';
import 'package:just_audio/just_audio.dart';

/// Audio playback service for voice notes.
///
/// Thin wrapper around just_audio's [AudioPlayer] exposing streams for UI
/// binding. Uses ExoPlayer on Android for reliable M4A playback.
class AudioPlayerService {
  final AudioPlayer _player;
  bool _sessionConfigured = false;

  AudioPlayerService({AudioPlayer? player}) : _player = player ?? AudioPlayer();

  /// Configure audio session for voice note playback.
  Future<void> _ensureSession() async {
    if (_sessionConfigured) return;
    final session = await AudioSession.instance;
    await session.configure(const AudioSessionConfiguration.speech());
    _sessionConfigured = true;
  }

  /// Stream that emits `true` when playback completes naturally.
  Stream<bool> get onCompleted =>
      _player.processingStateStream.map((s) => s == ProcessingState.completed);

  /// Stream of playback position updates.
  Stream<Duration> get onPositionChanged => _player.positionStream;

  /// Stream of total duration changes.
  Stream<Duration?> get onDurationChanged => _player.durationStream;

  /// Whether the player is currently playing.
  bool get isPlaying => _player.playing;

  /// Play a local audio file from the beginning.
  ///
  /// Sets up the audio source and starts playback. Returns after playback
  /// begins (does not block until completion). Use [onCompleted] to detect
  /// when playback finishes.
  Future<void> play(String filePath) async {
    await _ensureSession();
    await _player.setFilePath(filePath);
    await _player.seek(Duration.zero);
    // Do not await play() — just_audio's play() future resolves only when
    // playback completes, which would block the caller from updating UI.
    unawaited(_player.play());
  }

  /// Pause playback.
  Future<void> pause() async {
    await _player.pause();
  }

  /// Resume playback from the current position.
  Future<void> resume() async {
    await _player.play();
  }

  /// Stop playback and reset position.
  Future<void> stop() async {
    await _player.stop();
  }

  /// Release player resources.
  Future<void> dispose() async {
    await _player.dispose();
  }
}
