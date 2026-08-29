using Armory.Data;
using Armory.Services;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameObjects;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Armory.Modules;

/// <summary>
///     Applies weapon paint/wear/seed, StatTrak, name tag, stickers and keychains through the
///     GiveNamedItem hook, and swaps knives to the player's selected knife definition.
/// </summary>
internal class WeaponSkins : IArmoryService
{
    private readonly InterfaceBridge      _bridge;
    private readonly IPlayerCache         _cache;
    private readonly IModelGuard          _modelGuard;
    private readonly InventoryRepository  _repository;
    private readonly ILogger<WeaponSkins> _logger;

    // ReSharper disable InconsistentNaming
    private readonly ISkinApplier _applier;
    // ReSharper restore InconsistentNaming

    public WeaponSkins(InterfaceBridge      bridge,
                       IPlayerCache         cache,
                       IModelGuard          modelGuard,
                       InventoryRepository  repository,
                       ISkinApplier         applier,
                       ILogger<WeaponSkins> logger)
    {
        _applier    = applier;
        _bridge     = bridge;
        _cache      = cache;
        _modelGuard = modelGuard;
        _repository = repository;
        _logger     = logger;

    }

    public bool Init()
    {
        _bridge.HookManager.GiveNamedItem.InstallHookPre(OnGiveNamedItemPre);
        _bridge.HookManager.GiveNamedItem.InstallHookPost(OnGiveNamedItemPost);
        _bridge.HookManager.PlayerKilledPost.InstallForward(OnPlayerKilledPost);

        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.GiveNamedItem.RemoveHookPre(OnGiveNamedItemPre);
        _bridge.HookManager.GiveNamedItem.RemoveHookPost(OnGiveNamedItemPost);
        _bridge.HookManager.PlayerKilledPost.RemoveForward(OnPlayerKilledPost);
    }

    private HookReturnValue<IBaseWeapon> OnGiveNamedItemPre(IGiveNamedItemHookParams @params, HookReturnValue<IBaseWeapon> ret)
    {
        var client = @params.Client;

        if (client.IsFakeClient)
        {
            return new();
        }

        var pawn = @params.Pawn;
        var team = pawn.Team;

        if (team <= CStrikeTeam.Spectator)
        {
            return new(EHookAction.SkipCallReturnOverride);
        }

        if (@params.Classname.StartsWith("weapon_knife")
            && _cache.GetLoadoutItem(client, team, CosmeticSlot.Knife) is { } knifeDef
            && _bridge.EconItemManager.GetEconItemDefinitionByIndex((EconItemId) knifeDef) is { } definition)
        {
            @params.SetOverride(definition.DefinitionName, true);

            return new(EHookAction.ChangeParamReturnDefault);
        }

        return new();
    }

    private void OnGiveNamedItemPost(IGiveNamedItemHookParams @params, HookReturnValue<IBaseWeapon> ret)
    {
        if (ret.Action == EHookAction.SkipCallReturnOverride)
        {
            return;
        }

        if (ret.ReturnValue is not { IsWeapon: true } weapon)
        {
            return;
        }

        var client = @params.Client;

        if (client.IsFakeClient)
        {
            return;
        }

        var view      = weapon.AttributeContainer.Item;
        var itemIndex = weapon.ItemDefinitionIndex;

        if (weapon.IsKnife
            && ret.Action                 != EHookAction.ChangeParamReturnDefault
            && weapon.ItemDefinitionIndex != (int) EconItemId.KnifeCt
            && weapon.ItemDefinitionIndex != (int) EconItemId.KnifeTe)
        {
            view.SetNetVar("m_iEntityQuality", 4);
            view.SetQualityLocal(4);
        }

        if (_cache.GetWeaponSkin(client, itemIndex) is not { } skin)
        {
            return;
        }

        _applier.ClaimItem(view, client.SteamId.AccountId);

        if (!string.IsNullOrEmpty(skin.NameTag))
        {
            view.SetCustomNameLocal(skin.NameTag);
        }

        if (skin.StatTrak is { } statTrak)
        {
            view.SetQualityLocal(9);
            _applier.SetAttribute(view, "kill eater"u8, statTrak);
            _applier.SetAttribute(view, "kill eater score type"u8, 0);
        }

        if (!string.IsNullOrEmpty(skin.CustomModel))
        {
            _modelGuard.TrySetModel(weapon, skin.CustomModel);

            return;
        }

        if (_bridge.EconItemManager.GetPaintKits().TryGetValue(skin.PaintId, out var paintKit))
        {
            _applier.ApplyPaint(view, skin.PaintId, skin.Wear, skin.Seed);

            if (weapon.Slot is GearSlot.Rifle or GearSlot.Pistol && paintKit.IsLegacyModel)
            {
                weapon.SetBodyGroupByName("body", 1);
            }
        }

        foreach (var sticker in skin.Stickers)
        {
            if (sticker.Id <= 0 || sticker.Slot is < 0 or > 4)
            {
                continue;
            }

            var schema = StickerSchemas.Get(sticker.Slot);
            _applier.SetAttribute(view, schema.Id, BitConverter.Int32BitsToSingle(sticker.Id));
            _applier.SetAttribute(view, schema.Wear, sticker.Wear);
            _applier.SetAttribute(view, schema.Scale, sticker.Scale);
            _applier.SetAttribute(view, schema.Rotation, sticker.Rotation);
            _applier.SetAttribute(view, schema.OffsetX, sticker.OffsetX);
            _applier.SetAttribute(view, schema.OffsetY, sticker.OffsetY);
        }

        if (skin.Keychain is { Id: > 0 } keychain)
        {
            var schema = StickerSchemas.GetKeychain(0);
            _applier.SetAttribute(view, schema.Id, BitConverter.Int32BitsToSingle(keychain.Id));
            _applier.SetAttribute(view, schema.Seed, keychain.Seed);
            _applier.SetAttribute(view, schema.OffsetX, keychain.X);
            _applier.SetAttribute(view, schema.OffsetY, keychain.Y);
            _applier.SetAttribute(view, schema.OffsetZ, keychain.Z);
        }
    }

    private void OnPlayerKilledPost(IPlayerKilledForwardParams @params)
    {
        var attackerSlot = @params.AttackerPlayerSlot;

        if (attackerSlot < 0
            || _bridge.ClientManager.GetGameClient((PlayerSlot) attackerSlot) is not { } attacker)
        {
            return;
        }

        if (_bridge.EntityManager.FindEntityByHandle(@params.AttackerPawnHandle) is not { IsValidEntity: true } attackerPawn
            || !attackerPawn.IsPlayer(true))
        {
            return;
        }

        var abilityEntity = _bridge.EntityManager.FindEntityByHandle(@params.AbilityHandle);

        if (abilityEntity is not { IsValidEntity: true } || abilityEntity.AsBaseWeapon() is not { } weapon)
        {
            return;
        }

        var itemDef = weapon.ItemDefinitionIndex;

        if (_cache.GetWeaponSkin(attacker, itemDef) is not { StatTrak: not null } skin)
        {
            return;
        }

        var view     = weapon.AttributeContainer.Item;
        var statTrak = skin.StatTrak.Value + 1;

        skin.StatTrak = statTrak;
        view.SetQualityLocal(9);
        _applier.SetAttribute(view, "kill eater"u8, statTrak);
        _applier.SetAttribute(view, "kill eater score type"u8, 0);

        var steamId = attacker.SteamId;

        Task.Run(async () =>
        {
            try
            {
                await _repository.UpdateStatTrak(steamId, itemDef, statTrak).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist StatTrak for {steamId}", steamId);
            }
        });
    }

}
