using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public partial class SvnWorkingCopy
    {
        public bool CanIgnoreSelectedPaths
        {
            get
            {
                if (!CanSelectedActions || _selectedEntries.Any(entry => entry.Entry.Item != "unversioned"))
                    return false;

                var parent = PropertyParentPath(_selectedEntries[0].Path);
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return _selectedEntries.All(entry => string.Equals(PropertyParentPath(entry.Path), parent, comparison)) &&
                    IsKnownPathOperationParentEligible(parent);
            }
        }

        public bool CanEditProperties(SvnWorkingCopyEntry entry) => CanFileOperations && entry != null &&
            Entries.Contains(entry) && IsPropertiesEntryEligible(entry) &&
            !Native.Svn.HasUnsafeOperationAncestor(entry.Entry, _operationStatusEntries);

        public bool CanEditPropertiesPath(string path)
        {
            if (!CanFileOperations || string.IsNullOrWhiteSpace(path))
                return false;

            var canonical = CanonicalizePropertiesPath(path);
            var entry = Entries.FirstOrDefault(item => string.Equals(CanonicalizePropertiesPath(item.Path), canonical,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (canonical == "." && entry == null)
                return true;
            // Clean versioned paths are omitted by `svn status`; the backend checks them
            // against a fresh snapshot when properties are read or changed.
            return entry == null || CanEditProperties(entry);
        }

        public async Task<Native.Svn.SvnPropertiesResult> ReadPropertiesAsync(string relativePath, CancellationToken cancellationToken)
        {
            if (!CanFileOperations)
                return new Native.Svn.SvnPropertiesResult("Failed", null, App.Text("SVN.Properties.WorkingCopyNotReady"));

            var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
            var configDirectory = Preferences.Instance.SvnConfigDirectory;
            return await Task.Run(() => Native.Svn.GetPropertiesAsync(executable, configDirectory, FullPath,
                CanonicalizePropertiesPath(relativePath), cancellationToken), cancellationToken);
        }

        public Task<bool> SetPropertyAsync(Native.Svn.SvnPropertiesSnapshot snapshot, string name, string value)
        {
            if (!CanFileOperations || snapshot == null || name == "svn:special")
                return Task.FromResult(false);

            var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
            var configDirectory = Preferences.Instance.SvnConfigDirectory;
            return ExecuteOperationAsync("SVN.Properties.Applying", "SVN.Properties.Saved", "SVN.Properties.SaveFailed",
                cancellationToken => Task.Run(() => Native.Svn.SetPropertyAsync(executable, configDirectory,
                    FullPath, snapshot, name, value, cancellationToken), cancellationToken),
                snapshot.RelativePath + Environment.NewLine + name);
        }

        public Task<bool> DeletePropertyAsync(Native.Svn.SvnPropertiesSnapshot snapshot, string name)
        {
            if (!CanFileOperations || snapshot == null || name == "svn:special")
                return Task.FromResult(false);

            var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
            var configDirectory = Preferences.Instance.SvnConfigDirectory;
            return ExecuteOperationAsync("SVN.Properties.Applying", "SVN.Properties.Deleted", "SVN.Properties.DeleteFailed",
                cancellationToken => Task.Run(() => Native.Svn.DeletePropertyAsync(executable, configDirectory,
                    FullPath, snapshot, name, cancellationToken), cancellationToken),
                snapshot.RelativePath + Environment.NewLine + name);
        }

        public async Task<Native.Svn.SvnPropertiesResult> ReadIgnoreParentAsync(string relativePath, CancellationToken cancellationToken)
        {
            if (!CanFileOperations)
                return new Native.Svn.SvnPropertiesResult("Failed", null, App.Text("SVN.Properties.WorkingCopyNotReady"));

            var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
            var configDirectory = Preferences.Instance.SvnConfigDirectory;
            return await Task.Run(() => Native.Svn.GetPropertiesAsync(executable, configDirectory, FullPath,
                CanonicalizePropertiesPath(relativePath), cancellationToken), cancellationToken);
        }

        public Task<bool> IgnoreAsync(Native.Svn.SvnPropertiesSnapshot snapshot, IReadOnlyList<string> selectedRelativePaths)
        {
            if (!CanFileOperations || snapshot == null || selectedRelativePaths == null || selectedRelativePaths.Count == 0)
                return Task.FromResult(false);

            var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
            var configDirectory = Preferences.Instance.SvnConfigDirectory;
            var scope = snapshot.RelativePath + Environment.NewLine + string.Join(Environment.NewLine, selectedRelativePaths);
            return ExecuteOperationAsync("SVN.Ignore.Applying", "SVN.Ignore.Completed", "SVN.Ignore.Failed",
                cancellationToken => Task.Run(() => Native.Svn.IgnoreAsync(executable, configDirectory,
                    FullPath, snapshot, selectedRelativePaths, cancellationToken), cancellationToken), scope);
        }

        public static bool TryCreateIgnoreValue(Native.Svn.SvnPropertiesSnapshot snapshot,
            IReadOnlyList<string> basenames, out string value, out string error) =>
            Native.Svn.TryCreateIgnoreValue(snapshot, basenames, out value, out error);

        private static bool IsPropertiesEntryEligible(SvnWorkingCopyEntry entry) =>
            (entry.Entry.Item is "normal" or "modified") &&
            (entry.Entry.Properties is "none" or "normal" or "modified") &&
            !entry.Entry.IsCopied && !entry.Entry.IsSwitched && !entry.Entry.IsTreeConflicted &&
            !entry.Entry.IsFileExternal && string.IsNullOrEmpty(entry.Entry.MovedFrom) &&
            string.IsNullOrEmpty(entry.Entry.MovedTo);

        private static string PropertyParentPath(string path)
        {
            var canonical = CanonicalizePropertiesPath(path).TrimEnd('/');
            var index = canonical.LastIndexOf('/');
            return index < 0 ? "." : canonical[..index];
        }

        private static string CanonicalizePropertiesPath(string path) => OperatingSystem.IsWindows()
            ? (path ?? string.Empty).Replace('\\', '/')
            : path ?? string.Empty;

        private void NotifyPropertiesState()
        {
            OnPropertyChanged(nameof(CanIgnoreSelectedPaths));
        }
    }
}
