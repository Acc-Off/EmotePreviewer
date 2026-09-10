using System.Numerics;

namespace EmotePreviewer.Core.Model;

/// <summary>
/// Degrees of freedom a bone exposes to animation (the skeleton's bone flags). The engine only drives the channels a
/// bone allows: most body bones are rotation-only, so translation tracks aimed at them are ignored.
/// </summary>
[Flags]
public enum BoneDofs : ushort
{
    None = 0,
    RotX = 0x1, RotY = 0x2, RotZ = 0x4,
    TransX = 0x10, TransY = 0x20, TransZ = 0x40,
    ScaleX = 0x100, ScaleY = 0x200, ScaleZ = 0x400,
    Rotation = RotX | RotY | RotZ,
    Translation = TransX | TransY | TransZ,
    Scale = ScaleX | ScaleY | ScaleZ,
    All = Rotation | Translation | Scale,
}

/// <summary>One bone of a ped skeleton. Local transform is relative to the parent (bind pose).</summary>
/// <param name="Dofs">Channels animations may drive; <see cref="BoneDofs.All"/> when the source did not say.</param>
public sealed record BoneDef(
    int Index,
    ushort Tag,
    string Name,
    int ParentIndex,
    Vector3 Translation,
    Quaternion Rotation,
    Vector3 Scale,
    BoneDofs Dofs = BoneDofs.All)
{
    /// <summary>
    /// Facial rig bones: <c>FB_*</c> on the freemode / ambient skeletons, <c>FACIAL_*</c> (the high-resolution rig under
    /// <c>FACIAL_facialRoot</c>) on the story characters. Driven by the facial animation layer in-game, which overrides
    /// whatever a body clip carries for them; a body clip's <c>FACIAL_facialRoot</c> track applied to a story ped folds
    /// the whole face into the skull.
    /// </summary>
    public bool IsFacial => Name.StartsWith("FB_", StringComparison.Ordinal) || Name.StartsWith("FACIAL_", StringComparison.Ordinal);
}

/// <summary>Immutable skeleton definition, independent of any file-format library.</summary>
public sealed class SkeletonDef
{
    public string Name { get; }
    public IReadOnlyList<BoneDef> Bones { get; }
    /// <summary>Bone indices ordered so that every parent appears before its children (file order is not guaranteed to be).</summary>
    public IReadOnlyList<int> EvaluationOrder { get; }
    readonly Dictionary<ushort, int> _byTag;

    public SkeletonDef(string name, IReadOnlyList<BoneDef> bones)
    {
        Name = name;
        Bones = bones;
        _byTag = new Dictionary<ushort, int>(bones.Count);
        foreach (var b in bones) _byTag.TryAdd(b.Tag, b.Index);
        for (int i = 0; i < bones.Count; i++)
        {
            if (bones[i].Index != i) throw new ArgumentException($"bone {i} has Index {bones[i].Index}; bones must be in index order");
            var p = bones[i].ParentIndex;
            if (p >= bones.Count || p == i) throw new ArgumentException($"bone {i} ({bones[i].Name}) has invalid parent {p}");
        }
        EvaluationOrder = BuildEvaluationOrder(bones);
    }

    static int[] BuildEvaluationOrder(IReadOnlyList<BoneDef> bones)
    {
        var children = new List<int>[bones.Count];
        var roots = new List<int>();
        for (int i = 0; i < bones.Count; i++)
        {
            var p = bones[i].ParentIndex;
            if (p < 0) { roots.Add(i); continue; }
            (children[p] ??= new List<int>()).Add(i);
        }
        var order = new List<int>(bones.Count);
        var stack = new Stack<int>();
        for (int r = roots.Count - 1; r >= 0; r--) stack.Push(roots[r]);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            order.Add(i);
            var ch = children[i];
            if (ch != null) for (int k = ch.Count - 1; k >= 0; k--) stack.Push(ch[k]);
        }
        if (order.Count != bones.Count) throw new ArgumentException("skeleton contains a parent cycle or unreachable bones");
        return order.ToArray();
    }

    public int IndexOfTag(ushort tag) => _byTag.TryGetValue(tag, out var i) ? i : -1;
    public BoneDef? FindByTag(ushort tag) => _byTag.TryGetValue(tag, out var i) ? Bones[i] : null;
}
