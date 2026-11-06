using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace SourceGit.Native
{
    public static partial class Svn
    {
        private const int MaxConflictInfoBytes = 8 * 1024 * 1024;
        private const int MaxConflictPropertyBytes = 1024 * 1024;
        private const int MaxConflictDescriptionLength = 2048;

        public sealed record ConflictResult(string Status, ConflictSnapshot Conflict = null, string Details = "");

        public sealed record ConflictSnapshot(
            string Path,
            bool HasTextConflict,
            bool HasPropertyConflict,
            bool HasTreeConflict,
            string Description,
            ConflictVersion Working,
            ConflictVersion Base,
            ConflictVersion Mine,
            ConflictVersion Theirs,
            ConflictVersion PropertyReject,
            IReadOnlyList<string> AvailableAccepts,
            string Fingerprint);

        public sealed record ConflictVersion(byte[] Content, string Details = "", bool IsBinary = false);

        public static bool CanInspectConflict(SvnWorkingCopyEntry entry)
        {
            return entry != null && (entry.Item == "conflicted" || entry.Properties == "conflicted" || entry.IsTreeConflicted);
        }

        private sealed record ConflictInspection(
            ConflictResult Result,
            string WorkingCopyRoot = "",
            string Target = "",
            SvnWorkingCopyEntry Entry = null);

        private sealed record ConflictArtifact(ConflictVersion Version, string State);

        private sealed record ConflictInfo(
            XmlElement Entry,
            bool HasText,
            bool HasProperty,
            bool HasTree,
            string Description,
            string BasePath,
            string MinePath,
            string TheirsPath,
            string PropertyPath);

        /// <summary>Reads local SVN conflict metadata and bounded working-copy conflict artifacts.</summary>
        public static async Task<ConflictResult> GetConflictAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            var inspection = await InspectConflictAsync(executable, configDirectory, workingCopyPath,
                relativePath, cancellationToken).ConfigureAwait(false);
            return inspection.Result;
        }

        /// <summary>
        /// Resolves exactly one reviewed working-copy path. The fingerprint binds confirmation to
        /// fresh status, conflict metadata, current properties, and the bytes/state of its artifacts.
        /// </summary>
        public static async Task<WorkingCopyActionResult> ResolveConflictAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string relativePath,
            string accept,
            string expectedFingerprint,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(expectedFingerprint))
                return new WorkingCopyActionResult("InvalidArgument", "Reload and review the conflict before resolving it.");

            var inspected = await InspectConflictAsync(executable, configDirectory, workingCopyPath,
                relativePath, cancellationToken).ConfigureAwait(false);
            if (inspected.Result.Status != "Ready" || inspected.Result.Conflict == null)
            {
                if (inspected.Result.Status is "NotConflict" or "PathNotChanged")
                    return new WorkingCopyActionResult("WorkingCopyChanged", "The conflict changed or was resolved after it was reviewed. Reload the conflict before resolving it.");
                return new WorkingCopyActionResult(inspected.Result.Status, inspected.Result.Details);
            }

            var conflict = inspected.Result.Conflict;
            if (!FixedTimeEquals(expectedFingerprint, conflict.Fingerprint))
                return new WorkingCopyActionResult("WorkingCopyChanged", "The conflict or its working-copy content changed after it was reviewed. Reload the conflict before resolving it.");
            if (accept is not ("working" or "base" or "mine-full" or "theirs-full") ||
                !conflict.AvailableAccepts.Contains(accept, StringComparer.Ordinal))
                return new WorkingCopyActionResult("NotEligible", "The selected conflict resolution source is unavailable for this conflict. Reload the conflict and choose an available source.");
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new WorkingCopyActionResult("InvalidConfigDirectory", "The Subversion configuration directory is invalid.");

            var arguments = new List<string>
            {
                "resolve", "--accept", accept, "--depth", "empty", "--non-interactive", "--no-auth-cache",
            };
            AppendConfigArgument(arguments, configDirectory, effectiveConfigDirectory);
            arguments.Add("--");
            arguments.Add(WithPegEscape(inspected.Target));

            try
            {
                var command = await RunFileCommandAsync(executable, arguments, credentials: null,
                    MaxOperationOutputBytes, cancellationToken, s_operationTimeout, killOnOutputLimit: false).ConfigureAwait(false);
                var mutation = FormatMutationResult(command, credentials: null, action: "conflict resolution");
                if (mutation.Status != "Ready")
                    return mutation;

                var refreshed = await GetStatusCoreAsync(executable, configDirectory, workingCopyPath,
                    cancellationToken, includeExternals: true).ConfigureAwait(false);
                if (refreshed.Result.Status != "Ready" || !PathEquals(refreshed.WorkingCopyRoot, inspected.WorkingCopyRoot))
                    return new WorkingCopyActionResult("Failed",
                        AppendPartialOperationNotice("SVN reported success, but a fresh local status could not verify the selected path was resolved. " + refreshed.Result.Details,
                            "conflict resolution"));
                if (TryPrepareWorkingCopyRoot(refreshed.WorkingCopyRoot, out var refreshedRoot, out _) &&
                    TryResolveSelectedPath(refreshedRoot, conflict.Path, refreshed.Result.Entries, out _, out var refreshedEntry, out _) &&
                    CanInspectConflict(refreshedEntry))
                    return new WorkingCopyActionResult("Failed",
                        AppendPartialOperationNotice("SVN reported success, but fresh status still reports a conflict on the selected path.",
                            "conflict resolution"));

                return mutation;
            }
            catch (OperationCanceledException)
            {
                return new WorkingCopyActionResult("Cancelled",
                    AppendPartialOperationNotice("", "conflict resolution"));
            }
        }

        private static async Task<ConflictInspection> InspectConflictAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string relativePath,
            CancellationToken cancellationToken)
        {
            var status = await GetStatusCoreAsync(executable, configDirectory, workingCopyPath,
                cancellationToken, includeExternals: true).ConfigureAwait(false);
            if (status.Result.Status != "Ready")
                return new ConflictInspection(new ConflictResult(status.Result.Status, Details: status.Result.Details));
            if (!TryPrepareWorkingCopyRoot(status.WorkingCopyRoot, out var root, out var rootError))
                return new ConflictInspection(new ConflictResult("InvalidPath", Details: rootError));
            if (!TryResolveSelectedPath(root, relativePath, status.Result.Entries,
                    out var target, out var entry, out var selectionError))
                return new ConflictInspection(new ConflictResult("PathNotChanged", Details: selectionError));

            var statusHasConflict = CanInspectConflict(entry);
            if (!statusHasConflict)
                return new ConflictInspection(new ConflictResult("NotConflict", Details: "The selected working-copy path has no current conflict."));
            if (entry.IsFileExternal || entry.Item == "external" || entry.IsSwitched ||
                (!entry.IsTreeConflicted && (entry.IsCopied || !string.IsNullOrEmpty(entry.MovedFrom) || !string.IsNullOrEmpty(entry.MovedTo))))
                return new ConflictInspection(new ConflictResult("NotEligible", Details: "The selected conflict is on an external, switched, copied, or moved boundary."));
            if (HasUnsafeOperationAncestor(entry, status.Result.Entries))
                return new ConflictInspection(new ConflictResult("NotEligible", Details: "The selected path is beneath a switched, external, copied, moved, missing, replaced, or conflicted working-copy boundary."));
            if (!TryValidateConflictPath(root, target, entry, cancellationToken, out var safePathError))
                return new ConflictInspection(new ConflictResult("UnsafePath", Details: safePathError));
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new ConflictInspection(new ConflictResult("InvalidConfigDirectory", Details: "The Subversion configuration directory is invalid."));

            var infoArguments = new List<string> { "info", "--xml", "--depth", "empty", "--non-interactive" };
            AppendConfigArgument(infoArguments, configDirectory, effectiveConfigDirectory);
            infoArguments.Add("--no-auth-cache");
            infoArguments.Add("--");
            infoArguments.Add(WithPegEscape(target));
            var infoResult = await RunFileCommandAsync(executable, infoArguments, credentials: null,
                MaxConflictInfoBytes, cancellationToken).ConfigureAwait(false);
            if (infoResult.Status != "Ready")
                return new ConflictInspection(new ConflictResult(infoResult.Status, Details: infoResult.StandardError));
            if (infoResult.OutputTruncated)
                return new ConflictInspection(new ConflictResult("TooLarge", Details: "SVN conflict info XML exceeded the 8 MiB output limit."));
            if (infoResult.ExitCode != 0)
                return new ConflictInspection(new ConflictResult("Failed", Details: FormatFileCommandFailure(infoResult, credentials: null)));

            ConflictInfo info;
            XmlDocument document;
            try
            {
                document = LoadSafeXml(DecodeXml(infoResult.StandardOutput), MaxConflictInfoBytes);
                if (!TryParseConflictInfo(document, root, target, out info, out var infoError))
                    return new ConflictInspection(new ConflictResult("Failed", Details: infoError));
            }
            catch (Exception ex) when (ex is XmlException or System.Text.DecoderFallbackException or ArgumentException)
            {
                return new ConflictInspection(new ConflictResult("Failed", Details: $"SVN returned malformed conflict info XML: {ex.Message}"));
            }

            if ((!entry.IsTreeConflicted && entry.Item != "conflicted" && entry.Properties != "conflicted") ||
                (entry.IsTreeConflicted && !info.HasTree) ||
                (entry.Item == "conflicted" && !info.HasText && !info.HasProperty && !info.HasTree) ||
                (entry.Properties == "conflicted" && !info.HasProperty))
                return new ConflictInspection(new ConflictResult("Failed", Details: "SVN status and conflict info do not describe the same current conflict."));

            var targetState = GetLocalPathState(target, out _, out _);
            var treeVictimPropertiesNotApplicable = info.HasTree && !info.HasText && !info.HasProperty &&
                (targetState == LocalPathState.Missing || entry.Item == "deleted" || entry.Item == "unversioned");
            (string Status, byte[] Content, string Details) properties;
            if (treeVictimPropertiesNotApplicable)
                properties = ("Ready", null, "");
            else
            {
                properties = await ReadCurrentConflictPropertiesAsync(executable, configDirectory,
                    effectiveConfigDirectory, target, cancellationToken).ConfigureAwait(false);
                if (properties.Status != "Ready")
                    return new ConflictInspection(new ConflictResult(properties.Status, Details: properties.Details));
            }

            var working = targetState == LocalPathState.File
                ? await ReadConflictArtifactAsync(root, target, target, cancellationToken).ConfigureAwait(false)
                : UnavailableVersion(targetState == LocalPathState.Missing && info.HasTree
                    ? "The tree-conflict victim is missing; repair its structure manually before acknowledging it."
                    : "The working path is not a regular file; directory contents are not expanded.",
                    targetState == LocalPathState.Missing ? "missing-target" : targetState.ToString());
            var baseVersion = info.HasText
                ? await ReadConflictArtifactAsync(root, target, info.BasePath, cancellationToken).ConfigureAwait(false)
                : UnavailableVersion("No text-conflict BASE artifact is reported.", "not-applicable");
            var mine = info.HasText
                ? await ReadConflictArtifactAsync(root, target, info.MinePath, cancellationToken).ConfigureAwait(false)
                : UnavailableVersion("No text-conflict MINE artifact is reported.", "not-applicable");
            var theirs = info.HasText
                ? await ReadConflictArtifactAsync(root, target, info.TheirsPath, cancellationToken).ConfigureAwait(false)
                : UnavailableVersion("No text-conflict THEIRS artifact is reported.", "not-applicable");
            var propertyPath = info.HasProperty ? info.PropertyPath : null;
            var propertyReject = info.HasProperty
                ? await ReadConflictArtifactAsync(root, target, propertyPath, cancellationToken).ConfigureAwait(false)
                : UnavailableVersion("No property-reject artifact is reported.", "not-applicable");

            if (HasUnsafeArtifactState(working) || HasUnsafeArtifactState(baseVersion) ||
                HasUnsafeArtifactState(mine) || HasUnsafeArtifactState(theirs) || HasUnsafeArtifactState(propertyReject))
                return new ConflictInspection(new ConflictResult("UnsafePath",
                    Details: "SVN conflict metadata points to a symlink, special file, or artifact outside the selected working-copy boundary."));

            var accepts = new List<string>();
            var hasSafeSources = (!info.HasText ||
                    (baseVersion.Version.Content != null && mine.Version.Content != null && theirs.Version.Content != null)) &&
                (!info.HasProperty || (propertyReject.Version.Content != null && properties.Content != null));
            var hasBoundedWorkingBytes = targetState != LocalPathState.File || working.Version.Content != null;
            if (hasBoundedWorkingBytes &&
                (working.Version.Content != null || info.HasProperty || info.HasTree))
                accepts.Add("working");
            if (!info.HasTree && hasSafeSources && hasBoundedWorkingBytes)
            {
                accepts.Add("base");
                accepts.Add("mine-full");
                accepts.Add("theirs-full");
            }

            var relativePathValue = entry.Path;
            var description = info.Description;
            var fingerprint = ComputeConflictFingerprint(root, workingCopyPath, relativePathValue, entry,
                status.Result.Entries, CanonicalizeConflictXml(infoResult.StandardOutput, MaxConflictInfoBytes),
                properties.Content == null ? Array.Empty<byte>() : CanonicalizeConflictXml(properties.Content, MaxConflictPropertyBytes),
                treeVictimPropertiesNotApplicable ? "tree-victim-properties-not-applicable" : "queried-properties",
                working, baseVersion, mine, theirs, propertyReject);
            var snapshot = new ConflictSnapshot(relativePathValue, info.HasText, info.HasProperty, info.HasTree,
                description, working.Version, baseVersion.Version, mine.Version, theirs.Version,
                propertyReject.Version, Array.AsReadOnly(accepts.ToArray()), fingerprint);
            return new ConflictInspection(new ConflictResult("Ready", snapshot), root, target, entry);
        }

        private static async Task<(string Status, byte[] Content, string Details)> ReadCurrentConflictPropertiesAsync(
            string executable,
            string configDirectory,
            string effectiveConfigDirectory,
            string target,
            CancellationToken cancellationToken)
        {
            var arguments = new List<string> { "proplist", "--xml", "--verbose", "--depth", "empty", "--non-interactive" };
            AppendConfigArgument(arguments, configDirectory, effectiveConfigDirectory);
            arguments.Add("--no-auth-cache");
            arguments.Add("--");
            arguments.Add(WithPegEscape(target));
            var result = await RunFileCommandAsync(executable, arguments, credentials: null,
                MaxConflictPropertyBytes, cancellationToken).ConfigureAwait(false);
            if (result.Status != "Ready")
                return (result.Status, null, result.StandardError);
            if (result.OutputTruncated)
                return ("TooLarge", null, "The current working-copy properties exceeded the 1 MiB conflict metadata limit.");
            if (result.ExitCode != 0)
                return ("Failed", null, FormatFileCommandFailure(result, credentials: null));
            try
            {
                _ = LoadSafeXml(DecodeXml(result.StandardOutput), MaxConflictPropertyBytes);
                return ("Ready", result.StandardOutput, "");
            }
            catch (Exception ex) when (ex is XmlException or System.Text.DecoderFallbackException or ArgumentException)
            {
                return ("Failed", null, $"SVN returned malformed current working-copy property XML: {ex.Message}");
            }
        }

        private static bool TryParseConflictInfo(XmlDocument document, string root, string target,
            out ConflictInfo info, out string error)
        {
            info = null;
            error = "SVN conflict info did not contain a working-copy entry.";
            var documentRoot = document.DocumentElement;
            if (documentRoot == null || documentRoot.Name != "info")
                return false;
            var entry = documentRoot.SelectSingleNode("entry") as XmlElement;
            var wcInfo = entry?.SelectSingleNode("wc-info") as XmlElement;
            var targetPath = entry?.GetAttribute("path");
            var reportedRoot = wcInfo?.SelectSingleNode("wcroot-abspath")?.InnerText;
            if (entry == null || wcInfo == null || string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(reportedRoot))
                return false;
            try
            {
                if (!Path.GetFullPath(targetPath).Equals(Path.GetFullPath(target), PathComparison) ||
                    !Path.GetFullPath(reportedRoot).Equals(Path.GetFullPath(root), PathComparison))
                {
                    error = "SVN conflict info refers to a different path or working-copy root.";
                    return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = "SVN conflict info contains an invalid target or working-copy root path.";
                return false;
            }

            var hasText = false;
            var hasProperty = false;
            var descriptions = new List<string>();
            string basePath = null, minePath = null, theirsPath = null, propertyPath = null;
            var seenTypes = new HashSet<string>(StringComparer.Ordinal);
            var conflicts = entry.SelectNodes("conflict");
            if (conflicts != null)
            {
                if (conflicts.Count > 32)
                {
                    error = "SVN returned too many conflict metadata entries.";
                    return false;
                }
                foreach (XmlNode node in conflicts)
                {
                    if (node is not XmlElement conflict)
                        continue;
                    var type = conflict.GetAttribute("type");
                    if (type is not ("text" or "property"))
                        continue;
                    if (!seenTypes.Add(type))
                        continue;
                    if (type == "text")
                    {
                        hasText = true;
                        if (!TryReadOneConflictPath(conflict, "prev-base-file", ref basePath) ||
                            !TryReadOneConflictPath(conflict, "prev-wc-file", ref minePath) ||
                            !TryReadOneConflictPath(conflict, "cur-base-file", ref theirsPath))
                        {
                            error = "SVN returned inconsistent or unsafe text-conflict artifact paths.";
                            return false;
                        }
                    }
                    else
                    {
                        hasProperty = true;
                        if (!TryReadOneConflictPath(conflict, "prop-file", ref propertyPath))
                        {
                            error = "SVN returned an inconsistent or unsafe property-conflict artifact path.";
                            return false;
                        }
                    }

                    var operation = BoundedAttribute(conflict, "operation");
                    var versions = conflict.SelectNodes("version")?.OfType<XmlElement>()
                        .Select(DescribeConflictVersion).Where(value => !string.IsNullOrEmpty(value)).ToArray() ?? Array.Empty<string>();
                    var detail = string.IsNullOrEmpty(operation) ? "" : $" during {operation}";
                    if (versions.Length > 0)
                        detail += " (" + string.Join("; ", versions) + ")";
                    descriptions.Add($"{(type == "text" ? "Text" : "Property")} conflict{detail}");
                }
            }

            var treeNodes = entry.SelectNodes("tree-conflict");
            var hasTree = treeNodes is { Count: > 0 };
            if (treeNodes != null)
            {
                if (treeNodes.Count > 8)
                {
                    error = "SVN returned too many tree-conflict metadata entries.";
                    return false;
                }
                foreach (XmlNode node in treeNodes)
                {
                    if (node is not XmlElement tree)
                        continue;
                    var reason = BoundedAttribute(tree, "reason");
                    var action = BoundedAttribute(tree, "action");
                    var kind = BoundedAttribute(tree, "kind");
                    var operation = BoundedAttribute(tree, "operation");
                    var victim = BoundedAttribute(tree, "victim");
                    var parts = new[]
                    {
                        string.IsNullOrEmpty(kind) ? "" : "kind=" + kind,
                        string.IsNullOrEmpty(reason) ? "" : "reason=" + reason,
                        string.IsNullOrEmpty(action) ? "" : "action=" + action,
                        string.IsNullOrEmpty(operation) ? "" : "operation=" + operation,
                        string.IsNullOrEmpty(victim) ? "" : "victim=" + victim,
                    }.Where(part => !string.IsNullOrEmpty(part));
                    var versions = tree.SelectNodes("version")?.OfType<XmlElement>()
                        .Select(DescribeConflictVersion).Where(value => !string.IsNullOrEmpty(value)).ToArray() ?? Array.Empty<string>();
                    var treeDescription = "Tree conflict" + (parts.Any() ? " (" + string.Join(", ", parts) + ")" : "");
                    if (versions.Length > 0)
                        treeDescription += " (" + string.Join("; ", versions) + ")";
                    descriptions.Add(treeDescription);
                }
            }

            if (!hasText && !hasProperty && !hasTree)
            {
                error = "SVN status reports a conflict but local info contains no conflict details.";
                return false;
            }
            var description = string.Join("; ", descriptions.Distinct(StringComparer.Ordinal));
            if (description.Length > MaxConflictDescriptionLength)
                description = description[..MaxConflictDescriptionLength];
            info = new ConflictInfo(entry, hasText, hasProperty, hasTree, description,
                basePath, minePath, theirsPath, propertyPath);
            error = null;
            return true;
        }

        private static string DescribeConflictVersion(XmlElement version)
        {
            var side = BoundedAttribute(version, "side");
            var revision = BoundedAttribute(version, "revision");
            var kind = BoundedAttribute(version, "kind");
            var path = BoundedAttribute(version, "path-in-repos");
            var repository = BoundedAttribute(version, "repos-url");
            var details = new List<string>();
            if (!string.IsNullOrEmpty(side))
                details.Add(side);
            if (!string.IsNullOrEmpty(kind))
                details.Add(kind);
            if (!string.IsNullOrEmpty(revision))
                details.Add("r" + revision);
            if (!string.IsNullOrEmpty(repository) || !string.IsNullOrEmpty(path))
                details.Add("from " + repository + (string.IsNullOrEmpty(path) ? "" : "/" + path));
            return string.Join(" ", details);
        }

        private static bool TryReadOneConflictPath(XmlElement conflict, string elementName, ref string value)
        {
            var element = conflict.SelectSingleNode(elementName) as XmlElement;
            if (element == null)
                return true;
            var reported = element.InnerText;
            if (string.IsNullOrWhiteSpace(reported))
                return false;
            try
            {
                if (!Path.IsPathFullyQualified(reported))
                    return false;
                reported = Path.GetFullPath(reported);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
            if (value != null && !value.Equals(reported, PathComparison))
                return false;
            value = reported;
            return true;
        }

        private static string BoundedAttribute(XmlElement element, string name)
        {
            var value = element.GetAttribute(name)?.Trim();
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Length > 256 ? value[..255] + "…" : value;
        }

        private static bool TryValidateConflictPath(string root, string target, SvnWorkingCopyEntry entry,
            CancellationToken cancellationToken, out string error)
        {
            error = "The selected conflict path could not be safely resolved inside this working copy.";
            if (!TryMakeRelativePath(root, target, out var relative))
                return false;
            if (relative == ".")
            {
                if (entry.Properties != "conflicted" || entry.Kind != "dir" || entry.IsTreeConflicted || !TryValidateWorkingCopyRoot(root))
                {
                    error = "Only a working-copy root property conflict can be resolved at the root.";
                    return false;
                }
                return true;
            }

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
                    error = "The selected conflict path or one of its parent directories is a symbolic link or reparse point.";
                    return false;
                }
                if (state == LocalPathState.Missing)
                {
                    selectedState = LocalPathState.Missing;
                    break;
                }
                if (index < components.Length - 1 && state != LocalPathState.Directory)
                {
                    error = "A parent component of the selected conflict path is not a directory.";
                    return false;
                }
                if (state == LocalPathState.Directory)
                {
                    var nestedMarker = GetLocalPathState(Path.Combine(current, ".svn"), out _, out var markerError);
                    if (nestedMarker != LocalPathState.Missing)
                    {
                        error = nestedMarker == LocalPathState.Error
                            ? $"A nested Subversion working-copy boundary could not be inspected: {markerError}"
                            : "The selected conflict path is at or inside a nested Subversion working copy.";
                        return false;
                    }
                }
                selectedState = state;
            }

            if (!TryResolveSafeLocalPath(root, target, out _, out var isSymbolicLink) || isSymbolicLink)
            {
                error = "The selected conflict path resolves outside this working copy.";
                return false;
            }
            if (!entry.IsTreeConflicted && selectedState != LocalPathState.Missing &&
                ((entry.Kind == "file" && selectedState != LocalPathState.File) ||
                 (entry.Kind == "dir" && selectedState != LocalPathState.Directory)))
            {
                error = "The selected conflict path changed type after status was read. Reload the conflict before resolving it.";
                return false;
            }
            return true;
        }

        private static async Task<ConflictArtifact> ReadConflictArtifactAsync(string root, string target, string artifactPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(artifactPath))
                return UnavailableVersion("SVN did not report this conflict artifact.", "unreported");
            try
            {
                if (!Path.IsPathFullyQualified(artifactPath))
                    return UnavailableVersion("The conflict artifact path is not absolute.", "unsafe-path");
                artifactPath = Path.GetFullPath(artifactPath);
                var fullTarget = Path.GetFullPath(target);
                var targetIsDirectory = fullTarget.Equals(Path.GetFullPath(root), PathComparison) ||
                    GetLocalPathState(fullTarget, out _, out _) == LocalPathState.Directory;
                var allowedParent = targetIsDirectory ? fullTarget : Path.GetDirectoryName(fullTarget);
                var artifactParent = Path.GetDirectoryName(artifactPath);
                if (allowedParent == null || artifactParent == null || !artifactParent.Equals(allowedParent, PathComparison) ||
                    !IsPathWithin(root, artifactPath))
                    return UnavailableVersion("The conflict artifact is outside the selected file's parent or target directory.", "unsafe-path");
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return UnavailableVersion("The conflict artifact path could not be resolved safely.", "unsafe-path");
            }

            var state = GetLocalPathState(artifactPath, out var attributes, out var stateError);
            if (state == LocalPathState.Missing)
                return UnavailableVersion("The conflict artifact is missing.", "missing");
            if (state != LocalPathState.File || (attributes & FileAttributes.Device) != 0)
                return UnavailableVersion(state == LocalPathState.Error ? $"The conflict artifact could not be inspected: {stateError}" :
                    "The conflict artifact is not a regular file.", "not-regular-file");

            try
            {
                using var stream = OpenConflictFileNoFollow(artifactPath);
                using var content = new MemoryStream();
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    if (content.Length + read > MaxFileSnapshotBytes)
                        return UnavailableVersion($"The conflict artifact exceeds the {MaxFileSnapshotBytes / (1024 * 1024)} MiB content limit.",
                            GetLargeFileState(artifactPath));
                    content.Write(buffer, 0, read);
                }
                var bytes = content.ToArray();
                return new ConflictArtifact(new ConflictVersion(bytes, IsBinary: IsBinaryContent(bytes)), "bytes:" + Convert.ToHexString(SHA256.HashData(bytes)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return UnavailableVersion($"The conflict artifact could not be read safely: {ex.Message}", "unreadable:" + GetLargeFileState(artifactPath));
            }
        }

        private static ConflictArtifact UnavailableVersion(string details, string state)
        {
            return new ConflictArtifact(new ConflictVersion(null, details), state);
        }

        private static bool HasUnsafeArtifactState(ConflictArtifact artifact)
        {
            return artifact?.State is "unsafe-path" or "not-regular-file";
        }

        private static string GetLargeFileState(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return $"oversize:{file.Length.ToString(CultureInfo.InvariantCulture)}:{file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}";
            }
            catch
            {
                return "oversize:unknown";
            }
        }

        private static string ComputeConflictFingerprint(string root, string workingCopyPath, string relativePath,
            SvnWorkingCopyEntry entry, IReadOnlyList<SvnWorkingCopyEntry> entries, byte[] infoXml, byte[] propertyXml,
            string propertyState,
            ConflictArtifact working, ConflictArtifact baseVersion, ConflictArtifact mine, ConflictArtifact theirs,
            ConflictArtifact propertyReject)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Add(string value) => AddFingerprintBytes(hash, Encoding.UTF8.GetBytes(value ?? ""));
            void AddArtifact(string name, ConflictArtifact artifact)
            {
                Add(name);
                Add(artifact.State);
                Add(artifact.Version.IsBinary ? "binary" : "text-or-unclassified");
                if (artifact.Version.Content != null)
                    AddFingerprintBytes(hash, artifact.Version.Content);
                else
                    Add(artifact.Version.Details);
            }

            Add("sourcegit-svn-conflict-v1");
            Add(Path.GetFullPath(root));
            Add(Path.GetFullPath(workingCopyPath));
            Add(relativePath);
            Add(entry.Item);
            Add(entry.Properties);
            Add(entry.Revision?.ToString(CultureInfo.InvariantCulture) ?? "");
            Add(entry.IsCopied.ToString());
            Add(entry.IsSwitched.ToString());
            Add(entry.IsTreeConflicted.ToString());
            Add(entry.IsFileExternal.ToString());
            Add(entry.Kind);
            Add(entry.MovedFrom);
            Add(entry.MovedTo);
            foreach (var ancestor in entries.Where(candidate => candidate.Path == "." || IsPathAncestor(candidate.Path, relativePath))
                         .OrderBy(candidate => candidate.Path, StringComparer.Ordinal))
            {
                Add(ancestor.Path);
                Add(ancestor.Item);
                Add(ancestor.Properties);
                Add(ancestor.IsSwitched.ToString());
                Add(ancestor.IsTreeConflicted.ToString());
                Add(ancestor.IsFileExternal.ToString());
                Add(ancestor.IsCopied.ToString());
                Add(ancestor.MovedFrom);
                Add(ancestor.MovedTo);
            }
            AddFingerprintBytes(hash, infoXml);
            AddFingerprintBytes(hash, propertyXml);
            Add(propertyState);
            AddArtifact("working", working);
            AddArtifact("base", baseVersion);
            AddArtifact("mine", mine);
            AddArtifact("theirs", theirs);
            AddArtifact("property-reject", propertyReject);
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static byte[] CanonicalizeConflictXml(byte[] bytes, int maxCharacters)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = maxCharacters,
            };
            var document = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            using (var reader = XmlReader.Create(new MemoryStream(bytes, writable: false), settings))
                document.Load(reader);
            var builder = new StringBuilder(bytes.Length);
            if (document.DocumentElement != null)
                AppendCanonicalXmlElement(builder, document.DocumentElement);
            return new UTF8Encoding(false).GetBytes(builder.ToString());
        }

        private static void AppendCanonicalXmlElement(StringBuilder builder, XmlElement element)
        {
            AppendCanonicalToken(builder, 'E', element.Name);
            foreach (XmlAttribute attribute in element.Attributes.Cast<XmlAttribute>().OrderBy(attribute => attribute.Name, StringComparer.Ordinal))
                AppendCanonicalToken(builder, 'A', attribute.Name + "=" + attribute.Value);

            var childElements = element.ChildNodes.OfType<XmlElement>().ToArray();
            if (childElements.Length == 0)
            {
                foreach (XmlNode child in element.ChildNodes)
                {
                    if (child is XmlCharacterData text)
                        AppendCanonicalToken(builder, 'T', text.Value);
                }
            }
            else
            {
                foreach (XmlNode child in element.ChildNodes)
                {
                    if (child is XmlCharacterData text && !string.IsNullOrWhiteSpace(text.Value))
                        AppendCanonicalToken(builder, 'T', text.Value);
                }
                foreach (var child in childElements.OrderBy(child => child.Name, StringComparer.Ordinal)
                             .ThenBy(CanonicalXmlSortKey, StringComparer.Ordinal))
                    AppendCanonicalXmlElement(builder, child);
            }
            builder.Append('Z');
        }

        private static string CanonicalXmlSortKey(XmlElement element)
        {
            var builder = new StringBuilder();
            AppendCanonicalXmlElement(builder, element);
            return builder.ToString();
        }

        private static void AppendCanonicalToken(StringBuilder builder, char kind, string value)
        {
            value ??= string.Empty;
            builder.Append(kind);
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(value);
        }

        private static void AddFingerprintBytes(IncrementalHash hash, byte[] bytes)
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes?.Length ?? 0);
            hash.AppendData(length);
            if (bytes is { Length: > 0 })
                hash.AppendData(bytes);
        }

        private static bool FixedTimeEquals(string expected, string actual)
        {
            if (expected == null || actual == null)
                return false;
            try
            {
                var expectedBytes = Convert.FromHexString(expected);
                var actualBytes = Convert.FromHexString(actual);
                return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static FileStream OpenConflictFileNoFollow(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                var handle = CreateFileNoFollow(path);
                if (handle.IsInvalid)
                {
                    var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new IOException($"The conflict artifact could not be opened safely (error {error}).");
                }
                if (!GetFileInformationByHandle(handle, out var information) ||
                    (information.FileAttributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                {
                    handle.Dispose();
                    throw new IOException("The conflict artifact is a reparse point or directory.");
                }
                return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
            }
            return OpenBoundedLocalFileNoHang(path);
        }

        private const uint GenericRead = 0x80000000;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint ShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagSequentialScan = 0x08000000;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Win32FileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileNoFollow(
            string filename, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            Microsoft.Win32.SafeHandles.SafeFileHandle file, out Win32FileInformation information);

        private static Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileNoFollow(string path)
        {
            return CreateFileNoFollow(path, GenericRead, ShareRead | ShareWrite | ShareDelete,
                IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint | FileFlagSequentialScan, IntPtr.Zero);
        }
    }
}
