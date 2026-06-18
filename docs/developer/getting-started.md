# Getting Started

## Developer Environment Setup

This guide will help you set up your development environment for WeredaConnect.

## Audience
- New developers
- Contributors
- DevOps engineers

## Prerequisites
- macOS, Linux, or Windows with WSL2
- Git installed
- Basic knowledge of Flutter, .NET, and TypeScript

---

## Quick Start

### 1. Clone the Repository

```bash
git clone https://github.com/your-org/WeredaConnect.git
cd WeredaConnect
```

### 2. Run Setup Script

```bash
./scripts/setup-dev.sh
```

This script will:
- Check for required tools
- Install dependencies
- Set up pre-commit hooks
- Create local configuration files

### 3. Verify Installation

```bash
./scripts/verify-setup.sh
```

---

## Manual Setup

If the automated script doesn't work, follow these manual steps:

### Mobile (Flutter)

#### 1. Install Flutter

```bash
# macOS/Linux
git clone https://github.com/flutter/flutter.git -b stable
export PATH="$PATH:`pwd`/flutter/bin"

# Verify installation
flutter doctor
```

**Required Version**: Flutter 3.38.9, Dart 3.10.8

#### 2. Install Dependencies

```bash
cd mobile
flutter pub get
```

#### 3. Generate Code

```bash
# Generate Isar database code
flutter pub run build_runner build --delete-conflicting-outputs

# Generate localization files
flutter gen-l10n
```

#### 4. Run on Emulator

```bash
# List available devices
flutter devices

# Run on Android emulator
flutter run -d <device-id>
```

#### 5. Run Tests

```bash
# Unit tests
flutter test

# Integration tests
flutter test integration_test/
```

---

### Backend (.NET)

#### 1. Install .NET 10 SDK

```bash
# macOS (Homebrew)
brew install dotnet@10

# Linux
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0

# Verify installation
dotnet --version  # Should be 10.x.x
```

#### 2. Install Dependencies

```bash
cd server
dotnet restore
```

#### 3. Build with AOT

```bash
# Build for Lambda (ARM64)
dotnet publish -c Release -r linux-arm64 --self-contained
```

#### 4. Run Tests

```bash
# Unit tests
dotnet test

# With coverage
dotnet test /p:CollectCoverage=true /p:CoverageReportsFormat=lcov
```

#### 5. Run Locally (Optional)

```bash
# Install Lambda test tool
dotnet tool install -g Amazon.Lambda.TestTool-10.0

# Run function
cd src/Functions/CreateReport
dotnet lambda-test-tool-10.0
```

---

### Infrastructure (CDK)

#### 1. Install Node.js

```bash
# macOS (Homebrew)
brew install node@20

# Linux (nvm)
curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/v0.39.0/install.sh | bash
nvm install 20
nvm use 20

# Verify installation
node --version  # Should be 20.x.x
npm --version   # Should be 10.x.x
```

#### 2. Install AWS CDK

```bash
npm install -g aws-cdk

# Verify installation
cdk --version  # Should be 2.x.x
```

#### 3. Install Dependencies

```bash
cd infra
npm install
```

#### 4. Configure AWS Credentials

```bash
# Install AWS CLI
brew install awscli  # macOS
# or
pip install awscli  # Linux/Windows

# Configure credentials
aws configure
# Enter: Access Key ID, Secret Access Key, Region (us-east-1), Output format (json)
```

#### 5. Bootstrap CDK (First Time Only)

```bash
cdk bootstrap aws://ACCOUNT-ID/REGION
```

#### 6. Run Tests

```bash
npm test
```

#### 7. Deploy to Dev

```bash
npm run cdk deploy WeredaConnect-*-Dev
```

---

## IDE Setup

### VS Code (Recommended)

#### 1. Install VS Code

Download from https://code.visualstudio.com/

#### 2. Install Extensions

```bash
# Flutter
code --install-extension Dart-Code.flutter
code --install-extension Dart-Code.dart-code

# .NET
code --install-extension ms-dotnettools.csharp
code --install-extension ms-dotnettools.csdevkit

# TypeScript/CDK
code --install-extension dbaeumer.vscode-eslint
code --install-extension esbenp.prettier-vscode

# General
code --install-extension eamodio.gitlens
code --install-extension GitHub.copilot  # Optional
```

#### 3. Configure Settings

Create `.vscode/settings.json`:

```json
{
  "dart.flutterSdkPath": "/path/to/flutter",
  "editor.formatOnSave": true,
  "editor.codeActionsOnSave": {
    "source.fixAll": true
  },
  "[dart]": {
    "editor.rulers": [80]
  },
  "[csharp]": {
    "editor.rulers": [120]
  }
}
```

---

## Environment Variables

### Mobile

Create `mobile/.env`:

```bash
API_BASE_URL=https://dev-api.weredaconnect.com
SENTRY_DSN=your-sentry-dsn  # Optional
```

### Backend

Create `server/.env`:

```bash
ENVIRONMENT=dev
AWS_REGION=us-east-1
DYNAMODB_TABLE_REPORTS=WeredaConnect-Reports-Dev
DYNAMODB_TABLE_USERS=WeredaConnect-Users-Dev
S3_BUCKET_MEDIA=weredaconnect-media-dev
```

### Infrastructure

Create `infra/.env`:

```bash
AWS_ACCOUNT_ID=123456789012
AWS_REGION=us-east-1
ENVIRONMENT=dev
```

---

## Common Commands

### Mobile

```bash
# Run app
flutter run

# Run tests
flutter test

# Generate code
flutter pub run build_runner build

# Analyze code
flutter analyze

# Format code
dart format .

# Build APK
flutter build apk --release
```

### Backend

```bash
# Build
dotnet build

# Test
dotnet test

# Publish (AOT)
dotnet publish -c Release -r linux-arm64

# Format code
dotnet format
```

### Infrastructure

```bash
# Synth CloudFormation
cdk synth

# Deploy
cdk deploy

# Diff changes
cdk diff

# Destroy stack
cdk destroy
```

---

## Troubleshooting

### Flutter Doctor Issues

**Problem**: Android licenses not accepted

```bash
flutter doctor --android-licenses
```

**Problem**: Xcode not configured (macOS)

```bash
sudo xcode-select --switch /Applications/Xcode.app/Contents/Developer
sudo xcodebuild -runFirstLaunch
```

### .NET Build Issues

**Problem**: AOT compilation fails

```bash
# Check for unsupported dependencies
dotnet publish -c Release -r linux-arm64 --self-contained /p:PublishAot=true
```

**Problem**: Missing SDK

```bash
# List installed SDKs
dotnet --list-sdks

# Install .NET 10
# See installation instructions above
```

### CDK Issues

**Problem**: Bootstrap required

```bash
cdk bootstrap
```

**Problem**: Credentials not configured

```bash
aws configure
# Or set environment variables:
export AWS_ACCESS_KEY_ID=your-key
export AWS_SECRET_ACCESS_KEY=your-secret
export AWS_DEFAULT_REGION=us-east-1
```

---

## Next Steps

1. **Read the architecture docs**: [System Architecture](../architecture/system-architecture.md)
2. **Review coding standards**: [Standards](../../.ai/standards.md)
3. **Understand the workflow**: [Development Workflow](./dev-workflow.md)
5. **Run your first test**: [Testing Guidelines](./testing-guidelines.md)
6. **Test end-to-end locally**: [E2E Local Testing Guide](./e2e-local-testing.md)

---

## Getting Help

- **Documentation**: Browse `docs/` folder
- **Issues**: Check GitHub Issues
- **Slack/Discord**: Join the community channel
- **Email**: dev@weredaconnect.com

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Maintainer**: Development Team
