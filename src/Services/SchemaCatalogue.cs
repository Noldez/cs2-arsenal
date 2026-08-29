using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Armory.Services;

/// <summary>One finish: the paint kit, what to call it, and how good it is.</summary>
internal sealed record CatFinish(int Paint, string Name, int Rarity, string Kit);

/// <summary>One glove and every finish it can wear.</summary>
internal sealed record CatGlove(int Def, string Name, IReadOnlyList<CatFinish> Finishes);

/// <summary>A music kit. Its id is a music_definitions id, NOT an item definition index.</summary>
internal sealed record CatMusic(int Id, string Name, string SchemaName, string Image);

internal sealed record Catalogues(
    IReadOnlyDictionary<string, IReadOnlyList<CatFinish>> Weapons,
    IReadOnlyDictionary<string, CatGlove>                 Gloves,
    IReadOnlyList<CatMusic>                               Music,
    IReadOnlyDictionary<int, string>                      ItemNames,
    IReadOnlyDictionary<int, string>                      ItemImages,
    IReadOnlyDictionary<string, IReadOnlyList<CatFinish>> Stickers);

internal interface ISchemaCatalogue
{
    Catalogues Build();
}

/// <summary>
///     Builds every catalogue from the game's own schema, at load time.
///     <br /><br />
///     This is a port of tools/build_catalog.py and tools/build_gloves.py, which read the same
///     files out of pak01 at build time and shipped JSON. That meant a CS2 update left the
///     catalogues quietly stale until someone remembered to regenerate and redeploy them.
///     <br /><br />
///     Every trap below fails SILENTLY and produces a plausible but wrong catalogue, so each is
///     commented where it bites:
///     <list type="bullet">
///         <item>items_game.txt has SEVERAL paint_kits blocks and 55 sticker_kits blocks; reading
///               only the first loses two thirds of them.</item>
///         <item>Localisation pairs must match per LINE, or the next line's key is read as this
///               line's value and every name falls back to its internal form.</item>
///         <item>Twelve paint kits share one description_tag, so Ruby, Sapphire, Black Pearl,
///               Emerald and eight Doppler phases all localise to the same two words.</item>
///         <item>Gloves are not in the loot list format weapons use, and each has TWO generations
///               of paint kit name prefix.</item>
///     </list>
/// </summary>
internal sealed partial class SchemaCatalogue : ISchemaCatalogue, IArmoryService
{
    private const string ItemsPath   = "scripts/items/items_game.txt";
    private const string EnglishPath = "resource/csgo_english.txt";
    private const string EconImages  = "panorama/images/econ/default_generated";

    private static readonly string[] Rarities =
        { "consumer", "industrial", "milspec", "restricted", "classified", "covert", "contraband" };

    private static readonly Dictionary<string, string> RarityMap = new(StringComparer.Ordinal)
    {
        ["common"] = "consumer", ["uncommon"] = "industrial", ["rare"] = "milspec",
        ["mythical"] = "restricted", ["legendary"] = "classified", ["ancient"] = "covert",
        ["immortal"] = "contraband",
    };

    /// <summary>
    ///     Which glove wears which finishes, matched on the paint kit's internal name because no
    ///     loot list connects the two. Each glove has an ORIGINAL prefix and a later
    ///     glove_&lt;type&gt;_ one, and mixing them up quietly offers another glove's finishes.
    ///     Broken Fang is the odd one: definition 4725, outside the 5027-5035 run, with paints
    ///     carrying the operation prefix rather than the glove name.
    /// </summary>
    private static readonly (string Glove, Func<string, bool> Owns)[] GloveRules =
    {
        ("studded_bloodhound_gloves", n => n.StartsWith("bloodhound_", StringComparison.Ordinal)
                                           && !n.StartsWith("bloodhound_hydra", StringComparison.Ordinal)),
        ("studded_hydra_gloves",      n => n.StartsWith("bloodhound_hydra", StringComparison.Ordinal)),
        ("studded_brokenfang_gloves", n => n.StartsWith("operation10_", StringComparison.Ordinal)),
        ("sporty_gloves",             n => n.StartsWith("sporty_", StringComparison.Ordinal)
                                           || n.StartsWith("glove_sport", StringComparison.Ordinal)),
        ("slick_gloves",              n => n.StartsWith("slick_", StringComparison.Ordinal)
                                           || n.StartsWith("glove_driver", StringComparison.Ordinal)),
        ("leather_handwraps",         n => n.StartsWith("handwrap", StringComparison.Ordinal)),
        ("motorcycle_gloves",         n => n.StartsWith("motorcycle_", StringComparison.Ordinal)),
        ("specialist_gloves",         n => n.StartsWith("specialist_", StringComparison.Ordinal)
                                           || n.StartsWith("glove_specialist", StringComparison.Ordinal)),
    };

    private readonly IGameFiles               _files;
    private readonly ILogger<SchemaCatalogue> _logger;

    public SchemaCatalogue(IGameFiles files, ILogger<SchemaCatalogue> logger)
    {
        _files  = files;
        _logger = logger;
    }

    public bool Init()
        => true;

    public void Shutdown()
    {
    }

    public Catalogues Build()
    {
        var items = _files.ReadText(ItemsPath);
        var eng   = _files.ReadText(EnglishPath);

        if (items is null || eng is null)
        {
            _logger.LogError("could not read the game schema; catalogues will be empty");

            return new Catalogues(new Dictionary<string, IReadOnlyList<CatFinish>>(),
                                  new Dictionary<string, CatGlove>(),
                                  Array.Empty<CatMusic>(),
                                  new Dictionary<int, string>(),
                                  new Dictionary<int, string>(),
                                  new Dictionary<string, IReadOnlyList<CatFinish>>());
        }

        var loc    = Localisation(eng);
        var kits   = PaintKits(items);
        var rarity = PaintKitRarity(items);
        var images = ImageStems();

        var weapons       = Weapons(kits, rarity, loc, images);
        var gloves        = Gloves(items, kits, loc);
        var music         = Music(items, loc);
        var (names, imgs) = Items(items, loc);
        var stickers      = Stickers(items, loc);

        _logger.LogInformation(
            "schema: {w} weapons / {f} finishes, {g} gloves / {gf} finishes, {m} music kits, "
            + "{i} items, {sc} sticker collections / {s} stickers",
            weapons.Count, weapons.Values.Sum(v => v.Count),
            gloves.Count, gloves.Values.Sum(v => v.Finishes.Count), music.Count, names.Count,
            stickers.Count, stickers.Values.Sum(v => v.Count));

        return new Catalogues(weapons, gloves, music, names, imgs, stickers);
    }

    /// <summary>
    ///     Token to display string. Anchored PER LINE: a greedy character class spans newlines,
    ///     which mis-pairs every entry so the next line's key reads as this line's value.
    /// </summary>
    private static Dictionary<string, string> Localisation(string eng)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in LocLine().Matches(eng))
        {
            map[m.Groups[1].Value] = m.Groups[2].Value;
        }

        return map;
    }

    /// <summary>Paint kit name to index and tag, merged across EVERY paint_kits block.</summary>
    private static Dictionary<string, (int Index, string Tag)> PaintKits(string items)
    {
        var map = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase);

        foreach (Match block in NamedBlock("paint_kits").Matches(items))
        {
            foreach (Match e in Entry().Matches(block.Groups[1].Value))
            {
                var name = Field(e.Groups[2].Value, "name");

                if (name.Length > 0 && int.TryParse(e.Groups[1].Value, out var idx))
                {
                    map[name] = (idx, Field(e.Groups[2].Value, "description_tag").TrimStart('#'));
                }
            }
        }

        return map;
    }

    private static Dictionary<string, string> PaintKitRarity(string items)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match block in NamedBlock("paint_kits_rarity").Matches(items))
        {
            foreach (Match m in Pair().Matches(block.Groups[1].Value))
            {
                map[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }

        return map;
    }

    /// <summary>
    ///     Every econ image that exists, minus the suffix the VPK files them under. Enumerating
    ///     the ART rather than the loot lists is deliberate: a loot list misses skins that were
    ///     never in a case, and anything with a rendered image is displayable.
    /// </summary>
    private HashSet<string> ImageStems()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string suffix = "_png.vtex_c";

        foreach (var file in _files.List(EconImages))
        {
            set.Add(file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        ? file[..^suffix.Length]
                        : file);
        }

        return set;
    }

    /// <summary>
    ///     Weapon to finishes, enumerated from the ART. A stem reads
    ///     weapon_&lt;weapon&gt;_&lt;paintkit&gt;_light and both halves contain underscores, so every
    ///     split is tried and the longest weapon half that names a real paint kit wins.
    /// </summary>
    private Dictionary<string, IReadOnlyList<CatFinish>> Weapons(
        Dictionary<string, (int Index, string Tag)>      kits,
        Dictionary<string, string>                       rarity,
        Dictionary<string, string>                       loc,
        HashSet<string>                                  images)
    {
        var cat = new Dictionary<string, List<CatFinish>>(StringComparer.OrdinalIgnoreCase);
        const string tail = "_light";

        foreach (var stem in images)
        {
            if (!stem.EndsWith(tail, StringComparison.OrdinalIgnoreCase)
                || !stem.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = stem[..^tail.Length].Split('_');

            string? weapon = null, paint = null;

            for (var i = 2; i < parts.Length; i++)
            {
                var p = string.Join('_', parts[i..]);

                if (kits.ContainsKey(p))
                {
                    weapon = string.Join('_', parts[..i]);
                    paint  = p;

                    break;
                }
            }

            if (weapon is null || paint is null)
            {
                continue;
            }

            var (idx, tag) = kits[paint];
            var name = Display(tag, paint, loc);

            if (name.Length == 0)
            {
                continue;
            }

            // Knives take their grade from the ITEM, not the paint: every knife is Covert, so the
            // paint's own rarity would colour the row wrongly.
            var grade = weapon.Contains("knife", StringComparison.Ordinal)
                        || weapon.Contains("bayonet", StringComparison.Ordinal)
                ? "covert"
                : RarityMap.GetValueOrDefault(rarity.GetValueOrDefault(paint, "default"), "milspec");

            cat.TryAdd(weapon, new List<CatFinish>());
            cat[weapon].Add(new CatFinish(idx, name + Variant(paint),
                                          Array.IndexOf(Rarities, grade), paint));
        }

        foreach (var list in cat.Values)
        {
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        return cat.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<CatFinish>) kv.Value,
                                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     What tells two identically named finishes apart. Twelve paint kits share ONE
    ///     description_tag, so Ruby, Sapphire, Black Pearl, Emerald and all eight Doppler phases
    ///     localise to just Doppler or Gamma Doppler, and a knife shows seven identical rows. The
    ///     distinguishing part exists only in the kit's internal name.
    /// </summary>
    private static string Variant(string paint)
    {
        var m = VariantRe().Match("_" + paint + "_");

        if (!m.Success)
        {
            return "";
        }

        if (m.Groups[1].Success)
        {
            return " Phase " + m.Groups[1].Value;
        }

        var word = m.Groups[2].Value;

        return word == "blackpearl"
            ? " Black Pearl"
            : " " + char.ToUpperInvariant(word[0]) + word[1..];
    }

    private static string Display(string tag, string paint, Dictionary<string, string> loc)
        => loc.TryGetValue(tag, out var byTag) && byTag.Length > 0
            ? byTag
            : loc.GetValueOrDefault("PaintKit_" + paint + "_Tag", "");

    /// <summary>Gloves, matched by paint kit prefix because no loot list connects them.</summary>
    private static Dictionary<string, CatGlove> Gloves(string items,
        Dictionary<string, (int Index, string Tag)>                kits,
        Dictionary<string, string>                                 loc)
    {
        // only hands_paintable can wear a finish; plain hands is the team default
        var defs = new Dictionary<string, (int Def, string Tag)>(StringComparer.Ordinal);

        foreach (Match e in Entry().Matches(items))
        {
            var body = e.Groups[2].Value;

            if (Field(body, "prefab") != "hands_paintable")
            {
                continue;
            }

            var name = Field(body, "name");

            if (name.Length > 0 && int.TryParse(e.Groups[1].Value, out var idx))
            {
                defs[name] = (idx, Field(body, "item_name").TrimStart('#'));
            }
        }

        var cat = new Dictionary<string, CatGlove>(StringComparer.Ordinal);

        foreach (var (glove, owns) in GloveRules)
        {
            if (!defs.TryGetValue(glove, out var d))
            {
                continue;
            }

            var finishes = kits.Where(k => owns(k.Key) && !defs.ContainsKey(k.Key))
                               .Select(k =>
                               {
                                   var n = Display(k.Value.Tag, k.Key, loc);

                                   return new CatFinish(k.Value.Index,
                                                        n.Length > 0 ? n : k.Key, -1, k.Key);
                               })
                               .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                               .ToList();

            if (finishes.Count > 0)
            {
                cat[glove] = new CatGlove(d.Def, loc.GetValueOrDefault(d.Tag, glove), finishes);
            }
        }

        return cat;
    }

    /// <summary>
    ///     Music kits live in their own block with their own ids. They are NOT item definitions:
    ///     the whole schema holds two musickit items and neither of them is a kit.
    /// </summary>
    private static List<CatMusic> Music(string items, Dictionary<string, string> loc)
    {
        var list = new List<CatMusic>();

        foreach (Match block in NamedBlock("music_definitions").Matches(items))
        {
            foreach (Match e in Entry().Matches(block.Groups[1].Value))
            {
                var body = e.Groups[2].Value;
                var name = Field(body, "name");
                var img  = Field(body, "image_inventory");

                if (name.Length == 0 || img.Length == 0
                    || !int.TryParse(e.Groups[1].Value, out var id))
                {
                    continue;
                }

                var tag = Field(body, "loc_name").TrimStart('#');

                list.Add(new CatMusic(id, loc.GetValueOrDefault(tag, name), name, img));
            }
        }

        return list.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Definition index to display name and icon, for pins and anything else equippable.</summary>
    private static (Dictionary<int, string> Names, Dictionary<int, string> Images) Items(
        string items, Dictionary<string, string> loc)
    {
        var names  = new Dictionary<int, string>();
        var images = new Dictionary<int, string>();

        foreach (Match e in Entry().Matches(items))
        {
            var body = e.Groups[2].Value;
            var tag  = Field(body, "item_name").TrimStart('#');
            var img  = Field(body, "image_inventory");

            if (tag.Length == 0 || img.Length == 0
                || !int.TryParse(e.Groups[1].Value, out var idx))
            {
                continue;
            }

            names[idx]  = loc.GetValueOrDefault(tag, tag);
            images[idx] = img;
        }

        return (names, images);
    }

    /// <summary>
    ///     Stickers, grouped by collection.
    ///     <br /><br />
    ///     items_game.txt contains FIFTY FIVE separate sticker_kits blocks. Reading only the first
    ///     yields 995 stickers instead of 11676, which looks like a working catalogue and is
    ///     missing five sixths of it.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<CatFinish>> Stickers(
        string items, Dictionary<string, string> loc)
    {
        var byCollection = new Dictionary<string, List<CatFinish>>(StringComparer.OrdinalIgnoreCase);

        foreach (Match block in NamedBlock("sticker_kits").Matches(items))
        {
            foreach (Match e in Entry().Matches(block.Groups[1].Value))
            {
                var body = e.Groups[2].Value;
                var name = Field(body, "name");

                if (name.Length == 0 || !int.TryParse(e.Groups[1].Value, out var id))
                {
                    continue;
                }

                var tag   = Field(body, "item_name").TrimStart('#');
                var label = loc.GetValueOrDefault(tag, name);

                // the collection is the leading token: tournament capsules read like
                // "cologne2016_team_dignitas", community sheets like "community02_foo"
                var cut        = name.IndexOf('_');
                var collection = cut > 0 ? name[..cut] : "misc";

                byCollection.TryAdd(collection, new List<CatFinish>());
                byCollection[collection].Add(new CatFinish(id, label, -1, name));
            }
        }

        foreach (var list in byCollection.Values)
        {
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        return byCollection.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<CatFinish>) kv.Value,
                                         StringComparer.OrdinalIgnoreCase);
    }

    private static string Field(string body, string key)
    {
        var m = Regex.Match(body, "\"" + key + "\"\\s*\"([^\"]*)\"");

        return m.Success ? m.Groups[1].Value : "";
    }

    private static Regex NamedBlock(string name)
        => new("\"" + name + "\"\\s*\\n\\s*\\{(.*?)\\n\\t\\}", RegexOptions.Singleline);

    [GeneratedRegex("^\\s*\"([^\"]+)\"\\s+\"([^\"]*)\"\\s*$", RegexOptions.Multiline)]
    private static partial Regex LocLine();

    [GeneratedRegex("\\n\\t\\t\"(\\d+)\"\\s*\\n\\t\\t\\{(.*?)\\n\\t\\t\\}", RegexOptions.Singleline)]
    private static partial Regex Entry();

    [GeneratedRegex("\"([a-zA-Z0-9_-]+)\"\\s*\"([a-zA-Z0-9_-]+)\"")]
    private static partial Regex Pair();

    [GeneratedRegex("_(?:gamma_)?doppler_phase(\\d)|_(ruby|sapphire|emerald|blackpearl)_")]
    private static partial Regex VariantRe();
}
