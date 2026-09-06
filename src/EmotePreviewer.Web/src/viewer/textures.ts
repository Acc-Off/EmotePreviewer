import * as THREE from "three";
import type { LoadedMesh } from "../shared/api";
import type { MeshTextureDto } from "../shared/types";

/**
 * Diffuse textures for props and peds. The server serves DDS files: BC1 / BC2 / BC3 (S3TC), BC7 (BPTC) and RGBA8.
 * Compressed data goes to the GPU as-is when the extension exists; BC1 / BC3 can be re-requested decoded to RGBA
 * (`?format=rgba`) for browsers without S3TC. BC7 without BPTC and every failure fall back to the plain material.
 */

interface ParsedDds {
  width: number;
  height: number;
  format: THREE.CompressedPixelFormat | null; // null = uncompressed RGBA8
  mipmaps: { data: Uint8Array; width: number; height: number }[];
}

const FOURCC_DXT1 = 0x31545844;
const FOURCC_DXT3 = 0x33545844;
const FOURCC_DXT5 = 0x35545844;
const FOURCC_DX10 = 0x30315844;
const DXGI_BC7_UNORM = 98;

function blockBytes(format: THREE.CompressedPixelFormat): number {
  return format === THREE.RGB_S3TC_DXT1_Format || format === THREE.RGBA_S3TC_DXT1_Format ? 8 : 16;
}

/** Parses the DDS container the server writes (see TextureImage.ToDds). Throws on anything else. */
export function parseDds(buffer: ArrayBuffer): ParsedDds {
  const view = new DataView(buffer);
  if (buffer.byteLength < 128 || view.getUint32(0, true) !== 0x20534444) throw new Error("not a DDS file");
  const height = view.getUint32(12, true);
  const width = view.getUint32(16, true);
  const mipmapCount = Math.max(1, view.getUint32(28, true));
  const pfFlags = view.getUint32(80, true);
  const fourCC = view.getUint32(84, true);
  let offset = 128;
  let format: THREE.CompressedPixelFormat | null = null;
  if (pfFlags & 0x4) {
    switch (fourCC) {
      case FOURCC_DXT1:
        format = THREE.RGB_S3TC_DXT1_Format;
        break;
      case FOURCC_DXT3:
        format = THREE.RGBA_S3TC_DXT3_Format;
        break;
      case FOURCC_DXT5:
        format = THREE.RGBA_S3TC_DXT5_Format;
        break;
      case FOURCC_DX10: {
        const dxgi = view.getUint32(128, true);
        if (dxgi !== DXGI_BC7_UNORM) throw new Error(`unsupported DXGI format ${dxgi}`);
        format = THREE.RGBA_BPTC_Format;
        offset += 20;
        break;
      }
      default:
        throw new Error(`unsupported DDS FourCC 0x${fourCC.toString(16)}`);
    }
  } else if (view.getUint32(88, true) !== 32) {
    throw new Error("unsupported uncompressed DDS layout");
  }
  const mipmaps: ParsedDds["mipmaps"] = [];
  let w = width;
  let h = height;
  for (let i = 0; i < mipmapCount; i++) {
    const size = format ? Math.max(1, (w + 3) >> 2) * Math.max(1, (h + 3) >> 2) * blockBytes(format) : w * h * 4;
    if (offset + size > buffer.byteLength) break;
    mipmaps.push({ data: new Uint8Array(buffer, offset, size), width: w, height: h });
    offset += size;
    w = Math.max(1, w >> 1);
    h = Math.max(1, h >> 1);
  }
  if (mipmaps.length === 0) throw new Error("DDS has no image data");
  return { width, height, format, mipmaps };
}

/** Which compressed formats the GPU accepts. */
export interface GpuSupport {
  s3tc: boolean;
  bptc: boolean;
}

export function gpuSupport(renderer: THREE.WebGLRenderer): GpuSupport {
  return {
    s3tc: renderer.extensions.has("WEBGL_compressed_texture_s3tc"),
    bptc: renderer.extensions.has("EXT_texture_compression_bptc"),
  };
}

function toTexture(dds: ParsedDds): THREE.Texture {
  let texture: THREE.Texture;
  if (dds.format) {
    const compressed = new THREE.CompressedTexture(dds.mipmaps, dds.width, dds.height, dds.format, THREE.UnsignedByteType);
    texture = compressed;
  } else {
    const level0 = dds.mipmaps[0];
    const data = new THREE.DataTexture(level0.data, level0.width, level0.height, THREE.RGBAFormat, THREE.UnsignedByteType);
    data.mipmaps = dds.mipmaps.length > 1 ? dds.mipmaps.map((m) => ({ data: m.data, width: m.width, height: m.height })) : [];
    texture = data;
  }
  const fullChain = 1 + Math.floor(Math.log2(Math.max(dds.width, dds.height)));
  const complete = dds.mipmaps.length >= fullChain;
  texture.generateMipmaps = !dds.format && !complete;
  texture.minFilter = complete || texture.generateMipmaps ? THREE.LinearMipmapLinearFilter : THREE.LinearFilter;
  texture.magFilter = THREE.LinearFilter;
  texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
  texture.anisotropy = 4;
  // GTA textures use DirectX conventions: row 0 is the top of the image, which is where v = 0 samples with flipY off.
  texture.flipY = false;
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.needsUpdate = true;
  return texture;
}

/**
 * Loads textures by URL once and shares them between meshes. A texture whose format the GPU cannot take is fetched
 * decoded when the server can decode it; otherwise the promise resolves to null and the caller keeps the plain material.
 */
export class TextureCache {
  private readonly cache = new Map<string, Promise<THREE.Texture | null>>();
  private readonly support: GpuSupport;

  constructor(renderer: THREE.WebGLRenderer) {
    this.support = gpuSupport(renderer);
  }

  /** The URL (with the server's ETag as the cache-busting version) a mesh texture is fetched from. */
  static url(scope: string, texture: MeshTextureDto, mode: "rgba" | "gray" | null): string {
    const base = `/api/textures/${scope.split("/").map(encodeURIComponent).join("/")}/${encodeURIComponent(texture.name)}.dds?v=${encodeURIComponent(texture.eTag.replace(/"/g, ""))}`;
    return mode ? `${base}&format=${mode}` : base;
  }

  private gpuAccepts(format: string): boolean {
    if (format === "bc1" || format === "bc2" || format === "bc3") return this.support.s3tc;
    if (format === "bc7") return this.support.bptc;
    return format === "rgba8";
  }

  load(scope: string, texture: MeshTextureDto, signal?: AbortSignal): Promise<THREE.Texture | null> {
    const direct = this.gpuAccepts(texture.format);
    if ((!direct || texture.palette) && !texture.decodable) return Promise.resolve(null);
    const url = TextureCache.url(scope, texture, texture.palette ? "gray" : direct ? null : "rgba");
    let pending = this.cache.get(url);
    if (!pending) {
      pending = fetch(url, { signal })
        .then((r) => (r.ok ? r.arrayBuffer() : Promise.reject(new Error(`HTTP ${r.status}`))))
        .then((buffer) => toTexture(parseDds(buffer)))
        .catch((err: unknown) => {
          this.cache.delete(url);
          if (signal?.aborted) throw err;
          console.warn(`texture ${texture.name}: ${err instanceof Error ? err.message : String(err)}`);
          return null;
        });
      this.cache.set(url, pending);
    }
    return pending;
  }

  dispose(): void {
    for (const p of this.cache.values()) void p.then((t) => t?.dispose());
    this.cache.clear();
  }
}

/** Materials for one mesh: one per sub-mesh when its diffuse resolved, the shared plain material otherwise. */
export interface MeshMaterials {
  /** Per geometry group (sub-mesh index). */
  textured: THREE.Material[];
  dispose(): void;
}

/**
 * Builds the textured material list for a mesh's geometry groups. Sub-meshes whose texture is missing (or still
 * loading) use `plain`; the promise resolves once every available texture has been applied.
 */
export function buildMaterials(
  loaded: LoadedMesh,
  plain: THREE.Material,
  textures: TextureCache,
  options: { doubleSide: boolean; paletteTint?: THREE.ColorRepresentation },
  signal?: AbortSignal,
): { materials: MeshMaterials; ready: Promise<void> } {
  const { meta } = loaded;
  const byName = new Map(meta.textures.map((t) => [t.name.toLowerCase(), t]));
  const owned: THREE.Material[] = [];
  const list: THREE.Material[] = meta.subMeshes.map(() => plain);
  const perTexture = new Map<string, THREE.MeshStandardMaterial>();
  const pending: Promise<void>[] = [];
  meta.subMeshes.forEach((sub, index) => {
    const info = sub.diffuse ? byName.get(sub.diffuse.toLowerCase()) : undefined;
    if (!info) return;
    // The shader decides whether alpha is coverage: the plain "ped" shader keeps a specular mask in alpha (0.4 all over
    // a bare-arms body texture), so cutting on it would hide the torso. Unknown shaders fall back to the texture format.
    const cutout = sub.cutout ?? (info.alpha && !meta.skinned);
    const key = `${info.name.toLowerCase()}|${cutout ? "cut" : "opaque"}`;
    let material = perTexture.get(key);
    if (!material) {
      material = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.85, metalness: 0.0, side: options.doubleSide ? THREE.DoubleSide : THREE.FrontSide });
      // Palette shaders store intensity; the in-game colour comes from a palette the preview does not have, so a flat tint stands in.
      if (info.palette) material.color.set(options.paletteTint ?? 0x8a8a8a);
      if (cutout && info.alpha) {
        material.alphaTest = 0.5;
        material.side = THREE.DoubleSide;
      }
      perTexture.set(key, material);
      owned.push(material);
      const m = material;
      pending.push(
        textures.load(meta.textureScope, info, signal).then((texture) => {
          if (texture) {
            m.map = texture;
            m.needsUpdate = true;
            return;
          }
          // Unavailable: put the plain material back on every group that waited for this texture (the array is
          // shared with the mesh, so the change shows on the next frame).
          for (let i = 0; i < list.length; i++) if (list[i] === m) list[i] = plain;
        }),
      );
    }
    list[index] = material;
  });
  const materials: MeshMaterials = {
    textured: list,
    dispose() {
      for (const m of owned) m.dispose();
    },
  };
  return { materials, ready: Promise.all(pending).then(() => undefined) };
}
