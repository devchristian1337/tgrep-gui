export interface Keybind {
  key: string;
  ctrl: boolean;
  shift: boolean;
  alt: boolean;
}

export const shortcutActions = [
  { id: "zoomIn", label: "Zoom in" },
  { id: "zoomOut", label: "Zoom out" },
  { id: "zoomReset", label: "Reset zoom to 100%" },
  { id: "focusSearch", label: "Focus search" },
  {
    id: "selectAll",
    label: "Select all text / all matching lines in the focused preview",
  },
  { id: "copy", label: "Copy selected text / matching lines" },
  { id: "moveUp", label: "Previous file or matching line" },
  { id: "moveDown", label: "Next file or matching line" },
  { id: "moveFirst", label: "First file or matching line" },
  { id: "moveLast", label: "Last file or matching line" },
  { id: "extendUp", label: "Extend line selection up" },
  { id: "extendDown", label: "Extend line selection down" },
  { id: "openEditor", label: "Open selected line in editor" },
  { id: "cancel", label: "Cancel search / close log" },
] as const;

export type ShortcutId = (typeof shortcutActions)[number]["id"];
export type Shortcuts = Record<ShortcutId, Keybind>;

const bind = (
  key: string,
  mods: { ctrl?: boolean; shift?: boolean; alt?: boolean } = {},
): Keybind => ({
  key,
  ctrl: !!mods.ctrl,
  shift: !!mods.shift,
  alt: !!mods.alt,
});

export const defaultShortcuts: Shortcuts = {
  zoomIn: bind("+", { ctrl: true }),
  zoomOut: bind("-", { ctrl: true }),
  zoomReset: bind("0", { ctrl: true }),
  focusSearch: bind("l", { ctrl: true }),
  selectAll: bind("a", { ctrl: true }),
  copy: bind("c", { ctrl: true }),
  moveUp: bind("ArrowUp"),
  moveDown: bind("ArrowDown"),
  moveFirst: bind("Home"),
  moveLast: bind("End"),
  extendUp: bind("ArrowUp", { shift: true }),
  extendDown: bind("ArrowDown", { shift: true }),
  openEditor: bind("F3"),
  cancel: bind("Escape"),
};

const SHIFT_FLEX = new Set(["+", "=", "-", "_", "0"]);

function copyBind(value: Keybind): Keybind {
  return {
    key: value.key,
    ctrl: !!value.ctrl,
    shift: !!value.shift,
    alt: !!value.alt,
  };
}

export function mergeShortcuts(raw?: Partial<Shortcuts> | null): Shortcuts {
  const out = {} as Shortcuts;
  for (const { id } of shortcutActions) {
    const value = raw?.[id];
    out[id] =
      value && typeof value.key === "string" && value.key
        ? copyBind(value)
        : copyBind(defaultShortcuts[id]);
  }
  return out;
}

function canonicalKey(key: string): string {
  if (key === "+" || key === "=" || key === "Add") return "=";
  if (key === "_" || key === "Subtract") return "-";
  return key.length === 1 ? key.toLowerCase() : key;
}

export interface KeyEventLike {
  key: string;
  code: string;
  ctrlKey: boolean;
  metaKey: boolean;
  altKey: boolean;
  shiftKey: boolean;
}

function keyMatches(bindKey: string, event: KeyEventLike): boolean {
  const key = event.key;
  const code = event.code;
  if (bindKey === "+" || bindKey === "=") {
    return (
      key === "+" ||
      key === "=" ||
      key === "Add" ||
      code === "NumpadAdd" ||
      code === "Equal"
    );
  }
  if (bindKey === "-" || bindKey === "_") {
    return (
      key === "-" ||
      key === "_" ||
      key === "Subtract" ||
      code === "NumpadSubtract" ||
      code === "Minus"
    );
  }
  if (bindKey === "0") {
    return key === "0" || code === "Digit0" || code === "Numpad0";
  }
  if (bindKey.length === 1 && key.length === 1) {
    return bindKey.toLowerCase() === key.toLowerCase();
  }
  return key === bindKey || code === bindKey;
}

export function matches(
  value: Keybind,
  event: KeyEventLike,
  opts?: { ignoreShift?: boolean },
): boolean {
  if (!value?.key) return false;
  if (["Control", "Shift", "Alt", "Meta"].includes(event.key)) return false;
  if (!keyMatches(value.key, event)) return false;
  const ctrl = event.ctrlKey || event.metaKey;
  if (!!value.ctrl !== ctrl) return false;
  if (!!value.alt !== event.altKey) return false;
  const flex = SHIFT_FLEX.has(value.key);
  if (!opts?.ignoreShift && !flex && !!value.shift !== event.shiftKey)
    return false;
  return true;
}

export function bindsConflict(a: Keybind, b: Keybind): boolean {
  if (!!a.ctrl !== !!b.ctrl || !!a.alt !== !!b.alt) return false;
  if (canonicalKey(a.key) !== canonicalKey(b.key)) return false;
  if (SHIFT_FLEX.has(a.key) || SHIFT_FLEX.has(b.key)) return true;
  return !!a.shift === !!b.shift;
}

export function fromEvent(event: KeyboardEvent): Keybind | null {
  if (["Control", "Shift", "Alt", "Meta"].includes(event.key)) return null;
  return {
    key: event.key.length === 1 ? event.key.toLowerCase() : event.key,
    ctrl: event.ctrlKey || event.metaKey,
    shift: event.shiftKey,
    alt: event.altKey,
  };
}

export function isAllowed(value: Keybind): boolean {
  if (!value.key) return false;
  if (value.ctrl || value.alt) return true;
  if (/^F([1-9]|1[0-2])$/.test(value.key)) return true;
  return [
    "Escape",
    "Home",
    "End",
    "ArrowUp",
    "ArrowDown",
    "ArrowLeft",
    "ArrowRight",
    "Tab",
    "Enter",
    "Delete",
    "Backspace",
    "Insert",
    "PageUp",
    "PageDown",
    " ",
  ].includes(value.key);
}

function displayKey(key: string): string {
  const labels: Record<string, string> = {
    " ": "Space",
    ArrowUp: "↑",
    ArrowDown: "↓",
    ArrowLeft: "←",
    ArrowRight: "→",
    Escape: "Esc",
    Equal: "=",
    Minus: "-",
    Add: "+",
    Subtract: "-",
  };
  if (labels[key]) return labels[key];
  if (key.length === 1) return key.toUpperCase();
  return key;
}

export function formatParts(value: Keybind): string[] {
  const parts: string[] = [];
  if (value.ctrl) parts.push("Ctrl");
  if (value.alt) parts.push("Alt");
  if (value.shift) parts.push("Shift");
  parts.push(displayKey(value.key));
  return parts;
}

export function formatShortcut(value: Keybind): string {
  return formatParts(value).join(" + ");
}
