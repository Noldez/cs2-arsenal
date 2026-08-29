using System.Text.Json;
using SqlSugar;

namespace Armory.Data;

internal class InventoryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Database _database;

    public InventoryRepository(Database database)
    {
        _database = database;
    }

    private SqlSugarScope Db => _database.Client;

    // mutable classes so SqlSugar maps columns leniently by name
    private class WeaponSkinRow
    {
        public int     item_def     { get; set; }
        public int     paint_id     { get; set; }
        public float   wear         { get; set; }
        public int     seed         { get; set; }
        public int?    stattrak     { get; set; }
        public string? name_tag     { get; set; }
        public string? stickers     { get; set; }
        public string? keychain     { get; set; }
        public string? custom_model { get; set; }
    }

    private class LoadoutRow
    {
        public int    team     { get; set; }
        public string slot     { get; set; } = string.Empty;
        public int    item_def { get; set; }
    }

    private class PlayerModelRow
    {
        public int    team       { get; set; }
        public string model_path { get; set; } = string.Empty;
    }

    public async Task<Inventory> GetInventory(ulong steamId)
    {
        var parameters = new { steamId };

        var weaponRows = await Db.Ado.SqlQueryAsync<WeaponSkinRow>(
                             "SELECT item_def, paint_id, wear, seed, stattrak, name_tag, stickers, keychain, custom_model " +
                             "FROM weapon_skins WHERE steam_id = @steamId", parameters);

        var loadoutRows = await Db.Ado.SqlQueryAsync<LoadoutRow>(
                              "SELECT team, slot, item_def FROM loadouts WHERE steam_id = @steamId", parameters);

        var playerModelRows = await Db.Ado.SqlQueryAsync<PlayerModelRow>(
                                  "SELECT team, model_path FROM player_models WHERE steam_id = @steamId", parameters);

        var weapons = new Dictionary<int, WeaponSkinInfo>();

        foreach (var row in weaponRows)
        {
            weapons[row.item_def] = new WeaponSkinInfo
            {
                ItemDef     = row.item_def,
                PaintId     = (ushort) row.paint_id,
                Wear        = row.wear,
                Seed        = row.seed,
                StatTrak    = row.stattrak,
                NameTag     = row.name_tag ?? string.Empty,
                CustomModel = row.custom_model,
                Stickers    = ParseJson<StickerInfo[]>(row.stickers) ?? [],
                Keychain    = ParseJson<KeychainInfo>(row.keychain),
            };
        }

        var loadout = new Dictionary<(int, CosmeticSlot), int>();

        foreach (var row in loadoutRows)
        {
            if (Enum.TryParse<CosmeticSlot>(row.slot, true, out var slot))
            {
                loadout[(row.team, slot)] = row.item_def;
            }
        }

        return new Inventory
        {
            Weapons      = weapons,
            Loadout      = loadout,
            PlayerModels = playerModelRows.ToDictionary(r => r.team, r => r.model_path),
        };
    }

    /// <summary>
    ///     Sets a player's skin for one weapon, preserving anything the row already carries that
    ///     the in-game browser does not edit (name tag, stickers, keychain, custom model).
    /// </summary>
    public Task UpsertWeaponSkin(ulong steamId, int itemDef, int paintId, float wear, int seed, int? statTrak)
        => Db.Ado.ExecuteCommandAsync(
            """
            INSERT INTO weapon_skins (steam_id, item_def, paint_id, wear, seed, stattrak)
            VALUES (@steamId, @itemDef, @paintId, @wear, @seed, @statTrak)
            ON DUPLICATE KEY UPDATE
                paint_id = VALUES(paint_id),
                wear     = VALUES(wear),
                seed     = VALUES(seed),
                stattrak = VALUES(stattrak)
            """,
            new { steamId, itemDef, paintId, wear, seed, statTrak });

    /// <summary>
    ///     Puts an item in a loadout slot for one team. Knives need this as well as a skin row:
    ///     the give hook swaps the default knife for whatever sits in the Knife slot, and a skin
    ///     saved against a karambit does nothing while the player is still handed a default knife.
    /// </summary>
    public Task UpsertLoadoutItem(ulong steamId, int team, string slot, int itemDef)
        => Db.Ado.ExecuteCommandAsync(
            """
            INSERT INTO loadouts (steam_id, team, slot, item_def)
            VALUES (@steamId, @team, @slot, @itemDef)
            ON DUPLICATE KEY UPDATE item_def = VALUES(item_def)
            """,
            new { steamId, team, slot, itemDef });

    /// <summary>
    ///     Replaces the sticker set on one weapon. Separate from <see cref="UpsertWeaponSkin" />
    ///     on purpose: that one deliberately preserves the stickers column so a finish change
    ///     never wipes a craft, so the browser has to say explicitly when it means to edit it.
    ///     Pass null to clear.
    /// </summary>
    public Task UpsertWeaponStickers(ulong steamId, int itemDef, string? stickersJson)
        => Db.Ado.ExecuteCommandAsync(
            """
            INSERT INTO weapon_skins (steam_id, item_def, stickers)
            VALUES (@steamId, @itemDef, @stickersJson)
            ON DUPLICATE KEY UPDATE stickers = VALUES(stickers)
            """,
            new { steamId, itemDef, stickersJson });

    public Task UpdateStatTrak(ulong steamId, int itemDef, int statTrak)
        => Db.Ado.ExecuteCommandAsync(
            "UPDATE weapon_skins SET stattrak = @statTrak WHERE steam_id = @steamId AND item_def = @itemDef",
            new { steamId, itemDef, statTrak });

    /// <summary>Every model path the server may ever SetModel — used to build the precache set.</summary>
    public async Task<HashSet<string>> GetAllModelPaths()
    {
        var paths = await Db.Ado.SqlQueryAsync<string>("""
                                                       SELECT model_path FROM precache_models
                                                       UNION SELECT model_path FROM player_models
                                                       UNION SELECT custom_model FROM weapon_skins WHERE custom_model IS NOT NULL
                                                       """);

        return paths.Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static T? ParseJson<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
