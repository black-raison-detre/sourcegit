using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Native
{
    public static partial class Svn
    {
        private const int MaxOperationOutputBytes = 128 * 1024;
        private const int MaxOperationMessageBytes = 1024 * 1024;
        private static readonly TimeSpan s_operationTimeout = TimeSpan.FromMinutes(5);

        /// <summary>Returns whether a fresh plain local edit can be reverted safely.</summary>
        public static bool CanRevert(SvnWorkingCopyEntry entry)
        {
            return entry != null && IsSafeOperationEntry(entry) &&
                (entry.Item == "modified" || (entry.Item == "normal" && entry.Properties == "modified") || entry.Item == "missing") &&
                entry.Properties is "none" or "normal" or "modified" &&
                !entry.IsCopied && !entry.IsSwitched && !entry.IsTreeConflicted && !entry.IsFileExternal &&
                string.IsNullOrEmpty(entry.MovedFrom) && string.IsNullOrEmpty(entry.MovedTo) &&
                (entry.Kind is "file" or "dir" || (entry.Item == "missing" && entry.Kind == "unknown"));
        }

        /// <summary>Returns whether a fresh, explicitly selectable working-copy change can be committed.</summary>
        public static bool CanCommit(SvnWorkingCopyEntry entry)
        {
            if (entry == null || (entry.Path != "." && !IsSafeActionEntry(entry)) ||
                entry.Properties is not ("none" or "normal" or "modified") ||
                entry.IsSwitched || entry.IsTreeConflicted || entry.IsFileExternal)
                return false;

            if (entry.Path == ".")
                return entry.Item == "normal" && entry.Properties == "modified" && !entry.IsCopied &&
                    string.IsNullOrEmpty(entry.MovedFrom) && string.IsNullOrEmpty(entry.MovedTo) && entry.Kind == "dir";

            var supportedState = entry.Item is "added" or "deleted" or "modified" ||
                (entry.Item == "normal" && entry.Properties == "modified") ||
                (entry.IsCopied && entry.Item == "normal") ||
                (entry.IsCopied && entry.Item == "modified");
            if (!supportedState)
                return false;

            if (entry.IsCopied && entry.Item is not ("added" or "normal" or "modified"))
                return false;
            if ((!string.IsNullOrEmpty(entry.MovedFrom) || !string.IsNullOrEmpty(entry.MovedTo)) &&
                entry.Item is not ("added" or "deleted"))
                return false;

            return entry.Kind is "file" or "dir" || (entry.Item == "deleted" && entry.Kind == "unknown");
        }

        /// <summary>
        /// Validates the explicit commit selection, including move pairs and complete copied or
        /// moved directory scopes. The same check is used by the commit UI and backend.
        /// </summary>
        public static bool CanCommitSelection(
            IReadOnlyList<SvnWorkingCopyEntry> selectedEntries,
            IReadOnlyList<SvnWorkingCopyEntry> allEntries,
            out string error)
        {
            error = "Select at least one changed working-copy path to commit.";
            if (selectedEntries == null || selectedEntries.Count == 0 || allEntries == null)
                return false;

            var selected = new List<SvnWorkingCopyEntry>(selectedEntries.Count);
            foreach (var requested in selectedEntries)
            {
                if (requested == null)
                {
                    error = "The selection contains an invalid working-copy path.";
                    return false;
                }
                var entry = allEntries.FirstOrDefault(item => item != null && PathEquals(item.Path, requested.Path));
                if (entry == null || !CanCommit(entry))
                {
                    error = "The selection contains an unversioned, missing, conflicted, switched, external, or unsupported path.";
                    return false;
                }
                if (selected.Any(item => PathEquals(item.Path, entry.Path)))
                {
                    error = "The selection contains the same path more than once.";
                    return false;
                }
                selected.Add(entry);
            }

            foreach (var entry in selected)
            {
                if (HasUnsafeCommitAncestor(entry, allEntries, selected))
                {
                    error = "A selected path is beneath an unselected copied or moved directory, or a switched, external, missing, replaced, or conflicted status boundary.";
                    return false;
                }
                if (!AreAllAddedAncestorsSelected(entry, allEntries, selected.Select(item => new OperationSelection(item.Path, item)).ToArray()))
                {
                    error = "A selected path beneath a scheduled directory addition requires that added parent in the same commit selection.";
                    return false;
                }
                if (!HasReciprocalSelectedMovePartner(entry, allEntries, selected))
                {
                    error = "Both reciprocal endpoints of a scheduled move must be selected for commit.";
                    return false;
                }
                if (entry.Item == "deleted" && (entry.Kind is "dir" or "unknown") &&
                    allEntries.Any(descendant => IsPathAncestor(entry.Path, descendant.Path) &&
                        descendant.Item != "unversioned" && !selected.Any(item => PathEquals(item.Path, descendant.Path))))
                {
                    error = "A scheduled directory deletion commits its full repository subtree; select every reported changed descendant.";
                    return false;
                }
                if (allEntries.Any(ancestor => ancestor.Item == "deleted" && (ancestor.Kind is "dir" or "unknown") &&
                        IsPathAncestor(ancestor.Path, entry.Path) && !selected.Any(item => PathEquals(item.Path, ancestor.Path))))
                {
                    error = "A selected path beneath a scheduled directory deletion requires that deleted parent in the same commit selection.";
                    return false;
                }
                if (IsDirectoryCopyOrMoveRoot(entry) && !HasCompleteScheduledDirectorySelection(entry, allEntries, selected))
                {
                    error = "A copied or moved directory requires a complete explicit selection of its versioned descendants.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        /// <summary>Returns whether a commit candidate is beneath an unsafe or unselected schedule boundary.</summary>
        public static bool HasUnsafeCommitAncestor(
            SvnWorkingCopyEntry entry,
            IReadOnlyList<SvnWorkingCopyEntry> entries,
            IReadOnlyList<SvnWorkingCopyEntry> selectedEntries)
        {
            if (entry == null || entries == null)
                return false;
            return entries.Any(ancestor => ancestor != null && !PathEquals(ancestor.Path, entry.Path) &&
                (ancestor.Path == "." && entry.Path != "." || IsPathAncestor(ancestor.Path, entry.Path)) &&
                (ancestor.IsSwitched || ancestor.IsTreeConflicted || ancestor.IsFileExternal ||
                 ancestor.Item is "missing" or "replaced" or "conflicted" or "external" or "incomplete" or "obstructed" ||
                 ancestor.Properties == "conflicted" ||
                 ((ancestor.IsCopied || !string.IsNullOrEmpty(ancestor.MovedFrom) || !string.IsNullOrEmpty(ancestor.MovedTo)) &&
                  (ancestor.Kind != "dir" || selectedEntries?.Any(item => PathEquals(item.Path, ancestor.Path)) != true))));
        }

        private static bool HasReciprocalSelectedMovePartner(
            SvnWorkingCopyEntry selected,
            IReadOnlyList<SvnWorkingCopyEntry> entries,
            IReadOnlyList<SvnWorkingCopyEntry> selections)
        {
            if (!string.IsNullOrEmpty(selected.MovedFrom))
            {
                var partner = entries.FirstOrDefault(item => item != null && PathEquals(item.Path, selected.MovedFrom));
                if (partner == null || !PathEquals(partner.MovedTo, selected.Path) ||
                    !selections.Any(item => PathEquals(item.Path, partner.Path)))
                    return false;
            }
            if (!string.IsNullOrEmpty(selected.MovedTo))
            {
                var partner = entries.FirstOrDefault(item => item != null && PathEquals(item.Path, selected.MovedTo));
                if (partner == null || !PathEquals(partner.MovedFrom, selected.Path) ||
                    !selections.Any(item => PathEquals(item.Path, partner.Path)))
                    return false;
            }
            return true;
        }

        private static bool IsDirectoryCopyOrMoveRoot(SvnWorkingCopyEntry entry)
        {
            return entry.Kind == "dir" && (entry.IsCopied ||
                !string.IsNullOrEmpty(entry.MovedFrom) || !string.IsNullOrEmpty(entry.MovedTo));
        }

        private static bool HasCompleteScheduledDirectorySelection(
            SvnWorkingCopyEntry directory,
            IReadOnlyList<SvnWorkingCopyEntry> entries,
            IReadOnlyList<SvnWorkingCopyEntry> selections)
        {
            var roots = new List<string> { directory.Path };
            if (!string.IsNullOrEmpty(directory.MovedFrom))
                roots.Add(directory.MovedFrom);
            if (!string.IsNullOrEmpty(directory.MovedTo))
                roots.Add(directory.MovedTo);
            foreach (var root in roots)
            {
                foreach (var descendant in entries.Where(item => item != null && IsPathAncestor(root, item.Path)))
                {
                    if (!CanCommit(descendant) || !selections.Any(item => PathEquals(item.Path, descendant.Path)))
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Returns whether an entry is beneath a switched, external, conflicted, copied, moved,
        /// missing, or replaced status boundary in the same fresh status snapshot.
        /// </summary>
        public static bool HasUnsafeOperationAncestor(SvnWorkingCopyEntry entry, IReadOnlyList<SvnWorkingCopyEntry> entries)
        {
            if (entry == null || entries == null || string.IsNullOrEmpty(entry.Path))
                return false;

            return entries.Any(ancestor => ancestor != null && !string.IsNullOrEmpty(ancestor.Path) &&
                (ancestor.Path == "." && entry.Path != "." || IsPathAncestor(ancestor.Path, entry.Path)) &&
                (ancestor.IsSwitched || ancestor.IsTreeConflicted || ancestor.IsFileExternal || ancestor.IsCopied ||
                 (ancestor.Item is "conflicted" or "external" or "incomplete" or "missing" or "obstructed" or "replaced") ||
                 ancestor.Properties == "conflicted" || !string.IsNullOrEmpty(ancestor.MovedFrom) ||
                 !string.IsNullOrEmpty(ancestor.MovedTo)));
        }

        public static async Task<WorkingCopyActionResult> RevertAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            IReadOnlyList<string> relativePaths,
            CancellationToken cancellationToken = default)
        {
            var resolved = await GetStatusCoreAsync(executable, configDirectory, workingCopyPath,
                cancellationToken, includeExternals: true).ConfigureAwait(false);
            if (resolved.Result.Status != "Ready")
                return new WorkingCopyActionResult(resolved.Result.Status, resolved.Result.Details);
            if (!TryPrepareWorkingCopyRoot(resolved.WorkingCopyRoot, out var root, out var rootError))
                return new WorkingCopyActionResult("InvalidPath", rootError);
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new WorkingCopyActionResult("InvalidConfigDirectory", "The Subversion configuration directory is invalid.");
            if (!TryResolveRevertSelections(root, relativePaths, resolved.Result.Entries, out var selections, out var selectionError))
                return new WorkingCopyActionResult("NotEligible", selectionError);

            var rootPropertySelection = selections.Any(selection => selection.Entry.Path == ".");
            if (rootPropertySelection && selections.Count != 1)
                return new WorkingCopyActionResult("NotEligible", "Revert the working-copy root property change separately so other selected edits remain untouched.");

            foreach (var selection in selections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (selection.Entry.Path == ".")
                {
                    if (!TryValidateWorkingCopyRoot(root))
                        return new WorkingCopyActionResult("UnsafePath", "The working-copy root could not be resolved safely.");
                    continue;
                }
                if (selection.IsImplicitDirectory)
                {
                    if (!TryValidateActionPath(root, selection.Target, selection.Entry, add: false, cancellationToken, out var pathError))
                        return new WorkingCopyActionResult("UnsafePath", pathError);
                    if (!TryValidateRevertDirectory(selection.Entry, resolved.Result.Entries, selection.Target,
                            cancellationToken, out var treeError))
                        return new WorkingCopyActionResult("NotEligible", treeError);
                }
                else
                {
                    if (!TryValidateActionPath(root, selection.Target, selection.Entry, add: false, cancellationToken, out var pathError))
                        return new WorkingCopyActionResult("UnsafePath", pathError);
                    if ((selection.Entry.Kind == "dir" || selection.Entry.Item == "missing") &&
                        HasUnsafeRevertDescendant(selection.Entry, resolved.Result.Entries))
                        return new WorkingCopyActionResult("NotEligible",
                            "The selected directory contains a scheduled, conflicted, switched, copied, moved, external, or unsupported change.");
                    if (selection.Entry.Kind == "dir" && !TryValidateRevertDirectory(selection.Entry,
                            resolved.Result.Entries, selection.Target, cancellationToken, out var treeError))
                        return new WorkingCopyActionResult("UnsafePath", treeError);
                }
            }

            // A selected directory already covers its selected descendants; pass each target once.
            var targets = selections.Where(selection => !selections.Any(parent =>
                !ReferenceEquals(parent, selection) && parent.Entry.Kind == "dir" &&
                IsPathAncestor(parent.Entry.Path, selection.Entry.Path)))
                .Select(selection => selection.Target).ToArray();
            var arguments = new List<string> { "revert", "--depth", rootPropertySelection ? "empty" : "infinity", "--non-interactive", "--no-auth-cache" };
            AppendConfigArgument(arguments, configDirectory, effectiveConfigDirectory);
            arguments.Add("--");
            arguments.AddRange(targets.Select(WithPegEscape));

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var command = await RunFileCommandAsync(executable, arguments, null, MaxOperationOutputBytes,
                    cancellationToken, s_operationTimeout, killOnOutputLimit: false).ConfigureAwait(false);
                return FormatMutationResult(command, null, "revert");
            }
            catch (OperationCanceledException)
            {
                return new WorkingCopyActionResult("Cancelled",
                    "SVN revert was cancelled after it started; some selected paths may already have changed. Refresh status before retrying.");
            }
        }

        public static async Task<WorkingCopyActionResult> UpdateAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string revision = "HEAD",
            Credentials credentials = null,
            CancellationToken cancellationToken = default,
            string expectedUrl = null)
        {
            if (!IsValidUpdateRevision(revision))
                return new WorkingCopyActionResult("InvalidArgument", "The update revision must be HEAD or a nonnegative integer.");
            if (credentials != null && (HasUnsafeLineBreak(credentials.Username) || HasUnsafeLineBreak(credentials.Password)))
                return new WorkingCopyActionResult("InvalidArgument", "Username and password cannot contain newline or control characters.");

            var workingCopy = await GetWorkingCopyAsync(executable, configDirectory, workingCopyPath, cancellationToken).ConfigureAwait(false);
            if (workingCopy.Status != "Ready")
                return new WorkingCopyActionResult(workingCopy.Status, workingCopy.Details);
            if (credentials != null && (string.IsNullOrWhiteSpace(expectedUrl) || !workingCopy.Url.Equals(expectedUrl, StringComparison.Ordinal)))
                return new WorkingCopyActionResult("WorkingCopyChanged", "The working-copy URL changed; credentials were not sent. Refresh its history before trying again.");
            if (!TryPrepareWorkingCopyRoot(workingCopy.WorkingCopyRoot, out var root, out var rootError))
                return new WorkingCopyActionResult("InvalidPath", rootError);
            if (!TryPrepareHistoryAccess(workingCopy.Url, configDirectory, credentials, out var accessStatus,
                    out var accessError, out var effectiveConfigDirectory, out var disabledSections))
                return new WorkingCopyActionResult(accessStatus, accessError);

            var arguments = new List<string> { "update", "--revision", revision, "--ignore-externals", "--non-interactive" };
            AppendHistoryAccessArguments(arguments, configDirectory, effectiveConfigDirectory, credentials, disabledSections);
            arguments.Add("--");
            arguments.Add(WithPegEscape(root));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var command = await RunFileCommandAsync(executable, arguments, credentials, MaxOperationOutputBytes,
                    cancellationToken, s_operationTimeout, killOnOutputLimit: false).ConfigureAwait(false);
                return FormatMutationResult(command, credentials, "update");
            }
            catch (OperationCanceledException)
            {
                return new WorkingCopyActionResult("Cancelled",
                    "SVN update was cancelled after it started; the working copy may be partially updated. Refresh status before retrying.");
            }
        }

        public static async Task<WorkingCopyActionResult> CommitAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            IReadOnlyList<string> relativePaths,
            string message,
            Credentials credentials = null,
            CancellationToken cancellationToken = default,
            string expectedUrl = null)
        {
            if (string.IsNullOrWhiteSpace(message) || Encoding.UTF8.GetByteCount(message) > MaxOperationMessageBytes ||
                message.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
                return new WorkingCopyActionResult("InvalidArgument", "The commit message is invalid or exceeds the 1 MiB limit.");
            if (credentials != null && (HasUnsafeLineBreak(credentials.Username) || HasUnsafeLineBreak(credentials.Password)))
                return new WorkingCopyActionResult("InvalidArgument", "Username and password cannot contain newline or control characters.");

            var resolved = await GetStatusCoreAsync(executable, configDirectory, workingCopyPath,
                cancellationToken, includeExternals: true).ConfigureAwait(false);
            if (resolved.Result.Status != "Ready")
                return new WorkingCopyActionResult(resolved.Result.Status, resolved.Result.Details);
            if (!TryPrepareWorkingCopyRoot(resolved.WorkingCopyRoot, out var root, out var rootError))
                return new WorkingCopyActionResult("InvalidPath", rootError);
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new WorkingCopyActionResult("InvalidConfigDirectory", "The Subversion configuration directory is invalid.");
            if (!TryResolveCommitSelections(root, relativePaths, resolved.Result.Entries,
                    out var selections, out var selectionError))
                return new WorkingCopyActionResult("NotEligible", selectionError);
            foreach (var selection in selections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (selection.Entry.Path == ".")
                {
                    if (!TryValidateWorkingCopyRoot(root))
                        return new WorkingCopyActionResult("UnsafePath", "The working-copy root could not be resolved safely.");
                }
                else if (selection.Entry.Kind == "unknown")
                {
                    var state = GetLocalPathState(selection.Target, out _, out var stateError);
                    if (state != LocalPathState.Missing)
                        return new WorkingCopyActionResult("UnsafePath", state == LocalPathState.Error
                            ? $"The unknown-kind deleted path could not be inspected safely: {stateError}"
                            : "An unknown-kind deleted path must be missing locally before commit.");
                    if (!TryValidateActionPath(root, selection.Target, selection.Entry, add: false,
                            cancellationToken, out var pathError))
                        return new WorkingCopyActionResult("UnsafePath", pathError);
                }
                else if (!TryValidateActionPath(root, selection.Target, selection.Entry, add: false,
                             cancellationToken, out var pathError))
                {
                    return new WorkingCopyActionResult("UnsafePath", pathError);
                }
                if (!HasSelectedMovePartner(selection.Entry, selections))
                    return new WorkingCopyActionResult("NotEligible", "Both ends of a scheduled move must be selected for commit.");
            }
            var workingCopy = await GetWorkingCopyAsync(executable, configDirectory, workingCopyPath, cancellationToken).ConfigureAwait(false);
            if (workingCopy.Status != "Ready")
                return new WorkingCopyActionResult(workingCopy.Status, workingCopy.Details);
            if (credentials != null && (string.IsNullOrWhiteSpace(expectedUrl) || !workingCopy.Url.Equals(expectedUrl, StringComparison.Ordinal)))
                return new WorkingCopyActionResult("WorkingCopyChanged", "The working-copy URL changed; credentials were not sent. Refresh its history before trying again.");
            if (!TryPrepareHistoryAccess(workingCopy.Url,
                    configDirectory, credentials, out var accessStatus, out var accessError,
                    out var effectiveAccessConfig, out var disabledSections))
                return new WorkingCopyActionResult(accessStatus, accessError);

            string messageFile = null;
            try
            {
                messageFile = CreateProtectedMessageFile(message);
                var arguments = new List<string> { "commit", "--depth", "empty", "--file", messageFile, "--encoding", "UTF-8", "--non-interactive" };
                AppendHistoryAccessArguments(arguments, configDirectory, effectiveAccessConfig, credentials, disabledSections);
                arguments.Add("--");
                arguments.AddRange(selections.Select(selection => WithPegEscape(selection.Target)));
                cancellationToken.ThrowIfCancellationRequested();
                var command = await RunFileCommandAsync(executable, arguments, credentials, MaxOperationOutputBytes,
                    cancellationToken, s_operationTimeout, killOnOutputLimit: false).ConfigureAwait(false);
                return FormatMutationResult(command, credentials, "commit");
            }
            catch (OperationCanceledException)
            {
                return new WorkingCopyActionResult("Cancelled",
                    "SVN commit was cancelled after it started; the server may have committed the selection. Refresh status and history before retrying.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new WorkingCopyActionResult("Failed", Redact($"The protected commit message file could not be created: {ex.Message}", credentials));
            }
            finally
            {
                if (messageFile != null)
                {
                    try
                    {
                        File.Delete(messageFile);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        private sealed record OperationSelection(string Target, SvnWorkingCopyEntry Entry, bool IsImplicitDirectory = false);

        private static bool TryResolveRevertSelections(
            string root,
            IReadOnlyList<string> relativePaths,
            IReadOnlyList<SvnWorkingCopyEntry> entries,
            out List<OperationSelection> selections,
            out string error)
        {
            selections = new List<OperationSelection>();
            error = "Select at least one changed working-copy path.";
            if (relativePaths == null || relativePaths.Count == 0)
                return false;
            foreach (var relativePath in relativePaths)
            {
                var isImplicitDirectory = false;
                if (!TryResolveSelectedPath(root, relativePath, entries, out var target, out var entry, out var selectionError))
                {
                    // Status omits normal directories. Permit one as a recursive selection only
                    // when it contains at least one reported change.
                    if (!TryCreateImplicitDirectorySelection(root, relativePath, entries, out target, out entry))
                    {
                        error = selectionError;
                        return false;
                    }
                    isImplicitDirectory = true;
                }
                if (!isImplicitDirectory && !CanRevert(entry))
                {
                    error = "Only plain text/property-modified or missing paths can be reverted.";
                    return false;
                }
                if (selections.Any(item => PathEquals(item.Entry.Path, entry.Path)))
                {
                    error = "The selection contains the same path more than once.";
                    return false;
                }
                selections.Add(new OperationSelection(target, entry, isImplicitDirectory));
            }

            foreach (var selection in selections)
            {
                if (HasUnsafeOperationAncestor(selection.Entry, entries))
                {
                    error = "A selected path is beneath a switched, external, copied, moved, missing, replaced, or conflicted status boundary.";
                    return false;
                }
                if (selection.IsImplicitDirectory && !entries.Any(entry =>
                        IsPathAncestor(selection.Entry.Path, entry.Path) && entry.Item != "unversioned"))
                {
                    error = "The selected directory has no changed descendants.";
                    return false;
                }
                if (selection.Entry.Path != "." && selection.Entry.Kind == "dir" && HasUnsafeRevertDescendant(selection.Entry, entries))
                {
                    error = "The selected directory contains a scheduled, conflicted, switched, copied, moved, external, or unsupported change.";
                    return false;
                }
            }
            error = string.Empty;
            return true;
        }

        private static bool TryCreateImplicitDirectorySelection(
            string root,
            string relativePath,
            IReadOnlyList<SvnWorkingCopyEntry> entries,
            out string target,
            out SvnWorkingCopyEntry entry)
        {
            target = string.Empty;
            entry = null;
            if (string.IsNullOrWhiteSpace(relativePath) || !TryMakeRelativePath(root, relativePath, out var relative) ||
                relative == "." || !relative.Equals(relativePath, PathComparison))
                return false;
            target = Path.GetFullPath(relative, root);
            if (GetLocalPathState(target, out _, out _) != LocalPathState.Directory ||
                !entries.Any(candidate => IsPathAncestor(relative, candidate.Path) && candidate.Item != "unversioned"))
                return false;
            entry = new SvnWorkingCopyEntry(relative, "normal", "none", Kind: "dir");
            return IsSafeActionEntry(entry);
        }

        private static bool TryValidateRevertDirectory(
            SvnWorkingCopyEntry selected,
            IReadOnlyList<SvnWorkingCopyEntry> entries,
            string target,
            CancellationToken cancellationToken,
            out string error)
        {
            error = "The selected directory cannot be verified safely.";
            if (selected.Kind != "dir")
                return true;
            if (HasUnsafeRevertDescendant(selected, entries))
            {
                error = "The selected directory contains a scheduled, conflicted, switched, copied, moved, external, or unsupported change.";
                return false;
            }

            var directoryState = GetLocalPathState(target, out _, out var directoryError);
            if (directoryState == LocalPathState.Error)
            {
                error = $"The selected directory could not be inspected safely: {directoryError}";
                return false;
            }
            if (directoryState == LocalPathState.Missing)
            {
                error = string.Empty;
                return true;
            }
            if (directoryState != LocalPathState.Directory)
            {
                error = "The selected path changed type after status was read.";
                return false;
            }

            var stack = new Stack<string>();
            stack.Push(target);
            var visited = 0;
            try
            {
                while (stack.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var child in Directory.EnumerateFileSystemEntries(stack.Pop()))
                    {
                        if (++visited > MaxActionTreeEntries)
                        {
                            error = "The selected directory contains too many entries to verify safely.";
                            return false;
                        }
                        if (Path.GetFileName(child).Equals(".svn", PathComparison))
                        {
                            error = "The selected directory contains a nested Subversion working copy.";
                            return false;
                        }
                        var state = GetLocalPathState(child, out _, out var stateError);
                        if (state == LocalPathState.Error)
                        {
                            error = $"A selected directory entry could not be inspected: {stateError}";
                            return false;
                        }
                        if (state == LocalPathState.SymbolicLink)
                        {
                            error = "The selected directory contains a symbolic link or reparse point.";
                            return false;
                        }
                        if (state == LocalPathState.Directory)
                            stack.Push(child);
                    }
                }
                error = string.Empty;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = $"The selected directory could not be inspected safely: {ex.Message}";
                return false;
            }
        }

        private static bool HasUnsafeRevertDescendant(SvnWorkingCopyEntry selected, IReadOnlyList<SvnWorkingCopyEntry> entries)
        {
            return entries.Any(entry => IsPathAncestor(selected.Path, entry.Path) && entry.Item != "unversioned" && !CanRevert(entry));
        }

        private static bool IsSafeOperationEntry(SvnWorkingCopyEntry entry)
        {
            if (IsSafeActionEntry(entry))
                return true;
            return entry != null && entry.Path == "." && entry.Item == "normal" && entry.Properties == "modified" &&
                entry.Kind == "dir" && !entry.IsCopied && !entry.IsSwitched && !entry.IsTreeConflicted &&
                !entry.IsFileExternal && string.IsNullOrEmpty(entry.MovedFrom) && string.IsNullOrEmpty(entry.MovedTo);
        }

        private static bool TryValidateWorkingCopyRoot(string root)
        {
            return GetLocalPathState(root, out _, out _) == LocalPathState.Directory &&
                TryResolveSafeLocalPath(root, root, out _, out var isSymbolicLink) && !isSymbolicLink;
        }

        private static bool TryResolveCommitSelections(
            string root,
            IReadOnlyList<string> relativePaths,
            IReadOnlyList<SvnWorkingCopyEntry> entries,
            out List<OperationSelection> selections,
            out string error)
        {
            selections = new List<OperationSelection>();
            error = "Select at least one changed working-copy path to commit.";
            if (relativePaths == null || relativePaths.Count == 0)
                return false;
            foreach (var relativePath in relativePaths)
            {
                if (!TryResolveSelectedPath(root, relativePath, entries, out var target, out var entry, out error))
                    return false;
                if (!CanCommit(entry))
                {
                    error = "The selection contains an unversioned, missing, conflicted, switched, external, or unsupported path.";
                    return false;
                }
                if (selections.Any(item => PathEquals(item.Entry.Path, entry.Path)))
                {
                    error = "The selection contains the same path more than once.";
                    return false;
                }
                selections.Add(new OperationSelection(target, entry));
            }
            return CanCommitSelection(selections.Select(selection => selection.Entry).ToArray(), entries, out error);
        }

        private static bool AreAllAddedAncestorsSelected(
            SvnWorkingCopyEntry selected,
            IReadOnlyList<SvnWorkingCopyEntry> entries,
            IReadOnlyList<OperationSelection> selections)
        {
            return entries.Where(entry => entry.Item == "added" && entry.Kind == "dir" && IsPathAncestor(entry.Path, selected.Path))
                .All(ancestor => selections.Any(item => PathEquals(item.Entry.Path, ancestor.Path)));
        }

        private static bool HasSelectedMovePartner(SvnWorkingCopyEntry selected, IReadOnlyList<OperationSelection> selections)
        {
            return (selected.MovedFrom == null || selections.Any(item => PathEquals(item.Entry.Path, selected.MovedFrom))) &&
                (selected.MovedTo == null || selections.Any(item => PathEquals(item.Entry.Path, selected.MovedTo)));
        }

        private static bool IsValidUpdateRevision(string revision)
        {
            return revision == "HEAD" || (!string.IsNullOrEmpty(revision) &&
                revision.All(character => character is >= '0' and <= '9') &&
                long.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        }

        private static void AppendConfigArgument(List<string> arguments, string configDirectory, string effectiveConfigDirectory)
        {
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                arguments.Add("--config-dir");
                arguments.Add(effectiveConfigDirectory);
            }
        }

        private static string CreateProtectedMessageFile(string message)
        {
            var directory = Path.GetFullPath(Path.GetTempPath());
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var path = Path.Combine(directory, "sourcegit-svn-commit-" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (!OperatingSystem.IsWindows())
                            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                        var bytes = new UTF8Encoding(false).GetBytes(message);
                        stream.Write(bytes);
                        stream.Flush(true);
                    }
                    return path;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Retry only a name collision; never reuse an existing file.
                }
            }
            throw new IOException("Could not allocate a unique temporary file.");
        }

        private static WorkingCopyActionResult FormatMutationResult(FileCommandResult command, Credentials credentials, string action)
        {
            if (command.Status != "Ready")
            {
                var detail = Redact(command.StandardError, credentials);
                return new WorkingCopyActionResult(command.Status, AppendPartialOperationNotice(detail, action));
            }
            if (command.ExitCode != 0)
            {
                var details = FormatFileCommandFailure(command, credentials);
                if (command.OutputTruncated)
                    details += Environment.NewLine + "[SVN stdout truncated after 128 KiB]";
                return new WorkingCopyActionResult("Failed", AppendPartialOperationNotice(details, action));
            }

            var output = Encoding.UTF8.GetString(command.StandardOutput).Trim();
            if (command.OutputTruncated)
                output = (output.Length == 0 ? string.Empty : output + Environment.NewLine) + "[SVN output truncated after 128 KiB]";
            output = Redact(output, credentials);
            return new WorkingCopyActionResult("Ready", output);
        }

        private static string AppendPartialOperationNotice(string details, string action)
        {
            var notice = $"SVN {action} did not complete; the working copy or repository may have changed partially. Refresh before retrying.";
            return string.IsNullOrWhiteSpace(details) ? notice : details + Environment.NewLine + notice;
        }

        private static bool IsPathAncestor(string parent, string child)
        {
            return child.Length > parent.Length && child.StartsWith(parent, PathComparison) &&
                (child[parent.Length] == Path.DirectorySeparatorChar || child[parent.Length] == Path.AltDirectorySeparatorChar);
        }

        private static bool PathEquals(string left, string right) => left != null && right != null && left.Equals(right, PathComparison);
        private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }
}
