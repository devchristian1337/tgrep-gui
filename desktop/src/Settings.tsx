import { useState } from "react";
import { Check, FolderOpen, Monitor, Sun, Moon, RefreshCw } from "lucide-react";
import type { Settings as Preferences } from "./types";
import { api, native } from "./api";
export default function Settings({
  value,
  onSave,
  busy,
  version,
  updateStatus,
  onUpdateStatus,
}: {
  value: Preferences;
  onSave: (s: Preferences) => Promise<void>;
  busy: boolean;
  version: string;
  updateStatus: string;
  onUpdateStatus: (status: string) => void;
}) {
  const [draft, setDraft] = useState(value);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState("");
  const [updating, setUpdating] = useState(false);
  const set = <K extends keyof Preferences>(key: K, v: Preferences[K]) => {
    setDraft((d) => ({ ...d, [key]: v }));
    setSaved(false);
  };
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
        <fieldset disabled={busy || saving}>
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
            <div className="swatches">
              {["cobalt", "jade", "amber", "rose"].map((a) => (
                <button
                  type="button"
                  key={a}
                  data-accent={a}
                  className="swatch"
                  aria-label={`${a} accent`}
                  aria-pressed={draft.accent === a}
                  onClick={() => set("accent", a)}
                >
                  {draft.accent === a && <Check size={16} />}
                </button>
              ))}
            </div>
          </div>
          <div className="setting-row">
            <div>
              <strong>Interface size</strong>
              <p>Scale text across the entire app.</p>
            </div>
            <label className="scale-control">
              <input
                aria-label="Interface size"
                type="range"
                min="0.75"
                max="1.5"
                step="0.05"
                value={draft.scale}
                onChange={(e) => set("scale", Number(e.target.value))}
              />
              <output>{Math.round(draft.scale * 100)}%</output>
            </label>
          </div>
          <div className="setting-row">
            <div>
              <strong>Result density</strong>
              <p>More breathing room, or more files at a glance.</p>
            </div>
            <select
              aria-label="Result density"
              value={draft.density}
              onChange={(e) => set("density", e.target.value)}
            >
              <option value="comfortable">Comfortable</option>
              <option value="compact">Compact</option>
            </select>
          </div>
        </fieldset>
        <fieldset disabled={busy || saving}>
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
            <button
              type="button"
              disabled={
                !native || updating || Boolean(draft.enginePath.trim())
              }
              aria-label="Update tgrep"
              title="Check GitHub for a newer official tgrep and install it for the next launch"
              onClick={async () => {
                setUpdating(true);
                onUpdateStatus("Checking for tgrep updates…");
                try {
                  onUpdateStatus(await api.checkEngine(draft));
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
          </div>
          {updateStatus && (
            <p className="fieldset-note" role="status">
              {updateStatus}
            </p>
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
          <button className="primary" disabled={busy || saving} type="submit">
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
