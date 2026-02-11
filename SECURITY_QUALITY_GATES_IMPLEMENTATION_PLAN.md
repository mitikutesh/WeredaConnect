# 🔐 Security & Quality Gates Implementation Plan

**Project**: WeredaConnect  
**Date**: 2026-02-10  
**Timeline**: 8 weeks (240 hours, 1 engineer)  
**Priority**: P0 - Critical for Production Readiness  
**Status**: Ready for Implementation

---

## 1. OVERVIEW

### Goals
Implement automated security scanning and quality gates to prevent security vulnerabilities from reaching production in WeredaConnect, an offline-first agritech platform for Ethiopian rural communities.

### Success Criteria
- ✅ Zero critical vulnerabilities in production
- ✅ 100% automated dependency scanning
- ✅ SAST/DAST integrated in CI/CD
- ✅ Secret scanning prevents credential leaks
- ✅ Quality gates block insecure code from merging
- ✅ OWASP Mobile/API Top 10 compliance
- ✅ Ethiopian data protection compliance

### Scope Boundaries

**Included**:
- GitHub Actions workflow modifications
- Free/open-source security tools configuration
- Quality gate definitions and enforcement
- Automated scanning (SAST, dependency, secrets)
- CI/CD integration and blocking mechanisms
- Compliance validation automation

**Excluded**:
- Paid enterprise tools (SonarQube Enterprise, Snyk Enterprise)
- Manual penetration testing (separate engagement)
- Authentication/authorization implementation (separate plan)
- Infrastructure security hardening (separate plan)

---

## 2. PREREQUISITES

### Required Tools & Services
- **GitHub Actions**: Already configured ✅
- **GitHub Advanced Security**: Enable for private repo (free for public repos)
- **Codecov Account**: Already configured ✅
- **Docker**: For running security scanners locally

### Dependencies to Install
```bash
# Mobile (Flutter)
flutter pub add --dev flutter_lints
flutter pub add --dev very_good_analysis

# Server (.NET)
dotnet tool install --global security-scan
dotnet add package SecurityCodeScan.VS2019

# Infrastructure (Node.js)
npm install --save-dev eslint-plugin-security
npm install --save-dev @microsoft/eslint-plugin-sdl
```

### Environment Configuration
```bash
# GitHub Secrets to configure
CODECOV_TOKEN          # Already configured ✅
SONAR_TOKEN           # To be created
ANDROID_KEYSTORE      # For release signing
ANDROID_KEY_PASSWORD  # For release signing
```

---

## 3. IMPLEMENTATION STEPS

### PHASE 1: Foundation (Week 1-2, 80 hours)

#### Step 1.1: Enable GitHub Security Features (4 hours)
**Description**: Activate built-in GitHub security scanning features

**Files to modify**:
- `.github/settings.yml` (create new)
- Repository settings (via GitHub UI)

**Implementation**:
1. Enable Dependabot security updates
2. Enable Dependabot version updates
3. Enable GitHub Secret Scanning
4. Enable GitHub Code Scanning (CodeQL)
5. Configure security policy

**Configuration Example**:
```yaml
# .github/dependabot.yml
version: 2
updates:
  # Flutter dependencies
  - package-ecosystem: "pub"
    directory: "/mobile"
    schedule:
      interval: "weekly"
    open-pull-requests-limit: 5
    labels:
      - "dependencies"
      - "mobile"
    
  # .NET dependencies
  - package-ecosystem: "nuget"
    directory: "/server"
    schedule:
      interval: "weekly"
    open-pull-requests-limit: 5
    labels:
      - "dependencies"
      - "server"
    
  # NPM dependencies (infrastructure)
  - package-ecosystem: "npm"
    directory: "/infra"
    schedule:
      interval: "weekly"
    open-pull-requests-limit: 5
    labels:
      - "dependencies"
      - "infra"
    
  # GitHub Actions
  - package-ecosystem: "github-actions"
    directory: "/"
    schedule:
      interval: "weekly"
```

**Testing**: Verify Dependabot creates PRs for outdated dependencies

---

#### Step 1.2: Add Secret Scanning Workflow (4 hours)
**Description**: Implement TruffleHog for comprehensive secret detection

**Files to create**:
- `.github/workflows/secret-scanning.yml`

**Implementation**:
```yaml
# .github/workflows/secret-scanning.yml
name: Secret Scanning

on:
  pull_request:
    branches: [main, develop]
  push:
    branches: [main, develop]
  schedule:
    - cron: '0 2 * * 1'  # Weekly scan on Mondays

jobs:
  trufflehog:
    name: TruffleHog Secret Scan
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v4
        with:
          fetch-depth: 0  # Full history for comprehensive scan

      - name: Run TruffleHog
        uses: trufflesecurity/trufflehog@main
        with:
          path: ./
          base: ${{ github.event.repository.default_branch }}
          head: HEAD
          extra_args: --debug --only-verified

      - name: Fail on secrets found
        if: failure()
        run: |
          echo "❌ Secrets detected! Review TruffleHog output above."
          exit 1

  gitleaks:
    name: GitLeaks Secret Scan
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Run Gitleaks
        uses: gitleaks/gitleaks-action@v2
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          GITLEAKS_LICENSE: ${{ secrets.GITLEAKS_LICENSE }}  # Optional
```

**Testing**:
1. Create test commit with fake AWS key
2. Verify workflow blocks the commit
3. Remove test commit

---

#### Step 1.3: Add Dependency Scanning Workflow (8 hours)
**Description**: Automated vulnerability scanning for all dependencies

**Files to create**:
- `.github/workflows/dependency-scanning.yml`

**Implementation**:
```yaml
# .github/workflows/dependency-scanning.yml
name: Dependency Scanning

on:
  pull_request:
    branches: [main, develop]
  push:
    branches: [main, develop]
  schedule:
    - cron: '0 3 * * 1'  # Weekly scan

jobs:
  mobile-dependencies:
    name: Mobile Dependencies (Flutter)
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v4

      - name: Setup Flutter
        uses: subosito/flutter-action@v2
        with:
          flutter-version: '3.38.9'
          channel: 'stable'

      - name: Get dependencies
        working-directory: mobile
        run: flutter pub get

      - name: Check for outdated packages
        working-directory: mobile
        run: flutter pub outdated --json > outdated.json || true

      - name: Analyze dependencies
        working-directory: mobile
        run: |
          # Check for known vulnerabilities
          if grep -q "security" outdated.json; then
            echo "⚠️ Security vulnerabilities found in dependencies"
            cat outdated.json
            exit 1
          fi

      - name: Upload dependency report
        uses: actions/upload-artifact@v4
        with:
          name: mobile-dependency-report
          path: mobile/outdated.json

  server-dependencies:
    name: Server Dependencies (.NET)
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore dependencies
        working-directory: server
        run: dotnet restore

      - name: Check for vulnerable packages
        working-directory: server
        run: |
          dotnet list package --vulnerable --include-transitive 2>&1 | tee vulnerable.txt
          if grep -q "has the following vulnerable packages" vulnerable.txt; then
            echo "❌ Vulnerable packages detected!"
            exit 1
          fi

      - name: Check for deprecated packages
        working-directory: server
        run: dotnet list package --deprecated

      - name: Upload dependency report
        uses: actions/upload-artifact@v4
        with:
          name: server-dependency-report
          path: server/vulnerable.txt

  infra-dependencies:
    name: Infrastructure Dependencies (npm)
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v4

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version: '20.20.0'

      - name: Install dependencies
        working-directory: infra
        run: npm ci

      - name: Run npm audit
        working-directory: infra
        run: |
          npm audit --audit-level=moderate --json > audit.json || true
          if [ $(jq '.metadata.vulnerabilities.moderate + .metadata.vulnerabilities.high + .metadata.vulnerabilities.critical' audit.json) -gt 0 ]; then
            echo "❌ Vulnerabilities found!"
            npm audit
            exit 1
          fi

      - name: Upload audit report
        uses: actions/upload-artifact@v4
        with:
          name: infra-audit-report
          path: infra/audit.json
```

**Testing**: Run workflow and verify it detects known vulnerabilities

---

#### Step 1.4: Configure SAST with CodeQL (12 hours)
**Description**: Static Application Security Testing for C# and TypeScript

**Files to create**:
- `.github/workflows/codeql-analysis.yml`
- `.github/codeql/codeql-config.yml`

**Implementation**:
```yaml
# .github/workflows/codeql-analysis.yml
name: CodeQL Security Analysis

on:
  pull_request:
    branches: [main, develop]
  push:
    branches: [main, develop]
  schedule:
    - cron: '0 4 * * 1'  # Weekly deep scan

jobs:
  analyze-csharp:
    name: Analyze C# (.NET Server)
    runs-on: ubuntu-latest
    permissions:
      actions: read
      contents: read
      security-events: write

    steps:
      - name: Checkout code
        uses: actions/checkout@v4

      - name: Initialize CodeQL
        uses: github/codeql-action/init@v3
        with:
          languages: csharp
          config-file: ./.github/codeql/codeql-config.yml
          queries: +security-and-quality

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Build project
        working-directory: server
        run: dotnet build --configuration Release

      - name: Perform CodeQL Analysis
        uses: github/codeql-action/analyze@v3
        with:
          category: "/language:csharp"

  analyze-typescript:
    name: Analyze TypeScript (Infrastructure)
    runs-on: ubuntu-latest
    permissions:
      actions: read
      contents: read
      security-events: write

    steps:
      - name: Checkout code
        uses: actions/checkout@v4

      - name: Initialize CodeQL
        uses: github/codeql-action/init@v3
        with:
          languages: javascript
          config-file: ./.github/codeql/codeql-config.yml
          queries: +security-and-quality

      - name: Perform CodeQL Analysis
        uses: github/codeql-action/analyze@v3
        with:
          category: "/language:javascript"
```

```yaml
# .github/codeql/codeql-config.yml
name: "WeredaConnect CodeQL Config"

queries:
  - uses: security-and-quality
  - uses: security-extended

paths-ignore:
  - '**/node_modules'
  - '**/build'
  - '**/dist'
  - '**/*.test.ts'
  - '**/*.test.cs'
  - '**/coverage'

query-filters:
  - exclude:
      id: js/unused-local-variable
  - exclude:
      id: cs/local-not-disposed
```

**Testing**: Verify CodeQL detects SQL injection and XSS vulnerabilities

---

#### Step 1.5: Add SonarQube Community Edition (16 hours)
**Description**: Comprehensive SAST for Dart, C#, and TypeScript

**Files to create**:
- `.github/workflows/sonarqube.yml`
- `sonar-project.properties`

**Implementation**:
```yaml
# .github/workflows/sonarqube.yml
name: SonarQube Analysis

on:
  pull_request:
    branches: [main, develop]
  push:
    branches: [main, develop]

jobs:
  sonarqube-server:
    name: SonarQube - Server (.NET)
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v4
        with:
          fetch-depth: 0  # Shallow clones disabled for better analysis

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Install SonarScanner
        run: dotnet tool install --global dotnet-sonarscanner

      - name: Begin SonarQube scan
        working-directory: server
        env:
          SONAR_TOKEN: ${{ secrets.SONAR_TOKEN }}
        run: |
          dotnet sonarscanner begin \
            /k:"wereda-connect-server" \
            /d:sonar.host.url="https://sonarcloud.io" \
            /d:sonar.login="${SONAR_TOKEN}" \
            /d:sonar.cs.opencover.reportsPaths="**/coverage.opencover.xml"

      - name: Build
        working-directory: server
        run: dotnet build --configuration Release

      - name: Run tests with coverage
        working-directory: server
        run: dotnet test --collect:"XPlat Code Coverage" -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover

      - name: End SonarQube scan
        working-directory: server
        env:
          SONAR_TOKEN: ${{ secrets.SONAR_TOKEN }}
        run: dotnet sonarscanner end /d:sonar.login="${SONAR_TOKEN}"

  sonarqube-infra:
    name: SonarQube - Infrastructure (TypeScript)
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version: '20.20.0'

      - name: Install dependencies
        working-directory: infra
        run: npm ci

      - name: Run tests with coverage
        working-directory: infra
        run: npm test -- --coverage

      - name: SonarQube Scan
        uses: SonarSource/sonarqube-scan-action@master
        env:
          SONAR_TOKEN: ${{ secrets.SONAR_TOKEN }}
          SONAR_HOST_URL: https://sonarcloud.io
        with:
          projectBaseDir: infra
          args: >
            -Dsonar.projectKey=wereda-connect-infra
            -Dsonar.sources=lib
            -Dsonar.tests=test
            -Dsonar.javascript.lcov.reportPaths=coverage/lcov.info
```

```properties
# sonar-project.properties
sonar.organization=wereda-connect
sonar.projectKey=wereda-connect
sonar.projectName=WeredaConnect
sonar.projectVersion=0.1.0

# Source directories
sonar.sources=mobile/lib,server/src,infra/lib
sonar.tests=mobile/test,server/tests,infra/test

# Exclusions
sonar.exclusions=**/*.g.dart,**/generated/**,**/build/**,**/dist/**,**/node_modules/**

# Coverage
sonar.coverage.exclusions=**/*.test.ts,**/*.test.cs,**/*_test.dart

# Quality Gates
sonar.qualitygate.wait=true
sonar.qualitygate.timeout=300
```

**Quality Gate Configuration** (SonarCloud UI):
- Overall Coverage: ≥75%
- New Code Coverage: ≥80%
- Duplicated Lines: ≤3%
- Maintainability Rating: A
- Reliability Rating: A
- Security Rating: A
- Security Hotspots Reviewed: 100%

**Testing**: Push code and verify SonarQube analysis completes

---

#### Step 1.6: Add Container Scanning with Trivy (8 hours)
**Description**: Scan Lambda deployment packages for vulnerabilities

**Files to create**:
- `.github/workflows/container-scanning.yml`

**Implementation**:
```yaml
# .github/workflows/container-scanning.yml
name: Container & Binary Scanning

on:
  pull_request:
    branches: [main, develop]
  push:
    branches: [main, develop]

jobs:
  trivy-server:
    name: Trivy - Server Binary Scan
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Build AOT binary
        working-directory: server
        run: dotnet publish -c Release -r linux-arm64

      - name: Run Trivy vulnerability scanner
        uses: aquasecurity/trivy-action@master
        with:
          scan-type: 'fs'
          scan-ref: 'server/bin/Release/net10.0/linux-arm64/publish'
          format: 'sarif'
          output: 'trivy-server-results.sarif'
          severity: 'CRITICAL,HIGH'
          exit-code: '1'  # Fail on vulnerabilities

      - name: Upload Trivy results to GitHub Security
        uses: github/codeql-action/upload-sarif@v3
        if: always()
        with:
          sarif_file: 'trivy-server-results.sarif'
          category: 'trivy-server'

  trivy-mobile:
    name: Trivy - Mobile APK Scan
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v4

      - name: Setup Flutter
        uses: subosito/flutter-action@v2
        with:
          flutter-version: '3.38.9'

      - name: Setup Java
        uses: actions/setup-java@v4
        with:
          distribution: 'zulu'
          java-version: '17'

      - name: Build APK
        working-directory: mobile
        run: |
          flutter pub get
          flutter build apk --release

      - name: Run Trivy on APK
        uses: aquasecurity/trivy-action@master
        with:
          scan-type: 'fs'
          scan-ref: 'mobile/build/app/outputs/flutter-apk/app-release.apk'
          format: 'sarif'
          output: 'trivy-mobile-results.sarif'
          severity: 'CRITICAL,HIGH'

      - name: Upload Trivy results
        uses: github/codeql-action/upload-sarif@v3
        if: always()
        with:
          sarif_file: 'trivy-mobile-results.sarif'
          category: 'trivy-mobile'

  grype-scan:
    name: Grype - Dependency Scan
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v4

      - name: Run Grype vulnerability scanner
        uses: anchore/scan-action@v3
        with:
          path: "."
          fail-build: true
          severity-cutoff: high

      - name: Upload Grype results
        uses: github/codeql-action/upload-sarif@v3
        if: always()
        with:
          sarif_file: ${{ steps.scan.outputs.sarif }}
```

**Testing**: Build and scan binaries for known vulnerabilities

---

### PHASE 2: Quality Gates (Week 3-4, 80 hours)

#### Step 2.1: Define Quality Gate Rules (8 hours)
**Description**: Create enforceable quality standards for all components

**Files to create**:
- `.github/quality-gates.yml`
- `mobile/analysis_options.yaml` (update)
- `server/.editorconfig` (update)

**Implementation**:
```yaml
# .github/quality-gates.yml
name: Quality Gates Configuration

# Mobile (Flutter)
mobile:
  coverage:
    overall: 75
    business_logic: 85
    sync_engine: 100
    ui: 60

  analysis:
    fatal_infos: true
    fatal_warnings: true

  formatting:
    enforce: true

  dependencies:
    max_outdated: 5
    block_vulnerable: true

# Server (.NET)
server:
  coverage:
    overall: 75
    business_logic: 85
    lambda_handlers: 70
    sync_resolution: 100

  analysis:
    treat_warnings_as_errors: true
    nullable_enabled: true

  security:
    block_vulnerable_packages: true
    require_code_signing: true

# Infrastructure (CDK)
infra:
  coverage:
    overall: 70
    custom_constructs: 80
    iam_policies: 100

  snapshot_tests:
    required: true
    all_stacks: true

  synthesis:
    must_succeed: true
    no_warnings: true

# General
general:
  pr_reviews_required: 2
  status_checks_required: all
  conversation_resolution: required
  branch_up_to_date: required
```

**Testing**: Validate quality gate configuration

---

