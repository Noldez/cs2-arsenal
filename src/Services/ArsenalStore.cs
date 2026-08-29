using Armory.Data;

namespace Armory.Services;

/// <summary>
///     Everything the browser needs from storage, and nothing else.
///     <br /><br />
///     The browser has no business knowing whether a choice ends up in MySQL, behind an HTTP API, or
///     in another plugin's schema. It only knows that a player picked something and that the next
///     weapon they are handed should be wearing it. Keeping that behind an interface means swapping
///     the backend later is a decision rather than a rewrite of the equip path: point the container
///     at a different implementation and the browser does not change.
///     <br /><br />
///     Borrowed from Ariiisu/WeaponSkin, which does the same thing with its IRequestManager, and it
///     is the right shape. If we ever move the applying side onto that plugin, this interface is the
///     seam it plugs into.
/// </summary>
internal interface IArsenalStore
{
    /// <summary>
    ///     Sets a player's finish for one item. Must preserve anything on the row the browser does
    ///     not edit, so changing a finish never wipes a name tag, a keychain or a custom model.
    /// </summary>
    Task SaveFinish(ulong steamId, int itemDef, int paint, float wear, int seed, int? statTrak);

    /// <summary>Replaces the sticker craft on one item. Null clears it.</summary>
    Task SaveStickers(ulong steamId, int itemDef, string? stickersJson);

    /// <summary>
    ///     Puts an item in a loadout slot for one team. Knives and gloves need this AS WELL AS a
    ///     finish, because the spawn hook reads the loadout slot to decide which item to hand out
    ///     and only then paints it. Saving the finish alone paints an item the player never gets.
    /// </summary>
    Task SaveLoadoutItem(ulong steamId, int team, string slot, int itemDef);

    /// <summary>Re-read this player so the change applies from their next spawn or buy.</summary>
    void Invalidate(ulong steamId);
}

/// <summary>The default store: our own tables, through the repository and the player cache.</summary>
internal sealed class SqlArsenalStore : IArsenalStore, IArmoryService
{
    private readonly InventoryRepository _repository;
    private readonly IPlayerCache        _cache;

    public SqlArsenalStore(InventoryRepository repository, IPlayerCache cache)
    {
        _repository = repository;
        _cache      = cache;
    }

    public bool Init()
        => true;

    public void Shutdown()
    {
    }

    public Task SaveFinish(ulong steamId, int itemDef, int paint, float wear, int seed, int? statTrak)
        => _repository.UpsertWeaponSkin(steamId, itemDef, paint, wear, seed, statTrak);

    public Task SaveStickers(ulong steamId, int itemDef, string? stickersJson)
        => _repository.UpsertWeaponStickers(steamId, itemDef, stickersJson);

    public Task SaveLoadoutItem(ulong steamId, int team, string slot, int itemDef)
        => _repository.UpsertLoadoutItem(steamId, team, slot, itemDef);

    public void Invalidate(ulong steamId)
        => _cache.RefreshBySteamId(steamId);
}
