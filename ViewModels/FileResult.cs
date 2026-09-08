using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Data;
using TgrepGui.Core;

namespace TgrepGui.ViewModels;

[Bindable]
public sealed class FileResult(string fullPath, string relativePath) : ObservableObject
{
    public string FullPath { get; } = fullPath;
    public string RelativePath { get; } = relativePath;
    public string Name => Path.GetFileName(FullPath);
    public ObservableCollection<SearchMatch> Lines { get; } = new MatchCollection();
    private int count;
    public int Count => count;
    public bool HasLines { get; private set; }
    public void AddCount(int matchCount) { count += matchCount; }
    public void NotifyCount() => OnPropertyChanged(nameof(Count));
    public long RetainedBytes { get; private set; }
    public void AddLine(SearchMatch match)
    {
        Lines.Add(match);
        RetainedBytes += EstimateBytes(match);
    }
    public static long EstimateBytes(SearchMatch match) => 128L + match.Text.Length * 2L + match.Highlights.Count * 32L;
    public void MarkLoaded() => HasLines = true;
    private sealed class MatchCollection : ObservableCollection<SearchMatch>
    {
        protected override void ClearItems()
        {
            base.ClearItems();
            if (Items is List<SearchMatch> items) items.TrimExcess();
        }
    }

    public void ResetLines()
    {
        Lines.Clear();
        RetainedBytes = 0;
        HasLines = false;
    }
}
