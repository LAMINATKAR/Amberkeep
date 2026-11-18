using System.Collections.ObjectModel;
using Amberkeep.Core.Exclusions;
using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Paths;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amberkeep.App.ViewModels;

/// <summary>
/// The source folder as a tree with checkboxes, for "back up only selected folders and files". A ticked folder
/// includes everything in it, also what is added later. Folders are listed only when expanded, off the UI thread.
/// The selection is kept as a set of paths; the tree only shows it.
/// </summary>
public sealed partial class SourcePickerViewModel : ObservableObject
{
    private readonly IFileSystem _fileSystem;
    private readonly StringComparer _comparer;
    private readonly bool _caseSensitive;
    private readonly HashSet<string> _selected;
    private readonly List<SourceNodeViewModel> _loaded = [];
    private readonly SourceNodeViewModel _root;

    public SourcePickerViewModel(IFileSystem fileSystem, string sourcePath, IEnumerable<string> selected, bool caseSensitive)
    {
        _fileSystem = fileSystem;
        _caseSensitive = caseSensitive;
        _comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        SourcePath = sourcePath;
        _selected = new HashSet<string>(IncludeFilter.Compact(selected, caseSensitive), _comparer);
        _root = new SourceNodeViewModel(this, null, string.Empty, string.Empty, -1, isDirectory: true, isMissing: false);
    }

    public string SourcePath { get; }

    /// <summary>Visible rows, in tree order.</summary>
    public ObservableCollection<SourceNodeViewModel> Rows { get; } = [];

    [ObservableProperty]
    private string _summaryText = "Nothing selected yet.";

    [ObservableProperty]
    private string? _loadError;

    /// <summary>The selection changed (ticked or unticked).</summary>
    public event EventHandler? Changed;

    /// <summary>The selected paths, shortest form ('/'-separated, sorted).</summary>
    public IReadOnlyList<string> GetSelection() => IncludeFilter.Compact(_selected, _caseSensitive);

    /// <summary>Lists the source folder and opens the folders that lead to selected items.</summary>
    public async Task LoadAsync()
    {
        await LoadChildrenAsync(_root);
        if (_root.LoadError is { } error)
        {
            LoadError = $"The source folder cannot be read: {error}";
        }

        RebuildRows();
        foreach (var path in _selected.OrderBy(p => p.Length).ToList())
        {
            var node = _root;
            var segments = path.Split(PathKey.Separator);
            for (var i = 0; i < segments.Length - 1 && node is not null; i++)
            {
                node = node.Children.FirstOrDefault(c => c.IsDirectory && _comparer.Equals(Key(c.Name), Key(segments[i])));
                if (node is not null && !node.IsExpanded)
                {
                    await SetExpandedAsync(node, true);
                }
            }
        }

        UpdateSummary();
    }

    /// <summary>Opens or closes a folder; its contents are listed the first time.</summary>
    public async Task SetExpandedAsync(SourceNodeViewModel node, bool expanded)
    {
        if (!node.IsDirectory || node.IsExpanded == expanded)
        {
            return;
        }

        if (expanded && !node.IsLoaded)
        {
            await LoadChildrenAsync(node);
        }

        node.SetExpanded(expanded);
        var index = Rows.IndexOf(node);
        if (index < 0)
        {
            return;
        }

        if (expanded)
        {
            var below = new List<SourceNodeViewModel>();
            AddVisible(node, below);
            for (var i = 0; i < below.Count; i++)
            {
                Rows.Insert(index + 1 + i, below[i]);
            }
        }
        else
        {
            while (index + 1 < Rows.Count && Rows[index + 1].Depth > node.Depth)
            {
                Rows.RemoveAt(index + 1);
            }
        }
    }

    /// <summary>True: selected or inside a selected folder. Null: something inside is selected. False: not selected.</summary>
    internal bool? StateOf(SourceNodeViewModel node)
    {
        var key = Key(node.Path);
        if (PathKey.IsSelfOrUnder(_selected, key))
        {
            return true;
        }

        var prefix = key + PathKey.Separator;
        return _selected.Any(p => p.StartsWith(prefix, _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            ? null
            : false;
    }

    /// <summary>Ticks a row (with everything in it) or unticks it (also when only a folder above it was ticked).</summary>
    internal async Task SetCheckedAsync(SourceNodeViewModel node, bool value)
    {
        var key = Key(node.Path);
        var prefix = key + PathKey.Separator;
        var comparison = _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _selected.RemoveWhere(p => p.StartsWith(prefix, comparison));
        if (value)
        {
            _selected.Add(key);
        }
        else if (!_selected.Remove(key) && FindSelectedAncestor(node) is { } ancestor)
        {
            // A folder above was ticked: it is replaced by everything in it except this row.
            _selected.Remove(Key(ancestor.Path));
            for (var current = node; current != ancestor; current = current.Parent!)
            {
                var parent = current.Parent!;
                if (!parent.IsLoaded)
                {
                    await LoadChildrenAsync(parent);
                }

                foreach (var sibling in parent.Children.Where(c => c != current && !c.IsMissing))
                {
                    _selected.Add(Key(sibling.Path));
                }
            }
        }

        foreach (var loaded in _loaded)
        {
            loaded.RaiseChecked();
        }

        UpdateSummary();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private SourceNodeViewModel? FindSelectedAncestor(SourceNodeViewModel node)
    {
        for (var current = node.Parent; current is not null && current != _root; current = current.Parent)
        {
            if (_selected.Contains(Key(current.Path)))
            {
                return current;
            }
        }

        return null;
    }

    private async Task LoadChildrenAsync(SourceNodeViewModel folder)
    {
        IReadOnlyList<FileSystemEntry> entries = [];
        if (!folder.IsMissing)
        {
            var full = PathKey.ToSystemPath(SourcePath, folder.Path);
            try
            {
                // Off the UI thread: a slow or unavailable disk must not freeze the window.
                entries = await Task.Run(() => _fileSystem.ListDirectory(full));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                folder.LoadError = ex.Message;
            }
        }

        var children = entries
            .Where(e => !(folder == _root && string.Equals(e.Name, Core.Scanning.Scanner.ProgramFolderName, StringComparison.OrdinalIgnoreCase)))
            .Select(e => new SourceNodeViewModel(
                this, folder, e.Name, PathKey.Combine(folder.Path, e.Name), folder.Depth + 1,
                e.Kind == EntryKind.Directory && !e.IsLink, isMissing: false))
            .ToList();

        // Selected items that are no longer there are shown, so they can be unticked.
        var names = new HashSet<string>(children.Select(c => Key(c.Name)), _comparer);
        var prefix = folder.Path.Length == 0 ? string.Empty : Key(folder.Path) + PathKey.Separator;
        foreach (var path in _selected.Where(p => prefix.Length == 0 || p.StartsWith(prefix, _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)))
        {
            var rest = path[prefix.Length..];
            var slash = rest.IndexOf(PathKey.Separator);
            var name = slash < 0 ? rest : rest[..slash];
            if (names.Add(name))
            {
                children.Add(new SourceNodeViewModel(
                    this, folder, name, PathKey.Combine(folder.Path, name), folder.Depth + 1, isDirectory: slash >= 0, isMissing: true));
            }
        }

        folder.SetChildren(children
            .OrderByDescending(c => c.IsDirectory)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
        _loaded.AddRange(folder.Children);
    }

    private void RebuildRows()
    {
        Rows.Clear();
        var rows = new List<SourceNodeViewModel>();
        AddVisible(_root, rows);
        foreach (var row in rows)
        {
            Rows.Add(row);
        }
    }

    private static void AddVisible(SourceNodeViewModel parent, List<SourceNodeViewModel> rows)
    {
        foreach (var node in parent.Children)
        {
            rows.Add(node);
            if (node.IsExpanded)
            {
                AddVisible(node, rows);
            }
        }
    }

    private void UpdateSummary()
    {
        var selection = GetSelection();
        if (selection.Count == 0)
        {
            SummaryText = "Nothing selected yet.";
            return;
        }

        var folders = 0;
        var files = 0;
        var missing = 0;
        foreach (var path in selection)
        {
            var full = PathKey.ToSystemPath(SourcePath, path);
            if (_fileSystem.DirectoryExists(full))
            {
                folders++;
            }
            else if (_fileSystem.FileExists(full))
            {
                files++;
            }
            else
            {
                missing++;
            }
        }

        var parts = new List<string>();
        if (folders > 0)
        {
            parts.Add(Services.Format.Count(folders, "folder", "folders"));
        }

        if (files > 0)
        {
            parts.Add(Services.Format.Count(files, "file", "files"));
        }

        SummaryText = (parts.Count > 0 ? string.Join(" and ", parts) + " selected" : "Nothing found")
                      + (missing > 0 ? $"; {missing:N0} selected {(missing == 1 ? "item is" : "items are")} missing in the source" : string.Empty)
                      + ".";
    }

    private static string Key(string path) => IncludeFilter.Normalize(path);
}

/// <summary>A folder or file of the source in the selection tree.</summary>
public sealed partial class SourceNodeViewModel : ObservableObject
{
    private readonly SourcePickerViewModel _owner;
    private List<SourceNodeViewModel> _children = [];

    internal SourceNodeViewModel(
        SourcePickerViewModel owner, SourceNodeViewModel? parent, string name, string path, int depth, bool isDirectory, bool isMissing)
    {
        _owner = owner;
        Parent = parent;
        Name = name;
        Path = path;
        Depth = depth;
        IsDirectory = isDirectory;
        IsMissing = isMissing;
    }

    public string Name { get; }

    /// <summary>'/'-separated path relative to the source.</summary>
    public string Path { get; }

    public int Depth { get; }

    public bool IsDirectory { get; }

    /// <summary>Selected earlier, but no longer in the source.</summary>
    public bool IsMissing { get; }

    public SourceNodeViewModel? Parent { get; }

    public IReadOnlyList<SourceNodeViewModel> Children => _children;

    public bool IsLoaded { get; private set; }

    public bool IsExpanded { get; private set; }

    /// <summary>The folder could not be listed.</summary>
    public string? LoadError { get; internal set; }

    public double Indent => Math.Max(0, Depth) * 18;

    /// <summary>A folder whose contents are unknown yet, or known not to be empty.</summary>
    public bool CanExpand => IsDirectory && (!IsLoaded || _children.Count > 0);

    public string? Note => IsMissing ? "missing" : LoadError is not null ? "cannot be read" : null;

    public bool? IsChecked
    {
        get => _owner.StateOf(this);
        set => _ = _owner.SetCheckedAsync(this, value == true);
    }

    [RelayCommand]
    private Task ToggleExpandedAsync() => _owner.SetExpandedAsync(this, !IsExpanded);

    internal void SetChildren(List<SourceNodeViewModel> children)
    {
        _children = children;
        IsLoaded = true;
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(Note));
    }

    internal void SetExpanded(bool expanded)
    {
        IsExpanded = expanded;
        OnPropertyChanged(nameof(IsExpanded));
    }

    internal void RaiseChecked() => OnPropertyChanged(nameof(IsChecked));
}
