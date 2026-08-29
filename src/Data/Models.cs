using System.Text.Json.Serialization;

namespace Armory.Data;

public enum CosmeticSlot
{
    Knife,
    Gloves,
    Agent,
    Music,
    Medal,
}

public record StickerInfo
{
    [JsonPropertyName("slot")]     public int   Slot     { get; init; }
    [JsonPropertyName("id")]       public int   Id       { get; init; }
    [JsonPropertyName("wear")]     public float Wear     { get; init; }
    [JsonPropertyName("scale")]    public float Scale    { get; init; } = 1f;
    [JsonPropertyName("rotation")] public float Rotation { get; init; }
    [JsonPropertyName("x")]        public float OffsetX  { get; init; }
    [JsonPropertyName("y")]        public float OffsetY  { get; init; }
}

public record KeychainInfo
{
    [JsonPropertyName("id")]   public int   Id   { get; init; }
    [JsonPropertyName("x")]    public float X    { get; init; }
    [JsonPropertyName("y")]    public float Y    { get; init; }
    [JsonPropertyName("z")]    public float Z    { get; init; }
    [JsonPropertyName("seed")] public float Seed { get; init; }
}

public record WeaponSkinInfo
{
    public int    ItemDef     { get; init; }
    public ushort PaintId     { get; init; }
    public float  Wear        { get; init; }
    public int    Seed        { get; init; }
    public int?   StatTrak    { get; set; }
    public string NameTag     { get; init; } = string.Empty;
    public string? CustomModel { get; init; }

    public StickerInfo[] Stickers { get; init; } = [];
    public KeychainInfo? Keychain { get; init; }
}

/// <summary>Everything Armory knows about one player, loaded in a single DB round-trip.</summary>
public record Inventory
{
    public static readonly Inventory Empty = new();

    public IReadOnlyDictionary<int, WeaponSkinInfo> Weapons { get; init; }
        = new Dictionary<int, WeaponSkinInfo>();

    // key: (team, slot) — team uses CStrikeTeam values (2 = T, 3 = CT)
    public IReadOnlyDictionary<(int Team, CosmeticSlot Slot), int> Loadout { get; init; }
        = new Dictionary<(int, CosmeticSlot), int>();

    // key: team (CStrikeTeam values: 2 = T, 3 = CT)
    public IReadOnlyDictionary<int, string> PlayerModels { get; init; }
        = new Dictionary<int, string>();
}
