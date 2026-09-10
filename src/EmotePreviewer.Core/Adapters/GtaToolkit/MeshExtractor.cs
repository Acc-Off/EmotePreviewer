using System.Buffers.Binary;
using System.Numerics;
using EmotePreviewer.Core.Gta;
using EmotePreviewer.Core.Model;
using EmotePreviewer.Core.Textures;
using RageLib.Resources.Common;
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
    /// <param name="cloth">
    /// The drawable's cloth binding when it ships cloth data (<c>.yld</c>). Its cloth-shader geometries are then flagged
    /// <see cref="SubMesh.Cloth"/> and their vertices, which the file stores as barycentric weights over simulation
    /// vertices rather than bone weights, are converted to bone weights through the binding.
    /// </param>
    public static MeshData Extract(Drawable drawable, string name, ClothBinding? cloth = null)
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
        // Bone palette of the skin: the drawable's own skeleton (see BlendBoneTags) plus bones cloth vertices bring in.
        var ownBones = drawable.Skeleton?.BoneData?.Bones;
        var paletteTags = new List<ushort>();
        var paletteIndex = new Dictionary<ushort, int>();
        if (ownBones != null)
            for (int i = 0; i < ownBones.Count; i++) { paletteTags.Add(ownBones[i].BoneId); paletteIndex.TryAdd(ownBones[i].BoneId, i); }
        int clothVertices = 0, clothUnbound = 0;

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
                    var shader = model.ShaderMapping != null && g < model.ShaderMapping.Count && shaders != null && model.ShaderMapping[g] < shaders.Count ? shaders[model.ShaderMapping[g]] : null;
                    var shaderName = shader != null ? ShaderNames.Resolve(shader.ShaderHash) : null;
                    var isCloth = cloth != null && (shaderName?.Contains("cloth", StringComparison.Ordinal) ?? false);
                    // Cloth vertices can only be converted when the drawable's own skeleton gives the bone slots a tag.
                    var decodeCloth = isCloth && hasSkin && ownBones != null;

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
                                // In cloth geometry, index slot 2 == 255 marks a simulated vertex; the others (sleeves,
                                // collars) are ordinary skin.
                                if (decodeCloth && ids[2] == 255)
                                {
                                    clothVertices++;
                                    if (!DecodeClothVertex(ids, w, cloth!, paletteTags, paletteIndex, blendIndices, blendWeights)) clothUnbound++;
                                }
                                else
                                {
                                    for (int k = 0; k < 4; k++)
                                    {
                                        int local = ids[k];
                                        // BonesId maps geometry-local bone slots to skeleton bone indices when present.
                                        var skeletonBone = boneIds != null && local < boneIds.Count ? boneIds[local] : (ushort)local;
                                        blendIndices.Add(skeletonBone);
                                    }
                                    blendWeights.Add(w.X); blendWeights.Add(w.Y); blendWeights.Add(w.Z); blendWeights.Add(w.W);
                                }
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
                    if (shader != null)
                    {
                        shaderHash = shader.ShaderHash;
                        diffuse = TextureExtractor.Diffuse(shader);
                        palette = TextureExtractor.HasPalette(shader);
                        hidden = (shaderName?.Contains("hair", StringComparison.Ordinal) ?? false)
                            && TextureExtractor.NumericParameter(shader, TextureExtractor.OrderNumberParam) >= 1f;
                    }
                    subMeshes.Add(new SubMesh(indexStart, indexCount, shaderHash, diffuse?.name, diffuse?.embedded ?? false, palette, ShaderNames.IsCutout(shaderHash), hidden, isCloth));
                }
            }
        }

        if (positions.Count == 0) warnings.Add("no readable geometry");
        if (clothUnbound > 0) warnings.Add($"{clothUnbound} of {clothVertices} cloth vertices reference no simulation vertex");
        // Ped component drawables (the heads of most peds, every part of a few) embed the skeleton they are skinned to,
        // often a subset of the ped's, and their blend indices number that skeleton; the tags let the caller translate
        // them to the ped's .yft skeleton. Cloth vertices may have added bones to the palette.
        ushort[]? boneTags = anySkin && paletteTags.Count > 0 ? paletteTags.ToArray() : null;
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

    /// <summary>
    /// Converts one simulated cloth vertex to bone weights. The file packs it as barycentric weights over up to three
    /// simulation vertices: index slots 0, 1 and 3 paired with weight slots 1, 0 and 2 (index slot 2 holds the 255
    /// marker). Each simulation vertex hangs on bones through the binding; the result is the four heaviest bones,
    /// renormalised. Returns false when nothing could be bound (the vertex is left on the root of the palette).
    /// </summary>
    static bool DecodeClothVertex(ReadOnlySpan<byte> ids, Vector4 w, ClothBinding cloth,
        List<ushort> paletteTags, Dictionary<ushort, int> paletteIndex, List<ushort> blendIndices, List<float> blendWeights)
    {
        Span<(ushort tag, float weight)> acc = stackalloc (ushort, float)[16];
        int n = 0;
        void Add(Span<(ushort tag, float weight)> a, ushort tag, float weight)
        {
            if (weight <= 0) return;
            for (int i = 0; i < n; i++) if (a[i].tag == tag) { a[i].weight += weight; return; }
            if (n < a.Length) a[n++] = (tag, weight);
        }
        void AddSim(Span<(ushort tag, float weight)> a, int simVertex, float weight)
        {
            if (weight <= 0 || simVertex >= cloth.VertexCount) return;
            foreach (var (tag, bw) in cloth.Bones[simVertex]) Add(a, tag, weight * bw);
        }
        AddSim(acc, ids[0], w.Y);
        AddSim(acc, ids[1], w.X);
        AddSim(acc, ids[3], w.Z);

        // Keep the four heaviest and renormalise.
        var top = acc[..n].ToArray();
        Array.Sort(top, (a, b) => b.weight.CompareTo(a.weight));
        float sum = 0;
        int count = Math.Min(4, top.Length);
        for (int i = 0; i < count; i++) sum += top[i].weight;
        for (int k = 0; k < 4; k++)
        {
            if (k < count && sum > 0)
            {
                if (!paletteIndex.TryGetValue(top[k].tag, out var slot)) { slot = paletteTags.Count; paletteTags.Add(top[k].tag); paletteIndex[top[k].tag] = slot; }
                blendIndices.Add((ushort)slot);
                blendWeights.Add(top[k].weight / sum);
            }
            else
            {
                blendIndices.Add(0);
                blendWeights.Add(k == 0 ? 1f : 0f);
            }
        }
        return count > 0 && sum > 0;
    }

    /// <summary>Raw per-vertex position, blend indices and weights of a geometry as CSV lines (diagnostics).</summary>
    public static IEnumerable<string> SkinDump(DrawableGeometry geom)
    {
        var vb = geom?.VertexBuffer;
        var decl = vb?.Info;
        var data = vb?.Data1?.Data ?? vb?.Data2?.Data ?? geom?.VertexData?.Data;
        if (geom == null || vb == null || decl == null || data == null) yield break;
        var layout = Layout(decl);
        if (!layout.TryGetValue(Semantic.BlendIndices, out var bi) || !layout.TryGetValue(Semantic.BlendWeights, out var bw) || !layout.TryGetValue(Semantic.Position, out var pos)) yield break;
        layout.TryGetValue(Semantic.Normal, out var nrm);
        int stride = decl.Stride > 0 ? decl.Stride : vb.VertexStride;
        int count = (int)Math.Min(vb.VertexCount, stride > 0 ? data.Length / stride : 0);
        for (int v = 0; v < count; v++)
        {
            var vertex = data.AsSpan(v * stride, stride);
            var p = ReadVector(vertex[pos.offset..], pos.type);
            var n = nrm.type != default ? ReadVector(vertex[nrm.offset..], nrm.type) : Vector4.Zero;
            yield return $"{p.X},{p.Y},{p.Z},{n.X},{n.Y},{n.Z},{vertex[bi.offset]},{vertex[bi.offset + 1]},{vertex[bi.offset + 2]},{vertex[bi.offset + 3]},{vertex[bw.offset]},{vertex[bw.offset + 1]},{vertex[bw.offset + 2]},{vertex[bw.offset + 3]}";
        }
    }

    /// <summary>Raw blend index / weight statistics of a geometry (diagnostics); null when it is not skinned.</summary>
    public static (int distinct, int max, float minSum, float maxSum, string sample)? SkinStats(DrawableGeometry geom)
    {
        var vb = geom?.VertexBuffer;
        var decl = vb?.Info;
        var data = vb?.Data1?.Data ?? vb?.Data2?.Data ?? geom?.VertexData?.Data;
        if (geom == null || vb == null || decl == null || data == null) return null;
        var layout = Layout(decl);
        if (!layout.TryGetValue(Semantic.BlendIndices, out var bi) || !layout.TryGetValue(Semantic.BlendWeights, out var bw)) return null;
        int stride = decl.Stride > 0 ? decl.Stride : vb.VertexStride;
        int count = (int)Math.Min(vb.VertexCount, stride > 0 ? data.Length / stride : 0);
        var seen = new HashSet<int>();
        int max = 0; float minSum = float.MaxValue, maxSum = float.MinValue;
        var sample = new System.Text.StringBuilder();
        for (int v = 0; v < count; v++)
        {
            var vertex = data.AsSpan(v * stride, stride);
            float sum = 0;
            for (int k = 0; k < 4; k++)
            {
                var w = vertex[bw.offset + k] / 255f;
                var idx = vertex[bi.offset + k];
                sum += w;
                if (w > 0) { seen.Add(idx); max = Math.Max(max, idx); }
                if (v < 3) sample.Append($"{idx}:{w:F2} ");
            }
            if (v < 3) sample.Append("| ");
            minSum = Math.Min(minSum, sum); maxSum = Math.Max(maxSum, sum);
        }
        return (seen.Count, max, minSum, maxSum, sample.ToString());
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
