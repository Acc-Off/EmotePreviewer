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

/** Filters of the lower list (the emote layered as the secondary); the same selects as the upper list, independent of it. */
export interface SecondaryFilters {
  text: string;
  source: string; // "" = all
  category: string; // "" = all
  kind: EmoteKind | "";
}

export const defaultSecondaryFilters: SecondaryFilters = { text: "", source: "", category: "", kind: "" };

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
  /** The main selection (the upper list). Plays in the slot its flag says: whole body, or upper body over the idle. */
  selectedId: string | null;
  /** The emote layered on top as the secondary (the lower list, flag SECONDARY only); only while `layering` is on. */
  secondaryId: string | null;
  /**
   * Whether the lower list (layering) is open. While open the upper list holds primaries and the lower one
   * secondaries, so only pairs the game can play are selectable. Closing it drops the secondary.
   */
  layering: boolean;
  manualClip: ManualClip | null;
  filters: Filters;
  secondaryFilters: SecondaryFilters;
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
  setSecondary(id: string | null): void;
  setLayering(on: boolean): void;
  setManualClip(clip: ManualClip | null): void;
  setFilters(patch: Partial<Filters>): void;
  setSecondaryFilters(patch: Partial<SecondaryFilters>): void;
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
  secondaryId: null,
  layering: false,
  manualClip: null,
  filters: defaultFilters,
  secondaryFilters: defaultSecondaryFilters,
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

  /** The primary changes; the secondary stays (the game keeps a secondary running across primary changes too). */
  select: (id) => {
    if (get().selectedId === id) return;
    set({ selectedId: id, manualClip: null });
  },
  setSecondary: (id) => {
    if (get().secondaryId === id) return;
    set({ secondaryId: id });
  },
  /** Opening the lower list moves a selected secondary-flag emote down into it (the upper list only shows primaries then). */
  setLayering: (layering) => {
    const state = get();
    if (!layering) {
      set({ layering, secondaryId: null });
      return;
    }
    const selected = selectEntry(state);
    if (selected && selected.slot === "secondary" && !state.manualClip) set({ layering, selectedId: null, secondaryId: selected.id });
    else set({ layering });
  },
  setManualClip: (manualClip) => set({ manualClip }),
  setFilters: (patch) => set({ filters: { ...get().filters, ...patch } }),
  setSecondaryFilters: (patch) => set({ secondaryFilters: { ...get().secondaryFilters, ...patch } }),
}));

/**
 * Applies the filters; `text` is split into words that must all match the haystack. With `primaryOnly` (the lower
 * list is open) emotes the game would put in the secondary slot are left out — they belong to the lower list.
 */
export function filterEntries(entries: EmoteRow[], f: Filters, primaryOnly = false): EmoteRow[] {
  const words = f.text.toLowerCase().split(/\s+/).filter(Boolean);
  return entries.filter((e) => {
    if (primaryOnly && e.slot === "secondary") return false;
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

/**
 * Candidates for the lower list: the emotes whose flag puts them in the game's secondary slot, narrowed by the lower
 * list's own filters (rows that cannot play are dimmed, as above), and silently to the primary's kind of ped — a
 * dog's emotes only under a dog emote of the same breed, human emotes under a human one (or nothing selected).
 */
export function filterSecondaries(entries: EmoteRow[], f: SecondaryFilters, ped: string | null): EmoteRow[] {
  const words = f.text.toLowerCase().split(/\s+/).filter(Boolean);
  const wantedPed = ped?.toLowerCase() ?? null;
  return entries.filter((e) => {
    if (e.slot !== "secondary") return false;
    if ((e.ped?.toLowerCase() ?? null) !== wantedPed) return false;
    if (f.source && e.source !== f.source) return false;
    if (f.category && e.category !== f.category) return false;
    if (f.kind && e.kind !== f.kind) return false;
    for (const w of words) if (!e.search.includes(w)) return false;
    return true;
  });
}

export function selectEntry(state: AppStore): EmoteRow | null {
  const id = state.selectedId;
  return id ? (state.entries.find((e) => e.id === id) ?? null) : null;
}

/** The layered secondary, or null while the lower list is closed. */
export function selectSecondary(state: AppStore): EmoteRow | null {
  const id = state.layering ? state.secondaryId : null;
  return id ? (state.entries.find((e) => e.id === id) ?? null) : null;
}
