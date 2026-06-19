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

Item schema (keys omitted when empty/default; consumer defaults them):
  {id, en, am?, om?, alts?, region?, lat?, lng?, population?}
Usage: python3 build_ethiopia_places.py [out.json]
"""
import urllib.request, zipfile, io, csv, os, json, sys

BASE = "https://download.geonames.org/export/dump/"
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = sys.argv[1] if len(sys.argv) > 1 else "ethiopia_woredas.json"
SEAT = {"PPLA", "PPLA2", "PPLA3", "PPLA4", "PPLC", "PPLG"}  # administrative seats


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

rows.sort(key=lambda p: (p.get("region", ""), -p.get("population", 0), p["en"]))
open(OUT, "w", encoding="utf-8").write(json.dumps(rows, ensure_ascii=False, separators=(",", ":")))

print(f"Wrote {len(rows)} places -> {OUT} ({os.path.getsize(OUT)} bytes, "
      f"{os.path.getsize(OUT)//1024}KB)", file=sys.stderr)
print(f"  region={sum(1 for p in rows if p.get('region'))} "
      f"am={sum(1 for p in rows if p.get('am'))} om={sum(1 for p in rows if p.get('om'))} "
      f"alts={sum(1 for p in rows if p.get('alts'))} pop={sum(1 for p in rows if p.get('population'))}",
      file=sys.stderr)
