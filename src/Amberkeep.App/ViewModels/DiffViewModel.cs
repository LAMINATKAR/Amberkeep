using System.Collections.ObjectModel;
using Amberkeep.App.Services;
using Amberkeep.Core.Diff;
using Amberkeep.Core.FileSystem;
using Amberkeep.Core.Paths;
using Amberkeep.Core.Safety;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Amberkeep.App.ViewModels;

public enum BannerKind
{
    Info,
    Warning,
    Error,
}

public sealed record BannerViewModel(BannerKind Kind, string Text);

public sealed partial class CategoryFilterViewModel(DiffViewModel owner, DiffCategory category, int count) : ObservableObject
{
    public DiffCategory Category { get; } = category;

    public string Label { get; } = $"{Format.Category(category)} {count:N0}";

    [ObservableProperty]
    private bool _isEnabled = true;

    partial void OnIsEnabledChanged(bool value) => owner.ApplyFilter();
}

/// <summary>
/// Review of a diff before a run (SPEC 6.3): a tree with tri-state checkboxes, category filters, search,
/// live totals of the selection and the safety checks recomputed for it.
/// </summary>
public sealed partial class DiffViewModel : ObservableObject
{
    private readonly IShell _shell;
    private readonly bool _archiveEnabled;
    private readonly Func<IReadOnlySet<DiffItem>, Task> _onRun;
    private readonly Action? _onCancel;
    private readonly DiffNodeViewModel _root;
    private readonly List<DiffNodeViewModel> _allNodes = [];
    private bool _suspendRebuild;

    public DiffViewModel(
        IShell shell,
        DiffResult diff,
        string title,
        bool archiveEnabled,
        Func<IReadOnlySet<DiffItem>, Task> onRun,
        Action? onCancel = null)
    {
        _shell = shell;
        Diff = diff;
        Title = title;
        _archiveEnabled = archiveEnabled;
        _onRun = onRun;
        _onCancel = onCancel;

        _root = BuildTree(diff);
        Filters = new ObservableCollection<CategoryFilterViewModel>(
            diff.Summary.ByCategory
                .OrderBy(p => p.Key)
                .Select(p => new CategoryFilterViewModel(this, p.Key, p.Value.Files + p.Value.Directories)));

        Banners = new ObservableCollection<BannerViewModel>(diff.SafetyIssues
            .Where(i => i.Kind != SafetyIssueKind.MassRemoval)
            .Select(i => new BannerViewModel(
                i.Severity switch
                {
                    SafetySeverity.Blocking => BannerKind.Error,
                    SafetySeverity.RequiresApproval => BannerKind.Warning,
                    _ => BannerKind.Info,
                },
                i.Message)));

        Problems = diff.Errors.Select(e => $"Cannot read ({e.Side.ToString().ToLowerInvariant()}): {e.RelativePath} – {e.Message}")
            .Concat(diff.Skipped.Select(s => $"Skipped ({s.Side.ToString().ToLowerInvariant()}): {s.RelativePath} – {s.Message}"))
            .ToList();

        ApplyFilter();
        RecalculateSelection();
    }

    public DiffResult Diff { get; }

    public string Title { get; }

    public string SourcePath => Diff.SourcePath;

    public string TargetPath => Diff.TargetPath;

    public bool IsBlocked => Diff.IsBlocked;

    public bool HasChanges => Diff.Items.Count > 0;

    public string UnchangedText => Format.Count(Diff.Summary.UnchangedFiles, "file is", "files are") + " unchanged";

    public ObservableCollection<CategoryFilterViewModel> Filters { get; }

    public ObservableCollection<BannerViewModel> Banners { get; }

    public IReadOnlyList<string> Problems { get; }

    public bool HasProblems => Problems.Count > 0;

    public string ProblemsTitle => Format.Count(Problems.Count, "path could not be compared", "paths could not be compared");

    /// <summary>Visible rows. Expanding or collapsing a folder inserts or removes only its rows, so the list keeps its scroll position.</summary>
    [ObservableProperty]
    private ObservableCollection<DiffNodeViewModel> _rows = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectionText = string.Empty;

    [ObservableProperty]
    private string? _massRemovalWarning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private int _selectedCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isStarting;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>Items that will be executed: selected and not errors.</summary>
    public IReadOnlySet<DiffItem> GetSelection() =>
        _allNodes.Where(n => n.OwnSelected).Select(n => n.Item!).ToHashSet();

    internal void ApplyFilter()
    {
        var enabled = Filters?.Where(f => f.IsEnabled).Select(f => f.Category).ToHashSet() ?? [];
        var search = SearchText.Trim();
        ComputeMatches(_root, enabled, search);
        RebuildRows();
    }

    internal void RebuildRows()
    {
        if (_suspendRebuild)
        {
            return;
        }

        var rows = new List<DiffNodeViewModel>();
        AddVisibleChildren(_root, rows);
        Rows = new ObservableCollection<DiffNodeViewModel>(rows);
    }

    /// <summary>Shows or hides the rows below one folder without rebuilding the list.</summary>
    internal void OnExpandedChanged(DiffNodeViewModel node)
    {
        if (_suspendRebuild || SearchText.Trim().Length > 0)
        {
            return;
        }

        var index = Rows.IndexOf(node);
        if (index < 0)
        {
            return;
        }

        if (node.IsExpanded)
        {
            var below = new List<DiffNodeViewModel>();
            AddVisibleChildren(node, below);
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

    private void AddVisibleChildren(DiffNodeViewModel parent, List<DiffNodeViewModel> rows)
    {
        var searching = SearchText.Trim().Length > 0;
        foreach (var node in parent.Children.Where(c => c.Matches))
        {
            rows.Add(node);
            if (node.HasChildren && (node.IsExpanded || searching))
            {
                AddVisibleChildren(node, rows);
            }
        }
    }

    internal void SetChecked(DiffNodeViewModel node, bool value)
    {
        SetSubtree(node, value);
        Recount(node);
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            parent.UpdateCountsFromChildren();
            parent.RaiseChecked();
        }

        RecalculateSelection();
    }

    /// <summary>Shows a row in the system file manager. Only opens a window; nothing is read or written.</summary>
    internal void Reveal(string relativePath, bool onTarget) =>
        _shell.Dialogs.RevealInFileManager(PathKey.ToSystemPath(onTarget ? Diff.TargetPath : Diff.SourcePath, relativePath));

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsStarting = true;
        try
        {
            await _onRun(GetSelection());
        }
        finally
        {
            IsStarting = false;
        }
    }

    private bool CanRun() => !IsBlocked && SelectedCount > 0 && !IsStarting;

    [RelayCommand]
    private void Cancel()
    {
        if (_onCancel is not null)
        {
            _onCancel();
        }
        else
        {
            _shell.ShowDashboard();
        }
    }

    [RelayCommand]
    private void ExpandAll() => SetExpanded(expanded: true);

    [RelayCommand]
    private void CollapseAll() => SetExpanded(expanded: false);

    [RelayCommand]
    private void SelectDefaults()
    {
        foreach (var node in _allNodes.Where(n => n.IsOwnCheckable))
        {
            node.OwnSelected = node.Item!.DefaultSelected;
        }

        Recount(_root);
        foreach (var node in _allNodes)
        {
            node.RaiseChecked();
        }

        RecalculateSelection();
    }

    private void SetExpanded(bool expanded)
    {
        _suspendRebuild = true;
        foreach (var node in _allNodes.Where(n => n.HasChildren))
        {
            node.IsExpanded = expanded;
        }

        _suspendRebuild = false;
        RebuildRows();
    }

    private void RecalculateSelection()
    {
        var selected = GetSelection();
        SelectedCount = selected.Count;
        var summary = DiffSummary.Compute(Diff.Items, selected.Contains, Diff.Summary.UnchangedFiles);

        var parts = new List<string> { Format.Count(selected.Count, "change selected", "changes selected") };
        if (summary.BytesToCopy > 0)
        {
            parts.Add($"{Format.Bytes(summary.BytesToCopy)} to copy");
        }

        if (summary.BytesToRemove > 0)
        {
            parts.Add($"{Format.Bytes(summary.BytesToRemove)} to remove");
        }

        parts.Add($"{Format.Bytes(summary.RequiredFreeSpace(_archiveEnabled))} free space needed");
        SelectionText = string.Join("  ·  ", parts);

        var removedFiles = selected.Count(i => i.Category == DiffCategory.Removed && i.Kind == EntryKind.File);
        MassRemovalWarning = SafetyChecks.CheckMassRemoval(removedFiles, Diff.TargetFileCount, Diff.Options.MassRemovalGuard)?.Message;
    }

    private DiffNodeViewModel BuildTree(DiffResult diff)
    {
        _suspendRebuild = true;
        var root = new DiffNodeViewModel(this, null, string.Empty, string.Empty, -1, isDirectory: true);
        var folders = new Dictionary<string, DiffNodeViewModel>(StringComparer.Ordinal) { [string.Empty] = root };

        foreach (var item in diff.Items)
        {
            var parent = EnsureFolder(ParentOf(item.RelativePath));
            if (item.Kind == EntryKind.Directory
                && folders.TryGetValue(item.RelativePath, out var existing)
                && existing.Item is null
                && existing != root)
            {
                existing.Item = item;
                continue;
            }

            var node = new DiffNodeViewModel(
                this, parent, PathKey.GetName(item.RelativePath), item.RelativePath, parent.Depth + 1, item.Kind == EntryKind.Directory)
            {
                Item = item,
            };
            parent.Children.Add(node);
            _allNodes.Add(node);
            if (node.IsDirectory)
            {
                folders.TryAdd(item.RelativePath, node);
            }
        }

        foreach (var node in _allNodes)
        {
            node.OwnSelected = node.IsOwnCheckable && node.Item!.DefaultSelected;
            node.IsExpanded = node.Depth < 2;
        }

        Recount(root);
        _suspendRebuild = false;
        return root;

        DiffNodeViewModel EnsureFolder(string path)
        {
            if (folders.TryGetValue(path, out var folder))
            {
                return folder;
            }

            var parent = EnsureFolder(ParentOf(path));
            folder = new DiffNodeViewModel(this, parent, PathKey.GetName(path), path, parent.Depth + 1, isDirectory: true);
            parent.Children.Add(folder);
            _allNodes.Add(folder);
            folders[path] = folder;
            return folder;
        }
    }

    private static string ParentOf(string path)
    {
        var index = path.LastIndexOf(PathKey.Separator);
        return index < 0 ? string.Empty : path[..index];
    }

    private static void SetSubtree(DiffNodeViewModel node, bool value)
    {
        if (node.IsOwnCheckable)
        {
            node.OwnSelected = value;
        }

        foreach (var child in node.Children)
        {
            SetSubtree(child, value);
        }
    }

    private static void Recount(DiffNodeViewModel node)
    {
        foreach (var child in node.Children)
        {
            Recount(child);
        }

        node.UpdateCountsFromChildren();
        node.RaiseChecked();
    }

    private static bool ComputeMatches(DiffNodeViewModel node, HashSet<DiffCategory> enabled, string search)
    {
        var any = false;
        foreach (var child in node.Children)
        {
            any |= ComputeMatches(child, enabled, search);
        }

        var own = node.Item is { } item
                  && enabled.Contains(item.Category)
                  && (search.Length == 0 || node.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        node.Matches = own || any;
        return node.Matches;
    }
}

/// <summary>A row of the diff tree: a change, or a folder that only groups changes.</summary>
public sealed partial class DiffNodeViewModel : ObservableObject
{
    private readonly DiffViewModel _owner;

    internal DiffNodeViewModel(DiffViewModel owner, DiffNodeViewModel? parent, string name, string path, int depth, bool isDirectory)
    {
        _owner = owner;
        Parent = parent;
        Name = name;
        Path = path;
        Depth = depth;
        IsDirectory = isDirectory;
    }

    public string Name { get; }

    public string Path { get; }

    public int Depth { get; }

    public bool IsDirectory { get; }

    public DiffNodeViewModel? Parent { get; }

    public List<DiffNodeViewModel> Children { get; } = [];

    public DiffItem? Item { get; internal set; }

    public DiffCategory? Category => Item?.Category;

    public bool HasChildren => Children.Count > 0;

    public bool IsOwnCheckable => Item is not null && Item.Category != DiffCategory.Error;

    public bool IsCheckable => CheckableCount > 0;

    public double Indent => Math.Max(0, Depth) * 18;

    public string CategoryText => Item is null ? string.Empty : Format.Category(Item.Category);

    public string SizeText => Item is { Kind: EntryKind.File } item ? Format.Bytes((item.Source ?? item.Target)!.Length) : string.Empty;

    public string? Detail => Item switch
    {
        { Category: DiffCategory.Error } error => error.Message,
        { Source: { } source, Target: { } target } when PathKey.GetName(source.RelativePath) != PathKey.GetName(target.RelativePath)
            => $"was {PathKey.GetName(target.RelativePath)}",
        _ => null,
    };

    internal bool OwnSelected { get; set; }

    internal int CheckableCount { get; private set; }

    internal int CheckedCount { get; private set; }

    internal bool Matches { get; set; } = true;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => _owner.OnExpandedChanged(this);

    /// <summary>True when everything below is selected, false when nothing is, null when some is.</summary>
    public bool? IsChecked
    {
        get => CheckedCount == 0 ? false : CheckedCount == CheckableCount ? true : null;
        set => _owner.SetChecked(this, value == true);
    }

    /// <summary>Path of this row on the source; null when it is not there (<see cref="DiffCategory.Removed"/>).</summary>
    /// <remarks>A folder that only groups changes is unchanged, so it exists on both sides under the same name.</remarks>
    public string? SourceRelativePath => Item is null ? Path : Item.Source?.RelativePath;

    /// <summary>Path of this row on the target, in the target's own letter case; null when it is not there (<see cref="DiffCategory.New"/>).</summary>
    public string? TargetRelativePath => Item is null ? Path : Item.Target?.RelativePath;

    public bool ExistsOnSource => SourceRelativePath is not null;

    public bool ExistsOnTarget => TargetRelativePath is not null;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand(CanExecute = nameof(ExistsOnSource))]
    private void ShowInSource() => _owner.Reveal(SourceRelativePath!, onTarget: false);

    [RelayCommand(CanExecute = nameof(ExistsOnTarget))]
    private void ShowInTarget() => _owner.Reveal(TargetRelativePath!, onTarget: true);

    internal void UpdateCountsFromChildren()
    {
        CheckableCount = (IsOwnCheckable ? 1 : 0) + Children.Sum(c => c.CheckableCount);
        CheckedCount = (OwnSelected ? 1 : 0) + Children.Sum(c => c.CheckedCount);
    }

    internal void RaiseChecked()
    {
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(IsCheckable));
    }
}
