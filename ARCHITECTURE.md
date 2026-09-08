# Architecture

## Boundaries

`App.xaml` loads Fluent resources; `MainWindow` hosts `NavigationView`, search, and `SettingsPage`. `MicaBackdrop` is enabled only when supported. `GridSplitter` is the CommunityToolkit.WinUI control; `ObservableObject`, `RelayCommand`, and `AsyncRelayCommand` come from CommunityToolkit.Mvvm.

`MainViewModel` owns UI state, commands, per-file grouping, counts, and settings. Core `TgrepGui.Core` is a .NET 10 library with no WinUI references and is tested separately. `TgrepClient` coordinates lifecycle; `ProcessRunner` owns transient processes; `TgrepJsonParser` converts the protocol; `Arguments` builds argv; `EditorLauncher` opens the result.

## Argv

Every argument goes through `ProcessStartInfo.ArgumentList`: the runtime performs Windows quoting. No command string is concatenated, no shell is used, and paths are not interpolated in PowerShell/cmd.

| UI | tgrep argument |
|---|---|
| Query | `-- <pattern> <root>` after all options |
| Include | One `-g <glob>` per filter |
| Exclude | One `-g !<glob>` per filter |
| Directory `bin/` | `-g !bin/**` |
| Ignore case | `-i` |
| Literal text | `-F` |
| Whole word | `-w` |
| Use index off | `--no-index` |
| Non-empty IndexPath | `--index-path <absolute-directory>` on **every** command |
| Always on search | `--json --line-buffered --color never -n` |

Negative globs are used for excluded directories instead of changing `index`/`serve` with `--exclude`. Changing a UI filter therefore does not leave a permanently incomplete index for the next search. Include filters come first; exclusions take precedence per tgrep/ripgrep semantics. Standard tgrep ignore files remain active.

GUI CLI options (`--folder/-f`, `--include-files/-i`, `--exclude-files/-e`, `--text/-t`) prefill the form and are not forwarded directly to tgrep.

## Serve lifecycle

```mermaid
flowchart TD
    A[Search] --> B{Use index?}
    B -- no --> S[search --no-index --json]
    B -- yes --> C[Normalize root and validate IndexPath]
    C --> D[tgrep status root]
    D --> E{Server reachable?}
    E -- yes --> H[Wait for Indexing complete]
    E -- no --> F{Index present?}
    F -- no --> G[tgrep index root]
    G --> I[Start owned serve child]
    F -- yes --> I
    I --> H
    H --> J[search --json]
    J --> K[Keep serve for later searches]
```

- Roots are normalized with `Path.GetFullPath` and, on Windows, `GetFinalPathNameByHandleW`: junctions, symlinks, device prefixes, trailing slashes, and case do not multiply servers for the same folder.
- A dictionary of **app-owned** servers per root is protected by a semaphore. Each entry keeps the `Process` object, executable, index path, and stdout/stderr drain tasks.
- A `.tgrep` directory alone does not mean an index exists: `status` is parsed. That command can exit 0 with “No index found”. The response distinguishes on-disk index, server, and in-progress indexing. Unknown formats or timeouts become visible errors, not an invitation to overwrite the index.
- `status` queries time out after 5 seconds. Startup with no response times out after 30 seconds; waiting for indexing that is actually in progress is cancellable and does not impose an arbitrary limit for large repositories.
- If `meta.json` exists, `root_path` must match the root. A custom index cannot be shared across projects. The index directory cannot be the root itself.
- An external server is used but not added to the owned-process dictionary. The PID reported by `status` is diagnostic: **it is never used to decide which process to terminate**.
- `RestartAsync` stops only the owned `Process` for the current root, then starts/waits for the server. If it finds an external server it reports that the owner must stop it. It does not rebuild an existing index.
- If two GUI instances try to start together, tgrep’s `serve.lock` arbitrates exclusivity. If the process just started exits but `status` finds another ready server, the app reuses the latter.
- Changing index path or executable retires the old owned server for the same root before the new start. Servers for other roots are kept until Exit for reuse.
- The watcher belongs to tgrep. No Git watcher or `index` command is tied to a branch change in the GUI.

## Processes, cancellation, and shutdown

tgrep processes use `UseShellExecute=false`, `CreateNoWindow=true`, redirected stdout/stderr, and UTF-8. Both streams are read concurrently. Per-process diagnostic capture is capped at 32 KiB. A parser failure stops the producer immediately: the first completed/failed task among stdout, stderr, and exit is awaited, avoiding deadlock when the process keeps writing.

Cancellation stops only the owned transient process (`search`, `status`, `index`) and waits for it to exit. It does not stop external servers or a reusable owned server. Results already shown remain, with a “partial results” message. A server still indexing may finish in the background.

`AppWindow.Closing` defers close, cancels and awaits the UI operation, then runs `DisposeAsync` on owned servers. Only then does it close the window. `Process` handles are used; never `Stop-Process -Name tgrep`, global enumerations, or PIDs reconstructed from files.

A forced GUI process exit from Task Manager or a system crash does not run the async shutdown path: a child server may stay alive and will be reused as external on the next launch. No Windows service is installed.

## Streaming and UI thread

`SearchAsync` runs on a .NET worker. The parser reads one JSON object per line; `begin`/`end` events are recognized, while only `match` creates results. `summary` and `context` do not produce match rows. `text` and base64 `bytes` fields are supported. Relative paths are resolved against the root used by the process.

`submatches.start/end` offsets are UTF-8 bytes; the parser converts them to UTF-16 indexes for `TextHighlighter`. Only trailing line terminators are stripped; leading spaces and indentation are kept. The counter sums occurrences, not just lines.

A `Channel<SearchMatch>` bounded to 1,024 items applies backpressure. The consumer adds up to 128 results per batch, then yields the thread for input/render. Observable collections are mutated only on the UI thread. ListViews virtualize items; collected results are kept in memory with no silent truncation.

A 100 ms UI timer reads progress, last server status, and the log queue. The last 1,000 lines are kept in the flyout and at most 2,000 lines are queued. Strings containing `warning`, including `warning: no index`, or `scanning every file` feed the InfoBar and warning count. JSON errors and exit codes other than 0/1 are visible; exit 1 is “no matches”. Indexed counts are updated during preparation and at the end of a search; they are not a continuous idle monitor.

## Persistence and file opening

`SettingsStore` uses `%AppData%\tgrep-gui\settings.json`, with a semaphore and atomic replacement via a temporary file. At most 12 roots are remembered; queries, contents, and logs are not saved. Settings are snapshotted when a search starts, so form changes do not alter argv already running.

`EditorLauncher` splits the template with `CommandLineToArgvW`, then substitutes tokens and uses `ArgumentList`. The editor is an interactive process, with no stdout/stderr capture; if none is configured the Windows file association is used. Explorer is started explicitly to select the file. The clipboard uses `DataPackage`.

## Build and tests

The application project explicitly excludes `Core`, `Tests`, `.tools`, and `artifacts` from automatic source inclusion. The core library is referenced as a project. The default is .NET framework-dependent with the Windows App SDK runtime included; Unpackaged publish also includes .NET. The MSIX profile is optional and does not sign or install certificates.

`Tests/Program.cs` checks argv, Unicode, base64, status, settings, pipes, and cancellation. `FakeTgrep` reproduces large stderr, waits, and malformed JSON. When a real tgrep path is passed, the suite creates temporary repositories and exercises the full cycle, including external server survival. No test uses existing project data or indexes.
