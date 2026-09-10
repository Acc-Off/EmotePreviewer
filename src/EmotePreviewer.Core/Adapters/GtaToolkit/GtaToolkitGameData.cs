using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using EmotePreviewer.Core.Gta;
using EmotePreviewer.Core.Model;
using EmotePreviewer.Core.Rage;
using EmotePreviewer.Core.Textures;
using EmotePreviewer.Core.Rage.Anim;
using RageLib.Archives;
using RageLib.GTA5.ArchiveWrappers;
using RageLib.Resources;
using RageLib.Resources.Common;
using RageLib.Resources.GTA5;
using RageLib.Resources.GTA5.PC.Clips;
using RageLib.Resources.GTA5.PC.Clothes;
using RageLib.Resources.GTA5.PC.Drawables;
using RageLib.Resources.GTA5.PC.Fragments;
using RageLib.Resources.GTA5.PC.Textures;
using AnimTrack = EmotePreviewer.Core.Model.AnimTrack;

namespace EmotePreviewer.Core.Adapters.GtaToolkit;

/// <summary>
/// <see cref="IGameDataSource"/> backed by gta-toolkit (RPF archives, RSC7 resources) and the animation
/// decoder in <see cref="EmotePreviewer.Core.Rage.Anim"/>.
/// </summary>
public sealed class GtaToolkitGameData : IGameDataSource
{
    public string GtaFolder { get; }
    public int ClipDictionaryCount => _ycd.Count;
    public int DrawableCount => _ydr.Count;
    public int ArchiveCount => _archives.Count;

    /// <summary>A resource entry inside an archive, plus the path it came from (for diagnostics).</summary>
    public sealed record ArchiveEntry(IArchiveResourceFile File, string ArchivePath, string DirectoryPath)
    {
        /// <summary>e.g. <c>updated\dlcpacks\patchday24ng\dlc.rpfdnim\ingame\clip_anim@.rpfoo.ycd</c></summary>
        public string FullPath => ArchivePath + "\\" + DirectoryPath + File.Name;
    }

    readonly Dictionary<uint, ArchiveEntry> _ycd = new();
    readonly Dictionary<uint, ArchiveEntry> _yft = new();
    readonly Dictionary<uint, ArchiveEntry> _ydr = new();
    readonly Dictionary<uint, ArchiveEntry> _ydd = new();
    /// <summary>"folder/name" (lower-case, no extension) → .ydd entry, for ped components whose file names repeat per ped.</summary>
    readonly Dictionary<string, ArchiveEntry> _yddByFolder = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<uint, ArchiveEntry> _ytd = new();
    readonly Dictionary<string, ArchiveEntry> _ytdByFolder = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Cloth dictionaries (<c>.yld</c>): the ped drawables whose cloth parts the game simulates, by name hash and by "folder/name".</summary>
    readonly Dictionary<uint, ArchiveEntry> _yld = new();
    readonly Dictionary<string, ArchiveEntry> _yldByFolder = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>clip_sets.ymt files in archive order (the base game's, then the update's, which supersedes it).</summary>
    readonly List<(IArchiveBinaryFile File, string Path)> _clipSetFiles = new();
    ClipSetTable? _clipSets;
    /// <summary>Dictionary hash → clip hashes, filled while resolving clip sets so each dictionary is decoded once.</summary>
    readonly Dictionary<uint, HashSet<uint>?> _clipSetDictionaryClips = new();
    /// <summary>Folder names that contain component .ydd files (folder-type peds), lower-case.</summary>
    readonly Dictionary<string, List<string>> _pedFolders = new(StringComparer.OrdinalIgnoreCase);
    readonly List<IDisposable> _archives = new();
    readonly Action<string>? _log;
    readonly Action<string>? _error;

    GtaToolkitGameData(string gtaFolder, Action<string>? log, Action<string>? error)
    {
        GtaFolder = gtaFolder; _log = log; _error = error;
    }

    /// <summary>
    /// Installs the user-provided keys (see <see cref="GtaKeys.Install"/>), then indexes every .ycd/.yft in the base
    /// archives, update/*.rpf and update/x64/dlcpacks (in dlclist.xml order). Later archives override earlier ones.
    /// </summary>
    /// <param name="keysFolder">Explicit key folder (<c>--keys</c>); null = environment variable / default folder.</param>
    /// <param name="progress">Called after each top-level archive with (archives done, archives total).</param>
    /// <param name="cancellation">Aborts the scan; the partially built instance is disposed and <see cref="OperationCanceledException"/> is thrown.</param>
    public static GtaToolkitGameData Open(string gtaFolder, string? keysFolder = null, Action<string>? log = null, Action<string>? error = null,
        Action<int, int>? progress = null, CancellationToken cancellation = default)
    {
        if (!File.Exists(Path.Combine(gtaFolder, "GTA5.exe"))) throw new FileNotFoundException("GTA5.exe not found in " + gtaFolder);
        var gd = new GtaToolkitGameData(gtaFolder, log, error);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!GtaKeys.IsInstalled)
        {
            var used = GtaKeys.Install(keysFolder);
            log?.Invoke($"keys loaded from {used} in {sw.ElapsedMilliseconds} ms");
        }
        sw.Restart();
        try
        {
            var archives = gd.EnumerateTopLevelArchives().ToList();
            for (int i = 0; i < archives.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                gd.IndexArchive(archives[i]);
                progress?.Invoke(i + 1, archives.Count);
            }
        }
        catch
        {
            gd.Dispose();
            throw;
        }
        log?.Invoke($"indexed {gd._archives.Count} archives: {gd._ycd.Count} clip dictionaries, {gd._yft.Count} fragments, {gd._ydr.Count} drawables, {gd._ydd.Count} drawable dictionaries in {sw.ElapsedMilliseconds} ms");
        return gd;
    }

    // ------------------------------------------------------------------ archive scan

    IEnumerable<string> EnumerateTopLevelArchives()
    {
        var list = new List<string>();
        list.AddRange(Directory.GetFiles(GtaFolder, "*.rpf").OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        var update = Path.Combine(GtaFolder, "update");
        if (Directory.Exists(update))
        {
            list.AddRange(Directory.GetFiles(update, "*.rpf").OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            var dlcpacks = Path.Combine(update, "x64", "dlcpacks");
            if (Directory.Exists(dlcpacks))
            {
                var packs = Directory.GetFiles(dlcpacks, "*.rpf", SearchOption.AllDirectories);
                var order = ReadDlcOrder(Path.Combine(update, "update.rpf"));
                int Rank(string p)
                {
                    var rel = Path.GetRelativePath(dlcpacks, p).Replace('\\', '/');
                    var pack = rel.Split('/')[0].ToLowerInvariant();
                    return order.TryGetValue(pack, out var r) ? r : int.MaxValue;
                }
                list.AddRange(packs.OrderBy(Rank).ThenBy(p => p, new NaturalComparer()));
            }
        }
        return list;
    }

    /// <summary>Reads common/data/dlclist.xml from update.rpf to get the DLC load order (pack name → rank).</summary>
    Dictionary<string, int> ReadDlcOrder(string updateRpf)
    {
        var order = new Dictionary<string, int>();
        if (!File.Exists(updateRpf)) return order;
        try
        {
            using var fs = new FileStream(updateRpf, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var arc = RageArchiveWrapper7.Open(fs, Path.GetFileName(updateRpf), leaveOpen: true);
            var file = arc.Root.GetDirectory("common")?.GetDirectory("data")?.GetFile("dlclist.xml") as IArchiveBinaryFile;
            if (file == null) return order;
            using var ms = new MemoryStream();
            file.ExportUncompressed(ms);
            var xml = Encoding.UTF8.GetString(ms.ToArray());
            foreach (Match m in Regex.Matches(xml, @"dlcpacks:[/\\]([^/\\<]+)[/\\]?", RegexOptions.IgnoreCase))
            {
                var name = m.Groups[1].Value.ToLowerInvariant();
                order.TryAdd(name, order.Count);
            }
            _log?.Invoke($"dlclist.xml: {order.Count} packs");
        }
        catch (Exception ex) { _error?.Invoke("dlclist.xml: " + ex.Message); }
        return order;
    }

    void IndexArchive(string path)
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            var arc = RageArchiveWrapper7.Open(fs, Path.GetFileName(path), leaveOpen: false);
            _archives.Add(arc);
            IndexDirectory(arc.Root, Path.GetRelativePath(GtaFolder, path), "");
        }
        catch (Exception ex)
        {
            fs?.Dispose();
            _error?.Invoke($"{path}: {ex.Message}");
        }
    }

    /// <param name="dirPath">Directory inside the archive, "" or "a\" (trailing backslash).</param>
    void IndexDirectory(IArchiveDirectory dir, string archivePath, string dirPath)
    {
        foreach (var file in dir.GetFiles())
        {
            var name = file.Name;
            if (file is IArchiveResourceFile res)
            {
                if (name.EndsWith(".ycd", StringComparison.OrdinalIgnoreCase)) _ycd[JenkinsHash.HashLower(name[..^4])] = new ArchiveEntry(res, archivePath, dirPath);
                else if (name.EndsWith(".yft", StringComparison.OrdinalIgnoreCase)) _yft[JenkinsHash.HashLower(name[..^4])] = new ArchiveEntry(res, archivePath, dirPath);
                else if (name.EndsWith(".ydr", StringComparison.OrdinalIgnoreCase)) _ydr[JenkinsHash.HashLower(name[..^4])] = new ArchiveEntry(res, archivePath, dirPath);
                else if (name.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase))
                {
                    var entry = new ArchiveEntry(res, archivePath, dirPath);
                    _ydd[JenkinsHash.HashLower(name[..^4])] = entry;
                    var folder = LastFolder(dirPath);
                    if (folder.Length > 0)
                    {
                        var key = folder + "/" + name[..^4];
                        if (!_yddByFolder.ContainsKey(key))
                        {
                            if (!_pedFolders.TryGetValue(folder, out var files)) _pedFolders[folder] = files = new List<string>();
                            files.Add(name[..^4]);
                        }
                        _yddByFolder[key] = entry;
                    }
                }
                else if (name.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase))
                {
                    var entry = new ArchiveEntry(res, archivePath, dirPath);
                    _ytd[JenkinsHash.HashLower(name[..^4])] = entry;
                    var folder = LastFolder(dirPath);
                    if (folder.Length > 0) _ytdByFolder[folder + "/" + name[..^4]] = entry;
                }
                else if (name.EndsWith(".yld", StringComparison.OrdinalIgnoreCase))
                {
                    var entry = new ArchiveEntry(res, archivePath, dirPath);
                    _yld[JenkinsHash.HashLower(name[..^4])] = entry;
                    var folder = LastFolder(dirPath);
                    if (folder.Length > 0) _yldByFolder[folder + "/" + name[..^4]] = entry;
                }
            }
            else if (file is IArchiveBinaryFile bin)
            {
                if (name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) OpenNested(bin, archivePath + "\\" + dirPath + name);
                else if (name.Equals("clip_sets.ymt", StringComparison.OrdinalIgnoreCase))
                    _clipSetFiles.Add((bin, archivePath + "\\" + dirPath + name));
            }
        }
        foreach (var sub in dir.GetDirectories()) IndexDirectory(sub, archivePath, dirPath + sub.Name + "\\");
    }

    static string LastFolder(string dirPath)
    {
        var folder = dirPath.TrimEnd('\\');
        var slash = folder.LastIndexOf('\\');
        return slash >= 0 ? folder[(slash + 1)..] : folder;
    }

    void OpenNested(IArchiveBinaryFile bin, string archivePath)
    {
        try
        {
            Stream stream;
            if (bin.IsCompressed || bin.IsEncrypted)
            {
                var ms = new MemoryStream();
                bin.ExportUncompressed(ms);
                ms.Position = 0;
                stream = ms;
            }
            else stream = bin.GetStream();
            var arc = RageArchiveWrapper7.Open(stream, bin.Name, leaveOpen: true);
            _archives.Add(arc);
            IndexDirectory(arc.Root, archivePath, "");
        }
        catch (Exception ex) { _error?.Invoke($"{archivePath}: {ex.Message}"); }
    }

    /// <summary>Paths of the indexed clip_sets.ymt files, in load order.</summary>
    public IReadOnlyList<string> ClipSetFilePaths => _clipSetFiles.Select(f => f.Path).ToList();

    /// <summary>Exports the i-th clip_sets.ymt (decompressed) into memory.</summary>
    public MemoryStream ExportClipSetFile(int index)
    {
        lock (_ioLock)
        {
            var ms = new MemoryStream();
            _clipSetFiles[index].File.ExportUncompressed(ms);
            ms.Position = 0;
            return ms;
        }
    }

    /// <summary>
    /// The merged clip set table (every clip_sets.ymt in load order, later definitions winning). Parsed on first use
    /// (about 0.3 s for the update's 970 KB file); null when the game data has no clip_sets.ymt or it cannot be read.
    /// </summary>
    public ClipSetTable? ClipSets
    {
        get
        {
            lock (_clipSetDictionaryClips)
            {
                if (_clipSets != null || _clipSetFiles.Count == 0) return _clipSets;
                var table = new ClipSetTable();
                for (int i = 0; i < _clipSetFiles.Count; i++)
                {
                    try
                    {
                        using var ms = ExportClipSetFile(i);
                        table.Merge(ClipSetTable.Parse(ms));
                    }
                    catch (Exception ex) { _error?.Invoke($"{_clipSetFiles[i].Path}: {ex.Message}"); }
                }
                _log?.Invoke($"clip sets: {table.Count} from {_clipSetFiles.Count} file(s)");
                return _clipSets = table;
            }
        }
    }

    /// <summary>Name of the indexed dictionary with that name hash (from its file name), or null.</summary>
    public string? ClipDictionaryName(uint hash) => _ycd.TryGetValue(hash, out var e) ? e.File.Name[..^4] : null;

    public ClipSetClip? ResolveClipSetClip(string clipSet, string clip)
    {
        var table = ClipSets;
        if (table == null) return null;
        var clipHash = JenkinsHash.HashLower(clip);
        var dictHash = table.ResolveDictionary(JenkinsHash.HashLower(clipSet), clipHash, DictionaryHasClip, out var chain);
        if (dictHash == null) return null;
        var name = ClipDictionaryName(dictHash.Value);
        return name == null ? null : new ClipSetClip(name, clip, chain.Count > 1);
    }

    bool DictionaryHasClip(uint dictHash, uint clipHash)
    {
        HashSet<uint>? clips;
        lock (_clipSetDictionaryClips)
        {
            if (!_clipSetDictionaryClips.TryGetValue(dictHash, out clips))
            {
                clips = null;
                if (_ycd.TryGetValue(dictHash, out var entry))
                {
                    try
                    {
                        using var ms = ExportResource(entry.File);
                        var res = new Resource7<ClipDictionary>();
                        res.Load(ms);
                        clips = new HashSet<uint>();
                        foreach (var (hash, _) in GtResourceHelpers.Entries(res.ResourceData.Clips)) clips.Add(hash);
                    }
                    catch (Exception ex) { _error?.Invoke($"{entry.FullPath}: {ex.Message}"); }
                }
                _clipSetDictionaryClips[dictHash] = clips;
            }
        }
        return clips != null && clips.Contains(clipHash);
    }

    /// <summary>Where a dictionary would be loaded from (for diagnostics / tests).</summary>
    public string? FindClipDictionaryPath(string name) => _ycd.TryGetValue(JenkinsHash.HashLower(name), out var e) ? e.FullPath : null;
    public string? FindSkeletonPath(string name) => _yft.TryGetValue(JenkinsHash.HashLower(name), out var e) ? e.FullPath : null;

    public bool HasClipDictionary(string name) => _ycd.ContainsKey(JenkinsHash.HashLower(name));
    public bool HasSkeleton(string name) => _yft.ContainsKey(JenkinsHash.HashLower(name));
    /// <summary>Props with physics (bottles, bins, cups) are stored as .yft fragments rather than .ydr drawables, so fragments count as drawables too.</summary>
    public string? FindDrawablePath(string name)
    {
        var hash = JenkinsHash.HashLower(name);
        return _ydr.TryGetValue(hash, out var e) ? e.FullPath : _ydd.TryGetValue(hash, out e) ? e.FullPath : _yft.TryGetValue(hash, out e) ? e.FullPath : null;
    }

    public bool HasDrawable(string name)
    {
        var hash = JenkinsHash.HashLower(name);
        return _ydr.ContainsKey(hash) || _ydd.ContainsKey(hash) || _yft.ContainsKey(hash);
    }

    public long? DrawableSize(string name)
    {
        var hash = JenkinsHash.HashLower(name);
        return _ydr.TryGetValue(hash, out var e) ? (long)e.File.Size : _ydd.TryGetValue(hash, out e) ? (long)e.File.Size : _yft.TryGetValue(hash, out e) ? (long)e.File.Size : null;
    }

    /// <summary>Export a skeleton fragment from the indexed GTA archives for external inspection tools.</summary>
    public bool ExportSkeleton(string yftName, string outPath)
    {
        if (!_yft.TryGetValue(JenkinsHash.HashLower(yftName), out var entry)) return false;
        using var ms = ExportResource(entry.File);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        using var outFile = File.Create(outPath);
        ms.CopyTo(outFile);
        return true;
    }

    /// <summary>Size of the archive entry, used together with the path as a cheap ETag for baked clips.</summary>
    public long? ClipDictionarySize(string name) => _ycd.TryGetValue(JenkinsHash.HashLower(name), out var e) ? (long)e.File.Size : null;

    // ------------------------------------------------------------------ loading

    /// <summary>
    /// The archive streams are shared and positional, so exports must not interleave: the app serves HTTP requests and
    /// runs the preview check concurrently, and both end up here.
    /// </summary>
    readonly object _ioLock = new();

    MemoryStream ExportResource(IArchiveResourceFile file)
    {
        lock (_ioLock)
        {
            var ms = new MemoryStream((int)Math.Min(int.MaxValue, file.Size + 16));
            file.Export(ms);
            ms.Position = 0;
            return ms;
        }
    }

    public SkeletonDef? LoadSkeleton(string yftName)
    {
        if (!_yft.TryGetValue(JenkinsHash.HashLower(yftName), out var entry)) return null;
        using var ms = ExportResource(entry.File);
        var res = new Resource7<FragType>();
        res.Load(ms);
        var skel = res.ResourceData?.PrimaryDrawable?.Skeleton ?? throw new InvalidDataException($"{yftName}.yft has no skeleton");
        var src = skel.BoneData?.Bones ?? throw new InvalidDataException($"{yftName}.yft has no bone data");
        var bones = new List<BoneDef>(src.Count);
        for (int i = 0; i < src.Count; i++)
        {
            var b = src[i];
            bones.Add(new BoneDef(i, b.BoneId, b.Name?.Value ?? $"bone{i}", b.ParentIndex, b.Translation, b.Rotation, b.Scale, (BoneDofs)((ushort)b.Flags & (ushort)BoneDofs.All)));
        }
        return new SkeletonDef(yftName, bones);
    }

    /// <summary>Bone table with the raw DOF flags of a skeleton (diagnostics).</summary>
    public List<(int index, string name, ushort tag, BoneFlags flags)>? LoadBoneFlags(string yftName)
    {
        if (!_yft.TryGetValue(JenkinsHash.HashLower(yftName), out var entry)) return null;
        using var ms = ExportResource(entry.File);
        var res = new Resource7<FragType>();
        res.Load(ms);
        var src = res.ResourceData?.PrimaryDrawable?.Skeleton?.BoneData?.Bones;
        if (src == null) return null;
        var list = new List<(int, string, ushort, BoneFlags)>();
        for (int i = 0; i < src.Count; i++) list.Add((i, src[i].Name?.Value ?? $"bone{i}", src[i].BoneId, src[i].Flags));
        return list;
    }

    public IClipDictionary? LoadClipDictionary(string dictionaryName)
    {
        if (!_ycd.TryGetValue(JenkinsHash.HashLower(dictionaryName), out var entry)) return null;
        using var ms = ExportResource(entry.File);
        var res = new Resource7<ClipDictionary>();
        res.Load(ms);
        return new GtClipDictionary(dictionaryName, res.ResourceData);
    }

    /// <summary>
    /// Loads a drawable by model name: a <c>.ydr</c> of that name, or a <c>.ydd</c> of that name whose dictionary contains the
    /// model hash (falling back to its first drawable). Null when nothing is indexed under the name.
    /// </summary>
    public MeshData? LoadDrawable(string modelName)
    {
        var hash = JenkinsHash.HashLower(modelName);
        if (_ydr.TryGetValue(hash, out var entry))
        {
            using var ms = ExportResource(entry.File);
            var res = new Resource7<GtaDrawable>();
            res.Load(ms);
            return MeshExtractor.Extract(res.ResourceData, modelName);
        }
        if (_ydd.TryGetValue(hash, out entry))
        {
            using var ms = ExportResource(entry.File);
            var res = new Resource7<PgDictionary64<GtaDrawable>>();
            res.Load(ms);
            var dict = res.ResourceData;
            var values = dict.Values?.Entries;
            if (values == null || values.Count == 0) return null;
            var hashes = dict.Hashes?.Entries;
            int index = 0;
            if (hashes != null)
                for (int i = 0; i < hashes.Count && i < values.Count; i++)
                    if (hashes[i] == hash) { index = i; break; }
            return MeshExtractor.Extract(values[index], modelName);
        }
        if (_yft.TryGetValue(hash, out entry))
        {
            using var ms = ExportResource(entry.File);
            var res = new Resource7<FragType>();
            res.Load(ms);
            var frag = res.ResourceData;
            var drawable = frag?.PrimaryDrawable ?? frag?.ClothDrawable;
            return drawable == null ? null : MeshExtractor.Extract(drawable, modelName);
        }
        return null;
    }

    /// <summary>
    /// Scans drawable dictionaries (<c>.ydd</c>) for the models they contain and returns model hash → dictionary entry.
    /// Every dictionary has to be decompressed and parsed, so this is slow (seconds to a minute); callers cache the result.
    /// </summary>
    public Dictionary<uint, string> ScanDrawableDictionaries(Func<string, bool>? filter = null, Action<int, int>? progress = null, CancellationToken cancellation = default)
    {
        var result = new Dictionary<uint, string>();
        var entries = _ydd.Where(kv => filter == null || filter(kv.Value.FullPath)).ToList();
        int done = 0;
        foreach (var (ddHash, entry) in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using var ms = ExportResource(entry.File);
                var res = new Resource7<PgDictionary64<GtaDrawable>>();
                res.Load(ms);
                var hashes = res.ResourceData.Hashes?.Entries;
                if (hashes != null)
                {
                    var name = entry.File.Name[..^4];
                    for (int i = 0; i < hashes.Count; i++) result.TryAdd(hashes[i], name);
                }
            }
            catch (Exception ex) { _error?.Invoke($"{entry.FullPath}: {ex.Message}"); }
            progress?.Invoke(++done, entries.Count);
        }
        return result;
    }

    /// <summary>Loads a drawable out of a specific dictionary (found via <see cref="ScanDrawableDictionaries"/>).</summary>
    public MeshData? LoadDrawableFromDictionary(string dictionaryName, string modelName)
    {
        if (!_ydd.TryGetValue(JenkinsHash.HashLower(dictionaryName), out var entry)) return null;
        var hash = JenkinsHash.HashLower(modelName);
        using var ms = ExportResource(entry.File);
        var res = new Resource7<PgDictionary64<GtaDrawable>>();
        res.Load(ms);
        var dict = res.ResourceData;
        var values = dict.Values?.Entries;
        var hashes = dict.Hashes?.Entries;
        if (values == null || hashes == null) return null;
        for (int i = 0; i < hashes.Count && i < values.Count; i++)
            if (hashes[i] == hash) return MeshExtractor.Extract(values[i], modelName);
        return null;
    }

    public int TextureDictionaryCount => _ytd.Count;
    public string? FindTextureDictionaryPath(string name) => _ytd.TryGetValue(JenkinsHash.HashLower(name), out var e) ? e.FullPath : null;
    public bool HasTextureDictionary(string name) => _ytd.ContainsKey(JenkinsHash.HashLower(name));
    public bool HasPedTextureDictionary(string pedFolder, string fileName) => _ytdByFolder.ContainsKey(pedFolder + "/" + fileName);

    /// <summary>Names of the .ytd files in a ped folder (diagnostics / variation discovery).</summary>
    public IEnumerable<string> PedTextureDictionaries(string pedFolder) =>
        _ytdByFolder.Keys.Where(k => k.StartsWith(pedFolder + "/", StringComparison.OrdinalIgnoreCase)).Select(k => k[(pedFolder.Length + 1)..]).Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads a texture dictionary by name (game-wide) or by ped folder + file name.</summary>
    public PgDictionary64<TextureDX11>? LoadTextureDictionary(string name, string? pedFolder = null)
    {
        ArchiveEntry? entry;
        if (pedFolder != null) { if (!_ytdByFolder.TryGetValue(pedFolder + "/" + name, out entry)) return null; }
        else if (!_ytd.TryGetValue(JenkinsHash.HashLower(name), out entry)) return null;
        using var ms = ExportResource(entry.File);
        var res = new Resource7<PgDictionary64<TextureDX11>>();
        res.Load(ms);
        return res.ResourceData;
    }

    /// <summary>Raw gta-toolkit drawable (for texture / shader inspection); null when not indexed.</summary>
    public GtaDrawable? LoadRawDrawable(string modelName)
    {
        if (!_ydr.TryGetValue(JenkinsHash.HashLower(modelName), out var entry)) return null;
        using var ms = ExportResource(entry.File);
        var res = new Resource7<GtaDrawable>();
        res.Load(ms);
        return res.ResourceData;
    }

    /// <summary>Raw drawable dictionary by name (component peds keep all their parts in <c>&lt;ped&gt;.ydd</c>).</summary>
    public PgDictionary64<GtaDrawable>? LoadRawDrawableDictionary(string name)
    {
        if (!_ydd.TryGetValue(JenkinsHash.HashLower(name), out var entry)) return null;
        using var ms = ExportResource(entry.File);
        var res = new Resource7<PgDictionary64<GtaDrawable>>();
        res.Load(ms);
        return res.ResourceData;
    }

    /// <summary>Raw component dictionary of a ped (for texture / shader inspection).</summary>
    public PgDictionary64<GtaDrawable>? LoadRawPedDictionary(string pedFolder, string fileName)
    {
        if (!_yddByFolder.TryGetValue(pedFolder + "/" + fileName, out var entry)) return null;
        using var ms = ExportResource(entry.File);
        var res = new Resource7<PgDictionary64<GtaDrawable>>();
        res.Load(ms);
        return res.ResourceData;
    }

    /// <summary>Indexed entries whose archive path contains <paramref name="substring"/> (diagnostics).</summary>
    public IEnumerable<string> FindEntries(string substring, int limit = 200)
    {
        return _ycd.Values.Concat(_yft.Values).Concat(_ydr.Values).Concat(_ydd.Values).Concat(_yddByFolder.Values).Concat(_ytd.Values).Concat(_ytdByFolder.Values)
            .Select(e => e.FullPath)
            .Where(p => p.Contains(substring, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(limit);
    }

    /// <summary>
    /// Loads a drawable from a ped's component dictionary, e.g. folder <c>mp_m_freemode_01</c>, file <c>uppr_000_u</c>
    /// (the archives keep one folder per ped, so the plain file name is not unique). Returns the first drawable of the
    /// dictionary unless <paramref name="modelName"/> matches one of its hashes.
    /// </summary>
    public MeshData? LoadPedComponent(string pedFolder, string fileName, string? modelName = null)
    {
        if (!_yddByFolder.TryGetValue(pedFolder + "/" + fileName, out var entry)) return null;
        using var ms = ExportResource(entry.File);
        var res = new Resource7<PgDictionary64<GtaDrawable>>();
        res.Load(ms);
        var dict = res.ResourceData;
        var values = dict.Values?.Entries;
        if (values == null || values.Count == 0) return null;
        int index = 0;
        if (modelName != null && dict.Hashes?.Entries is { } hashes)
        {
            var hash = JenkinsHash.HashLower(modelName);
            for (int i = 0; i < hashes.Count && i < values.Count; i++)
                if (hashes[i] == hash) { index = i; break; }
        }
        return MeshExtractor.Extract(values[index], $"{pedFolder}/{fileName}", LoadClothBinding(pedFolder, fileName));
    }

    /// <summary>The cloth binding of a folder ped's component file, or null when it has no <c>.yld</c> (or it is unreadable).</summary>
    public ClothBinding? LoadClothBinding(string pedFolder, string fileName)
    {
        if (!_yldByFolder.TryGetValue(pedFolder + "/" + fileName, out var entry)) return null;
        try
        {
            var dict = LoadClothDictionary(entry);
            var cloth = dict.Values?.Entries?.FirstOrDefault();
            return cloth == null ? null : ToClothBinding(cloth);
        }
        catch (Exception ex) { _error?.Invoke($"{pedFolder}/{fileName}.yld: {ex.Message}"); return null; }
    }

    /// <summary>The cloth bindings of a component ped, keyed by drawable name hash; empty without a <c>&lt;ped&gt;.yld</c>.</summary>
    Dictionary<uint, ClothBinding> LoadClothBindings(string ped)
    {
        var result = new Dictionary<uint, ClothBinding>();
        if (!_yld.TryGetValue(JenkinsHash.HashLower(ped), out var entry)) return result;
        try
        {
            var dict = LoadClothDictionary(entry);
            var values = dict.Values?.Entries;
            var hashes = dict.Hashes?.Entries;
            if (values == null || hashes == null) return result;
            for (int i = 0; i < values.Count && i < hashes.Count; i++)
            {
                var b = values[i] == null ? null : ToClothBinding(values[i]);
                if (b != null) result[hashes[i]] = b;
            }
        }
        catch (Exception ex) { _error?.Invoke($"{ped}.yld: {ex.Message}"); }
        return result;
    }

    PgDictionary64<CharacterCloth> LoadClothDictionary(ArchiveEntry entry)
    {
        using var ms = ExportResource(entry.File);
        var res = new Resource7<PgDictionary64<CharacterCloth>>();
        res.Load(ms);
        return res.ResourceData;
    }

    /// <summary>
    /// Per simulation vertex, the bones it is bound to: <c>BindingInfo</c> holds four weights and indices into
    /// <c>BoneIDMap</c>, whose entries are bone tags.
    /// </summary>
    static ClothBinding? ToClothBinding(CharacterCloth cloth)
    {
        var ctl = cloth.Controller;
        var binding = ctl?.BindingInfo?.Entries;
        var tags = ctl?.BoneIDMap?.Entries;
        if (binding == null || tags == null || binding.Count == 0) return null;
        var bones = new List<IReadOnlyList<(ushort, float)>>(binding.Count);
        for (int v = 0; v < binding.Count; v++)
        {
            var b = binding[v];
            var list = new List<(ushort, float)>(4);
            var weights = new[] { b.Weights.X, b.Weights.Y, b.Weights.Z, b.Weights.W };
            var indices = new[] { b.BlendIndex0, b.BlendIndex1, b.BlendIndex2, b.BlendIndex3 };
            for (int k = 0; k < 4; k++)
            {
                if (weights[k] <= 0 || indices[k] >= tags.Count) continue;
                list.Add(((ushort)tags[(int)indices[k]], weights[k]));
            }
            bones.Add(list);
        }
        return new ClothBinding(binding.Count, bones);
    }

    public bool HasPedComponent(string pedFolder, string fileName) => _yddByFolder.ContainsKey(pedFolder + "/" + fileName);

    /// <summary>Whether a folder ped's component file comes with a cloth dictionary (<c>uppr_000_u.yld</c>): its cloth parts are simulated in-game.</summary>
    public bool HasPedCloth(string pedFolder, string fileName) => _yldByFolder.ContainsKey(pedFolder + "/" + fileName);

    /// <summary>The raw cloth dictionary of a folder ped's component file (diagnostics); null when there is none.</summary>
    public PgDictionary64<CharacterCloth>? LoadRawPedCloth(string pedFolder, string fileName)
    {
        if (!_yldByFolder.TryGetValue(pedFolder + "/" + fileName, out var entry)) return null;
        using var ms = ExportResource(entry.File);
        var res = new Resource7<PgDictionary64<CharacterCloth>>();
        res.Load(ms);
        return res.ResourceData;
    }

    /// <summary>Whether a component ped has a cloth dictionary (<c>&lt;ped&gt;.yld</c>).</summary>
    public bool HasPedCloth(string ped) => _yld.ContainsKey(JenkinsHash.HashLower(ped));

    // ------------------------------------------------------------------ peds

    /// <summary>A ped model found in the archives.</summary>
    public sealed record PedInfo(string Name, PedStorage Storage, string Category, string Path);

    /// <summary>
    /// Every <c>.yft</c> whose name has a ped prefix and whose drawables exist either as a folder of component files or
    /// as a single <c>&lt;ped&gt;.ydd</c> (patch archives keep updated peds outside the <c>peds</c> folders). Sorted by name.
    /// </summary>
    public IReadOnlyList<PedInfo> ListPeds()
    {
        var list = new List<PedInfo>();
        foreach (var (hash, entry) in _yft)
        {
            var name = entry.File.Name[..^4].ToLowerInvariant();
            var category = PedNaming.Category(name);
            if (category == null || name.EndsWith("headtargets", StringComparison.Ordinal)) continue;
            if (_pedFolders.ContainsKey(name)) list.Add(new PedInfo(name, PedStorage.Folder, category, entry.FullPath));
            else if (_ydd.ContainsKey(hash)) list.Add(new PedInfo(name, PedStorage.Component, category, entry.FullPath));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    /// <summary>Storage form of a ped, or null when it is not in the game data.</summary>
    public PedStorage? PedStorageOf(string ped)
    {
        if (_pedFolders.ContainsKey(ped)) return PedStorage.Folder;
        var hash = JenkinsHash.HashLower(ped);
        return _ydd.ContainsKey(hash) && _yft.ContainsKey(hash) ? PedStorage.Component : null;
    }

    /// <summary>File names (without extension) of the component dictionaries in a ped folder.</summary>
    public IReadOnlyList<string> PedFolderFiles(string pedFolder) =>
        _pedFolders.TryGetValue(pedFolder, out var files) ? files : Array.Empty<string>();

    /// <summary>
    /// All drawables of a component ped's <c>&lt;ped&gt;.ydd</c> with their names resolved from the hash where possible
    /// (<c>head_000_r</c>); unresolved ones are named by hash and can still be classified by their diffuse texture.
    /// </summary>
    public List<(string name, MeshData mesh)> LoadPedDictionary(string ped)
    {
        var result = new List<(string, MeshData)>();
        if (!_ydd.TryGetValue(JenkinsHash.HashLower(ped), out var entry)) return result;
        using var ms = ExportResource(entry.File);
        var res = new Resource7<PgDictionary64<GtaDrawable>>();
        res.Load(ms);
        var dict = res.ResourceData;
        var values = dict.Values?.Entries;
        var hashes = dict.Hashes?.Entries;
        if (values == null) return result;
        var names = PedNaming.DrawableNameCandidates(ped);
        var cloth = LoadClothBindings(ped);
        for (int i = 0; i < values.Count; i++)
        {
            var hash = hashes != null && i < hashes.Count ? hashes[i] : 0u;
            var name = names.TryGetValue(hash, out var n) ? n : $"0x{hash:X8}";
            try { result.Add((name, MeshExtractor.Extract(values[i], $"{ped}/{name}", cloth.GetValueOrDefault(hash)))); }
            catch (Exception ex) { _error?.Invoke($"{ped}.ydd[{i}]: {ex.Message}"); }
        }
        return result;
    }

    // ------------------------------------------------------------------ textures

    /// <summary>A texture by name from a game-wide <c>.ytd</c>; null when either is missing.</summary>
    public TextureImage? LoadTexture(string dictionaryName, string textureName)
    {
        var dict = LoadTextureDictionary(dictionaryName);
        return dict == null ? null : TextureExtractor.Find(dict, textureName);
    }

    /// <summary>The (single) texture of a folder ped's <c>&lt;folder&gt;/&lt;name&gt;.ytd</c>.</summary>
    public TextureImage? LoadPedTexture(string pedFolder, string textureName)
    {
        var dict = LoadTextureDictionary(textureName, pedFolder);
        return dict == null ? null : TextureExtractor.First(dict);
    }

    /// <summary>Whether a texture of that name can be served for a ped: folder peds keep one .ytd per texture, component peds one .ytd per ped.</summary>
    public bool HasPedTexture(string ped, string textureName) =>
        _pedFolders.ContainsKey(ped) ? _ytdByFolder.ContainsKey(ped + "/" + textureName) : _ytd.ContainsKey(JenkinsHash.HashLower(ped));

    /// <summary>Archive entry size of a texture source, used in ETags.</summary>
    public long? PedTextureSize(string ped, string textureName)
    {
        if (_pedFolders.ContainsKey(ped)) return _ytdByFolder.TryGetValue(ped + "/" + textureName, out var e) ? (long)e.File.Size : null;
        return _ytd.TryGetValue(JenkinsHash.HashLower(ped), out var d) ? (long)d.File.Size : null;
    }

    public long? TextureDictionarySize(string name) => _ytd.TryGetValue(JenkinsHash.HashLower(name), out var e) ? (long)e.File.Size : null;

    /// <summary>Reads a resource-shipped .ydr (RSC7 header expected).</summary>
    public static MeshData LoadLooseDrawable(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var res = new Resource7<GtaDrawable>();
        res.Load(new MemoryStream(bytes));
        return MeshExtractor.Extract(res.ResourceData, Path.GetFileNameWithoutExtension(path));
    }

    public IClipDictionary LoadLooseClipDictionary(string path) => LoadLooseDictionary(path);

    /// <summary>Loads a resource-shipped .ycd; needs no game data (the keys are only required for archives).</summary>
    public static IClipDictionary LoadLooseDictionary(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return new GtClipDictionary(name, LoadLooseYcd(path));
    }

    /// <summary>Reads a .ycd with an RSC7 header (deflate) or a raw, headerless system page.</summary>
    public static ClipDictionary LoadLooseYcd(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 4 && bytes[0] == (byte)'R' && bytes[1] == (byte)'S' && bytes[2] == (byte)'C' && bytes[3] == (byte)'7')
        {
            var res = new Resource7<ClipDictionary>();
            res.Load(new MemoryStream(bytes));
            return res.ResourceData;
        }
        var reader = new ResourceDataReader(new MemoryStream(bytes), new MemoryStream(Array.Empty<byte>()));
        reader.Position = 0x50000000;
        return reader.ReadBlock<ClipDictionary>();
    }

    public void Dispose()
    {
        for (int i = _archives.Count - 1; i >= 0; i--)
        {
            try { _archives[i].Dispose(); } catch { }
        }
        _archives.Clear();
        _ycd.Clear(); _yft.Clear(); _ydr.Clear(); _ydd.Clear(); _yddByFolder.Clear(); _ytd.Clear(); _ytdByFolder.Clear(); _pedFolders.Clear();
    }

    /// <summary>Orders "patchday2ng" before "patchday10ng".</summary>
    sealed class NaturalComparer : IComparer<string>
    {
        static readonly Regex Num = new(@"\d+|\D+", RegexOptions.Compiled);
        public int Compare(string? x, string? y)
        {
            if (x == null || y == null) return string.CompareOrdinal(x, y);
            var xs = Num.Matches(x); var ys = Num.Matches(y);
            for (int i = 0; i < Math.Min(xs.Count, ys.Count); i++)
            {
                var a = xs[i].Value; var b = ys[i].Value;
                int c = char.IsDigit(a[0]) && char.IsDigit(b[0]) && long.TryParse(a, out var na) && long.TryParse(b, out var nb)
                    ? na.CompareTo(nb) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
            return xs.Count.CompareTo(ys.Count);
        }
    }
}

/// <summary>Helpers to walk gta-toolkit hash maps and convert its animation blocks into decoder input.</summary>
public static class GtResourceHelpers
{
    public static IEnumerable<(uint hash, T value)> Entries<T>(ResourceHashMap<T>? map) where T : IResourceSystemBlock, new()
    {
        if (map?.Buckets == null) yield break;
        foreach (var bucket in map.Buckets)
        {
            for (var e = bucket; e != null; e = e.Next)
                if (e.Data != null) yield return (e.Hash, e.Data);
        }
    }

    /// <summary>Maps gta-toolkit's raw sequence fields onto the spec §3.1 header.</summary>
    public static SequenceHeader ToHeader(Sequence s) => new(
        DataLength: s.DataLength,
        FrameOffset: s.Unknown_Ch,
        RootMotionRefsOffset: s.Unknown_10h,
        NumFrames: (ushort)(s.Unknown_14h >> 16),
        FrameLength: (ushort)(s.Unknown_18h & 0xFFFF),
        IndirectQuantizeFloatNumInts: (ushort)(s.Unknown_18h >> 16),
        QuantizeFloatValueBits: s.Unknown_1Ch,
        ChunkSize: (byte)(s.Unknown_1Eh & 0xFF),
        RootMotionRefCounts: (byte)(s.Unknown_1Eh >> 8));

    public static TrackDef[] ToTracks(Animation a)
    {
        var src = a.Tracks?.Entries;
        if (src == null) return Array.Empty<TrackDef>();
        var tracks = new TrackDef[src.Count];
        for (int i = 0; i < tracks.Length; i++)
        {
            var t = src[i];
            tracks[i] = new TrackDef(t.BoneId, (byte)t.TrackType, t.TrackId);
        }
        return tracks;
    }

    public static AnimationData Decode(Animation a)
    {
        var tracks = ToTracks(a);
        var seqs = new List<DecodedSequence>();
        if (a.Sequences?.Entries != null)
            foreach (var s in a.Sequences.Entries)
                if (s != null) seqs.Add(DecodedSequence.Parse(ToHeader(s), s.Data, tracks));
        return new AnimationData(a.Unknown_14h, a.Unknown_16h, a.Unknown_18h, tracks, seqs);
    }
}

public sealed class GtClipDictionary : IClipDictionary
{
    public string Name { get; }
    public IReadOnlyList<IClip> Clips { get; }
    readonly Dictionary<string, IClip> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<uint, IClip> _byHash = new();
    readonly Dictionary<Animation, uint> _animHash = new(ReferenceEqualityComparer.Instance);

    /// <summary>Hash under which an animation is stored in the dictionary's animation map, if known.</summary>
    public uint? AnimationHash(Animation a) => _animHash.TryGetValue(a, out var h) ? h : null;

    public GtClipDictionary(string name, ClipDictionary dict)
    {
        Name = name;
        foreach (var (hash, anim) in GtResourceHelpers.Entries(dict.Animations?.Animations)) _animHash[anim] = hash;
        var animCache = new Dictionary<Animation, AnimationEvaluator>(ReferenceEqualityComparer.Instance);
        var clips = new List<IClip>();
        foreach (var (hash, clip) in GtResourceHelpers.Entries(dict.Clips))
        {
            var c = new GtClip(clip, animCache) { Hash = hash };
            clips.Add(c);
            _byHash[hash] = c;
            _byName.TryAdd(c.Name, c);
        }
        clips.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Clips = clips;
    }

    public IClip? FindClip(string name)
    {
        if (_byName.TryGetValue(name, out var c)) return c;
        if (_byHash.TryGetValue(JenkinsHash.Hash(name), out c)) return c;
        if (_byHash.TryGetValue(JenkinsHash.HashLower(name), out c)) return c;
        return null;
    }
}

public sealed class GtClip : IClip
{
    public string Name { get; }
    public string FullName { get; }
    public float Duration { get; }
    public float NativeFrameRate { get; }
    public IReadOnlyList<TrackKey> Tracks { get; }
    /// <summary>Hash under which the clip is stored in the dictionary.</summary>
    public uint Hash { get; init; }
    /// <summary>The underlying gta-toolkit clip (ClipAnimation or ClipAnimations), for diagnostics and fixture generation.</summary>
    public Clip RawClip { get; }

    readonly Dictionary<Animation, AnimationEvaluator> _cache;
    readonly ClipAnimation? _single;
    readonly ClipAnimations? _list;

    internal GtClip(Clip clip, Dictionary<Animation, AnimationEvaluator> cache)
    {
        _cache = cache;
        RawClip = clip;
        FullName = clip.Name?.Value ?? "";
        Name = ShortName(FullName);
        var tracks = new List<TrackKey>();
        var seen = new HashSet<TrackKey>();
        void AddTracks(Animation? a)
        {
            if (a?.Tracks?.Entries == null) return;
            var entries = a.Tracks.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                var t = entries[i];
                var key = new TrackKey(t.BoneId, t.TrackId);
                if (seen.Add(key)) tracks.Add(key);
            }
        }
        switch (clip)
        {
            case ClipAnimation ca:
                _single = ca;
                Duration = ClipTiming.ClipAnimationDuration(ca.StartTime, ca.EndTime, ca.Rate);
                // Community clips often declare EndTime one frame past the animation (e.g. 3.75 s for a 90-frame, 3.708 s
                // animation at rate 1/24): the last second of playback would wrap to frame 0 (spec §5.2). Cap the playback
                // length at the animation length (rate-scaled) so previews loop within real keyframes, as the PoC did.
                if (ca.Animation != null && ca.Animation.Unknown_18h > 0)
                {
                    var animLength = ClipTiming.ClipAnimationDuration(0f, ca.Animation.Unknown_18h, ca.Rate);
                    if (animLength < Duration) Duration = animLength;
                }
                AddTracks(ca.Animation);
                NativeFrameRate = FrameRate(ca.Animation, ca.Rate);
                break;
            case ClipAnimations cl:
                _list = cl;
                Duration = cl.Duration;
                if (cl.Animations?.Entries != null)
                    foreach (var e in cl.Animations.Entries)
                    {
                        AddTracks(e.Animation);
                        NativeFrameRate = Math.Max(NativeFrameRate, FrameRate(e.Animation, e.Rate));
                    }
                break;
            default:
                throw new NotSupportedException($"clip type {clip.Type}");
        }
        Tracks = tracks;
    }

    /// <summary>(frames − 1) / duration, scaled by the clip rate; 0 when the animation has no usable header.</summary>
    static float FrameRate(Animation? a, float rate)
    {
        if (a == null || a.Unknown_14h <= 1 || !(a.Unknown_18h > 0f)) return 0f;
        var fps = (a.Unknown_14h - 1) / a.Unknown_18h;
        return rate > 0f ? fps * rate : fps;
    }

    /// <summary>"pack:/name.clip" → "name" (clip names are stored with a pack prefix and .clip suffix).</summary>
    static string ShortName(string full)
    {
        var s = full;
        int slash = s.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) s = s[(slash + 1)..];
        if (s.EndsWith(".clip", StringComparison.OrdinalIgnoreCase)) s = s[..^5];
        return s;
    }

    /// <summary>Raw clip timing for diagnostics: StartTime/EndTime/Rate of a ClipAnimation, or the list Duration.</summary>
    public string TimingInfo => _single != null
        ? $"start={_single.StartTime} end={_single.EndTime} rate={_single.Rate} anim={_single.Animation?.Unknown_18h}s/{_single.Animation?.Unknown_14h}f"
        : $"list duration={_list?.Duration} entries={_list?.Animations?.Entries?.Count}";

    /// <summary>The gta-toolkit animation blocks behind this clip, in clip order (diagnostics / tests).</summary>
    public IEnumerable<Animation> RawAnimations()
    {
        if (_single?.Animation != null) yield return _single.Animation;
        if (_list?.Animations?.Entries != null)
            foreach (var e in _list.Animations.Entries) if (e?.Animation != null) yield return e.Animation;
    }

    AnimationEvaluator Evaluator(Animation a)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(a, out var ev))
            {
                ev = new AnimationEvaluator(GtResourceHelpers.Decode(a));
                _cache[a] = ev;
            }
            return ev;
        }
    }

    public void Sample(double time, ClipSample into)
    {
        into.Clear();
        if (_single != null)
        {
            if (_single.Animation == null) return;
            var t = ClipTiming.ClipAnimationTime((float)time, _single.StartTime, _single.EndTime, _single.Rate);
            Evaluate(_single.Animation, t, into);
        }
        else if (_list?.Animations?.Entries != null)
        {
            // Spec §5.4: every element gets the same wrapped clip time; later elements override earlier ones.
            var t = ClipTiming.ClipAnimationsTime((float)time, _list.Duration);
            foreach (var e in _list.Animations.Entries)
                if (e?.Animation != null) Evaluate(e.Animation, t, into);
        }
    }

    void Evaluate(Animation a, float animTime, ClipSample into)
    {
        var ev = Evaluator(a);
        var anim = ev.Animation;
        var pos = ev.GetFramePosition(animTime);
        for (int i = 0; i < anim.Tracks.Count; i++)
        {
            var track = anim.Tracks[i];
            switch (track.TrackId)
            {
                case (byte)AnimTrack.BonePosition:
                {
                    var v = ev.EvaluateVector(i, pos, true);
                    into.Translations[track.BoneId] = new Vector3(v.X, v.Y, v.Z);
                    break;
                }
                case (byte)AnimTrack.BoneRotation:
                    into.Rotations[track.BoneId] = ev.EvaluateQuaternion(i, pos, true);
                    break;
                case (byte)AnimTrack.BoneScale:
                {
                    var v = ev.EvaluateVector(i, pos, true);
                    into.Scales[track.BoneId] = new Vector3(v.X, v.Y, v.Z);
                    break;
                }
                case (byte)AnimTrack.RootMotionPosition:
                {
                    var v = ev.EvaluateVector(i, pos, true);
                    into.RootMotionTranslation += new Vector3(v.X, v.Y, v.Z);
                    break;
                }
                case (byte)AnimTrack.RootMotionRotation:
                    into.RootMotionRotation = ev.EvaluateQuaternion(i, pos, true) * into.RootMotionRotation;
                    break;
            }
        }
    }
}
