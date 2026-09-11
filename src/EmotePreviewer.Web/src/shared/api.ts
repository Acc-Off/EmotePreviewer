import type {
  ApiErrorBody,
  AppSettings,
  CatalogDto,
  CatalogEvent,
  ClipMetaDto,
  DiagnosticsDto,
  DictionaryClipsDto,
  MeshMetaDto,
  PedDto,
  PedListDto,
  ResourceDetectDto,
  ResourceJobDto,
  ResourcesDto,
  SkeletonDto,
  StatusDto,
} from "./types";

/** An HTTP error carrying the server's `{ error: { code, message } }` envelope when there was one. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
  ) {
    super(message);
  }
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { cache: "no-store", ...init });
  if (!response.ok) throw await toApiError(response);
  return (await response.json()) as T;
}

async function toApiError(response: Response): Promise<ApiError> {
  let body: { error?: ApiErrorBody } | null = null;
  try {
    body = (await response.json()) as { error?: ApiErrorBody };
  } catch {
    // not JSON
  }
  return new ApiError(response.status, body?.error?.code ?? `HTTP_${response.status}`, body?.error?.message ?? `HTTP ${response.status}`);
}

function json(method: string, body: unknown): RequestInit {
  return { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) };
}

export const api = {
  status: () => request<StatusDto>("/api/status"),
  catalog: () => request<CatalogDto>("/api/catalog"),
  settings: () => request<AppSettings>("/api/settings"),
  saveSettings: (settings: AppSettings) => request<AppSettings>("/api/settings", json("PUT", settings)),
  diagnostics: () => request<DiagnosticsDto>("/api/diagnostics"),
  detectResource: (path: string) => request<ResourceDetectDto>(`/api/resources/detect?path=${encodeURIComponent(path)}`),
  resources: () => request<ResourcesDto>("/api/resources"),
  addFolderResource: (path: string) => request<unknown>("/api/resources", json("POST", { origin: "folder", path })),
  addGitHubResource: (repository: string, ref: string | null, id: string | null) =>
    request<{ id: string }>("/api/resources", json("POST", { origin: "github", repository, ref, id })),
  refreshResource: (id: string) => request<{ id: string }>(`/api/resources/${encodeURIComponent(id)}/refresh`, { method: "POST" }),
  rescanResource: (id: string) => request<{ id: string; folder: string }>(`/api/resources/${encodeURIComponent(id)}/rescan`, { method: "POST" }),
  removeResource: (id: string) => request<{ id: string }>(`/api/resources/${encodeURIComponent(id)}`, { method: "DELETE" }),
  setResourceEnabled: (id: string, enabled: boolean) =>
    request<{ id: string; enabled: boolean }>(`/api/resources/${encodeURIComponent(id)}/enabled`, json("POST", { enabled })),
  pickFolder: (initial: string | null, title: string) =>
    request<{ path: string | null }>("/api/dialogs/folder", json("POST", { initial, title })).then((r) => r.path),
  quit: () => request<{ ok: boolean }>("/api/quit", { method: "POST" }),
  notices: async () => {
    const response = await fetch("/api/notices", { cache: "no-store" });
    if (!response.ok) throw await toApiError(response);
    return response.text();
  },
  /** Bind pose of a ped (the configured one when omitted). */
  skeleton: (ped: string | null, signal?: AbortSignal) => request<SkeletonDto>(`/api/skeleton${pedQuery(ped)}`, { signal }),
  peds: (signal?: AbortSignal) => request<PedListDto>("/api/peds", { signal }),
  ped: (ped: string, signal?: AbortSignal) => request<PedDto>(`/api/peds/${encodeURIComponent(ped)}`, { signal }),
  dictionaryClips: (dictionary: string, signal?: AbortSignal) =>
    request<DictionaryClipsDto>(`/api/dictionaries/${encodeURIComponent(dictionary)}/clips`, { signal }),

  /** Meta and frame data of an emote's clip baked for `ped`, fetched in parallel. The `.bin` is cacheable (ETag) so re-selection is cheap. */
  emoteClip: (id: string, ped: string | null, signal?: AbortSignal) => loadClip(`/api/emotes/${encodeURIComponent(id)}/clip`, pedQuery(ped), signal),
  /** Same for an arbitrary clip of a dictionary (manual selection). */
  dictionaryClip: (dictionary: string, clip: string, ped: string | null, signal?: AbortSignal) =>
    loadClip(`/api/clips/${encodeURIComponent(dictionary)}/${encodeURIComponent(clip)}`, pedQuery(ped), signal),
  /** A clip of a movement clip set (`move_m@generic` / `idle`), resolved through the game's clip set table. */
  clipSetClip: (set: string, clip: string, ped: string | null, signal?: AbortSignal) =>
    loadClip(`/api/clipsets/${encodeURIComponent(set)}/${encodeURIComponent(clip)}`, pedQuery(ped), signal),
  /** Prop mesh (meta + vertex data) for a model name. */
  propMesh: (model: string, signal?: AbortSignal) => loadMesh(`/api/props/${encodeURIComponent(model)}`, signal),
  /** Skinned component of a ped (a slot such as uppr, or an explicit file name such as uppr_003_r). */
  pedComponent: (ped: string, component: string, signal?: AbortSignal) =>
    loadMesh(`/api/ped/${encodeURIComponent(ped)}/${encodeURIComponent(component)}`, signal),
};

function pedQuery(ped: string | null): string {
  return ped ? `?ped=${encodeURIComponent(ped)}` : "";
}

export interface LoadedMesh {
  meta: MeshMetaDto;
  data: ArrayBuffer;
}

async function loadMesh(base: string, signal?: AbortSignal): Promise<LoadedMesh> {
  const meta = await request<MeshMetaDto>(base, { signal });
  const data = await fetchBinary(versioned(base, meta.eTag), signal);
  const expected =
    meta.vertexCount * 12 + (meta.hasNormals ? meta.vertexCount * 12 : 0) + (meta.hasUvs ? meta.vertexCount * 8 : 0) + (meta.skinned ? meta.vertexCount * 24 : 0) + meta.indexCount * 4;
  if (data.byteLength !== expected) throw new ApiError(500, "MESH_LAYOUT", `mesh .bin has ${data.byteLength} bytes, expected ${expected}`);
  return { meta, data };
}

export interface LoadedClip {
  meta: ClipMetaDto;
  data: Float32Array;
}

/**
 * The `.bin` responses are cacheable for an hour, so their URL carries the server's ETag: a different skeleton or a
 * refreshed resource changes the tag and therefore the URL, while an unchanged clip is served from the browser cache.
 */
function versioned(base: string, eTag: string, query = ""): string {
  return `${base}.bin?v=${encodeURIComponent(eTag.replace(/"/g, ""))}${query ? "&" + query.slice(1) : ""}`;
}

async function fetchBinary(url: string, signal?: AbortSignal): Promise<ArrayBuffer> {
  const r = await fetch(url, { signal });
  if (!r.ok) throw await toApiError(r);
  return r.arrayBuffer();
}

async function loadClip(base: string, query: string, signal?: AbortSignal): Promise<LoadedClip> {
  const meta = await request<ClipMetaDto>(base + query, { signal });
  const bin = await fetchBinary(versioned(base, meta.eTag, query), signal);
  const expected = meta.frames * meta.boneCount * 7 + (meta.hasRootMotion ? meta.frames * 7 : 0);
  const data = new Float32Array(bin);
  if (data.length !== expected) throw new ApiError(500, "CLIP_LAYOUT", `clip.bin has ${data.length} floats, expected ${expected}`);
  return { meta, data };
}

export interface EventHandlers {
  status?: (status: StatusDto) => void;
  catalog?: (event: CatalogEvent) => void;
  resource?: (job: ResourceJobDto) => void;
  /** Called with `true` once connected and `false` when the connection drops (the server quit or is restarting). */
  connection?: (connected: boolean) => void;
}

/**
 * Subscribes to `/api/events`. `EventSource` reconnects on its own; the disconnect callback fires after the first
 * failed reconnect so a short hiccup does not flash the "server stopped" overlay.
 */
export function subscribeEvents(handlers: EventHandlers): () => void {
  const source = new EventSource("/api/events");
  let connected = false;
  let failures = 0;
  source.addEventListener("open", () => {
    failures = 0;
    if (!connected) {
      connected = true;
      handlers.connection?.(true);
    }
  });
  source.addEventListener("error", () => {
    failures++;
    if (connected && failures >= 2) {
      connected = false;
      handlers.connection?.(false);
    }
  });
  source.addEventListener("status", (e) => handlers.status?.(JSON.parse((e as MessageEvent).data) as StatusDto));
  source.addEventListener("catalog", (e) => handlers.catalog?.(JSON.parse((e as MessageEvent).data) as CatalogEvent));
  source.addEventListener("resource", (e) => handlers.resource?.(JSON.parse((e as MessageEvent).data) as ResourceJobDto));
  return () => source.close();
}
