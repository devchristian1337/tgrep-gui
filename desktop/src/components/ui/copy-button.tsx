"use client";

import {
  useEffect,
  useRef,
  useState,
  type ComponentProps,
  type ReactNode,
} from "react";
import { CheckIcon, CopyIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

async function writeToClipboard(text: string) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch (e) {
    console.error("Failed to copy text: ", e);
    return false;
  }
}

export type CopyButtonProps = Omit<
  ComponentProps<typeof Button>,
  "children" | "value" | "onCopy"
> & {
  /** Text to copy, or a getter read at click time. */
  value: string | (() => string);
  label?: ReactNode;
  copiedLabel?: ReactNode;
  /** How long the copied state stays up, in ms. */
  resetAfter?: number;
  /** Icon size in px; match the label around it. */
  iconSize?: number;
  /** Overrides the clipboard write; return false to keep the idle state. */
  onCopy?: (text: string) => boolean | Promise<boolean>;
};

/* Copy button with a confirmed state: the clipboard icon crossfades into a
   check and the label swaps, while the button holds still — both icons share
   one grid cell, so the swap never shifts the text. Disabling it during the
   confirmation stops double copies; `disabled:opacity-100` keeps it legible
   while it is disabled. The transition is dropped by the global
   `prefers-reduced-motion` rule in `styles.css`. */
export function CopyButton({
  value,
  label = "Copy",
  copiedLabel = "Copied!",
  resetAfter = 1500,
  iconSize = 17,
  onCopy,
  className,
  variant = "outline",
  disabled,
  ...props
}: CopyButtonProps) {
  const [copied, setCopied] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(
    () => () => {
      if (timer.current) clearTimeout(timer.current);
    },
    [],
  );

  const handleCopy = async () => {
    const text = typeof value === "function" ? value() : value;
    const done = await (onCopy ? onCopy(text) : writeToClipboard(text));
    if (!done) return;
    setCopied(true);
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => setCopied(false), resetAfter);
  };

  return (
    <Button
      variant={variant}
      className={cn("disabled:opacity-100", className)}
      onClick={() => void handleCopy()}
      disabled={copied || disabled}
      {...props}
    >
      <span className="grid shrink-0 place-items-center">
        <span
          data-slot="copied-icon"
          className={cn(
            "col-start-1 row-start-1 transition-all",
            copied ? "scale-100 opacity-100" : "scale-0 opacity-0",
          )}
        >
          <CheckIcon
            className="stroke-[var(--color-success)]"
            size={iconSize}
            aria-hidden
          />
        </span>
        <span
          data-slot="copy-icon"
          className={cn(
            "col-start-1 row-start-1 transition-all",
            copied ? "scale-0 opacity-0" : "scale-100 opacity-100",
          )}
        >
          <CopyIcon size={iconSize} aria-hidden />
        </span>
      </span>
      <span aria-live="polite">{copied ? copiedLabel : label}</span>
    </Button>
  );
}

export default CopyButton;
