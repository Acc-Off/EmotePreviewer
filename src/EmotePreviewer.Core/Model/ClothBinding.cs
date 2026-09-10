namespace EmotePreviewer.Core.Model;

/// <summary>
/// How the vertices of a cloth simulation mesh hang on the skeleton, read from a ped's cloth dictionary (<c>.yld</c>).
/// The game moves these vertices with its verlet solver; without one, the viewer skins them to the bones they are
/// bound to, which gives the resting shape and a garment that follows the body rigidly.
/// </summary>
/// <param name="VertexCount">Simulation vertices (a few hundred; far fewer than the render mesh).</param>
/// <param name="Bones">Up to four (bone tag, weight) pairs per simulation vertex, weights summing to 1.</param>
public sealed record ClothBinding(int VertexCount, IReadOnlyList<IReadOnlyList<(ushort tag, float weight)>> Bones);
