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
}
