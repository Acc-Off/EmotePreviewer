using System.Text.RegularExpressions;

namespace EmotePreviewer.Core.Catalog;

/// <summary>Where a resource came from.</summary>
public enum ResourceOrigin
{
    /// <summary>A folder the user pointed at (possibly customised).</summary>
    Folder,
    /// <summary>Downloaded by the app from GitHub into the data directory.</summary>
    GitHub,
}

/// <summary>Layout of an emote resource, detected from its files.</summary>
public enum ResourceKind
{
    Unknown,
    /// <summary>
    /// rpemotes / rpemotes-reborn: <c>client/AnimationList.lua</c> filling the global <c>RP</c> (<c>types.lua</c> is
    /// optional; the legacy rpemotes has none and uses boolean options only).
    /// </summary>
    RpEmotes,
    /// <summary>dpemotes: <c>Client/AnimationList.lua</c> filling the global <c>DP</c> (same layout otherwise).</summary>
    DpEmotes,
    /// <summary>scully_emotemenu: <c>shared/data/emotes/*.lua</c>.</summary>
    Scully,
}

/// <summary>An emote resource the catalog is built from. <see cref="Id"/> becomes <see cref="EmoteEntry.Source"/>.</summary>
public sealed record ResourceSource(string Id, string Path, ResourceOrigin Origin = ResourceOrigin.Folder, string? Ref = null)
{
    /// <summary>Detects the resource layout from the folder contents.</summary>
    public static ResourceKind DetectKind(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return ResourceKind.Unknown;
        if (FindAnimationList(folder) is { } list)
            return DeclaresDpGlobal(list) ? ResourceKind.DpEmotes : ResourceKind.RpEmotes;
        if (Directory.Exists(System.IO.Path.Combine(folder, "shared", "data", "emotes")))
            return ResourceKind.Scully;
        return ResourceKind.Unknown;
    }

    /// <summary>Short name of a kind as used by the API and the settings UI.</summary>
    public static string KindName(ResourceKind kind) => kind switch
    {
        ResourceKind.RpEmotes => "rpemotes",
        ResourceKind.DpEmotes => "dpemotes",
        ResourceKind.Scully => "scully",
        _ => "unknown",
    };

    /// <summary>
    /// <c>client/AnimationList.lua</c> of an rpemotes-family resource, or null. The folder is matched without regard to
    /// case because dpemotes ships <c>Client/</c> and resources are also used on case-sensitive servers.
    /// </summary>
    public static string? FindAnimationList(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            if (!string.Equals(System.IO.Path.GetFileName(dir), "client", StringComparison.OrdinalIgnoreCase)) continue;
            var file = System.IO.Path.Combine(dir, "AnimationList.lua");
            if (File.Exists(file)) return file;
        }
        return null;
    }

    static readonly Regex DpGlobal = new(@"^\s*DP\s*=\s*\{", RegexOptions.Multiline);

    /// <summary>dpemotes lists start with <c>DP = {}</c>; rpemotes lists with <c>RP = {}</c> (after a comment banner).</summary>
    static bool DeclaresDpGlobal(string animationList)
    {
        try
        {
            using var reader = new StreamReader(animationList);
            var buffer = new char[4096];
            var read = reader.Read(buffer, 0, buffer.Length);
            return DpGlobal.IsMatch(new string(buffer, 0, read));
        }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Some archives (GitHub zips) unpack into a single sub-folder; if <paramref name="folder"/> itself is not a resource
    /// but its only sub-folder is, that sub-folder is returned. Otherwise <paramref name="folder"/> is returned unchanged.
    /// </summary>
    public static string ResolveRoot(string folder)
    {
        if (DetectKind(folder) != ResourceKind.Unknown || !Directory.Exists(folder)) return folder;
        var subs = Directory.GetDirectories(folder);
        if (subs.Length == 1 && DetectKind(subs[0]) != ResourceKind.Unknown) return subs[0];
        return folder;
    }

    /// <summary>Turns a folder or repository name into an id usable in URLs and file names.</summary>
    public static string MakeId(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        var id = new string(chars).Trim('-', '.');
        return id.Length == 0 ? "resource" : id;
    }
}
