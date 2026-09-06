using EmotePreviewer.Core.Rage;

namespace EmotePreviewer.Core.Gta;

/// <summary>How a ped's drawables are stored in the archives.</summary>
public enum PedStorage
{
    /// <summary>One <c>&lt;ped&gt;.ydd</c> (all parts) + <c>&lt;ped&gt;.ytd</c> + <c>&lt;ped&gt;.yft</c>.</summary>
    Component,
    /// <summary>A folder <c>&lt;ped&gt;/</c> with <c>uppr_000_u.ydd</c> … and one <c>.ytd</c> per texture (freemode, animals, many DLC peds).</summary>
    Folder,
}

/// <summary>A drawable dictionary file of a ped, parsed from its name (<c>uppr_003_r</c>).</summary>
/// <param name="Slot">Component slot (<c>uppr</c>, <c>lowr</c>, …).</param>
/// <param name="Number">Variation number.</param>
/// <param name="Race">True for <c>_r</c> (race-specific textures), false for <c>_u</c> (universal).</param>
public sealed record PedComponentFile(string Slot, int Number, bool Race)
{
    public string FileName => $"{Slot}_{Number:000}_{(Race ? "r" : "u")}";
}

/// <summary>Naming conventions of ped models, component files and their textures.</summary>
public static class PedNaming
{
    /// <summary>Component slots in the order the game numbers them (component id 0–11).</summary>
    public static readonly string[] Slots = { "head", "berd", "hair", "uppr", "lowr", "hand", "feet", "teef", "accs", "task", "decl", "jbib" };

    static readonly HashSet<string> SlotSet = new(Slots, StringComparer.OrdinalIgnoreCase);

    /// <summary>Name prefixes of ped models, with the category shown in the picker.</summary>
    static readonly (string prefix, string category)[] Prefixes =
    {
        ("a_c_", "animal"), ("a_f_", "ambient"), ("a_m_", "ambient"), ("s_f_", "service"), ("s_m_", "service"),
        ("g_f_", "gang"), ("g_m_", "gang"), ("u_f_", "unique"), ("u_m_", "unique"), ("csb_", "cutscene"), ("cs_", "cutscene"),
        ("ig_", "story"), ("player_", "story"), ("mp_", "multiplayer"),
    };

    /// <summary>Category of a ped name by prefix, or null when the name does not look like a ped.</summary>
    public static string? Category(string pedName)
    {
        foreach (var (prefix, category) in Prefixes)
            if (pedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return category;
        return null;
    }

    /// <summary>Parses <c>uppr_003_r</c> (optionally prefixed with the ped name and an underscore).</summary>
    public static PedComponentFile? ParseFile(string fileName, string? pedName = null)
    {
        var s = fileName;
        if (pedName != null && s.StartsWith(pedName + "_", StringComparison.OrdinalIgnoreCase)) s = s[(pedName.Length + 1)..];
        var parts = s.Split('_');
        if (parts.Length != 3 || !SlotSet.Contains(parts[0]) || !int.TryParse(parts[1], out var number)) return null;
        if (!string.Equals(parts[2], "r", StringComparison.OrdinalIgnoreCase) && !string.Equals(parts[2], "u", StringComparison.OrdinalIgnoreCase)) return null;
        return new PedComponentFile(parts[0].ToLowerInvariant(), number, string.Equals(parts[2], "r", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parses a diffuse texture name <c>uppr_diff_003_a_whi</c> into slot and variation number.</summary>
    public static (string slot, int number)? ParseDiffuse(string textureName)
    {
        var parts = textureName.Split('_');
        if (parts.Length < 4 || !SlotSet.Contains(parts[0]) || !string.Equals(parts[1], "diff", StringComparison.OrdinalIgnoreCase)) return null;
        return int.TryParse(parts[2], out var number) ? (parts[0].ToLowerInvariant(), number) : null;
    }

    /// <summary>
    /// Hash → name table for the drawable names a component ped's <c>.ydd</c> may use (<c>head_000_r</c>, optionally
    /// prefixed with the ped name). Component dictionaries key their drawables by hash only.
    /// </summary>
    public static Dictionary<uint, string> DrawableNameCandidates(string pedName, int maxNumber = 63)
    {
        var table = new Dictionary<uint, string>();
        foreach (var slot in Slots)
            for (int n = 0; n <= maxNumber; n++)
                foreach (var suffix in new[] { "r", "u" })
                {
                    var name = $"{slot}_{n:000}_{suffix}";
                    table.TryAdd(JenkinsHash.HashLower(name), name);
                    table.TryAdd(JenkinsHash.HashLower(pedName + "_" + name), name);
                }
        return table;
    }
}
