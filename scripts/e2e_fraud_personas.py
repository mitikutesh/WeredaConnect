#!/usr/bin/env python3
"""
WeredaConnect adversarial personas (FRD-17)
===========================================
Runs six adversarial personas against a deployed API (dev by default), each on
its OWN freshly registered accounts, and writes a markdown report per persona:
the actions taken, the signal types the server should write, the signals it
did write (read back through the admin Fraud Review API), the risk tier those
signals give, the step-up or refusal the persona met, and the escrow hold where
one applies. A persona no rule catches today is reported as NOT DETECTED: that
is a finding, never something this script fakes.

Personas (specs/architecture/fraud-detection-architecture.md sections 2, 15 to 18):
  spammer    listing spammer: duplicates, an absurd price, more than 5 listings in an hour
  selftrade  self-trader: one person with a farmer and a buyer account on one device
             buys their own crop, collects it and rates themselves 5 stars
  idor       IDOR prober: a buyer and a farmer account create ids of other users' rows,
             act outside their role and rewrite immutable fields
  payer      payer mismatch: a payment from a wallet other than the buyer's phone
  gps        GPS spoofer: a trucker sends mock-location and teleporting check-ins and a
             drop-off confirmed minutes after a pickup hundreds of km away
  dispute    confirm-then-dispute buyer: pays 5 orders, confirms 2 deliveries and
             disputes both

Accounts. Every persona registers its own accounts through the dev mock Fayda
provider: POST /auth/fayda-start, the mock's authorize URL with
`auto=1&phone=<number>` (an identity whose phone_number claim is the account
phone, specs/features/fayda-mock-server.md), POST /auth/fayda-verify, then
POST /auth/register, which confirms the phone from the Fayda claim and answers
with tokens (no SMS is sent). Phones are drawn from +25190017XXXX, never the
shared test users (+2519001000xx), and names start with `frd17`. Two support
accounts (a clean farmer with a listing, and a clean buyer) are registered when
a persona needs a counterparty. Every request of an account carries one
X-Device-Id, so no new-device SMS is ever triggered. A full run registers 9
accounts (the register route allows 10 per IP in a window that opens with the
first registration and closes an hour later). The last phase waits until two
sign-ins are over 5 minutes old to observe AUTH_040, so a full run takes about
7 minutes.

Hosts called: the API, the dev mock Fayda Lambda (only through the authorize URL
the API hands out; a live or sandbox Fayda host is refused), and the Cognito
admin pool's public InitiateAuth for the admin read (as scripts/e2e_admin.py).
Payments go through dev's mock gateway (no money moves). Crop listings carry
photo ids with no uploaded photos (the server does not check them at create,
crop-listing-api rule 9), so nothing is sent to S3.

Usage:
  python3 scripts/e2e_fraud_personas.py --self-test          # offline checks, no network
  python3 scripts/e2e_fraud_personas.py --dry-run            # print the plan, no network
  python3 scripts/e2e_fraud_personas.py                      # all personas against dev
  python3 scripts/e2e_fraud_personas.py --personas idor,gps --report /tmp/r.md
  python3 scripts/e2e_fraud_personas.py --state-file /tmp/frd17-state.json  # keep the accounts (outside the repo)
  python3 scripts/e2e_fraud_personas.py --recheck /tmp/frd17-state.json     # later: tiers and held escrows

Exit codes: 0 every persona matched its expectation (detected or honestly not
detected), 1 a persona deviated, 2 setup failed (API refused, admin login
refused, registration refused, or a --state-file path inside a git checkout).
--recheck: 0 every hold target is held or not due yet, 1 a target is past its
farmer deadline plus --hold-grace-hours and not held, 2 setup failed.

Observation (FRD-07): GET /admin/fraud-signals lists one status at a time, so
both `pending` and `reviewed` are read (a row an admin rejected is `reviewed`
and still counts), and each page's `users[]` carries the server's score and
tier. Those are what the run asserts; a replica of section 14.2 recomputes them
from the rows as a cross-check and a disagreement is flagged.
"""
import argparse
import base64
import calendar
import json
import os
import random
import re
import ssl
import sys
import time
import uuid
from collections import defaultdict
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, quote, urlencode, urlparse
from urllib.request import HTTPRedirectHandler, Request, build_opener, urlopen

DEV_BASE_URL = "https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com"
FAYDA_REDIRECT = "weredaconnect://fayda-callback"
FAYDA_LINK_REDIRECT = "weredaconnect://fayda-link-callback"
APP_BUILD = "3"
SHARED_TEST_PHONES = {"+251900100001", "+251900100002", "+251900100003", "+251900100009"}
PERSONA_PHONE_PREFIX = "+25190017"
PHONE_RE = re.compile(r"^\+25190017\d{4}$")
# A password is fresh for STEP_UP_MAX_AGE_SECONDS (300); sign in again well before, and wait
# past it (STALE_SECONDS) for the stale-password probes.
FRESH_SECONDS = 200
STALE_SECONDS = 315
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Places (Ethiopia bounding box) the personas use.
DEBRE_BIRHAN = (9.6797, 39.5308)
DIRE_DAWA = (9.6009, 41.8501)

# ── Risk model (fraud-detection-architecture.md sections 14 to 16, FraudDetectionService) ──

WEIGHTS = {
    "GPS_ANOMALY": 25, "FRAUD_DUPLICATE_LISTING": 20, "FRAUD_VELOCITY_EXCEEDED": 15, "PRICE_ANOMALY": 10,
    "IDOR_CREATE_CONFLICT": 40, "ROLE_FORBIDDEN": 25, "IMMUTABLE_FIELD_CHANGE": 25, "QR_HANDOFF_FAILURE": 5,
    "TELEBIRR_PAYER_MISMATCH": 0, "TELEBIRR_PAYER_REFUSED": 5, "STALE_VERSION_BURST": 0,
    "SUSPENDED_TOKEN_USE": 0, "GPS_JUMP": 10, "GPS_MOCKED": 25, "AMOUNT_ANOMALY": 10,
    "NEW_ACCOUNT_LARGE_AMOUNT": 10, "KYC_THRESHOLD_STRUCTURING": 20, "CANCEL_AFTER_FUND_RATE": 15,
    "DISPUTE_RATE": 10, "IMPOSSIBLE_DELIVERY_TIME": 15,
    # ESC-33 reputation hooks; NO_SHOW's producer is ESC-34's pickup judgement (fraud spec 15.3).
    "NO_SHOW": 15, "DELIVERY_OVERDUE": 25,
}
DEFAULT_WEIGHT = 10
PATTERN_TYPES = {"AMOUNT_ANOMALY", "NEW_ACCOUNT_LARGE_AMOUNT", "KYC_THRESHOLD_STRUCTURING",
                 "CANCEL_AFTER_FUND_RATE", "DISPUTE_RATE", "IMPOSSIBLE_DELIVERY_TIME"}
PER_ENTITY_TYPES = {"ROLE_FORBIDDEN", "IMMUTABLE_FIELD_CHANGE", "QR_HANDOFF_FAILURE",
                    "TELEBIRR_PAYER_MISMATCH", "TELEBIRR_PAYER_REFUSED", "STALE_VERSION_BURST",
                    "SUSPENDED_TOKEN_USE", "GPS_JUMP", "GPS_MOCKED", "NO_SHOW", "DELIVERY_OVERDUE"} | PATTERN_TYPES
CAPPED_TYPES = {"ROLE_FORBIDDEN", "IMMUTABLE_FIELD_CHANGE"} | PATTERN_TYPES
# Types that share one cap (fraud spec 15.3: missed pickups and late deliveries stop at MEDIUM together).
CAP_GROUPS = {"NO_SHOW": "group:reliability", "DELIVERY_OVERDUE": "group:reliability"}
TYPE_CAP = 50
# FUP-204: a farmer's NO_SHOW rests on another party's check-in and goes through the attribution guard
# (fraud spec 14.3): at most this many rows on one user from other users' reports in 30 days.
ATTRIBUTED_PER_TARGET_CAP = 2
TIER_MEDIUM, TIER_HIGH, TIER_CRITICAL = 30, 60, 100

# Where each signal type comes from in the server (rule, producer).
RULES = {
    "FRAUD_DUPLICATE_LISTING": ("Rule 1", "FraudDetectionService.CheckListingCreationAsync, recorded by the SyncFunction crop create"),
    "FRAUD_VELOCITY_EXCEEDED": ("Rule 3", "FraudDetectionService.CheckListingCreationAsync (5 listings per hour), SyncFunction crop create"),
    "PRICE_ANOMALY": ("Rule 2", "FraudDetectionService.CheckListingCreationAsync (more than 3 robust spreads from the median of the last 50 listings of the type: 1.4826 x MAD, floored at 10% of the median), flag only"),
    "IDOR_CREATE_CONFLICT": ("FRD-08", "SyncFunction create conflict on a crop, trip, order or delivery id the caller does not own"),
    "ROLE_FORBIDDEN": ("FRD-08", "SyncFunction.RefuseWithSignalAsync: role gates, caller id binding, rows the caller is no party to"),
    "IMMUTABLE_FIELD_CHANGE": ("FRD-08", "SyncFunction.RejectImmutableFieldChanges (crop SIM-02, order SIM-06)"),
    "GPS_MOCKED": ("LT-07", "SyncFunction.CheckInFraudSignalsAsync (SyncFunction.CheckInEffects.cs), check-in flagged mocked"),
    "GPS_JUMP": ("LT-07", "SyncFunction.CheckInFraudSignalsAsync, neighbouring check-ins over 1 km apart at over 130 km/h"),
    "IMPOSSIBLE_DELIVERY_TIME": ("FRD-09", "TransactionPatternService.CheckDeliveryTime via SyncFunction.TryRecordDeliveryPatternSignalsAsync, on the delivery's trucker"),
    "DISPUTE_RATE": ("FRD-09", "TransactionPatternService.CheckDisputeRateAsync via EscrowFunction.HandleDisputeAsync"),
    "TELEBIRR_PAYER_MISMATCH": ("FRD-08", "TelebirrWebhookFunction.PayerMatchesBuyerAsync, TELEBIRR_REQUIRE_PAYER_MATCH off, weight 0"),
    "TELEBIRR_PAYER_REFUSED": ("FRD-08", "TelebirrWebhookFunction.PayerMatchesBuyerAsync, TELEBIRR_REQUIRE_PAYER_MATCH on, weight 5"),
    "GPS_ANOMALY": ("Rule 4", "FraudDetectionService.CheckGpsConsistency, recorded on the caller of the delivered update (the buyer)"),
    "NEW_ACCOUNT_LARGE_AMOUNT": ("FRD-09", "TransactionPatternRules, payment of 5,000 ETB or more from an account under 7 days"),
    "NO_SHOW": ("ESC-33, ESC-34", "ReputationFraudHooks after a NO_SHOW reputation event: the pickup judgement's trucker no-show "
                "(R10, written plainly) or farmer no-show (R11, through the attribution guard, the trucker's check-in as the source)"),
    "DELIVERY_OVERDUE": ("ESC-33, ESC-36", "ReputationFraudHooks after a DELIVERY_OVERDUE reputation event: ESC-36's T8, no drop-off "
                         "check-in 72 hours after pickup (R12, written plainly: it rests on the trucker alone, FUP-204)"),
}
GATES = {
    "FRAUD_ACCOUNT_HELD": "RiskGatePolicy (FRD-04): CRITICAL refuses crop, trip, order create and payments",
    "AUTH_041": "StepUpPolicy.CheckPayment (FRD-04): HIGH asks a fresh Fayda login at any amount",
    "AUTH_040": "StepUpPolicy.CheckPayment: a fresh password for a payment up to 10,000 ETB (LOW and MEDIUM)",
    "held": "SettlementRiskGate / PartySuspensionGate + EscrowService.HoldForReviewAsync in the EscrowCronFunction sweeps (FRD-03, FRD-04)",
}


def tier_of(score: int) -> str:
    if score >= TIER_CRITICAL:
        return "CRITICAL"
    if score >= TIER_HIGH:
        return "HIGH"
    if score >= TIER_MEDIUM:
        return "MEDIUM"
    return "LOW"


def risk_score(rows: List[dict]) -> Tuple[int, str]:
    """Replica of RiskScoreCalculator for one user's signal rows (no credits, advisory or
    whitelist rows: the personas are fresh accounts). Rows: signalType, entityId, bucket (UTC
    hour 'YYYY-MM-DDTHH'), resolution, createdAt. Newest first; one counted row per (type, hour),
    or per (type, entity) for the per-entity types; an approve or whitelist clears its bucket;
    capped types stop at 50 each."""
    counted: Dict[tuple, int] = {}
    cleared = set()
    for row in sorted(rows, key=lambda r: r.get("createdAt") or "", reverse=True):
        kind = row.get("signalType") or ""
        entity = row.get("entityId")
        per_entity = kind in PER_ENTITY_TYPES and bool(entity)
        key = (kind, "entity", entity) if per_entity else (kind, "hour", row.get("bucket") or row.get("createdAt", "")[:13])
        if row.get("resolution") in ("approve", "whitelist"):
            if per_entity:
                cleared.add(key)
                counted.pop(key, None)
            continue
        if key in counted or key in cleared:
            continue
        counted[key] = WEIGHTS.get(kind, DEFAULT_WEIGHT)
    per_type: Dict[str, int] = defaultdict(int)
    for (kind, _, _), weight in counted.items():
        per_type[CAP_GROUPS.get(kind, kind)] += weight
    score = sum(min(v, TYPE_CAP) if k in CAPPED_TYPES or k in CAP_GROUPS.values() else v for k, v in per_type.items())
    score = max(0, score)
    return score, tier_of(score)


def expected_rows(types: List[str]) -> List[dict]:
    """One row per type in one hour, each on its own entity: what a persona run writes."""
    return [{"signalType": t, "entityId": f"e{i}", "bucket": "2026-11-07T10", "createdAt": f"2026-11-07T10:00:{i:02d}Z"}
            for i, t in enumerate(types)]


# ── Persona catalogue ─────────────────────────────────────────────────────


@dataclass
class Expect:
    signals: List[str]                       # must appear on the account
    optional: List[str] = field(default_factory=list)  # may appear (data dependent)
    absent: List[str] = field(default_factory=list)    # must not appear (attribution checks)
    tier: str = "LOW"
    challenge: str = ""
    hold: str = ""


@dataclass
class Persona:
    key: str
    title: str
    threat: str
    accounts: List[Tuple[str, str]]          # (label, role)
    plan: List[str]
    expect: Dict[str, Expect]
    detection: str                           # what should catch it today
    exercisable: bool = True


PERSONAS: Dict[str, Persona] = {
    "spammer": Persona(
        key="spammer", title="Listing spammer", threat="T1 listing spam, T2 duplicates, T3 price manipulation",
        accounts=[("spammer", "farmer")],
        plan=["create 5 listings of 4 crop types, one teff at 999 ETB/kg (the price rule's range ends at 1,000)",
              "repost the first teff listing at a price 2 percent away (duplicate)",
              "create 2 more listings in the same hour (over the 5 per hour limit)"],
        expect={"spammer": Expect(
            signals=["FRAUD_DUPLICATE_LISTING", "FRAUD_VELOCITY_EXCEEDED"], optional=["PRICE_ANOMALY"],
            tier="MEDIUM",
            challenge="none (a farmer makes no payment); the duplicate and both creates after the 5th accepted listing "
                      "are refused; creates stay allowed below CRITICAL (the 8th create attempt meets FRAUD_VELOCITY_EXCEEDED, "
                      "not FRAUD_ACCOUNT_HELD)",
            hold="none (below CRITICAL)")},
        detection="Rules 1 to 3 (live)"),
    "selftrade": Persona(
        key="selftrade", title="Self-trader", threat="wash trading for volume and reputation (FRD-15 relationship patterns)",
        accounts=[("self-farmer", "farmer"), ("self-buyer", "buyer")],
        plan=["one person, one device (shared X-Device-Id): the farmer account lists maize with a farm location",
              "the buyer account orders 50 kg (self-pickup) and pays",
              "the farmer accepts and shows the QR, the buyer confirms the pickup at the farm",
              "the buyer rates the farmer 5 stars",
              "after 5 minutes, the buyer pays a second order with the same sign-in: expected AUTH_040 (LOW, stale password)"],
        expect={"self-farmer": Expect(signals=[], tier="LOW", challenge="none", hold="none"),
                "self-buyer": Expect(signals=[], tier="LOW",
                                     challenge="a fresh password passes; AUTH_040 once the sign-in is over 5 minutes old (baseline)",
                                     hold="none")},
        detection="none today: self-trading and same-device pairs are FRD-15 (planned, no producer)"),
    "idor": Persona(
        key="idor", title="IDOR prober", threat="tampered client probing other users' rows (FRD-08)",
        accounts=[("idor-buyer", "buyer"), ("idor-farmer", "farmer")],
        plan=["buyer: create an order whose id is another buyer's order (create conflict)",
              "buyer: create a crop listing (role gate), cancel another buyer's order (no party), "
              "rewrite pricePerUnit on its own order (immutable), read another buyer's order over REST",
              "buyer: pay its own order: expected AUTH_041 (HIGH)",
              "buyer, after 5 minutes: Fayda step-up through the mock (its own identity), pay again with the "
              "old sign-in: expected AUTH_040 (HIGH asks Fayda, then a fresh password)",
              "farmer: list coffee, repost it (duplicate), create a listing on another farmer's listing id, "
              "create an order (role gate), move its own listing to another farmer (immutable)",
              "farmer: list again: expected FRAUD_ACCOUNT_HELD (CRITICAL)",
              "a clean buyer then pays the CRITICAL farmer's coffee listing (counterparty tier is not checked at order create, "
              "section 17 known gap); its escrow is due a risk_critical hold at settlement"],
        expect={"idor-buyer": Expect(
                    signals=["IDOR_CREATE_CONFLICT", "ROLE_FORBIDDEN", "IMMUTABLE_FIELD_CHANGE"],
                    tier="HIGH", challenge="AUTH_041 on its payment at any amount, then AUTH_040 after the Fayda step-up "
                                           "while the password is stale (HIGH asks both, Fayda first)",
                    hold="none (HIGH holds nothing; it lengthens the payout delay only)"),
                "idor-farmer": Expect(
                    signals=["IDOR_CREATE_CONFLICT", "ROLE_FORBIDDEN", "IMMUTABLE_FIELD_CHANGE", "FRAUD_DUPLICATE_LISTING"],
                    optional=["PRICE_ANOMALY"],
                    tier="CRITICAL", challenge="FRAUD_ACCOUNT_HELD on its next crop create",
                    hold="the escrow of the clean buyer's paid order is held (risk_critical) by the auto-refund sweep "
                         "once the farmer's answer deadline passes; check later with --recheck")},
        detection="FRD-08 reject signals and the FRD-04 tier gates (live)"),
    "payer": Persona(
        key="payer", title="Payer mismatch", threat="payment from a wallet that is not the buyer's (FRD-08)",
        accounts=[],
        plan=["probe the Telebirr webhook route the detector lives on with an unsigned, empty notification"],
        expect={},
        detection="TelebirrWebhookFunction.PayerMatchesBuyerAsync (live where the Telebirr webhook is deployed): "
                  "TELEBIRR_PAYER_MISMATCH weight 0 (logged, never scored) or TELEBIRR_PAYER_REFUSED weight 5",
        exercisable=False),
    "gps": Persona(
        key="gps", title="GPS spoofer", threat="T4 fake delivery to claim escrow (trucker)",
        accounts=[("gps-trucker", "trucker")],
        plan=["list a trip Debre Birhan to Dire Dawa; carry a paid order of the support farmer's teff",
              "pick up at the farm with the farmer's QR",
              "send two check-ins: the first flagged mocked by the phone, the second 50 km away 30 s later",
              "the buyer confirms the drop-off at Dire Dawa minutes after the pickup (about 250 km)"],
        expect={"gps-trucker": Expect(
            signals=["GPS_MOCKED", "GPS_JUMP", "IMPOSSIBLE_DELIVERY_TIME"], absent=["GPS_ANOMALY"],
            tier="MEDIUM", challenge="none (a trucker makes no payment; trips stay allowed below CRITICAL)",
            hold="none (below CRITICAL); the payout waits the new-payee delay as for any new account")},
        detection="LT-07 check-in signals and the FRD-09 delivery time rule (live); Rule 4 GPS_ANOMALY is written "
                  "on the confirming buyer, never on the trucker"),
    "dispute": Persona(
        key="dispute", title="Confirm-then-dispute buyer", threat="buyer griefing: keep the goods and claw the money back",
        accounts=[("dispute-buyer", "buyer")],
        plan=["order 5 times from the support farmer and pay each (mock gateway)",
              "confirm 2 deliveries (the GPS spoofer's trucker delivery when that persona runs, and a self-pickup)",
              "dispute both right after confirming"],
        expect={"dispute-buyer": Expect(
            signals=["DISPUTE_RATE"], tier="LOW",
            challenge="AUTH_040 only when the password is stale; disputes under 10,000 ETB need no step-up",
            hold="none (LOW); each disputed escrow is frozen by the dispute itself")},
        detection="FRD-09 DISPUTE_RATE (live, 30 percent of 5 or more paid orders); the confirm-then-dispute "
                  "pattern itself is FRD-15 (planned)"),
    "noshow": Persona(
        key="noshow", title="No-show at the pickup", threat="a party who commits to a paid order and does not come (ESC-34)",
        accounts=[],
        plan=["a paid trip order accepted by the farmer and the trucker with pickupClassification on",
              "trucker no-show: the farmer says the crops are ready, the trucker never checks in; 48 hours after the "
              "departure the order cron judges the trucker absent (awaiting_transport): NO_SHOW (15, hard) on the "
              "trucker, written plainly",
              "farmer no-show: the trucker checks in at the farm, the farmer never does; the order is cancelled with a "
              "full refund: NO_SHOW on the farmer through the attribution guard (the trucker as the source, one per "
              "trucker and farmer, two per farmer in 30 days)"],
        expect={},
        detection="ESC-34's pickup judgement and ESC-33's reputation hook (built). Not exercisable on dev: "
                  "pickupClassification and reputationPenalties are off there, and the judgement runs 48 hours after "
                  "the trip departs, longer than a run",
        exercisable=False),
    "overdue": Persona(
        key="overdue", title="Overdue delivery and silent buyer",
        threat="a trucker who picks the goods up and never delivers, and a buyer who never confirms a delivery (ESC-36)",
        accounts=[],
        plan=["a paid trip order picked up with dropoffClassification on (the order stamped for T8 and T9)",
              "overdue delivery: the trucker sends no drop-off check-in; 72 hours after the pickup the order cron freezes "
              "the escrow and disputes the order as delivery_overdue: DELIVERY_OVERDUE (25, hard) on the trucker, "
              "written plainly (it rests on the trucker's own missing check-in), and the restriction pending review",
              "silent buyer: the trucker checks in at the drop-off with GPS and the arrival photo, the buyer never "
              "confirms; 48 hours later the order cron confirms the delivery (the normal dispute window and payout "
              "delay follow) or disputes it as silent_buyer: no fraud signal and no reputation event either way"],
        expect={},
        detection="ESC-36's drop-off judgement and ESC-33's reputation hook (built). Not exercisable on dev: "
                  "dropoffClassification and reputationPenalties are off there, and T8 runs 72 hours after the pickup, "
                  "longer than a run",
        exercisable=False),
}
PERSONA_ORDER = ["spammer", "selftrade", "idor", "gps", "dispute", "payer", "noshow", "overdue"]


# ── HTTP ──────────────────────────────────────────────────────────────────

_ctx = ssl.create_default_context()


class _NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: D401
        return None


@dataclass
class Resp:
    status: int
    body: str
    headers: dict = field(default_factory=dict)

    def json(self):
        try:
            return json.loads(self.body) if self.body else {}
        except ValueError:
            return {}

    def code(self) -> str:
        d = self.json()
        err = d.get("error") if isinstance(d, dict) else None
        return (err or {}).get("code", "") if isinstance(err, dict) else ""


class Http:
    def __init__(self, base: str, pace: float):
        self.base = base.rstrip("/")
        self.pace = pace
        self.calls = 0

    def call(self, method, path, token=None, body=None, params=None, headers=None, retries=3) -> Resp:
        url = (path if path.startswith("http") else self.base + path) + ("?" + urlencode(params) if params else "")
        data = json.dumps(body).encode() if body is not None else None
        for attempt in range(retries + 1):
            time.sleep(self.pace)
            self.calls += 1
            req = Request(url, data=data, method=method)
            req.add_header("Content-Type", "application/json")
            req.add_header("X-App-Build", APP_BUILD)
            for k, v in (headers or {}).items():
                req.add_header(k, v)
            if token:
                req.add_header("Authorization", f"Bearer {token}")
            try:
                with urlopen(req, timeout=30, context=_ctx) as r:
                    return Resp(r.status, r.read().decode(), dict(r.headers))
            except HTTPError as e:
                text = e.read().decode() if e.fp else ""
                if e.code == 429 and attempt < retries:
                    time.sleep(5 * (attempt + 1))
                    continue
                return Resp(e.code, text, dict(e.headers or {}))
            except URLError as e:
                if attempt < 1:
                    time.sleep(2)
                    continue
                return Resp(0, str(e.reason))
        return Resp(429, "retries exceeded")

    def location_of(self, url: str) -> Tuple[int, str]:
        """GET without following redirects; returns (status, Location)."""
        time.sleep(self.pace)
        self.calls += 1
        opener = build_opener(_NoRedirect)
        try:
            with opener.open(Request(url, method="GET"), timeout=30) as r:
                return r.status, r.headers.get("Location", "")
        except HTTPError as e:
            return e.code, (e.headers or {}).get("Location", "")
        except URLError as e:
            return 0, str(e.reason)


def guard_base_url(base: str, allow_any: bool) -> Optional[str]:
    if base.rstrip("/") == DEV_BASE_URL or allow_any:
        return None
    return f"refusing {base}: personas write fraud signals and suspensions; pass --allow-any-api for a non-dev stack"


def guard_fayda_host(authorize_url: str) -> Optional[str]:
    host = (urlparse(authorize_url).hostname or "").lower()
    if not host:
        return "the authorize URL has no host"
    if host.endswith("fayda.et") or "esignet" in host:
        return f"the API points at a real Fayda provider ({host}); personas register only against the dev mock"
    return None


# ── Accounts ──────────────────────────────────────────────────────────────


def uid() -> str:
    return str(uuid.uuid4())


def now_iso() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def iso_at(epoch_s: int) -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(epoch_s))


def new_phone(rng: random.Random, taken: set) -> str:
    while True:
        phone = f"{PERSONA_PHONE_PREFIX}{rng.randrange(10000):04d}"
        if phone not in taken and phone not in SHARED_TEST_PHONES:
            taken.add(phone)
            return phone


@dataclass
class Account:
    label: str
    role: str
    persona: str
    phone: str
    password: str
    device_id: str
    name: str = ""
    user_id: str = ""
    token: str = ""
    signed_in_at: float = 0.0

    def headers(self) -> dict:
        return {"X-Device-Id": self.device_id}


class SetupError(Exception):
    pass


class PersonaAbort(Exception):
    pass


# ── Run context ───────────────────────────────────────────────────────────


@dataclass
class Step:
    persona: str
    account: str
    action: str
    expect: str
    got: str
    ok: bool
    kind: str  # abuse, gate, setup, cleanup, info


class Run:
    def __init__(self, http: Http, run_id: str, rng: random.Random, verbose: bool = True):
        self.http = http
        self.run_id = run_id
        self.rng = rng
        self.verbose = verbose
        self.accounts: Dict[str, Account] = {}
        self.phones: set = set()
        self.steps: List[Step] = []
        self.ids: Dict[str, str] = {}
        self.notes: Dict[str, List[str]] = defaultdict(list)
        self.aborted: Dict[str, str] = {}
        self.left_on_dev: List[str] = []
        self.cleanup: List[tuple] = []
        self.signals: Dict[str, List[dict]] = {}
        self.server_scores: Dict[str, dict] = {}
        self.signals_error: Optional[str] = None
        self.support_listing: Optional[dict] = None
        self.started = time.strftime("%Y-%m-%d %H:%M:%SZ", time.gmtime())
        self.device_pairs: Dict[str, str] = {}

    def log(self, text: str):
        if self.verbose:
            print(text, flush=True)

    def step(self, persona, account, action, expect, got, ok, kind="abuse") -> bool:
        self.steps.append(Step(persona, account, action, expect, got, ok, kind))
        mark = "+" if ok else "X"
        self.log(f"  [{mark}] {account}: {action} -- expect {expect}; got {got}")
        return ok

    # Accounts

    def account(self, label: str, role: str, persona: str, device_of: Optional[str] = None) -> Account:
        if label in self.accounts:
            return self.accounts[label]
        device = self.accounts[device_of].device_id if device_of else uid()
        acct = Account(label=label, role=role, persona=persona, phone=new_phone(self.rng, self.phones),
                       password=f"Frd17-{self.rng.randrange(16 ** 8):08x}a1", device_id=device,
                       name=f"frd17 {label}")
        self.register(acct)
        self.accounts[label] = acct
        return acct

    def register(self, a: Account):
        h = self.http
        r = h.call("POST", "/auth/fayda-start", body={"redirectUri": FAYDA_REDIRECT}, headers=a.headers())
        authorize = r.json().get("authorizeUrl") if r.status == 200 else None
        if not authorize:
            raise SetupError(f"fayda-start {r.status}: {r.body[:160]}")
        problem = guard_fayda_host(authorize)
        if problem:
            raise SetupError(problem)
        sep = "&" if "?" in authorize else "?"
        status, location = h.location_of(f"{authorize}{sep}auto=1&phone={quote(a.phone)}")
        query = parse_qs(urlparse(location).query)
        if status not in (301, 302, 303) or "code" not in query:
            raise SetupError(f"mock Fayda authorize {status}: {location[:160] or 'no redirect'}")
        r = h.call("POST", "/auth/fayda-verify", headers=a.headers(), body={
            "authorizationCode": query["code"][0], "state": query["state"][0], "redirectUri": FAYDA_REDIRECT})
        token = r.json().get("verificationToken") if r.status == 200 else None
        if not token:
            raise SetupError(f"fayda-verify {r.status}: {r.body[:160]}")
        body = {"phoneNumber": a.phone, "password": a.password, "role": a.role, "woreda": "Debre Birhan",
                "displayName": a.name, "preferredLanguage": "en", "faydaVerificationToken": token}
        r = h.call("POST", "/auth/register", body=body, headers=a.headers())
        d = r.json()
        tokens = d.get("tokens") or {}
        if r.status not in (200, 201) or not tokens.get("idToken"):
            raise SetupError(f"register {a.label} {r.status}: {r.body[:200]}")
        a.user_id = d.get("userId") or (d.get("user") or {}).get("userId", "")
        a.token = tokens["idToken"]
        a.signed_in_at = time.time()
        self.log(f"  registered {a.label} ({a.role}) {a.phone} user {a.user_id[:8]}")

    def login(self, a: Account):
        r = self.http.call("POST", "/auth/login", body={"phoneNumber": a.phone, "password": a.password},
                           headers=a.headers())
        tokens = r.json().get("tokens") or {}
        if r.status != 200 or not tokens.get("idToken"):
            raise PersonaAbort(f"login {a.label} {r.status}: {r.body[:160]}")
        a.token = tokens["idToken"]
        a.signed_in_at = time.time()

    def fresh(self, a: Account) -> Account:
        if time.time() - a.signed_in_at > FRESH_SECONDS:
            self.login(a)
        return a

    # API helpers

    def get(self, a: Account, path: str, params=None) -> Resp:
        return self.http.call("GET", path, token=a.token, params=params, headers=a.headers())

    def sync(self, a: Account, ops: List[dict]) -> List[dict]:
        r = self.http.call("POST", "/sync", token=a.token, headers=a.headers(), body={
            "syncBatchId": uid(), "clientId": f"{self.run_id}-{a.label}", "operations": ops})
        if r.status != 200:
            return [{"status": "http", "message": f"HTTP {r.status}: {r.body[:200]}"} for _ in ops]
        results = r.json().get("results") or []
        return results + [{"status": "missing", "message": "no result"}] * (len(ops) - len(results))

    def op(self, a: Account, entity: str, entity_id: str, action: str, data: dict, ts: Optional[str] = None) -> dict:
        stamp = int(time.time() * 1000) + len(self.steps)
        return {"operationId": uid(), "idempotencyKey": f"{a.user_id}:{entity}:{entity_id}:{action}:{stamp}",
                "entityType": entity, "entityId": entity_id, "action": action, "data": data,
                "timestamp": ts or now_iso()}

    def sync1(self, a: Account, entity: str, entity_id: str, action: str, data: dict, ts: Optional[str] = None) -> dict:
        return self.sync(a, [self.op(a, entity, entity_id, action, data, ts)])[0]

    def order(self, a: Account, order_id: str) -> dict:
        r = self.get(a, f"/orders/{order_id}")
        d = r.json() if r.status == 200 else {}
        return d.get("order") or d

    def delivery_version(self, a: Account, delivery_id: str) -> int:
        r = self.get(a, f"/deliveries/{delivery_id}")
        d = r.json() if r.status == 200 else {}
        return int((d.get("delivery") or d).get("version") or 1)


def msg(res: dict) -> str:
    return str(res.get("message") or res.get("status") or "")[:160]


def ok(res: dict) -> bool:
    return res.get("status") == "success"


def require(res: dict, what: str) -> dict:
    if not ok(res):
        raise PersonaAbort(f"{what} failed: {msg(res)}")
    return res


# ── Shared fixtures ───────────────────────────────────────────────────────


def fake_photo_ids() -> List[str]:
    return [uid(), uid()]


def create_listing(run: Run, a: Account, crop: str, price: float, quantity: float, place=DEBRE_BIRHAN,
                   crop_id: Optional[str] = None) -> Tuple[str, dict]:
    crop_id = crop_id or uid()
    res = run.sync1(a, "crop_listing", crop_id, "create", {
        "farmerId": a.user_id, "farmerName": a.name, "woreda": "Debre Birhan", "cropType": crop,
        "quantity": quantity, "pricePerKg": price, "latitude": place[0], "longitude": place[1],
        "description": f"{run.run_id} synthetic persona listing, please ignore", "status": "available",
        "photoIds": fake_photo_ids()})
    return crop_id, res


def support_farmer(run: Run) -> Tuple[Account, dict]:
    if run.support_listing:
        return run.accounts["support-farmer"], run.support_listing
    farmer = run.account("support-farmer", "farmer", "support")
    crop_id, res = create_listing(run, farmer, "teff", 42.0, 40.0)
    require(res, "support farmer listing")
    run.support_listing = {"cropId": crop_id, "cropType": "teff", "pricePerKg": 42.0,
                           "latitude": DEBRE_BIRHAN[0], "longitude": DEBRE_BIRHAN[1], "farmer": farmer}
    run.left_on_dev.append(f"support farmer teff listing `{crop_id}` (orders reference it)")
    return farmer, run.support_listing


def place_order(run: Run, buyer: Account, farmer: Account, listing: dict, kg: float = 50.0,
                trip: Optional[dict] = None, trucker: Optional[Account] = None, order_id: Optional[str] = None,
                extra: Optional[dict] = None) -> Tuple[str, dict]:
    order_id = order_id or uid()
    data = {"listingId": listing["cropId"], "buyerId": buyer.user_id, "farmerId": farmer.user_id,
            "buyerName": buyer.name, "farmerName": farmer.name, "cropType": listing["cropType"],
            "quantity": kg, "pricePerUnit": float(listing["pricePerKg"]), "paymentMethod": "telebirr",
            "status": "created"}
    if trip and trucker:
        data.update({"tripId": trip["tripId"], "truckerId": trucker.user_id, "truckerName": trucker.name,
                     "transportCost": kg / 100.0 * float(trip["pricePerQuintal"])})
    data.update(extra or {})
    return order_id, run.sync1(buyer, "order", order_id, "create", data)


def pay(run: Run, buyer: Account, order_id: str, sign_in: bool = True) -> dict:
    if sign_in:
        run.fresh(buyer)
    version = int(run.order(buyer, order_id).get("version") or 1)
    return run.sync1(buyer, "order", order_id, "update",
                     {"status": "payment_submitted", "paymentMethod": "telebirr", "version": version})


def wait_funded(run: Run, buyer: Account, order_id: str) -> dict:
    order = {}
    for _ in range(10):
        order = run.order(buyer, order_id)
        if order.get("status") not in ("payment_submitted", "created", None):
            break
        time.sleep(1)
    return order


def answer(run: Run, a: Account, order_id: str, field_name: str, command: str) -> dict:
    version = int(run.order(a, order_id).get("version") or 1)
    return run.sync1(a, "order", order_id, "update", {field_name: command, "version": version})


def self_pickup(run: Run, buyer: Account, farmer: Account, listing: dict, order_id: str) -> str:
    """The farmer accepts, the buyer opens a self-pickup delivery, the farmer stores the QR nonce and
    the buyer confirms at the farm (GPS within 5 km of the listing). Returns the delivery id."""
    require(answer(run, farmer, order_id, "farmerResponse", "accept"), "farmer accept")
    delivery_id = uid()
    require(run.sync1(buyer, "delivery", delivery_id, "create", {
        "deliveryType": "self_pickup", "orderId": order_id, "listingId": listing["cropId"],
        "farmerId": farmer.user_id, "farmerName": farmer.name, "buyerId": buyer.user_id,
        "buyerName": buyer.name, "status": "pending"}), "self-pickup delivery create")
    nonce = uuid.uuid4().hex
    require(run.sync1(farmer, "delivery", delivery_id, "update", {"qrNonce": nonce, "version": 1}), "farmer QR nonce")
    res = run.sync1(buyer, "delivery", delivery_id, "update", {
        "status": "delivered", "scannedQrNonce": nonce, "version": run.delivery_version(buyer, delivery_id),
        "gpsDropoff": {"latitude": listing["latitude"] + 0.002, "longitude": listing["longitude"] + 0.002}})
    require(res, "self-pickup confirm")
    return delivery_id


def escrow_of(run: Run, a: Account, order_id: str) -> str:
    return run.order(a, order_id).get("escrowId") or f"escrow-order-{order_id}"


# ── Personas ──────────────────────────────────────────────────────────────


def persona_spammer(run: Run):
    p = "spammer"
    a = run.account("spammer", "farmer", p)
    made = []

    def listing(crop, price, expect_code):
        crop_id, res = create_listing(run, a, crop, price, 10.0)
        if expect_code is None:
            if ok(res):
                made.append(crop_id)
            run.step(p, a.label, f"create {crop} @ {price} ETB/kg", "success", msg(res), ok(res))
        else:
            run.step(p, a.label, f"create {crop} @ {price} ETB/kg", f"refused {expect_code}", msg(res),
                     res.get("status") == "error" and expect_code in msg(res))
        return crop_id, res

    listing("teff", 40.0, None)
    time.sleep(2)  # the duplicate rule reads the farmer's listings on an index
    listing("teff", 40.8, "FRAUD_DUPLICATE_LISTING")
    listing("wheat", 22.0, None)
    listing("barley", 26.0, None)
    listing("maize", 18.0, None)
    listing("teff", 999.0, None)  # Rule 2 flags it (PRICE_ANOMALY) only when the type's sample allows
    listing("chickpea", 60.0, "FRAUD_VELOCITY_EXCEEDED")
    time.sleep(3)  # let the score read see this hour's rows
    _, res = create_listing(run, a, "lentils", 70.0, 10.0)
    run.step(p, a.label, "create lentils @ 70.0 ETB/kg (8th create attempt, the tier gate runs first)",
             "refused FRAUD_VELOCITY_EXCEEDED, not FRAUD_ACCOUNT_HELD (below CRITICAL)", msg(res),
             "FRAUD_VELOCITY_EXCEEDED" in msg(res) and "FRAUD_ACCOUNT_HELD" not in msg(res), "gate")
    for crop_id in made:
        run.cleanup.append(("delete-crop", a.label, crop_id))


def persona_selftrade(run: Run):
    p = "selftrade"
    farmer = run.account("self-farmer", "farmer", p)
    buyer = run.account("self-buyer", "buyer", p, device_of="self-farmer")
    crop_id, res = create_listing(run, farmer, "maize", 20.0, 30.0)
    require(res, "self-trade listing")
    run.ids["selftrade-listing"] = crop_id
    listing = {"cropId": crop_id, "cropType": "maize", "pricePerKg": 20.0,
               "latitude": DEBRE_BIRHAN[0], "longitude": DEBRE_BIRHAN[1]}
    order_id, res = place_order(run, buyer, farmer, listing, kg=50.0)
    require(res, "self-trade order")
    run.ids["selftrade-order"] = order_id
    res = pay(run, buyer, order_id)
    run.step(p, buyer.label, "pay own crop (50 kg maize, fresh password)",
             "accepted (LOW: AUTH_040 only for a stale password)", msg(res), ok(res), "gate")
    require(res, "self-trade payment")
    funded = wait_funded(run, buyer, order_id)
    run.step(p, buyer.label, "order funded by the mock gateway", "funded", str(funded.get("status")),
             funded.get("status") == "funded", "setup")
    delivery_id = self_pickup(run, buyer, farmer, listing, order_id)
    run.step(p, buyer.label, "collect at the farm (self-pickup, same device as the seller)", "delivered",
             f"delivery {delivery_id[:8]} delivered", True, "abuse")
    time.sleep(2)
    rating_id = f"{order_id}:{farmer.user_id}"  # rating-api: ratingId is {orderId}:{rateeId}
    res = run.sync1(buyer, "rating", rating_id, "create", {
        "ratingId": rating_id, "orderId": order_id, "raterId": buyer.user_id, "rateeId": farmer.user_id,
        "rateeRole": "farmer", "stars": 5, "comment": "frd17 self rating"})
    run.step(p, buyer.label, "rate the seller (itself) 5 stars", "accepted (nothing checks the pair)", msg(res),
             ok(res), "abuse")
    run.left_on_dev.append(f"self-trade order `{order_id}` delivered, escrow auto-releases after the dispute window and payout delay")


def persona_idor(run: Run):
    p = "idor"
    h_farmer, listing = support_farmer(run)
    victim = run.account("support-buyer", "buyer", "support")
    victim_order, res = place_order(run, victim, h_farmer, listing, kg=20.0)
    require(res, "victim order")
    run.ids["idor-victim-order"] = victim_order
    run.cleanup.append(("cancel-order", victim.label, victim_order))

    buyer = run.account("idor-buyer", "buyer", p)
    own_order, res = place_order(run, buyer, h_farmer, listing, kg=20.0)
    require(res, "prober's own order")
    run.ids["idor-own-order"] = own_order
    run.cleanup.append(("cancel-order", buyer.label, own_order))

    _, res = place_order(run, buyer, h_farmer, listing, kg=20.0, order_id=victim_order)
    run.step(p, buyer.label, "create an order on another buyer's order id", "conflict, nothing echoed",
             f"{res.get('status')} serverData={'yes' if res.get('serverData') else 'no'}",
             res.get("status") == "conflict" and not res.get("serverData"))
    res = run.sync1(buyer, "crop_listing", uid(), "create", {
        "farmerId": buyer.user_id, "farmerName": buyer.name, "woreda": "Debre Birhan", "cropType": "teff",
        "quantity": 5.0, "pricePerKg": 41.0, "status": "available", "photoIds": fake_photo_ids()})
    run.step(p, buyer.label, "create a crop listing as a buyer", "refused CROP_UNAUTHORIZED", msg(res),
             "CROP_UNAUTHORIZED" in msg(res))
    version = int(run.order(buyer, own_order).get("version") or 1)
    res = run.sync1(buyer, "order", own_order, "update", {"pricePerUnit": 1.0, "version": version})
    run.step(p, buyer.label, "rewrite pricePerUnit on its own order to 1 ETB", "refused, immutable", msg(res),
             res.get("status") == "error" and "immutable" in msg(res))
    res = run.sync1(buyer, "order", victim_order, "update", {"status": "cancelled", "version": 1})
    run.step(p, buyer.label, "cancel another buyer's order", "refused ORDER_UNAUTHORIZED (no new row: one per type per hour)",
             msg(res), "ORDER_UNAUTHORIZED" in msg(res))
    r = run.get(buyer, f"/orders/{victim_order}")
    # A 200 here is a role-visibility leak: kind abuse, so it fails the persona (the read writes no signal by decision).
    run.step(p, buyer.label, "read another buyer's order over REST", "refused 403 or 404 (not a signal source, by decision)",
             f"HTTP {r.status}", r.status in (403, 404), "abuse")
    time.sleep(3)  # the score is read eventually consistent at the start of the next batch
    res = pay(run, buyer, own_order)
    run.step(p, buyer.label, "pay its own order (20 kg teff, under 10,000 ETB) with a fresh password", "refused AUTH_041 (HIGH)", msg(res),
             res.get("status") == "error" and msg(res).startswith("AUTH_041"), "gate")

    farmer = run.account("idor-farmer", "farmer", p)
    coffee_id, res = create_listing(run, farmer, "coffee", 300.0, 20.0)
    require(res, "prober farmer's coffee listing")
    time.sleep(2)
    _, res = create_listing(run, farmer, "coffee", 300.0, 20.0)
    run.step(p, farmer.label, "repost its coffee listing", "refused FRAUD_DUPLICATE_LISTING", msg(res),
             "FRAUD_DUPLICATE_LISTING" in msg(res))
    _, res = create_listing(run, farmer, "sesame", 150.0, 5.0, crop_id=listing["cropId"])
    run.step(p, farmer.label, "create a listing on another farmer's listing id", "conflict, nothing echoed",
             f"{res.get('status')} serverData={'yes' if res.get('serverData') else 'no'}",
             res.get("status") == "conflict" and not res.get("serverData"))
    _, res = place_order(run, farmer, h_farmer, listing, kg=10.0, extra={"buyerId": farmer.user_id})
    run.step(p, farmer.label, "create an order as a farmer", "refused ORDER_UNAUTHORIZED", msg(res),
             "ORDER_UNAUTHORIZED" in msg(res))
    res = run.sync1(farmer, "crop_listing", coffee_id, "update", {"farmerId": h_farmer.user_id, "version": 1})
    run.step(p, farmer.label, "move its own listing to another farmer", "refused, immutable", msg(res),
             res.get("status") == "error" and "immutable" in msg(res))
    time.sleep(3)
    _, res = create_listing(run, farmer, "lentils", 55.0, 5.0)
    run.step(p, farmer.label, "list lentils after the probes", "refused FRAUD_ACCOUNT_HELD (CRITICAL)", msg(res),
             "FRAUD_ACCOUNT_HELD" in msg(res), "gate")

    coffee = {"cropId": coffee_id, "cropType": "coffee", "pricePerKg": 300.0}
    held_order, res = place_order(run, victim, farmer, coffee, kg=5.0)
    run.step(p, victim.label, "a clean buyer orders the CRITICAL farmer's coffee",
             "accepted (section 17 known gap: the counterparty's tier is not checked)", msg(res), ok(res), "info")
    if ok(res):
        res = pay(run, victim, held_order)
        funded = wait_funded(run, victim, held_order) if ok(res) else {}
        run.step(p, victim.label, "the clean buyer pays it (5 kg coffee)",
                 "funded; the money is protected by the settlement hold, not by a refusal",
                 f"{msg(res)}, status={funded.get('status')}", funded.get("status") == "funded", "info")
        run.ids["idor-hold-order"] = held_order
        run.ids["idor-hold-escrow"] = escrow_of(run, victim, held_order)
        due = funded.get("farmerDeadlineAt")
        run.ids["idor-hold-deadline"] = due or ""
        run.notes[p].append(f"hold target: order `{held_order}`, escrow `{run.ids['idor-hold-escrow']}`, "
                            f"farmer deadline {due}; after it the order cron cancels it and the auto-refund sweep "
                            "should write `held` (holdReason risk_critical), dev has FRAUD_HOLD_ENABLED on")
        run.left_on_dev.append(f"funded order `{held_order}` on the CRITICAL farmer's listing (hold target, left on purpose)")


def gps_trip_delivery(run: Run, buyer: Account, persona_buyer: str) -> Optional[Tuple[str, str]]:
    """The GPS spoofer carries [buyer]'s paid order. Returns (order id, delivery id) once the buyer
    confirmed the drop-off, else None."""
    p = "gps"
    trucker = run.account("gps-trucker", "trucker", p)
    h_farmer, listing = support_farmer(run)
    trip_id = uid()
    departure = time.strftime("%Y-%m-%dT06:00:00Z", time.gmtime(time.time() + 5 * 86400))
    require(run.sync1(trucker, "trip", trip_id, "create", {
        "truckerId": trucker.user_id, "truckerName": trucker.name, "fromWoreda": "Debre Birhan",
        "toWoreda": "Dire Dawa", "capacity": 20.0, "pricePerQuintal": 100.0, "departureDate": departure,
        "status": "open"}), "trip create")
    trip = run.get(trucker, f"/trips/{trip_id}").json()
    trip = trip.get("trip") or trip
    trip.setdefault("tripId", trip_id)
    trip.setdefault("pricePerQuintal", 100.0)
    order_id, res = place_order(run, buyer, h_farmer, listing, kg=50.0, trip=trip, trucker=trucker)
    require(res, "trip order")
    require(pay(run, buyer, order_id), "trip order payment")
    funded = wait_funded(run, buyer, order_id)
    if funded.get("status") != "funded":
        raise PersonaAbort(f"trip order not funded: {funded.get('status')}")
    require(answer(run, h_farmer, order_id, "farmerResponse", "accept"), "farmer accept")
    require(answer(run, trucker, order_id, "truckerResponse", "accept"), "trucker accept")
    delivery_id = uid()
    require(run.sync1(trucker, "delivery", delivery_id, "create", {
        "tripId": trip_id, "orderId": order_id, "listingId": listing["cropId"], "farmerId": h_farmer.user_id,
        "farmerName": h_farmer.name, "truckerId": trucker.user_id, "truckerName": trucker.name,
        "buyerId": buyer.user_id, "buyerName": buyer.name, "qrToken": f"qr-{delivery_id[:8]}",
        "status": "pending"}), "delivery create")
    nonce = uuid.uuid4().hex
    require(run.sync1(h_farmer, "delivery", delivery_id, "update", {"qrNonce": nonce, "version": 1}), "QR nonce")
    pickup = (listing["latitude"] + 0.001, listing["longitude"] + 0.001)
    res = run.sync1(trucker, "delivery", delivery_id, "update", {
        "status": "pickedUp", "scannedQrNonce": nonce, "version": 1,
        "gpsPickup": {"latitude": pickup[0], "longitude": pickup[1]}})
    run.step(p, trucker.label, "pick up at the farm with the farmer's QR", "success", msg(res), ok(res), "setup")
    require(res, "pickup")

    t0 = int(time.time())
    fixes = [(t0 - 30, pickup, True), (t0, (pickup[0] + 0.45, pickup[1]), False)]
    ops = []
    for at, (lat, lng), mocked in fixes:
        ops.append(run.op(trucker, "location_ping", f"{trip_id}#{at * 1000}", "create", {
            "tripId": trip_id, "kind": "fix", "source": "manual", "capturedAt": iso_at(at),
            "latitude": lat, "longitude": lng, "accuracyM": 10, "mocked": mocked}))
    results = run.sync(trucker, ops)
    run.step(p, trucker.label, "check-in flagged mocked by the phone", "accepted (flag only)", msg(results[0]),
             ok(results[0]))
    run.step(p, trucker.label, "check-in 50 km away 30 s later (6,000 km/h)", "accepted (flag only)",
             msg(results[1]), ok(results[1]))

    lat = trip.get("destinationLatitude")
    lng = trip.get("destinationLongitude")
    dropoff = (float(lat), float(lng)) if lat is not None and lng is not None else DIRE_DAWA
    time.sleep(2)
    res = run.sync1(buyer, "delivery", delivery_id, "update", {
        "status": "delivered", "scannedQrNonce": nonce, "version": run.delivery_version(buyer, delivery_id),
        "gpsDropoff": {"latitude": dropoff[0], "longitude": dropoff[1]}})
    run.step(persona_buyer, buyer.label, "confirm the drop-off about 250 km away minutes after the pickup",
             "accepted (the delivery time rule flags the trucker)", msg(res), ok(res),
             "abuse" if persona_buyer == "gps" else "setup")
    require(res, "drop-off confirm")
    run.left_on_dev.append(f"GPS spoofer trip `{trip_id}` with delivered delivery `{delivery_id}`")
    return order_id, delivery_id


def persona_gps_and_dispute(run: Run, with_gps: bool, with_dispute: bool):
    if with_dispute:
        buyer = run.account("dispute-buyer", "buyer", "dispute")
    else:
        buyer = run.account("support-buyer", "buyer", "support")
    h_farmer, listing = support_farmer(run)
    confirmed: List[str] = []

    if with_gps:
        try:
            done = gps_trip_delivery(run, buyer, "dispute" if with_dispute else "gps")
            if done:
                confirmed.append(done[0])
        except PersonaAbort as e:
            run.aborted["gps"] = str(e)
            run.log(f"  [!] gps aborted: {e}")
    if not with_dispute:
        return

    p = "dispute"
    orders = []
    for i in range(5 - len(confirmed)):
        order_id, res = place_order(run, buyer, h_farmer, listing, kg=20.0)
        require(res, f"order {i + 1}")
        res = pay(run, buyer, order_id)
        run.step(p, buyer.label, f"pay order {len(confirmed) + i + 1} (20 kg teff, fresh password)",
                 "accepted (LOW)", msg(res), ok(res), "setup")
        require(res, "payment")
        if wait_funded(run, buyer, order_id).get("status") != "funded":
            raise PersonaAbort(f"order {order_id} not funded")
        orders.append(order_id)
    need = 2 - len(confirmed)
    for order_id in orders[:need]:
        self_pickup(run, buyer, h_farmer, listing, order_id)
        run.step(p, buyer.label, f"confirm self-pickup of order {order_id[:8]}", "delivered", "delivered", True, "setup")
        confirmed.append(order_id)
    for order_id in orders[need:]:
        run.cleanup.append(("decline-order", "support-farmer", order_id))

    time.sleep(2)
    for n, order_id in enumerate(confirmed, start=1):
        escrow_id = escrow_of(run, buyer, order_id)
        r = run.http.call("POST", "/escrow/dispute", token=run.fresh(buyer).token, headers=buyer.headers(), body={
            "escrowId": escrow_id, "disputedBy": buyer.user_id, "reason": "not_delivered",
            "description": "frd17 persona: confirmed, then claims the goods never came.",
            # escrow-protocol: {actor}:{entityId}:{action}:{timestamp}, exactly four parts
            "idempotencyKey": f"{buyer.user_id}:{escrow_id}:dispute:{int(time.time() * 1000)}"})
        run.step(p, buyer.label, f"dispute confirmed delivery {n} of 2 (escrow {escrow_id[-8:]})",
                 "accepted (disputes are not tier-gated)", f"HTTP {r.status} {r.code()}", r.status == 200)
        run.left_on_dev.append(f"disputed escrow `{escrow_id}` in the admin dispute queue")
        time.sleep(1)


def fayda_stepup(run: Run, a: Account) -> Tuple[bool, str]:
    """The Fayda re-verification a payment step-up asks for (AUTH_041): POST /user/fayda-start, the
    mock's authorize URL with `auto=1&fin=hint` (the identity its login_hint names, the account's own),
    POST /user/fayda-link. Mock only: a real Fayda host is refused."""
    r = run.http.call("POST", "/user/fayda-start", token=a.token, headers=a.headers(),
                      body={"redirectUri": FAYDA_LINK_REDIRECT})
    authorize = r.json().get("authorizeUrl") if r.status == 200 else None
    if not authorize:
        return False, f"user/fayda-start HTTP {r.status} {r.code()}"
    problem = guard_fayda_host(authorize)
    if problem:
        return False, problem
    sep = "&" if "?" in authorize else "?"
    status, location = run.http.location_of(f"{authorize}{sep}auto=1&fin=hint")
    query = parse_qs(urlparse(location).query)
    if "code" not in query:
        return False, f"mock authorize {status}: {location[:100]}"
    r = run.http.call("POST", "/user/fayda-link", token=a.token, headers=a.headers(), body={
        "authorizationCode": query["code"][0], "state": query["state"][0], "redirectUri": FAYDA_LINK_REDIRECT})
    return r.status == 200, f"HTTP {r.status} {r.code() or 'faydaVerified=' + str(r.json().get('faydaVerified'))}"


def stale_password_phase(run: Run):
    """AUTH_040 needs a sign-in older than STEP_UP_MAX_AGE_SECONDS (300). Runs last, waiting until the
    probed accounts' last sign-in is that old, and never signs them in again."""
    probes = [a for a in (run.accounts.get("idor-buyer"), run.accounts.get("self-buyer")) if a]
    if not probes:
        return
    wait = max(0.0, max(STALE_SECONDS - (time.time() - a.signed_in_at) for a in probes))
    run.log(f"  waiting {int(wait)} s until the sign-ins are over 5 minutes old")
    time.sleep(wait)
    buyer = run.accounts.get("idor-buyer")
    if buyer and "idor" not in run.aborted and run.ids.get("idor-own-order"):
        good, detail = fayda_stepup(run, buyer)
        run.step("idor", buyer.label, "Fayda step-up through the mock (its own identity)", "linked", detail, good,
                 "setup")
        res = pay(run, buyer, run.ids["idor-own-order"], sign_in=False)
        run.step("idor", buyer.label, "pay again with the 5-minute-old sign-in", "refused AUTH_040 (HIGH: password next)",
                 msg(res), res.get("status") == "error" and msg(res).startswith("AUTH_040"), "gate")
    buyer = run.accounts.get("self-buyer")
    listing = run.ids.get("selftrade-listing")
    if buyer and listing and "selftrade" not in run.aborted:
        farmer = run.accounts["self-farmer"]
        order_id, res = place_order(run, buyer, farmer, {"cropId": listing, "cropType": "maize", "pricePerKg": 20.0},
                                    kg=20.0)
        if ok(res):
            run.cleanup.append(("cancel-order", buyer.label, order_id))
            res = pay(run, buyer, order_id, sign_in=False)
        run.step("selftrade", buyer.label, "pay a second order with the 5-minute-old sign-in",
                 "refused AUTH_040 (LOW, stale password)", msg(res),
                 res.get("status") == "error" and msg(res).startswith("AUTH_040"), "gate")


def price_sample_note(run: Run):
    """Why the 999 ETB/kg teff listing was or was not flagged: the price rule (Rule 2) compares a new
    price with the mean and sample deviation of the type's 50 newest listings. A buyer sees every
    listing, so the run reads the browse API as one (an approximation: the rule reads its own index)."""
    buyer = next((a for a in run.accounts.values() if a.role == "buyer"), None)
    if not buyer:
        return
    r = run.get(buyer, "/crops", params={"cropType": "teff", "limit": "50"})
    crops = r.json().get("crops", []) if r.status == 200 else []
    prices = [float(c["pricePerKg"]) for c in crops if c.get("pricePerKg") is not None
              and c.get("farmerId") != run.accounts["spammer"].user_id]
    if len(prices) < 2:
        run.notes["spammer"].append(f"teff price sample from the browse API: {len(prices)} listings (HTTP {r.status})")
        return
    mean = sum(prices) / len(prices)
    sd = (sum((x - mean) ** 2 for x in prices) / (len(prices) - 1)) ** 0.5
    run.notes["spammer"].append(
        f"teff price sample a buyer sees (other farmers, up to 50): n={len(prices)}, mean {mean:.1f}, "
        f"sample deviation {sd:.1f}, max {max(prices):.1f} ETB/kg; 999 is {'above' if 999 > mean + 3 * sd else 'within'} "
        f"mean + 3 sigma ({mean + 3 * sd:.1f})")


def persona_payer(run: Run):
    p = "payer"
    r = run.http.call("POST", "/webhooks/telebirr", body={})
    exists = r.status not in (403, 404)
    run.step(p, "-", "POST an unsigned empty notification to /webhooks/telebirr",
             "absent on dev (mock gateway, enableTelebirrLive off): 403 or 404; refused by the signature check where deployed",
             f"HTTP {r.status} {r.code() or r.body[:60]}", True, "info")
    run.notes[p].append(
        "the payer check runs only on a verified Telebirr notification; dev funds orders through the mock gateway, "
        "which has no payer wallet, so no request can produce TELEBIRR_PAYER_MISMATCH or TELEBIRR_PAYER_REFUSED there"
        + ("" if not exists else f" (the route answered HTTP {r.status}, so it is deployed: run against a live-gateway stack)"))


def persona_overdue(run: Run):
    p = "overdue"
    run.notes[p].append(
        "the drop-off judgement runs only with dropoffClassification and reputationPenalties on and 72 hours after the "
        "pickup; the replica checks of --self-test cover the DELIVERY_OVERDUE weight, the shared reliability cap and "
        "that the silent buyer writes no signal")


def persona_noshow(run: Run):
    p = "noshow"
    run.notes[p].append(
        "the pickup judgement runs only with pickupClassification and reputationPenalties on and 48 hours after the "
        "trip departs; the replica checks of --self-test cover the NO_SHOW weights, the shared reliability cap and "
        "the attribution guard's bound")


# ── Observation through the admin API ─────────────────────────────────────


def signal_bucket(signal_id: str, created_at: str) -> str:
    """UTC hour of a row: from its signalKey ('{hour}#{type}', inside the base64url id
    'userId|signalKey'), else from createdAt."""
    try:
        raw = base64.urlsafe_b64decode(signal_id + "=" * (-len(signal_id) % 4)).decode()
        key = raw.split("|", 1)[1]
        if re.match(r"^\d{4}-\d{2}-\d{2}T\d{2}", key):
            return key[:13]
    except (ValueError, IndexError, UnicodeDecodeError):
        pass
    return (created_at or "")[:13]


def admin_token(args) -> Tuple[Optional[str], str]:
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    try:
        from e2e_admin import cognito_login  # noqa: E402
    except ImportError as e:
        return None, f"cannot import e2e_admin: {e}"
    token, err = cognito_login(args.region, args.admin_client_id, args.admin_phone, args.admin_password, "id")
    return token, err or ""


SIGNAL_STATUSES = ("pending", "reviewed")
ADMIN_PAGE = 50  # FRD-07: the index pages hold at most 50 rows


def read_signals(http, token: str, user_ids: set, max_pages: int) -> Tuple[Dict[str, List[dict]], Dict[str, dict], str]:
    """The persona accounts' signal rows and the server's score of each (FRD-07). GET /admin/fraud-signals lists
    one status at a time (`pending` by default), so both statuses are read: a row an admin rejected is `reviewed`
    and still counts (section 14.2). Each page's `users[]` carries the score and tier of its users. Returns
    (rows by user, users[] entry by user, problem); a page cap that leaves rows unread is a problem."""
    found: Dict[str, List[dict]] = defaultdict(list)
    scores: Dict[str, dict] = {}
    problems = []
    for status in SIGNAL_STATUSES:
        next_token = None
        for _ in range(max_pages):
            params = {"status": status, "limit": str(ADMIN_PAGE)}
            if next_token:
                params["nextToken"] = next_token
            r = http.call("GET", "/admin/fraud-signals", token=token, params=params)
            if r.status != 200:
                return found, scores, f"GET /admin/fraud-signals?status={status} HTTP {r.status}: {r.body[:120]}"
            d = r.json()
            for row in d.get("signals") or []:
                if row.get("userId") in user_ids:
                    row["bucket"] = signal_bucket(row.get("signalId", ""), row.get("createdAt", ""))
                    found[row["userId"]].append(row)
            for user in d.get("users") or []:
                if user.get("userId") in user_ids:
                    scores[user["userId"]] = user
            next_token = d.get("nextToken")
            if not next_token:
                break
        else:
            problems.append(f"{status}: stopped after {max_pages} pages, rows beyond them were not read")
    return found, scores, "; ".join(problems)


def read_held_escrows(http, token: str, watch: set, max_pages: int) -> Tuple[List[dict], str]:
    """Held escrows naming a watched user or escrow id, from the indexed held queue (FRD-07,
    GET /admin/escrows/held)."""
    held = []
    next_token = None
    for _ in range(max_pages):
        params = {"limit": str(ADMIN_PAGE)}
        if next_token:
            params["nextToken"] = next_token
        r = http.call("GET", "/admin/escrows/held", token=token, params=params)
        if r.status != 200:
            return held, f"GET /admin/escrows/held HTTP {r.status}: {r.body[:120]}"
        d = r.json()
        for row in d.get("escrows") or []:
            if watch & {str(v) for v in row.values() if isinstance(v, str)}:
                held.append(row)
        next_token = d.get("nextToken")
        if not next_token:
            return held, ""
    return held, f"stopped after {max_pages} pages of held escrows"


def parse_iso(text: Optional[str]) -> Optional[float]:
    """Epoch seconds of an ISO 8601 UTC time (any fraction digits), None when it does not parse."""
    try:
        return float(calendar.timegm(time.strptime((text or "")[:19], "%Y-%m-%dT%H:%M:%S")))
    except ValueError:
        return None


def hold_targets(state: dict, extra: Optional[List[str]]) -> List[dict]:
    """The escrows a run expects held: the state's hold target, plus `ESCROW_ID=DEADLINE` from --hold-target
    (state files written before the deadline was kept)."""
    targets: Dict[str, Optional[str]] = {}
    ids = state.get("ids") or {}
    if ids.get("idor-hold-escrow"):
        targets[ids["idor-hold-escrow"]] = ids.get("idor-hold-deadline") or None
    for item in extra or []:
        escrow_id, _, deadline = item.partition("=")
        if escrow_id:
            targets[escrow_id.strip()] = deadline.strip() or targets.get(escrow_id.strip())
    return [{"escrowId": k, "deadline": v} for k, v in targets.items()]


def evaluate_holds(targets: List[dict], held: Dict[str, dict], now: float, grace_hours: float) -> Tuple[List[str], int]:
    """One line per hold target and the --recheck exit code: 1 when a target is past its farmer deadline plus
    the grace (the order cron cancels the order, then the hourly auto-refund sweep meets the gate) and is not
    held, else 0."""
    lines, code = [], 0
    for t in targets:
        escrow_id, deadline = t["escrowId"], t.get("deadline")
        due = parse_iso(deadline)
        if escrow_id in held:
            row = held[escrow_id]
            lines.append(f"- `{escrow_id}`: HELD, holdReason {row.get('holdReason')}, heldFor {row.get('heldFor')}")
        elif due is None:
            lines.append(f"- `{escrow_id}`: not held; deadline unknown, pass --hold-target {escrow_id}=<farmerDeadlineAt>")
        elif now < due + grace_hours * 3600:
            lines.append(f"- `{escrow_id}`: not held yet, due after {deadline} plus {grace_hours:g} h")
        else:
            lines.append(f"- `{escrow_id}`: NOT HELD although its deadline {deadline} plus {grace_hours:g} h has passed")
            code = 1
    return lines, code


# ── Verdicts and report ───────────────────────────────────────────────────


def account_outcome(run: Run, label: str, exp: Expect) -> dict:
    acct = run.accounts.get(label)
    rows = run.signals.get(acct.user_id, []) if acct else []
    types = sorted({r.get("signalType", "") for r in rows})
    replica_score, replica_tier = risk_score(rows)
    score, tier, source = server_tier(run, acct.user_id if acct else "", rows, replica_score, replica_tier)
    missing = [t for t in exp.signals if t not in types]
    unexpected = [t for t in types if t not in exp.signals + exp.optional]
    wrongly_present = [t for t in exp.absent if t in types]
    return {"types": types, "score": score, "tier": tier, "source": source, "replica_score": replica_score,
            "replica_tier": replica_tier, "mismatch": source == "server" and (score, tier) != (replica_score, replica_tier),
            "missing": missing, "unexpected": unexpected, "wrongly_present": wrongly_present, "rows": rows}


def server_tier(run: Run, user_id: str, rows: List[dict], replica_score: int, replica_tier: str) -> Tuple[int, str, str]:
    """(score, tier, source): the server's (FRD-07 `users[]`) when it computed one; a user with no signal row is
    in no page, so LOW; otherwise (a server before FRD-07, or a score it could not read) the replica."""
    user = run.server_scores.get(user_id)
    if user and user.get("scoreAvailable", True) and user.get("riskTier"):
        return int(user.get("riskScore") or 0), str(user["riskTier"]).upper(), "server"
    if not user and not rows and run.server_scores:
        return 0, "LOW", "server"
    return replica_score, replica_tier, "replica"


def persona_verdict(run: Run, persona: Persona) -> Tuple[str, bool]:
    """(verdict text, matched expectation)."""
    if not persona.exercisable:
        return "NOT EXERCISABLE ON DEV", True
    if persona.key in run.aborted:
        return f"INCOMPLETE: {run.aborted[persona.key]}", False
    steps = [s for s in run.steps if s.persona == persona.key]
    failed = [s for s in steps if not s.ok and s.kind != "info"]
    if run.signals_error:
        return f"UNOBSERVED: {run.signals_error}", False
    detected = False
    matched = not failed
    expects_any = any(e.signals for e in persona.expect.values())
    disagree = ""
    for label, exp in persona.expect.items():
        out = account_outcome(run, label, exp)
        detected = detected or any(t in out["types"] for t in exp.signals)
        if out["missing"] or out["wrongly_present"] or out["tier"] != exp.tier:
            matched = False
        if out["mismatch"]:
            disagree = "; the replica disagrees with the server's score"
    enforced = any(s.ok and s.kind == "gate" and ("refused" in s.expect) for s in steps) or any(
        s.ok and s.kind == "abuse" and "refused" in s.expect for s in steps)
    if not expects_any:
        any_signal = any(account_outcome(run, l, e)["types"] for l, e in persona.expect.items())
        return ("NOT DETECTED (no rule today)" if not any_signal else "UNEXPECTED SIGNAL") + disagree, \
            matched and not any_signal
    if detected and enforced:
        return "DETECTED AND ENFORCED" + disagree, matched
    if detected:
        return "DETECTED, NOT ENFORCED (flag only)" + disagree, matched
    return "NOT DETECTED" + disagree, False


def run_exit_code(verdicts: List[Tuple[str, str, bool]]) -> int:
    """0 when every persona matched its expectation, else 1 (2 is returned earlier, for setup failures)."""
    return 0 if all(matched for _, _, matched in verdicts) else 1


def md_escape(text: str) -> str:
    return str(text).replace("|", "\\|").replace("\n", " ")


def render_report(run: Run, selected: List[str]) -> str:
    out = []
    w = out.append
    w(f"# Adversarial personas: dev stack, {run.started[:10]} (run `{run.run_id}`)\n")
    w(f"FRD-17. Script `scripts/e2e_fraud_personas.py`, API `{run.http.base}`, started {run.started}, "
      f"{run.http.calls} HTTP calls. Expectations come from `specs/architecture/fraud-detection-architecture.md` "
      f"(sections 2, 14 to 18) and the server code on `main` at the time of the run.\n")
    w("Observed signals are the rows `GET /admin/fraud-signals` returned for each persona account, read for both "
      "statuses (`pending` and `reviewed`). The score and tier are the server's, from the same responses' `users[]` "
      "(FRD-07); a replica of section 14.2 recomputes them from the rows as a cross-check, and a disagreement is "
      "flagged (a server without `users[]` leaves the replica's numbers). The gates the persona met cross-check the "
      "tier: a payment with a fresh password passes at LOW and MEDIUM, meets `AUTH_041` at HIGH and "
      "`FRAUD_ACCOUNT_HELD` at CRITICAL; a create meets `FRAUD_ACCOUNT_HELD` only at CRITICAL.\n")
    if run.signals_error:
        w(f"Signal read: {run.signals_error}\n")

    w("## Summary\n")
    w("| Persona | Accounts | Expected signals | Observed signals | Tier exp / obs | Challenge (expected) | Hold (expected) | Verdict |")
    w("|---|---|---|---|---|---|---|---|")
    all_matched = True
    for key in selected:
        persona = PERSONAS[key]
        verdict, matched = persona_verdict(run, persona)
        all_matched = all_matched and matched
        if not persona.expect:
            w(f"| {persona.title} | none | {md_escape(persona.detection)} | n/a | n/a | n/a | n/a | {verdict} |")
            continue
        for label, exp in persona.expect.items():
            o = account_outcome(run, label, exp)
            exp_sig = ", ".join(exp.signals + [f"({t})" for t in exp.optional]) or "none"
            obs = ", ".join(o["types"]) or "none"
            w(f"| {persona.title} | {label} | {exp_sig} | {obs} | {exp.tier} / {o['tier']} ({o['score']}, {o['source']}) | "
              f"{md_escape(exp.challenge)} | {md_escape(exp.hold)} | {verdict}{'' if matched else ' (deviates)'} |")
    w("\nParenthesised signals are data dependent (the price rule needs 5 or more recent listings of the type with a spread).\n")

    for key in selected:
        persona = PERSONAS[key]
        verdict, _ = persona_verdict(run, persona)
        w(f"## {persona.title} (`{key}`)\n")
        w(f"Threat: {persona.threat}. What should catch it today: {persona.detection}.\n")
        w("Plan:\n")
        for line in persona.plan:
            w(f"- {line}")
        w("")
        steps = [s for s in run.steps if s.persona == key]
        if steps:
            w("| Account | Action | Expected | Got | OK |")
            w("|---|---|---|---|---|")
            for s in steps:
                w(f"| {s.account} | {md_escape(s.action)} | {md_escape(s.expect)} | {md_escape(s.got)} | "
                  f"{'yes' if s.ok else 'NO'} |")
            w("")
        for label, exp in persona.expect.items():
            o = account_outcome(run, label, exp)
            acct = run.accounts.get(label)
            w(f"**{label}** (`{acct.user_id if acct else 'not registered'}`): signals "
              f"{', '.join(o['types']) or 'none'}; score {o['score']}, tier {o['tier']} from the {o['source']} "
              f"(expected {exp.tier}; replica {o['replica_score']}, {o['replica_tier']}"
              f"{', DISAGREES' if o['mismatch'] else ''}).")
            for t in exp.signals + exp.optional:
                rule, producer = RULES.get(t, ("?", "?"))
                seen = "seen" if t in o["types"] else ("not seen" if t in exp.signals else "not seen (data dependent)")
                w(f"- `{t}` (weight {WEIGHTS.get(t, DEFAULT_WEIGHT)}, {rule}): {producer}. {seen}.")
            for t in exp.absent:
                rule, producer = RULES.get(t, ("?", "?"))
                w(f"- `{t}` must not be on this account ({rule}: {producer}): "
                  f"{'PRESENT' if t in o['wrongly_present'] else 'absent'}.")
            if o["unexpected"]:
                w(f"- Also written: {', '.join(o['unexpected'])}.")
            w("")
        for note in run.notes.get(key, []):
            w(f"Note: {note}.\n")
        w(f"Verdict: **{verdict}**.\n")

    support = [a for a in run.accounts.values() if a.persona == "support"]
    if support:
        w("## Support accounts (counterparties, should stay clean)\n")
        for a in support:
            rows = run.signals.get(a.user_id, [])
            score, tier, _ = server_tier(run, a.user_id, rows, *risk_score(rows))
            w(f"- {a.label} ({a.role}, `{a.user_id}`): signals "
              f"{', '.join(sorted({r.get('signalType') for r in rows})) or 'none'}, tier {tier}.")
        w("")

    w("## Gates and holds: where they live\n")
    for code, where in GATES.items():
        w(f"- `{code}`: {where}.")
    w("")
    if run.left_on_dev:
        w("## Left on dev\n")
        for line in run.left_on_dev:
            w(f"- {line}")
        w("")
    w("## Accounts\n")
    w("| Label | Role | User id |")
    w("|---|---|---|")
    for a in run.accounts.values():
        w(f"| {a.label} | {a.role} | `{a.user_id}` |")
    w("\nPhones (from +25190017XXXX) and the random passwords are kept only in the `--state-file` when one was given.\n")
    w(f"Overall: {'every persona matched its expectation' if all_matched else 'at least one persona deviated'}.\n")
    return "\n".join(out)


# ── Plan, self-test, main ─────────────────────────────────────────────────


def accounts_for(selected: List[str]) -> List[Tuple[str, str]]:
    labels: List[Tuple[str, str]] = []
    for key in selected:
        labels.extend(PERSONAS[key].accounts)
    if any(k in selected for k in ("idor", "gps", "dispute")):
        labels.append(("support-farmer", "farmer"))
    if "idor" in selected or ("gps" in selected and "dispute" not in selected):
        labels.append(("support-buyer", "buyer"))
    return labels


def print_plan(selected: List[str]):
    print("Adversarial personas: plan (no network)\n")
    for key in selected:
        persona = PERSONAS[key]
        print(f"{persona.title} [{key}]  ({persona.detection})")
        for line in persona.plan:
            print(f"  - {line}")
        for label, exp in persona.expect.items():
            print(f"  expect {label}: signals {exp.signals or 'none'} optional {exp.optional or '-'} "
                  f"tier {exp.tier}; challenge: {exp.challenge}; hold: {exp.hold}")
        print()
    accts = accounts_for(selected)
    print(f"Accounts to register ({len(accts)}; the register route allows 10 per IP per hour):")
    for label, role in accts:
        print(f"  {label} ({role})")


def self_test() -> int:
    failures = []

    def check(cond, what):
        if not cond:
            failures.append(what)

    # Persona catalogue
    check(sorted(PERSONA_ORDER) == sorted(PERSONAS), "PERSONA_ORDER lists every persona once")
    labels = [l for p in PERSONAS.values() for l, _ in p.accounts]
    check(len(labels) == len(set(labels)), "account labels are unique")
    for p in PERSONAS.values():
        check(set(p.expect) == {l for l, _ in p.accounts}, f"{p.key}: one expectation per account")
        for l, role in p.accounts:
            check(role in ("farmer", "trucker", "buyer"), f"{p.key}/{l}: role {role}")
        for label, exp in p.expect.items():
            for t in exp.signals + exp.optional + exp.absent:
                check(t in WEIGHTS, f"{p.key}/{label}: unknown signal type {t}")
                check(t in RULES, f"{p.key}/{label}: no rule mapping for {t}")
            check(exp.tier in ("LOW", "MEDIUM", "HIGH", "CRITICAL"), f"{p.key}/{label}: tier {exp.tier}")
            low = risk_score(expected_rows(exp.signals))[1]
            high = risk_score(expected_rows(exp.signals + exp.optional))[1]
            check(low == exp.tier and high == exp.tier,
                  f"{p.key}/{label}: expected tier {exp.tier} but the expected signals give {low}/{high}")
            check(bool(exp.challenge) and bool(exp.hold), f"{p.key}/{label}: challenge and hold written down")
        check(bool(p.plan) and bool(p.detection), f"{p.key}: plan and detection written down")
    check(not PERSONAS["payer"].exercisable, "payer is marked not exercisable on dev")
    check(not PERSONAS["noshow"].exercisable, "noshow is marked not exercisable on dev")
    check(not PERSONAS["overdue"].exercisable, "overdue is marked not exercisable on dev")
    check(len(accounts_for(PERSONA_ORDER)) <= 10, "a full run stays under the 10 registrations per IP per hour")

    # Score replica (fraud-detection-architecture.md 14.2, 16)
    for score, tier in ((0, "LOW"), (29, "LOW"), (30, "MEDIUM"), (59, "MEDIUM"), (60, "HIGH"), (99, "HIGH"),
                        (100, "CRITICAL")):
        check(tier_of(score) == tier, f"tier_of({score}) == {tier}")
    burst = [{"signalType": "FRAUD_VELOCITY_EXCEEDED", "bucket": "2026-11-07T10", "createdAt": f"2026-11-07T10:0{i}:00Z"}
             for i in range(7)]
    check(risk_score(burst)[0] == 15, "a burst in one hour counts once")
    two_hours = burst[:1] + [dict(burst[1], bucket="2026-11-07T11", createdAt="2026-11-07T11:00:00Z")]
    check(risk_score(two_hours)[0] == 30, "two hours count twice")
    same_entity = [{"signalType": "ROLE_FORBIDDEN", "entityId": "o1", "bucket": f"2026-11-07T1{i}",
                    "createdAt": f"2026-11-07T1{i}:00:00Z"} for i in range(3)]
    check(risk_score(same_entity)[0] == 25, "a per-entity type counts once per entity")
    many = [dict(r, entityId=f"o{i}") for i, r in enumerate(same_entity)]
    check(risk_score(many)[0] == 50, "a capped type stops at 50")
    idor_hours = [{"signalType": "IDOR_CREATE_CONFLICT", "entityId": f"x{i}", "bucket": f"2026-11-07T1{i}",
                   "createdAt": f"2026-11-07T1{i}:00:00Z"} for i in range(3)]
    check(risk_score(idor_hours) == (120, "CRITICAL"), "IDOR probes in three hours are CRITICAL")
    approved = [dict(same_entity[0], resolution="approve", createdAt="2026-11-07T19:00:00Z")] + same_entity
    check(risk_score(approved)[0] == 0, "an approved row clears its entity")
    check(risk_score(expected_rows(["TELEBIRR_PAYER_MISMATCH"])) == (0, "LOW"), "a payer mismatch scores 0")
    # ESC-34 / FUP-204: NO_SHOW is 15 and hard, once per order, sharing a cap of 50 with DELIVERY_OVERDUE.
    check(risk_score(expected_rows(["NO_SHOW"])) == (15, "LOW"), "a no-show is 15, LOW")
    check(risk_score(expected_rows(["NO_SHOW"] * 4 + ["DELIVERY_OVERDUE"] * 2))[0] == 50,
          "no-shows and overdue deliveries stop at 50 together")
    check(risk_score(expected_rows(["NO_SHOW"] * ATTRIBUTED_PER_TARGET_CAP)) == (30, "MEDIUM"),
          "other users' check-ins alone put at most 30 on a farmer")
    # ESC-36 / FUP-204: DELIVERY_OVERDUE is 25 and hard, once per order, written plainly (T8 rests on the trucker's own
    # missing drop-off check-in), in the reliability cap; the silent buyer (T9) writes no signal at all.
    check(risk_score(expected_rows(["DELIVERY_OVERDUE"])) == (25, "LOW"), "an overdue delivery is 25, LOW")
    check(risk_score(expected_rows(["DELIVERY_OVERDUE"] * 2)) == (50, "MEDIUM"), "two overdue deliveries are 50, MEDIUM")
    check(risk_score(expected_rows(["DELIVERY_OVERDUE"] * 3 + ["NO_SHOW"]))[0] == 50,
          "overdue deliveries and no-shows stop at 50 together")
    check("DELIVERY_OVERDUE" in RULES and "SILENT_BUYER" not in WEIGHTS and "SILENT_BUYER" not in RULES,
          "the silent buyer is no fraud signal")

    # Phones and safety guards
    rng = random.Random(17)
    taken: set = set()
    phones = [new_phone(rng, taken) for _ in range(500)]
    check(all(PHONE_RE.match(ph) for ph in phones), "persona phones are +25190017XXXX")
    check(not (set(phones) & SHARED_TEST_PHONES), "persona phones never hit the shared test users")
    check(len(set(phones)) == len(phones), "persona phones are unique in a run")
    check(guard_base_url(DEV_BASE_URL, False) is None, "dev API allowed")
    check(guard_base_url("https://api.weredaconnect.com", False) is not None, "a non-dev API is refused")
    check(guard_fayda_host("https://abc.lambda-url.eu-north-1.on.aws/authorize?x=1") is None, "mock host allowed")
    check(guard_fayda_host("https://esignet.ida.fayda.et/authorize") is not None, "real Fayda host refused")
    key = base64.urlsafe_b64encode(b"user-1|2026-11-07T10:00:00.000Z#ROLE_FORBIDDEN").decode().rstrip("=")
    check(signal_bucket(key, "") == "2026-11-07T10", "signal id decodes to its hour")
    check(signal_bucket("not-base64!", "2026-11-07T12:34:56Z") == "2026-11-07T12", "fallback to createdAt")

    # Report renders, with no em or en dash
    run = Run(Http(DEV_BASE_URL, 0), "frd17-selftest", random.Random(1), verbose=False)
    for label, role in accounts_for(PERSONA_ORDER):
        persona = next((p.key for p in PERSONAS.values() if label in {l for l, _ in p.accounts}), "support")
        run.accounts[label] = Account(label, role, persona, new_phone(rng, taken), "x", "d", user_id=f"u-{label}")
    run.signals = {f"u-{l}": expected_rows(e.signals) for p in PERSONAS.values() for l, e in p.expect.items()}
    run.step("idor", "idor-buyer", "pay", "refused AUTH_041 (HIGH)", "AUTH_041: ...", True, "gate")
    text = render_report(run, PERSONA_ORDER)
    check("\u2014" not in text and "\u2013" not in text, "the report has no em or en dash")
    check("DETECTED AND ENFORCED" in text and "NOT DETECTED (no rule today)" in text, "verdicts rendered")
    for p in PERSONAS.values():
        check(f"`{p.key}`" in text, f"report has a section for {p.key}")

    # Verdicts: every way a persona can fail (a fresh run from the same fixture for each case)
    def fixture() -> Run:
        r = Run(Http(DEV_BASE_URL, 0), "frd17-selftest", random.Random(1), verbose=False)
        r.accounts = dict(run.accounts)
        r.signals = {k: list(v) for k, v in run.signals.items()}
        r.step("idor", "idor-buyer", "pay", "refused AUTH_041 (HIGH)", "AUTH_041: ...", True, "gate")
        return r

    def verdict(r: Run, key: str) -> Tuple[str, bool]:
        return persona_verdict(r, PERSONAS[key])

    base = fixture()
    check(verdict(base, "idor") == ("DETECTED AND ENFORCED", True), "baseline idor matches")
    r = fixture()
    r.step("idor", "idor-buyer", "create on another order id", "conflict", "success", False, "abuse")
    check(verdict(r, "idor")[1] is False, "a failed abuse step deviates")
    r = fixture()
    r.step("idor", "idor-buyer", "read another buyer's order over REST", "refused 403 or 404", "HTTP 200", False, "abuse")
    check(verdict(r, "idor")[1] is False, "a REST read answered 200 (role-visibility leak) deviates")
    r = fixture()
    r.step("idor", "-", "a note", "anything", "something else", False, "info")
    check(verdict(r, "idor")[1] is True, "a failed info step does not deviate")
    r = fixture()
    r.signals["u-idor-buyer"] = expected_rows(["IDOR_CREATE_CONFLICT", "IMMUTABLE_FIELD_CHANGE"])
    check(verdict(r, "idor")[1] is False, "a missing expected signal deviates")
    r = fixture()
    r.signals_error = "reviewed: stopped after 40 pages"
    check(verdict(r, "idor")[0].startswith("UNOBSERVED") and verdict(r, "idor")[1] is False,
          "any signal read problem is UNOBSERVED")
    r = fixture()
    r.aborted["idor"] = "trip order failed"
    check(verdict(r, "idor")[0].startswith("INCOMPLETE") and verdict(r, "idor")[1] is False, "an abort is INCOMPLETE")
    r = fixture()
    r.signals["u-self-buyer"] = expected_rows(["ROLE_FORBIDDEN"])
    check(verdict(r, "selftrade") == ("UNEXPECTED SIGNAL", False), "any signal on the self-trader is UNEXPECTED SIGNAL")
    check(verdict(base, "selftrade") == ("NOT DETECTED (no rule today)", True), "a clean self-trader matches")
    check(verdict(base, "payer") == ("NOT EXERCISABLE ON DEV", True), "the payer persona is not exercisable")
    check(verdict(base, "noshow") == ("NOT EXERCISABLE ON DEV", True), "the noshow persona is not exercisable")
    check(verdict(base, "overdue") == ("NOT EXERCISABLE ON DEV", True), "the overdue persona is not exercisable")

    # The server's tier (FRD-07 users[]) decides; the replica only cross-checks
    r = fixture()
    r.server_scores = {"u-idor-buyer": {"userId": "u-idor-buyer", "riskScore": 115, "riskTier": "critical",
                                        "scoreAvailable": True}}
    v = verdict(r, "idor")
    check(v[1] is False and "replica disagrees" in v[0], "a server tier above the expectation deviates and is flagged")
    r.server_scores["u-idor-buyer"].update(riskScore=90, riskTier="high")
    check(verdict(r, "idor") == ("DETECTED AND ENFORCED", True), "a server tier that agrees matches")
    out = account_outcome(r, "self-buyer", PERSONAS["selftrade"].expect["self-buyer"])
    check((out["tier"], out["source"]) == ("LOW", "server"), "a user in no page is LOW by the server")
    r.server_scores["u-idor-buyer"].update(scoreAvailable=False)
    check(account_outcome(r, "idor-buyer", PERSONAS["idor"].expect["idor-buyer"])["source"] == "replica",
          "an unavailable server score falls back to the replica")

    # Both statuses are read, and users[] collected from every page (stub API)
    class StubHttp:
        def __init__(self):
            self.statuses = []

        def call(self, method, path, token=None, params=None, **_):
            status = (params or {}).get("status")
            self.statuses.append(status)
            page = {"pending": {"signals": [{"userId": "u1", "signalType": "ROLE_FORBIDDEN", "signalId": "x",
                                             "createdAt": "2026-11-07T10:00:00Z"}],
                                "users": [{"userId": "u1", "riskScore": 25, "riskTier": "low"}]},
                    "reviewed": {"signals": [{"userId": "u1", "signalType": "IDOR_CREATE_CONFLICT", "signalId": "y",
                                              "createdAt": "2026-11-07T11:00:00Z", "resolution": "reject"},
                                             {"userId": "other", "signalType": "GPS_JUMP", "signalId": "z",
                                              "createdAt": "2026-11-07T11:00:00Z"}],
                                 "users": [{"userId": "u1", "riskScore": 65, "riskTier": "high"},
                                           {"userId": "other", "riskScore": 10, "riskTier": "low"}]}}[status]
            return Resp(200, json.dumps(page))

    stub = StubHttp()
    rows, scores, problem = read_signals(stub, "t", {"u1"}, 3)
    check(stub.statuses == ["pending", "reviewed"], "the signal read asks for pending and reviewed")
    check(sorted(x["signalType"] for x in rows["u1"]) == ["IDOR_CREATE_CONFLICT", "ROLE_FORBIDDEN"]
          and "other" not in rows, "a rejected (reviewed) row is kept, other users are not")
    check(scores.get("u1", {}).get("riskTier") == "high" and not problem, "users[] is collected from every page")
    check(risk_score(rows["u1"]) == (65, "HIGH"), "a rejected row still counts in the replica")

    # Exit codes
    check(run_exit_code([("a", "x", True), ("b", "y", True)]) == 0, "all personas matched: exit 0")
    check(run_exit_code([("a", "x", True), ("b", "y", False)]) == 1, "a deviating persona: exit 1")
    now = parse_iso("2026-11-08T03:00:00Z")
    targets = [{"escrowId": "e-due", "deadline": "2026-11-07T23:43:47.2862315Z"}]
    check(evaluate_holds(targets, {}, now, 2)[1] == 1, "a target past its deadline and grace, not held: exit 1")
    check(evaluate_holds(targets, {"e-due": {"holdReason": "risk_critical"}}, now, 2)[1] == 0, "a held target: exit 0")
    check(evaluate_holds(targets, {}, parse_iso("2026-11-08T00:30:00Z"), 2)[1] == 0, "inside the grace: exit 0")
    check(evaluate_holds([{"escrowId": "e", "deadline": None}], {}, now, 2)[1] == 0, "an unknown deadline does not fail")
    state = {"ids": {"idor-hold-escrow": "e1", "idor-hold-deadline": "2026-11-07T22:41:00Z"}}
    check(hold_targets(state, ["e2=2026-11-07T23:00:00Z"]) == [
        {"escrowId": "e1", "deadline": "2026-11-07T22:41:00Z"}, {"escrowId": "e2", "deadline": "2026-11-07T23:00:00Z"}],
        "hold targets come from the state and --hold-target")
    check(state_path_problem(os.path.join(REPO_ROOT, "frd17-state.json")) is not None, "a state file in the repo is refused")
    check(state_path_problem("/tmp/frd17-state.json") is None, "a state file in /tmp is allowed")

    for f in failures:
        print(f"  [X] {f}")
    print(f"self-test: {'PASS' if not failures else 'FAIL'} ({len(failures)} failure(s))")
    return 0 if not failures else 1


def recheck(args) -> int:
    """Re-read an earlier run's accounts: the server's tiers (with the replica as a cross-check) and the held
    queue. Exit code from evaluate_holds: 1 when a hold target is overdue and not held; 2 on a setup failure."""
    with open(args.recheck) as fh:
        state = json.load(fh)
    base = state.get("base") or args.base_url
    problem = guard_base_url(base, args.allow_any_api)
    if problem:
        print(problem)
        return 2
    http = Http(base, args.pace)
    token, err = admin_token(args)
    if not token:
        print(f"admin login failed: {err}")
        return 2
    users = {a["userId"]: a["label"] for a in state["accounts"] if a.get("userId")}
    targets = hold_targets(state, args.hold_target)
    signals, scores, err = read_signals(http, token, set(users), args.max_signal_pages)
    held_rows, err2 = read_held_escrows(http, token, set(users) | {t["escrowId"] for t in targets}, 20)
    if err2:
        print(f"held escrow read failed: {err2}")
        return 2
    run = Run(http, state.get("runId", "?"), random.Random(0), verbose=False)
    run.server_scores = scores
    lines = [f"\n## Re-check {time.strftime('%Y-%m-%d %H:%M:%SZ', time.gmtime())} (run `{state.get('runId')}`)\n"]
    for user_id, label in users.items():
        rows = signals.get(user_id, [])
        replica = risk_score(rows)
        score, tier, source = server_tier(run, user_id, rows, *replica)
        flag = ", DISAGREES" if source == "server" and (score, tier) != replica else ""
        lines.append(f"- {label}: {', '.join(sorted({r['signalType'] for r in rows})) or 'none'}; "
                     f"score {score}, tier {tier} from the {source} (replica {replica[0]}, {replica[1]}{flag})")
    held = {e.get("escrowId"): e for e in held_rows}
    lines.append(f"\nHeld escrows naming a persona account or a hold target: {len(held_rows)}")
    lines.extend(f"- `{e.get('escrowId')}` holdReason {e.get('holdReason')} heldFor {e.get('heldFor')}" for e in held_rows)
    hold_lines, code = evaluate_holds(targets, held, time.time(), args.hold_grace_hours)
    lines.append(f"\nHold targets (grace {args.hold_grace_hours:g} h after the farmer deadline):")
    lines.extend(hold_lines or ["- none in the state file; pass --hold-target ESCROW_ID=DEADLINE"])
    if err:
        lines.append(f"\nSignal read note: {err}")
    text = "\n".join(lines) + "\n"
    print(text)
    if args.report:
        with open(args.report, "a") as fh:
            fh.write(text)
        print(f"appended to {args.report}")
    return code


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base-url", default=os.environ.get("API_BASE_URL", DEV_BASE_URL))
    ap.add_argument("--allow-any-api", action="store_true", help="run against a stack other than dev")
    ap.add_argument("--personas", default=",".join(PERSONA_ORDER), help="comma-separated: " + ",".join(PERSONA_ORDER))
    ap.add_argument("--dry-run", action="store_true", help="print the plan and the accounts, no network")
    ap.add_argument("--self-test", action="store_true", help="offline checks of the personas and the score replica")
    ap.add_argument("--report", default=None, help="markdown report path (default specs/reports/fraud-personas-<date>.md)")
    ap.add_argument("--state-file", default=None, help="write the accounts (with passwords) and ids for --recheck")
    ap.add_argument("--recheck", default=None, help="state file of an earlier run: re-read tiers and held escrows")
    ap.add_argument("--hold-target", action="append", default=None, metavar="ESCROW_ID=DEADLINE",
                    help="with --recheck: an escrow expected held after its farmer deadline (repeatable)")
    ap.add_argument("--hold-grace-hours", type=float, default=2.0,
                    help="with --recheck: hours after the deadline before a missing hold fails (order cron, hourly sweep)")
    ap.add_argument("--no-cleanup", action="store_true", help="leave every created row in place")
    ap.add_argument("--pace", type=float, default=0.3, help="seconds between requests")
    ap.add_argument("--seed", type=int, default=None)
    ap.add_argument("--max-signal-pages", type=int, default=40)
    ap.add_argument("--region", default="eu-north-1")
    ap.add_argument("--admin-client-id", default=os.environ.get("ADMIN_CLIENT_ID", "4dntti20l1if7jfutdskjdkrmm"))
    ap.add_argument("--admin-phone", default=os.environ.get("ADMIN_PHONE", "+251900100009"))
    ap.add_argument("--admin-password", default=os.environ.get("ADMIN_PASSWORD", "AdminPass1dev"))
    args = ap.parse_args()

    if args.self_test:
        return self_test()
    if args.recheck:
        return recheck(args)
    if args.state_file and state_path_problem(args.state_file):
        print(state_path_problem(args.state_file))
        return 2
    selected = [k.strip() for k in args.personas.split(",") if k.strip()]
    unknown = [k for k in selected if k not in PERSONAS]
    if unknown:
        print(f"unknown persona(s): {', '.join(unknown)}")
        return 2
    selected = [k for k in PERSONA_ORDER if k in selected]
    if args.dry_run:
        print_plan(selected)
        return 0
    problem = guard_base_url(args.base_url, args.allow_any_api)
    if problem:
        print(problem)
        return 2

    seed = args.seed if args.seed is not None else int(time.time())
    run_id = f"frd17-{time.strftime('%m%d%H%M', time.gmtime())}"
    http = Http(args.base_url, args.pace)
    run = Run(http, run_id, random.Random(seed))
    print(f"Adversarial personas {run_id} against {http.base}: {', '.join(selected)}")
    if http.call("GET", "/health").status != 200:
        print("API health check failed")
        return 2
    # The read-back needs the admin; fail before anything is created on the stack.
    token, err = admin_token(args)
    if not token:
        print(f"admin login failed: {err}")
        return 2

    runners = {"spammer": persona_spammer, "selftrade": persona_selftrade, "idor": persona_idor,
               "payer": persona_payer, "noshow": persona_noshow, "overdue": persona_overdue}

    def attempt(key: str, fn):
        try:
            fn()
        except SetupError:
            raise
        except Exception as e:  # noqa: BLE001  one persona's surprise must not lose the others' report
            run.aborted[key] = f"{type(e).__name__}: {e}"
            print(f"  [!] {key} aborted: {run.aborted[key]}")

    try:
        for key in selected:
            if key in ("gps", "dispute"):
                continue
            print(f"\n== {PERSONAS[key].title}")
            attempt(key, lambda: runners[key](run))
        if "gps" in selected or "dispute" in selected:
            print("\n== GPS spoofer / confirm-then-dispute buyer")
            attempt("dispute" if "dispute" in selected else "gps",
                    lambda: persona_gps_and_dispute(run, "gps" in selected, "dispute" in selected))
        if "idor" in selected or "selftrade" in selected:
            print("\n== Stale password probes (AUTH_040)")
            attempt("idor" if "idor" in selected else "selftrade", lambda: stale_password_phase(run))
    except SetupError as e:
        print(f"SETUP FAILED: {e}")
        if not args.no_cleanup:
            do_cleanup(run)
        save_state(run, args)
        return 2

    print("\n== Reading the signals back (admin Fraud Review API)")
    time.sleep(5)
    ids = {a.user_id for a in run.accounts.values()}
    run.signals, run.server_scores, problem = read_signals(http, token, ids, args.max_signal_pages)
    run.signals_error = problem or None
    for a in run.accounts.values():
        rows = run.signals.get(a.user_id, [])
        print(f"  {a.label}: {sorted({r['signalType'] for r in rows}) or 'none'} -> "
              f"{server_tier(run, a.user_id, rows, *risk_score(rows))} (replica {risk_score(rows)})")
    if run.signals_error:
        print(f"  {run.signals_error}")

    if "spammer" in selected and "spammer" in run.accounts:
        price_sample_note(run)

    if not args.no_cleanup:
        print("\n== Cleanup")
        do_cleanup(run)

    report = args.report or os.path.join(REPO_ROOT, "specs", "reports", f"fraud-personas-{run.started[:10]}.md")
    text = render_report(run, selected)
    with open(report, "w") as fh:
        fh.write(text + "\n")
    save_state(run, args)
    verdicts = [(k, *persona_verdict(run, PERSONAS[k])) for k in selected]
    print("\n== Verdicts")
    for key, verdict, matched in verdicts:
        print(f"  {key}: {verdict}{'' if matched else '  (DEVIATES)'}")
    print(f"\nreport: {report}\nHTTP calls: {http.calls}")
    return run_exit_code(verdicts)


def do_cleanup(run: Run):
    for kind, label, entity_id in run.cleanup:
        a = run.accounts.get(label)
        if not a:
            continue
        try:
            if kind == "delete-crop":
                res = run.sync1(a, "crop_listing", entity_id, "delete", {"version": 1})
            elif kind == "cancel-order":
                version = int(run.order(a, entity_id).get("version") or 1)
                res = run.sync1(a, "order", entity_id, "update", {"status": "cancelled", "version": version})
            elif kind == "decline-order":
                res = answer(run, a, entity_id, "farmerResponse", "decline")
            else:
                continue
            run.log(f"  {kind} {entity_id[:8]} by {label}: {msg(res)}")
            if not ok(res):
                run.left_on_dev.append(f"{kind} of `{entity_id}` failed: {msg(res)}")
        except PersonaAbort as e:
            run.left_on_dev.append(f"{kind} of `{entity_id}` failed: {e}")


def state_path_problem(path: str) -> Optional[str]:
    """The state file holds the synthetic accounts' passwords: never inside a git checkout (any directory on
    its path holding a `.git`), so it cannot be staged by accident."""
    directory = os.path.dirname(os.path.realpath(path))
    while True:
        if os.path.exists(os.path.join(directory, ".git")):
            return (f"refusing --state-file {path}: it holds passwords and is inside the git checkout {directory}; "
                    "use a path outside it, e.g. /tmp/frd17-state.json")
        parent = os.path.dirname(directory)
        if parent == directory:
            return None
        directory = parent


def save_state(run: Run, args):
    if not args.state_file:
        return
    state = {"runId": run.run_id, "base": run.http.base, "started": run.started, "ids": run.ids,
             "accounts": [{"label": a.label, "role": a.role, "persona": a.persona, "phone": a.phone,
                           "password": a.password, "deviceId": a.device_id, "userId": a.user_id}
                          for a in run.accounts.values()]}
    fd = os.open(args.state_file, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    os.fchmod(fd, 0o600)  # an existing file keeps its old mode otherwise
    with os.fdopen(fd, "w") as fh:
        json.dump(state, fh, indent=2)
    print(f"state: {args.state_file} (mode 0600, holds the synthetic accounts' passwords)")


if __name__ == "__main__":
    sys.exit(main())
