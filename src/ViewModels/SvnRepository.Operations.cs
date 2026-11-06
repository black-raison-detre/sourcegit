using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public partial class SvnRepository
    {
        public bool CanMutateWorkingCopy => !_disposed && !IsLoading && WorkingCopy.CanRefresh;
        public string CommitMessageDraft { get; set; } = string.Empty;

        public Task UpdateWorkingCopyAsync(string revision = "HEAD") => ApplyWorkingCopyOperationAsync(false, revision, null, null);

        public Task CommitWorkingCopyAsync(IReadOnlyList<string> paths, string message) =>
            ApplyWorkingCopyOperationAsync(true, null, paths, message);

        private async Task ApplyWorkingCopyOperationAsync(bool commit, string revision, IReadOnlyList<string> paths, string message)
        {
            if (!CanMutateWorkingCopy)
                return;

            if (commit)
                CommitMessageDraft = message ?? string.Empty;

            var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
            var config = Preferences.Instance.SvnConfigDirectory;
            if (!string.Equals(config, _credentialsConfigDirectory, StringComparison.Ordinal))
                _credentials = null;
            var credentials = _credentials;
            var expectedUrl = Url;
            var selectedPaths = paths?.ToArray();
            var operation = commit ? "SVN.Commit." : "SVN.Update.";
            SelectedTabIndex = 1;
            CancelChangedPaths();
            InvalidateDiff();
            var succeeded = await WorkingCopy.ExecuteOperationAsync(operation + "Applying", operation + "Completed", operation + "Failed",
                cancellationToken => Task.Run(() => commit
                    ? Native.Svn.CommitAsync(executable, config, FullPath, selectedPaths, message, credentials, cancellationToken, expectedUrl: expectedUrl)
                    : Native.Svn.UpdateAsync(executable, config, FullPath, revision, credentials, cancellationToken, expectedUrl: expectedUrl), cancellationToken), FullPath);
            if (commit && succeeded)
                CommitMessageDraft = string.Empty;

            // Refresh bypasses cached history and changed paths. Never replay a mutation
            // after an authentication failure or ambiguous commit result.
            if (!_disposed)
            {
                await RefreshAsync();
                if (succeeded && !HasError)
                    await RetryChangedPathsAsync();
            }
        }
    }
}
