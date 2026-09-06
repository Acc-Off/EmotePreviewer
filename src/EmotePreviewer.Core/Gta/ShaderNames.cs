using EmotePreviewer.Core.Rage;

namespace EmotePreviewer.Core.Gta;

/// <summary>
/// Shader names behind the hashes drawables carry (the archives store only the hash). The name decides how the diffuse
/// alpha is meant: cutout / alpha / decal / hair / glass shaders discard or blend it, every other shader uses it as a
/// mask (specular, tint) and must be drawn opaque — the plain <c>ped</c> shader's body textures carry alpha 0.4 all over.
/// </summary>
public static class ShaderNames
{
    static readonly string[] Known =
    {
        // peds
        "ped", "ped_alpha", "ped_default", "ped_default_enveff", "ped_default_mp", "ped_default_palette", "ped_default_cloth",
        "ped_hair_spiked", "ped_hair_spiked_enveff", "ped_hair_cutout_alpha", "ped_hair_cutout_enveff", "ped_hair_cutout_alpha_cloth",
        "ped_decal", "ped_decal_decoration", "ped_decal_expensive", "ped_decal_medals", "ped_decal_nodiff",
        "ped_cloth", "ped_cloth_enveff", "ped_fur", "ped_emissive", "ped_enveff", "ped_nopeddamagedecals", "ped_palette",
        "ped_wrinkle", "ped_wrinkle_cloth", "ped_wrinkle_cloth_enveff", "ped_wrinkle_cs", "ped_wrinkle_enveff",
        // props / world
        "default", "default_spec", "default_tnt", "default_um", "default_terrain_wet", "normal", "normal_spec", "normal_spec_reflect",
        "normal_spec_detail", "normal_spec_tnt", "normal_spec_um", "normal_spec_emissive", "normal_spec_pxm", "normal_spec_decal",
        "normal_spec_alpha", "normal_spec_cutout", "normal_spec_reflect_alpha", "normal_spec_reflect_decal", "normal_spec_reflect_emissive",
        "normal_spec_reflect_emissivenight", "normal_spec_reflect_emissivenight_alpha", "normal_spec_wrinkle", "normal_alpha",
        "normal_cutout", "normal_decal", "normal_decal_pxm", "normal_decal_tnt", "normal_detail", "normal_diffspec", "normal_diffspec_detail",
        "normal_reflect", "normal_reflect_alpha", "normal_reflect_decal", "normal_tnt", "normal_um", "normal_um_tnt", "normal_pxm",
        "normal_pxm_tnt", "normal_terrain_wet", "normal_spec_detail_tnt", "normal_spec_detail_dpm", "normal_spec_detail_dpm_tnt",
        "spec", "spec_alpha", "spec_decal", "spec_reflect", "spec_reflect_alpha", "spec_reflect_decal", "spec_tnt", "spec_twiddle_tnt",
        "cutout_fence", "cutout_fence_normal", "cutout_hard", "alpha", "decal", "decal_amb_only", "decal_diff_only_um", "decal_dirt",
        "decal_emissive_only", "decal_emissivenight_only", "decal_glue", "decal_normal_only", "decal_shadow_only", "decal_spec_only",
        "decal_tnt", "emissive", "emissive_additive_alpha", "emissive_additive_uv_alpha", "emissive_alpha", "emissive_alpha_tnt",
        "emissive_clip", "emissive_speclum", "emissive_tnt", "emissivenight", "emissivenight_alpha", "emissivestrong", "emissivestrong_alpha",
        "glass", "glass_pv", "glass_pv_env", "glass_env", "glass_emissive", "glass_emissivenight", "glass_emissivenight_alpha", "glass_emissive_alpha",
        "glass_normal_spec_reflect", "glass_reflect", "glass_spec", "glass_displacement", "glass_breakable", "glass_breakable_screendooralpha",
        "cloth_default", "cloth_normal_spec", "cloth_normal_spec_alpha", "cloth_normal_spec_cutout", "cloth_normal_spec_tnt", "cloth_spec_alpha",
        "cloth_spec_cutout", "mirror_default", "mirror_decal", "reflect", "reflect_alpha", "reflect_decal", "weapon_normal_spec",
        "weapon_normal_spec_alpha", "weapon_normal_spec_cutout_palette", "weapon_normal_spec_detail_palette", "weapon_normal_spec_detail_tnt",
        "weapon_normal_spec_palette", "weapon_normal_spec_tnt", "weapon_emissivestrong_alpha", "weapon_emissive_tnt", "trees", "trees_lod",
        "trees_normal", "trees_normal_spec", "trees_normal_diffspec", "grass", "grass_fur", "vehicle_paint1", "vehicle_paint2", "vehicle_paint3",
        "vehicle_paint4", "vehicle_paint5_enveff", "vehicle_mesh", "vehicle_mesh2_enveff", "vehicle_vehglass", "vehicle_vehglass_inner",
        "vehicle_basic", "vehicle_interior", "vehicle_interior2", "vehicle_tire", "vehicle_track", "vehicle_lightsemissive", "vehicle_decal",
        "vehicle_decal2", "vehicle_badges", "vehicle_detail", "vehicle_detail2", "vehicle_generic", "vehicle_cloth", "vehicle_cloth2",
        "vehicle_dash_emissive", "vehicle_dash_emissive_opaque", "vehicle_blurredrotor", "vehicle_blurredrotor_emissive", "vehicle_nosplash",
        "vehicle_novehglass", "vehicle_cutout", "vehicle_licenseplate", "vehicle_shuts", "vehicle_paint6", "vehicle_paint6_enveff",
        "vehicle_paint7", "vehicle_paint7_enveff", "vehicle_paint8", "vehicle_paint9", "vehicle_paint4_enveff", "vehicle_paint4_emissive",
        "vehicle_paint3_enveff", "vehicle_paint3_lvr", "vehicle_paint2_enveff", "vehicle_paint1_enveff",
    };

    static readonly Dictionary<uint, string> ByHash = BuildTable();

    static Dictionary<uint, string> BuildTable()
    {
        var table = new Dictionary<uint, string>();
        foreach (var name in Known) table.TryAdd(JenkinsHash.HashLower(name), name);
        return table;
    }

    /// <summary>The shader name for a hash, or null when it is not in the table.</summary>
    public static string? Resolve(uint hash) => ByHash.TryGetValue(hash, out var name) ? name : null;

    /// <summary>
    /// Whether the shader treats the diffuse alpha as coverage (discard / blend). Null when the shader is unknown, so
    /// the caller can fall back to a heuristic.
    /// </summary>
    public static bool? IsCutout(uint hash)
    {
        var name = Resolve(hash);
        if (name == null) return null;
        return name.Contains("alpha", StringComparison.Ordinal) || name.Contains("cutout", StringComparison.Ordinal)
            || name.Contains("decal", StringComparison.Ordinal) || name.Contains("hair", StringComparison.Ordinal)
            || name.Contains("glass", StringComparison.Ordinal) || name.Contains("fur", StringComparison.Ordinal)
            || name.StartsWith("trees", StringComparison.Ordinal) || name.StartsWith("grass", StringComparison.Ordinal);
    }
}
