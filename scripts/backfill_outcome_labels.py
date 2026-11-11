#!/usr/bin/env python3
"""
WeredaConnect outcome label backfill (FUP-143)
==============================================
One-off. Writes the FRD-18 outcome label (specs/architecture/fraud-detection-
architecture.md section 20.1) of every case that was resolved before FRD-18
shipped, or whose live label write failed (OUTCOME_LABEL_FAILED), from the
decision rows the server left in the transaction-audit table:

  audit action                               case          label
  fraud_approve | fraud_reject |             fraud_signal  false_positive | fraud_confirmed |
  fraud_whitelist                                          whitelisted
  admin_dispute_resolved                     dispute       buyer_wins | farmer_wins | partial_refund
  fraud_hold_release | fraud_hold_refund     held_escrow   hold_released | hold_refunded

DRY RUN IS THE DEFAULT. Without --apply nothing is written: the script reads,
prints one JSON line per decision (what it would write, or why it skips it) and
a summary. --apply writes the planned labels.

What it reads and writes
  1. One Scan of the transaction-audit table, filtered on `admin_dispute_resolved`
     and every action that starts with `fraud_`, projecting three ids. Rows of
     any other `fraud_*` action (`fraud_hold`, the hold itself, or one this
     script does not know) are only counted and named in the summary, never
     labelled. The wider filter costs nothing more: a Scan is billed for the
     rows it reads, not the rows it returns. The table has no index on `action`, so this is a full
     table Scan: DynamoDB reads every row once (about 1 read unit per 8 KB,
     eventually consistent). A table of a million 0.5 KB rows is about 62,500
     read units, a few US cents on demand. It is a one-off; do not schedule it.
     Only the case ids are kept in memory, never the rows.
  2. Per case, one consistent Query of its audit partition (the decision rows and
     any label already there), and one read of the case's own row: GetItem on
     fraud-signals (a signal) or Query on escrows (a dispute, a held escrow).
  3. With --apply, one PutItem per label, conditional on
     attribute_not_exists(transactionId). Append only: no row is ever updated or
     deleted, and a row already at the key is left as it is.

Idempotent. A case that already has its label (written live or by an earlier
run) is skipped before any write: a dispute or a held escrow by its case id, a
Fraud Review decision by a label of the same source decided within two minutes
of the audit row. The key is the live key (the case's key time plus the
outcome's slot), so the conditional put is the second guard. Run it as often as
you like.

Never guessed. A decision is skipped and reported when the label the server
would have written cannot be established: the escrow is gone or still held, the
escrow and the audit row disagree, a field the key needs is missing. A
whitelist from before FRD-05 has no `whitelistUntil=` on its audit row: it is
labelled `whitelisted` without `whitelistUntil`, with a note on its line. See
SKIP_REASONS for the list. A held exit attempt is audited before the money
moves, so it counts only once the escrow row shows the exit settled (released,
completed or refunded). An exit still waiting in the manual payout queue reads
`still_held`: run again once it is paid.

Where a backfilled label differs from a live one
  - It carries `labelBackfill: FUP-143` and `backfilledAt` (the run time).
  - Fraud Review: `labelledAt` is the signal's `reviewedAt` when the signal row
    still holds this decision, else the audit row's time (written moments after
    the decision). The key time of a change of mind is the time of the decision
    before it, taken the same way. A signal row that its 30 day TTL removed
    gives no `createdAt`, entity or type field: the key time is then the hour
    in the signal key and the type is read from the key, as the server does for
    a row without those fields; the entity links are left out.
  - Held escrow: `labelledAt` is the time of the last exit audit row.
  - Dispute: built from the escrow row like the server's own repair on a replay
    (same key, same fields); the admin's free-text notes were never stored, so
    `labelReason` is the disposition reason or absent.

Prerequisites: python3 (stdlib only) and the AWS CLI v2 with credentials that
may dynamodb:Scan, Query and (for --apply) PutItem on the transaction-audit
table, GetItem on fraud-signals and Query on escrows.

Usage:
  python3 -I scripts/backfill_outcome_labels.py --env dev                 # dry run
  python3 -I scripts/backfill_outcome_labels.py --env dev --limit 5       # dry run, first 5 cases found
  python3 -I scripts/backfill_outcome_labels.py --env dev --limit 5 --apply
  python3 -I scripts/backfill_outcome_labels.py --env dev --apply
  python3 -I scripts/export_outcome_labels.py --env dev --out /tmp/labels.jsonl   # verify

Options:
  --env dev|staging|prod  tables wereda-connect-<env>-transaction-audit, -fraud-signals, -escrows;
                          region eu-north-1 (dev) or me-south-1
  --audit-table, --signals-table, --escrows-table, --region   overrides
  --profile               AWS CLI profile
  --apply                 write the labels (default: dry run)
  --limit N               stop the Scan once N cases are found and process only those
  --page-size N           rows the Scan reads per page (default 500, at most 1000)
  --max-pages N           Scan pages read at most (default 5000); more pages exit 3 after
                          processing what was found

Output: JSON lines on stdout (ids only, never the admin's free text), the
summary on stderr.

Exit codes: 0 done, 1 an AWS call or a write failed, 2 usage error, 3 the Scan
stopped at --max-pages (the cases found were processed).

Tests: python3 -I scripts/test_backfill_outcome_labels.py
"""
import argparse
import base64
import calendar
import collections
import datetime
import json
import re
import subprocess
import sys

BACKFILL_TAG = "FUP-143"

# The label row constants: server/src/Helpers/OutcomeLabelWriter.cs (OutcomeLabels).
LABEL_ACTION = "outcome_label"
LABEL_ENTITY_TYPE = "label"
STREAM_ENTITY_ID = "outcome-labels"
SCHEMA_VERSION = 1
CASE_FRAUD_SIGNAL = "fraud_signal"
CASE_DISPUTE = "dispute"
CASE_HELD_ESCROW = "held_escrow"

# Fraud Review verb -> outcome: server/src/Helpers/OutcomeLabelWriter.cs (OutcomeLabels.FraudOutcome).
# The audit action is `fraud_<verb>` (server/src/Functions/AdminFunction.cs, HandleFraudActionAsync) and so is
# the label's source (AdminFunction.LabelFraudDecisionAsync).
FRAUD_OUTCOME = {
    "approve": "false_positive",
    "reject": "fraud_confirmed",
    "whitelist": "whitelisted",
}

# Held exit op -> outcome: server/src/Helpers/OutcomeLabelWriter.cs (OutcomeLabels.HeldExitOutcome). The audit
# action and the label's source: server/src/Services/EscrowService.cs (RecordHeldExitAsync, LabelHeldExitAsync).
HELD_EXIT = {
    "fraud_hold_release": ("release", "hold_released"),
    "fraud_hold_refund": ("refund", "hold_refunded"),
}

# A dispute label's outcome is the verdict itself: server/src/Functions/AdminFunction.cs, DisputeLabel
# (`Outcome = body.Resolution`). The verdicts: server/src/Services/EscrowService.LedgerSettle.cs (DisputeVerdicts).
DISPUTE_VERDICTS = ("buyer_wins", "farmer_wins", "partial_refund")
DISPUTE_ACTION = "admin_dispute_resolved"  # AdminFunction.HandleResolveDisputeAsync (the audit row), DisputeLabel (the source)

# The ticks an outcome adds to the key time: server/src/Helpers/OutcomeLabelWriter.cs (OutcomeLabels.Slot).
SLOTS = {
    "false_positive": 1,
    "fraud_confirmed": 2,
    "whitelisted": 3,
    "buyer_wins": 4,
    "farmer_wins": 5,
    "hold_released": 6,
    "hold_refunded": 7,
}
OTHER_SLOT = 8

# The status an escrow has once an admin exit of its hold has settled, and the exit that leads there
# (EscrowService.ReleaseAsync writes released then completed, RefundAsync writes refunded).
SETTLED_EXIT = {"released": "release", "completed": "release", "refunded": "refund"}

FRAUD_ACTIONS = tuple(f"fraud_{verb}" for verb in FRAUD_OUTCOME)
DECISION_ACTIONS = FRAUD_ACTIONS + (DISPUTE_ACTION,) + tuple(HELD_EXIT)
FAMILY = {**{a: CASE_FRAUD_SIGNAL for a in FRAUD_ACTIONS}, DISPUTE_ACTION: CASE_DISPUTE,
          **{a: CASE_HELD_ESCROW for a in HELD_EXIT}}

# A live fraud label is decided (`labelledAt`) before its audit row is written, in the same request; a
# backfilled one takes the audit row's time. Within this window a label of the same source is this decision's.
MATCH_WINDOW_TICKS = 120 * 10_000_000

ENVIRONMENTS = {
    "dev": ("wereda-connect-dev", "eu-north-1"),
    "staging": ("wereda-connect-staging", "me-south-1"),
    "prod": ("wereda-connect-prod", "me-south-1"),
}

SKIP_REASONS = {
    "already_labelled": "the case already has this label (written live or by an earlier run)",
    "same_decision": "the same verb again on a signal: the server writes no label for it (AdminFunction.LabelFraudDecisionAsync)",
    "not_a_raised_dispute": "the escrow has no disputedBy: the server labels it as a held exit, not a dispute "
                            "(AdminFunction.RepairDisputeLabelAsync)",
    "still_held": "the escrow is still held: no exit has settled yet, run again after it has",
    "unresolvable": "the label the server would have written cannot be established (see `detail`)",
}


class BackfillError(Exception):
    """Exit 1: an AWS call failed."""


class UsageError(Exception):
    """Exit 2."""


# ── time, the way the server keys a label ─────────────────────────────────────────────────────────────

ISO = re.compile(r"(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?\s*(Z|[+-]\d{2}:?\d{2})?")


def parse_ticks(value):
    """An ISO 8601 time as 100 ns ticks since 1970 UTC, or None. No offset means UTC, as
    OutcomeLabelWriter.ParseUtc reads it."""
    m = ISO.fullmatch(value.strip()) if isinstance(value, str) else None
    if not m:
        return None
    year, month, day, hour, minute, second, fraction, zone = m.groups()
    try:
        seconds = calendar.timegm(datetime.datetime(
            int(year), int(month), int(day), int(hour), int(minute), int(second or 0)).timetuple())
    except ValueError:
        return None
    if zone and zone != "Z":
        sign = 1 if zone[0] == "+" else -1
        digits = zone[1:].replace(":", "")
        seconds -= sign * (int(digits[:2]) * 3600 + int(digits[2:]) * 60)
    return seconds * 10_000_000 + int((fraction or "")[:7].ljust(7, "0"))


def format_ticks(ticks):
    """Ticks as .NET writes a UTC time with "o": seven fraction digits and Z."""
    seconds, fraction = divmod(ticks, 10_000_000)
    at = datetime.datetime(1970, 1, 1) + datetime.timedelta(seconds=seconds)
    return f"{at:%Y-%m-%dT%H:%M:%S}.{fraction:07d}Z"


def key_timestamp(key_time, labelled_at, outcome):
    """The label's sort key: the key time, else the decision time, plus the outcome's slot
    (OutcomeLabelWriter.KeyTimestamp). None when neither is a time: the
    server then falls back to the clock, which a backfill must not do (the key would change every run)."""
    ticks = parse_ticks(key_time)
    if ticks is None:
        ticks = parse_ticks(labelled_at)
    if ticks is None:
        return None
    return format_ticks(ticks + SLOTS.get(outcome, OTHER_SLOT))


# ── the label row ─────────────────────────────────────────────────────────────────────────────────────

LINK_FIELDS = [
    "subjectUserId", "signalIds", "signalType", "signalEntityType", "signalEntityId", "signalCreatedAt",
    "orderId", "escrowId", "deliveryId", "buyerId", "farmerId", "truckerId", "amount", "disputedBy",
    "disposition", "holdReason", "heldFor", "heldFromStatus", "heldAt", "whitelistUntil",
]


def build_entry(label, backfilled_at):
    """The row OutcomeLabelWriter.BuildEntry writes, plus the two backfill
    marks. `label` is a dict with case, caseId, outcome, source, transactionId, keyTime, adminId, labelledAt
    and the optional reason, link fields, ipAddress and correlationId. None when it has no usable key."""
    timestamp = key_timestamp(label.get("keyTime"), label.get("labelledAt"), label["outcome"])
    if timestamp is None or not label.get("transactionId"):
        return None
    entry = {
        "auditId": f"label-{label['case']}-{label['caseId']}-{label['outcome']}-"
                   + "".join(c for c in timestamp if c.isascii() and c.isalnum()),
        "transactionId": label["transactionId"],
        "timestamp": timestamp,
        "entityType": LABEL_ENTITY_TYPE,
        "entityId": STREAM_ENTITY_ID,
        "action": LABEL_ACTION,
        "toState": label["outcome"],
        "actor": label["adminId"],
        "actorRole": "admin",
        "metadata": f"case={label['case']};outcome={label['outcome']}",
        "labelVersion": SCHEMA_VERSION,
        "labelCase": label["case"],
        "labelCaseId": label["caseId"],
        "labelOutcome": label["outcome"],
        "labelSource": label["source"],
        "labelledAt": label["labelledAt"],
    }
    reason = label.get("reason")
    if isinstance(reason, str) and reason.strip():
        entry["labelReason"] = reason.strip()
    for name in LINK_FIELDS + ["ipAddress", "correlationId"]:
        value = label.get(name)
        if value not in (None, "", []):
            entry[name] = value
    entry["labelBackfill"] = BACKFILL_TAG
    entry["backfilledAt"] = backfilled_at
    return entry


class Number(str):
    """A DynamoDB number kept as its text, so an amount is written back exactly as it was read."""


def unmarshal(value):
    """One DynamoDB JSON attribute value to a plain Python value."""
    if "S" in value:
        return value["S"]
    if "N" in value:
        return Number(value["N"])
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
        return [Number(n) for n in value["NS"]]
    return None


def plain(item):
    return {k: unmarshal(v) for k, v in (item or {}).items()}


def marshal(entry):
    """A label row to DynamoDB JSON, with the types the server writes (DynamoDbHelper.ToAttributeValue):
    strings S, labelVersion and amount N, signalIds a list of S."""
    item = {}
    for key, value in entry.items():
        if isinstance(value, Number):
            item[key] = {"N": str(value)}
        elif isinstance(value, bool):
            item[key] = {"BOOL": value}
        elif isinstance(value, int):
            item[key] = {"N": str(value)}
        elif isinstance(value, list):
            item[key] = {"L": [{"S": str(v)} for v in value]}
        else:
            item[key] = {"S": str(value)}
    return item


def text(row, name):
    """A non-empty string attribute, else None (the server's Field helpers)."""
    value = (row or {}).get(name)
    return value if isinstance(value, str) and not isinstance(value, Number) and value else None


# ── planning: pure functions over plain rows ──────────────────────────────────────────────────────────

def write(entry, row, note=None):
    return {"kind": "write", "entry": entry, "action": row.get("action"), "note": note}


def skip(why, case, case_id, row, detail=None):
    return {"kind": "skip", "why": why, "case": case, "caseId": case_id, "action": (row or {}).get("action"),
            "at": (row or {}).get("timestamp"), "detail": detail}


def labels_of(rows, case, case_id):
    return [r for r in rows if r.get("action") == LABEL_ACTION and r.get("labelCase") == case
            and r.get("labelCaseId") == case_id]


def decisions_of(rows, actions, entity_id):
    found = [r for r in rows if r.get("action") in actions and r.get("entityId") == entity_id
             and isinstance(r.get("timestamp"), str)]
    return sorted(found, key=lambda r: (parse_ticks(r["timestamp"]) or 0, r["timestamp"]))


def finish(label, row, rows, backfilled_at, note=None):
    """The planned write of one label, or the skip when it has no key or the key is taken."""
    label.setdefault("ipAddress", text(row, "ipAddress"))
    label.setdefault("correlationId", text(row, "correlationId"))
    entry = build_entry(label, backfilled_at)
    if entry is None:
        return skip("unresolvable", label["case"], label["caseId"], row, "no usable key time or partition")
    taken = any(r.get("transactionId") == entry["transactionId"] and r.get("timestamp") == entry["timestamp"]
                for r in rows)
    if taken:
        return skip("already_labelled", label["case"], label["caseId"], row, "a row is already at the label's key")
    return write(entry, row, note)


def parse_signal_id(signal_id):
    """(userId, signalKey) of a signal id, or None: FraudDetectionService.TryParseSignalId
    (server/src/Services/FraudDetectionService.cs)."""
    try:
        padded = signal_id.replace("-", "+").replace("_", "/")
        padded += "=" * ((4 - len(padded) % 4) % 4)
        decoded = base64.b64decode(padded, validate=True).decode("utf-8")
    except (ValueError, AttributeError):
        return None
    separator = decoded.find("|")
    if separator <= 0 or separator == len(decoded) - 1:
        return None
    return decoded[:separator], decoded[separator + 1:]


def signal_order_id(entity_type, entity_id):
    """AdminFunction.SignalOrderId."""
    if not entity_id:
        return None
    if entity_type == "order":
        return entity_id
    prefix = "escrow-order-"
    if entity_type == "escrow" and entity_id.startswith(prefix) and len(entity_id) > len(prefix):
        return entity_id[len(prefix):]
    return None


WHITELIST_METADATA = re.compile(r"whitelistUntil=([^;]+)(?:;notes=(.*))?", re.DOTALL)

# The actions of the fraud family that are not an admin decision, so never a label: the hold itself.
NOT_A_DECISION = ("fraud_hold",)


def plan_fraud(signal_id, rows, signal_row, backfilled_at):
    """The labels of one signal's Fraud Review decisions, as AdminFunction.LabelFraudDecisionAsync writes them:
    one per decision that changes what the signal says, keyed on the time of
    the decision before it, or on the signal's createdAt for the first.

    rows: every row of the signal's audit partition (transactionId = the signal id), labels included.
    signal_row: the fraud-signals row, or None when it is gone (its TTL)."""
    decisions = decisions_of(rows, FRAUD_ACTIONS, signal_id)
    parsed = parse_signal_id(signal_id)
    if parsed is None:
        return [skip("unresolvable", CASE_FRAUD_SIGNAL, signal_id, d, "the signal id does not decode") for d in decisions]
    user_id, signal_key = parsed
    if signal_row and signal_row.get("recordType") not in (None, "signal"):
        return [skip("unresolvable", CASE_FRAUD_SIGNAL, signal_id, d, "the row at the signal's key is not a signal")
                for d in decisions]

    # LabelFraudDecisionAsync: `Field("createdAt") ?? signalKey.Split('#')[0]`.
    created_at = text(signal_row, "createdAt") or signal_key.split("#")[0]
    signal_type = text(signal_row, "signalType") or (signal_key[signal_key.index("#") + 1:] if "#" in signal_key else None)
    entity_type = text(signal_row, "entityType")
    entity_id = text(signal_row, "entityId")
    unused = labels_of(rows, CASE_FRAUD_SIGNAL, signal_id)

    results = []
    previous_verb = None
    previous_time = None
    for index, row in enumerate(decisions):
        verb = row["action"][len("fraud_"):]
        at = parse_ticks(row["timestamp"])
        # The audit row carries the verb twice (action and toState, HandleFraudActionAsync).
        if row.get("toState") not in (None, verb) or at is None:
            results.append(skip("unresolvable", CASE_FRAUD_SIGNAL, signal_id, row,
                                "the audit row's toState does not match its action"))
            previous_verb, previous_time = verb, row["timestamp"]
            continue

        # The live label of this decision, if there is one: same source, decided just before the audit row.
        near = [l for l in unused if l.get("labelSource") == row["action"]
                and parse_ticks(l.get("labelledAt")) is not None
                and abs(parse_ticks(l["labelledAt"]) - at) <= MATCH_WINDOW_TICKS]
        match = min(near, key=lambda l: abs(parse_ticks(l["labelledAt"]) - at)) if near else None

        # When the admin decided. The stamp on the signal row is the decision time itself; it is this
        # decision's only for the last audit row, and only when it says the same verb at about the same time.
        labelled_at = row["timestamp"]
        stamped = parse_ticks(text(signal_row, "reviewedAt"))
        if (index == len(decisions) - 1 and signal_row and signal_row.get("resolution") == verb
                and stamped is not None and 0 <= at - stamped <= MATCH_WINDOW_TICKS):
            labelled_at = signal_row["reviewedAt"]
        if match is not None:
            unused.remove(match)
            labelled_at = match["labelledAt"]

        if match is not None:
            results.append(skip("already_labelled", CASE_FRAUD_SIGNAL, signal_id, row))
        elif previous_verb == verb:
            # LabelFraudDecisionAsync: reviewed with the same resolution writes no label.
            results.append(skip("same_decision", CASE_FRAUD_SIGNAL, signal_id, row))
        else:
            label = {
                "case": CASE_FRAUD_SIGNAL,
                "caseId": signal_id,
                "outcome": FRAUD_OUTCOME[verb],
                "source": row["action"],
                "transactionId": signal_id,
                # LabelFraudDecisionAsync: the previous decision's reviewedAt, or createdAt while pending.
                "keyTime": created_at if previous_time is None else previous_time,
                "adminId": text(row, "actor") or "admin",
                "labelledAt": labelled_at,
                "subjectUserId": user_id,
                "signalIds": [signal_id],
                "signalType": signal_type,
                "signalEntityType": entity_type,
                "signalEntityId": entity_id,
                "signalCreatedAt": created_at,
                "orderId": signal_order_id(entity_type, entity_id),
                "escrowId": entity_id if entity_type == "escrow" else None,
                "deliveryId": entity_id if entity_type == "delivery" else None,
            }
            metadata = text(row, "metadata")
            notes = [] if signal_row else ["signal row gone (TTL): key time and type from the signal key, no entity links"]
            m = WHITELIST_METADATA.fullmatch(metadata or "") if verb == "whitelist" else None
            if m is not None:
                # HandleFraudActionAsync: `whitelistUntil=<iso>` then `;notes=<notes>` when there are notes.
                label["whitelistUntil"], label["reason"] = m.group(1), m.group(2)
            else:
                # The notes themselves, or nothing. A whitelist from before FRD-05 (#180) has this shape too:
                # no whitelist row was written then, so its label has no whitelistUntil (optional on the row).
                label["reason"] = metadata
                if verb == "whitelist":
                    notes.append("whitelist from before FRD-05: no whitelistUntil on its audit row")
            results.append(finish(label, row, rows, backfilled_at, "; ".join(notes) or None))

        # Every action stamps reviewedAt, the same verb again included (the stamp of HandleFraudActionAsync).
        previous_verb, previous_time = verb, labelled_at
    return results


def plan_dispute(escrow_id, rows, escrow, backfilled_at):
    """The label of an escrow's dispute verdict, from what the escrow stored: what
    AdminFunction.RepairDisputeLabelAsync writes on a replay, with the fields of AdminFunction.DisputeLabel."""
    decisions = decisions_of(rows, (DISPUTE_ACTION,), escrow_id)
    last = decisions[-1] if decisions else None

    def unresolvable(detail):
        return [skip("unresolvable", CASE_DISPUTE, escrow_id, last, detail)]

    if labels_of(rows, CASE_DISPUTE, escrow_id):
        return [skip("already_labelled", CASE_DISPUTE, escrow_id, last)]
    if last is None:
        return []
    if not escrow:
        return unresolvable("the escrow row was not found")
    resolved_at = text(escrow, "disputeResolvedAt")
    if text(escrow, "disputedBy") is None:
        return [skip("not_a_raised_dispute", CASE_DISPUTE, escrow_id, last)]  # RepairDisputeLabelAsync
    if resolved_at is None or parse_ticks(resolved_at) is None:
        return unresolvable("the escrow stores no disputeResolvedAt")
    resolution = text(escrow, "disputeResolution")
    if resolution not in DISPUTE_VERDICTS:
        return unresolvable(f"the escrow's disputeResolution is {resolution!r}")
    # The audit row's metadata is the verdict (HandleResolveDisputeAsync).
    said = sorted({text(d, "metadata") or "" for d in decisions})
    if said != [resolution]:
        return unresolvable(f"the audit rows say {said}, the escrow says {resolution!r}")
    # The verdict the escrow stores is the one an audit row records: written in the same request.
    resolved = parse_ticks(resolved_at)
    row = min(decisions, key=lambda d: abs((parse_ticks(d["timestamp"]) or 0) - resolved))
    if abs((parse_ticks(row["timestamp"]) or 0) - resolved) > MATCH_WINDOW_TICKS:
        return unresolvable("no audit row is from the time of the escrow's disputeResolvedAt")

    order_id = text(escrow, "orderId")
    stored_id = text(escrow, "escrowId") or escrow_id
    label = {
        "case": CASE_DISPUTE,
        "caseId": stored_id,
        "outcome": resolution,                          # DisputeLabel: Outcome
        "source": DISPUTE_ACTION,                       # DisputeLabel: Source
        "transactionId": order_id or stored_id,         # the audit row's partition, RepairDisputeLabelAsync
        "keyTime": resolved_at,                         # DisputeLabel: KeyTime
        "adminId": text(escrow, "disputeResolvedBy") or text(row, "actor") or "admin",  # RepairDisputeLabelAsync
        "labelledAt": resolved_at,                      # DisputeLabel: LabelledAt
        "reason": text(escrow, "disputeDispositionReason"),  # the notes were never stored
        "orderId": order_id,
        "escrowId": stored_id,
        "deliveryId": text(escrow, "deliveryId"),
        "buyerId": text(escrow, "buyerId"),
        "farmerId": text(escrow, "farmerId"),
        "truckerId": text(escrow, "truckerId"),
        "amount": escrow.get("amount") if isinstance(escrow.get("amount"), Number) else None,
        "disputedBy": text(escrow, "disputedBy"),
        "disposition": text(escrow, "disputeDisposition"),
    }
    return [finish(label, row, rows, backfilled_at)]


HELD_EXIT_METADATA = re.compile(r"reason=(.*);holdReason=[^;]*;heldFor=[^;]*;heldFromStatus=[^;]*", re.DOTALL)


def plan_held(escrow_id, rows, escrow, backfilled_at):
    """The label of an admin exit of a held escrow, as EscrowService.LabelHeldExitAsync writes it once the
    exit has committed the money (server/src/Services/EscrowService.cs). The audit row is written on every
    attempt, before the money moves (EscrowService.RecordHeldExitAsync), so the exit counts only when the
    escrow row shows it settled, and the last attempt must be the exit that leads to that status. The admin,
    the decision time and the reason come from that last attempt row: when an earlier attempt of the same
    kind was the one that settled, they are a later admin's, while the outcome is still right."""
    attempts = decisions_of(rows, tuple(HELD_EXIT), escrow_id)
    last = attempts[-1] if attempts else None

    def unresolvable(detail):
        return [skip("unresolvable", CASE_HELD_ESCROW, escrow_id, last, detail)]

    if labels_of(rows, CASE_HELD_ESCROW, escrow_id):
        return [skip("already_labelled", CASE_HELD_ESCROW, escrow_id, last)]
    if last is None:
        return []
    if not escrow:
        return unresolvable("the escrow row was not found")
    status = text(escrow, "status")
    if status == "held":
        return [skip("still_held", CASE_HELD_ESCROW, escrow_id, last)]
    op, outcome = HELD_EXIT[last["action"]]
    if SETTLED_EXIT.get(status) is None:
        return unresolvable(f"the escrow is {status!r}, which does not say how it left the hold")
    if SETTLED_EXIT[status] != op:
        return unresolvable(f"the last exit attempt is a {op}, the escrow is {status!r}")
    held_at = text(escrow, "heldAt")
    if held_at is None or parse_ticks(held_at) is None:
        return unresolvable("the escrow stores no heldAt")
    if parse_ticks(last["timestamp"]) is None:
        return unresolvable("the exit audit row has no usable time")

    order_id = text(escrow, "orderId")
    stored_id = text(escrow, "escrowId") or escrow_id
    reason = HELD_EXIT_METADATA.fullmatch(text(last, "metadata") or "")
    label = {
        "case": CASE_HELD_ESCROW,
        "caseId": stored_id,
        "outcome": outcome,                              # LabelHeldExitAsync: Outcome
        "source": last["action"],                        # LabelHeldExitAsync: Source
        "transactionId": order_id or stored_id,          # LabelHeldExitAsync: TransactionId
        "keyTime": held_at,                              # LabelHeldExitAsync: KeyTime
        "adminId": text(last, "actor") or "admin",       # LabelHeldExitAsync: AdminId
        "labelledAt": last["timestamp"],                 # LabelHeldExitAsync: LabelledAt, to within the request
        "reason": reason.group(1) if reason else None,   # RecordHeldExitAsync (the row's metadata)
        "orderId": order_id,
        "escrowId": stored_id,
        "deliveryId": text(escrow, "deliveryId"),
        "buyerId": text(escrow, "buyerId"),
        "farmerId": text(escrow, "farmerId"),
        "truckerId": text(escrow, "truckerId"),
        "amount": escrow.get("amount") if isinstance(escrow.get("amount"), Number) else None,
        "holdReason": text(escrow, "holdReason"),
        "heldFor": text(escrow, "heldFor"),
        "heldFromStatus": text(escrow, "heldFromStatus"),
        "heldAt": held_at,
    }
    return [finish(label, last, rows, backfilled_at)]


# ── AWS calls (the AWS CLI, replaced by a fake in the tests) ───────────────────────────────────────────

def compact(value):
    return json.dumps(value, separators=(",", ":"), ensure_ascii=False)


class Aws:
    def __init__(self, run, region, profile):
        self.other_actions = collections.Counter()
        self._run = run
        self._tail = ["--output", "json", "--region", region] + (["--profile", profile] if profile else [])

    def call(self, *args):
        return self._run(["aws", "dynamodb", *args, *self._tail])

    def scan_cases(self, table, page_size, max_pages, limit):
        """The cases that have a decision row: {(case, entityId): {transactionId, ...}}, in the order found.
        Returns (cases, truncated). Only ids are kept."""
        names = {"#a": "action"}
        values = {":fraud": {"S": "fraud_"}, ":dispute": {"S": DISPUTE_ACTION}}
        cases, start_key, pages = collections.OrderedDict(), None, 0
        self.other_actions = collections.Counter()
        while True:
            args = ["scan", "--table-name", table,
                    "--filter-expression", "begins_with(#a, :fraud) OR #a = :dispute",
                    "--projection-expression", "transactionId, entityId, #a",
                    "--expression-attribute-names", compact(names),
                    "--expression-attribute-values", compact(values),
                    "--limit", str(page_size), "--no-paginate"]
            if start_key:
                args += ["--exclusive-start-key", compact(start_key)]
            page = self.call(*args)
            pages += 1
            for item in page.get("Items", []):
                row = plain(item)
                case, entity_id, partition = FAMILY.get(row.get("action")), text(row, "entityId"), text(row, "transactionId")
                if case is None:
                    self.other_actions[str(row.get("action"))] += 1  # reported in the summary, never labelled
                    continue
                if entity_id is None or partition is None:
                    continue
                key = (case, entity_id)
                if key not in cases and limit is not None and len(cases) >= limit:
                    continue
                cases.setdefault(key, set()).add(partition)
            start_key = page.get("LastEvaluatedKey") or None
            if not start_key or (limit is not None and len(cases) >= limit):
                return cases, False
            if pages >= max_pages:
                return cases, True

    def partition(self, table, transaction_id):
        """Every row of one audit partition, read consistently."""
        rows, start_key = [], None
        while True:
            args = ["query", "--table-name", table,
                    "--key-condition-expression", "transactionId = :id",
                    "--expression-attribute-values", compact({":id": {"S": transaction_id}}),
                    "--consistent-read", "--no-paginate"]
            if start_key:
                args += ["--exclusive-start-key", compact(start_key)]
            page = self.call(*args)
            rows += [plain(item) for item in page.get("Items", [])]
            start_key = page.get("LastEvaluatedKey") or None
            if not start_key:
                return rows

    def signal(self, table, user_id, signal_key):
        page = self.call("get-item", "--table-name", table, "--consistent-read",
                         "--key", compact({"userId": {"S": user_id}, "signalKey": {"S": signal_key}}))
        return plain(page.get("Item")) or None

    def escrow(self, table, escrow_id):
        page = self.call("query", "--table-name", table,
                         "--key-condition-expression", "escrowId = :id",
                         "--expression-attribute-values", compact({":id": {"S": escrow_id}}),
                         "--consistent-read", "--limit", "1", "--no-paginate")
        items = page.get("Items", [])
        return plain(items[0]) if items else None

    def put_label(self, table, entry):
        """True when written, False when a row is already at the key. Never overwrites."""
        try:
            self.call("put-item", "--table-name", table, "--item", compact(marshal(entry)),
                      "--condition-expression", "attribute_not_exists(transactionId)")
            return True
        except BackfillError as e:
            if "ConditionalCheckFailedException" in str(e):
                return False
            raise


def aws_runner(cmd):
    """Run one AWS CLI call and return its JSON answer."""
    try:
        done = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
    except (OSError, subprocess.TimeoutExpired) as e:
        raise BackfillError(f"aws call failed: {type(e).__name__}")
    if done.returncode != 0:
        raise BackfillError(f"aws {cmd[2]} failed ({done.returncode}): {done.stderr.strip()[:500]}")
    try:
        return json.loads(done.stdout or "{}")
    except ValueError:
        raise BackfillError(f"aws {cmd[2]} answered something that is not JSON: {done.stdout.strip()[:200]!r}")


def plan_case(aws, args, case, entity_id, partitions, backfilled_at):
    """Read one case and plan its labels."""
    if case == CASE_FRAUD_SIGNAL:
        rows = aws.partition(args.audit_table, entity_id)  # a fraud row's partition is the signal id
        parsed = parse_signal_id(entity_id)
        signal_row = aws.signal(args.signals_table, *parsed) if parsed else None
        return plan_fraud(entity_id, rows, signal_row, backfilled_at)

    escrow = aws.escrow(args.escrows_table, entity_id)
    # The label's partition is the escrow's order, else the escrow: read it too, so a label there is seen.
    wanted = set(partitions) | {text(escrow, "orderId") or entity_id}
    rows = [row for partition in sorted(wanted) for row in aws.partition(args.audit_table, partition)]
    planner = plan_dispute if case == CASE_DISPUTE else plan_held
    return planner(entity_id, rows, escrow, backfilled_at)


def public_line(result, status):
    """One output line: ids and codes only, never the reason text, IP or correlation id."""
    if result["kind"] == "skip":
        return {"result": "skip", "why": result["why"], "case": result["case"], "caseId": result["caseId"],
                "action": result["action"], "auditTimestamp": result["at"], "detail": result["detail"]}
    entry = result["entry"]
    return {"result": status, "case": entry["labelCase"], "caseId": entry["labelCaseId"],
            "outcome": entry["labelOutcome"], "source": entry["labelSource"],
            "transactionId": entry["transactionId"], "timestamp": entry["timestamp"],
            "labelledAt": entry["labelledAt"], "adminId": entry["actor"], "hasReason": "labelReason" in entry,
            "note": result["note"]}


def parse_args(argv):
    p = argparse.ArgumentParser(
        description="Backfill FRD-18 outcome labels from past audit rows (FUP-143). Dry run unless --apply. "
                    "Reads the transaction-audit table with one full Scan (no index on action): a one-off.")
    p.add_argument("--env", choices=sorted(ENVIRONMENTS))
    p.add_argument("--audit-table")
    p.add_argument("--signals-table")
    p.add_argument("--escrows-table")
    p.add_argument("--region")
    p.add_argument("--profile")
    p.add_argument("--apply", action="store_true", help="write the labels (default: dry run)")
    p.add_argument("--limit", type=int, help="process only the first N cases the Scan finds")
    p.add_argument("--page-size", type=int, default=500)
    p.add_argument("--max-pages", type=int, default=5000)
    args = p.parse_args(argv)
    if args.env:
        prefix, region = ENVIRONMENTS[args.env]
        args.audit_table = args.audit_table or f"{prefix}-transaction-audit"
        args.signals_table = args.signals_table or f"{prefix}-fraud-signals"
        args.escrows_table = args.escrows_table or f"{prefix}-escrows"
        args.region = args.region or region
    if not (args.audit_table and args.signals_table and args.escrows_table and args.region):
        raise UsageError("give --env, or --audit-table, --signals-table, --escrows-table and --region")
    if args.limit is not None and args.limit < 1:
        raise UsageError("--limit must be at least 1")
    if not 1 <= args.page_size <= 1000:
        raise UsageError("--page-size must be 1 to 1000")
    if args.max_pages < 1:
        raise UsageError("--max-pages must be at least 1")
    return args


def main(argv=None, run=aws_runner, stdout=None, stderr=None, now=None):
    stdout = stdout or sys.stdout
    stderr = stderr or sys.stderr
    try:
        args = parse_args(argv if argv is not None else sys.argv[1:])
    except UsageError as e:
        print(f"usage error: {e}", file=stderr)
        return 2

    backfilled_at = now or format_ticks(int(datetime.datetime.now(datetime.timezone.utc).timestamp()) * 10_000_000)
    aws = Aws(run, args.region, args.profile)
    mode = "APPLY" if args.apply else "DRY RUN"
    print(f"{mode}: scanning {args.audit_table} for decision rows (a full table Scan)", file=stderr)
    try:
        cases, truncated = aws.scan_cases(args.audit_table, args.page_size, args.max_pages, args.limit)
    except BackfillError as e:
        print(f"backfill failed: {e}", file=stderr)
        return 1

    counts = collections.Counter()
    failed = 0
    for (case, entity_id), partitions in cases.items():
        try:
            results = plan_case(aws, args, case, entity_id, partitions, backfilled_at)
        except BackfillError as e:
            failed += 1
            counts[("failed", case, "read")] += 1
            print(compact({"result": "failed", "case": case, "caseId": entity_id, "detail": str(e)}), file=stdout)
            continue
        for result in results:
            if result["kind"] == "skip":
                counts[("skip", result["why"], result["action"] or "-")] += 1
                print(compact(public_line(result, "skip")), file=stdout)
                continue
            status = "would_write"
            if args.apply:
                try:
                    status = "written" if aws.put_label(args.audit_table, result["entry"]) else "exists"
                except BackfillError as e:
                    failed += 1
                    status = "failed"
                    print(f"write failed for {case} {entity_id}: {e}", file=stderr)
            if status == "exists":
                counts[("skip", "already_labelled", result["action"])] += 1
            else:
                counts[(status, result["action"], result["entry"]["labelOutcome"])] += 1
            print(compact(public_line(result, status)), file=stdout)

    print(f"{mode} summary for {args.audit_table}: {len(cases)} cases with a decision row", file=stderr)
    for (status, first, second), count in sorted(counts.items()):
        if status == "skip":
            print(f"  skipped  {first:<22} {second:<24} {count}", file=stderr)
        else:
            print(f"  {status:<12} {first:<24} {second:<18} {count}", file=stderr)
    for why in sorted({first for (status, first, _) in counts if status == "skip"}):
        print(f"  {why}: {SKIP_REASONS[why]}", file=stderr)
    for action, count in sorted(aws.other_actions.items()):
        what = "not an admin decision" if action in NOT_A_DECISION else "UNKNOWN to this script: check what it decides"
        print(f"  not examined  {action:<24} {count} rows ({what}; never labelled)", file=stderr)
    if not args.apply:
        print("nothing was written; run again with --apply to write the would_write rows", file=stderr)
    if args.limit is not None and len(cases) >= args.limit:
        print(f"stopped at --limit {args.limit}: more cases may exist", file=stderr)
    if failed:
        print(f"{failed} read or write calls failed; fix the cause and run again (a re-run skips what is labelled)",
              file=stderr)
        return 1
    if truncated:
        print(f"stopped at --max-pages {args.max_pages}: the Scan did not reach the end; raise --max-pages", file=stderr)
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
