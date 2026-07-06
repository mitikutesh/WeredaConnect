import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/audio_player_service.dart';
import 'package:wereda_connect/shared/services/audio_recorder_service.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Self-contained voice note widget handling record/playback lifecycle.
///
/// States:
/// 1. **Empty** — shows record button (mic icon)
/// 2. **Recording** — stop button + elapsed timer + animated red dot
/// 3. **Recorded/Has file** — play/pause + duration + delete + re-record
/// 4. **Playing** — pause button + progress indicator + re-record
class VoiceNoteWidget extends StatefulWidget {
  /// Path to existing audio file (null if no recording yet).
  final String? audioFilePath;

  /// User ID for directory scoping.
  final String userId;

  /// Entity ID (listing ID) for file naming.
  final String entityId;

  /// Called when a recording is completed with the local file path.
  final ValueChanged<String> onRecorded;

  /// Called when a recording is deleted.
  final VoidCallback onDeleted;

  /// Injectable recorder service (for testing).
  final AudioRecorderService? recorderService;

  /// Injectable player service (for testing).
  final AudioPlayerService? playerService;

  const VoiceNoteWidget({
    required this.userId,
    required this.entityId,
    required this.onRecorded,
    required this.onDeleted,
    this.audioFilePath,
    this.recorderService,
    this.playerService,
    super.key,
  });

  @override
  State<VoiceNoteWidget> createState() => _VoiceNoteWidgetState();
}

enum _VoiceNoteState { empty, recording, recorded, playing }

class _VoiceNoteWidgetState extends State<VoiceNoteWidget>
    with SingleTickerProviderStateMixin {
  late final AudioRecorderService _recorderService;
  late final AudioPlayerService _playerService;

  _VoiceNoteState _state = _VoiceNoteState.empty;
  int _recordingSeconds = 0;
  int _recordedDuration = 0;
  Timer? _recordingTimer;
  Duration _playbackPosition = Duration.zero;
  Duration _playbackDuration = Duration.zero;

  StreamSubscription<bool>? _completedSub;
  StreamSubscription<Duration>? _positionSub;
  StreamSubscription<Duration?>? _durationSub;

  late final AnimationController _pulseController;

  @override
  void initState() {
    super.initState();
    _recorderService = widget.recorderService ?? AudioRecorderService();
    _playerService = widget.playerService ?? AudioPlayerService();

    _pulseController = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 800),
      lowerBound: 0.3,
    );

    if (widget.audioFilePath != null &&
        File(widget.audioFilePath!).existsSync()) {
      _state = _VoiceNoteState.recorded;
    }
    _setupPlayerListeners();
  }

  void _setupPlayerListeners() {
    _completedSub = _playerService.onCompleted.listen((completed) {
      if (!mounted || !completed) return;
      setState(() {
        _state = _VoiceNoteState.recorded;
        _playbackPosition = Duration.zero;
      });
    });
    _positionSub = _playerService.onPositionChanged.listen((position) {
      if (!mounted) return;
      setState(() {
        _playbackPosition = position;
      });
    });
    _durationSub = _playerService.onDurationChanged.listen((duration) {
      if (!mounted || duration == null) return;
      setState(() {
        _playbackDuration = duration;
      });
    });
  }

  @override
  void didUpdateWidget(VoiceNoteWidget oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.audioFilePath != oldWidget.audioFilePath) {
      if (widget.audioFilePath != null &&
          File(widget.audioFilePath!).existsSync()) {
        _state = _VoiceNoteState.recorded;
      } else {
        _state = _VoiceNoteState.empty;
      }
    }
  }

  @override
  void dispose() {
    _recordingTimer?.cancel();
    _pulseController.dispose();
    _completedSub?.cancel();
    _positionSub?.cancel();
    _durationSub?.cancel();
    _recorderService.dispose();
    _playerService.dispose();
    super.dispose();
  }

  /// Display duration: prefer playback duration (more accurate from codec),
  /// fall back to recorded timer value.
  int get _displayDuration {
    if (_playbackDuration > Duration.zero) {
      return _playbackDuration.inSeconds;
    }
    return _recordedDuration;
  }

  Future<void> _startRecording() async {
    final l10n = AppLocalizations.of(context)!;

    final result = await _recorderService.startRecording(
      userId: widget.userId,
      entityId: widget.entityId,
    );

    if (!result.success) {
      if (!mounted) return;
      final message = result.errorMessage?.contains('permission') == true
          ? l10n.microphonePermissionRequired
          : l10n.audioRecordingFailed;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(message)),
      );
      return;
    }

    setState(() {
      _state = _VoiceNoteState.recording;
      _recordingSeconds = 0;
    });

    _pulseController.repeat(reverse: true);

    _recordingTimer = Timer.periodic(const Duration(seconds: 1), (timer) {
      if (!mounted) {
        timer.cancel();
        return;
      }
      setState(() {
        _recordingSeconds++;
      });
      if (_recordingSeconds >= AudioRecorderService.maxDurationSeconds) {
        _stopRecording();
        if (mounted) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.audioTooLong)),
          );
        }
      }
    });
  }

  Future<void> _stopRecording() async {
    _recordingTimer?.cancel();
    _recordingTimer = null;
    _pulseController.stop();
    _pulseController.value = _pulseController.lowerBound;

    final result = await _recorderService.stopRecording();

    if (!mounted) return;

    if (result.success && result.localPath != null) {
      setState(() {
        _recordedDuration = _recordingSeconds;
        _state = _VoiceNoteState.recorded;
      });
      widget.onRecorded(result.localPath!);
    } else {
      final l10n = AppLocalizations.of(context)!;
      setState(() {
        _state = _VoiceNoteState.empty;
      });
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(l10n.audioRecordingFailed)),
      );
    }
  }

  Future<void> _playAudio() async {
    if (widget.audioFilePath == null) return;
    // Resume from the paused position when playback was paused mid-clip;
    // otherwise start the file from the beginning.
    if (_playbackPosition > Duration.zero) {
      await _playerService.resume();
    } else {
      await _playerService.play(widget.audioFilePath!);
    }
    if (!mounted) return;
    setState(() {
      _state = _VoiceNoteState.playing;
    });
  }

  Future<void> _pauseAudio() async {
    await _playerService.pause();
    if (!mounted) return;
    setState(() {
      _state = _VoiceNoteState.recorded;
    });
  }

  Future<void> _reRecord() async {
    await _playerService.stop();
    if (widget.audioFilePath != null) {
      await _recorderService.deleteLocalAudio(widget.audioFilePath!);
    }
    if (!mounted) return;
    setState(() {
      _playbackPosition = Duration.zero;
      _playbackDuration = Duration.zero;
      _recordedDuration = 0;
    });
    widget.onDeleted();
    await _startRecording();
  }

  Future<void> _deleteRecording() async {
    final l10n = AppLocalizations.of(context)!;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(l10n.deleteRecording),
        content: Text(l10n.deleteRecordingConfirm),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: Text(l10n.cancel),
          ),
          TextButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: Text(l10n.delete),
          ),
        ],
      ),
    );

    if (confirmed != true) return;

    await _playerService.stop();
    if (widget.audioFilePath != null) {
      await _recorderService.deleteLocalAudio(widget.audioFilePath!);
    }

    if (!mounted) return;
    setState(() {
      _state = _VoiceNoteState.empty;
      _playbackPosition = Duration.zero;
      _playbackDuration = Duration.zero;
      _recordedDuration = 0;
    });
    widget.onDeleted();
  }

  @override
  Widget build(BuildContext context) {
    return switch (_state) {
      _VoiceNoteState.empty => _buildEmptyState(),
      _VoiceNoteState.recording => _buildRecordingState(),
      _VoiceNoteState.recorded => _buildRecordedState(),
      _VoiceNoteState.playing => _buildPlayingState(),
    };
  }

  Widget _buildEmptyState() {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        OutlinedButton.icon(
          onPressed: _startRecording,
          icon: const Icon(Icons.mic),
          label: Text(l10n.recordAudioNote),
          style: OutlinedButton.styleFrom(
            padding: const EdgeInsets.symmetric(vertical: 16),
            minimumSize: const Size.fromHeight(48),
          ),
        ),
        const SizedBox(height: 4),
        Text(
          l10n.audioRecordingLimit,
          style: theme.textTheme.bodySmall?.copyWith(
            color: theme.colorScheme.onSurface.withValues(alpha: 0.5),
          ),
          textAlign: TextAlign.center,
        ),
      ],
    );
  }

  Widget _buildRecordingState() {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        border: Border.all(color: AppTheme.statusError),
        borderRadius: BorderRadius.circular(12),
        color: AppTheme.statusErrorContainer,
      ),
      child: Row(
        children: [
          // Animated red dot
          AnimatedBuilder(
            animation: _pulseController,
            builder: (context, child) {
              return Container(
                width: 12,
                height: 12,
                decoration: BoxDecoration(
                  color: AppTheme.statusError
                      .withValues(alpha: _pulseController.value),
                  shape: BoxShape.circle,
                ),
              );
            },
          ),
          const SizedBox(width: 12),
          // Recording label + timer
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  l10n.recording,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: AppTheme.onStatusErrorContainer,
                    fontWeight: FontWeight.w600,
                  ),
                ),
                Text(
                  l10n.secondsAbbreviation(_recordingSeconds),
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: AppTheme.onStatusErrorContainer,
                  ),
                ),
              ],
            ),
          ),
          // Stop button
          FilledButton.icon(
            onPressed: _stopRecording,
            icon: const Icon(Icons.stop, size: 20),
            label: Text(l10n.stopRecording),
            style: FilledButton.styleFrom(
              backgroundColor: AppTheme.statusError,
              minimumSize: const Size(0, 48),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildRecordedState() {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        border:
            Border.all(color: theme.colorScheme.outline.withValues(alpha: 0.3)),
        borderRadius: BorderRadius.circular(12),
      ),
      child: Row(
        children: [
          // Play button
          IconButton(
            onPressed: _playAudio,
            icon: const Icon(Icons.play_arrow),
            iconSize: 32,
            style: IconButton.styleFrom(
              backgroundColor: theme.colorScheme.primaryContainer,
              foregroundColor: theme.colorScheme.onPrimaryContainer,
            ),
          ),
          const SizedBox(width: 12),
          // Voice note label + duration
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  l10n.audioNote,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    fontWeight: FontWeight.w600,
                  ),
                ),
                if (_displayDuration > 0)
                  Text(
                    l10n.secondsAbbreviation(_displayDuration),
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                    ),
                  ),
              ],
            ),
          ),
          // Re-record button
          IconButton(
            onPressed: _reRecord,
            icon: const Icon(Icons.mic),
            iconSize: 20,
            tooltip: l10n.reRecord,
            color: theme.colorScheme.primary,
          ),
          // Delete button
          IconButton(
            onPressed: _deleteRecording,
            icon: const Icon(Icons.delete_outline),
            iconSize: 20,
            tooltip: l10n.deleteRecording,
            color: AppTheme.statusError,
          ),
        ],
      ),
    );
  }

  Widget _buildPlayingState() {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final progress = _playbackDuration.inMilliseconds > 0
        ? _playbackPosition.inMilliseconds / _playbackDuration.inMilliseconds
        : 0.0;

    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        border:
            Border.all(color: theme.colorScheme.primary.withValues(alpha: 0.3)),
        borderRadius: BorderRadius.circular(12),
        color: theme.colorScheme.primaryContainer.withValues(alpha: 0.2),
      ),
      child: Row(
        children: [
          // Pause button
          IconButton(
            onPressed: _pauseAudio,
            icon: const Icon(Icons.pause),
            iconSize: 32,
            style: IconButton.styleFrom(
              backgroundColor: theme.colorScheme.primaryContainer,
              foregroundColor: theme.colorScheme.onPrimaryContainer,
            ),
          ),
          const SizedBox(width: 12),
          // Progress bar + time
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                ClipRRect(
                  borderRadius: BorderRadius.circular(4),
                  child: LinearProgressIndicator(
                    value: progress.clamp(0.0, 1.0),
                    minHeight: 4,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  '${l10n.secondsAbbreviation(_playbackPosition.inSeconds)} / ${l10n.secondsAbbreviation(_playbackDuration.inSeconds)}',
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                  ),
                ),
              ],
            ),
          ),
          // Re-record button
          IconButton(
            onPressed: _reRecord,
            icon: const Icon(Icons.mic),
            iconSize: 20,
            tooltip: l10n.reRecord,
            color: theme.colorScheme.primary,
          ),
          // Delete button
          IconButton(
            onPressed: _deleteRecording,
            icon: const Icon(Icons.delete_outline),
            iconSize: 20,
            tooltip: l10n.deleteRecording,
            color: AppTheme.statusError,
          ),
        ],
      ),
    );
  }
}

/// Compact playback-only widget for displaying voice notes on listing cards.
///
/// Shows a small play/stop button with duration. No recording or delete.
class VoiceNotePlaybackWidget extends StatefulWidget {
  final String audioFilePath;

  const VoiceNotePlaybackWidget({
    required this.audioFilePath,
    super.key,
  });

  @override
  State<VoiceNotePlaybackWidget> createState() =>
      _VoiceNotePlaybackWidgetState();
}

class _VoiceNotePlaybackWidgetState extends State<VoiceNotePlaybackWidget> {
  final AudioPlayerService _playerService = AudioPlayerService();
  bool _isPlaying = false;
  Duration _position = Duration.zero;
  Duration _duration = Duration.zero;

  StreamSubscription<bool>? _completedSub;
  StreamSubscription<Duration>? _posSub;
  StreamSubscription<Duration?>? _durSub;

  @override
  void initState() {
    super.initState();
    _completedSub = _playerService.onCompleted.listen((completed) {
      if (!mounted || !completed) return;
      setState(() {
        _isPlaying = false;
        _position = Duration.zero;
      });
    });
    _posSub = _playerService.onPositionChanged.listen((pos) {
      if (!mounted) return;
      setState(() => _position = pos);
    });
    _durSub = _playerService.onDurationChanged.listen((dur) {
      if (!mounted || dur == null) return;
      setState(() => _duration = dur);
    });
  }

  @override
  void dispose() {
    _completedSub?.cancel();
    _posSub?.cancel();
    _durSub?.cancel();
    _playerService.dispose();
    super.dispose();
  }

  Future<void> _toggle() async {
    if (_isPlaying) {
      await _playerService.stop();
      if (!mounted) return;
      setState(() => _isPlaying = false);
    } else {
      await _playerService.play(widget.audioFilePath);
      if (!mounted) return;
      setState(() => _isPlaying = true);
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return InkWell(
      onTap: _toggle,
      borderRadius: BorderRadius.circular(8),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
        decoration: BoxDecoration(
          color: theme.colorScheme.primaryContainer.withValues(alpha: 0.3),
          borderRadius: BorderRadius.circular(8),
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              _isPlaying ? Icons.stop : Icons.play_arrow,
              size: 16,
              color: theme.colorScheme.primary,
            ),
            const SizedBox(width: 4),
            Text(
              _isPlaying && _duration > Duration.zero
                  ? l10n.secondsAbbreviation(_position.inSeconds)
                  : l10n.audioNote,
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.primary,
                fontWeight: FontWeight.w500,
              ),
            ),
          ],
        ),
      ),
    );
  }
}
