# Performance checks

Measured locally on Windows on 2026-09-13, before and after the performance changes.
These are focused synthetic measurements, not end-to-end search or startup times.
The Rust measurements use the unoptimized test profile on both sides; do not
extrapolate their speedups to the release application or Microsoft tgrep itself.

| Operation | Before | After |
| --- | ---: | ---: |
| Clone search context with 100,000 allowed paths (100 iterations) | 9.948 ms | 0.001 ms |
| Parse a Unicode line with 10,000 matches (5 iterations) | 9,056.098 ms | 15.257 ms |
| Prepare clipboard text for 10,000 selected lines (median of 5, Chromium) | 4.2 ms | 0.6 ms |

The context shares an immutable allowed-path set instead of copying every path
when opening a preview or restarting a server. Unicode offset conversion advances
through ordered matches instead of recounting each prefix. Unordered and
overlapping spans retain their previous behavior. Preview selection uses a Set
for membership checks, keeping full-selection copying linear.

The file list also skips filtering when the filter is empty and skips parent-only
renders when its inputs are unchanged. Selected-file lookup uses the existing
result map. Recent-folder saves no longer reapply the native theme unnecessarily.

## Reproduce

Use the repository's Rust toolchain under `.tools/rust` as configured by
`scripts/Build.ps1`. From the repository root:

```powershell
cargo test --manifest-path desktop/src-tauri/Cargo.toml benchmark_ -- --ignored --nocapture --test-threads=1
```

From `desktop`:

```powershell
npm test -- -g 'large results stay' --workers=1
```

The browser test uses simulated IPC with 100,000 files and 10,000 preview lines.
It checks bounded DOM rows, case-insensitive filtering, and complete clipboard
output. Clipboard preparation timing excludes the operating system's actual
clipboard write. Timing values are diagnostic, not flaky CI thresholds.

The two Rust microbenchmarks are ignored by default; regular correctness tests
cover Unicode boundaries, invalid and overlapping spans, result authorization,
real-engine search/preview and process shutdown. Run the complete suites after
changing either path.
