# tgrep-gui

Windows desktop GUI for [Microsoft tgrep](https://github.com/microsoft/tgrep), written in **C# / .NET 10, WinUI 3, Windows App SDK 2.4.0 stable**. Search a folder, pick a matching file, and inspect highlighted lines in a Fluent interface. No WPF, WinForms, UWP, Electron, or webview dependency.

## Install

1. Download **tgrep-gui-win-x64.zip** or **tgrep-gui-win-arm64.zip** from [Releases](https://github.com/devchristian1337/tgrep-gui/releases).
2. Extract the zip.
3. Double-click `tgrep-gui.exe` inside the `tgrep-gui` folder.

Keep every extracted file next to the EXE; this is not a single-file build. .NET, Windows App SDK, and Microsoft tgrep 1.0.4 are included. Windows 10 64-bit (build 17763) or later is required. No installer, certificate, or Developer Mode is needed.

## Prerequisites

These apply when you build from source. The Release zip does not require the .NET SDK.

- Windows 11 recommended; Windows 10 build 17763 minimum. Mica is enabled where supported.
- **.NET 10 SDK** x64, or ARM64 to build on ARM. `dotnet --list-sdks` must list a 10.0 SDK, not only the runtime.
- Visual Studio 2026 with WinUI/.NET desktop development for the F5 experience. CLI builds work without Visual Studio: Windows SDK BuildTools is restored from NuGet.
- Windows App SDK **2.4.0 stable**, CommunityToolkit.Mvvm **8.4.2**, CommunityToolkit.WinUI.Controls.Sizers **8.2.251219**: all declared in the project and restored automatically. The Windows App SDK runtime is included in the output; a separate install is not required.
- Windows Developer Mode for MSIX development/distribution. The default **unpackaged** executable does not require an MSIX identity, certificates, or Developer Mode.
- [tgrep for Windows](https://github.com/microsoft/tgrep/releases): download `tgrep-v*-x86_64-pc-windows-msvc.zip`, or `tgrep-v*-aarch64-pc-windows-msvc.zip` on ARM64. Extract the package and select `tgrep.exe` in Settings. Integration tests were run against **1.0.4**.

The default theme is **System**: on dark Windows the app starts dark; Settings can force Light or Dark.

## Build and run

The sources are already complete: **do not run `dotnet new` over this folder**. From the repository root, run these PowerShell commands one at a time:

```powershell
dotnet restore .\tgrep-gui.csproj
```

```powershell
dotnet build .\tgrep-gui.csproj -c Debug --no-restore
```

```powershell
dotnet run --project .\tgrep-gui.csproj --no-build
```

The default output is `bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\tgrep-gui.exe`. You can launch it directly; keep every file in the output folder next to the EXE. `WindowsPackageType=None` and the first launch profile `Project` make **F5 and dotnet run unpackaged**. Open `tgrep-gui.csproj` in Visual Studio and choose x64 / Unpackaged profile.

This workspace may also contain a local .NET SDK under `.tools\dotnet`, which is excluded from Git. To use it in the current PowerShell session:

```powershell
$env:PATH = "$PWD\.tools\dotnet;$env:PATH"
```

Alternatively, the following script picks the local SDK automatically when present:

```powershell
.\scripts\Build.ps1 -Task Run
```

### Microsoft template, to create a separate project from scratch

The template is documented here to reproduce the starting point; it is not needed to build the delivered sources. The template package may be an alpha version even when the **app Windows App SDK is stable**.

```powershell
dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates --nuget-source https://api.nuget.org/v3/index.json
```

```powershell
dotnet new winui -n WinUiExample -o "$env:TEMP\WinUiExample"
```

### Self-contained EXE distribution

```powershell
.\scripts\Build.ps1 -Task Package
```

The script publishes x64 and ARM64, copies the matching `tgrep.exe` next to the app, and writes `artifacts\tgrep-gui-win-x64.zip` and `artifacts\tgrep-gui-win-arm64.zip`. Extract and run `tgrep-gui.exe`. Single-file and trimming are not used, so XAML and bindings stay reliable.

```powershell
dotnet publish .\tgrep-gui.csproj -p:PublishProfile=Unpackaged -r win-x64
```

For ARM64:

```powershell
dotnet publish .\tgrep-gui.csproj -p:PublishProfile=Unpackaged -p:Platform=ARM64 -r win-arm64
```

### Optional MSIX

The manifest, icons, and MSIX profile are included. The default distribution remains unpackaged.

```powershell
dotnet publish .\tgrep-gui.csproj -p:PublishProfile=MSIX -p:WindowsPackageType=MSIX -p:Platform=x64 -r win-x64
```

The profile produces an **unsigned** package in `artifacts\msix`. To install it outside the Store you must sign it with a trusted certificate whose subject matches the manifest Publisher (`CN=TgrepGui`), or use Visual Studio Package and Publish. The MSIX launch profile requires a build with `WindowsPackageType=MSIX`; it is not the default F5 profile. No certificate is created or installed automatically.

## Usage

1. Choose a folder with **Browse**, paste a path, or pick one of the last 12 folders.
2. Optional globs: `*.cs; *.xaml`, `*.{js,ts}`, or `"my files/**"`. Spaces, commas, and semicolons separate filters; quotes preserve spaces. Commas inside `{...}` or `[...]` are preserved.
3. To exclude directories use `bin/; obj/; node_modules/` (or `**/bin/**`); for files use `*.min.js`. Filters apply to the search and do not permanently shrink the index.
4. Enter a regex, or enable **Literal text**, then press **Search** or **Enter** in the search box.
5. Pick a file on the left. The right pane then loads that file’s highlighted lines (up to 10,000 matches per file); you can select text or multiple lines with Ctrl/Shift.

| Shortcut | Action |
|---|---|
| Enter, in the search box | Start search |
| Esc | Cancel search or index preparation |
| Ctrl+L | Show search and select the query text |
| F3 or double-click a line | Open in the editor at that line |
| Ctrl+C in the lines pane | Copy selected lines with path and line number |

A file context menu offers Open, Open containing folder, and Copy path. The **Log** pane shows the last 1,000 diagnostic lines; **Copy log** copies them to the clipboard. The app does not write logs to disk.

### Search, index, and branch changes

With **Use index** on, the app runs `tgrep status`. If there is no index it runs `index`, showing progress messages; then it starts `serve` and waits until the index is ready. An already running server is reused, including one started externally. The status bar reports PID, port, indexed files, match count, total time (including preparation), and warnings.

Later searches reuse the server. The tgrep watcher handles edits and branch changes: **the GUI does not rerun `index` on a branch change**. After a large change, wait for the watcher and search again. If results look stale, **Restart server**, then Search; restart does not rebuild an existing index. The app refuses to restart an external server and explains why.

Turning **Use index** off passes `--no-index` and neither builds an index nor starts a server. Any servers already started remain available. The status bar shows that the operation is not using the server.

**Cancel** stops the current search or `index` process and keeps partial results with an explicit message. A server already started stays up and is reused. If it is still busy with the initial index, a later search waits for it to finish; Esc cancels that wait. On app close, current operations are awaited and only child processes started by the app itself are terminated. Processes are never killed by name.

`--line-buffered` avoids CLI pipe buffering. The file list streams as JSON records arrive; tgrep may still compute some results internally before emitting them. Line text is loaded when a file is selected, at most 10,000 matches per file, with an explicit warning if the file has more.

### Settings and editor

The file is created on first launch at `%AppData%\tgrep-gui\settings.json` and written atomically. If it is unreadable the app reports that and does not overwrite it automatically when saving recents; Save settings lets you replace it explicitly.

```json
{
  "TgrepPath": "",
  "IndexPath": "",
  "EditorPath": "",
  "EditorArguments": "\"$FILE\"",
  "Theme": "System",
  "IgnoreCase": true,
  "Literal": false,
  "RecentFolders": []
}
```

The engine is resolved in this order: a valid configured path, the app folder, then PATH. A custom index path must be **absolute and dedicated to a single folder**. An empty field lets every command use `<folder>\.tgrep`. Environment variables in paths are supported. Executable/index changes apply to the next operation.

To open at the exact line, set the editor `.exe` path:

| Editor | Arguments |
|---|---|
| Visual Studio Code (`Code.exe`, not `code.cmd`) | `--goto "$FILE:$LINE"` |
| Notepad++ | `-n$LINE "$FILE"` |
| Sublime Text | `"$FILE:$LINE"` |

With no editor configured, the Windows file association is used; in that case the line number cannot be forced. The argument template is split **before** substituting `$FILE` and `$LINE`, so a name containing quotes or symbols cannot introduce new arguments.

### Command-line prefill

```powershell
dotnet run --project .\tgrep-gui.csproj --no-build -- --folder .\Core --include-files '*.cs' --exclude-files 'bin/;obj/' --text 'SearchAsync'
```

Aliases are `-f`, `-i`, `-e`, `-t`; `--text=value` is also accepted. Prefill does not start the search automatically. GUI CLI options are distinct from the engine CLI options.

## Tests

A suite with no external test dependencies, plus a helper executable for pipes, argv, and cancellation:

```powershell
dotnet run --project .\Tests\Tests.csproj
```

To include real tests against a downloaded release in this workspace:

```powershell
.\scripts\Build.ps1 -Task Test -TgrepPath '.\.tools\tgrep\tgrep.exe'
```

The suite uses unique temporary folders, never user repositories. It checks Unicode/base64 parsing, globs, CLI, settings, Windows escaping, cancellation, large stderr, and parser failure without deadlock. With tgrep it also checks first index, directory filter, no-index search, watcher, reuse, restart, and external server survival.

The project source is split into `Core` (UI-independent engine), `ViewModels`, `Views`, `Controls`; see [ARCHITECTURE.md](ARCHITECTURE.md). MSIX icons can be regenerated with `scripts\GenerateAssets.ps1`.

## Sources

- [Create a WinUI app with VS 2026 or dotnet new](https://learn.microsoft.com/en-us/windows/apps/get-started/start-here)
- [Windows App SDK 2.x stable release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0)
- [Microsoft tgrep: commands and architecture](https://github.com/microsoft/tgrep)
