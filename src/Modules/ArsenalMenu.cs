using Armory.Data;
using System.Text.Json;
using Armory.Services;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameObjects;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Armory.Modules;

/// <summary>
///     ARMORY / ARSENAL - the in-game weapon skin browser, built on the creation screen's design.
///     <br /><br />
///     There is NO skin art anywhere in this. The old 2D browser needed ~200MB of extracted econ
///     renders and 2017 CSS class pairs to show a picture of each finish; here the real weapon
///     entity is spawned and the paint written onto it, so the preview IS the item. The layout only
///     ever carries text, and the catalogue is 85KB rather than 200MB of images.
///     <br /><br />
///     Paint is applied through <see cref="ISkinApplier" />, the same implementation the loadout
///     system uses - that code resolves a raw engine function and writes through a schema offset,
///     so it must exist exactly once.
/// </summary>
internal sealed class ArsenalMenu : IArmoryService, IGameListener
{
    private const string LayoutResource = "panorama/layout/custom_game/skins.vxml_c";
    private const string LayoutName     = "armory_arsenal";
    private const string PropName       = "armory_arsenal_item";
    private const string CamName        = "armory_arsenal_cam";
    private const string RoomName       = "armory_arsenal_room";
    private const string LampName       = "armory_arsenal_lamp";
    private const float  LampHeight     = 34f;

    /// <summary>
    ///     Backdrop model. A plain dev cube - it ships in pak01, is not map-specific, and we
    ///     precache it ourselves, so it resolves anywhere.
    /// </summary>
    private const string RoomModel      = "models/dev/dev_cube.vmdl";

    /// <summary>
    ///     Particle systems that carry a REAL light renderer, not just a glow sprite.
    ///     <br /><br />
    ///     CS2 has no spawnable light entities, but its particle system does have light
    ///     renderers (C_OP_RenderStandardLight), and a handful of shipped effects use them.
    ///     These two are Valve's own answer to this exact problem - lighting a model that is
    ///     being presented in a UI - so they are the right tool rather than a workaround.
    /// </summary>
    /// <summary>
    ///     OUR particle, not Valve's. Theirs is a one-shot reveal flash that decays; a dispatched
    ///     effect is a temporary entity, so the particle's own lifecycle is what decides whether
    ///     it lingers. This one has the decay and fade ramp removed and a near-infinite lifespan.
    /// </summary>
    private const string LightFx   = "particles/armory/wardrobe_light.vpcf";

    /// <summary>Loudly visible, purely to prove whether dispatch works at all.</summary>
    private const string ControlFx = "particles/inferno_fx/molotov_fire_a.vpcf";

    /// <summary>Every HUD element off.</summary>
    private const uint   HudHidden      = 0xFFFFFFFF;

    /// <summary>
    ///     Rows declared per column in the layout. The client scrolls them natively -
    ///     custom_hud_layout has no scroll event at all, so the server is never told about a
    ///     wheel and does not need to be. At 72, weapons (55), finishes (max 60) and
    ///     collections (65) all fit whole and need no pager; only a sticker collection can
    ///     overflow, the largest being 1404.
    /// </summary>
    private const int   Rows        = 72;
    private const int   MaxSlots     = 64;
    private const int   StickerSlots = 5;
    /// <summary>
    ///     A gun is not a character. The creation screen orbits the CAMERA around a 72-unit
    ///     mannequin, which frames a person well but leaves a 46-unit rifle drifting off-centre as
    ///     the arc sweeps. Here the camera is nailed dead ahead and the ITEM spins instead - the
    ///     subject cannot leave the frame, and turning the object in place is how you inspect a gun
    ///     anyway.
    /// </summary>
    private const float ZDrop    = 0f;    // dead level with the eye, so the camera needs no pitch
    private const float RotStep  = 15f;   // degrees per nudge
    private const float StartYaw = 90f;   // broadside to the camera
    private const float PanStep  = 2f;    // world units per nudge

    /// <summary>
    ///     How far ahead of the camera each class of item sits. One fixed distance cannot serve
    ///     both a 46-unit AWP and a 7-unit knife - the rifle overflows the frame or the knife
    ///     becomes a speck - so the framing is bucketed by what the thing actually is.
    /// </summary>
    private static float DistanceFor(string weapon)
    {
        if (IsKnife(weapon))
        {
            return 26f;
        }

        return weapon switch
        {
            "weapon_glock" or "weapon_usp_silencer" or "weapon_hkp2000" or "weapon_p250"
                or "weapon_elite" or "weapon_fiveseven" or "weapon_tec9" or "weapon_cz75a"
                or "weapon_deagle" or "weapon_revolver" => 34f,
            "weapon_mac10" or "weapon_mp9" or "weapon_mp7" or "weapon_mp5sd" or "weapon_ump45"
                or "weapon_p90" or "weapon_bizon" => 46f,
            "weapon_m249" or "weapon_negev" => 58f,
            _ => 52f,
        };
    }

    /// <summary>Rarity name to the R0..R6 class the stylesheet tints rows with.</summary>
    private static readonly string[] Rarities =
        { "consumer", "industrial", "milspec", "restricted", "classified", "covert", "contraband" };

    private sealed record Finish(int Paint, string Name, int Rarity);

    private readonly InterfaceBridge      _bridge;
    private readonly ICustomHudManager    _customHud;
    private readonly ISkinApplier         _applier;
    private readonly ILogger<ArsenalMenu> _logger;

    private readonly List<string>                _weapons  = new();
    private readonly Dictionary<string, Finish[]> _finishes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One sticker on one of the weapon's five slots.</summary>
    private struct Applied
    {
        public int   Id;
        public float X;
        public float Y;
        public float Rotation;

        /// <summary>
        ///     Scratched-ness, 0 pristine to 1 worn. This slot used to hold Scale, but CS2 does
        ///     not honour `sticker slot N scale` - it stores the value and ignores it, exactly as
        ///     in-game sticker crafts let you rotate and reposition a sticker but never resize it.
        /// </summary>
        public float Wear;
    }

    private readonly float[] _yaw   = new float[MaxSlots];
    private readonly float[] _zoom  = new float[MaxSlots];
    private readonly float[] _panH  = new float[MaxSlots];   // across the view
    private readonly float[] _panV  = new float[MaxSlots];   // and up/down
    private readonly int[]  _wsel  = new int[MaxSlots];
    private readonly int[]  _fsel  = new int[MaxSlots];
    private readonly int[]  _wpage = new int[MaxSlots];
    private readonly int[]  _fpage = new int[MaxSlots];
    private readonly bool[] _open  = new bool[MaxSlots];

    // ---- sticker mode -------------------------------------------------------
    private readonly bool[]      _stickerMode = new bool[MaxSlots];

    /// <summary>
    ///     Glove mode. Mutually exclusive with sticker mode: both are "not the weapon list", and
    ///     letting them overlap would leave the dock showing sticker controls for a glove.
    /// </summary>
    private readonly bool[]      _gloveMode = new bool[MaxSlots];

    private readonly int[]       _gsel  = new int[MaxSlots];
    private readonly int[]       _gfsel = new int[MaxSlots];

    /// <summary>
    ///     Slot whose gloves were applied this tick and still need their refresh, and the frame
    ///     it becomes due. cs2-WeaponPaints refreshes gloves on a TIMER rather than inline, and
    ///     doing all of it in one tick is the one meaningful difference left between our attempt
    ///     and theirs.
    /// </summary>
    private readonly bool[]      _glovePending = new bool[MaxSlots];

    /// <summary>
    ///     Show the selected glove as a MODEL in the world, the way weapons are previewed, rather
    ///     than on the player's hands. ChangeSubclass takes an item definition index and a glove
    ///     has one, so a weapon entity - which carries an econ attribute container, unlike a
    ///     prop - can in principle be handed a glove definition and composite the finish onto the
    ///     glove's own model.
    /// </summary>
    /// <remarks>
    ///     OFF: tested 2026-08-29 and it does not work. The host entity keeps its own weapon
    ///     model - a knife handed def 5027 renders as a knife, not as Bloodhound Gloves - and
    ///     it crashed the server. ChangeSubclass sets which ITEM an entity is, it does not make
    ///     a weapon entity adopt a wearable's model.
    /// </remarks>
    private bool                 _glove3d;

    /// <summary>Slot to respawn on the next frame, set by EQUIP in glove mode.</summary>
    private readonly bool[]      _respawnPending = new bool[MaxSlots];

    private readonly float[]     _gloveDueAt = new float[MaxSlots];

    /// <summary>Glove key -> item definition index and display name, from armory_gloves.json.</summary>
    private readonly List<string>                 _gloves      = new();
    private readonly Dictionary<string, int>      _gloveDefs   = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>   _gloveNames  = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Finish[]> _gloveSkins  = new(StringComparer.Ordinal);
    private readonly int[]       _csel        = new int[MaxSlots];   // collection
    private readonly int[]       _cpage       = new int[MaxSlots];
    private readonly int[]       _ksel        = new int[MaxSlots];   // sticker within it
    private readonly int[]       _kpage       = new int[MaxSlots];
    private readonly int[]       _stkSlot     = new int[MaxSlots];   // which of the five
    private readonly Applied[,]  _applied     = new Applied[MaxSlots, StickerSlots];

    private readonly List<string>                 _collections = new();
    private readonly Dictionary<string, Finish[]> _stickers    = new(StringComparer.Ordinal);

    private readonly Dictionary<string, ushort?> _itemDefs = new(StringComparer.Ordinal);


    private ICustomHudLayout? _layout;
    private readonly IBaseWeapon?[] _item = new IBaseWeapon?[MaxSlots];
    private readonly IBaseEntity?[] _lamp = new IBaseEntity?[MaxSlots];
    private readonly List<IBaseEntity> _roomParts = new();

    /// <summary>Where the team-select prefab room sits on this map, found once per map load.</summary>
    private Vector?           _roomAnchor;
    /// <summary>
    ///     Move the browsing player into the team-select prefab room. It is enclosed, sits ~15000
    ///     units off the playable area on every CS2 map including workshop ones, and - unlike
    ///     anything we could build - is compiled WITH LIGHTING, which the preview needs. The old
    ///     objection to it (people watching you run there) does not apply: the pawn is teleported
    ///     and blocked from transmission, so nobody sees the trip or the destination.
    /// </summary>
    /// <summary>
    ///     OFF by default. The booth crashed the server repeatedly and the void does not light
    ///     the weapon anyway - it renders as a silhouette up there - so neither half of the
    ///     wardrobe is earning its risk yet. The code stays because the approach is sound once
    ///     lighting is solved; the switch stays off until it is, and until the crash is
    ///     reproduced somewhere that is not a server people are playing on.
    /// </summary>
    /// <summary>Teleport into empty space above the map. Cheap and, so far, not the crash.</summary>
    /// <summary>
    ///     OFF. The teleport kept landing outside the map, where dynamic lights are culled - and
    ///     the light is the thing that actually matters. Worse, it offset from the player's
    ///     CURRENT position, so repeated opens walked them further out each time.
    ///     <br /><br />
    ///     Lighting the weapon where the player already stands works, because that is inside the
    ///     map's lit volume. Backdrop can be solved separately; a dark weapon cannot.
    /// </summary>
    private bool              _useTeamSelectRoom;

    /// <summary>
    ///     The prop booth. Kept OFF: it is the prime suspect for the repeated crashes - five
    ///     props with transmit hooks, spawned and torn down together - and it has never been
    ///     ruled in or out. Teleport alone isolates that.
    /// </summary>
    private bool              _useBooth;
    private int               _lightKind;

    /// <summary>Where each player was standing before we moved them into the room.</summary>
    private readonly Vector?[] _homePos = new Vector?[MaxSlots];
    private readonly Vector?[] _homeAng = new Vector?[MaxSlots];

    /// <summary>The team each player was on before the browser put them in spectate.</summary>
    private readonly CStrikeTeam[] _homeTeam = new CStrikeTeam[MaxSlots];
    /// <summary>
    ///     Fixed, and modest on purpose. A live tuner that tore down and rebuilt these five
    ///     hooked props on demand crashed the server; adjustable scale is not worth that.
    /// </summary>
    private float             _roomScale = 26f;
    private float             _roomBack  = 520f;

    /// <summary>How far above the prefab room the booth is parked. Empty, but still in bounds.</summary>
    /// <summary>
    ///     Kept INSIDE the map's own volume. Dynamic lights are culled out in the skybox - a
    ///     light_omni2 that visibly lights a floor inside the map does nothing 2600 units up -
    ///     so the wardrobe has to stay where the renderer still processes lights.
    /// </summary>
    private const float       VoidLift   = 420f;
    private const float       VoidSide   = 900f;

    /// <summary>
    ///     Below the map instead of above it. Up gives sky ambient and fog - lit, but not a
    ///     controlled test. Down gives true blackness, which is the only way to tell whether the
    ///     particle is lighting the weapon or the sky is.
    /// </summary>
    private bool              _voidDown;
    private int               _smokeKind;
    private float             _exposure;
    private readonly IBaseEntity?[] _cam = new IBaseEntity?[MaxSlots];
    private readonly Vector?[] _eye = new Vector?[MaxSlots];
    private readonly IPlayerCache        _cache;
    private readonly InventoryRepository _repository;

    private readonly bool[]   _spawnFailed = new bool[MaxSlots];

    /// <summary>
    ///     Spawn the preview weapon directly instead of giving it to the player and dropping it.
    ///     <br /><br />
    ///     The step every earlier attempt missed is ChangeSubclass. A bare weapon_ak47 entity has
    ///     no item definition behind it, which is what "SpawnEntitySync produces a fundamentally
    ///     wrong object" in the docs really meant: rifles with no renderable world model and
    ///     knives that killed the client. Feeding it the econ definition index gives it an
    ///     identity, and it becomes a real weapon.
    ///     <br /><br />
    ///     OFF by default on purpose. A bad weapon entity takes the server down with it, and a
    ///     default-on crash reproduces itself on every restart. Enable with armory_arsenal_build.
    /// </summary>
    private bool              _buildOurselves = true;

    /// <summary>Whether the weapon standing there was spawned rather than given.</summary>
    private readonly bool[]   _builtOurselves = new bool[MaxSlots];
    private readonly bool[]   _traceLog = new bool[MaxSlots];
    private readonly float[]  _clearance = new float[MaxSlots];
    private readonly EntityIndex[] _dressedPawn = new EntityIndex[MaxSlots];
    private readonly uint[]   _ownerAccount = new uint[MaxSlots];
    private bool              _ownClass;
    private readonly float[]  _lastRebuild = new float[MaxSlots];
    /// <summary>
    ///     The preview light. Worth knowing: a long hunt for a "pulsing" light turned out to be
    ///     the MAP - cosmic_princess_kaguya animates its own lighting - not this particle. Bisect
    ///     against a static map before blaming anything here.
    /// </summary>
    private bool              _lightOn = true;
    private float             _lastCapture;

    /// <summary>Diagnostic override for the presentation distance; 0 = automatic.</summary>
    private float             _distOverride;


    /// <summary>Diagnostic toggle for the legacy-model bodygroup switch.</summary>
    private bool              _legacyBodygroup;
    private readonly float[]  _viewYaw = new float[MaxSlots];

    /// <summary>Weapons we alpha'd out on open, so close can put them back.</summary>
    private readonly List<IBaseModelEntity>[] _hiddenWeapons =
        Enumerable.Range(0, MaxSlots).Select(_ => new List<IBaseModelEntity>()).ToArray();
    private readonly string[] _spawned = Enumerable.Repeat("", MaxSlots).ToArray();

    public ArsenalMenu(InterfaceBridge      bridge,
                       ISharedSystem        sharedSystem,
                       ISkinApplier         applier,
                       IPlayerCache         cache,
                       InventoryRepository  repository,
                       ILogger<ArsenalMenu> logger)
    {
        _cache      = cache;
        _repository = repository;
        _bridge    = bridge;
        _customHud = sharedSystem.GetCustomHudManager();
        _applier   = applier;
        _logger    = logger;
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    public bool Init()
    {
        LoadCatalogue();
        LoadStickers();
        LoadGloves();

        _bridge.ConVarManager.CreateServerCommand("armory_arsenal", OnCommandOpen, "Open the arsenal browser");
        _bridge.ConVarManager.CreateServerCommand("armory_arsenal_close", OnCommandClose, "Close the arsenal browser");
        _bridge.ConVarManager.CreateServerCommand("armory_arsenal_dist", OnCommandDist,
                                                  "Force the preview distance (0 = automatic)");
        _bridge.ConVarManager.CreateServerCommand("armory_arsenal_legacy", OnCommandLegacy,
                                                  "Toggle the legacy-model bodygroup switch");
        _bridge.ConVarManager.CreateServerCommand("armory_arsenal_findroom", OnCommandFindRoom,
                                                  "Dump candidate anchors for the prefab room");
        _bridge.ConVarManager.CreateServerCommand("armory_arsenal_probe", OnCommandLightSchema,
                                                  "Report which light schema fields resolve");
        _bridge.ConVarManager.CreateServerCommand("armory_arsenal_exposure", OnCommandExposure,
                                                  "Cycle auto-exposure via env_tonemap_controller");
        _bridge.ConVarManager.CreateServerCommand("armory_arsenal_control", OnCommandSmoke,
                                                  "Dispatch a visible particle to prove dispatch works");
        _bridge.ConVarManager.CreateServerCommand("armory_arsenal_void", OnCommandVoid,
                                                  "Toggle the wardrobe between above and below the map");
        _bridge.ConVarManager.CreateServerCommand("armory_arsenal_lighttoggle", OnCommandLightToggle,
                                                  "Toggle the preview light");
        _bridge.ConVarManager.CreateServerCommand("armory_arsenal_fx", OnCommandFx,
                                                  "Light the preview with a light-emitting particle");
        _bridge.ConVarManager.CreateServerCommand("armory_arsenal_light", OnCommandLight,
                                                  "Spawn a test light (cycles classnames)");
        _bridge.ConVarManager.CreateServerCommand("armory_arsenal_build", OnCommandBuild,
                                                  "Toggle spawning the preview vs giving and dropping it");
        _bridge.ConVarManager.CreateServerCommand("armory_arsenal_knife", OnCommandProbe,
                                                  "Drop a bare knife at the preview spot");

        _bridge.HookManager.PlayerCanAcquire.InstallHookPre(OnCanAcquirePre);
        _customHud.InstallClickListener(OnClicked);
        _bridge.ModSharp.InstallGameListener(this);
        _bridge.ModSharp.InstallGameFrameHook(null, OnFramePost);

        return true;
    }

    public void Shutdown()
    {
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_close");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_dist");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_legacy");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_findroom");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_probe");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_knife");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_exposure");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_control");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_void");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_lighttoggle");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_build");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_fx");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_light");
        _bridge.ConVarManager.ReleaseCommand("armory_arsenal_probe");
        _bridge.HookManager.PlayerCanAcquire.RemoveHookPre(OnCanAcquirePre);
        _customHud.RemoveClickListener(OnClicked);
        _bridge.ModSharp.RemoveGameListener(this);
        _bridge.ModSharp.RemoveGameFrameHook(null, OnFramePost);
    }

    public void OnServerActivate()
    {
        _layout = null;

        // a map change invalidates every player's preview, not just one
        for (var i = 0; i < MaxSlots; i++)
        {
            _item[i]    = null;
            _lamp[i]    = null;
            _cam[i]     = null;
            _spawned[i] = "";
        }

        for (var i = 0; i < MaxSlots; i++)
        {
            _open[i] = false;
        }
    }

    /// <summary>
    ///     Rebuild the whole presentation after a round restart.
    ///     <br /><br />
    ///     Warmup ending left the menu fully drawn but dead to clicks. Watching the pawn was not
    ///     enough - a restart can leave its index and HUD state looking untouched while the input
    ///     capture is silently dropped - so the engine's own round signal is what re-arms it. The
    ///     capture is cycled off and on, because re-asserting one the layout still believes it
    ///     holds does nothing.
    /// </summary>
    public void OnRoundRestarted()
    {
        for (var s = 0; s < MaxSlots; s++)
        {
            if (!_open[s])
            {
                continue;
            }

            var slot = new PlayerSlot((byte) s);

            _dressedPawn[s] = default;   // force the pawn to be re-dressed

            Layout()?.SetInputCaptureEnabled(slot, false);
            Layout()?.SetInputCaptureEnabled(slot, true);

            Reassert(slot);
            Refresh(slot);

            _logger.LogInformation("re-armed the arsenal for slot {slot} after a round restart", s);
        }
    }

    /// <summary>
    ///     Precache the layout, and NOTHING else.
    ///     <br /><br />
    ///     This used to precache all 55 weapon world models so they could be forced onto the
    ///     preview entity. That model override is gone - it crashed clients - so the precaching
    ///     serves no purpose, and pushing 55 extra model entries at a connecting client is itself
    ///     a plausible way to kill it at load. Weapons carry their own models; leave them alone.
    /// </summary>
    public void OnResourcePrecache()
    {
        try
        {
            _bridge.ModSharp.PrecacheResource(LayoutResource);

            // Precache EVERY particle here, at map load. Precaching on demand is too late -
            // the client never receives the resource and the effect silently renders nothing,
            // which is exactly how the weapon models failed earlier today. The control effect
            // is precached too so the "does dispatch work at all" test is actually valid.
            foreach (var fx in new[] { LightFx, ControlFx, "particles/ui/ui_item_present_lighting.vpcf" })
            {
                try
                {
                    _bridge.ModSharp.PrecacheResource(fx);
                    _logger.LogInformation("precached {fx}", fx);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("could not precache {fx}: {msg}", fx, ex.Message);
                }
            }
            _bridge.ModSharp.PrecacheResource(RoomModel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to precache {resource}: {msg}", LayoutResource, ex.Message);
        }
    }

    // ------------------------------------------------------------------ catalogue

    private void LoadCatalogue()
    {
        var path = Path.Combine(_bridge.SharpPath, "configs", "armory_arsenal.json");

        if (!File.Exists(path))
        {
            _logger.LogError("Arsenal catalogue missing at {path} - the browser will be empty", path);

            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            foreach (var weapon in doc.RootElement.EnumerateObject())
            {
                var list = new List<Finish>();

                foreach (var s in weapon.Value.EnumerateArray())
                {
                    var rarity = Array.IndexOf(Rarities, s.GetProperty("r").GetString() ?? "consumer");
                    list.Add(new Finish(s.GetProperty("i").GetInt32(),
                                        s.GetProperty("n").GetString() ?? "?",
                                        rarity < 0 ? 0 : rarity));
                }

                _weapons.Add(weapon.Name);
                _finishes[weapon.Name] = list.ToArray();
            }

            _weapons.Sort(StringComparer.Ordinal);
            _logger.LogInformation("Arsenal: {weapons} weapons, {skins} finishes",
                                   _weapons.Count, _finishes.Values.Sum(v => v.Length));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the arsenal catalogue");
        }
    }

    /// <summary>
    ///     All 20 knives share ONE spawnable entity class - `weapon_bayonet` and friends are not
    ///     entities at all, which is why they came back "Could not spawn". The specific knife lives
    ///     in the item definition index on the econ view, so we spawn the generic knife and stamp
    ///     the identity on afterwards.
    /// </summary>
    private static bool IsKnife(string weapon)
        => weapon.StartsWith("weapon_knife", StringComparison.Ordinal) || weapon == "weapon_bayonet";

    private static string SpawnClass(string weapon)
        => IsKnife(weapon) ? "weapon_knife" : weapon;

    /// <summary>Classname to item definition index. Cached; the mapping never changes at runtime.</summary>
    private ushort? ResolveItemDef(string weapon)
    {
        if (_itemDefs.TryGetValue(weapon, out var cached))
        {
            return cached;
        }

        var resolved = _bridge.EconItemManager.GetEconItemDefinitionByName(weapon)?.Index;
        _itemDefs[weapon] = resolved;

        return resolved;
    }

    /// <summary>
    ///     Gloves are not in the weapon catalogue and cannot be: they are not in the loot-list
    ///     format weapons use, so the weapon builder never sees them. tools/build_gloves.py maps
    ///     them by paint-kit name prefix instead, and every glove has TWO generations of prefix.
    /// </summary>
    private void LoadGloves()
    {
        var path = Path.Combine(_bridge.SharpPath, "configs", "armory_gloves.json");

        if (!File.Exists(path))
        {
            _logger.LogWarning("Glove catalogue missing at {path} - glove mode will be empty", path);

            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            foreach (var glove in doc.RootElement.EnumerateObject())
            {
                var list = new List<Finish>();

                foreach (var f in glove.Value.GetProperty("finishes").EnumerateArray())
                {
                    list.Add(new Finish(f.GetProperty("i").GetInt32(),
                                        f.GetProperty("n").GetString() ?? "?",
                                        0));
                }

                _gloves.Add(glove.Name);
                _gloveDefs[glove.Name]  = glove.Value.GetProperty("def").GetInt32();
                _gloveNames[glove.Name] = glove.Value.GetProperty("name").GetString() ?? glove.Name;
                _gloveSkins[glove.Name] = list.ToArray();
            }

            _gloves.Sort((a, b) => string.CompareOrdinal(_gloveNames[a], _gloveNames[b]));
            _logger.LogInformation("Gloves: {gloves} gloves, {skins} finishes",
                                   _gloves.Count, _gloveSkins.Values.Sum(v => v.Length));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the glove catalogue");
        }
    }

    private Finish[] GloveSkinsFor(int s)
        => _gloves.Count > 0 && _gloveSkins.TryGetValue(_gloves[_gsel[s]], out var f)
            ? f
            : Array.Empty<Finish>();

    private void LoadStickers()
    {
        var path = Path.Combine(_bridge.SharpPath, "configs", "armory_stickers.json");

        if (!File.Exists(path))
        {
            _logger.LogWarning("Sticker catalogue missing at {path} - sticker mode will be empty", path);

            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            foreach (var collection in doc.RootElement.EnumerateObject())
            {
                var list = new List<Finish>();

                foreach (var k in collection.Value.EnumerateArray())
                {
                    var rarity = Array.IndexOf(Rarities, k.GetProperty("r").GetString() ?? "consumer");
                    list.Add(new Finish(k.GetProperty("i").GetInt32(),
                                        k.GetProperty("n").GetString() ?? "?",
                                        rarity < 0 ? 0 : rarity));
                }

                _collections.Add(collection.Name);
                _stickers[collection.Name] = list.ToArray();
            }

            _collections.Sort(StringComparer.Ordinal);
            _logger.LogInformation("Stickers: {collections} collections, {count} stickers",
                                   _collections.Count, _stickers.Values.Sum(v => v.Length));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the sticker catalogue");
        }
    }

    private Finish[] StickersFor(int slot)
        => _collections.Count > 0 && _stickers.TryGetValue(_collections[_csel[slot]], out var k)
            ? k
            : Array.Empty<Finish>();

    /// <summary>Right-hand meta column: what the row is, in one token.</summary>
    private static string Calibre(string weapon)
        => weapon switch
        {
            "weapon_ak47" or "weapon_galilar" => "7.62",
            "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_aug" or "weapon_sg556"
                or "weapon_famas" => "5.56",
            "weapon_awp" or "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1" => "SNIPER",
            "weapon_deagle" or "weapon_revolver" => ".50",
            "weapon_nova" or "weapon_xm1014" or "weapon_sawedoff" or "weapon_mag7" => "12GA",
            "weapon_m249" or "weapon_negev" => "LMG",
            _ => IsKnife(weapon) ? "MELEE" : "9x19",
        };

    private static string Grade(int rarity)
        => rarity switch
        {
            0 => "CON", 1 => "IND", 2 => "MIL", 3 => "RES",
            4 => "CLA", 5 => "CVT", 6 => "CTB", _ => "",
        };

    private static string Pretty(string weapon)
        => weapon.StartsWith("weapon_", StringComparison.Ordinal)
            ? weapon[7..].ToUpperInvariant()
            : weapon.ToUpperInvariant();

    // ------------------------------------------------------------------ helpers

    private ICustomHudLayout? Layout()
    {
        if (_layout is not null)
        {
            return _layout;
        }

        _layout = _customHud.CreateLayout(LayoutResource, LayoutName);

        if (_layout is null)
        {
            _logger.LogError("CreateLayout failed for {resource}", LayoutResource);
        }

        return _layout;
    }

    private void Cls(PlayerSlot slot, string panel, string name, bool on)
        => Layout()?.SetClassOverrideForPlayer(slot, panel, name,
                                               on ? CustomHudClassOverride.Present : CustomHudClassOverride.Absent);

    private void Txt(PlayerSlot slot, string panel, string value)
        => Layout()?.SetDialogVariableStringForPlayer(slot, panel, "v", value);

    private static string Two(int n)
        => n < 10 ? "0" + n : n.ToString();

    private Finish[] FinishesFor(int slot)
        => _finishes.TryGetValue(_weapons[_wsel[slot]], out var f) ? f : Array.Empty<Finish>();

    // ------------------------------------------------------------------ render

    private void Refresh(PlayerSlot slot)
    {
        var s = slot.AsPrimitive();

        Cls(slot, "root", "Stickers", _stickerMode[s]);
        Cls(slot, "stk_row", "Hide", !_stickerMode[s]);
        Cls(slot, "btn_mode", "On", _stickerMode[s]);
        Txt(slot, "rot_v", ((int) _yaw[s]).ToString());
        Txt(slot, "zoom_v", Zoomed(s).ToString("0.00"));
        Txt(slot, "panx_v", _panH[s].ToString("0"));
        Txt(slot, "pany_v", _panV[s].ToString("0"));
        Cls(slot, "btn_mode", "On", _stickerMode[s]);

        Cls(slot, "btn_gloves", "On", _gloveMode[s]);
        Cls(slot, "stk_row", "Hide", !_stickerMode[s]);

        if (_gloveMode[s])
        {
            try
            {
                RefreshGloves(slot, s);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("glove list failed to draw: {msg}", ex.Message);
            }

            ShowGloves(slot);

            return;
        }

        if (_stickerMode[s])
        {
            RefreshStickers(slot, s);
        }
        else
        {
            RefreshFinishes(slot, s);
        }

        ShowItem(slot);
    }

    private void RefreshFinishes(PlayerSlot slot, int s)
    {
        Txt(slot, "title", "FINISH");
        Txt(slot, "sec_l", "01  WEAPON");
        Txt(slot, "sec_r", "02  FINISH");
        Txt(slot, "sec_l_c", _weapons.Count.ToString());

        // No page offset: 55 weapons fit inside the 72 declared rows and the client scrolls.
        Rows_(slot, "wp", _weapons.Count, 0, _wsel[s],
              i => Pretty(_weapons[i]), i => Calibre(_weapons[i]), _ => -1);

        var list = FinishesFor(s);
        Txt(slot, "sec_r_c", list.Length.ToString());

        // Likewise: the worst weapon has 60 finishes.
        Rows_(slot, "fn", list.Length, 0, _fsel[s],
              i => list[i].Name, i => Grade(list[i].Rarity), i => list[i].Rarity);

        Cls(slot, "fn_pager", "Hide", true);   // pager belongs to the sticker list only
        Txt(slot, "idx", Two(_fsel[s] + 1));

        if (list.Length > 0 && _fsel[s] < list.Length)
        {
            Readout(slot, list[_fsel[s]].Name, list[_fsel[s]].Rarity);
        }
    }

    private void RefreshGloves(PlayerSlot slot, int s)
    {
        Txt(slot, "title", "GLOVES");
        Txt(slot, "sec_l", "01  GLOVE");
        Txt(slot, "sec_r", "02  FINISH");
        Txt(slot, "sec_l_c", _gloves.Count.ToString());

        Rows_(slot, "wp", _gloves.Count, 0, _gsel[s],
              i => _gloveNames[_gloves[i]].ToUpperInvariant(),
              i => _gloveSkins[_gloves[i]].Length.ToString(),
              _ => -1);

        var list = GloveSkinsFor(s);
        Txt(slot, "sec_r_c", list.Length.ToString());

        Rows_(slot, "fn", list.Length, 0, _gfsel[s],
              i => list[i].Name, _ => string.Empty, _ => -1);

        Cls(slot, "fn_pager", "Hide", true);   // 20 finishes at most, the column scrolls
        Txt(slot, "idx", Two(_gfsel[s] + 1));

        if (list.Length > 0 && _gfsel[s] < list.Length)
        {
            Readout(slot, list[_gfsel[s]].Name, -1);
        }
    }

    private void RefreshStickers(PlayerSlot slot, int s)
    {
        Txt(slot, "title", "STICKER");
        Txt(slot, "sec_l", "01  COLLECTION");
        Txt(slot, "sec_r", "02  STICKER");
        Txt(slot, "sec_l_c", _collections.Count.ToString());

        // 65 collections fit; scroll, no pager.
        Rows_(slot, "wp", _collections.Count, 0, _csel[s],
              i => _collections[i].ToUpperInvariant(), i => _stickers[_collections[i]].Length.ToString(),
              _ => -1);

        var list = StickersFor(s);
        Txt(slot, "sec_r_c", list.Length.ToString());

        // The ONE list that can overflow 72 rows - the biggest capsule is 1404 - so this
        // alone keeps a pager, and it is hidden whenever the collection fits.
        var pages = Math.Max(1, (list.Length + Rows - 1) / Rows);

        Rows_(slot, "fn", list.Length, _kpage[s], _ksel[s],
              i => list[i].Name, i => Grade(list[i].Rarity), i => list[i].Rarity);

        Cls(slot, "fn_pager", "Hide", pages <= 1);
        Txt(slot, "fn_page", (_kpage[s] + 1) + " / " + pages);

        var applied = _applied[s, _stkSlot[s]];

        Txt(slot, "idx", Two(_stkSlot[s] + 1));

        for (var i = 0; i < StickerSlots; i++)
        {
            Cls(slot, "stkslot" + i, "On", i == _stkSlot[s]);
            Cls(slot, "stkslot" + i, "Has", _applied[s, i].Id > 0);
        }

        Txt(slot, "stk_x_v", applied.X.ToString("0.00"));
        Txt(slot, "stk_y_v", applied.Y.ToString("0.00"));
        Txt(slot, "stk_r_v", ((int) applied.Rotation).ToString());
        Txt(slot, "stk_s_v", applied.Wear.ToString("0.00"));

        if (list.Length > 0 && _ksel[s] < list.Length)
        {
            Readout(slot, list[_ksel[s]].Name, list[_ksel[s]].Rarity);
        }
    }

    /// <summary>Paint one column. Both modes share the same two columns.</summary>
    private void Rows_(PlayerSlot slot, string prefix, int count, int page, int selected,
                       Func<int, string> label, Func<int, string> meta, Func<int, int> rarity)
    {
        var start = page * Rows;

        for (var i = 0; i < Rows; i++)
        {
            var idx = start + i;
            var has = idx < count;

            Cls(slot, prefix + i, "Hide", !has);
            Cls(slot, prefix + i, "On", has && idx == selected);
            Txt(slot, prefix + i + "_l", has ? label(idx) : "");
            Txt(slot, prefix + i + "_n", has ? Two(idx + 1) : "");
            Txt(slot, prefix + i + "_m", has ? meta(idx) : "");

            var r = has ? rarity(idx) : -1;

            for (var k = 0; k < Rarities.Length; k++)
            {
                Cls(slot, prefix + i, "R" + k, r == k);
            }
        }
    }

    private void Readout(PlayerSlot slot, string name, int rarity)
    {
        Txt(slot, "skinname", name);

        // Gloves have no grade in the schema, so they pass -1. This used to index Rarities
        // directly and threw, which killed RefreshGloves on its LAST line - the list had already
        // been drawn, so the menu looked fine and the preview silently never ran.
        Txt(slot, "skinrarity",
            rarity >= 0 && rarity < Rarities.Length ? Rarities[rarity].ToUpperInvariant() : "");

        for (var r = 0; r < Rarities.Length; r++)
        {
            Cls(slot, "skinrarity", "R" + r, rarity == r);
        }
    }

    // ------------------------------------------------------------------ the item itself

    /// <summary>
    ///     Spawn the actual weapon and paint it. SpawnEntitySync is explicitly safe for weapons
    ///     (it runs the full precache pipeline), which the fast create-then-dispatch path is not.
    /// </summary>
    private void ShowItem(PlayerSlot slot)
    {
        var s      = slot.AsPrimitive();
        var wanted = _weapons[_wsel[s]];
        var list   = FinishesFor(s);
        var paint  = list.Length > 0 && _fsel[s] < list.Length ? list[_fsel[s]].Paint : 0;
        var key    = wanted + "|" + paint + "|" + StickerKey(s);

        // The paint MUST be on the item view before the entity networks to the client - the
        // loadout system applies skins in the give-item hook for exactly this reason. Writing
        // attributes onto an already-networked weapon changes nothing on screen, which is why
        // picking a finish appeared to do nothing. So a finish change respawns the item too.
        // UpdateEconItemAttributes does not get round this; see docs/ARSENAL.md.
        if (_item[s] is not null && _item[s].IsValid() && _spawned[s] == key)
        {
            PlaceItem(s);

            return;
        }

        DropItem(s);

        // The non-generic SpawnEntitySync hands back a plain BaseEntity wrapper, so every
        // `is IBaseWeapon` test failed and the paint silently never ran. The typed overload is
        // what gives access to the attribute container.
        // Weapons must enter the world the way the GAME makes them. A bare weapon entity built
        // with SpawnEntitySync is not the same object: rifles came out with no renderable world
        // model, and an untouched knife - no attributes, no paint, nothing applied - crashed the
        // client outright the moment it was asked to draw. Give it and drop it instead; that runs
        // the engine's own construction path, the one every dropped gun on the ground uses.
        if (Pawn(slot) is not { } pawn)
        {
            _spawnFailed[s] = true;

            return;
        }

        IBaseWeapon? given = null;

        _builtOurselves[s] = false;
        SetPickup(pawn, false);

        // Spawn it ourselves, the way SpawnTools does: spawn at the origin we want, then hand it
        // its econ definition index through ChangeSubclass so it becomes a real weapon rather
        // than an empty shell. Falls through to the give if anything about it looks wrong.
        if (_buildOurselves && ResolveItemDef(wanted) is { } def)
        {
            try
            {
                // spawn it somewhere legal in the world; PlaceItem moves it into shot after
                var at = pawn.GetAbsOrigin();

                // SpawnClass, not `wanted`: weapon_knife_karambit and friends are item definition
                // names, not entities. Every knife spawns as weapon_knife and gets its identity
                // from ChangeSubclass below - which is precisely what that input is for.
                var built = _bridge.EntityManager.SpawnEntitySync<IBaseWeapon>(
                    SpawnClass(wanted),
                    new Dictionary<string, KeyValuesVariantValueItem>
                    {
                        ["origin"] = $"{at.X} {at.Y} {at.Z + 16f}",
                    });

                if (built is not null && built.IsValid())
                {
                    built.AcceptInput("ChangeSubclass", null, null, def.ToString());

                    if (built.IsValid())
                    {
                        given           = built;
                        _item[s]           = built;
                        _builtOurselves[s] = true;
                        Paint(slot);

                        _logger.LogInformation("spawned {weapon} as {cls} subclass {def}, no give",
                                               wanted, SpawnClass(wanted), def);
                    }
                    else
                    {
                        _logger.LogWarning("{weapon} died on ChangeSubclass {def}", wanted, def);
                        _item[s] = null;
                    }
                }
                else
                {
                    _logger.LogWarning("SpawnEntitySync({weapon}) came back unusable", wanted);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("spawning {weapon} threw: {msg}", wanted, ex.Message);
                given = null;
                _item[s] = null;
            }
        }

        try
        {
            given ??= pawn.GiveNamedItem(wanted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("GiveNamedItem({weapon}) threw: {msg}", wanted, ex.Message);
            _spawnFailed[s] = true;

            return;
        }

        if (given is null || !given.IsValid())
        {
            _logger.LogWarning("Could not give {weapon}", wanted);
            _spawnFailed[s] = true;

            return;
        }

        _item[s] = given;

        // dress it while it is still ours, before it becomes a world object
        Paint(slot);

        // Getting it OUT of the player's hands is as important as getting it in them. A give puts
        // the weapon in the inventory, and PreventWeaponPickup does not apply to a direct give -
        // so if it stays owned, the player is carrying a pinned, non-solid entity, and switching
        // to it crashes the client.
        try
        {
            if (!_builtOurselves[s])
            {
                pawn.DropWeapon(given);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("DropWeapon threw: {msg}", ex.Message);
        }

        // NOT RemovePlayerItem or SetOwner(null) here: both invalidate the entity the drop just
        // built, the tick then sees the preview missing and rebuilds it, and that runs away into a
        // give/drop every frame - an endless stream of equip sounds and nothing on screen.
        Pin(given);
        ShowOnlyTo(given, slot);

        if (_useBooth && _roomParts.Count == 0)
        {
            PlaceRoom(slot);
        }

        _logger.LogInformation("{weapon}: owner={owner} after drop",
                               wanted, given.OwnerEntity is null ? "none" : "STILL OWNED");

        // the give switched the active weapon, so whatever is in their hands now needs re-hiding
        HideHeldWeapons(pawn, s);

        _spawnFailed[s] = false;
        _spawned[s]     = key;
        _traceLog[s]    = true;

        Pin(_item[s]);
        PlaceItem(s);
        LightItem(slot);   // after PlaceItem, or the lamp lands where the weapon was dropped
    }

    /// <summary>
    ///     Refuse every pickup while the browser is open.
    ///     <br /><br />
    ///     The preview is a genuinely dropped weapon lying in the world, and the player walked
    ///     straight into it: the AK appeared and was instantly equipped, leaving the menu empty.
    ///     PreventWeaponPickup does not hold - this hook is the reliable way to say no.
    /// </summary>
    private HookReturnValue<EAcquireResult> OnCanAcquirePre(IPlayerCanAcquireHookParams @params,
                                                            HookReturnValue<EAcquireResult>  ret)
    {
        if (@params.Method != EAcquireMethod.PickUp)
        {
            return new();
        }

        var slot = @params.Client.Slot.AsPrimitive();

        if (slot >= MaxSlots || !_open[slot])
        {
            return new();
        }

        return new(EHookAction.SkipCallReturnOverride, EAcquireResult.NotAllowedByProhibition);
    }

    /// <summary>Everything about the stickers that would change what the weapon looks like.</summary>
    private string StickerKey(int s)
    {
        var key = new System.Text.StringBuilder();

        for (var i = 0; i < StickerSlots; i++)
        {
            var a = _applied[s, i];
            key.Append(a.Id).Append(',').Append(a.X).Append(',').Append(a.Y)
               .Append(',').Append(a.Rotation).Append(',').Append(a.Wear).Append(';');
        }

        return key.ToString();
    }

    private IPlayerPawn? Pawn(PlayerSlot slot)
    {
        foreach (var controller in _bridge.EntityManager.GetPlayerControllers())
        {
            if (controller is null || !controller.IsValid() || new PlayerSlot(controller) != slot)
            {
                continue;
            }

            var pawn = controller.GetPlayerPawn();

            return pawn is not null && pawn.IsValid() ? pawn : null;
        }

        return null;
    }

    /// <summary>
    ///     Build a private booth around the presentation.
    ///     <br /><br />
    ///     This is the wardrobe, and it works because of two things that were solved separately:
    ///     the panels are ordinary props standing INSIDE the map, so the map's own baked lighting
    ///     falls on them and on the weapon - no runtime light needed, which is just as well since
    ///     none of them render - and every panel is scoped per-receiver, so nobody but the
    ///     browsing player ever sees a room appear in the middle of the level.
    ///     <br /><br />
    ///     No teleport, no prefab, no addon, and it works on any map.
    /// </summary>
    private void PlaceRoom(PlayerSlot owner)
    {
        var s = owner.AsPrimitive();

        if (_eye[s] is null)
        {
            return;
        }

        DropRoom();

        var yr = _viewYaw[s] * MathF.PI / 180f;

        // forward and right in view space, so the booth is built around the sight line
        var fx = MathF.Cos(yr);
        var fy = MathF.Sin(yr);
        var rx = MathF.Sin(yr);
        var ry = -MathF.Cos(yr);

        var mid = new Vector(_eye[s].Value.X + (fx * (_roomBack * 0.5f)),
                             _eye[s].Value.Y + (fy * (_roomBack * 0.5f)),
                             _eye[s].Value.Z);

        // back wall, both sides, floor and ceiling - the front is left open for the camera
        var half = _roomBack * 0.5f;
        var faces = new (float F, float R, float U)[]
        {
            (half, 0f, 0f),      // back
            (0f, half, 0f),      // right
            (0f, -half, 0f),     // left
            (0f, 0f, -half),     // floor
            (0f, 0f, half),      // ceiling
        };

        foreach (var (f, r, u) in faces)
        {
            var panel = _bridge.EntityManager.SpawnEntitySync("prop_dynamic",
                                                              new Dictionary<string, KeyValuesVariantValueItem>
                                                              {
                                                                  ["targetname"]     = RoomName,
                                                                  ["model"]          = RoomModel,
                                                                  ["solid"]          = "0",
                                                                  ["disableshadows"] = "1",
                                                              });

            if (panel is null)
            {
                continue;
            }

            try
            {
                panel.SetMoveType(MoveType.None);
                panel.SetSolid(SolidType.None);

                if (panel is IBaseModelEntity m)
                {
                    m.SetModelScale(_roomScale);
                }

                panel.Teleport(new Vector(mid.X + (fx * f) + (rx * r),
                                          mid.Y + (fy * f) + (ry * r),
                                          mid.Z + u),
                               new Vector(0f, _viewYaw[s], 0f),
                               new Vector(0f, 0f, 0f));
            }
            catch
            {
                // a panel we cannot place is simply left out
            }

            ShowOnlyTo(panel, owner);
            _roomParts.Add(panel);
        }

        _logger.LogInformation("wardrobe: {n} panels, scale {s}, half-size {h}",
                               _roomParts.Count, _roomScale, (int) half);
    }

    private void DropRoom()
    {
        foreach (var part in _roomParts)
        {
            ShowToEveryone(part);

            try
            {
                if (part.IsValid())
                {
                    part.Kill();
                }
            }
            catch
            {
                // already gone
            }
        }

        _roomParts.Clear();
    }

    /// <summary>
    ///     Find the team-select prefab room.
    ///     <br /><br />
    ///     `prefabs/misc/team_select` loads as a point_prefab spawn group on EVERY CS2 map,
    ///     workshop maps included, and it is compiled WITH ITS OWN BAKED LIGHTING. That matters:
    ///     CS2 has no spawnable light entities, so any room built from props at runtime is pitch
    ///     black and the weapon in it renders as a silhouette. This room is already lit, already
    ///     enclosed, and already ~15000 units off the playable area on every map.
    /// </summary>
    private Vector? FindRoom()
    {
        if (_roomAnchor is not null)
        {
            return _roomAnchor;
        }

        // the room's own spawn points are the most reliable marker of where it was placed
        var sum = new Vector(0f, 0f, 0f);
        var n = 0;

        foreach (var cls in new[] { "info_player_counterterrorist", "info_player_terrorist" })
        {
            foreach (var e in _bridge.EntityManager.GetAllEntitiesByClassname(cls))
            {
                if (e is null || !e.IsValid())
                {
                    continue;
                }

                var p = e.GetAbsOrigin();

                // the prefab sits far outside the map; playable spawns are near the origin
                if (MathF.Abs(p.X) < 6000f && MathF.Abs(p.Y) < 6000f)
                {
                    continue;
                }

                sum = new Vector(sum.X + p.X, sum.Y + p.Y, sum.Z + p.Z);
                n++;
            }
        }

        if (n == 0)
        {
            _logger.LogWarning("No team-select room found on this map - staying put");

            return null;
        }

        _roomAnchor = new Vector(sum.X / n, sum.Y / n, sum.Z / n);
        _logger.LogInformation("wardrobe: team-select room at {x},{y},{z} from {n} spawns",
                               (int) _roomAnchor.Value.X, (int) _roomAnchor.Value.Y,
                               (int) _roomAnchor.Value.Z, n);

        return _roomAnchor;
    }

    /// <summary>
    ///     Move the player into the room and stop anyone else seeing them.
    ///     <br /><br />
    ///     Blocking transmission is what makes the shared room workable: several people can be in
    ///     there at once, and anyone genuinely picking a team is not shown a row of players
    ///     fiddling with skins.
    /// </summary>
    private void EnterRoom(PlayerSlot slot, IPlayerPawn pawn)
    {
        var s = slot.AsPrimitive();

        if (_homePos[s] is not null)
        {
            return;   // already in the wardrobe - do not offset from the offset
        }

        _homePos[s] = pawn.GetAbsOrigin();
        _homeAng[s] = pawn.GetEyeAngles();

        // Straight UP from wherever the player is standing. Locating the team-select prefab
        // and offsetting from it only worked where that prefab sat far outside the map -
        // de_ancient_night keeps it close in, so the search found nothing and nobody moved.
        // Up is empty on every map, still inside the vertical bounds, and needs no landmark.
        // Up AND well to the side, so the wardrobe sits outside the playable area rather than
        // directly over it. Kaguya's team-select prefab is ~15000 units out, which is proof the
        // world extends far enough for this to stay in bounds.
        var home = _homePos[s]!.Value;
        var spot = new Vector(home.X + VoidSide + (s % 6 * 200f),
                              home.Y + VoidSide + (s / 6 * 200f),
                              home.Z + (_voidDown ? -VoidLift : VoidLift));

        try
        {
            pawn.Teleport(spot, new Vector(0f, 0f, 0f), new Vector(0f, 0f, 0f));

            // nothing to stand on out there, so movement is off entirely - no falling, and
            // no fall damage on the way to a floor that does not exist
            pawn.SetMoveType(MoveType.None);
            pawn.SetGravityScale(0f);
            _bridge.TransmitManager.SetEntityBlock(pawn.Index, true);

            _logger.LogInformation("wardrobe: slot {s} moved to {x},{y},{z}",
                                   s, (int) spot.X, (int) spot.Y, (int) spot.Z);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not enter the wardrobe: {msg}", ex.Message);
        }
    }

    private void LeaveRoom(PlayerSlot slot, IPlayerPawn pawn)
    {
        var s = slot.AsPrimitive();

        try
        {
            _bridge.TransmitManager.SetEntityBlock(pawn.Index, false);
            pawn.SetGravityScale(1f);
            pawn.SetMoveType(MoveType.Walk);

            if (_homePos[s] is { } home)
            {
                pawn.Teleport(home, _homeAng[s], new Vector(0f, 0f, 0f));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not leave the wardrobe: {msg}", ex.Message);
        }

        _homePos[s] = null;
        _homeAng[s] = null;
    }

    /// <summary>
    ///     Put the player into spectate for as long as the browser is open.
    ///     <br /><br />
    ///     This is the piece that makes a shared wardrobe workable at all. A spectator has no body
    ///     in the world, so nobody watches them walk anywhere, nothing blocks a doorway, and they
    ///     are correctly not participating in the round while they pick skins. It also means the
    ///     presentation can sit in the team-select prefab room - the one enclosed, already-lit
    ///     space that exists on every CS2 map - without anyone in the map seeing it.
    ///     <br /><br />
    ///     Leaving the round mid-play is a real consequence, not a side effect: changing skins is
    ///     not something to be doing while alive in a live round.
    /// </summary>
    private void EnterSpectate(PlayerSlot slot, IPlayerController controller)
    {
        var s = slot.AsPrimitive();
        _homeTeam[s] = controller.Team;

        if (controller.Team == CStrikeTeam.Spectator)
        {
            return;   // already there; nothing to restore later
        }

        try
        {
            controller.SwitchTeam(CStrikeTeam.Spectator);
            _logger.LogInformation("arsenal: slot {slot} moved to spectate from {team}",
                                   s, _homeTeam[s]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not move to spectate: {msg}", ex.Message);
            _homeTeam[s] = CStrikeTeam.Spectator;
        }
    }

    private void LeaveSpectate(PlayerSlot slot, IPlayerController controller)
    {
        var s = slot.AsPrimitive();

        if (_homeTeam[s] is CStrikeTeam.Spectator or CStrikeTeam.UnAssigned)
        {
            return;
        }

        try
        {
            controller.SwitchTeam(_homeTeam[s]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not restore the team: {msg}", ex.Message);
        }

        _homeTeam[s] = CStrikeTeam.Spectator;
    }

    /// <summary>
    ///     Show an entity to ONE player and nobody else.
    ///     <br /><br />
    ///     Everything the browser spawns - the preview weapon above all - is a normal world entity
    ///     and is transmitted to every client by default, so without this the rest of the server
    ///     watches a rifle hanging in mid-air while somebody browses skins. Blocking the player's
    ///     pawn is not enough on its own: the pawn and the preview are separate entities.
    ///     <br /><br />
    ///     AddEntityHooks arms per-entity transmit control; SetEntityState then decides it per
    ///     receiver.
    /// </summary>
    private void ShowOnlyTo(IBaseEntity? entity, PlayerSlot owner)
    {
        if (entity is null || !entity.IsValid())
        {
            return;
        }

        try
        {
            _bridge.TransmitManager.AddEntityHooks(entity, true);

            foreach (var c in _bridge.EntityManager.GetPlayerControllers())
            {
                if (c is null || !c.IsValid())
                {
                    continue;
                }

                var slot = new PlayerSlot(c);

                if (slot == owner || c.GetPlayerPawn() is not { } pawn || !pawn.IsValid())
                {
                    continue;
                }

                _bridge.TransmitManager.SetEntityState(entity.Index, pawn.Index, false, 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not scope {e} to its owner: {msg}",
                               entity.GetType().Name, ex.Message);
        }
    }

    private void ShowToEveryone(IBaseEntity? entity)
    {
        if (entity is null || !entity.IsValid())
        {
            return;
        }

        try
        {
            // order matters, and only ever on an entity that is still alive and hooked
            if (_bridge.TransmitManager.IsEntityHooked(entity))
            {
                _bridge.TransmitManager.ClearReceiverState(entity.Index);
                _bridge.TransmitManager.RemoveEntityHooks(entity);
            }
        }
        catch
        {
            // the entity is going away anyway
        }
    }

    /// <summary>
    ///     Save the finish the player is looking at, so their REAL weapon wears it.
    ///     <br /><br />
    ///     The browser never touches the player's weapon itself. The loadout system already
    ///     applies skins in the GiveNamedItem post hook from the player cache, so equipping is
    ///     just writing the row and refreshing the cache; the skin lands on their next spawn or
    ///     buy. Applying it to a weapon already in their hands would mean re-giving it, which is
    ///     the wall documented in docs/ARSENAL.md.
    /// </summary>
    private void Equip(PlayerSlot slot, IPlayerController player)
    {
        var s = slot.AsPrimitive();

        if (_gloveMode[s])
        {
            EquipGloves(slot, player, s);

            return;
        }

        var wanted = _weapons[_wsel[s]];
        var list   = FinishesFor(s);

        if (list.Length == 0 || _fsel[s] >= list.Length || ResolveItemDef(wanted) is not { } def)
        {
            _logger.LogWarning("equip: nothing selected for {weapon}", wanted);

            return;
        }

        var pick     = list[_fsel[s]];
        var isKnife  = IsKnife(wanted);
        var stickers = StickerJson(s);

        if (player.GetGameClient() is not { IsFakeClient: false } client)
        {
            return;
        }

        ulong steamId = client.SteamId;

        // fire and forget: the browser must not block a frame on the database
        _ = Task.Run(async () =>
        {
            try
            {
                await _repository.UpsertWeaponSkin(steamId, def, pick.Paint, 0.01f, 0, null);
                await _repository.UpsertWeaponStickers(steamId, def, stickers);

                // A knife needs the loadout slot as well as the skin row. The give hook swaps the
                // default knife for whatever sits in the Knife slot, so a paint saved against a
                // karambit does nothing while the player is still handed a default knife - which
                // is exactly why equipping a knife finish appeared to do nothing at all. Both
                // teams, because the browser does not ask which side they are picking for.
                if (isKnife)
                {
                    foreach (var team in (int[]) [2, 3])
                    {
                        await _repository.UpsertLoadoutItem(steamId, team, "Knife", def);
                    }
                }

                _cache.RefreshBySteamId(steamId);

                _logger.LogInformation(
                    "equip: {weapon} paint {paint}, {stickers} sticker(s) saved for {steam}{knife}",
                    wanted, pick.Paint, StickerCount(s), steamId,
                    isKnife ? " (+ knife loadout, both teams)" : "");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("equip: could not save {weapon}: {msg}", wanted, ex.Message);
            }
        });

        Refresh(slot);
    }

    private int StickerCount(int s)
    {
        var n = 0;

        for (var i = 0; i < StickerSlots; i++)
        {
            if (_applied[s, i].Id > 0)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    ///     The applied stickers as the JSON the weapon_skins row carries, or null when there are
    ///     none - which clears the column rather than leaving a stale craft behind.
    ///     <br /><br />
    ///     Scale is written as 1 and never edited: CS2 stores `sticker slot N scale` and ignores
    ///     it, which is why the fourth control in the editor is wear.
    /// </summary>
    private string? StickerJson(int s)
    {
        var list = new List<StickerInfo>();

        for (var i = 0; i < StickerSlots; i++)
        {
            var a = _applied[s, i];

            if (a.Id <= 0)
            {
                continue;
            }

            list.Add(new StickerInfo
            {
                Slot     = i,
                Id       = a.Id,
                Wear     = a.Wear,
                Scale    = 1f,
                Rotation = a.Rotation,
                OffsetX  = a.X,
                OffsetY  = a.Y,
            });
        }

        return list.Count == 0 ? null : JsonSerializer.Serialize(list);
    }

    /// <summary>
    ///     Glove preview: hand the player their own hands back.
    ///     <br /><br />
    ///     Gloves are WORN, not held, so there is nothing to float in front of a camera the way
    ///     the weapon preview does. The only place gloves actually look like gloves is first
    ///     person, so glove mode reverses the three things the browser normally does to the
    ///     player: the detached camera goes away, the pawn becomes visible AGAIN TO ITSELF ONLY,
    ///     and it gets a weapon to hold so the arms are drawn at all.
    ///     <br /><br />
    ///     It stays SolidType.None throughout: visible to yourself is not the same as being back
    ///     in the round, and nobody should be able to shoot a player who is picking gloves.
    /// </summary>
    private void ShowGloves(PlayerSlot slot)
    {
        var s = slot.AsPrimitive();

        _logger.LogInformation("glove preview: entered, {n} gloves in catalogue", _gloves.Count);

        DropItem(s);          // no floating weapon in glove mode
        KillLamp(s);

        if (_gloves.Count == 0)
        {
            _logger.LogWarning("glove preview: catalogue is empty");

            return;
        }

        if (Pawn(slot) is not { } pawn || !pawn.IsValid())
        {
            _logger.LogWarning("glove preview: no valid pawn for slot {s}", slot.AsPrimitive());

            return;
        }

        if (_glove3d)
        {
            ShowGloveModel(slot, s);

            return;
        }

        AttachView(slot, null);   // back to the player's own eyes

        try
        {
            // visible to themselves, still hidden from everyone else
            _bridge.TransmitManager.SetEntityBlock(pawn.Index, false);
            pawn.RenderMode  = RenderMode.Normal;
            pawn.RenderColor = new Color32(255, 255, 255, 255);
            HideFromOthers(pawn, slot);
            RestoreHeldWeapons(s);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("glove preview: could not show the pawn: {msg}", ex.Message);
        }

        var glove = _gloves[_gsel[s]];
        var list  = GloveSkinsFor(s);
        var paint = list.Length > 0 && _gfsel[s] < list.Length ? list[_gfsel[s]].Paint : 0;

        // Applied anyway, so the gloves are already correct the moment EQUIP respawns them.
        // It will NOT show up by scrolling: gloves are resolved when the pawn is created and
        // never again, which is why Gloves.cs applies them in PlayerSpawnPost. GiveGloves, the
        // bodygroup flip, a weapon holster/redraw and a deferred pass of all three were each
        // confirmed to run and none of them make the client look again. See docs/PREVIEW.md -
        // it is the same wall as re-skinning a live weapon.
        try
        {
            pawn.GiveGloves((EconGlovesId) _gloveDefs[glove], paint, 0.01f, 0);

            // GiveGloves alone does not make the client redraw the hands. Gloves.cs follows it
            // with CCSPlayerPawn::SetGlovesBodyGroup, but that is found by a signature scan which
            // expects ONE reader of the first_or_third_person token and now finds six, so it has
            // been a null pointer since some CS2 update - the module warns about it at startup.
            // Toggling the bodygroup by name is the same thing by a supported route.
            _glovePending[s] = true;
            _gloveDueAt[s]   = _bridge.ModSharp.GetGlobals().CurTime + 0.10f;

            var toggled = false;

            foreach (var group in new[] { "default_gloves", "gloves", "hands" })
            {
                try
                {
                    // 1 then 0: a single set is not a CHANGE the second time round, so scrolling
                    // to another finish wrote the same value and the client had no reason to
                    // rebuild anything. The flip is what forces the refresh.
                    pawn.SetBodyGroupByName(group, 1);
                    pawn.SetBodyGroupByName(group, 0);
                    toggled = true;

                    break;
                }
                catch
                {
                    // wrong name for this model; try the next
                }
            }

            // Gloves are drawn on the VIEWMODEL ARMS, and the arms are only drawn when a weapon
            // is deployed. Browsing with empty hands renders nothing at all, so there is nothing
            // for a glove to appear on however correctly it was applied.
            var held = "none";

            try
            {
                if (pawn.GetWeaponService() is { } svc)
                {
                    IBaseWeapon? first = null;

                    foreach (var h in svc.GetMyWeapons())
                    {
                        if (_bridge.EntityManager.FindEntityByHandle(h) is IBaseWeapon w && w.IsValid())
                        {
                            first = w;

                            break;
                        }
                    }

                    var active = pawn.GetActiveWeapon();

                    if (active is null || !active.IsValid())
                    {
                        active = first;
                    }

                    if (active is not null && active.IsValid())
                    {
                        held = active.GetWeaponClassname();

                        // The arms are rebuilt when a weapon is DEPLOYED, and that is the only
                        // moment the client resolves which gloves are on them. Holster and draw
                        // to force it - the server-side equivalent of the lastinv trick the
                        // reference plugin uses for exactly this.
                        pawn.SwitchWeapon(null);
                        pawn.SwitchWeapon(active);
                        held += " redeployed";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("glove preview: could not deploy a weapon: {msg}", ex.Message);
            }

            var cam = pawn.GetCameraService()?.ViewEntityHandle.IsValid() == true ? "detached" : "first person";

            _logger.LogInformation(
                "glove preview: {glove} paint {paint}, bodygroup {b}, camera {cam}, weapon {held}",
                _gloveNames[glove], paint, toggled ? "toggled" : "NOT toggled", cam, held);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("glove preview: GiveGloves threw: {msg}", ex.Message);
        }
    }

    /// <summary>
    ///     The second half of a glove change, a tick after the gloves were written. Flipping the
    ///     bodygroup and redeploying in the SAME tick as GiveGloves never took; the reference
    ///     implementation defers this and that is the difference being tested.
    /// </summary>
    private void RefreshGloveModel(PlayerSlot slot)
    {
        if (Pawn(slot) is not { } pawn || !pawn.IsValid())
        {
            return;
        }

        try
        {
            pawn.SetBodyGroupByName("default_gloves", 1);
            pawn.SetBodyGroupByName("default_gloves", 0);

            if (pawn.GetActiveWeapon() is { } active && active.IsValid())
            {
                pawn.SwitchWeapon(null);
                pawn.SwitchWeapon(active);
            }

            _logger.LogInformation("glove refresh: deferred pass ran");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("glove refresh threw: {msg}", ex.Message);
        }
    }

    /// <summary>
    ///     Rebuild the player's pawn so the gloves they just equipped are actually on their
    ///     hands. Gloves.cs applies them in PlayerSpawnPost, so a respawn is the supported way
    ///     to make a glove change visible at all.
    /// </summary>
    private void RespawnFor(PlayerSlot slot)
    {
        var s = slot.AsPrimitive();

        foreach (var controller in _bridge.EntityManager.GetPlayerControllers())
        {
            if (controller is null || !controller.IsValid() || new PlayerSlot(controller) != slot)
            {
                continue;
            }

            try
            {
                controller.Respawn();
                _dressedPawn[s] = 0;   // the new pawn needs dressing from scratch
                _logger.LogInformation("equip: respawned slot {s} to show the gloves",
                                       slot.AsPrimitive());
            }
            catch (Exception ex)
            {
                _logger.LogWarning("equip: could not respawn: {msg}", ex.Message);
            }

            return;
        }
    }

    /// <summary>
    ///     Spawn the glove itself in front of the camera, as a weapon entity wearing the glove's
    ///     item definition. Keeps the whole weapon-preview rig - camera, light, framing - and
    ///     sidesteps the fact that gloves on a PLAYER only resolve when the pawn is built.
    /// </summary>
    private void ShowGloveModel(PlayerSlot slot, int s)
    {
        var glove = _gloves[_gsel[s]];
        var list  = GloveSkinsFor(s);
        var paint = list.Length > 0 && _gfsel[s] < list.Length ? list[_gfsel[s]].Paint : 0;
        var def   = _gloveDefs[glove];
        var key   = "glove:" + def + "|" + paint;

        if (_item[s] is not null && _item[s].IsValid() && _spawned[s] == key)
        {
            PlaceItem(s);

            return;
        }

        DropItem(s);

        try
        {
            var built = _bridge.EntityManager.SpawnEntitySync<IBaseWeapon>(
                "weapon_knife",
                new Dictionary<string, KeyValuesVariantValueItem>
                {
                    ["origin"] = "0 0 -9000",   // out of sight until PlaceItem moves it
                });

            if (built is null || !built.IsValid())
            {
                _logger.LogWarning("glove model: could not spawn a host entity");

                return;
            }

            built.AcceptInput("ChangeSubclass", null, null, def.ToString());

            if (!built.IsValid())
            {
                _logger.LogWarning("glove model: died on ChangeSubclass {def}", def);
                _item[s] = null;

                return;
            }

            _item[s] = built;

            var view = built.AttributeContainer.Item;
            _applier.ClaimItem(view, _ownerAccount[s]);
            view.SetItemDefinitionIndexLocal((ushort) def);
            view.SetInitializedLocal(true);
            _applier.ApplyPaint(view, paint, 0.01f, 0);

            _spawned[s]     = key;
            _spawnFailed[s] = false;
            _traceLog[s]    = true;

            Pin(built);
            ShowOnlyTo(built, slot);
            PlaceItem(s);
            LightItem(slot);

            _logger.LogInformation("glove model: {glove} def {def} paint {paint} spawned",
                                   _gloveNames[glove], def, paint);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("glove model threw: {msg}", ex.Message);
            _item[s] = null;
        }
    }

    /// <summary>Hide one pawn from every OTHER player, leaving it visible to its owner.</summary>
    private void HideFromOthers(IPlayerPawn pawn, PlayerSlot owner)
    {
        try
        {
            _bridge.TransmitManager.AddEntityHooks(pawn, true);

            foreach (var c in _bridge.EntityManager.GetPlayerControllers())
            {
                if (c is null || !c.IsValid())
                {
                    continue;
                }

                if (new PlayerSlot(c) == owner || c.GetPlayerPawn() is not { } other || !other.IsValid())
                {
                    continue;
                }

                _bridge.TransmitManager.SetEntityState(pawn.Index, other.Index, false, 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("glove preview: could not scope the pawn: {msg}", ex.Message);
        }
    }

    private void KillLamp(int s)
    {
        if (_lamp[s] is null)
        {
            return;
        }

        try
        {
            if (_lamp[s].IsValid())
            {
                _lamp[s].Kill();
            }
        }
        catch
        {
            // already gone
        }

        _lamp[s] = null;
    }

    /// <summary>Row clicks while in glove mode. Returns true when the click was ours.</summary>
    private bool GloveClick(PlayerSlot slot, int s, string buttonId)
    {
        if (buttonId.StartsWith("wp", StringComparison.Ordinal)
            && int.TryParse(buttonId.AsSpan(2), out var g) && g < _gloves.Count)
        {
            _gsel[s]  = g;
            _gfsel[s] = 0;
            Refresh(slot);

            return true;
        }

        if (buttonId.StartsWith("fn", StringComparison.Ordinal)
            && int.TryParse(buttonId.AsSpan(2), out var f) && f < GloveSkinsFor(s).Length)
        {
            _gfsel[s] = f;
            Refresh(slot);

            return true;
        }

        return false;
    }

    /// <summary>
    ///     Put the player back the way the browser wants them when glove mode is left: hidden,
    ///     frozen and looking through the detached camera again. Without this, leaving glove mode
    ///     would leave a fully visible player standing in the world with the menu still open.
    /// </summary>
    private void LeaveGloveMode(PlayerSlot slot)
    {
        var s = slot.AsPrimitive();

        _dressedPawn[s] = 0;   // force Reassert to re-apply everything on the next frame

        if (Pawn(slot) is { } pawn && pawn.IsValid())
        {
            try
            {
                _bridge.TransmitManager.SetEntityBlock(pawn.Index, true);
                pawn.RenderMode  = RenderMode.TransAlpha;
                pawn.RenderColor = new Color32(255, 255, 255, 0);
                HideHeldWeapons(pawn, s);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("could not re-hide the player leaving glove mode: {msg}",
                                   ex.Message);
            }
        }

        AttachView(slot, _cam[s]);
    }

    /// <summary>
    ///     Save the glove and its finish. Two rows, like a knife: the Gloves loadout slot decides
    ///     WHICH glove the spawn hook hands out, and the skin row decides what it wears. Saving
    ///     only the skin row would paint a glove the player never gets.
    /// </summary>
    private void EquipGloves(PlayerSlot slot, IPlayerController player, int s)
    {
        var list = GloveSkinsFor(s);

        if (_gloves.Count == 0 || list.Length == 0 || _gfsel[s] >= list.Length)
        {
            _logger.LogWarning("equip: no glove finish selected");

            return;
        }

        var glove = _gloves[_gsel[s]];
        var def   = _gloveDefs[glove];
        var paint = list[_gfsel[s]].Paint;

        if (player.GetGameClient() is not { IsFakeClient: false } client)
        {
            return;
        }

        ulong steamId = client.SteamId;

        _ = Task.Run(async () =>
        {
            try
            {
                await _repository.UpsertWeaponSkin(steamId, def, paint, 0.01f, 0, null);

                foreach (var team in (int[]) [2, 3])
                {
                    await _repository.UpsertLoadoutItem(steamId, team, "Gloves", def);
                }

                _cache.RefreshBySteamId(steamId);

                _logger.LogInformation("equip: {glove} paint {paint} saved for {steam}",
                                       _gloveNames[glove], paint, steamId);

                // Respawn: a glove only reaches the client when the pawn is built, so this is
                // what actually puts it on their hands. Without it the player would have to die
                // or rebuy to see the thing they just chose.
                _respawnPending[slot.AsPrimitive()] = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("equip: could not save {glove}: {msg}", glove, ex.Message);
            }
        });

        Refresh(slot);
    }

    /// <summary>
    ///     The slot with the browser open, for the diagnostic commands that act on "the" preview.
    ///     They are single player by nature; everything a real player touches is per slot.
    /// </summary>
    private int FirstOpen()
    {
        for (var i = 0; i < MaxSlots; i++)
        {
            if (_open[i])
            {
                return i;
            }
        }

        return 0;
    }

    private void DropItem(int s)
    {
        ShowToEveryone(_item[s]);

        if (_item[s] is not null && _item[s].IsValid())
        {
            try
            {
                _item[s].Kill();
            }
            catch
            {
                // already gone
            }
        }

        _item[s]    = null;
        _spawned[s] = "";
    }

    private void Paint(PlayerSlot slot)
    {
        var s      = slot.AsPrimitive();
        var list   = FinishesFor(s);
        var wanted = _weapons[_wsel[s]];

        if (_item[s] is not { } weapon || list.Length == 0)
        {
            return;
        }

        var pick = list[_fsel[s]];

        try
        {
            var view = weapon.AttributeContainer.Item;

            // The account id MUST be the viewing player's own. The loadout system always passes
            // client.SteamId.AccountId; this passed 0, leaving an initialised econ item owned by
            // nobody - the client has to resolve that to render the weapon, and an unowned item
            // with a real definition index on it is a good way to make it fail or crash outright.
            _applier.ClaimItem(view, _ownerAccount[s]);

            // Which item this actually is. All 20 knives spawn from the same entity class, so
            // without this every one of them renders as the default knife.
            // Only when the entity class could not carry the identity itself. Stamping an index
            // that disagrees with the spawned class is what kills the client.
            if (!_ownClass && ResolveItemDef(wanted) is { } def)
            {
                view.SetItemDefinitionIndexLocal(def);
            }

            // The client ignores the whole attribute block on a view that was never marked
            // initialised - normally the give-item path does this, and a hand-spawned weapon
            // never goes down that path.
            view.SetInitializedLocal(true);

            _applier.ApplyPaint(view, pick.Paint, 0.01f, 0);

            // The loadout system flips this bodygroup for legacy paint kits, but it does so on a
            // weapon the game itself just handed out. On a free-standing preview, body index 1 is
            // empty geometry for many models - the weapon renders as nothing at all, which is why
            // it looked like "some guns are missing" rather than a clean failure.
            if (_legacyBodygroup
                && _bridge.EconItemManager.GetPaintKits().TryGetValue((uint) pick.Paint, out var kit)
                && kit.IsLegacyModel)
            {
                weapon.SetBodyGroupByName("body", 1);
            }

            ApplyStickers(view, slot.AsPrimitive());

            // NOTHING calls SetModel here any more. Forcing a knife's world model crashed the
            // client outright - that is the crash IModelGuard exists to prevent, and precaching
            // server-side is not the same as the client having the model ready. The definition
            // index above plus an initialised view is the supported way to say which knife this
            // is; the client resolves the model from that itself.
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not dress the preview: {msg}", ex.Message);
        }
    }

    /// <summary>
    ///     Write the five sticker slots onto the item view.
    ///     <br /><br />
    ///     Like the paint, these only take effect on an item that has not networked yet, so any
    ///     sticker edit rebuilds the preview through the give/drop path rather than poking the
    ///     weapon already standing in the world.
    /// </summary>
    private void ApplyStickers(IEconItemView view, int s)
    {
        for (var i = 0; i < StickerSlots; i++)
        {
            var applied = _applied[s, i];

            if (applied.Id <= 0)
            {
                continue;
            }

            var schema = StickerSchemas.Get(i);

            _applier.SetAttribute(view, schema.Id, BitConverter.Int32BitsToSingle(applied.Id));

            // Integer-typed, so it goes across as raw int bits like the id does. Scale had no
            // effect at either extreme without this; the marker is the client's cue for how to
            // read the rest of the slot.
            _applier.SetAttribute(view, schema.Wear, applied.Wear);
            _applier.SetAttribute(view, schema.Rotation, applied.Rotation);
            _applier.SetAttribute(view, schema.OffsetX, applied.X);
            _applier.SetAttribute(view, schema.OffsetY, applied.Y);
        }
    }

    /// <summary>
    ///     Keep the weapon where it is put - and do NOTHING else to it.
    ///     <br /><br />
    ///     This used to also clear gravity and velocity, drop it to a debris collision group and
    ///     set it non-solid. On an entity the engine has just constructed through its own drop
    ///     path and still simulates, that fights the physics every frame and crashed the client.
    ///     Pickup is refused through the acquire hook instead, which touches nothing.
    /// </summary>
    private static void Pin(IBaseEntity entity)
    {
        try
        {
            entity.SetMoveType(MoveType.None);
        }
        catch
        {
            // it will simply rest on the ground instead
        }
    }

    /// <summary>
    ///     Hold the item still. It does NOT auto-rotate: a spinning gun is unusable once you are
    ///     placing stickers on it, so the angle only ever moves when the player nudges it.
    /// </summary>
    private void PlaceItem(int s)
    {
        if (_item[s] is null || !_item[s].IsValid() || _eye[s] is null)
        {
            return;
        }

        var yr = _viewYaw[s] * MathF.PI / 180f;

        // right-hand vector of the view, so MOVE reads as left/right on screen rather
        // than in world space
        var rightX = MathF.Sin(yr);
        var rightY = -MathF.Cos(yr);

        // never push it past what actually fits - a clamped rifle just reads a little larger
        var zoom = _zoom[s] > 0f ? _zoom[s] : 1f;
        var dist = _distOverride > 0f
            ? _distOverride
            : MathF.Max(10f, MathF.Min(DistanceFor(_weapons[_wsel[s]]) * zoom, _clearance[s] - 12f));
        var pos = new Vector(_eye[s].Value.X + (MathF.Cos(yr) * dist) + (rightX * _panH[s]),
                             _eye[s].Value.Y + (MathF.Sin(yr) * dist) + (rightY * _panH[s]),
                             _eye[s].Value.Z - ZDrop + _panV[s]);

        try
        {
            _item[s].Teleport(pos,
                           new Vector(0f, (_viewYaw[s] + _yaw[s]) % 360f, 0f),
                           new Vector(0f, 0f, 0f));

            if (_traceLog[s])
            {
                _traceLog[s] = false;
                _logger.LogInformation("placed at {px},{py},{pz} eye {ex},{ey},{ez} dist {d} yaw {yaw}",
                                       (int) pos.X, (int) pos.Y, (int) pos.Z,
                                       (int) _eye[s].Value.X, (int) _eye[s].Value.Y, (int) _eye[s].Value.Z,
                                       (int) dist, (int) _viewYaw[s]);
            }
        }
        catch
        {
            // gone
        }
    }

    /// <summary>
    ///     The pawn goes invisible on open, but the guns in its hands are SEPARATE entities and keep
    ///     rendering - with the body gone they read as a rifle hanging in mid-air next to the
    ///     preview. The creation screen destroyed them outright; that is fine for a mannequin in a
    ///     character creator, but this browser runs on a live server where the player's weapons are
    ///     real, so they are only alpha'd out and handed straight back on close.
    /// </summary>
    /// <summary>
    ///     Put the presentation back on whatever pawn the player currently has.
    ///     <br /><br />
    ///     A round restart respawns the pawn, and the new one arrives with the normal HUD, full
    ///     opacity, visible weapons and no input capture - the menu was still on screen but had
    ///     stopped responding to clicks entirely. The old pawn's state cannot be carried over, so
    ///     it is simply re-applied whenever the pawn underneath us changes.
    /// </summary>
    private void Reassert(PlayerSlot slot)
    {
        var s = slot.AsPrimitive();

        foreach (var controller in _bridge.EntityManager.GetPlayerControllers())
        {
            if (controller is null || !controller.IsValid() || new PlayerSlot(controller) != slot)
            {
                continue;
            }

            if (controller.GetPlayerPawn() is not { } pawn || !pawn.IsValid())
            {
                return;
            }

            // A round restart does not necessarily hand back a DIFFERENT pawn - the index can be
            // reused - but it does reset the HUD, the opacity and the weapons. Watching the pawn
            // identity alone missed that, and the whole game HUD came back over the menu. Watch
            // the state we actually care about instead.
            var id = pawn.Index;

            if (id == _dressedPawn[s] && pawn.HideHud == HudHidden)
            {
                return;
            }

            _dressedPawn[s] = id;

            try
            {
                pawn.HideHud     = HudHidden;
                pawn.RenderMode  = RenderMode.TransAlpha;
                pawn.RenderColor = new Color32(255, 255, 255, 0);
            }
            catch
            {
                // cosmetic only
            }

            _dressedPawn[s] = pawn.Index;
            HideHeldWeapons(pawn, s);
            SetPickup(pawn, false);

            // Take the pawn OUT OF THE WORLD for as long as the browser is open. Three separate
            // things, because hiding alone is not enough:
            //   * transmit block  - nobody renders them
            //   * SolidType.None  - no bullet or trace can find them, so they cannot be shot AND
            //                       they cannot soak a shot meant for whoever is behind them
            //   * MoveType.None   - they do not drift or fall while frozen
            // Without the solid change an invisible player still blocks hitscan, which is worse
            // than being visible.
            try
            {
                _bridge.TransmitManager.SetEntityBlock(pawn.Index, true);
                pawn.SetSolid(SolidType.None);
                pawn.SetCollisionGroup(CollisionGroupType.Debris);
                pawn.SetMoveType(MoveType.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not take the browsing player out of the world: {msg}",
                                   ex.Message);
            }
            Layout()?.SetInputCaptureEnabled(slot, true);
            _logger.LogInformation("re-dressed pawn {id} for slot {slot}", id, slot.AsPrimitive());

            return;
        }
    }

    private void HideHeldWeapons(IPlayerPawn pawn, int s)
    {
        _hiddenWeapons[s].Clear();

        try
        {
            if (pawn.GetWeaponService() is not { } service)
            {
                return;
            }

            foreach (var handle in service.GetMyWeapons())
            {
                if (_bridge.EntityManager.FindEntityByHandle(handle) is not IBaseModelEntity w
                    || !w.IsValid())
                {
                    continue;
                }

                w.RenderMode  = RenderMode.TransAlpha;
                w.RenderColor = new Color32(255, 255, 255, 0);
                _hiddenWeapons[s].Add(w);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not hide the held weapons: {msg}", ex.Message);
        }
    }

    /// <summary>
    ///     The preview is a real dropped weapon, so the player can walk into it and take it - it
    ///     went straight into the loadout and vanished from the menu. Turn pickup off for as long
    ///     as the browser is open.
    /// </summary>
    private static void SetPickup(IPlayerPawn pawn, bool allowed)
    {
        try
        {
            if (pawn.GetWeaponService() is { } service)
            {
                service.PreventWeaponPickup = !allowed;
            }
        }
        catch
        {
            // not fatal; the item is also non-solid
        }
    }

    private void RestoreHeldWeapons(int s)
    {
        foreach (var w in _hiddenWeapons[s])
        {
            try
            {
                if (w.IsValid())
                {
                    w.RenderMode  = RenderMode.Normal;
                    w.RenderColor = new Color32(255, 255, 255, 255);
                }
            }
            catch
            {
                // the weapon is gone; nothing to give back
            }
        }

        _hiddenWeapons[s].Clear();
    }

    /// <summary>
    ///     Find a direction with room to stand the item in.
    ///     <br /><br />
    ///     Placing it blindly along the player's view put rifles inside whatever wall happened to
    ///     be there: knives at 26 units and pistols at 34 cleared it, rifles at 52 did not, so the
    ///     long guns simply never appeared. The view is detached while the menu is open, so the
    ///     presentation does not have to face the way the player was looking - it sweeps for the
    ///     roomiest direction and uses that.
    /// </summary>
    private (float Yaw, float Clearance) FindClearDirection(Vector eye, float viewYaw)
    {
        const float Wanted = 74f;   // the longest bucket plus a margin

        var bestYaw   = viewYaw;
        var bestClear = 0f;

        // the player's own facing first, so it wins whenever it is good enough
        for (var step = 0; step <= 9; step++)
        {
            foreach (var sign in step == 0 ? new[] { 1f } : new[] { 1f, -1f })
            {
                var yaw   = (viewYaw + (sign * step * 20f) + 360f) % 360f;
                var clear = ClearanceAlong(eye, yaw, Wanted);

                if (clear >= Wanted)
                {
                    return (yaw, clear);
                }

                if (clear > bestClear)
                {
                    bestClear = clear;
                    bestYaw   = yaw;
                }
            }
        }

        return (bestYaw, bestClear);
    }

    private float ClearanceAlong(Vector eye, float yaw, float wanted)
    {
        var rad = yaw * MathF.PI / 180f;
        var end = new Vector(eye.X + (MathF.Cos(rad) * wanted),
                             eye.Y + (MathF.Sin(rad) * wanted),
                             eye.Z);

        try
        {
            var trace = _bridge.PhysicsQuery.TraceLineNoPlayers(
                eye,
                end,
                InteractionLayers.Solid | InteractionLayers.WorldGeometry,
                CollisionGroupType.Default,
                TraceQueryFlag.All,
                InteractionLayers.None,
                null,
                null);

            return trace.DidHit() ? trace.Fraction * wanted : wanted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Clearance trace failed: {msg}", ex.Message);

            return wanted;   // assume clear rather than jamming the item against the player
        }
    }

    // ------------------------------------------------------------------ camera

    /// <summary>Park the camera where the player's eye was, looking straight down the anchor line.</summary>
    private void PlaceCamera(PlayerSlot slot)
    {
        var s = slot.AsPrimitive();

        if (_eye[s] is null)
        {
            return;
        }

        if (_cam[s] is null || !_cam[s].IsValid())
        {
            _cam[s] = _bridge.EntityManager.SpawnEntitySync("point_camera",
                                                         new Dictionary<string, KeyValuesVariantValueItem>
                                                         {
                                                             ["targetname"] = CamName,
                                                             ["fov"]        = "50",
                                                         });

            if (_cam[s] is null)
            {
                return;
            }
        }

        try
        {
            // the item sits straight ahead at eye height, so pitch stays 0 and it lands dead centre
            _cam[s].Teleport(_eye[s].Value, new Vector(0f, _viewYaw[s], 0f), null);
        }
        catch
        {
            _cam[s] = null;

            return;
        }

        AttachView(slot, _cam[s]);
    }

    private void AttachView(PlayerSlot slot, IBaseEntity? entity)
    {
        foreach (var controller in _bridge.EntityManager.GetPlayerControllers())
        {
            if (controller is null || !controller.IsValid() || new PlayerSlot(controller) != slot)
            {
                continue;
            }

            if (controller.GetPlayerPawn()?.GetCameraService() is { } cam)
            {
                cam.ViewEntityHandle = entity?.Handle ?? default;
            }

            return;
        }
    }

    private void OnFramePost(bool simulating, bool firstTick, bool lastTick)
    {
        if (!lastTick)
        {
            return;
        }

        var now = _bridge.ModSharp.GetGlobals().CurTime;

        for (var s = 0; s < MaxSlots; s++)
        {
            if (!_open[s])
            {
                continue;
            }

            var slot = new PlayerSlot((byte) s);

            // Input capture is re-asserted on its own cadence, NOT inside the pawn re-dress: a
            // round change can leave the pawn looking untouched while the capture is dropped, and
            // the menu then sits there fully drawn but dead to clicks.
            if (now - _lastCapture >= 1f)
            {
                _lastCapture = now;
                Layout()?.SetInputCaptureEnabled(slot, true);
            }

            // Glove mode deliberately undoes all three of these: the player is looking through
            // their OWN eyes at their OWN hands, so re-hiding the pawn, re-attaching the detached
            // camera and re-spawning a floating weapon every tick would stamp it straight back
            // out. That is exactly what happened the first time: the menu switched but the view
            // never did.
            if (_gloveMode[s] && !_glove3d)
            {
                if (_glovePending[s] && now >= _gloveDueAt[s])
                {
                    _glovePending[s] = false;
                    RefreshGloveModel(slot);
                }

                // Respawn on the frame, never inside the database callback that requested it.
                if (_respawnPending[s])
                {
                    _respawnPending[s] = false;
                    RespawnFor(slot);
                }

                continue;
            }

            Reassert(slot);
            PlaceCamera(slot);

            // a weapon lying in the world with no owner is a dropped weapon, and the game removes
            // those on its own - that is the preview blinking out. Put it straight back.
            // No per-frame teleport. Re-placing a simulated entity every tick is what the client
            // choked on; it is put where it belongs when the selection changes and left alone.
            if (!_spawnFailed[s] && (_item[s] is null || !_item[s].IsValid()) && now - _lastRebuild[s] >= 1f)
            {
                _lastRebuild[s] = now;
                ShowItem(slot);
            }
        }
    }

    // ------------------------------------------------------------------ open / close

    private void Open(IPlayerController controller)
    {
        var s = new PlayerSlot(controller).AsPrimitive();

        if (_weapons.Count == 0)
        {
            _logger.LogWarning("Arsenal catalogue is empty - nothing to show");

            return;
        }

        var slot = new PlayerSlot(controller);
        var pawn = controller.GetPlayerPawn();

        _ownerAccount[s] = controller.SteamId.AccountId;

        _yaw[s]  = StartYaw;
        _zoom[s] = 1f;
        _panH[s] = 0f;
        _panV[s] = 0f;
        _spawnFailed[s]  = false;
        _traceLog[s]     = true;

        if (pawn is not null && pawn.IsValid())
        {
            // Into the lit prefab room. Safe to use now that the pawn is teleported rather than
            // walked, and blocked from transmission the whole time.
            if (_useTeamSelectRoom)
            {
                EnterRoom(slot, pawn);
            }

            var eye = pawn.GetEyePosition();
            var ang = pawn.GetEyeAngles();

            var (yaw, clear) = FindClearDirection(eye, ang.Y);

            _viewYaw[s]   = yaw;
            _clearance[s] = clear;
            _eye[s]       = eye;

            _logger.LogInformation("presenting along yaw {yaw} with {clear} units clear (player faced {faced})",
                                   (int) yaw, (int) clear, (int) ang.Y);

            try
            {
                pawn.HideHud     = HudHidden;
                pawn.RenderMode  = RenderMode.TransAlpha;
                pawn.RenderColor = new Color32(255, 255, 255, 0);
            }
            catch
            {
                // cosmetic only
            }

            _dressedPawn[s] = pawn.Index;
            HideHeldWeapons(pawn, s);
            SetPickup(pawn, false);

            // Take the pawn OUT OF THE WORLD for as long as the browser is open. Three separate
            // things, because hiding alone is not enough:
            //   * transmit block  - nobody renders them
            //   * SolidType.None  - no bullet or trace can find them, so they cannot be shot AND
            //                       they cannot soak a shot meant for whoever is behind them
            //   * MoveType.None   - they do not drift or fall while frozen
            // Without the solid change an invisible player still blocks hitscan, which is worse
            // than being visible.
            try
            {
                _bridge.TransmitManager.SetEntityBlock(pawn.Index, true);
                pawn.SetSolid(SolidType.None);
                pawn.SetCollisionGroup(CollisionGroupType.Debris);
                pawn.SetMoveType(MoveType.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not take the browsing player out of the world: {msg}",
                                   ex.Message);
            }
        }

        Refresh(slot);

        Cls(slot, "root", "Closed", false);
        Cls(slot, "root", "Open", true);
        Layout()?.SetInputCaptureEnabled(slot, true);

        _open[slot.AsPrimitive()] = true;
        _logger.LogInformation("Arsenal opened for slot {slot}", slot.AsPrimitive());
    }

    private void Close(PlayerSlot slot)
    {
        var s = slot.AsPrimitive();

        AttachView(slot, null);
        RestoreHeldWeapons(s);
        DropRoom();

        if (_lamp[s] is not null && _lamp[s].IsValid())
        {
            try
            {
                _lamp[s].Kill();
            }
            catch
            {
                // already gone
            }
        }

        _lamp[s] = null;


        if (Pawn(slot) is { } owner)
        {
            SetPickup(owner, true);

            try
            {
                _bridge.TransmitManager.SetEntityBlock(owner.Index, false);
                owner.SetSolid(SolidType.BBox);
                owner.SetCollisionGroup(CollisionGroupType.Player);
                owner.SetMoveType(MoveType.Walk);
            }
            catch
            {
                // the pawn is gone; nothing to restore
            }

            if (_useTeamSelectRoom)
            {
                LeaveRoom(slot, owner);
            }
        }

        DropItem(s);   // the preview is scenery; it must not survive the menu

        foreach (var controller in _bridge.EntityManager.GetPlayerControllers())
        {
            if (controller is null || !controller.IsValid() || new PlayerSlot(controller) != slot)
            {
                continue;
            }

            if (controller.GetPlayerPawn() is { } pawn && pawn.IsValid())
            {
                try
                {
                    pawn.HideHud     = 0;
                    pawn.RenderMode  = RenderMode.Normal;
                    pawn.RenderColor = new Color32(255, 255, 255, 255);
                }
                catch
                {
                    // nothing to restore
                }
            }

            break;
        }

        Cls(slot, "root", "Open", false);
        Cls(slot, "root", "Closed", true);
        Layout()?.SetInputCaptureEnabled(slot, false);
        _open[slot.AsPrimitive()] = false;
        _dressedPawn[s]              = default;
    }

    // ------------------------------------------------------------------ input

    private void OnClicked(IPlayerController player, ICustomHudLayout layout, string buttonId)
    {
        if (_layout is null || !ReferenceEquals(layout, _layout))
        {
            return;
        }

        var slot = new PlayerSlot(player);
        var s    = slot.AsPrimitive();

        if (buttonId == "btn_close")
        {
            Close(slot);

            return;
        }

        if (buttonId == "btn_equip")
        {
            Equip(slot, player);

            return;
        }

        if (buttonId == "btn_gloves")
        {
            _gloveMode[s] = !_gloveMode[s];

            if (_gloveMode[s])
            {
                _stickerMode[s] = false;   // never both
            }
            else
            {
                LeaveGloveMode(slot);
            }

            Refresh(slot);

            return;
        }

        if (_gloveMode[s] && GloveClick(slot, s, buttonId))
        {
            return;
        }

        var leftCount  = _gloveMode[s] ? _gloves.Count
            : _stickerMode[s] ? _collections.Count : _weapons.Count;

        var rightCount = _gloveMode[s] ? GloveSkinsFor(s).Length
            : _stickerMode[s] ? StickersFor(s).Length : FinishesFor(s).Length;
        var fpages     = Math.Max(1, (rightCount + Rows - 1) / Rows);

        if (buttonId == "btn_mode")
        {
            _stickerMode[s] = !_stickerMode[s];

            if (_stickerMode[s] && _gloveMode[s])
            {
                _gloveMode[s] = false;
                LeaveGloveMode(slot);
            }

            // Knives cannot carry stickers, so entering sticker mode on one would offer 11,676
            // stickers that can never be applied. Move to the first gun instead.
            if (_stickerMode[s] && IsKnife(_weapons[_wsel[s]]))
            {
                for (var i = 0; i < _weapons.Count; i++)
                {
                    if (!IsKnife(_weapons[i]))
                    {
                        _wsel[s] = i;
                        _fsel[s] = 0;

                        break;
                    }
                }
            }

            Refresh(slot);

            return;
        }

        if (_stickerMode[s] && StickerClick(slot, s, buttonId))
        {
            return;
        }

        switch (buttonId)
        {
            case "rot_l":    _yaw[s]  = (_yaw[s] - RotStep + 360f) % 360f;  Refresh(slot); return;
            case "rot_r":    _yaw[s]  = (_yaw[s] + RotStep) % 360f;         Refresh(slot); return;

            // closer means a smaller stand-off distance, so zooming in shrinks it
            // "+" zooms in, which means a SHORTER stand-off distance
            case "zoom_r":   _zoom[s] = Clamp(Zoomed(s) * 0.82f, 0.35f, 2f); Refresh(slot); return;
            case "zoom_l":   _zoom[s] = Clamp(Zoomed(s) / 0.82f, 0.35f, 2f); Refresh(slot); return;

            case "panx_l": _panH[s] = Clamp(_panH[s] - PanStep, -40f, 40f); Refresh(slot); return;
            case "panx_r": _panH[s] = Clamp(_panH[s] + PanStep, -40f, 40f); Refresh(slot); return;
            case "pany_l": _panV[s] = Clamp(_panV[s] - PanStep, -40f, 40f); Refresh(slot); return;
            case "pany_r": _panV[s] = Clamp(_panV[s] + PanStep, -40f, 40f); Refresh(slot); return;

            case "view_reset":
                _yaw[s]  = StartYaw;
                _zoom[s] = 1f;
                _panH[s] = 0f;
                _panV[s] = 0f;
                Refresh(slot);

                return;
            // Only the sticker list pages; everything else fits the declared rows and scrolls.
            case "fn_prev": Page(s, false, -1, fpages); Refresh(slot); return;
            case "fn_next": Page(s, false, +1, fpages); Refresh(slot); return;
        }

        for (var i = 0; i < Rows; i++)
        {
            if (buttonId == "wp" + i)
            {
                var idx = i;   // the left column never pages; the client scrolls it

                if (idx >= leftCount)
                {
                    return;
                }

                // the right-hand list belongs to the left-hand pick, so it resets with it
                if (_stickerMode[s])
                {
                    _csel[s]  = idx;
                    _ksel[s]  = 0;
                    _kpage[s] = 0;
                }
                else
                {
                    _wsel[s]  = idx;
                    _fsel[s]  = 0;
                    _fpage[s] = 0;
                }

                Refresh(slot);

                return;
            }

            if (buttonId == "fn" + i)
            {
                var idx = (_stickerMode[s] ? _kpage[s] : _fpage[s]) * Rows + i;

                if (idx >= rightCount)
                {
                    return;
                }

                if (_stickerMode[s])
                {
                    _ksel[s] = idx;
                    _applied[s, _stkSlot[s]].Id = StickersFor(s)[idx].Paint;
                }
                else
                {
                    _fsel[s] = idx;
                }

                Refresh(slot);

                return;
            }
        }
    }

    private ECommandAction OnCommandOpen(StringCommand command)
    {
        foreach (var controller in _bridge.EntityManager.GetPlayerControllers())
        {
            if (controller is not null && controller.IsValid() && !controller.IsFakeClient)
            {
                Open(controller);
            }
        }

        return ECommandAction.Stopped;
    }

    private void Page(int s, bool left, int delta, int pages)
    {
        if (left)
        {
            ref var page = ref (_stickerMode[s] ? ref _cpage[s] : ref _wpage[s]);
            page = ((page + delta) % pages + pages) % pages;
        }
        else
        {
            ref var page = ref (_stickerMode[s] ? ref _kpage[s] : ref _fpage[s]);
            page = ((page + delta) % pages + pages) % pages;
        }
    }

    /// <summary>
    ///     The placement controls. No dragging exists in custom_hud_layout, so each axis is a
    ///     nudge pair - which is steadier anyway for lining a sticker up on a receiver.
    /// </summary>
    private bool StickerClick(PlayerSlot slot, int s, string buttonId)
    {
        const float Step  = 0.02f;   // offsets run roughly -1..1 across the weapon
        const float Turn  = 5f;
        const float Grow  = 0.10f;

        ref var applied = ref _applied[s, _stkSlot[s]];

        for (var i = 0; i < StickerSlots; i++)
        {
            if (buttonId == "stkslot" + i)
            {
                _stkSlot[s] = i;
                Refresh(slot);

                return true;
            }
        }

        switch (buttonId)
        {
            case "stk_xl": applied.X        = Clamp(applied.X - Step, -1f, 1f); break;
            case "stk_xr": applied.X        = Clamp(applied.X + Step, -1f, 1f); break;
            case "stk_yl": applied.Y        = Clamp(applied.Y - Step, -1f, 1f); break;
            case "stk_yr": applied.Y        = Clamp(applied.Y + Step, -1f, 1f); break;
            case "stk_rl": applied.Rotation = Wrap(applied.Rotation - Turn); break;
            case "stk_rr": applied.Rotation = Wrap(applied.Rotation + Turn); break;
            case "stk_sl": applied.Wear     = Clamp(applied.Wear - Grow, 0f, 1f); break;
            case "stk_sr": applied.Wear     = Clamp(applied.Wear + Grow, 0f, 1f); break;

            case "stk_clear":
                applied = new Applied();

                break;

            default:
                return false;
        }

        Refresh(slot);

        return true;
    }

    private float Zoomed(int s)
        => _zoom[s] > 0f ? _zoom[s] : 1f;

    private static float Clamp(float v, float lo, float hi)
        => v < lo ? lo : v > hi ? hi : v;

    private static float Wrap(float deg)
        => (deg % 360f + 360f) % 360f;

    private ECommandAction OnCommandDist(StringCommand command)
    {
        var s = FirstOpen();

        _logger.LogInformation("dist command: argc={n} raw='{raw}'", command.ArgCount,
                               command.ArgCount > 1 ? command.GetArg(1) : "");

        if (command.ArgCount > 1 && float.TryParse(command.GetArg(1), out var d))
        {
            _distOverride = d;
            _logger.LogInformation("preview distance override = {d} (clearance {c})", d, (int) _clearance[s]);
            PlaceItem(s);
        }

        return ECommandAction.Stopped;
    }

    private ECommandAction OnCommandLegacy(StringCommand command)
    {
        var s = FirstOpen();

        _legacyBodygroup = !_legacyBodygroup;
        _logger.LogInformation("legacy bodygroup switch = {on}", _legacyBodygroup);
        DropItem(s);

        return ECommandAction.Stopped;
    }

    /// <summary>
    ///     Put a bare, untouched knife at the exact spot the preview occupies - no paint, no
    ///     definition index, no attributes at all.
    ///     <br /><br />
    ///     This separates "the camera is not looking where I think" from "this weapon will not
    ///     render". If the knife appears while the selected rifle does not, the framing is right
    ///     and the fault is in the weapon entity; if neither appears, everything I believe about
    ///     the placement is wrong.
    /// </summary>
    private ECommandAction OnCommandLightSchema(StringCommand command)
    {
        // Probe the fields CS2Fixes actually sets on its working flashlight. Its recipe is
        // light_barn with SCHEMA FIELDS - not spawn keyvalues - and m_nDirectLight = 3.
        // The previous probe only logged offsets > 0, so a legitimate offset of 0 or a thrown
        // lookup both looked like "does not exist".
        var fields = new[]
        {
            "m_bEnabled", "m_Color", "m_flBrightness", "m_flRange", "m_flSkirt",
            "m_flSkirtNear", "m_flSoftX", "m_flSoftY", "m_vSizeParams",
            "m_nCastShadows", "m_nDirectLight", "m_flBrightnessScale", "m_flColorTemperature",
        };

        foreach (var cls in new[] { "CBarnLight", "CLightComponent", "CLightEntity",
                                    "COmniLight", "CLightDirectionalEntity" })
        {
            var hits = 0;

            foreach (var field in fields)
            {
                try
                {
                    var off = _bridge.SchemaManager.GetNetVarOffset(cls, field);
                    _logger.LogInformation("schema {cls}.{field} -> {off}", cls, field, off);
                    hits++;
                }
                catch (Exception ex)
                {
                    _logger.LogInformation("schema {cls}.{field} -> THREW {msg}", cls, field, ex.Message);
                }
            }

            _logger.LogInformation("schema {cls}: {n} field lookups returned", cls, hits);
        }

        return ECommandAction.Stopped;
    }

    /// <summary>
    ///     Locate the team-select prefab room.
    ///     <br /><br />
    ///     `prefabs/misc/team_select` is loaded as a point_prefab spawn group on EVERY CS2 map,
    ///     workshop maps included. It is an enclosed room, off the playable area, and compiled
    ///     WITH ITS OWN BAKED LIGHTING - which matters because CS2 has no spawnable light
    ///     entities, so anywhere we build ourselves would be pitch black.
    ///     <br /><br />
    ///     This dumps candidate anchors so the room's position can be pinned per map family.
    /// </summary>
    private ECommandAction OnCommandFindRoom(StringCommand command)
    {
        var s = FirstOpen();

        var origin = _eye[s] ?? new Vector(0f, 0f, 0f);

        foreach (var cls in new[] { "point_camera", "info_player_counterterrorist",
                                    "info_player_terrorist", "sky_camera", "point_prefab",
                                    "info_target", "prop_dynamic" })
        {
            var found = _bridge.EntityManager.GetAllEntitiesByClassname(cls);

            if (found is null || found.Length == 0)
            {
                continue;
            }

            var far = 0;

            foreach (var e in found)
            {
                if (e is null || !e.IsValid())
                {
                    continue;
                }

                var p = e.GetAbsOrigin();
                var d = MathF.Sqrt(((p.X - origin.X) * (p.X - origin.X))
                                 + ((p.Y - origin.Y) * (p.Y - origin.Y))
                                 + ((p.Z - origin.Z) * (p.Z - origin.Z)));

                // only the ones well away from where the player is standing
                if (d < 1500f || far >= 4)
                {
                    continue;
                }

                far++;
                _logger.LogInformation("room candidate {cls} '{name}' at {x},{y},{z}  ({d} away)",
                                       cls, e.Name ?? "", (int) p.X, (int) p.Y, (int) p.Z, (int) d);
            }

            _logger.LogInformation("{cls}: {n} total, {far} far away", cls, found.Length, far);
        }

        return ECommandAction.Stopped;
    }

    /// <summary>
    ///     Try to spawn a runtime light next to the preview.
    ///     <br /><br />
    ///     libserver.so carries light_omni / light_omni2 / light_dynamic / light_barn and friends,
    ///     so the classnames exist server-side. Whether one spawned at runtime actually RENDERS is
    ///     the open question - CS2 bakes most lighting - and it decides whether a wardrobe room can
    ///     be built anywhere or has to reuse an already-lit part of the map.
    /// </summary>
    private ECommandAction OnCommandLight(StringCommand command)
    {
        // CS2Fixes' flashlight, as close to verbatim as this API allows: created unspawned,
        // schema fields written, DispatchSpawn with the cookie, then parented to the player
        // and pushed 54 units out along their view.
        IPlayerPawn? here = null;

        foreach (var c in _bridge.EntityManager.GetPlayerControllers())
        {
            if (c is not null && c.IsValid() && !c.IsFakeClient && c.GetPlayerPawn() is { } pn && pn.IsValid())
            {
                here = pn;

                break;
            }
        }

        if (here is null)
        {
            _logger.LogInformation("flashlight: no live player");

            return ECommandAction.Stopped;
        }

        foreach (var old in _bridge.EntityManager.GetAllEntitiesByClassname("light_barn"))
        {
            if (old is not null && old.IsValid() && old.Name == "armory_flashlight")
            {
                try
                {
                    old.Kill();
                }
                catch
                {
                    // gone
                }
            }
        }

        var lamp = _bridge.EntityManager.CreateEntityByName("light_barn");

        if (lamp is null)
        {
            _logger.LogInformation("flashlight: could not create light_barn");

            return ECommandAction.Stopped;
        }

        void Field(string name, Action set)
        {
            try
            {
                set();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("flashlight: {name} failed - {msg}", name, ex.Message);
            }
        }

        Field("m_bEnabled",    () => lamp.SetNetVar("m_bEnabled", false));
        Field("m_Color",       () => lamp.SetNetVar("m_Color", new Color32(255, 255, 255, 255)));
        Field("m_flBrightness",() => lamp.SetNetVar("m_flBrightness", 1.0f));
        Field("m_flRange",     () => lamp.SetNetVar("m_flRange", 2048.0f));
        Field("m_flSoftX",     () => lamp.SetNetVar("m_flSoftX", 1.0f));
        Field("m_flSoftY",     () => lamp.SetNetVar("m_flSoftY", 1.0f));
        Field("m_flSkirt",     () => lamp.SetNetVar("m_flSkirt", 0.5f));
        Field("m_flSkirtNear", () => lamp.SetNetVar("m_flSkirtNear", 1.0f));
        Field("m_vSizeParams", () => lamp.SetNetVar("m_vSizeParams", new Vector(45f, 45f, 0.02f)));
        Field("m_nCastShadows",() => lamp.SetNetVar("m_nCastShadows", 1));
        Field("m_nDirectLight",() => lamp.SetNetVar("m_nDirectLight", 3));

        try
        {
            lamp.DispatchSpawn(new Dictionary<string, KeyValuesVariantValueItem>
            {
                ["targetname"]  = "armory_flashlight",
                ["lightcookie"] = "materials/effects/lightcookies/flashlight.vtex",
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("flashlight: DispatchSpawn failed - {msg}", ex.Message);
        }

        var eye = here.GetEyePosition();
        var ang = here.GetEyeAngles();
        var yr  = ang.Y * MathF.PI / 180f;
        var pr  = ang.X * MathF.PI / 180f;

        // 54 units out along the view, exactly as the cvar default describes
        var pos = new Vector(eye.X + (MathF.Cos(yr) * MathF.Cos(pr) * 54f),
                             eye.Y + (MathF.Sin(yr) * MathF.Cos(pr) * 54f),
                             eye.Z - (MathF.Sin(pr) * 54f));

        try
        {
            lamp.Teleport(pos, new Vector(ang.X, ang.Y, 0f), null);
            lamp.SetNetVar("m_bEnabled", true);
            lamp.AcceptInput("Enable", null, null, 0, 0);

            _logger.LogInformation("flashlight: light_barn at {x},{y},{z} facing {p}/{y2}",
                                   (int) pos.X, (int) pos.Y, (int) pos.Z, (int) ang.X, (int) ang.Y);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("flashlight: enable failed - {msg}", ex.Message);
        }

        return ECommandAction.Stopped;
    }

    // RUNTIME PREFAB LOADING IS NOT AVAILABLE. Tested twice and it crashed the server both
    // times: first with prefabs/misc/team_select (already loaded, so possibly a duplicate
    // spawn group), then with prefabs/misc/counterterrorist_team_intro which was NOT loaded
    // on the map - same crash. Spawn groups are load-time only.
    //
    // That closes the cross-map wardrobe: `team_select` reaches every map only because each
    // map author places a point_prefab for it, and we cannot place one ourselves.

    /// <summary>
    ///     Light the preview with a particle that carries a light renderer.
    ///     <br /><br />
    ///     Cycles the candidates because rcon does not deliver arguments to these handlers.
    /// </summary>
    /// <summary>
    ///     Light the preview.
    ///     <br /><br />
    ///     CS2 has no spawnable light entity - every classname spawns without error and
    ///     illuminates nothing - but its PARTICLE system has light renderers, and Valve ships one
    ///     built for exactly this: lighting an item being presented in a UI. The MVP variant works
    ///     too but pulses by design, which is wrong for a browser you sit in.
    ///     <br /><br />
    ///     Attached to the weapon so it follows the preview, and filtered to the one player
    ///     browsing so nobody else sees a light source hanging in the level.
    /// </summary>
    /// <summary>
    ///     Light the preview with a particle ENTITY, not a dispatch call.
    ///     <br /><br />
    ///     UTIL_DispatchParticleEffect is a known-broken signature on LINUX: it resolves to
    ///     nothing and silently no-ops. That is why every particle test showed absolutely nothing,
    ///     including a molotov fire that would have been impossible to miss - so "particles do not
    ///     light the weapon" was never actually demonstrated. Nothing was ever dispatched.
    ///     <br /><br />
    ///     info_particle_system is an ordinary entity: spawn it, point it at an effect, send it
    ///     Start. No signature involved, so the Linux breakage does not apply.
    /// </summary>
    /// <summary>
    ///     Light the preview with a light_barn, built the way CS2Fixes builds its flashlight.
    ///     <br /><br />
    ///     THE ORDER IS THE WHOLE TRICK: create the entity UNSPAWNED, write the schema fields,
    ///     and only THEN DispatchSpawn with the keyvalues. Every earlier attempt used
    ///     SpawnEntitySync, which creates and spawns in one call, so every field was written
    ///     after the light had already spawned - far too late to matter.
    ///     <br /><br />
    ///     m_vSizeParams is the cone (angle, angle, 0.02); without it a barn light has no shape
    ///     and lights nothing. lightcookie has to go through keyvalues because the schema prop is
    ///     a resource handle.
    /// </summary>
    private void LightItem(PlayerSlot slot)
    {
        var s = slot.AsPrimitive();

        if (!_lightOn || _item[s] is null || !_item[s].IsValid())
        {
            return;
        }

        var at = _item[s].GetAbsOrigin();
        var yr = _viewYaw[s] * MathF.PI / 180f;

        // A barn light is a SPOT with a cone, so aim matters. The lamp sits between the camera
        // and the weapon and must face ALONG the view - facing viewYaw+180 pointed the cone back
        // at the camera, lighting nothing.
        var pos = new Vector(at.X - (MathF.Cos(yr) * 54f),
                             at.Y - (MathF.Sin(yr) * 54f),
                             at.Z + 26f);
        var ang = new Vector(22f, _viewYaw[s], 0f);

        if (_lamp[s] is not null && _lamp[s].IsValid())
        {
            try
            {
                _lamp[s].Teleport(pos, ang, null);
            }
            catch
            {
                _lamp[s] = null;
            }

            return;
        }

        // created, NOT spawned
        _lamp[s] = _bridge.EntityManager.CreateEntityByName("light_barn");

        if (_lamp[s] is null)
        {
            _logger.LogWarning("light: could not create light_barn");

            return;
        }

        try
        {
            _lamp[s].SetNetVar("m_bEnabled", false);

            // Without a colour the light emits BLACK - CS2Fixes sets this and I had skipped it.
            try
            {
                _lamp[s].SetNetVar("m_Color", new Color32(255, 250, 240, 255));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("light: m_Color as Color32 failed ({msg}), trying raw", ex.Message);

                try
                {
                    _lamp[s].SetNetVar("m_Color", unchecked((int) 0xFFFFFAF0));
                }
                catch
                {
                    // leave it at whatever the default is
                }
            }

            _lamp[s].SetNetVar("m_flBrightness", 1.0f);
            _lamp[s].SetNetVar("m_flRange", 2048.0f);
            _lamp[s].SetNetVar("m_flSoftX", 1.0f);
            _lamp[s].SetNetVar("m_flSoftY", 1.0f);
            _lamp[s].SetNetVar("m_flSkirt", 0.5f);
            _lamp[s].SetNetVar("m_flSkirtNear", 1.0f);
            try
            {
                _lamp[s].SetNetVar("m_vSizeParams", new Vector(45f, 45f, 0.02f));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("light: m_vSizeParams failed: {msg}", ex.Message);
            }
            _lamp[s].SetNetVar("m_nCastShadows", 1);
            _lamp[s].SetNetVar("m_nDirectLight", 3);

            // NOW spawn it, with the cookie - the schema prop is a resource handle, so it can
            // only be set this way
            _lamp[s].DispatchSpawn(new Dictionary<string, KeyValuesVariantValueItem>
            {
                ["targetname"]  = LampName,
                ["lightcookie"] = "materials/effects/lightcookies/flashlight.vtex",
            });

            _lamp[s].Teleport(pos, ang, null);
            _lamp[s].SetNetVar("m_bEnabled", true);
            _lamp[s].AcceptInput("Enable", null, null, 0, 0);

            _logger.LogInformation("light: light_barn spawned at {x},{y},{z}",
                                   (int) pos.X, (int) pos.Y, (int) pos.Z);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("light: light_barn setup failed: {msg}", ex.Message);
        }
    }

    /// <summary>
    ///     Probe the light entity schema.
    ///     <br /><br />
    ///     Spawning light_omni2 with keyvalues produced no light. In CS2 a light is an entity that
    ///     owns a CLightComponent, and the component - not the entity - holds brightness, colour
    ///     and range. Keyvalues handed to SpawnEntitySync may never reach it, which would explain
    ///     a light that spawns cleanly and illuminates nothing.
    ///     <br /><br />
    ///     This reports which schema fields actually resolve, so the next step is informed rather
    ///     than another guess.
    /// </summary>
    private ECommandAction OnCommandProbe(StringCommand command)
    {
        foreach (var cls in new[] { "CLightComponent", "CLightEntity", "CBarnLight",
                                    "COmniLight", "CLightDirectionalEntity" })
        {
            foreach (var field in new[] { "m_flBrightness", "m_Color", "m_flRange",
                                          "m_bEnabled", "m_flBrightnessScale",
                                          "m_nCastShadows", "m_flColorTemperature",
                                          "m_CLightComponent" })
            {
                try
                {
                    var off = _bridge.SchemaManager.GetNetVarOffset(cls, field);

                    if (off > 0)
                    {
                        _logger.LogInformation("schema {cls}.{field} = +0x{off:X}", cls, field, off);
                    }
                }
                catch
                {
                    // field does not exist on this class
                }
            }
        }

        return ECommandAction.Stopped;
    }

    /// <summary>
    ///     Control test: dispatch a LOUDLY VISIBLE particle at the preview.
    ///     <br /><br />
    ///     Every light particle we tried renders light and nothing else, so "no light" and "the
    ///     dispatch never happened" look identical. This proves which one it is. If fire appears,
    ///     dispatch works and light particles genuinely do not light; if nothing appears, particle
    ///     lighting was never actually tested and the earlier conclusion was wrong.
    /// </summary>
    /// <summary>
    ///     Try EXPOSURE instead of light.
    ///     <br /><br />
    ///     A plugin cannot create light in CS2 - that is settled. But it may be able to change how
    ///     the camera meters the scene. env_tonemap_controller drives auto-exposure, and in a dark
    ///     void with nothing else on screen to blow out, opening the exposure right up could make
    ///     an unlit weapon readable without lighting it at all.
    ///     <br /><br />
    ///     Credit for the idea goes to the post_processing_volume suggestion - same family.
    /// </summary>
    private ECommandAction OnCommandExposure(StringCommand command)
    {
        foreach (var old in _bridge.EntityManager.GetAllEntitiesByClassname("env_tonemap_controller"))
        {
            if (old is not null && old.IsValid() && old.Name == "armory_tonemap")
            {
                try
                {
                    old.Kill();
                }
                catch
                {
                    // gone
                }
            }
        }

        var tone = _bridge.EntityManager.SpawnEntitySync("env_tonemap_controller",
                                                         new Dictionary<string, KeyValuesVariantValueItem>
                                                         {
                                                             ["targetname"] = "armory_tonemap",
                                                         });

        if (tone is null)
        {
            _logger.LogInformation("exposure: env_tonemap_controller did NOT spawn");

            return ECommandAction.Stopped;
        }

        _exposure = _exposure <= 0f ? 4f : (_exposure >= 16f ? 0.5f : _exposure * 2f);

        try
        {
            tone.AcceptInput("SetAutoExposureMin", null, null, _exposure, 0);
            tone.AcceptInput("SetAutoExposureMax", null, null, _exposure, 0);
            tone.AcceptInput("SetBloomScale", null, null, 2.0f, 0);
            tone.AcceptInput("Enable", null, null, 0, 0);

            _logger.LogInformation("exposure: tonemap enabled at {v}", _exposure);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("exposure inputs failed: {msg}", ex.Message);
        }

        return ECommandAction.Stopped;
    }

    private ECommandAction OnCommandSmoke(StringCommand command)
    {
        var s = FirstOpen();

        if (_item[s] is null || !_item[s].IsValid())
        {
            _logger.LogInformation("control: open the browser first");

            return ECommandAction.Stopped;
        }

        var visible = new[]
        {
            "particles/inferno_fx/molotov_fire_a.vpcf",
            "particles/explosions_fx/explosion_smokegrenade_01.vpcf",
            "particles/burning_fx/env_fire_large.vpcf",
        };

        var fx = ControlFx;   // precached at map load, where it has to be

        // Cycle the CALL SHAPE, not the particle. Nothing rendered with the entity-attached
        // overload and a per-slot filter, so the fault may be the filter or the attachment
        // rather than particles themselves.
        var how = _smokeKind % 4;
        _smokeKind++;

        var at = _item[s].GetAbsOrigin();
        var spot = new Vector(at.X, at.Y, at.Z + 20f);

        // RecipientFilter exposes IsEmpty(), which means an empty one is a real possibility -
        // and an empty filter reaches NOBODY. Log what each one actually contains before
        // blaming the particle system.
        var fAll  = new RecipientFilter();
        var fSlot = new PlayerSlot((byte) s);
        var fOne  = new RecipientFilter(fSlot);

        _logger.LogInformation("filters: default empty={a}  slot({s}) empty={b}",
                               fAll.IsEmpty(), s, fOne.IsEmpty());

        try
        {
            switch (how)
            {
                case 0:   // position, broadcast to everyone
                    _bridge.ParticleManager.DispatchParticleEffect(
                        fx, spot, new Vector(0f, 0f, 0f), new RecipientFilter());

                    break;

                case 1:   // position, filtered to the one player
                    _bridge.ParticleManager.DispatchParticleEffect(
                        fx, spot, new Vector(0f, 0f, 0f),
                        new RecipientFilter(new PlayerSlot((byte) s)));

                    break;

                case 2:   // attached to the weapon, broadcast
                    _bridge.ParticleManager.DispatchParticleEffect(
                        fx, ParticleAttachmentType.AbsOrigin, _item[s], 0, false, new RecipientFilter());

                    break;

                default:  // filter built straight from the live controller
                {
                    IPlayerController? who = null;

                    foreach (var c in _bridge.EntityManager.GetPlayerControllers())
                    {
                        if (c is not null && c.IsValid() && !c.IsFakeClient)
                        {
                            who = c;

                            break;
                        }
                    }

                    if (who is null)
                    {
                        _logger.LogInformation("control: no live controller");

                        break;
                    }

                    var f = new RecipientFilter(who);
                    _logger.LogInformation("control: controller filter empty={e}", f.IsEmpty());

                    _bridge.ParticleManager.DispatchParticleEffect(
                        fx, spot, new Vector(0f, 0f, 0f), f);

                    break;
                }
            }

            _logger.LogInformation("control: call shape {how} dispatched at {x},{y},{z}",
                                   how, (int) spot.X, (int) spot.Y, (int) spot.Z);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("control shape {how} threw: {msg}", how, ex.Message);
        }

        return ECommandAction.Stopped;
    }

    private ECommandAction OnCommandVoid(StringCommand command)
    {
        _voidDown = !_voidDown;
        _logger.LogInformation("void direction: {d}", _voidDown ? "DOWN (black)" : "UP (sky)");

        return ECommandAction.Stopped;
    }

    private ECommandAction OnCommandLightToggle(StringCommand command)
    {
        _lightOn = !_lightOn;
        _logger.LogInformation("preview light: {s}", _lightOn ? "ON" : "OFF");

        return ECommandAction.Stopped;
    }

    private ECommandAction OnCommandBuild(StringCommand command)
    {
        _buildOurselves = !_buildOurselves;
        _logger.LogInformation("spawn the preview ourselves: {s}", _buildOurselves ? "ON" : "OFF");

        return ECommandAction.Stopped;
    }

    private ECommandAction OnCommandFx(StringCommand command)
    {
        var s = FirstOpen();

        if (_item[s] is not null && _item[s].IsValid())
        {
            LightItem(new PlayerSlot((byte) s));
        }

        return ECommandAction.Stopped;
    }

    private ECommandAction OnCommandClose(StringCommand command)
    {
        for (var s = 0; s < MaxSlots; s++)
        {
            if (_open[s])
            {
                Close(new PlayerSlot((byte) s));
            }
        }

        return ECommandAction.Stopped;
    }
}
