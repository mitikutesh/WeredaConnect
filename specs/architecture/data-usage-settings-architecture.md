# Data Usage Tracking & Settings Architecture

## Version
1.0.0 | 2026-02-21

## Status
Implemented (R4)

---

## 1. Overview

Two cross-cutting features that support WeredaConnect's cost-conscious design for Ethiopian rural users:

- **Data Usage Tracking**: Monitors all HTTP traffic to inform users about mobile data consumption
- **Settings Management**: App-level preferences including data saver mode and language selection

Both features are fully offline-capable with SharedPreferences persistence.

---

## 2. Data Usage Tracking

### Architecture

```
┌──────────────────────────────────────────────────────────┐
│                       MOBILE (Flutter)                    │
│                                                           │
│  ┌────────────────────┐  ┌───────────────────────────┐   │
│  │ DataUsageTracker    │  │ DataUsageInterceptor      │   │
│  │ (ChangeNotifier)    │◄─│ (Dio Interceptor)         │   │
│  │                     │  │                           │   │
│  │ bytesDownloaded     │  │ onResponse: measure body  │   │
│  │ bytesUploaded       │  │ onRequest: measure body   │   │
│  │ trackingSince       │  │                           │   │
│  │                     │  │ Uses Content-Length header │   │
│  │ Persists to         │  │ or JSON encode fallback   │   │
│  │ SharedPreferences   │  │                           │   │
│  └─────────┬──────────┘  └───────────────────────────┘   │
│            │                                              │
│            ▼                                              │
│  ┌─────────────────────┐                                  │
│  │ DataUsageScreen      │                                  │
│  │                      │                                  │
│  │ Downloaded: 12.5 MB  │                                  │
│  │ Uploaded:   3.2 MB   │                                  │
│  │ Total:     15.7 MB   │                                  │
│  │                      │                                  │
│  │ Tracking since       │                                  │
│  │ Feb 15, 2026         │                                  │
│  │                      │                                  │
│  │ [Reset Statistics]   │                                  │
│  └─────────────────────┘                                  │
└──────────────────────────────────────────────────────────┘
```

### DataUsageTracker (Core Service)

**Pattern**: ChangeNotifier (Provider for reactive UI updates)
**Persistence**: SharedPreferences

| Key | Type | Purpose |
|-----|------|---------|
| `data_downloaded_bytes` | int | Total bytes received |
| `data_uploaded_bytes` | int | Total bytes sent |
| `data_tracking_since` | int (ms epoch) | When tracking started |

**API**:
```dart
Future<void> initialize()        // Load from SharedPreferences
void addDownloaded(int bytes)    // Increment + notify
void addUploaded(int bytes)      // Increment + notify
Future<void> reset()             // Clear all + reset date
static String formatBytes(int)   // "123 B" / "45.6 KB" / "12.34 MB"
```

### DataUsageInterceptor (Dio)

Hooks into all HTTP requests/responses automatically:

| Direction | Measurement Strategy |
|-----------|---------------------|
| Upload (request) | Estimate body: `utf8.encode(jsonEncode(data)).length` |
| Download (response) | Prefer `Content-Length` header; fall back to body estimation |

Handles null data gracefully, catches serialization errors silently.

### DataUsageScreen

Three stat cards (Downloaded/Uploaded/Total) with:
- Color-coded icons (blue/orange/green)
- Formatted byte display
- Tracking start date
- Reset button with confirmation dialog

---

## 3. Settings Management

### SettingsProvider

**Pattern**: ChangeNotifier (Provider)
**Persistence**: SharedPreferences

| Setting | Key | Type | Default |
|---------|-----|------|---------|
| Data Saver Mode | `data_saver_mode` | bool | false |

```dart
class SettingsProvider extends ChangeNotifier {
  Future<void> initialize()                     // Load from SharedPreferences
  Future<void> setDataSaver({required bool})    // Toggle + persist
  bool get dataSaverEnabled                      // Current state
}
```

**Design**: Idempotent setter (returns early if value unchanged). Extensible for future settings.

### Settings Screen

| Menu Item | Action |
|-----------|--------|
| Language | Opens LanguageSelectionDialog (Amharic, Afan Oromo, English) |
| Data Usage | Navigates to DataUsageScreen |
| Data Saver Mode | Toggle switch with description |
| About | App info dialog (version, description, tagline) |

---

## 4. Voice Note System

Shared service for recording audio descriptions in local languages, addressing low-literacy needs.

### Architecture

```
┌────────────────────────────────────────────────────┐
│                 VoiceNoteWidget                      │
│  States: Empty → Recording → Recorded → Playing     │
│                                                      │
│  ┌──────────────────┐  ┌──────────────────────┐    │
│  │ AudioRecorder     │  │ AudioPlayer          │    │
│  │ Service           │  │ Service              │    │
│  │                   │  │                      │    │
│  │ record/stop/      │  │ play/pause/stop/     │    │
│  │ delete             │  │ position stream      │    │
│  └──────────────────┘  └──────────────────────┘    │
└────────────────────────────────────────────────────┘
```

### Audio Codec & Storage

| Parameter | Value |
|-----------|-------|
| Codec | AAC-LC |
| Bitrate | 32 kbps (data-budget optimized) |
| Sample Rate | 22050 Hz |
| Channels | Mono |
| Container | M4A |
| Max Duration | 60 seconds |
| File Size | ~240 KB per recording |
| Storage | `{appDir}/audio/{userId}/{entityId}.m4a` |

### AudioRecorderService

```dart
Future<AudioResult> startRecording({userId, entityId})
Future<AudioResult> stopRecording()
Future<bool> isRecording()
Future<bool> hasPermission()
Future<String?> getLocalAudioPath({userId, entityId})
Future<void> deleteLocalAudio(String path)
```

**Security**:
- UUID validation: regex `^[0-9a-f]{8}-...$` prevents directory traversal
- Canonical path validation before delete (resolves symlinks, checks prefix)
- Microphone permission requested before recording

### AudioPlayerService

Thin reactive wrapper exposing streams:
- `onPlayerStateChanged` — playing, paused, stopped, completed
- `onPositionChanged` — current playback position
- `onDurationChanged` — total duration

### VoiceNoteWidget (4-State Machine)

| State | UI |
|-------|-----|
| Empty | "Record Audio Note" button |
| Recording | Animated red dot, elapsed timer, "Stop" button |
| Recorded | Play button, duration display, delete button |
| Playing | Pause button, progress bar, current/total time |

**Max duration enforced**: Auto-stops at 60 seconds.

### VoiceNotePlaybackWidget (Compact)

Read-only playback for listing cards. Play/Stop icon + duration. No recording or delete.

---

## 5. App Initialization Order

```
main() async {
  1. DataUsageTracker.initialize()        // Load byte counters
  2. ApiClient.initialize([               // HTTP client
       DataUsageInterceptor(tracker)      // Attach tracking
     ])
  3. IsarService.initialize()             // Local database
  4. ConnectivityMonitor()                 // Network state
  5. EscrowSyncService()                   // Payment sync
  6. ConflictNotificationService()         // Sync conflict UI
  7. LanguageProvider.initialize()          // Restore language
  8. SettingsProvider.initialize()          // Restore settings
}

// Provider registration:
MultiProvider([
  ChangeNotifierProvider.value(value: languageProvider),
  ChangeNotifierProvider.value(value: settingsProvider),
  ChangeNotifierProvider.value(value: dataUsageTracker),
])
```

---

## 6. Offline Capability

| Feature | Offline | Explanation |
|---------|---------|-------------|
| Data Usage Tracking | Full | Counters in SharedPreferences, no server sync |
| Settings | Full | SharedPreferences, local-only |
| Voice Notes | Full | Record/play entirely local, no network |
| Language Selection | Full | ARB files bundled, SharedPreferences |

---

## 7. Low-End Device Considerations

| Concern | Mitigation |
|---------|-----------|
| Audio file size | AAC-LC @ 32kbps (~240KB/60s) |
| Image cache | Limited to 50 images, 50MB total |
| Recording duration | Max 60 seconds (enforced) |
| Stream subscriptions | Canceled on widget dispose |
| SharedPreferences | Minimal keys, no heavy serialization |

---

## 8. Localization

All features fully localized in English, Amharic, and Afan Oromo.

**Key sections** (from ARB files):
- Data Usage: 7 keys (`dataUsage`, `dataUsageDownloaded`, etc.)
- Audio Notes: 11 keys (`recordAudioNote`, `recording`, etc.)
- Settings: integrated with existing l10n keys

---

## 9. File Structure

```
mobile/lib/core/network/
├── data_usage_tracker.dart        # Core tracking service
└── data_usage_interceptor.dart    # Dio interceptor

mobile/lib/screens/
├── data_usage_screen.dart         # Data usage display
└── settings_screen.dart           # Settings menu

mobile/lib/shared/
├── providers/
│   ├── settings_provider.dart     # App settings persistence
│   └── language_provider.dart     # Language persistence
├── services/
│   ├── audio_recorder_service.dart
│   └── audio_player_service.dart
└── widgets/
    ├── voice_note_widget.dart     # Full recording UI
    └── voice_note_playback_widget.dart  # Compact playback
```

---

## 10. Design Patterns

| Pattern | Usage |
|---------|-------|
| ChangeNotifier | DataUsageTracker, SettingsProvider, LanguageProvider |
| Dio Interceptor | DataUsageInterceptor (auto-measures all traffic) |
| State Machine | VoiceNoteWidget (4 states: empty/recording/recorded/playing) |
| Stream-based Reactivity | AudioPlayerService (position, state, duration streams) |
| UUID Validation | AudioRecorderService (prevents path traversal attacks) |

---

**Last Updated**: 2026-02-21
