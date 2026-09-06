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
    /// <summary>rpemotes / rpemotes-reborn: <c>types.lua</c> + <c>client/AnimationList.lua</c>.</summary>
    RpEmotes,
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
        if (File.Exists(System.IO.Path.Combine(folder, "client", "AnimationList.lua")) && File.Exists(System.IO.Path.Combine(folder, "types.lua")))
            return ResourceKind.RpEmotes;
        if (Directory.Exists(System.IO.Path.Combine(folder, "shared", "data", "emotes")))
            return ResourceKind.Scully;
        return ResourceKind.Unknown;
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
