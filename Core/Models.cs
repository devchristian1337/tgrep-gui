namespace TgrepGui.Core;

public sealed record SearchOptions(string Folder, string Pattern, string Include = "", string Exclude = "",
    bool IgnoreCase = true, bool Literal = false, bool WholeWord = false, bool UseIndex = true);

public sealed record TextSpan(int Start, int Length);
public sealed class SearchMatch(string fullPath, string relativePath, long lineNumber, string text,
    IReadOnlyList<TextSpan> highlights, int matchCount)
{
    public string FullPath { get; } = fullPath;
    public string RelativePath { get; } = relativePath;
    public long LineNumber { get; } = lineNumber;
    public string Text { get; } = text;
    public IReadOnlyList<TextSpan> Highlights { get; } = highlights;
    public int MatchCount { get; } = matchCount;
}
public sealed record JsonEvent(string Type, string? Path, SearchMatch? Match);
public sealed record ServerStatus(bool HasIndex, int? Pid, int? Port, long? Files, bool Indexing,
    bool WatcherActive, string Description)
{
    public bool Running => Pid is > 0 && Port is > 0;
}
public sealed record TgrepLog(DateTimeOffset Time, string Source, string Text)
{
    public bool IsWarning => Text.Contains("warning", StringComparison.OrdinalIgnoreCase)
        || Text.Contains("scanning every file", StringComparison.OrdinalIgnoreCase);
    public override string ToString() => $"{Time:HH:mm:ss} [{Source}] {Text}";
}

public sealed class TgrepMissingException() : Exception(
    "tgrep.exe was not found. Download the Windows release and select the executable in Settings.");
