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
        private const int MaxPropertyValueBytes = 1024 * 1024;
        private const int MaxPropertyBytes = 4 * 1024 * 1024;
        private const int MaxPropertyXmlBytes = 8 * 1024 * 1024;
        private const int MaxIgnoreSelections = 1000;

        public sealed record SvnProperty(string Name, string Value, bool IsBinary);

        public sealed record SvnPropertiesSnapshot(
            string RelativePath,
            string Kind,
            IReadOnlyList<SvnProperty> Properties,
            string Token);

        public sealed record SvnPropertiesResult(string Status, SvnPropertiesSnapshot Snapshot = null, string Details = "");

        private sealed record PropertyCommandResult(
            string Status,
            int ExitCode,
            byte[] StandardOutput,
            bool OutputTruncated,
            string StandardError,
            bool ErrorTruncated);

        private sealed record PropertyReadResult(
            string Status,
            string Root = "",
            string Target = "",
            string RelativePath = "",
            string Kind = "",
            string Identity = "",
            IReadOnlyList<SvnProperty> Properties = null,
            string Details = "");

        private sealed record PropertyTargetState(
            string Root,
            string Target,
            string RelativePath,
            string Kind,
            string Identity,
            IReadOnlyList<SvnProperty> Properties,
            string Token);

        /// <summary>Reads local properties on exactly one versioned working-copy node.</summary>
        public static async Task<SvnPropertiesResult> GetPropertiesAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var read = await ReadPropertiesAsync(executable, configDirectory, workingCopyPath,
                    relativePath, cancellationToken).ConfigureAwait(false);
                if (read.Status != "Ready")
                    return new SvnPropertiesResult(read.Status, Details: read.Details);

                var token = CreatePropertyToken(read.Identity, read.RelativePath, read.Kind, read.Properties);
                return new SvnPropertiesResult("Ready", new SvnPropertiesSnapshot(read.RelativePath,
                    read.Kind, read.Properties, token));
            }
            catch (OperationCanceledException)
            {
                return new SvnPropertiesResult("Cancelled", Details: "The local property read was cancelled.");
            }
        }

        /// <summary>Sets one text property after re-reading and matching its complete local snapshot.</summary>
        public static async Task<WorkingCopyActionResult> SetPropertyAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            SvnPropertiesSnapshot snapshot,
            string name,
            string value,
            CancellationToken cancellationToken = default)
        {
            if (!TryValidateMutablePropertyName(name, out var nameError))
                return new WorkingCopyActionResult("InvalidArgument", nameError);
            if (value == null)
                return new WorkingCopyActionResult("InvalidArgument", "A property value is required.");

            byte[] bytes;
            try
            {
                bytes = new UTF8Encoding(false, true).GetBytes(value);
            }
            catch (EncoderFallbackException)
            {
                return new WorkingCopyActionResult("InvalidArgument", "The property value contains invalid Unicode text.");
            }
            if (bytes.Length > MaxPropertyValueBytes)
                return new WorkingCopyActionResult("TooLarge", "A property value cannot exceed 1 MiB when encoded as UTF-8.");
            if (ContainsUnsupportedPropertyControl(value))
                return new WorkingCopyActionResult("InvalidArgument", "Text properties cannot contain control characters other than tab, carriage return, or line feed.");

            try
            {
                var current = await ReadFreshPropertyStateAsync(executable, configDirectory, workingCopyPath,
                    snapshot, cancellationToken).ConfigureAwait(false);
                if (current.State == null)
                    return new WorkingCopyActionResult(current.Status, current.Details);
                return await MutatePropertyAsync(executable, configDirectory, current.State, name,
                    valueBytes: bytes, delete: false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new WorkingCopyActionResult("Cancelled", "The property update was cancelled; refresh the working-copy properties before retrying.");
            }
        }

        /// <summary>Deletes one property after re-reading and matching its complete local snapshot.</summary>
        public static async Task<WorkingCopyActionResult> DeletePropertyAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            SvnPropertiesSnapshot snapshot,
            string name,
            CancellationToken cancellationToken = default)
        {
            if (!TryValidateMutablePropertyName(name, out var nameError))
                return new WorkingCopyActionResult("InvalidArgument", nameError);

            try
            {
                var current = await ReadFreshPropertyStateAsync(executable, configDirectory, workingCopyPath,
                    snapshot, cancellationToken).ConfigureAwait(false);
                if (current.State == null)
                    return new WorkingCopyActionResult(current.Status, current.Details);
                if (!current.State.Properties.Any(property => property.Name.Equals(name, StringComparison.Ordinal)))
                    return new WorkingCopyActionResult("NotFound", "The property no longer exists. Refresh the property list before retrying.");
                return await MutatePropertyAsync(executable, configDirectory, current.State, name,
                    valueBytes: null, delete: true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new WorkingCopyActionResult("Cancelled", "The property deletion was cancelled; refresh the working-copy properties before retrying.");
            }
        }

        /// <summary>Ignores only freshly verified, immediate unversioned children of the selected directory.</summary>
        public static async Task<WorkingCopyActionResult> IgnoreAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            SvnPropertiesSnapshot snapshot,
            IReadOnlyList<string> selectedRelativePaths,
            CancellationToken cancellationToken = default)
        {
            if (selectedRelativePaths == null || selectedRelativePaths.Count == 0 || selectedRelativePaths.Count > MaxIgnoreSelections)
                return new WorkingCopyActionResult("InvalidArgument", "Select between one and 1,000 unversioned child paths to ignore.");

            try
            {
                var current = await ReadFreshPropertyStateAsync(executable, configDirectory, workingCopyPath,
                    snapshot, cancellationToken).ConfigureAwait(false);
                if (current.State == null)
                    return new WorkingCopyActionResult(current.Status, current.Details);

                var selected = await ValidateIgnoreSelectionsAsync(executable, configDirectory,
                    current.State, selectedRelativePaths, cancellationToken).ConfigureAwait(false);
                if (selected.Status != "Ready")
                    return new WorkingCopyActionResult(selected.Status, selected.Details);

                // Refresh the parent after child validation so a changed parent identity or property set
                // cannot turn a stale Ignore request into a write to another working-copy node.
                current = await ReadFreshPropertyStateAsync(executable, configDirectory, workingCopyPath,
                    snapshot, cancellationToken).ConfigureAwait(false);
                if (current.State == null)
                    return new WorkingCopyActionResult(current.Status, current.Details);

                if (!TryCreateIgnoreValue(new SvnPropertiesSnapshot(current.State.RelativePath,
                        current.State.Kind, current.State.Properties, current.State.Token), selected.Basenames,
                        out var value, out var error))
                    return new WorkingCopyActionResult("InvalidArgument", error);

                var existing = current.State.Properties.FirstOrDefault(property => property.Name == "svn:ignore");
                if (existing != null && !existing.IsBinary && existing.Value.Equals(value, StringComparison.Ordinal))
                    return new WorkingCopyActionResult("Ready", "The selected names are already ignored.");

                return await MutatePropertyAsync(executable, configDirectory, current.State, "svn:ignore",
                    new UTF8Encoding(false, true).GetBytes(value), delete: false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new WorkingCopyActionResult("Cancelled", "The Ignore request was cancelled; refresh status and properties before retrying.");
            }
        }

        /// <summary>Combines exact local child names with an existing text svn:ignore value.</summary>
        public static bool TryCreateIgnoreValue(
            SvnPropertiesSnapshot snapshot,
            IReadOnlyList<string> selectedBasenames,
            out string value,
            out string error)
        {
            value = null;
            error = "Select at least one immediate child name to ignore.";
            if (snapshot == null || selectedBasenames == null || selectedBasenames.Count == 0 ||
                selectedBasenames.Count > MaxIgnoreSelections)
                return false;

            var existing = snapshot.Properties?.FirstOrDefault(property => property != null && property.Name == "svn:ignore");
            if (existing?.IsBinary == true)
            {
                error = "The existing svn:ignore property is binary or contains unsupported control characters and cannot be edited as text.";
                return false;
            }

            var current = existing?.Value ?? string.Empty;
            var result = new StringBuilder(current);
            var names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var basename in selectedBasenames)
            {
                if (!IsSafeIgnoreBasename(basename))
                {
                    error = "Ignore accepts only immediate filenames without path separators, control characters, or line breaks.";
                    return false;
                }
                if (!names.Add(basename))
                {
                    error = "The Ignore selection contains the same child name more than once.";
                    return false;
                }

                var pattern = EscapeIgnoreGlob(basename);
                if (HasIgnoreLine(current, pattern))
                    continue;
                if (result.Length > 0 && result[^1] is not ('\r' or '\n'))
                    result.Append('\n');
                result.Append(pattern);
            }

            value = result.ToString();
            if (Encoding.UTF8.GetByteCount(value) > MaxPropertyValueBytes)
            {
                value = null;
                error = "The resulting svn:ignore value exceeds the 1 MiB property limit.";
                return false;
            }
            error = string.Empty;
            return true;
        }

        private sealed record FreshPropertyRead(string Status, PropertyTargetState State = null, string Details = "");
        private sealed record IgnoreSelectionResult(string Status, IReadOnlyList<string> Basenames = null, string Details = "");

        private static async Task<PropertyReadResult> ReadPropertiesAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string relativePath,
            CancellationToken cancellationToken)
        {
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new PropertyReadResult("InvalidConfigDirectory", Details: "The Subversion configuration directory is invalid.");

            var workingCopy = await GetWorkingCopyAsync(executable, configDirectory, workingCopyPath, cancellationToken).ConfigureAwait(false);
            if (workingCopy.Status != "Ready")
                return new PropertyReadResult(workingCopy.Status, Details: workingCopy.Details);
            if (!TryPrepareWorkingCopyRoot(workingCopy.WorkingCopyRoot, out var root, out var rootError))
                return new PropertyReadResult("InvalidPath", Details: rootError);
            if (!TryResolvePropertiesPath(root, relativePath, out var target, out var normalizedRelative, out var pathError))
                return new PropertyReadResult("InvalidPath", Details: pathError);
            if (!TryValidatePropertiesPath(root, target, out var localKind, out pathError))
                return new PropertyReadResult("UnsafePath", Details: pathError);

            var fullStatus = await GetStatusCoreAsync(executable, configDirectory, root,
                cancellationToken, includeExternals: true).ConfigureAwait(false);
            if (fullStatus.Result.Status != "Ready")
                return new PropertyReadResult(fullStatus.Result.Status, Details: fullStatus.Result.Details);
            if (!HasSafePropertyAncestors(normalizedRelative, fullStatus.Result.Entries, out var boundaryError))
                return new PropertyReadResult("NotEligible", Details: boundaryError);

            var targetStatus = await GetPathStatusAsync(executable, configDirectory, effectiveConfigDirectory,
                root, target, depthInfinity: false, cancellationToken).ConfigureAwait(false);
            if (targetStatus.Status != "Ready")
                return new PropertyReadResult(targetStatus.Status, Details: targetStatus.Details);
            var entry = targetStatus.Entries?.FirstOrDefault(item => PathEquals(item.Path, normalizedRelative));
            if (entry == null)
                return new PropertyReadResult("NotVersioned", Details: "SVN did not return status for the selected working-copy node.");
            if (!IsSafePropertyEntry(entry))
                return new PropertyReadResult("NotEligible", Details: "Properties are available only on normal or text-modified versioned nodes without schedules, conflicts, externals, or switched paths.");

            var targetInfo = await GetWorkingCopyAsync(executable, configDirectory, target, cancellationToken).ConfigureAwait(false);
            if (targetInfo.Status != "Ready")
                return new PropertyReadResult(targetInfo.Status, Details: targetInfo.Details);
            if (!PathEquals(Path.GetFullPath(targetInfo.WorkingCopyRoot), root))
                return new PropertyReadResult("NotEligible", Details: "The selected path is inside a nested Subversion working copy.");

            var pathInfo = await GetPathInfoAsync(executable, configDirectory, effectiveConfigDirectory,
                root, target, cancellationToken).ConfigureAwait(false);
            if (pathInfo.Status != "Ready")
                return new PropertyReadResult(pathInfo.Status, Details: pathInfo.Details);
            if (!pathInfo.IsVersioned || !PathEquals(Path.GetFullPath(pathInfo.WorkingCopyRoot), root) ||
                pathInfo.Kind != localKind || pathInfo.Kind is not ("file" or "dir"))
                return new PropertyReadResult("NotEligible", Details: "The selected node is scheduled, switched, missing, or changed type. Refresh the working-copy status.");

            // Keep repository URL, UUID, revision and working-copy root in the token. A switch or update
            // invalidates an old dialog even when the local property bytes happen to be unchanged.
            var identity = string.Join("\n", new[]
            {
                Path.GetFullPath(workingCopy.WorkingCopyRoot),
                workingCopy.Url,
                workingCopy.RepositoryRoot,
                workingCopy.RepositoryUuid,
                workingCopy.Revision.ToString(CultureInfo.InvariantCulture),
                Path.GetFullPath(targetInfo.WorkingCopyRoot),
                targetInfo.Url,
                targetInfo.RepositoryRoot,
                targetInfo.RepositoryUuid,
                targetInfo.Revision.ToString(CultureInfo.InvariantCulture),
                pathInfo.Kind,
            });

            var properties = await ReadLocalPropertiesAsync(executable, configDirectory,
                effectiveConfigDirectory, target, cancellationToken).ConfigureAwait(false);
            if (properties.Status != "Ready")
                return new PropertyReadResult(properties.Status, Details: properties.Details);
            return new PropertyReadResult("Ready", root, target, normalizedRelative,
                pathInfo.Kind, identity, properties.Properties);
        }

        private static async Task<FreshPropertyRead> ReadFreshPropertyStateAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            SvnPropertiesSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Token) ||
                string.IsNullOrWhiteSpace(snapshot.RelativePath) || snapshot.Kind is not ("file" or "dir"))
                return new FreshPropertyRead("InvalidArgument", Details: "A current property snapshot is required for this action.");

            var fresh = await ReadPropertiesAsync(executable, configDirectory, workingCopyPath,
                snapshot.RelativePath, cancellationToken).ConfigureAwait(false);
            if (fresh.Status != "Ready")
                return new FreshPropertyRead(fresh.Status, Details: fresh.Details);
            var token = CreatePropertyToken(fresh.Identity, fresh.RelativePath, fresh.Kind, fresh.Properties);
            if (!PathEquals(snapshot.RelativePath, fresh.RelativePath) || snapshot.Kind != fresh.Kind ||
                !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(snapshot.Token), Encoding.ASCII.GetBytes(token)))
                return new FreshPropertyRead("StaleSnapshot", Details: "The working-copy target or its properties changed. Refresh the property list before retrying.");

            return new FreshPropertyRead("Ready", new PropertyTargetState(fresh.Root, fresh.Target,
                fresh.RelativePath, fresh.Kind, fresh.Identity, fresh.Properties, token));
        }

        private static async Task<IgnoreSelectionResult> ValidateIgnoreSelectionsAsync(
            string executable,
            string configDirectory,
            PropertyTargetState parent,
            IReadOnlyList<string> selectedRelativePaths,
            CancellationToken cancellationToken)
        {
            if (parent.Kind != "dir")
                return new IgnoreSelectionResult("NotEligible", Details: "Ignore requires a directory property target.");
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new IgnoreSelectionResult("InvalidConfigDirectory", Details: "The Subversion configuration directory is invalid.");

            var basenames = new List<string>(selectedRelativePaths.Count);
            var seenPaths = new HashSet<string>(PathComparer);
            foreach (var selected in selectedRelativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryResolvePropertiesPath(parent.Root, selected, out var target, out var normalized, out var error))
                    return new IgnoreSelectionResult("InvalidArgument", Details: error);
                var selectedParent = Path.GetDirectoryName(normalized);
                if (string.IsNullOrEmpty(selectedParent))
                    selectedParent = ".";
                if (!PathEquals(selectedParent, parent.RelativePath))
                    return new IgnoreSelectionResult("NotEligible", Details: "Every ignored path must be an immediate child of the selected directory.");
                if (!seenPaths.Add(normalized))
                    return new IgnoreSelectionResult("InvalidArgument", Details: "The Ignore selection contains a duplicate path.");
                var basename = Path.GetFileName(normalized);
                if (!IsSafeIgnoreBasename(basename))
                    return new IgnoreSelectionResult("InvalidArgument", Details: "Ignore accepts only immediate filenames without path separators, control characters, or line breaks.");
                if (!TryValidatePropertiesPath(parent.Root, target, out _, out error))
                    return new IgnoreSelectionResult("UnsafePath", Details: error);

                var status = await GetPathStatusAsync(executable, configDirectory, effectiveConfigDirectory,
                    parent.Root, target, depthInfinity: false, cancellationToken).ConfigureAwait(false);
                if (status.Status != "Ready")
                    return new IgnoreSelectionResult(status.Status, Details: status.Details);
                var entry = status.Entries?.FirstOrDefault(item => PathEquals(item.Path, normalized));
                if (entry == null || entry.Item != "unversioned" || entry.Kind is not ("file" or "dir") ||
                    entry.IsCopied || entry.IsSwitched || entry.IsTreeConflicted || entry.IsFileExternal ||
                    entry.Properties is "conflicted" or "modified" ||
                    !string.IsNullOrEmpty(entry.MovedFrom) || !string.IsNullOrEmpty(entry.MovedTo))
                    return new IgnoreSelectionResult("NotEligible", Details: "Every selected path must still be a normal, unversioned file or directory. Refresh status before ignoring it.");
                basenames.Add(basename);
            }

            return new IgnoreSelectionResult("Ready", Array.AsReadOnly(basenames.ToArray()));
        }

        private static async Task<WorkingCopyActionResult> MutatePropertyAsync(
            string executable,
            string configDirectory,
            PropertyTargetState target,
            string name,
            byte[] valueBytes,
            bool delete,
            CancellationToken cancellationToken)
        {
            if (name.Equals("svn:special", StringComparison.Ordinal))
                return new WorkingCopyActionResult("NotEligible", "The svn:special property cannot be changed here because it controls symlink representation.");
            var existingProperty = target.Properties.FirstOrDefault(property => property.Name.Equals(name, StringComparison.Ordinal));
            if (existingProperty?.IsBinary == true)
                return new WorkingCopyActionResult("NotEligible", "Binary or control-character properties are read-only.");
            if (delete && !target.Properties.Any(property => property.Name.Equals(name, StringComparison.Ordinal)))
                return new WorkingCopyActionResult("NotFound", "The property no longer exists. Refresh the property list before retrying.");
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new WorkingCopyActionResult("InvalidConfigDirectory", "The Subversion configuration directory is invalid.");

            var args = new List<string> { delete ? "propdel" : "propset" };
            string valueFile = null;
            var mutationStarted = false;
            var result = new WorkingCopyActionResult("Failed", "The property action did not complete.");
            string cleanupFailure = null;
            try
            {
                if (!delete)
                {
                    valueFile = CreateProtectedPropertyFile(valueBytes);
                    args.Add("--file");
                    args.Add(valueFile);
                }
                args.Add("--depth");
                args.Add("empty");
                args.Add("--non-interactive");
                args.Add("--no-auth-cache");
                AppendConfigArgument(args, configDirectory, effectiveConfigDirectory);
                args.Add("--");
                args.Add(name);
                // A terminal peg escape makes every @ in the literal path unambiguous to SVN.
                args.Add(target.Target + "@");

                cancellationToken.ThrowIfCancellationRequested();
                mutationStarted = true;
                var command = await RunPropertyCommandAsync(executable, args, MaxOperationOutputBytes,
                    cancellationToken, s_operationTimeout).ConfigureAwait(false);
                result = FormatPropertyMutation(command, delete ? "property deletion" : "property update");
            }
            catch (OperationCanceledException)
            {
                result = new WorkingCopyActionResult("Cancelled", mutationStarted
                    ? "The property action was cancelled after SVN started; the working copy may have changed partially. Refresh properties and status before retrying."
                    : "The property action was cancelled before it started.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                var details = mutationStarted
                    ? $"The property action may have changed the working copy partially: {ex.Message}"
                    : $"The property value could not be prepared safely: {ex.Message}";
                result = new WorkingCopyActionResult("Failed", details);
            }
            finally
            {
                if (valueFile != null)
                {
                    try
                    {
                        File.Delete(valueFile);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        cleanupFailure = $"The temporary file containing the property value could not be removed: {valueFile}. {ex.Message}";
                    }
                }
            }

            if (!string.IsNullOrEmpty(cleanupFailure))
            {
                var details = string.IsNullOrWhiteSpace(result.Details) ? cleanupFailure : result.Details + Environment.NewLine + cleanupFailure;
                return new WorkingCopyActionResult(result.Status == "Ready" ? "CleanupFailed" : result.Status, details);
            }
            return result;
        }

        private static async Task<(string Status, IReadOnlyList<SvnProperty> Properties, string Details)> ReadLocalPropertiesAsync(
            string executable,
            string configDirectory,
            string effectiveConfigDirectory,
            string target,
            CancellationToken cancellationToken)
        {
            var args = new List<string> { "proplist", "--xml", "--verbose", "--depth", "empty", "--non-interactive" };
            AppendConfigArgument(args, configDirectory, effectiveConfigDirectory);
            args.Add("--no-auth-cache");
            args.Add("--");
            args.Add(target + "@");
            var command = await RunPropertyCommandAsync(executable, args, MaxPropertyXmlBytes, cancellationToken).ConfigureAwait(false);
            if (command.Status != "Ready")
                return (command.Status, null, command.StandardError);
            if (command.OutputTruncated)
                return ("TooLarge", null, "SVN local property XML exceeded the 8 MiB output limit.");
            if (command.ExitCode != 0)
                return ("Failed", null, FormatPropertyCommandFailure(command));

            try
            {
                var xml = DecodeXml(command.StandardOutput);
                var document = LoadPropertyXml(xml, MaxPropertyXmlBytes);
                var root = document.DocumentElement;
                var targets = root?.SelectNodes("target");
                if (root == null || root.Name != "properties" || targets == null || targets.Count > 1)
                    return ("Failed", null, "SVN returned incomplete or unexpected local property XML.");

                if (targets.Count == 0)
                {
                    if (root.ChildNodes.Cast<XmlNode>().Any(node => node.NodeType is not (XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)))
                        return ("Failed", null, "SVN returned unexpected data for a target without local properties.");
                    return ("Ready", Array.AsReadOnly(Array.Empty<SvnProperty>()), string.Empty);
                }
                if (targets[0] is not XmlElement targetElement || !PropertyXmlPathMatches(targetElement.GetAttribute("path"), target))
                    return ("Failed", null, "SVN returned incomplete or unexpected local property XML.");

                var properties = new List<SvnProperty>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var totalBytes = 0;
                foreach (XmlNode node in targetElement.ChildNodes)
                {
                    if (node is not XmlElement property || property.Name != "property")
                    {
                        if (node.NodeType is XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                            continue;
                        return ("Failed", null, "SVN returned an invalid local property entry.");
                    }
                    var name = property.GetAttribute("name");
                    if (!TryValidatePropertyName(name, out _) || !seen.Add(name))
                        return ("Failed", null, "SVN returned a missing, invalid, or duplicate property name.");

                    byte[] bytes;
                    var base64 = property.GetAttribute("encoding");
                    if (base64.Length == 0)
                    {
                        try
                        {
                            bytes = new UTF8Encoding(false, true).GetBytes(property.InnerText);
                        }
                        catch (EncoderFallbackException)
                        {
                            return ("Failed", null, "SVN returned a property that is not valid UTF-8 text.");
                        }
                    }
                    else if (base64.Equals("base64", StringComparison.Ordinal))
                    {
                        try
                        {
                            bytes = Convert.FromBase64String(property.InnerText);
                        }
                        catch (FormatException)
                        {
                            return ("Failed", null, "SVN returned malformed base64 property data.");
                        }
                    }
                    else
                    {
                        return ("Failed", null, "SVN returned an unsupported property encoding.");
                    }

                    if (bytes.Length > MaxPropertyValueBytes || totalBytes > MaxPropertyBytes - bytes.Length)
                        return ("TooLarge", null, "Local properties exceeded the 1 MiB per-property or 4 MiB total limit.");
                    totalBytes += bytes.Length;

                    string text = null;
                    var binary = false;
                    try
                    {
                        text = new UTF8Encoding(false, true).GetString(bytes);
                        binary = ContainsUnsupportedPropertyControl(text);
                    }
                    catch (DecoderFallbackException)
                    {
                        binary = true;
                    }
                    if (binary)
                        text = Convert.ToBase64String(bytes);
                    properties.Add(new SvnProperty(name, text ?? string.Empty, binary));
                }

                return ("Ready", Array.AsReadOnly(properties.ToArray()), string.Empty);
            }
            catch (Exception ex) when (ex is XmlException or DecoderFallbackException or ArgumentException or IOException)
            {
                return ("Failed", null, $"SVN returned malformed local property XML: {ex.Message}");
            }
        }

        private static async Task<PropertyCommandResult> RunPropertyCommandAsync(
            string executable,
            IReadOnlyList<string> arguments,
            int outputLimit,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null)
        {
            var result = await RunFileCommandAsync(executable, arguments, null, outputLimit,
                cancellationToken, timeout).ConfigureAwait(false);
            return new PropertyCommandResult(result.Status, result.ExitCode, result.StandardOutput,
                result.OutputTruncated, result.StandardError, result.ErrorTruncated);
        }

        private static XmlDocument LoadPropertyXml(string xml, int maxCharacters)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = false,
                MaxCharactersInDocument = maxCharacters,
            };
            var document = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
                document.Load(reader);
            return document;
        }

        private static string FormatPropertyCommandFailure(PropertyCommandResult result)
        {
            var compatible = new SvnProcessResult("Ready", result.ExitCode, string.Empty, false,
                result.StandardError, result.ErrorTruncated);
            return FormatHistoryFailure(compatible, credentials: null);
        }

        private static WorkingCopyActionResult FormatPropertyMutation(PropertyCommandResult result, string action)
        {
            if (result.Status != "Ready")
                return new WorkingCopyActionResult(result.Status,
                    AppendPartialOperationNotice(result.StandardError, action));
            if (result.ExitCode != 0)
                return new WorkingCopyActionResult("Failed",
                    AppendPartialOperationNotice(FormatPropertyCommandFailure(result), action));
            return new WorkingCopyActionResult("Ready", Encoding.UTF8.GetString(result.StandardOutput).Trim());
        }

        private static bool TryValidatePropertyName(string name, out string error)
        {
            error = "The property name is invalid.";
            if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || HasUnsafeLineBreak(name))
                return false;
            try
            {
                XmlConvert.VerifyName(name);
                error = string.Empty;
                return true;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        private static bool TryValidateMutablePropertyName(string name, out string error)
        {
            if (!TryValidatePropertyName(name, out error))
                return false;
            if (name.Equals("svn:special", StringComparison.Ordinal))
            {
                error = "The svn:special property cannot be changed here because it controls symlink representation.";
                return false;
            }
            if (name.StartsWith("svn:entry:", StringComparison.Ordinal) || name.StartsWith("svn:wc:", StringComparison.Ordinal))
            {
                error = "Subversion working-copy metadata properties cannot be changed here.";
                return false;
            }
            return true;
        }

        private static bool ContainsUnsupportedPropertyControl(string value)
        {
            return value.Any(character => char.IsControl(character) && character is not ('\t' or '\r' or '\n'));
        }

        private static bool IsSafePropertyEntry(SvnWorkingCopyEntry entry)
        {
            return entry != null && entry.Item is "normal" or "modified" &&
                entry.Kind is "file" or "dir" &&
                entry.Properties is "none" or "normal" or "modified" &&
                !entry.IsCopied && !entry.IsSwitched && !entry.IsTreeConflicted && !entry.IsFileExternal &&
                string.IsNullOrEmpty(entry.MovedFrom) && string.IsNullOrEmpty(entry.MovedTo);
        }

        private static bool HasSafePropertyAncestors(
            string relativePath,
            IReadOnlyList<SvnWorkingCopyEntry> entries,
            out string error)
        {
            error = "The selected path is beneath a scheduled, conflicted, switched, external, or replaced working-copy boundary.";
            if (entries == null)
                return false;
            foreach (var ancestor in entries)
            {
                if (ancestor == null || PathEquals(ancestor.Path, relativePath) ||
                    (ancestor.Path != "." && !IsPathAncestor(ancestor.Path, relativePath)))
                    continue;
                if (ancestor.IsCopied || ancestor.IsSwitched || ancestor.IsTreeConflicted || ancestor.IsFileExternal ||
                    ancestor.Item is "added" or "deleted" or "replaced" or "missing" or "conflicted" or "external" or "incomplete" or "obstructed" ||
                    ancestor.Properties == "conflicted" || !string.IsNullOrEmpty(ancestor.MovedFrom) || !string.IsNullOrEmpty(ancestor.MovedTo))
                    return false;
            }
            error = string.Empty;
            return true;
        }

        private static bool TryResolvePropertiesPath(
            string root,
            string relativePath,
            out string target,
            out string normalizedRelative,
            out string error)
        {
            target = null;
            normalizedRelative = null;
            error = "The selected working-copy path must be a canonical relative path.";
            if (string.IsNullOrEmpty(relativePath) || HasUnsafeLineBreak(relativePath) || Path.IsPathRooted(relativePath))
                return false;
            try
            {
                target = Path.GetFullPath(relativePath, root);
                if (!TryMakeRelativePath(root, target, out normalizedRelative))
                    return false;
                var requested = OperatingSystem.IsWindows()
                    ? relativePath.Replace('/', Path.DirectorySeparatorChar)
                    : relativePath;
                if (!normalizedRelative.Equals(requested, PathComparison) ||
                    (normalizedRelative == "." && relativePath != "."))
                    return false;
                if (normalizedRelative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                        StringSplitOptions.RemoveEmptyEntries)
                    .Any(component => component.Equals(".svn", StringComparison.OrdinalIgnoreCase) ||
                        (OperatingSystem.IsWindows() && IsUnsafeWindowsPathComponent(component))))
                {
                    error = "Subversion administrative paths and unsafe filename components cannot be edited.";
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = "The selected working-copy path could not be resolved safely.";
                return false;
            }
        }

        private static bool TryValidatePropertiesPath(string root, string target, out string kind, out string error)
        {
            kind = string.Empty;
            error = "The selected path or one of its parents could not be safely resolved inside the working copy.";
            if (!TryMakeRelativePath(root, target, out var relative))
                return false;
            if (relative == ".")
            {
                if (!TryValidateWorkingCopyRoot(root))
                    return false;
                kind = "dir";
                error = string.Empty;
                return true;
            }

            var current = root;
            var components = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < components.Length; index++)
            {
                current = Path.Combine(current, components[index]);
                var state = GetLocalPathState(current, out _, out var stateError);
                if (state == LocalPathState.Error)
                {
                    error = $"The selected path could not be inspected: {stateError}";
                    return false;
                }
                if (state == LocalPathState.SymbolicLink)
                {
                    error = "The selected path or one of its parents is a symbolic link or reparse point.";
                    return false;
                }
                if (state is not (LocalPathState.File or LocalPathState.Directory))
                {
                    error = "The selected path is missing. Refresh status before editing properties.";
                    return false;
                }
                if (index < components.Length - 1 && state != LocalPathState.Directory)
                {
                    error = "A parent component of the selected path is not a directory.";
                    return false;
                }
                if (state == LocalPathState.Directory)
                {
                    var marker = GetLocalPathState(Path.Combine(current, ".svn"), out _, out stateError);
                    if (marker != LocalPathState.Missing)
                    {
                        error = marker == LocalPathState.Error
                            ? $"A Subversion working-copy boundary could not be inspected: {stateError}"
                            : "The selected path is inside a nested Subversion working copy.";
                        return false;
                    }
                }
                kind = state == LocalPathState.Directory ? "dir" : "file";
            }

            if (!TryResolveSafeLocalPath(root, target, out _, out var symbolicLink) || symbolicLink)
            {
                error = "The selected path resolves outside this working copy or is a symbolic link.";
                return false;
            }
            error = string.Empty;
            return true;
        }

        private static string CreatePropertyToken(
            string identity,
            string relativePath,
            string kind,
            IReadOnlyList<SvnProperty> properties)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.Write("sourcegit-svn-properties-v1");
                writer.Write(identity ?? string.Empty);
                writer.Write(relativePath ?? string.Empty);
                writer.Write(kind ?? string.Empty);
                var ordered = properties?.OrderBy(property => property.Name, StringComparer.Ordinal).ToArray() ?? Array.Empty<SvnProperty>();
                writer.Write(ordered.Length);
                foreach (var property in ordered)
                {
                    writer.Write(property.Name);
                    writer.Write(property.IsBinary);
                    var bytes = property.IsBinary
                        ? Convert.FromBase64String(property.Value)
                        : new UTF8Encoding(false, true).GetBytes(property.Value);
                    writer.Write(bytes.Length);
                    writer.Write(bytes);
                }
            }
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }

        private static bool PropertyXmlPathMatches(string xmlPath, string target)
        {
            if (string.IsNullOrWhiteSpace(xmlPath))
                return false;
            try
            {
                var resolved = Path.IsPathFullyQualified(xmlPath) ? Path.GetFullPath(xmlPath) : Path.GetFullPath(xmlPath, Path.GetDirectoryName(target));
                return PathEquals(resolved, Path.GetFullPath(target));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }

        private static bool IsSafeIgnoreBasename(string basename)
        {
            return !string.IsNullOrEmpty(basename) && basename is not ("." or "..") &&
                basename.IndexOfAny(OperatingSystem.IsWindows()
                    ? new[] { '/', '\\', '\r', '\n', '\0' }
                    : new[] { '/', '\r', '\n', '\0' }) < 0 &&
                !basename.Any(character => char.IsControl(character) && character != '\t');
        }

        private static string EscapeIgnoreGlob(string basename)
        {
            var escaped = new StringBuilder(basename.Length);
            foreach (var character in basename)
            {
                if (character == '\\' || character is '*' or '?' or '[' or ']')
                    escaped.Append('\\');
                escaped.Append(character);
            }
            return escaped.ToString();
        }

        private static bool HasIgnoreLine(string value, string pattern)
        {
            var start = 0;
            for (var index = 0; index <= value.Length; index++)
            {
                if (index != value.Length && value[index] is not ('\r' or '\n'))
                    continue;
                if (value.AsSpan(start, index - start).SequenceEqual(pattern.AsSpan()))
                    return true;
                if (index < value.Length && value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
                    index++;
                start = index + 1;
            }
            return false;
        }

        private static string CreateProtectedPropertyFile(byte[] value)
        {
            return CreateProtectedPropertyFile(value, static (stream, bytes) =>
            {
                stream.Write(bytes);
                ((FileStream)stream).Flush(flushToDisk: true);
            });
        }

        private static string CreateProtectedPropertyFile(byte[] value, Action<Stream, byte[]> writeValue)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (writeValue == null)
                throw new ArgumentNullException(nameof(writeValue));
            var directory = Path.GetFullPath(Path.GetTempPath());
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var path = Path.Combine(directory, "sourcegit-svn-property-" + Guid.NewGuid().ToString("N") + ".tmp");
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

                FileStream stream;
                try
                {
                    stream = new FileStream(path, options);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Retry only a CreateNew collision. The write and flush happen outside this catch.
                    continue;
                }

                try
                {
                    using (stream)
                        writeValue(stream, value);
                    return path;
                }
                catch (Exception writeError)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                    {
                        throw new IOException($"Could not write or remove the partial protected property file '{path}': {cleanupError.Message}",
                            new AggregateException(writeError, cleanupError));
                    }
                    throw;
                }
            }
            throw new IOException("Could not allocate a unique protected property file.");
        }
    }
}
