# tgrep Studio — desktop app

Tauri 2 + Rust + React implementation of tgrep-gui. Product documentation, install, and usage are in the [repository README](../README.md).

## Run

Install Node.js 22+ and stable Rust. Windows also requires Visual Studio Build Tools with **Desktop development with C++** (MSVC x64/x86 and a Windows SDK), plus WebView2. See [Tauri prerequisites](https://v2.tauri.app/start/prerequisites/).

From the repository root:

```powershell
.\scripts\Build.ps1 -Task Dev
```

The script uses `.tools/rust` if present and ensures Microsoft tgrep 1.0.5 is in `.tools/tgrep`. Standard commands from this directory:

```sh
npm ci
npm run tauri -- dev
```

Release builds bundle `tgrep.exe` next to `tgrep-gui.exe`. Debug builds also look in `../.tools/tgrep/`. A configured path in Settings takes precedence, then PATH.

To inspect just the interface:

```sh
npm run dev
```

Open `http://127.0.0.1:1420`. Browser mode is labelled and does not search local files.

## Layout

- `src/` React workbench (`App.tsx`, `Results.tsx`, `Settings.tsx`)
- `tokens.css` / `src/styles.css` palette and layout
- `src-tauri/` Rust backend, Tauri config, bundled `binaries/tgrep.exe` (downloaded at package time, not committed)
- `tests/` Playwright browser tests with mock Tauri IPC

## Tests and package

```powershell
.\scripts\Build.ps1 -Task Test
.\scripts\Build.ps1 -Task Package
```

`Package` produces `artifacts/tgrep-gui-win-x64-setup.exe` and `artifacts/tgrep-gui-win-x64.zip`.
