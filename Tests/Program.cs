using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TgrepGui.Core;

if (args is ["--performance"]) { await Performance.RunAsync(); return; }
if (args is ["--engine-update", var engineDirectory]) { await EngineUpdaterTests.LiveAsync(engineDirectory); return; }
if (args is ["--engine-update", var updateTestDirectory, var baseline]) { await EngineUpdaterTests.LiveAsync(updateTestDirectory, baseline); return; }
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); } catch (T) { Check(true, name); return; }
    throw new Exception("FAIL: " + name);
}
bool Alive(int pid)
{
    try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch (ArgumentException) { return false; }
}

string root = Path.Combine(Path.GetTempPath(), "tgrep GUI tests " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await PerformanceTests.RunAsync(Check);
    await EngineUpdaterTests.RunAsync(root, Check);
    var globs = Arguments.SplitGlobs("*.cs;*.xaml,*.{js,ts} \"my files/**\" [a,;].txt");
    Check(globs.SequenceEqual(new[] { "*.cs", "*.xaml", "*.{js,ts}", "my files/**", "[a,;].txt" }), "glob separators, braces, quotes and classes");
    var opts = new SearchOptions(root, "-serve", "*.cs;*.xaml", "bin/;*.min.js", true, true, true, false);
    var argv = Arguments.Search(opts, Path.Combine(root, "custom index"));
    Check(argv.Contains("-i") && argv.Contains("-F") && argv.Contains("-w") && argv.Contains("--no-index")
        && argv.Contains("!bin/**") && argv[^3] == "--" && argv[^2] == "-serve", "flag mapping and leading-dash pattern");
    string oneFile = Path.Combine(root, "one file.cs");
    var fileArgv = Arguments.Search(new SearchOptions(root, "needle", File: oneFile, MaxCount: SearchLimits.MaxMatchesPerFile), null);
    Check(fileArgv.Contains("-m") && fileArgv[^1] == oneFile && !fileArgv.Contains("-g"),
        "single-file search uses path and max-count without globs");
    foreach (string verb in new[] { "index", "serve", "status" })
    {
        Check(Arguments.Command(verb, root, null).Count == 2 && Arguments.Command(verb, root, "C:\\index")[^2] == "--index-path",
            verb + " consistent index-path");
    }
    var prefill = Arguments.ParsePrefill(["-f", root, "--text=-needle", "-i", "*.cs", "-e", "bin/"]);
    Check(prefill["folder"] == root && prefill["text"] == "-needle" && prefill["include"] == "*.cs", "CLI prefill aliases and equals");
    string json = JsonSerializer.Serialize(new { type = "match", data = new
    {
        path = new { text = "caffè file.cs" }, lines = new { text = "è😀 needle\r\n" }, line_number = 7,
        submatches = new[] { new { start = 7, end = 13 } }
    }});
    var match = TgrepJsonParser.Parse(json, root).Match!;
    Check(match.Text == "è😀 needle" && match.Highlights[0] == new TextSpan(4, 6) && match.LineNumber == 7,
        "UTF-8 byte offsets to UTF-16 highlights and CRLF");
    string two = JsonSerializer.Serialize(new { type = "match", data = new
    {
        path = new { text = "caffè file.cs" }, lines = new { text = "è😀 needle needle" }, line_number = 8,
        submatches = new[] { new { start = 7, end = 13 }, new { start = 14, end = 20 } }
    }});
    var twice = TgrepJsonParser.Parse(two, root).Match!;
    Check(twice.Highlights.Count == 2 && twice.Highlights[0] == new TextSpan(4, 6) && twice.Highlights[1] == new TextSpan(11, 6),
        "ordered UTF-8 submatches mapped in one pass");
    var again = TgrepJsonParser.Parse(json, root).Match!;
    Check(ReferenceEquals(match.FullPath, again.FullPath) && ReferenceEquals(match.RelativePath, again.RelativePath),
        "reuse path strings for consecutive rows of the same file");
    Check(TgrepJsonParser.Parse(Encoding.UTF8.GetBytes(json), root).Match!.Highlights[0] == new TextSpan(4, 6),
        "UTF-8 span parser matches the string parser");
    Check(TgrepJsonParser.TryReadHit(Encoding.UTF8.GetBytes(two), root, out var hit)
        && hit.MatchCount == 2 && hit.FullPath == match.FullPath, "hit parser counts submatches without line text");
    Check(TgrepJsonParser.Parse("{\"type\":\"begin\",\"data\":{\"path\":{\"text\":\"x.cs\"}}}", root).Match == null
        && TgrepJsonParser.Parse("{\"type\":\"end\",\"data\":{\"path\":{\"text\":\"x.cs\"}}}", root).Type == "end", "begin and end events");
    string bytesJson = JsonSerializer.Serialize(new { type = "match", data = new
    { path = new { bytes = Convert.ToBase64String(Encoding.UTF8.GetBytes("b.cs")) }, lines = new { bytes = "YWJjCg==" }, line_number = 1, submatches = Array.Empty<object>() }});
    Check(TgrepJsonParser.Parse(bytesJson, root).Match!.Text == "abc", "base64 JSON fields");
    Check(!TgrepClient.ParseStatus("No index found at C:\\test").HasIndex, "status exit zero is not server readiness");
    Check(TgrepClient.ParseStatus("Index status for x\n Files: 3\n Server: not running").Files == 3, "disk-only status");
    Check(TgrepClient.ParseStatus("Server status for x\n PID: 123\n Port: 456\n Files: 8\n Watcher: active\n Indexing: 2/8 files").Indexing,
        "background indexing status");
    Check(new TgrepLog(DateTimeOffset.Now, "search", "warning: no index").IsWarning
        && new TgrepLog(DateTimeOffset.Now, "search", "scanning every file").IsWarning, "fallback warnings detected");
    string? originalPath = Environment.GetEnvironmentVariable("PATH");
    string localTgrep = Path.Combine(AppContext.BaseDirectory, "tgrep.exe");
    string localBackup = localTgrep + ".bak";
    bool hadLocalTgrep = File.Exists(localTgrep);
    try
    {
        Environment.SetEnvironmentVariable("PATH", "");
        if (hadLocalTgrep) File.Move(localTgrep, localBackup, overwrite: true);
        await ThrowsAsync<TgrepMissingException>(() => Task.Run(() => TgrepClient.Discover(Path.Combine(root, "absent.exe"))), "missing tgrep detected");
        await File.WriteAllBytesAsync(localTgrep, [0x4D, 0x5A]);
        Check(TgrepClient.Discover("") == Path.GetFullPath(localTgrep), "discover tgrep next to the app");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", originalPath);
        if (File.Exists(localTgrep)) File.Delete(localTgrep);
        if (hadLocalTgrep && File.Exists(localBackup)) File.Move(localBackup, localTgrep, overwrite: true);
    }
    var settingsStore = new SettingsStore(Path.Combine(root, "settings.json"));
    await settingsStore.SaveAsync(new() { EditorArguments = "--goto \"$FILE:$LINE\"", RecentFolders = [root] });
    var loadedSettings = await settingsStore.LoadAsync();
    Check(loadedSettings.RecentFolders.Single() == root && loadedSettings.AutoUpdateEngine, "settings round trip and atomic replacement");
    string legacySettings = Path.Combine(root, "legacy-settings.json");
    await File.WriteAllTextAsync(legacySettings, """{"TgrepPath":"","IndexPath":""}""");
    Check((await new SettingsStore(legacySettings).LoadAsync()).AutoUpdateEngine, "legacy settings keep automatic engine updates enabled");
    Check(EditorLauncher.SplitWindowsArguments("--goto \"$FILE:$LINE\"").SequenceEqual(new[] { "--goto", "$FILE:$LINE" }), "editor argv tokenization");
    await using (var prepareClient = new TgrepClient())
    {
        await ThrowsAsync<ArgumentException>(() => prepareClient.PrepareAsync("", new(), default), "prepare requires a folder");
        await ThrowsAsync<DirectoryNotFoundException>(() => prepareClient.PrepareAsync(Path.Combine(root, "missing-folder"), new(), default), "prepare requires an existing folder");
    }

    string configuration = AppContext.BaseDirectory.Contains("Release") ? "Release" : "Debug";
    string fake = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "FakeTgrep", "bin", configuration, "net10.0", "FakeTgrep.exe"));
    var echoArgs = new[] { "echo", root, "a\"b", "C:\\trailing path\\", "$FILE & special", "-needle" };
    var echo = await ProcessRunner.RunAsync(fake, echoArgs, root, null, null, default);
    Check(JsonSerializer.Deserialize<string[]>(echo.Output)!.SequenceEqual(echoArgs.Skip(1)), "Windows argv round trip: spaces, quotes, backslashes and metacharacters");
    int sleepPid = 0;
    using (var cancellation = new CancellationTokenSource())
    {
        await ThrowsAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(fake, ["sleep"], root,
            line => { sleepPid = int.Parse(line); cancellation.Cancel(); return ValueTask.CompletedTask; }, null, cancellation.Token), "cancel running child");
    }
    Check(!Alive(sleepPid), "cancelled child exited");
    var watchdog = Stopwatch.StartNew();
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        await ThrowsAsync<JsonException>(() => ProcessRunner.RunAsync(fake, ["malformed"], root,
            line => { JsonDocument.Parse(line).Dispose(); return ValueTask.CompletedTask; }, null, timeout.Token), "parser failure kills producer without pipe deadlock");
    Check(watchdog.Elapsed < TimeSpan.FromSeconds(8), "parser failure terminates promptly");
    watchdog.Restart();
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        await ThrowsAsync<JsonException>(() => ProcessRunner.RunJsonAsync(fake, ["malformed"], root,
            line => { TgrepJsonParser.Parse(line.Span, root); return ValueTask.CompletedTask; }, null, timeout.Token), "JSON byte pump kills producer without pipe deadlock");
    Check(watchdog.Elapsed < TimeSpan.FromSeconds(8), "JSON byte pump terminates promptly");
    var flooded = await ProcessRunner.RunAsync(fake, ["stderr"], root, null, null, default);
    Check(flooded.Output.Trim() == "done" && flooded.Error.Length <= 32768, "concurrent stderr drainage and bounded capture");

    if (args.Length > 0)
    {
        string exe = Path.GetFullPath(args[0]);
        string repo = Path.Combine(root, "répo with spaces");
        Directory.CreateDirectory(repo); Directory.CreateDirectory(Path.Combine(repo, "skip"));
        await File.WriteAllTextAsync(Path.Combine(repo, "one file.cs"), "è😀 needle\nNEEDLE\n-serve\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "two.txt"), "needle\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "skip", "three.cs"), "needle\n");
        var settings = new AppSettings { TgrepPath = exe, IndexPath = Path.Combine(root, "custom index") };
        int? pid = null, restartedPid = null;
        var results = new List<SearchMatch>();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using (var client = new TgrepClient())
        {
            client.Log += line => Console.WriteLine("  " + line);
            client.StatusChanged += status => { if (status.Running) pid = status.Pid; };
            async Task Search(SearchOptions options)
            { results.Clear(); await client.SearchAsync(options, settings, m => { results.Add(m); return ValueTask.CompletedTask; }, testTimeout.Token); }
            await client.PrepareAsync(repo, settings, testTimeout.Token);
            Check(pid.HasValue && Alive(pid.Value), "prepare starts owned server without a search");
            int warmed = pid!.Value;
            await Search(new(repo, "needle", "*.cs", "skip/"));
            Check(results.Count == 2 && results.All(m => m.RelativePath == "one file.cs") && pid == warmed, "real tgrep: first index, serve, JSON, include/exclude, ignore case");
            var hits = new List<FileHit>();
            await client.SearchHitsAsync(new(repo, "needle", "*.cs", "skip/"), settings,
                h => { hits.Add(h); return ValueTask.CompletedTask; }, testTimeout.Token);
            Check(hits.Sum(h => h.MatchCount) == 2 && hits.All(h => h.RelativePath == "one file.cs"),
                "real tgrep: hit listing without keeping line text");
            Check(pid.HasValue && Alive(pid.Value), "real owned server alive");
            int firstPid = pid!.Value;
            await Search(new(repo, "-serve", "*.cs", "", Literal: true));
            Check(results.Count == 1 && pid == firstPid, "real literal leading-dash query and server reuse");
            await File.AppendAllTextAsync(Path.Combine(repo, "one file.cs"), "watcher_unique_token\n");
            for (int attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(200, testTimeout.Token); await Search(new(repo, "watcher_unique_token"));
                if (results.Count == 1) break;
            }
            Check(results.Count == 1 && pid == firstPid, "real watcher sees file change without reindex or restart");
            await client.RestartAsync(repo, settings, testTimeout.Token);
            restartedPid = pid;
            Check(pid != firstPid && !Alive(firstPid) && Alive(pid!.Value), "restart replaces only owned child");
            await Search(new(repo, "does_not_exist_012345"));
            Check(results.Count == 0, "real exit 1 means no matches");
            await ThrowsAsync<IOException>(() => Search(new(repo, "[")), "real invalid regex is an error");
            await Search(new(repo, "needle", "*.cs", "skip/", IgnoreCase: false, WholeWord: true));
            Check(results.Count == 1, "real whole-word and case-sensitive flags");
            await Search(new(repo, "needle", UseIndex: false));
            Check(results.Count == 4, "real no-index scanning");
            string another = Path.Combine(root, "other repo"); Directory.CreateDirectory(another);
            await ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(new(another, "needle"), settings,
                _ => ValueTask.CompletedTask, testTimeout.Token), "custom index cannot be reused for another root");
            await File.WriteAllTextAsync(Path.Combine(another, "new.txt"), "needle\n");
            results.Clear();
            await client.SearchAsync(new(another, "needle", UseIndex: false), settings with { IndexPath = "" },
                m => { results.Add(m); return ValueTask.CompletedTask; }, testTimeout.Token);
            Check(results.Count == 1 && !Directory.Exists(Path.Combine(another, ".tgrep")), "no-index on fresh folder never creates index or server");
        }
        Check(!Alive(restartedPid!.Value), "app disposal terminates owned server");
        await using (var boundedClient = new TgrepClient())
        {
            var roots = Enumerable.Range(0, 3).Select(i => Path.Combine(root, $"cache-repo-{i}")).ToArray();
            var pids = new int[3];
            int active = 0;
            boundedClient.StatusChanged += status => { if (status.Running) pids[active] = status.Pid!.Value; };
            async Task Visit(int index)
            {
                active = index;
                await boundedClient.SearchHitsAsync(new(roots[index], "needle"),
                    new() { TgrepPath = exe }, _ => ValueTask.CompletedTask, testTimeout.Token);
            }
            foreach (string directory in roots)
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "file.txt"), "needle");
            }
            await Visit(0); await Visit(1);
            int reused = pids[0], retired = pids[1];
            await Visit(0); await Visit(2);
            Check(pids[0] == reused && Alive(reused) && !Alive(retired) && Alive(pids[2]),
                "server cache keeps two recent owned servers and evicts the least recently used");
        }
        using var external = ProcessRunner.Start(exe, Arguments.Command("serve", repo, settings.IndexPath), repo);
        Task stdout = external.StandardOutput.ReadToEndAsync(), stderr = external.StandardError.ReadToEndAsync();
        try
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                var status = await ProcessRunner.RunAsync(exe, Arguments.Command("status", repo, settings.IndexPath), repo, null, null, testTimeout.Token);
                if (TgrepClient.ParseStatus(status.Output).Running) break;
                await Task.Delay(100, testTimeout.Token);
            }
            await using (var otherClient = new TgrepClient())
            {
                await otherClient.SearchAsync(new(repo, "needle"), settings, _ => ValueTask.CompletedTask, testTimeout.Token);
                await ThrowsAsync<InvalidOperationException>(() => otherClient.RestartAsync(repo, settings, testTimeout.Token), "external server restart refused");
            }
            Check(!external.HasExited, "external server survives GUI disposal");
        }
        finally
        {
            if (!external.HasExited) external.Kill(true);
            await external.WaitForExitAsync(); await Task.WhenAll(stdout, stderr);
        }
    }
    else Console.WriteLine("SKIP: real tgrep integration (pass the tgrep.exe path to enable)");
    Console.WriteLine($"\n{passed} checks passed.");
}
finally
{
    // Only delete this run's uniquely named fixture, never user repositories/indexes.
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(root, true);
}
