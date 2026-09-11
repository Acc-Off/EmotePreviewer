import * as THREE from "three";
import type { SkeletonDto } from "../shared/types";
import { cssVar, type Scene } from "./scene";

/** Helper bones (mover / prop / IK / roll / skin / eye targets) are hidden unless the user asks for them. */
export function isSkeletonBone(name: string): boolean {
  return name.startsWith("SKEL_");
}

/**
 * three.js object name used for animation track binding; display names live in userData. The rig key keeps the names
 * unique when one rig hangs inside another (a carried ped), because the mixer finds bones by name in the subtree.
 */
export function boneObjectName(index: number, rigKey = ""): string {
  return `${rigKey}b${index}`;
}

let rigCounter = 0;

/** The bone whose subtree the game's UPPERBODY mask covers. */
export const UPPER_BODY_ROOT = "SKEL_Spine_Root";

/** Indices of `root` and every bone under it; every bone except index 0 when `root` is -1. */
export function subtree(def: SkeletonDto, root: number): Set<number> {
  const set = new Set<number>();
  if (root < 0) {
    for (const b of def.bones) if (b.index !== 0) set.add(b.index);
    return set;
  }
  set.add(root);
  // Bones are listed parents first in every .yft seen so far, but walk up the parent chain anyway to be safe.
  for (const b of def.bones) {
    let p = b.parent;
    while (p >= 0 && p !== root) p = def.bones[p].parent;
    if (p === root) set.add(b.index);
  }
  return set;
}

/**
 * The ped skeleton as a `THREE.Bone` hierarchy, plus a stick figure (`LineSegments` between SKEL_ bones, faint lines
 * for helper bones, points at the joints) that follows the bones every frame.
 *
 * Two groups wrap the bones: `entity` is the ped's own GTA-space frame (x right, y forward, z up; the bones hang
 * directly under it, as do the mannequin meshes), and `frame` converts that to three.js Y-up and stands the ped on
 * the grid. A ped that is placed next to another gets its offset on `entity`; a ped that hangs on another ped's bone
 * has its `entity` re-parented under that bone and `frame` stays empty.
 */
export class SkeletonRig {
  /** World-space wrapper: GTA Z-up → three.js Y-up rotation and the lift that puts the feet on the grid. */
  readonly frame = new THREE.Group();
  /** The ped's entity frame in GTA space; bones and meshes are its children. */
  readonly entity = new THREE.Group();
  /** Prefix of the bone object names (unique per rig). */
  readonly key: string;
  readonly bones: THREE.Bone[] = [];
  readonly def: SkeletonDto;
  /** Index of SKEL_Spine_Root (-1 when the skeleton has none, e.g. animals). */
  readonly spineRootIndex: number;
  /**
   * Bones a secondary emote with the UPPERBODY flag drives: the SKEL_Spine_Root subtree (spine, neck, head, clavicles,
   * arms, fingers and their helper bones). SKEL_ROOT, the pelvis, the legs and the IK / skirt bones stay with the
   * primary clip. Skeletons without that bone fall back to "everything but the root".
   */
  readonly upperBodyMask: ReadonlySet<number>;

  private readonly skelSegments: [number, number][] = [];
  private readonly helperSegments: [number, number][] = [];
  private readonly skelIndices: number[] = [];
  private readonly helperIndices: number[] = [];
  private readonly boneLines: THREE.LineSegments;
  private readonly helperLines: THREE.LineSegments;
  private readonly skelJoints: THREE.Points;
  private readonly helperJoints: THREE.Points;
  private readonly boneMat = new THREE.LineBasicMaterial({ color: 0xffffff });
  private readonly helperMat = new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.4 });
  private readonly jointMat = new THREE.PointsMaterial({ size: 0.028, sizeAttenuation: true });
  private readonly helperJointMat = new THREE.PointsMaterial({ size: 0.016, sizeAttenuation: true, transparent: true, opacity: 0.6 });
  private readonly tmp = new THREE.Vector3();
  private readonly scene: Scene;
  private readonly unTheme: () => void;

  constructor(def: SkeletonDto, scene: Scene) {
    this.def = def;
    this.scene = scene;
    this.key = `r${rigCounter++}_`;
    // GTA: x right, y forward, z up  →  three.js: x right, y up, z towards the viewer  ⇒ rotate −90° about x.
    this.frame.rotation.x = -Math.PI / 2;
    this.frame.add(this.entity);

    for (const b of def.bones) {
      const bone = new THREE.Bone();
      bone.name = boneObjectName(b.index, this.key);
      bone.userData.displayName = b.name;
      bone.userData.tag = b.tag;
      this.bones.push(bone);
    }
    for (const b of def.bones) {
      const bone = this.bones[b.index];
      if (b.parent >= 0) this.bones[b.parent].add(bone);
      else this.entity.add(bone);
    }
    this.resetPose();

    this.spineRootIndex = def.bones.find((b) => b.name === UPPER_BODY_ROOT)?.index ?? -1;
    this.upperBodyMask = subtree(def, this.spineRootIndex);

    // Segments: SKEL_ bones connect to their nearest SKEL_ ancestor so hidden helper bones do not break the figure.
    for (const b of def.bones) {
      const skel = isSkeletonBone(b.name);
      (skel ? this.skelIndices : this.helperIndices).push(b.index);
      if (b.parent < 0) continue;
      if (skel) {
        let p = b.parent;
        while (p >= 0 && !isSkeletonBone(def.bones[p].name)) p = def.bones[p].parent;
        if (p >= 0) this.skelSegments.push([p, b.index]);
      } else {
        this.helperSegments.push([b.parent, b.index]);
      }
    }

    this.boneLines = new THREE.LineSegments(lineGeometry(this.skelSegments.length * 2), this.boneMat);
    this.helperLines = new THREE.LineSegments(lineGeometry(this.helperSegments.length * 2), this.helperMat);
    this.skelJoints = new THREE.Points(lineGeometry(this.skelIndices.length), this.jointMat);
    this.helperJoints = new THREE.Points(lineGeometry(this.helperIndices.length), this.helperJointMat);
    for (const o of [this.boneLines, this.helperLines, this.skelJoints, this.helperJoints]) o.frustumCulled = false;
    this.applyVisibility();

    // Stand the figure on the grid: lift it by the lowest bind-pose bone.
    this.frame.updateMatrixWorld(true);
    let minY = Infinity;
    for (const bone of this.bones) minY = Math.min(minY, bone.getWorldPosition(this.tmp).y);
    if (Number.isFinite(minY)) this.frame.position.y = -minY;

    scene.scene.add(this.frame, this.boneLines, this.helperLines, this.skelJoints, this.helperJoints);
    this.unTheme = scene.onTheme(() => {
      this.boneMat.color.set(cssVar("--accent") || "#d1602a");
      this.helperMat.color.set(cssVar("--grid-strong") || "#9aa3ae");
      this.jointMat.color.set(cssVar("--joint") || "#2b3440");
      this.helperJointMat.color.set(cssVar("--grid-strong") || "#9aa3ae");
    });
    this.setIdlePose();
    this.update();
  }

  /** Object name of a bone of this rig (what the animation tracks bind to). */
  boneName(index: number): string {
    return boneObjectName(index, this.key);
  }

  /** The bone with the given tag (the id emote definitions use for prop attachment), if any. */
  boneByTag(tag: number): THREE.Bone | null {
    const def = this.def.bones.find((b) => b.tag === tag);
    return def ? this.bones[def.index] : null;
  }

  /** True while the entity stands in the world on its own frame (not attached to another rig). */
  get standalone(): boolean {
    return this.entity.parent === this.frame;
  }

  /** Puts the entity back under its own frame at the origin (undoes an offset or an attachment). */
  detach(): void {
    this.entity.removeFromParent();
    this.entity.position.set(0, 0, 0);
    this.entity.rotation.set(0, 0, 0);
    this.frame.add(this.entity);
  }

  /** Puts every bone back into its bind pose (the mixer only touches bones that have tracks). */
  resetPose(): void {
    for (const b of this.def.bones) {
      const bone = this.bones[b.index];
      bone.position.set(b.t[0], b.t[1], b.t[2]);
      bone.quaternion.set(b.r[0], b.r[1], b.r[2], b.r[3]);
      bone.scale.set(b.s[0], b.s[1], b.s[2]);
    }
  }

  /**
   * The idle pose shown while no clip is loaded: the bind pose with the root turned 180 degrees about GTA's Z. The
   * .yft bind pose faces -Y (SKEL_ROOT carries a half turn) whereas every animation's root track faces +Y, so
   * without this the ped would show its back to the "front" camera between emotes.
   */
  setIdlePose(): void {
    this.resetPose();
    const root = this.bones[0];
    if (root) root.quaternion.multiply(new THREE.Quaternion(0, 0, 1, 0));
  }

  private helpers = false;
  private figure = true;

  setHelpersVisible(visible: boolean): void {
    this.helpers = visible;
    this.applyVisibility();
  }

  /** Hides the stick figure (bones and joints) while the mannequin is shown; helper bones follow their own toggle. */
  setFigureVisible(visible: boolean): void {
    this.figure = visible;
    this.applyVisibility();
  }

  private applyVisibility(): void {
    this.boneLines.visible = this.figure;
    this.skelJoints.visible = this.figure;
    this.helperLines.visible = this.helpers;
    this.helperJoints.visible = this.helpers;
  }

  /**
   * Copies the bones' world positions into the stick-figure geometry. Call after the mixer updated the bones, and
   * after the rig this one hangs on (if any) was updated, because the entity's world matrix builds on its parent's.
   */
  update(): void {
    if (this.standalone) this.frame.updateMatrixWorld(true);
    else this.entity.updateMatrixWorld(true);
    const world = (i: number) => this.bones[i].getWorldPosition(this.tmp);
    this.fillSegments(this.boneLines, this.skelSegments, world);
    if (this.helperLines.visible) this.fillSegments(this.helperLines, this.helperSegments, world);
    this.fillPoints(this.skelJoints, this.skelIndices, world);
    if (this.helperJoints.visible) this.fillPoints(this.helperJoints, this.helperIndices, world);
  }

  private fillSegments(lines: THREE.LineSegments, segments: [number, number][], world: (i: number) => THREE.Vector3): void {
    const attr = lines.geometry.getAttribute("position") as THREE.BufferAttribute;
    const a = attr.array as Float32Array;
    for (let n = 0; n < segments.length; n++) {
      const [p, c] = segments[n];
      const wp = world(p);
      a[n * 6] = wp.x; a[n * 6 + 1] = wp.y; a[n * 6 + 2] = wp.z;
      const wc = world(c);
      a[n * 6 + 3] = wc.x; a[n * 6 + 4] = wc.y; a[n * 6 + 5] = wc.z;
    }
    attr.needsUpdate = true;
  }

  private fillPoints(points: THREE.Points, indices: number[], world: (i: number) => THREE.Vector3): void {
    const attr = points.geometry.getAttribute("position") as THREE.BufferAttribute;
    const a = attr.array as Float32Array;
    for (let n = 0; n < indices.length; n++) {
      const w = world(indices[n]);
      a[n * 3] = w.x; a[n * 3 + 1] = w.y; a[n * 3 + 2] = w.z;
    }
    attr.needsUpdate = true;
  }

  dispose(): void {
    this.unTheme();
    this.detach();
    this.scene.scene.remove(this.frame, this.boneLines, this.helperLines, this.skelJoints, this.helperJoints);
    for (const o of [this.boneLines, this.helperLines, this.skelJoints, this.helperJoints]) o.geometry.dispose();
    for (const m of [this.boneMat, this.helperMat, this.jointMat, this.helperJointMat]) m.dispose();
  }
}

function lineGeometry(vertices: number): THREE.BufferGeometry {
  const g = new THREE.BufferGeometry();
  g.setAttribute("position", new THREE.BufferAttribute(new Float32Array(Math.max(1, vertices) * 3), 3));
  g.setDrawRange(0, vertices);
  return g;
}
