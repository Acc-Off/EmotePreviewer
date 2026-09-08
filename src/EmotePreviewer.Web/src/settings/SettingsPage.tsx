import { useCallback, useEffect, useState } from "react";
import { api, ApiError } from "../shared/api";
import { errorText, useT, type MessageKey } from "../shared/i18n";
import { KEY_TOOL_URL } from "../shared/links";
import { useAppStore } from "../shared/store";
import { PedPicker, usePedList } from "../shared/PedPicker";
import type { AppSettings, DiagnosticsDto, ResourceJobDto, ResourcesDto } from "../shared/types";

interface SettingsPageProps {
  onBack: () => void;
}

export function SettingsPage({ onBack }: SettingsPageProps) {
  const t = useT();
  const settings = useAppStore((s) => s.settings);
  const saveSettings = useAppStore((s) => s.saveSettings);
  const status = useAppStore((s) => s.status);
  const sources = useAppStore((s) => s.sources);
  const [diagnostics, setDiagnostics] = useState<DiagnosticsDto | null>(null);
  const [toast, setToast] = useState<{ kind: "ok" | "error"; text: string } | null>(null);

  const refreshDiagnostics = useCallback(() => {
    api.diagnostics().then(setDiagnostics).catch(() => setDiagnostics(null));
  }, []);

  useEffect(() => {
    refreshDiagnostics();
  }, [refreshDiagnostics, status?.gta, sources]);

  useEffect(() => {
    if (!toast) return;
    const id = setTimeout(() => setToast(null), toast.kind === "ok" ? 1800 : 6000);
    return () => clearTimeout(id);
  }, [toast]);

  const save = useCallback(
    async (next: AppSettings) => {
      try {
        await saveSettings(next);
        setToast({ kind: "ok", text: t("settings.saved") });
        refreshDiagnostics();
        return true;
      } catch (err) {
        const message = err instanceof ApiError ? errorText(err.code, err.message) : String(err);
        setToast({ kind: "error", text: t("settings.saveFailed", { message }) });
        return false;
      }
    },
    [saveSettings, refreshDiagnostics, t],
  );

  if (!settings) return <main className="settings">{t("common.loading")}</main>;

  const update = (patch: Partial<AppSettings>) => save({ ...settings, ...patch });

  return (
    <main className="settings">
      <header className="settings-header">
        <button type="button" className="link" onClick={onBack}>
          ← {t("app.back")}
        </button>
        <h1>{t("settings.title")}</h1>
      </header>

      <FolderSection
        title={t("settings.gta.title")}
        hint={t("settings.gta.hint")}
        value={settings.gtaFolder}
        placeholder={t("settings.gta.placeholder")}
        note={status?.gtaDetected && status.gtaFolder ? t("settings.gta.detected", { path: status.gtaFolder }) : status?.gta === "missingGta" ? t("settings.gta.notDetected") : null}
        noteKind={status?.gta === "missingGta" ? "warn" : "ok"}
        onChange={(v) => update({ gtaFolder: v })}
      />

      <FolderSection
        title={t("settings.keys.title")}
        hint={t("settings.keys.hint")}
        link={{ href: KEY_TOOL_URL, label: t("settings.keys.link") }}
        value={settings.keysFolder}
        placeholder={diagnostics?.keysFolder ?? ""}
        note={
          diagnostics
            ? diagnostics.missingKeyFiles.length === 0
              ? t("settings.keys.ok")
              : t("settings.keys.missing", { files: diagnostics.missingKeyFiles.join(", ") })
            : null
        }
        noteKind={diagnostics && diagnostics.missingKeyFiles.length > 0 ? "warn" : "ok"}
        onChange={(v) => update({ keysFolder: v })}
      />

      <ResourcesSection settings={settings} onSave={save} onToast={(text) => setToast({ kind: "error", text })} />

      <PedSection ped={settings.ped} gtaReady={status?.gta === "ready"} onChange={(ped) => update({ ped })} />
      <PartnerPedSection ped={settings.ped} partnerPed={settings.partnerPed} gtaReady={status?.gta === "ready"} onChange={(partnerPed) => update({ partnerPed })} />

      <section className="card">
        <h2>{t("settings.viewer.title")}</h2>
        <div className="grid2">
          <label>
            {t("settings.viewer.theme")}
            <select value={settings.viewer.theme} onChange={(e) => update({ viewer: { ...settings.viewer, theme: e.target.value as AppSettings["viewer"]["theme"] } })}>
              <option value="system">{t("settings.viewer.theme.system")}</option>
              <option value="light">{t("settings.viewer.theme.light")}</option>
              <option value="dark">{t("settings.viewer.theme.dark")}</option>
            </select>
          </label>
          <label>
            {t("settings.viewer.language")}
            <select value={settings.viewer.language} onChange={(e) => update({ viewer: { ...settings.viewer, language: e.target.value as AppSettings["viewer"]["language"] } })}>
              <option value="auto">{t("settings.viewer.language.auto")}</option>
              <option value="ja">{t("settings.viewer.language.ja")}</option>
              <option value="en">{t("settings.viewer.language.en")}</option>
            </select>
          </label>
        </div>
      </section>

      <DiagnosticsSection diagnostics={diagnostics} />
      <NoticesSection />

      <section className="card danger">
        <button
          type="button"
          onClick={() => {
            if (confirm(t("settings.quit.confirm"))) void api.quit().catch(() => undefined);
          }}
        >
          {t("settings.quit")}
        </button>
      </section>

      {toast && <div className={`toast ${toast.kind}`}>{toast.text}</div>}
    </main>
  );
}

interface FolderSectionProps {
  title: string;
  hint: string;
  /** Optional external link shown under the hint (e.g. where to get the key tool). */
  link?: { href: string; label: string };
  value: string | null;
  placeholder: string;
  note: string | null;
  noteKind: "ok" | "warn";
  onChange: (value: string | null) => void;
}

/** Text box + server-side folder dialog. The value is saved on blur / Enter, not on every keystroke. */
function FolderSection({ title, hint, link, value, placeholder, note, noteKind, onChange }: FolderSectionProps) {
  const t = useT();
  const [draft, setDraft] = useState(value ?? "");
  useEffect(() => setDraft(value ?? ""), [value]);

  const commit = () => {
    const v = draft.trim() || null;
    if (v !== value) onChange(v);
  };

  return (
    <section className="card">
      <h2>{title}</h2>
      <p className="hint">{hint}</p>
      {link && (
        <p className="hint">
          <a href={link.href} target="_blank" rel="noreferrer">
            {link.label}
          </a>
        </p>
      )}
      <div className="folder-row">
        <input
          type="text"
          value={draft}
          placeholder={placeholder}
          onChange={(e) => setDraft(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => {
            if (e.key === "Enter") commit();
          }}
          spellCheck={false}
        />
        <button
          type="button"
          onClick={() => {
            void api.pickFolder(draft || null, title).then((picked) => {
              if (picked) {
                setDraft(picked);
                onChange(picked);
              }
            });
          }}
        >
          {t("common.browse")}
        </button>
      </div>
      {note && <p className={`note ${noteKind}`}>{note}</p>}
    </section>
  );
}

interface ResourcesSectionProps {
  settings: AppSettings;
  onSave: (next: AppSettings) => Promise<boolean>;
  onToast: (text: string) => void;
}

/** Configured resources (folder or GitHub) with download progress, plus one-click templates. */
function ResourcesSection({ settings, onSave, onToast }: ResourcesSectionProps) {
  const t = useT();
  const jobs = useAppStore((s) => s.resourceJobs);
  const sources = useAppStore((s) => s.sources);
  const loadSettings = useAppStore((s) => s.loadSettings);
  const [data, setData] = useState<ResourcesDto | null>(null);

  const refresh = useCallback(() => {
    api.resources().then(setData).catch(() => undefined);
  }, []);

  // Re-read whenever the settings, the catalog sources or a download state changed.
  useEffect(() => {
    refresh();
  }, [refresh, settings, sources, jobs]);

  const fail = (err: unknown) => onToast(err instanceof ApiError ? errorText(err.code, err.message) : String(err));
  const after = () => void loadSettings().then(refresh).catch(() => undefined);

  const addFolder = async () => {
    const picked = await api.pickFolder(null, t("settings.resources.addFolder"));
    if (!picked) return;
    await api.addFolderResource(picked);
    after();
  };

  const move = (index: number, delta: number) => {
    const next = [...settings.resources];
    const [item] = next.splice(index, 1);
    next.splice(index + delta, 0, item);
    void onSave({ ...settings, resources: next });
  };

  const items = data?.resources ?? [];
  const templates = data?.templates ?? [];
  const jobFor = (id: string) => jobs[id] ?? items.find((r) => r.id === id)?.job ?? null;
  const isBusy = (job: ResourceJobDto | null) => job != null && (job.state === "downloading" || job.state === "extracting");

  return (
    <section className="card">
      <h2>{t("settings.resources.title")}</h2>
      <p className="hint">{t("settings.resources.hint")}</p>
      {items.length === 0 ? (
        <p className="muted">{t("settings.resources.empty")}</p>
      ) : (
        <ul className="resources">
          {items.map((r) => {
            const index = settings.resources.findIndex((x) => x.id === r.id);
            const job = jobFor(r.id);
            const busy = isBusy(job);
            return (
              <li key={r.id} className={r.enabled ? "" : "disabled"}>
                <label className="check resource-enabled" title={t("settings.resources.enabledHint")}>
                  <input
                    type="checkbox"
                    checked={r.enabled}
                    disabled={busy || index < 0}
                    onChange={(e) => void api.setResourceEnabled(r.id, e.target.checked).then(after).catch(fail)}
                    aria-label={t("settings.resources.enabled")}
                  />
                </label>
                <div className="resource-main">
                  <span className="resource-id">
                    {r.id}
                    <span className={`tag ${r.kind}`}>{t(`settings.resources.kind.${r.kind}` as MessageKey)}</span>
                    {r.origin === "github" && <span className="tag kind">GitHub</span>}
                    {!r.enabled && <span className="tag kind">{t("settings.resources.disabled")}</span>}
                    {r.enabled && r.entries > 0 && <span className="muted"> {t("settings.resources.entries", { count: r.entries })}</span>}
                    {!r.exists && !busy && <span className="warn"> {t("settings.resources.missingFolder")}</span>}
                  </span>
                  <span className="resource-path mono">
                    {r.origin === "github" && r.repository ? `${r.repository}@${r.ref ?? ""}` : r.path}
                    {r.source && <span className="muted"> · {t("settings.resources.fetchedAt", { date: new Date(r.source.fetchedAt).toLocaleString() })}</span>}
                  </span>
                  {job && busy && (
                    <span className="resource-progress">
                      <span className="progress">
                        <span
                          className={job.total ? "" : "indeterminate"}
                          style={{ width: job.total ? `${Math.min(100, Math.round((job.bytes / job.total) * 100))}%` : "40%" }}
                        />
                      </span>
                      <span className="muted">
                        {job.state === "downloading" ? t("settings.resources.downloading", { mb: (job.bytes / 1048576).toFixed(1) }) : t("settings.resources.extracting")}
                      </span>
                    </span>
                  )}
                  {job?.state === "error" && <span className="warn">{t("common.failed", { message: job.message ?? "" })}</span>}
                </div>
                <div className="resource-actions">
                  {index >= 0 && (
                    <>
                      <button type="button" disabled={index === 0} onClick={() => move(index, -1)} aria-label={t("common.moveUp")}>
                        ↑
                      </button>
                      <button type="button" disabled={index === settings.resources.length - 1} onClick={() => move(index, 1)} aria-label={t("common.moveDown")}>
                        ↓
                      </button>
                    </>
                  )}
                  {r.origin === "github" && index >= 0 && (
                    <button type="button" disabled={busy} onClick={() => void api.refreshResource(r.id).then(refresh).catch(fail)}>
                      {t("settings.resources.refresh")}
                    </button>
                  )}
                  {index >= 0 && (
                    <button type="button" disabled={busy} title={t("settings.resources.rescanHint")} onClick={() => void api.rescanResource(r.id).then(refresh).catch(fail)}>
                      {t("settings.resources.rescan")}
                    </button>
                  )}
                  <button type="button" disabled={busy} onClick={() => void api.removeResource(r.id).then(after).catch(fail)}>
                    {t("common.remove")}
                  </button>
                </div>
              </li>
            );
          })}
        </ul>
      )}
      <div className="resource-add">
        <button type="button" onClick={() => void addFolder().catch(fail)}>
          {t("settings.resources.addFolder")}
        </button>
      </div>
      <label className="check" title={t("settings.resources.watchHint")}>
        <input type="checkbox" checked={settings.watchFolders ?? true} onChange={(e) => void onSave({ ...settings, watchFolders: e.target.checked })} />
        {t("settings.resources.watch")}
      </label>
      {templates.some((x) => !x.installed) && (
        <div className="templates">
          <p className="hint">{t("settings.resources.templates")}</p>
          <ul className="resources">
            {templates
              .filter((x) => !x.installed)
              .map((x) => (
                <li key={x.id}>
                  <div className="resource-main">
                    <span className="resource-id">
                      {x.id}
                      <span className={`tag ${x.kind}`}>{t(`settings.resources.kind.${x.kind}` as MessageKey)}</span>
                    </span>
                    <span className="resource-path mono">
                      github.com/{x.repository}@{x.ref}
                    </span>
                  </div>
                  <div className="resource-actions">
                    <button type="button" disabled={isBusy(jobFor(x.id))} onClick={() => void api.addGitHubResource(x.repository, x.ref, x.id).then(refresh).catch(fail)}>
                      {t("settings.resources.download")}
                    </button>
                  </div>
                </li>
              ))}
          </ul>
        </div>
      )}
    </section>
  );
}

function DiagnosticsSection({ diagnostics }: { diagnostics: DiagnosticsDto | null }) {
  const t = useT();
  if (!diagnostics) return null;
  const rows: [string, string][] = [
    [t("settings.diagnostics.version"), diagnostics.version],
    [t("settings.diagnostics.url"), diagnostics.url],
    [t("settings.diagnostics.dataDirectory"), diagnostics.dataDirectory],
    [t("settings.diagnostics.settingsPath"), diagnostics.settingsPath],
    [t("settings.diagnostics.logPath"), diagnostics.logPath],
    [t("settings.diagnostics.gta"), `${diagnostics.gtaFolder ?? t("common.none")} (${t(`state.${diagnostics.gta}` as MessageKey)})`],
    [t("settings.diagnostics.keys"), `${diagnostics.keysFolder ?? ""}${diagnostics.missingKeyFiles.length ? ` — ${t("settings.keys.missing", { files: diagnostics.missingKeyFiles.join(", ") })}` : ""}`],
    [t("settings.diagnostics.archives"), String(diagnostics.archives)],
    [t("settings.diagnostics.dictionaries"), String(diagnostics.clipDictionaries)],
    [t("settings.diagnostics.indexTime"), t("settings.diagnostics.ms", { ms: diagnostics.indexMilliseconds })],
    [t("settings.diagnostics.skeleton"), diagnostics.skeletonBones != null ? t("settings.diagnostics.bones", { count: diagnostics.skeletonBones }) : t("common.none")],
    [t("settings.diagnostics.catalog"), String(diagnostics.catalogEntries)],
  ];
  return (
    <section className="card">
      <h2>{t("settings.diagnostics.title")}</h2>
      <dl className="diag">
        {rows.map(([k, v]) => (
          <div key={k} className="detail-row">
            <dt>{k}</dt>
            <dd className="mono">{v}</dd>
          </div>
        ))}
        {diagnostics.catalogWarnings.length > 0 && (
          <div className="detail-row">
            <dt>{t("settings.diagnostics.warnings")}</dt>
            <dd>
              {diagnostics.catalogWarnings.map((w, i) => (
                <div key={i} className="warn">
                  {w}
                </div>
              ))}
            </dd>
          </div>
        )}
      </dl>
    </section>
  );
}

function NoticesSection() {
  const t = useT();
  const [open, setOpen] = useState(false);
  const [text, setText] = useState<string | null>(null);
  useEffect(() => {
    if (open && text === null) api.notices().then(setText).catch(() => setText(""));
  }, [open, text]);
  return (
    <section className="card">
      <details onToggle={(e) => setOpen((e.target as HTMLDetailsElement).open)}>
        <summary>{t("settings.notices.title")}</summary>
        {open && <pre className="notices">{text ?? t("common.loading")}</pre>}
      </details>
    </section>
  );
}

interface PedSectionProps {
  ped: string;
  gtaReady: boolean;
  onChange: (ped: string) => void;
}

interface PartnerPedSectionProps {
  ped: string;
  partnerPed: string | null;
  gtaReady: boolean;
  onChange: (partnerPed: string | null) => void;
}

/** The second ped of shared emotes: "same as the main ped" (null), a freemode ped, or any ped from the list. */
function PartnerPedSection({ ped, partnerPed, gtaReady, onChange }: PartnerPedSectionProps) {
  const t = useT();
  const peds = usePedList(gtaReady);
  const current = partnerPed ?? ped;
  const freemode = partnerPed !== null && ["mp_m_freemode_01", "mp_f_freemode_01"].includes(partnerPed);
  const choose = (value: string) => onChange(value === ped ? null : value);
  return (
    <section className="card">
      <h2>{t("settings.partnerPed.title")}</h2>
      <p className="hint">{t("settings.partnerPed.hint")}</p>
      <div className="ped-current">
        <select value={partnerPed === null ? "same" : freemode ? partnerPed : "custom"} onChange={(e) => (e.target.value === "same" ? onChange(null) : e.target.value !== "custom" && choose(e.target.value))}>
          <option value="same">{t("settings.partnerPed.same")}</option>
          <option value="mp_m_freemode_01">{t("settings.ped.male")}</option>
          <option value="mp_f_freemode_01">{t("settings.ped.female")}</option>
          {partnerPed !== null && !freemode && <option value="custom">{partnerPed}</option>}
        </select>
        <span className="mono muted">{t("settings.ped.current", { ped: current })}</span>
      </div>
      {peds ? <PedPicker peds={peds} selected={current} onSelect={choose} /> : <p className="hint">{t("settings.ped.notReady")}</p>}
    </section>
  );
}

/** Ped picker: the two freemode peds are always offered; the full list needs the indexed game data. */
function PedSection({ ped, gtaReady, onChange }: PedSectionProps) {
  const t = useT();
  const peds = usePedList(gtaReady);
  const freemode = ["mp_m_freemode_01", "mp_f_freemode_01"].includes(ped);
  return (
    <section className="card">
      <h2>{t("settings.ped.title")}</h2>
      <p className="hint">{t("settings.ped.hint")}</p>
      <div className="ped-current">
        <select value={freemode ? ped : "custom"} onChange={(e) => e.target.value !== "custom" && onChange(e.target.value)}>
          <option value="mp_m_freemode_01">{t("settings.ped.male")}</option>
          <option value="mp_f_freemode_01">{t("settings.ped.female")}</option>
          {!freemode && <option value="custom">{ped}</option>}
        </select>
        <span className="mono muted">{t("settings.ped.current", { ped })}</span>
      </div>
      {peds ? <PedPicker peds={peds} selected={ped} onSelect={onChange} /> : <p className="hint">{t("settings.ped.notReady")}</p>}
      <p className="hint">{t("settings.ped.animalHint")}</p>
    </section>
  );
}
