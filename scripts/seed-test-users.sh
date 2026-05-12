#!/bin/bash
# Seed persistent test users in Cognito + DynamoDB for dev testing.
#
# These users are created once and reused across all test sessions.
# If a user already exists, the script skips creation and just ensures
# the password is set and the user is confirmed.
#
# Usage:
#   ./scripts/seed-test-users.sh
#
# Prerequisites:
#   - AWS CLI configured with eu-north-1 access
#   - Dev environment deployed (CDK stacks running)

set -euo pipefail

REGION="eu-north-1"
USER_POOL_ID="eu-north-1_1LL9tzoxw"
CLIENT_ID="5s1bblbi8vl6cb58q02vk9paml"
USERS_TABLE="wereda-connect-dev-users"
PASSWORD="TestPass1dev"
NOW=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Test users: phone, role, woreda, displayName, preferredLanguage
declare -a USERS=(
  "+251900100001|farmer|Debre Birhan|Abebe Kebede|am"
  "+251900100002|trucker|Addis Ababa|Tadesse Worku|am"
  "+251900100003|buyer|Dire Dawa|Meron Alemu|en"
)

echo "=== WeredaConnect Test User Seeder ==="
echo "Region:     $REGION"
echo "Pool:       $USER_POOL_ID"
echo "Table:      $USERS_TABLE"
echo "Users:      ${#USERS[@]}"
echo ""

CREATED=0
SKIPPED=0
ERRORS=0

for entry in "${USERS[@]}"; do
  IFS='|' read -r PHONE ROLE WOREDA DISPLAY_NAME LANG <<< "$entry"

  echo "--- Seeding $ROLE: $DISPLAY_NAME ($PHONE)"

  # 1. Create user in Cognito (skip if exists)
  USER_SUB=""
  if aws cognito-idp admin-get-user \
      --user-pool-id "$USER_POOL_ID" \
      --username "$PHONE" \
      --region "$REGION" &>/dev/null; then
    echo "    Cognito user already exists, skipping creation."
    USER_SUB=$(aws cognito-idp admin-get-user \
      --user-pool-id "$USER_POOL_ID" \
      --username "$PHONE" \
      --region "$REGION" \
      --query "UserAttributes[?Name=='sub'].Value | [0]" \
      --output text)
    SKIPPED=$((SKIPPED + 1))
  else
    # Create the user
    CREATE_OUTPUT=$(aws cognito-idp admin-create-user \
      --user-pool-id "$USER_POOL_ID" \
      --username "$PHONE" \
      --user-attributes \
        "Name=phone_number,Value=$PHONE" \
        "Name=phone_number_verified,Value=true" \
        "Name=custom:role,Value=$(echo "$ROLE" | awk '{print toupper(substr($0,1,1)) tolower(substr($0,2))}')" \
        "Name=custom:woreda,Value=$WOREDA" \
        "Name=custom:displayName,Value=$DISPLAY_NAME" \
        "Name=custom:preferredLanguage,Value=$LANG" \
      --message-action SUPPRESS \
      --region "$REGION" 2>&1) || {
        echo "    ERROR creating Cognito user: $CREATE_OUTPUT"
        ERRORS=$((ERRORS + 1))
        continue
      }

    USER_SUB=$(aws cognito-idp admin-get-user \
      --user-pool-id "$USER_POOL_ID" \
      --username "$PHONE" \
      --region "$REGION" \
      --query "UserAttributes[?Name=='sub'].Value | [0]" \
      --output text)
    echo "    Cognito user created (sub=$USER_SUB)"
    CREATED=$((CREATED + 1))
  fi

  # 2. Set permanent password (makes user CONFIRMED)
  aws cognito-idp admin-set-user-password \
    --user-pool-id "$USER_POOL_ID" \
    --username "$PHONE" \
    --password "$PASSWORD" \
    --permanent \
    --region "$REGION" 2>/dev/null || {
      echo "    WARNING: Could not set password"
    }
  echo "    Password set."

  # 3. Confirm signup (ensure user is not in FORCE_CHANGE_PASSWORD state)
  aws cognito-idp admin-confirm-sign-up \
    --user-pool-id "$USER_POOL_ID" \
    --username "$PHONE" \
    --region "$REGION" 2>/dev/null || true

  # 4. Create/update DynamoDB user record
  if [ -n "$USER_SUB" ] && [ "$USER_SUB" != "None" ]; then
    aws dynamodb put-item \
      --table-name "$USERS_TABLE" \
      --item "{
        \"userId\":           {\"S\": \"$USER_SUB\"},
        \"phoneNumber\":      {\"S\": \"$PHONE\"},
        \"displayName\":      {\"S\": \"$DISPLAY_NAME\"},
        \"role\":             {\"S\": \"$ROLE\"},
        \"woreda\":           {\"S\": \"$WOREDA\"},
        \"preferredLanguage\":{\"S\": \"$LANG\"},
        \"isPhoneVerified\":  {\"BOOL\": true},
        \"faydaVerified\":    {\"BOOL\": false},
        \"createdAt\":        {\"S\": \"$NOW\"},
        \"updatedAt\":        {\"S\": \"$NOW\"},
        \"version\":          {\"N\": \"1\"}
      }" \
      --region "$REGION" 2>/dev/null && \
      echo "    DynamoDB record written." || \
      echo "    WARNING: Could not write DynamoDB record."
  fi

  # 5. Verify login works
  LOGIN_RESULT=$(aws cognito-idp initiate-auth \
    --auth-flow USER_PASSWORD_AUTH \
    --client-id "$CLIENT_ID" \
    --auth-parameters "USERNAME=$PHONE,PASSWORD=$PASSWORD" \
    --region "$REGION" \
    --query 'AuthenticationResult.AccessToken' \
    --output text 2>&1) || true

  if [ -n "$LOGIN_RESULT" ] && [ "$LOGIN_RESULT" != "None" ] && [[ ! "$LOGIN_RESULT" == *"error"* ]]; then
    echo "    Login verified OK."
  else
    echo "    WARNING: Login verification failed — $LOGIN_RESULT"
  fi

  echo ""
done

echo "=== Done ==="
echo "Created: $CREATED | Skipped (existing): $SKIPPED | Errors: $ERRORS"
echo ""
echo "Test credentials:"
echo "  Password: $PASSWORD"
echo "  Farmer:   +251900100001"
echo "  Trucker:  +251900100002"
echo "  Buyer:    +251900100003"
