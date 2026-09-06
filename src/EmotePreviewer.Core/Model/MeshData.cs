using System.Numerics;
using EmotePreviewer.Core.Textures;

namespace EmotePreviewer.Core.Model;

/// <summary>
/// A contiguous range of triangle indices drawn with one shader. <paramref name="Diffuse"/> names the shader's diffuse
/// texture (embedded in the drawable or referenced from a texture dictionary); null when the shader has none.
/// <paramref name="Palette"/> marks palette-tinted shaders (hair, some clothes) whose diffuse holds intensity, not colour.
/// <paramref name="Cutout"/> says whether the shader uses the diffuse alpha as coverage (null = unknown shader).
/// <paramref name="Hidden"/> marks geometry the game does not draw in the colour pass: hair drawables carry a low-poly
/// hull with planar UVs under a second shader instance whose <c>orderNumber</c> is 1 (a secondary hair pass); drawn
/// as an ordinary mesh it shows up as a black helmet around the strands.
/// </summary>
public sealed record SubMesh(int IndexStart, int IndexCount, uint ShaderHash, string? Diffuse = null, bool DiffuseEmbedded = false, bool Palette = false, bool? Cutout = null, bool Hidden = false)
{
    /// <summary>Shader name when the hash is known (see <see cref="Gta.ShaderNames"/>).</summary>
    public string? ShaderName => Gta.ShaderNames.Resolve(ShaderHash);
}

/// <summary>
/// Triangle mesh extracted from a drawable, laid out the way three.js <c>BufferGeometry</c> wants it: flat float
/// attribute arrays plus one index array. Coordinates are GTA object space (Z-up, metres).
/// </summary>
public sealed class MeshData
{
    public const int LayoutVersion = 1;

    public required string Name { get; init; }
    /// <summary>x, y, z per vertex.</summary>
    public required float[] Positions { get; init; }
    /// <summary>x, y, z per vertex; null when the vertex format has no normals.</summary>
    public float[]? Normals { get; init; }
    /// <summary>u, v per vertex (first texture channel); null when absent.</summary>
    public float[]? Uvs { get; init; }
    /// <summary>Four skeleton bone indices per vertex; null for rigid meshes.</summary>
    public ushort[]? BlendIndices { get; init; }
    /// <summary>Four weights per vertex (sum 1); null for rigid meshes.</summary>
    public float[]? BlendWeights { get; init; }
    public required uint[] Indices { get; init; }
    public required IReadOnlyList<SubMesh> SubMeshes { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    /// <summary>Textures stored inside the drawable (props mostly carry their diffuse here).</summary>
    public IReadOnlyList<TextureImage> EmbeddedTextures { get; init; } = Array.Empty<TextureImage>();
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }

    public int VertexCount => Positions.Length / 3;
    public bool IsSkinned => BlendIndices != null && BlendWeights != null;

    /// <summary>The embedded texture of that name, if any.</summary>
    public TextureImage? FindEmbedded(string name) => EmbeddedTextures.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Serialises the arrays back to back as little-endian: positions (f32 ×3), normals (f32 ×3, if present), uvs
    /// (f32 ×2, if present), blend indices (u16 ×4, if skinned), blend weights (f32 ×4, if skinned), indices (u32).
    /// The metadata tells the reader which blocks exist; every block starts 4-byte aligned.
    /// </summary>
    public byte[] ToBytes()
    {
        var size = Positions.Length * 4 + (Normals?.Length ?? 0) * 4 + (Uvs?.Length ?? 0) * 4 + (BlendIndices?.Length ?? 0) * 2 + (BlendWeights?.Length ?? 0) * 4 + Indices.Length * 4;
        var bytes = new byte[size];
        var span = bytes.AsSpan();
        int o = 0;
        o += WriteFloats(span[o..], Positions);
        if (Normals != null) o += WriteFloats(span[o..], Normals);
        if (Uvs != null) o += WriteFloats(span[o..], Uvs);
        if (BlendIndices != null)
        {
            foreach (var v in BlendIndices) { System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[o..], v); o += 2; }
        }
        if (BlendWeights != null) o += WriteFloats(span[o..], BlendWeights);
        foreach (var v in Indices) { System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[o..], v); o += 4; }
        return bytes;
    }

    static int WriteFloats(Span<byte> dst, float[] src)
    {
        for (int i = 0; i < src.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(dst[(i * 4)..], src[i]);
        return src.Length * 4;
    }
}
