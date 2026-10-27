#!/usr/bin/env python3
"""
WeredaConnect three-party acceptance (ESC-15, ESC-21)
=====================================================
Walks the sequential commitments on a deployed API with the three dev test users
(farmer, trucker, buyer; CLAUDE.md) and checks after every step what the server
holds and what each party's inbox (GET /notifications) says:

  A. happy path: pay -> the farmer is asked -> the trucker cannot answer first ->
     the farmer accepts -> the trucker is asked and accepts -> accepted, the trip's
     room goes down -> the trucker creates the delivery;
  B. the trucker declines -> awaiting_transport -> the buyer cancels for a refund;
  C. the buyer cannot cancel a funded order while the parties decide;
  D. self-pickup: the farmer's accept agrees the order at once;
  E. the farmer declines -> rejected;
  F. a trip without room: the second payment is refused;
  G. the trucker cancels the trip: a funded order loses its trucker, an accepted
     one waits for the buyer (trip_cancelled);
  H. the farmer's deadline is stamped; with --cron-function and AWS credentials the
     commitment_deadlines cron action is invoked by hand (a deadline in the past
     cannot be set from outside).

A server with COMMITMENTS_ENABLED off (no `fulfillment` stamp at funding) runs
only the legacy checks and skips the rest. A server without GET /notifications
(the ESC-21 pipeline not deployed) skips the inbox checks. Delivery pickup and
drop-off need the device QR keys and are covered by scripts/e2e_full_flow.py
and the server suite.

Stdlib only. Every /sync call sends X-App-Build: 3 (the first build that knows
the commitments). Usage:
  python3 scripts/e2e_three_party.py                         # deployed dev
  python3 scripts/e2e_three_party.py --only A,C              # some scenarios
  python3 scripts/e2e_three_party.py --cron-function wereda-connect-dev-order-cron
Exit code 1 when a check fails.
"""
import argparse
import json
import shutil
import ssl
import subprocess
import sys
import time
import uuid
from dataclasses import dataclass, field
from typing import Optional
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

BASE_URL = "https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com"
PASSWORD = "TestPass1dev"
APP_BUILD = "3"
PHONES = {"farmer": "+251900100001", "trucker": "+251900100002", "buyer": "+251900100003"}

# Kilograms per order: well under the 10,000 ETB payment step-up (AUTH_041).
ORDER_KG = 100.0
KG_PER_QUINTAL = 100.0
# Listing stock a run wants on hand (quintals; orders hold up to about 4 at once).
MIN_STOCK_QUINTALS = 20.0
INBOX_WAIT_SECONDS = 60


def uid() -> str:
    return str(uuid.uuid4())


def now_iso() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def hdr(title: str):
    print(f"\n{'=' * 66}\n  {title}\n{'=' * 66}")


@dataclass
class Resp:
    status: int
    body: str

    def json(self) -> dict:
        try:
            return json.loads(self.body) if self.body else {}
        except ValueError:
            return {}


def api(method, path, token=None, body=None, params=None, retries=3) -> Resp:
    url = BASE_URL + path + ("?" + urlencode(params) if params else "")
    data = json.dumps(body).encode() if body is not None else None
    for attempt in range(retries + 1):
        req = Request(url, data=data, method=method)
        req.add_header("Content-Type", "application/json")
        req.add_header("X-App-Build", APP_BUILD)
        if token:
            # The API authorizes on the ID token (custom:role lives there).
            req.add_header("Authorization", f"Bearer {token}")
        try:
            with urlopen(req, timeout=30, context=ssl.create_default_context()) as r:
                return Resp(r.status, r.read().decode())
        except HTTPError as e:
            text = e.read().decode() if e.fp else ""
            if e.code == 429 and attempt < retries:
                time.sleep(5 * (attempt + 1))
                continue
            return Resp(e.code, text)
        except URLError as e:
            return Resp(0, str(e))
    return Resp(429, "retries exceeded")


@dataclass
class User:
    role: str
    phone: str
    user_id: str = ""
    name: str = ""
    woreda: str = ""
    token: str = ""
    signed_in_at: float = 0.0


@dataclass
class State:
    passed: int = 0
    failed: int = 0
    skipped: int = 0
    warned: int = 0
    commitments: Optional[bool] = None
    inbox: Optional[bool] = None
    ids: dict = field(default_factory=dict)

    def check(self, label, ok, detail=""):
        print(f"  {'[+]' if ok else '[X]'} {label}: {'PASS' if ok else 'FAIL'}"
              + (f" -- {detail}" if detail else ""))
        if ok:
            self.passed += 1
        else:
            self.failed += 1
        return ok

    def skip(self, label, detail=""):
        print(f"  [~] {label}: SKIP" + (f" -- {detail}" if detail else ""))
        self.skipped += 1

    def warn(self, label, detail=""):
        print(f"  [!] {label}: WARN" + (f" -- {detail}" if detail else ""))
        self.warned += 1


# ── API helpers ──────────────────────────────────────────────────────────


def login(u: User):
    r = api("POST", "/auth/login", body={"phoneNumber": u.phone, "password": PASSWORD})
    if r.status != 200:
        raise SystemExit(f"FATAL: login {u.role} {r.status}: {r.body[:200]}")
    d = r.json()
    u.user_id = d["user"]["userId"]
    u.name = d["user"].get("displayName") or u.role.title()
    u.woreda = d["user"].get("woreda") or "Addis Ababa"
    u.token = d["tokens"]["idToken"]
    u.signed_in_at = time.time()


# A payment and a drop-out need a sign-in under 10 minutes old (AUTH_040).
FRESH_SIGN_IN_SECONDS = 8 * 60


def fresh(u: User) -> User:
    if time.time() - u.signed_in_at > FRESH_SIGN_IN_SECONDS:
        login(u)
    return u


def sync(u: User, entity, entity_id, action, data) -> dict:
    """One /sync operation; returns its result ({status, message, serverData})."""
    r = api("POST", "/sync", token=u.token, body={
        "syncBatchId": uid(), "clientId": f"e2e-three-party-{u.role}",
        "operations": [{
            "operationId": uid(),
            "idempotencyKey": f"{u.user_id}:{entity}:{entity_id}:{action}:{int(time.time() * 1000)}",
            "entityType": entity, "entityId": entity_id, "action": action,
            "data": data, "timestamp": now_iso(),
        }],
    })
    if r.status != 200:
        return {"status": "http", "message": f"{r.status}: {r.body[:200]}"}
    return (r.json().get("results") or [{}])[0]


def ok_result(res: dict) -> bool:
    return res.get("status") == "success"


def message(res: dict) -> str:
    return str(res.get("message") or res.get("status") or "")[:200]


def get_order(u: User, order_id: str) -> dict:
    r = api("GET", f"/orders/{order_id}", token=u.token)
    d = r.json() if r.status == 200 else {}
    return d.get("order") or d


def get_trip(u: User, trip_id: str) -> dict:
    r = api("GET", f"/trips/{trip_id}", token=u.token)
    d = r.json() if r.status == 200 else {}
    return d.get("trip") or d


def version(u: User, order_id: str) -> int:
    return int(get_order(u, order_id).get("version") or 1)


def room_left(trip: dict) -> float:
    remaining = trip.get("remainingQuintals")
    return float(trip.get("capacity") or 0) if remaining is None else float(remaining)


def inbox_types(u: User, order_id: str) -> list:
    """The types of [u]'s inbox entries about [order_id], newest first."""
    r = api("GET", "/notifications", token=u.token, params={"limit": "50"})
    if r.status != 200:
        return []
    return [n.get("type") for n in r.json().get("notifications", []) if n.get("orderId") == order_id]


# ── Fixtures ─────────────────────────────────────────────────────────────


def stock_quintals(crop: dict) -> float:
    """A listing's stock. Listings are kept in quintals (crop-listing-api.md);
    GET /crops returns that number in a field named `quantityKg`."""
    return float(crop.get("quantityKg") or crop.get("quantity") or 0)


def seed_crop(s: State, farmer: User) -> Optional[dict]:
    # Listing quantity is in quintals; a run holds up to about 4 at once. A unique
    # size and price keep the duplicate-listing rule out of the way.
    salt = int(time.time()) % 20
    crop_id = uid()
    res = sync(farmer, "crop_listing", crop_id, "create", {
        "farmerId": farmer.user_id, "farmerName": farmer.name, "woreda": farmer.woreda,
        "cropType": "teff", "quantity": MIN_STOCK_QUINTALS + salt, "pricePerKg": 40.0 + salt % 9,
        "status": "available",
    })
    if not ok_result(res):
        # The farmer has a similar teff listing: use the largest available one.
        print(f"  (crop create: {message(res)}; reusing an available listing)")
        r = api("GET", "/crops", token=farmer.token)
        crops = sorted(
            (c for c in r.json().get("crops", []) if c.get("status") == "available"),
            key=lambda c: -stock_quintals(c))
        if not crops:
            s.check("farmer lists a crop", False, message(res))
            return None
        crop_id = crops[0]["cropId"]
    r = api("GET", f"/crops/{crop_id}", token=farmer.token)
    crop = r.json().get("crop") or r.json()
    if stock_quintals(crop) < MIN_STOCK_QUINTALS / 2:
        top_up = sync(farmer, "crop_listing", crop_id, "update", {
            "quantity": MIN_STOCK_QUINTALS, "version": int(crop.get("version") or 1),
        })
        if ok_result(top_up):
            crop = (api("GET", f"/crops/{crop_id}", token=farmer.token).json().get("crop")) or crop
        else:
            s.warn("top up the listing's stock", message(top_up))
    s.check("farmer lists a crop", bool(crop.get("cropId")),
            f"{crop_id[:8]} teff {stock_quintals(crop)} q @ {crop.get('pricePerKg')} ETB/kg")
    return crop


def seed_trip(s: State, trucker: User, capacity: float, label: str) -> Optional[dict]:
    trip_id = uid()
    # Five days out: an order is refused on a trip leaving within 24 hours.
    departure = time.strftime("%Y-%m-%dT06:00:00Z", time.gmtime(time.time() + 5 * 86400))
    res = sync(trucker, "trip", trip_id, "create", {
        "truckerId": trucker.user_id, "truckerName": trucker.name, "truckerPhone": trucker.phone,
        "fromWoreda": "Debre Birhan", "toWoreda": "Dire Dawa", "capacity": capacity,
        "pricePerQuintal": 90.0 + int(time.time()) % 10, "departureDate": departure, "status": "open",
    })
    if not s.check(f"trucker lists a trip ({label})", ok_result(res), message(res)):
        return None
    return get_trip(trucker, trip_id)


def place_order(s: State, buyer: User, farmer: User, crop: dict, trip: Optional[dict],
                trucker: Optional[User], label: str) -> Optional[str]:
    order_id = uid()
    data = {
        "listingId": crop["cropId"], "buyerId": buyer.user_id, "farmerId": farmer.user_id,
        "buyerName": buyer.name, "farmerName": farmer.name, "cropType": crop.get("cropType", "teff"),
        "quantity": ORDER_KG, "pricePerUnit": float(crop["pricePerKg"]),
        "paymentMethod": "telebirr", "status": "created",
    }
    if trip and trucker:
        data.update({
            "tripId": trip["tripId"], "truckerId": trucker.user_id, "truckerName": trucker.name,
            "transportCost": ORDER_KG / KG_PER_QUINTAL * float(trip["pricePerQuintal"]),
        })
    res = sync(buyer, "order", order_id, "create", data)
    if not s.check(f"buyer places the order ({label})", ok_result(res), message(res)):
        return None
    s.ids[label] = order_id
    return order_id


def pay(s: State, buyer: User, order_id: str, label: str) -> dict:
    """The buyer pays; dev's mock gateway funds the order at once."""
    fresh(buyer)
    res = sync(buyer, "order", order_id, "update", {
        "status": "payment_submitted", "paymentMethod": "telebirr", "version": version(buyer, order_id),
    })
    if not s.check(f"buyer pays ({label})", ok_result(res), message(res)):
        return {}
    order = {}
    for _ in range(10):
        order = get_order(buyer, order_id)
        if order.get("status") not in ("payment_submitted", None):
            break
        time.sleep(1)
    return order


def answer(u: User, order_id: str, field_name: str, command: str, reason: Optional[str] = None) -> dict:
    data = {field_name: command, "version": version(u, order_id)}
    if reason:
        data["declineReason"] = reason
    return sync(u, "order", order_id, "update", data)


def cancel(u: User, order_id: str) -> dict:
    return sync(u, "order", order_id, "update", {"status": "cancelled", "version": version(u, order_id)})


def expect_inbox(s: State, who: str, u: User, order_id: str, wanted: list, absent: tuple = ()):
    """Waits for [wanted] in [u]'s inbox about the order, then checks [absent] never came."""
    if not s.inbox:
        s.skip(f"{who} inbox {wanted}", "GET /notifications not on this deployment")
        return
    deadline = time.time() + INBOX_WAIT_SECONDS
    seen = []
    while time.time() < deadline:
        seen = inbox_types(u, order_id)
        if all(w in seen for w in wanted):
            break
        time.sleep(3)
    for w in wanted:
        s.check(f"{who} is told {w}", w in seen, f"inbox has {seen}")
    for a in absent:
        s.check(f"{who} is not told {a}", a not in seen, f"inbox has {seen}")


def flag_on(s: State, label: str) -> bool:
    if s.commitments:
        return True
    s.skip(label, "COMMITMENTS_ENABLED is off on this server")
    return False


# ── Scenarios ────────────────────────────────────────────────────────────


def scenario_a(s, F, T, B, crop):
    hdr("A. Happy path: farmer first, then trucker")
    trip = seed_trip(s, T, 40.0, "A")
    if not trip:
        return
    room_before = room_left(trip)
    oid = place_order(s, B, F, crop, trip, T, "A")
    if not oid:
        return
    order = pay(s, B, oid, "A")
    s.commitments = bool(order.get("fulfillment"))
    print(f"  (server runs the commitments: {s.commitments})")
    if not s.commitments:
        s.check("funded", order.get("status") == "funded", f"status={order.get('status')}")
        res = answer(F, oid, "farmerResponse", "accept")
        order = get_order(B, oid)
        s.check("legacy: the farmer's accept is final", ok_result(res) and order.get("status") == "accepted",
                f"status={order.get('status')} {message(res)}")
        return
    s.check("funded with the farmer asked first",
            order.get("status") == "funded" and order.get("farmerResponse") == "pending"
            and order.get("truckerResponse") == "not_asked" and bool(order.get("farmerDeadlineAt")),
            f"status={order.get('status')} farmer={order.get('farmerResponse')} "
            f"trucker={order.get('truckerResponse')} due={order.get('farmerDeadlineAt')}")
    trip_after = get_trip(T, trip["tripId"])
    s.check("funding reserves the trip's room", abs(room_before - room_left(trip_after) - ORDER_KG / KG_PER_QUINTAL) < 0.01,
            f"room {room_before} -> {room_left(trip_after)}")
    expect_inbox(s, "buyer", B, oid, ["payment_confirmed"])
    expect_inbox(s, "farmer", F, oid, ["order_request"])
    if s.inbox:
        s.check("the trucker is told nothing before the farmer answers", inbox_types(T, oid) == [],
                f"inbox has {inbox_types(T, oid)}")

    res = answer(T, oid, "truckerResponse", "accept")
    s.check("the trucker cannot accept before the farmer",
            not ok_result(res) and "ORDER_RESPONSE_NOT_PENDING" in message(res), message(res))

    res = answer(F, oid, "farmerResponse", "accept")
    order = get_order(B, oid)
    s.check("farmer accepts: still funded, the trucker is asked",
            ok_result(res) and order.get("status") == "funded" and order.get("farmerResponse") == "accepted"
            and order.get("truckerResponse") == "pending" and bool(order.get("truckerDeadlineAt")),
            f"status={order.get('status')} trucker={order.get('truckerResponse')} {message(res)}")
    s.check("buyer and farmer may now reach each other", order.get("contactOpen") is True,
            f"contactOpen={order.get('contactOpen')}")
    t_view = get_order(T, oid)
    s.check("the asked trucker sees no phones yet",
            not t_view.get("buyerPhone") and not t_view.get("farmerPhone"),
            f"contactOpen={t_view.get('contactOpen')}")
    expect_inbox(s, "buyer", B, oid, ["farmer_accepted"])
    expect_inbox(s, "trucker", T, oid, ["transport_request"])

    res = answer(T, oid, "truckerResponse", "accept")
    order = get_order(B, oid)
    s.check("trucker accepts: the order is agreed",
            ok_result(res) and order.get("status") == "accepted" and order.get("truckerResponse") == "accepted"
            and bool(order.get("pickupDeadlineAt")),
            f"status={order.get('status')} pickup by {order.get('pickupDeadlineAt')} {message(res)}")
    t_view = get_order(T, oid)
    s.check("the committed trucker may reach the parties", t_view.get("contactOpen") is True,
            f"contactOpen={t_view.get('contactOpen')}")
    expect_inbox(s, "buyer", B, oid, ["order_confirmed"])
    expect_inbox(s, "farmer", F, oid, ["order_confirmed"], absent=("farmer_accepted",))
    expect_inbox(s, "trucker", T, oid, [], absent=("order_confirmed",))

    did = uid()
    res = sync(T, "delivery", did, "create", {
        "tripId": trip["tripId"], "orderId": oid, "listingId": crop["cropId"],
        "farmerId": F.user_id, "farmerName": F.name, "farmerPhone": F.phone,
        "truckerId": T.user_id, "truckerName": T.name, "truckerPhone": T.phone,
        "buyerId": B.user_id, "buyerName": B.name, "buyerPhone": B.phone,
        "qrToken": f"qr-{did[:8]}", "status": "pending",
    })
    s.check("trucker creates the delivery", ok_result(res), message(res))
    expect_inbox(s, "buyer", B, oid, ["delivery_scheduled"])
    expect_inbox(s, "farmer", F, oid, ["delivery_scheduled"])


def scenario_b(s, F, T, B, crop):
    hdr("B. The trucker declines; the buyer cancels for a refund")
    if not flag_on(s, "B"):
        return
    trip = seed_trip(s, T, 40.0, "B")
    if not trip:
        return
    oid = place_order(s, B, F, crop, trip, T, "B")
    if not oid or not pay(s, B, oid, "B"):
        return
    answer(F, oid, "farmerResponse", "accept")
    room_asked = room_left(get_trip(T, trip["tripId"]))
    res = answer(T, oid, "truckerResponse", "decline", reason="capacity")
    order = get_order(B, oid)
    s.check("trucker declines: the order waits for the buyer",
            ok_result(res) and order.get("status") == "awaiting_transport"
            and order.get("transportFailure") == "trucker_declined" and bool(order.get("buyerChoiceDeadlineAt")),
            f"status={order.get('status')} failure={order.get('transportFailure')} {message(res)}")
    s.check("the decline gives the room back", room_left(get_trip(T, trip["tripId"])) > room_asked,
            f"room {room_asked} -> {room_left(get_trip(T, trip['tripId']))}")
    expect_inbox(s, "buyer", B, oid, ["transport_failed"])
    expect_inbox(s, "farmer", F, oid, ["transport_failed_keep_crops"])

    res = cancel(B, oid)
    order = get_order(B, oid)
    s.check("buyer cancels for a full refund", ok_result(res) and order.get("status") == "cancelled",
            f"status={order.get('status')} reason={order.get('cancelReason')} {message(res)}")
    expect_inbox(s, "farmer", F, oid, ["order_cancelled"])
    expect_inbox(s, "trucker", T, oid, [], absent=("order_cancelled",))
    if s.inbox:
        deadline = time.time() + INBOX_WAIT_SECONDS
        while time.time() < deadline and "refund_sent" not in inbox_types(B, oid):
            time.sleep(3)
        if "refund_sent" in inbox_types(B, oid):
            s.check("buyer is told refund_sent", True)
        else:
            s.warn("buyer is told refund_sent", "the refund may still be on its way (escrow sweep)")


def scenario_c(s, F, T, B, crop):
    hdr("C. No buyer cancel while the parties decide")
    if not flag_on(s, "C"):
        return
    oid = place_order(s, B, F, crop, None, None, "C")
    if not oid or not pay(s, B, oid, "C"):
        return
    res = cancel(B, oid)
    s.check("a funded order cannot be cancelled by the buyer",
            not ok_result(res) and "ORDER_CANCEL_NOT_ALLOWED" in message(res), message(res))
    res = answer(F, oid, "farmerResponse", "decline")
    s.check("(cleanup) the farmer declines it", ok_result(res), message(res))


def scenario_d(s, F, T, B, crop):
    hdr("D. Self-pickup: the farmer's accept agrees the order")
    if not flag_on(s, "D"):
        return
    oid = place_order(s, B, F, crop, None, None, "D")
    if not oid:
        return
    order = pay(s, B, oid, "D")
    s.check("self-pickup has no trucker to ask",
            order.get("fulfillment") == "self_pickup" and order.get("truckerResponse") == "none",
            f"fulfillment={order.get('fulfillment')} trucker={order.get('truckerResponse')}")
    res = answer(F, oid, "farmerResponse", "accept")
    order = get_order(B, oid)
    s.check("farmer accepts: agreed at once", ok_result(res) and order.get("status") == "accepted",
            f"status={order.get('status')} {message(res)}")
    expect_inbox(s, "buyer", B, oid, ["payment_confirmed", "order_confirmed"])
    expect_inbox(s, "farmer", F, oid, ["order_request"], absent=("order_confirmed",))


def scenario_e(s, F, T, B, crop):
    hdr("E. The farmer declines")
    if not flag_on(s, "E"):
        return
    trip = seed_trip(s, T, 40.0, "E")
    if not trip:
        return
    oid = place_order(s, B, F, crop, trip, T, "E")
    if not oid or not pay(s, B, oid, "E"):
        return
    res = answer(F, oid, "farmerResponse", "decline", reason="crop_not_ready")
    order = get_order(B, oid)
    s.check("farmer declines: rejected, the buyer is refunded",
            ok_result(res) and order.get("status") == "rejected" and order.get("farmerResponse") == "declined",
            f"status={order.get('status')} {message(res)}")
    expect_inbox(s, "buyer", B, oid, ["order_rejected"])
    if s.inbox:
        s.check("the trucker was never asked, so is told nothing", inbox_types(T, oid) == [],
                f"inbox has {inbox_types(T, oid)}")


def scenario_f(s, F, T, B, crop):
    hdr("F. A trip without room refuses the second payment")
    if not flag_on(s, "F"):
        return
    # 1.5 quintals of room: each order takes 1, so the second one no longer fits.
    trip = seed_trip(s, T, 1.5, "F")
    if not trip:
        return
    first = place_order(s, B, F, crop, trip, T, "F1")
    second = place_order(s, B, F, crop, trip, T, "F2")
    if not first or not second:
        return
    s.check("the first order is funded", bool(pay(s, B, first, "F1")))
    res = sync(fresh(B), "order", second, "update", {
        "status": "payment_submitted", "paymentMethod": "telebirr", "version": version(B, second),
    })
    s.check("the second payment is refused (no room)", not ok_result(res) and "ORDER_" in message(res),
            message(res))
    for oid in (first,):
        answer(F, oid, "farmerResponse", "decline")


def scenario_g(s, F, T, B, crop):
    hdr("G. The trucker cancels a trip with orders on it")
    if not flag_on(s, "G"):
        return
    trip = seed_trip(s, T, 40.0, "G")
    if not trip:
        return
    waiting = place_order(s, B, F, crop, trip, T, "G-waiting")
    agreed = place_order(s, B, F, crop, trip, T, "G-agreed")
    if not waiting or not agreed:
        return
    if not pay(s, B, waiting, "G-waiting") or not pay(s, B, agreed, "G-agreed"):
        return
    answer(F, agreed, "farmerResponse", "accept")
    res = answer(T, agreed, "truckerResponse", "accept")
    s.check("one order agreed before the cancel", ok_result(res), message(res))

    current = get_trip(T, trip["tripId"])
    res = sync(T, "trip", trip["tripId"], "update", {"status": "cancelled", "version": int(current.get("version") or 1)})
    s.check("trucker cancels the trip", ok_result(res), message(res))
    time.sleep(2)
    w = get_order(B, waiting)
    s.check("the undecided order loses its trucker, the farmer still decides",
            w.get("status") == "funded" and w.get("truckerResponse") == "cancelled",
            f"status={w.get('status')} trucker={w.get('truckerResponse')}")
    a = get_order(B, agreed)
    s.check("the agreed order waits for the buyer (trip_cancelled)",
            a.get("status") == "awaiting_transport" and a.get("transportFailure") == "trip_cancelled",
            f"status={a.get('status')} failure={a.get('transportFailure')}")
    expect_inbox(s, "buyer", B, agreed, ["transport_failed"])
    expect_inbox(s, "farmer", F, agreed, ["transport_failed_keep_crops"])
    # Leave nothing open: the buyer cancels the waiting one, the farmer declines the other.
    cancel(B, agreed)
    answer(F, waiting, "farmerResponse", "decline")


def scenario_h(s, F, T, B, crop, cron_function: Optional[str]):
    hdr("H. Deadlines: stamped, and settled by the cron")
    if not flag_on(s, "H"):
        return
    oid = place_order(s, B, F, crop, None, None, "H")
    if not oid:
        return
    order = pay(s, B, oid, "H")
    s.check("the farmer's deadline is stamped at funding", bool(order.get("farmerDeadlineAt")),
            f"due {order.get('farmerDeadlineAt')}")
    if not cron_function:
        s.skip("cron commitment_deadlines run", "pass --cron-function to invoke it with AWS credentials")
    elif not shutil.which("aws"):
        s.skip("cron commitment_deadlines run", "aws CLI not installed")
    else:
        payload = json.dumps({"body": json.dumps({"action": "commitment_deadlines"})})
        run = subprocess.run(
            ["aws", "lambda", "invoke", "--function-name", cron_function,
             "--cli-binary-format", "raw-in-base64-out", "--payload", payload, "/dev/stdout"],
            capture_output=True, text=True, timeout=120)
        s.check("cron commitment_deadlines runs", run.returncode == 0,
                (run.stdout or run.stderr).strip()[:200])
        # The deadline is a day out, so the order must still be waiting for the farmer.
        s.check("an open window is left alone", get_order(B, oid).get("status") == "funded")
    answer(F, oid, "farmerResponse", "decline")


SCENARIOS = {"A": scenario_a, "B": scenario_b, "C": scenario_c, "D": scenario_d,
             "E": scenario_e, "F": scenario_f, "G": scenario_g}


def main():
    global BASE_URL, PASSWORD
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base-url", default=BASE_URL)
    ap.add_argument("--password", default=PASSWORD)
    ap.add_argument("--only", default="A,B,C,D,E,F,G,H", help="comma-separated scenario letters")
    ap.add_argument("--cron-function", default=None, help="order cron Lambda name for scenario H")
    args = ap.parse_args()
    BASE_URL = args.base_url.rstrip("/")
    PASSWORD = args.password
    only = [x.strip().upper() for x in args.only.split(",") if x.strip()]

    print(f"\nWeredaConnect three-party acceptance\nAPI: {BASE_URL}\nScenarios: {','.join(only)}")
    if api("GET", "/health").status != 200:
        raise SystemExit(f"FATAL: API health check failed at {BASE_URL}")

    s = State()
    F, T, B = (User(role, PHONES[role]) for role in ("farmer", "trucker", "buyer"))
    for u in (F, T, B):
        login(u)
    print(f"  farmer {F.user_id[:8]}  trucker {T.user_id[:8]}  buyer {B.user_id[:8]}")
    s.inbox = api("GET", "/notifications", token=B.token, params={"limit": "1"}).status == 200
    print(f"  (inbox endpoint deployed: {s.inbox})")

    crop = seed_crop(s, F)
    if not crop:
        raise SystemExit(1)
    if "A" not in only:
        # The other scenarios need to know whether the server runs the commitments.
        probe = place_order(s, B, F, crop, None, None, "probe")
        if probe:
            s.commitments = bool(pay(s, B, probe, "probe").get("fulfillment"))
            answer(F, probe, "farmerResponse", "decline")
    for letter in only:
        if letter == "H":
            scenario_h(s, F, T, B, crop, args.cron_function)
        elif letter in SCENARIOS:
            SCENARIOS[letter](s, F, T, B, crop)
        else:
            print(f"  unknown scenario {letter}")

    hdr("SUMMARY")
    print(f"  Passed: {s.passed}   Failed: {s.failed}   Skipped: {s.skipped}   Warnings: {s.warned}")
    print(f"  Orders: {json.dumps(s.ids)}")
    sys.exit(1 if s.failed else 0)


if __name__ == "__main__":
    main()
