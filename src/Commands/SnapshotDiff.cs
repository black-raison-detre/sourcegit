using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    // Compares immutable snapshots without consulting or modifying a working copy.
    public static class SnapshotDiff
    {
        public record Result(string Status, Models.TextDiff Diff = null, bool IsBinary = false, string Details = "");

        public static async Task<Result> ReadAsync(byte[] before, byte[] after, CancellationToken cancellationToken = default,
            int contextLines = 3, bool ignoreWhitespace = false, bool ignoreCRAtEOL = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDecode(before, out var oldText) || !TryDecode(after, out var newText))
                return new Result("Ready", IsBinary: true);
            if (ExceedsTextLimit(oldText) || ExceedsTextLimit(newText))
                return new Result("TooLarge");
            if (oldText == newText)
                return new Result("Ready");

            var directory = Directory.CreateTempSubdirectory("sourcegit-snapshot-diff-").FullName;
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var process = new Process();
            var started = false;
            try
            {
                var oldPath = Path.Combine(directory, "before");
                var newPath = Path.Combine(directory, "after");
                var configPath = Path.Combine(directory, "empty-config");
                await File.WriteAllTextAsync(oldPath, oldText, new UTF8Encoding(false), timeout.Token).ConfigureAwait(false);
                await File.WriteAllTextAsync(newPath, newText, new UTF8Encoding(false), timeout.Token).ConfigureAwait(false);
                await File.WriteAllTextAsync(configPath, string.Empty, timeout.Token).ConfigureAwait(false);

                var git = Native.OS.GitExecutable;
                if (string.IsNullOrEmpty(git))
                    git = Native.OS.FindGitExecutable();
                var start = new ProcessStartInfo(git)
                {
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                // Repository pointers, config injection, external drivers and tracing must not
                // carry over from the application environment into a snapshot comparison.
                foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                    start.Environment.Remove(key);
                start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
                start.Environment["GIT_CONFIG_GLOBAL"] = configPath;
                start.Environment["GIT_CONFIG_SYSTEM"] = configPath;
                start.Environment["GIT_ATTR_NOSYSTEM"] = "1";
                foreach (var argument in new[]
                {
                    "--no-pager", "-c", "core.attributesFile=" + configPath, "diff", "--no-index", "--no-color",
                    "--no-ext-diff", "--no-textconv", "--no-renames", "--full-index", "--text",
                    "--unified=" + Math.Clamp(contextLines, 0, 50000).ToString(System.Globalization.CultureInfo.InvariantCulture),
                })
                    start.ArgumentList.Add(argument);
                if (ignoreWhitespace)
                {
                    start.ArgumentList.Add("--ignore-space-change");
                    start.ArgumentList.Add("--ignore-blank-lines");
                }
                if (ignoreCRAtEOL)
                    start.ArgumentList.Add("--ignore-cr-at-eol");
                start.ArgumentList.Add("--");
                start.ArgumentList.Add(oldPath);
                start.ArgumentList.Add(newPath);
                process.StartInfo = start;
                process.Start();
                started = true;

                var output = ReadBoundedAsync(process.StandardOutput.BaseStream, 8 * 1024 * 1024, timeout.Token);
                var error = ReadBoundedAsync(process.StandardError.BaseStream, 64 * 1024, timeout.Token);
                await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode is not (0 or 1))
                    return new Result("Failed", Details: Encoding.UTF8.GetString(error.Result.Bytes));
                if (output.Result.Truncated)
                    return new Result("TooLarge");
                var patchLines = 0;
                foreach (var value in output.Result.Bytes)
                {
                    if (value == (byte)'\n' && ++patchLines > 12008)
                        return new Result("TooLarge");
                }

                var diff = Diff.ParseSnapshotPatch(output.Result.Bytes).TextDiff;
                if (diff?.Lines.Count > 12000)
                    return new Result("TooLarge");
                return new Result("Ready", diff);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new Result("TimedOut", Details: "The snapshot comparison exceeded 15 seconds.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new Result("Failed", Details: ex.Message);
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
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // The comparison may have exited while cancellation was being handled.
                    }
                }
                try
                {
                    Directory.Delete(directory, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A transient file lock must not replace the comparison or cancellation result.
                }
            }
        }

        private static bool TryDecode(byte[] bytes, out string text)
        {
            text = string.Empty;
            try
            {
                Encoding encoding = new UTF8Encoding(false, true);
                if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }))
                    encoding = new UTF32Encoding(false, false, true);
                else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
                    encoding = new UTF32Encoding(true, false, true);
                else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
                    encoding = new UnicodeEncoding(false, false, true);
                else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
                    encoding = new UnicodeEncoding(true, false, true);
                text = encoding.GetString(bytes);
                return !text.Contains('\0');
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static bool ExceedsTextLimit(string text)
        {
            if (text.Length > 2 * 1024 * 1024)
                return true;
            var lines = 1;
            var lineLength = 0;
            foreach (var character in text)
            {
                if (character == '\n')
                {
                    lineLength = 0;
                    if (++lines > 50000)
                        return true;
                }
                else if (++lineLength > 16384)
                    return true;
            }
            return false;
        }

        private static async Task<(byte[] Bytes, bool Truncated)> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
        {
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            var truncated = false;
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                var remaining = limit - (int)output.Length;
                if (count > remaining)
                    truncated = true;
                output.Write(buffer, 0, Math.Min(remaining, count));
            }
            return (output.ToArray(), truncated);
        }
    }
}
