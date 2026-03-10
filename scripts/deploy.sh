#!/bin/bash
# scripts/deploy.sh
# Build and deploy WeredaConnect server + infrastructure + web app
# Usage: ./scripts/deploy.sh [dev|staging|prod]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SERVER_DIR="$ROOT_DIR/server"
INFRA_DIR="$ROOT_DIR/infra"
MOBILE_DIR="$ROOT_DIR/mobile"

ENV="${1:-dev}"

if [[ "$ENV" != "dev" && "$ENV" != "staging" && "$ENV" != "prod" ]]; then
  echo "Error: Invalid environment '$ENV'. Use: dev, staging, or prod"
  exit 1
fi

if [[ "$ENV" == "prod" ]]; then
  echo "WARNING: You are deploying to PRODUCTION."
  read -r -p "Are you sure? (yes/no): " confirm
  if [[ "$confirm" != "yes" ]]; then
    echo "Aborted."
    exit 0
  fi
fi

echo "=== WeredaConnect Deploy: $ENV ==="
echo ""

# Step 1: Run server tests
echo "[1/6] Running server tests..."
cd "$SERVER_DIR"
dotnet test --verbosity minimal --no-restore 2>/dev/null || dotnet test --verbosity minimal
echo "Tests passed."
echo ""

# Step 2: Build .NET AOT for Linux ARM64 via Docker
echo "[2/6] Building .NET AOT (linux-arm64) via Docker..."
docker run --rm \
  --platform linux/arm64 \
  -v "$SERVER_DIR":/src \
  -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -c "apt-get update -qq && apt-get install -y -qq clang zlib1g-dev >/dev/null 2>&1 && dotnet publish -c Release -r linux-arm64 --self-contained true -p:CI=true"
echo "Build complete: $SERVER_DIR/bin/Release/net10.0/linux-arm64/publish/"
echo ""

# Step 3: Install CDK dependencies if needed
echo "[3/6] Preparing CDK..."
cd "$INFRA_DIR"
if [ ! -d "node_modules" ]; then
  npm ci
fi
npx tsc
echo "CDK ready."
echo ""

# Step 4: Deploy with CDK
echo "[4/6] Deploying infrastructure to AWS ($ENV)..."
npx cdk deploy --all --context environment="$ENV" --require-approval broadening
echo ""

# Step 5: Build Flutter web app
echo "[5/6] Building Flutter web app..."
cd "$MOBILE_DIR"

# Resolve --dart-define values from CDK outputs / AWS
# Fetch API URL from CloudFormation
API_STACK="wereda-connect-${ENV}-api"
API_BASE_URL=$(aws cloudformation describe-stacks \
  --stack-name "$API_STACK" \
  --region me-south-1 \
  --query 'Stacks[0].Outputs[?OutputKey==`ApiUrl`].OutputValue' \
  --output text 2>/dev/null || echo "")

if [[ -z "$API_BASE_URL" ]]; then
  echo "Warning: Could not resolve API_BASE_URL from stack $API_STACK. Checking ApiEndpoint..."
  API_BASE_URL=$(aws cloudformation describe-stacks \
    --stack-name "$API_STACK" \
    --region me-south-1 \
    --query 'Stacks[0].Outputs[?contains(OutputKey, `Api`)].OutputValue' \
    --output text 2>/dev/null | head -1 || echo "")
fi

AUTH_STACK="wereda-connect-${ENV}-api"
COGNITO_USER_POOL_ID=$(aws cloudformation describe-stacks \
  --stack-name "$AUTH_STACK" \
  --region me-south-1 \
  --query 'Stacks[0].Outputs[?OutputKey==`UserPoolId`].OutputValue' \
  --output text 2>/dev/null || echo "")

COGNITO_CLIENT_ID=$(aws cloudformation describe-stacks \
  --stack-name "$AUTH_STACK" \
  --region me-south-1 \
  --query 'Stacks[0].Outputs[?OutputKey==`UserPoolClientId`].OutputValue' \
  --output text 2>/dev/null || echo "")

echo "  API_BASE_URL: ${API_BASE_URL:-<not found>}"
echo "  COGNITO_USER_POOL_ID: ${COGNITO_USER_POOL_ID:-<not found>}"
echo "  COGNITO_CLIENT_ID: ${COGNITO_CLIENT_ID:-<not found>}"

DART_DEFINES=""
if [[ -n "$API_BASE_URL" ]]; then
  DART_DEFINES="$DART_DEFINES --dart-define=API_BASE_URL=$API_BASE_URL"
fi
if [[ -n "$COGNITO_USER_POOL_ID" ]]; then
  DART_DEFINES="$DART_DEFINES --dart-define=COGNITO_USER_POOL_ID=$COGNITO_USER_POOL_ID"
fi
if [[ -n "$COGNITO_CLIENT_ID" ]]; then
  DART_DEFINES="$DART_DEFINES --dart-define=COGNITO_CLIENT_ID=$COGNITO_CLIENT_ID"
fi
DART_DEFINES="$DART_DEFINES --dart-define=COGNITO_REGION=me-south-1"
DART_DEFINES="$DART_DEFINES --dart-define=ENVIRONMENT=$ENV"

# shellcheck disable=SC2086
flutter build web --release --no-tree-shake-icons --target lib/main_web.dart $DART_DEFINES
echo "Web build complete: $MOBILE_DIR/build/web/"
echo ""

# Step 6: Deploy web app to S3 + invalidate CloudFront
echo "[6/6] Deploying web app to S3..."

WEB_STACK="wereda-connect-${ENV}-web"
WEBAPP_BUCKET=$(aws cloudformation describe-stacks \
  --stack-name "$WEB_STACK" \
  --region me-south-1 \
  --query 'Stacks[0].Outputs[?OutputKey==`WebappBucketName`].OutputValue' \
  --output text 2>/dev/null || echo "")

WEBAPP_CF_ID=$(aws cloudformation describe-stacks \
  --stack-name "$WEB_STACK" \
  --region me-south-1 \
  --query 'Stacks[0].Outputs[?OutputKey==`WebappDistributionIdOutput`].OutputValue' \
  --output text 2>/dev/null || echo "")

if [[ -z "$WEBAPP_BUCKET" ]]; then
  echo "Error: Could not resolve webapp S3 bucket from stack $WEB_STACK"
  echo "Skipping web deploy. Deploy manually via: gh workflow run deploy-webapp.yml"
  exit 1
fi

echo "  S3 Bucket: $WEBAPP_BUCKET"
echo "  CloudFront: ${WEBAPP_CF_ID:-<not found>}"

# Sync assets with long cache (immutable hashed files)
aws s3 sync "$MOBILE_DIR/build/web/" "s3://$WEBAPP_BUCKET/" \
  --delete \
  --cache-control "public, max-age=31536000, immutable" \
  --exclude "index.html" \
  --exclude "flutter_service_worker.js" \
  --exclude "version.json" \
  --region me-south-1

# Upload mutable files with no-cache
aws s3 cp "$MOBILE_DIR/build/web/index.html" \
  "s3://$WEBAPP_BUCKET/index.html" \
  --cache-control "public, max-age=0, must-revalidate" \
  --region me-south-1

aws s3 cp "$MOBILE_DIR/build/web/flutter_service_worker.js" \
  "s3://$WEBAPP_BUCKET/flutter_service_worker.js" \
  --cache-control "public, max-age=0, must-revalidate" \
  --region me-south-1

aws s3 cp "$MOBILE_DIR/build/web/version.json" \
  "s3://$WEBAPP_BUCKET/version.json" \
  --cache-control "public, max-age=0, must-revalidate" \
  --region me-south-1

echo "S3 sync complete."

# Invalidate CloudFront cache
if [[ -n "$WEBAPP_CF_ID" ]]; then
  echo "Invalidating CloudFront cache..."
  aws cloudfront create-invalidation \
    --distribution-id "$WEBAPP_CF_ID" \
    --paths "/index.html" "/flutter_service_worker.js" "/version.json" \
    --region us-east-1 \
    --output text
  echo "CloudFront invalidation submitted."
fi

echo ""
echo "=== Deployment complete: $ENV ==="
echo ""

# Resolve webapp URL
case "$ENV" in
  dev)     WEBAPP_URL="https://dev-app.weredaconnect.com" ;;
  staging) WEBAPP_URL="https://staging-app.weredaconnect.com" ;;
  prod)    WEBAPP_URL="https://app.weredaconnect.com" ;;
esac
echo "  Server API: ${API_BASE_URL:-<check CDK outputs>}"
echo "  Web App:    $WEBAPP_URL"
