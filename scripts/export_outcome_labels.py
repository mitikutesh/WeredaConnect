#!/usr/bin/env python3
"""
WeredaConnect outcome label export (FRD-18)
===========================================
Exports the labelled outcomes of resolved fraud and dispute cases for later
analysis (FRD-20, ML scoring). Every resolved case writes one `outcome_label`
row into the transaction-audit table (specs/architecture/fraud-detection-
architecture.md section 20.1):

  case          outcome                                    written by
  fraud_signal  false_positive | fraud_confirmed |         Fraud Review approve, reject, whitelist
                whitelisted
  dispute       buyer_wins | farmer_wins                   the admin dispute verdict
  held_escrow   hold_released | hold_refunded              the admin release or refund of a held escrow

Every label row has `entityId = outcome-labels`, so this script reads them with
one Query of the table's `EntityIndex` (paged, at most --max-pages pages). No
Scan. The index sort key is the row's `timestamp`, the case's key time (the
signal's time or its previous decision, the verdict time, the hold time), so
--since and --until select by that time, not by when the admin decided
(`labelledAt` in the output).

Ids only. The output carries the label, its case and the ids it links (signal
ids, user ids, order, escrow and delivery ids), the amount and the hold or
dispute context. A fraud_signal label written since FRD-20 P1 also carries the
feature snapshot of its user just before the decision (`snapshotVersion` and
the `snapshot*` columns: the counted signal units per type over the score
window, the clean-delivery credits, whether the read was cut, the account age
in days, the Fayda flag, the role and the completed escrows). Counts, codes and
ages only; a label without `snapshotVersion` has no snapshot. In CSV the units
are `TYPE:n` pairs joined by `;`. It never carries the admin's free text (`labelReason`, which
can hold names or phone numbers), the request IP or the correlation id, and the
export stops (exit 1) if any exported value looks like a phone number. Do not
write the output inside a git checkout: the script refuses (exit 2).

Prerequisites: python3 (stdlib only) and the AWS CLI v2 with credentials that
may `dynamodb:Query` the table and its indexes. Read only: nothing is written.

Usage:
  python3 -I scripts/export_outcome_labels.py --env dev --dry-run            # print the AWS call, no network
  python3 -I scripts/export_outcome_labels.py --env dev --out /tmp/labels.jsonl
  python3 -I scripts/export_outcome_labels.py --env prod --format csv --out /tmp/labels.csv \\
      --since 2026-11-01T00:00:00Z --latest-only
  python3 -I scripts/export_outcome_labels.py --table my-table --region eu-north-1 --profile ops --out /tmp/l.jsonl

Options:
  --env dev|staging|prod  table wereda-connect-<env>-transaction-audit, region eu-north-1 (dev) or me-south-1
  --table, --region       override the table name and region
  --profile               AWS CLI profile
  --format jsonl|csv      default jsonl (one JSON object per line)
  --out PATH              default stdout
  --since, --until        bounds on the case key time, inclusive: a UTC date (YYYY-MM-DD; --until
                          then runs to the end of that day) or a UTC time (YYYY-MM-DDTHH:MM[:SS[.f]]Z)
  --latest-only           one row per case: the latest decision (by labelledAt)
  --page-size N           items per Query page (default 100, at most 1000)
  --max-pages N           pages read at most (default 50); more pages exit 3 with what was read

Every row has `final`: true for the latest decision of its case (a Fraud Review
signal can be decided again, for example reject then approve; the other cases
have one label each). It is computed over the rows this run read only: with
--since, --until or a stop at --max-pages, a later decision outside what was read
is not seen, and an earlier one can be marked final.

Exit codes: 0 done, 1 the AWS call failed or a value looked like a phone number,
2 usage error (bad arguments, an output path inside a git checkout), 3 stopped
at --max-pages (the output holds the pages read).

Tests: python3 -I scripts/test_export_outcome_labels.py
"""
import argparse
import csv
import datetime
import io
import json
import os
import re
import subprocess
import sys

INDEX_NAME = "EntityIndex"
STREAM_ENTITY_ID = "outcome-labels"
LABEL_ACTION = "outcome_label"

ENVIRONMENTS = {
    "dev": ("wereda-connect-dev-transaction-audit", "eu-north-1"),
    "staging": ("wereda-connect-staging-transaction-audit", "me-south-1"),
    "prod": ("wereda-connect-prod-transaction-audit", "me-south-1"),
}

# Output column -> attribute of the label row. Ids and codes only: never labelReason, ipAddress,
# correlationId or metadata.
FIELDS = [
    ("labelVersion", "labelVersion"),
    ("case", "labelCase"),
    ("caseId", "labelCaseId"),
    ("outcome", "labelOutcome"),
    ("source", "labelSource"),
    ("labelledAt", "labelledAt"),
    ("keyTimestamp", "timestamp"),
    ("adminId", "actor"),
    ("transactionId", "transactionId"),
    ("subjectUserId", "subjectUserId"),
    ("signalIds", "signalIds"),
    ("signalType", "signalType"),
    ("signalEntityType", "signalEntityType"),
    ("signalEntityId", "signalEntityId"),
    ("signalCreatedAt", "signalCreatedAt"),
    ("orderId", "orderId"),
    ("escrowId", "escrowId"),
    ("deliveryId", "deliveryId"),
    ("buyerId", "buyerId"),
    ("farmerId", "farmerId"),
    ("truckerId", "truckerId"),
    ("amount", "amount"),
    ("disputedBy", "disputedBy"),
    ("disposition", "disposition"),
    ("holdReason", "holdReason"),
    ("heldFor", "heldFor"),
    ("heldFromStatus", "heldFromStatus"),
    ("heldAt", "heldAt"),
    ("whitelistUntil", "whitelistUntil"),
    # FRD-20 P1: the feature snapshot of a fraud_signal label (counts, codes and ages only).
    ("snapshotVersion", "snapshotVersion"),
    ("snapshotWindowDays", "snapshotWindowDays"),
    ("snapshotSignalUnits", "snapshotSignalUnits"),
    ("snapshotCreditUnits", "snapshotCreditUnits"),
    ("snapshotCreditPoints", "snapshotCreditPoints"),
    ("snapshotTruncated", "snapshotTruncated"),
    ("snapshotAccountAgeDays", "snapshotAccountAgeDays"),
    ("snapshotFaydaVerified", "snapshotFaydaVerified"),
    ("snapshotRole", "snapshotRole"),
    ("snapshotCompletedEscrows", "snapshotCompletedEscrows"),
    ("snapshotCompletedEscrowsPartial", "snapshotCompletedEscrowsPartial"),
]
COLUMNS = [name for name, _ in FIELDS] + ["hasReason", "final"]
BOOLEAN_COLUMNS = ("snapshotTruncated", "snapshotFaydaVerified", "snapshotCompletedEscrowsPartial", "hasReason", "final")

# An Ethiopian phone number as a whole value: +2519XXXXXXXX, 2519XXXXXXXX, 09XXXXXXXX or 07XXXXXXXX.
PHONE = re.compile(r"^(\+?251|0)[79]\d{8}$")


class ExportError(Exception):
    """Exit 1: the AWS call failed or the output would leak a phone number."""


class UsageError(Exception):
    """Exit 2."""


def unmarshal(value):
    """One DynamoDB JSON attribute value to a plain Python value."""
    if "S" in value:
        return value["S"]
    if "N" in value:
        text = value["N"]
        return int(text) if re.fullmatch(r"-?\d+", text) else float(text)
    if "BOOL" in value:
        return value["BOOL"]
    if "NULL" in value:
        return None
    if "L" in value:
        return [unmarshal(v) for v in value["L"]]
    if "M" in value:
        return {k: unmarshal(v) for k, v in value["M"].items()}
    if "SS" in value:
        return list(value["SS"])
    if "NS" in value:
        return [unmarshal({"N": n}) for n in value["NS"]]
    raise ExportError(f"unsupported attribute type {sorted(value)}")


def to_record(item):
    """A label row (DynamoDB JSON) to an export record: the whitelisted ids and codes only."""
    plain = {k: unmarshal(v) for k, v in item.items()}
    if plain.get("action") != LABEL_ACTION:
        return None
    record = {name: plain.get(attr) for name, attr in FIELDS}
    record["hasReason"] = bool(plain.get("labelReason"))
    units = record.get("snapshotSignalUnits")
    if units is not None and not (isinstance(units, dict) and all(
            re.fullmatch(r"[A-Z0-9_]{1,64}", str(k)) and isinstance(v, int) and not isinstance(v, bool)
            for k, v in units.items())):
        raise ExportError(f"snapshotSignalUnits of case {record.get('caseId')} is not a map of type codes to counts; nothing exported")
    for name, value in record.items():
        for part in value if isinstance(value, list) else [value]:
            if isinstance(part, str) and PHONE.match(part.strip()):
                raise ExportError(f"field {name} of case {record.get('caseId')} looks like a phone number; nothing exported")
    return record


def mark_final(records):
    """`final` is true for the latest decision (labelledAt, then key) of each case."""
    latest = {}
    for i, r in enumerate(records):
        key = (r.get("case"), r.get("caseId"))
        rank = (r.get("labelledAt") or "", r.get("keyTimestamp") or "")
        if key not in latest or rank >= latest[key][0]:
            latest[key] = (rank, i)
    winners = {i for _, i in latest.values()}
    for i, r in enumerate(records):
        r["final"] = i in winners
    return records


def build_query(table, region, profile, page_size, since, until, start_key):
    """The AWS CLI call for one page of the label stream."""
    condition = "entityId = :stream"
    values = {":stream": {"S": STREAM_ENTITY_ID}}
    names = None
    if since and until:
        condition += " AND #ts BETWEEN :since AND :until"
        values[":since"] = {"S": since}
        values[":until"] = {"S": until}
    elif since:
        condition += " AND #ts >= :since"
        values[":since"] = {"S": since}
    elif until:
        condition += " AND #ts <= :until"
        values[":until"] = {"S": until}
    if since or until:
        names = {"#ts": "timestamp"}  # a reserved word
    cmd = [
        "aws", "dynamodb", "query",
        "--table-name", table,
        "--index-name", INDEX_NAME,
        "--key-condition-expression", condition,
        "--expression-attribute-values", json.dumps(values, separators=(",", ":")),
        "--limit", str(page_size),
        "--no-paginate",
        "--output", "json",
        "--region", region,
    ]
    if names:
        cmd += ["--expression-attribute-names", json.dumps(names, separators=(",", ":"))]
    if start_key:
        cmd += ["--exclusive-start-key", json.dumps(start_key, separators=(",", ":"))]
    if profile:
        cmd += ["--profile", profile]
    return cmd


def aws_runner(cmd):
    """Run one AWS CLI call and return its JSON answer."""
    try:
        done = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.TimeoutExpired) as e:
        raise ExportError(f"aws call failed: {type(e).__name__}")
    if done.returncode != 0:
        raise ExportError(f"aws call failed ({done.returncode}): {done.stderr.strip()[:500]}")
    return json.loads(done.stdout or "{}")


def read_labels(run, table, region, profile, page_size, max_pages, since=None, until=None):
    """Every label row, page by page. Returns (records, truncated)."""
    records, start_key, pages = [], None, 0
    while True:
        page = run(build_query(table, region, profile, page_size, since, until, start_key))
        pages += 1
        for item in page.get("Items", []):
            record = to_record(item)
            if record is not None:
                records.append(record)
        start_key = page.get("LastEvaluatedKey") or None
        if not start_key:
            return records, False
        if pages >= max_pages:
            return records, True


def render(records, fmt):
    if fmt == "jsonl":
        return "".join(json.dumps(r, ensure_ascii=False, sort_keys=False) + "\n" for r in records)
    buf = io.StringIO()
    writer = csv.DictWriter(buf, fieldnames=COLUMNS, lineterminator="\n")
    writer.writeheader()
    for r in records:
        row = dict(r)
        row["signalIds"] = ";".join(r.get("signalIds") or [])
        units = r.get("snapshotSignalUnits")
        row["snapshotSignalUnits"] = None if units is None else ";".join(f"{k}:{units[k]}" for k in sorted(units))
        for name in BOOLEAN_COLUMNS:
            # A snapshot flag stays empty on a label without one; hasReason and final are always set.
            row[name] = "" if r.get(name) is None else "true" if r.get(name) else "false"
        writer.writerow({k: "" if row.get(k) is None else row.get(k) for k in COLUMNS})
    return buf.getvalue()


def inside_git_checkout(path):
    """True when the path (or the folder it would be written to) is inside a git work tree."""
    folder = os.path.dirname(os.path.abspath(path))
    while True:
        if os.path.exists(os.path.join(folder, ".git")):
            return True
        parent = os.path.dirname(folder)
        if parent == folder:
            return False
        folder = parent


BOUND = re.compile(r"(\d{4}-\d{2}-\d{2})(?:T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,7}))?)?Z)?")


def normalize_bound(name, value, end_of_day):
    """A --since or --until value in the key's own form (UTC, seven fraction digits), so the string compare
    DynamoDB makes on the sort key is a time compare. A date alone is the start of that day, or for --until
    its last tick, so the day is included."""
    if value is None:
        return None
    m = BOUND.fullmatch(value)
    error = UsageError(f"--{name} must be a UTC date (2026-11-01) or time (2026-11-01T10:00:00Z), not {value!r}")
    if not m:
        raise error
    day, hour, minute, second, fraction = m.groups()
    try:
        datetime.date.fromisoformat(day)
    except ValueError:
        raise error
    if hour is None:
        return f"{day}T23:59:59.9999999Z" if end_of_day else f"{day}T00:00:00.0000000Z"
    second = second or "00"
    if int(hour) > 23 or int(minute) > 59 or int(second) > 59:
        raise error
    return f"{day}T{hour}:{minute}:{second}.{(fraction or '').ljust(7, '0')}Z"


def parse_args(argv):
    p = argparse.ArgumentParser(description="Export FRD-18 outcome labels (ids only).")
    p.add_argument("--env", choices=sorted(ENVIRONMENTS))
    p.add_argument("--table")
    p.add_argument("--region")
    p.add_argument("--profile")
    p.add_argument("--format", choices=["jsonl", "csv"], default="jsonl")
    p.add_argument("--out")
    p.add_argument("--since")
    p.add_argument("--until")
    p.add_argument("--latest-only", action="store_true")
    p.add_argument("--page-size", type=int, default=100)
    p.add_argument("--max-pages", type=int, default=50)
    p.add_argument("--dry-run", action="store_true")
    args = p.parse_args(argv)
    if args.env:
        table, region = ENVIRONMENTS[args.env]
        args.table = args.table or table
        args.region = args.region or region
    if not args.table or not args.region:
        raise UsageError("give --env, or --table and --region")
    if not 1 <= args.page_size <= 1000:
        raise UsageError("--page-size must be 1 to 1000")
    if args.max_pages < 1:
        raise UsageError("--max-pages must be at least 1")
    args.since = normalize_bound("since", args.since, end_of_day=False)
    args.until = normalize_bound("until", args.until, end_of_day=True)
    if args.since and args.until and args.since > args.until:
        raise UsageError("--since is after --until")
    if args.out and inside_git_checkout(args.out):
        raise UsageError(f"{args.out} is inside a git checkout; write the export outside the repository")
    return args


def main(argv=None, run=aws_runner, stdout=None, stderr=None):
    stdout = stdout or sys.stdout
    stderr = stderr or sys.stderr
    try:
        args = parse_args(argv if argv is not None else sys.argv[1:])
    except UsageError as e:
        print(f"usage error: {e}", file=stderr)
        return 2

    if args.dry_run:
        cmd = build_query(args.table, args.region, args.profile, args.page_size, args.since, args.until, None)
        print(" ".join(_quote(c) for c in cmd), file=stdout)
        return 0

    try:
        records, truncated = read_labels(run, args.table, args.region, args.profile, args.page_size,
                                         args.max_pages, args.since, args.until)
    except ExportError as e:
        print(f"export failed: {e}", file=stderr)
        return 1

    mark_final(records)
    if args.latest_only:
        records = [r for r in records if r["final"]]
    records.sort(key=lambda r: (r.get("labelledAt") or "", r.get("caseId") or ""))
    text = render(records, args.format)
    if args.out:
        with open(args.out, "w", encoding="utf-8", newline="") as f:
            f.write(text)
    else:
        stdout.write(text)

    cases = len({(r.get("case"), r.get("caseId")) for r in records})
    print(f"exported {len(records)} labels of {cases} cases from {args.table}", file=stderr)
    if truncated:
        print(f"stopped at --max-pages {args.max_pages}: more labels exist; raise --max-pages or narrow --since/--until",
              file=stderr)
        return 3
    return 0


def _quote(part):
    return part if re.fullmatch(r"[A-Za-z0-9_./:=,@+-]+", part) else "'" + part.replace("'", "'\\''") + "'"


if __name__ == "__main__":
    sys.exit(main())
