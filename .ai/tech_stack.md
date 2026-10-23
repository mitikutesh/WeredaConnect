# Technology Stack

Single source of truth for dependency versions and technology choices.

---

## Mobile Stack

- **Flutter**: `3.38.9` (stable), **Dart**: `3.10.8`
- **Target**: Android 8+ (API 26+), 2GB RAM minimum, ARM64

### Key Dependencies

| Category | Package | Version | Notes |
|----------|---------|---------|-------|
| Database | `isar` / `isar_flutter_libs` | ^3.1.0 | Offline-first database |
| Code gen | `isar_generator` / `build_runner` | ^3.1.0 / ^2.4.0 | Dev dependency |
| Networking | `dio` | ^5.4.0 | HTTP client |
| Connectivity | `connectivity_plus` | ^7.0.0 | Network status |
| Retry | `retry` | ^3.1.0 | Flaky network handling |
| State | `flutter_bloc` / `equatable` | ^8.1.0 / ^2.0.5 | BLoC pattern |
| Localization | `intl` | ^0.19.0 | en, am (Amharic), om (Afan Oromo) |
| SVG | `flutter_svg` | ^2.0.0 | Smaller than PNG |
| Images | `cached_network_image` | ^3.3.0 | Image caching |
| Loading | `shimmer` | ^3.0.0 | Loading states |
| File paths | `path_provider` | ^2.1.0 | File system access |
| Push | `firebase_messaging` | ^15.x | FCM device token + push receipt (Android). Requires `google-services.json` — see `.ai/push-notifications-setup.md` |

### Build Targets
- `compileSdkVersion 34`, `minSdkVersion 26`, `targetSdkVersion 34`
- ABI filters: `armeabi-v7a`, `arm64-v8a`
- Release: `shrinkResources true`, `minifyEnabled true`

---

## Server Stack

- **.NET**: `10.0` (SDK 10.0.102), **C#**: `13.0`
- **Runtime**: Native AOT, **Deployment**: AWS Lambda ARM64

### Key Dependencies

| Category | Package | Version | Notes |
|----------|---------|---------|-------|
| Lambda | `Amazon.Lambda.Core` | 2.8.0 | |
| Lambda | `Amazon.Lambda.APIGatewayEvents` | 2.7.3 | |
| Lambda | `Amazon.Lambda.Serialization.SystemTextJson` | 2.4.5 | |
| JSON | `System.Text.Json` | 10.0.0 | AOT source generators |
| Database | `AWSSDK.DynamoDBv2` | 3.7.400 | |
| Database | `Dapper` | 2.1.0 | |
| Testing | `xUnit` | 2.9.0 | |
| Mocking | `NSubstitute` | 5.1.0 | AOT-compatible |

### AOT Publishing
- Target: `net10.0`, `linux-arm64`, self-contained, symbols stripped
- See `server/AOT_COMPLIANCE.md` for full banned library list

---

## Infrastructure Stack

- **AWS CDK**: `2.242.x` (TypeScript), **Node.js**: `20.20.0 LTS`, **TypeScript**: `7.0.x`
- TypeScript 7 (native `tsc`) has no JS compiler API, so `infra/package.json` installs it under the alias `@typescript/native` and aliases `@typescript/typescript6` (TS 6 JS API) as `typescript` for ts-jest and ts-node. `tsc` (lint/build) runs TS 7; jest and `cdk synth` compile with TS 6. Keep both aliases in place until ts-jest and ts-node support TS 7 directly.

### AWS Services

| Service | Config | Notes |
|---------|--------|-------|
| Lambda | ARM64, 512MB, 10s (API) / 60s (sync) | Reserved concurrency to cap costs |
| DynamoDB | On-demand billing | Low traffic optimization |
| S3 | Standard tier | User uploads |
| CloudFront | CDN | Static assets |
| API Gateway | HTTP API | Cheaper than REST API |
| CloudWatch | Logs, metrics, alarms | Monitoring |
| DynamoDB Streams | NEW_IMAGE on crops | Triggers match-on-insert push alerts |
| SNS Mobile Push | Platform Application (FCM) | Buyer match push alerts. Wraps FCM; credential in Secrets Manager. See `.ai/push-notifications-setup.md` |

> **External dependency**: Android push requires a Google **Firebase/FCM** project (free).
> AWS SNS relays to FCM; the backend stays on AWS. This is the only non-AWS dependency.

---

## Banned Technologies

| Scope | Banned | Use Instead | Reason |
|-------|--------|-------------|--------|
| Mobile | Hive | Isar | Better performance |
| Mobile | GetX | BLoC | Better testability |
| Mobile | Provider | BLoC | Consistency |
| Server | Newtonsoft.Json | System.Text.Json | Not AOT-compatible |
| Server | Entity Framework Core | Dapper / AWS SDK | Not AOT-compatible |
| Server | Moq | NSubstitute | Uses reflection |
| Server | AutoMapper | Manual mapping | Uses reflection |
| Infra | Serverless Framework | AWS CDK | Type safety |
| Infra | Terraform | AWS CDK | Consistency |

---

## Development Tools

- **Git** 2.40+, **GitHub** hosting, trunk-based development
- **CI/CD**: GitHub Actions, **Local AWS**: LocalStack
- **Code quality**: `flutter analyze`, `dotnet format`, ESLint/Prettier
- **E2E**: Maestro (mobile UI testing)

## Ethiopian Context Optimizations

- **Images**: WebP format, max 800px width
- **Fonts**: Only include needed Amharic/Oromo glyphs
- **APK target**: <50MB
- **API responses**: Gzip/Brotli, <100KB per response
- **Cold start**: <2 seconds (ARM64 + AOT)

## Version Update Policy

1. Security patches: immediately. Minor versions: quarterly. Major versions: annually.
2. Breaking changes documented in `specs/migrations/`.
