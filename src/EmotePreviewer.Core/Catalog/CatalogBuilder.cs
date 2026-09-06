namespace EmotePreviewer.Core.Catalog;

public sealed class EmoteCatalog
{
    public static readonly EmoteCatalog Empty = new()
    {
        Entries = Array.Empty<EmoteEntry>(),
        CustomYcds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        CustomDrawables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Sources = Array.Empty<ResourceSource>(),
        Warnings = Array.Empty<string>(),
    };

    public required IReadOnlyList<EmoteEntry> Entries { get; init; }
    /// <summary>Dictionary name (case-insensitive) to .ycd path shipped in a resource. Earlier sources win on collisions.</summary>
    public required IReadOnlyDictionary<string, string> CustomYcds { get; init; }
    /// <summary>Prop model name (case-insensitive) to .ydr path shipped in a resource (custom props). Earlier sources win.</summary>
    public required IReadOnlyDictionary<string, string> CustomDrawables { get; init; }
    /// <summary>The sources the catalog was built from, in precedence order.</summary>
    public required IReadOnlyList<ResourceSource> Sources { get; init; }
    /// <summary>Non-fatal problems found while loading (unknown layout, Lua errors).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    Dictionary<string, EmoteEntry>? _byId;

    public EmoteEntry? FindById(string id)
    {
        _byId ??= Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        return _byId.TryGetValue(id, out var e) ? e : null;
    }

    public IEnumerable<EmoteEntry> Search(string text) =>
        Entries.Where(e => e.Command.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || e.Label.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || (e.Dictionary?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (e.Clip?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
}

public static class CatalogBuilder
{
    /// <summary>
    /// Builds the catalog from the given resources. Entries keep the order of <paramref name="sources"/>; when two
    /// resources ship a .ycd with the same dictionary name, the earlier resource wins. Resources whose layout is not
    /// recognised produce a warning instead of an exception.
    /// </summary>
    public static EmoteCatalog Build(IReadOnlyList<ResourceSource> sources)
    {
        var entries = new List<EmoteEntry>();
        var ycds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ydrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var root = ResourceSource.ResolveRoot(source.Path);
            var kind = ResourceSource.DetectKind(root);
            List<EmoteEntry> loaded;
            try
            {
                loaded = kind switch
                {
                    ResourceKind.RpEmotes => RpEmotesLoader.Load(source.Id, root),
                    ResourceKind.Scully => ScullyLoader.Load(source.Id, root),
                    _ => throw new InvalidDataException("unrecognised resource layout (expected types.lua + client/AnimationList.lua, or shared/data/emotes/)"),
                };
            }
            catch (Exception ex)
            {
                warnings.Add($"{source.Id} ({source.Path}): {ex.Message}");
                continue;
            }

            foreach (var e in loaded)
            {
                var baseId = $"{e.Source}/{e.Category}/{e.Command}";
                var id = baseId;
                for (int n = 2; !ids.Add(id); n++) id = $"{baseId}#{n}";
                e.Id = id;
            }
            ResolvePartners(loaded);
            entries.AddRange(loaded);

            if (Directory.Exists(root))
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.ycd", SearchOption.AllDirectories))
                    ycds.TryAdd(Path.GetFileNameWithoutExtension(f), f);
                foreach (var f in Directory.EnumerateFiles(root, "*.ydr", SearchOption.AllDirectories))
                    ydrs.TryAdd(Path.GetFileNameWithoutExtension(f), f);
            }
        }

        foreach (var e in entries)
        {
            if (e.HasClip && ycds.TryGetValue(e.Dictionary!, out var p))
            {
                e.IsCustom = true;
                e.CustomYcdPath = p;
            }
        }
        return new EmoteCatalog { Entries = entries, CustomYcds = ycds, CustomDrawables = ydrs, Sources = sources, Warnings = warnings };
    }

    /// <summary>
    /// Links the two halves of shared emotes: the partner command is looked up in the same source and category (the
    /// first entry with that command, which is also how the menus look it up). Unresolved names keep their
    /// <see cref="EmoteEntry.PartnerCommand"/> with a null <see cref="EmoteEntry.PartnerId"/>.
    /// </summary>
    static void ResolvePartners(List<EmoteEntry> loaded)
    {
        Dictionary<(string category, string command), EmoteEntry>? byCommand = null;
        foreach (var e in loaded)
        {
            if (e.PartnerCommand == null) continue;
            if (byCommand == null)
            {
                byCommand = new Dictionary<(string, string), EmoteEntry>();
                foreach (var x in loaded) byCommand.TryAdd((x.Category.ToLowerInvariant(), x.Command.ToLowerInvariant()), x);
            }
            e.PartnerId = byCommand.TryGetValue((e.Category.ToLowerInvariant(), e.PartnerCommand.ToLowerInvariant()), out var p) ? p.Id : null;
        }
    }

    /// <summary>
    /// Convenience for tools: every recognised resource folder directly under <paramref name="dataRoot"/> becomes a
    /// source whose id is the folder name (alphabetical order).
    /// </summary>
    public static EmoteCatalog BuildFromDataRoot(string dataRoot)
    {
        var sources = Directory.Exists(dataRoot)
            ? Directory.GetDirectories(dataRoot)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Where(d => ResourceSource.DetectKind(ResourceSource.ResolveRoot(d)) != ResourceKind.Unknown)
                .Select(d => new ResourceSource(ResourceSource.MakeId(Path.GetFileName(d)), d))
                .ToList()
            : new List<ResourceSource>();
        return Build(sources);
    }
}
