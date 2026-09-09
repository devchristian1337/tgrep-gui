using System.Text.Json;

namespace TgrepGui.Core;

public sealed record AppSettings
{
    public string TgrepPath { get; init; } = "";
    public bool AutoUpdateEngine { get; init; } = true;
    public string IndexPath { get; init; } = "";
    public string EditorPath { get; init; } = "";
    public string EditorArguments { get; init; } = "\"$FILE\"";
    public string Theme { get; init; } = "System";
    public bool IgnoreCase { get; init; } = true;
    public bool Literal { get; init; }
    public List<string> RecentFolders { get; init; } = [];
}

public sealed class SettingsStore(string? filePath = null)
{
    public string FilePath { get; } = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tgrep-gui", "settings.json");
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(FilePath)) return new();
        await using var stream = File.OpenRead(FilePath);
        var value = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions)
            ?? throw new InvalidDataException("settings.json is empty.");
        return value with
        {
            TgrepPath = value.TgrepPath ?? "", IndexPath = value.IndexPath ?? "",
            EditorPath = value.EditorPath ?? "", EditorArguments = value.EditorArguments ?? "\"$FILE\"",
            Theme = value.Theme is "Light" or "Dark" ? value.Theme : "System",
            RecentFolders = (value.RecentFolders ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Take(12).ToList()
        };
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await gate.WaitAsync();
        var temporary = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { gate.Release(); }
    }
}
