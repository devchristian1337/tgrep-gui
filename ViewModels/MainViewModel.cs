using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
        CancelCommand = new RelayCommand(() => operation?.Cancel(), () => IsBusy);
        client.Log += log => { pendingLogs.Enqueue(log); while (pendingLogs.Count > 2000) pendingLogs.TryDequeue(out _); };
        client.Progress += progress => Interlocked.Exchange(ref pendingProgress, progress);
        client.StatusChanged += status => Interlocked.Exchange(ref pendingStatus, status);
        timer.Tick += (_, _) => DrainUpdates();
        timer.Start();
    }

    private string folder = "", query = "", include = "", exclude = "", message = "Scegli una cartella e cerca nel codice.";
    private string warning = "", server = "Server non avviato", indexedFiles = "—", elapsed = "0,0 s";
    private bool ignoreCase = true, literal, wholeWord, useIndex = true, isBusy, isReady, missingTgrep;
    private long matchCount;
    private int warningCount;
    private FileResult? selectedFile;
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
    public FileResult? SelectedFile { get => selectedFile; set { if (SetProperty(ref selectedFile, value)) OnPropertyChanged(nameof(SelectedPath)); } }
    public string SelectedPath => SelectedFile?.FullPath ?? "Seleziona un file per leggere le corrispondenze";
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
        catch (Exception ex) { settingsReadable = false; ReportError(new IOException("Impostazioni non caricate: " + ex.Message)); }
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
        if (IsBusy) throw new InvalidOperationException("Attendi il termine della ricerca prima di salvare.");
        if (!string.IsNullOrWhiteSpace(value.TgrepPath) && !File.Exists(Environment.ExpandEnvironmentVariables(value.TgrepPath)))
            throw new FileNotFoundException("Il percorso tgrep.exe non esiste.");
        if (!string.IsNullOrWhiteSpace(value.IndexPath) && !Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(value.IndexPath)))
            throw new ArgumentException("Usa un percorso assoluto per l’indice.");
        if (!string.IsNullOrWhiteSpace(value.EditorPath) && !File.Exists(Environment.ExpandEnvironmentVariables(value.EditorPath)))
            throw new FileNotFoundException("Il percorso dell’editor non esiste. Seleziona il file .exe.");
        if (!string.IsNullOrWhiteSpace(value.EditorPath) && !value.EditorArguments.Contains("$FILE"))
            throw new ArgumentException("Gli argomenti dell’editor devono contenere $FILE.");
        value = value with { RecentFolders = Settings.RecentFolders };
        await store.SaveAsync(value);
        Settings = value; settingsReadable = true;
        IgnoreCase = value.IgnoreCase; Literal = value.Literal;
        ThemeChanged?.Invoke(value.Theme);
        await CheckTgrepAsync();
    }

    private async Task SearchAsync()
    {
        if (string.IsNullOrEmpty(Query)) { ReportError(new ArgumentException("Inserisci il testo o una espressione regolare.")); return; }
        BeginOperation();
        Files.Clear(); byPath.Clear(); SelectedFile = null; MatchCount = 0;
        var channel = Channel.CreateBounded<SearchMatch>(new BoundedChannelOptions(1024)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var token = operation!.Token;
        var options = new SearchOptions(Folder, Query, Include, Exclude, IgnoreCase, Literal, WholeWord, UseIndex);
        var snapshot = Settings;
        Task producer = Task.Run(async () =>
        {
            try { await client.SearchAsync(options, snapshot, match => channel.Writer.WriteAsync(match, token).AsTask(), token); }
            finally { channel.Writer.TryComplete(); }
        });
        try
        {
            await foreach (var first in channel.Reader.ReadAllAsync())
            {
                var batch = new List<SearchMatch>(128) { first };
                while (batch.Count < 128 && channel.Reader.TryRead(out var next)) batch.Add(next);
                foreach (var match in batch)
                {
                    if (!byPath.TryGetValue(match.FullPath, out var file))
                    {
                        file = new(match.FullPath, match.RelativePath);
                        byPath.Add(match.FullPath, file); Files.Add(file);
                    }
                    file.Add(match); MatchCount += match.MatchCount;
                    SelectedFile ??= file;
                }
                // Yield between bounded batches so input and paint keep running.
                await Task.Delay(1);
            }
            await producer;
            DrainUpdates();
            Message = MatchCount == 0 ? "Nessuna corrispondenza. Prova a cambiare testo o filtri." : $"Ricerca completata · {Files.Count:N0} file";
            try { await RememberFolderAsync(); }
            catch (Exception ex) { ReportWarning("Risultati disponibili; cronologia non salvata: " + ex.Message); }
        }
        catch (OperationCanceledException) { Message = "Ricerca annullata · risultati parziali conservati"; }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            operation!.Cancel();
            // Always observe the producer, including UI/consumer failures.
            try { await producer; } catch { }
            EndOperation();
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
            DrainUpdates(); Message = "Server pronto. Avvia una ricerca per aggiornare i risultati.";
        }
        catch (OperationCanceledException) { Message = "Operazione annullata"; }
        catch (Exception ex) { ReportError(ex); }
        finally { EndOperation(); }
    }

    private void BeginOperation()
    {
        DrainUpdates(); Warning = ""; WarningCount = 0;
        operation = new(); IsBusy = true; clock.Restart(); Message = "Preparazione…";
        Server = "Controllo server…"; IndexedFiles = "—";
    }
    private void EndOperation()
    {
        Interlocked.Exchange(ref pendingProgress, null);
        clock.Stop(); Elapsed = $"{clock.Elapsed.TotalSeconds:N1} s";
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
            Server = status.Running ? $"PID {status.Pid} · porta {status.Port}"
                : status.Description == "Scansione senza indice" ? status.Description : "Server non attivo";
            IndexedFiles = status.Files?.ToString("N0") ?? "—";
        }
        int count = 0;
        while (count++ < 200 && pendingLogs.TryDequeue(out var log))
        {
            Logs.Add(log.ToString());
            if (Logs.Count > 1000) Logs.RemoveAt(0);
            if (log.IsWarning) ReportWarning(log.Text);
        }
        if (IsBusy) Elapsed = $"{clock.Elapsed.TotalSeconds:N1} s";
    }
    public void ReportError(Exception ex)
    {
        if (ex is TgrepMissingException) MissingTgrep = true;
        Warning = ex.Message; WarningCount++; Message = "Operazione non completata";
        Logs.Add($"{DateTime.Now:HH:mm:ss} [GUI] {ex.Message}");
    }
    private void ReportWarning(string text) { Warning = text; WarningCount++; }

    public async Task ShutdownAsync()
    {
        IsReady = false;
        OnPropertyChanged(nameof(CanEdit));
        operation?.Cancel();
        if (SearchCommand.ExecutionTask is { } search) await search;
        if (RestartCommand.ExecutionTask is { } restart) await restart;
        timer.Stop();
        await Task.Run(async () => await client.DisposeAsync());
    }
}
