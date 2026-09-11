import { test, expect, type Page } from "@playwright/test";

// This fixture exercises the React/IPC boundary. It does not replace native Rust tests.
async function mockDesktop(page: Page) {
  await page.addInitScript(() => {
    const w = window as any;
    w.isTauri = true;
    let cancelled = false;
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
      autoUpdateEngine: true,
    };
    const callbacks = new Map();
    let seq = 0;
    w.calls = [];
    w.__TAURI_INTERNALS__ = {
      transformCallback: (cb: any) => {
        callbacks.set(++seq, cb);
        return seq;
      },
      unregisterCallback: (id: number) => callbacks.delete(id),
      invoke: async (cmd: string, args: any) => {
        w.calls.push({ cmd, args });
        if (cmd === "load_settings") return currentSettings;
        if (cmd === "save_settings") {
          currentSettings = args.settings;
          return;
        }
        if (cmd === "get_prefill") return {};
        if (cmd === "engine_version") return "tgrep 1.0.5";
        if (cmd === "check_engine_update")
          return "No newer Windows engine is available; keeping tgrep 1.0.5.";
        if (cmd === "plugin:dialog|open") return "C:\\projects\\atlas";
        if (cmd === "get_logs") return ["Fixture log: search completed"];
        if (cmd === "open_result") return;
        if (cmd === "cancel_search") {
          cancelled = true;
          return;
        }
        if (cmd === "restart_server") throw "This app does not own the server.";
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
          callback({ index: 1, message: { type: "hits", id: args.id, hits } });
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
            lines: [
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
  });
}
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
test("right-click on the workbench does not keep the browser context menu", async ({
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
  expect(onQuery).toBe(false);
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
  await page.getByRole("button", { name: "Search", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText(
    "Choose a project folder",
  );
  await page.getByRole("button", { name: "Filters", exact: true }).click();
  await expect(page.getByLabel("Include files")).toBeVisible();
  await expect(page.getByLabel("Ignore case")).toHaveCSS("cursor", "pointer");
  await expect(
    page.getByRole("button", { name: "Search", exact: true }),
  ).toHaveCSS("cursor", "pointer");
});
test("search options, streaming results, stale previews and editor requests", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.goto("/");
  await page.getByLabel("Search pattern").fill("hello");
  await page.getByRole("button", { name: "Filters", exact: true }).click();
  await page.getByLabel("Include files").fill("*.{rs,ts}");
  await page.getByRole("button", { name: "Search", exact: true }).click();
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
  await page.getByRole("button", { name: "Search", exact: true }).click();
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.locator(".statusbar")).toContainText("Search cancelled");
  await expect(page.getByRole("button", { name: /main.rs/ })).toBeEnabled();
  await page.getByLabel("Search pattern").fill("invalid[");
  await page.getByRole("button", { name: "Search", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText(
    "Invalid regular expression",
  );
  await page.getByLabel("Search pattern").fill("absent");
  await page.getByRole("button", { name: "Search", exact: true }).click();
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
    page.getByText("No newer Windows engine is available; keeping tgrep 1.0.5."),
  ).toBeVisible();
});
test("settings persist theme and accent, shortcuts and log dialog work", async ({
  page,
}) => {
  await mockDesktop(page);
  await page.goto("/");
  await page.getByRole("button", { name: "Settings", exact: true }).click();
  await page.getByRole("button", { name: "Dark", exact: true }).click();
  await page.getByRole("button", { name: "jade accent" }).click();
  await page.getByRole("button", { name: "Save settings" }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await expect(page.locator("html")).toHaveAttribute("data-accent", "jade");
  await page.screenshot({ path: "test-results/workbench-settings.png" });
  await page.keyboard.press("Control+l");
  await expect(page.getByLabel("Search pattern")).toBeFocused();
  await page.getByRole("button", { name: "Engine log" }).click();
  await expect(page.getByRole("dialog")).toContainText("Fixture log");
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).not.toBeVisible();
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
