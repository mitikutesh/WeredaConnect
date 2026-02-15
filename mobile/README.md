# 📱 WeredaConnect Mobile

## Overview
Flutter-based Android application for WeredaConnect — an offline-first agritech logistics platform connecting Ethiopian farmers, transport providers, and wholesalers. Built with **offline-first** architecture for 3G networks and low-end devices.

---

## Project Structure

```
mobile/
├── lib/
│   ├── core/                 # Shared infrastructure
│   │   ├── database/         # Isar schemas and DAOs
│   │   ├── sync/             # Sync engine (offline → server)
│   │   ├── network/          # HTTP client, connectivity
│   │   └── error/            # Error handling, exceptions
│   ├── features/             # Feature modules (clean architecture)
│   │   └── [feature_name]/
│   │       ├── data/         # Data sources, repositories
│   │       ├── domain/       # Business logic, entities
│   │       └── presentation/ # UI, BLoC, widgets
│   └── shared/               # Shared across features
│       ├── l10n/             # Localization (am, om, en)
│       ├── widgets/          # Reusable UI components
│       └── utils/            # Helper functions
├── test/                     # Unit tests
├── integration_test/         # Integration tests
└── android/                  # Android-specific config
```

---

## Getting Started

### Prerequisites
- Flutter 3.27.x (stable channel)
- Dart 3.6.x
- Android Studio or VS Code
- Android SDK (API 26+)

### Setup
```bash
# Install dependencies
flutter pub get

# Generate code (Isar, l10n)
flutter pub run build_runner build --delete-conflicting-outputs

# Run on device/emulator (uses deployed AWS infrastructure by default)
flutter run
```

### Environment Configuration

The mobile app is configured to connect to the deployed AWS infrastructure in the `me-south-1` region by default.

**Default Configuration (Development)**:
- API Endpoint: `https://s0om0hib4i.execute-api.me-south-1.amazonaws.com`
- Cognito User Pool ID: `me-south-1_lKHy1XJrd`
- Cognito Client ID: `7g3kbk30cmbc297ghmid8pu1nm`
- Region: `me-south-1` (Middle East - Bahrain)

**Override Configuration at Build Time**:
```bash
# Use custom API endpoint
flutter run --dart-define=API_BASE_URL=https://custom-api.example.com

# Use custom Cognito credentials
flutter run \
  --dart-define=API_BASE_URL=https://custom-api.example.com \
  --dart-define=COGNITO_USER_POOL_ID=your-pool-id \
  --dart-define=COGNITO_CLIENT_ID=your-client-id \
  --dart-define=COGNITO_REGION=your-region

# Use local development server (LocalStack)
flutter run --dart-define=API_BASE_URL=http://10.0.2.2:4566
# Note: 10.0.2.2 is Android emulator's way to access host localhost
# For iOS simulator: http://localhost:4566
# For physical device: http://YOUR_COMPUTER_IP:4566
```

**Environment Variables**:
All environment configuration is managed in `/Users/mitiku.geleta/Projects/WeredaConnect/mobile/lib/core/config/environment.dart`. You can override any of the following at build time:
- `API_BASE_URL` - Backend API endpoint
- `COGNITO_USER_POOL_ID` - AWS Cognito User Pool ID
- `COGNITO_CLIENT_ID` - AWS Cognito Client ID
- `COGNITO_REGION` - AWS Cognito Region
- `ENVIRONMENT` - Environment mode (development, staging, production)

---

## Key Technologies

### Database
- **Isar**: Fast, offline-first NoSQL database
- **Why**: Better performance than Hive, built for Flutter
- **Location**: `lib/core/database/`

### State Management
- **BLoC**: Business Logic Component pattern
- **Why**: Testable, predictable, scales well
- **Location**: `lib/features/*/presentation/bloc/`

### Networking
- **Dio**: HTTP client with interceptors
- **Why**: Retry logic, request/response transformation
- **Location**: `lib/core/network/`

### Localization
- **flutter_localizations**: Official Flutter l10n
- **Supported**: Amharic (am), Afan Oromo (om), English (en)
- **Location**: `lib/shared/l10n/`

---

## Offline-First Architecture

### Principles
1. **Local-first**: All data stored in Isar database
2. **Sync queue**: Operations queued when offline, synced when online
3. **Conflict resolution**: Server wins, client merges
4. **Graceful degradation**: Features work offline, enhance online

### Data Flow
```
User Action → BLoC → Repository → Local DB (Isar)
                                      ↓
                                  Sync Queue
                                      ↓
                              (When online) → Server API
                                      ↓
                              Update Local DB
```

---

## Localization

### Supported Languages
- **Amharic** (አማርኛ): `am`
- **Afan Oromo** (Afaan Oromoo): `om`
- **English**: `en`

### Adding Translations
1. Edit `.arb` files in `lib/shared/l10n/`
2. Run `flutter pub get` to regenerate
3. Use `AppLocalizations.of(context)!.keyName` in code

### Cultural Considerations
- Use local terminology (Kebele, Wereda, Gullit)
- Adapt UX for low literacy (icons + text)
- Test with native speakers

---

## Performance Targets

### App Size
- **Target**: <50MB APK
- **Current**: TBD
- **Why**: 3G download costs

### Memory
- **Target**: <100MB RAM usage
- **Device**: 2GB RAM minimum
- **Why**: Low-end Android devices

### Load Time
- **Target**: <3s on 3G
- **Why**: User retention

### Battery
- **Target**: <5% per hour active use
- **Why**: Unreliable charging

---

## Testing

### Unit Tests
```bash
flutter test
```

### Integration Tests
```bash
flutter test integration_test/
```

### Offline Testing
1. Enable airplane mode
2. Test all critical flows
3. Verify sync queue works when back online

### Device Testing
- **Emulator**: Android 8.0, 2GB RAM, 480x800
- **Physical**: Tecno Spark 7 or similar

---

## Build & Release

### Debug Build
```bash
flutter build apk --debug
```

### Release Build
```bash
flutter build apk --release --split-per-abi
```

### Size Optimization
- `--split-per-abi`: Separate APKs for ARM32/ARM64
- `--obfuscate`: Code obfuscation
- `--tree-shake-icons`: Remove unused icons

---

## Common Issues

### Isar Build Errors
```bash
# Clean and rebuild
flutter clean
flutter pub get
flutter pub run build_runner build --delete-conflicting-outputs
```

### Localization Not Updating
```bash
# Regenerate l10n
flutter pub get
# Restart app (hot reload won't work)
```

### Sync Not Working
- Check `lib/core/sync/sync_service.dart`
- Verify connectivity with `connectivity_plus`
- Check CloudWatch logs on server

---

## Contributing

See `CLAUDE.md` for development principles and `TASK.md` for current work.

---

**Last Updated**: 2026-02-09  
**Version**: 0.1.0 (pre-alpha)

