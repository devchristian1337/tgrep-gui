import { useMemo, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import {
  FileCode2,
  Search,
  ArrowUpRight,
  Copy,
  FolderOpen,
  Check,
} from "lucide-react";
import type { Hit, Preview, MatchLine } from "./types";
export function Highlight({ line }: { line: MatchLine }) {
  const pieces = [];
  let offset = 0;
  for (const [start, end] of line.spans) {
    if (start < offset || end <= start) continue;
    pieces.push(line.text.slice(offset, start));
    pieces.push(
      <mark key={`${start}-${end}`}>{line.text.slice(start, end)}</mark>,
    );
    offset = end;
  }
  pieces.push(line.text.slice(offset));
  return <>{pieces}</>;
}
export function FileList({
  hits,
  selected,
  onSelect,
  busy,
  dense,
}: {
  hits: Hit[];
  selected: string;
  onSelect: (p: string) => void;
  busy: boolean;
  dense: boolean;
}) {
  const [filter, setFilter] = useState("");
  const parent = useRef<HTMLDivElement>(null);
  const filtered = useMemo(
    () =>
      hits.filter((h) =>
        h.relativePath.toLowerCase().includes(filter.toLowerCase()),
      ),
    [hits, filter],
  );
  const virtual = useVirtualizer({
    count: filtered.length,
    getScrollElement: () => parent.current,
    estimateSize: () => (dense ? 54 : 64),
    overscan: 8,
  });
  return (
    <section className="file-panel" aria-label="Search results">
      <div className="panel-title">
        <span>Files</span>
        <span className="count">{hits.length.toLocaleString()}</span>
      </div>
      <div className="result-filter">
        <Search size={14} />
        <input
          aria-label="Filter results"
          placeholder="Filter results…"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
        />
      </div>
      <div
        className="file-scroll"
        ref={parent}
        aria-busy={busy}
        tabIndex={0}
        aria-label="Files. Use arrow keys to select a result."
        onKeyDown={(e) => {
          if (
            busy ||
            !filtered.length ||
            !["ArrowDown", "ArrowUp", "Home", "End"].includes(e.key)
          )
            return;
          e.preventDefault();
          const current = filtered.findIndex((h) => h.path === selected);
          const next =
            e.key === "Home"
              ? 0
              : e.key === "End"
                ? filtered.length - 1
                : Math.max(
                    0,
                    Math.min(
                      filtered.length - 1,
                      current + (e.key === "ArrowDown" ? 1 : -1),
                    ),
                  );
          virtual.scrollToIndex(next);
          onSelect(filtered[next].path);
        }}
      >
        {filtered.length === 0 ? (
          <div className="list-empty">
            {filter
              ? "No files match this filter."
              : busy
                ? "Waiting for results…"
                : "Matching files appear here."}
          </div>
        ) : (
          <div style={{ height: virtual.getTotalSize(), position: "relative" }}>
            {virtual.getVirtualItems().map((v) => {
              const hit = filtered[v.index];
              const parts = hit.relativePath.split(/[\\/]/);
              const name = parts.pop();
              return (
                <button
                  key={hit.path}
                  className={`file-row ${selected === hit.path ? "selected" : ""}`}
                  aria-pressed={selected === hit.path}
                  disabled={busy}
                  style={{
                    height: v.size,
                    transform: `translateY(${v.start}px)`,
                  }}
                  onClick={() => onSelect(hit.path)}
                  title={hit.relativePath}
                >
                  <FileCode2 size={18} />
                  <span className="file-meta">
                    <strong>{name}</strong>
                    <small>{parts.join("/") || "Project root"}</small>
                  </span>
                  <span className="file-count">
                    {hit.count.toLocaleString()}
                  </span>
                </button>
              );
            })}
          </div>
        )}
      </div>
    </section>
  );
}
export function CodePreview({
  hit,
  preview,
  loading,
  error,
  onOpen,
  onFolder,
  onCopy,
}: {
  hit?: Hit;
  preview: Preview | null;
  loading: boolean;
  error: string;
  onOpen: (line: number) => void;
  onFolder: () => void;
  onCopy: (text: string) => Promise<boolean>;
}) {
  const parent = useRef<HTMLDivElement>(null);
  const [copied, setCopied] = useState(false);
  const [chosen, setChosen] = useState<number[]>([]);
  const anchor = useRef(0);
  const lines = preview?.lines || [];
  const virtual = useVirtualizer({
    count: lines.length,
    getScrollElement: () => parent.current,
    estimateSize: () => 32,
    overscan: 12,
  });
  const copyLines = () =>
    onCopy(
      lines
        .filter((_, i) => chosen.includes(i))
        .map((l) => `${hit?.path}:${l.number}: ${l.text}`)
        .join("\n"),
    );
  return (
    <section className="preview-panel" aria-label="Matching lines">
      <div className="panel-title preview-title">
        <span className="preview-path">
          {hit ? (
            <>
              <FileCode2 size={15} />
              {hit.relativePath}
            </>
          ) : (
            <>
              <span className="eyebrow">PREVIEW</span>
              <span className="quiet">Select a file to inspect matches</span>
            </>
          )}
        </span>
        {hit && (
          <div className="actions">
            <button
              className="icon-button"
              title="Copy file path"
              aria-label="Copy file path"
              onClick={async () => {
                if (await onCopy(hit.path)) {
                  setCopied(true);
                  setTimeout(() => setCopied(false), 2000);
                }
              }}
            >
              {copied ? <Check size={16} /> : <Copy size={16} />}
            </button>
            <button
              className="icon-button"
              title="Open containing folder"
              aria-label="Open containing folder"
              onClick={onFolder}
            >
              <FolderOpen size={16} />
            </button>
            <button
              className="small-button"
              onClick={() => onOpen(lines[chosen[0] || 0]?.number || 1)}
            >
              Open <ArrowUpRight size={14} />
            </button>
          </div>
        )}
      </div>
      {!hit ? (
        <div className="empty-preview">
          <div className="empty-symbol">
            <Search size={32} strokeWidth={1.2} />
          </div>
          <h2>A closer look at your code.</h2>
          <p>
            Search a project, choose a file,
            <br />
            and find the line that matters.
          </p>
          <div className="keyboard-hint">
            <kbd>Ctrl</kbd>
            <span>+</span>
            <kbd>L</kbd>
            <span>to focus search</span>
          </div>
        </div>
      ) : loading ? (
        <div className="empty-preview" role="status">
          Loading matching lines…
        </div>
      ) : error ? (
        <div className="error-inline" role="alert">
          {error}
        </div>
      ) : (
        <>
          <div className="preview-summary">
            <span>{lines.length.toLocaleString()} matching lines</span>
            <span>Double-click to open in editor</span>
          </div>
          <div
            className="code-scroll"
            ref={parent}
            tabIndex={0}
            aria-label="Code preview. Control C copies selected lines; F3 opens the editor."
            onKeyDown={(e) => {
              if (
                lines.length &&
                ["ArrowDown", "ArrowUp", "Home", "End"].includes(e.key)
              ) {
                e.preventDefault();
                const current = chosen.at(-1) ?? -1;
                const next =
                  e.key === "Home"
                    ? 0
                    : e.key === "End"
                      ? lines.length - 1
                      : Math.max(
                          0,
                          Math.min(
                            lines.length - 1,
                            current + (e.key === "ArrowDown" ? 1 : -1),
                          ),
                        );
                if (e.shiftKey) {
                  setChosen(
                    Array.from(
                      { length: Math.abs(next - anchor.current) + 1 },
                      (_, i) => i + Math.min(next, anchor.current),
                    ),
                  );
                } else {
                  setChosen([next]);
                  anchor.current = next;
                }
                virtual.scrollToIndex(next);
              }
              if ((e.ctrlKey || e.metaKey) && e.key === "c" && chosen.length) {
                e.preventDefault();
                void copyLines();
              }
              if (e.key === "F3") {
                e.preventDefault();
                onOpen(lines[chosen[0] || 0]?.number || 1);
              }
            }}
          >
            <div
              style={{
                height: virtual.getTotalSize(),
                position: "relative",
                minWidth: "100%",
              }}
            >
              {virtual.getVirtualItems().map((v) => {
                const l = lines[v.index];
                return (
                  <div
                    key={v.index}
                    className={`code-line ${chosen.includes(v.index) ? "chosen" : ""}`}
                    style={{ transform: `translateY(${v.start}px)` }}
                    onClick={(e) => {
                      if (e.shiftKey) {
                        setChosen(
                          Array.from(
                            { length: Math.abs(v.index - anchor.current) + 1 },
                            (_, i) => i + Math.min(v.index, anchor.current),
                          ),
                        );
                      } else if (e.ctrlKey || e.metaKey) {
                        setChosen((old) =>
                          old.includes(v.index)
                            ? old.filter((i) => i !== v.index)
                            : [...old, v.index],
                        );
                        anchor.current = v.index;
                      } else {
                        setChosen([v.index]);
                        anchor.current = v.index;
                      }
                    }}
                    onDoubleClick={() => onOpen(l.number)}
                  >
                    <span className="line-number">{l.number}</span>
                    <code>
                      <Highlight line={l} />
                    </code>
                  </div>
                );
              })}
            </div>
          </div>
          {preview?.truncated && (
            <p className="limit-note">
              Showing the first 10,000 matching lines. Narrow the search for
              more detail.
            </p>
          )}
          <div className="preview-bottom">
            <span>UTF-8 · Matching lines</span>
            <button
              className="text-button"
              disabled={!chosen.length}
              onClick={() => void copyLines()}
            >
              Copy selected lines {chosen.length > 0 && `(${chosen.length})`}
            </button>
          </div>
        </>
      )}
    </section>
  );
}
