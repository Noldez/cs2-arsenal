"""
Build the ARMORY skin catalog from CS2's own item schema.

Sources (all extracted from pak01):
  items_game.txt    - paint_kits (index + name), paint_kits_rarity, loot-list weapon/paintkit pairs
  csgo_english.txt  - PaintKit_<name>_Tag -> display name  (UTF-8 with BOM)
  pak01 file list   - to verify every econ image actually exists

items_game.txt contains SEVERAL "paint_kits" and "paint_kits_rarity" blocks (base schema
plus later additions), so every occurrence has to be merged - reading only the first one
loses ~2/3 of the paint ids.

Output: catalog.json  { weapon: [ {paint, idx, name, rarity, img}, ... ] }
"""
import json, re, struct, os, collections

HERE = os.path.dirname(os.path.abspath(__file__))
ITEMS = os.path.join(HERE, "items_game.txt")
ENG = os.path.join(HERE, "csgo_english.txt")
VPK = r"D:\steam\steamapps\common\Counter-Strike Global Offensive\game\csgo\pak01_dir.vpk"

# CS schema rarity -> the grade players actually see
RARITY_MAP = {
    "common":    "consumer",     # Consumer Grade   (white)
    "uncommon":  "industrial",   # Industrial Grade (light blue)
    "rare":      "milspec",      # Mil-Spec         (blue)
    "mythical":  "restricted",   # Restricted       (purple)
    "legendary": "classified",   # Classified       (pink)
    "ancient":   "covert",       # Covert           (red)
    "immortal":  "contraband",   # Contraband       (gold)
    "unusual":   "covert",
    "default":   "consumer",
}


def vpk_econ_images():
    """Every basename under panorama/images/econ/default_generated in pak01."""
    f = open(VPK, "rb")
    sig, ver = struct.unpack("<II", f.read(8))
    treelen = struct.unpack("<IIIII", f.read(20))[0] if ver == 2 else struct.unpack("<I", f.read(4))[0]
    tree = f.read(treelen)

    def rs(b, i):
        j = b.index(b"\x00", i)
        return b[i:j].decode("utf-8", "replace"), j + 1

    i, out = 0, set()
    while i < len(tree):
        ext, i = rs(tree, i)
        if not ext:
            break
        while True:
            path, i = rs(tree, i)
            if not path:
                break
            while True:
                name, i = rs(tree, i)
                if not name:
                    break
                crc, pre, arc, off, ln = struct.unpack("<IHHII", tree[i:i + 16])
                i += 16 + pre + 2
                if path == "panorama/images/econ/default_generated":
                    out.add(name)
    return out


def iter_blocks(lines, key):
    """Yield (start,end) line spans for every top-level `"key"` block, at any indent."""
    pat = re.compile(r'^\s*"%s"\s*$' % re.escape(key))
    i = 0
    while i < len(lines):
        if pat.match(lines[i]):
            j = i + 1
            while j < len(lines) and "{" not in lines[j]:
                j += 1
            depth, k = 0, j
            while k < len(lines):
                depth += lines[k].count("{") - lines[k].count("}")
                if depth == 0 and k > j:
                    break
                k += 1
            yield j, k
            i = k
        i += 1


def parse_paint_kits(lines):
    """
    name -> (index, description_tag), merged across every paint_kits block.

    The tag matters: a paint kit's localised name is NOT reliably
    "PaintKit_<name>_Tag". Plenty of kits point elsewhere - AK Redline is
    `cu_ak47_cobra` but its tag is `#PaintKit_cu_awp_cobra_tag`. Deriving the key
    from the name silently loses Redline, Howl, Vulcan, Hyper Beast and ~165 more.
    """
    out = {}
    id_re = re.compile(r'^\s*"(\d+)"\s*$')
    name_re = re.compile(r'^\s*"name"\s+"([^"]+)"')
    tag_re = re.compile(r'^\s*"description_tag"\s+"#?([^"]+)"')
    for s, e in iter_blocks(lines, "paint_kits"):
        cur_idx = cur_name = None
        for ln in lines[s:e]:
            m = id_re.match(ln)
            if m:
                cur_idx, cur_name = int(m.group(1)), None
                continue
            m = name_re.match(ln)
            if m and cur_idx is not None:
                cur_name = m.group(1).lower()
                out.setdefault(cur_name, [cur_idx, None])
                continue
            m = tag_re.match(ln)
            if m and cur_name and out.get(cur_name):
                out[cur_name][1] = m.group(1).lower()
    return {k: tuple(v) for k, v in out.items()}


def parse_rarity(lines):
    rar = {}
    kv = re.compile(r'^\s*"([A-Za-z0-9_]+)"\s+"([A-Za-z0-9_]+)"\s*$')
    for s, e in iter_blocks(lines, "paint_kits_rarity"):
        for ln in lines[s:e]:
            m = kv.match(ln)
            if m:
                rar[m.group(1).lower()] = m.group(2).lower()
    return rar


def parse_loot_rarity(lines):
    """
    (paint, weapon) -> rarity, from client_loot_lists sub-list names
    ("set_nuke_2_mythical" -> mythical). This is the grade players actually see.
    paint_kits_rarity disagrees for a chunk of legacy items (Fire Serpent, Blaze,
    Bloodsport...), so this wins and paint_kits_rarity is only the fallback.
    """
    out = {}
    tiers = ("common", "uncommon", "rare", "mythical", "legendary", "ancient", "immortal")
    name_re = re.compile(r'^\s*"([A-Za-z0-9_]+)"\s*$')
    pair_re = re.compile(r'^\s*"\[([a-z0-9_]+)\]weapon_([a-z0-9_]+)"')
    for s, e in iter_blocks(lines, "client_loot_lists"):
        cur = None
        for ln in lines[s:e]:
            m = name_re.match(ln)
            if m:
                nm = m.group(1).lower()
                cur = next((t for t in tiers if nm.endswith("_" + t)), None)
                continue
            m = pair_re.match(ln)
            if m and cur:
                out[(m.group(1).lower(), "weapon_" + m.group(2).lower())] = cur
    return out


# Several paint kits deliberately share ONE description_tag, so they all localise to the same
# string: every Doppler phase, Ruby, Sapphire and Black Pearl are "Doppler", and every Gamma
# Doppler phase plus Emerald are "Gamma Doppler". A knife then shows seven identical rows with
# no way to tell Phase 1 from Ruby. The distinguishing part only exists in the kit's INTERNAL
# name, so recover it from there.
VARIANT = re.compile(r'_(?:gamma_)?doppler_phase(\d)|_(ruby|sapphire|emerald|blackpearl)_')


def variant_of(paint):
    """The bit that tells two same-named finishes apart, or None when there is nothing to add."""
    m = VARIANT.search("_" + paint + "_")

    if not m:
        return None

    if m.group(1):
        return f"Phase {m.group(1)}"

    return {"blackpearl": "Black Pearl"}.get(m.group(2), m.group(2).title())


def main():
    text = open(ITEMS, encoding="utf-8", errors="replace").read()
    lines = text.split("\n")
    eng = open(ENG, encoding="utf-8-sig", errors="replace").read()

    # Key on the WHOLE token, lowercased, so description_tag can be looked up directly.
    # Parse line-wise and skip // comments: csgo_english contains commented-out keys
    # (`// re-use "PaintKit_cu_m4a1_hyper_beast_Tag"`), and a value pattern that can
    # span newlines swallows the NEXT line's key as the display name.
    names = {}
    kv_line = re.compile(r'^\s*"(PaintKit_[A-Za-z0-9_]+)"\s+"([^"\n]*)"\s*$', re.I)
    for ln in eng.split("\n"):
        if ln.lstrip().startswith("//"):
            continue
        m = kv_line.match(ln)
        if m:
            names.setdefault(m.group(1).lower(), m.group(2).strip())
    kits = parse_paint_kits(lines)
    rarity = parse_rarity(lines)
    loot_rarity = parse_loot_rarity(lines)
    imgs = vpk_econ_images()

    print(f"display names : {len(names)}")
    print(f"paint kits    : {len(kits)}")
    print(f"rarities      : {len(rarity)} (paint_kits) + {len(loot_rarity)} (loot lists)")
    print(f"econ images   : {len(imgs)}")

    # Enumerate from the IMAGES, not the loot lists: a loot list misses skins that were
    # never in a case (M4A1-S Hyper Beast among them), whereas anything with a rendered
    # image is displayable. Loot lists are still the rarity source below.
    known_weapons = {f"weapon_{w}" for _, w in
                     re.findall(r'"\[([a-z0-9_]+)\]weapon_([a-z0-9_]+)"', text)}
    paint_names = set(kits)
    pairs = set()
    for stem in imgs:
        if not stem.endswith("_light_png") or not stem.startswith("weapon_"):
            continue
        core = stem[:-len("_light_png")]
        parts = core.split("_")
        cands = []
        for i in range(2, len(parts)):
            w, p = "_".join(parts[:i]), "_".join(parts[i:])
            if p in paint_names:
                cands.append((w, p))
        if not cands:
            continue
        # prefer a split whose weapon half is one we've actually seen in the schema
        w, p = next((c for c in cands if c[0] in known_weapons), cands[0])
        pairs.add((p, w[len("weapon_"):]))
    pairs = sorted(pairs)
    cat = collections.defaultdict(list)
    drop_img = drop_name = drop_idx = 0
    for paint, weap in pairs:
        stem = f"weapon_{weap}_{paint}_light_png"
        if stem not in imgs:
            drop_img += 1
            continue
        idx, tag = kits.get(paint.lower(), (None, None))
        if not idx:
            drop_idx += 1
            continue
        disp = names.get(tag) if tag else None
        if not disp:
            disp = names.get(f"paintkit_{paint.lower()}_tag")
        if not disp:
            drop_name += 1
            continue
        wkey = f"weapon_{weap}"
        # Knives take their grade from the item, not the paint kit - every knife is
        # Covert (the yellow star), so the paint's own rarity would be wrong.
        if "knife" in wkey or "bayonet" in wkey:
            grade = "covert"
        else:
            grade = RARITY_MAP.get(
                loot_rarity.get((paint.lower(), wkey))
                or rarity.get(paint.lower(), "default"), "milspec")
        if (v := variant_of(paint)) is not None:
            disp = f"{disp} {v}"

        cat[wkey].append({
            "paint": paint,
            "idx": idx,
            "name": disp,
            "rarity": grade,
            "img": stem,
        })

    for w in cat:
        cat[w].sort(key=lambda s: s["name"])

    json.dump(cat, open(os.path.join(HERE, "catalog.json"), "w"), indent=1)
    total = sum(len(v) for v in cat.values())
    print(f"\ncatalog: {len(cat)} weapons, {total} skins")
    print(f"dropped: {drop_img} no-image, {drop_name} no-name, {drop_idx} no-paint-id")
    print("rarity spread:", collections.Counter(s["rarity"] for w in cat for s in cat[w]).most_common())
    print("\ntop weapons:")
    for w in sorted(cat, key=lambda k: -len(cat[k]))[:10]:
        print(f"  {w:24} {len(cat[w]):4}")


main()
