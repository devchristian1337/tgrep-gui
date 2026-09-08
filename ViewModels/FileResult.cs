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
    public int Count { get => count; private set => SetProperty(ref count, value); }
    public void Add(SearchMatch match) { Lines.Add(match); Count += match.MatchCount; }
}
