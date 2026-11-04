using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Win32.SafeHandles;

namespace SourceGit.Native
{
    public static partial class Svn
    {
        private const int MaxStatusXmlBytes = 8 * 1024 * 1024;
        private const int MaxStatusEntries = 10000;
        private const int MaxLocalDiffLines = 12000;
        private const int MaxLocalDiffLineLength = 16 * 1024;

        public sealed record SvnWorkingCopyEntry(
            string Path,
            string Item,
            string Properties,
            long? Revision = null,
            bool IsCopied = false,
            bool IsSwitched = false,
            bool IsTreeConflicted = false,
            bool IsFileExternal = false,
            string MovedFrom = null,
            string MovedTo = null,
            string Changelist = null,
            string Kind = "unknown");

        public record StatusResult(string Status, IReadOnlyList<SvnWorkingCopyEntry> Entries = null, string Details = "");

        public record LocalDiffResult(
            string Status,
            byte[] Patch = null,
            byte[] UnversionedContent = null,
            string PropertyDiff = "",
            bool IsBinary = false,
            bool IsDirectory = false,
            bool IsSpecial = false,
            bool IsMissing = false,
            string BeforeLabel = "",
            string AfterLabel = "",
            string Details = "",
            byte[] MissingBaseContent = null);

        private sealed record ResolvedStatus(StatusResult Result, string WorkingCopyRoot);

        /// <summary>
        /// Reads only local working-copy status. No update, remote status, or auth retry is used.
        /// Returned paths are relative to the working-copy root.
        /// </summary>
        public static async Task<StatusResult> GetStatusAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            CancellationToken cancellationToken = default)
        {
            var resolved = await GetStatusCoreAsync(executable, configDirectory, workingCopyPath, cancellationToken).ConfigureAwait(false);
            return resolved.Result;
        }

        private static async Task<ResolvedStatus> GetStatusCoreAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            CancellationToken cancellationToken)
        {
            var workingCopy = await GetWorkingCopyAsync(executable, configDirectory, workingCopyPath, cancellationToken).ConfigureAwait(false);
            if (workingCopy.Status != "Ready")
                return new ResolvedStatus(new StatusResult(workingCopy.Status, Details: workingCopy.Details), "");
            if (!TryPrepareWorkingCopyRoot(workingCopy.WorkingCopyRoot, out var root, out var rootError))
                return new ResolvedStatus(new StatusResult("InvalidPath", Details: rootError), "");
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new ResolvedStatus(new StatusResult("InvalidConfigDirectory", Details: "The Subversion configuration directory is invalid."), "");

            var arguments = new List<string> { "status", "--xml", "--ignore-externals", "--non-interactive" };
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                arguments.Add("--config-dir");
                arguments.Add(effectiveConfigDirectory);
            }
            arguments.Add("--no-auth-cache");
            arguments.Add("--");
            arguments.Add(WithPegEscape(root));

            var result = await RunFileCommandAsync(executable, arguments, credentials: null, MaxStatusXmlBytes, cancellationToken).ConfigureAwait(false);
            if (result.Status != "Ready")
                return new ResolvedStatus(new StatusResult(result.Status, Details: result.StandardError), root);
            if (result.OutputTruncated)
                return new ResolvedStatus(new StatusResult("TooLarge", Details: "SVN status XML exceeded the 8 MiB output limit."), root);
            if (result.ExitCode != 0)
                return new ResolvedStatus(new StatusResult("Failed", Details: FormatFileCommandFailure(result, credentials: null)), root);

            try
            {
                var xml = DecodeXml(result.StandardOutput);
                var document = LoadSafeXml(xml, MaxStatusXmlBytes);
                var status = document.DocumentElement;
                if (status == null || status.Name != "status")
                    return new ResolvedStatus(new StatusResult("Failed", Details: "SVN returned incomplete working-copy status XML."), root);

                var entries = new List<SvnWorkingCopyEntry>();
                var nodes = status.SelectNodes("target/entry | target/changelist/entry | changelist/entry");
                if (nodes == null || nodes.Count > MaxStatusEntries)
                    return new ResolvedStatus(new StatusResult("Failed", Details: "SVN status exceeded the 10,000 entry limit."), root);

                foreach (XmlNode node in nodes)
                {
                    if (node is not XmlElement entry)
                        return new ResolvedStatus(new StatusResult("Failed", Details: "SVN returned an invalid working-copy status entry."), root);
                    var wcStatus = entry.SelectSingleNode("wc-status") as XmlElement;
                    if (wcStatus == null)
                        return new ResolvedStatus(new StatusResult("Failed", Details: "SVN returned a status entry without a working-copy status."), root);
                    if (!TryReadOptionalBool(wcStatus, "file-external", out var isExternal))
                        return new ResolvedStatus(new StatusResult("Failed", Details: "SVN returned an invalid file-external status flag."), root);
                    if (isExternal || wcStatus.GetAttribute("item") == "external")
                        continue;
                    if (!TryGetWorkingCopyStatus(root, entry, out var parsed, out var parseError))
                        return new ResolvedStatus(new StatusResult("Failed", Details: parseError), root);

                    if (parsed.Item == "normal" && parsed.Properties is "none" or "normal" &&
                        !parsed.IsCopied && !parsed.IsSwitched && !parsed.IsTreeConflicted &&
                        string.IsNullOrEmpty(parsed.MovedFrom) && string.IsNullOrEmpty(parsed.MovedTo))
                        continue;

                    entries.Add(parsed);
                }

                return new ResolvedStatus(new StatusResult("Ready", Array.AsReadOnly(entries.ToArray())), root);
            }
            catch (Exception ex) when (ex is XmlException or DecoderFallbackException or ArgumentException or IOException)
            {
                return new ResolvedStatus(new StatusResult("Failed", Details: $"SVN returned malformed working-copy status XML: {ex.Message}"), root);
            }
        }

        /// <summary>
        /// Loads a bounded local diff for a path that is still present in current working-copy status.
        /// Local bytes are read only for unversioned regular files and symlink targets are never read.
        /// </summary>
        public static async Task<LocalDiffResult> GetLocalDiffAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string relativePath,
            CancellationToken cancellationToken = default,
            bool propertiesOnly = false,
            int contextLines = 3,
            bool ignoreWhitespace = false,
            bool ignoreCRAtEOL = false)
        {
            var resolvedStatus = await GetStatusCoreAsync(executable, configDirectory, workingCopyPath, cancellationToken).ConfigureAwait(false);
            var status = resolvedStatus.Result;
            if (status.Status != "Ready")
                return new LocalDiffResult(status.Status, Details: status.Details);
            if (string.IsNullOrEmpty(resolvedStatus.WorkingCopyRoot))
                return new LocalDiffResult("InvalidPath", Details: "SVN returned an invalid working-copy root path.");
            if (!TryPrepareWorkingCopyRoot(resolvedStatus.WorkingCopyRoot, out var root, out var rootError))
                return new LocalDiffResult("InvalidPath", Details: rootError);
            if (!TryResolveSelectedPath(root, relativePath, status.Entries, out var target, out var entry, out var selectionError))
                return new LocalDiffResult("PathNotChanged", Details: selectionError);

            if (!TryResolveSafeLocalPath(root, target, out var physicalPath, out var isSymbolicLink))
                return new LocalDiffResult("PathOutsideWorkingCopy",
                    Details: "The selected path resolves outside this working copy.");

            var existence = GetLocalPathState(physicalPath, out var attributes, out var stateError);
            if (existence == LocalPathState.Error)
                return new LocalDiffResult("Failed", Details: stateError);

            var isDirectory = entry.Kind == "dir" || existence == LocalPathState.Directory;

            var propertyDiff = "";
            if (entry.Properties != "none" && entry.Item != "unversioned")
            {
                var propertyResult = await RunDiffCommandAsync(executable, configDirectory, target,
                    propertiesOnly: true, contextLines, ignoreWhitespace, ignoreCRAtEOL, cancellationToken).ConfigureAwait(false);
                if (propertyResult.Status != "Ready")
                    return new LocalDiffResult(propertyResult.Status, IsDirectory: isDirectory, Details: propertyResult.Details);
                if (propertyResult.OutputTruncated)
                    return new LocalDiffResult("TooLarge", IsDirectory: isDirectory,
                        Details: "The property diff exceeded the 8 MiB output limit.");
                if (propertyResult.ExitCode != 0)
                    return new LocalDiffResult("Failed", IsDirectory: isDirectory, Details: propertyResult.Details);
                if (!TryDecodeDiff(propertyResult.StandardOutput, out propertyDiff, out _, out var propertyTooLarge, out var propertyError))
                    return new LocalDiffResult(propertyTooLarge ? "TooLarge" : "Failed", IsDirectory: isDirectory, Details: propertyError);
            }

            if (propertiesOnly)
            {
                if (entry.Item == "missing" || (entry.Item != "deleted" && existence == LocalPathState.Missing))
                    return await ReadMissingBaseAsync(executable, configDirectory, target, entry, propertyDiff,
                        isDirectory, cancellationToken).ConfigureAwait(false);
                return new LocalDiffResult("Ready", PropertyDiff: propertyDiff, IsDirectory: isDirectory,
                    BeforeLabel: entry.Item == "added" || entry.Item == "unversioned" ? "absent" : "BASE",
                    AfterLabel: entry.Item == "deleted" ? "absent" : "WORKING");
            }

            if (entry.Item is "obstructed" or "incomplete")
                return new LocalDiffResult(entry.Item == "obstructed" ? "Obstructed" : "Incomplete", PropertyDiff: propertyDiff,
                    IsDirectory: isDirectory, Details: $"SVN reports this path as {entry.Item}; local text diff is unavailable.");

            if (entry.Item == "missing" || (entry.Item != "deleted" && existence == LocalPathState.Missing))
                return await ReadMissingBaseAsync(executable, configDirectory, target, entry, propertyDiff,
                    isDirectory, cancellationToken).ConfigureAwait(false);

            if (isSymbolicLink || existence == LocalPathState.SymbolicLink)
                return new LocalDiffResult("Special", PropertyDiff: propertyDiff, IsSpecial: true, IsDirectory: isDirectory,
                    BeforeLabel: entry.Item == "added" ? "absent" : "BASE", AfterLabel: "WORKING",
                    Details: "The selected path is a symbolic link. Its target was not read.");

            if (entry.Item == "unversioned")
            {
                if (isDirectory || existence == LocalPathState.Directory)
                    return new LocalDiffResult("Directory", PropertyDiff: propertyDiff, IsDirectory: true,
                        Details: "This unversioned directory is not expanded into a recursive diff.");
                if (existence != LocalPathState.File)
                    return new LocalDiffResult("Missing", PropertyDiff: propertyDiff, IsMissing: true,
                        BeforeLabel: "absent", AfterLabel: "absent", Details: "The unversioned path is no longer present.");
                if ((attributes & FileAttributes.Device) != 0)
                    return new LocalDiffResult("Unsupported", IsSpecial: true,
                        Details: "The unversioned path is not a regular file and was not opened.");

                var content = await ReadBoundedLocalFileAsync(physicalPath, cancellationToken).ConfigureAwait(false);
                if (content.Status != "Ready")
                    return new LocalDiffResult(content.Status, Details: content.Details);
                return new LocalDiffResult("Ready", UnversionedContent: content.Content,
                    IsBinary: IsBinaryContent(content.Content), BeforeLabel: "absent", AfterLabel: "WORKING");
            }

            if (entry.Item is not "deleted" && existence == LocalPathState.File)
            {
                var special = await HasSvnSpecialPropertyAsync(executable, configDirectory, target, cancellationToken).ConfigureAwait(false);
                if (special.Status != "Ready")
                    return new LocalDiffResult(special.Status, PropertyDiff: propertyDiff, IsDirectory: isDirectory, Details: special.Details);
                if (special.HasSpecial)
                    return new LocalDiffResult("Special", PropertyDiff: propertyDiff, IsSpecial: true,
                        BeforeLabel: entry.Item == "added" ? "absent" : "BASE", AfterLabel: "WORKING",
                        Details: "This path has the svn:special property and is shown as a special file.");
            }

            if (isDirectory || existence == LocalPathState.Directory)
                return new LocalDiffResult("Directory", PropertyDiff: propertyDiff, IsDirectory: true,
                    Details: "This directory change is shown without recursive expansion.");

            var localDiff = await RunDiffCommandAsync(executable, configDirectory, target,
                propertiesOnly: false, contextLines, ignoreWhitespace, ignoreCRAtEOL, cancellationToken).ConfigureAwait(false);
            if (localDiff.Status != "Ready")
                return new LocalDiffResult(localDiff.Status, PropertyDiff: propertyDiff, IsDirectory: isDirectory, Details: localDiff.Details);
            if (localDiff.OutputTruncated)
                return new LocalDiffResult("TooLarge", PropertyDiff: propertyDiff, IsDirectory: isDirectory,
                    Details: "The local diff exceeded the 8 MiB output limit.");
            if (localDiff.ExitCode != 0)
                return new LocalDiffResult("Failed", PropertyDiff: propertyDiff, IsDirectory: isDirectory, Details: localDiff.Details);
            if (!TryDecodeDiff(localDiff.StandardOutput, out var patch, out var isBinaryPatch, out var patchTooLarge, out var patchError))
                return new LocalDiffResult(patchTooLarge ? "TooLarge" : "Failed", PropertyDiff: propertyDiff, IsDirectory: isDirectory, Details: patchError);

            var binary = isBinaryPatch || IsBinaryDiff(patch);
            var conflictDetails = entry.Item == "conflicted" || entry.IsTreeConflicted
                ? "This path has a conflict. The current working text is shown against BASE; no resolution action is performed."
                : "";
            return new LocalDiffResult("Ready", Patch: binary ? null : localDiff.StandardOutput,
                PropertyDiff: propertyDiff, IsBinary: binary, IsDirectory: isDirectory,
                BeforeLabel: entry.Item == "added" ? "absent" : "BASE",
                AfterLabel: entry.Item == "deleted" ? "absent" : "WORKING", Details: conflictDetails);
        }

        private sealed record ParsedDiffResult(string Status, int ExitCode, byte[] StandardOutput,
            bool OutputTruncated, string Details = "");

        private static async Task<ParsedDiffResult> RunDiffCommandAsync(
            string executable,
            string configDirectory,
            string targetPath,
            bool propertiesOnly,
            int contextLines,
            bool ignoreWhitespace,
            bool ignoreCRAtEOL,
            CancellationToken cancellationToken)
        {
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new ParsedDiffResult("InvalidConfigDirectory", -1, Array.Empty<byte>(), false,
                    "The Subversion configuration directory is invalid.");

            var arguments = new List<string> { "diff", "--internal-diff" };
            if (propertiesOnly)
                arguments.Add("--properties-only");
            else
                arguments.Add("--ignore-properties");
            arguments.Add("--depth");
            arguments.Add("empty");
            if (!propertiesOnly)
                arguments.Add("--show-copies-as-adds");
            if (!propertiesOnly)
            {
                var extensions = new List<string> { "-U", Math.Clamp(contextLines, 0, 50000).ToString(CultureInfo.InvariantCulture) };
                if (ignoreWhitespace)
                    extensions.Add("-b");
                if (ignoreCRAtEOL)
                    extensions.Add("--ignore-eol-style");
                arguments.Add("--extensions");
                arguments.Add(string.Join(" ", extensions));
            }
            arguments.Add("--non-interactive");
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                arguments.Add("--config-dir");
                arguments.Add(effectiveConfigDirectory);
            }
            arguments.Add("--no-auth-cache");
            arguments.Add("--");
            // The local `svn diff` target parser treats a trailing @ as part of the filename.
            // Paths are selected from the current WC status and need no peg revision here.
            arguments.Add(targetPath);

            var result = await RunFileCommandAsync(executable, arguments, credentials: null, MaxFileSnapshotBytes, cancellationToken).ConfigureAwait(false);
            if (result.Status != "Ready")
                return new ParsedDiffResult(result.Status, -1, Array.Empty<byte>(), false, result.StandardError);
            return new ParsedDiffResult("Ready", result.ExitCode, result.StandardOutput, result.OutputTruncated,
                result.ExitCode == 0 ? "" : FormatFileCommandFailure(result, credentials: null));
        }

        private static bool TryGetWorkingCopyStatus(string workingCopyRoot, XmlElement entry,
            out SvnWorkingCopyEntry parsed, out string error)
        {
            parsed = null;
            error = null;
            var pathValue = entry.GetAttribute("path");
            var wcStatus = entry.SelectSingleNode("wc-status") as XmlElement;
            if (string.IsNullOrEmpty(pathValue) || wcStatus == null ||
                !TryMakeRelativePath(workingCopyRoot, pathValue, out var relativePath))
            {
                error = "SVN returned a status path outside the selected working copy or without a working-copy status.";
                return false;
            }

            if (!TryReadOptionalBool(wcStatus, "copied", out var copied) ||
                !TryReadOptionalBool(wcStatus, "switched", out var switched) ||
                !TryReadOptionalBool(wcStatus, "tree-conflicted", out var treeConflicted) ||
                !TryReadOptionalBool(wcStatus, "file-external", out var fileExternal))
            {
                error = "SVN returned an invalid boolean working-copy status flag.";
                return false;
            }

            long? revision = null;
            var revisionText = wcStatus.GetAttribute("revision");
            if (!string.IsNullOrEmpty(revisionText))
            {
                if (!long.TryParse(revisionText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedRevision) || parsedRevision < -1)
                {
                    error = "SVN returned an invalid working-copy revision.";
                    return false;
                }
                if (parsedRevision >= 0)
                    revision = parsedRevision;
            }

            string NormalizeMove(string attribute)
            {
                var value = wcStatus.GetAttribute(attribute);
                if (string.IsNullOrEmpty(value))
                    return null;
                return TryMakeRelativePath(workingCopyRoot, value, out var movedRelative) ? movedRelative : value;
            }

            var kind = entry.GetAttribute("kind");
            if (kind is not ("file" or "dir"))
            {
                var selectedPath = Path.GetFullPath(relativePath, workingCopyRoot);
                if (TryResolveSafeLocalPath(workingCopyRoot, selectedPath, out var localPath, out _))
                {
                    var localState = GetLocalPathState(localPath, out _, out _);
                    kind = localState switch
                    {
                        LocalPathState.Directory => "dir",
                        LocalPathState.File or LocalPathState.SymbolicLink => "file",
                        _ => "unknown",
                    };
                }
            }

            var changelist = entry.SelectSingleNode("changelist")?.Attributes?["name"]?.Value;
            if (string.IsNullOrEmpty(changelist) && entry.ParentNode is XmlElement parent && parent.Name == "changelist")
                changelist = parent.GetAttribute("name");

            parsed = new SvnWorkingCopyEntry(
                relativePath,
                wcStatus.GetAttribute("item"),
                string.IsNullOrEmpty(wcStatus.GetAttribute("props")) ? "none" : wcStatus.GetAttribute("props"),
                revision,
                copied,
                switched,
                treeConflicted,
                fileExternal,
                NormalizeMove("moved-from"),
                NormalizeMove("moved-to"),
                changelist,
                kind is "file" or "dir" ? kind : "unknown");
            if (string.IsNullOrEmpty(parsed.Item))
            {
                error = "SVN returned a status entry without an item state.";
                parsed = null;
                return false;
            }

            return true;
        }

        private static bool TryReadOptionalBool(XmlElement element, string attribute, out bool value)
        {
            var text = element.GetAttribute(attribute);
            if (string.IsNullOrEmpty(text))
            {
                value = false;
                return true;
            }
            return bool.TryParse(text, out value);
        }

        private static string WithPegEscape(string path) => path.Contains('@') ? path + "@" : path;

        private static bool TryMakeRelativePath(string root, string path, out string relative)
        {
            relative = null;
            try
            {
                var absolute = Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, root);
                relative = Path.GetRelativePath(root, absolute);
                if (relative == ".")
                    return true;
                return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                    !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) &&
                    !Path.IsPathRooted(relative);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }

        private static bool TryPrepareWorkingCopyRoot(string path, out string root, out string error)
        {
            root = "";
            error = "SVN returned an invalid working-copy root path.";
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || HasUnsafeLineBreak(path))
                return false;
            try
            {
                root = Path.GetFullPath(path);
                if (!Directory.Exists(root))
                    return false;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = "The working-copy root path could not be resolved.";
                return false;
            }
        }

        private static bool TryResolveSelectedPath(string root, string relativePath,
            IReadOnlyList<SvnWorkingCopyEntry> entries, out string target, out SvnWorkingCopyEntry selected, out string error)
        {
            target = "";
            selected = null;
            error = "The selected path is not in current working-copy status.";
            if (string.IsNullOrWhiteSpace(relativePath) || HasUnsafeLineBreak(relativePath) || Path.IsPathRooted(relativePath))
            {
                error = "The selected working-copy path must be a relative path.";
                return false;
            }
            try
            {
                target = Path.GetFullPath(relativePath, root);
                if (!TryMakeRelativePath(root, target, out var normalizedRelative) ||
                    !normalizedRelative.Equals(relativePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    error = "The selected path is not a canonical working-copy-relative path.";
                    return false;
                }

                selected = entries?.FirstOrDefault(entry =>
                    entry.Path.Equals(normalizedRelative, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                if (selected == null)
                    return false;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = "The selected working-copy path could not be resolved.";
                return false;
            }
        }

        private enum LocalPathState
        {
            Missing,
            File,
            Directory,
            SymbolicLink,
            Error,
        }

        private static LocalPathState GetLocalPathState(string path, out FileAttributes attributes, out string error)
        {
            attributes = 0;
            error = "";
            try
            {
                attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return LocalPathState.SymbolicLink;
                return (attributes & FileAttributes.Directory) != 0 ? LocalPathState.Directory : LocalPathState.File;
            }
            catch (FileNotFoundException)
            {
                return LocalPathState.Missing;
            }
            catch (DirectoryNotFoundException)
            {
                return LocalPathState.Missing;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = ex.Message;
                return LocalPathState.Error;
            }
        }

        private static bool TryResolveSafeLocalPath(string workingCopyRoot, string target,
            out string physicalPath, out bool isSymbolicLink)
        {
            physicalPath = "";
            isSymbolicLink = false;
            try
            {
                var canonicalRoot = ResolveExistingPath(Path.GetFullPath(workingCopyRoot), out var rootSafe);
                if (!rootSafe)
                    return false;
                var relative = Path.GetRelativePath(workingCopyRoot, target);
                if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    Path.IsPathRooted(relative))
                    return false;

                var current = canonicalRoot;
                if (relative == ".")
                {
                    physicalPath = current;
                    return true;
                }

                var components = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < components.Length; i++)
                {
                    var candidate = Path.Combine(current, components[i]);
                    var state = GetLocalPathState(candidate, out var attributes, out _);
                    if (state == LocalPathState.Error)
                        return false;
                    if (state == LocalPathState.SymbolicLink)
                    {
                        if (i == components.Length - 1)
                        {
                            isSymbolicLink = true;
                            physicalPath = candidate;
                            return IsPathWithin(canonicalRoot, current);
                        }

                        var resolved = ResolveLink(candidate, attributes);
                        if (resolved == null || !IsPathWithin(canonicalRoot, resolved))
                            return false;
                        current = resolved;
                    }
                    else
                    {
                        current = candidate;
                        if (!IsPathWithin(canonicalRoot, current))
                            return false;
                    }
                }

                physicalPath = current;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }

        private static string ResolveExistingPath(string path, out bool safe)
        {
            safe = false;
            var full = Path.GetFullPath(path);
            var current = Path.GetPathRoot(full);
            var rest = full[current.Length..];
            var components = rest.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var component in components)
            {
                var candidate = Path.Combine(current, component);
                var state = GetLocalPathState(candidate, out var attributes, out _);
                if (state is LocalPathState.Missing or LocalPathState.Error)
                    return full;
                if (state == LocalPathState.SymbolicLink)
                {
                    var resolved = ResolveLink(candidate, attributes);
                    if (resolved == null)
                        return full;
                    current = resolved;
                }
                else
                    current = candidate;
            }
            safe = true;
            return current;
        }

        private static string ResolveLink(string path, FileAttributes attributes)
        {
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }

        private static bool IsPathWithin(string root, string path)
        {
            var relative = Path.GetRelativePath(root, path);
            return relative == "." || (relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative));
        }

        private sealed record LocalContentResult(string Status, byte[] Content = null, string Details = "");

        private sealed record LocalPropertyResult(string Status, bool HasSpecial = false, string Details = "");

        private static async Task<LocalDiffResult> ReadMissingBaseAsync(
            string executable,
            string configDirectory,
            string target,
            SvnWorkingCopyEntry entry,
            string propertyDiff,
            bool isDirectory,
            CancellationToken cancellationToken)
        {
            var kind = entry.Kind;
            if (kind == "unknown")
            {
                var kindResult = await GetLocalPathKindAsync(executable, configDirectory, target, cancellationToken).ConfigureAwait(false);
                if (kindResult.Status == "Ready")
                    kind = kindResult.Kind;
            }
            if (kind == "dir" || isDirectory)
                return new LocalDiffResult("Directory", PropertyDiff: propertyDiff, IsDirectory: true, IsMissing: true,
                    BeforeLabel: "BASE", AfterLabel: "absent",
                    Details: "The directory is missing locally; recursive BASE content is not loaded.");
            if (entry.Item == "added" || entry.Item == "unversioned" || !entry.Revision.HasValue)
                return new LocalDiffResult("Missing", PropertyDiff: propertyDiff, IsMissing: true,
                    BeforeLabel: "absent", AfterLabel: "absent",
                    Details: "The path is missing locally and has no BASE file content.");

            var metadata = await GetLocalBaseMetadataAsync(executable, configDirectory, target, cancellationToken).ConfigureAwait(false);
            if (metadata.Status != "Ready")
                return new LocalDiffResult(metadata.Status, PropertyDiff: propertyDiff, IsMissing: true,
                    BeforeLabel: "BASE", AfterLabel: "absent", Details: metadata.Details);
            if (metadata.Metadata.IsSpecial)
                return new LocalDiffResult("Special", PropertyDiff: propertyDiff, IsSpecial: true, IsMissing: true,
                    BeforeLabel: "BASE", AfterLabel: "absent",
                    Details: "The missing path's BASE node has the svn:special property.");

            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new LocalDiffResult("InvalidConfigDirectory", PropertyDiff: propertyDiff, IsMissing: true,
                    Details: "The Subversion configuration directory is invalid.");
            var arguments = new List<string> { "cat", "--ignore-keywords", "--revision", "BASE", "--non-interactive" };
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                arguments.Add("--config-dir");
                arguments.Add(effectiveConfigDirectory);
            }
            arguments.Add("--no-auth-cache");
            arguments.Add("--");
            arguments.Add(WithPegEscape(target));

            var baseResult = await RunFileCommandAsync(executable, arguments, credentials: null, MaxFileSnapshotBytes, cancellationToken).ConfigureAwait(false);
            if (baseResult.Status != "Ready")
                return new LocalDiffResult(baseResult.Status, PropertyDiff: propertyDiff, IsMissing: true,
                    BeforeLabel: "BASE", AfterLabel: "absent", Details: baseResult.StandardError);
            if (baseResult.OutputTruncated)
                return new LocalDiffResult("TooLarge", PropertyDiff: propertyDiff, IsMissing: true,
                    BeforeLabel: "BASE", AfterLabel: "absent", Details: "The missing path's BASE content exceeded the 8 MiB limit.");
            if (baseResult.ExitCode != 0)
                return new LocalDiffResult("Missing", PropertyDiff: propertyDiff, IsMissing: true,
                    BeforeLabel: "BASE", AfterLabel: "absent",
                    Details: "The path is missing locally; its BASE content could not be read from the local working copy. " +
                        FormatFileCommandFailure(baseResult, credentials: null));

            return new LocalDiffResult("Ready", MissingBaseContent: baseResult.StandardOutput, PropertyDiff: propertyDiff,
                IsBinary: metadata.Metadata.IsBinary || IsBinaryContent(baseResult.StandardOutput), IsMissing: true,
                BeforeLabel: "BASE", AfterLabel: "absent",
                Details: "The path is missing locally and is not scheduled for deletion.");
        }

        private sealed record LocalMetadataResult(string Status, FileMetadata Metadata = null, string Details = "");

        private static async Task<LocalMetadataResult> GetLocalBaseMetadataAsync(
            string executable, string configDirectory, string target, CancellationToken cancellationToken)
        {
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new LocalMetadataResult("InvalidConfigDirectory", Details: "The Subversion configuration directory is invalid.");
            var arguments = new List<string> { "proplist", "--xml", "--verbose", "--revision", "BASE", "--non-interactive" };
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                arguments.Add("--config-dir");
                arguments.Add(effectiveConfigDirectory);
            }
            arguments.Add("--no-auth-cache");
            arguments.Add("--");
            arguments.Add(WithPegEscape(target));

            var result = await RunFileCommandAsync(executable, arguments, credentials: null, MaxFileMetadataBytes, cancellationToken).ConfigureAwait(false);
            if (result.Status != "Ready")
                return new LocalMetadataResult(result.Status, Details: result.StandardError);
            if (result.OutputTruncated)
                return new LocalMetadataResult("TooLarge", Details: "SVN BASE properties XML exceeded the 128 KiB output limit.");
            if (result.ExitCode != 0)
                return new LocalMetadataResult("Failed", Details: FormatFileCommandFailure(result, credentials: null));
            try
            {
                var metadata = ParseFileMetadata(DecodeXml(result.StandardOutput));
                return new LocalMetadataResult("Ready", metadata);
            }
            catch (Exception ex) when (ex is XmlException or DecoderFallbackException)
            {
                return new LocalMetadataResult("Failed", Details: $"SVN returned malformed BASE properties XML: {ex.Message}");
            }
        }

        private static async Task<(string Status, string Kind)> GetLocalPathKindAsync(
            string executable, string configDirectory, string target, CancellationToken cancellationToken)
        {
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return ("InvalidConfigDirectory", "unknown");
            var arguments = new List<string> { "info", "--xml", "--depth", "empty", "--non-interactive" };
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                arguments.Add("--config-dir");
                arguments.Add(effectiveConfigDirectory);
            }
            arguments.Add("--no-auth-cache");
            arguments.Add("--");
            arguments.Add(WithPegEscape(target));

            var result = await RunFileCommandAsync(executable, arguments, credentials: null, MaxFileMetadataBytes, cancellationToken).ConfigureAwait(false);
            if (result.Status != "Ready")
                return (result.Status, "unknown");
            if (result.ExitCode != 0 || result.OutputTruncated)
                return ("Failed", "unknown");
            try
            {
                var document = LoadSafeXml(DecodeXml(result.StandardOutput), MaxFileMetadataBytes);
                var entry = document.DocumentElement?.SelectSingleNode("entry") as XmlElement;
                var kind = entry?.GetAttribute("kind");
                return kind is "file" or "dir" ? ("Ready", kind) : ("Failed", "unknown");
            }
            catch (Exception ex) when (ex is XmlException or DecoderFallbackException)
            {
                return ("Failed", "unknown");
            }
        }

        private static async Task<LocalPropertyResult> HasSvnSpecialPropertyAsync(
            string executable, string configDirectory, string target, CancellationToken cancellationToken)
        {
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new LocalPropertyResult("InvalidConfigDirectory", Details: "The Subversion configuration directory is invalid.");
            var arguments = new List<string> { "proplist", "--xml", "--verbose", "--non-interactive" };
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                arguments.Add("--config-dir");
                arguments.Add(effectiveConfigDirectory);
            }
            arguments.Add("--no-auth-cache");
            arguments.Add("--");
            arguments.Add(WithPegEscape(target));

            var result = await RunFileCommandAsync(executable, arguments, credentials: null, MaxFileMetadataBytes, cancellationToken).ConfigureAwait(false);
            if (result.Status != "Ready")
                return new LocalPropertyResult(result.Status, Details: result.StandardError);
            if (result.OutputTruncated)
                return new LocalPropertyResult("TooLarge", Details: "SVN properties XML exceeded the 128 KiB output limit.");
            if (result.ExitCode != 0)
                return new LocalPropertyResult("Failed", Details: FormatFileCommandFailure(result, credentials: null));
            try
            {
                var document = LoadSafeXml(DecodeXml(result.StandardOutput), MaxFileMetadataBytes);
                return new LocalPropertyResult("Ready", document.SelectSingleNode("//property[@name='svn:special']") != null);
            }
            catch (Exception ex) when (ex is XmlException or DecoderFallbackException)
            {
                return new LocalPropertyResult("Failed", Details: $"SVN returned malformed local properties XML: {ex.Message}");
            }
        }

        private static async Task<LocalContentResult> ReadBoundedLocalFileAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                using var stream = OpenBoundedLocalFileNoHang(path);
                using var content = new MemoryStream();
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    if (content.Length + read > MaxFileSnapshotBytes)
                        return new LocalContentResult("TooLarge", Details: "The unversioned file exceeds the 8 MiB content limit.");
                    content.Write(buffer, 0, read);
                }
                return new LocalContentResult("Ready", content.ToArray());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new LocalContentResult("Failed", Details: ex.Message);
            }
        }

        private static FileStream OpenBoundedLocalFileNoHang(string path)
        {
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

            var flags = OperatingSystem.IsMacOS()
                ? OpenReadOnly | OpenNonBlockingMacOS | OpenNoFollowMacOS | OpenCloseOnExecMacOS
                : OpenReadOnly | OpenNonBlockingLinux |
                    (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? OpenNoFollowLinuxArm64 : OpenNoFollowLinux) |
                    OpenCloseOnExecLinux;
            var descriptor = OpenFile(path, flags);
            if (descriptor < 0)
                throw new IOException($"The unversioned path could not be opened safely (errno {Marshal.GetLastWin32Error()}).");

            var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            try
            {
                if (SeekFile(descriptor, 0, SeekCurrent) < 0)
                    throw new IOException("The selected unversioned path is not a seekable file.");
                return new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private const int OpenReadOnly = 0;
        private const int OpenNonBlockingMacOS = 0x0004;
        private const int OpenNoFollowMacOS = 0x0100;
        private const int OpenCloseOnExecMacOS = 0x01000000;
        private const int OpenNonBlockingLinux = 0x00000800;
        private const int OpenNoFollowLinux = 0x00020000;
        private const int OpenNoFollowLinuxArm64 = 0x00008000;
        private const int OpenCloseOnExecLinux = 0x00080000;
        private const int SeekCurrent = 1;

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int OpenFile([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);

        [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
        private static extern long SeekFile(int descriptor, long offset, int whence);

        private static bool TryDecodeDiff(byte[] bytes, out string text, out bool isBinary, out bool tooLarge, out string error)
        {
            text = "";
            isBinary = false;
            tooLarge = false;
            error = "";
            if (bytes.Contains((byte)0))
            {
                isBinary = true;
                return true;
            }
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
                var lines = text.Split('\n');
                if (lines.Length > MaxLocalDiffLines || lines.Any(line => line.Length > MaxLocalDiffLineLength))
                {
                    tooLarge = true;
                    error = "The local diff exceeds the 12,000 line or 16,384 character per-line limit.";
                    return false;
                }
                return true;
            }
            catch (DecoderFallbackException)
            {
                isBinary = true;
                return true;
            }
        }

        private static bool IsBinaryDiff(string diff)
        {
            foreach (var line in diff.Split('\n'))
            {
                var diagnostic = line.TrimEnd('\r');
                if (diagnostic.Equals("Cannot display: file marked as a binary type.", StringComparison.OrdinalIgnoreCase) ||
                    diagnostic.StartsWith("Binary files ", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
