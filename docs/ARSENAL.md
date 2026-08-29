# ARMORY / ARSENAL

An in game weapon skin browser for CS2, written as a ModSharp module. It shows the real
weapon in the world with the real finish applied, so there is no skin artwork anywhere in
the plugin. The old 2D version of this needed roughly 200 MB of extracted econ renders and
one CSS class pair per finish. This version spawns the item instead, and the preview is the
item.

Catalogue sizes it drives: 55 weapons, 2017 finishes, 65 sticker collections, 11676
stickers.

## Layout

```
src/Armory/Modules/ArsenalMenu.cs     the module
../ArmoryUI/v5-skins/                 the Panorama layout and stylesheet
../ArmoryUI/v5-skins/tools/           generators and deploy script
game/sharp/configs/armory_arsenal.json    weapon and finish catalogue
game/sharp/configs/armory_stickers.json   sticker catalogue
```

Both catalogues are built from CS2's own `items_game.txt` and `csgo_english.txt`, extracted
from `pak01_dir.vpk`. Note that `items_game.txt` contains **55 separate `sticker_kits`
blocks**. Reading only the first gives 995 stickers instead of 11676. The same is true of
`paint_kits`.

## Showing the weapon

This is the part that took longest to get right, so it is worth stating plainly.

**A weapon has to be created the way the game creates one.** Call `GiveNamedItem` on the
player's pawn, apply the econ attributes while it is still owned, then `DropWeapon`. That
runs the engine's own construction path, the same one every dropped gun on the ground goes
through.

Building the entity yourself with `SpawnEntitySync` produces something that is not the same
object. Rifles come out with no renderable world model, and an untouched knife with no
attributes at all crashes the client the moment it is asked to draw one. A bare knife probe
placed at the preview spot is what proved this: it had no paint, no definition index and no
attributes, and it still killed the client.

Other things that matter:

* `SpawnEntitySync` has a typed generic overload. The untyped one returns a plain
  `BaseEntity` that is never an `IBaseWeapon`, so every attribute write silently does
  nothing.
* Econ attributes only apply before the entity networks. They also need
  `view.SetInitializedLocal(true)`, otherwise the client ignores the whole attribute block.
* Claim the item with the viewing player's own account id.
* Do not call `SetModel` on it. Forcing a world model crashes clients, and precaching a
  model server side is not the same as the client having it ready. `IModelGuard` refuses
  these for a reason.
* Do not pin it with non solid, zero gravity, zero velocity and a per frame teleport. That
  fights the engine's own simulation on an entity it just built and crashes the client.
  `MoveType.None` plus a single teleport is enough.
* All 20 knives share `weapon_knife` as their entity class and carry their identity in the
  item definition index.
* Pickup is refused through the `PlayerCanAcquire` hook. `PreventWeaponPickup` does not
  hold.

## Lighting

CS2 bakes lighting at map compile, so an unlit spot leaves the weapon a black silhouette.
A runtime light does work, but only if it is built in the right order. This recipe is taken
from CS2Fixes' flashlight.

```
1. CreateEntityByName("light_barn")        created, NOT spawned
2. write the schema fields
3. DispatchSpawn(keyvalues)                lightcookie goes here
4. Teleport, set m_bEnabled, send Enable
```

Step 2 before step 3 is the whole trick. `SpawnEntitySync` creates and spawns in one call,
so anything written afterwards lands on an already spawned light and does nothing.

Fields:

```
m_bEnabled       false at build time, true after spawn
m_Color          without this the light emits black
m_flBrightness   1.0
m_flRange        2048
m_flSoftX        1.0
m_flSoftY        1.0
m_flSkirt        0.5
m_flSkirtNear    1.0
m_vSizeParams    (45, 45, 0.02)   the cone. Without it the light has no shape
m_nCastShadows   1
m_nDirectLight   3
```

`lightcookie` must be a keyvalue passed to `DispatchSpawn`, because the schema property is
a resource handle:

```
lightcookie = materials/effects/lightcookies/flashlight.vtex
```

A barn light is a spotlight with a cone, so aim it. Place it between the camera and the
weapon facing along the view direction. Facing `viewYaw + 180` points the cone back at the
camera and lights nothing.

Dynamic lights built this way do light weapon models.

## The UI

`custom_hud_layout` has exactly two channels from server to client: class overrides and
dialog variables. Dialog variables carry text only, never widths. Only `<Panel>`,
`<Label>`, `<Image>` and `<Button>` exist, `hittest` is not allowed on a `<Button>`, and one
disallowed attribute fails the entire layout silently.

**Scrolling.** There is no scroll or wheel event. `InstallClickListener` is the whole input
surface, so the server can never be told a wheel moved. It does not need to be: a panel with
`overflow: squish scroll` scrolls on the client with no server involvement. The only
requirement is that the row exists in the XML. Each column declares 72 rows, which covers
weapons (55), finishes (60 at most) and collections (65) whole, so those have no pager at
all. Only a sticker collection can overflow, the largest being 1404, so that list alone
keeps one and the server hides it when the collection fits.

The layout is generated by `tools/gen_layout.py` because 144 rows is not hand editable.

**Caching.** Panorama caches compiled layouts for the lifetime of the CS2 process. A changed
`.vxml_c` or `.vcss_c` does not take effect on reconnect. The client must fully restart.
This cost several hours once: a correctly placed, cleanly compiling button that "did not
appear" was the cached layout, not the markup.

**Images.** Every image reference is resolved against this addon's own content tree. A path
into Valve's `pak01` fails to compile as `_png.vtex_c` and renders as an empty box as
`_png.vtex`, and neither `{images}` nor `{resources}/images` changes that. Art has to live
in our tree and be referenced as `.png`, which the compiler rewrites to `_png.vtex_c`.
`resourcecompiler` refuses a bare PNG on this build, so `tools/png2vtex.py` wraps them.

**Sizing.** Size control groups from the real CSS values. `.PgBtn` is 34px wide with a 6px
margin, so an arrow costs 40, not 26. Undersizing clipped the right hand arrow off every
group.

**Labels.** A `<Label>` draws its glyphs at the top of its own box. Setting `height: 100%`
on a label inside a button pins the text to the button's top edge. Give the label no size
and centre the label itself.

## Stickers

Five slots per weapon, written as econ attributes through `StickerSchemas`. Position,
rotation and wear all work. `sticker slot N scale` does not: CS2 stores the value and
ignores it, matching in game sticker crafts, which allow rotating and repositioning but
never resizing. The fourth control is wear for that reason.

Because attributes only apply before the entity networks, every sticker edit rebuilds the
preview through the give and drop path. The rebuild key includes all five slots.

## Hiding the player

While the browser is open the player is taken out of the world:

* pawn blocked from transmission, so nobody renders them
* `SolidType.None` and the `Debris` collision group, so they cannot be shot and cannot
  block hitscan for someone behind them
* `MoveType.None`

Everything the browser spawns is scoped per receiver with `AddEntityHooks` and
`SetEntityState`, so nobody else sees a rifle hanging in mid air. Spawned entities are
transmitted to everyone by default.

True spectate does not work here, because `GiveNamedItem` needs a pawn and a spectator does
not have one.

## Things that do not work

* `IEntityManager.UpdateEconItemAttributes` does not re-skin a weapon that is already standing
  in the world. This was tested to exhaustion, so it is worth recording what was ruled out.

  The native returns true and writes the data correctly. Dumping the econ view from both paths
  and comparing them field by field gives the same answer every time:

  ```
  view[fresh give] def=8 qual=0 lvl=1 idHigh=16391 idLow=4294967295 acct=447828158 init=True
  view[in-place]   def=8 qual=0 lvl=1 idHigh=16392 idLow=4294967295 acct=447828158 init=True
  ```

  Identical apart from `m_iItemIDHigh`, which the native bumps on purpose. Note `idLow` is -1 in
  both: the native hardcodes it, and our own `ClaimItem` writes the same value, so that is not
  the difference either. The server side truth is right and the weapon still renders with no
  finish.

  None of these made it appear:

  * `NetworkStateChanged` on `m_iItemIDHigh`, `m_iItemIDLow`, `m_iAccountID` and
    `m_NetworkedDynamicAttributes`, plus `m_AttributeManager` as a struct on the weapon.
  * Re-running our own give-time `Paint()` on top of the native call.
  * `cl_fullupdate` on the client. The command exists and a full state resend does not fix it,
    which rules out the delta simply never being sent.
  * Pulling the preview out of the viewer's transmit set for 150 ms and putting it back, to
    force the client to destroy and re-create its copy. The toggle fires cleanly and changes
    nothing. Whether the client really destroyed the entity was not verified, so this is the one
    door left slightly open.

  The important detail is WHAT the weapon renders as afterwards: not the previous finish, but
  the DEFAULT one. That rules out the client ignoring the update. If it were ignoring it, the
  old finish would simply stay on screen. Changing to default means the client re-read the item,
  evaluated it and rejected it, then fell back.

  So this is not a networking problem, and the three fixes above all addressed the wrong bug.
  The client is receiving the update. It refuses the contents. Whatever the give path puts on
  the view that makes it acceptable is not surviving an update on a live entity, even though
  every field we can read back matches.

  `DetachWeapon` is not a way round it. It returns false for every weapon tried (ak47, awp, aug,
  bizon), both before and after `SwitchWeapon(null)` to empty the pawn's hands. ModSharp prints
  resolved addresses for the weapon services functions it finds at startup and
  `CCSPlayer_WeaponServices::DetachWeapon` is not among them, so it may simply not be wired up
  on this build. Unverified.

  The call is still the right one for a weapon that has not networked yet, and it carries four
  sticker slots as id and wear only: no position, no rotation, no fifth slot, so it could never
  express what the sticker editor sets.
* `UTIL_DispatchParticleEffect` is a broken signature on Linux. It no ops silently, with no
  error and no effect. Spawn an `info_particle_system` entity instead, which does render.
* Runtime `point_prefab` crashes the server. Tested twice, including with a prefab that was
  not already loaded. Spawn groups are load time only, so a room prefab cannot be
  instantiated into a running map.
* `-dual_addon` mounts another addon's files, not its world geometry. A room shaped map
  mounted that way has no geometry in the running level.
* Server commands do not receive arguments over rcon. `GetArg(1)` comes back empty and
  `ArgCount` stays 1. Cycle through options on repeated invocation instead.

## Commands

```
armory_arsenal            open the browser
armory_arsenal_close      close it
armory_arsenal_light      spawn a test light on the player
armory_arsenal_probe      report which light schema fields resolve
armory_arsenal_fx         re trigger the preview light
armory_arsenal_void       toggle the wardrobe above or below the map
armory_arsenal_control    dispatch a visible particle, to prove dispatch works
```

There is no client command yet, so a player cannot open the browser themselves.

## Deploying

```
cd ArmoryUI/v5-skins && bash tools/deploy.sh      layout, styles, images
dotnet build src/Armory/Armory.csproj -c Release  the module
```

Copy `Armory.dll` to `game/sharp/modules/Armory/`, and the compiled `.vxml_c`, `.vcss_c`
and `_png.vtex_c` files to `game/sharp/assets/`. Module changes need a server restart.
Layout and stylesheet changes need a full CS2 restart on the client as well.

## Debugging notes

Two habits would have saved most of the time spent on this.

Instrument the silent path first. Several of the hardest bugs here were early returns with
no logging: the `IBaseWeapon` cast that failed on every call, the pager hidden by id where
the panel had no id, the schema probe that only logged offsets above zero and so reported
working lookups as missing.

Bisect against a known good. A long hunt for a pulsing light turned out to be the map, since
`cosmic_princess_kaguya` animates its own lighting. Pointing the code at a particle path
that did not exist, and seeing no change, is what proved the effect on screen was never ours.
