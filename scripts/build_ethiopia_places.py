#!/usr/bin/env python3
"""Generate ethiopia_woredas.json for WeredaConnect's place selector.

Source : GeoNames (https://www.geonames.org/) — Creative Commons Attribution 4.0.
Scope  : Ethiopia (ET) populated places + admin units that carry a notability
         signal (administrative seat, recorded population, or any alternate /
         wiki name). This excludes ~7,500 unnamed micro-hamlets that add only
         noise to a typeahead; off-list places remain enterable as free text.

NOTE   : The authoritative woreda list (1,082 admin-3 units with full
         region>zone>woreda hierarchy) lives in the OCHA/ITOS COD-AB service
         (https://codgis.itos.uga.edu/.../COD_External/ETH_pcode/MapServer/3).
         That host was unreachable from the build sandbox, so this dataset uses
         GeoNames instead: region (admin1) is populated; zone (admin2) is not
         coded by GeoNames for Ethiopia (left empty); am/om names are sparse.
         To refresh from the canonical source later, query the ITOS layer 3 with
         returnGeometry=false and map ADM1_EN/ADM2_EN/ADM3_EN -> region/zone/en.

Overrides: curated fixes in mobile/assets/data/ethiopia_woredas.overrides.json
         (display names, aliases, Amharic / Oromo names) are merged into every
         build, so a regeneration keeps them. `id` and `en` are never changed:
         entities store the id and legacy free-text fields store `en`.

Item schema (keys omitted when empty/default; consumer defaults them):
  {id, en, display?, am?, om?, alts?, region?, lat?, lng?, population?}
Usage:
  python3 build_ethiopia_places.py [out.json]           full build (downloads GeoNames)
  python3 build_ethiopia_places.py --apply-overrides f  re-apply the overrides to an
                                                        existing asset, no download
"""
import csv
import io
import json
import os
import sys
import unicodedata
import urllib.request
import zipfile

BASE = "https://download.geonames.org/export/dump/"
HERE = os.path.dirname(os.path.abspath(__file__))
OVERRIDES = os.path.join(HERE, "..", "mobile", "assets", "data",
                         "ethiopia_woredas.overrides.json")
SEAT = {"PPLA", "PPLA2", "PPLA3", "PPLA4", "PPLC", "PPLG"}  # administrative seats

# Canonical key order of an asset row (matches the full build's output).
KEY_ORDER = ("id", "en", "display", "am", "om", "alts", "region", "lat", "lng",
             "population")
OVERRIDE_KEYS = {"id", "en", "display", "am", "om", "alts", "review"}

# Transliteration marks GeoNames uses for ejectives and the glottal stop
# ("Mek’ī", "‘Alem T’ēna", "Mek'ele"). Dropped from display names and search
# keys. Mirrors `cleanPlaceName` / `placeSearchKey` in
# mobile/lib/shared/services/place_normalizer.dart; keep the two in lockstep.
MARKS = "'‘’ʼʻ`"


class OverrideError(ValueError):
    """The overrides file is malformed or no longer matches the dataset."""


def clean_display_name(name):
    """Display form of a GeoNames name: marks dropped, Latin diacritics folded,
    case kept, whitespace collapsed ("Mek’ī" -> "Meki"). Ge'ez is untouched."""
    decomposed = unicodedata.normalize("NFD", name)
    kept = "".join(c for c in decomposed
                   if not unicodedata.combining(c) and c not in MARKS)
    return " ".join(unicodedata.normalize("NFC", kept).split())


def search_key(name):
    """Case-insensitive comparison key: two names with the same key are the same
    search term to the app ("Mek'i", "Mek’ī" and "meki" all give "meki")."""
    return clean_display_name(name).lower()


def is_ethiopic(text):
    """True when [text] contains Ge'ez (Ethiopic) letters."""
    return any(0x1200 <= ord(c) <= 0x139F or 0x2D80 <= ord(c) <= 0x2DDF
               or 0xAB00 <= ord(c) <= 0xAB2F for c in text)


def load_overrides(path=OVERRIDES):
    """Read and validate the overrides file; returns the list of entries."""
    with open(path, encoding="utf-8") as fh:
        data = json.load(fh)
    places = data.get("places") if isinstance(data, dict) else None
    if not isinstance(places, list):
        raise OverrideError(f"{path}: expected an object with a 'places' list")
    validate_overrides(places)
    return places


def validate_overrides(places):
    """Reject entries the merge cannot apply safely."""
    seen = set()
    for entry in places:
        if not isinstance(entry, dict):
            raise OverrideError(f"override is not an object: {entry!r}")
        pid, en = entry.get("id"), entry.get("en")
        label = f"{pid} ({en})"
        if not pid or not en:
            raise OverrideError(f"{label}: 'id' and 'en' are required")
        if pid in seen:
            raise OverrideError(f"{label}: duplicate id")
        seen.add(pid)
        unknown = set(entry) - OVERRIDE_KEYS
        if unknown:
            raise OverrideError(f"{label}: unknown keys {sorted(unknown)}")
        for key in ("display", "am", "om"):
            if key in entry and (not isinstance(entry[key], str)
                                 or not entry[key].strip()):
                raise OverrideError(f"{label}: '{key}' must be a non-empty string")
        alts = entry.get("alts", [])
        if not isinstance(alts, list) or not all(
                isinstance(a, str) and a.strip() for a in alts):
            raise OverrideError(f"{label}: 'alts' must be a list of names")
        if "am" in entry and not is_ethiopic(entry["am"]):
            raise OverrideError(f"{label}: 'am' must be written in Ge'ez")
        if "om" in entry and is_ethiopic(entry["om"]):
            raise OverrideError(f"{label}: 'om' must be written in Latin")
        if ("am" in entry or "om" in entry) and not isinstance(
                entry.get("review"), bool):
            raise OverrideError(
                f"{label}: entries with am / om need a 'review' flag")
        display = entry.get("display")
        if display and display == clean_display_name(en):
            raise OverrideError(
                f"{label}: 'display' equals the cleaned name; drop it")


def apply_overrides(rows, overrides):
    """Merge [overrides] into [rows] in place; returns the number of rows changed.

    Never changes `id` or `en`. A replaced GeoNames am / om value, the display
    name and the override aliases are added to `alts` unless an existing name
    already has the same [search_key]. Idempotent: applying twice is a no-op.
    """
    by_id = {row["id"]: row for row in rows}
    changed = 0
    for entry in overrides:
        row = by_id.get(entry["id"])
        label = f"{entry['id']} ({entry['en']})"
        if row is None:
            raise OverrideError(f"{label}: no such place in the dataset")
        if row["en"] != entry["en"]:
            raise OverrideError(
                f"{label}: the dataset calls it {row['en']!r}; re-check the entry")
        before = dict(row)
        alts = list(row.get("alts", []))
        candidates = []
        for lang in ("am", "om"):
            new = entry.get(lang)
            if new:
                old = row.get(lang, "")
                if old and old != new:
                    candidates.append(old)
                row[lang] = new
        if entry.get("display"):
            row["display"] = entry["display"]
            candidates.append(entry["display"])
        candidates.extend(entry.get("alts", []))
        taken = {search_key(n) for n in
                 [row["en"], row.get("am", ""), row.get("om", "")] + alts if n}
        for name in candidates:
            key = search_key(name)
            if key and key not in taken:
                alts.append(name)
                taken.add(key)
        if alts:
            row["alts"] = alts
        ordered = {k: row[k] for k in KEY_ORDER if k in row}
        ordered.update({k: v for k, v in row.items() if k not in ordered})
        row.clear()
        row.update(ordered)
        if row != before or list(row) != list(before):
            changed += 1
    return changed


def write_rows(rows, path):
    """Minified JSON, one line, with a trailing newline (as committed)."""
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(json.dumps(rows, ensure_ascii=False, separators=(",", ":")) + "\n")


def load(url, member, fname):
    """Download (cached in script dir) and return text of a dump file."""
    path = os.path.join(HERE, fname)
    if not os.path.exists(path):
        req = urllib.request.Request(url, headers={"User-Agent": "wereda-connect-build/1.0"})
        data = urllib.request.urlopen(req, timeout=60).read()
        if member:
            zf = zipfile.ZipFile(io.BytesIO(data))
            data = zf.read(next(n for n in zf.namelist() if n.endswith(member)))
        open(path, "wb").write(data)
    return open(path, encoding="utf-8").read()


def build_rows():
    """Download the GeoNames dumps and build the asset rows (unsorted)."""
    et = load(BASE + "ET.zip", "ET.txt", "ET.txt")
    alt = load(BASE + "alternatenames/ET.zip", "ET.txt", "ET_alt.txt")
    a1 = load(BASE + "admin1CodesASCII.txt", None, "admin1.txt")

    region = {r[0]: r[1] for r in csv.reader(io.StringIO(a1), delimiter="\t")
              if len(r) >= 2 and r[0].startswith("ET.")}

    am, om, alts, alt_gids = {}, {}, {}, set()
    for r in csv.reader(io.StringIO(alt), delimiter="\t"):
        if len(r) < 4:
            continue
        gid, lang, name = r[1], r[2], r[3].strip()
        if not name:
            continue
        alt_gids.add(gid)
        pref = len(r) > 4 and r[4] == "1"
        if lang == "am" and (gid not in am or pref):
            am[gid] = name
        elif lang == "om" and (gid not in om or pref):
            om[gid] = name
        elif lang in ("en", "abbr"):
            alts.setdefault(gid, [])
            if name not in alts[gid] and len(alts[gid]) < 4:
                alts[gid].append(name)

    rows, seen = [], set()
    for r in csv.reader(io.StringIO(et), delimiter="\t"):
        if len(r) < 19:
            continue
        gid, name, ascii_name = r[0], r[1].strip(), r[2].strip()
        lat, lng, fclass, fcode = r[4], r[5], r[6], r[7]
        a1c, pop = r[10], r[14]
        popn = int(pop) if pop.isdigit() else 0
        is_place, is_woreda = fclass == "P", fcode == "ADM3"
        if not (is_place or is_woreda):
            continue
        if is_place and not (fcode in SEAT or popn > 0 or gid in alt_gids):
            continue  # drop noise: unnamed micro-hamlets
        en = name or ascii_name
        if not en:
            continue
        key = (en.lower(), round(float(lat), 2) if lat else 0, round(float(lng), 2) if lng else 0)
        if key in seen:
            continue
        seen.add(key)
        a = list(alts.get(gid, []))
        if ascii_name and ascii_name != en and ascii_name not in a and len(a) < 5:
            a.append(ascii_name)
        item = {"id": f"gn-{gid}", "en": en}
        if am.get(gid):
            item["am"] = am[gid]
        if om.get(gid):
            item["om"] = om[gid]
        if a:
            item["alts"] = a
        if a1c and region.get(f"ET.{a1c}"):
            item["region"] = region[f"ET.{a1c}"]
        if lat:
            item["lat"] = round(float(lat), 4)
        if lng:
            item["lng"] = round(float(lng), 4)
        if popn:
            item["population"] = popn
        rows.append(item)
    return rows


def main(argv):
    overrides = load_overrides()
    if argv[:1] == ["--apply-overrides"]:
        if len(argv) != 2:
            print("usage: build_ethiopia_places.py --apply-overrides <asset.json>",
                  file=sys.stderr)
            return 2
        out = argv[1]
        with open(out, encoding="utf-8") as fh:
            rows = json.load(fh)
        changed = apply_overrides(rows, overrides)  # keeps the row order
        write_rows(rows, out)
        print(f"Applied {len(overrides)} overrides -> {out} ({changed} rows changed)",
              file=sys.stderr)
        return 0

    out = argv[0] if argv else "ethiopia_woredas.json"
    rows = build_rows()
    apply_overrides(rows, overrides)
    rows.sort(key=lambda p: (p.get("region", ""), -p.get("population", 0), p["en"]))
    write_rows(rows, out)

    print(f"Wrote {len(rows)} places -> {out} ({os.path.getsize(out)} bytes, "
          f"{os.path.getsize(out)//1024}KB)", file=sys.stderr)
    print(f"  region={sum(1 for p in rows if p.get('region'))} "
          f"am={sum(1 for p in rows if p.get('am'))} om={sum(1 for p in rows if p.get('om'))} "
          f"alts={sum(1 for p in rows if p.get('alts'))} pop={sum(1 for p in rows if p.get('population'))} "
          f"display={sum(1 for p in rows if p.get('display'))}",
          file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
