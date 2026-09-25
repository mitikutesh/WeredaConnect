#!/usr/bin/env python3
"""
WeredaConnect E2E Test Script
=============================
Tests the full marketplace flow against the deployed dev API.

Prerequisites:
    - Python 3.8+ (no external dependencies, stdlib only)
    - AWS CLI configured (only needed for clearing rate limits)
    - API deployed to dev (run `bash scripts/deploy.sh dev` first)

Usage:
    # Run against default dev API
    python3 scripts/e2e_test.py

    # Run against a custom API URL
    python3 scripts/e2e_test.py --base-url https://your-api-url

Clearing rate limits:
    If tests fail with 429 (SYNC_RATE_LIMITED), clear the rate limit
    entries before re-running. This is common after multiple test runs.

    aws dynamodb scan \\
      --table-name wereda-connect-dev-rate-limits \\
      --region eu-north-1 \\
      --filter-expression "begins_with(pk, :prefix)" \\
      --expression-attribute-values '{":prefix":{"S":"SYNC#"}}' \\
      --projection-expression "pk,sk" \\
    | python3 -c "
    import json, sys, subprocess
    data = json.load(sys.stdin)
    items = data.get('Items', [])
    for item in items:
        key = json.dumps({'pk': item['pk'], 'sk': item['sk']})
        subprocess.run([
            'aws', 'dynamodb', 'delete-item',
            '--table-name', 'wereda-connect-dev-rate-limits',
            '--region', 'eu-north-1', '--key', key
        ], capture_output=True)
    print(f'Cleared {len(items)} SYNC rate limit entries')
    "

Test phases (10 phases, ~43 checks):
    Phase  1: Register/login 7 users (3 farmers, 2 truckers, 2 buyers)
    Phase  2: Create 6 crop listings (or reuse existing if fraud-dup)
    Phase  3: Create 3 trip listings
    Phase  4: Browse & filter crops and trips
    Phase  5: Create 3 deliveries (crop + trip + buyer)
    Phase  6: Delivery state machine (pending -> pickedUp -> inTransit -> delivered)
    Phase  7: Escrow payments (initiate, check status, payment history)
    Phase  8: Dispute flow (raise dispute, verify status)
    Phase  9: Browse deliveries, get specific crop/trip by ID
    Phase 10: Token refresh
"""

import argparse
import json
import ssl
import sys
import time
import uuid
from dataclasses import dataclass, field
from typing import Optional
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

_BASE_URL = "https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com"
PASSWORD = "testpass1a"  # 8+ chars, lowercase + digit

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def ts() -> int:
    """Millisecond timestamp for idempotency keys."""
    return int(time.time() * 1000)


def uid() -> str:
    return str(uuid.uuid4())


def print_header(title: str):
    print(f"\n{'='*60}")
    print(f"  {title}")
    print(f"{'='*60}")


def print_step(step: str):
    print(f"\n--- {step}")


def print_result(label: str, ok: bool, detail: str = ""):
    symbol = "[+]" if ok else "[X]"
    status = "PASS" if ok else "FAIL"
    msg = f"  {symbol} {label}: {status}"
    if detail:
        msg += f" -- {detail}"
    print(msg)


@dataclass
class ApiResponse:
    status_code: int
    body: str

    def json(self) -> dict:
        return json.loads(self.body)


def api(method: str, path: str, token: Optional[str] = None,
        json_body: Optional[dict] = None, params: Optional[dict] = None,
        retries: int = 3) -> ApiResponse:
    """Make an API call using urllib (no external deps). Retries on 429."""
    url = f"{_BASE_URL}{path}"
    if params:
        url += "?" + urlencode(params)

    data = None
    if json_body is not None:
        data = json.dumps(json_body).encode("utf-8")

    ctx = ssl.create_default_context()

    for attempt in range(retries + 1):
        req = Request(url, data=data, method=method)
        req.add_header("Content-Type", "application/json")
        if token:
            req.add_header("Authorization", f"Bearer {token}")

        try:
            with urlopen(req, timeout=30, context=ctx) as resp:
                body = resp.read().decode("utf-8")
                return ApiResponse(status_code=resp.status, body=body)
        except HTTPError as e:
            body = e.read().decode("utf-8") if e.fp else ""
            if e.code == 429 and attempt < retries:
                wait = 5 * (attempt + 1)  # 5s, 10s, 15s
                print(f"    Rate limited, waiting {wait}s (attempt {attempt + 1}/{retries})...")
                time.sleep(wait)
                continue
            return ApiResponse(status_code=e.code, body=body)

    return ApiResponse(status_code=429, body="Max retries exceeded")


# ---------------------------------------------------------------------------
# Data model for registered users
# ---------------------------------------------------------------------------

@dataclass
class User:
    phone: str
    role: str
    woreda: str
    display_name: str
    user_id: str = ""
    access_token: str = ""
    refresh_token: str = ""


@dataclass
class CropRecord:
    crop_id: str
    farmer_idx: int
    crop_type: str
    price_per_kg: float = 0.0  # orders must quote exactly this (SIM-05)


@dataclass
class TripRecord:
    trip_id: str
    trucker_idx: int
    from_woreda: str
    to_woreda: str
    price_per_quintal: float = 0.0  # transportCost = kg / 100 x this (SIM-05)


@dataclass
class DeliveryRecord:
    delivery_id: str
    farmer_idx: int
    trucker_idx: int
    buyer_idx: int
    crop_id: str
    trip_id: str
    qr_nonce: str = ""  # Server-generated, needed for pickup/delivery transitions
    order_id: str = ""  # Backing order; the escrow amount must equal its orderTotal


@dataclass
class TestState:
    """Accumulates IDs created during the test for cross-step references."""
    users: dict = field(default_factory=dict)
    crop_ids: list = field(default_factory=list)
    crops: list = field(default_factory=list)  # CropRecord list
    trip_ids: list = field(default_factory=list)
    trips: list = field(default_factory=list)  # TripRecord list
    delivery_ids: list = field(default_factory=list)
    deliveries: list = field(default_factory=list)  # DeliveryRecord list
    escrow_ids: list = field(default_factory=list)
    passed: int = 0
    failed: int = 0

    def record(self, label: str, ok: bool, detail: str = ""):
        print_result(label, ok, detail)
        if ok:
            self.passed += 1
        else:
            self.failed += 1


# ---------------------------------------------------------------------------
# Test users -- each needs a unique +251 phone number
# ---------------------------------------------------------------------------

TEST_USERS = [
    User(phone="+251900000011", role="farmer",  woreda="Debre Birhan",  display_name="Abebe Kebede"),
    User(phone="+251900000002", role="farmer",  woreda="Bishoftu",      display_name="Tigist Haile"),
    User(phone="+251900000003", role="farmer",  woreda="Adama",         display_name="Dawit Tadesse"),
    User(phone="+251900000004", role="trucker", woreda="Addis Ababa",   display_name="Tadesse Worku"),
    User(phone="+251900000005", role="trucker", woreda="Hawassa",       display_name="Bekele Girma"),
    User(phone="+251900000006", role="buyer",   woreda="Addis Ababa",   display_name="Meron Alemu"),
    User(phone="+251900000007", role="buyer",   woreda="Dire Dawa",     display_name="Yonas Tesfaye"),
]

# Crop listings to create (farmer index -> listings)
CROP_LISTINGS = [
    {"farmer_idx": 0, "cropType": "teff",    "quantity": 50.0,  "pricePerKg": 45.0,  "description": "White teff, premium quality"},
    {"farmer_idx": 0, "cropType": "wheat",   "quantity": 30.0,  "pricePerKg": 28.0,  "description": "Durum wheat, freshly harvested"},
    {"farmer_idx": 1, "cropType": "coffee",  "quantity": 20.0,  "pricePerKg": 120.0, "description": "Yirgacheffe washed coffee beans"},
    {"farmer_idx": 1, "cropType": "teff",    "quantity": 80.0,  "pricePerKg": 42.0,  "description": "Red teff from Bishoftu"},
    {"farmer_idx": 2, "cropType": "maize",   "quantity": 100.0, "pricePerKg": 15.0,  "description": "Yellow maize, dried and clean"},
    {"farmer_idx": 2, "cropType": "sesame",  "quantity": 25.0,  "pricePerKg": 95.0,  "description": "Humera-type sesame seeds"},
]

# Trip listings to create (trucker index in TEST_USERS)
TRIP_LISTINGS = [
    {"trucker_idx": 3, "fromWoreda": "Debre Birhan", "toWoreda": "Addis Ababa", "capacity": 150.0, "pricePerQuintal": 120.0},
    {"trucker_idx": 3, "fromWoreda": "Bishoftu",     "toWoreda": "Addis Ababa", "capacity": 100.0, "pricePerQuintal": 80.0},
    {"trucker_idx": 4, "fromWoreda": "Adama",        "toWoreda": "Dire Dawa",   "capacity": 200.0, "pricePerQuintal": 200.0},
]


# ---------------------------------------------------------------------------
# Phase 1: Register Users
# ---------------------------------------------------------------------------

def phase_register(state: TestState):
    print_header("PHASE 1: Register Users (Fayda verify + register)")

    # Step 1a: Get a Fayda verification token (dev bypass -- only used for first user)
    print_step("Getting Fayda dev bypass token")
    fayda_resp = api("POST", "/auth/fayda-verify", json_body={
        "authorizationCode": "test-code-123",
        "state": "test-state",
        "redirectUri": "weredaconnect://fayda-callback"
    })
    fayda_token = None
    if fayda_resp.status_code == 200:
        body = fayda_resp.json()
        fayda_token = body.get("verificationToken")
        state.record("Fayda dev bypass", True, f"token expires at {body.get('expiresAt', '?')}")
    else:
        state.record("Fayda dev bypass", False, f"{fayda_resp.status_code}: {fayda_resp.body[:200]}")

    # Step 1b: Register each user
    for i, user in enumerate(TEST_USERS):
        print_step(f"Registering {user.role} '{user.display_name}' ({user.phone})")

        body = {
            "phoneNumber": user.phone,
            "password": PASSWORD,
            "role": user.role,
            "woreda": user.woreda,
            "displayName": user.display_name,
            "preferredLanguage": "am",
        }
        # Only the first user gets Fayda token (FAYDA-DEV-001 is unique per account)
        if i == 0 and fayda_token:
            body["faydaVerificationToken"] = fayda_token

        resp = api("POST", "/auth/register", json_body=body)

        if resp.status_code == 201:
            data = resp.json()
            user.user_id = data["userId"]
            user.access_token = data["tokens"]["accessToken"]
            user.refresh_token = data["tokens"].get("refreshToken", "")
            state.users[user.phone] = user
            state.record(f"Register {user.display_name}", True, f"userId={user.user_id[:12]}...")
        elif resp.status_code in (409, 500):
            # Already registered or Fayda conflict -- try logging in instead
            reason = "already exists" if resp.status_code == 409 else "register error, trying login"
            print(f"    {reason}...")
            login_resp = api("POST", "/auth/login", json_body={
                "phoneNumber": user.phone,
                "password": PASSWORD,
            })
            if login_resp.status_code == 200:
                data = login_resp.json()
                user.user_id = data["user"]["userId"]
                user.access_token = data["tokens"]["accessToken"]
                user.refresh_token = data["tokens"].get("refreshToken", "")
                state.users[user.phone] = user
                state.record(f"Login {user.display_name} (existing)", True, f"userId={user.user_id[:12]}...")
            else:
                # Try register without Fayda token as fallback
                if i == 0 and fayda_token and "faydaVerificationToken" in body:
                    print(f"    Login failed too, retrying register without Fayda token...")
                    body.pop("faydaVerificationToken", None)
                    resp2 = api("POST", "/auth/register", json_body=body)
                    if resp2.status_code == 201:
                        data = resp2.json()
                        user.user_id = data["userId"]
                        user.access_token = data["tokens"]["accessToken"]
                        user.refresh_token = data["tokens"].get("refreshToken", "")
                        state.users[user.phone] = user
                        state.record(f"Register {user.display_name} (no Fayda)", True, f"userId={user.user_id[:12]}...")
                        continue
                state.record(f"Login {user.display_name}", False, f"{login_resp.status_code}: {login_resp.body[:200]}")
        else:
            state.record(f"Register {user.display_name}", False, f"{resp.status_code}: {resp.body[:200]}")


# ---------------------------------------------------------------------------
# Phase 2: Create Crop Listings
# ---------------------------------------------------------------------------

def phase_create_crops(state: TestState):
    print_header("PHASE 2: Create Crop Listings (Farmers)")

    farmers = [u for u in TEST_USERS if u.role == "farmer" and u.access_token]
    if not farmers:
        state.record("Create crops", False, "No authenticated farmers")
        return

    created_any = False
    for listing in CROP_LISTINGS:
        farmer = TEST_USERS[listing["farmer_idx"]]
        if not farmer.access_token:
            state.record(f"Create {listing['cropType']}", False, "Farmer not authenticated")
            continue

        crop_id = uid()
        now = ts()
        print_step(f"Creating {listing['cropType']} listing for {farmer.display_name}")

        sync_body = {
            "syncBatchId": uid(),
            "clientId": uid(),
            "operations": [{
                "operationId": uid(),
                "idempotencyKey": f"{farmer.user_id}:crop_listing:{crop_id}:create:{now}",
                "entityType": "crop_listing",
                "entityId": crop_id,
                "action": "create",
                "data": {
                    "farmerId": farmer.user_id,
                    "cropType": listing["cropType"],
                    "quantity": listing["quantity"],
                    "pricePerKg": listing["pricePerKg"],
                    "description": listing["description"],
                    "woreda": farmer.woreda,
                    "status": "available",
                },
                "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            }]
        }

        resp = api("POST", "/sync", token=farmer.access_token, json_body=sync_body)

        if resp.status_code == 200:
            results = resp.json().get("results", [])
            if results and results[0].get("status") == "success":
                state.crop_ids.append(crop_id)
                state.crops.append(CropRecord(crop_id=crop_id, farmer_idx=listing["farmer_idx"], crop_type=listing["cropType"],
                                              price_per_kg=listing["pricePerKg"]))
                created_any = True
                state.record(
                    f"Create {listing['cropType']} ({farmer.display_name})",
                    True, f"cropId={crop_id[:12]}..."
                )
            else:
                msg = results[0].get("message", "unknown") if results else "no results"
                # Fraud duplicate is acceptable — we'll fetch existing crops below
                is_dup = "FRAUD_DUPLICATE" in msg
                state.record(f"Create {listing['cropType']} ({farmer.display_name})",
                             is_dup, f"(duplicate ok) {msg}" if is_dup else msg)
        else:
            state.record(f"Create {listing['cropType']}", False, f"{resp.status_code}: {resp.body[:200]}")

        time.sleep(1)  # Avoid rate limiting

    # If no new crops were created (all duplicates), fetch existing crops from the API
    if not state.crops:
        print_step("No new crops created — fetching existing crops from API")
        # Build userId -> farmer_idx map for matching
        farmer_id_to_idx = {}
        for idx, u in enumerate(TEST_USERS):
            if u.role == "farmer" and u.user_id:
                farmer_id_to_idx[u.user_id] = idx

        any_token = next((u.access_token for u in TEST_USERS if u.access_token), None)
        if any_token:
            resp = api("GET", "/crops", token=any_token)
            if resp.status_code == 200:
                data = resp.json()
                items = data.get("crops", data.get("items", []))
                for item in items:
                    cid = item.get("cropId", "")
                    ctype = item.get("cropType", "unknown")
                    fid = item.get("farmerId", "")
                    # Only include crops belonging to our authenticated test farmers
                    if cid and fid in farmer_id_to_idx:
                        fidx = farmer_id_to_idx[fid]
                        if cid not in state.crop_ids:
                            state.crop_ids.append(cid)
                            state.crops.append(CropRecord(crop_id=cid, farmer_idx=fidx, crop_type=ctype,
                                                          price_per_kg=float(item.get("pricePerKg") or 0)))
            time.sleep(0.2)

        if state.crops:
            state.record("Fetch existing crops", True, f"Found {len(state.crops)} crops for our test farmers")
        else:
            state.record("Fetch existing crops", False, "No crops found for our test farmers")


# ---------------------------------------------------------------------------
# Phase 3: Create Trip Listings
# ---------------------------------------------------------------------------

def phase_create_trips(state: TestState):
    print_header("PHASE 3: Create Trip Listings (Truckers)")

    for trip_def in TRIP_LISTINGS:
        trucker = TEST_USERS[trip_def["trucker_idx"]]
        if not trucker.access_token:
            state.record(f"Create trip {trip_def['fromWoreda']}->{trip_def['toWoreda']}", False, "Trucker not authenticated")
            continue

        trip_id = uid()
        now = ts()
        # Departure date: 7 days from now
        dep_date = time.strftime("%Y-%m-%dT06:00:00Z", time.gmtime(time.time() + 7 * 86400))
        route = f"{trip_def['fromWoreda']} -> {trip_def['toWoreda']}"
        print_step(f"Creating trip {route} for {trucker.display_name}")

        sync_body = {
            "syncBatchId": uid(),
            "clientId": uid(),
            "operations": [{
                "operationId": uid(),
                "idempotencyKey": f"{trucker.user_id}:trip:{trip_id}:create:{now}",
                "entityType": "trip",
                "entityId": trip_id,
                "action": "create",
                "data": {
                    "truckerId": trucker.user_id,
                    "truckerName": trucker.display_name,
                    "truckerPhone": trucker.phone,
                    "fromWoreda": trip_def["fromWoreda"],
                    "toWoreda": trip_def["toWoreda"],
                    "departureDate": dep_date,
                    "capacity": trip_def["capacity"],
                    "pricePerQuintal": trip_def["pricePerQuintal"],
                    "status": "open",
                },
                "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            }]
        }

        resp = api("POST", "/sync", token=trucker.access_token, json_body=sync_body)

        if resp.status_code == 200:
            results = resp.json().get("results", [])
            if results and results[0].get("status") == "success":
                state.trip_ids.append(trip_id)
                state.trips.append(TripRecord(trip_id=trip_id, trucker_idx=trip_def["trucker_idx"],
                                              from_woreda=trip_def["fromWoreda"], to_woreda=trip_def["toWoreda"],
                                              price_per_quintal=trip_def["pricePerQuintal"]))
                state.record(f"Create trip {route}", True, f"tripId={trip_id[:12]}...")
            else:
                msg = results[0].get("message", "unknown") if results else "no results"
                state.record(f"Create trip {route}", False, msg)
        else:
            state.record(f"Create trip {route}", False, f"{resp.status_code}: {resp.body[:200]}")

        time.sleep(1)


# ---------------------------------------------------------------------------
# Phase 4: Browse & Filter (verify data is visible)
# ---------------------------------------------------------------------------

def phase_browse(state: TestState):
    print_header("PHASE 4: Browse & Filter Listings")

    any_token = next((u.access_token for u in TEST_USERS if u.access_token), None)

    # Browse all crops
    print_step("Browse all crops")
    resp = api("GET", "/crops", token=any_token)
    if resp.status_code == 200:
        data = resp.json()
        crops = data.get("crops", data.get("items", []))
        count = len(crops) if isinstance(crops, list) else 0
        state.record("Browse crops", True, f"{count} crops returned")
    else:
        state.record("Browse crops", False, f"{resp.status_code}: {resp.body[:200]}")

    # Filter crops by type
    print_step("Filter crops by type=teff")
    resp = api("GET", "/crops", token=any_token, params={"cropType": "teff"})
    if resp.status_code == 200:
        data = resp.json()
        crops = data.get("crops", data.get("items", []))
        count = len(crops) if isinstance(crops, list) else 0
        state.record("Filter teff crops", True, f"{count} teff crops")
    else:
        state.record("Filter teff crops", False, f"{resp.status_code}: {resp.body[:200]}")

    # Browse all trips
    print_step("Browse all trips")
    resp = api("GET", "/trips", token=any_token)
    if resp.status_code == 200:
        data = resp.json()
        trips = data.get("trips", data.get("items", []))
        count = len(trips) if isinstance(trips, list) else 0
        state.record("Browse trips", True, f"{count} trips returned")
    else:
        state.record("Browse trips", False, f"{resp.status_code}: {resp.body[:200]}")

    # Filter trips by route
    print_step("Filter trips toWoreda=Addis Ababa")
    resp = api("GET", "/trips", token=any_token, params={"toWoreda": "Addis Ababa"})
    if resp.status_code == 200:
        data = resp.json()
        trips = data.get("trips", data.get("items", []))
        count = len(trips) if isinstance(trips, list) else 0
        state.record("Filter trips to Addis", True, f"{count} trips")
    else:
        state.record("Filter trips to Addis", False, f"{resp.status_code}: {resp.body[:200]}")


# ---------------------------------------------------------------------------
# Phase 5: Create Deliveries (link crop + trip + buyer)
# ---------------------------------------------------------------------------

def phase_create_deliveries(state: TestState):
    print_header("PHASE 5: Create Deliveries")

    if not state.crops or not state.trips:
        state.record("Create deliveries", False, "Need at least 1 crop and 1 trip")
        return

    # Dynamically build delivery scenarios from actually-created crops and trips.
    # Pick up to 3 deliveries, matching each crop with a trip and a buyer.
    buyers = [u for u in TEST_USERS if u.role == "buyer" and u.access_token]
    if not buyers:
        state.record("Create deliveries", False, "No authenticated buyers")
        return

    num_deliveries = min(len(state.crops), len(state.trips), 3)

    # Find indices of buyers in TEST_USERS for DeliveryRecord tracking
    buyer_indices = [i for i, u in enumerate(TEST_USERS) if u.role == "buyer" and u.access_token]

    for i in range(num_deliveries):
        crop = state.crops[i]
        trip = state.trips[i % len(state.trips)]
        farmer = TEST_USERS[crop.farmer_idx]
        trucker = TEST_USERS[trip.trucker_idx]
        buyer = buyers[i % len(buyers)]
        buyer_idx = buyer_indices[i % len(buyer_indices)]

        if not farmer.access_token or not trucker.access_token:
            state.record(f"Create delivery {i+1}", False, "Farmer or trucker not authenticated")
            continue

        delivery_id = uid()
        now = ts()
        crop_id = crop.crop_id
        trip_id = trip.trip_id

        print_step(f"Creating delivery {i+1}: {farmer.display_name} -> {trucker.display_name} -> {buyer.display_name}")

        # Deliveries require an orderId (delivery-verification-api.md v1.2.0):
        # the buyer creates the backing order first. truckerId/tripId are set so
        # the server's trucker-binding check matches the delivery creator.
        order_id = uid()
        order_body = {
            "syncBatchId": uid(),
            "clientId": uid(),
            "operations": [{
                "operationId": uid(),
                "idempotencyKey": f"{buyer.user_id}:order:{order_id}:create:{now}",
                "entityType": "order",
                "entityId": order_id,
                "action": "create",
                "data": {
                    "listingId": crop_id,
                    "buyerId": buyer.user_id,
                    "buyerName": buyer.display_name,
                    "farmerId": farmer.user_id,
                    "farmerName": farmer.display_name,
                    "cropType": crop.crop_type,
                    "quantity": 10.0,  # kilograms (order-lifecycle.md, Units)
                    "pricePerUnit": crop.price_per_kg,
                    "tripId": trip_id,
                    "truckerId": trucker.user_id,
                    "truckerName": trucker.display_name,
                    "transportCost": 10.0 / 100 * trip.price_per_quintal,
                    "paymentMethod": "telebirr",
                    "status": "created",
                },
                "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            }]
        }
        order_resp = api("POST", "/sync", token=buyer.access_token, json_body=order_body)
        order_ok = (order_resp.status_code == 200
                    and order_resp.json().get("results", [{}])[0].get("status") == "success")
        if not order_ok:
            state.record(f"Create delivery {i+1}", False,
                         f"backing order failed: {order_resp.body[:160]}")
            continue

        # A delivery requires an accepted order (delivery-verification-api.md 1.3.0):
        # the buyer pays (dev auto-funds the mocked payment), then the farmer accepts.
        def _order_version(u):
            r = api("GET", f"/orders/{order_id}", token=u.access_token)
            d = r.json() if r.status_code == 200 else {}
            return int((d.get("order") or d).get("version") or 1)

        def _sync_update_order(u, data):
            return api("POST", "/sync", token=u.access_token, json_body={
                "syncBatchId": uid(),
                "clientId": uid(),
                "operations": [{
                    "operationId": uid(),
                    "idempotencyKey": f"{u.user_id}:order:{order_id}:update:{ts()}",
                    "entityType": "order",
                    "entityId": order_id,
                    "action": "update",
                    "data": data,
                    "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                }],
            })

        pay_resp = _sync_update_order(buyer, {
            "status": "payment_submitted", "paymentMethod": "telebirr",
            "version": _order_version(buyer),
        })
        accept_resp = _sync_update_order(farmer, {
            "status": "accepted", "version": _order_version(farmer),
        })
        accepted = (accept_resp.status_code == 200
                    and accept_resp.json().get("results", [{}])[0].get("status") == "success")
        if not accepted:
            state.record(f"Create delivery {i+1}", False,
                         f"order not accepted: {accept_resp.body[:120]} (pay: {pay_resp.status_code})")
            continue

        sync_body = {
            "syncBatchId": uid(),
            "clientId": uid(),
            "operations": [{
                "operationId": uid(),
                "idempotencyKey": f"{trucker.user_id}:delivery:{delivery_id}:create:{now}",
                "entityType": "delivery",
                "entityId": delivery_id,
                "action": "create",
                "data": {
                    "tripId": trip_id,
                    "orderId": order_id,
                    "listingId": crop_id,
                    "farmerId": farmer.user_id,
                    "farmerName": farmer.display_name,
                    "farmerPhone": farmer.phone,
                    "truckerId": trucker.user_id,
                    "truckerName": trucker.display_name,
                    "truckerPhone": trucker.phone,
                    "buyerId": buyer.user_id,
                    "buyerName": buyer.display_name,
                    "buyerPhone": buyer.phone,
                    "qrToken": f"qr-e2e-{delivery_id[:8]}",
                    "status": "pending",
                },
                "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            }]
        }

        resp = api("POST", "/sync", token=trucker.access_token, json_body=sync_body)

        if resp.status_code == 200:
            results = resp.json().get("results", [])
            if results and results[0].get("status") == "success":
                # Extract server-generated qrNonce from response
                server_data = results[0].get("serverData", {}) or {}
                qr_nonce = server_data.get("qrNonce", "")
                state.delivery_ids.append(delivery_id)
                state.deliveries.append(DeliveryRecord(
                    delivery_id=delivery_id,
                    farmer_idx=crop.farmer_idx,
                    trucker_idx=trip.trucker_idx,
                    buyer_idx=buyer_idx,
                    crop_id=crop_id,
                    trip_id=trip_id,
                    qr_nonce=qr_nonce,
                    order_id=order_id,
                ))
                state.record(f"Create delivery {i+1}", True,
                             f"deliveryId={delivery_id[:12]}..., qrNonce={'yes' if qr_nonce else 'none'}")
            else:
                msg = results[0].get("message", "unknown") if results else "no results"
                state.record(f"Create delivery {i+1}", False, msg)
        else:
            state.record(f"Create delivery {i+1}", False, f"{resp.status_code}: {resp.body[:200]}")

        time.sleep(1)


# ---------------------------------------------------------------------------
# Phase 6: Confirm Pickup & Delivery
# ---------------------------------------------------------------------------

def phase_confirm_delivery(state: TestState):
    print_header("PHASE 6: Confirm Pickup & Delivery")

    if not state.deliveries:
        state.record("Confirm delivery", False, "No deliveries created")
        return

    for i, drec in enumerate(state.deliveries):
        delivery_id = drec.delivery_id
        trucker = TEST_USERS[drec.trucker_idx]
        buyer = TEST_USERS[drec.buyer_idx]
        qr_nonce = drec.qr_nonce
        # Last delivery: only pickup (test partial state) — but only if we have 2+
        do_delivery = (i < len(state.deliveries) - 1) or (len(state.deliveries) == 1)
        idx = i + 1

        # --- Pickup confirmation (trucker) ---
        print_step(f"Delivery {idx}: Trucker confirms pickup")
        now = ts()
        pickup_data = {
            "status": "pickedUp",
            "version": 1,
        }
        if qr_nonce:
            pickup_data["scannedQrNonce"] = qr_nonce
        sync_body = {
            "syncBatchId": uid(),
            "clientId": uid(),
            "operations": [{
                "operationId": uid(),
                "idempotencyKey": f"{trucker.user_id}:delivery:{delivery_id}:update:{now}",
                "entityType": "delivery",
                "entityId": delivery_id,
                "action": "update",
                "data": pickup_data,
                "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            }]
        }

        resp = api("POST", "/sync", token=trucker.access_token, json_body=sync_body)

        if resp.status_code == 200:
            results = resp.json().get("results", [])
            ok = results and results[0].get("status") == "success"
            msg = results[0].get("message", "") if results else "no results"
            state.record(f"Delivery {idx} pickup", ok, msg if not ok else "pickedUp")
        else:
            state.record(f"Delivery {idx} pickup", False, f"{resp.status_code}: {resp.body[:200]}")

        time.sleep(1)  # Avoid rate limiting

        if not do_delivery:
            print(f"    (Skipping delivery confirmation for delivery {idx} -- testing partial state)")
            continue

        # --- In-transit (trucker) ---
        now = ts()
        sync_body = {
            "syncBatchId": uid(),
            "clientId": uid(),
            "operations": [{
                "operationId": uid(),
                "idempotencyKey": f"{trucker.user_id}:delivery:{delivery_id}:update:{now}",
                "entityType": "delivery",
                "entityId": delivery_id,
                "action": "update",
                "data": {"status": "inTransit", "version": 2},
                "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            }]
        }

        resp = api("POST", "/sync", token=trucker.access_token, json_body=sync_body)
        if resp.status_code == 200:
            results = resp.json().get("results", [])
            ok = results and results[0].get("status") == "success"
            state.record(f"Delivery {idx} in-transit", ok,
                         "inTransit" if ok else (results[0].get("message", "") if results else ""))
        else:
            state.record(f"Delivery {idx} in-transit", False, f"{resp.status_code}: {resp.body[:200]}")

        time.sleep(1)

        # --- Delivery confirmation (buyer confirms receipt) ---
        print_step(f"Delivery {idx}: Buyer confirms delivery")
        now = ts()
        deliver_data = {
            "status": "delivered",
            "version": 3,
        }
        if qr_nonce:
            deliver_data["scannedQrNonce"] = qr_nonce
        sync_body = {
            "syncBatchId": uid(),
            "clientId": uid(),
            "operations": [{
                "operationId": uid(),
                "idempotencyKey": f"{buyer.user_id}:delivery:{delivery_id}:update:{now}",
                "entityType": "delivery",
                "entityId": delivery_id,
                "action": "update",
                "data": deliver_data,
                "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            }]
        }

        resp = api("POST", "/sync", token=buyer.access_token, json_body=sync_body)
        if resp.status_code == 200:
            results = resp.json().get("results", [])
            ok = results and results[0].get("status") == "success"
            state.record(f"Delivery {idx} delivered", ok,
                         "delivered" if ok else (results[0].get("message", "") if results else ""))
        else:
            state.record(f"Delivery {idx} delivered", False, f"{resp.status_code}: {resp.body[:200]}")

        time.sleep(1)


# ---------------------------------------------------------------------------
# Phase 7: Escrow Payments
# ---------------------------------------------------------------------------

def phase_escrow(state: TestState):
    print_header("PHASE 7: Escrow Payments (Buyers)")

    # Escrow on delivered deliveries (all except the last one if 2+, or all if only 1)
    if len(state.deliveries) == 1:
        delivered = list(state.deliveries)
    else:
        delivered = [d for i, d in enumerate(state.deliveries) if i < len(state.deliveries) - 1]
    if len(delivered) < 1:
        state.record("Escrow", False, "Need at least 1 fully delivered delivery")
        return

    amounts = [5000.00, 8000.00, 6000.00]
    first_buyer = None

    for i, drec in enumerate(delivered[:2]):  # Escrow on up to 2 deliveries
        buyer = TEST_USERS[drec.buyer_idx]
        farmer = TEST_USERS[drec.farmer_idx]
        if not buyer.access_token:
            state.record(f"Escrow (delivery {i+1})", False, "Buyer not authenticated")
            continue
        if first_buyer is None:
            first_buyer = buyer

        delivery_id = drec.delivery_id
        # The server binds the escrow amount to the backing order's total
        # (ESCROW_028); the fixed amounts only remain as a fallback.
        amount = amounts[i % len(amounts)]
        if drec.order_id:
            oresp = api("GET", f"/orders/{drec.order_id}", token=buyer.access_token)
            if oresp.status_code == 200:
                odata = oresp.json()
                odata = odata.get("order", odata)
                amount = float(odata.get("orderTotal") or amount)
        now = ts()

        print_step(f"Initiating escrow for delivery {i+1} ({amount} ETB)")
        resp = api("POST", "/escrow/initiate", token=buyer.access_token, json_body={
            "deliveryId": delivery_id,
            "buyerId": buyer.user_id,
            "farmerId": farmer.user_id,
            "amount": amount,
            "currency": "ETB",
            "idempotencyKey": f"{buyer.user_id}:{delivery_id}:initiate:{now}",
        })

        if resp.status_code in (200, 201):
            data = resp.json()
            escrow_id = data.get("escrowId", "")
            state.escrow_ids.append(escrow_id)
            state.record(f"Escrow initiate (delivery {i+1})", True,
                         f"escrowId={escrow_id[:12]}..., status={data.get('status', '?')}, commission={data.get('commission', '?')}")
        else:
            state.record(f"Escrow initiate (delivery {i+1})", False, f"{resp.status_code}: {resp.body[:200]}")

        time.sleep(1)

    # Check escrow status
    if state.escrow_ids and first_buyer:
        print_step("Checking escrow status")
        resp = api("GET", f"/escrow/{state.escrow_ids[0]}", token=first_buyer.access_token)
        if resp.status_code == 200:
            data = resp.json()
            state.record("Get escrow status", True,
                         f"status={data.get('status')}, amount={data.get('amount')}, farmerPayout={data.get('farmerPayout')}")
        else:
            state.record("Get escrow status", False, f"{resp.status_code}: {resp.body[:200]}")

    # Check payment history
    if first_buyer:
        print_step("Checking buyer payment history")
        resp = api("GET", "/escrow/history", token=first_buyer.access_token)
        if resp.status_code == 200:
            data = resp.json()
            escrows = data.get("escrows", [])
            state.record("Payment history", True, f"{len(escrows)} escrow(s) in history")
        else:
            state.record("Payment history", False, f"{resp.status_code}: {resp.body[:200]}")


# ---------------------------------------------------------------------------
# Phase 8: Dispute Flow
# ---------------------------------------------------------------------------

def phase_dispute(state: TestState):
    print_header("PHASE 8: Dispute Flow")

    if not state.escrow_ids:
        state.record("Dispute", False, "Need at least 1 escrow")
        return

    # Dispute on the last escrow
    escrow_idx = len(state.escrow_ids) - 1
    if len(state.deliveries) == 1:
        delivered = list(state.deliveries)
    else:
        delivered = [d for i, d in enumerate(state.deliveries) if i < len(state.deliveries) - 1]

    if escrow_idx >= len(delivered):
        escrow_idx = 0

    buyer = TEST_USERS[delivered[escrow_idx].buyer_idx]
    escrow_id = state.escrow_ids[escrow_idx]
    now = ts()

    print_step(f"Buyer raising dispute on escrow {escrow_id[:12]}...")
    resp = api("POST", "/escrow/dispute", token=buyer.access_token, json_body={
        "escrowId": escrow_id,
        "disputedBy": buyer.user_id,
        "reason": "damaged_goods",
        "description": "Coffee beans arrived wet and moldy. 30% of bags affected.",
        "idempotencyKey": f"{buyer.user_id}:{escrow_id}:dispute:{now}",
    })

    if resp.status_code == 200:
        data = resp.json()
        state.record("Raise dispute", True,
                     f"disputeId={data.get('disputeId', '?')}, status={data.get('status')}")
    else:
        state.record("Raise dispute", False, f"{resp.status_code}: {resp.body[:200]}")

    time.sleep(0.5)

    # Verify escrow now shows disputed status
    print_step("Verifying escrow shows disputed status")
    resp = api("GET", f"/escrow/{escrow_id}", token=buyer.access_token)
    if resp.status_code == 200:
        data = resp.json()
        is_disputed = data.get("status") == "disputed"
        has_dispute = data.get("dispute") is not None
        dispute_reason = data.get("dispute", {}).get("reason", "N/A") if data.get("dispute") else "N/A"
        state.record("Escrow disputed status", is_disputed and has_dispute,
                     f"status={data.get('status')}, reason={dispute_reason}")
    else:
        state.record("Escrow disputed status", False, f"{resp.status_code}")


# ---------------------------------------------------------------------------
# Phase 9: Browse Deliveries & Final Verification
# ---------------------------------------------------------------------------

def phase_browse_deliveries(state: TestState):
    print_header("PHASE 9: Browse Deliveries & Final Verification")

    # Check deliveries for a trucker (pick first trucker with a token)
    trucker = next((u for u in TEST_USERS if u.role == "trucker" and u.access_token), None)
    if trucker:
        print_step(f"Trucker ({trucker.display_name}) browsing their deliveries")
        resp = api("GET", "/deliveries", token=trucker.access_token)
        if resp.status_code == 200:
            data = resp.json()
            deliveries = data.get("deliveries", data.get("items", []))
            count = len(deliveries) if isinstance(deliveries, list) else 0
            state.record("Trucker deliveries", True, f"{count} deliveries")
        else:
            state.record("Trucker deliveries", False, f"{resp.status_code}: {resp.body[:200]}")

    # Check deliveries for a farmer (pick first farmer with a token)
    farmer = next((u for u in TEST_USERS if u.role == "farmer" and u.access_token), None)
    if farmer:
        print_step(f"Farmer ({farmer.display_name}) browsing their deliveries")
        resp = api("GET", "/deliveries", token=farmer.access_token)
        if resp.status_code == 200:
            data = resp.json()
            deliveries = data.get("deliveries", data.get("items", []))
            count = len(deliveries) if isinstance(deliveries, list) else 0
            state.record("Farmer deliveries", True, f"{count} deliveries")
        else:
            state.record("Farmer deliveries", False, f"{resp.status_code}: {resp.body[:200]}")

    # Get a specific crop by ID
    if state.crop_ids:
        print_step("Get specific crop by ID")
        any_token = next((u.access_token for u in TEST_USERS if u.access_token), None)
        resp = api("GET", f"/crops/{state.crop_ids[0]}", token=any_token)
        if resp.status_code == 200:
            data = resp.json()
            crop = data.get("crop", data)
            state.record("Get crop by ID", True,
                         f"cropType={crop.get('cropType', '?')}, status={crop.get('status', '?')}")
        else:
            state.record("Get crop by ID", False, f"{resp.status_code}: {resp.body[:200]}")

    # Get a specific trip by ID
    if state.trip_ids:
        print_step("Get specific trip by ID")
        any_token = next((u.access_token for u in TEST_USERS if u.access_token), None)
        resp = api("GET", f"/trips/{state.trip_ids[0]}", token=any_token)
        if resp.status_code == 200:
            data = resp.json()
            trip = data.get("trip", data)
            state.record("Get trip by ID", True,
                         f"route={trip.get('fromWoreda', '?')}->{trip.get('toWoreda', '?')}, status={trip.get('status', '?')}")
        else:
            state.record("Get trip by ID", False, f"{resp.status_code}: {resp.body[:200]}")


# ---------------------------------------------------------------------------
# Phase 10: Token Refresh
# ---------------------------------------------------------------------------

def phase_token_refresh(state: TestState):
    print_header("PHASE 10: Token Refresh")

    user = next((u for u in TEST_USERS if u.refresh_token), None)
    if not user or not user.refresh_token:
        state.record("Token refresh", False, "No refresh token available")
        return

    print_step("Refreshing access token")
    resp = api("POST", "/auth/refresh", json_body={
        "refreshToken": user.refresh_token,
    })
    if resp.status_code == 200:
        data = resp.json()
        new_token = data.get("tokens", {}).get("accessToken", "")
        state.record("Token refresh", bool(new_token), "New access token received")
    else:
        state.record("Token refresh", False, f"{resp.status_code}: {resp.body[:200]}")


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    global _BASE_URL

    parser = argparse.ArgumentParser(description="WeredaConnect E2E Test Script")
    parser.add_argument("--base-url", default=_BASE_URL,
                        help=f"API base URL (default: {_BASE_URL})")
    args = parser.parse_args()

    _BASE_URL = args.base_url.rstrip("/")

    print(f"\nWeredaConnect E2E Test Suite")
    print(f"API: {_BASE_URL}")
    print(f"Time: {time.strftime('%Y-%m-%d %H:%M:%S UTC', time.gmtime())}")

    state = TestState()

    # Quick connectivity check
    print_step("Checking API health")
    try:
        resp = api("GET", "/health")
        if resp.status_code == 200:
            state.record("API health", True, resp.body[:100])
        else:
            state.record("API health", False, f"{resp.status_code}")
    except URLError as e:
        print(f"\n  FATAL: Cannot connect to {_BASE_URL}")
        print(f"  Error: {e}")
        sys.exit(1)

    # Run all phases
    phase_register(state)
    phase_create_crops(state)
    phase_create_trips(state)
    phase_browse(state)
    phase_create_deliveries(state)
    phase_confirm_delivery(state)
    phase_escrow(state)
    phase_dispute(state)
    phase_browse_deliveries(state)
    phase_token_refresh(state)

    # Summary
    print_header("SUMMARY")
    total = state.passed + state.failed
    print(f"  Total:  {total}")
    print(f"  Passed: {state.passed}")
    print(f"  Failed: {state.failed}")
    print(f"\n  Created: {len(state.crop_ids)} crops, {len(state.trip_ids)} trips, "
          f"{len(state.delivery_ids)} deliveries, {len(state.escrow_ids)} escrows")

    if state.failed > 0:
        print(f"\n  Some tests failed -- review output above for details.")
        sys.exit(1)
    else:
        print(f"\n  All tests passed!")
        sys.exit(0)


if __name__ == "__main__":
    main()
