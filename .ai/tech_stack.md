# 🛠️ Technology Stack

## Purpose
This document defines the **exact versions and technologies** used in WeredaConnect. This is the single source of truth for dependency management.

---

## Mobile Stack

### Core Framework
- **Flutter**: `3.38.9` (stable channel)
- **Dart**: `3.10.8`
- **Target Platform**: Android 8+ (API 26+)
- **Minimum Device**: 2GB RAM, ARM64

### Key Dependencies

#### Database & Storage
```yaml
dependencies:
  isar: ^4.0.0              # Offline-first database
  isar_flutter_libs: ^4.0.0
  path_provider: ^2.1.0     # File system access

dev_dependencies:
  isar_generator: ^4.0.0
  build_runner: ^2.4.0
```

#### Networking & Sync
```yaml
dependencies:
  dio: ^5.4.0               # HTTP client
  connectivity_plus: ^5.0.0 # Network status
  retry: ^3.1.0             # Retry logic for flaky networks
```

#### State Management
```yaml
dependencies:
  flutter_bloc: ^8.1.0      # BLoC pattern
  equatable: ^2.0.5         # Value equality
```

#### Localization
```yaml
dependencies:
  flutter_localizations:
    sdk: flutter
  intl: ^0.19.0

# Supported locales: en, am (Amharic), om (Afan Oromo)
```

#### UI & UX
```yaml
dependencies:
  flutter_svg: ^2.0.0       # Vector graphics (smaller than PNG)
  cached_network_image: ^3.3.0 # Image caching
  shimmer: ^3.0.0           # Loading states
```

### Build Configuration

#### Android (`android/app/build.gradle`)
```gradle
android {
    compileSdkVersion 34
    
    defaultConfig {
        minSdkVersion 26  // Android 8.0
        targetSdkVersion 34
        
        // Optimize APK size
        ndk {
            abiFilters 'armeabi-v7a', 'arm64-v8a'
        }
    }
    
    buildTypes {
        release {
            shrinkResources true
            minifyEnabled true
            proguardFiles getDefaultProguardFile('proguard-android.txt')
        }
    }
}
```

---

## Server Stack

### Core Framework
- **.NET**: `10.0` (SDK 10.0.102)
- **C#**: `13.0`
- **Runtime**: Native AOT (Ahead-of-Time compilation)
- **Deployment**: AWS Lambda (ARM64)

### Key Dependencies

#### AWS Lambda
```xml
<PackageReference Include="Amazon.Lambda.Core" Version="2.3.0" />
<PackageReference Include="Amazon.Lambda.APIGatewayEvents" Version="2.8.0" />
<PackageReference Include="Amazon.Lambda.Serialization.SystemTextJson" Version="2.5.0" />
```

#### JSON Serialization (AOT-Compatible)
```xml
<!-- ❌ BANNED: Newtonsoft.Json (uses reflection) -->
<!-- ✅ USE: System.Text.Json with source generators -->
<PackageReference Include="System.Text.Json" Version="10.0.0" />
```

#### Database Access
```xml
<!-- ❌ BANNED: Entity Framework Core (not AOT-compatible) -->
<!-- ✅ USE: Dapper or AWS SDK -->
<PackageReference Include="Dapper" Version="2.1.0" />
<PackageReference Include="AWSSDK.DynamoDBv2" Version="3.7.400" />
```

#### Testing
```xml
<PackageReference Include="xUnit" Version="2.9.0" />
<PackageReference Include="xUnit.runner.visualstudio" Version="2.8.0" />
<PackageReference Include="NSubstitute" Version="5.1.0" />
<!-- ❌ BANNED: Moq (uses reflection) -->
```

### Project Configuration

#### AOT Publishing (`server.csproj`)
```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <InvariantGlobalization>false</InvariantGlobalization>
  <RuntimeIdentifier>linux-arm64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <StripSymbols>true</StripSymbols>
</PropertyGroup>
```

---

## Infrastructure Stack

### Core Framework
- **AWS CDK**: `2.170.x` (TypeScript)
- **Node.js**: `20.20.0 LTS`
- **npm**: `10.8.2`
- **TypeScript**: `5.7.x`

### AWS Services

#### Compute
- **Lambda**: ARM64, .NET 10 custom runtime
- **Memory**: 512MB (start small, optimize later)
- **Timeout**: 10 seconds (API), 60 seconds (sync jobs)

#### Storage
- **DynamoDB**: On-demand billing (low traffic)
- **S3**: Standard tier for user uploads
- **CloudFront**: CDN for static assets

#### Networking
- **API Gateway**: HTTP API (cheaper than REST API)
- **Route 53**: DNS management
- **ACM**: SSL/TLS certificates

#### Monitoring
- **CloudWatch**: Logs, metrics, alarms
- **X-Ray**: Distributed tracing (optional)

### CDK Dependencies
```json
{
  "dependencies": {
    "aws-cdk-lib": "^2.170.0",
    "constructs": "^10.0.0",
    "@aws-cdk/aws-lambda-dotnet": "^2.170.0"
  },
  "devDependencies": {
    "typescript": "^5.7.0",
    "@types/node": "^20.0.0"
  }
}
```

---

## Development Tools

### Version Control
- **Git**: `2.40+`
- **GitHub**: Repository hosting
- **Branch Strategy**: Trunk-based development

### CI/CD
- **GitHub Actions**: Build, test, deploy
- **AWS SAM**: Local Lambda testing
- **LocalStack**: Local AWS emulation

### Code Quality
- **Flutter**: `flutter analyze`, `dart format`
- **.NET**: `dotnet format`, Roslyn analyzers
- **TypeScript**: ESLint, Prettier

### Testing Tools
- **Mobile**: Flutter test framework, integration_test
- **Server**: xUnit, NSubstitute
- **E2E**: Maestro (mobile UI testing)

---

## Banned Technologies

### ❌ Mobile
- **Hive**: Use Isar instead (better performance)
- **GetX**: Use BLoC instead (better testability)
- **Provider**: Use BLoC instead (consistency)

### ❌ Server
- **Newtonsoft.Json**: Not AOT-compatible
- **Entity Framework Core**: Not AOT-compatible
- **Moq**: Uses reflection
- **AutoMapper**: Uses reflection

### ❌ Infrastructure
- **Serverless Framework**: Use AWS CDK instead (type safety)
- **Terraform**: Use AWS CDK instead (consistency)

---

## Version Update Policy

1. **Security patches**: Apply immediately
2. **Minor versions**: Update quarterly
3. **Major versions**: Evaluate carefully, update annually
4. **Breaking changes**: Document in `specs/migrations/`

---

## Ethiopian Context Optimizations

### Mobile
- **Image compression**: WebP format, max 800px width
- **Font subsetting**: Only include Amharic/Oromo glyphs needed
- **APK size target**: <50MB (critical for 3G downloads)

### Server
- **Response compression**: Gzip/Brotli enabled
- **Payload size**: <100KB per API response
- **Cold start**: <2 seconds (ARM64 + AOT)

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Next Review**: 2026-05-09 (quarterly)

