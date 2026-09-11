import { useEffect, useState } from "react";
import { Tooltip } from "@/components/ui/beui-tooltip";
import { Check, FolderOpen, Monitor, Sun, Moon, RefreshCw } from "lucide-react";
import type { Settings as Preferences, EngineUpdate } from "./types";
import { api, native } from "./api";
import AccentPicker from "./AccentPicker";
import DensitySelect from "./DensitySelect";
import {
  bindsConflict,
  formatShortcut,
  fromEvent,
  isAllowed,
  mergeShortcuts,
  shortcutActions,
  type ShortcutId,
} from "./shortcuts";
export default function Settings({
  value,
  onSave,
  busy,
  version,
  updateStatus,
  onUpdateStatus,
  onUpdateResult,
  restartRequired,
}: {
  value: Preferences;
  onSave: (s: Preferences) => Promise<void>;
  busy: boolean;
  version: string;
  updateStatus: string;
  onUpdateStatus: (status: string) => void;
  onUpdateResult: (update: EngineUpdate) => void;
  restartRequired: boolean;
}) {
  const [draft, setDraft] = useState(() => ({
    ...value,
    shortcuts: mergeShortcuts(value.shortcuts),
  }));
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState("");
  const [updating, setUpdating] = useState(false);
  const [restarting, setRestarting] = useState(false);
  const [recording, setRecording] = useState<ShortcutId | null>(null);
  const set = <K extends keyof Preferences>(key: K, v: Preferences[K]) => {
    setDraft((d) => ({ ...d, [key]: v }));
    setSaved(false);
  };
  useEffect(() => {
    if (busy || saving || restarting) setRecording(null);
  }, [busy, saving, restarting]);
  useEffect(() => {
    if (!recording) return;
    const onKey = (e: KeyboardEvent) => {
      e.preventDefault();
      e.stopPropagation();
      if (e.key === "Escape") {
        setRecording(null);
        return;
      }
      const bind = fromEvent(e);
      if (!bind) return;
      if (!isAllowed(bind)) {
        setError("Add Ctrl or Alt, or use a function key.");
        return;
      }
      const conflict = shortcutActions.find(
        ({ id }) =>
          id !== recording && bindsConflict(draft.shortcuts[id], bind),
      );
      if (conflict) {
        setError(`That combination is already used by ${conflict.label}.`);
        return;
      }
      set("shortcuts", { ...draft.shortcuts, [recording]: bind });
      setRecording(null);
      setError("");
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, [recording, draft.shortcuts]);
  const browse = async (key: "enginePath" | "editorPath" | "indexPath") => {
    try {
      const path = await api.browse(key === "indexPath");
      if (typeof path === "string") set(key, path);
    } catch (e) {
      setError(String(e));
    }
  };
  return (
    <section className="settings-view">
      <header>
        <span className="eyebrow">YOUR WORKSPACE</span>
        <h1>Make it yours.</h1>
        <p>Appearance, search defaults, and the tools you work with.</p>
      </header>
      <form
        onSubmit={async (e) => {
          e.preventDefault();
          setSaving(true);
          setError("");
          try {
            await onSave(draft);
            setSaved(true);
          } catch (e) {
            setError(String(e));
          } finally {
            setSaving(false);
          }
        }}
      >
        <fieldset
          id="settings-appearance"
          tabIndex={-1}
          disabled={busy || saving || restarting}
        >
          <legend>Appearance</legend>
          <div className="setting-row">
            <div>
              <strong>Theme</strong>
              <p>Choose how your workspace looks.</p>
            </div>
            <div className="segmented" aria-label="Theme">
              {(["system", "light", "dark"] as const).map((key) => {
                const Glyph = { system: Monitor, light: Sun, dark: Moon }[key];
                return (
                  <button
                    type="button"
                    key={key}
                    aria-pressed={draft.theme === key}
                    onClick={() => set("theme", key)}
                  >
                    <Glyph size={16} />
                    {key[0].toUpperCase() + key.slice(1)}
                  </button>
                );
              })}
            </div>
          </div>
          <div className="setting-row">
            <div>
              <strong>Accent color</strong>
              <p>A little color, exactly where it helps.</p>
            </div>
            <AccentPicker
              value={draft.accent}
              onChange={(color) => set("accent", color)}
              disabled={busy || saving || restarting}
            />
          </div>
          <div className="setting-row">
            <div>
              <strong>Result density</strong>
              <p>More breathing room, or more files at a glance.</p>
            </div>
            <DensitySelect
              value={draft.density}
              onChange={(value) => set("density", value)}
            />
          </div>
        </fieldset>
        <fieldset
          id="settings-engine"
          tabIndex={-1}
          disabled={busy || saving || restarting}
        >
          <legend>Search engine</legend>
          <div className="setting-row">
            <div>
              <strong>Installed version</strong>
              <p>
                {version === "Engine unavailable"
                  ? "tgrep is not installed"
                  : version === "Checking engine…"
                    ? "Checking engine version…"
                    : `${version} · in use`}
              </p>
            </div>
            <Tooltip content="Check GitHub for a newer tgrep" side="left">
              <button
                type="button"
                disabled={
                  !native || updating || Boolean(draft.enginePath.trim())
                }
                aria-label="Update tgrep"
                onClick={async () => {
                  setUpdating(true);
                  onUpdateStatus("Checking for tgrep updates…");
                  try {
                    onUpdateResult(await api.checkEngine(draft));
                  } catch (e) {
                    onUpdateStatus(String(e));
                  } finally {
                    setUpdating(false);
                  }
                }}
              >
                <RefreshCw size={16} className={updating ? "spin" : ""} />
                {updating ? "Checking…" : "Update tgrep"}
              </button>
            </Tooltip>
          </div>
          {updateStatus && (
            <p className="fieldset-note" role="status">
              {updateStatus}
            </p>
          )}
          {restartRequired && !draft.enginePath.trim() && (
            <button
              type="button"
              className="small-button"
              disabled={!native || updating || restarting}
              onClick={async () => {
                setRestarting(true);
                setError("");
                try {
                  await onSave(draft);
                  await api.restartApp();
                } catch (e) {
                  setError(String(e));
                  setRestarting(false);
                }
              }}
            >
              <RefreshCw size={16} className={restarting ? "spin" : ""} />
              {restarting ? "Restarting…" : "Restart app"}
            </button>
          )}
          <label className="setting-check">
            <input
              type="checkbox"
              aria-label="Automatically update tgrep"
              checked={draft.autoUpdateEngine}
              onChange={(e) => set("autoUpdateEngine", e.target.checked)}
            />
            Automatically update tgrep
          </label>
          <p className="fieldset-note">
            Verified updates apply after restarting the app. Update tgrep also
            works when automatic checks are off. A manual engine path disables
            managed updates.
          </p>
          {(["enginePath", "indexPath", "editorPath"] as const).map((key) => (
            <label className="setting-field" key={key}>
              <span>
                {
                  {
                    enginePath: "tgrep executable",
                    indexPath: "Custom index directory",
                    editorPath: "External editor",
                  }[key]
                }
              </span>
              <div className="path-control">
                <input
                  value={draft[key]}
                  placeholder={
                    key === "indexPath"
                      ? "Default: .tgrep inside each project"
                      : key === "enginePath"
                        ? "Automatic: verified update, then PATH"
                        : "Automatic"
                  }
                  onChange={(e) => set(key, e.target.value)}
                />
                <button
                  type="button"
                  className="icon-button"
                  disabled={!native}
                  aria-label={`Browse ${key}`}
                  onClick={() => void browse(key)}
                >
                  <FolderOpen size={17} />
                </button>
              </div>
              {key === "indexPath" && (
                <small>Use an absolute path dedicated to one project.</small>
              )}
            </label>
          ))}
          <label className="setting-field">
            <span>Editor arguments</span>
            <input
              value={draft.editorArguments}
              onChange={(e) => set("editorArguments", e.target.value)}
            />
            <small>Use $FILE and $LINE. VS Code: --goto "$FILE:$LINE"</small>
          </label>
          <div className="setting-row">
            <strong>Search defaults</strong>
            <div className="options">
              <label>
                <input
                  type="checkbox"
                  checked={draft.ignoreCase}
                  onChange={(e) => set("ignoreCase", e.target.checked)}
                />
                Ignore case
              </label>
              <label>
                <input
                  type="checkbox"
                  checked={draft.literal}
                  onChange={(e) => set("literal", e.target.checked)}
                />
                Literal text
              </label>
            </div>
          </div>
        </fieldset>
        <fieldset
          id="settings-shortcuts"
          tabIndex={-1}
          disabled={busy || saving || restarting}
        >
          <legend>Shortcuts</legend>
          <p className="fieldset-note">
            Click a shortcut, then press the new keys. Esc cancels editing.
          </p>
          <dl className="shortcuts-list">
            {shortcutActions.map(({ id, label }) => (
              <div key={id}>
                <dt>{label}</dt>
                <dd>
                  <button
                    type="button"
                    className="shortcut-bind"
                    data-shortcut-capture=""
                    aria-label={`${label} shortcut`}
                    aria-pressed={recording === id}
                    onClick={() =>
                      setRecording((current) => (current === id ? null : id))
                    }
                  >
                    {recording === id
                      ? "Press keys…"
                      : formatShortcut(draft.shortcuts[id])}
                  </button>
                </dd>
              </div>
            ))}
          </dl>
          <button
            type="button"
            className="text-button"
            onClick={() => {
              set("shortcuts", mergeShortcuts());
              setRecording(null);
              setError("");
            }}
          >
            Reset shortcuts
          </button>
        </fieldset>
        {error && (
          <p role="alert" className="error-inline">
            {error}
          </p>
        )}
        <div className="settings-save">
          <span role="status">
            {saved
              ? "Settings saved."
              : busy
                ? "Settings are locked while searching."
                : ""}
          </span>
          <button
            className="primary"
            disabled={busy || saving || restarting}
            type="submit"
          >
            {saved ? (
              <>
                <Check size={16} />
                Saved
              </>
            ) : saving ? (
              "Saving…"
            ) : (
              "Save settings"
            )}
          </button>
        </div>
      </form>
    </section>
  );
}
