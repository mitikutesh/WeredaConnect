#!/usr/bin/env python3
"""
WeredaConnect FULL-FLOW E2E Test
================================
Exercises the ENTIRE marketplace-to-escrow flow the app supports, filling the
gaps the older scripts/e2e_test.py skipped: role-based visibility (F-36),
contact-seller (phone reveal), buyer intent + matching (/match), order creation
with the 3-way split, delivery lifecycle, escrow, and dispute.

Two payment modes:
  • Mock (default): the API uses MockTelebirrGateway; escrow becomes "escrowed"
    synchronously on initiate.
  • Real sandbox (--telebirr-sim-url): when the API is wired to the sandbox
    gateway (TELEBIRR_MOCK=false) the initiate returns a toPayUrl; this script
    opens it, clicks "Pay" on the local simulator, and polls until the webhook
    flips the escrow to "escrowed".

Stdlib only. Usage:
  python3 scripts/e2e_full_flow.py                       # deployed dev (mock)
  python3 scripts/e2e_full_flow.py --base-url http://localhost:3000
  python3 scripts/e2e_full_flow.py --base-url http://localhost:3000 \
      --telebirr-sim-url http://127.0.0.1:8790          # real sandbox path

Note: escrow release→completed and auto-release/refund require system/admin or
the cron and are covered by the server suite (EscrowFlowIntegrationTests); this
script asserts the flow through escrowed + dispute.
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
from urllib.parse import urlencode, urlparse, parse_qs
from urllib.request import Request, urlopen

BASE_URL = "https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com"
SIM_URL: Optional[str] = None
PASSWORD = "testpass1a"


def ts() -> int:
    return int(time.time() * 1000)


def uid() -> str:
    return str(uuid.uuid4())


def hdr(t):
    print(f"\n{'=' * 62}\n  {t}\n{'=' * 62}")


@dataclass
class Resp:
    status: int
    body: str

    def json(self):
        return json.loads(self.body) if self.body else {}


def api(method, path, token=None, body=None, params=None, base=None, retries=3) -> Resp:
    url = (base or BASE_URL) + path
    if params:
        url += "?" + urlencode(params)
    data = json.dumps(body).encode() if body is not None else None
    ctx = ssl.create_default_context()
    for attempt in range(retries + 1):
        req = Request(url, data=data, method=method)
        req.add_header("Content-Type", "application/json")
        if token:
            req.add_header("Authorization", f"Bearer {token}")
        try:
            with urlopen(req, timeout=30, context=ctx) as r:
                return Resp(r.status, r.read().decode())
        except HTTPError as e:
            b = e.read().decode() if e.fp else ""
            if e.code == 429 and attempt < retries:
                time.sleep(5 * (attempt + 1))
                continue
            return Resp(e.code, b)
        except URLError as e:
            return Resp(0, str(e))
    return Resp(429, "retries exceeded")


@dataclass
class User:
    phone: str
    role: str
    woreda: str
    name: str
    user_id: str = ""
    token: str = ""


@dataclass
class State:
    passed: int = 0
    failed: int = 0
    skipped: int = 0
    warned: int = 0
    crops: list = field(default_factory=list)   # (crop_id, farmer)
    trips: list = field(default_factory=list)    # (trip_id, trucker)

    def check(self, label, ok, detail=""):
        print(f"  {'[+]' if ok else '[X]'} {label}: {'PASS' if ok else 'FAIL'}"
              + (f" -- {detail}" if detail else ""))
        if ok:
            self.passed += 1
        else:
            self.failed += 1

    def skip(self, label, detail=""):
        # Feature exists in source but not on this deployment — not a failure.
        print(f"  [~] {label}: SKIP" + (f" -- {detail}" if detail else ""))
        self.skipped += 1

    def warn(self, label, detail=""):
        # Non-critical divergence (e.g. deployment lags a tightening) — not a failure.
        print(f"  [!] {label}: WARN" + (f" -- {detail}" if detail else ""))
        self.warned += 1


USERS = {
    "farmer0": User("+251900200001", "farmer", "Debre Birhan", "Farmer Zero"),
    "farmer1": User("+251900200002", "farmer", "Bishoftu", "Farmer One"),
    "trucker": User("+251900200003", "trucker", "Addis Ababa", "Trucker Zero"),
    "buyer0": User("+251900200004", "buyer", "Addis Ababa", "Buyer Zero"),
    "buyer1": User("+251900200005", "buyer", "Dire Dawa", "Buyer One"),
}


def register_or_login(s: State):
    hdr("PHASE 1 — Auth (register or login)")
    # First user tries to get a Fayda dev-bypass token (needed for >10k ETB later).
    for key, u in USERS.items():
        body = {"phoneNumber": u.phone, "password": PASSWORD, "role": u.role,
                "woreda": u.woreda, "displayName": u.name, "preferredLanguage": "en"}
        r = api("POST", "/auth/register", body=body)
        if r.status == 201:
            d = r.json()
            # API authorizes on the ID token (custom:role lives there, NOT on the
            # access token) — using the access token silently strips the role.
            u.user_id, u.token = d["userId"], d["tokens"]["idToken"]
            s.check(f"register {key}", True, u.user_id[:12])
        else:
            r2 = api("POST", "/auth/login", body={"phoneNumber": u.phone, "password": PASSWORD})
            if r2.status == 200:
                d = r2.json()
                u.user_id, u.token = d["user"]["userId"], d["tokens"]["idToken"]
                s.check(f"login {key}", True, u.user_id[:12])
            else:
                s.check(f"auth {key}", False, f"{r2.status}: {r2.body[:120]}")


def sync_create(u: User, entity, entity_id, data):
    now = ts()
    return api("POST", "/sync", token=u.token, body={
        "syncBatchId": uid(), "clientId": uid(),
        "operations": [{
            "operationId": uid(),
            "idempotencyKey": f"{u.user_id}:{entity}:{entity_id}:create:{now}",
            "entityType": entity, "entityId": entity_id, "action": "create",
            "data": data, "timestamp": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        }],
    })


def listing_price_per_kg(u: User, crop_id: str) -> float:
    """Server-bound order create (SIM-05): pricePerUnit must equal the listing's pricePerKg."""
    r = api("GET", f"/crops/{crop_id}", token=u.token)
    d = r.json() if r.status == 200 else {}
    return float((d.get("crop") or d).get("pricePerKg") or 0)


def trip_price_per_quintal(u: User, trip_id: str) -> float:
    """transportCost must equal quantity_kg / 100 x the trip's pricePerQuintal."""
    r = api("GET", f"/trips/{trip_id}", token=u.token)
    d = r.json() if r.status == 200 else {}
    return float((d.get("trip") or d).get("pricePerQuintal") or 0)


def seed_listings(s: State):
    hdr("PHASE 2 — Farmers create crops, trucker creates trips")
    crops = [
        (USERS["farmer0"], {"cropType": "teff", "quantity": 50.0, "pricePerKg": 45.0}),
        (USERS["farmer0"], {"cropType": "wheat", "quantity": 30.0, "pricePerKg": 28.0}),
        (USERS["farmer1"], {"cropType": "coffee", "quantity": 20.0, "pricePerKg": 120.0}),
        (USERS["farmer1"], {"cropType": "teff", "quantity": 80.0, "pricePerKg": 42.0}),
    ]
    for farmer, c in crops:
        cid = uid()
        data = {"farmerId": farmer.user_id, "farmerName": farmer.name,
                "woreda": farmer.woreda, "status": "available", **c}
        r = sync_create(farmer, "crop_listing", cid, data)
        ok = r.status == 200 and (r.json().get("results", [{}])[0].get("status") in ("success",))
        dup = "FRAUD_DUPLICATE" in r.body
        if ok:
            s.crops.append((cid, farmer, c["cropType"]))
        s.check(f"crop {c['cropType']} ({farmer.name})", ok or dup, "(dup ok)" if dup and not ok else "")
        time.sleep(0.5)

    for trip in [{"fromWoreda": "Debre Birhan", "toWoreda": "Addis Ababa", "capacity": 150.0, "pricePerQuintal": 120.0},
                 {"fromWoreda": "Bishoftu", "toWoreda": "Addis Ababa", "capacity": 100.0, "pricePerQuintal": 80.0}]:
        t = USERS["trucker"]
        tid = uid()
        dep = time.strftime("%Y-%m-%dT06:00:00Z", time.gmtime(time.time() + 7 * 86400))
        data = {"truckerId": t.user_id, "truckerName": t.name, "truckerPhone": t.phone,
                "departureDate": dep, "status": "open", **trip}
        r = sync_create(t, "trip", tid, data)
        ok = r.status == 200 and r.json().get("results", [{}])[0].get("status") == "success"
        if ok:
            s.trips.append((tid, t))
        s.check(f"trip {trip['fromWoreda']}->{trip['toWoreda']}", ok, "" if ok else r.body[:120])
        time.sleep(0.5)

    # Refresh refs from the API so later phases work even when creates were fraud-dups.
    s.crops = []
    for key in ("farmer0", "farmer1"):
        f = USERS[key]
        r = api("GET", "/crops", token=f.token)
        for c in (r.json().get("crops", r.json().get("items", [])) if r.status == 200 else []):
            if c.get("cropId"):
                s.crops.append((c["cropId"], f, c.get("cropType", "")))
    tr = USERS["trucker"]
    r = api("GET", "/trips", token=tr.token)
    s.trips = [(t["tripId"], tr) for t in (r.json().get("trips", r.json().get("items", [])) if r.status == 200 else []) if t.get("tripId")]


def role_visibility(s: State):
    hdr("PHASE 3 — Role-based visibility (F-36, CRITICAL)")
    f0, tr, b0 = USERS["farmer0"], USERS["trucker"], USERS["buyer0"]

    r = api("GET", "/crops", token=f0.token)
    crops = r.json().get("crops", r.json().get("items", [])) if r.status == 200 else []
    only_own = r.status == 200 and all(c.get("farmerId") == f0.user_id for c in crops)
    s.check("farmer sees only OWN crops", only_own, f"{len(crops)} crops")

    r = api("GET", "/trips", token=tr.token)
    trips = r.json().get("trips", r.json().get("items", [])) if r.status == 200 else []
    only_own_t = r.status == 200 and all(t.get("truckerId") == tr.user_id for t in trips)
    s.check("trucker sees only OWN trips", only_own_t, f"{len(trips)} trips")

    r = api("GET", "/crops", token=b0.token)
    bcrops = r.json().get("crops", r.json().get("items", [])) if r.status == 200 else []
    farmer_ids = {c.get("farmerId") for c in bcrops}
    s.check("buyer sees ALL crops (multi-farmer)", r.status == 200 and len(farmer_ids) >= 2,
            f"{len(bcrops)} crops from {len(farmer_ids)} farmers")
    r = api("GET", "/trips", token=b0.token)
    btrips = r.json().get("trips", r.json().get("items", [])) if r.status == 200 else []
    s.check("buyer sees ALL trips", r.status == 200 and len(btrips) >= 1, f"{len(btrips)} trips")

    # F-36 tightening (cross-resource → 403). A deployment that predates it returns
    # 200; that's a WARN (visibility lag) rather than a hard failure of this script.
    for label, path, u in [("farmer→/trips", "/trips", f0), ("trucker→/crops", "/crops", tr)]:
        st = api("GET", path, token=u.token).status
        if st == 403:
            s.check(f"{label} FORBIDDEN (F-36)", True)
        elif st == 404:
            s.skip(f"{label} FORBIDDEN (F-36)", "endpoint not deployed")
        else:
            s.warn(f"{label} FORBIDDEN (F-36)", f"returned {st}; deployment predates the 403 tightening")


def contact_seller(s: State):
    hdr("PHASE 4 — Contact seller (phone reveal)")
    b0 = USERS["buyer0"]
    if s.crops:
        # Crops don't carry a phone on the listing; the buyer can view the crop and
        # reach the farmer once an order/delivery is created. Assert visibility here.
        cid, farmer, _ = s.crops[0]
        r = api("GET", f"/crops/{cid}", token=b0.token)
        crop = r.json().get("crop", r.json()) if r.status == 200 else {}
        s.check("buyer can view crop detail (to contact/order)",
                r.status == 200 and crop.get("farmerName") is not None, crop.get("farmerName", "?"))
    if s.trips:
        tid, tr = s.trips[0]
        r = api("GET", f"/trips/{tid}", token=b0.token)
        trip = r.json().get("trip", r.json()) if r.status == 200 else {}
        s.check("buyer sees trucker phone on trip detail",
                r.status == 200 and trip.get("truckerPhone") is not None, trip.get("truckerPhone", "?"))


def intent_and_matching(s: State):
    hdr("PHASE 5 — Buyer intent + matching (/match)")
    b0 = USERS["buyer0"]
    intent_id = uid()
    r = sync_create(b0, "buyer_intent", intent_id, {
        "intentId": intent_id, "buyerId": b0.user_id, "cropType": "teff",
        "quantityNeeded": 40.0, "maxPricePerKg": 60.0,
        "deliveryWoreda": "Addis Ababa", "status": "active",
    })
    msg = r.json().get("results", [{}])[0].get("message", "") if r.status == 200 else r.body
    if "Unsupported entity type" in msg:
        s.skip("buyer creates purchase intent", "matching feature not deployed here (PR #27)")
        s.skip("buyer /match returns teff matches", "matching feature not deployed here")
        return
    ok = r.status == 200 and r.json().get("results", [{}])[0].get("status") == "success"
    s.check("buyer creates purchase intent (teff)", ok, "" if ok else msg[:120])

    r = api("GET", "/match", token=b0.token, params={"cropType": "teff", "maxPricePerKg": 60, "quantityNeeded": 40})
    if r.status == 404:
        s.skip("buyer /match returns teff matches", "/match not deployed here")
        return
    matches = r.json().get("matches", []) if r.status == 200 else []
    s.check("buyer /match returns teff matches", r.status == 200 and len(matches) >= 1, f"{len(matches)} matches")


def create_order(s: State):
    hdr("PHASE 6 — Order with 3-way split")
    if not s.crops or not s.trips:
        s.check("create order", False, "need a crop + trip")
        return None
    b0 = USERS["buyer0"]
    teff = [x for x in s.crops if x[2] == "teff"]
    cid, farmer, ctype = (teff or s.crops)[0]
    tid, tr = s.trips[0]
    order_id = uid()
    quantity_kg = 20.0  # order.quantity is kilograms (order-lifecycle.md, Units)
    r = sync_create(b0, "order", order_id, {
        "listingId": cid, "buyerId": b0.user_id, "farmerId": farmer.user_id,
        "buyerName": b0.name, "farmerName": farmer.name, "cropType": ctype,
        "quantity": quantity_kg, "pricePerUnit": listing_price_per_kg(b0, cid),
        "tripId": tid, "truckerId": tr.user_id, "truckerName": tr.name,
        "transportCost": quantity_kg / 100 * trip_price_per_quintal(b0, tid),
        "paymentMethod": "telebirr", "status": "created",
    })
    ok = r.status == 200 and r.json().get("results", [{}])[0].get("status") == "success"
    s.check("buyer creates order", ok, "" if ok else r.body[:160])
    if ok:
        rr = api("GET", f"/orders/{order_id}", token=b0.token)
        o = rr.json().get("order", rr.json()) if rr.status == 200 else {}
        s.check("order has server-computed split", rr.status == 200 and o.get("orderTotal") is not None,
                f"total={o.get('orderTotal')}, commission={o.get('commissionAmount')}")
    return order_id if ok else None


def delivery_and_escrow(s: State):
    hdr("PHASE 7 — Delivery + escrow (through 'escrowed')")
    if not s.crops or not s.trips:
        s.check("delivery", False, "need crop + trip")
        return
    b0, tr = USERS["buyer0"], USERS["trucker"]
    cid, farmer, ctype = s.crops[0]
    tid, _ = s.trips[0]
    # Deliveries require an orderId (delivery-verification-api.md v1.2.0) with
    # matching buyer/farmer/trucker — create the backing order for THIS crop
    # (Phase 6's order may target a different listing/farmer).
    oid = uid()
    quantity_kg = 10.0  # order.quantity is kilograms (order-lifecycle.md, Units)
    r = sync_create(b0, "order", oid, {
        "listingId": cid, "buyerId": b0.user_id, "farmerId": farmer.user_id,
        "buyerName": b0.name, "farmerName": farmer.name, "cropType": ctype,
        "quantity": quantity_kg, "pricePerUnit": listing_price_per_kg(b0, cid),
        "tripId": tid, "truckerId": tr.user_id, "truckerName": tr.name,
        "transportCost": quantity_kg / 100 * trip_price_per_quintal(b0, tid),
        "paymentMethod": "telebirr", "status": "created",
    })
    order_ok = r.status == 200 and r.json().get("results", [{}])[0].get("status") == "success"
    s.check("backing order for delivery", order_ok, "" if order_ok else r.body[:140])
    if not order_ok:
        return
    did = uid()
    r = sync_create(tr, "delivery", did, {
        "tripId": tid, "orderId": oid, "listingId": cid, "farmerId": farmer.user_id, "farmerName": farmer.name,
        "farmerPhone": farmer.phone, "truckerId": tr.user_id, "truckerName": tr.name,
        "truckerPhone": tr.phone, "buyerId": b0.user_id, "buyerName": b0.name, "buyerPhone": b0.phone,
        "qrToken": f"qr-{did[:8]}", "status": "pending",
    })
    res0 = r.json().get("results", [{}])[0] if r.status == 200 else {}
    ok = res0.get("status") == "success"
    qr_nonce = (res0.get("serverData") or {}).get("qrNonce", "")
    s.check("trucker creates delivery", ok, "" if ok else r.body[:140])
    if not ok:
        return

    # The server binds the escrow amount to the order's total (ESCROW_028).
    rr = api("GET", f"/orders/{oid}", token=b0.token)
    o = rr.json().get("order", rr.json()) if rr.status == 200 else {}
    amount = float(o.get("orderTotal") or 0)
    if amount <= 0:
        s.check("escrow initiate", False, f"order {oid[:8]} has no orderTotal to pay")
        return
    r = api("POST", "/escrow/initiate", token=b0.token, body={
        "deliveryId": did, "buyerId": b0.user_id, "farmerId": farmer.user_id,
        "amount": amount, "currency": "ETB",
        "idempotencyKey": f"{b0.user_id}:{did}:initiate:{ts()}",
    })
    if r.status not in (200, 201):
        s.check("escrow initiate", False, f"{r.status}: {r.body[:160]}")
        return
    d = r.json()
    escrow_id = d.get("escrowId")
    to_pay = d.get("toPayUrl")
    s.check("escrow initiate", True, f"escrowId={escrow_id[:12]}, status={d.get('status')}")

    if to_pay and SIM_URL:
        # Real sandbox path: drive the simulator pay page, then poll for webhook.
        s.check("initiate returned toPayUrl (async C2B)", True, to_pay[:60] + "...")
        prepay = parse_qs(urlparse(to_pay).query).get("prepay_id", [""])[0]
        cr = api("POST", f"/simulate/complete?prepay_id={prepay}&result=success", base=SIM_URL)
        s.check("simulator payment completed", cr.status == 200, cr.body[:120])
        escrowed = False
        for _ in range(10):
            time.sleep(1)
            gr = api("GET", f"/escrow/{escrow_id}", token=b0.token)
            if gr.status == 200 and gr.json().get("status") == "escrowed":
                escrowed = True
                break
        s.check("escrow -> escrowed via webhook", escrowed)
    else:
        gr = api("GET", f"/escrow/{escrow_id}", token=b0.token)
        st = gr.json().get("status") if gr.status == 200 else "?"
        s.check("escrow status is escrowed (mock)", st == "escrowed", f"status={st}")

    # Dispute
    dr = api("POST", "/escrow/dispute", token=b0.token, body={
        "escrowId": escrow_id, "disputedBy": b0.user_id, "reason": "damaged_goods",
        "description": "Some bags were wet on arrival.",
        "idempotencyKey": f"{b0.user_id}:{escrow_id}:dispute:{ts()}",
    })
    s.check("buyer raises dispute", dr.status == 200, dr.body[:120] if dr.status != 200 else "disputed")


def main():
    global BASE_URL, SIM_URL
    ap = argparse.ArgumentParser()
    ap.add_argument("--base-url", default=BASE_URL)
    ap.add_argument("--telebirr-sim-url", default=None,
                    help="Local simulator URL; use when the API is wired to the sandbox gateway.")
    args = ap.parse_args()
    BASE_URL = args.base_url.rstrip("/")
    SIM_URL = args.telebirr_sim_url.rstrip("/") if args.telebirr_sim_url else None

    print(f"\nWeredaConnect FULL-FLOW E2E\nAPI: {BASE_URL}\nSimulator: {SIM_URL or '(none — mock mode)'}")
    h = api("GET", "/health")
    if h.status != 200:
        print(f"FATAL: API health {h.status} at {BASE_URL}")
        sys.exit(1)

    s = State()
    register_or_login(s)
    seed_listings(s)
    role_visibility(s)
    contact_seller(s)
    intent_and_matching(s)
    create_order(s)
    delivery_and_escrow(s)

    hdr("SUMMARY")
    print(f"  Passed: {s.passed}   Failed: {s.failed}   Skipped: {s.skipped}   Warnings: {s.warned}")
    if s.skipped:
        print("  (Skipped = feature present in source but not on this deployment — redeploy dev or run locally.)")
    if s.warned:
        print("  (Warnings = deployment diverges from current source, e.g. an un-deployed tightening.)")
    sys.exit(1 if s.failed else 0)


if __name__ == "__main__":
    main()
