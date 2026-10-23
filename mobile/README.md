# WeredaConnect Mobile

Flutter-based Android app for WeredaConnect -- offline-first agritech logistics connecting Ethiopian farmers, transport providers, and wholesalers. Built for 3G networks and low-end devices.

> **Setup instructions**: See `docs/developer/getting-started.md`

---

## Project Structure

```
mobile/
├── lib/
│   ├── core/                 # Shared infrastructure
│   │   ├── database/         # Isar schemas and DAOs
│   │   ├── sync/             # Sync engine (offline -> server)
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

## Key Technologies

| Component | Choice | Location |
|-----------|--------|----------|
| Database | **Isar** (offline-first NoSQL) | `lib/core/database/` |
| State | **BLoC** (testable, predictable) | `lib/features/*/presentation/bloc/` |
| HTTP | **Dio** (retry, interceptors) | `lib/core/network/` |
| L10n | **flutter_localizations** (am, om, en) | `lib/shared/l10n/` |

---

## Offline-First Architecture

1. **Local-first**: All data stored in Isar
2. **Sync queue**: Operations queued offline, synced when online
3. **Conflict resolution**: Server wins, client merges
4. **Graceful degradation**: Features work offline, enhance online

```
User Action -> BLoC -> Repository -> Local DB (Isar)
                                        |
                                    Sync Queue
                                        |
                                (When online) -> Server API
                                        |
                                Update Local DB
```

---

## Environment Configuration

Default (dev): connects to deployed AWS infra in `eu-north-1` (Stockholm).

```bash
# Override at build time
flutter run --dart-define=API_BASE_URL=https://custom-api.example.com
flutter run --dart-define=API_BASE_URL=http://10.0.2.2:4566  # LocalStack (emulator)
```

All config in `lib/core/config/environment.dart`:
`API_BASE_URL`, `COGNITO_USER_POOL_ID`, `COGNITO_CLIENT_ID`, `COGNITO_REGION`, `ENVIRONMENT`

`ENVIRONMENT` takes `dev`, `staging` or `prod` (what CI and `scripts/deploy.sh` pass; the names
`development` and `production` work too). Leave it out for a local run (development). Any other
value stops a debug build at startup and gives a release build the production settings.

---

## Performance Targets

| Metric | Target | Rationale |
|--------|--------|-----------|
| APK size | <50MB | 3G download costs |
| RAM usage | <100MB | 2GB RAM devices |
| Load time | <3s on 3G | User retention |
| Battery | <5%/hr active | Unreliable charging |

---

## Common Issues

**Isar build errors**: `flutter clean && flutter pub get && flutter pub run build_runner build --delete-conflicting-outputs`

**L10n not updating**: `flutter pub get` then full restart (hot reload insufficient)

**Sync not working**: Check `lib/core/sync/sync_service.dart`, verify connectivity, check CloudWatch logs

---

**Last Updated**: 2026-02-09
**Version**: 0.1.0 (pre-alpha)
