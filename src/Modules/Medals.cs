using Armory.Data;
using Armory.Services;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;

namespace Armory.Modules;

internal class Medals : IArmoryService
{
    private readonly InterfaceBridge _bridge;
    private readonly IPlayerCache    _cache;

    public Medals(InterfaceBridge bridge, IPlayerCache cache)
    {
        _bridge = bridge;
        _cache  = cache;
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);

        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var client = @params.Client;

        if (client.IsFakeClient)
        {
            return;
        }

        if (@params.Controller.GetInventoryService() is not { } inventory)
        {
            return;
        }

        if (_cache.GetLoadoutItem(client, @params.Pawn.Team, CosmeticSlot.Medal) is not { } medalDef
            || _bridge.EconItemManager.GetEconItemDefinitionByIndex((EconItemId) medalDef)
                is not { DefaultLoadoutSlot: 55 })
        {
            return;
        }

        var ranks = inventory.GetSchemaFixedArray<uint>("m_rank");
        ranks[5] = (uint) medalDef;
    }
}
