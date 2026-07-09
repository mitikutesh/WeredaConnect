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

# 3. Add to the "admin" group — THIS is what authorizes /admin/* access.
#    Both the admin app (cognito:groups) and the server (AdminAuthHelper) gate on it.
aws cognito-idp admin-add-user-to-group \
  --user-pool-id "$ADMIN_USER_POOL_ID" \
  --username "$ADMIN_PHONE" \
  --group-name "$ADMIN_GROUP" \
  --region "$REGION"
echo "--- Added to '$ADMIN_GROUP' group."

# 4. Verify login and that the token carries the admin group.
echo "--- Verifying login..."
ID_TOKEN=$(aws cognito-idp initiate-auth \
  --auth-flow USER_PASSWORD_AUTH \
  --client-id "$ADMIN_CLIENT_ID" \
  --auth-parameters "USERNAME=$ADMIN_PHONE,PASSWORD=$ADMIN_PASSWORD" \
  --region "$REGION" \
  --query 'AuthenticationResult.IdToken' \
  --output text 2>&1) || true

if [ -n "$ID_TOKEN" ] && [ "$ID_TOKEN" != "None" ] && [[ "$ID_TOKEN" == *.*.* ]]; then
  GROUPS=$(python3 -c "
import base64, json, sys
p = '$ID_TOKEN'.split('.')[1]; p += '=' * (-len(p) % 4)
print(','.join(json.loads(base64.urlsafe_b64decode(p)).get('cognito:groups', [])))
" 2>/dev/null || echo "")
  if [[ ",$GROUPS," == *",$ADMIN_GROUP,"* ]]; then
    echo "    Login OK, token carries '$ADMIN_GROUP' group."
  else
    echo "    WARNING: logged in but token groups = [$GROUPS] (missing '$ADMIN_GROUP')."
  fi
else
  echo "    WARNING: Login verification failed — $ID_TOKEN"
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
