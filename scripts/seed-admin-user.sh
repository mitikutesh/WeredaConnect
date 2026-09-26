#!/bin/bash
# Seed a platform admin user in the ADMIN Cognito pool for dev testing.
#
# The admin dashboard (admin/) authenticates against a DEDICATED Cognito pool
# (AdminUserPool, see infra/lib/stacks/api-stack.ts) — NOT the main app pool
# used by seed-test-users.sh. Admin authorization is by membership in the
# Cognito "admin" group (checked client-side in admin/src/lib/auth.ts and
# server-side in AdminAuthHelper.IsAdmin), so this script:
#   1. Creates the user in the admin pool (phone = username)
#   2. Sets a permanent password (USER_PASSWORD_AUTH — admin app uses this flow)
#   3. Adds the user to the "admin" group  <-- REQUIRED, this is what grants access
#   4. Verifies login + that the "admin" group is present in the token
#
# Usage:
#   ./scripts/seed-admin-user.sh
#   ADMIN_PHONE=+251900100009 ADMIN_PASSWORD='AdminPass1dev' ./scripts/seed-admin-user.sh
#   ADMIN_USER_POOL_ID=... ADMIN_CLIENT_ID=... ./scripts/seed-admin-user.sh   # skip auto-discovery
#   SEED_ADMIN_STRICT=true ./scripts/seed-admin-user.sh   # exit 1 when the login check fails (CI)
#
# CI: deploy-server.yml runs this after every dev deploy with the ids taken
# from the deploy's own CDK outputs (SIM-18), so the dashboard's test admin
# always exists on dev. Idempotent: safe to re-run, also resets the password.
#
# Prerequisites:
#   - AWS CLI configured with eu-north-1 access
#   - Dev environment deployed (CDK stacks running)

set -euo pipefail

REGION="${REGION:-eu-north-1}"
ADMIN_PHONE="${ADMIN_PHONE:-+251900100009}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-AdminPass1dev}"
ADMIN_DISPLAY_NAME="${ADMIN_DISPLAY_NAME:-Platform Admin}"
ADMIN_GROUP="admin"
STRICT="${SEED_ADMIN_STRICT:-false}"

# Discover the admin pool + client from CloudFormation outputs unless supplied.
# The outputs are AdminUserPoolId / AdminClientId (api-stack.ts:1579).
ADMIN_USER_POOL_ID="${ADMIN_USER_POOL_ID:-}"
ADMIN_CLIENT_ID="${ADMIN_CLIENT_ID:-}"

if [ -z "$ADMIN_USER_POOL_ID" ] || [ -z "$ADMIN_CLIENT_ID" ]; then
  echo "Discovering admin pool from CloudFormation outputs..."
  ADMIN_USER_POOL_ID=$(aws cloudformation describe-stacks --region "$REGION" \
    --query "Stacks[].Outputs[?OutputKey=='AdminUserPoolId'].OutputValue | [][0]" \
    --output text 2>/dev/null | head -n1)
  ADMIN_CLIENT_ID=$(aws cloudformation describe-stacks --region "$REGION" \
    --query "Stacks[].Outputs[?OutputKey=='AdminClientId'].OutputValue | [][0]" \
    --output text 2>/dev/null | head -n1)
fi

if [ -z "$ADMIN_USER_POOL_ID" ] || [ "$ADMIN_USER_POOL_ID" = "None" ] \
   || [ -z "$ADMIN_CLIENT_ID" ] || [ "$ADMIN_CLIENT_ID" = "None" ]; then
  echo "ERROR: Could not resolve the admin pool. Set ADMIN_USER_POOL_ID and" >&2
  echo "       ADMIN_CLIENT_ID env vars (AdminUserPoolId / AdminClientId stack outputs)." >&2
  exit 1
fi

echo "=== WeredaConnect Admin User Seeder ==="
echo "Region:     $REGION"
echo "Admin pool: $ADMIN_USER_POOL_ID"
echo "Client:     $ADMIN_CLIENT_ID"
echo "Admin:      $ADMIN_PHONE ($ADMIN_DISPLAY_NAME)"
echo ""

# 1. Create user in the admin pool (skip if it already exists)
if aws cognito-idp admin-get-user \
    --user-pool-id "$ADMIN_USER_POOL_ID" \
    --username "$ADMIN_PHONE" \
    --region "$REGION" &>/dev/null; then
  echo "--- Cognito admin user already exists, skipping creation."
else
  aws cognito-idp admin-create-user \
    --user-pool-id "$ADMIN_USER_POOL_ID" \
    --username "$ADMIN_PHONE" \
    --user-attributes \
      "Name=phone_number,Value=$ADMIN_PHONE" \
      "Name=custom:role,Value=admin" \
      "Name=custom:displayName,Value=$ADMIN_DISPLAY_NAME" \
    --message-action SUPPRESS \
    --region "$REGION" >/dev/null
  echo "--- Cognito admin user created."
fi

# 2. Set a permanent password (makes user CONFIRMED, clears FORCE_CHANGE_PASSWORD)
aws cognito-idp admin-set-user-password \
  --user-pool-id "$ADMIN_USER_POOL_ID" \
  --username "$ADMIN_PHONE" \
  --password "$ADMIN_PASSWORD" \
  --permanent \
  --region "$REGION"
echo "--- Password set."

# 2b. The admin pool requires TOTP MFA in every environment. Clearing the
#     software-token preference makes the next login answer MFA_SETUP, so the
#     dashboard e2e (scripts/e2e_admin.py) can enrol a fresh authenticator each
#     run without a persisted secret. A human admin enrols once in the web app.
aws cognito-idp admin-set-user-mfa-preference \
  --user-pool-id "$ADMIN_USER_POOL_ID" \
  --username "$ADMIN_PHONE" \
  --software-token-mfa-settings Enabled=false,PreferredMfa=false \
  --sms-mfa-settings Enabled=false,PreferredMfa=false \
  --region "$REGION"
echo "--- MFA preference reset (e2e enrols a fresh TOTP)."

# 3. Add to the "admin" group — THIS is what authorizes /admin/* access.
#    Both the admin app (cognito:groups) and the server (AdminAuthHelper) gate on it.
aws cognito-idp admin-add-user-to-group \
  --user-pool-id "$ADMIN_USER_POOL_ID" \
  --username "$ADMIN_PHONE" \
  --group-name "$ADMIN_GROUP" \
  --region "$REGION"
echo "--- Added to '$ADMIN_GROUP' group."

# 4. Verify login and the admin group. The pool requires MFA, so a correct
#    password answers with an MFA challenge (MFA_SETUP after step 2b) rather
#    than tokens; that proves the password, and the group is read directly.
echo "--- Verifying login..."
AUTH_RESULT=$(aws cognito-idp initiate-auth \
  --auth-flow USER_PASSWORD_AUTH \
  --client-id "$ADMIN_CLIENT_ID" \
  --auth-parameters "USERNAME=$ADMIN_PHONE,PASSWORD=$ADMIN_PASSWORD" \
  --region "$REGION" \
  --query '[ChallengeName, AuthenticationResult.IdToken]' \
  --output text 2>&1) || true
read -r CHALLENGE ID_TOKEN <<< "$AUTH_RESULT"

VERIFY_OK=false
LOGIN_OK=false
TOKEN_GROUPS=""
if [ -n "$ID_TOKEN" ] && [ "$ID_TOKEN" != "None" ] && [[ "$ID_TOKEN" == *.*.* ]]; then
  # NOTE: not named GROUPS. In bash, GROUPS is a special variable (the user's
  # group ids) and assignments to it are silently ignored, which made this
  # check always report the admin group as missing.
  TOKEN_GROUPS=$(python3 -c "
import base64, json, sys
p = '$ID_TOKEN'.split('.')[1]; p += '=' * (-len(p) % 4)
print(','.join(json.loads(base64.urlsafe_b64decode(p)).get('cognito:groups', [])))
" 2>/dev/null || echo "")
  echo "    Login OK (tokens issued)."
  LOGIN_OK=true
elif [ "$CHALLENGE" = "MFA_SETUP" ] || [ "$CHALLENGE" = "SOFTWARE_TOKEN_MFA" ]; then
  TOKEN_GROUPS=$(aws cognito-idp admin-list-groups-for-user \
    --user-pool-id "$ADMIN_USER_POOL_ID" \
    --username "$ADMIN_PHONE" \
    --region "$REGION" \
    --query 'Groups[].GroupName' \
    --output text 2>/dev/null | tr '\t' ',' || echo "")
  echo "    Password OK (MFA challenge $CHALLENGE, as the pool requires)."
  LOGIN_OK=true
else
  echo "    WARNING: Login verification failed: $AUTH_RESULT"
fi

if [[ ",$TOKEN_GROUPS," == *",$ADMIN_GROUP,"* ]]; then
  echo "    User carries the '$ADMIN_GROUP' group."
  VERIFY_OK=true
elif [ "$LOGIN_OK" = true ]; then
  echo "    WARNING: groups = [$TOKEN_GROUPS] (missing '$ADMIN_GROUP')."
fi

if [ "$VERIFY_OK" != true ] && [ "$STRICT" = true ]; then
  echo "ERROR: SEED_ADMIN_STRICT is set and the login check failed." >&2
  exit 1
fi

echo ""
echo "=== Done ==="
echo "Admin login (admin dashboard):"
echo "  Phone:    $ADMIN_PHONE"
echo "  Password: $ADMIN_PASSWORD"
echo ""
echo "Set these in admin/.env (from stack outputs):"
echo "  VITE_COGNITO_ADMIN_USER_POOL_ID=$ADMIN_USER_POOL_ID"
echo "  VITE_COGNITO_ADMIN_CLIENT_ID=$ADMIN_CLIENT_ID"
echo "  VITE_COGNITO_REGION=$REGION"
