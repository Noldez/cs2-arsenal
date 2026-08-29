"""Build the CS2 content for the wardrobe room: TGA textures, .vmat per material, and the .vmdl.

Nothing here needs ModelDoc or the material editor. Constants that were shipped as flat 4K
PNGs become inline values in the .vmat, which is the form the existing marisa_01.vmat on
this install already uses, so it is a proven shape rather than a guess.
"""
import os, glob, io
from PIL import Image
Image.MAX_IMAGE_PIXELS = None

CS   = r"D:\steam\steamapps\common\Counter-Strike Global Offensive"
SRC  = r"C:\Users\Administrator\Desktop\room\textures"        # originals (for constants)
OPT  = r"C:\Users\Administrator\Desktop\room\textures_cs2"    # resized set
MATD = os.path.join(CS, r"content\csgo\materials\cstema\wardrobe")
MDLD = os.path.join(CS, r"content\csgo\models\cstema\wardrobe")
REL  = "materials/cstema/wardrobe"

os.makedirs(MATD, exist_ok=True); os.makedirs(MDLD, exist_ok=True)

MATERIALS = ["Chair","Closet-02","Closet-1","Closet-3","Door-02","Doors-1","Fan",
             "Glass-windows","Music-player","Paints","Room","Sheets","Small-probs","Table"]

DEFAULT_NORMAL = "materials/default/default_normal.tga"
DEFAULT_AO     = "materials/default/default_ao.tga"
DEFAULT_COLOR  = "materials/default/default_color.tga"


def avg_of(path):
    im = Image.open(path).convert("RGB").resize((16, 16))
    px = list(im.getdata())
    return tuple(sum(c[i] for c in px) / len(px) / 255.0 for i in range(3))


def const(v):
    return '"[%.6f %.6f %.6f]"' % v


written_tga = 0
lines_report = []

for mat in MATERIALS:
    slots = {}
    for kind in ("BaseColor", "Normal", "Roughness", "Metallic"):
        opt = os.path.join(OPT, f"{mat}_{kind}.png")
        org = os.path.join(SRC, f"{mat}_{kind}.png")
        if os.path.exists(opt):                       # carries detail -> ship a texture
            # The compiler derives a mesh's .vmat path from its BASE COLOUR texture path
            # by swapping the extension, so that texture has to be named after the
            # material itself or it goes looking for <mat>_basecolor.vmat.
            low = mat.lower() if kind == "BaseColor" else f"{mat.lower()}_{kind.lower()}"
            tga = os.path.join(MATD, low + ".tga")
            if not os.path.exists(tga):
                Image.open(opt).convert("RGB").save(tga)
                written_tga += 1
            slots[kind] = ('tex', f"{REL}/{low}.tga")
        elif os.path.exists(org):                     # was flat -> inline the value
            slots[kind] = ('const', avg_of(org))
        else:
            slots[kind] = ('none', None)

    def emit(kind, fallback):
        k, v = slots[kind]
        if k == 'tex':   return '"%s"' % v
        if k == 'const': return const(v)
        return '"%s"' % fallback

    vmat = (
        '"Layer0"\n'
        '{\n'
        '\t"Shader"\t\t"csgo_vertexlitgeneric.vfx"\n'
        f'\t"TextureColor"\t\t{emit("BaseColor", DEFAULT_COLOR)}\n'
        f'\t"TextureNormal"\t\t{emit("Normal", DEFAULT_NORMAL)}\n'
        f'\t"TextureAmbientOcclusion"\t\t"{DEFAULT_AO}"\n'
        f'\t"TextureRoughness"\t\t{emit("Roughness", DEFAULT_COLOR)}\n'
        f'\t"TextureMetalness"\t\t{emit("Metallic", DEFAULT_COLOR)}\n'
        '}\n'
    )
    with io.open(os.path.join(MATD, f"{mat.lower()}.vmat"), "w", encoding="utf-8", newline="\n") as f:
        f.write(vmat)
    lines_report.append("  %-14s %s" % (mat, "  ".join(
        f"{k}={'tex' if s[0]=='tex' else ('const' if s[0]=='const' else 'default')}"
        for k, s in slots.items())))

# ---- the model ------------------------------------------------------------------
# Header GUIDs copied verbatim from this install's own marisa.vmdl so the format
# version is whatever this build of resourcecompiler actually expects.
remaps = "\n".join(
    '\t\t\t\t\t\t\t{\n'
    f'\t\t\t\t\t\t\t\tfrom = "{m.lower()}.vmat"\n'
    f'\t\t\t\t\t\t\t\tto = "{REL}/{m.lower()}.vmat"\n'
    '\t\t\t\t\t\t\t},' for m in MATERIALS)

vmdl = f'''<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc41:version{{12fc9d44-453a-4ae4-b4d9-7e2ac0bbd4e0}} -->
{{
\trootNode =
\t{{
\t\t_class = "RootNode"
\t\tchildren =
\t\t[
\t\t\t{{
\t\t\t\t_class = "RenderMeshList"
\t\t\t\tchildren =
\t\t\t\t[
\t\t\t\t\t{{
\t\t\t\t\t\t_class = "RenderMeshFile"
\t\t\t\t\t\tname = "room"
\t\t\t\t\t\tfilename = "models/cstema/wardrobe/room.fbx"
\t\t\t\t\t\timport_scale = 1.0
\t\t\t\t\t\timport_filter =
\t\t\t\t\t\t{{
\t\t\t\t\t\t\texclude_by_default = false
\t\t\t\t\t\t\texception_list = [  ]
\t\t\t\t\t\t}}
\t\t\t\t\t}},
\t\t\t\t]
\t\t\t}},
\t\t\t{{
\t\t\t\t_class = "MaterialGroupList"
\t\t\t\tchildren =
\t\t\t\t[
\t\t\t\t\t{{
\t\t\t\t\t\t_class = "MaterialGroup"
\t\t\t\t\t\tname = "wardrobe"
\t\t\t\t\t\tremaps =
\t\t\t\t\t\t[
{remaps}
\t\t\t\t\t\t]
\t\t\t\t\t}},
\t\t\t\t]
\t\t\t}},
\t\t]
\t}}
}}
'''
with io.open(os.path.join(MDLD, "room.vmdl"), "w", encoding="utf-8", newline="\n") as f:
    f.write(vmdl)

print("materials (%d):" % len(MATERIALS))
print("\n".join(lines_report))
print("\nTGA written : %d  (%s)" % (written_tga, MATD))
print("vmat written: %d" % len(MATERIALS))
print("vmdl written: %s" % os.path.join(MDLD, "room.vmdl"))
tot = sum(os.path.getsize(f) for f in glob.glob(os.path.join(MATD, "*.tga")))
print("tga on disk : %.0f MB (source only, not shipped)" % (tot / 1048576))
