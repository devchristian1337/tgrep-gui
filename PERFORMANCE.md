# Performance verification — 2026-09-09

Measured locally on the same Windows host and .NET 10 runtime. These are targeted
measurements, not a claim that every search becomes thousands of times faster.

## Repeatable commands

    dotnet run --project Tests/Tests.csproj -c Release -- --performance
    dotnet run --project Tests/Tests.csproj -c Release -- .tools/tgrep/tgrep.exe
    .\scripts\Build.ps1 -Task Publish

The streaming benchmark calls the production UTF-8 pump with deterministic
4 KiB reads. It checks line lengths, excludes one warmup and reports the median
of five iterations. Regression tests separately verify the actual line contents.
The parser allocation benchmark uses the same input and harness before/after
(including its current-directory lookup).

| Measurement | Before | After |
| --- | ---: | ---: |
| 1 MiB fragmented line, pump median | 53.76 ms | 0.16 ms |
| 8 MiB fragmented line, pump median | 6,440.41 ms | 1.79 ms |
| 100,000 hit parses, allocated bytes | 21,600,000 | 14,400,000 |
| 100,000 hit parses, elapsed (single sample) | 187.14 ms | 179.30 ms |
| Notifications for 100,002 adjacent/interleaved test hits | 100,002 | 785 |

Parser elapsed time is close enough to be treated as noisy; the repeatable
allocation reduction is 33.3%. Batching callback counts can vary slightly when
the 16 ms streaming flush triggers. Unique-file workloads benefit less from
coalescing. Total match counts and first-hit visibility are preserved.

## Memory and lifecycle policy

- Active preview: approximately 8 MiB of retained match data; one oversized first
  line is permitted so the preview does not become empty. The existing 10,000-line
  query limit still applies.
- Inactive previews: at most four, within a 32 MiB estimated budget. Eviction
  releases text/highlights and shrinks the backing list. Selecting an evicted
  preview reloads it.
- These budgets do not cap total process RAM: WinUI, the runtime, transient channel
  data, file metadata, native allocations and tgrep's own processes are separate.
- At most two app-owned servers are kept warm. A third indexed project evicts the
  least recently used owned server; indexes remain on disk. External servers are
  untouched.
- Idle progress/log polling drops from ten to one tick per second; active
  operations retain the 100 ms cadence.
- File loads are canceled and serialized, disposed cancellation sources are
  cleared, and shutdown awaits pending preview loads.

## Validation performed

61 automated checks passed, including real tgrep indexing, server reuse and LRU
eviction, external-server survival, cancellation, malformed JSON, pipe handling,
Unicode and CRLF split across reads, complete batched counts, and cache eviction.

WinUI debug build: zero warnings/errors. Native UI smoke test used eight files,
each with 10,000 matching lines (487 characters per line plus CRLF), in a temporary
folder outside the repository's ignored artifact directory. The app showed all
80,000 matches / eight files and approximately 1.4 seconds including the first
preview. No before/after end-to-end comparison was made. The preview stopped at
7,397 lines under its memory policy and displayed the expected truncation warning.
Rapid file navigation and a subsequent no-match search were exercised; the latter
cleared results and restored the empty state. UIA input was verified before invoking
search to avoid dispatch timing races in the automation.

This does not establish optimality for every repository, storage device or query.
Use representative repositories for broader throughput and peak-memory profiling.
