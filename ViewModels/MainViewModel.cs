using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using TgrepGui.Core;

namespace TgrepGui.ViewModels;

[Bindable]
public sealed class MainViewModel : ObservableObject
{
    private readonly TgrepClient client = new();
    private readonly SettingsStore store = new();
    private readonly ConcurrentQueue<TgrepLog> pendingLogs = new();
    private readonly Dictionary<string, FileResult> byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch clock = new();
    private CancellationTokenSource? operation;
    private string? pendingProgress;
    private ServerStatus? pendingStatus;
    private bool settingsReadable = true;
    public AppSettings Settings { get; private set; } = new();
    public event Action<string>? ThemeChanged;
    public ObservableCollection<FileResult> Files { get; } = [];
    public ObservableCollection<string> RecentFolders { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand RestartCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public MainViewModel()
    {
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => IsReady && !IsBusy);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => IsReady && !IsBusy);
        CancelCommand = new RelayCommand(() => { operation?.Cancel(); lineLoad?.Cancel(); }, () => IsBusy);
        client.Log += log => { pendingLogs.Enqueue(log); while (pendingLogs.Count > 2000) pendingLogs.TryDequeue(out _); };
        client.Progress += progress => Interlocked.Exchange(ref pendingProgress, progress);
        client.StatusChanged += status => Interlocked.Exchange(ref pendingStatus, status);
        timer.Tick += (_, _) => DrainUpdates();
        timer.Start();
    }

    private string folder = "", query = "", include = "", exclude = "", message = "Choose a folder and search your code.";
    private string warning = "", server = "Server not started", indexedFiles = "—", elapsed = "0.0 s";
    private bool ignoreCase = true, literal, wholeWord, useIndex = true, isBusy, isReady, missingTgrep;
    private long matchCount;
    private int warningCount;
    private FileResult? selectedFile;
    private SearchOptions? lastSearch;
    private AppSettings lastSearchSettings = new();
    private bool listingInProgress;
    private CancellationTokenSource? lineLoad;
    public string Folder { get => folder; set => SetProperty(ref folder, value); }
    public string Query { get => query; set => SetProperty(ref query, value); }
    public string Include { get => include; set => SetProperty(ref include, value); }
    public string Exclude { get => exclude; set => SetProperty(ref exclude, value); }
    public bool IgnoreCase { get => ignoreCase; set => SetProperty(ref ignoreCase, value); }
    public bool Literal { get => literal; set => SetProperty(ref literal, value); }
    public bool WholeWord { get => wholeWord; set => SetProperty(ref wholeWord, value); }
    public bool UseIndex { get => useIndex; set => SetProperty(ref useIndex, value); }
    public bool IsReady { get => isReady; private set { SetProperty(ref isReady, value); NotifyCommands(); } }
    public bool IsBusy { get => isBusy; private set { SetProperty(ref isBusy, value); OnPropertyChanged(nameof(CanEdit)); NotifyCommands(); } }
    public bool CanEdit => IsReady && !IsBusy;
    public bool MissingTgrep { get => missingTgrep; set => SetProperty(ref missingTgrep, value); }
    public string Message { get => message; private set => SetProperty(ref message, value); }
    public string Warning { get => warning; private set { SetProperty(ref warning, value); OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => !string.IsNullOrEmpty(Warning);
    public string Server { get => server; private set => SetProperty(ref server, value); }
    public string IndexedFiles { get => indexedFiles; private set => SetProperty(ref indexedFiles, value); }
    public string Elapsed { get => elapsed; private set => SetProperty(ref elapsed, value); }
    public long MatchCount { get => matchCount; private set => SetProperty(ref matchCount, value); }
    public int WarningCount { get => warningCount; private set => SetProperty(ref warningCount, value); }
    public FileResult? SelectedFile
    {
        get => selectedFile;
        set
        {
            SetSelected(value);
            if (value is { HasLines: false } && !listingInProgress && lastSearch != null)
                _ = LoadLinesAsync(value);
        }
    }
    private void SetSelected(FileResult? file)
    {
        if (SetProperty(ref selectedFile, file, nameof(SelectedFile))) OnPropertyChanged(nameof(SelectedPath));
    }
    public string SelectedPath => SelectedFile?.FullPath ?? "Select a file to read the matches";
    public string SettingsPath => store.FilePath;

    private void NotifyCommands()
    {
        SearchCommand?.NotifyCanExecuteChanged(); RestartCommand?.NotifyCanExecuteChanged(); CancelCommand?.NotifyCanExecuteChanged();
    }

    public async Task InitializeAsync()
    {
        try
        {
            Settings = await store.LoadAsync();
            if (!File.Exists(store.FilePath)) await store.SaveAsync(Settings);
        }
        catch (Exception ex) { settingsReadable = false; ReportError(new IOException("Settings were not loaded: " + ex.Message)); }
        foreach (var recent in Settings.RecentFolders) RecentFolders.Add(recent);
        Folder = RecentFolders.FirstOrDefault() ?? "";
        IgnoreCase = Settings.IgnoreCase; Literal = Settings.Literal;
        ThemeChanged?.Invoke(Settings.Theme);
        try
        {
            var prefill = Arguments.ParsePrefill(Environment.GetCommandLineArgs().Skip(1).ToArray());
            if (prefill.TryGetValue("folder", out var f)) Folder = f;
            if (prefill.TryGetValue("include", out var i)) Include = i;
            if (prefill.TryGetValue("exclude", out var e)) Exclude = e;
            if (prefill.TryGetValue("text", out var t)) Query = t;
        }
        catch (Exception ex) { ReportError(ex); }
        await CheckTgrepAsync();
        IsReady = true;
        OnPropertyChanged(nameof(CanEdit));
    }

    public async Task CheckTgrepAsync()
    {
        try { await Task.Run(() => TgrepClient.Discover(Settings.TgrepPath)); MissingTgrep = false; }
        catch (TgrepMissingException) { MissingTgrep = true; }
    }

    public async Task SaveSettingsAsync(AppSettings value)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the search to finish before saving.");
        if (!string.IsNullOrWhiteSpace(value.TgrepPath) && !File.Exists(Environment.ExpandEnvironmentVariables(value.TgrepPath)))
            throw new FileNotFoundException("The tgrep.exe path does not exist.");
        if (!string.IsNullOrWhiteSpace(value.IndexPath) && !Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(value.IndexPath)))
            throw new ArgumentException("Use an absolute path for the index.");
        if (!string.IsNullOrWhiteSpace(value.EditorPath) && !File.Exists(Environment.ExpandEnvironmentVariables(value.EditorPath)))
            throw new FileNotFoundException("The editor path does not exist. Select the .exe file.");
        if (!string.IsNullOrWhiteSpace(value.EditorPath) && !value.EditorArguments.Contains("$FILE"))
            throw new ArgumentException("Editor arguments must contain $FILE.");
        value = value with { RecentFolders = Settings.RecentFolders };
        await store.SaveAsync(value);
        Settings = value; settingsReadable = true;
        IgnoreCase = value.IgnoreCase; Literal = value.Literal;
        ThemeChanged?.Invoke(value.Theme);
        await CheckTgrepAsync();
    }

    private async Task SearchAsync()
    {
        if (string.IsNullOrEmpty(Query)) { ReportError(new ArgumentException("Enter text or a regular expression.")); return; }
        BeginOperation();
        listingInProgress = true;
        Files.Clear(); byPath.Clear(); SelectedFile = null; MatchCount = 0;
        var channel = Channel.CreateBounded<FileHit>(new BoundedChannelOptions(1024)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var token = operation!.Token;
        var options = new SearchOptions(Folder, Query, Include, Exclude, IgnoreCase, Literal, WholeWord, UseIndex);
        var snapshot = Settings;
        lastSearch = options;
        lastSearchSettings = snapshot;
        Task producer = Task.Run(async () =>
        {
            try { await client.SearchHitsAsync(options, snapshot, hit => channel.Writer.WriteAsync(hit, token), token); }
            finally { channel.Writer.TryComplete(); }
        });
        try
        {
            var touched = new HashSet<FileResult>();
            var yieldClock = Stopwatch.StartNew();
            await foreach (var first in channel.Reader.ReadAllAsync(token))
            {
                var batch = new List<FileHit>(128) { first };
                while (batch.Count < 128 && channel.Reader.TryRead(out var next)) batch.Add(next);
                touched.Clear();
                long added = 0;
                foreach (var hit in batch)
                {
                    if (!byPath.TryGetValue(hit.FullPath, out var file))
                    {
                        file = new(hit.FullPath, hit.RelativePath);
                        byPath.Add(hit.FullPath, file); Files.Add(file);
                    }
                    file.AddCount(hit.MatchCount); added += hit.MatchCount; touched.Add(file);
                }
                foreach (var file in touched) file.NotifyCount();
                MatchCount += added;
                if (yieldClock.ElapsedMilliseconds >= 16)
                {
                    await Task.Delay(1, token);
                    yieldClock.Restart();
                }
            }
            await producer;
            listingInProgress = false;
            DrainUpdates();
            Message = MatchCount == 0 ? "No matches. Try a different query or filters." : $"Search completed · {Files.Count.ToString("N0", CultureInfo.InvariantCulture)} files";
            try { await RememberFolderAsync(); }
            catch (Exception ex) { ReportWarning("Results are available; recent folders were not saved: " + ex.Message); }
            var selected = SelectedFile ?? Files.FirstOrDefault();
            if (selected != null)
            {
                SetSelected(selected);
                await LoadLinesAsync(selected);
            }
        }
        catch (OperationCanceledException) { Message = "Search cancelled · partial results kept"; }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            listingInProgress = false;
            operation!.Cancel();
            try { await producer; } catch { }
            EndOperation();
        }
    }

    private async Task LoadLinesAsync(FileResult file)
    {
        if (file.HasLines || lastSearch is null) return;
        lineLoad?.Cancel();
        var local = CancellationTokenSource.CreateLinkedTokenSource(operation?.Token ?? CancellationToken.None);
        lineLoad = local;
        var token = local.Token;
        bool showBusy = operation is null;
        if (showBusy) IsBusy = true;
        Message = "Loading matches…";
        var channel = Channel.CreateBounded<SearchMatch>(new BoundedChannelOptions(1024)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var options = lastSearch with { File = file.FullPath, MaxCount = SearchLimits.MaxMatchesPerFile, Include = "", Exclude = "" };
        var snapshot = lastSearchSettings;
        Task producer = Task.Run(async () =>
        {
            try { await client.SearchAsync(options, snapshot, match => channel.Writer.WriteAsync(match, token), token); }
            finally { channel.Writer.TryComplete(); }
        });
        int shown = 0;
        try
        {
            var yieldClock = Stopwatch.StartNew();
            await foreach (var first in channel.Reader.ReadAllAsync(token))
            {
                var batch = new List<SearchMatch>(128) { first };
                while (batch.Count < 128 && channel.Reader.TryRead(out var next)) batch.Add(next);
                foreach (var match in batch)
                {
                    file.AddLine(match);
                    shown += match.MatchCount;
                }
                if (yieldClock.ElapsedMilliseconds >= 16)
                {
                    await Task.Delay(1, token);
                    yieldClock.Restart();
                }
            }
            await producer;
            file.MarkLoaded();
            if (shown < file.Count)
                ReportWarning($"Showing the first {shown.ToString("N0", CultureInfo.InvariantCulture)} matches in this file. Narrow the query to see the rest.");
            Message = MatchCount == 0
                ? "No matches. Try a different query or filters."
                : $"Search completed · {Files.Count.ToString("N0", CultureInfo.InvariantCulture)} files";
        }
        catch (OperationCanceledException)
        {
            file.ResetLines();
        }
        catch (Exception ex)
        {
            file.ResetLines();
            ReportError(ex);
        }
        finally
        {
            try { await producer; } catch { }
            if (showBusy && ReferenceEquals(lineLoad, local)) IsBusy = false;
            local.Dispose();
        }
    }

    private async Task RestartAsync()
    {
        BeginOperation();
        try
        {
            var token = operation!.Token;
            string root = Folder; var snapshot = Settings;
            await Task.Run(() => client.RestartAsync(root, snapshot, token));
            DrainUpdates(); Message = "Server ready. Run a search to refresh the results.";
        }
        catch (OperationCanceledException) { Message = "Operation cancelled"; }
        catch (Exception ex) { ReportError(ex); }
        finally { EndOperation(); }
    }

    private void BeginOperation()
    {
        DrainUpdates(); Warning = ""; WarningCount = 0;
        operation = new(); IsBusy = true; clock.Restart(); Message = "Preparing…";
        Server = "Checking server…"; IndexedFiles = "—";
    }
    private void EndOperation()
    {
        Interlocked.Exchange(ref pendingProgress, null);
        clock.Stop(); Elapsed = $"{clock.Elapsed.TotalSeconds.ToString("N1", CultureInfo.InvariantCulture)} s";
        operation?.Dispose(); operation = null; IsBusy = false;
    }
    private async Task RememberFolderAsync()
    {
        string root = await Task.Run(() => Paths.Normalize(Folder));
        var recent = new[] { root }.Concat(Settings.RecentFolders)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        Settings = Settings with { RecentFolders = recent };
        RecentFolders.Clear(); foreach (var item in recent) RecentFolders.Add(item);
        if (settingsReadable) await store.SaveAsync(Settings);
    }
    private void DrainUpdates()
    {
        if (Interlocked.Exchange(ref pendingProgress, null) is { } progress && IsBusy) Message = progress;
        if (Interlocked.Exchange(ref pendingStatus, null) is { } status)
        {
            Server = status.Running ? $"PID {status.Pid} · port {status.Port}"
                : status.Description == "Scanning without index" ? status.Description : "Server not running";
            IndexedFiles = status.Files?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
        }
        int count = 0;
        while (count++ < 200 && pendingLogs.TryDequeue(out var log))
        {
            Logs.Add(log.ToString());
            if (Logs.Count > 1000)
            {
                for (int i = 0; i < 100; i++) Logs.RemoveAt(0);
            }
            if (log.IsWarning) ReportWarning(log.Text);
        }
        if (IsBusy) Elapsed = $"{clock.Elapsed.TotalSeconds.ToString("N1", CultureInfo.InvariantCulture)} s";
    }
    public void ReportError(Exception ex)
    {
        if (ex is TgrepMissingException) MissingTgrep = true;
        Warning = ex.Message; WarningCount++; Message = "Operation did not complete";
        Logs.Add($"{DateTime.Now:HH:mm:ss} [GUI] {ex.Message}");
    }
    private void ReportWarning(string text) { Warning = text; WarningCount++; }

    public async Task ShutdownAsync()
    {
        IsReady = false;
        OnPropertyChanged(nameof(CanEdit));
        lineLoad?.Cancel();
        operation?.Cancel();
        if (SearchCommand.ExecutionTask is { } search) await search;
        if (RestartCommand.ExecutionTask is { } restart) await restart;
        timer.Stop();
        await Task.Run(async () => await client.DisposeAsync());
    }
}
