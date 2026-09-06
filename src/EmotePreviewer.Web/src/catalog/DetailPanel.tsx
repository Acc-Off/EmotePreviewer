import { useEffect, useState } from "react";
import { api, ApiError } from "../shared/api";
import { errorText, useT } from "../shared/i18n";
import { selectEntry, useAppStore } from "../shared/store";
import type { ClipInfoDto, PartnerPlacementDto } from "../shared/types";

/** Facts about the selected entry, why it cannot be previewed, and the manual clip picker. */
export function DetailPanel() {
  const t = useT();
  const entry = useAppStore(selectEntry);
  const manualClip = useAppStore((s) => s.manualClip);
  const setManualClip = useAppStore((s) => s.setManualClip);
  const select = useAppStore((s) => s.select);
  const [clips, setClips] = useState<ClipInfoDto[] | null>(null);
  const [clipsError, setClipsError] = useState<string | null>(null);
  const [listOpen, setListOpen] = useState(false);

  const dictionary = entry?.kind === "animation" ? entry.dictionary : null;

  // The dictionary listing is fetched on demand and dropped when the selection moves to another dictionary.
  useEffect(() => {
    setClips(null);
    setClipsError(null);
    setListOpen(false);
  }, [dictionary]);

  useEffect(() => {
    if (!listOpen || !dictionary || clips) return;
    const abort = new AbortController();
    api
      .dictionaryClips(dictionary, abort.signal)
      .then((d) => setClips(d.clips))
      .catch((err: unknown) => {
        if (abort.signal.aborted) return;
        setClipsError(err instanceof ApiError ? errorText(err.code, err.message) : String(err));
      });
    return () => abort.abort();
  }, [listOpen, dictionary, clips]);

  if (!entry) return <section className="detail empty">{t("detail.empty")}</section>;

  const rows: [string, React.ReactNode][] = [
    [t("detail.command"), <span className="mono">{entry.command}</span>],
    [t("detail.label"), entry.label],
    [t("detail.source"), `${entry.source} / ${entry.category}`],
    [t("detail.kind"), t(`kind.${entry.kind}`)],
  ];
  if (entry.kind === "animation") {
    rows.push([t("detail.dictionary"), <span className="mono">{entry.dictionary}</span>]);
    rows.push([t("detail.clip"), <span className="mono">{manualClip ? `${manualClip.clip} *` : entry.clip}</span>]);
  } else {
    rows.push([t("detail.name"), <span className="mono">{entry.name}</span>]);
  }
  if (entry.durationMs != null) rows.push([t("detail.duration"), t("common.seconds", { seconds: (entry.durationMs / 1000).toFixed(1) })]);
  const flags = [entry.loop && t("detail.loop"), entry.move && t("detail.move"), entry.custom && t("detail.custom")].filter(Boolean);
  if (flags.length) rows.push([t("detail.flags"), flags.join(" · ")]);
  if (entry.exitEmote) rows.push([t("detail.exitEmote"), <span className="mono">{entry.exitEmote}</span>]);
  if (entry.props.length) {
    rows.push([
      t("detail.props"),
      <ul className="props">
        {entry.props.map((p, i) => (
          <li key={i} className="mono">
            {p.model} <span className="muted">({t("detail.propBone", { bone: p.bone })})</span>
            {p.available === false && <span className="warn"> — {t("detail.propMissing")}</span>}
          </li>
        ))}
      </ul>,
    ]);
  }
  if (entry.partnerCommand) {
    const partner = entry.partner;
    rows.push([
      t("detail.partner"),
      partner ? (
        <span>
          <button type="button" className="link mono" onClick={() => select(partner.id)}>
            {partner.command}
          </button>
          <span className="muted"> — {partner.label}</span>
          {!partner.previewable && partner.previewReason && (
            <span className="warn"> — {t("detail.partner.notPreviewable", { reason: t(`reason.${partner.previewReason}`) })}</span>
          )}
        </span>
      ) : (
        <span className="warn">{t("detail.partner.missing", { command: entry.partnerCommand })}</span>
      ),
    ]);
    if (partner) rows.push([t("detail.placement"), describePlacement(partner.placement, t)]);
    if (entry.startDelayMs > 0) rows.push([t("detail.startDelay"), t("common.seconds", { seconds: (entry.startDelayMs / 1000).toFixed(2) })]);
  }
  if (!entry.previewable && entry.previewReason) rows.push([t("detail.notPreviewable"), <span className="warn">{t(`reason.${entry.previewReason}`)}</span>]);

  return (
    <section className="detail">
      <dl>
        {rows.map(([k, v], i) => (
          <div key={i} className="detail-row">
            <dt>{k}</dt>
            <dd>{v}</dd>
          </div>
        ))}
      </dl>
      {dictionary && (
        <div className="clip-picker">
          {!listOpen ? (
            <button type="button" className="link" onClick={() => setListOpen(true)} title={t("detail.pickClip.hint")}>
              {t("detail.pickClip")}
            </button>
          ) : clipsError ? (
            <span className="warn">{t("detail.clipList.failed", { message: clipsError })}</span>
          ) : !clips ? (
            <span className="muted">{t("common.loading")}</span>
          ) : (
            <label>
              <span className="muted">{t("detail.clipList.count", { count: clips.length })}</span>
              <select
                value={manualClip?.clip ?? entry.clip ?? ""}
                onChange={(e) => {
                  const clip = e.target.value;
                  setManualClip(clip === entry.clip ? null : { dictionary, clip });
                }}
              >
                {entry.clip && !clips.some((c) => c.name === entry.clip) && <option value={entry.clip}>{entry.clip} (?)</option>}
                {clips.map((c) => (
                  <option key={c.name} value={c.name}>
                    {c.name} — {c.duration.toFixed(2)} s
                  </option>
                ))}
              </select>
            </label>
          )}
        </div>
      )}
    </section>
  );
}

/** One line about where the partner goes, in the same terms as the emote definitions. */
function describePlacement(p: PartnerPlacementDto, t: ReturnType<typeof useT>): string {
  if (p.kind === "attach") {
    const main = p.attached === "main";
    if (p.bone == null || p.bone < 0) return main ? t("detail.placement.attachMainOrigin") : t("detail.placement.attachPartnerOrigin");
    return main ? t("detail.placement.attachMain", { bone: p.bone }) : t("detail.placement.attachPartner", { bone: p.bone });
  }
  if (!p.explicit) return t("detail.placement.offsetDefault");
  const [x, y] = p.position ?? [0, 1, 0];
  return t("detail.placement.offset", { front: y.toFixed(2), side: x.toFixed(2), heading: Math.round(p.heading ?? 180) });
}
