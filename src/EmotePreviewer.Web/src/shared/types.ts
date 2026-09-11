/** Mirrors the DTOs served by EmotePreviewer.App (camelCase JSON). Keep in step with src/EmotePreviewer.App/Web/Dtos.cs. */

export type GtaState = "missingGta" | "missingKeys" | "indexing" | "ready" | "error";

export interface StatusDto {
  version: string;
  gta: GtaState;
  message: string | null;
  progressDone: number;
  progressTotal: number;
  gtaFolder: string | null;
  keysFolder: string | null;
  gtaDetected: boolean;
  clipDictionaries: number;
  catalogEntries: number;
  skeletonBones: number | null;
  /** Name of the configured ped's skeleton when it is available (cached or loaded); null otherwise. */
  skeleton: string | null;
}

export type EmoteKind = "animation" | "scenario" | "walk" | "expression";
/**
 * The game's two animation slots. A primary emote (flag 0 / 1) poses the whole body and replaces the previous primary;
 * a secondary one (flags with bit 32, e.g. MOVING = 51 / STUCK = 50) plays on top of it, upper body only when bit 16 is set.
 */
export type EmoteSlot = "primary" | "secondary";
export type PreviewReason = "not-indexed" | "kind" | "animal" | "no-dictionary" | "no-clip";

export interface PropDto {
  model: string;
  bone: number;
  placement: number[];
  /** true = a mesh can be served, false = not in the game data nor shipped, null = unknown until indexed. */
  available: boolean | null;
}

/**
 * Where the second ped of a shared emote goes, relative to the main (selected) ped. `offset`: the partner's entity at
 * `position` (GTA frame of the main ped: x right, y forward, z up) turned by `heading` degrees. `attach`: one entity
 * hangs on a bone of the other (`attached` says which one hangs) with a prop-style offset.
 */
export interface PartnerPlacementDto {
  kind: "offset" | "attach";
  position: number[] | null;
  heading: number | null;
  attached: "main" | "partner" | null;
  bone: number | null;
  offset: number[] | null;
  /** False when neither side gives a placement and the default (facing each other 1 m apart) is used. */
  explicit: boolean;
}

/** The other half of a shared emote. */
export interface PartnerDto {
  id: string;
  command: string;
  label: string;
  dictionary: string | null;
  clip: string | null;
  loop: boolean;
  durationMs: number | null;
  startDelayMs: number;
  props: PropDto[];
  previewable: boolean;
  previewReason: PreviewReason | null;
  /** The animal ped the partner clip is meant for; null for human partners. */
  ped: string | null;
  placement: PartnerPlacementDto;
}

export interface EmoteDto {
  id: string;
  source: string;
  category: string;
  command: string;
  label: string;
  kind: EmoteKind;
  dictionary: string | null;
  clip: string | null;
  name: string | null;
  loop: boolean;
  move: boolean;
  /** The animation flag the menu passes to the game (LOOP = 1, STUCK = 50, MOVING = 51 …; 0 when none). */
  flag: number;
  /** Slot the flag puts the emote in (see `EmoteSlot`). */
  slot: EmoteSlot;
  /** UPPERBODY bit: as a secondary only the spine, arms and head come from this clip. */
  upperBody: boolean;
  durationMs: number | null;
  exitEmote: string | null;
  props: PropDto[];
  custom: boolean;
  previewable: boolean;
  previewReason: PreviewReason | null;
  /** The animal ped the clip is meant for; null for human emotes (played on the configured ped). */
  ped: string | null;
  /** Milliseconds this side starts after the other. */
  startDelayMs: number;
  /** The other side's command for shared emotes (kept even when it did not resolve); null for solo emotes. */
  partnerCommand: string | null;
  /** The resolved partner; null for solo emotes and unresolved partner commands. */
  partner: PartnerDto | null;
}

export interface CatalogSourceDto {
  id: string;
  path: string;
  origin: "folder" | "github";
  ref: string | null;
  kind: "rpemotes" | "scully" | "unknown";
  entries: number;
}

export interface CatalogDto {
  entries: EmoteDto[];
  sources: CatalogSourceDto[];
  warnings: string[];
  previewResolved: boolean;
  revision: number;
}

export interface CatalogEvent {
  revision: number;
  entries: number;
  previewResolved: boolean;
}

export interface ResourceSetting {
  id: string;
  path: string;
  origin: "folder" | "github";
  ref?: string | null;
  repository?: string | null;
  /** Disabled resources stay configured but are left out of the catalog. */
  enabled?: boolean;
}

export interface ViewerSettings {
  showHelperBones: boolean;
  rootMotion: boolean;
  showProps: boolean;
  showMesh: boolean;
  /** Diffuse textures on props and the ped mesh. */
  showTextures: boolean;
  /** Draw the cloth-simulated parts of ped components (skinned to the body; the simulation itself is not reproduced). */
  showCloth: boolean;
  /** Play animal emotes on their animal ped instead of the configured one. */
  animalPeds: boolean;
  /** Show the other ped of shared emotes. */
  showPartner: boolean;
  theme: "system" | "light" | "dark";
  language: "auto" | "ja" | "en";
}

export interface AppSettings {
  gtaFolder: string | null;
  keysFolder: string | null;
  resources: ResourceSetting[];
  ped: string;
  /** Ped of the second character of shared emotes; null = the same as `ped`. */
  partnerPed: string | null;
  viewer: ViewerSettings;
  /** Watch folder resources for changed .ycd / .lua / .ydr files and rescan them automatically. */
  watchFolders?: boolean;
}

export interface DiagnosticsDto {
  version: string;
  dataDirectory: string;
  settingsPath: string;
  logPath: string;
  gtaFolder: string | null;
  gtaDetected: boolean;
  keysFolder: string | null;
  missingKeyFiles: string[];
  gta: GtaState;
  archives: number;
  clipDictionaries: number;
  indexMilliseconds: number;
  skeletonBones: number | null;
  resources: CatalogSourceDto[];
  catalogEntries: number;
  catalogWarnings: string[];
  url: string;
}

export interface SkeletonBoneDto {
  index: number;
  tag: number;
  name: string;
  parent: number;
  t: [number, number, number];
  r: [number, number, number, number];
  s: [number, number, number];
}

export interface SkeletonDto {
  name: string;
  bones: SkeletonBoneDto[];
}

export interface ClipMetaDto {
  dictionary: string;
  clip: string;
  custom: boolean;
  fps: number;
  frames: number;
  duration: number;
  boneCount: number;
  hasRootMotion: boolean;
  layoutVersion: number;
  animatedBones: number[];
  warnings: string[];
  /** Skeleton the clip was baked for. */
  skeleton: string;
  eTag: string;
}

export interface ClipInfoDto {
  name: string;
  duration: number;
  tracks: number;
}

export interface DictionaryClipsDto {
  dictionary: string;
  custom: boolean;
  clips: ClipInfoDto[];
}

export interface ResourceDetectDto {
  path: string;
  kind: "rpemotes" | "scully" | "unknown";
  suggestedId: string;
}

export interface ApiErrorBody {
  code: string;
  message: string;
}

export type ResourceJobState = "downloading" | "extracting" | "done" | "error";

export interface ResourceJobDto {
  id: string;
  state: ResourceJobState;
  bytes: number;
  total: number | null;
  message: string | null;
  updatedAt: string;
}

export interface ResourceSourceInfo {
  repository: string;
  ref: string;
  fetchedAt: string;
  eTag: string | null;
}

export interface ResourceItemDto {
  id: string;
  path: string;
  origin: "folder" | "github";
  ref: string | null;
  repository: string | null;
  enabled: boolean;
  kind: "rpemotes" | "scully" | "unknown";
  entries: number;
  exists: boolean;
  source: ResourceSourceInfo | null;
  job: ResourceJobDto | null;
}

export interface ResourceTemplateDto {
  id: string;
  repository: string;
  ref: string;
  kind: "rpemotes" | "scully";
  description: string;
  installed: boolean;
}

export interface ResourcesDto {
  resources: ResourceItemDto[];
  templates: ResourceTemplateDto[];
}

export interface MeshSubMeshDto {
  indexStart: number;
  indexCount: number;
  shaderHash: number;
  /** Diffuse texture name (see MeshMetaDto.textures for the ones that resolved); null when the shader has none. */
  diffuse: string | null;
  /** Shader name when known (`ped`, `ped_hair_spiked`, `normal_spec` …). */
  shader: string | null;
  /** Whether the shader discards by diffuse alpha; null for unknown shaders. */
  cutout: boolean | null;
  /** Geometry the game keeps out of the colour pass (hair hulls); not drawn. */
  hidden: boolean;
  /** Geometry the game shapes with its cloth simulation (story peds' jackets); skinned rigidly here, drawn only with `viewer.showCloth`. */
  cloth: boolean;
}

/** A texture fetchable as `/api/textures/{textureScope}/{name}.dds?v={eTag}`. */
export interface MeshTextureDto {
  name: string;
  format: "bc1" | "bc2" | "bc3" | "bc4" | "bc5" | "bc7" | "rgba8" | "unknown";
  width: number;
  height: number;
  alpha: boolean;
  /** `?format=rgba` / `gray` is available (BC1 / BC3 decoded on the server). */
  decodable: boolean;
  /** A palette shader uses it: fetched as `?format=gray` and tinted, since the stored colour is an intensity map. */
  palette: boolean;
  eTag: string;
}

/** Describes `/api/props/{model}.bin` and `/api/ped/{ped}/{component}.bin`: positions, normals?, uvs?, skinIndex/skinWeight?, then uint32 indices. */
export interface MeshMetaDto {
  model: string;
  custom: boolean;
  vertexCount: number;
  indexCount: number;
  hasNormals: boolean;
  hasUvs: boolean;
  skinned: boolean;
  subMeshes: MeshSubMeshDto[];
  boundsMin: number[];
  boundsMax: number[];
  warnings: string[];
  layoutVersion: number;
  eTag: string;
  /** `prop/<model>` or `ped/<ped>`. */
  textureScope: string;
  textures: MeshTextureDto[];
}

export type PedStorage = "folder" | "component";
export type PedCategory = "animal" | "ambient" | "service" | "gang" | "unique" | "cutscene" | "story" | "multiplayer" | "other";

export interface PedInfoDto {
  name: string;
  storage: PedStorage;
  category: PedCategory;
}

export interface PedListDto {
  peds: PedInfoDto[];
}

export interface PedComponentDto {
  slot: string;
  file: string;
  vertices: number;
}

/** `GET /api/peds/{ped}`: the default drawable per component slot. */
export interface PedDto {
  name: string;
  storage: PedStorage;
  category: PedCategory;
  components: PedComponentDto[];
  bones: number | null;
}
