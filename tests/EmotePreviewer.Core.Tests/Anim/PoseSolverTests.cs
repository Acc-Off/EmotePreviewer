using System.Numerics;
using EmotePreviewer.Core.Anim;
using EmotePreviewer.Core.Model;

namespace EmotePreviewer.Core.Tests.Anim;

public sealed class PoseSolverTests
{
    const ushort ThighTag = 51826, ThighRollTag = 6442, CalfTag = 36864;

    /// <summary>Pelvis → right thigh → calf, plus the thigh roll helper as a sibling of the thigh (the freemode layout).</summary>
    static SkeletonDef Leg() => new("test", new[]
    {
        new BoneDef(0, 0, "SKEL_ROOT", -1, Vector3.Zero, Quaternion.Identity, Vector3.One),
        new BoneDef(1, 11816, "SKEL_Pelvis", 0, Vector3.Zero, Quaternion.Identity, Vector3.One),
        new BoneDef(2, ThighTag, "SKEL_R_Thigh", 1, new Vector3(0.07f, 0, 0.1f), Quaternion.Identity, Vector3.One),
        new BoneDef(3, CalfTag, "SKEL_R_Calf", 2, new Vector3(0.4f, 0, 0), Quaternion.Identity, Vector3.One),
        new BoneDef(4, ThighRollTag, "RB_R_ThighRoll", 1, new Vector3(0.07f, 0, 0.1f), Quaternion.Identity, Vector3.One),
    });

    /// <summary>
    /// The game poses the thigh roll helpers from the thigh after the animation, so a clip that carries its own track
    /// for them (synthetic clips written for every bone do) must not leave them behind: the thigh mesh is partly
    /// skinned to them and the leg bends visibly otherwise.
    /// </summary>
    [Fact]
    public void ThighRollFollowsTheThighEvenWhenTheClipDrivesIt()
    {
        var solver = new PoseSolver(Leg());
        var swing = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);
        var sample = new ClipSample();
        sample.Rotations[ThighTag] = swing;
        sample.Rotations[ThighRollTag] = Quaternion.Identity; // the bind pose, as a synthetic clip would write it
        solver.Apply(sample);
        Assert.Equal(swing, solver.LocalRotation[2]);
        Assert.Equal(swing, solver.LocalRotation[4]);

        solver.FixThighRollBones = false;
        solver.Apply(sample);
        Assert.Equal(Quaternion.Identity, solver.LocalRotation[4]);
    }
}
