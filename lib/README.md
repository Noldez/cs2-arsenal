Put Sharp.Shared.dll here.

It must come from a ModSharp build that still exposes the custom_hud_layout API
(ICustomHudManager, ICustomHudLayout). Upstream removed those in favour of the
script interop API, so the current NuGet package will not build this.
