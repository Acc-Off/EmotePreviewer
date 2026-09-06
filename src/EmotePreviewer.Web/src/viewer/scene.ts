import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";

export type CameraPreset = "reset" | "front" | "side" | "top";

/** Reads a CSS custom property from :root (theme colours live there). */
export function cssVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/**
 * Renderer, camera, controls, grid and theme handling. Everything three.js lives outside React; the component only
 * owns the canvas element and calls into this object.
 */
export class Scene {
  readonly renderer: THREE.WebGLRenderer;
  readonly scene = new THREE.Scene();
  readonly camera: THREE.PerspectiveCamera;
  readonly controls: OrbitControls;
  readonly grid: THREE.GridHelper;
  /** Called every frame before rendering with the elapsed seconds. */
  onTick: ((dt: number) => void) | null = null;

  private readonly canvas: HTMLCanvasElement;
  private readonly resizeObserver: ResizeObserver;
  private readonly themeObserver: MutationObserver;
  private readonly media = matchMedia("(prefers-color-scheme: dark)");
  private frame = 0;
  private last = 0;
  private disposed = false;
  private readonly target = new THREE.Vector3(0, 0.9, 0);

  constructor(canvas: HTMLCanvasElement) {
    this.canvas = canvas;
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: false, powerPreference: "low-power" });
    this.renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    this.camera = new THREE.PerspectiveCamera(40, 1, 0.05, 100);
    this.controls = new OrbitControls(this.camera, canvas);
    this.controls.enableDamping = true;
    this.controls.dampingFactor = 0.12;
    this.controls.maxPolarAngle = Math.PI * 0.95;
    this.controls.minDistance = 0.3;
    this.controls.maxDistance = 20;
    this.grid = new THREE.GridHelper(4, 16, 0xffffff, 0xffffff);
    (this.grid.material as THREE.Material).transparent = true;
    (this.grid.material as THREE.Material).opacity = 0.9;
    this.scene.add(this.grid);
    const hemi = new THREE.HemisphereLight(0xffffff, 0x8899aa, 1.6);
    const sun = new THREE.DirectionalLight(0xffffff, 1.4);
    sun.position.set(2.5, 4, -3); // in front of the ped (its face is on the −Z side)
    this.scene.add(hemi, sun);
    this.setCamera("reset");
    this.applyTheme();

    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(canvas);
    this.themeObserver = new MutationObserver(() => this.applyTheme());
    this.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    this.media.addEventListener("change", this.applyTheme);
    this.resize();
    this.last = performance.now();
    this.frame = requestAnimationFrame(this.tick);
  }

  /**
   * Camera presets. A GTA ped faces +Y, which the Z-up → Y-up rotation of the rig turns into −Z in three.js, so
   * "front" (the face) is on the −Z side and the default view looks at the ped's front-left three-quarter.
   */
  setCamera(preset: CameraPreset): void {
    const t = this.target;
    switch (preset) {
      case "front":
        this.camera.position.set(0, 1.1, -3.4);
        break;
      case "side":
        this.camera.position.set(3.4, 1.1, 0);
        break;
      case "top":
        this.camera.position.set(0, 4.2, -0.01);
        break;
      default:
        this.camera.position.set(2.2, 1.6, -2.6);
    }
    this.controls.target.copy(t);
    this.controls.update();
  }

  private readonly applyTheme = (): void => {
    this.scene.background = new THREE.Color(cssVar("--viewport") || "#e3e6eb");
    (this.grid.material as THREE.LineBasicMaterial).color.set(cssVar("--grid") || "#c4cad3");
    this.themeListeners.forEach((l) => l());
  };

  private readonly themeListeners = new Set<() => void>();

  /** Registers a callback run whenever the theme colours change (materials re-read their CSS variables). */
  onTheme(listener: () => void): () => void {
    this.themeListeners.add(listener);
    listener();
    return () => this.themeListeners.delete(listener);
  }

  private resize(): void {
    const w = this.canvas.clientWidth;
    const h = this.canvas.clientHeight;
    if (w === 0 || h === 0) return;
    this.renderer.setSize(w, h, false);
    this.camera.aspect = w / h;
    this.camera.updateProjectionMatrix();
  }

  private readonly tick = (now: number): void => {
    if (this.disposed) return;
    this.frame = requestAnimationFrame(this.tick);
    const dt = Math.min(0.25, (now - this.last) / 1000);
    this.last = now;
    this.onTick?.(dt);
    this.controls.update();
    this.renderer.render(this.scene, this.camera);
  };

  dispose(): void {
    this.disposed = true;
    cancelAnimationFrame(this.frame);
    this.resizeObserver.disconnect();
    this.themeObserver.disconnect();
    this.media.removeEventListener("change", this.applyTheme);
    this.controls.dispose();
    this.renderer.dispose();
  }
}
