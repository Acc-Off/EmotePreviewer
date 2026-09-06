import * as THREE from "three";
import type { LoadedClip } from "../shared/api";
import type { SkeletonRig } from "./skeleton";

/**
 * Poses one rig from a baked clip (`clip.bin`: [frame][bone] × (px, py, pz, qx, qy, qz, qw), then optional root
 * motion per frame) through a `THREE.AnimationMixer`. It has no clock of its own: the controller's `Timeline` decides
 * the time of every rig so that the two peds of a shared emote stay in step.
 */
export class Playback {
  readonly mixer: THREE.AnimationMixer;
  private readonly rig: SkeletonRig;
  private action: THREE.AnimationAction | null = null;
  private loaded: LoadedClip | null = null;
  private _rootMotion = false;
  private _time = 0;

  constructor(rig: SkeletonRig) {
    this.rig = rig;
    this.mixer = new THREE.AnimationMixer(rig.entity);
  }

  get clip(): LoadedClip | null {
    return this.loaded;
  }

  get duration(): number {
    return this.loaded?.meta.duration ?? 0;
  }

  /** The clip time the bones currently show. */
  get time(): number {
    return this._time;
  }

  get rootMotion(): boolean {
    return this._rootMotion;
  }

  load(clip: LoadedClip): void {
    this.clear();
    this.loaded = clip;
    this.rebuild();
  }

  clear(): void {
    if (this.action) {
      this.action.stop();
      this.mixer.uncacheClip(this.action.getClip());
      this.action = null;
    }
    this.loaded = null;
    this._time = 0;
    this.rig.setIdlePose();
  }

  /** Shows the pose at a clip time in seconds (clamped to the clip). The caller refreshes the stick figure. */
  pose(t: number): void {
    if (!this.action) return;
    const clamped = Math.max(0, Math.min(this.duration, t));
    this._time = clamped;
    this.action.time = clamped;
    this.mixer.update(0);
  }

  setRootMotion(on: boolean): void {
    if (this._rootMotion === on) return;
    this._rootMotion = on;
    if (this.loaded) this.rebuild();
  }

  private rebuild(): void {
    if (!this.loaded) return;
    if (this.action) {
      this.action.stop();
      this.mixer.uncacheClip(this.action.getClip());
    }
    this.rig.resetPose();
    const clip = buildAnimationClip(this.loaded, this.rig, this._rootMotion && this.loaded.meta.hasRootMotion);
    this.action = this.mixer.clipAction(clip);
    // The action is only ever sampled at an explicit time (mixer.update(0)), so it must never finish or wrap by itself.
    this.action.setLoop(THREE.LoopRepeat, Infinity);
    this.action.play();
    this.pose(this._time);
  }

  dispose(): void {
    this.clear();
    this.mixer.stopAllAction();
  }
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

/** One position and one quaternion track per bone. Times are `frame / fps`; the mixer interpolates between frames. */
export function buildAnimationClip(loaded: LoadedClip, rig: SkeletonRig, rootMotion: boolean): THREE.AnimationClip {
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
