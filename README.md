# CS2 Arsenal

An in game weapon skin browser for Counter-Strike 2, written as a [ModSharp](https://github.com/Kxnrl/modsharp-public) module.

The browser shows the **real weapon** in the world with the **real finish** applied. There is no skin
artwork anywhere in the plugin. An earlier 2D version of this needed roughly 200 MB of extracted econ
renders and one CSS class pair per finish. This version spawns the item instead, so the preview *is*
the item.

It drives 55 weapons, 2017 finishes, 65 sticker collections, 11676 stickers, and 8 gloves with 94
glove finishes, all read from CS2's own schema.

## What it does

* Browse every weapon and every finish, previewed as a real weapon entity in front of the camera
* Place up to five stickers per weapon with position, rotation and wear
* Browse gloves and their finishes
* Equip a selection so it applies to the player's actual loadout
* Light the preview with a runtime spotlight, so it works on maps with no usable lighting where it
  happens to stand

## How it works

### Spawning the preview

This is the part that took longest to get right, so it is worth stating plainly.

```csharp
var built = EntityManager.SpawnEntitySync<IBaseWeapon>(
    SpawnClass(weapon),
    new Dictionary<string, KeyValuesVariantValueItem> { ["origin"] = $"{x} {y} {z}" });

built.AcceptInput("ChangeSubclass", null, null, def.ToString());
Paint(slot);
```

Three details carry the whole thing.

**ChangeSubclass is not optional.** A bare `weapon_ak47` entity is a shell with no item definition
behind it. The client cannot resolve it, so rifles render as nothing and knives kill the client
outright. Feeding it the econ definition index turns the shell into a real weapon. Every earlier
attempt at spawning weapons here skipped this step, which produced a long held belief that weapons
could not be spawned at all. They can. They were unfinished, not wrong.

**Spawn the entity class, not the item name.** `weapon_knife_karambit` and `weapon_bayonet` are item
definition names, not entities. All 20 knives spawn as `weapon_knife` and are told apart by the
subclass index.

**The origin goes in the keyvalues**, so the entity is created where it is wanted rather than at the
world origin and moved afterwards.

The payoff is that the player's pawn is never involved. There is no `GiveNamedItem`, so nothing can
collide with the loadout the player is actually carrying, there is no deploy sound, no window in
which the preview is theirs to pick up, and no drop physics to fight.

### Skins cannot be changed in place

The preview is destroyed and respawned for every finish. That is not laziness. **The client resolves
a weapon's skin when the entity is created and never looks again.**

The clearest proof is to take the working rebuild and remove only `GiveNamedItem` from it, so the
same entity is re-equipped, re-dressed and re-dropped:

```
EquipWeapon(weapon) -> Paint() -> DropWeapon(weapon)
```

Same code, same order, same engine calls. The finish does not change. The single difference between
that and the path that works is whether the entity is new.

Eight approaches were tested and falsified, each with server side confirmation that it really ran.
[docs/PREVIEW.md](docs/PREVIEW.md) has the full table, including the econ view dumped from both paths
and compared field by field. They are identical.

### Equipping

The browser never skins the player's weapon itself. A separate module already applies skins in the
`GiveNamedItem` post hook from a player cache, so equipping only has to write the row and refresh:

```csharp
await repository.UpsertWeaponSkin(steamId, def, paint, wear, seed, statTrak);
await repository.UpsertWeaponStickers(steamId, def, stickersJson);
cache.RefreshBySteamId(steamId);
```

The skin lands on the player's next spawn or buy, because that is when the engine gives the weapon.

Knives and gloves need **two** rows, not one. A loadout row decides which knife or glove the spawn
hook hands out, and the skin row decides what it wears. Saving only the skin row paints an item the
player never receives, which looks exactly like nothing happening at all.

### The interface

`custom_hud_layout` has exactly two channels from server to client: class overrides and dialog
variables. Dialog variables carry text only, never widths. Only `<Panel>`, `<Label>`, `<Image>` and
`<Button>` exist, `hittest` is not allowed on a `<Button>`, and one disallowed attribute fails the
entire layout silently.

**Scrolling.** There is no scroll or wheel event. `InstallClickListener` is the whole input surface,
so the server can never be told that a wheel moved. It does not need to be. A panel with
`overflow: squish scroll` scrolls on the client with no server involvement. The only requirement is
that the row exists in the markup. Each column declares 72 rows, which covers weapons, finishes and
collections whole, so those have no pager at all. Only a sticker collection can overflow, the largest
being 1404, so that list alone keeps one.

The layout is generated by [tools/gen_layout.py](tools/gen_layout.py) because 144 rows is not hand
editable.

**Caching.** Panorama caches compiled layouts for the lifetime of the CS2 process. A changed
`.vxml_c` or `.vcss_c` does not take effect on reconnect. The client must fully restart. This cost
several hours once, when a correctly placed, cleanly compiling button did not appear.

### Lighting

CS2 bakes lighting at map compile, so an unlit spot leaves the preview a black silhouette. A runtime
light does work, but only if it is built in the right order:

```
1. CreateEntityByName("light_barn")   created, NOT spawned
2. write the schema fields
3. DispatchSpawn(keyvalues)           lightcookie goes here
4. Teleport, set m_bEnabled, send Enable
```

Step 2 before step 3 is the whole trick. `SpawnEntitySync` creates and spawns in one call, so
anything written afterwards lands on an already spawned light and does nothing. A barn light is a
spotlight with a cone, so it has to be aimed along the view direction. Facing it back at the camera
lights nothing.

## Catalogues

Both catalogues are built from CS2's own `items_game.txt` and `csgo_english.txt`, extracted from
`pak01_dir.vpk`.

* [tools/build_catalog.py](tools/build_catalog.py) builds the weapon and finish catalogue.
  `items_game.txt` contains **55 separate sticker_kits blocks** and several `paint_kits` blocks.
  Reading only the first gives 995 stickers instead of 11676.
* [tools/build_gloves.py](tools/build_gloves.py) builds the glove catalogue. Gloves are not in the
  loot list format weapons use, so the weapon builder cannot see them at all. Each glove has **two**
  generations of paint kit name prefix, an original one and a later `glove_<type>_` scheme, and
  mixing them up silently offers finishes belonging to a different glove. Broken Fang is definition
  4725, outside the 5027 to 5035 run, and its paints are prefixed `operation10_` rather than by the
  glove name. The mapping is checked against every paint kit whose material path lives under gloves,
  and all 94 are accounted for.

## Layout of this repository

```
src/Modules/ArsenalMenu.cs     the browser
src/Modules/WeaponSkins.cs     applies a saved skin on give
src/Modules/Gloves.cs          applies saved gloves on spawn
src/Modules/StickerSchemas.cs  sticker attribute names
src/Services/SkinApplier.cs    the attribute writing primitives
src/Services/PlayerCache.cs    per player inventory cache
src/Data/                      schema and repository
ui/layout/skins.xml            generated, do not edit by hand
ui/styles/skins.css
tools/                         catalogue and layout generators
docs/                          how it works, and what does not
```

This is the arsenal subsystem extracted from a larger plugin and published as a reference rather than
a drop in build. It expects ModSharp's `InterfaceBridge` and a MySQL compatible database, and module
registration and configuration live in the parent plugin.

## Things that do not work

Recorded so they are not attempted again. Each was tested, not assumed.

* Re-skinning a weapon that is already in the world. See [docs/PREVIEW.md](docs/PREVIEW.md).
* `UpdateEconItemAttributes` on a live entity. It returns true, writes the attributes correctly, and
  changes nothing on screen.
* `cl_fullupdate`, and forcing the client to re-create the entity through the transmit set.
* Building a weapon with `CreateEntityByName` plus `DispatchSpawn`. It crashes the server and every
  client, because it is missing `ChangeSubclass`.
* Giving a weapon entity a glove definition index so a glove renders as a 3D model. The host entity
  keeps its own model, and it crashes the server.
* Changing gloves on a player who is already alive. Gloves resolve when the pawn is built, which is
  why they are applied in the spawn hook.
* `UTIL_DispatchParticleEffect`, a broken signature on Linux that no ops silently. Spawn an
  `info_particle_system` entity instead.
* Runtime `point_prefab`, which crashes the server. Spawn groups are load time only.

## Compatibility

The interface is built on ModSharp's `custom_hud_layout` support, which lives on the open pull
request [Kxnrl/modsharp-public#123](https://github.com/Kxnrl/modsharp-public/pull/123) and is not
in `master` yet. Build against `Sharp.Shared.dll` from that branch, or from its CI artifact, and
run the matching runtime; the API and the native library have to come from the same revision.

The branch is under active development and does rename things, so expect small breaks when you
update. Moving from the 27 August snapshot to the 29 August head cost exactly three renames:
`ICustomHudManager` to `IPanoramaManager`, `GetCustomHudManager` to `GetPanoramaManager`, and the
class override enum `Present`/`Absent` to `ForceEnable`/`ForceDisable`.

`master` also has a separate script interop API (`IScriptManager`) that bridges C# to CS2's
`cs_script` V8 VM. It is **not** a replacement for this. cs_script runs from a `point_script`
entity that ships inside the map, and a `point_script` spawned at runtime never executes: tested
with three keyvalue names, both spawn orderings and three path forms, using Valve's own compiled
`hello.vjs_c`. On a server that runs arbitrary workshop maps the script VM is unreachable, whereas
a `custom_hud_layout` entity can be spawned at runtime on any map, which is how this works.

## Licence

MIT.
