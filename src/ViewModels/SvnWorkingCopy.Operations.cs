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
        public bool CanInspectConflict => CanRefresh && _status == "Ready" && _selectedEntries.Count == 1 &&
            CanInspectConflictEntry(_selectedEntries[0]);
        public bool CanRevert => CanApplySelection(Native.Svn.CanRevert) && HasSafeOperationSelection() &&
            (_selectedEntries.Count == 1 || _selectedEntries.All(entry => entry.Path != "."));
        public bool CanCommit => CanApplySelection(Native.Svn.CanCommit) && HasSafeOperationSelection();
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

        public IReadOnlyList<SvnWorkingCopyEntry> GetCommitCandidates() =>
            _status == "Ready" ? Entries.Where(entry => Native.Svn.CanCommit(entry.Entry) &&
                !Native.Svn.HasUnsafeOperationAncestor(entry.Entry, _operationStatusEntries)).ToArray() : [];

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
