# Scoping the preview, and the crash it caused for months

The browser spawns real weapons in the world, and a world entity is transmitted to every
client by default. Without scoping, everyone on the server watches a rifle hang in mid air
while somebody browses skins. Scoping it is `ITransmitManager`, and getting it wrong took a
lab server down nineteen times and a live one twice.

## The rule

**Never remove a transmit hook by hand from an entity you are about to destroy.**

```csharp
// arming, on a freshly spawned preview
_bridge.TransmitManager.AddEntityHooks(entity, false);
_bridge.TransmitManager.SetEntityState(entity.Index, controller.Index, isOwner, -1);

// tearing down
entity.Kill();          // and nothing else. The hook dies with the entity.
```

`DropItem` used to call `ShowToEveryone`, which did `ClearReceiverState` then
`RemoveEntityHooks`, and only then `Kill()`. That is what crashed:

```
segfault every ~20 preview rebuilds     ->   139 rebuilds and counting, no crash
```

Nothing else that uses this API does it. ModSharp's own consumers arm a hook and never
remove it; they only ever read `IsEntityHooked`. Their entities are long lived, created once
per player, and what changes is `SetEntityState` and a CSS class. Ours is different in the
one way that matters: a finish only applies before the entity networks, so **every click
destroys a weapon and builds another**, and the next spawn takes the dead one's index
straight back. Hand-unhooking in that window is what nothing else in the ecosystem does.

## Two more that were wrong on the way

**The second argument is a CONTROLLER index, not a pawn index.**

```csharp
bool SetEntityState(EntityIndex entity, EntityIndex controllerIndex, bool transmit, int channel);
```

This passed `pawn.Index` for months. Controllers sit at low entity indices and pawns at much
higher ones, so every scoped preview wrote receiver state past the end of the table. On
CSTEMA.LT that surfaced as `*** stack smashing detected ***`, which is glibc reporting
exactly that. Fixing it was necessary and did not, on its own, stop the crash.

**`defaultTransmit` should be `false`.** `true` means "visible to everyone until told
otherwise", so the preview is briefly shown to the whole server and any receiver the loop
does not reach keeps seeing it. `false` plus enabling the owner fails closed.

## How this was found, because the method mattered more than the answer

Four hypotheses were wrong before the right one, and each looked reasonable:

| hypothesis | why it looked right | why it was wrong |
|---|---|---|
| dead `lightcookie` handle | the failing texture is logged immediately before our light on the server that crashed, and never on the lab | pointing the cookie at a path that cannot resolve, deliberately, still crashed - and so did a good cookie |
| rapid clicking | every crash followed a burst of finish clicks | a debounce collapsed two clicks into one build and it died on that single rebuild |
| kill timing | we killed an entity in the same frame as its replacement, which recycles the index | deferring the kill changed nothing except leaving a ghost weapon in the shot |
| out-of-bounds `SetEntityState` | genuinely a bug, and it explains a stack smash precisely | fixing it left the crash at 22 rebuilds |

What actually found it was a **bisect switch**, not reasoning: `armory_t_scope` skips
`ShowOnlyTo` on a running server. One test proved scoping was the difference. From there it
was a question of which part of scoping, and the answer came from reading what other people's
working code does NOT do.

Those switches are still in - `armory_t_scope`, `armory_t_paint`, `armory_t_pin` - because
the next crash of this shape will be found the same way. A toggle that isolates one call on a
live server is worth more than any amount of staring at a log.

## Symptoms to recognise

* `*** stack smashing detected ***: terminated` - a bounded buffer overrun. Look for an index
  passed where a different kind of index was expected.
* `Segmentation fault (core dumped)` with nothing in ModSharp's `Error` log - a native fault,
  so the managed side made an ordinary call and the engine died later. Do not go looking for
  a C# exception; there will not be one.
* Both, appearing only after N repetitions rather than on the first - state accumulating or
  being corrupted a little at a time, not a bad call in itself.
