import { useEffect, useRef, useState } from "react";
import {
  Search,
  Settings2,
  Terminal,
  FolderOpen,
  SlidersHorizontal,
  ArrowRight,
  Square,
  RotateCw,
  X,
  ChevronRight,
  AlertCircle,
  PanelLeftClose,
  PanelLeftOpen,
} from "lucide-react";
import { api, native } from "./api";
import { applyAccent } from "./appearance";
import {
  defaults,
  emptyOptions,
  normalizeSettings,
  type Settings as Preferences,
  type SearchOptions,
  type Hit,
  type Preview,
  type Outcome,
  type EngineUpdate,
} from "./types";
import Settings from "./Settings";
import { FileList, CodePreview } from "./Results";
import { formatShortcut, matches } from "./shortcuts";
import SettingsSidebarAccordion from "@/components/ui/settings-sidebar-accordion";
import WindowControls from "./WindowControls";
import AppContextMenu from "@/components/app-context-menu";
import { Tooltip } from "@/components/ui/beui-tooltip";
import { CopyButton } from "@/components/ui/copy-button";

export default function App() {
  const [page, setPage] = useState<"search" | "settings">("search");
  const [settingsSection, setSettingsSection] = useState("appearance");
  const openSettingsSection = (section: string) => {
    setPage("settings");
    setSettingsSection(section);
    requestAnimationFrame(() => {
      const target = document.getElementById(`settings-${section}`);
      target?.scrollIntoView({ block: "start" });
      target?.focus({ preventScroll: true });
    });
  };
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => {
    try {
      return localStorage.getItem("tgrep-sidebar-collapsed") === "true";
    } catch {
      return false;
    }
  });
  const toggleSidebar = () => {
    const collapsed = !sidebarCollapsed;
    setSidebarCollapsed(collapsed);
    try {
      localStorage.setItem("tgrep-sidebar-collapsed", String(collapsed));
    } catch {
      // The toggle still works when persistent storage is unavailable.
    }
  };
  const [settings, setSettings] = useState<Preferences>(defaults);
  const [ready, setReady] = useState(false);
  const settingsReadable = useRef(true);
  const [options, setOptions] = useState<SearchOptions>(emptyOptions);
  const [filters, setFilters] = useState(false);
  const [hits, setHits] = useState<Hit[]>([]);
  const hitMap = useRef(new Map<string, Hit>());
  const [selected, setSelected] = useState("");
  const [preview, setPreview] = useState<Preview | null>(null);
  const [previewLoading, setPreviewLoading] = useState(false);
  const [previewError, setPreviewError] = useState("");
  const previewId = useRef(0);
  const [busy, setBusy] = useState(false);
  const running = useRef(false);
  const searchId = useRef(0);
  const [message, setMessage] = useState("Ready when you are");
  const [error, setError] = useState("");
  const [outcome, setOutcome] = useState<Outcome | null>(null);
  const [version, setVersion] = useState("Checking engine…");
  const [engineUpdate, setEngineUpdate] = useState("");
  const [restartRequired, setRestartRequired] = useState(false);
  const zoom = useRef(1);
  const receiveEngineUpdate = (update: EngineUpdate) => {
    setEngineUpdate(update.message);
    // A later failed check must not hide an update already ready to use.
    if (update.restartRequired) setRestartRequired(true);
  };
  const query = useRef<HTMLInputElement>(null);
  const logDialog = useRef<HTMLDialogElement>(null);
  /* Modal open / close (transitions.dev #06). showModal() paints the closed
     state, so .is-open has to land a frame later for the scale-up to run;
     closing keeps the dialog in the top layer for --modal-close-dur so the
     scale-down is not cut off. Every exit - the X, the cancel shortcut,
     Escape - goes through closeLogs, and it is safe to call twice. */
  const openLogs = () => {
    const el = logDialog.current;
    if (!el || el.open) return;
    el.classList.remove("is-closing");
    el.showModal();
    requestAnimationFrame(() => el.classList.add("is-open"));
  };
  const closeLogs = () => {
    const el = logDialog.current;
    if (!el || !el.open || el.classList.contains("is-closing")) return;
    el.classList.remove("is-open");
    el.classList.add("is-closing");
    const ms =
      parseFloat(
        getComputedStyle(document.documentElement).getPropertyValue(
          "--modal-close-dur",
        ),
      ) || 150;
    setTimeout(() => {
      el.classList.remove("is-closing");
      el.close();
    }, ms);
  };
  const [logs, setLogs] = useState<string[]>([]);
  const [split, setSplit] = useState(31);
  const results = useRef<HTMLDivElement>(null);
  const opt = <K extends keyof SearchOptions>(
    key: K,
    value: SearchOptions[K],
  ) => setOptions((o) => ({ ...o, [key]: value }));
  useEffect(() => {
    let live = true;
    (async () => {
      try {
        const s = normalizeSettings(await api.settings());
        const prefill = await api.prefill();
        if (live) {
          setSettings(s);
          setOptions({
            ...emptyOptions,
            ignoreCase: s.ignoreCase,
            literal: s.literal,
            folder: s.recentFolders[0] || "",
            ...prefill,
          });
          setFilters(Boolean(prefill.include || prefill.exclude));
        }
      } catch (e) {
        settingsReadable.current = false;
        if (live) setError(String(e));
      } finally {
        if (live) setReady(true);
      }
    })();
    return () => {
      live = false;
    };
  }, []);
  useEffect(() => {
    const root = document.documentElement;
    const media = matchMedia("(prefers-color-scheme: dark)");
    const apply = () => {
      root.dataset.theme =
        settings.theme === "system"
          ? media.matches
            ? "dark"
            : "light"
          : settings.theme;
      root.dataset.accent = settings.accent;
      applyAccent(root, settings.accent);
      if (ready)
        void api.theme(root.dataset.theme).catch((e) => setError(String(e)));
    };
    apply();
    media.addEventListener("change", apply);
    return () => media.removeEventListener("change", apply);
  }, [settings, ready]);
  useEffect(() => {
    if (!ready) return;
    let live = true;
    api
      .version(settings)
      .then((v) => {
        if (live) setVersion(v);
      })
      .catch((e) => {
        if (live) {
          setVersion("Engine unavailable");
          setError(String(e));
        }
      });
    return () => {
      live = false;
    };
  }, [ready, settings.enginePath]);
  useEffect(() => {
    if (!ready || !native || !settingsReadable.current) return;
    if (!settings.autoUpdateEngine || settings.enginePath.trim()) {
      if (settings.enginePath.trim()) {
        setRestartRequired(false);
        setEngineUpdate(
          "A custom engine path is configured. Clear it and save settings to use managed updates.",
        );
      }
      return;
    }
    let live = true;
    setEngineUpdate("Checking for tgrep updates…");
    api
      .checkEngine(settings)
      .then((status) => {
        if (live) receiveEngineUpdate(status);
      })
      .catch((e) => {
        if (live) setEngineUpdate(String(e));
      });
    return () => {
      live = false;
    };
  }, [ready, settings.autoUpdateEngine, settings.enginePath]);
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if ((e.target as HTMLElement | null)?.closest("[data-shortcut-capture]"))
        return;
      const sc = settings.shortcuts;
      const zoomDir = matches(sc.zoomIn, e)
        ? 1
        : matches(sc.zoomOut, e)
          ? -1
          : matches(sc.zoomReset, e)
            ? 0
            : null;
      if (zoomDir !== null) {
        e.preventDefault();
        const previous = zoom.current;
        const next =
          zoomDir === 0
            ? 1
            : Math.max(
                0.5,
                Math.min(2, Math.round((previous + zoomDir * 0.1) * 10) / 10),
              );
        zoom.current = next;
        void api.zoom(next).catch((error) => {
          if (zoom.current === next) zoom.current = previous;
          setError(`Could not change zoom: ${error}`);
        });
        return;
      }
      if (matches(sc.focusSearch, e)) {
        e.preventDefault();
        setPage("search");
        setTimeout(() => {
          query.current?.focus();
          query.current?.select();
        }, 0);
        return;
      }
      if (matches(sc.cancel, e)) {
        if (logDialog.current?.open) {
          e.preventDefault();
          closeLogs();
        } else if (running.current) {
          e.preventDefault();
          void api.cancel().catch((err) => setError(String(err)));
        }
      }
    };
    window.addEventListener("keydown", key);
    return () => window.removeEventListener("keydown", key);
  }, [settings.shortcuts]);
  useEffect(() => {
    if (!logDialog.current?.open) return;
    const timer = setInterval(() => {
      api
        .logs()
        .then(setLogs)
        .catch((e) => setError(String(e)));
    }, 1000);
    return () => clearInterval(timer);
  }, [logs]);
  const copy = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch (e) {
      setError(`Could not copy to clipboard: ${e}`);
      return false;
    }
  };
  const select = async (path: string) => {
    const id = ++previewId.current;
    setSelected(path);
    setPreview(null);
    setPreviewLoading(true);
    setPreviewError("");
    try {
      const p = await api.preview(path);
      if (id === previewId.current) setPreview(p);
    } catch (e) {
      if (id === previewId.current) setPreviewError(String(e));
    } finally {
      if (id === previewId.current) setPreviewLoading(false);
    }
  };
  const start = async () => {
    if (running.current || !ready) return;
    if (!options.folder.trim() || !options.pattern) {
      setError("Choose a project folder and enter a search pattern.");
      return;
    }
    if (!native) {
      setError(
        "This is the browser preview. Run the Tauri desktop app to search local files.",
      );
      return;
    }
    running.current = true;
    setBusy(true);
    setError("");
    setOutcome(null);
    setMessage("Preparing search…");
    setHits([]);
    hitMap.current.clear();
    setSelected("");
    setPreview(null);
    ++previewId.current;
    const id = ++searchId.current;
    let scheduled: number | undefined;
    const flush = () => {
      scheduled = undefined;
      setHits([...hitMap.current.values()]);
    };
    try {
      const result = await api.search(id, options, settings, (e) => {
        if (e.id !== searchId.current) return;
        if (e.type === "progress") setMessage(e.message);
        else {
          for (const h of e.hits) {
            const old = hitMap.current.get(h.path);
            hitMap.current.set(h.path, {
              ...h,
              count: (old?.count || 0) + h.count,
            });
          }
          if (scheduled === undefined) scheduled = requestAnimationFrame(flush);
        }
      });
      if (scheduled !== undefined) cancelAnimationFrame(scheduled);
      flush();
      setOutcome(result);
      setMessage(
        result.cancelled
          ? "Search cancelled · partial results retained"
          : result.truncated
            ? "Result limit reached · narrow your search"
            : result.files
              ? "Search complete"
              : "No matches · try another pattern or adjust filters",
      );
      if (settingsReadable.current) {
        const next = {
          ...settings,
          recentFolders: [
            options.folder,
            ...settings.recentFolders.filter((f) => f !== options.folder),
          ].slice(0, 12),
        };
        try {
          await api.save(next);
          setSettings(next);
        } catch (e) {
          setError(
            `Search finished, but recent folders could not be saved: ${e}`,
          );
        }
      }
    } catch (e) {
      setError(String(e));
      setMessage("Search stopped");
    } finally {
      if (scheduled !== undefined) cancelAnimationFrame(scheduled);
      flush();
      running.current = false;
      setBusy(false);
    }
  };
  const browse = async () => {
    try {
      const p = await api.browse(true);
      if (typeof p === "string") opt("folder", p);
    } catch (e) {
      setError(String(e));
    }
  };
  const open = async (line: number, folder = false) => {
    try {
      await api.open(selected, line, settings, folder);
    } catch (e) {
      setError(String(e));
    }
  };
  const showLogs = async () => {
    try {
      setLogs(await api.logs());
      openLogs();
    } catch (e) {
      setError(String(e));
    }
  };
  const restart = async () => {
    if (running.current) return;
    running.current = true;
    setBusy(true);
    setMessage("Restarting owned server…");
    try {
      await api.restart();
      setMessage("Server restarted");
    } catch (e) {
      setError(String(e));
    } finally {
      running.current = false;
      setBusy(false);
    }
  };
  const save = async (s: Preferences) => {
    const next = normalizeSettings(s);
    await api.save(next);
    settingsReadable.current = true;
    setSettings(next);
    setOptions((o) => ({
      ...o,
      ignoreCase: next.ignoreCase,
      literal: next.literal,
    }));
  };
  return (
    <AppContextMenu
      className="desktop-frame"
      previewPath={selected}
      canOpen={native && !busy}
      onError={setError}
      onOpen={(path, line, folder) =>
        void api.open(path, line, settings, folder).catch((e) => {
          setError(String(e));
        })
      }
    >
      {native && (
        <header className="titlebar" data-tauri-drag-region>
          <span className="titlebar-title" data-tauri-drag-region>
            tgrep Studio
          </span>
          <div className="titlebar-drag-space" data-tauri-drag-region />
          <WindowControls onError={setError} />
        </header>
      )}
      <div
        className={`app-shell${sidebarCollapsed ? " sidebar-collapsed" : ""}`}
      >
        <div
          className="sidebar-track"
          inert={sidebarCollapsed}
          aria-hidden={sidebarCollapsed}
        >
          <aside className="sidebar" id="workspace-sidebar">
            <a
              className="brand"
              href="#"
              onClick={(e) => {
                e.preventDefault();
                setPage("search");
              }}
              aria-label="tgrep home"
            >
              <span className="brand-symbol">
                t<span>›</span>
              </span>
              <span>
                tgrep<small>STUDIO</small>
              </span>
            </a>
            <div className="nav-caption eyebrow">WORKSPACE</div>
            <nav aria-label="Main navigation">
              <button
                className={page === "search" ? "active" : ""}
                onClick={() => setPage("search")}
              >
                <Search size={18} />
                Search
              </button>
              <SettingsSidebarAccordion
                heading=""
                defaultValue={["settings"]}
                active={page === "settings" ? settingsSection : undefined}
                items={[
                  {
                    icon: Settings2,
                    label: "Settings",
                    value: "settings",
                    onSelect: () => setPage("settings"),
                    links: [
                      {
                        label: "Appearance",
                        value: "appearance",
                        onSelect: () => openSettingsSection("appearance"),
                      },
                      {
                        label: "Search engine",
                        value: "engine",
                        onSelect: () => openSettingsSection("engine"),
                      },
                      {
                        label: "Shortcuts",
                        value: "shortcuts",
                        onSelect: () => openSettingsSection("shortcuts"),
                      },
                    ],
                  },
                ]}
              />
            </nav>
            <div className="recent-projects">
              <span className="eyebrow">RECENT PROJECTS</span>
              {settings.recentFolders.length ? (
                settings.recentFolders.slice(0, 6).map((f) => (
                  <button
                    key={f}
                    disabled={busy}
                    title={f}
                    onClick={() => {
                      opt("folder", f);
                      setPage("search");
                    }}
                  >
                    <FolderOpen size={14} />
                    <span>{f.split(/[\\/]/).filter(Boolean).pop()}</span>
                  </button>
                ))
              ) : (
                <p>
                  Your projects will
                  <br />
                  feel at home here.
                </p>
              )}
            </div>
            <div className="sidebar-bottom">
              <button onClick={() => void showLogs()}>
                <Terminal size={17} />
                Engine log
              </button>
              <span className="engine-label">
                <span
                  className={`status-dot ${version === "Engine unavailable" ? "unavailable" : ""}`}
                />
                {version}
              </span>
            </div>
          </aside>
        </div>
        <main>
          <header className="topbar">
            <span>
              <Tooltip
                content={
                  sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar"
                }
                side="bottom"
              >
                <button
                  className="icon-button sidebar-toggle"
                  aria-label={
                    sidebarCollapsed ? "Expand sidebar" : "Collapse sidebar"
                  }
                  aria-expanded={!sidebarCollapsed}
                  aria-controls="workspace-sidebar"
                  onClick={toggleSidebar}
                >
                  {sidebarCollapsed ? (
                    <PanelLeftOpen size={18} />
                  ) : (
                    <PanelLeftClose size={18} />
                  )}
                </button>
              </Tooltip>
              <span className="workspace-label">Workspace</span>{" "}
              <ChevronRight size={14} />
              <strong>{page === "search" ? "Search" : "Settings"}</strong>
            </span>
            <button
              className="shortcut"
              onClick={() => {
                setPage("search");
                setTimeout(() => query.current?.focus(), 0);
              }}
            >
              Quick search{" "}
              <kbd>{formatShortcut(settings.shortcuts.focusSearch)}</kbd>
            </button>
          </header>
          {error && (
            <div className="error-banner" role="alert">
              <AlertCircle size={17} />
              <span>{error}</span>
              <button
                className="icon-button"
                aria-label="Dismiss error"
                onClick={() => setError("")}
              >
                <X size={15} />
              </button>
            </div>
          )}
          {!native && (
            <div className="preview-notice">
              Interface preview · Local search is available in the desktop app.
            </div>
          )}
          {page === "settings" ? (
            <Settings
              value={settings}
              onSave={save}
              busy={busy}
              version={version}
              updateStatus={engineUpdate}
              restartRequired={restartRequired}
              onUpdateResult={receiveEngineUpdate}
              onUpdateStatus={setEngineUpdate}
            />
          ) : (
            <div className="search-view">
              <header className="search-heading">
                <div>
                  <span className="eyebrow">PROJECT SEARCH</span>
                  <h1>Find your next line.</h1>
                  <p>Search across your code. Stay in your flow.</p>
                </div>
                <span className="local-label">Local files. Local search.</span>
              </header>
              <form
                onSubmit={(e) => {
                  e.preventDefault();
                  void start();
                }}
                className="search-form"
              >
                <label htmlFor="folder" className="field-label">
                  Project folder
                </label>
                <div className="project-control">
                  <FolderOpen size={18} />
                  <input
                    id="folder"
                    list="recent-folders"
                    placeholder="Choose a project directory…"
                    value={options.folder}
                    disabled={busy}
                    onChange={(e) => opt("folder", e.target.value)}
                    spellCheck={false}
                    autoComplete="off"
                  />
                  <datalist id="recent-folders">
                    {settings.recentFolders.map((f) => (
                      <option key={f} value={f} />
                    ))}
                  </datalist>
                  <button
                    type="button"
                    disabled={busy || !native}
                    onClick={() => void browse()}
                  >
                    Browse <span>…</span>
                  </button>
                </div>
                <div className="query-row">
                  <div className="query-control">
                    <Search size={21} />
                    <input
                      ref={query}
                      aria-label="Search pattern"
                      placeholder="What are you looking for?"
                      value={options.pattern}
                      disabled={busy}
                      onChange={(e) => opt("pattern", e.target.value)}
                      spellCheck={false}
                    />
                    <kbd>↵</kbd>
                  </div>
                  {busy ? (
                    <button
                      type="button"
                      className="cancel-button"
                      onClick={() =>
                        void api.cancel().catch((e) => setError(String(e)))
                      }
                    >
                      <Square size={14} />
                      Cancel
                    </button>
                  ) : (
                    <button
                      className="primary search-button"
                      type="submit"
                      disabled={!ready}
                    >
                      Search <ArrowRight size={17} />
                    </button>
                  )}
                </div>
                <div className="options-row">
                  <div className="options">
                    {(
                      [
                        "ignoreCase",
                        "literal",
                        "wholeWord",
                        "useIndex",
                      ] as const
                    ).map((key) => (
                      <label key={key}>
                        <input
                          type="checkbox"
                          checked={options[key]}
                          disabled={busy}
                          onChange={(e) => opt(key, e.target.checked)}
                        />
                        {
                          {
                            ignoreCase: "Ignore case",
                            literal: "Literal text",
                            wholeWord: "Whole word",
                            useIndex: "Use index",
                          }[key]
                        }
                      </label>
                    ))}
                  </div>
                  <button
                    type="button"
                    className={`filter-button ${filters ? "selected" : ""}`}
                    aria-expanded={filters}
                    aria-controls="filter-fields"
                    onClick={() => setFilters((f) => !f)}
                  >
                    <SlidersHorizontal size={15} />
                    Filters
                    {(options.include || options.exclude) && (
                      <span className="filter-dot" />
                    )}
                  </button>
                </div>
                <div
                  className="filter-disclosure t-acc"
                  data-open={filters ? "true" : "false"}
                >
                  <div className="t-acc-panel">
                    <div
                      className="t-acc-panel-inner"
                      inert={!filters}
                      aria-hidden={!filters}
                    >
                      <div id="filter-fields" className="filter-fields">
                        <label>
                          Include files
                          <input
                            placeholder="*.rs; *.{ts,tsx}"
                            value={options.include}
                            disabled={busy}
                            onChange={(e) => opt("include", e.target.value)}
                          />
                        </label>
                        <label>
                          Exclude files
                          <input
                            placeholder="target/; node_modules/"
                            value={options.exclude}
                            disabled={busy}
                            onChange={(e) => opt("exclude", e.target.value)}
                          />
                        </label>
                      </div>
                    </div>
                  </div>
                </div>
              </form>
              <div className="results-toolbar">
                <span role="status">
                  {busy ? (
                    <span className="working">{message}</span>
                  ) : outcome ? (
                    <>
                      <strong>{outcome.matches.toLocaleString()}</strong>{" "}
                      matches in{" "}
                      <strong>{outcome.files.toLocaleString()}</strong> files{" "}
                      <span className="quiet">
                        ·{" "}
                        {outcome.elapsedMs < 1000
                          ? `${outcome.elapsedMs} ms`
                          : `${(outcome.elapsedMs / 1000).toFixed(2)} s`}
                      </span>
                    </>
                  ) : (
                    <span className="quiet">
                      Your results, without the noise.
                    </span>
                  )}
                </span>
                <Tooltip
                  content="Restart server started by this app"
                  side="left"
                >
                  <button
                    className="icon-button"
                    aria-label="Restart server"
                    disabled={busy || !native}
                    onClick={() => void restart()}
                  >
                    <RotateCw size={15} />
                  </button>
                </Tooltip>
              </div>
              <div
                className="results"
                ref={results}
                style={{
                  gridTemplateColumns: `minmax(180px, ${split}%) 5px minmax(0, 1fr)`,
                }}
              >
                <FileList
                  hits={hits}
                  selected={selected}
                  onSelect={(p) => void select(p)}
                  busy={busy}
                  dense={settings.density === "compact"}
                  shortcuts={settings.shortcuts}
                />
                <div
                  className="splitter"
                  role="separator"
                  aria-label="Resize result panels"
                  aria-orientation="vertical"
                  aria-valuemin={20}
                  aria-valuemax={65}
                  aria-valuenow={split}
                  tabIndex={0}
                  onKeyDown={(e) => {
                    if (e.key === "ArrowLeft" || e.key === "ArrowRight") {
                      e.preventDefault();
                      setSplit((s) =>
                        Math.max(
                          20,
                          Math.min(65, s + (e.key === "ArrowLeft" ? -2 : 2)),
                        ),
                      );
                    }
                  }}
                  onPointerDown={(e) => {
                    e.currentTarget.setPointerCapture(e.pointerId);
                  }}
                  onPointerMove={(e) => {
                    if (e.currentTarget.hasPointerCapture(e.pointerId)) {
                      const r = results.current?.getBoundingClientRect();
                      if (r)
                        setSplit(
                          Math.max(
                            20,
                            Math.min(
                              65,
                              ((e.clientX - r.left) / r.width) * 100,
                            ),
                          ),
                        );
                    }
                  }}
                  onPointerUp={(e) =>
                    e.currentTarget.releasePointerCapture(e.pointerId)
                  }
                />
                <CodePreview
                  key={selected}
                  hit={hits.find((h) => h.path === selected)}
                  preview={preview}
                  loading={previewLoading}
                  error={previewError}
                  onOpen={(l) => void open(l)}
                  onFolder={() => void open(1, true)}
                  onCopy={copy}
                  shortcuts={settings.shortcuts}
                />
              </div>
            </div>
          )}
          <footer className="statusbar">
            <span>
              <span className={`status-dot ${busy ? "working-dot" : ""}`} />
              {message}
            </span>
            <span>
              {options.useIndex ? "Indexed search" : "Direct scan"}
              <span className="status-divider">/</span>tgrep Studio
            </span>
          </footer>
        </main>
        <dialog
          ref={logDialog}
          className="log-dialog t-modal"
          onCancel={(e) => {
            e.preventDefault();
            closeLogs();
          }}
        >
          <div className="panel-title">
            <span>
              <Terminal size={17} />
              Engine log
            </span>
            <button
              className="icon-button"
              aria-label="Close log"
              onClick={closeLogs}
            >
              <X size={18} />
            </button>
          </div>
          <pre>{logs.length ? logs.join("\n") : "No engine messages yet."}</pre>
          <div className="log-actions">
            <span>Last 1,000 messages · kept in memory</span>
            <CopyButton
              value={() => logs.join("\n")}
              label="Copy log"
              copiedLabel="Copied"
              resetAfter={2000}
              iconSize={15}
              onCopy={copy}
            />
          </div>
        </dialog>
      </div>
    </AppContextMenu>
  );
}
