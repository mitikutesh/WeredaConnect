#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# setup-gh-secrets.sh — Configure GitHub repository secrets for WeredaConnect
#
# Prerequisites:
#   1. gh CLI authenticated:  gh auth login
#   2. AWS CLI configured:    aws sts get-caller-identity
#   3. CDK deployed:          cd infra && npx cdk deploy --all
#
# Usage:
#   ./scripts/setup-gh-secrets.sh              # auto-detect from CDK outputs
#   ./scripts/setup-gh-secrets.sh --env dev    # specify environment
#   ./scripts/setup-gh-secrets.sh --manual     # prompt for every value
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

ENV="dev"
MANUAL=false
REPO=""
AWS_REGION="me-south-1"

# ── Parse arguments ──────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
  case $1 in
    --env)     ENV="$2"; shift 2 ;;
    --manual)  MANUAL=true; shift ;;
    --repo)    REPO="$2"; shift 2 ;;
    --region)  AWS_REGION="$2"; shift 2 ;;
    -h|--help)
      echo "Usage: $0 [--env dev|staging|prod] [--manual] [--repo owner/repo] [--region aws-region]"
      exit 0 ;;
    *) echo "Unknown option: $1"; exit 1 ;;
  esac
done

PREFIX="wereda-connect-${ENV}"

# ── Verify prerequisites ────────────────────────────────────────────────────
echo -e "${CYAN}Checking prerequisites...${NC}"

if ! command -v gh &>/dev/null; then
  echo -e "${RED}Error: gh CLI not found. Install from https://cli.github.com${NC}"
  exit 1
fi

if ! gh auth status &>/dev/null; then
  echo -e "${RED}Error: Not authenticated with GitHub. Run: gh auth login${NC}"
  exit 1
fi

if ! command -v aws &>/dev/null; then
  echo -e "${YELLOW}Warning: AWS CLI not found. Will use manual mode for AWS values.${NC}"
  MANUAL=true
fi

# Auto-detect repo from git remote
if [[ -z "$REPO" ]]; then
  REPO=$(gh repo view --json nameWithOwner -q '.nameWithOwner' 2>/dev/null || true)
  if [[ -z "$REPO" ]]; then
    echo -e "${RED}Error: Could not detect repository. Use --repo owner/name${NC}"
    exit 1
  fi
fi

echo -e "${GREEN}Repository: ${REPO}${NC}"
echo -e "${GREEN}Environment: ${ENV}${NC}"
echo -e "${GREEN}AWS Region: ${AWS_REGION}${NC}"
echo ""

# ── Helper: set a GitHub secret ──────────────────────────────────────────────
set_secret() {
  local name="$1"
  local value="$2"
  local env_name="${3:-}"

  if [[ -z "$value" ]]; then
    echo -e "${YELLOW}  Skipping ${name} (empty value)${NC}"
    return
  fi

  if [[ -n "$env_name" ]]; then
    echo "$value" | gh secret set "$name" --repo "$REPO" --env "$env_name"
    echo -e "${GREEN}  ✓ ${name} → env:${env_name}${NC}"
  else
    echo "$value" | gh secret set "$name" --repo "$REPO"
    echo -e "${GREEN}  ✓ ${name}${NC}"
  fi
}

# ── Helper: get CDK stack output ─────────────────────────────────────────────
get_stack_output() {
  local stack_name="$1"
  local output_key="$2"
  aws cloudformation describe-stacks \
    --stack-name "$stack_name" \
    --region "$AWS_REGION" \
    --query "Stacks[0].Outputs[?OutputKey=='${output_key}'].OutputValue" \
    --output text 2>/dev/null || echo ""
}

# ── Helper: prompt with default ──────────────────────────────────────────────
prompt_value() {
  local prompt_text="$1"
  local default_val="${2:-}"
  local result

  if [[ -n "$default_val" ]]; then
    read -rp "  ${prompt_text} [${default_val}]: " result
    echo "${result:-$default_val}"
  else
    read -rp "  ${prompt_text}: " result
    echo "$result"
  fi
}

# ── Step 1: Create GitHub OIDC IAM Role (if needed) ─────────────────────────
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo -e "${CYAN}  Step 1: AWS OIDC Role for GitHub Actions${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo ""

DEPLOY_ROLE_ARN=""

if [[ "$MANUAL" == true ]]; then
  DEPLOY_ROLE_ARN=$(prompt_value "AWS_DEPLOY_ROLE_ARN (IAM role ARN for GitHub OIDC)")
else
  # Try to find existing role
  DEPLOY_ROLE_ARN=$(aws iam get-role \
    --role-name "${PREFIX}-github-deploy" \
    --query 'Role.Arn' --output text 2>/dev/null || echo "")

  if [[ -z "$DEPLOY_ROLE_ARN" || "$DEPLOY_ROLE_ARN" == "None" ]]; then
    echo -e "${YELLOW}  No existing deploy role found. Creating one...${NC}"

    ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
    GITHUB_ORG=$(echo "$REPO" | cut -d'/' -f1)
    GITHUB_REPO_NAME=$(echo "$REPO" | cut -d'/' -f2)

    # Check if the OIDC provider exists
    OIDC_PROVIDER_ARN="arn:aws:iam::${ACCOUNT_ID}:oidc-provider/token.actions.githubusercontent.com"
    if ! aws iam get-open-id-connect-provider --open-id-connect-provider-arn "$OIDC_PROVIDER_ARN" &>/dev/null; then
      echo -e "${CYAN}  Creating GitHub OIDC provider...${NC}"
      aws iam create-open-id-connect-provider \
        --url "https://token.actions.githubusercontent.com" \
        --thumbprint-list "6938fd4d98bab03faadb97b34396831e3780aea1" \
        --client-id-list "sts.amazonaws.com" \
        --region "$AWS_REGION"
      echo -e "${GREEN}  ✓ OIDC provider created${NC}"
    else
      echo -e "${GREEN}  ✓ OIDC provider already exists${NC}"
    fi

    # Create the trust policy
    TRUST_POLICY=$(cat <<POLICY
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": {
        "Federated": "arn:aws:iam::${ACCOUNT_ID}:oidc-provider/token.actions.githubusercontent.com"
      },
      "Action": "sts:AssumeRoleWithWebIdentity",
      "Condition": {
        "StringEquals": {
          "token.actions.githubusercontent.com:aud": "sts.amazonaws.com"
        },
        "StringLike": {
          "token.actions.githubusercontent.com:sub": "repo:${GITHUB_ORG}/${GITHUB_REPO_NAME}:*"
        }
      }
    }
  ]
}
POLICY
)

    # Create the role
    DEPLOY_ROLE_ARN=$(aws iam create-role \
      --role-name "${PREFIX}-github-deploy" \
      --assume-role-policy-document "$TRUST_POLICY" \
      --description "GitHub Actions deploy role for WeredaConnect ${ENV}" \
      --query 'Role.Arn' --output text)

    # Attach policies for S3 + CloudFront access
    DEPLOY_POLICY=$(cat <<POLICY
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "S3Deploy",
      "Effect": "Allow",
      "Action": [
        "s3:PutObject",
        "s3:GetObject",
        "s3:DeleteObject",
        "s3:ListBucket"
      ],
      "Resource": [
        "arn:aws:s3:::${PREFIX}-website",
        "arn:aws:s3:::${PREFIX}-website/*",
        "arn:aws:s3:::${PREFIX}-webapp",
        "arn:aws:s3:::${PREFIX}-webapp/*",
        "arn:aws:s3:::${PREFIX}-admin",
        "arn:aws:s3:::${PREFIX}-admin/*"
      ]
    },
    {
      "Sid": "CloudFrontInvalidation",
      "Effect": "Allow",
      "Action": "cloudfront:CreateInvalidation",
      "Resource": "arn:aws:cloudfront::${ACCOUNT_ID}:distribution/*"
    }
  ]
}
POLICY
)

    aws iam put-role-policy \
      --role-name "${PREFIX}-github-deploy" \
      --policy-name "${PREFIX}-deploy-policy" \
      --policy-document "$DEPLOY_POLICY"

    echo -e "${GREEN}  ✓ IAM role created: ${DEPLOY_ROLE_ARN}${NC}"
  else
    echo -e "${GREEN}  ✓ Existing role found: ${DEPLOY_ROLE_ARN}${NC}"
  fi
fi

echo ""

# ── Step 2: Collect values from CDK outputs or .env files ────────────────────
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo -e "${CYAN}  Step 2: Collecting deployment values${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo ""

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

if [[ "$MANUAL" == true ]]; then
  # ── Manual mode: prompt for everything ──
  API_BASE_URL=$(prompt_value "API_BASE_URL" "https://b8xqjg4myb.execute-api.me-south-1.amazonaws.com")
  COGNITO_USER_POOL_ID=$(prompt_value "COGNITO_USER_POOL_ID" "me-south-1_ZoNImlLr7")
  COGNITO_CLIENT_ID=$(prompt_value "COGNITO_CLIENT_ID (mobile)" "430thc2sqqtpefbrmo98lacpg6")
  COGNITO_ADMIN_CLIENT_ID=$(prompt_value "COGNITO_ADMIN_CLIENT_ID" "5v277d3f7kufgp5661njtnsi36")
  COGNITO_REGION=$(prompt_value "COGNITO_REGION" "$AWS_REGION")

  WEBSITE_S3_BUCKET=$(prompt_value "WEBSITE_S3_BUCKET" "${PREFIX}-website")
  WEBAPP_S3_BUCKET=$(prompt_value "WEBAPP_S3_BUCKET" "${PREFIX}-webapp")
  ADMIN_S3_BUCKET=$(prompt_value "ADMIN_S3_BUCKET" "${PREFIX}-admin")

  WEBSITE_CF_DISTRIBUTION_ID=$(prompt_value "WEBSITE_CF_DISTRIBUTION_ID")
  WEBAPP_CF_DISTRIBUTION_ID=$(prompt_value "WEBAPP_CF_DISTRIBUTION_ID")
  ADMIN_CF_DISTRIBUTION_ID=$(prompt_value "ADMIN_CF_DISTRIBUTION_ID")

else
  # ── Auto mode: read from CDK outputs + .env fallback ──
  echo -e "${CYAN}  Reading CDK stack outputs...${NC}"

  WEB_STACK="${PREFIX}-web"
  API_STACK="${PREFIX}-api"

  # API & Cognito — try CDK first, fall back to .env
  API_BASE_URL=$(get_stack_output "$API_STACK" "ApiUrl")
  COGNITO_USER_POOL_ID=$(get_stack_output "$API_STACK" "UserPoolId")
  COGNITO_CLIENT_ID=$(get_stack_output "$API_STACK" "UserPoolClientId")
  COGNITO_ADMIN_CLIENT_ID=$(get_stack_output "$API_STACK" "AdminClientId")
  COGNITO_REGION="$AWS_REGION"

  # Fall back to .env files if CDK outputs are empty
  if [[ -z "$API_BASE_URL" && -f "$PROJECT_ROOT/mobile/.env" ]]; then
    echo -e "${YELLOW}  CDK outputs not found, falling back to .env files${NC}"
    API_BASE_URL=$(grep '^API_BASE_URL=' "$PROJECT_ROOT/mobile/.env" | cut -d= -f2-)
    COGNITO_USER_POOL_ID=$(grep '^COGNITO_USER_POOL_ID=' "$PROJECT_ROOT/mobile/.env" | cut -d= -f2-)
    COGNITO_CLIENT_ID=$(grep '^COGNITO_CLIENT_ID=' "$PROJECT_ROOT/mobile/.env" | cut -d= -f2-)
    COGNITO_REGION=$(grep '^COGNITO_REGION=' "$PROJECT_ROOT/mobile/.env" | cut -d= -f2-)
  fi
  if [[ -z "$COGNITO_ADMIN_CLIENT_ID" && -f "$PROJECT_ROOT/admin/.env" ]]; then
    COGNITO_ADMIN_CLIENT_ID=$(grep '^VITE_COGNITO_ADMIN_CLIENT_ID=' "$PROJECT_ROOT/admin/.env" | cut -d= -f2-)
  fi

  # S3 Buckets — deterministic names from CDK
  WEBSITE_S3_BUCKET=$(get_stack_output "$WEB_STACK" "WebsiteBucketName")
  WEBAPP_S3_BUCKET=$(get_stack_output "$WEB_STACK" "WebappBucketName")
  ADMIN_S3_BUCKET=$(get_stack_output "$WEB_STACK" "AdminBucketName")

  # Fall back to constructed names
  [[ -z "$WEBSITE_S3_BUCKET" ]] && WEBSITE_S3_BUCKET="${PREFIX}-website"
  [[ -z "$WEBAPP_S3_BUCKET" ]]  && WEBAPP_S3_BUCKET="${PREFIX}-webapp"
  [[ -z "$ADMIN_S3_BUCKET" ]]   && ADMIN_S3_BUCKET="${PREFIX}-admin"

  # CloudFront Distribution IDs — must come from CDK
  WEBSITE_CF_DISTRIBUTION_ID=$(get_stack_output "$WEB_STACK" "WebsiteDistributionIdOutput")
  WEBAPP_CF_DISTRIBUTION_ID=$(get_stack_output "$WEB_STACK" "WebappDistributionIdOutput")
  ADMIN_CF_DISTRIBUTION_ID=$(get_stack_output "$WEB_STACK" "AdminDistributionIdOutput")

  # Prompt for CloudFront IDs if not found
  if [[ -z "$WEBSITE_CF_DISTRIBUTION_ID" ]]; then
    echo -e "${YELLOW}  CloudFront IDs not found in CDK outputs. Please enter manually.${NC}"
    WEBSITE_CF_DISTRIBUTION_ID=$(prompt_value "WEBSITE_CF_DISTRIBUTION_ID")
    WEBAPP_CF_DISTRIBUTION_ID=$(prompt_value "WEBAPP_CF_DISTRIBUTION_ID")
    ADMIN_CF_DISTRIBUTION_ID=$(prompt_value "ADMIN_CF_DISTRIBUTION_ID")
  fi
fi

echo ""

# ── Step 3: Review and confirm ───────────────────────────────────────────────
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo -e "${CYAN}  Step 3: Review secrets to be set${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo ""
echo "  AWS_DEPLOY_ROLE_ARN         = ${DEPLOY_ROLE_ARN:-(empty)}"
echo "  API_BASE_URL                = ${API_BASE_URL:-(empty)}"
echo "  COGNITO_USER_POOL_ID        = ${COGNITO_USER_POOL_ID:-(empty)}"
echo "  COGNITO_CLIENT_ID           = ${COGNITO_CLIENT_ID:-(empty)}"
echo "  COGNITO_ADMIN_CLIENT_ID     = ${COGNITO_ADMIN_CLIENT_ID:-(empty)}"
echo "  COGNITO_REGION              = ${COGNITO_REGION:-(empty)}"
echo "  WEBSITE_S3_BUCKET           = ${WEBSITE_S3_BUCKET:-(empty)}"
echo "  WEBAPP_S3_BUCKET            = ${WEBAPP_S3_BUCKET:-(empty)}"
echo "  ADMIN_S3_BUCKET             = ${ADMIN_S3_BUCKET:-(empty)}"
echo "  WEBSITE_CF_DISTRIBUTION_ID  = ${WEBSITE_CF_DISTRIBUTION_ID:-(empty)}"
echo "  WEBAPP_CF_DISTRIBUTION_ID   = ${WEBAPP_CF_DISTRIBUTION_ID:-(empty)}"
echo "  ADMIN_CF_DISTRIBUTION_ID    = ${ADMIN_CF_DISTRIBUTION_ID:-(empty)}"
echo ""

read -rp "Proceed with setting these secrets on ${REPO}? [y/N] " confirm
if [[ "$confirm" != [yY] ]]; then
  echo -e "${YELLOW}Aborted.${NC}"
  exit 0
fi

echo ""

# ── Step 4: Set GitHub secrets ───────────────────────────────────────────────
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo -e "${CYAN}  Step 4: Setting GitHub repository secrets${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════════════${NC}"
echo ""

set_secret "AWS_DEPLOY_ROLE_ARN"        "$DEPLOY_ROLE_ARN"
set_secret "API_BASE_URL"               "$API_BASE_URL"
set_secret "COGNITO_USER_POOL_ID"       "$COGNITO_USER_POOL_ID"
set_secret "COGNITO_CLIENT_ID"          "$COGNITO_CLIENT_ID"
set_secret "COGNITO_ADMIN_CLIENT_ID"    "$COGNITO_ADMIN_CLIENT_ID"
set_secret "COGNITO_REGION"             "$COGNITO_REGION"
set_secret "WEBSITE_S3_BUCKET"          "$WEBSITE_S3_BUCKET"
set_secret "WEBAPP_S3_BUCKET"           "$WEBAPP_S3_BUCKET"
set_secret "ADMIN_S3_BUCKET"            "$ADMIN_S3_BUCKET"
set_secret "WEBSITE_CF_DISTRIBUTION_ID" "$WEBSITE_CF_DISTRIBUTION_ID"
set_secret "WEBAPP_CF_DISTRIBUTION_ID"  "$WEBAPP_CF_DISTRIBUTION_ID"
set_secret "ADMIN_CF_DISTRIBUTION_ID"   "$ADMIN_CF_DISTRIBUTION_ID"

echo ""
echo -e "${GREEN}═══════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}  All secrets configured!${NC}"
echo -e "${GREEN}═══════════════════════════════════════════════════════════${NC}"
echo ""
echo "  Next steps:"
echo "    1. Verify secrets:   gh secret list --repo $REPO"
echo "    2. Trigger deploys:"
echo "       gh workflow run 'Deploy Admin Dashboard (Vite)' --field environment=${ENV}"
echo "       gh workflow run 'Deploy Webapp (Flutter Web)' --field environment=${ENV}"
echo "       gh workflow run 'Deploy Website (Astro)' --field environment=${ENV}"
echo ""
