import { useEffect, useRef, useState } from "react";
import { create } from "zustand";
import { api } from "./api";
import { useT, type MessageKey } from "./i18n";
import type { PedCategory, PedInfoDto } from "./types";

export const PED_CATEGORIES: PedCategory[] = ["multiplayer", "ambient", "service", "gang", "unique", "story", "cutscene", "animal", "other"];
const MAX_ROWS = 300;
const FILTER_KEY = "emotePreviewer.pedFilter";

interface PedFilterState {
  text: string;
  category: PedCategory | "";
  /** Scroll offset of the list, kept so reopening the popover lands where the user was. */
  scroll: number;
  set(patch: Partial<Pick<PedFilterState, "text" | "category" | "scroll">>): void;
  /** Scroll offsets change constantly, so they are kept in memory only. */
  setScroll(scroll: number): void;
}

function loadFilter(): { text: string; category: PedCategory | "" } {
  try {
    const raw = localStorage.getItem(FILTER_KEY);
    if (raw) {
      const parsed = JSON.parse(raw) as { text?: string; category?: string };
      return { text: parsed.text ?? "", category: (PED_CATEGORIES as string[]).includes(parsed.category ?? "") ? (parsed.category as PedCategory) : "" };
    }
  } catch {
    // no storage
  }
  return { text: "", category: "" };
}

/**
 * Filter state shared by every picker (settings page, viewer popover) and kept across open / close and reloads:
 * trying many peds means reopening the picker over and over, so the search must not reset each time.
 */
export const usePedFilter = create<PedFilterState>((set) => ({
  ...loadFilter(),
  scroll: 0,
  set: (patch) =>
    set((state) => {
      const next = { ...state, ...patch };
      try {
        localStorage.setItem(FILTER_KEY, JSON.stringify({ text: next.text, category: next.category }));
      } catch {
        // no storage
      }
      return next;
    }),
  setScroll: (scroll) => set({ scroll }),
}));

let pedListCache: PedInfoDto[] | null = null;

/** Loads the ped list once per page (it only changes when the game data is re-indexed). */
export function usePedList(gtaReady: boolean): PedInfoDto[] | null {
  const [peds, setPeds] = useState<PedInfoDto[] | null>(pedListCache);
  useEffect(() => {
    if (!gtaReady) {
      pedListCache = null;
      setPeds(null);
      return;
    }
    if (pedListCache) {
      setPeds(pedListCache);
      return;
    }
    const abort = new AbortController();
    api
      .peds(abort.signal)
      .then((list) => {
        if (abort.signal.aborted) return;
        pedListCache = list.peds;
        setPeds(list.peds);
      })
      .catch(() => {
        if (!abort.signal.aborted) setPeds(null);
      });
    return () => abort.abort();
  }, [gtaReady]);
  return peds;
}

interface PedPickerProps {
  peds: PedInfoDto[];
  selected: string;
  onSelect: (ped: string) => void;
  /** Focus the search box when shown (popover use). */
  autoFocus?: boolean;
}

/** Search box + category filter + list of peds. Shared by the settings page and the viewer popover. */
export function PedPicker({ peds, selected, onSelect, autoFocus }: PedPickerProps) {
  const t = useT();
  const text = usePedFilter((s) => s.text);
  const category = usePedFilter((s) => s.category);
  const setFilter = usePedFilter((s) => s.set);
  const setScroll = usePedFilter((s) => s.setScroll);
  const listRef = useRef<HTMLUListElement>(null);
  const words = text.toLowerCase().split(/\s+/).filter(Boolean);
  const filtered = peds.filter((p) => (!category || p.category === category) && words.every((w) => p.name.includes(w)));
  const shown = filtered.slice(0, MAX_ROWS);

  // Restore the scroll offset on mount (or bring the selected row into view when nothing was scrolled yet).
  useEffect(() => {
    const list = listRef.current;
    if (!list) return;
    const saved = usePedFilter.getState().scroll;
    if (saved > 0) list.scrollTop = saved;
    else list.querySelector<HTMLElement>("li.selected")?.scrollIntoView({ block: "center" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div className="ped-picker">
      <div className="ped-filters">
        <input
          type="search"
          value={text}
          placeholder={t("settings.ped.search")}
          onChange={(e) => setFilter({ text: e.target.value, scroll: 0 })}
          aria-label={t("settings.ped.search")}
          autoFocus={autoFocus}
          onFocus={(e) => e.target.select()}
          onKeyDown={(e) => {
            if (e.key === "Enter" && shown.length > 0) onSelect(shown[0].name);
          }}
        />
        <select value={category} onChange={(e) => setFilter({ category: e.target.value as PedCategory | "", scroll: 0 })} aria-label={t("settings.ped.category")}>
          <option value="">{t("settings.ped.category.all")}</option>
          {PED_CATEGORIES.map((c) => (
            <option key={c} value={c}>
              {t(`settings.ped.category.${c}` as MessageKey)}
            </option>
          ))}
        </select>
        <span className="muted">{t("settings.ped.count", { shown: shown.length, total: peds.length })}</span>
      </div>
      <ul className="ped-list" role="listbox" aria-label={t("settings.ped.title")} ref={listRef} onScroll={(e) => setScroll(e.currentTarget.scrollTop)}>
        {shown.map((p) => (
          <li key={p.name} role="option" aria-selected={p.name === selected} className={p.name === selected ? "selected" : ""} onClick={() => onSelect(p.name)}>
            <span className="mono">{p.name}</span>
            <span className="muted">{t(`settings.ped.category.${p.category}` as MessageKey)}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
