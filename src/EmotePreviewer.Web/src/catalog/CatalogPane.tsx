import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useT } from "../shared/i18n";
import { filterEntries, useAppStore, type EmoteRow } from "../shared/store";
import type { EmoteKind } from "../shared/types";

const ROW_HEIGHT = 46;
const OVERSCAN = 8;
const KINDS: EmoteKind[] = ["animation", "scenario", "walk", "expression"];

/** Search box, filters and the virtualised list. Arrow keys move the selection; `/` focuses the search box. */
export function CatalogPane() {
  const t = useT();
  const entries = useAppStore((s) => s.entries);
  const sources = useAppStore((s) => s.sources);
  const filters = useAppStore((s) => s.filters);
  const setFilters = useAppStore((s) => s.setFilters);
  const selectedId = useAppStore((s) => s.selectedId);
  const select = useAppStore((s) => s.select);
  const catalogLoaded = useAppStore((s) => s.catalogLoaded);
  const searchRef = useRef<HTMLInputElement>(null);

  const categories = useMemo(() => {
    const set = new Set<string>();
    for (const e of entries) if (!filters.source || e.source === filters.source) set.add(e.category);
    return [...set].sort((a, b) => a.localeCompare(b));
  }, [entries, filters.source]);

  const shown = useMemo(() => filterEntries(entries, filters), [entries, filters]);

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

  return (
    <aside className="catalog">
      <div className="catalog-controls">
        <input
          ref={searchRef}
          type="search"
          className="search"
          placeholder={t("catalog.search.placeholder")}
          value={filters.text}
          onChange={(e) => setFilters({ text: e.target.value })}
          autoFocus
        />
        <div className="filters">
          <select value={filters.source} onChange={(e) => setFilters({ source: e.target.value, category: "" })} aria-label={t("catalog.filter.allSources")}>
            <option value="">{t("catalog.filter.allSources")}</option>
            {sources.map((s) => (
              <option key={s.id} value={s.id}>
                {s.id}
              </option>
            ))}
          </select>
          <select value={filters.category} onChange={(e) => setFilters({ category: e.target.value })} aria-label={t("catalog.filter.allCategories")}>
            <option value="">{t("catalog.filter.allCategories")}</option>
            {categories.map((c) => (
              <option key={c} value={c}>
                {c}
              </option>
            ))}
          </select>
          <select value={filters.kind} onChange={(e) => setFilters({ kind: e.target.value as EmoteKind | "" })} aria-label={t("catalog.filter.allKinds")}>
            <option value="">{t("catalog.filter.allKinds")}</option>
            {KINDS.map((k) => (
              <option key={k} value={k}>
                {t(`kind.${k}`)}
              </option>
            ))}
          </select>
        </div>
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
      {catalogLoaded && shown.length === 0 ? (
        <div className="catalog-empty">{t("catalog.empty")}</div>
      ) : (
        <VirtualList rows={shown} selectedId={selectedId} selectedIndex={selectedIndex} onSelect={select} />
      )}
    </aside>
  );
}

interface VirtualListProps {
  rows: EmoteRow[];
  selectedId: string | null;
  selectedIndex: number;
  onSelect: (id: string) => void;
}

/** Fixed-height rows; only the visible window (plus overscan) is in the DOM. */
function VirtualList({ rows, selectedId, selectedIndex, onSelect }: VirtualListProps) {
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
