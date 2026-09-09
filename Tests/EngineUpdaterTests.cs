using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TgrepGui.Core;

internal static class EngineUpdaterTests
{
    private sealed class Server : HttpMessageHandler
    {
        public string Version = "1.0.5";
        public string Arch = "x86_64";
        public bool BadHash, Offline, Prerelease, MaliciousUrl, MissingDigest, WrongEntry;
        public int Downloads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Offline) throw new HttpRequestException("offline test");
            byte[] zip = Zip(Version, WrongEntry ? "../tgrep.exe" : "tgrep.exe");
            string digest = BadHash ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(zip));
            if (request.RequestUri!.Host == "api.github.com")
            {
                string name = $"tgrep-v{Version}-{Arch}-pc-windows-msvc.zip";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    tag_name = "v" + Version, draft = false, prerelease = Prerelease,
                    assets = new[] { new { name, digest = MissingDigest ? null : "sha256:" + digest, size = zip.Length,
                        browser_download_url = MaliciousUrl ? "https://example.com/evil.exe"
                            : $"https://github.com/microsoft/tgrep/releases/download/v{Version}/{name}" } }
                })) });
            }
            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
        }
    }

    private static byte[] Zip(string text, string entry)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var file = zip.CreateEntry(entry);
            file.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var writer = new StreamWriter(file.Open());
            writer.Write(text);
        }
        return memory.ToArray();
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        using var server = new Server();
        using var http = new HttpClient(server);
        int probes = 0;
        bool reject = false;
        bool rejectMigration = false;
        Task Probe(string path, Version version, CancellationToken token)
        {
            probes++;
            token.ThrowIfCancellationRequested();
            if (reject) throw new InvalidDataException("incompatible protocol");
            check(File.ReadAllText(path) == version.ToString(), "updater probes the extracted release");
            return Task.CompletedTask;
        }
        var directory = Path.Combine(root, "updater");
        var updater = new EngineUpdater(directory, http, Probe, "x86_64",
            (path, _) => Task.FromResult(Version.Parse(File.ReadAllText(path))), (_, _, _) =>
                rejectMigration ? Task.FromException(new InvalidDataException("incompatible index")) : Task.CompletedTask);
        check(await updater.ResolveAsync(default) == null, "no installed update falls back to default discovery");
        string result = await updater.CheckAsync(null, default);
        string? first = await updater.ResolveAsync(default);
        check(result.Contains("Restart the app") && first != null && probes == 2, "verified download persists for next startup");
        check((await updater.CheckAsync(null, default)).Contains("already installed") && server.Downloads == 1,
            "repeated manual update reports pending restart without downloading again");
        string manifestPath = Path.Combine(directory, "x86_64", "installed.json");
        string manifest = File.ReadAllText(manifestPath);
        check((await updater.CheckAsync(first, default)).Contains("up to date") && server.Downloads == 1, "same version is not downloaded again");
        server.Version = "1.0.4";
        check((await updater.CheckAsync(first, default)).Contains("up to date") && server.Downloads == 1, "older release never downgrades active engine");
        server.Version = "1.0.6";
        server.BadHash = true;
        int before = probes;
        check((await updater.CheckAsync(first, default)).Contains("checksum mismatch") && probes == before
            && File.ReadAllText(manifestPath) == manifest, "bad checksum is never executed or activated");
        server.BadHash = false;
        reject = true;
        check((await updater.CheckAsync(first, default)).Contains("incompatible protocol") && File.ReadAllText(manifestPath) == manifest,
            "incompatible candidate preserves installed engine");
        reject = false;
        rejectMigration = true;
        check((await updater.CheckAsync(first, default)).Contains("incompatible index") && File.ReadAllText(manifestPath) == manifest,
            "incompatible index migration preserves installed engine");
        rejectMigration = false;
        server.Offline = true;
        check((await updater.CheckAsync(first, default)).Contains("keeping the current engine")
            && await updater.ResolveAsync(default) == first, "offline startup keeps cached engine available");
        server.Offline = false;
        foreach (string failure in new[] { "prerelease", "url", "digest", "entry" })
        {
            server.Prerelease = failure == "prerelease"; server.MaliciousUrl = failure == "url";
            server.MissingDigest = failure == "digest"; server.WrongEntry = failure == "entry";
            before = probes;
            check((await updater.CheckAsync(first, default)).Contains("keeping the current engine") && probes == before
                && File.ReadAllText(manifestPath) == manifest, "unsafe release rejected: " + failure);
        }
        server.WrongEntry = false;
        using (var gate = new FileStream(Path.Combine(directory, "x86_64", "update.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            check((await updater.CheckAsync(first, default)).Contains("Another app instance"), "update installation is serialized across instances");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            bool observed = false;
            try { await updater.CheckAsync(first, cancelled.Token); } catch (OperationCanceledException) { observed = true; }
            check(observed && File.ReadAllText(manifestPath) == manifest, "cancelled update preserves manifest");
        }
        await updater.CheckAsync(first, default);
        string? second = await updater.ResolveAsync(default);
        check(second != null && second != first && File.ReadAllText(first!) == "1.0.5", "upgrade retains the previous executable unchanged");
        check(await updater.ResolveAsync(default, new Version(1, 0, 7)) == null, "newer bundled engine takes precedence over an older cached update");
        File.WriteAllText(second!, "corrupted");
        check(await updater.ResolveAsync(default) == first, "corrupt latest engine falls back to previous verified version");
        File.WriteAllText(second!, "1.0.6");
        var rejectingLatest = new EngineUpdater(directory, http, (path, version, token) => version == new Version(1, 0, 6)
            ? Task.FromException(new InvalidDataException("startup probe failure")) : Probe(path, version, token), "x86_64");
        check(await rejectingLatest.ResolveAsync(default) == first, "startup protocol failure falls back even with a valid checksum");
        File.WriteAllText(manifestPath, "{not json");
        check(await updater.ResolveAsync(default) == null, "corrupt metadata falls back to bundled engine");
        check(!Directory.EnumerateDirectories(Path.Combine(directory, "x86_64"), "stage-*").Any(), "staging directories cleaned after success and failure");
        server.Arch = "aarch64";
        var arm = new EngineUpdater(Path.Combine(root, "arm"), http, Probe, "aarch64");
        check((await arm.CheckAsync(null, default)).Contains("Restart the app"), "ARM64 selects its own release asset");
    }

    public static async Task LiveAsync(string directory, string? baseline = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var updater = new EngineUpdater(directory);
        updater.Log += Console.WriteLine;
        Console.WriteLine(await updater.CheckAsync(baseline, timeout.Token));
        string exe = await updater.ResolveAsync(timeout.Token) ?? throw new Exception("Live update did not install a usable engine.");
        Console.WriteLine("VERIFIED_ENGINE=" + exe);
    }
}
