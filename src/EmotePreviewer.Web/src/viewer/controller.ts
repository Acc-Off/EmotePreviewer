import * as THREE from "three";
import type { LoadedClip, LoadedMesh } from "../shared/api";
import type { PartnerPlacementDto, PropDto, SkeletonDto } from "../shared/types";
import { PedMesh } from "./ped";
import { Playback, Timeline } from "./playback";
import { PropLayer } from "./props";
import { Scene, type CameraPreset } from "./scene";
import { SkeletonRig } from "./skeleton";
import { TextureCache } from "./textures";

/** The two characters a viewer can show: the selected emote's ped and, for shared emotes, the other side. */
export type Slot = "main" | "partner";

/** How a slot's clip sits on the shared timeline. */
export interface ClipTiming {
  /** Seconds after the timeline start at which this clip begins (its first pose is held before that). */
  delay: number;
  /** Whether the clip repeats when the timeline runs past its end (otherwise the last pose is held). */
  loop: boolean;
}

/** Everything that belongs to one ped: skeleton, stick figure, props, mannequin and the clip posing it. */
class RigSlot {
  readonly rig: SkeletonRig;
  readonly props: PropLayer;
  readonly ped: PedMesh;
  readonly playback: Playback;
  timing: ClipTiming = { delay: 0, loop: false };

  constructor(skeleton: SkeletonDto, scene: Scene, textures: TextureCache) {
    this.rig = new SkeletonRig(skeleton, scene);
    this.props = new PropLayer(this.rig, scene, textures);
    this.ped = new PedMesh(this.rig, scene, textures);
    this.playback = new Playback(this.rig);
  }

  /** Clip time for a timeline time: waits out the delay, then wraps or holds at the end. */
  clipTime(t: number): number {
    const local = t - this.timing.delay;
    const d = this.playback.duration;
    if (local <= 0 || d <= 0) return 0;
    if (local < d) return local;
    return this.timing.loop ? local % d : d;
  }

  /** Seconds of timeline this clip occupies (delay + duration; a looping clip does not extend the timeline on its own). */
  get extent(): number {
    return this.playback.clip ? this.timing.delay + this.playback.duration : 0;
  }

  dispose(): void {
    this.playback.dispose();
    this.props.dispose();
    this.ped.dispose();
    this.rig.dispose();
  }
}

/**
 * Owns the three.js objects for one canvas: the scene, up to two rigs (main and partner) and the timeline that poses
 * them. Created in an effect, disposed on unmount; never stored in React state.
 */
export class ViewerController {
  readonly scene: Scene;
  readonly timeline = new Timeline();
  private readonly textures: TextureCache;
  private readonly slots: { main: RigSlot | null; partner: RigSlot | null } = { main: null, partner: null };
  private placement: PartnerPlacementDto | null = null;
  /** Group that carries the attachment offset when one rig hangs on a bone of the other. */
  private attachGroup: THREE.Group | null = null;
  private helpers = false;
  private showProps = true;
  private showMesh = false;
  private showTextures = true;
  private rootMotion = false;
  /** Set when a rig must be re-posed even though the timeline did not move (clip loaded, time sought, rig rebuilt). */
  private dirty = true;
  /** Notified when playback reaches the end of a non-looping timeline (to flip the play button). */
  onFinished: (() => void) | null = null;

  constructor(canvas: HTMLCanvasElement) {
    this.scene = new Scene(canvas);
    this.scene.onTick = (dt) => this.tick(dt);
    this.textures = new TextureCache(this.scene.renderer);
    this.timeline.onFinished = () => this.onFinished?.();
  }

  /** Name of the skeleton a slot's rig was built from (null while the slot is empty). */
  skeleton(slot: Slot = "main"): string | null {
    return this.slots[slot]?.rig.def.name ?? null;
  }

  hasRig(slot: Slot = "main"): boolean {
    return this.slots[slot] !== null;
  }

  /** The main rig's clip time (what the transport shows as frames). */
  get mainTime(): number {
    const main = this.slots.main;
    return main ? main.clipTime(this.timeline.time) : 0;
  }

  /** (Re)builds a slot's bone hierarchy. A clip that was loaded is dropped; the caller reloads it. */
  setSkeleton(slot: Slot, skeleton: SkeletonDto): void {
    const existing = this.slots[slot];
    if (existing && existing.rig.def.name === skeleton.name && existing.rig.def.bones.length === skeleton.bones.length) return;
    this.removeRig(slot);
    const rigSlot = new RigSlot(skeleton, this.scene, this.textures);
    rigSlot.rig.setHelpersVisible(this.helpers);
    rigSlot.rig.setFigureVisible(!this.showMesh);
    rigSlot.props.setVisible(this.showProps);
    rigSlot.props.setTextured(this.showTextures);
    rigSlot.ped.setVisible(this.showMesh);
    rigSlot.ped.setTextured(this.showTextures);
    rigSlot.playback.setRootMotion(this.rootMotion);
    this.slots[slot] = rigSlot;
    this.applyPlacement();
    this.updateDuration();
    this.dirty = true;
  }

  removeRig(slot: Slot): void {
    const existing = this.slots[slot];
    if (!existing) return;
    this.detachAll();
    existing.dispose();
    this.slots[slot] = null;
    this.applyPlacement();
    this.updateDuration();
    this.dirty = true;
  }

  /** Loads a clip into a slot. The main clip restarts the timeline; the partner joins it where it is. */
  loadClip(slot: Slot, clip: LoadedClip, timing: ClipTiming, autoplay = true): void {
    const rigSlot = this.slots[slot];
    if (!rigSlot) return;
    rigSlot.timing = timing;
    rigSlot.playback.load(clip);
    this.updateDuration();
    if (slot === "main") {
      this.timeline.seek(0);
      if (autoplay) this.timeline.play();
      else this.timeline.pause();
    }
    this.dirty = true;
  }

  /** Replaces a slot's clip data without touching the timeline (a rewritten .ycd picked up by a rescan): the playhead and play state stay. */
  replaceClip(slot: Slot, clip: LoadedClip): void {
    const rigSlot = this.slots[slot];
    if (!rigSlot) return;
    rigSlot.playback.load(clip);
    this.updateDuration();
    this.dirty = true;
  }

  clearClip(slot: Slot): void {
    const rigSlot = this.slots[slot];
    if (!rigSlot) return;
    rigSlot.playback.clear();
    rigSlot.timing = { delay: 0, loop: false };
    this.updateDuration();
    if (slot === "main") {
      this.timeline.pause();
      this.timeline.seek(0);
    }
    this.dirty = true;
  }

  /** Where the partner goes relative to the main ped; null puts both at the origin. Applied whenever both rigs exist. */
  setPlacement(placement: PartnerPlacementDto | null): void {
    this.placement = placement;
    this.applyPlacement();
    this.dirty = true;
  }

  /** Attaches the loaded prop meshes to a slot's bones (replacing the previous props). */
  setProps(slot: Slot, props: { prop: PropDto; mesh: LoadedMesh | null }[]): void {
    this.slots[slot]?.props.set(props);
  }

  clearProps(slot: Slot): void {
    this.slots[slot]?.props.clear();
  }

  setPropsVisible(on: boolean): void {
    this.showProps = on;
    this.forEach((s) => s.props.setVisible(on));
  }

  /** Debug hook: switch the placement Euler order and re-attach the props. */
  setPropEulerOrder(order: "XYZ" | "XZY" | "YXZ" | "YZX" | "ZXY" | "ZYX"): void {
    PropLayer.eulerOrder = order;
    this.forEach((s) => s.props.refresh());
    this.applyPlacement();
  }

  /** Diffuse textures on props and the peds, or flat colours. */
  setTextures(on: boolean): void {
    this.showTextures = on;
    this.forEach((s) => {
      s.props.setTextured(on);
      s.ped.setTextured(on);
    });
  }

  /** Binds a slot's mannequin components; the current pose is restored afterwards. */
  setPedMesh(slot: Slot, components: LoadedMesh[]): void {
    const rigSlot = this.slots[slot];
    if (!rigSlot) return;
    const clip = rigSlot.playback.clip;
    rigSlot.ped.set(components);
    if (clip) {
      // Binding reset the bones to the bind pose; rebuild the action so the next tick poses them again.
      rigSlot.playback.load(clip);
    } else {
      rigSlot.rig.setIdlePose();
    }
    this.dirty = true;
  }

  hasPedMesh(slot: Slot = "main"): boolean {
    return this.slots[slot]?.ped.loaded ?? false;
  }

  /** Shows the mannequins instead of the stick figures (helper bones stay toggleable). */
  setMeshVisible(on: boolean): void {
    this.showMesh = on;
    this.forEach((s) => {
      s.ped.setVisible(on);
      s.rig.setFigureVisible(!on);
    });
  }

  setHelpers(on: boolean): void {
    this.helpers = on;
    this.forEach((s) => s.rig.setHelpersVisible(on));
    this.dirty = true;
  }

  setRootMotion(on: boolean): void {
    this.rootMotion = on;
    this.forEach((s) => s.playback.setRootMotion(on));
    this.dirty = true;
  }

  setLoop(on: boolean): void {
    this.timeline.setLoop(on);
  }

  setSpeed(speed: number): void {
    this.timeline.setSpeed(speed);
  }

  /** Jumps the timeline and shows that pose even while paused. */
  seek(t: number): void {
    this.timeline.seek(t);
    this.dirty = true;
  }

  setCamera(preset: CameraPreset): void {
    this.scene.setCamera(preset);
  }

  /** World positions of every bone of a slot in three.js space (debugging / tests). */
  worldPositions(slot: Slot = "main"): number[][] {
    const rigSlot = this.slots[slot];
    if (!rigSlot) return [];
    this.scene.scene.updateMatrixWorld(true);
    const v = new THREE.Vector3();
    return rigSlot.rig.bones.map((b) => {
      b.getWorldPosition(v);
      return [v.x, v.y, v.z];
    });
  }

  dispose(): void {
    this.detachAll();
    this.forEach((s) => s.dispose());
    this.slots.main = this.slots.partner = null;
    this.textures.dispose();
    this.scene.dispose();
  }

  private forEach(fn: (slot: RigSlot) => void): void {
    if (this.slots.main) fn(this.slots.main);
    if (this.slots.partner) fn(this.slots.partner);
  }

  /** Rigs in dependency order: the one another rig hangs on comes first. */
  private ordered(): RigSlot[] {
    const { main, partner } = this.slots;
    const list: RigSlot[] = [];
    if (this.placement?.kind === "attach" && this.placement.attached === "main") {
      if (partner) list.push(partner);
      if (main) list.push(main);
    } else {
      if (main) list.push(main);
      if (partner) list.push(partner);
    }
    return list;
  }

  private tick(dt: number): void {
    const moved = this.timeline.advance(dt);
    const t = this.timeline.time;
    for (const s of this.ordered()) {
      if ((moved || this.dirty) && s.playback.clip) s.playback.pose(s.clipTime(t));
      s.rig.update();
    }
    this.dirty = false;
  }

  private updateDuration(): void {
    let duration = 0;
    this.forEach((s) => (duration = Math.max(duration, s.extent)));
    this.timeline.setDuration(duration);
  }

  private detachAll(): void {
    this.forEach((s) => s.rig.detach());
    if (this.attachGroup) {
      this.attachGroup.removeFromParent();
      this.attachGroup = null;
    }
  }

  /**
   * Places the partner relative to the main ped the way the emote menus do: an offset puts the partner's entity at
   * (x, y, z) in the main ped's frame turned by the heading, an attachment re-parents one entity under a bone of the
   * other with the prop-style offset (rotation order Y, Z, X). Both rigs stand at the origin while one is missing.
   */
  private applyPlacement(): void {
    this.detachAll();
    const { main, partner } = this.slots;
    const placement = this.placement;
    if (!main || !partner || !placement) return;
    if (placement.kind === "offset") {
      const [x, y, z] = placement.position ?? [0, 1, 0];
      partner.rig.entity.position.set(x, y, z);
      partner.rig.entity.rotation.set(0, 0, THREE.MathUtils.degToRad(placement.heading ?? 180));
      return;
    }
    const attachedMain = placement.attached === "main";
    const anchor = attachedMain ? partner : main;
    const hanging = attachedMain ? main : partner;
    const bone = placement.bone ?? -1;
    const parent = (bone >= 0 ? anchor.rig.boneByTag(bone) : null) ?? anchor.rig.entity;
    const [x, y, z, rx, ry, rz] = placement.offset ?? [0, 0, 0, 0, 0, 0];
    const group = new THREE.Group();
    group.name = "attach";
    group.position.set(x, y, z);
    group.rotation.set(THREE.MathUtils.degToRad(rx), THREE.MathUtils.degToRad(ry), THREE.MathUtils.degToRad(rz), PropLayer.eulerOrder);
    parent.add(group);
    hanging.rig.entity.removeFromParent();
    group.add(hanging.rig.entity);
    this.attachGroup = group;
  }
}
