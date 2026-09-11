const legacy: Record<string, string> = {
  cobalt: "#0058ce",
  jade: "#006747",
  amber: "#804600",
  rose: "#af1040",
};

export function accentHex(value: string): string {
  return /^#[\da-f]{6}$/i.test(value)
    ? value.toLowerCase()
    : legacy[value] || legacy.cobalt;
}

export function applyAccent(root: HTMLElement, value: string) {
  const hex = accentHex(value);
  const rgb = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255);
  const linear = rgb.map((c) =>
    c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4,
  );
  const luminance =
    linear[0] * 0.2126 + linear[1] * 0.7152 + linear[2] * 0.0722;
  root.style.setProperty("--color-accent", hex);
  root.style.setProperty(
    "--color-accent-ink",
    luminance > 0.179 ? "#111111" : "#ffffff",
  );
  root.style.setProperty(
    "--color-selection",
    `color-mix(in srgb, ${hex} 14%, var(--color-surface))`,
  );
  root.style.setProperty(
    "--color-highlight",
    `color-mix(in srgb, ${hex} 30%, var(--color-surface))`,
  );
}
