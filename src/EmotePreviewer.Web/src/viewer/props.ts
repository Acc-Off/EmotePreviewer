import * as THREE from "three";
import type { LoadedMesh } from "../shared/api";
import type { PropDto } from "../shared/types";
import { cssVar, type Scene } from "./scene";
import type { SkeletonRig } from "./skeleton";
import { buildMaterials, type MeshMaterials, type TextureCache } from "./textures";

/** Builds a BufferGeometry from the server's mesh layout (positions, normals?, uvs?, blend data?, indices). */
export function buildGeometry(loaded: LoadedMesh): THREE.BufferGeometry {
  const { meta, data } = loaded;
  const geometry = new THREE.BufferGeometry();
  let offset = 0;
  const floats = (count: number) => {
    const view = new Float32Array(data, offset, count);
    offset += count * 4;
    return view;
  };
  geometry.setAttribute("position", new THREE.BufferAttribute(floats(meta.vertexCount * 3), 3));
  if (meta.hasNormals) geometry.setAttribute("normal", new THREE.BufferAttribute(floats(meta.vertexCount * 3), 3));
  if (meta.hasUvs) geometry.setAttribute("uv", new THREE.BufferAttribute(floats(meta.vertexCount * 2), 2));
  if (meta.skinned) {
    const indices = new Uint16Array(data, offset, meta.vertexCount * 4);
    offset += meta.vertexCount * 8;
    geometry.setAttribute("skinIndex", new THREE.BufferAttribute(indices, 4));
    geometry.setAttribute("skinWeight", new THREE.BufferAttribute(floats(meta.vertexCount * 4), 4));
  }
  geometry.setIndex(new THREE.BufferAttribute(new Uint32Array(data, offset, meta.indexCount), 1));
  // One group per sub-mesh so each can carry its own (textured) material; a single material ignores the index.
  // Hidden sub-meshes get no group and are therefore never drawn.
  meta.subMeshes.forEach((sub, i) => {
    if (!sub.hidden && !sub.cloth) geometry.addGroup(sub.indexStart, sub.indexCount, i);
  });
  if (!meta.hasNormals) geometry.computeVertexNormals();
  geometry.computeBoundingSphere();
  return geometry;
}

/**
 * Props attached to bones, the way the emote menus do it with ATTACH_ENTITY_TO_ENTITY: the placement offset is in the
 * bone's local frame and the rotation uses rotation order 1 (Y, then Z, then X about the prop's own axes), which is
 * three.js Euler order "YZX". Checked against a cigar in the mouth and an umbrella held overhead; "XZY" puts both wrong.
 */
export class PropLayer {
  /** three.js Euler order used for the placement rotation (GTA rotation order 1). Overridable for experiments. */
  static eulerOrder: THREE.EulerOrder = "YZX";

  private readonly rig: SkeletonRig;
  private readonly textures: TextureCache;
  private readonly material: THREE.MeshStandardMaterial;
  private readonly attached: { object: THREE.Mesh; materials: MeshMaterials }[] = [];
  private readonly unTheme: () => void;
  private visible = true;
  private textured = true;
  private abort: AbortController | null = null;
  private last: { prop: PropDto; mesh: LoadedMesh | null }[] = [];

  constructor(rig: SkeletonRig, scene: Scene, textures: TextureCache) {
    this.rig = rig;
    this.textures = textures;
    this.material = new THREE.MeshStandardMaterial({ color: 0xb0b6be, roughness: 0.75, metalness: 0.05, side: THREE.DoubleSide });
    this.unTheme = scene.onTheme(() => this.material.color.set(cssVar("--prop") || "#b0b6be"));
  }

  /** Removes the current props and attaches the given ones. Meshes that failed to load are skipped. */
  set(props: { prop: PropDto; mesh: LoadedMesh | null }[]): void {
    this.clear();
    this.last = props;
    this.abort = new AbortController();
    for (const { prop, mesh } of props) {
      if (!mesh) continue;
      const bone = this.rig.boneByTag(prop.bone) ?? this.rig.bones[0];
      const { materials } = buildMaterials(mesh, this.material, this.textures, { doubleSide: true }, this.abort.signal);
      const object = new THREE.Mesh(buildGeometry(mesh), this.textured ? materials.textured : this.material);
      object.name = prop.model;
      const [x, y, z, rx, ry, rz] = prop.placement;
      object.position.set(x, y, z);
      object.rotation.set(THREE.MathUtils.degToRad(rx), THREE.MathUtils.degToRad(ry), THREE.MathUtils.degToRad(rz), PropLayer.eulerOrder);
      object.visible = this.visible;
      bone.add(object);
      this.attached.push({ object, materials });
    }
  }

  setVisible(visible: boolean): void {
    this.visible = visible;
    for (const a of this.attached) a.object.visible = visible;
  }

  /** Switches between the diffuse textures and the flat colour. */
  setTextured(on: boolean): void {
    this.textured = on;
    for (const a of this.attached) a.object.material = on ? a.materials.textured : this.material;
  }

  /** Re-attaches the current props (after the Euler order changed). */
  refresh(): void {
    this.set(this.last);
  }

  clear(): void {
    this.abort?.abort();
    this.abort = null;
    for (const a of this.attached) {
      a.object.parent?.remove(a.object);
      a.object.geometry.dispose();
      a.materials.dispose();
    }
    this.attached.length = 0;
  }

  dispose(): void {
    this.clear();
    this.unTheme();
    this.material.dispose();
  }
}
