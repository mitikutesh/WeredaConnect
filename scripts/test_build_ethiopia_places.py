#!/usr/bin/env python3
"""Tests for build_ethiopia_places.py: name cleaning and the overrides merge.

Run from the repo root: python3 -I scripts/test_build_ethiopia_places.py
No network: only the pure functions and the committed files are exercised.
"""
import copy
import json
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import build_ethiopia_places as bep  # noqa: E402

ASSET = os.path.join(HERE, "..", "mobile", "assets", "data", "ethiopia_woredas.json")


def _row(**kw):
    return dict(kw)


class CleanDisplayNameTest(unittest.TestCase):
    # Same cases as mobile/test/shared/services/place_normalizer_test.dart.
    CASES = {
        "Mek’ī": "Meki",
        "‘Alem T’ēna": "Alem Tena",
        "K’ok’a": "Koka",
        "Ādama": "Adama",
        "Nazrēt": "Nazret",
        "Mek'ele": "Mekele",
        "Debre Mark’os": "Debre Markos",
        "Yirga ‘Alem": "Yirga Alem",
        "ĪTANG": "ITANG",
        "Addis Ababa": "Addis Ababa",
        "  Dirē   Dawa ": "Dire Dawa",
        "ጎንደር": "ጎንደር",
    }

    def test_drops_marks_folds_diacritics_and_keeps_case(self):
        for raw, expected in self.CASES.items():
            with self.subTest(raw=raw):
                self.assertEqual(bep.clean_display_name(raw), expected)

    def test_search_key_matches_old_and_new_spellings(self):
        self.assertEqual(bep.search_key("Mek’ī"), "meki")
        self.assertEqual(bep.search_key("Mek'i"), "meki")
        self.assertEqual(bep.search_key("MEKI"), "meki")
        self.assertNotEqual(bep.search_key("Adama"), bep.search_key("Nazret"))

    def test_is_ethiopic(self):
        self.assertTrue(bep.is_ethiopic("አዳማ"))
        self.assertFalse(bep.is_ethiopic("Ādīs Ābeba"))
        self.assertFalse(bep.is_ethiopic(""))


class ApplyOverridesTest(unittest.TestCase):
    def _rows(self):
        return [
            _row(id="gn-1", en="Nazrēt", alts=["Nazret"], region="Oromiya",
                 lat=8.55, lng=39.2667, population=456900),
            _row(id="gn-2", en="Addis Ababa", am="Ādīs Ābeba",
                 alts=["Addis Ababa"], region="Addis Ababa", population=3860000),
            _row(id="gn-3", en="Mek’ī", alts=["Mek'i"], region="Oromiya"),
            _row(id="gn-4", en="Untouched", region="Amhara"),
        ]

    def test_adds_display_translations_and_aliases_without_touching_id_or_en(self):
        rows = self._rows()
        overrides = [
            {"id": "gn-1", "en": "Nazrēt", "display": "Adama", "am": "አዳማ",
             "om": "Adaamaa", "alts": ["Nazareth"], "review": True},
        ]
        changed = bep.apply_overrides(rows, overrides)
        self.assertEqual(changed, 1)
        row = rows[0]
        self.assertEqual(row["id"], "gn-1")
        self.assertEqual(row["en"], "Nazrēt")
        self.assertEqual(row["display"], "Adama")
        self.assertEqual(row["am"], "አዳማ")
        self.assertEqual(row["om"], "Adaamaa")
        # The display name becomes searchable for alias-only consumers too.
        self.assertEqual(row["alts"], ["Nazret", "Adama", "Nazareth"])
        self.assertEqual(list(row), ["id", "en", "display", "am", "om", "alts",
                                     "region", "lat", "lng", "population"])
        self.assertNotIn("review", row)  # curation metadata stays in the overrides

    def test_replaced_name_stays_searchable(self):
        rows = self._rows()
        bep.apply_overrides(rows, [
            {"id": "gn-2", "en": "Addis Ababa", "am": "አዲስ አበባ", "review": True},
        ])
        self.assertEqual(rows[1]["am"], "አዲስ አበባ")
        self.assertIn("Ādīs Ābeba", rows[1]["alts"])

    def test_skips_aliases_the_app_already_matches(self):
        rows = self._rows()
        bep.apply_overrides(rows, [
            {"id": "gn-3", "en": "Mek’ī", "am": "መቂ", "alts": ["Meki", "MEK'I"],
             "review": True},
        ])
        self.assertEqual(rows[2]["alts"], ["Mek'i"])

    def test_is_idempotent(self):
        rows = self._rows()
        once = [
            {"id": "gn-1", "en": "Nazrēt", "display": "Adama", "am": "አዳማ",
             "review": True},
            {"id": "gn-2", "en": "Addis Ababa", "am": "አዲስ አበባ", "review": True},
        ]
        bep.apply_overrides(rows, once)
        snapshot = copy.deepcopy(rows)
        self.assertEqual(bep.apply_overrides(rows, once), 0)
        self.assertEqual(rows, snapshot)

    def test_leaves_other_rows_alone(self):
        rows = self._rows()
        untouched = copy.deepcopy(rows[3])
        bep.apply_overrides(rows, [
            {"id": "gn-1", "en": "Nazrēt", "display": "Adama"},
        ])
        self.assertEqual(rows[3], untouched)

    def test_rejects_unknown_id_and_renamed_place(self):
        with self.assertRaises(bep.OverrideError):
            bep.apply_overrides(self._rows(), [{"id": "gn-404", "en": "Nowhere"}])
        with self.assertRaises(bep.OverrideError):
            bep.apply_overrides(self._rows(), [{"id": "gn-1", "en": "Adama"}])


class ValidateOverridesTest(unittest.TestCase):
    def _bad(self, entry):
        with self.assertRaises(bep.OverrideError):
            bep.validate_overrides([entry])

    def test_rejects_malformed_entries(self):
        self._bad({"id": "gn-1"})                                     # no en
        self._bad({"id": "gn-1", "en": "X", "zone": "Y"})            # unknown key
        self._bad({"id": "gn-1", "en": "X", "am": "Adama", "review": True})  # am not Ge'ez
        self._bad({"id": "gn-1", "en": "X", "om": "አዳማ", "review": True})   # om not Latin
        self._bad({"id": "gn-1", "en": "X", "am": "አዳማ"})            # no review flag
        self._bad({"id": "gn-1", "en": "Mek’ī", "display": "Meki"})  # redundant display
        self._bad({"id": "gn-1", "en": "X", "alts": "Y"})            # alts not a list
        with self.assertRaises(bep.OverrideError):
            bep.validate_overrides([{"id": "gn-1", "en": "X"}, {"id": "gn-1", "en": "X"}])

    def test_committed_overrides_load(self):
        self.assertGreater(len(bep.load_overrides()), 0)

    def test_committed_overrides_mark_every_translation_for_review(self):
        for entry in bep.load_overrides():
            if "am" in entry or "om" in entry:
                with self.subTest(id=entry["id"]):
                    self.assertIs(entry["review"], True)


class CommittedAssetTest(unittest.TestCase):
    """The committed asset already contains every override (no drift)."""

    @classmethod
    def setUpClass(cls):
        with open(ASSET, encoding="utf-8") as fh:
            cls.rows = json.load(fh)

    def test_reapplying_the_overrides_changes_nothing(self):
        rows = copy.deepcopy(self.rows)
        self.assertEqual(bep.apply_overrides(rows, bep.load_overrides()), 0)
        self.assertEqual(rows, self.rows)

    def test_key_places_resolve(self):
        by_id = {r["id"]: r for r in self.rows}
        self.assertEqual(len(self.rows), 3860)
        self.assertEqual(by_id["gn-330186"]["display"], "Adama")
        self.assertEqual(by_id["gn-325579"]["display"], "Batu")
        self.assertIn("Debre Zeyit", by_id["gn-339666"]["alts"])
        self.assertEqual(bep.clean_display_name(by_id["gn-331163"]["en"]), "Meki")


if __name__ == "__main__":
    unittest.main()
