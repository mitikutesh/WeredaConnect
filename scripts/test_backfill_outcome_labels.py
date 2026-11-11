#!/usr/bin/env python3
"""Tests for backfill_outcome_labels.py (FUP-143): the mapping of each audit action to the label the
server writes, the label's key, the skips, and that a re-run or an overlap with a live label writes
nothing. No network: the AWS CLI is replaced by a fake that holds the three tables in memory.

The audit row fixtures have the shape TransactionAuditWriter.BuildEntry writes, with the values of the
server tests (AdminFraudActionTests, AdminFunctionTests, EscrowServiceFraudHoldTests,
OutcomeLabelWriterTests).

Run from the repo root: python3 -I scripts/test_backfill_outcome_labels.py
"""
import base64
import io
import json
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import backfill_outcome_labels as bol  # noqa: E402

NOW = "2026-11-11T09:00:00.0000000Z"
USER = "user-7"
SIGNAL_KEY = "2026-10-01T10:00:00.0000000Z#gps_spoofing"


def signal_id(user=USER, key=SIGNAL_KEY):
    """FraudDetectionService.BuildSignalId."""
    return base64.b64encode(f"{user}|{key}".encode()).decode().rstrip("=").replace("+", "-").replace("/", "_")


SIGNAL = signal_id()


def audit(transaction_id, entity_type, entity_id, action, timestamp, from_state=None, to_state=None,
          metadata=None, actor="admin-1"):
    """A transaction-audit row as TransactionAuditWriter.BuildEntry writes it."""
    row = {"auditId": f"txaudit-{action}-{timestamp}", "transactionId": transaction_id, "entityType": entity_type,
           "entityId": entity_id, "action": action, "actor": actor, "timestamp": timestamp, "actorRole": "admin",
           "ipAddress": "196.188.0.1", "correlationId": "corr-1"}
    if from_state:
        row["fromState"] = from_state
    if to_state:
        row["toState"] = to_state
    if metadata:
        row["metadata"] = metadata
    return row


def fraud_row(verb, timestamp, metadata=None, sid=SIGNAL):
    return audit(sid, "fraud", sid, f"fraud_{verb}", timestamp, None, verb, metadata)


def signal_row(**extra):
    row = {"userId": USER, "signalKey": SIGNAL_KEY, "recordType": "signal", "signalId": SIGNAL,
           "signalType": "gps_spoofing", "status": "pending", "entityType": "order", "entityId": "ord-9",
           "createdAt": "2026-10-01T10:20:30.1234567Z"}
    row.update(extra)
    return row


def escrow_row(**extra):
    row = {"escrowId": "escrow-order-ord-1", "createdAt": "2026-10-01T08:00:00.0000000Z", "orderId": "ord-1",
           "deliveryId": "del-1", "buyerId": "buyer-1", "farmerId": "farmer-1", "truckerId": "trucker-1",
           "amount": bol.Number("8000.50"), "status": "completed"}
    row.update(extra)
    return row


def dispute_escrow(resolution="buyer_wins", **extra):
    row = escrow_row(status="refunded" if resolution == "buyer_wins" else "completed", disputedBy="buyer-1",
                     disputeResolution=resolution, disputeResolvedAt="2026-11-01T10:00:00.0000000Z",
                     disputeResolvedBy="admin-1")
    row.update(extra)
    return row


def dispute_audit(resolution="buyer_wins", timestamp="2026-11-01T10:00:00.2000000Z"):
    return audit("ord-1", "escrow", "escrow-order-ord-1", "admin_dispute_resolved", timestamp, "disputed",
                 "dispute_resolved", resolution)


def held_escrow(status="completed", **extra):
    row = escrow_row(status=status, holdReason="fraud_confirmed", heldFor="release", heldFromStatus="escrowed",
                     heldAt="2026-10-20T06:00:00.0000000Z")
    row.update(extra)
    return row


def held_audit(op="release", timestamp="2026-10-21T09:00:00.0000000Z", reason="Checked the delivery; photos fine."):
    return audit("ord-1", "escrow", "escrow-order-ord-1", f"fraud_hold_{op}", timestamp, "held", "held",
                 f"reason={reason};holdReason=fraud_confirmed;heldFor=release;heldFromStatus=escrowed")


def only(results):
    assert len(results) == 1, results
    return results[0]


class TimeTests(unittest.TestCase):
    def test_key_is_the_key_time_in_utc_plus_the_slot_else_the_decision_time(self):
        # The cases of OutcomeLabelWriterTests.TheKey_IsTheKeyTimeInUtc_ElseTheDecisionTime.
        decided = "2026-11-02T08:30:00.0000000Z"
        self.assertEqual(bol.key_timestamp("2026-11-01T10:00:00Z", decided, "buyer_wins"), "2026-11-01T10:00:00.0000004Z")
        self.assertEqual(bol.key_timestamp("2026-11-01T13:00:00+03:00", decided, "buyer_wins"),
                         "2026-11-01T10:00:00.0000004Z")
        self.assertEqual(bol.key_timestamp("not a time", decided, "buyer_wins"), "2026-11-02T08:30:00.0000004Z")

    def test_no_time_at_all_gives_no_key(self):
        self.assertIsNone(bol.key_timestamp(None, "", "buyer_wins"))

    def test_slots_are_the_servers(self):
        # OutcomeLabels.Slot.
        self.assertEqual(bol.SLOTS, {"false_positive": 1, "fraud_confirmed": 2, "whitelisted": 3, "buyer_wins": 4,
                                     "farmer_wins": 5, "hold_released": 6, "hold_refunded": 7})
        self.assertEqual(bol.key_timestamp("2026-11-01T10:00:00Z", None, "partial_refund"), "2026-11-01T10:00:00.0000008Z")

    def test_the_slot_carries_over_a_second(self):
        self.assertEqual(bol.key_timestamp("2026-12-31T23:59:59.9999999Z", None, "fraud_confirmed"),
                         "2027-01-01T00:00:00.0000001Z")

    def test_signal_id_round_trip(self):
        self.assertEqual(bol.parse_signal_id(SIGNAL), (USER, SIGNAL_KEY))
        self.assertIsNone(bol.parse_signal_id("!!"))
        self.assertIsNone(bol.parse_signal_id(base64.b64encode(b"nokey").decode()))


class EntryTests(unittest.TestCase):
    def test_entry_is_the_servers_row_plus_the_backfill_marks(self):
        # The row of OutcomeLabelWriterTests.Write_AppendsOneRowIfAbsent_InTheCasePartition_OnTheLabelStream.
        entry = bol.build_entry({
            "case": "dispute", "caseId": "escrow-order-ord-1", "outcome": "buyer_wins",
            "source": "admin_dispute_resolved", "transactionId": "ord-1", "keyTime": "2026-11-01T10:00:00.0000000Z",
            "adminId": "admin-1", "labelledAt": "2026-11-02T08:30:00.0000000Z",
            "reason": "  Photos show spoiled maize.  ", "orderId": "ord-1", "truckerId": None,
            "amount": bol.Number("8000"), "signalIds": []}, NOW)
        self.assertEqual(entry, {
            "auditId": "label-dispute-escrow-order-ord-1-buyer_wins-20261101T1000000000004Z",
            "transactionId": "ord-1", "timestamp": "2026-11-01T10:00:00.0000004Z", "entityType": "label",
            "entityId": "outcome-labels", "action": "outcome_label", "toState": "buyer_wins", "actor": "admin-1",
            "actorRole": "admin", "metadata": "case=dispute;outcome=buyer_wins", "labelVersion": 1,
            "labelCase": "dispute", "labelCaseId": "escrow-order-ord-1", "labelOutcome": "buyer_wins",
            "labelSource": "admin_dispute_resolved", "labelledAt": "2026-11-02T08:30:00.0000000Z",
            "labelReason": "Photos show spoiled maize.", "orderId": "ord-1", "amount": "8000",
            "labelBackfill": "FUP-143", "backfilledAt": NOW})

    def test_marshal_uses_the_servers_types(self):
        item = bol.marshal({"labelVersion": 1, "amount": bol.Number("8000.50"), "signalIds": ["s1"], "actor": "a"})
        self.assertEqual(item, {"labelVersion": {"N": "1"}, "amount": {"N": "8000.50"},
                                "signalIds": {"L": [{"S": "s1"}]}, "actor": {"S": "a"}})


class FraudTests(unittest.TestCase):
    def test_each_verb_maps_to_the_servers_outcome(self):
        # OutcomeLabels.FraudOutcome.
        for verb, outcome, slot in (("approve", "false_positive", 1), ("reject", "fraud_confirmed", 2)):
            rows = [fraud_row(verb, "2026-10-02T08:00:00.5000000Z", "Looks honest.")]
            stamped = signal_row(status="reviewed", resolution=verb, reviewedAt="2026-10-02T08:00:00.1000000Z")
            entry = only(bol.plan_fraud(SIGNAL, rows, stamped, NOW))["entry"]
            self.assertEqual(entry["labelOutcome"], outcome)
            self.assertEqual(entry["labelSource"], f"fraud_{verb}")
            self.assertEqual(entry["transactionId"], SIGNAL)
            self.assertEqual(entry["labelCaseId"], SIGNAL)
            # First decision: keyed on the signal's createdAt.
            self.assertEqual(entry["timestamp"], f"2026-10-01T10:20:30.123456{7 + slot}Z")
            # The decision time is the stamp on the signal row.
            self.assertEqual(entry["labelledAt"], "2026-10-02T08:00:00.1000000Z")
            self.assertEqual(entry["labelReason"], "Looks honest.")
            self.assertEqual(entry["subjectUserId"], USER)
            self.assertEqual(entry["signalIds"], [SIGNAL])
            self.assertEqual(entry["signalType"], "gps_spoofing")
            self.assertEqual((entry["signalEntityType"], entry["signalEntityId"], entry["orderId"]),
                             ("order", "ord-9", "ord-9"))
            self.assertEqual(entry["signalCreatedAt"], "2026-10-01T10:20:30.1234567Z")
            self.assertEqual(entry["actor"], "admin-1")
            self.assertNotIn("whitelistUntil", entry)

    def test_whitelist_reads_its_end_and_notes_from_the_metadata(self):
        rows = [fraud_row("whitelist", "2026-10-02T08:00:00Z",
                          "whitelistUntil=2026-11-01T08:00:00.0000000Z;notes=Known co-op; two lines")]
        entry = only(bol.plan_fraud(SIGNAL, rows, signal_row(), NOW))["entry"]
        self.assertEqual(entry["labelOutcome"], "whitelisted")
        self.assertEqual(entry["whitelistUntil"], "2026-11-01T08:00:00.0000000Z")
        self.assertEqual(entry["labelReason"], "Known co-op; two lines")

        no_notes = [fraud_row("whitelist", "2026-10-02T08:00:00Z", "whitelistUntil=2026-11-01T08:00:00.0000000Z")]
        entry = only(bol.plan_fraud(SIGNAL, no_notes, signal_row(), NOW))["entry"]
        self.assertNotIn("labelReason", entry)

    def test_whitelist_from_before_frd05_is_labelled_without_its_end(self):
        # Before FRD-05 the audit row's metadata was the notes alone, or absent without notes.
        result = only(bol.plan_fraud(SIGNAL, [fraud_row("whitelist", "2026-10-02T08:00:00Z", "just notes")],
                                     signal_row(), NOW))
        self.assertEqual(result["kind"], "write")
        entry = result["entry"]
        self.assertEqual((entry["labelOutcome"], entry["labelSource"]), ("whitelisted", "fraud_whitelist"))
        self.assertEqual(entry["timestamp"], "2026-10-01T10:20:30.1234570Z")  # createdAt plus slot 3
        self.assertEqual(entry["labelReason"], "just notes")
        self.assertNotIn("whitelistUntil", entry)
        self.assertIn("before FRD-05", result["note"])

        result = only(bol.plan_fraud(SIGNAL, [fraud_row("whitelist", "2026-10-02T08:00:00Z")], signal_row(), NOW))
        self.assertEqual(result["kind"], "write")
        self.assertEqual(result["entry"]["labelOutcome"], "whitelisted")
        self.assertNotIn("whitelistUntil", result["entry"])
        self.assertNotIn("labelReason", result["entry"])
        self.assertIn("before FRD-05", result["note"])

    def test_a_current_whitelist_has_no_note(self):
        rows = [fraud_row("whitelist", "2026-10-02T08:00:00Z", "whitelistUntil=2026-11-01T08:00:00.0000000Z")]
        self.assertIsNone(only(bol.plan_fraud(SIGNAL, rows, signal_row(), NOW))["note"])

    def test_escrow_and_delivery_entities_link_as_the_server_does(self):
        rows = [fraud_row("approve", "2026-10-02T08:00:00Z")]
        entry = only(bol.plan_fraud(SIGNAL, rows, signal_row(entityType="escrow", entityId="escrow-order-ord-4"), NOW))["entry"]
        self.assertEqual((entry["escrowId"], entry["orderId"]), ("escrow-order-ord-4", "ord-4"))
        entry = only(bol.plan_fraud(SIGNAL, rows, signal_row(entityType="delivery", entityId="del-4"), NOW))["entry"]
        self.assertEqual(entry["deliveryId"], "del-4")
        self.assertNotIn("orderId", entry)

    def test_change_of_mind_writes_one_label_each_keyed_on_the_decision_before(self):
        rows = [fraud_row("reject", "2026-10-02T08:00:00.0000000Z"), fraud_row("approve", "2026-10-03T09:00:00.4000000Z")]
        stamped = signal_row(status="reviewed", resolution="approve", reviewedAt="2026-10-03T09:00:00.0000000Z")
        first, second = bol.plan_fraud(SIGNAL, rows, stamped, NOW)
        self.assertEqual(first["entry"]["labelOutcome"], "fraud_confirmed")
        self.assertEqual(first["entry"]["timestamp"], "2026-10-01T10:20:30.1234569Z")
        self.assertEqual(first["entry"]["labelledAt"], "2026-10-02T08:00:00.0000000Z")  # the audit row's time
        self.assertEqual(second["entry"]["labelOutcome"], "false_positive")
        self.assertEqual(second["entry"]["timestamp"], "2026-10-02T08:00:00.0000001Z")  # reject's time plus slot 1
        self.assertEqual(second["entry"]["labelledAt"], "2026-10-03T09:00:00.0000000Z")  # the stamp

    def test_same_verb_again_writes_no_label(self):
        rows = [fraud_row("approve", "2026-10-02T08:00:00Z"), fraud_row("approve", "2026-10-02T08:05:00Z")]
        first, second = bol.plan_fraud(SIGNAL, rows, signal_row(), NOW)
        self.assertEqual(first["kind"], "write")
        self.assertEqual((second["kind"], second["why"]), ("skip", "same_decision"))

    def test_signal_row_gone_uses_the_signal_key(self):
        result = only(bol.plan_fraud(SIGNAL, [fraud_row("reject", "2026-10-02T08:00:00Z")], None, NOW))
        entry = result["entry"]
        self.assertEqual(entry["timestamp"], "2026-10-01T10:00:00.0000002Z")  # the hour in the key, slot 2
        self.assertEqual(entry["signalType"], "gps_spoofing")
        self.assertEqual(entry["signalCreatedAt"], "2026-10-01T10:00:00.0000000Z")
        self.assertNotIn("signalEntityId", entry)
        self.assertIn("TTL", result["note"])

    def test_live_label_is_not_duplicated(self):
        decision = fraud_row("reject", "2026-10-02T08:00:00.5000000Z")
        live = bol.build_entry({"case": "fraud_signal", "caseId": SIGNAL, "outcome": "fraud_confirmed",
                                "source": "fraud_reject", "transactionId": SIGNAL,
                                "keyTime": "2026-10-01T10:20:30.1234567Z", "adminId": "admin-1",
                                "labelledAt": "2026-10-02T08:00:00.1000000Z"}, NOW)
        result = only(bol.plan_fraud(SIGNAL, [decision, live], signal_row(), NOW))
        self.assertEqual((result["kind"], result["why"]), ("skip", "already_labelled"))

    def test_live_label_of_a_later_decision_leaves_the_earlier_one_to_backfill(self):
        # reject before FRD-18 (no label), approve after it (labelled live, keyed on the reject's reviewedAt).
        reject = fraud_row("reject", "2026-10-02T08:00:00.5000000Z")
        approve = fraud_row("approve", "2026-11-08T09:00:00.3000000Z")
        live = bol.build_entry({"case": "fraud_signal", "caseId": SIGNAL, "outcome": "false_positive",
                                "source": "fraud_approve", "transactionId": SIGNAL,
                                "keyTime": "2026-10-02T08:00:00.1000000Z", "adminId": "admin-1",
                                "labelledAt": "2026-11-08T09:00:00.0000000Z"}, NOW)
        first, second = bol.plan_fraud(SIGNAL, [reject, approve, live], signal_row(), NOW)
        self.assertEqual(first["entry"]["labelOutcome"], "fraud_confirmed")
        self.assertEqual((second["kind"], second["why"]), ("skip", "already_labelled"))

    def test_a_row_at_the_key_is_never_planned_again(self):
        decision = fraud_row("approve", "2026-10-02T08:00:00Z")
        squatter = {"transactionId": SIGNAL, "timestamp": "2026-10-01T10:20:30.1234568Z", "action": "other"}
        result = only(bol.plan_fraud(SIGNAL, [decision, squatter], signal_row(), NOW))
        self.assertEqual((result["kind"], result["why"]), ("skip", "already_labelled"))

    def test_bad_signal_id_and_wrong_row_kind_are_skipped(self):
        bad = fraud_row("approve", "2026-10-02T08:00:00Z", sid="!!")
        self.assertEqual(only(bol.plan_fraud("!!", [bad], None, NOW))["why"], "unresolvable")
        rows = [fraud_row("approve", "2026-10-02T08:00:00Z")]
        self.assertEqual(only(bol.plan_fraud(SIGNAL, rows, signal_row(recordType="whitelist"), NOW))["why"], "unresolvable")

    def test_action_and_to_state_that_disagree_are_skipped(self):
        row = fraud_row("approve", "2026-10-02T08:00:00Z")
        row["toState"] = "reject"
        self.assertEqual(only(bol.plan_fraud(SIGNAL, [row], signal_row(), NOW))["why"], "unresolvable")


class DisputeTests(unittest.TestCase):
    def test_verdict_label_is_built_from_the_escrow_like_the_servers_repair(self):
        for resolution, slot in (("buyer_wins", 4), ("farmer_wins", 5), ("partial_refund", 8)):
            escrow = dispute_escrow(resolution, disputeDisposition="goods_returned",
                                    disputeDispositionReason="Buyer sent the maize back.")
            entry = only(bol.plan_dispute("escrow-order-ord-1", [dispute_audit(resolution)], escrow, NOW))["entry"]
            self.assertEqual(entry["labelCase"], "dispute")
            self.assertEqual(entry["labelOutcome"], resolution)
            self.assertEqual(entry["labelSource"], "admin_dispute_resolved")
            self.assertEqual(entry["transactionId"], "ord-1")
            self.assertEqual(entry["labelCaseId"], "escrow-order-ord-1")
            self.assertEqual(entry["timestamp"], f"2026-11-01T10:00:00.000000{slot}Z")
            self.assertEqual(entry["labelledAt"], "2026-11-01T10:00:00.0000000Z")
            self.assertEqual(entry["actor"], "admin-1")
            self.assertEqual(entry["amount"], "8000.50")
            self.assertEqual(entry["disposition"], "goods_returned")
            self.assertEqual(entry["labelReason"], "Buyer sent the maize back.")
            self.assertEqual((entry["buyerId"], entry["farmerId"], entry["truckerId"], entry["deliveryId"],
                              entry["disputedBy"]), ("buyer-1", "farmer-1", "trucker-1", "del-1", "buyer-1"))
            self.assertEqual(entry["ipAddress"], "196.188.0.1")

    def test_an_escrow_without_an_order_keeps_its_own_partition(self):
        escrow = dispute_escrow(orderId="", escrowId="esc-7")
        row = audit("esc-7", "escrow", "esc-7", "admin_dispute_resolved", "2026-11-01T10:00:00.1Z", metadata="buyer_wins")
        entry = only(bol.plan_dispute("esc-7", [row], escrow, NOW))["entry"]
        self.assertEqual(entry["transactionId"], "esc-7")
        self.assertNotIn("orderId", entry)

    def test_existing_label_is_left_alone(self):
        rows = [dispute_audit(), {"action": "outcome_label", "labelCase": "dispute", "labelCaseId": "escrow-order-ord-1",
                                  "transactionId": "ord-1", "timestamp": "2026-11-01T10:00:00.0000004Z"}]
        result = only(bol.plan_dispute("escrow-order-ord-1", rows, dispute_escrow(), NOW))
        self.assertEqual((result["kind"], result["why"]), ("skip", "already_labelled"))

    def test_unresolvable_verdicts_are_skipped(self):
        def why(escrow, rows=None):
            result = only(bol.plan_dispute("escrow-order-ord-1", rows or [dispute_audit()], escrow, NOW))
            self.assertEqual(result["kind"], "skip")
            return result["why"]

        self.assertEqual(why(None), "unresolvable")
        self.assertEqual(why(dispute_escrow(disputedBy="")), "not_a_raised_dispute")
        self.assertEqual(why(dispute_escrow(disputeResolvedAt="")), "unresolvable")
        self.assertEqual(why(dispute_escrow(disputeResolution="something_else")), "unresolvable")
        self.assertEqual(why(dispute_escrow("farmer_wins")), "unresolvable")  # the audit row says buyer_wins
        self.assertEqual(why(dispute_escrow(), [dispute_audit(timestamp="2026-11-01T11:00:00Z")]), "unresolvable")


class HeldTests(unittest.TestCase):
    def test_each_exit_maps_to_the_servers_outcome(self):
        # OutcomeLabels.HeldExitOutcome.
        for op, status, outcome, slot in (("release", "completed", "hold_released", 6),
                                          ("release", "released", "hold_released", 6),
                                          ("refund", "refunded", "hold_refunded", 7)):
            entry = only(bol.plan_held("escrow-order-ord-1", [held_audit(op)], held_escrow(status), NOW))["entry"]
            self.assertEqual(entry["labelCase"], "held_escrow")
            self.assertEqual(entry["labelOutcome"], outcome)
            self.assertEqual(entry["labelSource"], f"fraud_hold_{op}")
            self.assertEqual(entry["transactionId"], "ord-1")
            self.assertEqual(entry["timestamp"], f"2026-10-20T06:00:00.000000{slot}Z")  # heldAt plus the slot
            self.assertEqual(entry["labelledAt"], "2026-10-21T09:00:00.0000000Z")
            self.assertEqual(entry["labelReason"], "Checked the delivery; photos fine.")
            self.assertEqual((entry["holdReason"], entry["heldFor"], entry["heldFromStatus"], entry["heldAt"]),
                             ("fraud_confirmed", "release", "escrowed", "2026-10-20T06:00:00.0000000Z"))
            self.assertEqual(entry["amount"], "8000.50")

    def test_the_last_attempt_labels_and_earlier_refused_ones_do_not(self):
        rows = [held_audit("refund", "2026-10-21T08:00:00.0000000Z"), held_audit("release", "2026-10-21T09:00:00.0000000Z")]
        entry = only(bol.plan_held("escrow-order-ord-1", rows, held_escrow("completed"), NOW))["entry"]
        self.assertEqual((entry["labelOutcome"], entry["labelledAt"]), ("hold_released", "2026-10-21T09:00:00.0000000Z"))

    def test_skips(self):
        def why(escrow, rows=None):
            result = only(bol.plan_held("escrow-order-ord-1", rows or [held_audit()], escrow, NOW))
            self.assertEqual(result["kind"], "skip")
            return result["why"]

        self.assertEqual(why(held_escrow("held")), "still_held")  # the attempt was refused, or is queued
        self.assertEqual(why(None), "unresolvable")
        self.assertEqual(why(held_escrow("refunded")), "unresolvable")  # last attempt was a release
        self.assertEqual(why(held_escrow("disputed")), "unresolvable")
        self.assertEqual(why(held_escrow(heldAt="")), "unresolvable")
        labelled = [held_audit(), {"action": "outcome_label", "labelCase": "held_escrow",
                                   "labelCaseId": "escrow-order-ord-1"}]
        self.assertEqual(why(held_escrow(), labelled), "already_labelled")


def to_item(row):
    return bol.marshal(row)


class FakeAws:
    """The three tables in memory, answering the AWS CLI calls the script makes."""

    def __init__(self, audit_rows, signals=(), escrows=(), page=2):
        self.audit = [dict(r) for r in audit_rows]
        self.signals = {(s["userId"], s["signalKey"]): s for s in signals}
        self.escrows = {e["escrowId"]: e for e in escrows}
        self.page = page
        self.calls = []
        self.puts = []

    def __call__(self, cmd):
        self.calls.append(cmd)
        op = cmd[2]
        opts = {cmd[i]: cmd[i + 1] for i in range(3, len(cmd) - 1) if cmd[i].startswith("--")}
        table = opts["--table-name"]
        if op == "scan":
            assert opts["--filter-expression"] == "begins_with(#a, :fraud) OR #a = :dispute"
            assert "--projection-expression" in opts
            values = json.loads(opts["--expression-attribute-values"])
            prefix, exact = values[":fraud"]["S"], values[":dispute"]["S"]
            start = json.loads(opts["--exclusive-start-key"])["i"]["N"] if "--exclusive-start-key" in opts else 0
            start = int(start)
            chunk = self.audit[start:start + self.page]
            answer = {"Items": [to_item({k: r[k] for k in ("transactionId", "entityId", "action") if k in r})
                                for r in chunk if r.get("action", "").startswith(prefix) or r.get("action") == exact]}
            if start + self.page < len(self.audit):
                answer["LastEvaluatedKey"] = {"i": {"N": str(start + self.page)}}
            return answer
        if op == "query" and table.endswith("transaction-audit"):
            key = json.loads(opts["--expression-attribute-values"])[":id"]["S"]
            return {"Items": [to_item(r) for r in self.audit if r["transactionId"] == key]}
        if op == "query":
            key = json.loads(opts["--expression-attribute-values"])[":id"]["S"]
            return {"Items": [to_item(self.escrows[key])] if key in self.escrows else []}
        if op == "get-item":
            key = json.loads(opts["--key"])
            row = self.signals.get((key["userId"]["S"], key["signalKey"]["S"]))
            return {"Item": to_item(row)} if row else {}
        if op == "put-item":
            assert opts["--condition-expression"] == "attribute_not_exists(transactionId)"
            item = bol.plain(json.loads(opts["--item"]))
            if any(r["transactionId"] == item["transactionId"] and r["timestamp"] == item["timestamp"] for r in self.audit):
                raise bol.BackfillError("aws put-item failed (254): An error occurred (ConditionalCheckFailedException)")
            self.audit.append(item)
            self.puts.append(item)
            return {}
        raise AssertionError(f"unexpected call {op}")


def world():
    other = audit("ord-1", "order", "ord-1", "status_change", "2026-09-30T08:00:00Z")
    rows = [other, fraud_row("reject", "2026-10-02T08:00:00.5000000Z", "Call +251911223344"), other,
            dispute_audit(), held_audit(op="release")]
    # One escrow per case so the dispute and the hold do not share a row.
    rows[4] = audit("ord-2", "escrow", "escrow-order-ord-2", "fraud_hold_release", "2026-10-21T09:00:00.0000000Z",
                    "held", "held", "reason=Checked it, fine to pay.;holdReason=risk_critical;heldFor=release;heldFromStatus=escrowed")
    escrows = [dispute_escrow(), held_escrow(escrowId="escrow-order-ord-2", orderId="ord-2")]
    return FakeAws(rows, [signal_row(status="reviewed", resolution="reject", reviewedAt="2026-10-02T08:00:00.1000000Z")],
                   escrows)


def run(aws, *argv):
    out, err = io.StringIO(), io.StringIO()
    code = bol.main(["--audit-table", "t-transaction-audit", "--signals-table", "t-fraud-signals",
                     "--escrows-table", "t-escrows", "--region", "eu-north-1", *argv], run=aws, stdout=out,
                    stderr=err, now=NOW)
    return code, [json.loads(line) for line in out.getvalue().splitlines()], err.getvalue()


class MainTests(unittest.TestCase):
    def test_dry_run_is_the_default_and_writes_nothing(self):
        aws = world()
        code, lines, err = run(aws)
        self.assertEqual(code, 0)
        self.assertEqual(aws.puts, [])
        self.assertFalse(any(c[2] == "put-item" for c in aws.calls))
        self.assertEqual(sorted((l["result"], l["outcome"]) for l in lines),
                         [("would_write", "buyer_wins"), ("would_write", "fraud_confirmed"), ("would_write", "hold_released")])
        self.assertIn("DRY RUN", err)
        self.assertIn("nothing was written", err)
        self.assertIn("full table Scan", err)

    def test_output_carries_no_free_text(self):
        code, lines, err = run(world())
        blob = json.dumps(lines) + err
        self.assertNotIn("+251911223344", blob)
        self.assertNotIn("196.188.0.1", blob)
        self.assertEqual({l["outcome"]: l["hasReason"] for l in lines},
                         {"fraud_confirmed": True, "hold_released": True, "buyer_wins": False})

    def test_apply_writes_each_label_once_and_a_rerun_writes_nothing(self):
        aws = world()
        code, lines, _ = run(aws, "--apply")
        self.assertEqual(code, 0)
        self.assertEqual(sorted(p["labelOutcome"] for p in aws.puts), ["buyer_wins", "fraud_confirmed", "hold_released"])
        self.assertTrue(all(l["result"] == "written" for l in lines))
        self.assertTrue(all(p["labelBackfill"] == "FUP-143" and p["entityId"] == "outcome-labels" for p in aws.puts))
        before = len(aws.audit)

        code, lines, err = run(aws, "--apply")
        self.assertEqual(code, 0)
        self.assertEqual(len(aws.audit), before)
        self.assertTrue(all((l["result"], l["why"]) == ("skip", "already_labelled") for l in lines))
        self.assertEqual(len(lines), 3)
        self.assertIn("already_labelled", err)

    def test_the_conditional_put_is_the_second_guard(self):
        # A label lands between the read and the write: the put answers "exists", nothing is overwritten.
        aws = world()
        real = aws.__call__

        def racing(cmd):
            if cmd[2] == "put-item":
                raise bol.BackfillError("aws put-item failed (254): An error occurred (ConditionalCheckFailedException)")
            return real(cmd)

        code, lines, err = run(racing, "--apply")
        self.assertEqual(code, 0)
        self.assertEqual([l["result"] for l in lines], ["exists"] * 3)
        self.assertEqual(aws.puts, [])
        self.assertIn("already_labelled", err)

    def test_never_updates_or_deletes(self):
        aws = world()
        run(aws, "--apply")
        self.assertEqual({c[2] for c in aws.calls}, {"scan", "query", "get-item", "put-item"})

    def test_limit_processes_only_the_first_cases(self):
        aws = world()
        code, lines, err = run(aws, "--apply", "--limit", "1")
        self.assertEqual(code, 0)
        self.assertEqual(len(aws.puts), 1)
        self.assertIn("--limit 1", err)

    def test_scan_pages_and_stops_at_max_pages(self):
        aws = world()
        code, lines, err = run(aws, "--max-pages", "1")
        self.assertEqual(code, 3)
        self.assertEqual(len([c for c in aws.calls if c[2] == "scan"]), 1)
        self.assertIn("--max-pages", err)
        aws = world()
        run(aws)
        self.assertEqual(len([c for c in aws.calls if c[2] == "scan"]), 3)  # 5 rows, 2 per page

    def test_a_failed_read_is_reported_and_exits_1(self):
        aws = world()
        real = aws.__call__

        def failing(cmd):
            if cmd[2] == "get-item":
                raise bol.BackfillError("aws get-item failed (254): AccessDenied")
            return real(cmd)

        code, lines, err = run(failing, "--apply")
        self.assertEqual(code, 1)
        self.assertEqual(len(aws.puts), 2)  # the other cases still go through
        self.assertTrue(any(l["result"] == "failed" for l in lines))

    def test_other_fraud_actions_are_counted_and_never_labelled(self):
        aws = world()
        aws.audit.append(audit("ord-2", "escrow", "escrow-order-ord-2", "fraud_hold", "2026-10-20T06:00:00Z", "escrowed", "held"))
        aws.audit.append(audit("x-1", "fraud", "x-1", "fraud_escalate", "2026-10-20T07:00:00Z"))
        code, lines, err = run(aws, "--apply")
        self.assertEqual(code, 0)
        self.assertEqual(len(aws.puts), 3)
        self.assertRegex(err, r"not examined\s+fraud_hold\s+1 rows \(not an admin decision")
        self.assertRegex(err, r"not examined\s+fraud_escalate\s+1 rows \(UNKNOWN")

    def test_an_answer_that_is_not_json_is_a_clear_failure(self):
        import subprocess
        import unittest.mock
        done = subprocess.CompletedProcess([], 0, stdout="<html>proxy</html>", stderr="")
        with unittest.mock.patch.object(bol.subprocess, "run", return_value=done):
            with self.assertRaisesRegex(bol.BackfillError, "not JSON"):
                bol.aws_runner(["aws", "dynamodb", "scan"])
            out, err = io.StringIO(), io.StringIO()
            code = bol.main(["--env", "dev"], stdout=out, stderr=err, now=NOW)
        self.assertEqual(code, 1)
        self.assertIn("not JSON", err.getvalue())

    def test_env_resolves_the_repo_table_names(self):
        args = bol.parse_args(["--env", "prod"])
        self.assertEqual((args.audit_table, args.signals_table, args.escrows_table, args.region),
                         ("wereda-connect-prod-transaction-audit", "wereda-connect-prod-fraud-signals",
                          "wereda-connect-prod-escrows", "me-south-1"))
        self.assertFalse(args.apply)
        self.assertEqual(bol.parse_args(["--env", "dev"]).region, "eu-north-1")

    def test_usage_errors_exit_2(self):
        out, err = io.StringIO(), io.StringIO()
        self.assertEqual(bol.main([], run=None, stdout=out, stderr=err), 2)
        self.assertEqual(bol.main(["--env", "dev", "--limit", "0"], run=None, stdout=out, stderr=err), 2)


if __name__ == "__main__":
    unittest.main()
