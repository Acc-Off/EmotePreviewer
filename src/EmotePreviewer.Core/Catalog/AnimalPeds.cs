namespace EmotePreviewer.Core.Catalog;

/// <summary>
/// Which animal ped an animal emote should play on. The game names animal dictionaries <c>creatures@&lt;animal&gt;@…</c>;
/// resources add hints of their own (scully <c>PedTypes</c>, rpemotes <c>AnimalEmote</c>).
/// </summary>
public static class AnimalPeds
{
    /// <summary><c>creatures@&lt;key&gt;@</c> → ped model.</summary>
    static readonly Dictionary<string, string> ByCreature = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rottweiler"] = "a_c_rottweiler", ["retriever"] = "a_c_retriever", ["husky"] = "a_c_husky", ["shepherd"] = "a_c_shepherd",
        ["chop"] = "a_c_chop", ["pug"] = "a_c_pug", ["poodle"] = "a_c_poodle", ["westy"] = "a_c_westy",
        ["cat"] = "a_c_cat_01", ["rabbit"] = "a_c_rabbit_01", ["pig"] = "a_c_pig", ["boar"] = "a_c_boar", ["deer"] = "a_c_deer",
        ["coyote"] = "a_c_coyote", ["mtlion"] = "a_c_mtlion", ["cow"] = "a_c_cow", ["hen"] = "a_c_hen", ["chickenhawk"] = "a_c_chickenhawk",
        ["cormorant"] = "a_c_cormorant", ["crow"] = "a_c_crow", ["seagull"] = "a_c_seagull", ["pigeon"] = "a_c_pigeon", ["rat"] = "a_c_rat",
        ["fish"] = "a_c_fish", ["dolphin"] = "a_c_dolphin", ["killerwhale"] = "a_c_killerwhale", ["sharkhammer"] = "a_c_sharkhammer",
        ["sharktiger"] = "a_c_sharktiger", ["stingray"] = "a_c_stingray", ["humpback"] = "a_c_humpback", ["chimp"] = "a_c_chimp",
        ["rhesus"] = "a_c_rhesus", ["panther"] = "a_c_panther",
    };

    /// <summary>scully <c>PedTypes</c> → representative ped.</summary>
    static readonly Dictionary<string, string> ByPedType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["big_dogs"] = "a_c_rottweiler", ["small_dogs"] = "a_c_pug", ["cats"] = "a_c_cat_01", ["birds"] = "a_c_seagull",
        ["marine_mammals"] = "a_c_dolphin", ["rodents"] = "a_c_rat", ["land_mammals"] = "a_c_deer", ["aquatic_animals"] = "a_c_fish",
    };

    public const string DefaultBigDog = "a_c_rottweiler";
    public const string DefaultSmallDog = "a_c_pug";

    /// <summary>Ped for a <c>creatures@…</c> dictionary name, or null when the name is not one.</summary>
    public static string? ForDictionary(string? dictionary)
    {
        if (dictionary == null || !dictionary.StartsWith("creatures@", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = dictionary[10..];
        var at = rest.IndexOf('@');
        var key = at >= 0 ? rest[..at] : rest;
        return ByCreature.TryGetValue(key, out var ped) ? ped : null;
    }

    /// <summary>
    /// For entries a resource flags as animal-related (rpemotes flags both halves of a human–animal pair): whether the
    /// dictionary is the animal's side. Human sides use the game's <c>anim@</c> packs or name the human actor.
    /// </summary>
    public static bool LooksAnimal(string? dictionary)
    {
        if (string.IsNullOrEmpty(dictionary)) return false;
        var segments = dictionary.Split('@');
        if (segments.Any(seg => seg is "hooman" or "male" or "female" or "player" or "human")) return false;
        if (segments.Any(seg => ByCreature.ContainsKey(seg) || seg.Contains("dog", StringComparison.OrdinalIgnoreCase) || seg.Contains("cat", StringComparison.OrdinalIgnoreCase)))
            return true;
        return !dictionary.StartsWith("anim@", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ped for the first recognised scully ped type, or null.</summary>
    public static string? ForPedTypes(IEnumerable<string> pedTypes)
    {
        foreach (var t in pedTypes)
            if (ByPedType.TryGetValue(t, out var ped)) return ped;
        return null;
    }

    /// <summary>
    /// Best guess for an animal entry: the dictionary name, then the resource's ped types, then a dog (small when the
    /// names hint at one) — every animal emote in the default resources is a dog or cat emote.
    /// </summary>
    public static string Resolve(string? dictionary, IEnumerable<string> pedTypes, string? clip = null)
    {
        var ped = ForDictionary(dictionary) ?? ForPedTypes(pedTypes);
        if (ped != null) return ped;
        var text = (dictionary ?? "") + " " + (clip ?? "");
        if (text.Contains("cat", StringComparison.OrdinalIgnoreCase) && !text.Contains("catch", StringComparison.OrdinalIgnoreCase)) return "a_c_cat_01";
        return text.Contains("little", StringComparison.OrdinalIgnoreCase) || text.Contains("pug", StringComparison.OrdinalIgnoreCase) || text.Contains("small", StringComparison.OrdinalIgnoreCase)
            ? DefaultSmallDog : DefaultBigDog;
    }
}
