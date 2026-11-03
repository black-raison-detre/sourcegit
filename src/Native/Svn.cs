using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Native
{
    public static partial class Svn
    {
        public record ProbeResult(string Status, string Version = "", string Details = "", string CredentialStores = null);

        public static string ResolveExecutable(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
                return configuredPath.Trim();

            var filename = OperatingSystem.IsWindows() ? "svn.exe" : "svn";
            foreach (var directory in GetSearchDirectories())
            {
                var path = Path.Combine(directory, filename);
                if (!File.Exists(path))
                    continue;

                try
                {
                    if (!OperatingSystem.IsWindows() &&
                        (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                        continue;

                    return path;
                }
                catch (IOException)
                {
                    // Keep searching if a candidate cannot be inspected.
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep searching if a candidate is inaccessible.
                }
            }

            return string.Empty;
        }

        public static Task<ProbeResult> ProbeAsync(string executable, CancellationToken cancellationToken = default)
        {
            return ProbeAsync(executable, null, cancellationToken);
        }

        public static async Task<ProbeResult> ProbeAsync(string executable, string configDirectory, CancellationToken cancellationToken = default)
        {
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new ProbeResult("InvalidConfigDirectory");
            if (string.IsNullOrEmpty(executable))
                return new ProbeResult("NotFound");
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
                return new ProbeResult("InvalidPath");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                process.StartInfo.ArgumentList.Add("--config-dir");
                process.StartInfo.ArgumentList.Add(effectiveConfigDirectory);
            }
            process.StartInfo.ArgumentList.Add("--version");
            SetInvariantEnvironment(process.StartInfo);

            var started = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                started = process.Start();
                if (!started)
                    return new ProbeResult("FailedToStart");

                var stdout = ReadBoundedOutputAsync(process.StandardOutput, 32 * 1024, timeout.Token);
                var stderr = ReadBoundedOutputAsync(process.StandardError, 32 * 1024, timeout.Token);
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).WaitAsync(timeout.Token).ConfigureAwait(false);

                if (process.ExitCode != 0)
                    return new ProbeResult("Failed", Details: $"Exit code: {process.ExitCode}\n{stderr.Result.Text}".Trim());

                var output = stdout.Result.Text;
                var match = REG_VERSION_LINE().Match(output);
                var version = match.Success ? match.Groups[1].Value : output.Trim();
                if (!REG_VERSION_QUIET().IsMatch(version))
                    return new ProbeResult("InvalidVersion", Details: output.Trim());
                var stores = stdout.Result.Truncated ? null : ParseCredentialStores(output);
                return new ProbeResult("Ready", version, CredentialStores: stores);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ProbeResult("TimedOut");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ProbeResult("FailedToStart", Details: ex.Message);
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

        private static IEnumerable<string> GetSearchDirectories()
        {
            var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = entry.Trim().Trim('"');
                if (Path.IsPathFullyQualified(directory))
                    yield return directory;
            }

            if (OperatingSystem.IsWindows())
            {
                foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
                {
                    var root = Environment.GetFolderPath(folder);
                    if (string.IsNullOrEmpty(root))
                        continue;

                    foreach (var client in new[] { "TortoiseSVN", "SlikSvn", "Subversion" })
                        yield return Path.Combine(root, client, "bin");
                }
            }
            else
            {
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
                if (OperatingSystem.IsMacOS())
                {
                    yield return "/opt/homebrew/bin";
                    yield return "/opt/homebrew/opt/subversion/bin";
                    yield return "/opt/local/bin";
                }

                yield return "/usr/local/bin";
                yield return "/usr/bin";
                yield return "/bin";
            }
        }

        private static async Task<BoundedOutput> ReadBoundedOutputAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
        {
            // Drain both streams even after the display limit to avoid blocking the child process.
            var output = new StringBuilder();
            var buffer = new char[1024];
            var truncated = false;
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                var remaining = limit - output.Length;
                if (remaining > 0)
                    output.Append(buffer, 0, Math.Min(read, remaining));
                if (read > remaining)
                    truncated = true;
            }

            return new BoundedOutput(output.ToString(), truncated);
        }

        private record BoundedOutput(string Text, bool Truncated);

        private static string ParseCredentialStores(string output)
        {
            const string header = "The following authentication credential caches are available:";
            var lines = output.Split('\n');
            var headerIndex = Array.FindIndex(lines, line => line.TrimEnd('\r').Equals(header, StringComparison.Ordinal));
            if (headerIndex < 0)
                return null;

            var lineIndex = headerIndex + 1;
            while (lineIndex < lines.Length && string.IsNullOrWhiteSpace(lines[lineIndex]))
                lineIndex++;
            if (lineIndex >= lines.Length)
                return null;

            var stores = new List<string>();
            for (var i = lineIndex; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.StartsWith("* ", StringComparison.Ordinal))
                    stores.Add(line[2..].Trim());
                else if (stores.Count > 0)
                    return string.Join(", ", stores);
                else if (IsNoCredentialStoresLine(line))
                    return string.Empty;
                else
                    return null;
            }

            return stores.Count > 0 ? string.Join(", ", stores) : null;
        }

        private static bool IsNoCredentialStoresLine(string line)
        {
            var value = line.Trim().TrimEnd('.');
            return value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("(none)", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("no authentication credential caches are available", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("no credential caches are available", StringComparison.OrdinalIgnoreCase);
        }

        [GeneratedRegex(@"(?m)^svn, version\s+([^\s]+)")]
        private static partial Regex REG_VERSION_LINE();

        [GeneratedRegex(@"^\d+\.\d+\.\d+(?:[-+][\w.-]+)?$")]
        private static partial Regex REG_VERSION_QUIET();
    }
}
