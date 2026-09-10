using System.Buffers.Binary;
using System.Numerics;
using EmotePreviewer.Core.Gta;
using EmotePreviewer.Core.Model;
using EmotePreviewer.Core.Textures;
using RageLib.Resources.GTA5.PC.Drawables;

namespace EmotePreviewer.Core.Adapters.GtaToolkit;

/// <summary>
/// Pulls positions, normals, texture coordinates and (for skinned meshes) blend weights out of a gta-toolkit
/// <see cref="Drawable"/>, following each geometry's <see cref="VertexDeclaration"/>. Only the highest LOD is used.
/// </summary>
public static class MeshExtractor
{
    /// <summary>Semantic slots of a vertex declaration, in the order the flag bits (and the type nibbles) use.</summary>
    enum Semantic
    {
        Position = 0, BlendWeights = 1, BlendIndices = 2, Normal = 3, Color0 = 4, Color1 = 5,
        TexCoord0 = 6, TexCoord1 = 7, TexCoord2 = 8, TexCoord3 = 9, TexCoord4 = 10, TexCoord5 = 11, TexCoord6 = 12, TexCoord7 = 13,
        Tangent = 14, Binormal = 15,
    }

    static int ComponentSize(VertexComponentTypes type) => type switch
    {
        VertexComponentTypes.Float16_2 => 4,
        VertexComponentTypes.Float => 4,
        VertexComponentTypes.Float16_4 => 8,
        VertexComponentTypes.FloatUnk => 4,
        VertexComponentTypes.Float2 => 8,
        VertexComponentTypes.Float3 => 12,
        VertexComponentTypes.Float4 => 16,
        VertexComponentTypes.UByte4 => 4,
        VertexComponentTypes.Color => 4,
        VertexComponentTypes.Dec3N => 4,
        _ => 4,
    };

    /// <summary>Byte offset and component type of every semantic present in the declaration.</summary>
    static Dictionary<Semantic, (int offset, VertexComponentTypes type)> Layout(VertexDeclaration decl)
    {
        var layout = new Dictionary<Semantic, (int, VertexComponentTypes)>();
        int offset = 0;
        for (int i = 0; i < 16; i++)
        {
            if (((ushort)decl.Flags & (1 << i)) == 0) continue;
            var type = (VertexComponentTypes)(((ulong)decl.Types >> (i * 4)) & 0xF);
            layout[(Semantic)i] = (offset, type);
            offset += ComponentSize(type);
        }
        return layout;
    }

    /// <summary>Extracts the highest LOD of <paramref name="drawable"/>. Unreadable geometries become warnings, not exceptions.</summary>
    /// <param name="clothSimulated">True when the drawable ships cloth data (<c>.yld</c>); its cloth-shader geometries are then flagged <see cref="SubMesh.Cloth"/>.</param>
    public static MeshData Extract(Drawable drawable, string name, bool clothSimulated = false)
    {
        var warnings = new List<string>();
        var lod = drawable.LodGroup.LodHigh ?? drawable.LodGroup.LodMedium ?? drawable.LodGroup.LodLow ?? drawable.LodGroup.LodVeryLow ?? drawable.PrimaryLod;
        var positions = new List<float>();
        var normals = new List<float>();
        var uvs = new List<float>();
        var blendIndices = new List<ushort>();
        var blendWeights = new List<float>();
        var indices = new List<uint>();
        var subMeshes = new List<SubMesh>();
        bool anyNormals = false, anyUvs = false, anySkin = false;
        var shaders = drawable.ShaderGroup?.Shaders?.Entries;

        if (lod?.Models?.Entries == null) warnings.Add("drawable has no LOD models");
        else
        {
            foreach (var model in lod.Models.Entries)
            {
                if (model?.Geometries?.Entries == null) continue;
                for (int g = 0; g < model.Geometries.Entries.Count; g++)
                {
                    var geom = model.Geometries.Entries[g];
                    var vb = geom?.VertexBuffer;
                    var decl = vb?.Info;
                    var data = vb?.Data1?.Data ?? vb?.Data2?.Data ?? geom?.VertexData?.Data;
                    var indexData = geom?.IndexBuffer?.Indices?.Data;
                    if (geom == null || vb == null || decl == null || data == null || indexData == null)
                    {
                        warnings.Add($"geometry {g} has no vertex or index data");
                        continue;
                    }
                    var layout = Layout(decl);
                    if (!layout.TryGetValue(Semantic.Position, out var pos))
                    {
                        warnings.Add($"geometry {g} has no position attribute");
                        continue;
                    }
                    int stride = decl.Stride > 0 ? decl.Stride : vb.VertexStride;
                    int vertexCount = (int)Math.Min(vb.VertexCount, stride > 0 ? data.Length / stride : 0);
                    int baseVertex = positions.Count / 3;

                    var hasNormal = layout.TryGetValue(Semantic.Normal, out var nrm);
                    var hasUv = layout.TryGetValue(Semantic.TexCoord0, out var uv);
                    var hasSkin = layout.TryGetValue(Semantic.BlendIndices, out var bi) & layout.TryGetValue(Semantic.BlendWeights, out var bw) && model.IsSkinned != 0;
                    var boneIds = geom.BonesId;

                    // Attributes missing in one geometry but present in another are padded so the arrays stay parallel.
                    if (hasNormal && !anyNormals && baseVertex > 0) normals.AddRange(new float[baseVertex * 3]);
                    if (hasUv && !anyUvs && baseVertex > 0) uvs.AddRange(new float[baseVertex * 2]);
                    if (hasSkin && !anySkin && baseVertex > 0) { blendIndices.AddRange(new ushort[baseVertex * 4]); blendWeights.AddRange(new float[baseVertex * 4]); }
                    anyNormals |= hasNormal; anyUvs |= hasUv; anySkin |= hasSkin;

                    for (int v = 0; v < vertexCount; v++)
                    {
                        var vertex = data.AsSpan(v * stride, stride);
                        var p = ReadVector(vertex[pos.offset..], pos.type);
                        positions.Add(p.X); positions.Add(p.Y); positions.Add(p.Z);
                        if (anyNormals)
                        {
                            var n = hasNormal ? ReadVector(vertex[nrm.offset..], nrm.type) : Vector4.UnitZ;
                            normals.Add(n.X); normals.Add(n.Y); normals.Add(n.Z);
                        }
                        if (anyUvs)
                        {
                            var t = hasUv ? ReadVector(vertex[uv.offset..], uv.type) : Vector4.Zero;
                            uvs.Add(t.X); uvs.Add(t.Y);
                        }
                        if (anySkin)
                        {
                            if (hasSkin)
                            {
                                // Weights and indices are D3DCOLOR / UBYTE4 quadruples; both are read in memory order so
                                // weight k always pairs with index k (a BGRA swizzle applied to only one of them scrambles the skin).
                                var w = bw.type is VertexComponentTypes.Color or VertexComponentTypes.UByte4
                                    ? new Vector4(vertex[bw.offset] / 255f, vertex[bw.offset + 1] / 255f, vertex[bw.offset + 2] / 255f, vertex[bw.offset + 3] / 255f)
                                    : ReadVector(vertex[bw.offset..], bw.type);
                                var ids = vertex.Slice(bi.offset, 4);
                                for (int k = 0; k < 4; k++)
                                {
                                    int local = ids[k];
                                    // BonesId maps geometry-local bone slots to skeleton bone indices when present.
                                    var skeletonBone = boneIds != null && local < boneIds.Count ? boneIds[local] : (ushort)local;
                                    blendIndices.Add(skeletonBone);
                                }
                                blendWeights.Add(w.X); blendWeights.Add(w.Y); blendWeights.Add(w.Z); blendWeights.Add(w.W);
                            }
                            else
                            {
                                blendIndices.AddRange(new ushort[4]);
                                blendWeights.Add(1f); blendWeights.Add(0f); blendWeights.Add(0f); blendWeights.Add(0f);
                            }
                        }
                    }

                    int indexStart = indices.Count;
                    int indexCount = (int)Math.Min(geom.IndicesCount, indexData.Length / 2);
                    for (int i = 0; i < indexCount; i++)
                        indices.Add((uint)(baseVertex + BinaryPrimitives.ReadUInt16LittleEndian(indexData.AsSpan(i * 2))));
                    uint shaderHash = 0;
                    (string name, bool embedded)? diffuse = null;
                    bool palette = false;
                    bool hidden = false;
                    bool cloth = false;
                    if (model.ShaderMapping != null && g < model.ShaderMapping.Count && shaders != null)
                    {
                        var shaderIndex = model.ShaderMapping[g];
                        if (shaderIndex < shaders.Count && shaders[shaderIndex] != null)
                        {
                            shaderHash = shaders[shaderIndex].ShaderHash;
                            diffuse = TextureExtractor.Diffuse(shaders[shaderIndex]);
                            palette = TextureExtractor.HasPalette(shaders[shaderIndex]);
                            var shaderName = ShaderNames.Resolve(shaderHash);
                            hidden = (shaderName?.Contains("hair", StringComparison.Ordinal) ?? false)
                                && TextureExtractor.NumericParameter(shaders[shaderIndex], TextureExtractor.OrderNumberParam) >= 1f;
                            cloth = clothSimulated && (shaderName?.Contains("cloth", StringComparison.Ordinal) ?? false);
                        }
                    }
                    subMeshes.Add(new SubMesh(indexStart, indexCount, shaderHash, diffuse?.name, diffuse?.embedded ?? false, palette, ShaderNames.IsCutout(shaderHash), hidden, cloth));
                }
            }
        }

        if (positions.Count == 0) warnings.Add("no readable geometry");
        // Ped component drawables (the heads of most peds, every part of a few) embed the skeleton they are skinned to,
        // often a subset of the ped's, and their blend indices number that skeleton; the tags let the caller translate
        // them to the ped's .yft skeleton.
        ushort[]? boneTags = null;
        var ownBones = drawable.Skeleton?.BoneData?.Bones;
        if (anySkin && ownBones != null && ownBones.Count > 0)
        {
            boneTags = new ushort[ownBones.Count];
            for (int i = 0; i < ownBones.Count; i++) boneTags[i] = ownBones[i].BoneId;
        }
        List<TextureImage> embedded;
        try { embedded = TextureExtractor.Embedded(drawable); }
        catch (Exception ex) { embedded = new(); warnings.Add("embedded textures unreadable: " + ex.Message); }
        return new MeshData
        {
            Name = name,
            Positions = positions.ToArray(),
            Normals = anyNormals ? normals.ToArray() : null,
            Uvs = anyUvs ? uvs.ToArray() : null,
            BlendIndices = anySkin ? blendIndices.ToArray() : null,
            BlendBoneTags = boneTags,
            BlendWeights = anySkin ? blendWeights.ToArray() : null,
            Indices = indices.ToArray(),
            SubMeshes = subMeshes,
            Warnings = warnings,
            EmbeddedTextures = embedded,
            BoundsMin = new Vector3(drawable.LodGroup.BoundingBoxMin.X, drawable.LodGroup.BoundingBoxMin.Y, drawable.LodGroup.BoundingBoxMin.Z),
            BoundsMax = new Vector3(drawable.LodGroup.BoundingBoxMax.X, drawable.LodGroup.BoundingBoxMax.Y, drawable.LodGroup.BoundingBoxMax.Z),
        };
    }

    /// <summary>Average of the first vertex colour channel (r, g, b, a in 0..255) of a geometry, for diagnostics; null when absent.</summary>
    public static float[]? AverageColor0(DrawableGeometry geom)
    {
        var vb = geom?.VertexBuffer;
        var decl = vb?.Info;
        var data = vb?.Data1?.Data ?? vb?.Data2?.Data ?? geom?.VertexData?.Data;
        if (geom == null || vb == null || decl == null || data == null) return null;
        var layout = Layout(decl);
        if (!layout.TryGetValue(Semantic.Color0, out var col)) return null;
        int stride = decl.Stride > 0 ? decl.Stride : vb.VertexStride;
        int count = (int)Math.Min(vb.VertexCount, stride > 0 ? data.Length / stride : 0);
        var sum = new float[4];
        for (int v = 0; v < count; v++)
        {
            var c = ReadVector(data.AsSpan(v * stride + col.offset), col.type);
            sum[0] += c.X; sum[1] += c.Y; sum[2] += c.Z; sum[3] += c.W;
        }
        for (int i = 0; i < 4; i++) sum[i] = count > 0 ? sum[i] * 255f / count : 0;
        return sum;
    }

    /// <summary>Decodes one vertex component to up to four floats.</summary>
    static Vector4 ReadVector(ReadOnlySpan<byte> src, VertexComponentTypes type)
    {
        switch (type)
        {
            case VertexComponentTypes.Float:
            case VertexComponentTypes.FloatUnk:
                return new Vector4(BinaryPrimitives.ReadSingleLittleEndian(src), 0, 0, 0);
            case VertexComponentTypes.Float2:
                return new Vector4(BinaryPrimitives.ReadSingleLittleEndian(src), BinaryPrimitives.ReadSingleLittleEndian(src[4..]), 0, 0);
            case VertexComponentTypes.Float3:
                return new Vector4(BinaryPrimitives.ReadSingleLittleEndian(src), BinaryPrimitives.ReadSingleLittleEndian(src[4..]), BinaryPrimitives.ReadSingleLittleEndian(src[8..]), 0);
            case VertexComponentTypes.Float4:
                return new Vector4(BinaryPrimitives.ReadSingleLittleEndian(src), BinaryPrimitives.ReadSingleLittleEndian(src[4..]), BinaryPrimitives.ReadSingleLittleEndian(src[8..]), BinaryPrimitives.ReadSingleLittleEndian(src[12..]));
            case VertexComponentTypes.Float16_2:
                return new Vector4((float)BinaryPrimitives.ReadHalfLittleEndian(src), (float)BinaryPrimitives.ReadHalfLittleEndian(src[2..]), 0, 0);
            case VertexComponentTypes.Float16_4:
                return new Vector4((float)BinaryPrimitives.ReadHalfLittleEndian(src), (float)BinaryPrimitives.ReadHalfLittleEndian(src[2..]), (float)BinaryPrimitives.ReadHalfLittleEndian(src[4..]), (float)BinaryPrimitives.ReadHalfLittleEndian(src[6..]));
            case VertexComponentTypes.UByte4:
                return new Vector4(src[0] / 255f, src[1] / 255f, src[2] / 255f, src[3] / 255f);
            case VertexComponentTypes.Color:
                return new Vector4(src[2] / 255f, src[1] / 255f, src[0] / 255f, src[3] / 255f); // BGRA → RGBA
            case VertexComponentTypes.Dec3N:
            {
                // 10:10:10:2, each 10-bit component two's complement, normalised by 511.
                var packed = BinaryPrimitives.ReadUInt32LittleEndian(src);
                static float Dec(uint v) { int s = (int)(v & 0x3FF); if (s >= 512) s -= 1024; return Math.Max(-1f, s / 511f); }
                return new Vector4(Dec(packed), Dec(packed >> 10), Dec(packed >> 20), (packed >> 30) / 3f);
            }
            default:
                return Vector4.Zero;
        }
    }
}
