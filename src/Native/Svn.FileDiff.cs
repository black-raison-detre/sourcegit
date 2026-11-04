using System;
using System.Collections.Generic;
using System.Globalization;
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
        private const int MaxFileSnapshotBytes = 8 * 1024 * 1024;
        private const int MaxFileMetadataBytes = 128 * 1024;

        public record SvnFileSnapshot(string Path, long Revision, byte[] Content, bool IsBinary = false, bool IsSpecial = false);

        public record FileDiffResult(
            string Status,
            SvnFileSnapshot Before = null,
            SvnFileSnapshot After = null,
            string Details = "",
            bool IsDirectory = false);

        private record FileEndpoint(string Path, long Revision);
        private record FileMetadata(bool IsBinary, bool IsSpecial);
        private record FileCommandResult(string Status, int ExitCode, byte[] StandardOutput, bool OutputTruncated, string StandardError, bool ErrorTruncated);

        /// <summary>
        /// Loads repository snapshots for one path changed in the selected revision. The working
        /// copy is only used to verify repository identity; local files and status are never read or changed.
        /// </summary>
        public static async Task<FileDiffResult> GetFileDiffAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            long revision,
            string changedPath,
            Credentials credentials = null,
            CancellationToken cancellationToken = default,
            string expectedUrl = null)
        {
            if (revision < 1 || !IsRepositoryPath(changedPath))
                return new FileDiffResult("InvalidArgument", Details: "The selected revision or repository path is invalid.");
            if (credentials != null && (HasUnsafeLineBreak(credentials.Username) || HasUnsafeLineBreak(credentials.Password)))
                return new FileDiffResult("Failed", Details: "Username and password cannot contain newline or control characters.");

            var changed = await GetChangedPathsAsync(executable, configDirectory, workingCopyPath, revision,
                credentials, cancellationToken, expectedUrl).ConfigureAwait(false);
            if (changed.Status != "Ready")
                return new FileDiffResult(changed.Status, Details: changed.Details);

            var selected = changed.Paths.FirstOrDefault(path => path.Path.Equals(changedPath, StringComparison.Ordinal));
            if (selected == null)
                return new FileDiffResult("PathNotChanged", Details: "The selected path is not part of this revision's changed-path list.");
            if (selected.Kind == "dir")
                return DirectoryDiffResult();

            if (!TryPrepareHistoryAccess(changed.RepositoryRoot, configDirectory, credentials,
                    out var accessFailureStatus, out var accessFailureDetails,
                    out var effectiveConfigDirectory, out var sectionsWithPlaintextDisabled))
                return new FileDiffResult(accessFailureStatus, Details: accessFailureDetails);

            var before = CreateBeforeEndpoint(selected, revision);
            var after = CreateAfterEndpoint(selected, revision);
            if (before == null && after == null)
                return new FileDiffResult("Failed", Details: "SVN returned a changed path without a readable revision endpoint.");

            var resolveKinds = selected.Kind == "unknown" || selected.Action == "R";
            if (resolveKinds)
            {
                if (before != null)
                {
                    var kind = await GetEndpointKindAsync(executable, configDirectory, effectiveConfigDirectory,
                        changed.RepositoryRoot, before, credentials, sectionsWithPlaintextDisabled, cancellationToken).ConfigureAwait(false);
                    if (kind.Status != "Ready")
                        return new FileDiffResult(kind.Status, Details: kind.Details);
                    if (kind.Kind == "dir")
                        return DirectoryDiffResult();
                }

                if (after != null)
                {
                    var kind = await GetEndpointKindAsync(executable, configDirectory, effectiveConfigDirectory,
                        changed.RepositoryRoot, after, credentials, sectionsWithPlaintextDisabled, cancellationToken).ConfigureAwait(false);
                    if (kind.Status != "Ready")
                        return new FileDiffResult(kind.Status, Details: kind.Details);
                    if (kind.Kind == "dir")
                        return DirectoryDiffResult();
                }
            }

            var beforeSnapshot = before == null ? null : await ReadSnapshotAsync(executable, configDirectory,
                effectiveConfigDirectory, changed.RepositoryRoot, before, credentials, sectionsWithPlaintextDisabled,
                cancellationToken).ConfigureAwait(false);
            if (beforeSnapshot != null && beforeSnapshot.Status != "Ready")
                return new FileDiffResult(beforeSnapshot.Status, Details: beforeSnapshot.Details);

            var afterSnapshot = after == null ? null : await ReadSnapshotAsync(executable, configDirectory,
                effectiveConfigDirectory, changed.RepositoryRoot, after, credentials, sectionsWithPlaintextDisabled,
                cancellationToken).ConfigureAwait(false);
            if (afterSnapshot != null && afterSnapshot.Status != "Ready")
                return new FileDiffResult(afterSnapshot.Status, Details: afterSnapshot.Details);

            return new FileDiffResult("Ready", beforeSnapshot?.Snapshot, afterSnapshot?.Snapshot);
        }

        private record SnapshotReadResult(string Status, SvnFileSnapshot Snapshot = null, string Details = "");
        private record EndpointKindResult(string Status, string Kind = "", string Details = "");

        private static FileDiffResult DirectoryDiffResult()
        {
            return new FileDiffResult("Directory", Details: "This change affects a directory; recursive expansion is not supported.", IsDirectory: true);
        }

        private static FileEndpoint CreateBeforeEndpoint(SvnChangedPath changedPath, long revision)
        {
            return changedPath.Action switch
            {
                "M" or "D" or "R" => new FileEndpoint(changedPath.Path, revision - 1),
                "A" when !string.IsNullOrEmpty(changedPath.CopyFromPath) && changedPath.CopyFromRevision.HasValue =>
                    new FileEndpoint(changedPath.CopyFromPath, changedPath.CopyFromRevision.Value),
                "A" => null,
                _ => null,
            };
        }

        private static FileEndpoint CreateAfterEndpoint(SvnChangedPath changedPath, long revision)
        {
            return changedPath.Action is "A" or "M" or "R" ? new FileEndpoint(changedPath.Path, revision) : null;
        }

        private static async Task<EndpointKindResult> GetEndpointKindAsync(
            string executable,
            string configDirectory,
            string effectiveConfigDirectory,
            string repositoryRoot,
            FileEndpoint endpoint,
            Credentials credentials,
            IReadOnlyList<string> sectionsWithPlaintextDisabled,
            CancellationToken cancellationToken)
        {
            var target = BuildRepositoryTarget(repositoryRoot, endpoint);
            if (target == null)
                return new EndpointKindResult("Failed", Details: "SVN returned an invalid repository path for a file endpoint.");

            var arguments = new List<string> { "info", "--xml", "--non-interactive", "--revision", endpoint.Revision.ToString(CultureInfo.InvariantCulture) };
            AppendHistoryAccessArguments(arguments, configDirectory, effectiveConfigDirectory, credentials, sectionsWithPlaintextDisabled);
            arguments.Add("--");
            arguments.Add(target);
            var result = await RunFileCommandAsync(executable, arguments, credentials, MaxFileMetadataBytes, cancellationToken).ConfigureAwait(false);
            if (result.Status != "Ready")
                return new EndpointKindResult(result.Status, Details: result.StandardError);
            if (result.OutputTruncated)
                return new EndpointKindResult("Failed", Details: "SVN endpoint info XML exceeded the 128 KiB output limit.");
            if (result.ExitCode != 0)
            {
                var failure = ClassifyConnectionFailure(result.StandardError, credentials != null);
                return new EndpointKindResult(failure.Status, Details: FormatFileCommandFailure(result, credentials));
            }
            try
            {
                var xml = DecodeXml(result.StandardOutput);
                var document = LoadSafeXml(xml, MaxFileMetadataBytes);
                var entry = document.DocumentElement?.SelectSingleNode("entry") as XmlElement;
                var repository = entry?.SelectSingleNode("repository/root")?.InnerText?.Trim() ?? string.Empty;
                var kind = entry?.GetAttribute("kind") ?? string.Empty;
                if (entry == null || !RepositoryRootsMatch(repositoryRoot, repository) || kind is not ("file" or "dir"))
                    return new EndpointKindResult("Failed", Details: "SVN returned incomplete or mismatched endpoint info XML.");
                return new EndpointKindResult("Ready", kind);
            }
            catch (Exception ex) when (ex is XmlException or DecoderFallbackException)
            {
                return new EndpointKindResult("Failed", Details: Redact($"SVN returned malformed endpoint info XML: {ex.Message}", credentials));
            }
        }

        private static async Task<SnapshotReadResult> ReadSnapshotAsync(
            string executable,
            string configDirectory,
            string effectiveConfigDirectory,
            string repositoryRoot,
            FileEndpoint endpoint,
            Credentials credentials,
            IReadOnlyList<string> sectionsWithPlaintextDisabled,
            CancellationToken cancellationToken)
        {
            var target = BuildRepositoryTarget(repositoryRoot, endpoint);
            if (target == null)
                return new SnapshotReadResult("Failed", Details: "SVN returned an invalid repository path for a file endpoint.");

            var propertyArguments = new List<string>
            {
                "proplist", "--xml", "--verbose", "--non-interactive", "--revision", endpoint.Revision.ToString(CultureInfo.InvariantCulture),
            };
            AppendHistoryAccessArguments(propertyArguments, configDirectory, effectiveConfigDirectory, credentials, sectionsWithPlaintextDisabled);
            propertyArguments.Add("--");
            propertyArguments.Add(target);
            var properties = await RunFileCommandAsync(executable, propertyArguments, credentials, MaxFileMetadataBytes, cancellationToken).ConfigureAwait(false);
            if (properties.Status != "Ready")
                return new SnapshotReadResult(properties.Status, Details: properties.StandardError);
            if (properties.OutputTruncated)
                return new SnapshotReadResult("Failed", Details: "SVN property XML exceeded the 128 KiB output limit.");
            if (properties.ExitCode != 0)
            {
                var failure = ClassifyConnectionFailure(properties.StandardError, credentials != null);
                return new SnapshotReadResult(failure.Status, Details: FormatFileCommandFailure(properties, credentials));
            }
            FileMetadata metadata;
            try
            {
                metadata = ParseFileMetadata(DecodeXml(properties.StandardOutput));
            }
            catch (Exception ex) when (ex is XmlException or DecoderFallbackException)
            {
                return new SnapshotReadResult("Failed", Details: Redact($"SVN returned malformed property XML: {ex.Message}", credentials));
            }

            var catArguments = new List<string>
            {
                "cat", "--ignore-keywords", "--non-interactive", "--revision", endpoint.Revision.ToString(CultureInfo.InvariantCulture),
            };
            AppendHistoryAccessArguments(catArguments, configDirectory, effectiveConfigDirectory, credentials, sectionsWithPlaintextDisabled);
            catArguments.Add("--");
            catArguments.Add(target);
            var content = await RunFileCommandAsync(executable, catArguments, credentials, MaxFileSnapshotBytes, cancellationToken).ConfigureAwait(false);
            if (content.Status != "Ready")
                return new SnapshotReadResult(content.Status, Details: content.StandardError);
            if (content.OutputTruncated)
                return new SnapshotReadResult("TooLarge", Details: "SVN file content exceeded the 8 MiB output limit.");
            if (content.ExitCode != 0)
            {
                var failure = ClassifyConnectionFailure(content.StandardError, credentials != null);
                return new SnapshotReadResult(failure.Status, Details: FormatFileCommandFailure(content, credentials));
            }
            var isBinary = metadata.IsBinary || IsBinaryContent(content.StandardOutput);
            return new SnapshotReadResult("Ready", new SvnFileSnapshot(endpoint.Path, endpoint.Revision,
                content.StandardOutput, isBinary, metadata.IsSpecial));
        }

        private static FileMetadata ParseFileMetadata(string xml)
        {
            var document = LoadSafeXml(xml, MaxFileMetadataBytes);
            if (document.DocumentElement?.Name != "properties")
                throw new XmlException("Expected a properties document root.");

            var isBinary = false;
            var isSpecial = false;
            foreach (XmlNode node in document.SelectNodes("/properties/target/property"))
            {
                if (node is not XmlElement property)
                    continue;
                var name = property.GetAttribute("name");
                if (name.Equals("svn:mime-type", StringComparison.Ordinal))
                    isBinary = !property.InnerText.Trim().StartsWith("text/", StringComparison.OrdinalIgnoreCase);
                else if (name.Equals("svn:special", StringComparison.Ordinal))
                    isSpecial = true;
            }

            return new FileMetadata(isBinary, isSpecial);
        }

        private static XmlDocument LoadSafeXml(string xml, int maxCharacters)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = maxCharacters,
            };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
                document.Load(reader);
            return document;
        }

        private static string DecodeXml(byte[] bytes)
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        private static bool IsBinaryContent(byte[] content)
        {
            try
            {
                if (content.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
                {
                    _ = new UTF32Encoding(false, true, true).GetString(content, 4, content.Length - 4);
                    return false;
                }
                if (content.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
                {
                    _ = new UTF32Encoding(true, true, true).GetString(content, 4, content.Length - 4);
                    return false;
                }
                if (content.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
                {
                    _ = new UnicodeEncoding(false, true, true).GetString(content, 2, content.Length - 2);
                    return false;
                }
                if (content.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
                {
                    _ = new UnicodeEncoding(true, true, true).GetString(content, 2, content.Length - 2);
                    return false;
                }
            }
            catch (DecoderFallbackException)
            {
                return true;
            }

            if (content.Any(value => value == 0))
                return true;
            try
            {
                _ = new UTF8Encoding(false, true).GetString(content);
                return false;
            }
            catch (DecoderFallbackException)
            {
                return true;
            }
        }

        private static string BuildRepositoryTarget(string repositoryRoot, FileEndpoint endpoint)
        {
            if (!IsRepositoryPath(endpoint.Path) || endpoint.Revision < 0 ||
                !Uri.TryCreate(repositoryRoot, UriKind.Absolute, out var rootUri) || ValidateUrl(repositoryRoot, out _, out _) != null)
                return null;

            var rootPath = rootUri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
            var suffix = string.Join("/", endpoint.Path.TrimStart('/').Split('/').Select(Uri.EscapeDataString));
            var url = rootUri.GetLeftPart(UriPartial.Authority) +
                (rootPath.Length == 0 ? string.Empty : "/" + rootPath) +
                (suffix.Length == 0 ? string.Empty : "/" + suffix);
            return url + "@" + endpoint.Revision.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatFileCommandFailure(FileCommandResult result, Credentials credentials)
        {
            var compatible = new SvnProcessResult("Ready", result.ExitCode, string.Empty, false,
                result.StandardError, result.ErrorTruncated);
            return FormatHistoryFailure(compatible, credentials);
        }

        private static async Task<FileCommandResult> RunFileCommandAsync(
            string executable,
            IReadOnlyList<string> arguments,
            Credentials credentials,
            int outputLimit,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(s_connectionTimeout);

            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = credentials != null,
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            if (credentials != null)
                process.StartInfo.StandardInputEncoding = new UTF8Encoding(false);
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);
            SetInvariantEnvironment(process.StartInfo);

            var started = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                started = process.Start();
                if (!started)
                    return new FileCommandResult("FailedToStart", -1, Array.Empty<byte>(), false, "The Subversion process could not be started.", false);

                var stdout = ReadBoundedBytesAsync(process.StandardOutput.BaseStream, outputLimit, process, timeout.Token);
                var stderr = ReadBoundedOutputAsync(process.StandardError, 128 * 1024, timeout.Token);
                var wait = process.WaitForExitAsync(timeout.Token);
                var sendPassword = credentials == null ? Task.CompletedTask : SendPasswordAsync(process, credentials.Password, timeout.Token);
                await Task.WhenAll(wait, stdout, stderr, sendPassword).WaitAsync(timeout.Token).ConfigureAwait(false);
                return new FileCommandResult("Ready", process.ExitCode, stdout.Result.Content, stdout.Result.Truncated,
                    stderr.Result.Text, stderr.Result.Truncated);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new FileCommandResult("TimedOut", -1, Array.Empty<byte>(), false, "The Subversion command exceeded 30 seconds.", false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new FileCommandResult(started ? "Failed" : "FailedToStart", -1, Array.Empty<byte>(), false,
                    Redact(ex.Message, credentials), false);
            }
            finally
            {
                if (started)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // The process may have exited while cancellation was being handled.
                    }
                }
            }
        }

        private record BoundedBytesResult(byte[] Content, bool Truncated);

        private static async Task<BoundedBytesResult> ReadBoundedBytesAsync(
            Stream stream,
            int limit,
            System.Diagnostics.Process process,
            CancellationToken cancellationToken)
        {
            using var content = new MemoryStream(Math.Min(limit, 64 * 1024));
            var buffer = new byte[64 * 1024];
            var truncated = false;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                var remaining = limit - (int)content.Length;
                var keep = Math.Min(remaining, read);
                if (keep > 0)
                    content.Write(buffer, 0, keep);
                if (keep < read)
                {
                    truncated = true;
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(true);
                    }
                    catch
                    {
                        // The child may have exited while the output limit was reached.
                    }
                    break;
                }
            }

            return new BoundedBytesResult(content.ToArray(), truncated);
        }
    }
}
