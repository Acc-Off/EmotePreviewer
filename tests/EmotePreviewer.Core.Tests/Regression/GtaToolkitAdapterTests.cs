using System.Text;
using EmotePreviewer.Core.Adapters.GtaToolkit;
using EmotePreviewer.Core.Model;
using EmotePreviewer.Fixtures;
using RageLib.Resources.GTA5.PC.Clips;
using Xunit.Abstractions;

namespace EmotePreviewer.Core.Tests.Regression;

/// <summary>
/// Integration test: opens the real GTA install through the gta-toolkit adapter and checks that every fixture clip is
/// still found, with byte-identical animation blocks. Skipped when GTA V, the key material or the fixture is absent.
/// </summary>
public sealed class GtaToolkitAdapterTests
{
    readonly ITestOutputHelper _out;
    public GtaToolkitAdapterTests(ITestOutputHelper output) => _out = output;

    static string? DetectGta()
    {
        var env = Environment.GetEnvironmentVariable("GTA_FOLDER");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "GTA5.exe"))) return env;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V");
            foreach (var name in new[] { "InstallFolderSteam", "InstallFolder", "InstallFolderEpic" })
                if (k?.GetValue(name) is string s && File.Exists(Path.Combine(s, "GTA5.exe"))) return s.TrimEnd('\\');
        }
        catch { }
        return null;
    }


    [SkippableFact]
    public void FixtureClipsResolveToSameArchivesAndBytes()
    {
        var fixture = FixtureLoader.Instance; Skip.If(fixture == null, "fixture file not found");
        var gta = DetectGta(); Skip.If(gta == null, "GTA V not installed");
        GtaToolkitGameData gd;
        try { gd = GtaToolkitGameData.Open(gta!, null, _out.WriteLine, e => _out.WriteLine("ERR " + e)); }
        catch (GtaKeys.KeyMaterialMissingException ex) { throw new SkipException(ex.Message); }
        using (gd)
        {
            var failures = new List<string>();
            // Archive resolution follows dlclist.xml; a clip that now resolves from a different archive is only a note
            // as long as the bytes are identical.
            var pathDiffs = new List<string>();
            var dictCache = new Dictionary<string, IClipDictionary?>(StringComparer.OrdinalIgnoreCase);
            int checkedClips = 0, checkedBlocks = 0;
            foreach (var oc in fixture!.Clips)
            {
                IClipDictionary? dict;
                if (oc.Custom)
                {
                    var path = oc.Source!["file:".Length..];
                    if (!File.Exists(path)) { failures.Add($"{oc}: loose file missing {path}"); continue; }
                    if (!dictCache.TryGetValue(path, out dict)) dictCache[path] = dict = gd.LoadLooseClipDictionary(path);
                }
                else
                {
                    var expectedPath = oc.Source!["rpf:".Length..];
                    var actualPath = gd.FindClipDictionaryPath(oc.Dictionary);
                    if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
                        pathDiffs.Add($"{oc}: fixture came from {expectedPath}, adapter resolves {actualPath ?? "(none)"}");
                    if (!dictCache.TryGetValue(oc.Dictionary, out dict)) dictCache[oc.Dictionary] = dict = gd.LoadClipDictionary(oc.Dictionary);
                }
                if (dict == null) { failures.Add($"{oc}: dictionary not loaded"); continue; }
                var clip = dict.FindClip(oc.Clip) as GtClip;
                if (clip == null) { failures.Add($"{oc}: clip not found"); continue; }
                checkedClips++;
                if (clip.FullName != oc.ClipFullName) failures.Add($"{oc}: full name {clip.FullName} != {oc.ClipFullName}");

                var anims = clip.RawAnimations().ToList();
                if (anims.Count != oc.Animations.Count) { failures.Add($"{oc}: {anims.Count} animations, fixture has {oc.Animations.Count}"); continue; }
                for (int a = 0; a < anims.Count; a++)
                {
                    var ra = anims[a]; var ea = oc.Animations[a];
                    var where = $"{oc} anim {ea.Hash:X8}";
                    if (ra.Unknown_14h != ea.Frames || ra.Unknown_16h != ea.SequenceFrameLimit || ra.Unknown_18h != ea.Duration)
                        failures.Add($"{where}: header (F={ra.Unknown_14h}, L={ra.Unknown_16h}, dur={ra.Unknown_18h}) != fixture ({ea.Frames}, {ea.SequenceFrameLimit}, {ea.Duration})");
                    var tracks = GtResourceHelpers.ToTracks(ra);
                    var expectedTracks = ea.TrackDefs();
                    if (!tracks.SequenceEqual(expectedTracks)) failures.Add($"{where}: track table differs");
                    var seqs = ra.Sequences?.Entries?.ToList() ?? new List<Sequence>();
                    if (seqs.Count != ea.Sequences.Count) { failures.Add($"{where}: {seqs.Count} blocks, fixture has {ea.Sequences.Count}"); continue; }
                    for (int s = 0; s < seqs.Count; s++)
                    {
                        checkedBlocks++;
                        var h = GtResourceHelpers.ToHeader(seqs[s]);
                        if (h != ea.Sequences[s].Header()) failures.Add($"{where} block {s}: header {h} != fixture {ea.Sequences[s].Header()}");
                        var expectedData = Convert.FromBase64String(ea.Sequences[s].Data);
                        if (!seqs[s].Data.AsSpan().SequenceEqual(expectedData)) failures.Add($"{where} block {s}: data bytes differ");
                    }
                }
            }
            _out.WriteLine($"checked {checkedClips} clips, {checkedBlocks} blocks; {pathDiffs.Count} resolved from a different (byte-identical) archive");
            foreach (var d in pathDiffs) _out.WriteLine("  note: " + d);
            if (failures.Count > 0)
            {
                var sb = new StringBuilder().AppendLine($"{failures.Count} mismatches:");
                foreach (var f in failures.Take(40)) sb.AppendLine("  " + f);
                Assert.Fail(sb.ToString());
            }
            Assert.True(checkedClips == fixture.Clips.Count);
        }
    }

    [SkippableFact]
    public void SkeletonMatchesKnownLayout()
    {
        var gta = DetectGta(); Skip.If(gta == null, "GTA V not installed");
        GtaToolkitGameData gd;
        try { gd = GtaToolkitGameData.Open(gta!); }
        catch (GtaKeys.KeyMaterialMissingException ex) { throw new SkipException(ex.Message); }
        using (gd)
        {
            var skel = gd.LoadSkeleton("mp_m_freemode_01");
            Assert.NotNull(skel);
            Assert.Equal(128, skel!.Bones.Count);
            Assert.Equal("SKEL_ROOT", skel.Bones[0].Name);
            Assert.Equal(-1, skel.Bones[0].ParentIndex);
            Assert.Equal(58271, skel.FindByTag(58271)!.Tag);
            Assert.Equal("SKEL_L_Thigh", skel.FindByTag(58271)!.Name);
        }
    }
}
