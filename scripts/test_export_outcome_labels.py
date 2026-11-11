#!/usr/bin/env python3
"""Tests for export_outcome_labels.py (FRD-18): the Query it sends, the paging bound, the
ids-only output and the refusals. No network: the AWS CLI is replaced by a fake runner.

Run from the repo root: python3 -I scripts/test_export_outcome_labels.py
"""
import csv
import io
import json
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import export_outcome_labels as eol  # noqa: E402


def S(v):
    return {"S": v}


def label_row(case="dispute", case_id="escrow-order-ord-1", outcome="buyer_wins", labelled_at="2026-11-02T08:00:00Z",
              **extra):
    """A label row as DynamoDB JSON, with every field the server may write (reason, IP included)."""
    row = {
        "auditId": S(f"label-{case}-{case_id}-{outcome}-x"),
        "transactionId": S("ord-1"),
        "timestamp": S("2026-11-01T10:00:00.0000004Z"),
        "entityType": S("label"),
        "entityId": S("outcome-labels"),
        "action": S("outcome_label"),
        "toState": S(outcome),
        "actor": S("admin-1"),
        "actorRole": S("admin"),
        "ipAddress": S("196.188.0.1"),
        "correlationId": S("corr-1"),
        "metadata": S(f"case={case};outcome={outcome}"),
        "labelVersion": {"N": "1"},
        "labelCase": S(case),
        "labelCaseId": S(case_id),
        "labelOutcome": S(outcome),
        "labelSource": S("admin_dispute_resolved"),
        "labelledAt": S(labelled_at),
        "labelReason": S("Called Abebe on +251911223344, the maize was wet."),
        "orderId": S("ord-1"),
        "escrowId": S(case_id),
        "buyerId": S("8b1c1f2e-0a4d-4f7e-9c11-3d5e7f9a0b12"),
        "farmerId": S("f3a9e2d1-77aa-4c3b-8e21-0f1e2d3c4b5a"),
        "amount": {"N": "8000.50"},
    }
    for key, value in extra.items():
        row[key] = value
    return row


class FakeAws:
    """Answers the Query pages in turn and records the commands."""

    def __init__(self, *pages):
        self.pages = list(pages)
        self.calls = []

    def __call__(self, cmd):
        self.calls.append(cmd)
        return self.pages.pop(0)


def arg(cmd, flag):
    return cmd[cmd.index(flag) + 1]


class QueryTest(unittest.TestCase):
    def test_reads_the_label_stream_of_the_entity_index_never_a_scan(self):
        cmd = eol.build_query("tbl", "eu-north-1", None, 100, None, None, None)
        self.assertEqual(cmd[:3], ["aws", "dynamodb", "query"])
        self.assertEqual(arg(cmd, "--index-name"), "EntityIndex")
        self.assertEqual(arg(cmd, "--key-condition-expression"), "entityId = :stream")
        self.assertEqual(json.loads(arg(cmd, "--expression-attribute-values")), {":stream": {"S": "outcome-labels"}})
        self.assertEqual(arg(cmd, "--limit"), "100")
        self.assertIn("--no-paginate", cmd)
        self.assertNotIn("--expression-attribute-names", cmd)

    def test_since_and_until_bound_the_key_time(self):
        cmd = eol.build_query("tbl", "eu-north-1", "ops", 50, "2026-11-01T00:00:00Z", "2026-11-30T00:00:00Z",
                              {"k": {"S": "v"}})
        self.assertEqual(arg(cmd, "--key-condition-expression"), "entityId = :stream AND #ts BETWEEN :since AND :until")
        self.assertEqual(json.loads(arg(cmd, "--expression-attribute-names")), {"#ts": "timestamp"})
        self.assertEqual(json.loads(arg(cmd, "--exclusive-start-key")), {"k": {"S": "v"}})
        self.assertEqual(arg(cmd, "--profile"), "ops")
        only_since = eol.build_query("tbl", "r", None, 50, "2026-11-01", None, None)
        self.assertEqual(arg(only_since, "--key-condition-expression"), "entityId = :stream AND #ts >= :since")

    def test_a_date_alone_bounds_whole_days_and_times_take_the_keys_form(self):
        since, until = eol.normalize_bound("since", "2026-11-01", False), eol.normalize_bound("until", "2026-11-30", True)
        self.assertEqual((since, until), ("2026-11-01T00:00:00.0000000Z", "2026-11-30T23:59:59.9999999Z"))
        # A label keyed late on the --until day is inside the range DynamoDB compares as strings.
        key = "2026-11-30T22:15:00.0000004Z"
        self.assertTrue(since <= key <= until)
        # A time takes the server's seven fraction digits, so a key a few ticks after it is not dropped.
        self.assertEqual(eol.normalize_bound("since", "2026-11-01T10:00:00Z", False), "2026-11-01T10:00:00.0000000Z")
        self.assertEqual(eol.normalize_bound("until", "2026-11-01T10:00Z", True), "2026-11-01T10:00:00.0000000Z")
        self.assertEqual(eol.normalize_bound("since", "2026-11-01T10:00:00.5Z", False), "2026-11-01T10:00:00.5000000Z")
        self.assertLess(eol.normalize_bound("since", "2026-11-01T10:00:00Z", False), "2026-11-01T10:00:00.0000004Z")

    def test_a_bound_that_is_not_a_utc_date_or_time_is_refused(self):
        for bad in ("yesterday", "2026-13-01", "2026-02-30", "2026-11-01T10:00:00", "2026-11-01T25:00:00Z",
                    "2026-11-01T10:00:00+03:00", "2026-11-01 10:00"):
            with self.assertRaises(eol.UsageError, msg=bad):
                eol.normalize_bound("until", bad, True)

    def test_pages_until_the_last_and_stops_at_the_bound(self):
        more = {"entityId": S("outcome-labels"), "timestamp": S("t"), "transactionId": S("x")}
        fake = FakeAws({"Items": [label_row()], "LastEvaluatedKey": more}, {"Items": [label_row(case_id="e2")]})
        records, truncated = eol.read_labels(fake, "tbl", "r", None, 1, 5)
        self.assertEqual([r["caseId"] for r in records], ["escrow-order-ord-1", "e2"])
        self.assertFalse(truncated)
        self.assertEqual(json.loads(arg(fake.calls[1], "--exclusive-start-key")), more)

        endless = FakeAws(*[{"Items": [], "LastEvaluatedKey": more} for _ in range(3)])
        records, truncated = eol.read_labels(endless, "tbl", "r", None, 1, 2)
        self.assertTrue(truncated)
        self.assertEqual(len(endless.calls), 2)


class RecordTest(unittest.TestCase):
    def test_ids_only_never_the_reason_the_ip_or_the_correlation(self):
        record = eol.to_record(label_row())
        self.assertEqual(record["case"], "dispute")
        self.assertEqual(record["outcome"], "buyer_wins")
        self.assertEqual(record["adminId"], "admin-1")
        self.assertEqual(record["amount"], 8000.5)
        self.assertEqual(record["labelVersion"], 1)
        self.assertTrue(record["hasReason"])
        text = json.dumps(record)
        for leaked in ("Abebe", "+251911223344", "196.188.0.1", "corr-1", "metadata"):
            self.assertNotIn(leaked, text)
        self.assertEqual(set(record), set(eol.COLUMNS) - {"final"})

    def test_a_backfilled_row_is_marked_and_a_live_one_is_not(self):
        # FUP-143: scripts/backfill_outcome_labels.py sets labelBackfill (and backfilledAt, not exported).
        self.assertIsNone(eol.to_record(label_row())["labelBackfill"])
        row = label_row(labelBackfill=S("FUP-143"), backfilledAt=S("2026-11-11T09:00:00.0000000Z"))
        record = eol.to_record(row)
        self.assertEqual(record["labelBackfill"], "FUP-143")
        self.assertNotIn("backfilledAt", record)
        self.assertIn("labelBackfill", eol.COLUMNS)
        csv_text = eol.render(eol.mark_final([record, eol.to_record(label_row())]), "csv")
        rows = list(csv.DictReader(io.StringIO(csv_text)))
        self.assertEqual([r["labelBackfill"] for r in rows], ["FUP-143", ""])

    def test_a_row_that_is_not_a_label_is_skipped(self):
        row = label_row()
        row["action"] = S("admin_dispute_resolved")
        self.assertIsNone(eol.to_record(row))

    def test_a_value_that_looks_like_a_phone_stops_the_export(self):
        for phone in ("+251911223344", "251911223344", "0911223344", "0711223344"):
            with self.assertRaises(eol.ExportError):
                eol.to_record(label_row(buyerId=S(phone)))
        with self.assertRaises(eol.ExportError):
            eol.to_record(label_row(signalIds={"L": [S("sig-1"), S("0911223344")]}))

    def test_signal_ids_stay_a_list(self):
        record = eol.to_record(label_row(case="fraud_signal", signalIds={"L": [S("c2lnLTE")]}))
        self.assertEqual(record["signalIds"], ["c2lnLTE"])

    def test_the_latest_decision_of_a_case_is_final(self):
        records = [
            eol.to_record(label_row(case="fraud_signal", case_id="sig", outcome="fraud_confirmed",
                                    labelled_at="2026-11-02T08:00:00Z")),
            eol.to_record(label_row(case="fraud_signal", case_id="sig", outcome="false_positive",
                                    labelled_at="2026-11-03T08:00:00Z")),
            eol.to_record(label_row()),
        ]
        eol.mark_final(records)
        self.assertEqual([r["final"] for r in records], [False, True, True])


def snapshot_fields(units=None):
    """The FRD-20 P1 snapshot attributes of a fraud label, as DynamoDB JSON."""
    units = {"PRICE_ANOMALY": 2, "SELF_TRADE": 1} if units is None else units
    return {
        "snapshotVersion": {"N": "1"},
        "snapshotWindowDays": {"N": "30"},
        "snapshotSignalUnits": {"M": {k: {"N": str(v)} for k, v in units.items()}},
        "snapshotCreditUnits": {"N": "3"},
        "snapshotCreditPoints": {"N": "15"},
        "snapshotTruncated": {"BOOL": False},
        "snapshotAccountAgeDays": {"N": "412"},
        "snapshotFaydaVerified": {"BOOL": True},
        "snapshotRole": S("farmer"),
        "snapshotCompletedEscrows": {"N": "9"},
        "snapshotCompletedEscrowsPartial": {"BOOL": False},
    }


class SnapshotTest(unittest.TestCase):
    def test_a_fraud_label_carries_its_feature_snapshot(self):
        record = eol.to_record(label_row(case="fraud_signal", outcome="fraud_confirmed", **snapshot_fields()))
        self.assertEqual(record["snapshotVersion"], 1)
        self.assertEqual(record["snapshotWindowDays"], 30)
        self.assertEqual(record["snapshotSignalUnits"], {"PRICE_ANOMALY": 2, "SELF_TRADE": 1})
        self.assertEqual(record["snapshotCreditUnits"], 3)
        self.assertEqual(record["snapshotCreditPoints"], 15)
        self.assertIs(record["snapshotTruncated"], False)
        self.assertEqual(record["snapshotAccountAgeDays"], 412)
        self.assertIs(record["snapshotFaydaVerified"], True)
        self.assertEqual(record["snapshotRole"], "farmer")
        self.assertEqual(record["snapshotCompletedEscrows"], 9)
        self.assertIs(record["snapshotCompletedEscrowsPartial"], False)

    def test_a_label_without_a_snapshot_has_empty_snapshot_columns(self):
        record = eol.to_record(label_row())
        for name in eol.COLUMNS:
            if name.startswith("snapshot"):
                self.assertIsNone(record[name], name)
        # What the trigger script of the FRD-20 record tests.
        self.assertFalse(record.get("snapshotVersion"))

    def test_units_that_are_not_type_codes_to_counts_stop_the_export(self):
        for units in ({"+251911223344": 1}, {"call Abebe": 1}, {"PRICE_ANOMALY": "many"}):
            row = label_row(case="fraud_signal", **snapshot_fields())
            row["snapshotSignalUnits"] = {"M": {k: ({"N": str(v)} if isinstance(v, int) else S(v)) for k, v in units.items()}}
            with self.assertRaises(eol.ExportError, msg=units):
                eol.to_record(row)
        row = label_row(case="fraud_signal", **snapshot_fields())
        row["snapshotSignalUnits"] = S("PRICE_ANOMALY")
        with self.assertRaises(eol.ExportError):
            eol.to_record(row)

    def test_a_phone_in_the_role_stops_the_export(self):
        row = label_row(case="fraud_signal", **snapshot_fields())
        row["snapshotRole"] = S("0911223344")
        with self.assertRaises(eol.ExportError):
            eol.to_record(row)

    def test_jsonl_keeps_the_units_a_map_and_csv_joins_them(self):
        with_snapshot = label_row(case="fraud_signal", case_id="sig-1", outcome="fraud_confirmed", **snapshot_fields())
        without = label_row(case="fraud_signal", case_id="sig-2", outcome="false_positive",
                            labelled_at="2026-11-03T08:00:00Z")
        out, err = io.StringIO(), io.StringIO()
        code = eol.main(["--env", "dev"], run=FakeAws({"Items": [with_snapshot, without]}), stdout=out, stderr=err)
        self.assertEqual(code, 0)
        first, second = [json.loads(line) for line in out.getvalue().splitlines()]
        self.assertEqual(first["snapshotSignalUnits"], {"PRICE_ANOMALY": 2, "SELF_TRADE": 1})
        self.assertEqual(first["snapshotVersion"], 1)
        self.assertIsNone(second["snapshotVersion"])

        out = io.StringIO()
        code = eol.main(["--env", "dev", "--format", "csv"], run=FakeAws({"Items": [with_snapshot, without]}),
                        stdout=out, stderr=io.StringIO())
        self.assertEqual(code, 0)
        rows = list(csv.DictReader(io.StringIO(out.getvalue())))
        self.assertEqual(rows[0]["snapshotSignalUnits"], "PRICE_ANOMALY:2;SELF_TRADE:1")
        self.assertEqual(rows[0]["snapshotVersion"], "1")
        self.assertEqual(rows[0]["snapshotTruncated"], "false")
        self.assertEqual(rows[0]["snapshotFaydaVerified"], "true")
        self.assertEqual(rows[0]["snapshotAccountAgeDays"], "412")
        self.assertEqual(rows[0]["snapshotRole"], "farmer")
        for name in eol.COLUMNS:
            if name.startswith("snapshot"):
                self.assertEqual(rows[1][name], "", name)
        self.assertEqual(rows[1]["hasReason"], "true")
        self.assertEqual(rows[1]["final"], "true")


class MainTest(unittest.TestCase):
    def run_main(self, argv, fake=None):
        out, err = io.StringIO(), io.StringIO()
        code = eol.main(argv, run=fake or FakeAws(), stdout=out, stderr=err)
        return code, out.getvalue(), err.getvalue()

    def test_jsonl_to_stdout(self):
        fake = FakeAws({"Items": [label_row(), label_row(case_id="e2", labelled_at="2026-11-01T00:00:00Z")]})
        code, out, err = self.run_main(["--env", "dev"], fake)
        self.assertEqual(code, 0, err)
        lines = [json.loads(line) for line in out.splitlines()]
        self.assertEqual([r["caseId"] for r in lines], ["e2", "escrow-order-ord-1"])  # by labelledAt
        self.assertIn("exported 2 labels of 2 cases from wereda-connect-dev-transaction-audit", err)
        self.assertEqual(arg(fake.calls[0], "--region"), "eu-north-1")

    def test_csv_with_latest_only_to_a_file_outside_the_repo(self):
        fake = FakeAws({"Items": [
            label_row(case="fraud_signal", case_id="sig", outcome="fraud_confirmed", labelled_at="2026-11-02T08:00:00Z",
                      signalIds={"L": [S("sig"), S("sig-b")]}),
            label_row(case="fraud_signal", case_id="sig", outcome="false_positive", labelled_at="2026-11-03T08:00:00Z",
                      signalIds={"L": [S("sig")]}),
        ]})
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "labels.csv")
            code, out, err = self.run_main(["--env", "prod", "--format", "csv", "--latest-only", "--out", path], fake)
            self.assertEqual(code, 0, err)
            with open(path, encoding="utf-8") as f:
                rows = list(csv.DictReader(f))
        self.assertEqual(out, "")
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["outcome"], "false_positive")
        self.assertEqual(rows[0]["final"], "true")
        self.assertEqual(rows[0]["signalIds"], "sig")
        self.assertEqual(arg(fake.calls[0], "--table-name"), "wereda-connect-prod-transaction-audit")
        self.assertEqual(arg(fake.calls[0], "--region"), "me-south-1")

    def test_an_output_inside_a_git_checkout_is_refused(self):
        code, _, err = self.run_main(["--env", "dev", "--out", os.path.join(HERE, "labels.jsonl")])
        self.assertEqual(code, 2)
        self.assertIn("inside a git checkout", err)

    def test_bad_arguments_are_refused(self):
        for argv in ([], ["--env", "dev", "--page-size", "0"], ["--env", "dev", "--since", "yesterday"],
                     ["--env", "dev", "--max-pages", "0"],
                     ["--env", "dev", "--since", "2026-11-02", "--until", "2026-11-01"]):
            code, _, _ = self.run_main(argv)
            self.assertEqual(code, 2, argv)

    def test_dry_run_prints_the_call_and_reads_nothing(self):
        fake = FakeAws()
        code, out, _ = self.run_main(["--env", "staging", "--dry-run", "--since", "2026-11-01T00:00:00Z"], fake)
        self.assertEqual(code, 0)
        self.assertEqual(fake.calls, [])
        self.assertIn("aws dynamodb query --table-name wereda-connect-staging-transaction-audit", out)
        self.assertIn("--index-name EntityIndex", out)
        self.assertIn('":since":{"S":"2026-11-01T00:00:00.0000000Z"}', out)

    def test_a_date_only_until_reads_through_the_end_of_that_day(self):
        fake = FakeAws({"Items": [label_row()]})
        code, _, err = self.run_main(["--env", "dev", "--until", "2026-11-30"], fake)
        self.assertEqual(code, 0, err)
        self.assertEqual(json.loads(arg(fake.calls[0], "--expression-attribute-values"))[":until"],
                         {"S": "2026-11-30T23:59:59.9999999Z"})

    def test_a_truncated_read_exits_3_with_what_it_read(self):
        more = {"entityId": S("outcome-labels")}
        fake = FakeAws({"Items": [label_row()], "LastEvaluatedKey": more})
        code, out, err = self.run_main(["--env", "dev", "--max-pages", "1"], fake)
        self.assertEqual(code, 3)
        self.assertEqual(len(out.splitlines()), 1)
        self.assertIn("stopped at --max-pages 1", err)

    def test_a_failed_aws_call_or_a_phone_exits_1_and_writes_nothing(self):
        def failing(_cmd):
            raise eol.ExportError("aws call failed (255): ExpiredToken")

        code, out, err = self.run_main(["--env", "dev"], failing)
        self.assertEqual((code, out), (1, ""))
        self.assertIn("ExpiredToken", err)

        code, out, _ = self.run_main(["--env", "dev"], FakeAws({"Items": [label_row(truckerId=S("0911223344"))]}))
        self.assertEqual((code, out), (1, ""))


if __name__ == "__main__":
    unittest.main()
