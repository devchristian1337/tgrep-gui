# UI components

This Vite app uses React, TypeScript, Tailwind CSS 4 and Base UI primitives with shadcn-compatible configuration in `components.json`.

- `src/components/ui`: reusable UI components, imported through `@/components/ui`. Keeping primitives here makes the supplied examples and the shadcn CLI resolve to the same location.
- `src/components/ui/settings-sidebar-accordion.tsx`: the supplied pattern, with optional section data and actions for the app's real navigation.
- `src/components/ui/context-menu.tsx`: the shadcn Base UI context menu, with its colour utilities mapped to the app tokens (`bg-popover`, `bg-menu-hover`, `text-destructive` in the `@theme inline` block) and open/close motion driven by Base UI's `data-starting-style` / `data-ending-style` attributes in `styles.css`.
- `src/components/ui/button.tsx`: the shadcn Base UI button (`base-nova`), with its variants mapped to the app tokens and its sizes written in px, so it matches the 36px chrome of the other controls at the 14px root font. `styles.css` excludes `[data-slot="button"]` from the global `button` rules through `:where()`, which costs no specificity: those rules are unlayered and would otherwise outrank every Tailwind utility. Type, cursor and the focus outline stay shared with the rest of the app.
- `src/components/ui/copy-button.tsx`: a copy button that confirms the copy, used for Copy log in the engine log dialog. The clipboard icon crossfades into a check and the label swaps for `resetAfter` ms, both icons sharing one grid cell so the text never shifts. It takes `value` (a string or a getter read at click time), `label`, `copiedLabel`, `iconSize`, and an `onCopy` returning whether the write succeeded - the app hands it its own clipboard helper, so a failure lands in the error banner and the button stays idle.
- `src/components/copy-button-demo.tsx`: the supplied copy button preview; it is not mounted in the desktop app.
- `src/components/app-context-menu.tsx`: the application menu. It wraps the whole window and decides its items from the right-clicked target: text fields get Undo/Redo/Cut/Copy/Paste/Select all, result rows (`data-context-path`) get open, reveal and copy-path actions, preview lines (`data-context-line`) get open-at-line and copy-line. Elsewhere the native menu stays suppressed by `chrome.ts`.
- `src/components/ui/beui-tooltip.tsx`: the beui tooltip (motion/react spring with blur), used in place of native `title` tooltips on icon-only controls: window controls, sidebar toggle, preview actions, restart server, the Update tgrep button and the accent presets. It positions itself with absolute CSS, so it is not used inside clipped scroll areas (recent projects, result rows), which keep their `title`.
- `src/components/tooltip-demo.tsx`: the supplied tooltip preview; it is not mounted in the desktop app.
- `src/components/demo.tsx`: the standalone Account/Security/Notifications/Billing example; it is not mounted in the desktop app.
- `src/styles.css`: global styles, Tailwind imports and accordion styles. Tailwind Preflight is intentionally omitted to preserve existing controls. Colors use the app's theme tokens.
- `src/lib/utils.ts`: shared `cn` helper.

From `desktop`, run `npm ci`, `npm run dev`, `npm run build` and `npm test`. No additional setup is required. For future components, use `npx shadcn@latest docs <component>` and `npx shadcn@latest add <component>`; review generated styles against the app's existing CSS.

The accordion adapts the [shadcn Base UI component](https://ui.shadcn.com/docs/components/base/accordion); the context menu adapts [its Base UI counterpart](https://ui.shadcn.com/docs/components/base/context-menu), and the button [its own](https://ui.shadcn.com/docs/components/base/button), which is what `class-variance-authority` is for. Tailwind is connected through its [Vite plugin](https://tailwindcss.com/docs/installation/using-vite). No images or provider wrappers are needed.
