import { test, expect, type Page } from "@playwright/test";

// This fixture exercises the React/IPC boundary. It does not replace native Rust tests.
async function mockDesktop(
  page: Page,
  updateReady = false,
  automaticUpdates = true,
) {
  await page.addInitScript(
    ({ updateReady, automaticUpdates }) => {
      const w = window as any;
      w.isTauri = true;
      let cancelled = false;
      let maximized = false;
      let currentSettings = {
        enginePath: "",
        indexPath: "",
        editorPath: "",
        editorArguments: '"$FILE"',
        theme: "light",
        accent: "cobalt",
        scale: 1,
        density: "comfortable",
        ignoreCase: true,
        literal: false,
        recentFolders: ["C:\\projects\\atlas"],
        autoUpdateEngine: automaticUpdates,
      };
      const callbacks = new Map();
      let seq = 0;
      w.calls = [];
      w.__TAURI_EVENT_PLUGIN_INTERNALS__ = { unregisterListener: () => {} };
      w.__TAURI_INTERNALS__ = {
        metadata: {
          currentWindow: { label: "main" },
          currentWebview: { label: "main" },
        },
        transformCallback: (cb: any) => {
          callbacks.set(++seq, cb);
          return seq;
        },
        unregisterCallback: (id: number) => callbacks.delete(id),
        invoke: async (cmd: string, args: any) => {
          w.calls.push({ cmd, args });
          if (cmd === "plugin:window|is_maximized") return maximized;
          if (cmd === "plugin:window|toggle_maximize") {
            maximized = !maximized;
            return;
          }
          if (cmd === "plugin:window|minimize" || cmd === "plugin:window|close")
            return;
          if (cmd === "plugin:event|listen") return ++seq;
          if (cmd === "plugin:event|unlisten") return;
          if (cmd === "load_settings") return currentSettings;
          if (cmd === "save_settings") {
            if (w.saveError) throw w.saveError;
            currentSettings = args.settings;
            return;
          }
          if (cmd === "get_prefill") return {};
          if (cmd === "engine_version") return "tgrep 1.0.5";
          if (cmd === "check_engine_update")
            return (
              w.updateResult || {
                message: updateReady
                  ? "tgrep 1.0.6 verified and ready. Restart the app to use it."
                  : "No newer Windows engine is available; keeping tgrep 1.0.5.",
                restartRequired: updateReady,
              }
            );
          if (cmd === "restart_app") {
            if (w.restartError) throw w.restartError;
            return;
          }
          if (cmd === "set_zoom") return;
          if (cmd === "set_theme") return;
          if (cmd === "plugin:dialog|open") return "C:\\projects\\atlas";
          if (cmd === "get_logs") return ["Fixture log: search completed"];
          if (cmd === "open_result") return;
          if (cmd === "cancel_search") {
            cancelled = true;
            return;
          }
          if (cmd === "restart_server")
            throw "This app does not own the server.";
          if (cmd === "search") {
            cancelled = false;
            const callback = callbacks.get(args.onEvent.id);
            if (args.options.pattern === "invalid[")
              throw "Invalid regular expression";
            callback({
              index: 0,
              message: { type: "progress", id: args.id, message: "Searching…" },
            });
            const hits =
              args.options.pattern === "absent"
                ? []
                : [
                    {
                      path: "C:\\projects\\atlas\\src\\search.rs",
                      relativePath: "src/search.rs",
                      count: 2,
                    },
                    {
                      path: "C:\\projects\\atlas\\src\\main.rs",
                      relativePath: "src/main.rs",
                      count: 1,
                    },
                  ];
            callback({
              index: 1,
              message: { type: "hits", id: args.id, hits },
            });
            if (args.options.pattern === "slow")
              await new Promise<void>((resolve) => {
                const timer = setInterval(() => {
                  if (cancelled) {
                    clearInterval(timer);
                    resolve();
                  }
                }, 20);
              });
            callback({ index: 2, end: true });
            return {
              cancelled,
              elapsedMs: 24,
              files: hits.length,
              matches: hits.length ? 3 : 0,
              truncated: false,
            };
          }
          if (cmd === "preview") {
            const slow = args.path.endsWith("search.rs");
            await new Promise((resolve) => setTimeout(resolve, slow ? 200 : 5));
            return {
              lines: w.previewLines || [
                {
                  number: slow ? 12 : 4,
                  text: slow
                    ? 'let greeting = "é😀hello";'
                    : "fn main() { search(); }",
                  spans: slow ? [[20, 25]] : [[12, 18]],
                  count: 1,
                },
              ],
              truncated: false,
            };
          }
          throw `Unexpected command: ${cmd}`;
        },
      };
    },
    { updateReady, automaticUpdates },
  );
}
test("empty preview remains reachable after reducing window height", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.goto("/");
  await expect(page.locator(".empty-preview h2")).toBeVisible();
  for (const height of [600, 480, 960]) {
    await page.setViewportSize({ width: 1280, height });
    for (const selector of [
      ".empty-symbol",
      ".empty-preview h2",
      ".empty-preview p",
      ".keyboard-hint",
    ]) {
      const content = page.locator(selector);
      await content.scrollIntoViewIfNeeded();
      const bounds = await content.boundingBox();
      const panel = await page.locator(".preview-panel").boundingBox();
      expect(bounds!.y).toBeGreaterThanOrEqual(panel!.y);
      expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(
        panel!.y + panel!.height + 1,
      );
    }
  }
});

for (const width of [1280, 768, 375]) {
  test(`sidebar can collapse and reopen at ${width}px`, async ({ page }) => {
    await mockDesktop(page);
    await page.setViewportSize({ width, height: 800 });
    await page.goto("/");
    const pattern = page.getByRole("textbox", { name: "Search pattern" });
    await pattern.fill("keep this query");
    const expandedMain = await page.locator("main").boundingBox();
    const toggle = page.getByRole("button", { name: "Collapse sidebar" });
    await toggle.focus();
    await page.keyboard.press("Enter");
    const expand = page.getByRole("button", { name: "Expand sidebar" });
    await expect(expand).toBeFocused();
    await expect(expand).toHaveAttribute("aria-expanded", "false");
    await expect(page.getByRole("navigation")).toHaveCount(0);
    await expect(pattern).toHaveValue("keep this query");
    await expect
      .poll(async () => (await page.locator("main").boundingBox())!.width)
      .toBe(width);
    const collapsedMain = await page.locator("main").boundingBox();
    expect(collapsedMain!.width).toBe(width);
    if (width > 600)
      expect(collapsedMain!.width).toBeGreaterThan(expandedMain!.width);
    await page.keyboard.press("Space");
    await expect(toggle).toHaveAttribute("aria-expanded", "true");
    await expect(pattern).toHaveValue("keep this query");
    await page.getByRole("button", { name: "Settings", exact: true }).click();
    await toggle.click();
    await expect(page.locator(".settings-view")).toBeVisible();
    await page.reload();
    await expect(expand).toHaveAttribute("aria-expanded", "false");
    await expand.click();
    await expect(page.getByRole("navigation")).toBeVisible();
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth),
    ).toBe(width);
  });
}

test("sidebar and accordion respect reduced motion and rapid toggles", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.goto("/");
  await page.getByRole("button", { name: "Collapse sidebar" }).click();
  await expect(page.locator(".sidebar-track")).toBeHidden();
  await expect(page.locator(".app-shell")).toHaveCSS(
    "transition-duration",
    "0s",
  );
  await page.getByRole("button", { name: "Expand sidebar" }).click();
  const settings = page.getByRole("button", { name: "Settings", exact: true });
  await settings.click();
  await expect(
    page.getByRole("button", { name: "Appearance", exact: true }),
  ).toHaveCount(0);
  await settings.click();
  await expect(page.locator(".accordion-chevron")).toHaveCSS(
    "transition-duration",
    "0s",
  );
  await page.emulateMedia({ reducedMotion: "no-preference" });
  await settings.focus();
  await page.keyboard.press("Enter");
  await page.keyboard.press("Enter");
  await expect(settings).toHaveAttribute("aria-expanded", "true");
  await page.getByRole("button", { name: "Appearance", exact: true }).click();
  await expect(page.locator("#settings-appearance")).toBeFocused();
});

test("interface labels cannot be selected while editable text and logs remain selectable", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.goto("/");
  const heading = page.getByRole("heading", { name: "Find your next line." });
  await heading.dblclick();
  expect(await page.evaluate(() => window.getSelection()?.toString())).toBe("");
  await page.keyboard.press("Control+a");
  expect(
    await page.evaluate(() => window.getSelection()?.toString()),
  ).not.toContain("Find your next line.");
  const input = page.getByRole("textbox", { name: "Search pattern" });
  await input.fill("editable query");
  await input.press("Control+a");
  expect(
    await input.evaluate((el: HTMLInputElement) =>
      el.value.slice(el.selectionStart!, el.selectionEnd!),
    ),
  ).toBe("editable query");
  await page.getByRole("button", { name: "Engine log", exact: true }).click();
  const logs = page.locator(".log-dialog pre");
  await logs.dblclick();
  expect(await page.evaluate(() => window.getSelection()?.toString())).not.toBe(
    "",
  );
});

test("settings accordion navigates to real sections and preserves drafts", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.goto("/");
  const menu = page.getByRole("button", { name: "Settings", exact: true });
  await expect(menu).toHaveAttribute("aria-expanded", "true");
  await menu.click();
  await expect(menu).toHaveAttribute("aria-expanded", "false");
  await expect(
    page.getByRole("button", { name: "Appearance", exact: true }),
  ).toHaveCount(0);
  await menu.focus();
  await page.keyboard.press("Enter");
  await page.getByRole("button", { name: "Shortcuts", exact: true }).click();
  await expect(page.locator("#settings-shortcuts")).toBeFocused();
  await page.getByRole("button", { name: "Appearance", exact: true }).click();
  await expect(page.locator("#settings-appearance")).toBeFocused();
  await page.getByRole("button", { name: "Dark", exact: true }).click();
  await page
    .getByRole("button", { name: "Search engine", exact: true })
    .click();
  await expect(page.locator("#settings-engine")).toBeFocused();
  await page.getByRole("button", { name: "Appearance", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "Dark", exact: true }),
  ).toHaveAttribute("aria-pressed", "true");
});

test("integrated titlebar controls the native window", async ({ page }) => {
  await mockDesktop(page);
  await page.goto("/");
  await expect(page.locator(".nav-key")).toHaveCount(0);
  await expect(page.locator(".titlebar-drag-space")).toHaveAttribute(
    "data-tauri-drag-region",
    "true",
  );
  await page
    .getByRole("button", { name: "Maximize window", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "Restore window", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Restore window", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "Maximize window", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Minimize window", exact: true })
    .click();
  const close = page.getByRole("button", { name: "Close window", exact: true });
  await close.hover();
  await expect(page.getByRole("tooltip")).toHaveText("Close");
  await close.click();
  const commands = await page.evaluate(() =>
    (window as any).calls.map((call: any) => call.cmd),
  );
  expect(
    commands.filter((cmd: string) => cmd === "plugin:window|toggle_maximize"),
  ).toHaveLength(2);
  expect(commands).toContain("plugin:window|minimize");
  expect(commands).toContain("plugin:window|close");
  await page.screenshot({ path: "test-results/integrated-titlebar.png" });
});

test("ready engine update exposes an app restart action", async ({ page }) => {
  await mockDesktop(page, true, false);
  await page.goto("/");
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "Restart app", exact: true }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "Update tgrep" }).click();
  await expect(
    page.getByText(
      "tgrep 1.0.6 verified and ready. Restart the app to use it.",
    ),
  ).toBeVisible();
  await page.getByRole("button", { name: "Dark", exact: true }).click();
  await page
    .getByRole("button", { name: "Restart app", exact: true })
    .click({ timeout: 3000 });
  await expect
    .poll(() =>
      page.evaluate(() =>
        (window as any).calls.some((c: any) => c.cmd === "restart_app"),
      ),
    )
    .toBe(true);
  expect(
    await page.evaluate(() =>
      (window as any).calls.some((c: any) => c.cmd === "restart_server"),
    ),
  ).toBe(false);
  const calls = await page.evaluate(() => (window as any).calls);
  const restartIndex = calls.findIndex((c: any) => c.cmd === "restart_app");
  const savedBeforeRestart = calls
    .slice(0, restartIndex)
    .filter((c: any) => c.cmd === "save_settings")
    .at(-1);
  expect(savedBeforeRestart?.args.settings.theme).toBe("dark");
});
test("automatic update keeps restart available across navigation and a failed recheck", async ({
  page,
}) => {
  await mockDesktop(page, true);
  await page.goto("/");
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  const restart = page.getByRole("button", {
    name: "Restart app",
    exact: true,
  });
  await expect(restart).toBeVisible();
  await page.keyboard.press("Control+l");
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(restart).toBeVisible();
  await page.evaluate(() => {
    (window as any).updateResult = {
      message: "Network unavailable",
      restartRequired: false,
    };
  });
  await page.getByRole("button", { name: "Update tgrep" }).click();
  await expect(page.getByText("Network unavailable")).toBeVisible();
  await expect(restart).toBeEnabled();
  await page.screenshot({ path: "test-results/settings-restart.png" });
  await page.setViewportSize({ width: 375, height: 1000 });
  await expect(restart).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  await page.screenshot({
    path: "test-results/settings-narrow.png",
    fullPage: true,
  });
});
test("restart preserves the draft when saving or restarting fails", async ({
  page,
}) => {
  await mockDesktop(page, true);
  await page.goto("/");
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await page.getByRole("button", { name: "Dark", exact: true }).click();
  const restart = page.getByRole("button", {
    name: "Restart app",
    exact: true,
  });
  await page.evaluate(() => {
    (window as any).saveError = "Could not save preferences";
  });
  await restart.click();
  await expect(page.getByRole("alert")).toContainText(
    "Could not save preferences",
  );
  expect(
    await page.evaluate(() =>
      (window as any).calls.some((c: any) => c.cmd === "restart_app"),
    ),
  ).toBe(false);
  await page.evaluate(() => {
    (window as any).saveError = "";
    (window as any).restartError = "Restart unavailable";
  });
  await restart.click();
  await expect(page.getByRole("alert")).toContainText("Restart unavailable");
  await expect(restart).toBeEnabled();
  await expect(
    page.getByRole("button", { name: "Dark", exact: true }),
  ).toHaveAttribute("aria-pressed", "true");
});
test("zoom shortcuts control the desktop webview and reset without changing preferences", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.goto("/");
  await page.getByLabel("Search pattern").fill("keep this query");
  for (const key of [
    "Control+Shift+Equal",
    "Control+Equal",
    "Control+Minus",
    "Control+0",
    "Control+NumpadAdd",
    "Control+NumpadSubtract",
    "Control+Numpad0",
  ])
    await page.keyboard.press(key);
  await expect
    .poll(() =>
      page.evaluate(() =>
        (window as any).calls
          .filter((c: any) => c.cmd === "set_zoom")
          .map((c: any) => c.args.factor),
      ),
    )
    .toEqual([1.1, 1.2, 1.1, 1, 1.1, 1, 1]);
  await expect(page.getByLabel("Search pattern")).toHaveValue(
    "keep this query",
  );
  await page.keyboard.press("Control+a");
  expect(
    await page
      .getByLabel("Search pattern")
      .evaluate(
        (el: HTMLInputElement) => el.selectionEnd! - el.selectionStart!,
      ),
  ).toBe(15);
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(page.getByText("Interface size", { exact: true })).toHaveCount(
    0,
  );
  await expect(page.locator("legend", { hasText: "Shortcuts" })).toBeVisible();
  await expect(page.locator(".shortcuts-list")).toContainText("Ctrl + A");
  await page.keyboard.press("Control+Equal");
  await page.getByRole("button", { name: "Save settings" }).click();
  const before = await page.evaluate(
    () => (window as any).calls.filter((c: any) => c.cmd === "set_zoom").length,
  );
  await page.keyboard.press("Control+0");
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          (window as any).calls.filter((c: any) => c.cmd === "set_zoom").length,
      ),
    )
    .toBe(before + 1);
  for (let i = 0; i < 20; i++) await page.keyboard.press("Control+Minus");
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          (window as any).calls.filter((c: any) => c.cmd === "set_zoom").at(-1)
            .args.factor,
      ),
    )
    .toBe(0.5);
  for (let i = 0; i < 25; i++) await page.keyboard.press("Control+Equal");
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          (window as any).calls.filter((c: any) => c.cmd === "set_zoom").at(-1)
            .args.factor,
      ),
    )
    .toBe(2);
});
test("select all copies matching lines beyond the virtualized viewport", async ({
  page,
  context,
}) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await mockDesktop(page);
  await page.goto("/");
  await page.evaluate(() => {
    (window as any).previewLines = Array.from({ length: 200 }, (_, i) => ({
      number: i + 1,
      text: `match ${i + 1}`,
      spans: [],
      count: 1,
    }));
  });
  await page.getByLabel("Search pattern").fill("match");
  await page
    .getByRole("main")
    .getByRole("button", { name: "Search", exact: true })
    .click();
  await page.getByRole("button", { name: /search.rs/ }).click();
  await page.locator(".code-line").first().click();
  await page.keyboard.press("Control+a");
  await expect(
    page.getByRole("button", { name: "Copy selected lines (200)" }),
  ).toBeEnabled();
  expect(await page.locator(".code-line").count()).toBeLessThan(200);
  await page.keyboard.press("Control+c");
  await expect
    .poll(() => page.evaluate(() => navigator.clipboard.readText()))
    .toContain("search.rs:200: match 200");
  expect(
    (await page.evaluate(() => navigator.clipboard.readText())).split("\n"),
  ).toHaveLength(200);
});
test("browser preview zoom scales controls and resets layout", async ({
  page,
}) => {
  await page.goto("/");
  const search = page
    .getByRole("main")
    .getByRole("button", { name: "Search", exact: true });
  const initial = (await search.boundingBox())!.height;
  await page.keyboard.press("Control+Equal");
  await expect
    .poll(async () => (await search.boundingBox())!.height)
    .toBeGreaterThan(initial);
  expect(
    Math.abs((await page.locator(".app-shell").boundingBox())!.height - 960),
  ).toBeLessThan(2);
  await page.keyboard.press("Control+0");
  await expect
    .poll(async () => (await search.boundingBox())!.height)
    .toBe(initial);
});
test("folder field focus rings the shell instead of the inner input", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto("/");
  const folder = page.getByLabel("Project folder");
  await folder.fill("C:\\Users\\Christian\\Documents\\Blackmagic Design");
  await folder.click();
  await expect(folder).toHaveCSS("outline-style", "none");
  await expect(folder).toHaveCSS("box-shadow", "none");
  const shell = page.locator(".project-control");
  await expect(shell).not.toHaveCSS("box-shadow", "none");
  await page.screenshot({ path: "test-results/workbench-folder-focus.png" });
});
test("right-click on the workbench replaces the browser context menu", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.goto("/");
  const onPage = await page.evaluate(
    () =>
      new Promise<boolean>((resolve) => {
        window.addEventListener(
          "contextmenu",
          (event) => resolve(event.defaultPrevented),
          { once: true },
        );
        document.querySelector("h1")?.dispatchEvent(
          new MouseEvent("contextmenu", {
            bubbles: true,
            cancelable: true,
          }),
        );
      }),
  );
  expect(onPage).toBe(true);
  const onQuery = await page.evaluate(() => {
    const input = document.querySelector(
      '[aria-label="Search pattern"]',
    ) as HTMLInputElement;
    const event = new MouseEvent("contextmenu", {
      bubbles: true,
      cancelable: true,
    });
    input.dispatchEvent(event);
    return event.defaultPrevented;
  });
  expect(onQuery).toBe(true);
  await expect(page.getByRole("menu")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("menu")).toHaveCount(0);
  const reloadBlocked = await page.evaluate(() => {
    const event = new KeyboardEvent("keydown", {
      key: "r",
      ctrlKey: true,
      cancelable: true,
      bubbles: true,
    });
    window.dispatchEvent(event);
    return event.defaultPrevented;
  });
  expect(reloadBlocked).toBe(true);
});
test("browser preview is honest and validates input", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByText("Interface preview ·")).toBeVisible();
  await page
    .getByRole("main")
    .getByRole("button", { name: "Search", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText(
    "Choose a project folder",
  );
  await page.getByRole("button", { name: "Filters", exact: true }).click();
  await expect(page.getByLabel("Include files")).toBeVisible();
  await expect(page.getByLabel("Ignore case")).toHaveCSS("cursor", "pointer");
  await expect(
    page.getByRole("main").getByRole("button", { name: "Search", exact: true }),
  ).toHaveCSS("cursor", "pointer");
});
test("Filters button hover is a complete control", async ({ page }) => {
  await page.goto("/");
  const filters = page.getByRole("button", { name: "Filters", exact: true });
  await expect(filters).toHaveCSS("appearance", "none");
  await expect(filters).toHaveCSS("overflow", "visible");
  const box = await filters.boundingBox();
  expect(box?.height).toBeGreaterThanOrEqual(36);
  await filters.hover();
  await page.screenshot({
    path: "test-results/filters-hover.png",
    clip: {
      x: Math.round(box!.x - 130),
      y: Math.round(box!.y - 70),
      width: 270,
      height: 150,
    },
  });
});
test("search options, streaming results, stale previews and editor requests", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.goto("/");
  await page.getByLabel("Search pattern").fill("hello");
  await page.getByRole("button", { name: "Filters", exact: true }).click();
  await page.getByLabel("Include files").fill("*.{rs,ts}");
  await page
    .getByRole("main")
    .getByRole("button", { name: "Search", exact: true })
    .click();
  await expect(page.locator(".results-toolbar")).toContainText(
    "3 matches in 2 files",
  );
  await page.getByRole("button", { name: /search.rs/ }).click();
  await page.getByRole("button", { name: /main.rs/ }).click();
  await expect(page.locator(".code-line")).toContainText("fn main()");
  await page.waitForTimeout(250);
  await expect(page.locator(".code-line")).toContainText("fn main()");
  await page.getByRole("button", { name: "Open", exact: true }).click();
  const calls = await page.evaluate(() => (window as any).calls);
  expect(calls.find((c: any) => c.cmd === "search").args.options.include).toBe(
    "*.{rs,ts}",
  );
  expect(calls.find((c: any) => c.cmd === "open_result").args.path).toContain(
    "main.rs",
  );
  await page.screenshot({ path: "test-results/workbench-results.png" });
});
test("cancellation retains partial results and errors recover", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.goto("/");
  await page.getByLabel("Search pattern").fill("slow");
  await page
    .getByRole("main")
    .getByRole("button", { name: "Search", exact: true })
    .click();
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.locator(".statusbar")).toContainText("Search cancelled");
  await expect(page.getByRole("button", { name: /main.rs/ })).toBeEnabled();
  await page.getByLabel("Search pattern").fill("invalid[");
  await page
    .getByRole("main")
    .getByRole("button", { name: "Search", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText(
    "Invalid regular expression",
  );
  await page.getByLabel("Search pattern").fill("absent");
  await page
    .getByRole("main")
    .getByRole("button", { name: "Search", exact: true })
    .click();
  await expect(page.locator(".statusbar")).toContainText("No matches");
});
test("settings can check for a tgrep engine update", async ({ page }) => {
  await mockDesktop(page);
  await page.goto("/");
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect(page.getByText("tgrep 1.0.5 · in use")).toBeVisible();
  const update = page.getByRole("button", { name: "Update tgrep" });
  await expect(update).toBeEnabled();
  await expect(page.getByLabel("Automatically update tgrep")).toBeChecked();
  await update.click();
  await expect(
    page.getByText(
      "No newer Windows engine is available; keeping tgrep 1.0.5.",
    ),
  ).toBeVisible();
});
test("shortcuts can be recorded, saved, and reset", async ({ page }) => {
  await mockDesktop(page);
  await page.goto("/");
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  const focus = page.getByRole("button", { name: "Focus search shortcut" });
  await focus.click();
  await expect(focus).toHaveText("Press keys…");
  await page.keyboard.press("Escape");
  await expect(focus).toContainText("Ctrl + L");
  await focus.click();
  await page.keyboard.press("Control+k");
  await expect(focus).toContainText("Ctrl + K");
  await page.getByRole("button", { name: "Zoom in shortcut" }).click();
  await page.keyboard.press("Control+k");
  await expect(page.getByRole("alert")).toContainText(
    "already used by Focus search",
  );
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: "Save settings" }).click();
  expect(
    await page.evaluate(
      () =>
        (window as any).calls
          .filter((c: any) => c.cmd === "save_settings")
          .at(-1).args.settings.shortcuts.focusSearch,
    ),
  ).toEqual({ key: "k", ctrl: true, shift: false, alt: false });
  await expect(
    page.getByRole("heading", { name: "Make it yours." }),
  ).toBeVisible();
  await page.keyboard.press("Control+l");
  await expect(
    page.getByRole("heading", { name: "Make it yours." }),
  ).toBeVisible();
  await page.keyboard.press("Control+k");
  await expect(page.getByLabel("Search pattern")).toBeFocused();
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await page.getByRole("button", { name: "Reset shortcuts" }).click();
  await expect(focus).toContainText("Ctrl + L");
  await page.getByRole("button", { name: "Save settings" }).click();
  await page.keyboard.press("Control+l");
  await expect(page.getByLabel("Search pattern")).toBeFocused();
});
test("White uses light native controls even when the OS is dark", async ({
  page,
}) => {
  await page.emulateMedia({ colorScheme: "dark" });
  await mockDesktop(page);
  await page.goto("/");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  await expect(page.locator("html")).toHaveCSS("color-scheme", "light");
  await expect(page.getByLabel("Literal text", { exact: true })).toHaveCSS(
    "color-scheme",
    "light",
  );
  await page.evaluate(() => {
    (window as any).previewLines = Array.from({ length: 100 }, (_, i) => ({
      number: i + 1,
      text: `match ${i + 1} ${"long preview line ".repeat(20)}`,
      spans: [],
      count: 1,
    }));
  });
  await page.getByLabel("Search pattern").fill("match");
  await page
    .getByRole("main")
    .getByRole("button", { name: "Search", exact: true })
    .click();
  await page.getByRole("button", { name: /search.rs/ }).click();
  await expect(page.locator(".code-line").first()).toBeVisible();
  await expect(page.locator(".code-scroll")).toHaveCSS("color-scheme", "light");
  await page.screenshot({ path: "test-results/white-dark-os.png" });
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await page.getByRole("button", { name: "Dark", exact: true }).click();
  await page.getByRole("button", { name: "Save settings" }).click();
  await expect(page.locator("html")).toHaveCSS("color-scheme", "dark");
});

test("RGB palette, density keyboard menu and native window theme follow saved settings", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.emulateMedia({ colorScheme: "dark" });
  await page.goto("/");
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          (window as any).calls.filter((c: any) => c.cmd === "set_theme").at(-1)
            ?.args.theme,
      ),
    )
    .toBe("light");
  await page.getByRole("button", { name: "Choose accent color" }).click();
  await page.getByLabel("Accent color", { exact: true }).fill("#3478ab");
  await page.getByLabel("Color format").selectOption("rgb");
  await expect(page.getByLabel("Accent R", { exact: true })).toHaveValue("52");
  await page.getByLabel("Accent R", { exact: true }).fill("120");
  await page.getByLabel("Color format").selectOption("hex");
  await expect(page.getByLabel("Accent color", { exact: true })).toHaveValue(
    "#7878AB",
  );
  await page.getByRole("button", { name: "Close color picker" }).click();
  const combo = page.getByRole("combobox", { name: "Result density" });
  await combo.click();
  await expect(page.getByRole("listbox")).toBeVisible();
  await page.screenshot({ path: "test-results/appearance-light.png" });
  await combo.press("ArrowDown");
  await combo.press("Enter");
  await expect(combo).toHaveText("Compact");
  await combo.click();
  await combo.press("Escape");
  await expect(page.getByRole("listbox")).toBeHidden();
  await page.getByRole("button", { name: "Dark", exact: true }).click();
  await page.getByRole("button", { name: "Save settings" }).click();
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          (window as any).calls.filter((c: any) => c.cmd === "set_theme").at(-1)
            ?.args.theme,
      ),
    )
    .toBe("dark");
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await page.getByRole("button", { name: "Choose accent color" }).click();
  await expect(page.getByLabel("Accent color", { exact: true })).toHaveValue(
    "#7878AB",
  );
  await page.getByRole("button", { name: "Close color picker" }).click();
  await expect(combo).toHaveText("Compact");
  await combo.click();
  await page.screenshot({ path: "test-results/appearance-dark.png" });
  await page.getByRole("option", { name: "Comfortable" }).click();
  await page.getByRole("button", { name: "System", exact: true }).click();
  await page.getByRole("button", { name: "Save settings" }).click();
  await page.emulateMedia({ colorScheme: "light" });
  await expect(page.locator("html")).toHaveAttribute("data-theme", "light");
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          (window as any).calls.filter((c: any) => c.cmd === "set_theme").at(-1)
            ?.args.theme,
      ),
    )
    .toBe("light");
});

test("settings persist theme and accent, shortcuts and log dialog work", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.goto("/");
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await page.getByRole("button", { name: "Dark", exact: true }).click();
  await page.getByRole("button", { name: "Choose accent color" }).click();
  await page.getByLabel("Accent color", { exact: true }).fill("#3478ab");
  await page.getByRole("button", { name: "Close color picker" }).click();
  await page.getByRole("button", { name: "Save settings" }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await expect(page.locator("html")).toHaveAttribute("data-accent", "#3478ab");
  await page.screenshot({ path: "test-results/workbench-settings.png" });
  await page.keyboard.press("Control+l");
  await expect(page.getByLabel("Search pattern")).toBeFocused();
  await page.getByRole("button", { name: "Engine log" }).click();
  await expect(page.getByRole("dialog")).toContainText("Fixture log");
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).not.toBeVisible();
});
test("the engine log dialog scales in and holds the top layer to close", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.goto("/");
  const dialog = page.getByRole("dialog");
  await page.getByRole("button", { name: "Engine log" }).click();
  await expect(dialog).toHaveClass(/is-open/);
  await expect(dialog).toHaveCSS("opacity", "1");
  await expect(dialog).toHaveCSS("transform", "matrix(1, 0, 0, 1, 0, 0)");
  // The scrim is a pseudo-element, so it needs a read of its own.
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          getComputedStyle(document.querySelector(".log-dialog")!, "::backdrop")
            .opacity,
      ),
    )
    .toBe("1");
  // Closing from inside the page: the click is synchronous, so this catches
  // the dialog mid-exit. It has to still hold the top layer, or the
  // scale-down is cut off the moment the button is pressed.
  const midExit = await page.evaluate(() => {
    const el = document.querySelector(".log-dialog") as HTMLDialogElement;
    el.querySelector<HTMLButtonElement>('[aria-label="Close log"]')!.click();
    return { open: el.open, closing: el.classList.contains("is-closing") };
  });
  expect(midExit).toEqual({ open: true, closing: true });
  await expect(dialog).not.toBeVisible();
  // A closed dialog leaves the a11y tree, so the cleanup is read off the node.
  await expect(page.locator(".log-dialog")).not.toHaveClass(/is-closing/);
});
test("the engine log copy button confirms a copy and reverts", async ({
  page,
  context,
}) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await mockDesktop(page);
  await page.goto("/");
  await page.getByRole("button", { name: "Engine log" }).click();
  await page.getByRole("button", { name: "Copy log" }).click();
  const copied = page.getByRole("button", { name: "Copied" });
  await expect(copied).toBeDisabled();
  await expect(copied.locator('[data-slot="copied-icon"]')).toHaveCSS(
    "opacity",
    "1",
  );
  await expect(copied.locator('[data-slot="copy-icon"]')).toHaveCSS(
    "opacity",
    "0",
  );
  // The app chrome is unlayered, so it outranks Tailwind by default: these
  // two prove the shadcn button styles itself instead of fading out at 0.45
  // opacity and shrinking to the rem-based 31.5px next to 36px controls.
  await expect(copied).toHaveCSS("opacity", "1");
  await expect(copied).toHaveCSS("min-height", "36px");
  expect(await page.evaluate(() => navigator.clipboard.readText())).toContain(
    "Fixture log: search completed",
  );
  await expect(page.getByRole("button", { name: "Copy log" })).toBeEnabled();
});
test("the status bar is never overlapped by the workbench", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1024, height: 567 });
  await page.goto("/");
  const bar = page.locator(".statusbar");
  await expect(bar).not.toHaveCSS("background-color", "rgba(0, 0, 0, 0)");
  const invaders = await page.evaluate(() => {
    const statusbar = document.querySelector(".statusbar")!;
    const strip = statusbar.getBoundingClientRect();
    // What the eye sees: every scrolling ancestor clips the element it holds.
    const clipped = (node: Element) => {
      const own = node.getBoundingClientRect();
      let [top, bottom] = [own.top, own.bottom];
      for (let p = node.parentElement; p; p = p.parentElement)
        if (getComputedStyle(p).overflow !== "visible") {
          const box = p.getBoundingClientRect();
          top = Math.max(top, box.top);
          bottom = Math.min(bottom, box.bottom);
        }
      return { top, bottom };
    };
    return [...document.querySelectorAll("main *")]
      .filter((el) => !statusbar.contains(el) && !el.contains(statusbar))
      .filter((el) => {
        const box = clipped(el);
        return box.bottom > strip.top + 1 && box.top < strip.bottom - 1;
      })
      .map((el) => String(el.className))
      .slice(0, 5);
  });
  expect(invaders).toEqual([]);
});
for (const height of [560, 900])
  test(`accent panel clears its trigger at ${height}px`, async ({ page }) => {
    await page.setViewportSize({ width: 1024, height });
    await page.goto("/");
    await page.getByRole("button", { name: "Settings", exact: true }).click();
    const trigger = page.getByRole("button", { name: "Choose accent color" });
    await trigger.click();
    const box = (await trigger.boundingBox())!;
    const panel = (await page
      .getByRole("dialog", { name: "Accent color picker" })
      .boundingBox())!;
    expect(
      panel.y + panel.height <= box.y || panel.y >= box.y + box.height,
      "the panel must sit fully above or below the trigger, never over it",
    ).toBe(true);
    expect(panel.y).toBeGreaterThanOrEqual(0);
    expect(panel.y + panel.height).toBeLessThanOrEqual(height);
    expect(
      await page
        .getByRole("dialog", { name: "Accent color picker" })
        .evaluate((el) => el.scrollHeight <= el.clientHeight + 1),
      "the panel must fit its content instead of hiding the presets",
    ).toBe(true);
  });
for (const width of [320, 375, 414, 768, 1440])
  test(`layout fits ${width}px in both themes`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.goto("/");
    await expect(page.locator("h1")).toBeVisible();
    for (const theme of ["light", "dark"]) {
      await page.evaluate(
        (theme) => (document.documentElement.dataset.theme = theme),
        theme,
      );
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= window.innerWidth,
        ),
      ).toBe(true);
    }
    if (width === 1440)
      await page.screenshot({ path: "test-results/workbench-empty.png" });
    await page.getByRole("button", { name: "Settings", exact: true }).click();
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= window.innerWidth,
      ),
    ).toBe(true);
  });

test("context menu adapts to text fields, result rows and preview lines", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.goto("/");
  await page.locator(".search-heading h1").click({ button: "right" });
  await expect(page.getByRole("menu")).toHaveCount(0);
  const pattern = page.getByLabel("Search pattern");
  await pattern.fill("hello");
  await pattern.click({ button: "right" });
  await expect(page.getByRole("menu")).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Paste" })).toBeVisible();
  await expect(page.getByRole("menuitem", { name: "Copy" })).toHaveAttribute(
    "aria-disabled",
    "true",
  );
  await page.getByRole("menuitem", { name: "Select all" }).click();
  await expect(page.getByRole("menu")).toHaveCount(0);
  await expect(pattern).toBeFocused();
  expect(
    await pattern.evaluate(
      (el: HTMLInputElement) => el.selectionEnd! - el.selectionStart!,
    ),
  ).toBe(5);
  await page
    .getByRole("main")
    .getByRole("button", { name: "Search", exact: true })
    .click();
  const row = page.getByRole("button", { name: /main.rs/ });
  await row.click({ button: "right" });
  await expect(page.getByRole("menuitem", { name: "Copy path" })).toBeVisible();
  await page.screenshot({ path: "test-results/context-menu.png" });
  await page.getByRole("menuitem", { name: "Open in editor" }).click();
  await row.click({ button: "right" });
  await page.getByRole("menuitem", { name: "Reveal in folder" }).click();
  await row.click();
  await expect(page.locator(".code-line").first()).toBeVisible();
  await page.locator(".code-line").first().click({ button: "right" });
  await expect(
    page.getByRole("menuitem", { name: /Open at line \d+/ }),
  ).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("menu")).toHaveCount(0);
  const opens = await page.evaluate(() =>
    (window as any).calls
      .filter((call: any) => call.cmd === "open_result")
      .map((call: any) => [call.args.path, call.args.containingFolder]),
  );
  expect(opens).toEqual([
    ["C:\\projects\\atlas\\src\\main.rs", false],
    ["C:\\projects\\atlas\\src\\main.rs", true],
  ]);
});
