import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useT } from "../shared/i18n";
import { filterEntries, filterSecondaries, selectEntry, selectSecondary, useAppStore, type EmoteRow } from "../shared/store";
import type { CatalogSourceDto, EmoteKind } from "../shared/types";

const ROW_HEIGHT = 46;
const OVERSCAN = 8;
const KINDS: EmoteKind[] = ["animation", "scenario", "walk", "expression"];
const SPLIT_KEY = "emotepreviewer.catalogSplit";

/**
 * The left pane: search box, filters and the virtualised list of the main selection, and below it (when "Layer" is
 * on) the list the secondary is picked from. While the lower list is open the upper one shows only primaries and the
 * lower one only secondaries, the game's two slots. Arrow keys move the main selection, `/` focuses the search box,
 * Esc clears the main selection.
 */
export function CatalogPane() {
  const t = useT();
  const entries = useAppStore((s) => s.entries);
  const sources = useAppStore((s) => s.sources);
  const filters = useAppStore((s) => s.filters);
  const setFilters = useAppStore((s) => s.setFilters);
  const selectedId = useAppStore((s) => s.selectedId);
  const select = useAppStore((s) => s.select);
  const layering = useAppStore((s) => s.layering);
  const setLayering = useAppStore((s) => s.setLayering);
  const catalogLoaded = useAppStore((s) => s.catalogLoaded);
  const searchRef = useRef<HTMLInputElement>(null);

  const shown = useMemo(() => filterEntries(entries, filters, layering), [entries, filters, layering]);

  // Keep the selection valid when the filters hide it? No: the selection stays; only the highlight disappears.
  const selectedIndex = selectedId ? shown.findIndex((e) => e.id === selectedId) : -1;

  const moveSelection = useCallback(
    (delta: number) => {
      if (shown.length === 0) return;
      const next = selectedIndex < 0 ? (delta > 0 ? 0 : shown.length - 1) : Math.max(0, Math.min(shown.length - 1, selectedIndex + delta));
      select(shown[next].id);
    },
    [shown, selectedIndex, select],
  );

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      const inField = target && (target.tagName === "INPUT" || target.tagName === "SELECT" || target.tagName === "TEXTAREA");
      if (e.key === "/" && !inField) {
        e.preventDefault();
        searchRef.current?.focus();
        searchRef.current?.select();
        return;
      }
      // Esc clears the main selection (the ped popover closes itself on Esc; leave that alone).
      if (e.key === "Escape") {
        if (document.querySelector(".ped-popover")) return;
        if (inField && (target as HTMLInputElement).value) return;
        select(null);
        return;
      }
      if (inField && target !== searchRef.current) return;
      if (target?.tagName === "INPUT" && (target as HTMLInputElement).type === "range") return;
      switch (e.key) {
        case "ArrowDown":
          e.preventDefault();
          moveSelection(1);
          break;
        case "ArrowUp":
          e.preventDefault();
          moveSelection(-1);
          break;
        case "PageDown":
          e.preventDefault();
          moveSelection(10);
          break;
        case "PageUp":
          e.preventDefault();
          moveSelection(-10);
          break;
        case "Home":
          if (!inField) {
            e.preventDefault();
            if (shown.length) select(shown[0].id);
          }
          break;
        case "End":
          if (!inField) {
            e.preventDefault();
            if (shown.length) select(shown[shown.length - 1].id);
          }
          break;
      }
    };
    addEventListener("keydown", onKey);
    return () => removeEventListener("keydown", onKey);
  }, [moveSelection, shown, select]);

  // The split between the two lists (fraction of the height the upper list gets), remembered per browser.
  const [split, setSplit] = useState(() => {
    try {
      const v = Number(localStorage.getItem(SPLIT_KEY));
      return v > 0.15 && v < 0.85 ? v : 0.55;
    } catch {
      return 0.55;
    }
  });
  const listsRef = useRef<HTMLDivElement>(null);
  const onSplitDown = (e: React.PointerEvent<HTMLDivElement>) => {
    const box = listsRef.current?.getBoundingClientRect();
    if (!box) return;
    e.preventDefault();
    const handle = e.currentTarget;
    handle.setPointerCapture(e.pointerId);
    const move = (ev: PointerEvent) => setSplit(Math.max(0.15, Math.min(0.85, (ev.clientY - box.top) / box.height)));
    const up = () => {
      handle.removeEventListener("pointermove", move);
      handle.removeEventListener("pointerup", up);
      setSplit((v) => {
        try {
          localStorage.setItem(SPLIT_KEY, String(v));
        } catch {
          // storage unavailable
        }
        return v;
      });
    };
    handle.addEventListener("pointermove", move);
    handle.addEventListener("pointerup", up);
  };

  return (
    <aside className="catalog">
      <div className="catalog-controls">
        <div className="search-row">
          <input
            ref={searchRef}
            type="search"
            className="search"
            placeholder={t("catalog.search.placeholder")}
            value={filters.text}
            onChange={(e) => setFilters({ text: e.target.value })}
            autoFocus
          />
          <button type="button" className={layering ? "on" : ""} aria-pressed={layering} title={t("catalog.layer.title")} onClick={() => setLayering(!layering)}>
            {t("catalog.layer")}
          </button>
        </div>
        <FilterSelects entries={entries} sources={sources} value={filters} onChange={setFilters} />
        <div className="filters toggles">
          <label className="check">
            <input type="checkbox" checked={filters.previewableOnly} onChange={(e) => setFilters({ previewableOnly: e.target.checked })} />
            {t("catalog.filter.previewableOnly")}
          </label>
          <label className="check">
            <input type="checkbox" checked={filters.withProps} onChange={(e) => setFilters({ withProps: e.target.checked })} />
            {t("catalog.filter.withProps")}
          </label>
          <label className="check">
            <input type="checkbox" checked={filters.customOnly} onChange={(e) => setFilters({ customOnly: e.target.checked })} />
            {t("catalog.filter.customOnly")}
          </label>
          <span className="count mono">{t("catalog.count", { shown: shown.length, total: entries.length })}</span>
        </div>
      </div>
      <div className="catalog-lists" ref={listsRef}>
        <div className="catalog-pane" style={{ flexBasis: layering ? `${split * 100}%` : "100%" }}>
          {catalogLoaded && shown.length === 0 ? (
            <div className="catalog-empty">{t("catalog.empty")}</div>
          ) : (
            <VirtualList rows={shown} selectedId={selectedId} selectedIndex={selectedIndex} onSelect={(id) => select(id === selectedId ? null : id)} slotBadge={!layering} />
          )}
        </div>
        {layering && (
          <>
            <div className="catalog-split" role="separator" aria-orientation="horizontal" onPointerDown={onSplitDown} />
            <SecondaryPane />
          </>
        )}
      </div>
    </aside>
  );
}

interface FilterSelectsProps {
  entries: EmoteRow[];
  sources: CatalogSourceDto[];
  value: { source: string; category: string; kind: EmoteKind | "" };
  onChange: (patch: { source?: string; category?: string; kind?: EmoteKind | "" }) => void;
}

/** Source / category / kind selects; the category list follows the chosen source. Used by both lists. */
function FilterSelects({ entries, sources, value, onChange }: FilterSelectsProps) {
  const t = useT();
  const categories = useMemo(() => {
    const set = new Set<string>();
    for (const e of entries) if (!value.source || e.source === value.source) set.add(e.category);
    return [...set].sort((a, b) => a.localeCompare(b));
  }, [entries, value.source]);
  return (
    <div className="filters">
      <select value={value.source} onChange={(e) => onChange({ source: e.target.value, category: "" })} aria-label={t("catalog.filter.allSources")}>
        <option value="">{t("catalog.filter.allSources")}</option>
        {sources.map((s) => (
          <option key={s.id} value={s.id}>
            {s.id}
          </option>
        ))}
      </select>
      <select value={value.category} onChange={(e) => onChange({ category: e.target.value })} aria-label={t("catalog.filter.allCategories")}>
        <option value="">{t("catalog.filter.allCategories")}</option>
        {categories.map((c) => (
          <option key={c} value={c}>
            {c}
          </option>
        ))}
      </select>
      <select value={value.kind} onChange={(e) => onChange({ kind: e.target.value as EmoteKind | "" })} aria-label={t("catalog.filter.allKinds")}>
        <option value="">{t("catalog.filter.allKinds")}</option>
        {KINDS.map((k) => (
          <option key={k} value={k}>
            {t(`kind.${k}`)}
          </option>
        ))}
      </select>
    </div>
  );
}

/**
 * The lower list: the emotes the game plays in its secondary slot (flag SECONDARY: upper body over the primary, or
 * over the idle when there is none). Its search box and selects are its own; the upper list's filters never narrow it.
 */
function SecondaryPane() {
  const t = useT();
  const entries = useAppStore((s) => s.entries);
  const sources = useAppStore((s) => s.sources);
  const filters = useAppStore((s) => s.secondaryFilters);
  const setFilters = useAppStore((s) => s.setSecondaryFilters);
  const secondary = useAppStore(selectSecondary);
  const setSecondary = useAppStore((s) => s.setSecondary);
  const mainPed = useAppStore(selectEntry)?.ped ?? null;
  const shown = useMemo(() => filterSecondaries(entries, filters, mainPed), [entries, filters, mainPed]);
  const selectedIndex = secondary ? shown.findIndex((e) => e.id === secondary.id) : -1;

  return (
    <div className="catalog-pane secondary">
      <div className="secondary-head">
        <span className="secondary-title" title={t("catalog.secondary.title.hint")}>
          <span className="tag slot secondary">{t("slot.secondary.short")}</span>
          {t("catalog.secondary.title")}
        </span>
        <span className="count mono">{t("catalog.count", { shown: shown.length, total: entries.length })}</span>
        <button type="button" className="link" disabled={!secondary} onClick={() => setSecondary(null)}>
          {t("catalog.secondary.clear")}
        </button>
      </div>
      <div className="secondary-filters">
        <input type="search" className="search" placeholder={t("catalog.secondary.placeholder")} value={filters.text} onChange={(e) => setFilters({ text: e.target.value })} />
        <FilterSelects entries={entries} sources={sources} value={filters} onChange={setFilters} />
      </div>
      {secondary && selectedIndex < 0 && (
        <div className="secondary-current mono" title={secondary.id}>
          {secondary.command}
        </div>
      )}
      {shown.length === 0 ? (
        <div className="catalog-empty">{t("catalog.secondary.empty")}</div>
      ) : (
        <VirtualList rows={shown} selectedId={secondary?.id ?? null} selectedIndex={selectedIndex} onSelect={(id) => setSecondary(id === secondary?.id ? null : id)} slotBadge={false} />
      )}
    </div>
  );
}

interface VirtualListProps {
  rows: EmoteRow[];
  selectedId: string | null;
  selectedIndex: number;
  onSelect: (id: string) => void;
  /** Mark the rows the game plays as upper-body-only secondaries (off while the lists are split by slot anyway). */
  slotBadge: boolean;
}

/** Fixed-height rows; only the visible window (plus overscan) is in the DOM. */
function VirtualList({ rows, selectedId, selectedIndex, onSelect, slotBadge }: VirtualListProps) {
  const t = useT();
  const ref = useRef<HTMLDivElement>(null);
  const [scrollTop, setScrollTop] = useState(0);
  const [height, setHeight] = useState(600);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setHeight(el.clientHeight));
    ro.observe(el);
    setHeight(el.clientHeight);
    return () => ro.disconnect();
  }, []);

  // Scroll the selected row into view when the selection moves (keyboard navigation).
  useEffect(() => {
    const el = ref.current;
    if (!el || selectedIndex < 0) return;
    const top = selectedIndex * ROW_HEIGHT;
    if (top < el.scrollTop) el.scrollTop = top;
    else if (top + ROW_HEIGHT > el.scrollTop + el.clientHeight) el.scrollTop = top + ROW_HEIGHT - el.clientHeight;
  }, [selectedIndex]);

  const first = Math.max(0, Math.floor(scrollTop / ROW_HEIGHT) - OVERSCAN);
  const last = Math.min(rows.length, Math.ceil((scrollTop + height) / ROW_HEIGHT) + OVERSCAN);
  const visible = rows.slice(first, last);

  return (
    <div ref={ref} className="list" role="listbox" aria-label="emotes" onScroll={(e) => setScrollTop((e.target as HTMLDivElement).scrollTop)}>
      <div style={{ height: rows.length * ROW_HEIGHT, position: "relative" }}>
        {visible.map((row, i) => {
          const index = first + i;
          const selected = row.id === selectedId;
          const secondary = slotBadge && row.kind === "animation" && row.slot === "secondary";
          return (
            <div
              key={row.id}
              role="option"
              aria-selected={selected}
              className={`row${selected ? " selected" : ""}${row.previewable ? "" : " dim"}`}
              style={{ top: index * ROW_HEIGHT, height: ROW_HEIGHT }}
              onClick={() => onSelect(row.id)}
            >
              <span className="row-main">
                <span className="row-label">{row.label || row.command}</span>
                <span className="row-tags">
                  {secondary && (
                    <span className="tag slot secondary" title={t("slot.secondary.title")}>
                      {t("slot.secondary.short")}
                    </span>
                  )}
                  {row.custom && <span className="tag">{t("catalog.tag.custom")}</span>}
                  {row.props.length > 0 && <span className="tag">{t("catalog.tag.props")}</span>}
                  {row.kind !== "animation" && <span className="tag kind">{t(`kind.${row.kind}`)}</span>}
                </span>
              </span>
              <span className="row-sub mono">
                {row.command} · {row.kind === "animation" ? `${row.dictionary ?? ""} / ${row.clip ?? ""}` : row.name ?? ""}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}
