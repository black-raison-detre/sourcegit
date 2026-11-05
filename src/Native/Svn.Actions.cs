using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Native
{
    public static partial class Svn
    {
        private const int MaxActionTreeEntries = 100_000;

        public record WorkingCopyActionResult(string Status, string Details = "");

        /// <summary>
        /// Returns whether a fresh unversioned status entry is eligible for a non-recursive svn add.
        /// Path and filesystem safety are rechecked by AddAsync immediately before invoking SVN.
        /// </summary>
        public static bool CanAdd(SvnWorkingCopyEntry entry)
        {
            return entry != null &&
                IsSafeActionEntry(entry) &&
                entry.Item == "unversioned" &&
                HasNoConflictingProperties(entry) &&
                !entry.IsCopied && !entry.IsSwitched && !entry.IsTreeConflicted && !entry.IsFileExternal &&
                string.IsNullOrEmpty(entry.MovedFrom) && string.IsNullOrEmpty(entry.MovedTo) &&
                entry.Kind is "file" or "dir";
        }

        /// <summary>
        /// Returns whether a fresh normal, text-modified, or missing status entry is eligible for
        /// svn delete --keep-local. Adds, replacements, copies, moves, conflicts and property edits
        /// are intentionally excluded because removing their metadata could discard pending work.
        /// </summary>
        public static bool CanRemove(SvnWorkingCopyEntry entry)
        {
            return entry != null &&
                IsSafeActionEntry(entry) &&
                entry.Item is "normal" or "modified" or "missing" &&
                HasNoConflictingProperties(entry) &&
                !entry.IsCopied && !entry.IsSwitched && !entry.IsTreeConflicted && !entry.IsFileExternal &&
                string.IsNullOrEmpty(entry.MovedFrom) && string.IsNullOrEmpty(entry.MovedTo) &&
                (entry.Kind is "file" or "dir" || (entry.Item == "missing" && entry.Kind == "unknown"));
        }

        /// <summary>
        /// Schedules one currently unversioned item for addition. A directory is added at depth
        /// empty so its children remain independently visible and are not silently added with it.
        /// </summary>
        public static Task<WorkingCopyActionResult> AddAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            return RunWorkingCopyActionAsync(add: true, executable, configDirectory, workingCopyPath, relativePath, cancellationToken);
        }

        /// <summary>
        /// Schedules a normal, text-modified, or missing item for deletion while retaining every
        /// local file. No filesystem deletion is performed by this API.
        /// </summary>
        public static Task<WorkingCopyActionResult> RemoveAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            return RunWorkingCopyActionAsync(add: false, executable, configDirectory, workingCopyPath, relativePath, cancellationToken);
        }

        private static async Task<WorkingCopyActionResult> RunWorkingCopyActionAsync(
            bool add,
            string executable,
            string configDirectory,
            string workingCopyPath,
            string relativePath,
            CancellationToken cancellationToken)
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

            if (add ? !CanAdd(entry) : !CanRemove(entry))
                return new WorkingCopyActionResult("NotEligible",
                    add ? "Only safe, unversioned files and directories can be added." :
                        "Only normal, text-modified, or missing files and directories without conflicts or property edits can be removed.");

            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new WorkingCopyActionResult("InvalidConfigDirectory", "The Subversion configuration directory is invalid.");

            if (!TryValidateActionPath(root, target, entry, add, cancellationToken, out var pathError))
                return new WorkingCopyActionResult("UnsafePath", pathError);

            if (!add && (entry.Kind == "dir" || entry.Item == "missing") &&
                HasUnsafeDescendantStatus(entry, status.Result.Entries))
                return new WorkingCopyActionResult("NotEligible",
                    "The directory contains a scheduled, conflicted, switched, copied, moved, external, or property-modified item.");

            var arguments = new List<string> { add ? "add" : "delete" };
            if (add)
            {
                arguments.Add("--depth");
                arguments.Add("empty");
            }
            else
                arguments.Add("--keep-local");
            arguments.Add("--non-interactive");
            arguments.Add("--no-auth-cache");
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                arguments.Add("--config-dir");
                arguments.Add(effectiveConfigDirectory);
            }
            arguments.Add("--");
            arguments.Add(WithPegEscape(target));

            var result = await RunFileCommandAsync(executable, arguments, credentials: null, 128 * 1024, cancellationToken).ConfigureAwait(false);
            if (result.Status != "Ready")
            {
                var details = result.Status is "TimedOut" or "Failed"
                    ? AppendPartialMutationNotice(result.StandardError, add)
                    : result.StandardError;
                return new WorkingCopyActionResult(result.Status, details);
            }
            if (result.ExitCode != 0)
            {
                var details = FormatFileCommandFailure(result, credentials: null);
                return new WorkingCopyActionResult("Failed", AppendPartialMutationNotice(details, add));
            }

            return new WorkingCopyActionResult("Ready");
        }

        private static bool HasNoConflictingProperties(SvnWorkingCopyEntry entry)
        {
            return entry.Properties is "none" or "normal";
        }

        private static bool IsSafeActionEntry(SvnWorkingCopyEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || HasUnsafeLineBreak(entry.Path) || Path.IsPathRooted(entry.Path) || entry.Path == ".")
                return false;

            var components = entry.Path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return components.Length > 0 && components.All(component =>
                component is not ("." or "..") && !component.Equals(".svn", comparer));
        }

        private static bool HasUnsafeDescendantStatus(
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
                if (CanRemove(child) || child.Item == "unversioned")
                    continue;
                return true;
            }

            return false;
        }

        private static bool TryValidateActionPath(
            string root,
            string target,
            SvnWorkingCopyEntry entry,
            bool add,
            CancellationToken cancellationToken,
            out string error)
        {
            error = "The selected path could not be safely resolved inside this working copy.";
            if (!TryMakeRelativePath(root, target, out var relative) || relative == ".")
                return false;

            var current = root;
            var components = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            LocalPathState selectedState = LocalPathState.Missing;
            for (var index = 0; index < components.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = Path.Combine(current, components[index]);
                var state = GetLocalPathState(current, out _, out var stateError);
                if (state == LocalPathState.Error)
                {
                    error = $"The selected path could not be inspected: {stateError}";
                    return false;
                }
                if (state == LocalPathState.SymbolicLink)
                {
                    error = "The selected path or one of its parent directories is a symbolic link or reparse point.";
                    return false;
                }
                if (state == LocalPathState.Missing)
                {
                    selectedState = LocalPathState.Missing;
                    break;
                }

                if (index < components.Length - 1 && state != LocalPathState.Directory)
                {
                    error = "A parent component of the selected path is not a directory.";
                    return false;
                }

                if (state == LocalPathState.Directory)
                {
                    var nestedMarker = GetLocalPathState(Path.Combine(current, ".svn"), out _, out var markerError);
                    if (nestedMarker != LocalPathState.Missing)
                    {
                        error = nestedMarker == LocalPathState.Error
                            ? $"A Subversion working-copy boundary could not be inspected: {markerError}"
                            : "The selected path is inside a nested Subversion working copy.";
                        return false;
                    }
                }

                selectedState = state;
            }

            if (!TryResolveSafeLocalPath(root, target, out _, out var isSymbolicLink) || isSymbolicLink)
            {
                error = "The selected path or one of its parent directories resolves outside this working copy.";
                return false;
            }

            if (add && selectedState == LocalPathState.Missing)
            {
                error = "The unversioned path no longer exists. Refresh status before trying again.";
                return false;
            }
            if (selectedState != LocalPathState.Missing &&
                ((entry.Kind == "dir" && selectedState != LocalPathState.Directory) ||
                 (entry.Kind == "file" && selectedState != LocalPathState.File)))
            {
                error = "The selected path changed type after status was read. Refresh status before trying again.";
                return false;
            }

            if (!add && entry.Kind == "dir" && selectedState == LocalPathState.Directory &&
                !TryCheckNestedWorkingCopies(target, cancellationToken, out error))
                return false;

            return true;
        }

        private static bool TryCheckNestedWorkingCopies(string selectedDirectory, CancellationToken cancellationToken, out string error)
        {
            error = "The selected directory could not be inspected safely.";
            var stack = new Stack<string>();
            stack.Push(selectedDirectory);
            var visited = 0;
            var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            try
            {
                while (stack.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var directory = stack.Pop();
                    foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++visited > MaxActionTreeEntries)
                        {
                            error = "The directory contains too many entries to verify nested working-copy boundaries safely.";
                            return false;
                        }

                        if (Path.GetFileName(child).Equals(".svn", comparer))
                        {
                            error = "The selected directory contains a nested Subversion working copy.";
                            return false;
                        }

                        var state = GetLocalPathState(child, out _, out var stateError);
                        if (state == LocalPathState.Error)
                        {
                            error = $"A directory entry could not be inspected: {stateError}";
                            return false;
                        }
                        if (state == LocalPathState.Directory)
                            stack.Push(child);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = $"The selected directory could not be inspected safely: {ex.Message}";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static string AppendPartialMutationNotice(string details, bool add)
        {
            var action = add ? "add" : "delete";
            var notice = $"SVN {action} did not complete; the working copy may have been partially changed. Refresh status before retrying.";
            return string.IsNullOrWhiteSpace(details) ? notice : details + Environment.NewLine + notice;
        }
    }
}
