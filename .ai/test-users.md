# Test Users (Dev Environment)

Persistent test users seeded in Cognito + DynamoDB for the `dev` environment.
Created by `scripts/seed-test-users.sh`. These users can be used for E2E testing,
Playwright web tests, Maestro mobile tests, and manual QA.

## Credentials

| Role | Phone | Password | Display Name | Woreda | Language |
|------|-------|----------|--------------|--------|----------|
| **farmer** | `+251900100001` | `TestPass1dev` | Abebe Kebede | Debre Birhan | am |
| **trucker** | `+251900100002` | `TestPass1dev` | Tadesse Worku | Addis Ababa | am |
| **buyer** | `+251900100003` | `TestPass1dev` | Meron Alemu | Dire Dawa | en |

**All users share the same password:** `TestPass1dev`

## Admin User (admin dashboard)

The admin dashboard (`admin/`) does **not** use the pool above. It authenticates
against a dedicated **AdminUserPool** (`infra/lib/stacks/api-stack.ts`), and
authorization is by membership in the Cognito **`admin` group** — the three
marketplace users above are not admins and cannot log in to the dashboard.

No admin user is seeded by default. Create one with:

```bash
./scripts/seed-admin-user.sh          # auto-discovers the admin pool from CFN outputs
```

| Role | Phone | Password | Notes |
|------|-------|----------|-------|
| **admin** | `+251900100009` | `AdminPass1dev` | member of the `admin` group |

The admin app also needs a local `admin/.env` (never committed) with
`VITE_COGNITO_ADMIN_USER_POOL_ID`, `VITE_COGNITO_ADMIN_CLIENT_ID`,
`VITE_API_BASE_URL`, and `VITE_COGNITO_REGION` — the seeder prints the pool/client
values on completion. Without this env, admin login fails with a generic
"service unavailable" before any credential check.

## API Details

- **API URL:** `https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com`
- **Cognito Pool:** `eu-north-1_1LL9tzoxw`
- **Client ID:** `5s1bblbi8vl6cb58q02vk9paml`
- **Auth Flow:** `USER_PASSWORD_AUTH`

## Login Example (curl)

```bash
curl -X POST https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com/auth/login \
  -H "Content-Type: application/json" \
  -d '{"phoneNumber": "+251900100001", "password": "TestPass1dev"}'
```

## Login Example (Python)

```python
import json
from urllib.request import Request, urlopen

BASE_URL = "https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com"

def login(phone: str, password: str = "TestPass1dev") -> dict:
    req = Request(f"{BASE_URL}/auth/login", method="POST",
                  data=json.dumps({"phoneNumber": phone, "password": password}).encode(),
                  headers={"Content-Type": "application/json"})
    with urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())

# Login as farmer
farmer = login("+251900100001")
farmer_token = farmer["tokens"]["idToken"]

# Login as trucker
trucker = login("+251900100002")
trucker_token = trucker["tokens"]["idToken"]

# Login as buyer
buyer = login("+251900100003")
buyer_token = buyer["tokens"]["idToken"]
```

## When to Use Which User

| Scenario | User |
|----------|------|
| Create crop listings | farmer (`+251900100001`) |
| Browse/buy crops, create orders | buyer (`+251900100003`) |
| Create trip listings, accept deliveries | trucker (`+251900100002`) |
| Full marketplace flow | All three |
| Escrow payment testing | buyer initiates, farmer receives |
| Delivery state machine | trucker updates status |

## Re-seeding

If users are deleted or corrupted, re-run:

```bash
./scripts/seed-test-users.sh
```

The script is idempotent — it skips existing users and ensures passwords are correct.
