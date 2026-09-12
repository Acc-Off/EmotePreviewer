using System.Buffers.Binary;
using System.Numerics;
using EmotePreviewer.Core.Adapters.GtaToolkit;
using EmotePreviewer.Core.Gta;
using EmotePreviewer.Core.Model;
using Xunit.Abstractions;

namespace EmotePreviewer.Core.Tests.Mesh;

/// <summary>Fixes the mesh <c>.bin</c> layout and checks the extractor on real drawables when GTA V is available.</summary>
public sealed class MeshDataTests
{
    readonly ITestOutputHelper _out;
    public MeshDataTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void BytesArePositionsNormalsUvsSkinThenIndices()
    {
        var mesh = new MeshData
        {
            Name = "quad",
            Positions = new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 },
            Normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 },
            Uvs = new float[] { 0, 0, 1, 0, 1, 1, 0, 1 },
            BlendIndices = new ushort[] { 1, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0 },
            BlendWeights = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
            Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
            SubMeshes = new[] { new SubMesh(0, 6, 0x1234u) },
            Warnings = Array.Empty<string>(),
        };
        Assert.Equal(4, mesh.VertexCount);
        Assert.True(mesh.IsSkinned);

        var bytes = mesh.ToBytes();
        Assert.Equal(4 * 12 + 4 * 12 + 4 * 8 + 4 * 8 + 4 * 16 + 6 * 4, bytes.Length);
        // positions
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(3 * 4)));
        // normals start after positions
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(48 + 2 * 4)));
        // uvs after normals
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(96 + 2 * 4)));
        // blend indices (u16) after uvs
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(128)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(128 + 8 * 2)));
        // weights after indices, then the index buffer
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(160)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(224 + 5 * 4)));
    }

    [Fact]
    public void RigidMeshOmitsOptionalBlocks()
    {
        var mesh = new MeshData
        {
            Name = "tri",
            Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
            Indices = new uint[] { 0, 1, 2 },
            SubMeshes = new[] { new SubMesh(0, 3, 0u) },
            Warnings = Array.Empty<string>(),
        };
        Assert.False(mesh.IsSkinned);
        Assert.Equal(3 * 12 + 3 * 4, mesh.ToBytes().Length);
    }

    static GtaToolkitGameData OpenOrSkip()
    {
        var gta = GtaLocator.Detect();
        Skip.If(gta == null, "GTA V not installed");
        try { return GtaToolkitGameData.Open(gta!); }
        catch (GtaKeys.KeyMaterialMissingException ex) { throw new SkipException(ex.Message); }
    }

    [Fact]
    public void RemapBonesTranslatesEmbeddedSkeletonIndicesByTag()
    {
        // The drawable's own skeleton lists three bones (tags 0, 500, 700); the ped skeleton has them at other indices.
        var mesh = new MeshData
        {
            Name = "part",
            Positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
            BlendIndices = new ushort[] { 1, 2, 0, 0, 2, 2, 2, 2, 0, 1, 0, 0 },
            BlendWeights = new float[] { 0.5f, 0.5f, 0, 0, 1, 0, 0, 0, 0.5f, 0.5f, 0, 0 },
            BlendBoneTags = new ushort[] { 0, 500, 700 },
            Indices = new uint[] { 0, 1, 2 },
            SubMeshes = new[] { new SubMesh(0, 3, 0u) },
            Warnings = Array.Empty<string>(),
        };
        var skeleton = new SkeletonDef("ped", new[]
        {
            new BoneDef(0, 0, "SKEL_ROOT", -1, Vector3.Zero, Quaternion.Identity, Vector3.One),
            new BoneDef(1, 100, "SKEL_Pelvis", 0, Vector3.Zero, Quaternion.Identity, Vector3.One),
            new BoneDef(2, 700, "SKEL_Spine0", 1, Vector3.Zero, Quaternion.Identity, Vector3.One),
            new BoneDef(3, 500, "SKEL_Spine1", 2, Vector3.Zero, Quaternion.Identity, Vector3.One),
        });

        var remapped = mesh.RemapBones(skeleton);
        Assert.NotSame(mesh, remapped);
        Assert.Null(remapped.BlendBoneTags);
        Assert.Equal(new ushort[] { 3, 2, 0, 0, 2, 2, 2, 2, 0, 3, 0, 0 }, remapped.BlendIndices);
        Assert.Same(mesh.BlendWeights, remapped.BlendWeights);
        Assert.Empty(remapped.Warnings);

        // Without an embedded skeleton the indices already refer to the ped skeleton and nothing happens.
        var direct = new MeshData
        {
            Name = "direct", Positions = mesh.Positions, BlendIndices = mesh.BlendIndices, BlendWeights = mesh.BlendWeights,
            Indices = mesh.Indices, SubMeshes = mesh.SubMeshes, Warnings = mesh.Warnings,
        };
        Assert.Same(direct, direct.RemapBones(skeleton));
    }

    [Fact]
    public void RemapBonesFallsBackToRootForUnknownTags()
    {
        var mesh = new MeshData
        {
            Name = "part",
            Positions = new float[] { 0, 0, 0 },
            BlendIndices = new ushort[] { 1, 0, 0, 0 },
            BlendWeights = new float[] { 1, 0, 0, 0 },
            BlendBoneTags = new ushort[] { 0, 999 },
            Indices = new uint[] { 0, 0, 0 },
            SubMeshes = new[] { new SubMesh(0, 3, 0u) },
            Warnings = Array.Empty<string>(),
        };
        var skeleton = new SkeletonDef("ped", new[] { new BoneDef(0, 0, "SKEL_ROOT", -1, Vector3.Zero, Quaternion.Identity, Vector3.One) });
        var remapped = mesh.RemapBones(skeleton);
        Assert.Equal(new ushort[] { 0, 0, 0, 0 }, remapped.BlendIndices);
        Assert.Contains(remapped.Warnings, w => w.Contains("999"));
    }

    /// <summary>
    /// Breakable props are fragments whose parts (tray, cup, burger) are separate models placed on bones of the
    /// drawable's own skeleton. Their vertices are stored in bone space, so every part sat at the origin and the cup
    /// pierced the tray until the bone matrix was applied; with it, the whole mesh fits the drawable's bounding box.
    /// </summary>
    [SkippableFact]
    public void FragmentPartsSitOnTheirBones()
    {
        using var gd = OpenOrSkip();
        Skip.If(!gd.HasDrawable("prop_food_bs_tray_03"), "food tray prop not in this game version");
        var mesh = gd.LoadDrawable("prop_food_bs_tray_03")!;
        _out.WriteLine($"{mesh.Name}: {mesh.VertexCount} vertices, bounds {mesh.BoundsMin} .. {mesh.BoundsMax}");
        Assert.Empty(mesh.Warnings);
        Assert.False(mesh.IsSkinned);
        // One sub-mesh per geometry: model 0 is the tray, model 1 the cup (its bone sits 0.139 m above the tray origin),
        // model 2 the food. The cup must stand on the tray, not sink through it.
        Assert.Equal(3, mesh.SubMeshes.Count);
        (float min, float max) ZRange(SubMesh s)
        {
            float min = float.MaxValue, max = float.MinValue;
            for (int i = s.IndexStart; i < s.IndexStart + s.IndexCount; i++)
            {
                var z = mesh.Positions[mesh.Indices[i] * 3 + 2];
                min = Math.Min(min, z); max = Math.Max(max, z);
            }
            return (min, max);
        }
        var tray = ZRange(mesh.SubMeshes[0]);
        var cup = ZRange(mesh.SubMeshes[1]);
        var food = ZRange(mesh.SubMeshes[2]);
        _out.WriteLine($"z: tray {tray.min:F3}..{tray.max:F3} cup {cup.min:F3}..{cup.max:F3} food {food.min:F3}..{food.max:F3}");
        // The cup's vertices are centred on its bone (about 13.5 cm below and above it); left in bone space its floor
        // was 13.5 cm under the tray.
        Assert.True(cup.min > tray.min - 0.01f, $"cup floor {cup.min} is below the tray floor {tray.min}");
        Assert.True(cup.min < tray.max + 0.03f, $"cup floats {cup.min - tray.max} above the tray");
        Assert.True(cup.max > tray.max + 0.15f, $"cup top {cup.max} is not a cup's height above the tray");
        Assert.True(food.min > tray.min - 0.01f, $"food floor {food.min} is below the tray floor {tray.min}");
        Assert.All(Enumerable.Range(0, mesh.VertexCount), v =>
        {
            var n = new Vector3(mesh.Normals![v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]);
            Assert.InRange(n.Length(), 0.8f, 1.2f);
        });
    }

    [SkippableFact]
    public void PropDrawableExtractsWithSanePositions()
    {
        using var gd = OpenOrSkip();
        Skip.If(!gd.HasDrawable("p_amb_brolly_01"), "umbrella prop not in this game version");
        var mesh = gd.LoadDrawable("p_amb_brolly_01")!;
        _out.WriteLine($"{mesh.Name}: {mesh.VertexCount} vertices, {mesh.Indices.Length / 3} triangles, {mesh.SubMeshes.Count} sub-meshes");
        Assert.True(mesh.VertexCount > 100);
        Assert.NotNull(mesh.Normals);
        Assert.NotNull(mesh.Uvs);
        Assert.False(mesh.IsSkinned);
        Assert.Empty(mesh.Warnings);
        Assert.All(mesh.Indices, i => Assert.True(i < mesh.VertexCount));
        Assert.Equal(0, mesh.Indices.Length % 3);
        // An umbrella is about 1.2 m wide and 1 m tall; every vertex lies inside the drawable's bounding box (with slack).
        for (int i = 0; i < mesh.Positions.Length; i += 3)
        {
            var p = new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]);
            Assert.True(p.X >= mesh.BoundsMin.X - 0.05f && p.X <= mesh.BoundsMax.X + 0.05f, $"x {p.X} outside bounds");
            Assert.True(p.Z >= mesh.BoundsMin.Z - 0.05f && p.Z <= mesh.BoundsMax.Z + 0.05f, $"z {p.Z} outside bounds");
        }
        Assert.All(Enumerable.Range(0, mesh.VertexCount), v =>
        {
            var n = new Vector3(mesh.Normals![v * 3], mesh.Normals[v * 3 + 1], mesh.Normals[v * 3 + 2]);
            Assert.InRange(n.Length(), 0.8f, 1.2f);
        });
    }

    [SkippableFact]
    public void FreemodeTorsoIsSkinnedToSkeletonBones()
    {
        using var gd = OpenOrSkip();
        Skip.If(!gd.HasPedComponent("mp_m_freemode_01", "uppr_000_r"), "freemode torso not in this game version");
        var skel = gd.LoadSkeleton("mp_m_freemode_01")!;
        var mesh = gd.LoadPedComponent("mp_m_freemode_01", "uppr_000_r")!;
        _out.WriteLine($"{mesh.Name}: {mesh.VertexCount} vertices, bones used {mesh.BlendIndices!.Distinct().Count()}");
        Assert.True(mesh.IsSkinned);
        Assert.Equal(mesh.VertexCount * 4, mesh.BlendIndices!.Length);
        Assert.All(mesh.BlendIndices, b => Assert.True(b < skel.Bones.Count));
        // Weights are normalised per vertex and the torso is driven by the arm / spine bones, never by the legs.
        var used = mesh.BlendIndices.Distinct().Select(i => skel.Bones[i].Name).ToHashSet();
        Assert.Contains("SKEL_L_UpperArm", used);
        Assert.Contains("SKEL_R_Hand", used);
        Assert.DoesNotContain("SKEL_L_Calf", used);
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            var sum = mesh.BlendWeights![v * 4] + mesh.BlendWeights[v * 4 + 1] + mesh.BlendWeights[v * 4 + 2] + mesh.BlendWeights[v * 4 + 3];
            Assert.InRange(sum, 0.97f, 1.03f);
        }
    }

    /// <summary>
    /// Peds whose component drawables embed a partial skeleton (the dead hooker, the story characters) number their
    /// blend indices against that skeleton; remapped by tag, the head is driven by the head bones, not by whatever
    /// bone happens to sit at the same index in the .yft.
    /// </summary>
    [SkippableFact]
    public void EmbeddedSkeletonHeadRemapsToHeadBones()
    {
        using var gd = OpenOrSkip();
        Skip.If(!gd.HasPedComponent("mp_f_deadhooker", "head_000_r"), "mp_f_deadhooker not in this game version");
        var skel = gd.LoadSkeleton("mp_f_deadhooker")!;
        var raw = gd.LoadPedComponent("mp_f_deadhooker", "head_000_r")!;
        Skip.If(raw.BlendBoneTags == null, "drawable no longer embeds a skeleton");
        Assert.NotEqual(skel.Bones.Count, raw.BlendBoneTags!.Length);

        var mesh = raw.RemapBones(skel);
        Assert.Null(mesh.BlendBoneTags);
        Assert.Empty(mesh.Warnings);
        // The bone with the most dominant weights has to be the head, and the vertices it drives sit near it.
        var headIndex = skel.Bones.First(b => b.Name == "SKEL_Head").Index;
        var counts = new int[skel.Bones.Count];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            int best = 0;
            for (int k = 1; k < 4; k++) if (mesh.BlendWeights![v * 4 + k] > mesh.BlendWeights[v * 4 + best]) best = k;
            counts[mesh.BlendIndices![v * 4 + best]]++;
        }
        var dominant = Array.IndexOf(counts, counts.Max());
        _out.WriteLine($"dominant bone {skel.Bones[dominant].Name} ({counts[dominant]} of {mesh.VertexCount} vertices)");
        Assert.Equal(headIndex, dominant);
        // Read as .yft indices, the same vertices would have landed on another bone (that is the bug being guarded).
        Assert.NotEqual(headIndex, raw.BlendBoneTags.Length > headIndex ? skel.IndexOfTag(raw.BlendBoneTags[headIndex]) : -1);
    }

    /// <summary>
    /// Michael's suit jacket is cloth-simulated in-game (the drawable ships a .yld); its cloth-shader geometries are
    /// flagged so the viewer can leave them out, while the shirt underneath and ordinary clothes are not.
    /// </summary>
    [SkippableFact]
    public void ClothSimulatedGeometryIsFlagged()
    {
        using var gd = OpenOrSkip();
        Skip.If(!gd.HasPedComponent("player_zero", "uppr_000_u") || !gd.HasPedCloth("player_zero", "uppr_000_u"), "player_zero jacket not in this game version");
        var jacket = gd.LoadPedComponent("player_zero", "uppr_000_u")!;
        _out.WriteLine(string.Join(", ", jacket.SubMeshes.Select(s => $"{s.ShaderName}:{(s.Cloth ? "cloth" : "solid")}")));
        Assert.Contains(jacket.SubMeshes, s => s.Cloth);
        Assert.Contains(jacket.SubMeshes, s => !s.Cloth);
        Assert.All(jacket.SubMeshes, s => Assert.Equal(s.ShaderName?.Contains("cloth") ?? false, s.Cloth));
        Assert.Empty(jacket.Warnings);
        // Cloth vertices are stored as barycentric weights over simulation vertices; decoded through the .yld binding
        // they become ordinary bone weights (sum 1, every index inside the bone palette) instead of the 1.5 sums that
        // pushed the jacket open.
        var palette = jacket.BlendBoneTags!.Length;
        for (int v = 0; v < jacket.VertexCount; v++)
        {
            var sum = jacket.BlendWeights![v * 4] + jacket.BlendWeights[v * 4 + 1] + jacket.BlendWeights[v * 4 + 2] + jacket.BlendWeights[v * 4 + 3];
            Assert.InRange(sum, 0.97f, 1.03f);
            for (int k = 0; k < 4; k++) Assert.True(jacket.BlendIndices![v * 4 + k] < palette, $"vertex {v} bone slot {k} outside the palette");
        }
        var skeleton = gd.LoadSkeleton("player_zero")!;
        var bound = jacket.RemapBones(skeleton);
        Assert.Empty(bound.Warnings);

        Skip.If(!gd.HasPedComponent("mp_m_freemode_01", "uppr_000_r"), "freemode torso not in this game version");
        var torso = gd.LoadPedComponent("mp_m_freemode_01", "uppr_000_r")!;
        Assert.DoesNotContain(torso.SubMeshes, s => s.Cloth);
    }
}
