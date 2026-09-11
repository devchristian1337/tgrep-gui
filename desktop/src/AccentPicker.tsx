import {
  useEffect,
  useLayoutEffect,
  useId,
  useRef,
  useState,
  type PointerEvent,
} from "react";
import { Check, ChevronDown, X } from "lucide-react";
import { accentHex } from "./appearance";
import "./accent-picker.css";

const presets = [
  ["Cobalt", "#0058ce"],
  ["Jade", "#006747"],
  ["Violet", "#7f56d9"],
  ["Indigo", "#4f46e5"],
  ["Pink", "#db2777"],
  ["Rose", "#af1040"],
  ["Orange", "#ea580c"],
  ["Amber", "#804600"],
];
const clamp = (n: number) => Math.max(0, Math.min(1, n));
function toHsv(hex: string, fallbackHue = 0) {
  const [r, g, b] = [1, 3, 5].map(
    (i) => parseInt(hex.slice(i, i + 2), 16) / 255,
  );
  const v = Math.max(r, g, b),
    delta = v - Math.min(r, g, b);
  const h = !delta
    ? fallbackHue
    : ((v === r
        ? (g - b) / delta
        : v === g
          ? (b - r) / delta + 2
          : (r - g) / delta + 4) *
        60 +
        360) %
      360;
  return { h, s: v ? delta / v : 0, v };
}
function toHex({ h, s, v }: ReturnType<typeof toHsv>) {
  const channel = (n: number) => {
    const k = (n + h / 60) % 6;
    return Math.round(255 * (v - v * s * Math.max(0, Math.min(k, 4 - k, 1))))
      .toString(16)
      .padStart(2, "0");
  };
  return `#${channel(5)}${channel(3)}${channel(1)}`;
}

export default function AccentPicker({
  value,
  onChange,
  disabled = false,
}: {
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
}) {
  const hex = accentHex(value);
  const id = useId();
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);
  const firstInput = useRef<HTMLInputElement>(null);
  const [open, setOpen] = useState(false);
  const [format, setFormat] = useState("hex");
  const [text, setText] = useState(hex.toUpperCase());
  const [hsv, setHsv] = useState(() => toHsv(hex));
  useLayoutEffect(() => {
    if (!open) return;
    const position = () => {
      if (!trigger.current || !panel.current) return;
      const anchor = trigger.current.getBoundingClientRect();
      const box = panel.current;
      const scrolled = box.scrollTop;
      box.style.maxHeight = "";
      box.classList.remove("tight");
      const natural = box.offsetHeight;
      const below = window.innerHeight - 36 - (anchor.bottom + 8);
      const above = anchor.top - 8 - 12;
      const flip = natural > below && above > below;
      const room = Math.max(flip ? above : below, 0);
      box.classList.toggle("tight", natural > room);
      box.style.maxHeight = `${room}px`;
      box.scrollTop = scrolled;
      box.style.left = `${Math.max(12, Math.min(anchor.right - box.offsetWidth, window.innerWidth - box.offsetWidth - 12))}px`;
      box.style.top = `${flip ? anchor.top - 8 - box.offsetHeight : anchor.bottom + 8}px`;
    };
    position();
    window.addEventListener("resize", position);
    window.addEventListener("scroll", position, true);
    return () => {
      window.removeEventListener("resize", position);
      window.removeEventListener("scroll", position, true);
    };
  }, [open]);
  useEffect(() => {
    setText(hex.toUpperCase());
    setHsv((current) =>
      toHex(current) === hex ? current : toHsv(hex, current.h),
    );
  }, [hex]);
  useEffect(() => {
    if (disabled) setOpen(false);
  }, [disabled]);
  useEffect(() => {
    if (!open) return;
    firstInput.current?.focus({ preventScroll: true });
    const outside = (event: globalThis.PointerEvent) => {
      if (!root.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("pointerdown", outside);
    return () => document.removeEventListener("pointerdown", outside);
  }, [open]);
  const update = (next: typeof hsv) => {
    setHsv(next);
    onChange(toHex(next));
  };
  const choose = (color: string) => {
    setHsv(toHsv(color, hsv.h));
    setText(color.toUpperCase());
    onChange(color);
  };
  const move = (event: PointerEvent<HTMLDivElement>) => {
    if (disabled) return;
    const bounds = event.currentTarget.getBoundingClientRect();
    update({
      ...hsv,
      s: clamp((event.clientX - bounds.left) / bounds.width),
      v: 1 - clamp((event.clientY - bounds.top) / bounds.height),
    });
  };
  const close = () => {
    setOpen(false);
    trigger.current?.focus();
  };
  return (
    <div
      className="accent-picker"
      ref={root}
      onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget)) setOpen(false);
      }}
      onKeyDown={(event) => {
        if (event.key === "Escape" && open) {
          event.preventDefault();
          event.stopPropagation();
          close();
        }
      }}
    >
      <button
        type="button"
        className="accent-trigger"
        ref={trigger}
        disabled={disabled}
        aria-label="Choose accent color"
        aria-expanded={open}
        aria-controls={id}
        aria-haspopup="dialog"
        onClick={() => setOpen(!open)}
      >
        <span className="accent-chip" style={{ background: hex }} />
        <span>{hex.toUpperCase()}</span>
        <ChevronDown size={15} />
      </button>
      {open && !disabled && (
        <div
          className="accent-panel"
          ref={panel}
          id={id}
          role="dialog"
          aria-label="Accent color picker"
        >
          <div className="accent-panel-heading">
            <span>Custom color</span>
            <button
              type="button"
              aria-label="Close color picker"
              onClick={close}
            >
              <X size={16} />
            </button>
          </div>
          <div
            className="accent-area"
            style={{ backgroundColor: `hsl(${hsv.h} 100% 50%)` }}
            onPointerDown={(event) => {
              if (event.button !== 0) return;
              event.preventDefault();
              event.currentTarget.setPointerCapture(event.pointerId);
              move(event);
            }}
            onPointerMove={(event) => {
              if (event.currentTarget.hasPointerCapture(event.pointerId))
                move(event);
            }}
            onPointerUp={(event) => {
              if (event.currentTarget.hasPointerCapture(event.pointerId))
                event.currentTarget.releasePointerCapture(event.pointerId);
            }}
          >
            <span
              className="accent-area-thumb"
              style={{
                left: `${hsv.s * 100}%`,
                top: `${(1 - hsv.v) * 100}%`,
                background: hex,
              }}
            />
            <input
              className="accent-sr-only"
              type="range"
              aria-label="Color saturation"
              min="0"
              max="100"
              step="1"
              value={Math.round(hsv.s * 100)}
              onChange={(e) =>
                update({ ...hsv, s: Number(e.target.value) / 100 })
              }
            />
            <input
              className="accent-sr-only"
              type="range"
              aria-label="Color brightness"
              min="0"
              max="100"
              step="1"
              value={Math.round(hsv.v * 100)}
              onChange={(e) =>
                update({ ...hsv, v: Number(e.target.value) / 100 })
              }
            />
          </div>
          <div className="accent-hue-row">
            <span className="accent-preview" style={{ background: hex }} />
            <input
              ref={firstInput}
              className="accent-hue"
              type="range"
              aria-label="Color hue"
              min="0"
              max="360"
              step="1"
              value={hsv.h}
              onChange={(e) => update({ ...hsv, h: Number(e.target.value) })}
            />
          </div>
          <div className="accent-value-row">
            <select
              aria-label="Color format"
              value={format}
              onChange={(e) => setFormat(e.target.value)}
            >
              <option value="hex">Hex</option>
              <option value="rgb">RGB</option>
            </select>
            {format === "hex" ? (
              <input
                aria-label="Accent color"
                spellCheck={false}
                maxLength={7}
                value={text}
                onChange={(e) => {
                  setText(e.target.value);
                  if (/^#?[\da-f]{6}$/i.test(e.target.value))
                    choose(`#${e.target.value.replace("#", "").toLowerCase()}`);
                }}
                onBlur={() => setText(hex.toUpperCase())}
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    e.preventDefault();
                    setText(hex.toUpperCase());
                  }
                }}
              />
            ) : (
              <div className="accent-rgb">
                {["R", "G", "B"].map((channel, index) => (
                  <input
                    key={channel}
                    type="number"
                    min="0"
                    max="255"
                    step="1"
                    aria-label={`Accent ${channel}`}
                    value={parseInt(
                      hex.slice(1 + index * 2, 3 + index * 2),
                      16,
                    )}
                    onChange={(e) => {
                      if (!e.target.value || !e.target.validity.valid) return;
                      const start = 1 + index * 2;
                      choose(
                        hex.slice(0, start) +
                          Number(e.target.value).toString(16).padStart(2, "0") +
                          hex.slice(start + 2),
                      );
                    }}
                  />
                ))}
              </div>
            )}
          </div>
          <div className="accent-palette">
            <span>Presets</span>
            <div className="accent-swatches">
              {presets.map(([name, color]) => (
                <button
                  type="button"
                  key={name}
                  aria-label={`${name} ${color}`}
                  aria-pressed={hex === color}
                  title={name}
                  onClick={() => choose(color)}
                  style={{ background: color }}
                >
                  {hex === color && <Check size={14} strokeWidth={3} />}
                </button>
              ))}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
