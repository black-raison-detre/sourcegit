using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace SourceGit.Native
{
    public record SvnChangedPath(
        string Path,
        string RelativePath,
        string Action,
        string Kind,
        string CopyFromPath = "",
        long? CopyFromRevision = null);

    public static partial class Svn
    {
        private const int MaxChangedPathsInfoCharacters = 128 * 1024;

        public record ChangedPathsResult(string Status, List<SvnChangedPath> Paths, string Details = "", string RepositoryRoot = "");

        /// <summary>
        /// Reads the changed paths for one repository revision, scoped to the selected working-copy
        /// node's historical URL at that revision. The working-copy target and credentials are
        /// pinned to the identity verified by <paramref name="expectedUrl"/>.
        /// </summary>
        public static async Task<ChangedPathsResult> GetChangedPathsAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            long revision,
            Credentials credentials = null,
            CancellationToken cancellationToken = default,
            string expectedUrl = null)
        {
            if (revision < 1)
                return new ChangedPathsResult("InvalidArgument", new List<SvnChangedPath>(), "The selected revision must be greater than zero.");
            if (credentials != null && (HasUnsafeLineBreak(credentials.Username) || HasUnsafeLineBreak(credentials.Password)))
                return new ChangedPathsResult("Failed", new List<SvnChangedPath>(), "Username and password cannot contain newline or control characters.");

            var workingCopy = await GetWorkingCopyAsync(executable, configDirectory, workingCopyPath, cancellationToken).ConfigureAwait(false);
            if (workingCopy.Status != "Ready")
                return new ChangedPathsResult(workingCopy.Status, new List<SvnChangedPath>(), workingCopy.Details);
            if (!string.IsNullOrEmpty(expectedUrl) && !workingCopy.Url.Equals(expectedUrl, StringComparison.Ordinal))
                return new ChangedPathsResult("WorkingCopyChanged", new List<SvnChangedPath>(), "The working-copy URL changed. Refresh its history before loading changed paths.");
            if (!TryPrepareHistoryAccess(workingCopy.Url, configDirectory, credentials,
                    out var accessFailureStatus, out var accessFailureDetails,
                    out var effectiveConfigDirectory, out var sectionsWithPlaintextDisabled))
                return new ChangedPathsResult(accessFailureStatus, new List<SvnChangedPath>(), accessFailureDetails);

            var revisionText = revision.ToString(CultureInfo.InvariantCulture);
            var baseRevisionText = workingCopy.Revision.ToString(CultureInfo.InvariantCulture);
            var infoResult = await RunChangedPathsInfoAsync(executable, configDirectory, effectiveConfigDirectory,
                workingCopy.Url, baseRevisionText, revision, credentials, sectionsWithPlaintextDisabled, cancellationToken).ConfigureAwait(false);
            var historicalPegRevision = revision;
            if (infoResult.Status == "Ready" && infoResult.ExitCode != 0 && revision > 1 &&
                ClassifyConnectionFailure(infoResult.StandardError, credentials != null).Status == "RepositoryNotFound")
            {
                // A deletion has no node at REV. Resolve the selected path at REV-1, then ask log
                // for REV with that peg so the deleted path remains in the returned scope.
                historicalPegRevision = revision - 1;
                infoResult = await RunChangedPathsInfoAsync(executable, configDirectory, effectiveConfigDirectory,
                    workingCopy.Url, baseRevisionText, historicalPegRevision, credentials, sectionsWithPlaintextDisabled, cancellationToken).ConfigureAwait(false);
            }
            if (infoResult.Status != "Ready")
                return new ChangedPathsResult(infoResult.Status, new List<SvnChangedPath>(), infoResult.StandardError);
            if (infoResult.ExitCode != 0)
            {
                var failure = ClassifyConnectionFailure(infoResult.StandardError, credentials != null);
                return new ChangedPathsResult(failure.Status, new List<SvnChangedPath>(), FormatHistoryFailure(infoResult, credentials));
            }
            if (infoResult.OutputTruncated)
                return new ChangedPathsResult("Failed", new List<SvnChangedPath>(), "SVN historical info XML exceeded the 128 KiB output limit.");

            string historicalUrl;
            string historicalRepositoryRoot;
            try
            {
                (historicalUrl, historicalRepositoryRoot) = ParseRepositoryInfoXml(infoResult.StandardOutput);
                if (ValidateUrl(historicalUrl, out _, out _) != null ||
                    ValidateUrl(historicalRepositoryRoot, out _, out _) != null ||
                    !RepositoryRootsMatch(workingCopy.RepositoryRoot, historicalRepositoryRoot) ||
                    !TryGetRepositoryPath(historicalRepositoryRoot, historicalUrl, out var historicalScope))
                    return new ChangedPathsResult("Failed", new List<SvnChangedPath>(), "SVN returned invalid historical URL or repository-root info XML.");

                var historicalTargetPegged = historicalUrl + "@" + historicalPegRevision.ToString(CultureInfo.InvariantCulture);
                var logArguments = new List<string>
                {
                    "log", "--xml", "--verbose", "--non-interactive", "--revision", revisionText + ":" + revisionText,
                };
                AppendHistoryAccessArguments(logArguments, configDirectory, effectiveConfigDirectory, credentials, sectionsWithPlaintextDisabled);
                logArguments.Add("--");
                logArguments.Add(historicalTargetPegged);

                var logResult = await RunHistoryCommandAsync(executable, logArguments, credentials,
                    MaxHistoryXmlCharacters, cancellationToken).ConfigureAwait(false);
                if (logResult.Status != "Ready")
                    return new ChangedPathsResult(logResult.Status, new List<SvnChangedPath>(), logResult.StandardError);
                if (logResult.ExitCode != 0)
                {
                    var failure = ClassifyConnectionFailure(logResult.StandardError, credentials != null);
                    return new ChangedPathsResult(failure.Status, new List<SvnChangedPath>(), FormatHistoryFailure(logResult, credentials));
                }
                if (logResult.OutputTruncated)
                    return new ChangedPathsResult("Failed", new List<SvnChangedPath>(), "SVN changed-path XML exceeded the 12 MiB output limit.");

                return new ChangedPathsResult("Ready", ParseChangedPathsXml(logResult.StandardOutput, historicalScope, revision), RepositoryRoot: historicalRepositoryRoot);
            }
            catch (XmlException ex)
            {
                return new ChangedPathsResult("Failed", new List<SvnChangedPath>(), Redact($"SVN returned malformed changed-path or historical info XML: {ex.Message}", credentials));
            }
        }

        private static async Task<SvnProcessResult> RunChangedPathsInfoAsync(
            string executable,
            string configDirectory,
            string effectiveConfigDirectory,
            string workingCopyUrl,
            string baseRevision,
            long operativeRevision,
            Credentials credentials,
            IReadOnlyList<string> sectionsWithPlaintextDisabled,
            CancellationToken cancellationToken)
        {
            var arguments = new List<string>
            {
                "info", "--xml", "--non-interactive", "--revision", operativeRevision.ToString(CultureInfo.InvariantCulture),
            };
            AppendHistoryAccessArguments(arguments, configDirectory, effectiveConfigDirectory, credentials, sectionsWithPlaintextDisabled);
            arguments.Add("--");
            arguments.Add(workingCopyUrl + "@" + baseRevision);
            return await RunHistoryCommandAsync(executable, arguments, credentials,
                MaxChangedPathsInfoCharacters, cancellationToken).ConfigureAwait(false);
        }

        private static (string Url, string RepositoryRoot) ParseRepositoryInfoXml(string xml)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxChangedPathsInfoCharacters,
            };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new System.IO.StringReader(xml), settings))
                document.Load(reader);

            var root = document.DocumentElement;
            if (root == null || root.Name != "info")
                throw new XmlException("Expected an info document root.");
            var entry = root.SelectSingleNode("entry") as XmlElement;
            if (entry == null)
                throw new XmlException("Expected one info entry.");

            var url = entry.SelectSingleNode("url")?.InnerText?.Trim() ?? string.Empty;
            var repositoryRoot = entry.SelectSingleNode("repository/root")?.InnerText?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(repositoryRoot))
                throw new XmlException("The info entry is missing its URL or repository root.");
            return (url, repositoryRoot);
        }

        private static List<SvnChangedPath> ParseChangedPathsXml(string xml, string historicalScope, long expectedRevision)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxHistoryXmlCharacters,
            };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new System.IO.StringReader(xml), settings))
                document.Load(reader);

            var root = document.DocumentElement;
            if (root == null || root.Name != "log")
                throw new XmlException("Expected a log document root.");
            var entries = root.SelectNodes("logentry");
            if (entries == null || entries.Count > 1)
                throw new XmlException("Expected at most one log entry for the selected revision.");

            var changedPaths = new List<SvnChangedPath>();
            if (entries.Count == 0)
                return changedPaths;

            var entry = entries[0] as XmlElement;
            if (entry == null || !long.TryParse(entry.GetAttribute("revision"), NumberStyles.None, CultureInfo.InvariantCulture, out var logRevision) ||
                logRevision != expectedRevision)
                throw new XmlException("The log entry has an invalid revision number.");

            foreach (XmlNode node in entry.SelectNodes("paths/path"))
            {
                if (node is not XmlElement pathElement)
                    throw new XmlException("A changed path is not an XML element.");

                var path = pathElement.InnerText;
                var action = pathElement.GetAttribute("action");
                if (!IsRepositoryPath(path) || action is not ("A" or "M" or "D" or "R"))
                    throw new XmlException("A changed path has an invalid repository path or action.");

                var kind = pathElement.GetAttribute("kind");
                if (kind is not ("file" or "dir"))
                    kind = "unknown";

                var copyFromPath = pathElement.GetAttribute("copyfrom-path");
                long? copyFromRevision = null;
                var copyFromRevisionText = pathElement.GetAttribute("copyfrom-rev");
                if (!string.IsNullOrEmpty(copyFromPath) || !string.IsNullOrEmpty(copyFromRevisionText))
                {
                    if (!IsRepositoryPath(copyFromPath) ||
                        !long.TryParse(copyFromRevisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCopyRevision))
                        throw new XmlException("A copied path has invalid copy-from metadata.");
                    copyFromRevision = parsedCopyRevision;
                }

                if (IsWithinScope(path, historicalScope))
                {
                    changedPaths.Add(new SvnChangedPath(path, GetRelativePath(path, historicalScope), action, kind, copyFromPath, copyFromRevision));
                    continue;
                }

                if (IsAncestor(path, historicalScope) && kind is ("dir" or "unknown") && action is ("A" or "M" or "D" or "R"))
                    changedPaths.Add(new SvnChangedPath(path, ".", action, kind, copyFromPath, copyFromRevision));
            }

            return changedPaths;
        }

        private static bool TryGetRepositoryPath(string repositoryRoot, string url, out string repositoryPath)
        {
            repositoryPath = string.Empty;
            if (!Uri.TryCreate(repositoryRoot, UriKind.Absolute, out var rootUri) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var urlUri) ||
                !SameUrlAuthority(rootUri, urlUri))
                return false;

            string rootPath;
            string absolutePath;
            try
            {
                rootPath = Uri.UnescapeDataString(rootUri.AbsolutePath).TrimEnd('/');
                absolutePath = Uri.UnescapeDataString(urlUri.AbsolutePath);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (rootPath.Length == 0)
                rootPath = "/";
            if (!absolutePath.StartsWith("/", StringComparison.Ordinal))
                return false;

            if (rootPath == "/")
            {
                repositoryPath = absolutePath;
                return true;
            }

            if (absolutePath.Equals(rootPath, StringComparison.Ordinal))
            {
                repositoryPath = "/";
                return true;
            }

            if (!absolutePath.StartsWith(rootPath + "/", StringComparison.Ordinal))
                return false;

            repositoryPath = absolutePath[rootPath.Length..];
            return true;
        }

        private static bool RepositoryRootsMatch(string expectedRoot, string actualRoot)
        {
            if (!Uri.TryCreate(expectedRoot, UriKind.Absolute, out var expectedUri) ||
                !Uri.TryCreate(actualRoot, UriKind.Absolute, out var actualUri) ||
                !SameUrlAuthority(expectedUri, actualUri))
                return false;

            try
            {
                return Uri.UnescapeDataString(expectedUri.AbsolutePath).TrimEnd('/') ==
                       Uri.UnescapeDataString(actualUri.AbsolutePath).TrimEnd('/');
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        private static bool SameUrlAuthority(Uri left, Uri right)
        {
            return left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                   left.Port == right.Port;
        }

        private static bool IsRepositoryPath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.StartsWith("/", StringComparison.Ordinal) &&
                   !HasUnsafeLineBreak(path);
        }

        private static bool IsWithinScope(string path, string scope)
        {
            if (scope == "/")
                return true;
            return path.Equals(scope, StringComparison.Ordinal) || path.StartsWith(scope + "/", StringComparison.Ordinal);
        }

        private static bool IsAncestor(string path, string scope)
        {
            return path != scope && (path == "/" || scope.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal));
        }

        private static string GetRelativePath(string path, string scope)
        {
            if (path.Equals(scope, StringComparison.Ordinal))
                return ".";
            if (scope == "/")
                return path.TrimStart('/');
            return path[(scope.Length + 1)..];
        }
    }
}
