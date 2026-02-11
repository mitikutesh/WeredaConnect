# WeredaConnect Deployment Workflows Implementation Plan

**Date**: 2026-02-10  
**Status**: Planning Phase  
**Timeline**: 7 weeks (phased implementation)

---

## Executive Summary

This plan outlines the implementation of automated deployment workflows for WeredaConnect's staging and production environments. The plan addresses critical gaps identified in the infrastructure exploration:

- **No automated deployments** for staging/production
- **Lambda functions commented out** in CDK stack
- **Mobile app signing** uses debug keys (security risk)
- **No monitoring/alerting** infrastructure
- **No canary deployments** or rollback automation
- **Manual secrets management** without AWS Secrets Manager integration

**Goal**: Enable safe, automated deployments while maintaining the cost-conscious, serverless architecture optimized for Ethiopian rural context.

---

## 1. Overview

### 1.1 Goals and Success Criteria

**Goals**:
1. Automate staging and production deployments via GitHub Actions
2. Implement environment-specific configuration management
3. Enable safe Lambda deployments with canary releases
4. Automate mobile app releases to Google Play
5. Establish monitoring, alerting, and rollback procedures
6. Secure secrets management across all environments

**Success Criteria**:
- ✅ Zero-touch deployments to staging (auto on main branch)
- ✅ One-click deployments to production (manual approval)
- ✅ Automated smoke tests post-deployment
- ✅ <5 minute rollback time for critical issues
- ✅ CloudWatch alarms for error rates, latency, costs
- ✅ Mobile app releases with staged rollout (10% → 50% → 100%)
- ✅ All secrets stored in AWS Secrets Manager
- ✅ Monthly AWS costs remain <$25 for all environments

### 1.2 Scope Boundaries

**In Scope**:
- GitHub Actions workflows for staging/production
- AWS CDK infrastructure for all environments
- .NET AOT Lambda deployment automation
- Android mobile app release automation (Google Play)
- CloudWatch monitoring and alarms
- AWS Secrets Manager integration
- Canary deployment strategy for Lambda
- Post-deployment smoke tests
- Rollback procedures

**Out of Scope** (Future Work):
- iOS App Store deployment (iOS app not yet developed)
- Blue-green deployments (canary sufficient for low traffic)
- Multi-region deployments (single region: us-east-1)
- VPC configuration (not needed for current scale)
- WAF rules (defer until production traffic justifies cost)
- Custom domain setup (defer to Phase 2)
- Database migrations automation (manual for now)

---

## 2. Prerequisites

### 2.1 Required Dependencies

**AWS Resources**:
- AWS account with appropriate permissions
- AWS CLI configured locally
- CDK bootstrapped in target account/region
- AWS Secrets Manager access
- CloudWatch access for monitoring

**GitHub**:
- GitHub repository with Actions enabled
- GitHub Secrets configured:
  - `AWS_ACCESS_KEY_ID`
  - `AWS_SECRET_ACCESS_KEY`
  - `AWS_ACCOUNT_ID`
  - `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON` (for mobile releases)
  - `ANDROID_KEYSTORE_BASE64` (release signing key)
  - `ANDROID_KEYSTORE_PASSWORD`
  - `ANDROID_KEY_ALIAS`
  - `ANDROID_KEY_PASSWORD`

**Local Development**:
- .NET 10 SDK
- Node.js 20.20.0 LTS
- Flutter 3.38.9
- AWS CDK CLI 2.170.0
- Java 17 (for Android builds)

### 2.2 Migrations and Data Changes

**No database migrations required** - DynamoDB tables already defined with on-demand billing.

**Required Setup**:
1. Generate Android release keystore (one-time)
2. Create Google Play service account (one-time)
3. Configure AWS Secrets Manager secrets (per environment)
4. Set up GitHub environment protection rules

### 2.3 Environment and Configuration Requirements

**GitHub Environments**:
- `staging` - 1 required reviewer
- `production` - 2 required reviewers

**AWS Secrets Manager** (per environment):
```
/wereda-connect/{env}/api-key
/wereda-connect/{env}/jwt-secret
/wereda-connect/{env}/database-encryption-key
```

**Environment Variables** (GitHub Secrets):
- Staging: `STAGING_API_URL`, `STAGING_AWS_REGION`
- Production: `PROD_API_URL`, `PROD_AWS_REGION`

---

## 3. Implementation Steps

### Phase 1: Foundation (Week 1-2)
**Goal**: Set up environment-specific infrastructure and configuration

#### Step 1.1: Create Environment Configuration Files
**Files to create**:
- `infra/lib/config/dev.ts`
- `infra/lib/config/staging.ts`
- `infra/lib/config/prod.ts`

**Key implementation details**:
```typescript
// infra/lib/config/staging.ts
export const stagingConfig = {
  environment: 'staging',
  lambda: {
    memorySize: 512,
    timeout: 10,
    reservedConcurrentExecutions: 5,
  },
  dynamodb: {
    billingMode: 'PAY_PER_REQUEST',
    pointInTimeRecovery: true,
  },
  api: {
    throttle: { rateLimit: 1000, burstLimit: 2000 },
  },
  monitoring: {
    enableDetailedMetrics: true,
    logRetentionDays: 14,
  },
};
```

**Testing considerations**:
- Validate config files load correctly
- Test CDK synth with each environment
- Verify environment-specific resource naming

**Files to modify**:
- `infra/bin/wereda-connect.ts` - Uncomment staging/prod stacks
- `infra/lib/stacks/wereda-connect-stack.ts` - Accept environment config

---

#### Step 1.2: Uncomment and Configure Lambda Functions
**Files to modify**:
- `infra/lib/stacks/wereda-connect-stack.ts`

**Key implementation details**:
- Uncomment Lambda function definitions (lines 63-84)
- Add environment-specific configuration
- Configure Lambda versioning and aliases
- Set up proper IAM permissions

**Testing considerations**:
- Build .NET AOT binary locally
- Test Lambda deployment to dev environment
- Verify cold start times <2 seconds
- Test DynamoDB/S3 permissions

---

#### Step 1.3: Set Up AWS Secrets Manager
**Secrets to create** (per environment):
```bash
# Create secrets for each environment
aws secretsmanager create-secret \
  --name /wereda-connect/staging/api-key \
  --secret-string "$(openssl rand -base64 32)"

aws secretsmanager create-secret \
  --name /wereda-connect/staging/jwt-secret \
  --secret-string "$(openssl rand -base64 64)"
```

**CDK Integration**:
```typescript
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';

const apiKeySecret = secretsmanager.Secret.fromSecretNameV2(
  this, 'ApiKeySecret',
  `/wereda-connect/${environment}/api-key`
);

// Grant Lambda read access
apiKeySecret.grantRead(healthCheckFunction);
```

**Testing considerations**:
- Verify secrets accessible from Lambda
- Test secret rotation (manual for now)
- Validate IAM permissions

---

### Phase 2: Server Deployment Automation (Week 2-3)
**Goal**: Automate .NET AOT Lambda deployments with canary releases

#### Step 2.1: Create Server Deployment Workflow
**File to create**: `.github/workflows/deploy-server.yml`

**Key implementation details**:
```yaml
name: Deploy Server

on:
  workflow_dispatch:
    inputs:
      environment:
        type: choice
        options: [staging, production]
        required: true
      canary_percentage:
        description: 'Canary traffic percentage (10-100)'
        default: '10'

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 10
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Build AOT binary
        working-directory: server
        run: |
          dotnet publish -c Release -r linux-arm64 --self-contained
          cd bin/Release/net10.0/linux-arm64/publish
          zip -r lambda.zip .

      - name: Upload artifact
        uses: actions/upload-artifact@v4
        with:
          name: lambda-package
          path: server/bin/Release/net10.0/linux-arm64/publish/lambda.zip

  deploy:
    needs: build
    runs-on: ubuntu-latest
    environment: ${{ github.event.inputs.environment }}
    steps:
      - uses: actions/checkout@v4

      - name: Download artifact
        uses: actions/download-artifact@v4
        with:
          name: lambda-package

      - name: Deploy to Lambda
        env:
          AWS_ACCESS_KEY_ID: ${{ secrets.AWS_ACCESS_KEY_ID }}
          AWS_SECRET_ACCESS_KEY: ${{ secrets.AWS_SECRET_ACCESS_KEY }}
          AWS_REGION: us-east-1
        run: |
          # Update function code
          aws lambda update-function-code \
            --function-name wereda-connect-api-${{ inputs.environment }} \
            --zip-file fileb://lambda.zip

          # Publish new version
          VERSION=$(aws lambda publish-version \
            --function-name wereda-connect-api-${{ inputs.environment }} \
            --query 'Version' --output text)

          # Update canary alias
          aws lambda update-alias \
            --function-name wereda-connect-api-${{ inputs.environment }} \
            --name canary \
            --function-version $VERSION \
            --routing-config AdditionalVersionWeights={"$VERSION"=${{ inputs.canary_percentage }}}

      - name: Run smoke tests
        run: |
          npm run smoke-test:${{ inputs.environment }}

      - name: Monitor for errors
        run: |
          # Wait 5 minutes and check error rate
          sleep 300
          ERROR_COUNT=$(aws cloudwatch get-metric-statistics \
            --namespace AWS/Lambda \
            --metric-name Errors \
            --dimensions Name=FunctionName,Value=wereda-connect-api-${{ inputs.environment }} \
            --start-time $(date -u -d '5 minutes ago' +%Y-%m-%dT%H:%M:%S) \
            --end-time $(date -u +%Y-%m-%dT%H:%M:%S) \
            --period 300 \
            --statistics Sum \
            --query 'Datapoints[0].Sum' --output text)

          if [ "$ERROR_COUNT" -gt 0 ]; then
            echo "Errors detected! Rolling back..."
            exit 1
          fi
```

**Testing considerations**:
- Test deployment to staging first
- Verify canary traffic routing
- Test rollback on error detection
- Validate smoke tests pass

---

#### Step 2.2: Create Infrastructure Deployment Workflow
**File to create**: `.github/workflows/deploy-infrastructure.yml`

**Key implementation details**:
```yaml
name: Deploy Infrastructure

on:
  workflow_dispatch:
    inputs:
      environment:
        type: choice
        options: [dev, staging, production]
        required: true

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: ${{ github.event.inputs.environment }}
    steps:
      - uses: actions/checkout@v4

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version: '20.20.0'
          cache: 'npm'
          cache-dependency-path: infra/package-lock.json

      - name: Install dependencies
        working-directory: infra
        run: npm ci

      - name: Build TypeScript
        working-directory: infra
        run: npm run build

      - name: CDK Diff
        working-directory: infra
        env:
          AWS_ACCESS_KEY_ID: ${{ secrets.AWS_ACCESS_KEY_ID }}
          AWS_SECRET_ACCESS_KEY: ${{ secrets.AWS_SECRET_ACCESS_KEY }}
        run: |
          npx cdk diff --context environment=${{ inputs.environment }}

      - name: CDK Deploy
        working-directory: infra
        env:
          AWS_ACCESS_KEY_ID: ${{ secrets.AWS_ACCESS_KEY_ID }}
          AWS_SECRET_ACCESS_KEY: ${{ secrets.AWS_SECRET_ACCESS_KEY }}
        run: |
          npx cdk deploy WeredaConnect*${{ inputs.environment }}* \
            --context environment=${{ inputs.environment }} \
            --require-approval never

      - name: Verify deployment
        run: |
          # Get API URL from CloudFormation outputs
          API_URL=$(aws cloudformation describe-stacks \
            --stack-name wereda-connect-${{ inputs.environment }} \
            --query 'Stacks[0].Outputs[?OutputKey==`ApiUrl`].OutputValue' \
            --output text)

          # Health check
          curl -f $API_URL/health || exit 1
```

**Testing considerations**:
- Test CDK diff output
- Verify CloudFormation stack updates
- Test rollback on deployment failure
- Validate outputs are correct

---

### Phase 3: Mobile App Release Automation (Week 3-4)
**Goal**: Automate Android app releases to Google Play

#### Step 3.1: Generate Android Release Keystore
**One-time setup**:
```bash
# Generate release keystore
keytool -genkey -v -keystore wereda-connect-release.jks \
  -keyalg RSA -keysize 2048 -validity 10000 \
  -alias wereda-connect \
  -dname "CN=WeredaConnect, OU=Engineering, O=WeredaConnect, L=Addis Ababa, ST=Addis Ababa, C=ET"

# Convert to base64 for GitHub Secrets
base64 -i wereda-connect-release.jks -o keystore.base64

# Store in GitHub Secrets:
# - ANDROID_KEYSTORE_BASE64 (contents of keystore.base64)
# - ANDROID_KEYSTORE_PASSWORD
# - ANDROID_KEY_ALIAS (wereda-connect)
# - ANDROID_KEY_PASSWORD
```

**Files to modify**:
- `mobile/android/app/build.gradle` - Configure release signing
- `mobile/android/key.properties` (gitignored) - Local signing config

**Key implementation details**:
```gradle
// mobile/android/app/build.gradle
android {
    signingConfigs {
        release {
            if (System.getenv("CI")) {
                // CI environment (GitHub Actions)
                storeFile file("${System.getenv('RUNNER_TEMP')}/keystore.jks")
                storePassword System.getenv("ANDROID_KEYSTORE_PASSWORD")
                keyAlias System.getenv("ANDROID_KEY_ALIAS")
                keyPassword System.getenv("ANDROID_KEY_PASSWORD")
            } else {
                // Local development
                def keystorePropertiesFile = rootProject.file("key.properties")
                if (keystorePropertiesFile.exists()) {
                    def keystoreProperties = new Properties()
                    keystoreProperties.load(new FileInputStream(keystorePropertiesFile))
                    storeFile file(keystoreProperties['storeFile'])
                    storePassword keystoreProperties['storePassword']
                    keyAlias keystoreProperties['keyAlias']
                    keyPassword keystoreProperties['keyPassword']
                }
            }
        }
    }

    buildTypes {
        release {
            signingConfig signingConfigs.release
            minifyEnabled true
            shrinkResources true
        }
    }
}
```

**Testing considerations**:
- Test local signing with key.properties
- Test CI signing with environment variables
- Verify APK signature validity
- Test app installation on device

---

#### Step 3.2: Create Mobile Release Workflow
**File to create**: `.github/workflows/release-mobile.yml`

