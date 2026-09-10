using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TgrepGui.Core;

// Downloads never replace the engine selected by an open GUI session.
public sealed class EngineUpdater
{
    private static readonly HttpClient Web = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly HttpClient web;
    private readonly string root;
    private readonly string architecture;
    private readonly Func<string, Version, CancellationToken, Task> probe;
    private readonly Func<string, CancellationToken, Task<Version>> readVersion;
    private readonly Func<string, string, CancellationToken, Task> migrationProbe;
    private const int MaxArchive = 64 * 1024 * 1024;
    private const int MaxExecutable = 128 * 1024 * 1024;
    public event Action<string>? Log;
    public sealed record Installed(string Version, string Sha256);
    private sealed record State(Installed? Current, Installed? Previous);
    internal sealed record Release(Version Version, string Url, string Sha256);

    public EngineUpdater(string? directory = null, HttpClient? http = null,
        Func<string, Version, CancellationToken, Task>? compatibilityProbe = null, string? arch = null,
        Func<string, CancellationToken, Task<Version>>? versionReader = null,
        Func<string, string, CancellationToken, Task>? indexCompatibilityProbe = null)
    {
        architecture = arch ?? RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64", Architecture.Arm64 => "aarch64",
            _ => "unsupported"
        };
        root = Path.GetFullPath(Path.Combine(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tgrep-gui", "engines"), architecture));
        web = http ?? Web;
        probe = compatibilityProbe ?? ProbeAsync;
        readVersion = versionReader ?? ReadVersionAsync;
        migrationProbe = indexCompatibilityProbe ?? ProbeIndexUpgradeAsync;
    }

    private string StatePath => Path.Combine(root, "installed.json");
    private string Executable(Installed engine)
    {
        if (!TryVersion(engine.Version, out _) || !IsHash(engine.Sha256))
            throw new InvalidDataException("Invalid installed engine metadata.");
        return Path.Combine(root, engine.Version, engine.Sha256, "tgrep.exe");
    }

    // Local checks only. A damaged/incompatible current version falls back to the previous one.
    public async Task<string?> ResolveAsync(CancellationToken token, Version? minimumVersion = null)
    {
        var state = await ReadStateAsync(token).ConfigureAwait(false);
        foreach (var item in new[] { state.Current, state.Previous })
        {
            if (item is null) continue;
            if (minimumVersion != null && Version.Parse(item.Version) < minimumVersion) continue;
            try
            {
                string path = Executable(item);
                if (!File.Exists(path) || await HashAsync(path, token).ConfigureAwait(false) != item.Sha256)
                    throw new InvalidDataException("Installed engine checksum mismatch.");
                await VerifyAsync(path, Version.Parse(item.Version), token).ConfigureAwait(false);
                Log?.Invoke($"Using verified tgrep {item.Version}.");
                return path;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { Log?.Invoke("Engine skipped: " + ex.Message); }
        }
        return null;
    }

    public async Task<string> CheckAsync(string? activeExecutable, CancellationToken token)
    {
        if (architecture == "unsupported") return "Automatic updates are unavailable for this architecture.";
        Directory.CreateDirectory(root);
        FileStream updateLock;
        try { updateLock = new(Path.Combine(root, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return "Another app instance is checking for engine updates."; }
        using (updateLock)
        {
            string stage = Path.Combine(root, "stage-" + Guid.NewGuid().ToString("N"));
            try
            {
                var currentVersion = activeExecutable is null ? new Version(0, 0, 0)
                    : await readVersion(activeExecutable, token).ConfigureAwait(false);
                Release? release = null;
                Version? latestWithoutWindows = null;
                // Una tag senza lo zip richiesto non interrompe la ricerca, anche fra pagine diverse.
                for (int page = 1; ; page++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        $"https://api.github.com/repos/microsoft/tgrep/releases?per_page=30&page={page}");
                    request.Headers.UserAgent.ParseAdd("tgrep-gui/1.0");
                    request.Headers.Accept.ParseAdd("application/vnd.github+json");
                    using var response = await web.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await using var metadataStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    using var metadata = new MemoryStream();
                    await CopyBoundedAsync(metadataStream, metadata, 4 * 1024 * 1024, token).ConfigureAwait(false);
                    var selection = SelectRelease(metadata.ToArray(), architecture, currentVersion);
                    if (selection.Candidate != null && (release == null || selection.Candidate.Version > release.Version))
                        release = selection.Candidate;
                    if (selection.LatestWithoutWindows != null && (latestWithoutWindows == null || selection.LatestWithoutWindows > latestWithoutWindows))
                        latestWithoutWindows = selection.LatestWithoutWindows;
                    if (selection.Count < 30) break;
                }
                if (release == null)
                {
                    string status = activeExecutable == null
                        ? $"No Windows engine is available for {architecture}."
                        : $"No newer Windows engine is available for {architecture}; keeping tgrep {currentVersion}.";
                    return latestWithoutWindows == null ? status
                        : status + $" tgrep {latestWithoutWindows} has no Windows package for this architecture.";
                }
                var pending = (await ReadStateAsync(token).ConfigureAwait(false)).Current;
                if (pending != null && Version.Parse(pending.Version) >= release.Version
                    && File.Exists(Executable(pending))
                    && await HashAsync(Executable(pending), token).ConfigureAwait(false) == pending.Sha256)
                    return $"tgrep {pending.Version} is already installed. Restart the app to use it.";
                Log?.Invoke($"Downloading tgrep {release.Version}…");
                Directory.CreateDirectory(stage);
                string archive = Path.Combine(stage, "download.zip");
                using (var download = await web.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    download.EnsureSuccessStatusCode();
                    await using var source = await download.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using var target = File.Create(archive);
                    await CopyBoundedAsync(source, target, MaxArchive, token).ConfigureAwait(false);
                }
                if (await HashAsync(archive, token).ConfigureAwait(false) != release.Sha256)
                    throw new InvalidDataException("Release archive checksum mismatch.");
                string candidate = Path.Combine(stage, "tgrep.exe");
                using (var zip = ZipFile.OpenRead(archive))
                {
                    // Extract only this exact entry: never trust archive paths or run ancillary files.
                    var entries = zip.Entries.Where(e => e.FullName == "tgrep.exe").ToArray();
                    if (entries.Length != 1 || entries[0].Length > MaxExecutable)
                        throw new InvalidDataException("Release must contain exactly one root tgrep.exe.");
                    await using var source = entries[0].Open();
                    await using var target = File.Create(candidate);
                    await CopyBoundedAsync(source, target, MaxExecutable, token).ConfigureAwait(false);
                }
                Log?.Invoke($"Testing tgrep {release.Version} compatibility…");
                await VerifyAsync(candidate, release.Version, token).ConfigureAwait(false);
                if (activeExecutable != null)
                {
                    using var migrationTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    migrationTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                    await migrationProbe(activeExecutable, candidate, migrationTimeout.Token).ConfigureAwait(false);
                }
                var installed = new Installed(release.Version.ToString(), await HashAsync(candidate, token).ConfigureAwait(false));
                string destination = Executable(installed);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!File.Exists(destination)) File.Move(candidate, destination);
                else if (await HashAsync(destination, token).ConfigureAwait(false) != installed.Sha256)
                    throw new InvalidDataException("Cached engine checksum mismatch.");
                var state = await ReadStateAsync(token).ConfigureAwait(false);
                // Preserve the engine actually used this session, including a previous-version fallback.
                var previous = new[] { state.Current, state.Previous }.FirstOrDefault(i => i != null
                    && string.Equals(Executable(i), activeExecutable, StringComparison.OrdinalIgnoreCase));
                string temporary = Path.Combine(stage, "installed.json");
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new State(installed, previous)), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, StatePath, overwrite: true);
                return $"tgrep {release.Version} verified and ready. Restart the app to use it.";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            { return "Engine update unavailable; keeping the current engine. " + ex.Message; }
            finally { DeleteTemporary(stage); }
        }
    }

    private async Task<State> ReadStateAsync(CancellationToken token)
    {
        try
        {
            if (!File.Exists(StatePath)) return new(null, null);
            if (new FileInfo(StatePath).Length > 4096) throw new InvalidDataException("Engine metadata is too large.");
            var state = JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(StatePath, token).ConfigureAwait(false)) ?? new(null, null);
            foreach (var item in new[] { state.Current, state.Previous }) if (item != null) _ = Executable(item);
            return state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Log?.Invoke("Installed engine metadata ignored: " + ex.Message); return new(null, null); }
    }

    internal static Release ParseRelease(byte[] metadata, string arch)
    {
        using var json = JsonDocument.Parse(metadata);
        var release = json.RootElement;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()
            || !tag.StartsWith('v') || !TryVersion(tag[1..], out var version))
            throw new InvalidDataException("Only stable versioned releases are supported.");
        if (arch is not ("x86_64" or "aarch64")) throw new InvalidDataException("Unsupported architecture.");
        string name = $"tgrep-{tag}-{arch}-pc-windows-msvc.zip";
        var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("Windows release asset not found.");
        var asset = assets[0];
        string expectedUrl = $"https://github.com/microsoft/tgrep/releases/download/{tag}/{name}";
        string digest = asset.GetProperty("digest").GetString() ?? "";
        if (asset.GetProperty("browser_download_url").GetString() != expectedUrl
            || !digest.StartsWith("sha256:") || !IsHash(digest[7..])
            || asset.GetProperty("size").GetInt64() is <= 0 or > MaxArchive)
            throw new InvalidDataException("Release URL, size or SHA-256 digest is invalid.");
        return new(version!, expectedUrl, digest[7..]);
    }

    internal sealed record Selection(Release? Candidate, Version? LatestWithoutWindows, int Count);

    internal static Selection SelectRelease(byte[] metadata, string arch, Version currentVersion)
    {
        if (arch is not ("x86_64" or "aarch64")) throw new InvalidDataException("Unsupported architecture.");
        using var json = JsonDocument.Parse(metadata);
        Release? candidate = null;
        Version? latestWithoutWindows = null;
        foreach (var release in json.RootElement.EnumerateArray())
        {
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()
                || !tag.StartsWith('v') || !TryVersion(tag[1..], out var version) || version <= currentVersion)
                continue;
            string name = $"tgrep-{tag}-{arch}-pc-windows-msvc.zip";
            if (!release.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == name))
            {
                if (latestWithoutWindows == null || version > latestWithoutWindows) latestWithoutWindows = version;
                continue;
            }
            // Mantiene i controlli di integrità esistenti per gli asset presenti.
            var parsed = ParseRelease(System.Text.Encoding.UTF8.GetBytes(release.GetRawText()), arch);
            if (candidate == null || parsed.Version > candidate.Version) candidate = parsed;
        }
        return new(candidate, latestWithoutWindows, json.RootElement.GetArrayLength());
    }

    private static bool TryVersion(string? value, out Version? version)
    {
        version = null;
        return value != null && Regex.IsMatch(value, @"^\d+\.\d+\.\d+$") && Version.TryParse(value, out version);
    }
    private static bool IsHash(string? hash) => hash != null && Regex.IsMatch(hash, "^[a-f0-9]{64}$");
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
    private static async Task CopyBoundedAsync(Stream source, Stream target, int limit, CancellationToken token)
    {
        byte[] buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("Download exceeds the size limit.");
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }
    public static async Task<Version> ReadVersionAsync(string exe, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var result = await ProcessRunner.RunAsync(exe, ["--version"], Path.GetDirectoryName(exe)!, null, null, timeout.Token).ConfigureAwait(false);
        var match = Regex.Match(result.Output.Trim(), @"^tgrep (\d+\.\d+\.\d+)$");
        if (result.ExitCode != 0 || !match.Success || !TryVersion(match.Groups[1].Value, out var version))
            throw new InvalidDataException("Unexpected tgrep version response.");
        return version!;
    }
    private async Task VerifyAsync(string exe, Version expected, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { await probe(exe, expected, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Engine compatibility test timed out."); }
    }

    public static async Task ProbeAsync(string exe, Version expected, CancellationToken token)
    {
        if (await ReadVersionAsync(exe, token).ConfigureAwait(false) != expected)
            throw new InvalidDataException("Downloaded executable version differs from the release.");
        string fixture = Path.Combine(Path.GetTempPath(), "tgrep-gui-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            string file = Path.Combine(fixture, "caffè file.txt");
            await File.WriteAllTextAsync(file, "è😀 needle needle\nNEEDLE\nneedles\n", token).ConfigureAwait(false);
            await using var client = new TgrepClient();
            bool ready = false;
            client.StatusChanged += s => ready |= s.Running && !s.Indexing;
            var settings = new AppSettings { TgrepPath = exe };
            foreach (bool indexed in new[] { false, true })
            {
                var hits = new List<FileHit>();
                var options = new SearchOptions(fixture, "needle", Include: "*.txt", Literal: true, WholeWord: true, UseIndex: indexed);
                await client.SearchHitsAsync(options, settings, h => { hits.Add(h); return ValueTask.CompletedTask; }, token).ConfigureAwait(false);
                if (hits.Sum(h => h.MatchCount) != 3 || hits.Any(h => h.FullPath != file))
                    throw new InvalidDataException("Engine returned incompatible search counts or paths.");
                var lines = new List<SearchMatch>();
                await client.SearchAsync(options with { File = file, MaxCount = SearchLimits.MaxMatchesPerFile }, settings,
                    m => { lines.Add(m); return ValueTask.CompletedTask; }, token).ConfigureAwait(false);
                if (lines.Count != 2 || lines[0].LineNumber != 1 || lines[0].MatchCount != 2
                    || lines[0].Highlights.Count != 2 || lines[0].Highlights[0] != new TextSpan(4, 6))
                    throw new InvalidDataException("Engine returned incompatible preview JSON.");
                hits.Clear();
                await client.SearchHitsAsync(options with { Pattern = "missing_probe_token" }, settings,
                    h => { hits.Add(h); return ValueTask.CompletedTask; }, token).ConfigureAwait(false);
                if (hits.Count != 0) throw new InvalidDataException("Engine returned false matches.");
            }
            if (!ready) throw new InvalidDataException("Engine server did not report a ready index.");
        }
        finally { DeleteTemporary(fixture); }
    }

    // Exercise an old index with the new engine, then verify that fallback can still read it.
    // The user's indexes are never opened by these checks.
    public static async Task ProbeIndexUpgradeAsync(string previous, string candidate, CancellationToken token)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "tgrep-gui-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(fixture, "probe.txt"), "index_compatibility_token\n", token).ConfigureAwait(false);
            foreach (string exe in new[] { previous, candidate, previous })
            {
                await using var client = new TgrepClient();
                int count = 0;
                await client.SearchHitsAsync(new(fixture, "index_compatibility_token", Literal: true), new() { TgrepPath = exe },
                    h => { count += h.MatchCount; return ValueTask.CompletedTask; }, token).ConfigureAwait(false);
                if (count != 1) throw new InvalidDataException("Engine index upgrade/fallback compatibility check failed.");
            }
        }
        finally { DeleteTemporary(fixture); }
    }

    private static void DeleteTemporary(string path)
    {
        // Only caller-created GUID staging/fixture directories reach this method.
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
