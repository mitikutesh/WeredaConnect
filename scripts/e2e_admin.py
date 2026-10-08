#!/usr/bin/env python3
"""
WeredaConnect Admin Dashboard API E2E
=====================================
Logs in as the dashboard admin through Cognito and reads every endpoint the
admin dashboard (admin/) renders: dashboard stats, users, escrows,
transactions, audit, fraud signals, disputes, manual payouts, health, compliance,
analytics, plus one detail read per list that has rows, and two refused manual
payout writes (ESC-02). Stdlib only, no AWS CLI.

deploy-server.yml runs this after every dev deploy (non-blocking) right after
scripts/seed-admin-user.sh has made sure the admin exists (SIM-18).

Usage:
    python3 scripts/e2e_admin.py                       # dev defaults
    python3 scripts/e2e_admin.py --base-url https://... --client-id ... --region eu-north-1
    ADMIN_PASSWORD='...' python3 scripts/e2e_admin.py --phone +2519...

Exit codes: 0 all checks passed, 1 an endpoint check failed, 2 login failed
(the admin is not seeded, wrong password, or the pool/client ids are stale).
"""

import argparse
import base64
import json
import os
import ssl
import sys
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

DEFAULT_BASE_URL = "https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com"
DEFAULT_REGION = "eu-north-1"
DEFAULT_CLIENT_ID = "4dntti20l1if7jfutdskjdkrmm"  # dev AdminClientId (.ai/test-users.md)
DEFAULT_PHONE = "+251900100009"
DEFAULT_PASSWORD = "AdminPass1dev"

LIST_ENDPOINTS = [
    ("/admin/dashboard/stats", None),
    ("/admin/users", "users"),
    ("/admin/escrows", "escrows"),
    ("/admin/transactions", "transactions"),
    ("/admin/audit", "entries"),
    ("/admin/fraud-signals", "signals"),
    ("/admin/disputes", "disputes"),
    ("/admin/payouts/manual", "payouts"),
    ("/admin/health/overview", None),
    ("/admin/compliance/summary", None),
    ("/admin/analytics/revenue", None),
    ("/admin/analytics/transactions", None),
]

# Negative POST checks that must write nothing: (path, body, expected status, expected code).
# ESC-02 manual payout completion: an unknown escrow is refused before any write, and an
# empty reference is refused by validation.
NEGATIVE_POSTS = [
    ("/admin/payouts/escrow-e2e-missing/farmer/complete", {"externalRef": "E2E-NOT-A-REAL-REF"}, 404, "ESCROW_016"),
    ("/admin/payouts/escrow-e2e-missing/farmer/complete", {"externalRef": ""}, 400, "ESCROW_037"),
]

# (list path, id field candidates, detail path template)
DETAIL_READS = [
    ("/admin/escrows", ("escrowId",), "/admin/escrows/{id}"),
    ("/admin/escrows", ("escrowId",), "/admin/escrows/{id}/audit"),
    ("/admin/users", ("userId",), "/admin/users/{id}"),
    ("/admin/transactions", ("orderId",), "/admin/transactions/{id}"),
]

_ctx = ssl.create_default_context()


def http(method, url, headers=None, body=None, timeout=30):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = Request(url, data=data, method=method)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urlopen(req, timeout=timeout, context=_ctx) as resp:
            return resp.status, resp.read().decode("utf-8")
    except HTTPError as e:
        return e.code, e.read().decode("utf-8") if e.fp else ""
    except URLError as e:
        return 0, str(e.reason)


def totp_code(secret_base32, at=None):
    """RFC 6238 TOTP (SHA-1, 6 digits, 30 s) with the standard library only."""
    import hmac
    import hashlib
    import struct
    import time
    padded = secret_base32.strip().upper() + "=" * (-len(secret_base32.strip()) % 8)
    key = base64.b32decode(padded)
    counter = int((at if at is not None else time.time()) // 30)
    digest = hmac.new(key, struct.pack(">Q", counter), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    value = struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7FFFFFFF
    return f"{value % 1_000_000:06d}"


def cognito_call(region, target, body):
    status, raw = http(
        "POST",
        f"https://cognito-idp.{region}.amazonaws.com/",
        headers={
            "Content-Type": "application/x-amz-json-1.1",
            "X-Amz-Target": f"AWSCognitoIdentityProviderService.{target}",
        },
        body=body,
    )
    try:
        payload = json.loads(raw) if raw else {}
    except json.JSONDecodeError:
        payload = {"message": raw}
    return status, payload


def cognito_login(region, client_id, phone, password, token_kind):
    """USER_PASSWORD_AUTH against the admin pool's app client (no secret). MFA is
    optional outside prod and seed-admin-user.sh clears the preference before every
    run, so this normally issues tokens directly. It still answers TOTP challenges:
    MFA_SETUP enrols a fresh authenticator; SOFTWARE_TOKEN_MFA answers with
    ADMIN_TOTP_SECRET when a secret is provided."""
    status, payload = cognito_call(region, "InitiateAuth", {
        "AuthFlow": "USER_PASSWORD_AUTH",
        "ClientId": client_id,
        "AuthParameters": {"USERNAME": phone, "PASSWORD": password},
    })
    if status != 200:
        return None, f"{payload.get('__type', status)}: {payload.get('message', payload)}"

    for _ in range(3):
        result = payload.get("AuthenticationResult") or {}
        if result:
            token = result.get("IdToken" if token_kind == "id" else "AccessToken")
            return (token, None) if token else (None, f"no {token_kind} token in response")

        challenge = payload.get("ChallengeName")
        session = payload.get("Session")
        if challenge == "MFA_SETUP":
            status, assoc = cognito_call(region, "AssociateSoftwareToken", {"Session": session})
            if status != 200:
                return None, f"AssociateSoftwareToken: {assoc.get('message', assoc)}"
            secret = assoc["SecretCode"]
            status, verified = cognito_call(region, "VerifySoftwareToken", {
                "Session": assoc["Session"],
                "UserCode": totp_code(secret),
                "FriendlyDeviceName": "ci-e2e",
            })
            if status != 200:
                return None, f"VerifySoftwareToken: {verified.get('message', verified)}"
            status, payload = cognito_call(region, "RespondToAuthChallenge", {
                "ChallengeName": "MFA_SETUP",
                "ClientId": client_id,
                "Session": verified["Session"],
                "ChallengeResponses": {"USERNAME": phone},
            })
        elif challenge == "SOFTWARE_TOKEN_MFA":
            secret = os.environ.get("ADMIN_TOTP_SECRET")
            if not secret:
                return None, "SOFTWARE_TOKEN_MFA challenge but ADMIN_TOTP_SECRET is not set"
            status, payload = cognito_call(region, "RespondToAuthChallenge", {
                "ChallengeName": "SOFTWARE_TOKEN_MFA",
                "ClientId": client_id,
                "Session": session,
                "ChallengeResponses": {"USERNAME": phone, "SOFTWARE_TOKEN_MFA_CODE": totp_code(secret)},
            })
        else:
            return None, f"unexpected challenge: {challenge or 'none'}"
        if status != 200:
            return None, f"{payload.get('__type', status)}: {payload.get('message', payload)}"

    return None, "too many challenge rounds"


def jwt_claims(token):
    part = token.split(".")[1]
    part += "=" * (-len(part) % 4)
    return json.loads(base64.urlsafe_b64decode(part))


def first_list(payload):
    """The first list-valued top-level field of a response (row source)."""
    if isinstance(payload, list):
        return payload
    if isinstance(payload, dict):
        for value in payload.values():
            if isinstance(value, list):
                return value
    return None


def main():
    ap = argparse.ArgumentParser(description="Admin dashboard API e2e (dev)")
    ap.add_argument("--base-url", default=os.environ.get("API_BASE_URL", DEFAULT_BASE_URL))
    ap.add_argument("--region", default=os.environ.get("REGION", DEFAULT_REGION))
    ap.add_argument("--client-id", default=os.environ.get("ADMIN_CLIENT_ID", DEFAULT_CLIENT_ID))
    ap.add_argument("--phone", default=os.environ.get("ADMIN_PHONE", DEFAULT_PHONE))
    ap.add_argument("--password", default=os.environ.get("ADMIN_PASSWORD", DEFAULT_PASSWORD))
    ap.add_argument("--token", choices=["id", "access"], default="id",
                    help="which Cognito token to send as Bearer (the admin app sends the ID token)")
    args = ap.parse_args()
    base = args.base_url.rstrip("/")

    print("=== WeredaConnect Admin API E2E ===")
    print(f"API:    {base}")
    print(f"Region: {args.region}")
    print(f"Client: {args.client_id}")
    print(f"Admin:  {args.phone}")

    print("\n--- Cognito login (USER_PASSWORD_AUTH + TOTP challenge)")
    token, err = cognito_login(args.region, args.client_id, args.phone, args.password, args.token)
    if not token:
        print(f"  [X] login: FAIL -- {err}")
        print("      Seed the admin first: scripts/seed-admin-user.sh (CI does this on every dev deploy).")
        return 2
    claims = jwt_claims(token)
    groups = claims.get("cognito:groups", [])
    print(f"  [+] login: PASS -- sub={claims.get('sub', '?')} groups={groups}")
    failures = 0
    if "admin" not in groups:
        print("  [X] admin group: FAIL -- token carries no 'admin' group; /admin/* will be refused")
        failures += 1

    headers = {"Authorization": f"Bearer {token}", "Content-Type": "application/json"}
    rows_by_path = {}

    print("\n--- Dashboard reads")
    for path, _ in LIST_ENDPOINTS:
        status, body = http("GET", base + path, headers=headers)
        try:
            payload = json.loads(body) if body else {}
        except json.JSONDecodeError:
            payload = None
        ok = status == 200 and payload is not None
        rows = first_list(payload) if ok else None
        rows_by_path[path] = rows or []
        detail = f"HTTP {status}"
        if rows is not None:
            detail += f", {len(rows)} rows"
        elif ok and isinstance(payload, dict):
            detail += f", keys={sorted(payload.keys())[:6]}"
        elif not ok:
            detail += f", {body[:160]}"
        print(f"  [{'+' if ok else 'X'}] GET {path}: {'PASS' if ok else 'FAIL'} -- {detail}")
        failures += 0 if ok else 1

    print("\n--- Detail reads (first row of each list, when any)")
    for list_path, id_fields, template in DETAIL_READS:
        rows = rows_by_path.get(list_path) or []
        row_id = None
        for row in rows:
            if isinstance(row, dict):
                row_id = next((row[f] for f in id_fields if row.get(f)), None)
                if row_id:
                    break
        if not row_id:
            print(f"  [ ] GET {template}: SKIP -- no rows in {list_path}")
            continue
        path = template.format(id=row_id)
        status, body = http("GET", base + path, headers=headers)
        ok = status == 200
        print(f"  [{'+' if ok else 'X'}] GET {path}: {'PASS' if ok else 'FAIL'} -- HTTP {status}"
              + ("" if ok else f", {body[:160]}"))
        failures += 0 if ok else 1

    print("\n--- Negative writes (must be refused, nothing written)")
    for path, body, want_status, want_code in NEGATIVE_POSTS:
        status, raw = http("POST", base + path, headers=headers, body=body)
        try:
            payload = json.loads(raw) if raw else {}
        except json.JSONDecodeError:
            payload = {}
        code = (payload.get("error") or {}).get("code") if isinstance(payload, dict) else None
        ok = status == want_status and code == want_code
        print(f"  [{'+' if ok else 'X'}] POST {path} {json.dumps(body)}: {'PASS' if ok else 'FAIL'}"
              f" -- HTTP {status} {code or ''} (want {want_status} {want_code})")
        failures += 0 if ok else 1

    print(f"\n=== {'ALL PASSED' if failures == 0 else f'{failures} FAILED'} ===")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
