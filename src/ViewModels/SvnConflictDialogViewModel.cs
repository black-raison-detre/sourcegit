using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public record SvnConflictVersionOption(string Id, string Label, Native.Svn.ConflictVersion Version)
    {
        public byte[] Content => Version.Content;
        public bool IsBinary => Version.IsBinary;
        public string Details => Version.Details;
    }

    public record SvnConflictResolutionOption(string Accept, string Label, string Description);

    public partial class SvnConflictDialogViewModel : ObservableObject, IDisposable
    {
        public SvnWorkingCopy WorkingCopy { get; }
        public SvnWorkingCopyEntry Entry { get; }
        public string Path => Entry.Path;
        public string FullPath => WorkingCopy.FullPath;
        public Native.Svn.ConflictSnapshot Snapshot => _snapshot;
        public bool HasTextConflict => _snapshot?.HasTextConflict == true;
        public bool HasPropertyConflict => _snapshot?.HasPropertyConflict == true;
        public bool HasTreeConflict => _snapshot?.HasTreeConflict == true;
        public string KindsText => _snapshot == null ? string.Empty : string.Join(" · ", new[]
        {
            HasTextConflict ? App.Text("SVN.Conflict.Kind.Text") : null,
            HasPropertyConflict ? App.Text("SVN.Conflict.Kind.Properties") : null,
            HasTreeConflict ? App.Text("SVN.Conflict.Kind.Tree") : null,
        }.Where(value => !string.IsNullOrEmpty(value)));
        public string Description => _snapshot?.Description ?? string.Empty;
        public string PropertyRejectText => FormatPropertyReject(_snapshot?.PropertyReject);
        public string PropertyDiffText => _propertyDiffText;
        public string UnavailableVersionsText => _unavailableVersionsText;
        public bool HasUnavailableVersions => !string.IsNullOrWhiteSpace(_unavailableVersionsText);
        public string TreeDetails => _snapshot?.Description ?? string.Empty;
        public IReadOnlyList<SvnConflictVersionOption> FromVersions => _fromVersions;
        public IReadOnlyList<SvnConflictVersionOption> ToVersions => _toVersions;
        public IReadOnlyList<SvnConflictResolutionOption> ResolutionChoices => _resolutionChoices;
        public string ResolutionHint => _selectedResolution?.Description ??
            (_resolutionChoices.Count == 0 ? App.Text("SVN.Conflict.NoSafeResolutionForSnapshot") : App.Text("SVN.Conflict.ChooseResolution"));
        public TextDiffContext DiffContent => _diffContent;
        public string CompareNotice => _compareNotice;
        public bool IsLoading => _isLoading;
        public bool IsResolving => _isResolving;
        public string CancelButtonContent => IsResolving ? App.Text("SVN.Conflict.CancelOperation") : App.Text("Cancel");
        public bool HasError => !string.IsNullOrEmpty(_errorMessage);
        public string ErrorMessage => _errorMessage;
        public string ErrorDetails => _errorDetails;
        public bool IsStaleSnapshot => _isStaleSnapshot;
        public bool NeedsReload => _needsReload;
        public bool ManualTreeChangesConfirmed
        {
            get => _manualTreeChangesConfirmed;
            set
            {
                if (SetProperty(ref _manualTreeChangesConfirmed, value))
                    OnPropertyChanged(nameof(CanResolve));
            }
        }
        public SvnConflictVersionOption SelectedFromVersion
        {
            get => _selectedFromVersion;
            set
            {
                if (SetProperty(ref _selectedFromVersion, value))
                    _ = LoadComparisonAsync();
            }
        }
        public SvnConflictVersionOption SelectedToVersion
        {
            get => _selectedToVersion;
            set
            {
                if (SetProperty(ref _selectedToVersion, value))
                    _ = LoadComparisonAsync();
            }
        }
        public SvnConflictResolutionOption SelectedResolution
        {
            get => _selectedResolution;
            set
            {
                if (SetProperty(ref _selectedResolution, value))
                {
                    OnPropertyChanged(nameof(HasSelectedResolution));
                    OnPropertyChanged(nameof(ResolutionHint));
                    OnPropertyChanged(nameof(CanResolve));
                }
            }
        }
        public bool HasSelectedResolution => _selectedResolution != null;
        public bool CanChooseResolution => !_disposed && !_isLoading && !IsResolving && !_needsReload &&
            WorkingCopy.CanRefresh && _resolutionChoices.Count > 0;
        public string NoticeMessage => _needsReload
            ? App.Text(_isStaleSnapshot ? "SVN.Conflict.ReloadAfterStale" : "SVN.Conflict.ReloadAfterFailure")
            : _noticeMessage;
        public bool CanReload => !_disposed && !_isLoading && !WorkingCopy.IsBusy;
        public bool CanResolve => !_disposed && _snapshot != null && !_isLoading && !IsResolving &&
            !_needsReload && WorkingCopy.CanRefresh && _selectedResolution != null &&
            _snapshot.AvailableAccepts?.Contains(_selectedResolution.Accept, StringComparer.Ordinal) == true &&
            (!HasTreeConflict || ManualTreeChangesConfirmed);

        public SvnConflictDialogViewModel(SvnWorkingCopy workingCopy, SvnWorkingCopyEntry entry)
        {
            WorkingCopy = workingCopy ?? throw new ArgumentNullException(nameof(workingCopy));
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            WorkingCopy.PropertyChanged += OnWorkingCopyPropertyChanged;
        }

        public async Task LoadAsync()
        {
            if (_disposed || _isLoading || !WorkingCopy.CanRefresh)
                return;

            _inspectionCancellation?.Cancel();
            _comparisonCancellation?.Cancel();
            using var cancellation = new CancellationTokenSource();
            _inspectionCancellation = cancellation;
            _isLoading = true;
            _errorMessage = string.Empty;
            _errorDetails = string.Empty;
            _noticeMessage = string.Empty;
            _needsReload = false;
            _isStaleSnapshot = false;
            _selectedResolution = null;
            _manualTreeChangesConfirmed = false;
            _snapshot = null;
            _propertyDiffText = string.Empty;
            _unavailableVersionsText = string.Empty;
            _resolutionChoices = [];
            _fromVersions = [];
            _toVersions = [];
            _selectedFromVersion = null;
            _selectedToVersion = null;
            _diffContent = null;
            _compareNotice = App.Text("SVN.Conflict.Loading");
            NotifyState();

            var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
            var configDirectory = Preferences.Instance.SvnConfigDirectory;
            try
            {
                var result = await Task.Run(() => Native.Svn.GetConflictAsync(executable, configDirectory,
                    WorkingCopy.FullPath, Entry.Path, cancellation.Token), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_disposed || _inspectionCancellation != cancellation)
                    return;

                if (result.Status != "Ready" || result.Conflict == null)
                {
                    _errorMessage = App.Text("SVN.Conflict.InspectFailed", Entry.Path);
                    _errorDetails = result.Details ?? string.Empty;
                    _compareNotice = App.Text("SVN.Conflict.NoComparison");
                    _needsReload = true;
                    return;
                }

                _snapshot = result.Conflict;
                _unavailableVersionsText = BuildUnavailableVersionsText(_snapshot);
                _resolutionChoices = BuildResolutionChoices(_snapshot);
                _fromVersions = BuildVersionOptions(_snapshot);
                _toVersions = BuildVersionOptions(_snapshot);
                _selectedFromVersion = FindVersion(_fromVersions, "base") ?? _fromVersions.FirstOrDefault();
                _selectedToVersion = FindVersion(_toVersions, "working") ?? _toVersions.FirstOrDefault();
                if (HasPropertyConflict)
                    await LoadPropertyDiffAsync(executable, configDirectory, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_disposed || _inspectionCancellation != cancellation)
                    return;
                await LoadComparisonAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                if (_inspectionCancellation == cancellation && !_disposed)
                    _compareNotice = App.Text("SVN.Conflict.InspectionCancelled");
            }
            catch (Exception ex)
            {
                if (_inspectionCancellation == cancellation && !_disposed)
                {
                    _errorMessage = App.Text("SVN.Conflict.InspectFailed", Entry.Path);
                    _errorDetails = ex.Message;
                    _compareNotice = App.Text("SVN.Conflict.NoComparison");
                    _needsReload = true;
                }
            }
            finally
            {
                if (_inspectionCancellation == cancellation)
                {
                    _inspectionCancellation = null;
                    _isLoading = false;
                    if (!_disposed)
                        NotifyState();
                }
            }
        }

        public async Task<bool> ResolveAsync(Func<string, Task<bool>> confirm)
        {
            if (!CanResolve || confirm == null)
                return false;

            var snapshot = _snapshot;
            var choice = _selectedResolution;
            var accept = choice.Accept;
            var scope = Path;
            var message = BuildConfirmationMessage(choice);
            var confirmationAccepted = false;
            Native.Svn.WorkingCopyActionResult resolveResult = null;
            _isResolving = true;
            _ownsOperation = false;
            NotifyState();
            bool succeeded;
            try
            {
                succeeded = await WorkingCopy.ExecuteOperationAsync("SVN.Conflict.Resolving", "SVN.Conflict.Resolved",
                    "SVN.Conflict.ResolveFailed", async cancellationToken =>
                {
                    _ownsOperation = true;
                    if (!await confirm(message))
                        return null;
                    confirmationAccepted = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    return resolveResult = await Task.Run(() => Native.Svn.ResolveConflictAsync(
                        Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath),
                        Preferences.Instance.SvnConfigDirectory, WorkingCopy.FullPath, Entry.Path,
                        accept, snapshot.Fingerprint, cancellationToken), cancellationToken);
                }, scope);
            }
            finally
            {
                _isResolving = false;
                _ownsOperation = false;
                if (!_disposed)
                    NotifyState();
            }

            if (_disposed)
                return succeeded;

            if (succeeded)
            {
                _noticeMessage = App.Text("SVN.Conflict.ResolvedNotice", Path);
                _needsReload = false;
                _isStaleSnapshot = false;
                _errorMessage = string.Empty;
                _errorDetails = string.Empty;
                _selectedResolution = null;
                NotifyState();
                return true;
            }

            if (resolveResult == null && !confirmationAccepted)
                return false;

            _errorMessage = WorkingCopy.ActionErrorMessage;
            _errorDetails = WorkingCopy.ActionErrorDetails;
            _isStaleSnapshot = resolveResult?.Status == "WorkingCopyChanged";
            _needsReload = true;
            _noticeMessage = App.Text(_isStaleSnapshot ? "SVN.Conflict.ReloadAfterStale" : "SVN.Conflict.ReloadAfterFailure");
            NotifyState();
            return false;
        }

        public void CancelInspection()
        {
            _inspectionCancellation?.Cancel();
            _comparisonCancellation?.Cancel();
        }

        public void CancelOperation()
        {
            if (_ownsOperation && WorkingCopy.IsMutating)
                WorkingCopy.Cancel();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            WorkingCopy.PropertyChanged -= OnWorkingCopyPropertyChanged;
            _inspectionCancellation?.Cancel();
            _comparisonCancellation?.Cancel();
        }

        private async Task LoadPropertyDiffAsync(string executable, string configDirectory, CancellationToken cancellationToken)
        {
            try
            {
                var result = await Task.Run(() => Native.Svn.GetLocalDiffAsync(executable, configDirectory,
                    WorkingCopy.FullPath, Entry.Path, cancellationToken, propertiesOnly: true), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed)
                    return;

                _propertyDiffText = result.Status == "Ready" && !string.IsNullOrEmpty(result.PropertyDiff)
                    ? BoundText(result.PropertyDiff)
                    : result.Status == "Ready" ? App.Text("SVN.Conflict.PropertyDiffEmpty") :
                        App.Text("SVN.Conflict.PropertyDiffUnavailable", result.Details ?? result.Status);
                OnPropertyChanged(nameof(PropertyDiffText));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _propertyDiffText = App.Text("SVN.Conflict.PropertyDiffUnavailable", ex.Message);
                OnPropertyChanged(nameof(PropertyDiffText));
            }
        }

        private async Task LoadComparisonAsync(CancellationToken cancellationToken = default)
        {
            _comparisonCancellation?.Cancel();
            if (_selectedFromVersion?.Content == null || _selectedToVersion?.Content == null)
            {
                _diffContent = null;
                _compareNotice = App.Text("SVN.Conflict.VersionUnavailable");
                NotifyComparisonState();
                return;
            }

            using var cancellation = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();
            _comparisonCancellation = cancellation;
            _diffContent = null;
            _compareNotice = App.Text("SVN.Conflict.LoadingComparison");
            NotifyComparisonState();
            try
            {
                if (_selectedFromVersion.IsBinary || _selectedToVersion.IsBinary)
                {
                    _compareNotice = App.Text("SVN.Conflict.BinaryComparison");
                    return;
                }

                var compare = await Task.Run(() => Commands.SnapshotDiff.ReadAsync(_selectedFromVersion.Content,
                    _selectedToVersion.Content, cancellation.Token, Preferences.Instance.UseFullTextDiff ? 50000 : 3,
                    Preferences.Instance.IgnoreWhitespaceChangesInDiff, Preferences.Instance.IgnoreCRAtEOLInDiff), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_disposed || _comparisonCancellation != cancellation)
                    return;

                if (compare.Status == "TooLarge")
                    _compareNotice = App.Text("SVN.Diff.TooLarge");
                else if (compare.Status != "Ready")
                    _compareNotice = string.IsNullOrEmpty(compare.Details)
                        ? App.Text("SVN.Conflict.ComparisonFailed") : compare.Details;
                else if (compare.IsBinary)
                    _compareNotice = App.Text("SVN.Conflict.BinaryComparison");
                else if (compare.Diff == null)
                    _compareNotice = App.Text(_selectedFromVersion.Content.Length == 0 && _selectedToVersion.Content.Length == 0
                        ? "SVN.Diff.Empty" : "SVN.Diff.Unchanged");
                else
                {
                    var option = new Models.DiffOption("SVN conflict", "SVN conflict",
                        new Models.Change { Path = Entry.Path });
                    _diffContent = Preferences.Instance.UseSideBySideDiff
                        ? new TwoSideTextDiff(option, compare.Diff)
                        : new CombinedTextDiff(option, compare.Diff);
                    _compareNotice = string.Empty;
                }
            }
            catch (OperationCanceledException)
            {
                if (_comparisonCancellation == cancellation && !_disposed)
                    _compareNotice = App.Text("SVN.Conflict.ComparisonCancelled");
            }
            catch (Exception ex)
            {
                if (_comparisonCancellation == cancellation && !_disposed)
                    _compareNotice = ex.Message;
            }
            finally
            {
                if (_comparisonCancellation == cancellation)
                {
                    _comparisonCancellation = null;
                    if (!_disposed)
                        NotifyComparisonState();
                }
            }
        }

        private string BuildConfirmationMessage(SvnConflictResolutionOption choice)
        {
            var kinds = KindsText;
            if (choice.Accept == "working")
            {
                var text = App.Text("SVN.Conflict.ConfirmWorking", Path, kinds);
                return HasTreeConflict ? text + Environment.NewLine + App.Text("SVN.Conflict.TreeWorkingWarning") : text;
            }

            var version = choice.Accept switch
            {
                "base" => App.Text("SVN.Conflict.Version.Base"),
                "mine-full" => App.Text("SVN.Conflict.Version.LocalBeforeOperation"),
                _ => App.Text("SVN.Conflict.Version.Incoming"),
            };
            return App.Text("SVN.Conflict.ConfirmReplacement", Path, version, kinds) + Environment.NewLine +
                App.Text("SVN.Conflict.ReplacementWarning");
        }

        private static IReadOnlyList<SvnConflictVersionOption> BuildVersionOptions(Native.Svn.ConflictSnapshot snapshot)
        {
            var items = new List<SvnConflictVersionOption>();
            Add("base", "SVN.Conflict.Version.Base", snapshot.Base);
            Add("mine", "SVN.Conflict.Version.LocalBeforeOperation", snapshot.Mine);
            Add("theirs", "SVN.Conflict.Version.Incoming", snapshot.Theirs);
            Add("working", "SVN.Conflict.Version.Working", snapshot.Working);
            return items;

            void Add(string id, string labelKey, Native.Svn.ConflictVersion version)
            {
                if (version?.Content != null)
                    items.Add(new SvnConflictVersionOption(id, App.Text(labelKey), version));
            }
        }

        private static string BuildUnavailableVersionsText(Native.Svn.ConflictSnapshot snapshot)
        {
            var unavailable = new List<string>();
            Add("SVN.Conflict.Version.Base", snapshot.Base);
            Add("SVN.Conflict.Version.LocalBeforeOperation", snapshot.Mine);
            Add("SVN.Conflict.Version.Incoming", snapshot.Theirs);
            Add("SVN.Conflict.Version.Working", snapshot.Working);
            return BoundText(string.Join(Environment.NewLine, unavailable), 8192);

            void Add(string labelKey, Native.Svn.ConflictVersion version)
            {
                if (version?.Content != null)
                    return;
                var detail = string.IsNullOrWhiteSpace(version?.Details)
                    ? App.Text("SVN.Conflict.Version.NoDetails") : BoundText(version.Details, 2048);
                unavailable.Add(App.Text("SVN.Conflict.VersionUnavailableDetail", App.Text(labelKey), detail));
            }
        }

        private static SvnConflictVersionOption FindVersion(IReadOnlyList<SvnConflictVersionOption> values, string id) =>
            values.FirstOrDefault(value => value.Id == id);

        private IReadOnlyList<SvnConflictResolutionOption> BuildResolutionChoices(Native.Svn.ConflictSnapshot snapshot)
        {
            var values = new List<SvnConflictResolutionOption>();
            foreach (var accept in snapshot.AvailableAccepts ?? [])
            {
                if (snapshot.HasTreeConflict && accept != "working")
                    continue;

                var (labelKey, descriptionKey) = accept switch
                {
                    "working" => ("SVN.Conflict.Resolution.Working", "SVN.Conflict.Resolution.WorkingDetails"),
                    "base" => ("SVN.Conflict.Resolution.Base", "SVN.Conflict.Resolution.ReplacementDetails"),
                    "mine-full" => ("SVN.Conflict.Resolution.Local", "SVN.Conflict.Resolution.ReplacementDetails"),
                    "theirs-full" => ("SVN.Conflict.Resolution.Incoming", "SVN.Conflict.Resolution.ReplacementDetails"),
                    _ => (null, null),
                };
                if (labelKey != null)
                    values.Add(new SvnConflictResolutionOption(accept, App.Text(labelKey), App.Text(descriptionKey)));
            }
            return values;
        }

        private static string FormatPropertyReject(Native.Svn.ConflictVersion reject)
        {
            if (reject == null)
                return App.Text("SVN.Conflict.PropertyReportUnavailable");
            if (reject.Content == null)
                return string.IsNullOrEmpty(reject.Details) ? App.Text("SVN.Conflict.PropertyReportUnavailable") :
                    BoundText(reject.Details);
            if (reject.IsBinary)
                return App.Text("SVN.Conflict.PropertyReportBinary");
            if (reject.Content.Length == 0)
                return App.Text("SVN.Conflict.PropertyReportEmpty");
            return BoundText(Encoding.UTF8.GetString(reject.Content));
        }

        private static string BoundText(string value, int maxCharacters = 256 * 1024)
        {
            if (value.Length <= maxCharacters)
                return value;
            return value.Substring(0, maxCharacters) + Environment.NewLine + App.Text("SVN.Conflict.TextTruncated");
        }

        private void OnWorkingCopyPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(SvnWorkingCopy.IsBusy) or nameof(SvnWorkingCopy.IsMutating) or nameof(SvnWorkingCopy.CanRefresh))
            {
                OnPropertyChanged(nameof(CanReload));
                OnPropertyChanged(nameof(CanChooseResolution));
                OnPropertyChanged(nameof(CanResolve));
            }
        }

        private void NotifyState()
        {
            OnPropertyChanged(nameof(Snapshot));
            OnPropertyChanged(nameof(HasTextConflict));
            OnPropertyChanged(nameof(HasPropertyConflict));
            OnPropertyChanged(nameof(HasTreeConflict));
            OnPropertyChanged(nameof(KindsText));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(PropertyRejectText));
            OnPropertyChanged(nameof(PropertyDiffText));
            OnPropertyChanged(nameof(UnavailableVersionsText));
            OnPropertyChanged(nameof(HasUnavailableVersions));
            OnPropertyChanged(nameof(TreeDetails));
            OnPropertyChanged(nameof(FromVersions));
            OnPropertyChanged(nameof(ToVersions));
            OnPropertyChanged(nameof(ResolutionChoices));
            OnPropertyChanged(nameof(ResolutionHint));
            OnPropertyChanged(nameof(SelectedFromVersion));
            OnPropertyChanged(nameof(SelectedToVersion));
            OnPropertyChanged(nameof(SelectedResolution));
            OnPropertyChanged(nameof(HasSelectedResolution));
            OnPropertyChanged(nameof(ManualTreeChangesConfirmed));
            OnPropertyChanged(nameof(CanChooseResolution));
            OnPropertyChanged(nameof(DiffContent));
            OnPropertyChanged(nameof(CompareNotice));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsResolving));
            OnPropertyChanged(nameof(CancelButtonContent));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(ErrorDetails));
            OnPropertyChanged(nameof(IsStaleSnapshot));
            OnPropertyChanged(nameof(NeedsReload));
            OnPropertyChanged(nameof(NoticeMessage));
            OnPropertyChanged(nameof(CanReload));
            OnPropertyChanged(nameof(CanResolve));
        }

        private void NotifyComparisonState()
        {
            OnPropertyChanged(nameof(DiffContent));
            OnPropertyChanged(nameof(CompareNotice));
        }

        private Native.Svn.ConflictSnapshot _snapshot;
        private IReadOnlyList<SvnConflictVersionOption> _fromVersions = [];
        private IReadOnlyList<SvnConflictVersionOption> _toVersions = [];
        private IReadOnlyList<SvnConflictResolutionOption> _resolutionChoices = [];
        private SvnConflictVersionOption _selectedFromVersion;
        private SvnConflictVersionOption _selectedToVersion;
        private SvnConflictResolutionOption _selectedResolution;
        private TextDiffContext _diffContent;
        private string _compareNotice = string.Empty;
        private string _propertyDiffText = string.Empty;
        private string _unavailableVersionsText = string.Empty;
        private string _errorMessage = string.Empty;
        private string _errorDetails = string.Empty;
        private string _noticeMessage = string.Empty;
        private bool _isLoading;
        private bool _isStaleSnapshot;
        private bool _needsReload;
        private bool _manualTreeChangesConfirmed;
        private bool _isResolving;
        private bool _ownsOperation;
        private bool _disposed;
        private CancellationTokenSource _inspectionCancellation;
        private CancellationTokenSource _comparisonCancellation;
    }
}
