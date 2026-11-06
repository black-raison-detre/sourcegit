using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Native
{
    public static partial class Svn
    {
        private const int MaxChangelistNameLength = 255;
        private const int MaxChangelistSelections = 10_000;

        public sealed record ChangelistSelection(string RelativePath, string Changelist);

        /// <summary>
        /// Returns whether a current status row for a normal, text- or property-modified, or added
        /// local file can be assigned to a changelist. The mutation rechecks its fresh status
        /// snapshot, filesystem, and working-copy boundary safety before SVN is invoked.
        /// </summary>
        public static bool CanChangeChangelist(SvnWorkingCopyEntry entry)
        {
            return entry != null &&
                IsSafeActionEntry(entry) &&
                entry.Item is "normal" or "modified" or "added" &&
                entry.Properties is "none" or "normal" or "modified" &&
                entry.Kind == "file" &&
                !entry.IsCopied && !entry.IsSwitched && !entry.IsTreeConflicted && !entry.IsFileExternal &&
                string.IsNullOrEmpty(entry.MovedFrom) && string.IsNullOrEmpty(entry.MovedTo);
        }

        /// <summary>
        /// Returns whether the selected file is beneath a switched, external, scheduled, copied,
        /// moved, or conflicted working-copy boundary.
        /// </summary>
        public static bool HasUnsafeChangelistAncestor(
            SvnWorkingCopyEntry entry,
            IReadOnlyList<SvnWorkingCopyEntry> entries)
        {
            return entry == null || string.IsNullOrEmpty(entry.Path) || entries == null ||
                HasUnsafePathOperationAncestor(entry.Path, entries);
        }

        /// <summary>
        /// Validates a changelist name while preserving its original spelling and whitespace.
        /// </summary>
        public static bool TryValidateChangelistName(string name, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "Enter a changelist name.";
                return false;
            }
            if (name.Length > MaxChangelistNameLength)
            {
                error = $"Changelist names must be {MaxChangelistNameLength} characters or shorter.";
                return false;
            }
            if (name.Any(char.IsControl))
            {
                error = "Changelist names cannot contain control characters.";
                return false;
            }
            try
            {
                _ = new UTF8Encoding(false, true).GetByteCount(name);
            }
            catch (EncoderFallbackException)
            {
                error = "Changelist names must contain valid Unicode text.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Assigns the explicitly selected current status rows to a changelist after matching each
        /// captured membership. Targets are files and use depth empty, so no descendants are visited.
        /// </summary>
        public static Task<WorkingCopyActionResult> SetChangelistAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            IReadOnlyList<ChangelistSelection> selections,
            string name,
            CancellationToken cancellationToken = default)
        {
            return ChangeChangelistAsync(executable, configDirectory, workingCopyPath,
                selections, name, remove: false, cancellationToken);
        }

        /// <summary>
        /// Removes changelist membership from explicitly selected current status rows after matching
        /// each captured membership. Targets are files and use depth empty, so no descendants are visited.
        /// </summary>
        public static Task<WorkingCopyActionResult> RemoveFromChangelistAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            IReadOnlyList<ChangelistSelection> selections,
            CancellationToken cancellationToken = default)
        {
            return ChangeChangelistAsync(executable, configDirectory, workingCopyPath,
                selections, name: null, remove: true, cancellationToken);
        }

        private sealed record PreparedChangelistTarget(string RelativePath, string Target);

        private static async Task<WorkingCopyActionResult> ChangeChangelistAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            IReadOnlyList<ChangelistSelection> selections,
            string name,
            bool remove,
            CancellationToken cancellationToken)
        {
            var action = remove ? "changelist removal" : "changelist assignment";
            var mutationStarted = false;
            try
            {
                if (selections == null || selections.Count == 0 || selections.Count > MaxChangelistSelections)
                    return new WorkingCopyActionResult("InvalidArgument", "Select between 1 and 10,000 files.");
                if (!remove && !TryValidateChangelistName(name, out var nameError))
                    return new WorkingCopyActionResult("InvalidArgument", nameError);

                var status = await GetStatusCoreAsync(executable, configDirectory, workingCopyPath,
                    cancellationToken, includeExternals: true).ConfigureAwait(false);
                if (status.Result.Status != "Ready")
                    return new WorkingCopyActionResult(status.Result.Status, status.Result.Details);
                if (string.IsNullOrEmpty(status.WorkingCopyRoot))
                    return new WorkingCopyActionResult("InvalidPath", "SVN returned an invalid working-copy root path.");
                if (!TryPrepareWorkingCopyRoot(status.WorkingCopyRoot, out var root, out var rootError))
                    return new WorkingCopyActionResult("InvalidPath", rootError);
                if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                    return new WorkingCopyActionResult("InvalidConfigDirectory", "The Subversion configuration directory is invalid.");

                var allEntries = status.Result.Entries ?? Array.Empty<SvnWorkingCopyEntry>();
                var seenPaths = new HashSet<string>(PathComparer);
                var prepared = new List<PreparedChangelistTarget>(selections.Count);
                var changed = new List<PreparedChangelistTarget>(selections.Count);
                foreach (var selection in selections)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (selection == null)
                        return new WorkingCopyActionResult("InvalidPath", "A changelist selection is invalid. Refresh status and retry.");
                    if (!TryResolvePathOperationPath(root, selection.RelativePath,
                            out var normalizedPath, out var target, out var pathError))
                        return new WorkingCopyActionResult("InvalidPath", pathError);
                    if (!seenPaths.Add(normalizedPath))
                        return new WorkingCopyActionResult("InvalidArgument", "A file was selected more than once.");

                    if (!TryResolveSelectedPath(root, normalizedPath, allEntries, out _, out var entry, out _))
                        return new WorkingCopyActionResult("StaleSelection", "A selected file is no longer in fresh working-copy status. Refresh and retry.");
                    if (!MembershipMatches(selection.Changelist, entry.Changelist))
                        return new WorkingCopyActionResult("StaleSelection", "A selected file's changelist changed while the dialog was open. Refresh and retry.");
                    if (!CanChangeChangelist(entry))
                        return new WorkingCopyActionResult("NotEligible", "Only normal, modified, or added regular files without copies, moves, conflicts, switches, or externals can be assigned to a changelist.");
                    if (HasUnsafeChangelistAncestor(entry, allEntries))
                        return new WorkingCopyActionResult("NotEligible", "A selected file is beneath a switched, external, scheduled, copied, moved, or conflicted path.");
                    if (allEntries.Any(candidate => candidate != null && IsPathAncestor(normalizedPath, candidate.Path)))
                        return new WorkingCopyActionResult("NotEligible", "A selected file has descendant working-copy status and cannot be treated as a file target.");
                    if (!TryValidatePathOperationPath(root, target, allowMissingFinal: false,
                            cancellationToken, out var localState, out var localPathError))
                        return new WorkingCopyActionResult("UnsafePath", localPathError);
                    if (localState != LocalPathState.File || entry.Kind != "file")
                        return new WorkingCopyActionResult("NotEligible", "A selected target is not an existing regular file.");

                    var targetSelection = new PreparedChangelistTarget(normalizedPath, target);
                    prepared.Add(targetSelection);
                    if (remove ? !string.IsNullOrEmpty(entry.Changelist) : !string.Equals(entry.Changelist, name, StringComparison.Ordinal))
                        changed.Add(targetSelection);
                }

                if (changed.Count == 0)
                    return new WorkingCopyActionResult("Ready");

                var arguments = new List<string> { "changelist" };
                if (remove)
                    arguments.Add("--remove");
                arguments.Add("--quiet");
                arguments.Add("--depth");
                arguments.Add("empty");
                arguments.Add("--non-interactive");
                arguments.Add("--no-auth-cache");
                AppendConfigArgument(arguments, configDirectory, effectiveConfigDirectory);
                arguments.Add("--");
                if (!remove)
                    arguments.Add(name);
                arguments.AddRange(changed.Select(selection => WithPegEscape(selection.Target)));

                cancellationToken.ThrowIfCancellationRequested();
                mutationStarted = true;
                var command = await RunFileCommandAsync(executable, arguments, credentials: null,
                    128 * 1024, cancellationToken, s_operationTimeout, killOnOutputLimit: false).ConfigureAwait(false);
                if (command.Status != "Ready")
                {
                    var details = command.Status == "FailedToStart"
                        ? command.StandardError
                        : AppendPartialOperationNotice(command.StandardError, action);
                    return new WorkingCopyActionResult(command.Status, details);
                }
                if (command.OutputTruncated)
                    return new WorkingCopyActionResult("Failed", AppendPartialOperationNotice(
                        "SVN changelist output exceeded the 128 KiB limit.", action));
                if (command.ExitCode != 0)
                    return new WorkingCopyActionResult("Failed",
                        AppendPartialOperationNotice(FormatFileCommandFailure(command, credentials: null), action));

                var refreshed = await GetStatusCoreAsync(executable, configDirectory, workingCopyPath,
                    cancellationToken, includeExternals: true).ConfigureAwait(false);
                if (refreshed.Result.Status != "Ready" || !PathEquals(refreshed.WorkingCopyRoot, root))
                {
                    var details = refreshed.Result.Status == "Ready"
                        ? "The working-copy root changed after the changelist command."
                        : refreshed.Result.Details;
                    return new WorkingCopyActionResult("Failed", AppendPartialOperationNotice(
                        "SVN reported success, but fresh local status could not verify the selected membership. " + details,
                        action));
                }

                var refreshedEntries = refreshed.Result.Entries ?? Array.Empty<SvnWorkingCopyEntry>();
                foreach (var selection in prepared)
                {
                    var current = refreshedEntries.FirstOrDefault(candidate => candidate != null && PathEquals(candidate.Path, selection.RelativePath));
                    var currentMembership = current?.Changelist;
                    var expectedMembership = remove ? null : name;
                    if (!MembershipMatches(expectedMembership, currentMembership))
                        return new WorkingCopyActionResult("Failed", AppendPartialOperationNotice(
                            $"SVN reported success, but fresh status did not confirm the requested changelist for '{selection.RelativePath}'.",
                            action));
                }

                return new WorkingCopyActionResult("Ready");
            }
            catch (OperationCanceledException)
            {
                var details = mutationStarted
                    ? $"SVN {action} was cancelled after it started; the working copy may have changed partially. Refresh status before retrying."
                    : $"SVN {action} was cancelled before it started.";
                return new WorkingCopyActionResult("Cancelled", details);
            }
        }

        private static bool MembershipMatches(string expected, string actual)
        {
            if (string.IsNullOrEmpty(expected))
                return string.IsNullOrEmpty(actual);
            return string.Equals(expected, actual, StringComparison.Ordinal);
        }
    }
}
