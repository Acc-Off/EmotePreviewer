import { useEffect, useState } from "react";
import { CatalogPane } from "./catalog/CatalogPane";
import { DetailPanel } from "./catalog/DetailPanel";
import { SettingsPage } from "./settings/SettingsPage";
import { subscribeEvents } from "./shared/api";
import { useI18nStore, useT } from "./shared/i18n";
import { KEY_TOOL_URL } from "./shared/links";
import { useAppStore } from "./shared/store";
import { Viewer } from "./viewer/Viewer";

type Route = "main" | "settings";

function routeFromPath(path: string): Route {
  return path.startsWith("/settings") ? "settings" : "main";
}

// Until the server's language setting arrives, follow the browser.
useI18nStore.getState().setLanguage("auto");

export function App() {
  const [route, setRoute] = useState<Route>(() => routeFromPath(location.pathname));
  const connected = useAppStore((s) => s.connected);
  const loadStatus = useAppStore((s) => s.loadStatus);
  const loadCatalog = useAppStore((s) => s.loadCatalog);
  const loadSettings = useAppStore((s) => s.loadSettings);
  const setStatus = useAppStore((s) => s.setStatus);
  const setConnected = useAppStore((s) => s.setConnected);
  const setResourceJob = useAppStore((s) => s.setResourceJob);
  const t = useT();

  // Initial data, then live updates over SSE. Every catalog event with a new revision triggers one re-fetch.
  useEffect(() => {
    void loadSettings().catch(() => undefined);
    void loadStatus().catch(() => undefined);
    void loadCatalog().catch(() => undefined);
    const unsubscribe = subscribeEvents({
      status: setStatus,
      catalog: (event) => {
        if (event.revision !== useAppStore.getState().catalogRevision) void loadCatalog().catch(() => undefined);
      },
      resource: (job) => {
        setResourceJob(job);
        if (job.state === "done") void loadSettings().catch(() => undefined);
      },
      connection: (ok) => {
        setConnected(ok);
        if (ok) {
          void loadStatus().catch(() => undefined);
          void loadCatalog().catch(() => undefined);
        }
      },
    });
    return unsubscribe;
  }, [loadSettings, loadStatus, loadCatalog, setStatus, setConnected, setResourceJob]);

  useEffect(() => {
    const onPop = () => setRoute(routeFromPath(location.pathname));
    addEventListener("popstate", onPop);
    return () => removeEventListener("popstate", onPop);
  }, []);

  const navigate = (next: Route) => {
    const path = next === "settings" ? "/settings" : "/";
    if (location.pathname !== path) history.pushState(null, "", path);
    setRoute(next);
  };

  return (
    <div className={`app route-${route}`}>
      <TopBar route={route} onNavigate={navigate} />
      {route === "settings" ? (
        <SettingsPage onBack={() => navigate("main")} />
      ) : (
        <div className="main">
          <CatalogPane />
          <div className="stage">
            <Viewer />
            <DetailPanel />
          </div>
        </div>
      )}
      <Banners onOpenSettings={() => navigate("settings")} />
      {connected === false && (
        <div className="overlay">
          <h2>{t("status.disconnected.title")}</h2>
          <p>{t("status.disconnected.body")}</p>
        </div>
      )}
    </div>
  );
}

function TopBar({ route, onNavigate }: { route: Route; onNavigate: (r: Route) => void }) {
  const t = useT();
  const status = useAppStore((s) => s.status);
  const previewResolved = useAppStore((s) => s.previewResolved);
  const catalogLoaded = useAppStore((s) => s.catalogLoaded);

  let pill: { text: string; kind: string } | null = null;
  if (status) {
    switch (status.gta) {
      case "indexing":
        pill = { text: status.progressTotal > 0 ? t("status.indexingProgress", { done: status.progressDone, total: status.progressTotal }) : t("status.indexing"), kind: "busy" };
        break;
      case "ready":
        pill = !previewResolved && catalogLoaded && status.catalogEntries > 0 ? { text: t("status.resolving"), kind: "busy" } : { text: t("status.ready", { count: status.clipDictionaries }), kind: "ok" };
        break;
      case "missingGta":
        pill = { text: t("status.missingGta"), kind: "warn" };
        break;
      case "missingKeys":
        pill = { text: t("status.missingKeys"), kind: "warn" };
        break;
      default:
        pill = { text: t("status.error", { message: status.message ?? "" }), kind: "error" };
    }
  }
  const progress = status?.gta === "indexing" && status.progressTotal > 0 ? status.progressDone / status.progressTotal : null;

  return (
    <header className="topbar">
      <button type="button" className="brand" onClick={() => onNavigate("main")}>
        <svg viewBox="0 0 32 32" width="22" height="22" aria-hidden="true">
          <rect width="32" height="32" rx="6" fill="var(--accent)" />
          <circle cx="16" cy="8" r="3.2" fill="#fff" />
          <path d="M16 11v9M16 14l-6 4M16 14l6 4M16 20l-4 8M16 20l4 8" stroke="#fff" strokeWidth="2.4" strokeLinecap="round" fill="none" />
        </svg>
        <span>{t("app.title")}</span>
        {status && <span className="version mono">v{status.version}</span>}
      </button>
      <div className="topbar-status">
        {pill && (
          <span className={`pill ${pill.kind}`} title={status?.message ?? undefined}>
            {pill.kind === "busy" && <span className="spinner" aria-hidden="true" />}
            {pill.text}
          </span>
        )}
        {progress != null && (
          <span className="progress" aria-hidden="true">
            <span style={{ width: `${Math.round(progress * 100)}%` }} />
          </span>
        )}
      </div>
      <nav>
        <button type="button" className={route === "settings" ? "on" : ""} onClick={() => onNavigate(route === "settings" ? "main" : "settings")}>
          ⚙ {t("app.settings")}
        </button>
      </nav>
    </header>
  );
}

/** First-run and missing-prerequisite hints shown above the list (never on the settings page itself). */
function Banners({ onOpenSettings }: { onOpenSettings: () => void }) {
  const t = useT();
  const status = useAppStore((s) => s.status);
  const settings = useAppStore((s) => s.settings);
  const catalogLoaded = useAppStore((s) => s.catalogLoaded);
  const entries = useAppStore((s) => s.entries);
  const [dismissed, setDismissed] = useState<string | null>(null);
  if (location.pathname.startsWith("/settings")) return null;

  let key: "noResources" | "missingGta" | "missingKeys" | null = null;
  if (catalogLoaded && settings && settings.resources.length === 0 && entries.length === 0) key = "noResources";
  else if (status?.gta === "missingGta") key = "missingGta";
  else if (status?.gta === "missingKeys") key = "missingKeys";
  if (!key || dismissed === key) return null;

  return (
    <div className={`banner ${key === "noResources" ? "info" : "warn"}`} role="status">
      <div>
        <strong>{t(`status.${key}.title` as "status.noResources.title")}</strong>
        <p>{t(`status.${key}.body` as "status.noResources.body")}</p>
        {key === "missingKeys" && (
          <p>
            <a href={KEY_TOOL_URL} target="_blank" rel="noreferrer">
              {t("settings.keys.link")}
            </a>
          </p>
        )}
      </div>
      <div className="banner-actions">
        <button type="button" onClick={onOpenSettings}>
          {t("status.openSettings")}
        </button>
        <button type="button" className="link" onClick={() => setDismissed(key)} aria-label={t("common.close")}>
          ✕
        </button>
      </div>
    </div>
  );
}
