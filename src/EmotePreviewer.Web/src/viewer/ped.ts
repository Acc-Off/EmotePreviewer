import * as THREE from "three";
import type { LoadedMesh } from "../shared/api";
import { buildGeometry } from "./props";
import { cssVar, type Scene } from "./scene";
import type { SkeletonRig } from "./skeleton";
import { buildMaterials, type MeshMaterials, type TextureCache } from "./textures";

/** Flat colours for palette-shaded components (the game picks them from a palette per outfit): hair brown, clothes grey. */
const PALETTE_TINTS: Record<string, number> = { hair: 0x3a2a20, berd: 0x3a2a20 };

/**
 * A ped as skinned meshes bound to the viewer's bone hierarchy. Vertices and bones share the ped's model space, so
 * binding in the bind pose with the current world matrices makes the mesh follow the animated bones.
 */
export class PedMesh {
  private readonly rig: SkeletonRig;
  private readonly textures: TextureCache;
  private readonly material: THREE.MeshStandardMaterial;
  private readonly meshes: { mesh: THREE.SkinnedMesh; materials: MeshMaterials }[] = [];
  private skeleton: THREE.Skeleton | null = null;
  private readonly unTheme: () => void;
  private visible = false;
  private textured = true;
  private abort: AbortController | null = null;

  constructor(rig: SkeletonRig, scene: Scene, textures: TextureCache) {
    this.rig = rig;
    this.textures = textures;
    this.material = new THREE.MeshStandardMaterial({ color: 0xc9c2b8, roughness: 0.85, metalness: 0.0 });
    this.unTheme = scene.onTheme(() => this.material.color.set(cssVar("--ped") || "#c9c2b8"));
  }

  get loaded(): boolean {
    return this.meshes.length > 0;
  }

  /** Replaces the ped with the given component meshes. Must be called while no clip is posing the bones. */
  set(components: LoadedMesh[]): void {
    this.clear();
    if (components.length === 0) return;
    this.abort = new AbortController();
    // Bind in the bind pose: the skeleton's inverse bind matrices come from the bones' current world matrices.
    this.rig.resetPose();
    this.rig.entity.updateMatrixWorld(true);
    this.skeleton = new THREE.Skeleton(this.rig.bones);
    const boneCount = this.rig.bones.length;
    for (const component of components) {
      if (!component.meta.skinned) continue;
      const geometry = buildGeometry(component);
      const skinIndex = geometry.getAttribute("skinIndex") as THREE.BufferAttribute;
      const idx = skinIndex.array as Uint16Array;
      for (let i = 0; i < idx.length; i++) if (idx[i] >= boneCount) idx[i] = 0;
      const slot = component.meta.model.split("/").pop()?.split("_")[0] ?? "";
      const { materials } = buildMaterials(component, this.material, this.textures, { doubleSide: false, paletteTint: PALETTE_TINTS[slot] ?? 0x8a8a8a }, this.abort.signal);
      const mesh = new THREE.SkinnedMesh(geometry, this.textured ? materials.textured : this.material);
      mesh.name = component.meta.model;
      mesh.frustumCulled = false;
      mesh.visible = this.visible;
      this.rig.entity.add(mesh);
      mesh.updateMatrixWorld(true);
      mesh.bind(this.skeleton, mesh.matrixWorld);
      this.meshes.push({ mesh, materials });
    }
  }

  setVisible(visible: boolean): void {
    this.visible = visible;
    for (const m of this.meshes) m.mesh.visible = visible;
  }

  /** Switches between the diffuse textures and the flat mannequin colour. */
  setTextured(on: boolean): void {
    this.textured = on;
    for (const m of this.meshes) m.mesh.material = on ? m.materials.textured : this.material;
  }

  clear(): void {
    this.abort?.abort();
    this.abort = null;
    for (const m of this.meshes) {
      m.mesh.parent?.remove(m.mesh);
      m.mesh.geometry.dispose();
      m.materials.dispose();
    }
    this.meshes.length = 0;
    this.skeleton?.dispose();
    this.skeleton = null;
  }

  dispose(): void {
    this.clear();
    this.unTheme();
    this.material.dispose();
  }
}
