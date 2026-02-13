#!/bin/bash
# scripts/deploy.sh
# Build and deploy WeredaConnect server + infrastructure
# Usage: ./scripts/deploy.sh [dev|staging|prod]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
SERVER_DIR="$ROOT_DIR/server"
INFRA_DIR="$ROOT_DIR/infra"

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
echo "[1/4] Running server tests..."
cd "$SERVER_DIR"
dotnet test --verbosity minimal --no-restore 2>/dev/null || dotnet test --verbosity minimal
echo "Tests passed."
echo ""

# Step 2: Build .NET AOT for Linux ARM64
echo "[2/4] Building .NET AOT (linux-arm64)..."
dotnet publish -c Release -r linux-arm64 --self-contained true
echo "Build complete: $SERVER_DIR/bin/Release/net10.0/linux-arm64/publish/"
echo ""

# Step 3: Install CDK dependencies if needed
echo "[3/4] Preparing CDK..."
cd "$INFRA_DIR"
if [ ! -d "node_modules" ]; then
  npm ci
fi
npx tsc
echo "CDK ready."
echo ""

# Step 4: Deploy with CDK
echo "[4/4] Deploying to AWS ($ENV)..."
npx cdk deploy --all --context env="$ENV" --require-approval broadening
echo ""
echo "=== Deployment complete: $ENV ==="
