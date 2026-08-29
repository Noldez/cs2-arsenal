using Armory.Data;
using Armory.Services;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;

namespace Armory.Modules;

internal class MusicKits : IArmoryService
{
    private readonly InterfaceBridge _bridge;
    private readonly IPlayerCache    _cache;

    public MusicKits(InterfaceBridge bridge, IPlayerCache cache)
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

        // NO DefaultLoadoutSlot check. A music kit is not an item definition: the whole schema
        // holds only two `musickit` items and neither is a kit. The kits live in their own
        // music_definitions block with their own ids, which is what MusicId takes, so an item
        // slot test can never pass and the module could not apply anything at all.
        if (_cache.GetLoadoutItem(client, @params.Pawn.Team, CosmeticSlot.Music) is not { } musicDef
            || musicDef <= 0)
        {
            return;
        }

        if (@params.Controller.GetInventoryService() is { } inventory)
        {
            inventory.MusicId = (ushort) musicDef;
        }
    }
}
