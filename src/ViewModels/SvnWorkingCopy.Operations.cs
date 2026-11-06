using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public partial class SvnWorkingCopy
    {
        public IReadOnlyList<SvnWorkingCopyEntry> SelectedEntries => _selectedEntries;
        public string SelectionSummary => App.Text("SVN.WorkingCopy.SelectedCount", _selectedEntries.Count);
        public bool CanSelectedActions => CanRefresh && _status == "Ready" && _selectedEntries.Count > 0;
        public bool CanFileOperations => CanRefresh && _status == "Ready";
        public bool CanInspectConflict => CanRefresh && _status == "Ready" && _selectedEntries.Count == 1 &&
            CanInspectConflictEntry(_selectedEntries[0]);
        public bool CanRevert => CanApplySelection(Native.Svn.CanRevert) && HasSafeOperationSelection() &&
            (_selectedEntries.Count == 1 || _selectedEntries.All(entry => entry.Path != "."));
        public bool CanCommit => CanApplySelection(Native.Svn.CanCommit) &&
            Native.Svn.CanCommitSelection(_selectedEntries.Select(entry => entry.Entry).ToArray(), _operationStatusEntries, out _);
        public string OperationOutput => _operationOutput;

        public void SetSelectedEntries(IEnumerable<SvnWorkingCopyEntry> entries)
        {
            if (IsBusy || _disposed)
                return;

            _selectedEntries = (entries ?? []).Where(entry => entry != null && Entries.Contains(entry)).Distinct().ToArray();
            if (!_selectedEntries.Contains(_selectedEntry))
            {
                SetProperty(ref _selectedEntry, _selectedEntries.FirstOrDefault(), nameof(SelectedEntry));
                InvalidateDiff();
                _ = RetryDiffAsync();
            }
            NotifyDiffState();
        }

        public IReadOnlyList<SvnWorkingCopyEntry> GetCommitCandidates()
        {
            if (_status != "Ready")
                return [];

            var candidates = Entries.Where(entry => Native.Svn.CanCommit(entry.Entry)).ToArray();
            var candidateEntries = candidates.Select(entry => entry.Entry).ToArray();
            return candidates.Where(entry => !Native.Svn.HasUnsafeCommitAncestor(
                entry.Entry, _operationStatusEntries, candidateEntries)).ToArray();
        }

        public bool CanCommitSelection(IReadOnlyList<SvnWorkingCopyEntry> selected, out string error)
        {
            error = string.Empty;
            return _status == "Ready" && Native.Svn.CanCommitSelection(
                (selected ?? []).Select(entry => entry.Entry).ToArray(), _operationStatusEntries, out error);
        }

        public bool CanMoveOrCopy(SvnWorkingCopyEntry entry) => CanFileOperations && entry != null &&
            Entries.Contains(entry) && Native.Svn.CanMoveOrCopy(entry.Entry) &&
            !Native.Svn.HasUnsafeOperationAncestor(entry.Entry, _operationStatusEntries);

        public bool IsKnownPathOperationSourceEligible(string path)
        {
            var entry = FindEntryByPath(path);
            // Normal versioned paths are omitted by `svn status`; the operation backend
            // verifies those paths against a fresh local status snapshot on submission.
            return entry == null || CanMoveOrCopy(entry);
        }

        public bool IsKnownPathOperationParentEligible(string path)
        {
            if (path == ".")
                return true;

            var entry = FindEntryByPath(path);
            if (entry == null)
                return true;

            return entry.Entry.Kind == "dir" && entry.Entry.Item is "normal" or "modified" &&
                entry.Entry.Properties is "none" or "normal" or "modified" &&
                !entry.Entry.IsCopied && !entry.Entry.IsSwitched && !entry.Entry.IsTreeConflicted &&
                !entry.Entry.IsFileExternal && string.IsNullOrEmpty(entry.Entry.MovedFrom) &&
                string.IsNullOrEmpty(entry.Entry.MovedTo) &&
                !Native.Svn.HasUnsafeOperationAncestor(entry.Entry, _operationStatusEntries);
        }

        public Task<bool> RunPathOperationAsync(string operation, string sourceRelativePath, string destinationRelativePath)
        {
            if (!CanFileOperations || string.IsNullOrWhiteSpace(sourceRelativePath) ||
                string.IsNullOrWhiteSpace(destinationRelativePath))
                return Task.FromResult(false);

            var source = CanonicalizePath(sourceRelativePath);
            var destination = CanonicalizePath(destinationRelativePath);
            var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
            var configDirectory = Preferences.Instance.SvnConfigDirectory;
            var normalizedOperation = operation?.ToLowerInvariant() switch
            {
                "move" => "move",
                "copy" => "copy",
                _ => "rename",
            };
            var operationKey = normalizedOperation switch
            {
                "move" => "Move",
                "copy" => "Copy",
                _ => "Rename",
            };
            var scope = source + Environment.NewLine + destination;

            return ExecuteOperationAsync("SVN.PathOperation." + operationKey + ".Applying",
                "SVN.PathOperation." + operationKey + ".Completed",
                "SVN.PathOperation." + operationKey + ".Failed",
                cancellationToken => Task.Run(() => normalizedOperation == "copy"
                    ? Native.Svn.CopyAsync(executable, configDirectory, FullPath, source, destination, cancellationToken)
                    : Native.Svn.MoveAsync(executable, configDirectory, FullPath, source, destination, cancellationToken),
                    cancellationToken), scope);
        }

        public void CancelAction() => _actionCancellation?.Cancel();

        public void SelectPathAfterOperation(string path)
        {
            if (!CanRefresh)
                return;

            var entry = FindEntryByPath(path);
            if (entry != null)
                SelectedEntry = entry;
        }

        private SvnWorkingCopyEntry FindEntryByPath(string path)
        {
            var canonical = CanonicalizePath(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return Entries.FirstOrDefault(entry => string.Equals(CanonicalizePath(entry.Path), canonical, comparison));
        }

        private static string CanonicalizePath(string path) => OperatingSystem.IsWindows()
            ? (path ?? string.Empty).Replace('\\', '/')
            : path ?? string.Empty;

        public bool CanInspectConflictEntry(SvnWorkingCopyEntry entry) => CanRefresh && _status == "Ready" &&
            entry != null && Entries.Contains(entry) && Native.Svn.CanInspectConflict(entry.Entry);

        private bool HasSafeOperationSelection() => _selectedEntries.All(entry =>
            !Native.Svn.HasUnsafeOperationAncestor(entry.Entry, _operationStatusEntries));

        private bool CanApplySelection(Func<Native.Svn.SvnWorkingCopyEntry, bool> predicate) =>
            CanRefresh && _status == "Ready" && _selectedEntries.Count > 0 &&
            _selectedEntries.All(entry => Entries.Contains(entry) && predicate(entry.Entry));

        public async Task RevertAsync(Func<string, Task<bool>> confirm)
        {
            if (!CanRevert)
                return;

            var paths = _selectedEntries.Select(entry => entry.Path).ToArray();
            // Hold the action lock throughout confirmation so selection and refresh cannot
            // change the reviewed targets while the modal is open.
            await ExecuteOperationAsync("SVN.WorkingCopy.Applying", "SVN.WorkingCopy.Reverted",
                "SVN.WorkingCopy.RevertFailed", async cancellationToken =>
                {
                    var confirmationKey = paths.Length == 1 && paths[0] == "."
                        ? "SVN.WorkingCopy.RevertRootConfirm" : "SVN.WorkingCopy.RevertConfirm";
                    if (confirm == null || !await confirm(App.Text(confirmationKey, string.Join(Environment.NewLine, paths))))
                        return null;
                    cancellationToken.ThrowIfCancellationRequested();
                    return await Task.Run(() => Native.Svn.RevertAsync(
                        Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath), Preferences.Instance.SvnConfigDirectory,
                        FullPath, paths, cancellationToken), cancellationToken);
                }, string.Join(Environment.NewLine, paths));
        }

        public async Task<bool> ExecuteOperationAsync(string applyingKey, string completedKey, string failedKey,
            Func<CancellationToken, Task<Native.Svn.WorkingCopyActionResult>> operation, string scope)
        {
            if (!CanRefresh)
                return false;

            _isMutating = true;
            _operationBusyKey = applyingKey;
            _actionErrorMessage = string.Empty;
            _actionErrorDetails = string.Empty;
            _actionNotice = string.Empty;
            _operationOutput = string.Empty;
            InvalidateDiff();
            NotifyState();
            using var cancellation = new CancellationTokenSource();
            _actionCancellation = cancellation;
            var attempted = false;
            var succeeded = false;
            try
            {
                attempted = true;
                var result = await operation(cancellation.Token);
                if (result == null)
                {
                    attempted = false;
                    return false;
                }

                succeeded = result.Status == "Ready";
                if (succeeded)
                {
                    _actionNotice = App.Text(completedKey, scope);
                    _operationOutput = result.Details ?? string.Empty;
                }
                else
                {
                    _actionErrorMessage = App.Text(failedKey, scope);
                    _actionErrorDetails = result.Details;
                }
            }
            catch (OperationCanceledException)
            {
                _actionErrorMessage = App.Text("SVN.Operation.Cancelled");
                _actionErrorDetails = App.Text("SVN.Operation.PartialUnknown");
            }
            catch (Exception ex)
            {
                _actionErrorMessage = App.Text(failedKey, scope);
                _actionErrorDetails = ex.Message + Environment.NewLine + App.Text("SVN.Operation.PartialUnknown");
            }
            finally
            {
                _actionCancellation = null;
                if (attempted && !_disposed)
                    await RefreshStatusAsync(true);
                _isMutating = false;
                if (!_disposed)
                {
                    NotifyState();
                    _ = RetryDiffAsync();
                }
            }

            return succeeded;
        }

        private IReadOnlyList<SvnWorkingCopyEntry> _selectedEntries = [];
        private IReadOnlyList<Native.Svn.SvnWorkingCopyEntry> _operationStatusEntries = [];
        private string _operationBusyKey = "SVN.WorkingCopy.Applying";
        private string _operationOutput = string.Empty;
    }
}
