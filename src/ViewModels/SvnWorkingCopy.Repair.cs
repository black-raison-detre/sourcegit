using System;
using System.Linq;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public partial class SvnWorkingCopy
    {
        // Recovery must remain available when status fails because an interrupted operation
        // left the working copy locked. The native layer validates the exact root independently.
        public bool CanCleanup => CanRefresh;
        public bool CanRestoreMissingFiles => CanApplySelection(Native.Svn.CanRepairMissingFile) && HasSafeOperationSelection();
        public bool CanRemoveMissingFiles => CanRestoreMissingFiles;
        public bool CanRepairRename => _selectedEntries.Count == 1 && CanApplySelection(Native.Svn.CanPrepareRenameRepair) &&
            HasSafeOperationSelection();

        public Task<bool> CleanupAsync(Func<string, Task<bool>> confirm)
        {
            if (!CanCleanup)
                return Task.FromResult(false);

            return ExecuteOperationAsync("SVN.Cleanup.Applying", "SVN.Cleanup.Completed", "SVN.Cleanup.Failed",
                async cancellationToken =>
                {
                    if (confirm == null || !await confirm(App.Text("SVN.Cleanup.Confirm", FullPath)))
                        return null;
                    cancellationToken.ThrowIfCancellationRequested();
                    return await Task.Run(() => Native.Svn.CleanupAsync(
                        Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath), Preferences.Instance.SvnConfigDirectory,
                        FullPath, cancellationToken), cancellationToken);
                }, FullPath);
        }

        public Task<bool> RestoreMissingFilesAsync(Func<string, Task<bool>> confirm) => ChangeMissingFilesAsync(false, confirm);
        public Task<bool> RemoveMissingFilesAsync(Func<string, Task<bool>> confirm) => ChangeMissingFilesAsync(true, confirm);

        public async Task<bool> RepairRenameAsync(Native.Svn.RenameRepairSnapshot snapshot)
        {
            if (!CanFileOperations || snapshot == null)
                return false;

            var scope = snapshot.SourceRelativePath + " → " + snapshot.DestinationRelativePath;
            var succeeded = await ExecuteOperationAsync("SVN.Repair.Applying", "SVN.Repair.RenameCompleted", "SVN.Repair.RenameFailed",
                cancellationToken => Task.Run(() => Native.Svn.RepairRenameAsync(
                    Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath), Preferences.Instance.SvnConfigDirectory,
                    FullPath, snapshot, cancellationToken), cancellationToken), scope);
            if (succeeded)
            {
                var destination = VisibleEntries.FirstOrDefault(entry => entry.Path == snapshot.DestinationRelativePath);
                if (destination != null)
                    SetSelectedEntries([destination]);
                else
                {
                    _actionNotice += Environment.NewLine + App.Text("SVN.Repair.HiddenResult");
                    OnPropertyChanged(nameof(ActionNotice));
                }
            }
            return succeeded;
        }

        private Task<bool> ChangeMissingFilesAsync(bool remove, Func<string, Task<bool>> confirm)
        {
            if (!CanRestoreMissingFiles)
                return Task.FromResult(false);

            var paths = _selectedEntries.Select(entry => entry.Path).ToArray();
            var operation = remove ? "SVN.Repair.Remove" : "SVN.Repair.Restore";
            var scope = string.Join(Environment.NewLine, paths);
            return ExecuteOperationAsync("SVN.Repair.Applying", operation + "Completed", operation + "Failed",
                async cancellationToken =>
                {
                    if (confirm == null || !await confirm(App.Text(operation + "Confirm", FullPath, paths.Length, scope)))
                        return null;
                    cancellationToken.ThrowIfCancellationRequested();
                    var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
                    var config = Preferences.Instance.SvnConfigDirectory;
                    return await Task.Run(() => remove
                        ? Native.Svn.RemoveMissingFilesAsync(executable, config, FullPath, paths, cancellationToken)
                        : Native.Svn.RestoreMissingFilesAsync(executable, config, FullPath, paths, cancellationToken), cancellationToken);
                }, scope);
        }

        private void NotifyRepairState()
        {
            OnPropertyChanged(nameof(CanCleanup));
            OnPropertyChanged(nameof(CanRestoreMissingFiles));
            OnPropertyChanged(nameof(CanRemoveMissingFiles));
            OnPropertyChanged(nameof(CanRepairRename));
        }
    }
}
