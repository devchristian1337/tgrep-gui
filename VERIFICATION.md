# Delivery verification

Run on Windows x64 on 8 September 2026.

| Check | Result |
|---|---|
| .NET SDK | 10.0.400, installed locally in `.tools/dotnet` |
| Windows App SDK | 2.4.0 stable, restored from NuGet |
| Debug unpackaged build | Passed, 0 errors and 0 warnings |
| `dotnet run`, default Project profile | App started correctly with no MSIX identity |
| Self-contained Release x64 publish | Passed; EXE launched directly from the publish folder |
| Optional x64 MSIX | Generated; unsigned and not installed |
| Core/process suite + real tgrep 1.0.4 | **36 checks passed** |
| GUI: search and results | Verified files, lines, highlighting after accents/emoji, counts, PID/port |
| GUI: settings | Verified navigation and correct layout containment in the window |
| GUI: Ctrl+L | Verified return from settings to the search box |
| GUI close | Verified normal exit and termination of the test child server |
| Log popup | Verified Copy log button immediately visible and stationary during vertical scroll; no horizontal scroll |
| Copy log after scrolling | Verified clipboard contains both the first index message and the last search message |

The suite also checks PID reuse across searches, watcher update after an edit, restart of the owned child only, external server survival, timeout/cancellation, large stderr pipes, malformed JSON without deadlock, Windows argv with spaces/quotes/backslashes, UTF-8/base64 parser, filters, and invalid regex. It uses temporary fixtures only.

The Windows tgrep release used for tests was compared with the SHA-256 digest published in the GitHub release API: `9b8d5488b1c342c10f222806de84a78049e8d8e8bdd35e34a0f872560c700b56`.

ARM64 configurations are included; no runtime test was run on ARM64 hardware. Signing and installing the MSIX package require the distributor certificate. Those steps are not required for the unpackaged EXE delivered here.
