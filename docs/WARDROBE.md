# The wardrobe

The browser used to preview weapons wherever the player happened to be standing, which meant
the backdrop was whatever the map gave you: a wall, a skybox, or a firefight. The wardrobe
replaces that with a room of our own, and it works on every map because it is a **model**, not
world geometry.

That distinction is the whole design. Everything that failed before failed because it tried to
put geometry into a running map.

## Why a model and not a room

Recorded so these are not attempted again. Each was tested.

* **Runtime `point_prefab` crashes the server.** Tried twice, including with a prefab that was
  not already loaded. Spawn groups are load time only, so a room prefab cannot be instantiated
  into a running map.
* **`-dual_addon` mounts another addon's files, not its world geometry.** A room shaped map
  mounted that way has no geometry in the running level.
* **Precaching is map load only.** `IEntityResourceManifest` does not outlive the load, so
  there is no API to precache mid map. CS2Fixes says the same thing in its own source:
  *"Any resource adding MUST be done here, the resource manifest is not long-lived"*
  (`gamesystem.cpp`). Its only spawn group call is `GetSpawnGroups`, which is read only and
  used to clear leaked groups off clients.

A `prop_dynamic` carrying a custom `.vmdl` sidesteps all of it. It is precached at map load
like any other model, it spawns anywhere, and it needs no cooperation from the map.

## The player never moves

The pawn stays exactly where it is standing. Only the **view** travels:

```csharp
pawn.GetCameraService().ViewEntity = camera;   // ICameraService.ViewEntity
```

That is the same mechanism CS2Fixes uses for `point_viewcontrol`, and ModSharp exposes it
directly. A `point_camera` is spawned inside the room and the browsing player's view is bound
to it; `Close()` sets it back to null.

Binding the view rather than teleporting removes a whole class of problems at once: no fall
damage, no kill triggers, no out of world removal, no position to save and restore, and
nothing to get wrong when someone dies or disconnects halfway through. The pawn is already
frozen, hidden and non solid while the browser is open, so it is safe where it stands.

It also means the room never needs a physics hull. It spawns `solid 0` with
`SolidType.None`, which is why an imported mesh can be used exactly as it comes out of
Blender.

## Scale: the trap that cost the most time

**`import_scale = 0.0254` in the `.vmdl` is load bearing.**

Source units are inches. The source mesh was authored in metres, so it was scaled by 39.3701
on export. In game the room came out roughly forty times too big: the weapon beside the camera
looked normal while the room around it was the size of a cathedral, and standing at eye height
88 put the camera on the floor of it.

The FBX was not wrong. Its raw vertex values were exactly the intended numbers (room height
92.2, `UnitScaleFactor` 1.0). **Source applies its own metres to inches conversion on import**,
so the factor landed twice. `import_scale = 0.0254` cancels the second one.

The lesson generalises: measure the compiled result in game against something of known size
before trusting any of it. A CS2 player is 72 units tall. Every measurement taken in Blender
looked correct, because the mesh always was.

## How the compiler finds materials

Not by the material's name. It takes the material's **base colour texture path** and swaps the
extension:

```
GetFbxMaterialPath Failed! FbxMesh: Mesh.001 FbxMaterial: Sheets
   - Searched sheets.vmat
   - Searched materials/cstema/wardrobe/sheets_basecolor.vmat
```

Two consequences:

* The FBX must carry texture paths at all. Exporting with `path_mode='STRIP'` removes them,
  the compiler cannot derive a directory, and it falls back to a bare `sheets.vmat` which
  resolves nowhere ("Trying to load an illegal resource name").
* The base colour texture has to be named after the material, so `chair.tga` and not
  `chair_basecolor.tga`, or the compiler goes looking for `chair_basecolor.vmat`.

`MaterialGroup` remaps do **not** help here. Those are alternate skins; the base reference has
to resolve on its own.

## Textures

The source set was 48 files, all 4096x4096, 267 MB of PNG. Compiled with block compression and
mips that would have been somewhere near 500 MB to 1 GB **per client**, since every client
downloads the model.

It ships as **18.8 MB**: 1.5 MB model, 17.3 MB materials and textures.

Two things got it there:

* **Ten of the 48 were 4K files holding a single value** - flat metallic maps, one flat normal
  map at (128,128,255). Those are inline constants in the `.vmat` instead, which is the form
  CS2's own material sources already use:
  `"TextureMetalness" "[0.000000 0.000000 0.000000]"`.
* Base colour and normal at 1024, roughness and metalness at 512. The room is a backdrop; it
  is never inspected up close.

`tools/build_room_assets.py` does both, writes the `.vmat` files and generates the `.vmdl`.

Note that materials and textures are **client side only**. The server needs the `.vmdl_c` and
nothing else, which is why the character model roster on this server costs 16 MB there and
147 MB on a client.

## Framing

Three values, tuned live and then baked in as defaults:

```csharp
private float _roomFwd    = 150f;   // how far back the camera sits along the room's long axis
private float _roomUp     = 76f;    // eye height. The ceiling is at 92
private float _roomYawOff = 90f;    // which wall the camera faces
```

They are fields rather than constants because each has a server command that cycles it, so the
framing can be adjusted on a live server without a rebuild:

```
armory_arsenal_roomyaw    turn the room 45 degrees about the camera
armory_arsenal_roomup     raise the camera 8 units, wrapping 12 to 88
armory_arsenal_roomfwd    move the camera 25 further back, wrapping 60 to 260
```

They cycle rather than take a value because **server commands do not receive arguments over
rcon** on this build: `GetArg(1)` comes back empty and `ArgCount` stays 1.

Two things worth knowing about the geometry. The room is turned about its own origin, not
about the camera, so changing the yaw also changes where in the room the camera ends up. And
zoom moves the **weapon**, not the camera, so in an enclosed room zooming out eventually walks
the item into a wall. `ICameraService.FieldOfView` is the alternative if that becomes a
problem.

## One room per player

`_room` is an array indexed by player slot, torn down per slot, and nulled on map change with
`_item` and `_lamp`.

It was briefly a single shared `List<IBaseEntity>` sitting between two per slot arrays. With
two people browsing, the second to open destroyed the first one's room and the first to press
ESC destroyed both, and the orphaned entity was left in the world with nothing tracking it.
This is the second time this module has had that exact bug; `_owner` was the first.

Teardown is covered on every path: `Close()` on ESC, `OnPlayerKilledPost` for dying mid
browse, `OnClientDisconnected` for leaving, and the level change for everything else.
`PlaceRoom` also drops the slot's existing room before spawning, so reopening cannot stack.
