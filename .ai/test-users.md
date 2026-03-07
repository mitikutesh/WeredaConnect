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

## API Details

- **API URL:** `https://b8xqjg4myb.execute-api.me-south-1.amazonaws.com`
- **Cognito Pool:** `me-south-1_ZoNImlLr7`
- **Client ID:** `430thc2sqqtpefbrmo98lacpg6`
- **Auth Flow:** `USER_PASSWORD_AUTH`

## Login Example (curl)

```bash
curl -X POST https://b8xqjg4myb.execute-api.me-south-1.amazonaws.com/auth/login \
  -H "Content-Type: application/json" \
  -d '{"phoneNumber": "+251900100001", "password": "TestPass1dev"}'
```

## Login Example (Python)

```python
import json
from urllib.request import Request, urlopen

BASE_URL = "https://b8xqjg4myb.execute-api.me-south-1.amazonaws.com"

def login(phone: str, password: str = "TestPass1dev") -> dict:
    req = Request(f"{BASE_URL}/auth/login", method="POST",
                  data=json.dumps({"phoneNumber": phone, "password": password}).encode(),
                  headers={"Content-Type": "application/json"})
    with urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())

# Login as farmer
farmer = login("+251900100001")
farmer_token = farmer["tokens"]["accessToken"]

# Login as trucker
trucker = login("+251900100002")
trucker_token = trucker["tokens"]["accessToken"]

# Login as buyer
buyer = login("+251900100003")
buyer_token = buyer["tokens"]["accessToken"]
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
