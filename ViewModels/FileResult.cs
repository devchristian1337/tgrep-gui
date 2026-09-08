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
    public ObservableCollection<SearchMatch> Lines { get; } = [];
    private int count;
    public int Count => count;
    public void Add(SearchMatch match) { Lines.Add(match); count += match.MatchCount; }
    public void NotifyCount() => OnPropertyChanged(nameof(Count));
}
