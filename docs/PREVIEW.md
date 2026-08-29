# Spawning a weapon preview

How the arsenal browser puts a weapon in the world, why it works, and why five other
approaches did not. Written down because most of these look correct, several return
success, and one of them is in the ModSharp API specifically for this job and still does
not do it.

## What works

```csharp
var built = EntityManager.SpawnEntitySync<IBaseWeapon>(
    SpawnClass(weapon),                                  // entity class, not item name
    new Dictionary<string, KeyValuesVariantValueItem> { ["origin"] = $"{x} {y} {z}" });

built.AcceptInput("ChangeSubclass", null, null, def.ToString());   // the identity
Paint(slot);                                                       // paint, wear, seed
```

Three details carry the whole thing.

**ChangeSubclass is not optional.** A bare `weapon_ak47` entity is a shell with no item
definition behind it. The client cannot resolve it, so rifles render as nothing and knives
kill the client outright. Feeding it the econ definition index turns the shell into a real
weapon. Every earlier attempt at spawning weapons skipped this, which is what produced the
note in ARSENAL.md that `SpawnEntitySync` "produces a fundamentally wrong object". The
object was not wrong, it was unfinished.

**Spawn the entity class, not the item name.** `weapon_knife_karambit` and `weapon_bayonet`
are item definition names. They are not entities. All 20 knives spawn as `weapon_knife`
and are told apart by the subclass index, which is the same mechanism as above.

**The origin goes in the keyvalues.** The entity is placed where it is wanted at spawn time
and moved afterwards, rather than being born at the world origin.

The payoff is that the player's pawn is never touched. No `GiveNamedItem`, so nothing can
collide with the loadout they are actually carrying, no deploy sound, no window in which
the weapon is theirs to pick up, and no `DropWeapon` throw physics to fight.

## Why the skin cannot be changed in place

The preview is destroyed and respawned for every finish. That is not laziness. **The client
resolves a weapon's skin when the entity is created and never looks again.**

The clearest proof is the last experiment rather than the first. Take the working rebuild
and remove only `GiveNamedItem` from it, so the same entity is re-equipped, re-dressed and
re-dropped:

```
EquipWeapon(weapon) -> Paint() -> DropWeapon(weapon)
```

Same code, same order, same engine calls as a working give. The finish does not change. The
single difference between that and the path that works is whether the entity is new.

### Ruled out, with the symptom each produced

Two symptoms recur and they mean different things. **Default** means the client re-read the
item and rejected it. **Stuck** means the client accepted it and never re-read.

| attempt | server said | screen |
|---|---|---|
| `UpdateEconItemAttributes` | returned true | default |
| `cl_fullupdate` on the client | command exists | unchanged |
| per-viewer transmit toggle, 150 ms gap | fired | unchanged |
| `SetOwner(pawn)` then dress | owner set | stuck |
| `m_nFallbackPaintKit` / `Seed` / `Wear` | 3/3 written | stuck |
| `ChangeSubclass` + `SetBodygroup` on a live weapon | 2 inputs fired | stuck |
| networked id bump via `NetworkStateChanged` | all flags applied | stuck |
| `EquipWeapon` -> `Paint` -> `DropWeapon` | detach true | stuck |

Notes worth keeping:

* The econ view written in place is identical to a working fresh give, field for field:
  same def index, quality, level, account id, `init=True`, and `idLow` is -1 in both. Only
  `m_iItemIDHigh` differs, and it is meant to. So the data was never the problem.
* `UpdateEconItemAttributes` is a give-time call. It writes the attributes correctly and
  returns true on a live entity, and nothing appears. It also stamps an empty name tag:
  the native does `if (pNameTag) SetCustomName(pNameTag)` and C#'s `string.Empty` marshals
  non-null, so every call sets a blank custom name. Passing null throws in the marshaller
  before reaching the native, so whether that is what drove it to default is still untested.
  It carries four sticker slots as id and wear only, with no position or rotation, so it
  could never express what the sticker editor sets.
* `ClaimItem` bumps the item id on every call, but through `SetItemIdHighLocal`. In ModSharp
  a `Local` setter writes without a network state change. Harmless on a give, because the
  entity networks fresh a moment later; useless on a weapon already in the world.
* `DetachWeapon` returning false for every weapon was not a refusal. A dropped weapon is
  already out of the pawn's weapon services, so there was nothing to detach. Pair it with
  `IWeaponService.EquipWeapon` and it returns true. Equipping holsters the weapon and
  detaching does not undo that, so it has to be made visible again explicitly.
* `CreateEntityByName` + write + `DispatchSpawn`, the ordering that made `light_barn` work,
  crashes the server and every client when applied to a weapon. A light is a point entity;
  a weapon is not. It was missing `ChangeSubclass`.

## Equipping

The browser never skins the player's weapon itself. `WeaponSkins` already applies skins in
the `GiveNamedItem` post hook from the player cache, so EQUIP only has to write the row and
refresh:

```csharp
await _repository.UpsertWeaponSkin(steamId, def, paint, 0.01f, 0, null);
_cache.RefreshBySteamId(steamId);
```

The skin lands on the next spawn or buy, because that is when the engine gives the weapon.
Applying it to a weapon already in the player's hands would mean re-giving it, for the
reason above. `UpsertWeaponSkin` only touches paint, wear and seed, so name tags, stickers,
keychains and custom models on that row survive.

Weapons picked up off the ground do not pass through `GiveNamedItem`, so they are not
skinned by that hook. That is a separate gap and is not fixed here.

## Method

Two habits earned their keep and one mistake kept repeating.

Instrument the branch that does nothing. An in-place update guarded on
`StickerKey(s).Length > 0` never ran once, because that string always contains five
`0,0,0,0,0;` groups and is never empty. It logged success on the path it fell through to,
so it read as working code with a broken result for a whole round of testing.

Log what happened, not what was configured. A line printing the `_detachExit` flag rather
than the actual return value reported "detached" on weapons that had been dropped, which
sent the investigation down a branch that had never executed.

Do not let a good analogy outrank a recorded result. ARSENAL.md said a hand-built weapon
crashes clients. The create-then-spawn ordering from the lighting work was a genuinely
better idea and still crashed the server and a client, because it was missing the one input
that gives the entity an identity.
