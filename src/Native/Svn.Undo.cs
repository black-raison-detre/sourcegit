using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Native
{
    public static partial class Svn
    {
        private const string UndoRecoveryDirectoryPrefix = ".sourcegit-svn-undo-";
        private const string UndoRecoveryManifestName = "RECOVERY.txt";

        private sealed record UndoRecovery(string Directory, string OriginalPath, bool OriginalIsDirectory);

        /// <summary>
        /// Returns whether a plain scheduled addition or deletion can be undone safely.
        /// Added-item property changes from automatic properties are part of that addition.
        /// </summary>
        public static bool CanUndo(SvnWorkingCopyEntry entry)
        {
            if (entry == null || !IsSafeActionEntry(entry) ||
                entry.Item is not ("added" or "deleted") ||
                entry.IsCopied || entry.IsSwitched || entry.IsTreeConflicted || entry.IsFileExternal ||
                !string.IsNullOrEmpty(entry.MovedFrom) || !string.IsNullOrEmpty(entry.MovedTo))
                return false;

            if (entry.Item == "added")
                return (entry.Properties is "none" or "normal" or "modified") && (entry.Kind is "file" or "dir");

            return (entry.Properties is "none" or "normal") &&
                (entry.Kind is "file" or "dir" or "unknown");
        }

        /// <summary>
        /// Reverts one fresh plain addition or deletion. Existing deleted paths are moved aside
        /// before svn revert and restored afterwards so their local bytes and directory contents
        /// survive even though svn revert has no metadata-only mode.
        /// </summary>
        public static async Task<WorkingCopyActionResult> UndoAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            var status = await GetStatusCoreAsync(executable, configDirectory, workingCopyPath,
                cancellationToken, includeExternals: true).ConfigureAwait(false);
            if (status.Result.Status != "Ready")
                return new WorkingCopyActionResult(status.Result.Status, status.Result.Details);
            if (string.IsNullOrEmpty(status.WorkingCopyRoot))
                return new WorkingCopyActionResult("InvalidPath", "SVN returned an invalid working-copy root path.");
            if (!TryPrepareWorkingCopyRoot(status.WorkingCopyRoot, out var root, out var rootError))
                return new WorkingCopyActionResult("InvalidPath", rootError);
            if (!TryResolveSelectedPath(root, relativePath, status.Result.Entries, out var target, out var entry, out var selectionError))
                return new WorkingCopyActionResult("PathNotChanged", selectionError);
            if (!CanUndo(entry))
                return new WorkingCopyActionResult("NotEligible",
                    "Only plain scheduled additions and deletions without copy, move, conflict, switch, external, or property-conflict state can be undone.");
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new WorkingCopyActionResult("InvalidConfigDirectory", "The Subversion configuration directory is invalid.");

            if (!TryValidateActionPath(root, target, entry, add: false, cancellationToken, out var pathError))
                return new WorkingCopyActionResult("UnsafePath", pathError);

            var targetState = GetLocalPathState(target, out _, out var targetStateError);
            if (targetState == LocalPathState.Error)
                return new WorkingCopyActionResult("UnsafePath", $"The selected path could not be inspected: {targetStateError}");
            if (entry.Kind == "unknown" && targetState != LocalPathState.Missing)
                return new WorkingCopyActionResult("UnsafePath", "The deleted path has an unknown kind and still exists locally.");

            if (HasScheduledUndoAncestor(entry, status.Result.Entries))
                return new WorkingCopyActionResult("NotEligible", "A parent path also has a scheduled change. Undo the parent selection first.");
            if ((entry.Kind == "dir" || (entry.Item == "deleted" && targetState == LocalPathState.Missing)) &&
                HasUnsafeUndoDescendant(entry, status.Result.Entries))
                return new WorkingCopyActionResult("NotEligible",
                    "The directory contains a copied, moved, replaced, conflicted, switched, external, or property-edited item.");

            var arguments = new List<string> { "revert", "--depth", "infinity", "--non-interactive", "--no-auth-cache" };
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                arguments.Add("--config-dir");
                arguments.Add(effectiveConfigDirectory);
            }
            arguments.Add("--");
            arguments.Add(WithPegEscape(target));

            UndoRecovery recovery = null;
            if (entry.Item == "deleted" && targetState != LocalPathState.Missing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryCreateUndoRecovery(target, targetState == LocalPathState.Directory, out recovery, out var recoveryError))
                    return new WorkingCopyActionResult("Failed", recoveryError);

                try
                {
                    MovePath(target, recovery.OriginalPath, recovery.OriginalIsDirectory);
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    var cleanup = TryCleanupUndoRecovery(recovery, revertedPath: null);
                    var details = $"The selected local path could not be moved into its recovery directory: {ex.Message}";
                    if (!cleanup.Success)
                        details += Environment.NewLine + cleanup.Details;
                    return new WorkingCopyActionResult("Failed", details);
                }
            }

            FileCommandResult command = null;
            UndoRestoreResult restoration = null;
            OperationCanceledException cancellation = null;
            Exception commandException = null;
            try
            {
                try
                {
                    command = await RunFileCommandAsync(executable, arguments, credentials: null, 128 * 1024, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    cancellation = ex;
                }
                catch (Exception ex)
                {
                    commandException = ex;
                }
            }
            finally
            {
                if (recovery != null)
                    restoration = RestoreUndoRecovery(target, recovery);
            }

            if (cancellation != null)
            {
                if (restoration != null && (!restoration.Success || !string.IsNullOrEmpty(restoration.Details)))
                    throw new OperationCanceledException(FormatUndoCancellationDetails(restoration?.Details ?? "The recovery state could not be inspected."), cancellation, cancellationToken);
                throw cancellation;
            }

            if (commandException != null)
            {
                var details = $"SVN revert failed: {commandException.Message}";
                if (restoration != null)
                    details = AppendUndoRestoreDetails(details, restoration);
                return new WorkingCopyActionResult("Failed", AppendPartialUndoNotice(details));
            }

            if (command.Status != "Ready" || command.ExitCode != 0)
            {
                var details = command.Status == "Ready"
                    ? FormatFileCommandFailure(command, credentials: null)
                    : command.StandardError;
                if (restoration != null)
                    details = AppendUndoRestoreDetails(details, restoration);
                return new WorkingCopyActionResult(command.Status == "Ready" ? "Failed" : command.Status,
                    AppendPartialUndoNotice(details));
            }

            if (recovery == null)
                return new WorkingCopyActionResult("Ready");

            return restoration.Success
                ? new WorkingCopyActionResult("Ready")
                : new WorkingCopyActionResult("Failed", restoration.Details);
        }

        private static bool HasScheduledUndoAncestor(
            SvnWorkingCopyEntry selected,
            IReadOnlyList<SvnWorkingCopyEntry> entries)
        {
            if (entries == null)
                return false;

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var candidate in entries)
            {
                if (candidate == null || !selected.Path.StartsWith(candidate.Path + Path.DirectorySeparatorChar, comparison))
                    continue;

                if (candidate.Item is "deleted" or "replaced" or "conflicted" or "external" ||
                    candidate.IsCopied || candidate.IsSwitched || candidate.IsTreeConflicted || candidate.IsFileExternal ||
                    !string.IsNullOrEmpty(candidate.MovedFrom) || !string.IsNullOrEmpty(candidate.MovedTo) ||
                    candidate.Properties == "conflicted" ||
                    (candidate.Item == "added" && candidate.Properties is not ("none" or "normal" or "modified")))
                    return true;
            }

            return false;
        }

        private static bool HasUnsafeUndoDescendant(
            SvnWorkingCopyEntry selected,
            IReadOnlyList<SvnWorkingCopyEntry> entries)
        {
            if (entries == null)
                return false;

            var prefix = selected.Path + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var child in entries)
            {
                if (child == null || !child.Path.StartsWith(prefix, comparison))
                    continue;

                if (selected.Item == "added")
                {
                    if (CanUndo(child) || IsPlainUnversionedUndoDescendant(child))
                        continue;
                    return true;
                }

                if (CanUndo(child) || CanRemove(child) || IsPlainUnversionedUndoDescendant(child))
                    continue;
                return true;
            }

            return false;
        }

        private static bool IsPlainUnversionedUndoDescendant(SvnWorkingCopyEntry entry)
        {
            return entry != null && IsSafeActionEntry(entry) && entry.Item == "unversioned" &&
                (entry.Properties is "none" or "normal") &&
                !entry.IsCopied && !entry.IsSwitched && !entry.IsTreeConflicted && !entry.IsFileExternal &&
                string.IsNullOrEmpty(entry.MovedFrom) && string.IsNullOrEmpty(entry.MovedTo) &&
                (entry.Kind is "file" or "dir");
        }

        private static bool TryCreateUndoRecovery(
            string target,
            bool originalIsDirectory,
            out UndoRecovery recovery,
            out string error)
        {
            recovery = null;
            error = "The selected local path could not be safely preserved before undo.";
            var parent = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(parent) || GetLocalPathState(parent, out _, out _) != LocalPathState.Directory)
                return false;

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var directory = Path.Combine(parent, UndoRecoveryDirectoryPrefix + Guid.NewGuid().ToString("N"));
                if (Directory.Exists(directory) || File.Exists(directory))
                    continue;

                var manifestPath = Path.Combine(directory, UndoRecoveryManifestName);
                var manifestCreated = false;
                try
                {
                    Directory.CreateDirectory(directory);
                    var stream = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    manifestCreated = true;
                    using (stream)
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.WriteLine("SourceGit SVN undo recovery");
                        writer.WriteLine($"Original path: {target}");
                        writer.WriteLine($"Saved local path: {Path.Combine(directory, "original")}");
                        writer.WriteLine("If undo is interrupted, move the saved local path back to the original path after checking SVN status.");
                    }

                    recovery = new UndoRecovery(directory, Path.Combine(directory, "original"), originalIsDirectory);
                    error = string.Empty;
                    return true;
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    var directoryRemoved = false;
                    try
                    {
                        if (manifestCreated)
                            File.Delete(manifestPath);
                        Directory.Delete(directory, recursive: false);
                        directoryRemoved = true;
                    }
                    catch (Exception cleanupException) when (IsFileSystemException(cleanupException))
                    {
                        // Leave unknown or non-empty recovery paths untouched.
                    }

                    if (!manifestCreated && Directory.Exists(directory))
                        continue;

                    error = $"The recovery directory could not be created: {ex.Message}";
                    if (!directoryRemoved && Directory.Exists(directory))
                        error += $" A partial recovery directory may remain at {directory}.";
                    return false;
                }
            }

            error = "A unique recovery directory could not be created beside the selected path.";
            return false;
        }

        private sealed record UndoRestoreResult(bool Success, string Details);

        private static UndoRestoreResult RestoreUndoRecovery(string target, UndoRecovery recovery)
        {
            var revertedPath = Path.Combine(recovery.Directory, "reverted");
            var targetState = GetLocalPathState(target, out _, out var stateError);
            if (targetState == LocalPathState.Error)
                return RecoveryRequired(recovery, $"The post-revert path could not be inspected: {stateError}");
            if (targetState == LocalPathState.SymbolicLink)
                return RecoveryRequired(recovery, "The post-revert path is now a symbolic link; it was left in place.");

            if (targetState != LocalPathState.Missing)
            {
                if (File.Exists(revertedPath) || Directory.Exists(revertedPath))
                    return RecoveryRequired(recovery, $"A generated recovery slot is already occupied: {revertedPath}");
                try
                {
                    MovePath(target, revertedPath, targetState == LocalPathState.Directory);
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    return RecoveryRequired(recovery, $"The path recreated by svn revert could not be moved aside: {ex.Message}");
                }
            }

            try
            {
                MovePath(recovery.OriginalPath, target, recovery.OriginalIsDirectory);
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                return RecoveryRequired(recovery, $"The saved local path could not be restored: {ex.Message}");
            }

            var cleanup = TryCleanupUndoRecovery(recovery, targetState == LocalPathState.Missing ? null : revertedPath);
            if (!cleanup.Success)
                return new UndoRestoreResult(false, cleanup.Details);
            return new UndoRestoreResult(true, "");
        }

        private static UndoRestoreResult RecoveryRequired(UndoRecovery recovery, string reason)
        {
            return new UndoRestoreResult(false,
                $"{reason}{Environment.NewLine}The original local contents remain in {recovery.OriginalPath}. " +
                $"Recovery notes: {Path.Combine(recovery.Directory, UndoRecoveryManifestName)}. " +
                "Restore the saved path manually after checking the working-copy status.");
        }

        private static (bool Success, string Details) TryCleanupUndoRecovery(UndoRecovery recovery, string revertedPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(revertedPath))
                {
                    var revertedState = GetLocalPathState(revertedPath, out _, out var stateError);
                    if (revertedState == LocalPathState.Error)
                        return (false, $"The generated reverted path remains at {revertedPath}: {stateError}");
                    if (revertedState == LocalPathState.Directory)
                        Directory.Delete(revertedPath, recursive: true);
                    else if (revertedState == LocalPathState.File)
                        File.Delete(revertedPath);
                    else if (revertedState == LocalPathState.SymbolicLink)
                        return (false, $"The generated recovery path is a symbolic link and was left at {revertedPath}.");
                }

                File.Delete(Path.Combine(recovery.Directory, UndoRecoveryManifestName));
                Directory.Delete(recovery.Directory, recursive: false);
                return (true, string.Empty);
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                return (false, $"The original local contents were restored to their original path, but generated recovery data remains at {recovery.Directory}: {ex.Message}");
            }
        }

        private static void MovePath(string source, string destination, bool isDirectory)
        {
            if (isDirectory)
                Directory.Move(source, destination);
            else
                File.Move(source, destination);
        }

        private static string AppendUndoRestoreDetails(string details, UndoRestoreResult restoration)
        {
            if (restoration.Success)
                return AppendDetails(details, "The original local contents were restored. Refresh working-copy status before retrying.");
            return AppendDetails(details, restoration.Details);
        }

        private static string AppendPartialUndoNotice(string details)
        {
            return AppendDetails(details, "SVN undo did not complete; the working copy may have changed partially. Refresh status before retrying.");
        }

        private static string AppendDetails(string details, string additional)
        {
            return string.IsNullOrWhiteSpace(details) ? additional : details + Environment.NewLine + additional;
        }

        private static string FormatUndoCancellationDetails(string details)
        {
            return "SVN undo was cancelled. " + details;
        }

        private static bool IsFileSystemException(Exception ex)
        {
            return ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
        }
    }
}
