import { create } from "zustand";
import { api } from "./api";
import { useI18nStore } from "./i18n";
import type { AppSettings, CatalogDto, CatalogSourceDto, EmoteDto, EmoteKind, ResourceJobDto, StatusDto } from "./types";

/** A catalog entry plus its lower-cased search haystack (computed once per catalog load). */
export interface EmoteRow extends EmoteDto {
  search: string;
}

export interface Filters {
  text: string;
  source: string; // "" = all
  category: string; // "" = all
  kind: EmoteKind | "";
  previewableOnly: boolean;
  withProps: boolean;
  customOnly: boolean;
}

export const defaultFilters: Filters = { text: "", source: "", category: "", kind: "", previewableOnly: false, withProps: false, customOnly: false };

export interface ManualClip {
  dictionary: string;
  clip: string;
}

interface AppStore {
  /** null until the event stream has connected once; false after the server went away. */
  connected: boolean | null;
  status: StatusDto | null;
  settings: AppSettings | null;
  entries: EmoteRow[];
  sources: CatalogSourceDto[];
  catalogWarnings: string[];
  catalogRevision: number;
  previewResolved: boolean;
  catalogLoaded: boolean;
  selectedId: string | null;
  manualClip: ManualClip | null;
  filters: Filters;
  /** Download / extraction progress per resource id (from the SSE `resource` event). */
  resourceJobs: Record<string, ResourceJobDto>;

  setConnected(connected: boolean): void;
  setStatus(status: StatusDto): void;
  setResourceJob(job: ResourceJobDto): void;
  loadStatus(): Promise<void>;
  loadCatalog(): Promise<void>;
  loadSettings(): Promise<void>;
  saveSettings(next: AppSettings): Promise<AppSettings>;
  select(id: string | null): void;
  setManualClip(clip: ManualClip | null): void;
  setFilters(patch: Partial<Filters>): void;
}

function toRow(e: EmoteDto): EmoteRow {
  return { ...e, search: [e.command, e.label, e.dictionary, e.clip, e.name, e.category].filter(Boolean).join(" ").toLowerCase() };
}

export function applyTheme(theme: AppSettings["viewer"]["theme"] | undefined): void {
  const root = document.documentElement;
  if (theme === "light" || theme === "dark") root.dataset.theme = theme;
  else delete root.dataset.theme;
}

let catalogRequest: Promise<void> | null = null;

export const useAppStore = create<AppStore>((set, get) => ({
  connected: null,
  status: null,
  settings: null,
  entries: [],
  sources: [],
  catalogWarnings: [],
  catalogRevision: -1,
  previewResolved: false,
  catalogLoaded: false,
  selectedId: null,
  manualClip: null,
  filters: defaultFilters,
  resourceJobs: {},

  setConnected: (connected) => set({ connected }),
  setStatus: (status) => set({ status }),
  setResourceJob: (job) => set({ resourceJobs: { ...get().resourceJobs, [job.id]: job } }),

  async loadStatus() {
    set({ status: await api.status() });
  },

  /** Coalesces concurrent calls (a burst of `catalog` events only costs one fetch). */
  loadCatalog() {
    if (catalogRequest) return catalogRequest;
    catalogRequest = api
      .catalog()
      .then((catalog: CatalogDto) => {
        set({
          entries: catalog.entries.map(toRow),
          sources: catalog.sources,
          catalogWarnings: catalog.warnings,
          catalogRevision: catalog.revision,
          previewResolved: catalog.previewResolved,
          catalogLoaded: true,
        });
      })
      .finally(() => {
        catalogRequest = null;
      });
    return catalogRequest;
  },

  async loadSettings() {
    const settings = await api.settings();
    applyTheme(settings.viewer.theme);
    useI18nStore.getState().setLanguage(settings.viewer.language);
    set({ settings });
  },

  async saveSettings(next) {
    const saved = await api.saveSettings(next);
    applyTheme(saved.viewer.theme);
    useI18nStore.getState().setLanguage(saved.viewer.language);
    set({ settings: saved });
    return saved;
  },

  select: (id) => {
    if (get().selectedId === id) return;
    set({ selectedId: id, manualClip: null });
  },
  setManualClip: (manualClip) => set({ manualClip }),
  setFilters: (patch) => set({ filters: { ...get().filters, ...patch } }),
}));

/** Applies the filters; `text` is split into words that must all match the haystack. */
export function filterEntries(entries: EmoteRow[], f: Filters): EmoteRow[] {
  const words = f.text.toLowerCase().split(/\s+/).filter(Boolean);
  return entries.filter((e) => {
    if (f.source && e.source !== f.source) return false;
    if (f.category && e.category !== f.category) return false;
    if (f.kind && e.kind !== f.kind) return false;
    if (f.previewableOnly && !e.previewable) return false;
    if (f.withProps && e.props.length === 0) return false;
    if (f.customOnly && !e.custom) return false;
    for (const w of words) if (!e.search.includes(w)) return false;
    return true;
  });
}

export function selectEntry(state: AppStore): EmoteRow | null {
  const id = state.selectedId;
  return id ? (state.entries.find((e) => e.id === id) ?? null) : null;
}
