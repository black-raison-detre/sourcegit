using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Collections;

namespace SourceGit.ViewModels
{
    public sealed record SvnChangelistFilter(string Name, bool IsUngrouped = false)
    {
        public string Label => Name == null
            ? App.Text(IsUngrouped ? "SVN.Changelist.Ungrouped" : "SVN.Changelist.All")
            : App.Text("SVN.Changelist.Named", Name);
    }

    public partial class SvnWorkingCopy
    {
        public AvaloniaList<SvnWorkingCopyEntry> VisibleEntries { get; } = new();
        public AvaloniaList<SvnChangelistFilter> ChangelistFilters { get; } = new() { new(null), new(null, true) };
        public IReadOnlyList<string> ChangelistNames => Entries.Select(entry => entry.Entry.Changelist)
            .Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        public SvnChangelistFilter SelectedChangelistFilter
        {
            get => _selectedChangelistFilter ?? ChangelistFilters[0];
            set
            {
                if (value == null || IsBusy || _updatingChangelistFilter || value == SelectedChangelistFilter)
                    return;

                _selectedChangelistFilter = value;
                RefreshChangelistView(false);
            }
        }

        public bool HasChangelistFilter => SelectedChangelistFilter.Name != null || SelectedChangelistFilter.IsUngrouped;
        public string ChangelistScopeText => App.Text("SVN.Changelist.Scope", SelectedChangelistFilter.Label);
        public bool IsFilteredEmpty => _status == "Ready" && HasChangelistFilter && VisibleEntries.Count == 0;
        public string FilteredEmptyMessage => App.Text(SelectedChangelistFilter.IsUngrouped
            ? "SVN.Changelist.EmptyUngrouped" : "SVN.Changelist.Empty");
        public bool CanAssignChangelist => CanApplySelection(Native.Svn.CanChangeChangelist) &&
            _selectedEntries.All(entry => !Native.Svn.HasUnsafeChangelistAncestor(entry.Entry, _operationStatusEntries));
        public bool CanRemoveFromChangelist => CanAssignChangelist &&
            _selectedEntries.All(entry => !string.IsNullOrEmpty(entry.Entry.Changelist));

        public void ShowAllChangelists() => SelectedChangelistFilter = ChangelistFilters[0];

        public Task<bool> SetChangelistAsync(IReadOnlyList<Native.Svn.ChangelistSelection> selections, string name)
        {
            if (!CanFileOperations || selections == null || selections.Count == 0 ||
                !Native.Svn.TryValidateChangelistName(name, out _))
                return Task.FromResult(false);

            return ExecuteOperationAsync("SVN.Changelist.Applying", "SVN.Changelist.Assigned", "SVN.Changelist.Failed",
                cancellationToken => Task.Run(() => Native.Svn.SetChangelistAsync(
                    Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath), Preferences.Instance.SvnConfigDirectory,
                    FullPath, selections, name, cancellationToken), cancellationToken),
                string.Join(Environment.NewLine, selections.Select(selection => selection.RelativePath)));
        }

        public async Task<bool> RemoveFromChangelistAsync(Func<string, Task<bool>> confirm)
        {
            if (!CanRemoveFromChangelist)
                return false;

            var selections = _selectedEntries.Select(entry =>
                new Native.Svn.ChangelistSelection(entry.Path, entry.Entry.Changelist)).ToArray();
            var scope = string.Join(Environment.NewLine, selections.Select(selection => selection.RelativePath));
            return await ExecuteOperationAsync("SVN.Changelist.Applying", "SVN.Changelist.Removed", "SVN.Changelist.Failed",
                async cancellationToken =>
                {
                    if (confirm == null || !await confirm(App.Text("SVN.Changelist.RemoveConfirm", selections.Length, scope)))
                        return null;
                    cancellationToken.ThrowIfCancellationRequested();
                    return await Task.Run(() => Native.Svn.RemoveFromChangelistAsync(
                        Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath), Preferences.Instance.SvnConfigDirectory,
                        FullPath, selections, cancellationToken), cancellationToken);
                }, scope);
        }

        private void RefreshChangelistView(bool refreshFilters = true)
        {
            var previousSelection = _selectedEntry;
            _updatingChangelistFilter = true;
            try
            {
                var active = SelectedChangelistFilter;
                _selectedChangelistFilter = active;
                if (refreshFilters)
                {
                    var names = ChangelistNames.ToHashSet(StringComparer.Ordinal);
                    // Preserve the selected object, including a vanished active group.
                    // Clearing/replacing ComboBox items can blank its selection even if
                    // the bound record compares equal to the old one.
                    foreach (var filter in ChangelistFilters.Where(filter => filter.Name != null &&
                        !names.Contains(filter.Name) && filter != active).ToArray())
                        ChangelistFilters.Remove(filter);
                    if (active.Name != null)
                        names.Add(active.Name);
                    foreach (var name in names.OrderBy(name => name, StringComparer.Ordinal))
                    {
                        if (ChangelistFilters.Any(filter => filter.Name == name))
                            continue;
                        var index = 2;
                        while (index < ChangelistFilters.Count && string.CompareOrdinal(ChangelistFilters[index].Name, name) < 0)
                            index++;
                        ChangelistFilters.Insert(index, active.Name == name ? active : new(name));
                    }
                }

                var visible = Entries.Where(entry => MatchesChangelistFilter(entry.Entry)).ToArray();
                _selectedEntries = _selectedEntries.Where(visible.Contains).ToArray();
                if (!visible.Contains(_selectedEntry))
                    SetProperty(ref _selectedEntry, _selectedEntries.FirstOrDefault(), nameof(SelectedEntry));
                VisibleEntries.Clear();
                VisibleEntries.AddRange(visible);
            }
            finally
            {
                _updatingChangelistFilter = false;
            }
            if (previousSelection != _selectedEntry)
            {
                InvalidateDiff();
                _ = RetryDiffAsync();
            }
            NotifyChangelistState();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsEmpty));
            NotifyDiffState();
        }

        private bool MatchesChangelistFilter(Native.Svn.SvnWorkingCopyEntry entry) =>
            SelectedChangelistFilter.Name is { } name
                ? string.Equals(entry.Changelist, name, StringComparison.Ordinal)
                : !SelectedChangelistFilter.IsUngrouped || string.IsNullOrEmpty(entry.Changelist);

        private void NotifyChangelistState()
        {
            OnPropertyChanged(nameof(SelectedChangelistFilter));
            OnPropertyChanged(nameof(ChangelistNames));
            OnPropertyChanged(nameof(HasChangelistFilter));
            OnPropertyChanged(nameof(ChangelistScopeText));
            OnPropertyChanged(nameof(IsFilteredEmpty));
            OnPropertyChanged(nameof(FilteredEmptyMessage));
            OnPropertyChanged(nameof(CanAssignChangelist));
            OnPropertyChanged(nameof(CanRemoveFromChangelist));
        }

        private SvnChangelistFilter _selectedChangelistFilter;
        private bool _updatingChangelistFilter;
    }
}
