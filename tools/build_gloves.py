"""
Build the glove catalogue for ARMORY / ARSENAL from CS2's own schema.

Gloves are NOT in the weapon loot-list format, so the weapon builder cannot see them:
there is no "[paintkit]weapon" token for a glove anywhere in items_game.txt. The link
between a glove and its finishes is the paint kit NAME PREFIX, and there are TWO
generations of prefix per glove - the original one and a later "glove_<type>_" scheme.
Getting that wrong silently offers finishes that belong to a different glove.

  Driver Gloves     slick_*         glove_driver_*
  Sport Gloves      sporty_*        glove_sport_*
  Specialist        specialist_*    glove_specialist_*
  Moto Gloves       motorcycle_*
  Bloodhound        bloodhound_*    (but NOT bloodhound_hydra_*)
  Hydra             bloodhound_hydra_*
  Hand Wraps        handwrap_*
  Broken Fang       operation10_*

Skinnable gloves are the ones whose item prefab is `hands_paintable`; `hands` is the
plain team default. Note Broken Fang is def 4725, outside the 5027-5035 run, so an
index range is not a safe way to find them.
"""
import io, json, os, re

HERE = os.path.dirname(os.path.abspath(__file__))

RULES = {
    'studded_bloodhound_gloves': lambda n: n.startswith('bloodhound_') and not n.startswith('bloodhound_hydra'),
    'studded_hydra_gloves':      lambda n: n.startswith('bloodhound_hydra'),
    # Operation Broken Fang is operation 10; its four glove paints carry the operation
    # prefix, not the glove name, so a `brokenfang*` rule silently finds nothing.
    'studded_brokenfang_gloves': lambda n: n.startswith('operation10_'),
    'sporty_gloves':             lambda n: n.startswith('sporty_') or n.startswith('glove_sport'),
    'slick_gloves':              lambda n: n.startswith('slick_') or n.startswith('glove_driver'),
    'leather_handwraps':         lambda n: n.startswith('handwrap'),
    'motorcycle_gloves':         lambda n: n.startswith('motorcycle_'),
    'specialist_gloves':         lambda n: n.startswith('specialist_') or n.startswith('glove_specialist'),
}


def build():
    t = io.open(os.path.join(HERE, 'items_game.txt'), encoding='utf-8', errors='replace').read()
    eng = io.open(os.path.join(HERE, 'csgo_english.txt'), encoding='utf-8-sig', errors='replace').read()

    gloves = {}
    for m in re.finditer(r'\n\t\t"(\d+)"\s*\n\t\t\{(.*?)\n\t\t\}', t, re.S):
        body = m.group(2)
        nm = re.search(r'"name"\s*"([^"]+)"', body)
        pf = re.search(r'"prefab"\s*"([^"]+)"', body)
        it = re.search(r'"item_name"\s*"([^"]+)"', body)
        if nm and pf and pf.group(1) == 'hands_paintable':
            gloves[nm.group(1)] = (int(m.group(1)), (it.group(1) if it else '').lstrip('#'))

    # every paint_kits block, not just the first
    kit_idx = {}
    for blk in re.finditer(r'"paint_kits"\s*\n\s*\{(.*?)\n\t\}', t, re.S):
        for m in re.finditer(r'\n\t\t"(\d+)"\s*\n\t\t\{(.*?)\n\t\t\}', blk.group(1), re.S):
            nm = re.search(r'"name"\s*"([^"]+)"', m.group(2))
            if nm:
                kit_idx[nm.group(1)] = int(m.group(1))

    # display names; the tag is _tag on older kits and _Tag on newer ones
    loc = {k.lower(): v for k, v in re.findall(r'"PaintKit_([^"]+)_[Tt]ag"\s*"([^"]*)"', eng)}
    wear = {k.lower(): v for k, v in re.findall(r'"(CSGO_Wearable_[^"]+)"\s*"([^"]*)"', eng)}

    out = {}
    for gname, pred in RULES.items():
        if gname not in gloves:
            print('  no item def for', gname)
            continue
        gdef, tag = gloves[gname]
        kits = sorted(n for n in kit_idx if pred(n) and n not in gloves)
        if not kits:
            continue
        out[gname] = {
            'def':      gdef,
            'name':     wear.get(tag.lower(), gname),
            'finishes': [{'i': kit_idx[n], 'n': loc.get(n.lower(), n)} for n in kits],
        }

    for g in sorted(out, key=lambda k: out[k]['name']):
        v = out[g]
        unnamed = sum(1 for f in v['finishes'] if f['n'].startswith(('glove_', 'slick_', 'sporty_',
                                                                    'handwrap', 'motorcycle_',
                                                                    'bloodhound', 'specialist_',
                                                                    'brokenfang')))
        print(f"  {v['name']:20} def={v['def']:5} {len(v['finishes']):3} finishes"
              + (f'  ({unnamed} unnamed)' if unnamed else ''))

    dest = os.path.join(HERE, 'armory_gloves.json')
    json.dump(out, io.open(dest, 'w', encoding='utf-8'), ensure_ascii=False, separators=(',', ':'))
    print(f"wrote {dest} - {len(out)} gloves, "
          f"{sum(len(v['finishes']) for v in out.values())} finishes, "
          f"{os.path.getsize(dest)} bytes")


if __name__ == '__main__':
    build()
