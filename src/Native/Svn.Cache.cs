using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SourceGit.Native
{
    public static partial class Svn
    {
        private const int MetadataCacheVersion = 1;
        private const long MaxMetadataCacheBytes = 64L * 1024 * 1024;
        private static readonly TimeSpan s_metadataCacheLifetime = TimeSpan.FromMinutes(5);

        private sealed record MetadataCacheDocument(
            string Kind,
            string Xml,
            string HistoricalUrl = "",
            string RepositoryRoot = "");

        private static bool TryCreateMetadataCacheKey(
            string cacheDirectory,
            string kind,
            string executable,
            string configuredConfigDirectory,
            string effectiveConfigDirectory,
            string workingCopyPath,
            WorkingCopyResult workingCopy,
            Credentials credentials,
            string query,
            out string cacheRoot,
            out string cacheKey)
        {
            cacheRoot = string.Empty;
            cacheKey = string.Empty;
            if (string.IsNullOrWhiteSpace(cacheDirectory) || string.IsNullOrWhiteSpace(workingCopy.RepositoryUuid))
                return false;

            try
            {
                if (HasUnsafeLineBreak(cacheDirectory))
                    return false;

                cacheRoot = Path.GetFullPath(cacheDirectory);
                var fullWorkingCopyPath = Path.GetFullPath(workingCopyPath);
                var executableContext = GetFileContext(Path.GetFullPath(executable));
                var configContext = GetConfigContext(effectiveConfigDirectory);
                var authContext = credentials == null ? "native-auth" : "explicit-user:" + (credentials.Username ?? string.Empty);
                var identity = new StringBuilder("sourcegit-svn-cache-v1");
                AppendIdentity(identity, kind);
                AppendIdentity(identity, fullWorkingCopyPath);
                AppendIdentity(identity, workingCopy.WorkingCopyRoot);
                AppendIdentity(identity, workingCopy.RepositoryRoot);
                AppendIdentity(identity, workingCopy.RepositoryUuid);
                AppendIdentity(identity, workingCopy.Url);
                AppendIdentity(identity, workingCopy.Revision.ToString(CultureInfo.InvariantCulture));
                AppendIdentity(identity, executableContext);
                AppendIdentity(identity, string.IsNullOrWhiteSpace(configuredConfigDirectory) ? "native-config-mode" : "explicit-config-mode");
                AppendIdentity(identity, configContext);
                AppendIdentity(identity, authContext);
                AppendIdentity(identity, query);
                cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()))).ToLowerInvariant();
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                cacheRoot = string.Empty;
                cacheKey = string.Empty;
                return false;
            }
        }

        private static string GetFileContext(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return path + "|" + (info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) : "missing") + "|" +
                    (info.Exists ? info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : string.Empty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return path + "|unavailable";
            }
        }

        private static string GetConfigContext(string effectiveConfigDirectory)
        {
            var directory = effectiveConfigDirectory ?? string.Empty;
            return directory + "|config=" + GetFileContext(Path.Combine(directory, "config")) +
                "|servers=" + GetFileContext(Path.Combine(directory, "servers"));
        }

        private static void AppendIdentity(StringBuilder builder, string value)
        {
            value ??= string.Empty;
            builder.Append('|').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        }

        private static bool TryReadMetadataCache(
            string cacheRoot,
            string cacheKey,
            string kind,
            out MetadataCacheDocument document)
        {
            document = null;
            var path = GetMetadataCachePath(cacheRoot, cacheKey);
            try
            {
                if (Directory.Exists(cacheRoot))
                    TrySetPrivateDirectoryMode(cacheRoot);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0 || info.Length > MaxMetadataCacheBytes)
                    return false;

                var bytes = File.ReadAllBytes(path);
                if (bytes.Length <= 0 || bytes.LongLength > MaxMetadataCacheBytes)
                    return false;

                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("version", out var version) || version.GetInt32() != MetadataCacheVersion ||
                    !root.TryGetProperty("kind", out var cachedKind) || cachedKind.GetString() != kind ||
                    !root.TryGetProperty("expiresUtcTicks", out var expires) || !expires.TryGetInt64(out var expiresUtcTicks) ||
                    !root.TryGetProperty("xml", out var xml) || xml.ValueKind != JsonValueKind.String)
                {
                    TryDeleteMetadataCache(path);
                    return false;
                }

                var nowTicks = DateTimeOffset.UtcNow.Ticks;
                if (expiresUtcTicks <= nowTicks || expiresUtcTicks > nowTicks + s_metadataCacheLifetime.Ticks + TimeSpan.FromSeconds(30).Ticks)
                {
                    TryDeleteMetadataCache(path);
                    return false;
                }

                var historicalUrl = string.Empty;
                var repositoryRoot = string.Empty;
                if (kind == "changed-paths")
                {
                    if (!root.TryGetProperty("historicalUrl", out var url) || url.ValueKind != JsonValueKind.String ||
                        !root.TryGetProperty("repositoryRoot", out var repoRoot) || repoRoot.ValueKind != JsonValueKind.String)
                    {
                        TryDeleteMetadataCache(path);
                        return false;
                    }
                    historicalUrl = url.GetString();
                    repositoryRoot = repoRoot.GetString();
                }

                document = new MetadataCacheDocument(kind, xml.GetString(), historicalUrl, repositoryRoot);
                TrySetPrivateFileMode(path);
                return true;
            }
            catch (Exception)
            {
                TryDeleteMetadataCache(path);
                return false;
            }
        }

        private static void WriteMetadataCache(
            string cacheRoot,
            string cacheKey,
            MetadataCacheDocument document)
        {
            string tempPath = null;
            try
            {
                Directory.CreateDirectory(cacheRoot);
                TrySetPrivateDirectoryMode(cacheRoot);

                // Serialize pruning and replacement across processes. If another writer owns the
                // lock, this successful SVN result simply remains uncached.
                using var cacheLock = new FileStream(Path.Combine(cacheRoot, ".metadata-cache.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                TrySetPrivateFileMode(cacheLock.Name);

                byte[] bytes;
                using (var stream = new MemoryStream())
                {
                    using (var writer = new Utf8JsonWriter(stream))
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("version", MetadataCacheVersion);
                        writer.WriteString("kind", document.Kind);
                        writer.WriteNumber("expiresUtcTicks", DateTimeOffset.UtcNow.Add(s_metadataCacheLifetime).Ticks);
                        writer.WriteString("xml", document.Xml);
                        if (document.Kind == "changed-paths")
                        {
                            writer.WriteString("historicalUrl", document.HistoricalUrl);
                            writer.WriteString("repositoryRoot", document.RepositoryRoot);
                        }
                        writer.WriteEndObject();
                    }
                    bytes = stream.ToArray();
                }

                if (bytes.LongLength == 0 || bytes.LongLength > MaxMetadataCacheBytes / 2)
                    return;

                var path = GetMetadataCachePath(cacheRoot, cacheKey);
                TryDeleteMetadataCache(path);
                if (!PruneMetadataCache(cacheRoot, bytes.LongLength * 2))
                    return;

                tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    TrySetPrivateFileMode(tempPath);
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(flushToDisk: true);
                }

                File.Move(tempPath, path, overwrite: true);
                tempPath = null;
                TrySetPrivateFileMode(path);
            }
            catch (Exception)
            {
                // Metadata is an optimization. A full, unwritable, or otherwise broken cache
                // must never prevent the caller from using the successful SVN result.
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempPath))
                    TryDeleteMetadataCache(tempPath);
            }
        }

        private static void InvalidateMetadataCache(string cacheRoot, string cacheKey)
        {
            TryDeleteMetadataCache(GetMetadataCachePath(cacheRoot, cacheKey));
        }

        private static bool PruneMetadataCache(string cacheRoot, long bytesToReserve)
        {
            try
            {
                foreach (var tempPath in Directory.EnumerateFiles(cacheRoot, "*.svnmeta.json.*.tmp", SearchOption.TopDirectoryOnly))
                {
                    TryDeleteMetadataCache(tempPath);
                    if (File.Exists(tempPath))
                        return false;
                }

                var files = new System.Collections.Generic.List<FileInfo>();
                long totalBytes = 0;
                foreach (var path in Directory.EnumerateFiles(cacheRoot, "*.svnmeta.json", SearchOption.TopDirectoryOnly))
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length <= 0 || info.Length > MaxMetadataCacheBytes)
                    {
                        TryDeleteMetadataCache(path);
                        continue;
                    }
                    totalBytes += info.Length;
                    files.Add(info);
                }

                var remainingCount = files.Count;
                if (remainingCount < 1024 && totalBytes <= MaxMetadataCacheBytes - bytesToReserve)
                    return true;

                files.Sort((left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc));
                foreach (var info in files)
                {
                    TryDeleteMetadataCache(info.FullName);
                    if (!File.Exists(info.FullName))
                    {
                        totalBytes -= info.Length;
                        remainingCount--;
                    }
                    if (remainingCount < 1024 && totalBytes <= MaxMetadataCacheBytes - bytesToReserve)
                        return true;
                }

                return remainingCount < 1024 && totalBytes <= MaxMetadataCacheBytes - bytesToReserve;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string GetMetadataCachePath(string cacheRoot, string cacheKey)
        {
            return Path.Combine(cacheRoot, cacheKey + ".svnmeta.json");
        }

        private static void TryDeleteMetadataCache(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        private static void TrySetPrivateDirectoryMode(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch (Exception)
                {
                }
            }
        }

        private static void TrySetPrivateFileMode(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
