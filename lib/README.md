Put `Sharp.Shared.dll` here.

`custom_hud_layout` support has been in ModSharp `master` since 29 August 2026, so a fork is
no longer needed: take `Sharp.Shared.dll` from a master build, or from its CI artifact.

Whichever you use, the managed assembly and the native runtime have to come from the **same
revision**. Mixing them fails at load with a missing export rather than anything that points
at the real cause.
