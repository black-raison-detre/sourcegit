using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace SourceGit.Native
{
    public static partial class Svn
    {
        /// <summary>
        /// Returns whether a visible normal or locally edited file or directory can be moved or copied.
        /// The operation rechecks status and filesystem safety immediately before invoking SVN.
        /// </summary>
        public static bool CanMoveOrCopy(SvnWorkingCopyEntry entry)
        {
            return entry != null && IsSafeActionEntry(entry) &&
                entry.Item is "normal" or "modified" &&
                entry.Properties is "none" or "normal" or "modified" &&
                !entry.IsCopied && !entry.IsSwitched && !entry.IsTreeConflicted && !entry.IsFileExternal &&
                string.IsNullOrEmpty(entry.MovedFrom) && string.IsNullOrEmpty(entry.MovedTo) &&
                entry.Kind is "file" or "dir";
        }

        public static Task<WorkingCopyActionResult> MoveAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string sourceRelativePath,
            string destinationRelativePath,
            CancellationToken cancellationToken = default)
        {
            return RunPathOperationAsync(executable, configDirectory, workingCopyPath,
                sourceRelativePath, destinationRelativePath, move: true, cancellationToken);
        }

        public static Task<WorkingCopyActionResult> CopyAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string sourceRelativePath,
            string destinationRelativePath,
            CancellationToken cancellationToken = default)
        {
            return RunPathOperationAsync(executable, configDirectory, workingCopyPath,
                sourceRelativePath, destinationRelativePath, move: false, cancellationToken);
        }

        private sealed record PathStatusResult(string Status, IReadOnlyList<SvnWorkingCopyEntry> Entries = null, string Details = "");

        private static async Task<WorkingCopyActionResult> RunPathOperationAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string sourceRelativePath,
            string destinationRelativePath,
            bool move,
            CancellationToken cancellationToken)
        {
            var action = move ? "move" : "copy";
            var mutationStarted = false;
            try
            {
                var resolved = await GetStatusCoreAsync(executable, configDirectory, workingCopyPath,
                    cancellationToken, includeExternals: true).ConfigureAwait(false);
                if (resolved.Result.Status != "Ready")
                    return new WorkingCopyActionResult(resolved.Result.Status, resolved.Result.Details);
                if (!TryPrepareWorkingCopyRoot(resolved.WorkingCopyRoot, out var root, out var rootError))
                    return new WorkingCopyActionResult("InvalidPath", rootError);
                if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                    return new WorkingCopyActionResult("InvalidConfigDirectory", "The Subversion configuration directory is invalid.");
                if (!TryResolvePathOperationPath(root, sourceRelativePath, out var sourceRelative, out var source,
                        out var sourcePathError))
                    return new WorkingCopyActionResult("InvalidPath", sourcePathError);
                if (!TryResolvePathOperationPath(root, destinationRelativePath, out var destinationRelative, out var destination,
                        out var destinationPathError))
                    return new WorkingCopyActionResult("InvalidPath", destinationPathError);
                if (PathEquals(sourceRelative, destinationRelative))
                    return new WorkingCopyActionResult("InvalidArgument", "The source and destination must be different paths.");
                if (!move && IsPathAncestor(sourceRelative, destinationRelative))
                    return new WorkingCopyActionResult("InvalidArgument", "A directory cannot be copied into itself.");

                if (!TryValidatePathOperationPath(root, source, allowMissingFinal: false,
                        cancellationToken, out var sourceState, out var sourcePathSafetyError))
                    return new WorkingCopyActionResult("UnsafePath", sourcePathSafetyError);
                if (sourceState is not (LocalPathState.File or LocalPathState.Directory))
                    return new WorkingCopyActionResult("NotEligible", "The source is not an existing regular file or directory.");
                if (!TryValidatePathOperationPath(root, destination, allowMissingFinal: true,
                        cancellationToken, out var destinationState, out var destinationSafetyError))
                    return new WorkingCopyActionResult("UnsafePath", destinationSafetyError);
                if (destinationState != LocalPathState.Missing)
                    return new WorkingCopyActionResult("DestinationExists", "The exact destination path already exists.");

                var initialStatus = resolved.Result.Entries ?? Array.Empty<SvnWorkingCopyEntry>();
                if (HasUnsafePathOperationAncestor(sourceRelative, initialStatus) ||
                    HasUnsafePathOperationAncestor(destinationRelative, initialStatus))
                    return new WorkingCopyActionResult("NotEligible", "The source or destination is beneath a switched, external, scheduled, copied, moved, missing, replaced, or conflicted path.");
                if (initialStatus.Any(entry => entry != null && PathEquals(entry.Path, destinationRelative)))
                    return new WorkingCopyActionResult("DestinationExists", "The destination has an existing Subversion working-copy schedule.");

                var sourceInfo = await GetPathInfoAsync(executable, configDirectory, effectiveConfigDirectory,
                    root, source, cancellationToken).ConfigureAwait(false);
                if (sourceInfo.Status != "Ready")
                    return new WorkingCopyActionResult(sourceInfo.Status, sourceInfo.Details);
                if (!sourceInfo.IsVersioned || !PathEquals(sourceInfo.WorkingCopyRoot, root) ||
                    sourceInfo.Kind is not ("file" or "dir") || sourceInfo.Kind != (sourceState == LocalPathState.Directory ? "dir" : "file"))
                    return new WorkingCopyActionResult("NotEligible", "The source is not a normal versioned file or directory in this working copy.");

                var sourceStatus = await GetPathStatusAsync(executable, configDirectory, effectiveConfigDirectory,
                    root, source, sourceInfo.Kind == "dir", cancellationToken).ConfigureAwait(false);
                if (sourceStatus.Status != "Ready")
                    return new WorkingCopyActionResult(sourceStatus.Status, sourceStatus.Details);
                var sourceEntry = sourceStatus.Entries?.FirstOrDefault(entry => PathEquals(entry.Path, sourceRelative));
                if (!CanMoveOrCopy(sourceEntry))
                    return new WorkingCopyActionResult("NotEligible", "The source must be a normal or locally edited versioned file or directory without a pending schedule, conflict, switch, or external.");
                if (sourceStatus.Entries.Any(entry => !CanMoveOrCopy(entry)))
                    return new WorkingCopyActionResult("NotEligible", "The source directory contains a scheduled, unversioned, ignored, missing, conflicted, switched, external, copied, moved, or unsupported entry.");
                if (sourceStatus.Entries.Any(entry => !entry.Revision.HasValue) ||
                    sourceStatus.Entries.Select(entry => entry.Revision).Distinct().Count() != 1)
                    return new WorkingCopyActionResult("NotEligible", "The source contains missing or mixed working-copy revisions.");
                if (HasUnsafePathOperationAncestor(sourceRelative, initialStatus))
                    return new WorkingCopyActionResult("NotEligible", "The source is beneath an unsafe working-copy status boundary.");

                if (sourceInfo.Kind == "dir")
                {
                    if (move && IsPathAncestor(sourceRelative, destinationRelative))
                        return new WorkingCopyActionResult("InvalidArgument", "A directory cannot be moved into itself.");
                    if (!TryInspectPathOperationTree(root, source, cancellationToken, out var treePaths, out var treeError))
                        return new WorkingCopyActionResult("UnsafePath", treeError);
                    var statusPaths = sourceStatus.Entries.Select(entry => entry.Path).ToHashSet(PathComparer);
                    if (treePaths.Any(path => !statusPaths.Contains(path)) || statusPaths.Any(path => !treePaths.Contains(path)))
                        return new WorkingCopyActionResult("NotEligible", "The complete directory tree could not be matched to versioned local status. Refresh status and check for sparse, ignored, or untracked descendants.");
                }
                else if (sourceStatus.Entries.Any(entry => !PathEquals(entry.Path, sourceRelative)))
                {
                    return new WorkingCopyActionResult("NotEligible", "The file source has unexpected descendant status entries.");
                }

                var destinationParentRelative = Path.GetDirectoryName(destinationRelative);
                if (string.IsNullOrEmpty(destinationParentRelative))
                    destinationParentRelative = ".";
                var destinationParent = destinationParentRelative == "."
                    ? root
                    : Path.GetFullPath(destinationParentRelative, root);
                LocalPathState parentState;
                string parentSafetyError;
                if (destinationParentRelative == ".")
                {
                    parentState = LocalPathState.Directory;
                    parentSafetyError = TryValidateWorkingCopyRoot(root)
                        ? string.Empty
                        : "The working-copy root could not be resolved safely.";
                }
                else if (!TryValidatePathOperationPath(root, destinationParent, allowMissingFinal: false,
                             cancellationToken, out parentState, out parentSafetyError))
                {
                    parentSafetyError = string.IsNullOrEmpty(parentSafetyError)
                        ? "The destination parent must be an existing safe directory in this working copy."
                        : parentSafetyError;
                }
                if (parentState != LocalPathState.Directory || !string.IsNullOrEmpty(parentSafetyError))
                    return new WorkingCopyActionResult("UnsafePath", string.IsNullOrEmpty(parentSafetyError)
                        ? "The destination parent must be an existing safe directory in this working copy."
                        : parentSafetyError);

                var parentInfo = await GetPathInfoAsync(executable, configDirectory, effectiveConfigDirectory,
                    root, destinationParent, cancellationToken).ConfigureAwait(false);
                if (parentInfo.Status != "Ready")
                    return new WorkingCopyActionResult(parentInfo.Status, parentInfo.Details);
                if (!parentInfo.IsVersioned || !PathEquals(parentInfo.WorkingCopyRoot, root) || parentInfo.Kind != "dir")
                    return new WorkingCopyActionResult("NotEligible", "The destination parent must be a versioned directory in the same working copy.");
                var parentStatus = await GetPathStatusAsync(executable, configDirectory, effectiveConfigDirectory,
                    root, destinationParent, depthInfinity: false, cancellationToken).ConfigureAwait(false);
                if (parentStatus.Status != "Ready")
                    return new WorkingCopyActionResult(parentStatus.Status, parentStatus.Details);
                var parentEntry = parentStatus.Entries?.FirstOrDefault(entry => PathEquals(entry.Path, destinationParentRelative));
                if (!IsSafePathOperationParent(parentEntry))
                    return new WorkingCopyActionResult("NotEligible", "The destination parent must be a normal, unchanged versioned directory.");
                if (HasUnsafePathOperationAncestor(destinationParentRelative, initialStatus))
                    return new WorkingCopyActionResult("NotEligible", "The destination parent is beneath a switched, external, scheduled, copied, moved, missing, replaced, or conflicted path.");

                var destinationInfo = await GetPathInfoAsync(executable, configDirectory, effectiveConfigDirectory,
                    root, destination, cancellationToken).ConfigureAwait(false);
                if (destinationInfo.Status == "Ready")
                    return new WorkingCopyActionResult("DestinationExists", "The destination already has Subversion working-copy metadata.");
                if (destinationInfo.Status != "NotVersioned" || !IsExpectedMissingNodeDiagnostic(destinationInfo.Details))
                    return new WorkingCopyActionResult(destinationInfo.Status == "NotVersioned" ? "Failed" : destinationInfo.Status,
                        string.IsNullOrWhiteSpace(destinationInfo.Details)
                            ? "SVN could not confirm that the destination is absent from working-copy metadata."
                            : destinationInfo.Details);

                // Recheck the destination and source immediately before scheduling the operation.
                if (!TryValidatePathOperationPath(root, source, allowMissingFinal: false,
                        cancellationToken, out var finalSourceState, out var finalSourceError) || finalSourceState != sourceState)
                    return new WorkingCopyActionResult("UnsafePath", string.IsNullOrEmpty(finalSourceError)
                        ? "The source changed while the operation was being prepared. Refresh status and retry."
                        : finalSourceError);
                if (!TryValidatePathOperationPath(root, destination, allowMissingFinal: true,
                        cancellationToken, out var finalDestinationState, out var finalDestinationError) || finalDestinationState != LocalPathState.Missing)
                    return new WorkingCopyActionResult("DestinationExists", string.IsNullOrEmpty(finalDestinationError)
                        ? "The exact destination path changed while the operation was being prepared."
                        : finalDestinationError);

                var arguments = new List<string> { action, "--non-interactive", "--no-auth-cache" };
                AppendConfigArgument(arguments, configDirectory, effectiveConfigDirectory);
                arguments.Add("--");
                arguments.Add(WithPegEscape(source));
                // SVN 1.14 parses peg escapes for copy destinations but move destinations are literal.
                arguments.Add(move ? destination : WithPegEscape(destination));

                cancellationToken.ThrowIfCancellationRequested();
                mutationStarted = true;
                var command = await RunFileCommandAsync(executable, arguments, null, MaxOperationOutputBytes,
                    cancellationToken, s_operationTimeout, killOnOutputLimit: false).ConfigureAwait(false);
                return FormatMutationResult(command, null, action);
            }
            catch (OperationCanceledException)
            {
                var details = mutationStarted
                    ? $"SVN {action} was cancelled after it started; the working copy may have changed partially. Refresh status before retrying."
                    : $"SVN {action} was cancelled before it started.";
                return new WorkingCopyActionResult("Cancelled", details);
            }
        }

        private sealed record PathInfoResult(string Status, string WorkingCopyRoot = "", string Kind = "", bool IsVersioned = false, string Details = "");

        private static async Task<PathInfoResult> GetPathInfoAsync(
            string executable,
            string configDirectory,
            string effectiveConfigDirectory,
            string root,
            string target,
            CancellationToken cancellationToken)
        {
            var arguments = new List<string> { "info", "--xml", "--depth", "empty", "--non-interactive" };
            AppendConfigArgument(arguments, configDirectory, effectiveConfigDirectory);
            arguments.Add("--no-auth-cache");
            arguments.Add("--");
            arguments.Add(WithPegEscape(target));
            var command = await RunFileCommandAsync(executable, arguments, null, MaxFileMetadataBytes,
                cancellationToken).ConfigureAwait(false);
            if (command.Status != "Ready")
                return new PathInfoResult(command.Status, Details: command.StandardError);
            if (command.OutputTruncated)
                return new PathInfoResult("TooLarge", Details: "SVN info XML exceeded the metadata output limit.");
            if (command.ExitCode != 0)
                return new PathInfoResult("NotVersioned", Details: FormatFileCommandFailure(command, null));

            try
            {
                var document = LoadSafeXml(DecodeXml(command.StandardOutput), MaxFileMetadataBytes);
                var entry = document.DocumentElement?.SelectSingleNode("entry") as XmlElement;
                var wcInfo = entry?.SelectSingleNode("wc-info") as XmlElement;
                var wcRoot = wcInfo?.SelectSingleNode("wcroot-abspath")?.InnerText;
                var kind = entry?.GetAttribute("kind");
                var schedule = wcInfo?.SelectSingleNode("schedule")?.InnerText;
                var copyFromUrl = wcInfo?.SelectSingleNode("copy-from-url")?.InnerText;
                if (entry == null || wcInfo == null || string.IsNullOrWhiteSpace(wcRoot) ||
                    !Path.IsPathFullyQualified(wcRoot) || string.IsNullOrEmpty(kind))
                    return new PathInfoResult("Failed", Details: "SVN returned incomplete working-copy path metadata.");
                var fullRoot = Path.GetFullPath(wcRoot);
                return new PathInfoResult("Ready", fullRoot, kind,
                    (string.IsNullOrEmpty(schedule) || schedule == "normal") && string.IsNullOrEmpty(copyFromUrl));
            }
            catch (Exception ex) when (ex is XmlException or DecoderFallbackException or ArgumentException or IOException)
            {
                return new PathInfoResult("Failed", Details: $"SVN returned malformed working-copy path metadata: {ex.Message}");
            }
        }

        private static async Task<PathStatusResult> GetPathStatusAsync(
            string executable,
            string configDirectory,
            string effectiveConfigDirectory,
            string root,
            string target,
            bool depthInfinity,
            CancellationToken cancellationToken)
        {
            var arguments = new List<string>
            {
                "status", "--xml", "--verbose", "--no-ignore", "--ignore-externals",
                "--depth", depthInfinity ? "infinity" : "empty", "--non-interactive",
            };
            AppendConfigArgument(arguments, configDirectory, effectiveConfigDirectory);
            arguments.Add("--no-auth-cache");
            arguments.Add("--");
            arguments.Add(WithPegEscape(target));
            var command = await RunFileCommandAsync(executable, arguments, null, MaxStatusXmlBytes,
                cancellationToken).ConfigureAwait(false);
            if (command.Status != "Ready")
                return new PathStatusResult(command.Status, Details: command.StandardError);
            if (command.OutputTruncated)
                return new PathStatusResult("TooLarge", Details: "SVN status XML exceeded the 8 MiB output limit.");
            if (command.ExitCode != 0)
                return new PathStatusResult("Failed", Details: FormatFileCommandFailure(command, null));

            try
            {
                var document = LoadSafeXml(DecodeXml(command.StandardOutput), MaxStatusXmlBytes);
                var status = document.DocumentElement;
                if (status == null || status.Name != "status")
                    return new PathStatusResult("Failed", Details: "SVN returned incomplete working-copy status XML.");
                var nodes = status.SelectNodes("target/entry | target/changelist/entry | changelist/entry");
                if (nodes == null || nodes.Count > MaxStatusEntries)
                    return new PathStatusResult("TooLarge", Details: "SVN status exceeded the 10,000 entry limit.");
                var entries = new List<SvnWorkingCopyEntry>(nodes.Count);
                foreach (XmlNode node in nodes)
                {
                    if (node is not XmlElement entry)
                        return new PathStatusResult("Failed", Details: "SVN returned an invalid working-copy status entry.");
                    if (!TryGetWorkingCopyStatus(root, entry, out var parsed, out var error))
                        return new PathStatusResult("Failed", Details: error);
                    entries.Add(parsed);
                }
                return new PathStatusResult("Ready", Array.AsReadOnly(entries.ToArray()));
            }
            catch (Exception ex) when (ex is XmlException or DecoderFallbackException or ArgumentException or IOException)
            {
                return new PathStatusResult("Failed", Details: $"SVN returned malformed working-copy status XML: {ex.Message}");
            }
        }

        private static bool TryResolvePathOperationPath(string root, string relativePath, out string normalizedRelative,
            out string target, out string error)
        {
            normalizedRelative = null;
            target = null;
            error = "The source and destination must be canonical working-copy-relative paths.";
            if (string.IsNullOrWhiteSpace(relativePath) || HasUnsafeLineBreak(relativePath) || Path.IsPathRooted(relativePath) ||
                relativePath.EndsWith(Path.DirectorySeparatorChar) || relativePath.EndsWith(Path.AltDirectorySeparatorChar))
                return false;
            try
            {
                var comparisonPath = OperatingSystem.IsWindows()
                    ? relativePath.Replace('/', Path.DirectorySeparatorChar)
                    : relativePath;
                target = Path.GetFullPath(relativePath, root);
                if (!TryMakeRelativePath(root, target, out normalizedRelative) || normalizedRelative == "." ||
                    !normalizedRelative.Equals(comparisonPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return false;
                var separator = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
                var components = normalizedRelative.Split(separator, StringSplitOptions.RemoveEmptyEntries);
                if (components.Any(component => component.Equals(".svn", StringComparison.OrdinalIgnoreCase)))
                {
                    error = "Subversion administrative paths cannot be moved or copied.";
                    return false;
                }
                if (components.Any(IsUnsafeWindowsPathComponent))
                {
                    error = "The path contains a filename that is invalid or ambiguous on Windows.";
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = "The source or destination path could not be resolved safely.";
                return false;
            }
        }

        private static bool IsUnsafeWindowsPathComponent(string component)
        {
            if (string.IsNullOrEmpty(component) || component.EndsWith(' ') || component.EndsWith('.') ||
                component.Any(char.IsControl))
                return true;
            if (component.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0)
                return true;
            var deviceName = component.Split('.')[0];
            if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
                return true;
            return deviceName.Length == 4 &&
                (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                deviceName[3] is >= '1' and <= '9';
        }

        private static bool IsExpectedMissingNodeDiagnostic(string details)
        {
            return details.Contains("E155010", StringComparison.OrdinalIgnoreCase) ||
                details.Contains("E200009", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryValidatePathOperationPath(string root, string target, bool allowMissingFinal,
            CancellationToken cancellationToken, out LocalPathState finalState, out string error)
        {
            finalState = LocalPathState.Error;
            error = "The selected path could not be safely resolved inside this working copy.";
            if (!TryMakeRelativePath(root, target, out var relative) || relative == ".")
                return false;
            var components = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var index = 0; index < components.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var component = components[index];
                current = Path.Combine(current, component);
                var state = GetLocalPathState(current, out var attributes, out var stateError);
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
                if ((attributes & FileAttributes.Device) != 0)
                {
                    error = "The selected path is a device or special file.";
                    return false;
                }
                if (index < components.Length - 1 && state != LocalPathState.Directory)
                {
                    error = state == LocalPathState.Missing
                        ? "Every parent directory must already exist."
                        : "A parent component of the selected path is not a directory.";
                    return false;
                }
                if (index < components.Length - 1 && component.Equals(".svn", StringComparison.OrdinalIgnoreCase))
                {
                    error = "The selected path crosses a Subversion administrative directory.";
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
                finalState = state;
                if (state == LocalPathState.Missing && index < components.Length - 1)
                    return false;
            }

            if (finalState == LocalPathState.Missing && !allowMissingFinal)
            {
                error = "The source no longer exists locally. Refresh status before trying again.";
                return false;
            }
            if (!TryResolveSafeLocalPath(root, target, out _, out var isSymbolicLink) || isSymbolicLink)
            {
                error = "The selected path resolves outside this working copy or is a symbolic link.";
                return false;
            }
            error = string.Empty;
            return true;
        }

        private static bool TryInspectPathOperationTree(string root, string source, CancellationToken cancellationToken,
            out HashSet<string> paths, out string error)
        {
            paths = new HashSet<string>(PathComparer);
            error = "The source directory could not be inspected safely.";
            var stack = new Stack<string>();
            stack.Push(source);
            if (!TryMakeRelativePath(root, source, out var sourceRelative))
            {
                error = "The source directory is outside this working copy.";
                return false;
            }
            paths.Add(sourceRelative);
            var visited = 0;
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
                            error = "The source directory contains too many entries to verify safely.";
                            return false;
                        }
                        if (Path.GetFileName(child).Equals(".svn", StringComparison.OrdinalIgnoreCase))
                        {
                            error = "The source directory contains a nested Subversion administrative directory.";
                            return false;
                        }
                        var state = GetLocalPathState(child, out var attributes, out var stateError);
                        if (state == LocalPathState.Error)
                        {
                            error = $"A source directory entry could not be inspected: {stateError}";
                            return false;
                        }
                        if (state == LocalPathState.SymbolicLink)
                        {
                            error = "The source directory contains a symbolic link or reparse point.";
                            return false;
                        }
                        if ((attributes & FileAttributes.Device) != 0)
                        {
                            error = "The source directory contains a device or special file.";
                            return false;
                        }
                        if (state is not (LocalPathState.File or LocalPathState.Directory) ||
                            !TryMakeRelativePath(root, child, out var relative))
                        {
                            error = "The source directory contains an unsupported or outside path.";
                            return false;
                        }
                        paths.Add(relative);
                        if (state == LocalPathState.Directory)
                            stack.Push(child);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = $"The source directory could not be inspected safely: {ex.Message}";
                return false;
            }
            error = string.Empty;
            return true;
        }

        private static bool IsSafePathOperationParent(SvnWorkingCopyEntry entry)
        {
            return entry != null && entry.Kind == "dir" && entry.Item == "normal" &&
                entry.Properties is "none" or "normal" &&
                !entry.IsCopied && !entry.IsSwitched && !entry.IsTreeConflicted && !entry.IsFileExternal &&
                string.IsNullOrEmpty(entry.MovedFrom) && string.IsNullOrEmpty(entry.MovedTo);
        }

        private static bool HasUnsafePathOperationAncestor(string path, IReadOnlyList<SvnWorkingCopyEntry> entries)
        {
            return entries.Any(entry => entry != null && !string.IsNullOrEmpty(entry.Path) &&
                (entry.Path == "." && path != "." || IsPathAncestor(entry.Path, path)) &&
                (entry.IsCopied || entry.IsSwitched || entry.IsTreeConflicted || entry.IsFileExternal ||
                 entry.Item is "added" or "deleted" or "replaced" or "missing" or "conflicted" or "external" or "incomplete" or "obstructed" ||
                 entry.Properties == "conflicted" || !string.IsNullOrEmpty(entry.MovedFrom) || !string.IsNullOrEmpty(entry.MovedTo)));
        }
    }
}
