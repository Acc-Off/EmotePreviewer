using System.Diagnostics;
using System.Text.Json;
using EmotePreviewer.Core.Adapters.GtaToolkit;
using EmotePreviewer.Core.Catalog;
using EmotePreviewer.Core.Gta;
using EmotePreviewer.Core.Model;
using EmotePreviewer.Core.Rage;
using EmotePreviewer.Fixtures;

// Developer tools. Not part of the distributed application.
//
//   devtools regression [out.json.gz] [builtin] [custom] [seed]   generate the decoder regression fixture (default tests/fixtures/)
//   devtools coverage [limit]                                     how many catalog entries resolve to a real dictionary + clip
//   devtools find <dictionary | yft name>                         which archive a dictionary / skeleton resolves to
//   devtools skeleton <yft name> [out.json]                       dump a skeleton definition as JSON
//   devtools yft <yft name> [out.yft]                             extract a skeleton fragment from GTA archives
//   devtools bake [limit]                                         bake every previewable entry with ClipBaker and count failures
//   devtools pose <dictionary> <clip> [t]                         world-space bone positions at time t (compare with the viewer)
//   devtools axes <dictionary> <clip> <bone> [bone...]            rotation-axis statistics of a bone's local rotation (relative to bind) over a clip
//   devtools mesh <model> [more models...]                        extract prop / ped drawables and print their statistics
//   devtools props [limit] [--textures]                           check how many prop models referenced by the catalog resolve (and their diffuse textures)
//   devtools peds [filter]                                        list the ped models in the game data (name, storage form, category)
//   devtools pedinfo <ped>                                        default component per slot of a ped (folder or component type)
//   devtools skinbones [filter]                                   peds whose component drawables embed their own skeleton: whether the bone numbering differs from the .yft and whether every tag resolves
//   devtools cloth [filter]                                       peds with cloth-simulated drawables (the "Cloth" toggle), default variations marked, decode check
//   devtools yld <folder>/<file> [--dump <dir>]                   contents of a ped component's cloth dictionary (simulation vertices, bindings, edges)
//   devtools dds <scope> <name> [out.dds]                         extract a diffuse texture (scope: prop model or ped) as DDS
//   devtools animals                                              which ped every animal entry of the catalog resolves to
//   devtools shared [--gta-check]                                 partner / placement / clip status of every shared emote (two-ped) entry
//   devtools walks [--list]                                       how the Walk entries resolve through clip_sets.ymt
//   devtools clipsets [<set> [clip]]                              clip_sets.ymt files; one set with its fallback chain and the clip it resolves to
//
// Options: --data <folder>   folder containing the resource folders (default: <repo>/data)
//          --gta <folder>    GTA V folder (default: GTA_FOLDER / registry)
//          --keys <folder>   key files (default: EMOTEPREVIEWER_KEYS / %LOCALAPPDATA%\EmotePreviewer\keys)

var rest = new List<string>(args);
string? Opt(string name)
{
    var i = rest.IndexOf(name);
    if (i < 0 || i + 1 >= rest.Count) return null;
    var v = rest[i + 1];
    rest.RemoveRange(i, 2);
    return v;
}

string? Opt2(string name)
{
    var i = rest.IndexOf(name);
    return i < 0 || i + 1 >= rest.Count ? null : rest[i + 1];
}
var dataRoot = Opt("--data") ?? FindDataRoot() ?? throw new DirectoryNotFoundException("data folder not found; pass --data <folder>");
var gtaFolder = Opt("--gta") ?? GtaLocator.Detect();
var keysFolder = Opt("--keys");
var skeletonName = Opt("--skel") ?? "mp_m_freemode_01";
var cmd = rest.Count > 0 ? rest[0] : "help";
var sw = Stopwatch.StartNew();

switch (cmd)
{
    case "regression":
    {
        var cat = BuildCatalog();
        using var gd = OpenGta();
        var outPath = rest.ElementAtOrDefault(1) ?? Path.Combine(RepoRoot(), "tests", "fixtures", FixtureLoader.FileName);
        var opts = new FixtureGenerator.Options(
            Builtin: rest.Count > 2 ? int.Parse(rest[2]) : 120,
            Custom: rest.Count > 3 ? int.Parse(rest[3]) : 60,
            Seed: rest.Count > 4 ? int.Parse(rest[4]) : 20260905);
        var fixture = FixtureGenerator.Generate(gd, cat, opts, Console.Error.WriteLine);
        FixtureLoader.Save(fixture, outPath);
        Console.WriteLine($"wrote {outPath}: {fixture.Clips.Count} clips, {new FileInfo(outPath).Length / 1024} KB");
        break;
    }
    case "coverage":
        Coverage(rest.Count > 1 ? int.Parse(rest[1]) : int.MaxValue);
        break;
    case "bake":
        BakeAll(rest.Count > 1 ? int.Parse(rest[1]) : int.MaxValue);
        break;
    case "mesh":
    {
        if (rest.Count < 2) return Usage();
        using var gd = OpenGta();
        foreach (var model in rest.Skip(1))
        {
            var sw2 = Stopwatch.StartNew();
            try
            {
                var mesh = gd.LoadDrawable(model);
                if (mesh == null) { Console.WriteLine($"{model}: not found"); continue; }
                Console.WriteLine($"{model}: {mesh.VertexCount} vertices, {mesh.Indices.Length / 3} triangles, {mesh.SubMeshes.Count} sub-meshes, normals={mesh.Normals != null} uvs={mesh.Uvs != null} skinned={mesh.IsSkinned}, bounds {mesh.BoundsMin} .. {mesh.BoundsMax}, {mesh.ToBytes().Length / 1024} KB in {sw2.ElapsedMilliseconds} ms  [{gd.FindDrawablePath(model)}]");
                foreach (var w in mesh.Warnings) Console.WriteLine("  warn " + w);
                if (mesh.VertexCount > 0)
                {
                    float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                    for (int i = 0; i < mesh.Positions.Length; i += 3) { minX = Math.Min(minX, mesh.Positions[i]); maxX = Math.Max(maxX, mesh.Positions[i]); minZ = Math.Min(minZ, mesh.Positions[i + 2]); maxZ = Math.Max(maxZ, mesh.Positions[i + 2]); }
                    Console.WriteLine($"  vertex x range {minX:F3}..{maxX:F3}, z range {minZ:F3}..{maxZ:F3}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"{model}: FAIL {ex.GetType().Name}: {ex.Message}"); }
        }
        break;
    }
    case "ped":
    {
        if (rest.Count < 3) return Usage();
        using var gd = OpenGta();
        foreach (var file in rest.Skip(2))
        {
            var sw3 = Stopwatch.StartNew();
            try
            {
                var mesh = gd.LoadPedComponent(rest[1], file);
                if (mesh == null) { Console.WriteLine($"{rest[1]}/{file}: not found"); continue; }
                var bones = mesh.BlendIndices == null ? "none" : $"{mesh.BlendIndices.Distinct().Count()} distinct (max {mesh.BlendIndices.Max()})";
                Console.WriteLine($"{rest[1]}/{file}: {mesh.VertexCount} vertices, {mesh.Indices.Length / 3} triangles, {mesh.SubMeshes.Count} sub-meshes, skinned={mesh.IsSkinned}, bones {bones}, {mesh.ToBytes().Length / 1024} KB in {sw3.ElapsedMilliseconds} ms");
                foreach (var w in mesh.Warnings) Console.WriteLine("  warn " + w);
            }
            catch (Exception ex) { Console.WriteLine($"{rest[1]}/{file}: FAIL {ex.GetType().Name}: {ex.Message}"); }
        }
        break;
    }
    case "walks":
    {
        // How the Walk entries resolve: through clip_sets.ymt (own dictionary or a fallback set), or only by the name heuristic.
        var cat = BuildCatalog();
        using var gd = OpenGta();
        var table = gd.ClipSets;
        Console.WriteLine($"clip set files: {string.Join(", ", gd.ClipSetFilePaths)}; sets: {table?.Count}");
        var walks = cat.Entries.Where(e => e.Kind == EmoteKind.Walk && !string.IsNullOrEmpty(e.Name)).ToList();
        int own = 0, fallback = 0, heuristicOnly = 0, unresolved = 0, notInTable = 0;
        var byDict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unresolvedNames = new List<string>();
        foreach (var e in walks)
        {
            var r = gd.ResolveClipSetClip(e.Name!, EmoteEntry.WalkClip);
            var inTable = table?.Contains(e.Name!) ?? false;
            if (!inTable) notInTable++;
            if (r != null)
            {
                if (r.ViaFallback) fallback++; else own++;
                byDict[r.Dictionary] = byDict.TryGetValue(r.Dictionary, out var n) ? n + 1 : 1;
                if (rest.Contains("--list")) Console.WriteLine($"  {e.Name,-40} -> {r.Dictionary} / {r.Clip}{(r.ViaFallback ? " (fallback)" : "")}{(inTable ? "" : " NOT IN TABLE")}");
                continue;
            }
            var heuristic = gd.HasClipDictionary(e.Name!) && gd.LoadClipDictionary(e.Name!)?.FindClip(EmoteEntry.WalkClip) != null;
            if (heuristic) heuristicOnly++; else { unresolved++; unresolvedNames.Add(e.Name! + (inTable ? "" : " (not in table)")); }
        }
        Console.WriteLine($"walk entries: {walks.Count}; resolved via clip sets: {own + fallback} (own dictionary {own}, fallback {fallback}); name heuristic only: {heuristicOnly}; unresolved: {unresolved}; not in table: {notInTable}");
        Console.WriteLine("  dictionaries: " + string.Join(", ", byDict.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine("  unresolved: " + string.Join(", ", unresolvedNames.Distinct()));
        break;
    }
    case "clipsets":
    {
        // Clip set definitions: "clipsets" lists the files and counts, "clipsets <set> [clip]" shows one set, its fallback chain and the resolved clip.
        using var gd = OpenGta();
        var table = gd.ClipSets;
        foreach (var p in gd.ClipSetFilePaths) Console.WriteLine("  " + p);
        Console.WriteLine($"sets: {table?.Count}");
        if (table == null || rest.Count < 2) break;
        var clip = rest.Count >= 3 ? rest[2] : EmoteEntry.WalkClip;
        var def = table.Find(rest[1]);
        if (def == null) { Console.WriteLine($"{rest[1]}: not in the table"); break; }
        var visited = new HashSet<uint>();
        for (var d = def; d != null && visited.Add(d.Id); d = d.Fallback == 0 ? null : table.Find(d.Fallback))
        {
            var dictName = gd.ClipDictionaryName(d.Dictionary);
            Console.WriteLine($"set 0x{d.Id:X8}: dictionary {dictName ?? "?"} (0x{d.Dictionary:X8}), fallback 0x{d.Fallback:X8}, {d.Items.Count} items");
            if (dictName != null)
            {
                var dict = gd.LoadClipDictionary(dictName);
                Console.WriteLine("  clips: " + string.Join(", ", dict?.Clips.Select(c => c.Name).Take(40) ?? Array.Empty<string>()));
            }
        }
        var r = gd.ResolveClipSetClip(rest[1], clip);
        Console.WriteLine($"resolved '{clip}': {(r == null ? "none" : $"{r.Dictionary} / {r.Clip}{(r.ViaFallback ? " (fallback)" : "")}")}");
        break;
    }
    case "tex":
    {
        // Shader texture parameters and embedded texture dictionaries of drawables (props: "tex <model>", peds: "tex <folder>/<file>").
        if (rest.Count < 2) return Usage();
        using var gd = OpenGta();
        foreach (var target in rest.Skip(1))
        {
            var drawables = new List<(string name, RageLib.Resources.GTA5.PC.Drawables.Drawable d)>();
            if (target.Contains('/'))
            {
                var parts = target.Split('/', 2);
                var dict = gd.LoadRawPedDictionary(parts[0], parts[1]);
                if (dict?.Values?.Entries != null) for (int i = 0; i < dict.Values.Entries.Count; i++) drawables.Add(($"{target}[{i}]", dict.Values.Entries[i]));
            }
            else
            {
                var d = gd.LoadRawDrawable(target);
                if (d != null) drawables.Add((target, d));
            }
            if (drawables.Count == 0) { Console.WriteLine($"{target}: not found"); continue; }
            foreach (var (name, d) in drawables)
            {
                Console.WriteLine($"{name}:");
                var txd = d.ShaderGroup?.TextureDictionary;
                var embedded = txd?.Values?.Entries;
                Console.WriteLine($"  embedded textures: {(embedded == null ? 0 : embedded.Count)}");
                if (embedded != null)
                    foreach (var t in embedded) Console.WriteLine($"    {t.Name?.Value,-40} {t.Width}x{t.Height} fmt=0x{t.Format:X8} ({FourCC(t.Format)}) levels={t.Levels} bytes={t.Data?.FullData?.Length}");
                var shaders = d.ShaderGroup?.Shaders?.Entries;
                if (shaders != null)
                    for (int si = 0; si < shaders.Count; si++)
                    {
                        var sh = shaders[si];
                        var ps = sh?.ParametersList;
                        Console.Write($"  shader {si}: hash=0x{sh?.ShaderHash:X8}");
                        if (ps?.Parameters != null)
                            for (int pi = 0; pi < ps.Parameters.Count; pi++)
                            {
                                var p = ps.Parameters[pi];
                                if (p.DataType != 0) continue;
                                var tex = p.Data as RageLib.Resources.GTA5.PC.Textures.Texture;
                                var hash = ps.Hashes != null && pi < ps.Hashes.Count ? ps.Hashes[pi] : 0u;
                                Console.Write($"  [0x{hash:X8} -> {tex?.Name?.Value ?? "(null)"}{(tex is RageLib.Resources.GTA5.PC.Textures.TextureDX11 ? " embedded" : " external")}]");
                            }
                        Console.WriteLine();
                    }
            }
        }
        break;
    }
    case "ydd":
    {
        // Drawable dictionary contents by name, with the component names the hashes resolve to (component peds keep every part in one .ydd).
        if (rest.Count < 2) return Usage();
        using var gd = OpenGta();
        var name = rest[1];
        var dict = gd.LoadRawDrawableDictionary(name);
        if (dict?.Values?.Entries == null) { Console.WriteLine($"{name}: not found ({gd.FindDrawablePath(name) ?? "no path"})"); break; }
        var hashes = dict.Hashes?.Entries;
        var candidates = new Dictionary<uint, string>();
        foreach (var comp in new[] { "head", "berd", "hair", "uppr", "lowr", "hand", "feet", "teef", "accs", "task", "decl", "jbib" })
            for (int n = 0; n < 40; n++)
                foreach (var suffix in new[] { "r", "u" })
                    foreach (var prefix in new[] { "", name + "_" })
                    {
                        var candidate = $"{prefix}{comp}_{n:000}_{suffix}";
                        candidates.TryAdd(EmotePreviewer.Core.Rage.JenkinsHash.HashLower(candidate), candidate);
                    }
        Console.WriteLine($"{name}: {dict.Values.Entries.Count} drawables  [{gd.FindDrawablePath(name)}]");
        for (int i = 0; i < dict.Values.Entries.Count; i++)
        {
            var h = hashes != null && i < hashes.Count ? hashes[i] : 0u;
            var d = dict.Values.Entries[i];
            var mesh = EmotePreviewer.Core.Adapters.GtaToolkit.MeshExtractor.Extract(d, candidates.TryGetValue(h, out var cn) ? cn : $"0x{h:X8}");
            var diffuse = d.ShaderGroup?.Shaders?.Entries?.Select(sh => sh?.ParametersList).Where(ps => ps?.Parameters != null)
                .SelectMany(ps => ps!.Parameters.Select((p, pi) => (p, hash: ps.Hashes != null && pi < ps.Hashes.Count ? ps.Hashes[pi] : 0u)))
                .Where(x => x.p.DataType == 0 && x.hash == 0xF1FE2B71).Select(x => (x.p.Data as RageLib.Resources.GTA5.PC.Textures.Texture)?.Name?.Value).FirstOrDefault();
            Console.WriteLine($"  [{i,2}] {mesh.Name,-28} {mesh.VertexCount,6} verts skinned={mesh.IsSkinned} diffuse={diffuse}");
        }
        break;
    }
    case "missing":
    {
        // Where do the "not found" props / dictionaries / clips of the catalog actually live (if anywhere)?
        var cat = BuildCatalog();
        using var gd = OpenGta();
        var models = cat.Entries.SelectMany(e => e.Props.Select(p => p.Model)).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m).ToList();
        var missingModels = models.Where(m => !cat.CustomDrawables.ContainsKey(m) && !gd.HasDrawable(m)).ToList();
        var asFragment = missingModels.Where(m => gd.FindSkeletonPath(m) != null).ToList();
        Console.WriteLine($"prop models not found as .ydr/.ydd: {missingModels.Count}; of those stored as .yft fragments: {asFragment.Count}");
        foreach (var m in asFragment.Take(12)) Console.WriteLine($"  yft  {m}: {gd.FindSkeletonPath(m)}");
        foreach (var m in missingModels.Except(asFragment).Take(30)) Console.WriteLine($"  none {m}");

        var anims = cat.Entries.Where(e => e.HasClip && !e.IsAnimal).ToList();
        var missingDict = anims.Where(e => !e.IsCustom && !gd.HasClipDictionary(e.Dictionary!)).Select(e => e.Dictionary!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"dictionaries not found: {missingDict.Count}");
        foreach (var d in missingDict.Take(20)) Console.WriteLine($"  {d}");
        var byDict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var cache = new Dictionary<string, IClipDictionary?>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in anims)
        {
            if (!cache.TryGetValue(e.Dictionary!, out var dict))
            {
                try { dict = e.IsCustom ? gd.LoadLooseClipDictionary(e.CustomYcdPath!) : gd.HasClipDictionary(e.Dictionary!) ? gd.LoadClipDictionary(e.Dictionary!) : null; } catch { dict = null; }
                cache[e.Dictionary!] = dict;
            }
            if (dict == null || dict.FindClip(e.Clip!) != null) continue;
            if (!byDict.TryGetValue(e.Dictionary!, out var list)) byDict[e.Dictionary!] = list = new List<string>();
            list.Add($"{e.Clip} ({e.Source}/{e.Command})");
        }
        Console.WriteLine($"dictionaries with missing clips: {byDict.Count} ({byDict.Sum(kv => kv.Value.Count)} entries)");
        foreach (var (d, wanted) in byDict.OrderByDescending(kv => kv.Value.Count).Take(25))
        {
            var dict = cache[d]!;
            Console.WriteLine($"  {d}: wanted {string.Join(", ", wanted.Take(4))}{(wanted.Count > 4 ? " …" : "")}");
            Console.WriteLine($"      has {dict.Clips.Count} clips: {string.Join(", ", dict.Clips.Select(c => c.Name).Take(8))}{(dict.Clips.Count > 8 ? " …" : "")}");
        }
        break;
    }
    case "pedtex":
    {
        if (rest.Count < 2) return Usage();
        using var gd = OpenGta();
        var names = gd.PedTextureDictionaries(rest[1]).ToList();
        Console.WriteLine($"{rest[1]}: {names.Count} texture dictionaries; ytd indexed game-wide: {gd.TextureDictionaryCount}");
        foreach (var n in names.Take(rest.Count > 2 ? int.Parse(rest[2]) : 60)) Console.WriteLine("  " + n);
        break;
    }
    case "ytd":
    {
        if (rest.Count < 2) return Usage();
        using var gd = OpenGta();
        var parts = rest[1].Split('/', 2);
        var dict = parts.Length == 2 ? gd.LoadTextureDictionary(parts[1], parts[0]) : gd.LoadTextureDictionary(parts[0]);
        if (dict?.Values?.Entries == null) { Console.WriteLine($"{rest[1]}: not found ({gd.FindTextureDictionaryPath(parts[^1]) ?? "no path"})"); break; }
        Console.WriteLine($"{rest[1]}: {dict.Values.Entries.Count} textures  [{(parts.Length == 2 ? "" : gd.FindTextureDictionaryPath(parts[0]))}]");
        foreach (var t in dict.Values.Entries) Console.WriteLine($"  {t.Name?.Value,-40} {t.Width}x{t.Height} fmt={FourCC(t.Format)} levels={t.Levels} bytes={t.Data?.FullData?.Length}");
        break;
    }
    case "props":
    {
        var cat = BuildCatalog();
        using var gd = OpenGta();
        var models = cat.Entries.SelectMany(e => e.Props.Select(p => p.Model)).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m).Take(rest.Count > 1 && int.TryParse(rest[1], out var lim) ? lim : int.MaxValue).ToList();
        int found = 0, failed = 0, missing = 0, warned = 0; long bytes = 0;
        var swp = Stopwatch.StartNew();
        foreach (var m in models)
        {
            var shipped = cat.CustomDrawables.TryGetValue(m, out var loosePath);
            if (!shipped && !gd.HasDrawable(m)) { missing++; if (missing <= 25) Console.WriteLine($"  missing {m}"); continue; }
            try
            {
                var mesh = shipped ? GtaToolkitGameData.LoadLooseDrawable(loosePath!) : gd.LoadDrawable(m);
                if (mesh == null || mesh.VertexCount == 0) { failed++; Console.WriteLine($"  empty {m}: {string.Join("; ", mesh?.Warnings ?? Array.Empty<string>())}"); continue; }
                found++; bytes += (long)mesh.VertexCount * 32 + mesh.Indices.Length * 4L;
                if (mesh.Warnings.Count > 0) { warned++; if (warned <= 10) Console.WriteLine($"  warn {m}: {string.Join("; ", mesh.Warnings)}"); }
            }
            catch (Exception ex) { failed++; if (failed <= 25) Console.WriteLine($"  FAIL {m}: {ex.GetType().Name}: {ex.Message}"); }
        }
        Console.WriteLine($"prop models: {models.Count}, found {found} (shipped {models.Count(m => cat.CustomDrawables.ContainsKey(m))}), missing {missing}, failed {failed}, warnings {warned}, {bytes / 1024 / 1024} MB total in {swp.Elapsed.TotalSeconds:F1} s");
        if (rest.Contains("--textures"))
        {
            // How many sub-meshes get a diffuse: embedded in the drawable, from <model>.ytd, or nothing.
            int subs = 0, embedded = 0, ytd = 0, none = 0, noDiffuse = 0, unknownFormat = 0;
            var formats = new Dictionary<string, int>();
            var unresolved = new Dictionary<string, int>();
            foreach (var m in models)
            {
                MeshData? mesh;
                try { mesh = cat.CustomDrawables.TryGetValue(m, out var lp) ? GtaToolkitGameData.LoadLooseDrawable(lp) : gd.LoadDrawable(m); } catch { continue; }
                if (mesh == null) continue;
                foreach (var sub in mesh.SubMeshes)
                {
                    subs++;
                    if (sub.Diffuse == null) { noDiffuse++; continue; }
                    var img = mesh.FindEmbedded(sub.Diffuse);
                    if (img != null) embedded++;
                    else if ((img = gd.LoadTexture(m, sub.Diffuse)) != null) ytd++;
                    else { none++; unresolved[sub.Diffuse] = unresolved.GetValueOrDefault(sub.Diffuse) + 1; continue; }
                    var f = EmotePreviewer.Core.Textures.TextureImage.FormatName(img.Format);
                    formats[f] = formats.GetValueOrDefault(f) + 1;
                }
                if (mesh.SubMeshes.Any(sb => sb.DiffuseEmbedded && mesh.FindEmbedded(sb.Diffuse!) == null)) unknownFormat++;
            }
            Console.WriteLine($"diffuse textures: {subs} sub-meshes, embedded {embedded}, from .ytd {ytd}, unresolved {none}, no diffuse {noDiffuse}; models with an embedded texture of unknown format: {unknownFormat}");
            Console.WriteLine("  formats: " + string.Join(", ", formats.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
            foreach (var (name, n) in unresolved.OrderByDescending(kv => kv.Value).Take(15)) Console.WriteLine($"  unresolved {name} x{n}");
        }
        if (missing > 0 && rest.Contains("--scan"))
        {
            var sws = Stopwatch.StartNew();
            var index = gd.ScanDrawableDictionaries(progress: (d, t) => { if (d % 500 == 0) Console.Error.WriteLine($"  scanned {d}/{t} ydd"); });
            Console.WriteLine($"scanned drawable dictionaries: {index.Count} models in {sws.Elapsed.TotalSeconds:F1} s");
            int rescued = 0;
            foreach (var m in models.Where(m => !cat.CustomDrawables.ContainsKey(m) && !gd.HasDrawable(m)))
            {
                if (index.TryGetValue(EmotePreviewer.Core.Rage.JenkinsHash.HashLower(m), out var dd)) { rescued++; if (rescued <= 15) Console.WriteLine($"  in ydd {dd}: {m}"); }
            }
            Console.WriteLine($"  models found inside dictionaries: {rescued} of {missing}");
        }
        break;
    }
    case "peds":
    {
        using var gd = OpenGta();
        var swl = Stopwatch.StartNew();
        var peds = gd.ListPeds();
        var filter = rest.ElementAtOrDefault(1);
        Console.WriteLine($"{peds.Count} peds in {swl.ElapsedMilliseconds} ms: " + string.Join(", ", peds.GroupBy(p => p.Category).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))
            + $"; folder {peds.Count(p => p.Storage == PedStorage.Folder)}, component {peds.Count(p => p.Storage == PedStorage.Component)}");
        foreach (var p in peds.Where(p => filter == null || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(filter == null ? 40 : 200))
            Console.WriteLine($"  {p.Name,-32} {p.Storage,-10} {p.Category,-12} {p.Path}");
        break;
    }
    case "geom":
    {
        // Raw model / geometry layout of a drawable (props: "geom <model>", component peds: "geom <ped>/<index>", folder peds: "geom <folder>/<file>").
        if (rest.Count < 2) return Usage();
        using var gd = OpenGta();
        foreach (var target in rest.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)))
        {
            RageLib.Resources.GTA5.PC.Drawables.Drawable? d = null;
            var parts = target.Split('/', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], out var idx)) d = gd.LoadRawDrawableDictionary(parts[0])?.Values?.Entries?.ElementAtOrDefault(idx);
            else if (parts.Length == 2) d = gd.LoadRawPedDictionary(parts[0], parts[1])?.Values?.Entries?.FirstOrDefault();
            else d = gd.LoadRawDrawable(target);
            if (d == null) { Console.WriteLine($"{target}: not found"); continue; }
            // Drawables may embed the skeleton their blend indices refer to (see MeshData.BlendBoneTags); --bones lists it as name:tag.
            var own = d.Skeleton?.BoneData?.Bones;
            Console.WriteLine($"{target}: embedded skeleton={(own?.Count.ToString() ?? "none")}"
                + (own != null && rest.Contains("--bones") ? " " + string.Join(",", Enumerable.Range(0, own.Count).Select(i => own[i].Name?.Value + ":" + own[i].BoneId)) : ""));
            Console.WriteLine($"{target}: lods high={d.LodGroup.LodHigh?.Models?.Entries?.Count} med={d.LodGroup.LodMedium?.Models?.Entries?.Count} low={d.LodGroup.LodLow?.Models?.Entries?.Count} vlow={d.LodGroup.LodVeryLow?.Models?.Entries?.Count}");
            var shs = d.ShaderGroup?.Shaders?.Entries;
            if (shs != null)
                for (int si = 0; si < shs.Count; si++)
                {
                    Console.WriteLine($"  shader {si}: 0x{shs[si]?.ShaderHash:X8} {EmotePreviewer.Core.Gta.ShaderNames.Resolve(shs[si]?.ShaderHash ?? 0)} bucket={shs[si]?.DrawBucket} unk12={shs[si]?.Unknown_12h} params={shs[si]?.ParameterCount}");
                    var ps = shs[si]?.ParametersList;
                    if (ps?.Parameters != null && rest.Contains("--params"))
                        for (int pi = 0; pi < ps.Parameters.Count; pi++)
                        {
                            var pr = ps.Parameters[pi];
                            var hash = ps.Hashes != null && pi < ps.Hashes.Count ? ps.Hashes[pi] : 0u;
                            string val = pr.DataType == 0 ? ((pr.Data as RageLib.Resources.GTA5.PC.Textures.Texture)?.Name?.Value ?? "(null)")
                                : pr.Data is RageLib.Resources.Common.SimpleArray<System.Numerics.Vector4> arr ? string.Join(" ", Enumerable.Range(0, arr.Count).Select(k => arr[k].ToString("F3"))) : "?";
                            Console.WriteLine($"      0x{hash:X8} type={pr.DataType} {val}");
                        }
                }
            var lods = new[] { ("high", d.LodGroup.LodHigh), ("med", d.LodGroup.LodMedium), ("low", d.LodGroup.LodLow), ("vlow", d.LodGroup.LodVeryLow) };
            foreach (var (name, lod) in lods)
            {
                if (lod?.Models?.Entries == null) continue;
                for (int mi = 0; mi < lod.Models.Entries.Count; mi++)
                {
                    var m = lod.Models.Entries[mi];
                    Console.WriteLine($"  {name} model {mi}: skinned={m.IsSkinned} mask=0x{m.Mask:X2} geometries={m.Geometries?.Entries?.Count} rootBone={m.RootBoneIndex} shaderMapping={(m.ShaderMapping == null ? "-" : string.Join(",", Enumerable.Range(0, m.ShaderMapping.Count).Select(i => m.ShaderMapping[i].ToString())))}");
                    if (m.Geometries?.Entries == null) continue;
                    for (int gi = 0; gi < m.Geometries.Entries.Count; gi++)
                    {
                        var g = m.Geometries.Entries[gi];
                        var vb = g?.VertexBuffer;
                        var col = g != null ? EmotePreviewer.Core.Adapters.GtaToolkit.MeshExtractor.AverageColor0(g) : null;
                        Console.WriteLine($"    geometry {gi}: vertices={vb?.VertexCount} stride={vb?.VertexStride} flags=0x{(ushort?)vb?.Info?.Flags:X4} types=0x{(ulong?)vb?.Info?.Types:X16} indices={g?.IndicesCount} boneIds={g?.BonesId?.Count} color0={(col == null ? "-" : string.Join(",", col.Select(x => x.ToString("F0"))))}");
                        if (g != null && Opt2("--skin-dump") is { } dumpDir)
                        {
                            Directory.CreateDirectory(dumpDir);
                            File.WriteAllLines(Path.Combine(dumpDir, $"{target.Replace('/', '_')}_{name}_{mi}_{gi}.csv"), EmotePreviewer.Core.Adapters.GtaToolkit.MeshExtractor.SkinDump(g));
                        }
                        if (g != null && rest.Contains("--skin"))
                        {
                            var stats = EmotePreviewer.Core.Adapters.GtaToolkit.MeshExtractor.SkinStats(g);
                            if (stats != null) Console.WriteLine($"      skin: distinct indices {stats.Value.distinct} max {stats.Value.max}; weight sum min {stats.Value.minSum:F2} max {stats.Value.maxSum:F2}; first vertices: {stats.Value.sample}");
                        }
                        if (g?.BonesId != null && rest.Contains("--bones"))
                            Console.WriteLine($"      boneIds: {string.Join(",", Enumerable.Range(0, g.BonesId.Count).Select(i => g.BonesId[i].ToString()))}");
                    }
                }
            }
        }
        break;
    }
    case "bones":
    {
        // Bone table of a skeleton with the DOF flags (which channels an animation may drive).
        if (rest.Count < 2) return Usage();
        using var gd = OpenGta();
        var flags = gd.LoadBoneFlags(rest[1]);
        if (flags == null) { Console.WriteLine($"{rest[1]}: not found"); break; }
        var filter = rest.ElementAtOrDefault(2);
        foreach (var (index, name, tag, flag) in flags)
            if (filter == null || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"  {index,3} {name,-28} tag={tag,-6} flags=0x{(ushort)flag:X4} {flag}");
        break;
    }
    case "skeleton":
    {
        // Complete skeleton dump for retargeting experiments and external tooling.
        if (rest.Count < 2) return Usage();
        using var gd = OpenGta();
        var skel = gd.LoadSkeleton(rest[1]) ?? throw new FileNotFoundException(rest[1] + ".yft not found");
        var dump = new
        {
            name = skel.Name,
            source = gd.FindSkeletonPath(rest[1]),
            boneCount = skel.Bones.Count,
            bones = skel.Bones.Select(b => new
            {
                b.Index,
                b.Tag,
                b.Name,
                b.ParentIndex,
                translation = new[] { b.Translation.X, b.Translation.Y, b.Translation.Z },
                rotation = new[] { b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W },
                scale = new[] { b.Scale.X, b.Scale.Y, b.Scale.Z },
                dofs = b.Dofs.ToString(),
                dofMask = (ushort)b.Dofs,
            })
        };
        var json = JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true });
        var outPath = rest.ElementAtOrDefault(2);
        if (outPath is null)
        {
            Console.WriteLine(json);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, json);
            Console.WriteLine("wrote " + outPath);
        }
        break;
    }
    case "yft":
    {
        if (rest.Count < 2) return Usage();
        using var gd = OpenGta();
        var outPath = rest.ElementAtOrDefault(2) ?? (rest[1] + ".yft");
        if (!gd.ExportSkeleton(rest[1], outPath)) throw new FileNotFoundException(rest[1] + ".yft not found");
        Console.WriteLine($"wrote {outPath} from {gd.FindSkeletonPath(rest[1])}");
        break;
    }
    case "pedinfo":
    {
        if (rest.Count < 2) return Usage();
        using var gd = OpenGta();
        foreach (var ped in rest.Skip(1))
        {
            var storage = gd.PedStorageOf(ped);
            Console.WriteLine($"{ped}: {(storage?.ToString() ?? "not found")}, skeleton {(gd.FindSkeletonPath(ped) ?? "none")}");
            if (storage == null) continue;
            var swp = Stopwatch.StartNew();
            if (storage == PedStorage.Folder)
            {
                var files = gd.PedFolderFiles(ped).Select(f => PedNaming.ParseFile(f)).Where(f => f != null).Select(f => f!).ToList();
                Console.WriteLine($"  {files.Count} component files: " + string.Join(", ", files.GroupBy(f => f.Slot).Select(g => $"{g.Key} x{g.Count()}")));
                foreach (var slot in PedNaming.Slots)
                {
                    foreach (var f in files.Where(f => f.Slot == slot).OrderBy(f => f.Number).Take(3))
                    {
                        var mesh = gd.LoadPedComponent(ped, f.FileName);
                        var diffuse = mesh?.SubMeshes.Select(sb => sb.Diffuse).FirstOrDefault(d => d != null);
                        var tex = diffuse != null && mesh != null ? (mesh.FindEmbedded(diffuse) != null ? "embedded" : gd.HasPedTexture(ped, diffuse) ? "ytd" : "MISSING") : "-";
                        Console.WriteLine($"  {f.FileName,-14} {mesh?.VertexCount,6} verts  diffuse={diffuse} ({tex}){(mesh?.BlendBoneTags != null ? $"  own skeleton {mesh.BlendBoneTags.Length} bones" : "")}");
                        if (mesh != null && mesh.VertexCount >= 8) break;
                    }
                }
            }
            else
            {
                foreach (var (name, mesh) in gd.LoadPedDictionary(ped))
                {
                    var diffuse = mesh.SubMeshes.Select(sb => sb.Diffuse).FirstOrDefault(d => d != null);
                    var parsed = PedNaming.ParseFile(name, ped);
                    var slot = parsed != null ? $"{parsed.Slot}/{parsed.Number}" : diffuse != null && PedNaming.ParseDiffuse(diffuse) is { } pd ? $"{pd.slot}/{pd.number} (by diffuse)" : "?";
                    var tex = diffuse != null ? (gd.LoadTexture(ped, diffuse) != null ? "ytd" : "MISSING") : "-";
                    Console.WriteLine($"  {name,-28} {mesh.VertexCount,6} verts  slot={slot,-22} diffuse={diffuse} ({tex}){(mesh.BlendBoneTags != null ? $"  own skeleton {mesh.BlendBoneTags.Length} bones" : "")}");
                }
            }
            Console.WriteLine($"  in {swp.ElapsedMilliseconds} ms");
        }
        break;
    }
    case "skinbones":
    {
        // Component drawables may embed the (partial) skeleton they are skinned to; the viewer remaps their blend
        // indices to the ped's .yft by bone tag. Lists the peds where that remap changes numbers or cannot resolve a tag.
        using var gd = OpenGta();
        var filter = rest.ElementAtOrDefault(1);
        var peds = gd.ListPeds().Where(p => filter == null || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        int scanned = 0, embedded = 0, renumbered = 0, unresolved = 0;
        foreach (var p in peds)
        {
            var skeleton = gd.LoadSkeleton(p.Name);
            if (skeleton == null) continue;
            IEnumerable<(string file, MeshData mesh)> meshes;
            try
            {
                meshes = p.Storage == PedStorage.Folder
                    ? gd.PedFolderFiles(p.Name).Select(f => (f, gd.LoadPedComponent(p.Name, f))).Where(x => x.Item2 != null).Select(x => (x.f, x.Item2!)).ToList()
                    : gd.LoadPedDictionary(p.Name).ToList();
            }
            catch (Exception ex) { Console.WriteLine($"  {p.Name}: {ex.Message}"); continue; }
            scanned++;
            var notes = new List<string>();
            foreach (var (file, mesh) in meshes)
            {
                if (mesh.BlendBoneTags == null) continue;
                embedded++;
                var missing = new List<int>();
                bool identity = true;
                for (int i = 0; i < mesh.BlendBoneTags.Length; i++)
                {
                    var index = skeleton.IndexOfTag(mesh.BlendBoneTags[i]);
                    if (index < 0) missing.Add(i);
                    else if (index != i) identity = false;
                }
                if (missing.Count > 0) { unresolved++; notes.Add($"{file}: {missing.Count} of {mesh.BlendBoneTags.Length} tags missing"); }
                else if (!identity) { renumbered++; notes.Add($"{file}: {mesh.BlendBoneTags.Length} bones renumbered (yft {skeleton.Bones.Count})"); }
            }
            if (notes.Count > 0) Console.WriteLine($"{p.Name} ({p.Storage}): " + string.Join("; ", notes));
        }
        Console.WriteLine($"{scanned} peds scanned: {embedded} drawables with an embedded skeleton, {renumbered} renumbered, {unresolved} with unresolved tags");
        break;
    }
    case "cloth":
    {
        using var gd = OpenGta();
        var filter = rest.ElementAtOrDefault(1);
        int pedsWithCloth = 0, parts = 0;
        foreach (var p in gd.ListPeds().Where(p => filter == null || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            List<(string file, MeshData mesh)> meshes;
            try
            {
                meshes = p.Storage == PedStorage.Folder
                    ? gd.PedFolderFiles(p.Name).Where(f => gd.HasPedCloth(p.Name, f)).Select(f => (f, gd.LoadPedComponent(p.Name, f))).Where(x => x.Item2 != null).Select(x => (x.f, x.Item2!)).ToList()
                    : gd.HasPedCloth(p.Name) ? gd.LoadPedDictionary(p.Name) : new();
            }
            catch (Exception ex) { Console.WriteLine($"  {p.Name}: {ex.Message}"); continue; }
            var cloth = meshes.Where(m => m.mesh.SubMeshes.Any(s => s.Cloth)).ToList();
            if (cloth.Count == 0) continue;
            pedsWithCloth++; parts += cloth.Count;
            var items = cloth.Select(m =>
            {
                var parsed = PedNaming.ParseFile(m.file, p.Name);
                var isDefault = parsed?.Number == 0;
                var verts = m.mesh.SubMeshes.Where(s => s.Cloth).Sum(s => s.IndexCount) / 3;
                var warn = m.mesh.Warnings.Count > 0 ? " WARN " + string.Join("; ", m.mesh.Warnings) : "";
                float minSum = float.MaxValue, maxSum = 0;
                for (int v = 0; v < m.mesh.VertexCount; v++)
                {
                    var sum = m.mesh.BlendWeights![v * 4] + m.mesh.BlendWeights[v * 4 + 1] + m.mesh.BlendWeights[v * 4 + 2] + m.mesh.BlendWeights[v * 4 + 3];
                    minSum = Math.Min(minSum, sum); maxSum = Math.Max(maxSum, sum);
                }
                var sums = minSum < 0.97f || maxSum > 1.03f ? $" weight sums {minSum:F2}..{maxSum:F2}" : "";
                return $"{m.file}{(isDefault ? "*" : "")} ({verts} tris{sums}){warn}";
            });
            Console.WriteLine($"{p.Name} ({p.Storage}): " + string.Join(", ", items));
        }
        Console.WriteLine($"{pedsWithCloth} peds with cloth-simulated parts ({parts} drawables); * = default variation");
        break;
    }
    case "yld":
    {
        // Raw contents of a folder ped's cloth dictionary (what the cloth simulation works with).
        if (rest.Count < 2) return Usage();
        using var gd = OpenGta();
        var parts = rest[1].Split('/', 2);
        var dict = parts.Length == 2 ? gd.LoadRawPedCloth(parts[0], parts[1]) : null;
        if (dict?.Values?.Entries == null) { Console.WriteLine($"{rest[1]}: not found"); break; }
        for (int i = 0; i < dict.Values.Entries.Count; i++)
        {
            var c = dict.Values.Entries[i];
            var hash = dict.Hashes?.Entries != null && i < dict.Hashes.Entries.Count ? dict.Hashes.Entries[i] : 0u;
            Console.WriteLine($"cloth {i} 0x{hash:X8}: poses={c.Poses?.Entries?.Count} unk30={c.Unknown_30h?.Entries?.Count} boneIndex={c.BoneIndex?.Entries?.Count} [{string.Join(",", Enumerable.Range(0, Math.Min(c.BoneIndex?.Entries?.Count ?? 0, 40)).Select(k => c.BoneIndex!.Entries![k]))}]");
            Console.WriteLine($"  unk50 matrix: {c.Unknown_50h}");
            var ctl = c.Controller;
            if (ctl != null)
            {
                Console.WriteLine($"  controller '{ctl.Name}' type={ctl.Type}: tri={ctl.TriIndices?.Entries?.Count} originalPos={ctl.OriginalPos?.Entries?.Count} boneIndexMap={ctl.BoneIndexMap?.Entries?.Count} binding={ctl.BindingInfo?.Entries?.Count} boneIdMap={ctl.BoneIDMap?.Entries?.Count} unkA0={ctl.Unknown_A0h} unkDC={ctl.Unknown_DCh}");
                if (ctl.BoneIndexMap != null) Console.WriteLine($"    boneIndexMap: {string.Join(",", Enumerable.Range(0, ctl.BoneIndexMap.Entries!.Count).Select(k => ctl.BoneIndexMap.Entries![k]))}");
                if (ctl.BoneIDMap != null) Console.WriteLine($"    boneIdMap: {string.Join(",", Enumerable.Range(0, ctl.BoneIDMap.Entries!.Count).Select(k => ctl.BoneIDMap.Entries![k]))}");
                if (ctl.OriginalPos != null) for (int k = 0; k < Math.Min(6, ctl.OriginalPos.Entries!.Count); k++) Console.WriteLine($"    originalPos[{k}] = {ctl.OriginalPos.Entries![k]}");
                if (ctl.BindingInfo != null) for (int k = 0; k < Math.Min(6, ctl.BindingInfo.Entries!.Count); k++) { var b = ctl.BindingInfo.Entries![k]; Console.WriteLine($"    binding[{k}] = w{b.Weights} idx {b.BlendIndex0},{b.BlendIndex1},{b.BlendIndex2},{b.BlendIndex3}"); }
                var bridge = ctl.BridgeSimGfx;
                if (bridge != null)
                {
                    Console.WriteLine($"  bridge: count={bridge.Count} unk14={bridge.Unknown_14h} unk18={bridge.Unknown_18h} pinRadius={bridge.PinRadius0?.Entries?.Count}/{bridge.PinRadius1?.Entries?.Count}/{bridge.PinRadius2?.Entries?.Count}/{bridge.PinRadius3?.Entries?.Count} vertexWeight={bridge.VertexWeight0?.Entries?.Count}/{bridge.VertexWeight1?.Entries?.Count} inflation={bridge.InflationScale0?.Entries?.Count} displayMap={bridge.ClothDisplayMap0?.Entries?.Count}/{bridge.ClothDisplayMap1?.Entries?.Count}/{bridge.ClothDisplayMap2?.Entries?.Count}/{bridge.ClothDisplayMap3?.Entries?.Count} unk128={bridge.Unknown_128h?.Entries?.Count}");
                    if (bridge.ClothDisplayMap0 != null) Console.WriteLine($"    displayMap0 first: {string.Join(",", Enumerable.Range(0, Math.Min(24, bridge.ClothDisplayMap0.Entries!.Count)).Select(k => bridge.ClothDisplayMap0.Entries![k]))} max={Enumerable.Range(0, bridge.ClothDisplayMap0.Entries!.Count).Max(k => bridge.ClothDisplayMap0.Entries![k])}");
                    if (bridge.PinRadius0 != null) Console.WriteLine($"    pinRadius0 first: {string.Join(",", Enumerable.Range(0, Math.Min(24, bridge.PinRadius0.Entries!.Count)).Select(k => bridge.PinRadius0.Entries![k].ToString("F3")))}");
                    if (bridge.VertexWeight0 != null) Console.WriteLine($"    vertexWeight0 first: {string.Join(",", Enumerable.Range(0, Math.Min(24, bridge.VertexWeight0.Entries!.Count)).Select(k => bridge.VertexWeight0.Entries![k].ToString("F3")))}");
                    if (bridge.Unknown_128h != null) Console.WriteLine($"    unk128 first: {string.Join(",", Enumerable.Range(0, Math.Min(24, bridge.Unknown_128h.Entries!.Count)).Select(k => bridge.Unknown_128h.Entries![k]))}");
                }
                foreach (var (vn, v) in new[] { ("verlet1", ctl.VerletCloth1), ("verlet2", ctl.VerletCloth2), ("verlet3", ctl.VerletCloth3) })
                {
                    if (v == null) continue;
                    Console.WriteLine($"  {vn}: unk30={v.Unknown_30h} unk34={v.Unknown_34h} unk38={v.Unknown_38h} unk3C={v.Unknown_3Ch} unk40={v.Unknown_40h} unk44={v.Unknown_44h} unk48={v.Unknown_48h} unk4C={v.Unknown_4Ch} unk50={v.Unknown_50h} unk70={v.Unknown_70h?.Entries?.Count} unk80={v.Unknown_80h?.Entries?.Count} unkA8={v.Unknown_A8h} unkAC={v.Unknown_ACh} unkE8={v.Unknown_E8h} numEdges={v.NumEdges} unkF0={v.Unknown_F0h} unkF8={v.Unknown_F8h} customEdges={v.CustomEdgeData?.Entries?.Count} edges={v.EdgeData?.Entries?.Count} unk148={v.Unknown_148h}");
                    if (v.Unknown_70h != null) for (int k = 0; k < Math.Min(4, v.Unknown_70h.Entries!.Count); k++) Console.WriteLine($"    unk70[{k}] = {v.Unknown_70h.Entries![k]}");
                    if (v.Unknown_80h != null) for (int k = 0; k < Math.Min(4, v.Unknown_80h.Entries!.Count); k++) Console.WriteLine($"    unk80[{k}] = {v.Unknown_80h.Entries![k]}");
                    if (v.EdgeData != null) for (int k = 0; k < Math.Min(4, v.EdgeData.Entries!.Count); k++) { var e = v.EdgeData.Entries![k]; Console.WriteLine($"    edge[{k}] = {e.vertIndices0}-{e.vertIndices1} len2={e.EdgeLength2} w0={e.Weight0} cw={e.CompressionWeight}"); }
                }
            }
            if (Opt2("--dump") is { } dumpDir && ctl != null)
            {
                Directory.CreateDirectory(dumpDir);
                string V(System.Numerics.Vector4 v) => $"{v.X},{v.Y},{v.Z},{v.W}";
                if (ctl.OriginalPos?.Entries != null) File.WriteAllLines(Path.Combine(dumpDir, "originalPos.csv"), Enumerable.Range(0, ctl.OriginalPos.Entries.Count).Select(k => V(ctl.OriginalPos.Entries[k])));
                if (ctl.VerletCloth1?.Unknown_80h?.Entries is { } vp) File.WriteAllLines(Path.Combine(dumpDir, "verletPos.csv"), Enumerable.Range(0, vp.Count).Select(k => V(vp[k])));
                if (ctl.VerletCloth1?.Unknown_70h?.Entries is { } vn) File.WriteAllLines(Path.Combine(dumpDir, "verletNormal.csv"), Enumerable.Range(0, vn.Count).Select(k => V(vn[k])));
                if (ctl.VerletCloth1?.EdgeData?.Entries is { } ed) File.WriteAllLines(Path.Combine(dumpDir, "edges.csv"), Enumerable.Range(0, ed.Count).Select(k => $"{ed[k].vertIndices0},{ed[k].vertIndices1},{ed[k].EdgeLength2},{ed[k].Weight0},{ed[k].CompressionWeight}"));
                if (ctl.VerletCloth1?.CustomEdgeData?.Entries is { } ced) File.WriteAllLines(Path.Combine(dumpDir, "customEdges.csv"), Enumerable.Range(0, ced.Count).Select(k => $"{ced[k].vertIndices0},{ced[k].vertIndices1},{ced[k].EdgeLength2},{ced[k].Weight0},{ced[k].CompressionWeight}"));
                if (ctl.TriIndices?.Entries is { } ti) File.WriteAllLines(Path.Combine(dumpDir, "tri.csv"), Enumerable.Range(0, ti.Count).Select(k => ti[k].ToString()));
                if (ctl.BindingInfo?.Entries is { } bd) File.WriteAllLines(Path.Combine(dumpDir, "binding.csv"), Enumerable.Range(0, bd.Count).Select(k => $"{V(bd[k].Weights)},{bd[k].BlendIndex0},{bd[k].BlendIndex1},{bd[k].BlendIndex2},{bd[k].BlendIndex3}"));
                if (ctl.BridgeSimGfx?.ClothDisplayMap0?.Entries is { } dm) File.WriteAllLines(Path.Combine(dumpDir, "displayMap.csv"), Enumerable.Range(0, dm.Count).Select(k => dm[k].ToString()));
                if (ctl.BridgeSimGfx?.PinRadius0?.Entries is { } pr) File.WriteAllLines(Path.Combine(dumpDir, "pinRadius.csv"), Enumerable.Range(0, pr.Count).Select(k => pr[k].ToString()));
                if (ctl.BridgeSimGfx?.VertexWeight0?.Entries is { } vw) File.WriteAllLines(Path.Combine(dumpDir, "vertexWeight.csv"), Enumerable.Range(0, vw.Count).Select(k => vw[k].ToString()));
                if (c.Poses?.Entries is { } ps) File.WriteAllLines(Path.Combine(dumpDir, "poses.csv"), Enumerable.Range(0, ps.Count).Select(k => $"{BitConverter.Int32BitsToSingle((int)ps[k].Unknown_0h)},{BitConverter.Int32BitsToSingle((int)ps[k].Unknown_4h)},{BitConverter.Int32BitsToSingle((int)ps[k].Unknown_8h)},{BitConverter.Int32BitsToSingle((int)ps[k].Unknown_Ch)}"));
            }
            var morph = c.Controller?.MorphController;
            if (morph != null)
                foreach (var (mn, m) in new[] { ("morph18", morph.Unknown_18h_Data), ("morph20", morph.Unknown_20h_Data), ("morph28", morph.Unknown_28h_Data) })
                {
                    if (m == null) continue;
                    string L<T>(RageLib.Resources.Common.SimpleList64<T>? l, int n = 12) where T : unmanaged => l?.Entries == null ? "-" : $"{l.Entries.Count}[{string.Join(",", Enumerable.Range(0, Math.Min(n, l.Entries.Count)).Select(k => l.Entries[k]!.ToString()))}]";
                    Console.WriteLine($"  {mn}: u50={L(m.Unknown_50h, 3)} u60={L(m.Unknown_60h)} u70={L(m.Unknown_70h)} u80={L(m.Unknown_80h)} u90={L(m.Unknown_90h)} uA0={L(m.Unknown_A0h, 3)} uB0={L(m.Unknown_B0h)} uC0={L(m.Unknown_C0h)} uD0={L(m.Unknown_D0h)} uE0={L(m.Unknown_E0h)} u150={L(m.Unknown_150h)} u160={L(m.Unknown_160h)} u180={m.Unknown_180h}");
                }
            if (c.Poses?.Entries != null) for (int k = 0; k < 4; k++) { var pv = c.Poses.Entries[k]; Console.WriteLine($"  poses[{k}] = {BitConverter.Int32BitsToSingle((int)pv.Unknown_0h)},{BitConverter.Int32BitsToSingle((int)pv.Unknown_4h)},{BitConverter.Int32BitsToSingle((int)pv.Unknown_8h)},{BitConverter.Int32BitsToSingle((int)pv.Unknown_Ch)} raw {pv.Unknown_0h:X8} {pv.Unknown_4h:X8} {pv.Unknown_8h:X8} {pv.Unknown_Ch:X8}"); }
            if (c.Unknown_30h?.Entries != null) Console.WriteLine($"  unk30: {string.Join(",", Enumerable.Range(0, c.Unknown_30h.Entries.Count).Select(k => c.Unknown_30h.Entries[k]))}");
            var bc = c.BoundComposite;
            Console.WriteLine($"  bounds: {(bc == null ? "none" : $"{bc.NumBounds} children: {string.Join(",", (bc.Bounds?.data_items ?? new()).Select(b => b?.GetType().Name ?? "null"))}")}");
        }
        break;
    }
    case "dds":
    {
        if (rest.Count < 3) return Usage();
        using var gd = OpenGta();
        var scope = rest[1]; var texName = rest[2];
        EmotePreviewer.Core.Textures.TextureImage? img;
        if (gd.PedStorageOf(scope) == PedStorage.Folder) img = gd.LoadPedTexture(scope, texName);
        else if (gd.PedStorageOf(scope) == PedStorage.Component) img = gd.LoadTexture(scope, texName);
        else img = gd.LoadDrawable(scope)?.FindEmbedded(texName) ?? gd.LoadTexture(scope, texName);
        if (img == null) { Console.WriteLine($"{scope}/{texName}: not found"); break; }
        var dds = img.ToDds();
        Console.WriteLine($"{img.Name}: {img.Width}x{img.Height} {img.Format} levels={img.Levels} (full chain {img.FullChainLevels}) data={img.Data.Length} dds={dds.Length} bytes intensityMap={img.IsIntensityMap}");
        var outPath = rest.ElementAtOrDefault(3);
        if (outPath != null) { File.WriteAllBytes(outPath, dds); Console.WriteLine("wrote " + outPath); }
        if (img.CanDecode)
        {
            var rgba = img.DecodeToRgba();
            Console.WriteLine($"  decoded {rgba.Width}x{rgba.Height} rgba, first pixel {rgba.Data[0]},{rgba.Data[1]},{rgba.Data[2]},{rgba.Data[3]}");
            if (outPath != null) File.WriteAllBytes(Path.ChangeExtension(outPath, ".rgba.dds"), rgba.ToDds());
        }
        break;
    }
    case "animals":
    {
        var cat = BuildCatalog();
        using var gd = OpenGta();
        var animals = cat.Entries.Where(e => e.IsAnimal).ToList();
        Console.WriteLine($"{animals.Count} animal entries");
        foreach (var g in animals.GroupBy(e => e.AnimalPed ?? "?").OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-20} x{g.Count(),-4} skeleton={(gd.HasSkeleton(g.Key) ? "yes" : "NO")}  e.g. {string.Join(", ", g.Take(3).Select(e => $"{e.Command} ({e.Dictionary})"))}");
        break;
    }
    case "shared":
    {
        var cat = BuildCatalog();
        var check = rest.Remove("--gta-check");
        using var gd = check ? OpenGta() : null;
        var shared = cat.Entries.Where(e => e.IsShared).ToList();
        var dictCache = new Dictionary<string, IClipDictionary?>(StringComparer.OrdinalIgnoreCase);
        string ClipState(EmoteEntry e)
        {
            if (gd == null) return "?";
            if (!e.HasClip) return "no-clip-name";
            if (!dictCache.TryGetValue(e.Dictionary!, out var d))
            {
                try { d = e.IsCustom ? gd.LoadLooseClipDictionary(e.CustomYcdPath!) : gd.HasClipDictionary(e.Dictionary!) ? gd.LoadClipDictionary(e.Dictionary!) : null; }
                catch (Exception) { d = null; }
                dictCache[e.Dictionary!] = d;
            }
            return d == null ? "no-dict" : d.FindClip(e.Clip!) != null ? "ok" : "no-clip";
        }
        static string Place(SharedPlacement? p) => p == null ? "-" : p.Kind == PlacementKind.Attach
            ? $"attach bone={p.Bone} pos=({p.Placement[0]},{p.Placement[1]},{p.Placement[2]}) rot=({p.Placement[3]},{p.Placement[4]},{p.Placement[5]})"
            : $"offset side={p.Side} front={p.Front}{(p.Height != 0 ? $" height={p.Height}" : "")}{(p.Heading != 180 ? $" heading={p.Heading}" : "")}";
        Console.WriteLine($"{shared.Count} shared entries in {shared.Select(e => e.Source).Distinct().Count()} source(s)");
        int unresolved = 0, selfPairs = 0, attach = 0, offset = 0, none = 0, animal = 0;
        foreach (var e in shared)
        {
            var partner = e.PartnerId != null ? cat.FindById(e.PartnerId) : null;
            if (partner == null) unresolved++;
            else if (ReferenceEquals(partner, e)) selfPairs++;
            var how = e.Placement?.Kind == PlacementKind.Attach ? "attach" : e.Placement?.Kind == PlacementKind.Offset ? "offset"
                    : partner?.Placement?.Kind == PlacementKind.Attach ? "attach*" : partner?.Placement?.Kind == PlacementKind.Offset ? "offset*" : "default";
            if (how.StartsWith("attach")) attach++; else if (how.StartsWith("offset")) offset++; else none++;
            if (e.IsAnimal || partner?.IsAnimal == true) animal++;
            Console.WriteLine($"  {e.Id,-52} -> {(partner == null ? $"{e.PartnerCommand} (NOT FOUND)" : partner.Command),-24} {how,-8} {Place(e.Placement)}"
                + (e.StartDelayMs != 0 ? $" delay={e.StartDelayMs}" : "") + (e.IsAnimal ? " [animal]" : "") + (partner?.IsAnimal == true ? " [animal partner]" : "")
                + (gd != null ? $" clip={ClipState(e)}/{(partner == null ? "-" : ClipState(partner))}" : ""));
        }
        Console.WriteLine($"  partner resolved: {shared.Count - unresolved}/{shared.Count} (self-pairs {selfPairs}, unresolved {unresolved}); placement: attach {attach}, offset {offset}, default {none}; with an animal side: {animal}");
        break;
    }
    case "pose":
    {
        if (rest.Count < 3) return Usage();
        var cat = BuildCatalog();
        using var gd = OpenGta();
        var skel = gd.LoadSkeleton(skeletonName) ?? throw new FileNotFoundException(skeletonName + ".yft not found");
        var dict = (cat.CustomYcds.TryGetValue(rest[1], out var loose) ? gd.LoadLooseClipDictionary(loose) : gd.LoadClipDictionary(rest[1])) ?? throw new FileNotFoundException("dictionary not found: " + rest[1]);
        var clip = dict.FindClip(rest[2]) ?? throw new KeyNotFoundException("clip not found: " + rest[2]);
        var t = rest.Count > 3 ? double.Parse(rest[3], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var solver = new EmotePreviewer.Core.Anim.PoseSolver(skel);
        var sample = new ClipSample();
        clip.Sample(t, sample);
        solver.Apply(sample);
        var matched = clip.Tracks.Select(tr => tr.BoneTag).Distinct().Count(tag => skel.IndexOfTag(tag) >= 0);
        Console.WriteLine($"{dict.Name} / {clip.Name}: duration={clip.Duration:F3}s native={clip.NativeFrameRate:F1}fps t={t}; skeleton {skel.Name} has {skel.Bones.Count} bones; {matched} of {clip.Tracks.Select(tr => tr.BoneTag).Distinct().Count()} animated bone tags exist in it");
        foreach (var b in skel.Bones)
        {
            var p = solver.WorldPosition(b.Index);
            Console.WriteLine($"  {b.Index,3} {b.Name,-24} ({p.X,8:F4}, {p.Y,8:F4}, {p.Z,8:F4})");
        }
        break;
    }
    case "axes":
    {
        // devtools axes <dictionary> <clip> <bone> [bone...]   rotation-axis statistics of a bone's local rotation relative to its bind pose
        if (rest.Count < 4) return Usage();
        var cat = BuildCatalog();
        using var gd = OpenGta();
        var skel = gd.LoadSkeleton(skeletonName) ?? throw new FileNotFoundException(skeletonName + ".yft not found");
        var dict = (cat.CustomYcds.TryGetValue(rest[1], out var loose) ? gd.LoadLooseClipDictionary(loose) : gd.LoadClipDictionary(rest[1])) ?? throw new FileNotFoundException("dictionary not found: " + rest[1]);
        var clip = dict.FindClip(rest[2]) ?? throw new KeyNotFoundException("clip not found: " + rest[2]);
        var sample = new ClipSample();
        Console.WriteLine($"{dict.Name} / {clip.Name}: duration={clip.Duration:F3}s");
        foreach (var boneName in rest.Skip(3))
        {
            var bone = skel.Bones.FirstOrDefault(b => b.Name.Equals(boneName, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException("bone not found: " + boneName);
            var bindInv = System.Numerics.Quaternion.Inverse(bone.Rotation);
            int n = 0, nearX = 0, nearY = 0, nearZ = 0; var mean = System.Numerics.Vector3.Zero; double maxDeg = 0;
            var cos25 = Math.Cos(Math.PI * 25 / 180);
            for (double t = 0; t < clip.Duration; t += 1.0 / 30)
            {
                sample.Clear();
                clip.Sample(t, sample);
                if (!sample.Rotations.TryGetValue(bone.Tag, out var q)) continue;
                var rel = System.Numerics.Quaternion.Normalize(bindInv * q);
                if (rel.W < 0) rel = -rel;
                var ang = 2 * Math.Acos(Math.Clamp(rel.W, -1, 1)) * 180 / Math.PI;
                if (ang < 25) continue;
                var s = Math.Sqrt(Math.Max(1e-12, 1 - rel.W * rel.W));
                var ax = new System.Numerics.Vector3(rel.X, rel.Y, rel.Z) / (float)s;
                n++; mean += ax; maxDeg = Math.Max(maxDeg, ang);
                if (Math.Abs(ax.X) > cos25) nearX++; if (Math.Abs(ax.Y) > cos25) nearY++; if (Math.Abs(ax.Z) > cos25) nearZ++;
            }
            if (n == 0) { Console.WriteLine($"  {bone.Name,-18} no rotation > 25 deg (bind rot {bone.Rotation})"); continue; }
            mean /= n;
            Console.WriteLine($"  {bone.Name,-18} n={n,4} mean axis=({mean.X,5:F2},{mean.Y,5:F2},{mean.Z,5:F2}) max={maxDeg,4:F0} deg  nearX={100.0 * nearX / n,3:F0}% nearY={100.0 * nearY / n,3:F0}% nearZ={100.0 * nearZ / n,3:F0}%  bind rot=({bone.Rotation.X:F3},{bone.Rotation.Y:F3},{bone.Rotation.Z:F3},{bone.Rotation.W:F3})");
        }
        break;
    }
    case "find":
    {
        if (rest.Count < 2) return Usage();
        using var gd = OpenGta();
        Console.WriteLine($"ycd: {gd.FindClipDictionaryPath(rest[1]) ?? "(not found)"}");
        Console.WriteLine($"yft: {gd.FindSkeletonPath(rest[1]) ?? "(not found)"}");
        Console.WriteLine($"ydr/ydd: {gd.FindDrawablePath(rest[1]) ?? "(not found)"}");
        Console.WriteLine("paths containing the name:");
        foreach (var p in gd.FindEntries(rest[1], 5000)) Console.WriteLine("  " + p);
        break;
    }
    default:
        return Usage();
}
Console.Error.WriteLine($"[done in {sw.Elapsed.TotalSeconds:F1}s]");
return 0;

int Usage()
{
    Console.Error.WriteLine("usage: devtools <regression | coverage | find | skeleton | yft | bones | bake | pose | axes | mesh | props | ped | walks | clipsets | tex | pedtex | ytd | ydd | missing | peds | pedinfo | dds | animals | shared> [args] [--data <folder>] [--gta <folder>] [--keys <folder>]");
    return 64;
}

EmoteCatalog BuildCatalog()
{
    Console.Error.WriteLine("data root: " + dataRoot);
    var cat = CatalogBuilder.BuildFromDataRoot(dataRoot);
    foreach (var w in cat.Warnings) Console.Error.WriteLine("  WARN " + w);
    Console.Error.WriteLine($"catalog: {cat.Entries.Count} entries from {cat.Sources.Count} resources ({string.Join(", ", cat.Sources.Select(s => s.Id))}), {cat.CustomYcds.Count} shipped .ycd");
    return cat;
}

GtaToolkitGameData OpenGta()
{
    if (!GtaLocator.IsGtaFolder(gtaFolder)) throw new DirectoryNotFoundException("GTA folder not found; pass --gta <folder>");
    Console.Error.WriteLine("GTA folder: " + gtaFolder);
    return GtaToolkitGameData.Open(gtaFolder!, keysFolder, s => Console.Error.WriteLine("  " + s), e => Console.Error.WriteLine("  ERR " + e));
}

void Coverage(int limit)
{
    var cat = BuildCatalog();
    using var gd = OpenGta();
    var anims = cat.Entries.Where(e => e.Kind == EmoteKind.Animation && e.Dictionary != null && e.Clip != null).Take(limit).ToList();
    int dictOk = 0, clipOk = 0, customOk = 0, customClipOk = 0;
    var cache = new Dictionary<string, IClipDictionary?>(StringComparer.OrdinalIgnoreCase);
    var missingDict = new List<EmoteEntry>();
    var missingClip = new List<EmoteEntry>();
    foreach (var e in anims)
    {
        if (!cache.TryGetValue(e.Dictionary!, out var dict))
        {
            try { dict = e.IsCustom ? gd.LoadLooseClipDictionary(e.CustomYcdPath!) : gd.LoadClipDictionary(e.Dictionary!); }
            catch (Exception ex) { Console.Error.WriteLine($"  load fail {e.Dictionary}: {ex.Message}"); dict = null; }
            cache[e.Dictionary!] = dict;
        }
        if (dict == null) { missingDict.Add(e); continue; }
        dictOk++;
        if (e.IsCustom) customOk++;
        if (dict.FindClip(e.Clip!) != null) { clipOk++; if (e.IsCustom) customClipOk++; }
        else missingClip.Add(e);
    }
    Console.WriteLine($"animation entries: {anims.Count}");
    Console.WriteLine($"  dictionary found: {dictOk} (custom {customOk})");
    Console.WriteLine($"  dict+clip found : {clipOk} (custom {customClipOk})  => previewable {(100.0 * clipOk / Math.Max(1, anims.Count)):F1}%");
    Console.WriteLine($"  missing dict    : {missingDict.Count}");
    foreach (var e in missingDict.Take(30)) Console.WriteLine("    " + e);
    Console.WriteLine($"  missing clip    : {missingClip.Count}");
    foreach (var e in missingClip.Take(30)) Console.WriteLine("    " + e);
}

void BakeAll(int limit)
{
    var cat = BuildCatalog();
    using var gd = OpenGta();
    var skel = gd.LoadSkeleton("mp_m_freemode_01") ?? throw new FileNotFoundException("mp_m_freemode_01.yft not found");
    var anims = cat.Entries.Where(e => e.Kind == EmoteKind.Animation && !e.IsAnimal && e.Dictionary != null && e.Clip != null).Take(limit).ToList();
    var cache = new Dictionary<string, IClipDictionary?>(StringComparer.OrdinalIgnoreCase);
    int baked = 0, skipped = 0, failed = 0, warned = 0;
    long totalBytes = 0, maxBytes = 0;
    string? biggest = null;
    var fpsHist = new SortedDictionary<int, int>();
    var swAll = Stopwatch.StartNew();
    double slowest = 0; string? slowestName = null;
    foreach (var e in anims)
    {
        if (!cache.TryGetValue(e.Dictionary!, out var dict))
        {
            try { dict = e.IsCustom ? gd.LoadLooseClipDictionary(e.CustomYcdPath!) : gd.LoadClipDictionary(e.Dictionary!); }
            catch { dict = null; }
            cache[e.Dictionary!] = dict;
        }
        var clip = dict?.FindClip(e.Clip!);
        if (clip == null) { skipped++; continue; }
        try
        {
            var sw = Stopwatch.StartNew();
            var b = EmotePreviewer.Core.Anim.ClipBaker.Bake(clip, skel);
            var ms = sw.Elapsed.TotalMilliseconds;
            baked++;
            long bytes = (long)b.TotalFloats * 4;
            totalBytes += bytes;
            if (bytes > maxBytes) { maxBytes = bytes; biggest = $"{e.Dictionary}/{e.Clip} ({b.Frames} f @ {b.Fps} fps, {b.Duration:F1} s)"; }
            if (ms > slowest) { slowest = ms; slowestName = $"{e.Dictionary}/{e.Clip}"; }
            fpsHist[b.Fps] = fpsHist.TryGetValue(b.Fps, out var n) ? n + 1 : 1;
            if (b.Fps <= 2 && fpsHist[b.Fps] <= 8) Console.WriteLine($"  low fps {e}: native {clip.NativeFrameRate:F2}, duration {clip.Duration:F2}, frames {b.Frames}, {(clip is GtClip g ? g.TimingInfo : "")}");
            if (b.Warnings.Count > 0) { warned++; if (warned <= 20) Console.WriteLine($"  warn {e}: {string.Join("; ", b.Warnings)}"); }
        }
        catch (Exception ex)
        {
            failed++;
            if (failed <= 30) Console.WriteLine($"  FAIL {e}: {ex.GetType().Name}: {ex.Message}");
        }
    }
    Console.WriteLine($"baked {baked} clips in {swAll.Elapsed.TotalSeconds:F1} s ({(baked > 0 ? swAll.Elapsed.TotalMilliseconds / baked : 0):F1} ms avg, slowest {slowest:F0} ms {slowestName})");
    Console.WriteLine($"  skipped (no dict/clip): {skipped}, failed: {failed}, with warnings: {warned}");
    Console.WriteLine($"  total {totalBytes / (1024 * 1024)} MB, biggest {maxBytes / 1024} KB: {biggest}");
    Console.WriteLine($"  fps: {string.Join(", ", fpsHist.Select(kv => $"{kv.Key}={kv.Value}"))}");
}

static string FourCC(uint format)
{
    var chars = new[] { (char)(format & 0xFF), (char)((format >> 8) & 0xFF), (char)((format >> 16) & 0xFF), (char)((format >> 24) & 0xFF) };
    return chars.All(c => c >= ' ' && c < 127) ? new string(chars) : format.ToString();
}

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EmotePreviewer.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

static string? FindDataRoot()
{
    var d = Path.Combine(RepoRoot(), "data");
    return Directory.Exists(d) ? d : null;
}
