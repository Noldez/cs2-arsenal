using Armory.Data;
using Microsoft.Extensions.Logging;
using Sharp.Extensions.CommandManager;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Armory.Services;

internal interface IPlayerCache
{
    Inventory Get(IGameClient client);

    WeaponSkinInfo? GetWeaponSkin(IGameClient client, int itemDef);

    int? GetLoadoutItem(IGameClient client, CStrikeTeam team, CosmeticSlot slot);

    void Refresh(IGameClient client, bool notify = false);

    bool RefreshBySteamId(ulong steamId);
}

internal class PlayerCache : IPlayerCache, IArmoryService, IClientListener
{
    public int ListenerVersion  => IClientListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge      _bridge;
    private readonly InventoryRepository  _repository;
    private readonly ICommandManager      _commands;
    private readonly ILogger<PlayerCache> _logger;

    private readonly Inventory[] _inventories = new Inventory[PlayerSlot.MaxPlayerCount];

    public PlayerCache(InterfaceBridge      bridge,
                       InventoryRepository  repository,
                       ICommandManager      commands,
                       ILogger<PlayerCache> logger)
    {
        _bridge     = bridge;
        _repository = repository;
        _commands   = commands;
        _logger     = logger;

        Array.Fill(_inventories, Inventory.Empty);
    }

    public bool Init()
    {
        _bridge.ClientManager.InstallClientListener(this);
        _commands.RegisterClientCommand("armory_refresh", OnCommandRefresh);

        return true;
    }

    public void Shutdown()
    {
        _bridge.ClientManager.RemoveClientListener(this);
    }

    public void OnClientPutInServer(IGameClient client)
    {
        if (client.IsFakeClient)
        {
            return;
        }

        _inventories[client.Slot] = Inventory.Empty;

        Load(client.SteamId, false);
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        _inventories[client.Slot] = Inventory.Empty;
    }

    public Inventory Get(IGameClient client)
        => _inventories[client.Slot];

    public WeaponSkinInfo? GetWeaponSkin(IGameClient client, int itemDef)
        => _inventories[client.Slot].Weapons.GetValueOrDefault(itemDef);

    public int? GetLoadoutItem(IGameClient client, CStrikeTeam team, CosmeticSlot slot)
        => _inventories[client.Slot].Loadout.TryGetValue(((int) team, slot), out var itemDef)
            ? itemDef
            : null;

    public void Refresh(IGameClient client, bool notify = false)
    {
        if (client.IsFakeClient)
        {
            return;
        }

        Load(client.SteamId, notify);
    }

    public bool RefreshBySteamId(ulong steamId)
    {
        if (_bridge.ClientManager.GetGameClient(new SteamID(steamId)) is not { IsFakeClient: false })
        {
            return false;
        }

        Load(new SteamID(steamId), true);

        return true;
    }

    private void OnCommandRefresh(IGameClient client, StringCommand command)
        => Refresh(client, true);

    private void Load(SteamID steamId, bool notify)
    {
        Task.Run(async () =>
        {
            try
            {
                var inventory = await _repository.GetInventory(steamId).ConfigureAwait(false);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    if (_bridge.ClientManager.GetGameClient(steamId) is not { } client)
                    {
                        return;
                    }

                    _inventories[client.Slot] = inventory;

                    if (notify)
                    {
                        client.Print(HudPrintChannel.SayText2,
                                     $" [{ChatColor.Gold}Armory{ChatColor.White}] Inventory refreshed.");
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load inventory for {steamId}", steamId);
            }
        });
    }
}
