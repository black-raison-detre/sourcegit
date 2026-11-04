using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Collections;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class SvnWorkingCopyEntry
    {
        public Native.Svn.SvnWorkingCopyEntry Entry { get; }
        public string Path => Entry.Path;
        public string StatusText { get; }
        public string DetailsText { get; }
        public bool HasWarning => Entry.IsTreeConflicted || Entry.Properties == "conflicted" ||
            Entry.Item is "conflicted" or "missing" or "obstructed" or "incomplete";

        public SvnWorkingCopyEntry(Native.Svn.SvnWorkingCopyEntry entry)
        {
            Entry = entry;
            var statusKey = entry.Item switch
            {
                "normal" => "Normal",
                "modified" => "Modified",
                "added" => "Added",
                "deleted" => "Deleted",
                "replaced" => "Replaced",
                "missing" => "Missing",
                "unversioned" => "Unversioned",
                "conflicted" => "Conflicted",
                "obstructed" => "Obstructed",
                "ignored" => "Ignored",
                "external" => "External",
                "incomplete" => "Incomplete",
                _ => "Unknown",
            };
            StatusText = App.Text("SVN.WorkingCopy.Status." + statusKey);
            var details = new List<string>();
            if (entry.Properties is "modified" or "conflicted")
                details.Add(App.Text("SVN.WorkingCopy.Details." + (entry.Properties == "conflicted" ? "PropertyConflicted" : "PropertyModified")));
            if (entry.IsTreeConflicted)
                details.Add(App.Text("SVN.WorkingCopy.Details.TreeConflict"));
            if (entry.IsCopied)
                details.Add(App.Text("SVN.WorkingCopy.Details.Copied"));
            if (entry.IsSwitched)
                details.Add(App.Text("SVN.WorkingCopy.Details.Switched"));
            if (entry.IsFileExternal)
                details.Add(App.Text("SVN.WorkingCopy.Status.External"));
            if (!string.IsNullOrEmpty(entry.MovedFrom))
                details.Add(App.Text("SVN.WorkingCopy.Details.MovedFrom", entry.MovedFrom));
            if (!string.IsNullOrEmpty(entry.MovedTo))
                details.Add(App.Text("SVN.WorkingCopy.Details.MovedTo", entry.MovedTo));
            if (!string.IsNullOrEmpty(entry.Changelist))
                details.Add(App.Text("SVN.WorkingCopy.Details.Changelist", entry.Changelist));
            DetailsText = string.Join(" · ", details);
        }
    }

    // Local inspection has its own lifetime and errors so remote history/authentication
    // cannot block access to the working copy.
    public class SvnWorkingCopy : ObservableObject, IDisposable
    {
        public string FullPath { get; }
        public AvaloniaList<SvnWorkingCopyEntry> Entries { get; } = new();

        public SvnWorkingCopyEntry SelectedEntry
        {
            get => _selectedEntry;
            set
            {
                if (SetProperty(ref _selectedEntry, value))
                {
                    InvalidateDiff();
                    _ = RetryDiffAsync();
                }
            }
        }

        public bool IsLoading => _isLoading;
        public bool CanRefresh => !_disposed && !IsLoading;
        public bool HasError => !string.IsNullOrEmpty(_status) && _status != "Ready";
        public string ErrorMessage => !HasError ? string.Empty : App.Text(_status switch
        {
            "Cancelled" => "SVN.WorkingCopy.Cancelled",
            "InvalidWorkingCopy" or "NotWorkingCopy" => "SVN.InvalidWorkingCopy",
            _ => "SVN.WorkingCopy.Failed",
        });
        public string ErrorDetails => _errorDetails;
        public bool IsEmpty => _status == "Ready" && Entries.Count == 0;
        public string Title => App.Text("SVN.WorkingCopy.Title", Entries.Count);
        public bool HasSelection => SelectedEntry != null;
        public string SelectionNotice => SelectedEntry == null ? string.Empty :
            SelectedEntry.Entry.Item == "missing" || _diffIsMissing ? App.Text("SVN.WorkingCopy.MissingNotice") :
            SelectedEntry.Entry.IsTreeConflicted || SelectedEntry.Entry.Item == "conflicted" || SelectedEntry.Entry.Properties == "conflicted"
                ? App.Text("SVN.WorkingCopy.ConflictNotice") : string.Empty;

        public bool IsLoadingDiff => _isLoadingDiff;
        public bool CanRetryDiff => !_disposed && !IsLoading && !IsLoadingDiff && HasSelection;
        public bool HasDiffError => !string.IsNullOrEmpty(_diffStatus) && _diffStatus != "Ready";
        public string DiffErrorMessage => !HasDiffError ? string.Empty :
            App.Text(_diffStatus == "Cancelled" ? "SVN.Diff.Cancelled" : "SVN.WorkingCopy.DiffFailed");
        public string DiffErrorDetails => _diffErrorDetails;
        public string DiffNotice => !HasSelection ? App.Text("SVN.WorkingCopy.SelectFile") : _diffNotice;
        public string DiffTitle => SelectedEntry?.Path ?? string.Empty;
        public string DiffBeforeText => _diffBeforeText;
        public string DiffAfterText => _diffAfterText;
        public TextDiffContext DiffContent => _diffContent;
        public bool IsTextDiff => DiffContent != null;
        public string PropertyDiffContent => _propertyDiffContent;
        public bool HasPropertyDiff => !string.IsNullOrEmpty(PropertyDiffContent);
        public string PropertyNotice => App.Text(!HasSelection ? "SVN.WorkingCopy.SelectFile" :
            IsLoadingDiff ? "SVN.WorkingCopy.Loading" : _diffIsTooLarge ? "SVN.Diff.TooLarge" : "SVN.WorkingCopy.PropertiesEmpty");

        public bool DiffModeSideBySide
        {
            get => Preferences.Instance.UseSideBySideDiff;
            set
            {
                Preferences.Instance.UseSideBySideDiff = value;
                if (_diffContent != null && _diffContent.IsSideBySide() != value)
                    _diffContent = _diffContent.SwitchMode();
                NotifyDiffState();
            }
        }

        public SvnWorkingCopy(string fullPath)
        {
            FullPath = fullPath;
            Preferences.Instance.PropertyChanged += OnDiffPreferencesChanged;
        }

        public async Task RefreshAsync()
        {
            if (!CanRefresh)
                return;

            var selectedPath = SelectedEntry?.Path;
            SetProperty(ref _selectedEntry, null, nameof(SelectedEntry));
            InvalidateDiff();
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _isLoading = true;
            _status = string.Empty;
            _errorDetails = string.Empty;
            NotifyState();
            var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
            var configDirectory = Preferences.Instance.SvnConfigDirectory;
            try
            {
                var result = await Task.Run(() => Native.Svn.GetStatusAsync(executable, configDirectory, FullPath,
                    cancellation.Token), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_disposed || _cancellation != cancellation)
                    return;

                _status = result.Status;
                _errorDetails = result.Details;
                if (result.Status == "Ready")
                {
                    Entries.Clear();
                    Entries.AddRange(result.Entries.Select(entry => new SvnWorkingCopyEntry(entry)));
                    SetProperty(ref _selectedEntry, Entries.FirstOrDefault(entry => entry.Path == selectedPath), nameof(SelectedEntry));
                }
            }
            catch (OperationCanceledException)
            {
                if (_cancellation == cancellation)
                    _status = "Cancelled";
            }
            catch
            {
                if (_cancellation == cancellation)
                    _status = "Failed";
            }
            finally
            {
                if (_cancellation == cancellation)
                {
                    _cancellation = null;
                    _isLoading = false;
                    if (!_disposed)
                    {
                        NotifyState();
                        _ = RetryDiffAsync();
                    }
                }
            }
        }

        public void Cancel() => _cancellation?.Cancel();
        public void CancelDiff() => _diffCancellation?.Cancel();

        public Task RetryDiffAsync() => !_disposed && !IsLoading && !IsLoadingDiff && HasSelection ? LoadDiffAsync() : Task.CompletedTask;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Preferences.Instance.PropertyChanged -= OnDiffPreferencesChanged;
            _cancellation?.Cancel();
            _diffCancellation?.Cancel();
        }

        private void InvalidateDiff()
        {
            _diffCancellation?.Cancel();
            _diffCancellation = null;
            _isLoadingDiff = false;
            _diffStatus = string.Empty;
            _diffErrorDetails = string.Empty;
            _diffNotice = string.Empty;
            _diffBeforeText = string.Empty;
            _diffAfterText = string.Empty;
            _diffContent = null;
            _propertyDiffContent = string.Empty;
            _diffIsMissing = false;
            _diffIsTooLarge = false;
            NotifyDiffState();
        }

        private void OnDiffPreferencesChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(Preferences.UseSideBySideDiff))
                return;

            if (_diffContent != null && _diffContent.IsSideBySide() != DiffModeSideBySide)
                _diffContent = _diffContent.SwitchMode();
            NotifyDiffState();
        }

        private async Task LoadDiffAsync()
        {
            InvalidateDiff();
            using var cancellation = new CancellationTokenSource();
            _diffCancellation = cancellation;
            var selected = SelectedEntry;
            _isLoadingDiff = true;
            NotifyDiffState();
            var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
            var configDirectory = Preferences.Instance.SvnConfigDirectory;
            var contextLines = Preferences.Instance.UseFullTextDiff ? 50000 : 3;
            var ignoreWhitespace = Preferences.Instance.IgnoreWhitespaceChangesInDiff;
            var ignoreCRAtEOL = Preferences.Instance.IgnoreCRAtEOLInDiff;
            try
            {
                var result = await Task.Run(() => Native.Svn.GetLocalDiffAsync(executable, configDirectory, FullPath,
                    selected.Path, cancellation.Token, contextLines: contextLines,
                    ignoreWhitespace: ignoreWhitespace, ignoreCRAtEOL: ignoreCRAtEOL), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_disposed || _diffCancellation != cancellation)
                    return;

                _diffStatus = result.Status;
                _diffErrorDetails = result.Details;
                _propertyDiffContent = result.PropertyDiff ?? string.Empty;
                _diffIsMissing = result.IsMissing;
                _diffBeforeText = string.IsNullOrEmpty(result.BeforeLabel) ? string.Empty :
                    App.Text("SVN.Diff.Before", SideTitle(result.BeforeLabel, selected));
                _diffAfterText = string.IsNullOrEmpty(result.AfterLabel) ? string.Empty :
                    App.Text("SVN.Diff.After", SideTitle(result.AfterLabel, selected));
                if (result.Status == "TooLarge")
                    SetNotice("SVN.Diff.TooLarge");
                else if (result.Status == "Missing" && result.MissingBaseContent == null)
                    SetNotice("SVN.WorkingCopy.MissingNotice");
                else if (result.Status is "Obstructed" or "Incomplete" or "External" or "Unsupported")
                    SetNotice("SVN.WorkingCopy.UnavailableNotice");
                else if (result.Status is "Directory" or "Special" or "Binary" or "Ready" or "Missing")
                {
                    if (result.IsDirectory || result.Status == "Directory")
                        SetNotice("SVN.WorkingCopy.DirectoryNotice");
                    else if (result.IsSpecial || result.Status == "Special")
                        SetNotice("SVN.Diff.Special");
                    else if (result.IsBinary || result.Status == "Binary")
                        SetNotice("SVN.WorkingCopy.BinaryNotice");
                    else
                    {
                        var comparison = result.UnversionedContent != null || result.MissingBaseContent != null
                            ? await Task.Run(() => Commands.SnapshotDiff.ReadAsync(result.MissingBaseContent ?? [], result.UnversionedContent ?? [],
                                cancellation.Token, contextLines, ignoreWhitespace, ignoreCRAtEOL), cancellation.Token)
                            : await Task.Run(() => ParseLocalPatch(result.Patch, cancellation.Token), cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (_disposed || _diffCancellation != cancellation)
                            return;

                        _diffStatus = comparison.Status;
                        _diffErrorDetails = comparison.Details;
                        if (comparison.Status == "TooLarge")
                            SetNotice("SVN.Diff.TooLarge");
                        else if (comparison.Status == "Ready")
                        {
                            if (comparison.IsBinary)
                                SetNotice("SVN.WorkingCopy.BinaryNotice");
                            else if (comparison.Diff != null)
                            {
                                // Keep Git's local hunk mutation commands disabled for SVN.
                                var option = new Models.DiffOption("BASE", "WORKING", new Models.Change { Path = selected.Path });
                                _diffContent = DiffModeSideBySide
                                    ? new TwoSideTextDiff(option, comparison.Diff)
                                    : new CombinedTextDiff(option, comparison.Diff);
                            }
                            else
                            {
                                SetNotice(result.UnversionedContent?.Length == 0 ? "SVN.Diff.Empty" : "SVN.Diff.Unchanged");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (_diffCancellation == cancellation)
                    _diffStatus = "Cancelled";
            }
            catch
            {
                if (_diffCancellation == cancellation)
                    _diffStatus = "Failed";
            }
            finally
            {
                if (_diffCancellation == cancellation)
                {
                    _diffCancellation = null;
                    _isLoadingDiff = false;
                    if (!_disposed)
                        NotifyDiffState();
                }
            }
        }

        private static Commands.SnapshotDiff.Result ParseLocalPatch(byte[] patch, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (patch == null || patch.Length == 0)
                return new Commands.SnapshotDiff.Result("Ready");

            var lines = 0;
            var lineLength = 0;
            foreach (var value in patch)
            {
                if (value == (byte)'\n')
                {
                    if (++lines > 12008)
                        return new Commands.SnapshotDiff.Result("TooLarge");
                    lineLength = 0;
                }
                else if (++lineLength > 16384)
                {
                    return new Commands.SnapshotDiff.Result("TooLarge");
                }
            }

            var result = Commands.Diff.ParseSnapshotPatch(patch);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.TextDiff?.Lines.Count > 12000)
                return new Commands.SnapshotDiff.Result("TooLarge");
            return new Commands.SnapshotDiff.Result("Ready", result.TextDiff, result.IsBinary);
        }

        private static string SideTitle(string label, SvnWorkingCopyEntry selected)
        {
            if (label == "absent")
                return App.Text("SVN.Diff.Absent");
            if (label == "BASE")
                return selected.Path + " · " + App.Text("SVN.WorkingCopy.Base") +
                    (selected.Entry.Revision.HasValue ? $" @ r{selected.Entry.Revision.Value}" : string.Empty);
            return selected.Path + " · " + App.Text("SVN.WorkingCopy.Local");
        }

        private void SetNotice(string key)
        {
            _diffStatus = "Ready";
            _diffNotice = App.Text(key);
            _diffIsTooLarge = key == "SVN.Diff.TooLarge";
        }

        private void NotifyState()
        {
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(CanRefresh));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(ErrorDetails));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Title));
            NotifyDiffState();
        }

        private void NotifyDiffState()
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectionNotice));
            OnPropertyChanged(nameof(IsLoadingDiff));
            OnPropertyChanged(nameof(CanRetryDiff));
            OnPropertyChanged(nameof(HasDiffError));
            OnPropertyChanged(nameof(DiffErrorMessage));
            OnPropertyChanged(nameof(DiffErrorDetails));
            OnPropertyChanged(nameof(DiffNotice));
            OnPropertyChanged(nameof(DiffTitle));
            OnPropertyChanged(nameof(DiffBeforeText));
            OnPropertyChanged(nameof(DiffAfterText));
            OnPropertyChanged(nameof(DiffContent));
            OnPropertyChanged(nameof(IsTextDiff));
            OnPropertyChanged(nameof(DiffModeSideBySide));
            OnPropertyChanged(nameof(PropertyDiffContent));
            OnPropertyChanged(nameof(HasPropertyDiff));
            OnPropertyChanged(nameof(PropertyNotice));
        }

        private SvnWorkingCopyEntry _selectedEntry;
        private CancellationTokenSource _cancellation;
        private CancellationTokenSource _diffCancellation;
        private TextDiffContext _diffContent;
        private bool _disposed;
        private bool _isLoading;
        private bool _isLoadingDiff;
        private bool _diffIsMissing;
        private bool _diffIsTooLarge;
        private string _status = string.Empty;
        private string _errorDetails = string.Empty;
        private string _diffStatus = string.Empty;
        private string _diffErrorDetails = string.Empty;
        private string _diffNotice = string.Empty;
        private string _diffBeforeText = string.Empty;
        private string _diffAfterText = string.Empty;
        private string _propertyDiffContent = string.Empty;
    }
}
