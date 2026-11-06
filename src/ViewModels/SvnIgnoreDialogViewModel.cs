using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public partial class SvnIgnoreDialogViewModel : ObservableObject
    {
        public SvnWorkingCopy WorkingCopy { get; }
        public ObservableCollection<string> SelectedPaths { get; } = new();
        public string ParentPath { get; }
        public string PatternsToAdd => _patternsToAdd;
        public bool IsLoading => _isLoading;
        public bool IsExecuting => _isExecuting;
        public bool IsBusy => IsLoading || IsExecuting;
        public bool IsLoaded => _snapshot != null;
        public bool HasError => !string.IsNullOrEmpty(_errorMessage);
        public string ErrorMessage => _errorMessage;
        public string ErrorDetails => _errorDetails;
        public string NoNewPatternsNotice => IsLoaded && !_willChange ? App.Text("SVN.Ignore.AlreadyPresent") : string.Empty;
        public bool HasNoNewPatterns => IsLoaded && !_willChange;
        public bool CanApply => IsLoaded && !IsBusy && !_requiresReload && _willChange &&
            WorkingCopy?.CanFileOperations == true;

        public SvnIgnoreDialogViewModel(SvnWorkingCopy workingCopy, IReadOnlyList<string> selectedPaths)
        {
            WorkingCopy = workingCopy;
            var paths = (selectedPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var path in paths)
                SelectedPaths.Add(path);
            ParentPath = paths.Length == 0 ? string.Empty : ParentOf(paths[0]);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (paths.Length == 0 || paths.Any(path => !ParentOf(path).Equals(ParentPath, comparison)))
                SetError(App.Text("SVN.Ignore.SelectionNotEligible"), string.Empty);
        }

        public async Task<bool> LoadAsync()
        {
            if (IsBusy || WorkingCopy == null || !WorkingCopy.CanFileOperations || string.IsNullOrEmpty(ParentPath))
            {
                SetError(App.Text("SVN.Ignore.WorkingCopyNotReady"), string.Empty);
                return false;
            }

            _isLoading = true;
            _requiresReload = true;
            _errorMessage = string.Empty;
            _errorDetails = string.Empty;
            _patternsToAdd = string.Empty;
            _willChange = false;
            NotifyState();
            _loadCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _loadCancellation = cancellation;
            try
            {
                var result = await WorkingCopy.ReadIgnoreParentAsync(ParentPath, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (result?.Status != "Ready" || result.Snapshot == null)
                {
                    SetError(App.Text("SVN.Ignore.LoadFailed"), result?.Details ?? string.Empty);
                    return false;
                }

                var basenames = SelectedPaths.Select(Basename).ToArray();
                if (!SvnWorkingCopy.TryCreateIgnoreValue(result.Snapshot, basenames, out var fullValue, out var error))
                {
                    SetError(App.Text("SVN.Ignore.PreviewFailed"), error);
                    return false;
                }

                var original = result.Snapshot.Properties.FirstOrDefault(property => property.Name == "svn:ignore")?.Value ?? string.Empty;
                _patternsToAdd = AppendedLines(original, fullValue);
                _willChange = _patternsToAdd.Length > 0;
                _snapshot = result.Snapshot;
                _requiresReload = false;
                return true;
            }
            catch (OperationCanceledException)
            {
                SetError(App.Text("SVN.Ignore.LoadCancelled"), string.Empty);
                return false;
            }
            catch (Exception ex)
            {
                SetError(App.Text("SVN.Ignore.LoadFailed"), ex.Message);
                return false;
            }
            finally
            {
                if (ReferenceEquals(_loadCancellation, cancellation))
                    _loadCancellation = null;
                cancellation.Dispose();
                _isLoading = false;
                NotifyState();
            }
        }

        public async Task<bool> ApplyAsync()
        {
            if (!CanApply)
                return false;

            _isExecuting = true;
            _errorMessage = string.Empty;
            _errorDetails = string.Empty;
            NotifyState();
            var succeeded = false;
            try
            {
                succeeded = await WorkingCopy.IgnoreAsync(_snapshot, SelectedPaths.ToArray());
                if (!succeeded)
                {
                    _requiresReload = true;
                    SetError(WorkingCopy.ActionErrorMessage.Length == 0 ? App.Text("SVN.Ignore.Failed") : WorkingCopy.ActionErrorMessage,
                        WorkingCopy.ActionErrorDetails);
                }
                return succeeded;
            }
            finally
            {
                _isExecuting = false;
                NotifyState();
            }
        }

        public void CancelLoad() => _loadCancellation?.Cancel();
        public void CancelOperation() => WorkingCopy?.CancelAction();

        private void SetError(string message, string details)
        {
            _errorMessage = message ?? string.Empty;
            _errorDetails = details ?? string.Empty;
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(ErrorDetails));
            OnPropertyChanged(nameof(HasError));
        }

        private void NotifyState()
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(NoNewPatternsNotice));
            OnPropertyChanged(nameof(HasNoNewPatterns));
            OnPropertyChanged(nameof(PatternsToAdd));
        }

        private static string ParentOf(string path)
        {
            var normalized = NormalizeLocalPath(path).TrimEnd('/');
            var separator = normalized.LastIndexOf('/');
            return separator < 0 ? "." : normalized[..separator];
        }

        private static string Basename(string path)
        {
            var normalized = NormalizeLocalPath(path).TrimEnd('/');
            var separator = normalized.LastIndexOf('/');
            return separator < 0 ? normalized : normalized[(separator + 1)..];
        }

        private static string NormalizeLocalPath(string path) => OperatingSystem.IsWindows()
            ? (path ?? string.Empty).Replace('\\', '/')
            : path ?? string.Empty;

        private static string AppendedLines(string original, string combined)
        {
            if (!combined.StartsWith(original, StringComparison.Ordinal))
                return combined;

            var appended = combined[original.Length..];
            if (appended.StartsWith("\r\n", StringComparison.Ordinal))
                return appended[2..];
            if (appended.StartsWith('\r') || appended.StartsWith('\n'))
                return appended[1..];
            return appended;
        }

        private Native.Svn.SvnPropertiesSnapshot _snapshot;
        private CancellationTokenSource _loadCancellation;
        private string _patternsToAdd = string.Empty;
        private string _errorMessage = string.Empty;
        private string _errorDetails = string.Empty;
        private bool _isLoading;
        private bool _isExecuting;
        private bool _willChange;
        private bool _requiresReload;
    }
}
