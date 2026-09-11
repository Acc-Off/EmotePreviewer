import * as THREE from "three";
import type { LoadedClip } from "../shared/api";
import type { EmoteSlot } from "../shared/types";
import type { SkeletonRig } from "./skeleton";

/** What one slot of a ped's playback holds. */
interface Layer {
  loaded: LoadedClip;
  action: THREE.AnimationAction | null;
  /** The clip time the bones currently show. */
  time: number;
}

/**
 * Poses one rig from baked clips (`clip.bin`: [frame][bone] × (px, py, pz, qx, qy, qz, qw), then optional root motion
 * per frame) through a `THREE.AnimationMixer`, the way the game layers its two animation slots:
 *
 * - the **primary** clip drives every bone;
 * - the **secondary** clip takes over the bones of the upper-body mask that it animates (SKEL_Spine_Root subtree), so
 *   the pelvis, the legs and SKEL_ROOT — position and rotation — stay with the primary clip;
 * - when the secondary animates SKEL_ROOT, its torso is re-rooted: the spine root's local rotation becomes
 *   inv(primary ROOT rotation) × secondary ROOT rotation × spine-root local, which leans the torso with the secondary's
 *   root while the legs follow the primary's;
 * - the two slots have independent clocks (`pose` takes one time per slot) and the boundary weights are 0 / 1.
 *
 * The two actions have disjoint tracks, so the mixer never blends them. It has no clock of its own: the controller's
 * `Timeline` decides the times so that the two peds of a shared emote stay in step.
 */
export class Playback {
  readonly mixer: THREE.AnimationMixer;
  private readonly rig: SkeletonRig;
  private readonly layers: Record<EmoteSlot, Layer | null> = { primary: null, secondary: null };
  /** Bones the secondary action owns (upper-body mask ∩ bones the secondary clip animates). */
  private secondaryBones = new Set<number>();
  /** True when the secondary clip animates SKEL_ROOT and the skeleton has a spine root to re-root. */
  private reRoot = false;
  private _rootMotion = false;
  private readonly qA = new THREE.Quaternion();
  private readonly qB = new THREE.Quaternion();
  private readonly qS = new THREE.Quaternion();

  constructor(rig: SkeletonRig) {
    this.rig = rig;
    this.mixer = new THREE.AnimationMixer(rig.entity);
  }

  clip(layer: EmoteSlot = "primary"): LoadedClip | null {
    return this.layers[layer]?.loaded ?? null;
  }

  get hasClip(): boolean {
    return this.layers.primary !== null || this.layers.secondary !== null;
  }

  duration(layer: EmoteSlot = "primary"): number {
    return this.layers[layer]?.loaded.meta.duration ?? 0;
  }

  /** The clip time a layer's bones currently show. */
  time(layer: EmoteSlot = "primary"): number {
    return this.layers[layer]?.time ?? 0;
  }

  get rootMotion(): boolean {
    return this._rootMotion;
  }

  /** Loads a clip into a slot (replacing what that slot had) and rebuilds both actions, since their bone sets depend on each other. */
  load(layer: EmoteSlot, clip: LoadedClip): void {
    this.stopActions();
    this.layers[layer] = { loaded: clip, action: null, time: 0 };
    this.rebuild();
  }

  clear(layer: EmoteSlot): void {
    if (!this.layers[layer]) return;
    this.stopActions();
    this.layers[layer] = null;
    this.rebuild();
  }

  clearAll(): void {
    this.stopActions();
    this.layers.primary = this.layers.secondary = null;
    this.rebuild();
  }

  /** Shows the poses at the given clip times in seconds (each clamped to its clip). The caller refreshes the stick figure. */
  pose(primaryTime: number, secondaryTime: number): void {
    const { primary, secondary } = this.layers;
    if (primary?.action) {
      primary.time = Math.max(0, Math.min(primary.loaded.meta.duration, primaryTime));
      primary.action.time = primary.time;
    }
    if (secondary?.action) {
      secondary.time = Math.max(0, Math.min(secondary.loaded.meta.duration, secondaryTime));
      secondary.action.time = secondary.time;
    }
    if (!primary?.action && !secondary?.action) return;
    this.mixer.update(0);
    if (this.reRoot && secondary) this.applyReRoot(primary, secondary);
  }

  setRootMotion(on: boolean): void {
    if (this._rootMotion === on) return;
    this._rootMotion = on;
    if (this.hasClip) {
      this.stopActions();
      this.rebuild();
    }
  }

  /** Rebuilds the actions after the bones were rebound (the mannequin was attached); the current times are kept. */
  rebind(): void {
    this.stopActions();
    this.rebuild();
  }

  dispose(): void {
    this.clearAll();
    this.mixer.stopAllAction();
  }

  private stopActions(): void {
    for (const layer of [this.layers.primary, this.layers.secondary]) {
      if (!layer?.action) continue;
      layer.action.stop();
      this.mixer.uncacheClip(layer.action.getClip());
      layer.action = null;
    }
  }

  private rebuild(): void {
    const { primary, secondary } = this.layers;
    // Bones no action binds keep what the rig shows: the bind pose under a primary clip (it drives them all anyway),
    // the idle pose (root turned to face the front camera) when only a secondary plays.
    if (primary) this.rig.resetPose();
    else this.rig.setIdlePose();
    this.secondaryBones = new Set(secondary ? secondary.loaded.meta.animatedBones.filter((b) => this.rig.upperBodyMask.has(b)) : []);
    this.reRoot = secondary !== null && this.rig.spineRootIndex >= 0 && secondary.loaded.meta.animatedBones.includes(0);
    if (primary) primary.action = this.start(buildAnimationClip(primary.loaded, this.rig, this._rootMotion && primary.loaded.meta.hasRootMotion, (b) => !this.secondaryBones.has(b)));
    if (secondary) secondary.action = this.start(buildAnimationClip(secondary.loaded, this.rig, false, (b) => this.secondaryBones.has(b)));
    this.pose(primary?.time ?? 0, secondary?.time ?? 0);
  }

  private start(clip: THREE.AnimationClip): THREE.AnimationAction {
    const action = this.mixer.clipAction(clip);
    // Actions are only ever sampled at an explicit time (mixer.update(0)), so they must never finish or wrap by themselves.
    action.setLoop(THREE.LoopRepeat, Infinity);
    action.play();
    return action;
  }

  /**
   * spineRoot.local = inv(primaryRoot(tP)) × secondaryRoot(tS) × spineRoot.own, all three read from the clip data:
   * the mixer skips bones whose evaluated value did not change, so building on the bone's current quaternion would
   * stack the correction every time the same times are posed again. The primary's root rotation comes from its data
   * rather than the bone, which may carry root motion (the mover turns the whole ped, torso included); without a
   * primary clip the root bone holds the idle pose and is used as is.
   */
  private applyReRoot(primary: Layer | null, secondary: Layer): void {
    const index = this.rig.spineRootIndex;
    const spine = this.rig.bones[index];
    if (primary) sampleQuaternion(primary.loaded, 0, primary.time, this.qA);
    else this.qA.copy(this.rig.bones[0].quaternion);
    sampleQuaternion(secondary.loaded, 0, secondary.time, this.qB);
    // The spine root's own local rotation, from whichever clip owns its track (the bind pose when neither does).
    if (this.secondaryBones.has(index)) sampleQuaternion(secondary.loaded, index, secondary.time, this.qS);
    else if (primary) sampleQuaternion(primary.loaded, index, primary.time, this.qS);
    else {
      const b = this.rig.def.bones[index];
      this.qS.set(b.r[0], b.r[1], b.r[2], b.r[3]);
    }
    spine.quaternion.copy(this.qS).premultiply(this.qB).premultiply(this.qA.invert());
  }
}

/** A bone's local rotation of a baked clip at a time, interpolated between frames the way the mixer does. */
export function sampleQuaternion(loaded: LoadedClip, bone: number, t: number, target: THREE.Quaternion): THREE.Quaternion {
  const { frames, boneCount, fps } = loaded.meta;
  const stride = boneCount * 7;
  const f = Math.max(0, Math.min(frames - 1, t * fps));
  const f0 = Math.floor(f);
  const f1 = Math.min(frames - 1, f0 + 1);
  const o0 = f0 * stride + bone * 7 + 3;
  target.set(loaded.data[o0], loaded.data[o0 + 1], loaded.data[o0 + 2], loaded.data[o0 + 3]);
  if (f1 !== f0) {
    const o1 = f1 * stride + bone * 7 + 3;
    target.slerp(new THREE.Quaternion(loaded.data[o1], loaded.data[o1 + 1], loaded.data[o1 + 2], loaded.data[o1 + 3]), f - f0);
  }
  return target;
}

/**
 * The shared clock of the viewer: one time axis that every rig is posed from. Its length is the longest
 * "start delay + clip" of the rigs; a rig whose clip is shorter either holds its last pose or wraps on its own
 * (looping emotes), and a rig whose start delay has not passed yet holds its first pose.
 */
export class Timeline {
  private _time = 0;
  private _playing = false;
  private _loop = true;
  private _speed = 1;
  private _duration = 0;
  /** Fires when a non-looping timeline reaches its end. */
  onFinished: (() => void) | null = null;

  get time(): number {
    return this._time;
  }

  get playing(): boolean {
    return this._playing;
  }

  get loop(): boolean {
    return this._loop;
  }

  get speed(): number {
    return this._speed;
  }

  get duration(): number {
    return this._duration;
  }

  setDuration(duration: number): void {
    this._duration = Math.max(0, duration);
    if (this._time > this._duration) this._time = this._duration;
  }

  setLoop(on: boolean): void {
    this._loop = on;
  }

  setSpeed(speed: number): void {
    this._speed = speed;
  }

  play(): void {
    if (this._duration <= 0) return;
    if (!this._loop && this._time >= this._duration - 1e-4) this._time = 0;
    this._playing = true;
  }

  pause(): void {
    this._playing = false;
  }

  toggle(): void {
    if (this._playing) this.pause();
    else this.play();
  }

  /** Jumps to a time in seconds (clamped). */
  seek(t: number): void {
    this._time = Math.max(0, Math.min(this._duration, t));
  }

  /** Advances the clock while playing; returns true when the time changed. */
  advance(dt: number): boolean {
    if (!this._playing || this._duration <= 0) return false;
    let t = this._time + dt * this._speed;
    if (t >= this._duration) {
      if (this._loop) {
        t = t % this._duration;
      } else {
        t = this._duration;
        this._playing = false;
        this._time = t;
        this.onFinished?.();
        return true;
      }
    }
    this._time = t;
    return true;
  }
}

/**
 * One position and one quaternion track per bone that passes `include` (all bones by default). Times are `frame / fps`;
 * the mixer interpolates between frames.
 */
export function buildAnimationClip(loaded: LoadedClip, rig: SkeletonRig, rootMotion: boolean, include: (bone: number) => boolean = () => true): THREE.AnimationClip {
  const { meta, data } = loaded;
  const { frames, boneCount, fps } = meta;
  const times = new Float32Array(frames);
  for (let f = 0; f < frames; f++) times[f] = f / fps;
  const tracks: THREE.KeyframeTrack[] = [];
  const stride = boneCount * 7;
  const rootBase = frames * stride;
  const rmT = new THREE.Vector3();
  const rmQ = new THREE.Quaternion();
  const boneT = new THREE.Vector3();
  const boneQ = new THREE.Quaternion();

  for (let b = 0; b < boneCount; b++) {
    if (!include(b)) continue;
    const pos = new Float32Array(frames * 3);
    const rot = new Float32Array(frames * 4);
    for (let f = 0; f < frames; f++) {
      const o = f * stride + b * 7;
      if (rootMotion && b === 0) {
        // Same composition as the server's PoseSolver: root' = rm.t + rm.r × root.t ; rootRot' = rm.r × root.r
        const r = rootBase + f * 7;
        rmT.set(data[r], data[r + 1], data[r + 2]);
        rmQ.set(data[r + 3], data[r + 4], data[r + 5], data[r + 6]);
        boneT.set(data[o], data[o + 1], data[o + 2]).applyQuaternion(rmQ).add(rmT);
        boneQ.set(data[o + 3], data[o + 4], data[o + 5], data[o + 6]).premultiply(rmQ);
        pos[f * 3] = boneT.x; pos[f * 3 + 1] = boneT.y; pos[f * 3 + 2] = boneT.z;
        rot[f * 4] = boneQ.x; rot[f * 4 + 1] = boneQ.y; rot[f * 4 + 2] = boneQ.z; rot[f * 4 + 3] = boneQ.w;
      } else {
        pos[f * 3] = data[o]; pos[f * 3 + 1] = data[o + 1]; pos[f * 3 + 2] = data[o + 2];
        rot[f * 4] = data[o + 3]; rot[f * 4 + 1] = data[o + 4]; rot[f * 4 + 2] = data[o + 5]; rot[f * 4 + 3] = data[o + 6];
      }
    }
    const name = rig.boneName(b);
    tracks.push(new THREE.VectorKeyframeTrack(`${name}.position`, times, pos));
    tracks.push(new THREE.QuaternionKeyframeTrack(`${name}.quaternion`, times, rot));
  }
  const duration = frames > 1 ? Math.max(meta.duration, times[frames - 1]) : Math.max(meta.duration, 1 / fps);
  return new THREE.AnimationClip(`${meta.dictionary}/${meta.clip}`, duration, tracks);
}
