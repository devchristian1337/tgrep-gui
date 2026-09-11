import { useRef, useState, type MouseEvent, type ReactNode } from "react";
import {
  ArrowUpRight,
  ClipboardPaste,
  Copy,
  FolderOpen,
  Redo2,
  Scissors,
  TextSelect,
  Undo2,
} from "lucide-react";
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuGroup,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuShortcut,
  ContextMenuTrigger,
} from "@/components/ui/context-menu";

type TextField = HTMLInputElement | HTMLTextAreaElement;

type MenuSpec =
  | { kind: "text"; field: TextField; hasSelection: boolean; editable: boolean }
  | { kind: "file"; path: string; relativePath: string }
  | { kind: "line"; path: string; line: number; text: string }
  | { kind: "selection"; text: string };

type BaseUIMouseEvent = MouseEvent<HTMLDivElement> & {
  preventBaseUIHandler?: () => void;
};

const TEXT_TYPES = new Set([
  "text",
  "search",
  "url",
  "email",
  "tel",
  "password",
]);

function textField(target: EventTarget | null): TextField | null {
  if (target instanceof HTMLTextAreaElement) return target;
  if (target instanceof HTMLInputElement && TEXT_TYPES.has(target.type))
    return target;
  return null;
}

function describe(
  target: EventTarget | null,
  previewPath: string,
): MenuSpec | null {
  if (!(target instanceof Element)) return null;
  const field = textField(target);
  if (field) {
    const start = field.selectionStart ?? 0;
    const end = field.selectionEnd ?? 0;
    return {
      kind: "text",
      field,
      hasSelection: end > start,
      editable: !field.readOnly && !field.disabled,
    };
  }
  const row = target.closest<HTMLElement>("[data-context-path]");
  if (row?.dataset.contextPath) {
    return {
      kind: "file",
      path: row.dataset.contextPath,
      relativePath: row.dataset.contextRelative ?? row.dataset.contextPath,
    };
  }
  const codeLine = target.closest<HTMLElement>("[data-context-line]");
  if (codeLine && previewPath) {
    return {
      kind: "line",
      path: previewPath,
      line: Number(codeLine.dataset.contextLine),
      text: codeLine.querySelector("code")?.textContent ?? "",
    };
  }
  const selection = window.getSelection()?.toString() ?? "";
  if (selection) return { kind: "selection", text: selection };
  return null;
}

export default function AppContextMenu({
  className,
  children,
  previewPath,
  canOpen,
  onOpen,
  onError,
}: {
  className?: string;
  children: ReactNode;
  previewPath: string;
  canOpen: boolean;
  onOpen: (path: string, line: number, containingFolder: boolean) => void;
  onError: (message: string) => void;
}) {
  const [spec, setSpec] = useState<MenuSpec | null>(null);
  const returnFocus = useRef<HTMLElement | null>(null);

  const onContextMenu = (event: BaseUIMouseEvent) => {
    const target = event.target;
    const inDialog =
      target instanceof Element && target.closest("dialog[open]") !== null;
    const next = inDialog ? null : describe(target, previewPath);
    if (!next) {
      // Nothing useful to offer here: keep Base UI closed and the native
      // menu suppressed. Inside the modal log dialog the popup would sit
      // under the top layer, so the event is left alone there.
      event.preventBaseUIHandler?.();
      if (!inDialog) event.preventDefault();
      return;
    }
    returnFocus.current = next.kind === "text" ? next.field : null;
    setSpec(next);
  };

  const copyText = (text: string) =>
    navigator.clipboard.writeText(text).catch((e) => onError(String(e)));
  const command = (field: TextField, name: string) => {
    field.focus();
    document.execCommand(name);
  };
  const paste = async (field: TextField) => {
    try {
      const text = await navigator.clipboard.readText();
      field.focus();
      document.execCommand("insertText", false, text);
    } catch (e) {
      onError(`Clipboard is not available: ${String(e)}`);
    }
  };

  return (
    <ContextMenu>
      <ContextMenuTrigger className={className} onContextMenu={onContextMenu}>
        {children}
      </ContextMenuTrigger>
      <ContextMenuContent
        aria-label="Context menu"
        finalFocus={() => returnFocus.current ?? false}
      >
        {spec?.kind === "text" && (
          <>
            <ContextMenuGroup>
              <ContextMenuItem
                disabled={!spec.editable}
                onClick={() => command(spec.field, "undo")}
              >
                <Undo2 />
                Undo
                <ContextMenuShortcut>Ctrl+Z</ContextMenuShortcut>
              </ContextMenuItem>
              <ContextMenuItem
                disabled={!spec.editable}
                onClick={() => command(spec.field, "redo")}
              >
                <Redo2 />
                Redo
                <ContextMenuShortcut>Ctrl+Y</ContextMenuShortcut>
              </ContextMenuItem>
            </ContextMenuGroup>
            <ContextMenuSeparator />
            <ContextMenuGroup>
              <ContextMenuItem
                disabled={!spec.hasSelection || !spec.editable}
                onClick={() => command(spec.field, "cut")}
              >
                <Scissors />
                Cut
                <ContextMenuShortcut>Ctrl+X</ContextMenuShortcut>
              </ContextMenuItem>
              <ContextMenuItem
                disabled={!spec.hasSelection}
                onClick={() =>
                  void copyText(
                    spec.field.value.slice(
                      spec.field.selectionStart ?? 0,
                      spec.field.selectionEnd ?? 0,
                    ),
                  )
                }
              >
                <Copy />
                Copy
                <ContextMenuShortcut>Ctrl+C</ContextMenuShortcut>
              </ContextMenuItem>
              <ContextMenuItem
                disabled={!spec.editable}
                onClick={() => void paste(spec.field)}
              >
                <ClipboardPaste />
                Paste
                <ContextMenuShortcut>Ctrl+V</ContextMenuShortcut>
              </ContextMenuItem>
            </ContextMenuGroup>
            <ContextMenuSeparator />
            <ContextMenuGroup>
              <ContextMenuItem
                disabled={!spec.field.value}
                onClick={() => {
                  spec.field.focus();
                  spec.field.select();
                }}
              >
                <TextSelect />
                Select all
                <ContextMenuShortcut>Ctrl+A</ContextMenuShortcut>
              </ContextMenuItem>
            </ContextMenuGroup>
          </>
        )}
        {spec?.kind === "file" && (
          <>
            <ContextMenuGroup>
              <ContextMenuItem
                disabled={!canOpen}
                onClick={() => onOpen(spec.path, 1, false)}
              >
                <ArrowUpRight />
                Open in editor
              </ContextMenuItem>
              <ContextMenuItem
                disabled={!canOpen}
                onClick={() => onOpen(spec.path, 1, true)}
              >
                <FolderOpen />
                Reveal in folder
              </ContextMenuItem>
            </ContextMenuGroup>
            <ContextMenuSeparator />
            <ContextMenuGroup>
              <ContextMenuItem onClick={() => void copyText(spec.path)}>
                <Copy />
                Copy path
              </ContextMenuItem>
              <ContextMenuItem onClick={() => void copyText(spec.relativePath)}>
                <Copy />
                Copy relative path
              </ContextMenuItem>
            </ContextMenuGroup>
          </>
        )}
        {spec?.kind === "line" && (
          <>
            <ContextMenuGroup>
              <ContextMenuItem
                disabled={!canOpen}
                onClick={() => onOpen(spec.path, spec.line, false)}
              >
                <ArrowUpRight />
                Open at line {spec.line}
              </ContextMenuItem>
            </ContextMenuGroup>
            <ContextMenuSeparator />
            <ContextMenuGroup>
              <ContextMenuItem onClick={() => void copyText(spec.text)}>
                <Copy />
                Copy line
              </ContextMenuItem>
              <ContextMenuItem onClick={() => void copyText(spec.path)}>
                <Copy />
                Copy path
              </ContextMenuItem>
            </ContextMenuGroup>
          </>
        )}
        {spec?.kind === "selection" && (
          <ContextMenuGroup>
            <ContextMenuItem onClick={() => void copyText(spec.text)}>
              <Copy />
              Copy
              <ContextMenuShortcut>Ctrl+C</ContextMenuShortcut>
            </ContextMenuItem>
          </ContextMenuGroup>
        )}
      </ContextMenuContent>
    </ContextMenu>
  );
}
