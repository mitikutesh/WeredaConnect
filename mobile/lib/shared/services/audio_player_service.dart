import 'package:audioplayers/audioplayers.dart';

/// Audio playback service for voice notes.
///
/// Thin wrapper around [AudioPlayer] exposing streams for UI binding.
/// Designed for local file playback of M4A voice notes.
class AudioPlayerService {
  final AudioPlayer _player;

  AudioPlayerService({AudioPlayer? player}) : _player = player ?? AudioPlayer();

  /// Stream of player state changes (playing, paused, stopped, etc.).
  Stream<PlayerState> get onPlayerStateChanged => _player.onPlayerStateChanged;

  /// Stream of playback position updates.
  Stream<Duration> get onPositionChanged => _player.onPositionChanged;

  /// Stream of total duration (emitted once source is loaded).
  Stream<Duration> get onDurationChanged => _player.onDurationChanged;

  /// Current player state.
  PlayerState get state => _player.state;

  /// Play a local audio file.
  Future<void> play(String filePath) async {
    await _player.play(DeviceFileSource(filePath));
  }

  /// Pause playback.
  Future<void> pause() async {
    await _player.pause();
  }

  /// Resume playback.
  Future<void> resume() async {
    await _player.resume();
  }

  /// Stop playback.
  Future<void> stop() async {
    await _player.stop();
  }

  /// Release player resources.
  Future<void> dispose() async {
    await _player.dispose();
  }
}
