using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace SourceGit.Native
{
    public static partial class Svn
    {
        public record Credentials(string Username, string Password, bool Remember)
        {
            public override string ToString() => $"Credentials {{ Username = [redacted], Password = [redacted], Remember = {Remember} }}";
        }

        public record ConnectionResult(
            string Status,
            string Url = "",
            string RepositoryRoot = "",
            string Revision = "",
            string Details = "");

        private static readonly TimeSpan s_connectionTimeout = TimeSpan.FromSeconds(30);
        private const string DisablePlaintextPasswords = "servers:global:store-plaintext-passwords=no";
        private const int MaxServersConfigBytes = 1024 * 1024;
        private const int MaxServersConfigLineLength = 16 * 1024;
        private const int MaxServersConfigSections = 256;

        /// <summary>
        /// Resolves an explicit override or the standard per-user Subversion configuration directory.
        /// This method never creates the directory.
        /// </summary>
        public static string ResolveConfigDirectory(string configuredDirectory)
        {
            var path = configuredDirectory?.Trim();
            if (string.IsNullOrEmpty(path))
            {
                if (OperatingSystem.IsWindows())
                {
                    var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    if (string.IsNullOrEmpty(applicationData))
                        return string.Empty;

                    path = Path.Combine(applicationData, "Subversion");
                }
                else
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (string.IsNullOrEmpty(home))
                        home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
                    if (string.IsNullOrEmpty(home))
                        return string.Empty;

                    path = Path.Combine(home, ".subversion");
                }
            }

            if (!Path.IsPathFullyQualified(path))
                return path;

            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return path;
            }
        }

        public static async Task<ConnectionResult> TestConnectionAsync(
            string executable,
            string configDirectory,
            string url,
            Credentials credentials = null,
            CancellationToken cancellationToken = default)
        {
            var hasExplicitConfigDirectory = !string.IsNullOrWhiteSpace(configDirectory);
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new ConnectionResult("InvalidConfigDirectory", Details: "The Subversion configuration directory must be an absolute directory path and must not be a file.");

            if (string.IsNullOrEmpty(executable))
                return new ConnectionResult("NotFound", Details: "The Subversion executable was not found.");
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
                return new ConnectionResult("InvalidPath", Details: "The Subversion executable path is invalid.");

            var urlStatus = ValidateUrl(url, out var uri, out var urlError);
            if (urlStatus != null)
                return new ConnectionResult(urlStatus, Details: urlError);

            if (credentials != null && (HasUnsafeLineBreak(credentials.Username) || HasUnsafeLineBreak(credentials.Password)))
                return new ConnectionResult("Failed", Details: "Username and password cannot contain newline or control characters.");

            // svn+ssh can invoke a configured tunnel or SSH command that may prompt on its own.
            // Do not replace SVN_SSH or tunnel settings: that could silently change user behavior.
            if (uri.Scheme.Equals("svn+ssh", StringComparison.OrdinalIgnoreCase))
                return new ConnectionResult("UnsupportedTransport", Details: "svn+ssh testing requires explicit SSH key or agent integration, which this connection check cannot safely provide.");

            IReadOnlyList<string> sectionsWithPlaintextDisabled = null;
            if (credentials?.Remember == true)
            {
                if (!hasExplicitConfigDirectory)
                    return new ConnectionResult("CredentialStorageUnavailable", Details: "Remembered credentials require an explicit Subversion configuration directory.");
                if (!TryReadServerSections(effectiveConfigDirectory, out var serverSections))
                    return new ConnectionResult("CredentialStorageUnavailable", Details: "Cannot safely disable plaintext password storage in the Subversion server configuration.");

                sectionsWithPlaintextDisabled = serverSections;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await RunConnectionAttemptAsync(
                executable,
                effectiveConfigDirectory,
                hasExplicitConfigDirectory,
                url,
                credentials,
                sectionsWithPlaintextDisabled,
                noAuthCache: credentials == null || !credentials.Remember,
                cancellationToken).ConfigureAwait(false);
        }

        private static bool TryResolveConfigDirectory(string configuredDirectory, out string effectiveDirectory)
        {
            effectiveDirectory = ResolveConfigDirectory(configuredDirectory);
            if (string.IsNullOrWhiteSpace(effectiveDirectory) || HasUnsafeLineBreak(effectiveDirectory) || !Path.IsPathFullyQualified(effectiveDirectory))
                return false;

            try
            {
                effectiveDirectory = Path.GetFullPath(effectiveDirectory);
                return !File.Exists(effectiveDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }
        }

        private static bool TryReadServerSections(string configDirectory, out List<string> sections)
        {
            sections = new List<string>();
            var path = Path.Combine(configDirectory, "servers");
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
            {
                return false;
            }

            if ((attributes & FileAttributes.Directory) != 0)
                return false;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaxServersConfigBytes)
                    return false;

                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var previousLineWasSetting = false;
                string currentSection = null;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length > MaxServersConfigLineLength)
                        return false;

                    foreach (var character in line)
                    {
                        if (char.IsControl(character) && character != '\t')
                            return false;
                    }

                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
                    {
                        previousLineWasSetting = false;
                        continue;
                    }

                    var indented = char.IsWhiteSpace(line[0]);
                    if (indented && previousLineWasSetting)
                    {
                        // A continued value cannot safely be distinguished from an indented section header.
                        if (trimmed.StartsWith("[", StringComparison.Ordinal))
                            return false;

                        continue;
                    }

                    if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        if (indented || trimmed.Length < 3 || trimmed[^1] != ']')
                            return false;

                        var section = trimmed[1..^1];
                        if (section.Length == 0 || !section.Equals(section.Trim(), StringComparison.Ordinal) ||
                            section.Contains(':') || section.Contains('[') || section.Contains(']') || section.Contains('\t'))
                            return false;

                        currentSection = section;
                        previousLineWasSetting = false;
                        if (!section.Equals("groups", StringComparison.OrdinalIgnoreCase) &&
                            !section.Equals("global", StringComparison.OrdinalIgnoreCase) &&
                            seen.Add(section))
                        {
                            if (seen.Count > MaxServersConfigSections)
                                return false;
                            sections.Add(section);
                        }

                        continue;
                    }

                    if (indented)
                        return false;

                    var equalsIndex = trimmed.IndexOf('=');
                    var key = equalsIndex > 0 ? trimmed[..equalsIndex].Trim() : string.Empty;
                    if (key.Length == 0 || key.Contains(':'))
                        return false;
                    if (currentSection?.Equals("groups", StringComparison.OrdinalIgnoreCase) == true &&
                        key.Equals("store-plaintext-passwords", StringComparison.OrdinalIgnoreCase))
                        return false;

                    previousLineWasSetting = true;
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
            {
                sections.Clear();
                return false;
            }
        }

        private static string ValidateUrl(string url, out Uri uri, out string error)
        {
            uri = null;
            error = "The repository URL must be an absolute HTTP, HTTPS, SVN, SVN+SSH, or file URL.";
            if (string.IsNullOrWhiteSpace(url) || HasUnsafeLineBreak(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri))
                return "InvalidUrl";

            var scheme = uri.Scheme;
            if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                !scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                !scheme.Equals("svn", StringComparison.OrdinalIgnoreCase) &&
                !scheme.Equals("svn+ssh", StringComparison.OrdinalIgnoreCase) &&
                !scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                return "InvalidUrl";

            try
            {
                if (Uri.UnescapeDataString(uri.UserInfo).Contains(':'))
                {
                    error = "Passwords embedded in repository URLs are not allowed. Enter credentials in the connection dialog.";
                    return "InvalidUrl";
                }
            }
            catch (UriFormatException)
            {
                return "InvalidUrl";
            }

            error = string.Empty;
            return null;
        }

        private static bool HasUnsafeLineBreak(string value)
        {
            if (value == null)
                return false;

            foreach (var character in value)
            {
                if (char.IsControl(character))
                    return true;
            }

            return false;
        }

        private static async Task<ConnectionResult> RunConnectionAttemptAsync(
            string executable,
            string configDirectory,
            bool useConfigDirectory,
            string url,
            Credentials credentials,
            IReadOnlyList<string> sectionsWithPlaintextDisabled,
            bool noAuthCache,
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

            var arguments = process.StartInfo.ArgumentList;
            arguments.Add("info");
            arguments.Add("--xml");
            arguments.Add("--non-interactive");
            if (useConfigDirectory)
            {
                arguments.Add("--config-dir");
                arguments.Add(configDirectory);
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
            if (noAuthCache)
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
            arguments.Add(url + "@");
            SetInvariantEnvironment(process.StartInfo);

            var started = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                started = process.Start();
                if (!started)
                    return new ConnectionResult("FailedToStart", Details: "The Subversion process could not be started.");

                var stdout = ReadBoundedOutputAsync(process.StandardOutput, 128 * 1024, timeout.Token);
                var stderr = ReadBoundedOutputAsync(process.StandardError, 128 * 1024, timeout.Token);
                var wait = process.WaitForExitAsync(timeout.Token);
                var sendPassword = credentials == null ? Task.CompletedTask : SendPasswordAsync(process, credentials.Password, timeout.Token);
                await Task.WhenAll(wait, stdout, stderr, sendPassword).WaitAsync(timeout.Token).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    var rawStderr = stderr.Result.Text;
                    var classified = ClassifyConnectionFailure(rawStderr, credentials != null);
                    var stderrText = credentials != null && stderr.Result.Truncated
                        ? "SVN error output omitted because it exceeded 128 KiB."
                        : rawStderr;
                    if (stderr.Result.Truncated && credentials == null)
                        stderrText += "\n[stderr output truncated after 128 KiB]";

                    var details = Redact($"Exit code: {process.ExitCode}\n{stderrText}".Trim(), credentials);
                    return classified with { Details = details };
                }

                if (stdout.Result.Truncated)
                    return new ConnectionResult("Failed", Details: "SVN info XML exceeded the 128 KiB output limit.");

                try
                {
                    var info = ParseInfoXml(stdout.Result.Text);
                    var infoUrlStatus = ValidateUrl(info.Url, out _, out _);
                    var repositoryRootStatus = ValidateUrl(info.RepositoryRoot, out _, out _);
                    if (infoUrlStatus != null || repositoryRootStatus != null ||
                        !long.TryParse(info.Revision, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                        return new ConnectionResult("Failed", Details: "SVN returned invalid or incomplete info XML.");

                    return new ConnectionResult("Ready", info.Url, info.RepositoryRoot, info.Revision);
                }
                catch (XmlException ex)
                {
                    return new ConnectionResult("Failed", Details: Redact($"SVN returned malformed info XML: {ex.Message}", credentials));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ConnectionResult("TimedOut", Details: "The Subversion connection check exceeded 30 seconds.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ConnectionResult(started ? "Failed" : "FailedToStart", Details: Redact(ex.Message, credentials));
            }
            finally
            {
                if (started)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(true);
                    }
                    catch
                    {
                        // The process may have exited while cancellation was being handled.
                    }
                }
            }
        }

        private static (string Url, string RepositoryRoot, string Revision) ParseInfoXml(string xml)
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

            var url = entry.SelectSingleNode("url")?.InnerText?.Trim() ?? string.Empty;
            var repositoryRoot = entry.SelectSingleNode("repository/root")?.InnerText?.Trim() ?? string.Empty;
            var revision = entry.GetAttribute("revision");
            return (url, repositoryRoot, revision);
        }

        private static async Task SendPasswordAsync(Process process, string password, CancellationToken cancellationToken)
        {
            try
            {
                await process.StandardInput.WriteLineAsync(password ?? string.Empty).WaitAsync(cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The client may exit before consuming stdin; its diagnostics determine the result.
            }
            finally
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // Closing an already exited process's input stream is best effort.
                }
            }
        }

        private static ConnectionResult ClassifyConnectionFailure(string details, bool explicitCredentials)
        {
            if (REG_CERTIFICATE_ERROR().IsMatch(details))
                return new ConnectionResult("CertificateError", Details: details);
            if (details.Contains("E175013", StringComparison.OrdinalIgnoreCase))
                return new ConnectionResult("AccessDenied", Details: details);
            if (REG_AUTH_ERROR().IsMatch(details))
                return new ConnectionResult(explicitCredentials ? "AuthenticationFailed" : "AuthenticationRequired", Details: details);
            if (REG_NOT_FOUND().IsMatch(details))
                return new ConnectionResult("RepositoryNotFound", Details: details);

            return new ConnectionResult("Failed", Details: details);
        }

        private static string Redact(string text, Credentials credentials)
        {
            if (credentials == null || string.IsNullOrEmpty(text))
                return text;

            if (!string.IsNullOrEmpty(credentials.Password))
                text = text.Replace(credentials.Password, "[redacted]", StringComparison.Ordinal);
            return text;
        }

        private static void SetInvariantEnvironment(ProcessStartInfo startInfo)
        {
            if (OperatingSystem.IsMacOS())
                startInfo.Environment["LC_ALL"] = "en_US.UTF-8";
            else if (OperatingSystem.IsLinux())
                startInfo.Environment["LC_ALL"] = "C.UTF-8";

            startInfo.Environment["LANGUAGE"] = "C";
        }

        [GeneratedRegex(@"E(?:215004|170001)", RegexOptions.IgnoreCase)]
        private static partial Regex REG_AUTH_ERROR();

        [GeneratedRegex(@"E23000[1-9]", RegexOptions.IgnoreCase)]
        private static partial Regex REG_CERTIFICATE_ERROR();

        [GeneratedRegex(@"E(?:160005|160013|160020|170000)", RegexOptions.IgnoreCase)]
        private static partial Regex REG_NOT_FOUND();
    }
}
