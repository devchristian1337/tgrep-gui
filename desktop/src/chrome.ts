const NON_TEXT_INPUT = new Set([
  "button",
  "checkbox",
  "radio",
  "range",
  "file",
  "submit",
  "reset",
  "color",
  "image",
  "hidden",
]);

function isTextEntry(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  if (target.isContentEditable) return true;
  if (target instanceof HTMLTextAreaElement) return true;
  if (target instanceof HTMLInputElement)
    return !NON_TEXT_INPUT.has(target.type);
  return false;
}

function isBrowserShortcut(event: KeyboardEvent): boolean {
  const key = event.key.toLowerCase();
  const command = event.ctrlKey || event.metaKey;
  if (command && (key === "r" || key === "p" || key === "u")) return true;
  if (key === "f5") return true;
  if (event.altKey && (event.key === "ArrowLeft" || event.key === "ArrowRight"))
    return true;
  return false;
}

export function suppressBrowserChrome(): void {
  window.addEventListener(
    "contextmenu",
    (event) => {
      if (!isTextEntry(event.target)) event.preventDefault();
    },
    true,
  );
  window.addEventListener(
    "keydown",
    (event) => {
      if (isBrowserShortcut(event)) event.preventDefault();
    },
    true,
  );
}
