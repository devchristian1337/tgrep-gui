using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TgrepGui.Core;

public sealed class TgrepClient : IAsyncDisposable
{
    private sealed record ServerChild(Process Process, string Index, string Executable, Task Output, Task Error)
    { public long LastUsed { get; set; } = Stopwatch.GetTimestamp(); }
    private readonly Dictionary<string, ServerChild> children = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    public event Action<TgrepLog>? Log;
    public event Action<ServerStatus>? StatusChanged;
    public event Action<string>? Progress;

    public static string Discover(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            if (File.Exists(configured)) return Path.GetFullPath(configured);
        }
        foreach (string? directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) })
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var local = Path.Combine(directory, "tgrep.exe");
            if (File.Exists(local)) return Path.GetFullPath(local);
        }
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')), "tgrep.exe");
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        throw new TgrepMissingException();
    }

    public Task SearchAsync(SearchOptions options, AppSettings settings,
        Func<SearchMatch, ValueTask> onMatch, CancellationToken cancellationToken)
        => SearchCoreAsync(options, settings, onMatch, null, cancellationToken);

    public Task SearchHitsAsync(SearchOptions options, AppSettings settings,
        Func<FileHit, ValueTask> onHit, CancellationToken cancellationToken)
        => SearchCoreAsync(options, settings, null, onHit, cancellationToken);

    public async Task PrepareAsync(string folder, AppSettings settings, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        folder = ValidateFolder(folder);
        string exe = Discover(settings.TgrepPath);
        string? index = GetIndex(settings, folder);
        await EnsureServerAsync(folder, exe, index, linked.Token).ConfigureAwait(false);
    }

    private async Task SearchCoreAsync(SearchOptions options, AppSettings settings,
        Func<SearchMatch, ValueTask>? onMatch, Func<FileHit, ValueTask>? onHit, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = linked.Token;
        string folder = ValidateFolder(options.Folder);
        string exe = Discover(settings.TgrepPath);
        string? index = GetIndex(settings, folder);
        options = options with { Folder = folder };
        if (options.File is { } file)
        {
            file = Path.GetFullPath(file);
            if (!File.Exists(file)) throw new FileNotFoundException("The file no longer exists.", file);
            options = options with { File = file };
        }
        // Validate filters before starting any indexing work.
        var args = Arguments.Search(options, index);
        if (options.UseIndex) await EnsureServerAsync(folder, exe, index, token).ConfigureAwait(false);
        else StatusChanged?.Invoke(new(false, null, null, null, false, false, "Scanning without index"));
        Progress?.Invoke("Searching…");
        var batcher = onHit is null ? null : new FileHitBatcher(onHit);
        var result = await ProcessRunner.RunJsonAsync(exe, args, folder, json =>
        {
            if (onHit != null)
                return TgrepJsonParser.TryReadHit(json.Span, folder, out var hit) ? batcher!.AddAsync(hit) : ValueTask.CompletedTask;
            return ReceiveMatch(json, folder, onMatch!);
        }, line => WriteLog("search", line), token).ConfigureAwait(false);
        if (batcher != null) await batcher.FlushAsync().ConfigureAwait(false);
        if (result.ExitCode is not (0 or 1))
            throw new IOException($"tgrep search: exit {result.ExitCode}. {result.Error.Trim()}");
        if (options.UseIndex)
        {
            try { StatusChanged?.Invoke(await GetStatusAsync(folder, exe, index, token).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { WriteLog("status", "warning: " + ex.Message); }
        }
    }

    public async Task RestartAsync(string folder, AppSettings settings, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = linked.Token;
        folder = ValidateFolder(folder);
        string exe = Discover(settings.TgrepPath);
        string? index = GetIndex(settings, folder);
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (children.Remove(folder, out var child)) await StopAsync(child).ConfigureAwait(false);
            var status = await GetStatusAsync(folder, exe, index, token).ConfigureAwait(false);
            if (status.Running)
                throw new InvalidOperationException($"Server PID {status.Pid} is external: I cannot restart it. Stop it from the program that started it.");
        }
        finally { lifecycle.Release(); }
        await EnsureServerAsync(folder, exe, index, token).ConfigureAwait(false);
    }

    private async Task EnsureServerAsync(string folder, string exe, string? index, CancellationToken token)
    {
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string effectiveIndex = index ?? Path.Combine(folder, ".tgrep");
            if (children.TryGetValue(folder, out var previous) &&
                (previous.Process.HasExited || !string.Equals(previous.Index, effectiveIndex, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(previous.Executable, exe, StringComparison.OrdinalIgnoreCase)))
            {
                children.Remove(folder);
                await StopAsync(previous).ConfigureAwait(false);
            }
            if (children.Any(p => !p.Key.Equals(folder, StringComparison.OrdinalIgnoreCase)
                && p.Value.Index.Equals(effectiveIndex, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This index is already used for another folder. Choose a dedicated index path.");
            await ValidateIndexRootAsync(effectiveIndex, folder, token).ConfigureAwait(false);
            Progress?.Invoke("Checking index and server…");
            var status = await GetStatusAsync(folder, exe, index, token).ConfigureAwait(false);
            if (!status.Running)
            {
                if (!status.HasIndex)
                {
                    Progress?.Invoke("Creating the first index…");
                    var result = await ProcessRunner.RunAsync(exe, Arguments.Command("index", folder, index), folder,
                        line => { WriteLog("index", line); Progress?.Invoke(line); return ValueTask.CompletedTask; },
                        line => { WriteLog("index", line); Progress?.Invoke(line); }, token).ConfigureAwait(false);
                    if (result.ExitCode != 0) throw new IOException($"Indexing failed ({result.ExitCode}). {result.Error.Trim()}");
                }
                if (children.Remove(folder, out var failed)) await StopAsync(failed).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                // Keep two recent projects warm; old indexes remain on disk.
                while (children.Count >= 2)
                {
                    var oldest = children.MinBy(pair => pair.Value.LastUsed);
                    children.Remove(oldest.Key);
                    await StopAsync(oldest.Value).ConfigureAwait(false);
                }
                Progress?.Invoke("Starting server…");
                var process = ProcessRunner.Start(exe, Arguments.Command("serve", folder, index), folder);
                Task output = PumpServerAsync(process.StandardOutput, "serve");
                Task error = PumpServerAsync(process.StandardError, "serve");
                children[folder] = new(process, effectiveIndex, exe, output, error);
                WriteLog("serve", $"Started child process PID {process.Id} for {folder}");
            }
            // Wait for a ready index; never show partial results as a completed search.
            var startup = Stopwatch.StartNew();
            int delayMs = 300;
            while (!status.Running || status.Indexing)
            {
                token.ThrowIfCancellationRequested();
                if (children.TryGetValue(folder, out var child) && child.Process.HasExited)
                {
                    // Another GUI may have won tgrep's serve.lock in the meantime.
                    status = await GetStatusAsync(folder, exe, index, token).ConfigureAwait(false);
                    if (!status.Running) throw new IOException($"The server stopped ({child.Process.ExitCode}). Check the log.");
                }
                if (!status.Running && startup.Elapsed > TimeSpan.FromSeconds(30))
                    throw new TimeoutException("The server did not respond within 30 seconds. Check the log or try Restart server.");
                StatusChanged?.Invoke(status);
                Progress?.Invoke(status.Indexing ? "The server is finishing the index…" : "Waiting for server…");
                await Task.Delay(delayMs, token).ConfigureAwait(false);
                delayMs = Math.Min(1000, delayMs * 2);
                status = await GetStatusAsync(folder, exe, index, token).ConfigureAwait(false);
            }
            if (children.TryGetValue(folder, out var current)) current.LastUsed = Stopwatch.GetTimestamp();
            StatusChanged?.Invoke(status);
            if (!status.WatcherActive) WriteLog("serve", "warning: watcher inactive; file changes may not be indexed.");
        }
        finally { lifecycle.Release(); }
    }

    public static ServerStatus ParseStatus(string text)
    {
        long? Read(string key)
        {
            var match = Regex.Match(text, @"^\s*" + key + @":\s*(\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success && long.TryParse(match.Groups[1].Value, out long value) ? value : null;
        }
        bool server = text.Contains("Server status for", StringComparison.OrdinalIgnoreCase);
        bool disk = text.Contains("Index status for", StringComparison.OrdinalIgnoreCase);
        bool missing = text.Contains("No index found", StringComparison.OrdinalIgnoreCase);
        if (!server && !disk && !missing) throw new InvalidDataException("Unrecognized tgrep status format: " + text.Trim());
        bool indexing = Regex.IsMatch(text, @"Indexing:\s*(?!complete\b)\S", RegexOptions.IgnoreCase);
        return new(server || disk, server ? (int?)Read("PID") : null, server ? (int?)Read("Port") : null,
            Read("Files"), indexing, Regex.IsMatch(text, @"Watcher:\s*active\b", RegexOptions.IgnoreCase), text.Trim());
    }

    private async Task<ServerStatus> GetStatusAsync(string folder, string exe, string? index, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var result = await ProcessRunner.RunAsync(exe, Arguments.Command("status", folder, index), folder,
                null, line => WriteLog("status", line), timeout.Token).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new IOException($"tgrep status: {result.Error.Trim()}");
            return ParseStatus(result.Output);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("tgrep status did not respond within 5 seconds."); }
    }

    private static async Task ValidateIndexRootAsync(string index, string folder, CancellationToken token)
    {
        string meta = Path.Combine(index, "meta.json");
        if (!File.Exists(meta)) return;
        await using var stream = File.OpenRead(meta);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        if (json.RootElement.TryGetProperty("root_path", out var root) && root.GetString() is { Length: > 0 } path
            && !Paths.Normalize(path).Equals(folder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Index {index} belongs to another folder. Choose a dedicated index.");
    }

    private static string ValidateFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Select a folder.");
        folder = Paths.Normalize(folder.Trim().Trim('"'));
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Folder not found: " + folder);
        return folder;
    }

    private static string? GetIndex(AppSettings settings, string folder)
    {
        if (string.IsNullOrWhiteSpace(settings.IndexPath)) return null;
        string expanded = Environment.ExpandEnvironmentVariables(settings.IndexPath.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(expanded)) throw new ArgumentException("The index path must be absolute.");
        string index = Paths.Normalize(expanded);
        if (index.Equals(folder, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The index must use a dedicated directory, different from the searched folder.");
        return index;
    }

    private async Task PumpServerAsync(StreamReader reader, string source)
    {
        try { await ProcessRunner.PumpAsync(reader, line => { WriteLog(source, line); return ValueTask.CompletedTask; }).ConfigureAwait(false); }
        catch (IOException ex) { WriteLog(source, ex.Message); }
        catch (ObjectDisposedException) { }
    }
    private static ValueTask ReceiveMatch(ReadOnlyMemory<byte> json, string folder, Func<SearchMatch, ValueTask> onMatch)
    {
        if (json.Length == 0) return ValueTask.CompletedTask;
        var item = TgrepJsonParser.Parse(json.Span, folder);
        return item.Match != null ? onMatch(item.Match) : ValueTask.CompletedTask;
    }
    private void WriteLog(string source, string text) => Log?.Invoke(new(DateTimeOffset.Now, source, text));
    private static async Task StopAsync(ServerChild child)
    {
        ProcessRunner.Kill(child.Process);
        await child.Process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(child.Output, child.Error).ConfigureAwait(false);
        child.Process.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var child in children.Values) await StopAsync(child).ConfigureAwait(false);
            children.Clear();
        }
        finally { lifecycle.Release(); }
    }
}
