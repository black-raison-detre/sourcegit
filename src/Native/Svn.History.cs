using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace SourceGit.Native
{
    public record SvnRevision(long Number, string Author, DateTimeOffset? Date, string Message)
    {
        public string RevisionText => "r" + Number.ToString(CultureInfo.InvariantCulture);

        public string Subject
        {
            get
            {
                var message = Message ?? string.Empty;
                var end = message.IndexOfAny('\r', '\n');
                return end < 0 ? message : message[..end].TrimEnd('\r');
            }
        }

        public string DateText => Date?.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;
    }

    public static partial class Svn
    {
        private const int MaxHistoryXmlCharacters = 12 * 1024 * 1024;
        private const int MaxHistoryLimit = 1000;

        public record WorkingCopyResult(
            string Status,
            string WorkingCopyRoot = "",
            string Url = "",
            string RepositoryRoot = "",
            long Revision = 0,
            string Details = "");

        public record HistoryResult(string Status, List<SvnRevision> Revisions, string Details = "");

        private record SvnProcessResult(string Status, int ExitCode, string StandardOutput, bool OutputTruncated, string StandardError, bool ErrorTruncated);

        /// <summary>
        /// Finds the nearest Subversion working-copy marker without invoking SVN.
        /// The root returned by svn info remains authoritative.
        /// </summary>
        public static string FindWorkingCopyRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || HasUnsafeLineBreak(path))
                return string.Empty;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return string.Empty;
            }

            var current = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(current))
            {
                // Git owns a working copy at the same directory and takes precedence for launch detection.
                var gitMarker = Path.Combine(current, ".git");
                if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
                    return string.Empty;

                var svnMarker = Path.Combine(current, ".svn");
                if (File.Exists(Path.Combine(svnMarker, "wc.db")))
                    return current;

                var parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.Ordinal))
                    break;
                current = parent;
            }

            return string.Empty;
        }

        public static async Task<WorkingCopyResult> GetWorkingCopyAsync(
            string executable,
            string configDirectory,
            string path,
            CancellationToken cancellationToken = default)
        {
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new WorkingCopyResult("InvalidConfigDirectory", Details: "The Subversion configuration directory must be an absolute directory path and must not be a file.");
            if (string.IsNullOrEmpty(executable))
                return new WorkingCopyResult("NotFound", Details: "The Subversion executable was not found.");
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
                return new WorkingCopyResult("InvalidPath", Details: "The Subversion executable path is invalid.");
            if (!TryResolveWorkingCopyPath(path, out var fullPath))
                return new WorkingCopyResult("InvalidPath", Details: "The working-copy path must be an existing absolute path without control characters.");
            if (FindWorkingCopyRoot(fullPath).Length == 0)
                return new WorkingCopyResult("NotWorkingCopy", Details: "The selected path is not inside a Subversion working copy.");

            var arguments = new List<string> { "info", "--xml", "--non-interactive" };
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                arguments.Add("--config-dir");
                arguments.Add(effectiveConfigDirectory);
            }
            arguments.Add("--no-auth-cache");
            arguments.Add("--");
            arguments.Add(fullPath + "@");

            var result = await RunHistoryCommandAsync(executable, arguments, credentials: null,
                outputLimit: 128 * 1024, cancellationToken).ConfigureAwait(false);
            if (result.Status != "Ready")
                return new WorkingCopyResult(result.Status, Details: result.StandardError);

            if (result.ExitCode != 0)
                return new WorkingCopyResult("Failed", Details: FormatHistoryFailure(result, credentials: null));
            if (result.OutputTruncated)
                return new WorkingCopyResult("Failed", Details: "SVN info XML exceeded the 128 KiB output limit.");

            try
            {
                var info = ParseWorkingCopyInfo(result.StandardOutput);
                var urlStatus = ValidateUrl(info.Url, out _, out _);
                var repositoryStatus = ValidateUrl(info.RepositoryRoot, out _, out _);
                if (urlStatus != null || repositoryStatus != null ||
                    !Path.IsPathFullyQualified(info.WorkingCopyRoot) ||
                    !long.TryParse(info.Revision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
                    return new WorkingCopyResult("Failed", Details: "SVN returned invalid or incomplete working-copy info XML.");

                return new WorkingCopyResult("Ready", info.WorkingCopyRoot, info.Url, info.RepositoryRoot, revision);
            }
            catch (XmlException ex)
            {
                return new WorkingCopyResult("Failed", Details: $"SVN returned malformed info XML: {ex.Message}");
            }
        }

        public static async Task<HistoryResult> GetHistoryAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            long? beforeRevision = null,
            int limit = 100,
            Credentials credentials = null,
            CancellationToken cancellationToken = default,
            string expectedUrl = null)
        {
            if (beforeRevision < 0 || limit < 1 || limit > MaxHistoryLimit)
                return new HistoryResult("InvalidArgument", new List<SvnRevision>(), "The history cursor and page size are invalid.");
            if (credentials != null && (HasUnsafeLineBreak(credentials.Username) || HasUnsafeLineBreak(credentials.Password)))
                return new HistoryResult("Failed", new List<SvnRevision>(), "Username and password cannot contain newline or control characters.");

            var workingCopy = await GetWorkingCopyAsync(executable, configDirectory, workingCopyPath, cancellationToken).ConfigureAwait(false);
            if (workingCopy.Status != "Ready")
                return new HistoryResult(workingCopy.Status, new List<SvnRevision>(), workingCopy.Details);
            if (!string.IsNullOrEmpty(expectedUrl) && !workingCopy.Url.Equals(expectedUrl, StringComparison.Ordinal))
                return new HistoryResult("WorkingCopyChanged", new List<SvnRevision>(), "The working-copy URL changed. Refresh its history before continuing.");

            if (Uri.TryCreate(workingCopy.Url, UriKind.Absolute, out var uri) &&
                uri.Scheme.Equals("svn+ssh", StringComparison.OrdinalIgnoreCase))
                return new HistoryResult("UnsupportedTransport", new List<SvnRevision>(), "svn+ssh history requires explicit SSH key or agent integration, which this history reader cannot safely provide.");

            var hasExplicitConfigDirectory = !string.IsNullOrWhiteSpace(configDirectory);
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new HistoryResult("InvalidConfigDirectory", new List<SvnRevision>(), "The Subversion configuration directory must be an absolute directory path and must not be a file.");

            IReadOnlyList<string> sectionsWithPlaintextDisabled = null;
            if (credentials?.Remember == true)
            {
                if (!hasExplicitConfigDirectory)
                    return new HistoryResult("CredentialStorageUnavailable", new List<SvnRevision>(), "Remembered credentials require an explicit Subversion configuration directory.");
                if (!TryReadServerSections(effectiveConfigDirectory, out var serverSections))
                    return new HistoryResult("CredentialStorageUnavailable", new List<SvnRevision>(), "Cannot safely disable plaintext password storage in the Subversion server configuration.");
                sectionsWithPlaintextDisabled = serverSections;
            }

            if (beforeRevision == 0)
                return new HistoryResult("Ready", new List<SvnRevision>());

            var range = beforeRevision.HasValue
                ? (beforeRevision.Value - 1).ToString(CultureInfo.InvariantCulture) + ":0"
                : "HEAD:0";
            var arguments = new List<string> { "log", "--xml", "--non-interactive", "--limit", limit.ToString(CultureInfo.InvariantCulture), "--revision", range };
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                arguments.Add("--config-dir");
                arguments.Add(effectiveConfigDirectory);
            }
            if (sectionsWithPlaintextDisabled != null)
            {
                arguments.Add("--config-option");
                arguments.Add(DisablePlaintextPasswords);
                foreach (var section in sectionsWithPlaintextDisabled)
                {
                    arguments.Add("--config-option");
                    arguments.Add($"servers:{section}:store-plaintext-passwords=no");
                }
            }
            if (credentials == null || !credentials.Remember)
                arguments.Add("--no-auth-cache");
            if (credentials != null)
            {
                if (!string.IsNullOrEmpty(credentials.Username))
                {
                    arguments.Add("--username");
                    arguments.Add(credentials.Username);
                }
                arguments.Add("--password-from-stdin");
            }
            arguments.Add("--");
            // Pin the target to the URL and BASE revision reported by svn info. This follows copy history
            // while keeping explicit credentials bound to the checked working-copy identity.
            arguments.Add(workingCopy.Url + "@" + workingCopy.Revision.ToString(CultureInfo.InvariantCulture));

            var result = await RunHistoryCommandAsync(executable, arguments, credentials, MaxHistoryXmlCharacters, cancellationToken).ConfigureAwait(false);
            if (result.Status != "Ready")
                return new HistoryResult(result.Status, new List<SvnRevision>(), result.StandardError);
            if (result.ExitCode != 0)
            {
                var failure = ClassifyConnectionFailure(result.StandardError, credentials != null);
                return new HistoryResult(failure.Status, new List<SvnRevision>(), FormatHistoryFailure(result, credentials));
            }
            if (result.OutputTruncated)
                return new HistoryResult("Failed", new List<SvnRevision>(), "SVN log XML exceeded the 12 MiB output limit.");

            try
            {
                return new HistoryResult("Ready", ParseHistoryXml(result.StandardOutput));
            }
            catch (XmlException ex)
            {
                return new HistoryResult("Failed", new List<SvnRevision>(), Redact($"SVN returned malformed log XML: {ex.Message}", credentials));
            }
        }

        private static bool TryResolveWorkingCopyPath(string path, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(path) || HasUnsafeLineBreak(path) || !Path.IsPathFullyQualified(path))
                return false;

            try
            {
                fullPath = Path.GetFullPath(path);
                return File.Exists(fullPath) || Directory.Exists(fullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }

        private static async Task<SvnProcessResult> RunHistoryCommandAsync(
            string executable,
            IReadOnlyList<string> arguments,
            Credentials credentials,
            int outputLimit,
            CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(s_connectionTimeout);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = credentials != null,
                StandardOutputEncoding = new UTF8Encoding(false),
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
                    return new SvnProcessResult("FailedToStart", -1, string.Empty, false, "The Subversion process could not be started.", false);

                var stdout = ReadBoundedOutputAsync(process.StandardOutput, outputLimit, timeout.Token);
                var stderr = ReadBoundedOutputAsync(process.StandardError, 128 * 1024, timeout.Token);
                var wait = process.WaitForExitAsync(timeout.Token);
                var sendPassword = credentials == null ? Task.CompletedTask : SendPasswordAsync(process, credentials.Password, timeout.Token);
                await Task.WhenAll(wait, stdout, stderr, sendPassword).WaitAsync(timeout.Token).ConfigureAwait(false);
                return new SvnProcessResult("Ready", process.ExitCode, stdout.Result.Text, stdout.Result.Truncated, stderr.Result.Text, stderr.Result.Truncated);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new SvnProcessResult("TimedOut", -1, string.Empty, false, "The Subversion command exceeded 30 seconds.", false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new SvnProcessResult(started ? "Failed" : "FailedToStart", -1, string.Empty, false, Redact(ex.Message, credentials), false);
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

        private static string FormatHistoryFailure(SvnProcessResult result, Credentials credentials)
        {
            var stderr = result.ErrorTruncated
                ? credentials == null ? result.StandardError + "\n[stderr output truncated after 128 KiB]" : "SVN error output omitted because it exceeded 128 KiB."
                : result.StandardError;
            return Redact($"Exit code: {result.ExitCode}\n{stderr}".Trim(), credentials);
        }

        private static (string WorkingCopyRoot, string Url, string RepositoryRoot, string Revision) ParseWorkingCopyInfo(string xml)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 128 * 1024,
            };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
                document.Load(reader);

            var root = document.DocumentElement;
            if (root == null || root.Name != "info")
                throw new XmlException("Expected an info document root.");
            var entry = root.SelectSingleNode("entry") as XmlElement;
            if (entry == null)
                throw new XmlException("Expected one info entry.");
            var wcInfo = entry.SelectSingleNode("wc-info") as XmlElement;
            if (wcInfo == null)
                throw new XmlException("The info entry does not describe a working copy.");

            return (
                wcInfo.SelectSingleNode("wcroot-abspath")?.InnerText ?? string.Empty,
                entry.SelectSingleNode("url")?.InnerText?.Trim() ?? string.Empty,
                entry.SelectSingleNode("repository/root")?.InnerText?.Trim() ?? string.Empty,
                entry.GetAttribute("revision"));
        }

        private static List<SvnRevision> ParseHistoryXml(string xml)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxHistoryXmlCharacters,
            };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
                document.Load(reader);

            var root = document.DocumentElement;
            if (root == null || root.Name != "log")
                throw new XmlException("Expected a log document root.");

            var revisions = new List<SvnRevision>();
            foreach (XmlNode node in root.SelectNodes("logentry"))
            {
                if (node is not XmlElement entry ||
                    !long.TryParse(entry.GetAttribute("revision"), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    throw new XmlException("A log entry has an invalid revision number.");

                var dateText = entry.SelectSingleNode("date")?.InnerText?.Trim();
                DateTimeOffset? date = null;
                if (!string.IsNullOrEmpty(dateText) && DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDate))
                    date = parsedDate;

                revisions.Add(new SvnRevision(number,
                    entry.SelectSingleNode("author")?.InnerText ?? string.Empty,
                    date,
                    entry.SelectSingleNode("msg")?.InnerText ?? string.Empty));
            }

            return revisions;
        }
    }
}
