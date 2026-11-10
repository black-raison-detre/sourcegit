using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Native
{
    public static partial class Svn
    {
        private const long MaxRenameRepairFileBytes = 16 * 1024 * 1024;

        public sealed class RenameRepairSnapshot
        {
            public string SourceRelativePath { get; }
            public string DestinationRelativePath { get; }

            internal string Token { get; }
            internal string WorkingCopyRoot { get; }
            internal WorkingCopyResult SourceInfo { get; }
            internal SvnWorkingCopyEntry SourceEntry { get; }
            internal IReadOnlyList<SvnProperty> SourceProperties { get; }
            internal long DestinationLength { get; }
            internal string DestinationSha256 { get; }

            internal RenameRepairSnapshot(
                string sourceRelativePath,
                string destinationRelativePath,
                string token,
                string workingCopyRoot,
                WorkingCopyResult sourceInfo,
                SvnWorkingCopyEntry sourceEntry,
                IReadOnlyList<SvnProperty> sourceProperties,
                long destinationLength,
                string destinationSha256)
            {
                SourceRelativePath = sourceRelativePath;
                DestinationRelativePath = destinationRelativePath;
                Token = token;
                WorkingCopyRoot = workingCopyRoot;
                SourceInfo = sourceInfo;
                SourceEntry = sourceEntry;
                SourceProperties = sourceProperties;
                DestinationLength = destinationLength;
                DestinationSha256 = destinationSha256;
            }
        }

        public sealed record RenameRepairSnapshotResult(
            string Status,
            string Details = "",
            RenameRepairSnapshot Snapshot = null);

        /// <summary>
        /// Returns whether a missing regular file is a candidate for the explicit missing-file
        /// recovery actions. The action rechecks the working copy, ancestors, and file metadata.
        /// </summary>
        public static bool CanRepairMissingFile(SvnWorkingCopyEntry entry)
        {
            return entry != null && IsSafeActionEntry(entry) &&
                entry.Item == "missing" &&
                entry.Properties is "none" or "normal" or "modified" &&
                !entry.IsCopied && !entry.IsSwitched && !entry.IsTreeConflicted && !entry.IsFileExternal &&
                string.IsNullOrEmpty(entry.MovedFrom) && string.IsNullOrEmpty(entry.MovedTo) &&
                entry.Kind is "file" or "unknown";
        }

        /// <summary>
        /// Runs the standard, local svn cleanup operation on the exact verified working-copy root.
        /// It does not request break-locks, removal of unversioned/ignored files, vacuuming, or
        /// external cleanup. Close other Subversion clients before using this operation.
        /// </summary>
        public static async Task<WorkingCopyActionResult> CleanupAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            CancellationToken cancellationToken = default)
        {
            var mutationStarted = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var workingCopy = await GetWorkingCopyAsync(executable, configDirectory, workingCopyPath,
                    cancellationToken).ConfigureAwait(false);
                if (workingCopy.Status != "Ready")
                    return new WorkingCopyActionResult(workingCopy.Status, workingCopy.Details);
                if (!TryPrepareWorkingCopyRoot(workingCopy.WorkingCopyRoot, out var root, out var rootError))
                    return new WorkingCopyActionResult("InvalidPath", rootError);
                if (!TryIsExactSafeRoot(workingCopyPath, root, out var exactRootError))
                    return new WorkingCopyActionResult("InvalidPath", exactRootError);
                if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                    return new WorkingCopyActionResult("InvalidConfigDirectory", "The Subversion configuration directory is invalid.");

                // Recheck the WC identity immediately before the mutating command. Status is
                // intentionally not required here: it can fail while the WC is locked.
                var current = await GetWorkingCopyAsync(executable, configDirectory, root,
                    cancellationToken).ConfigureAwait(false);
                if (current.Status != "Ready")
                    return new WorkingCopyActionResult(current.Status, current.Details);
                if (!SameWorkingCopyIdentity(workingCopy, current) ||
                    !TryIsExactSafeRoot(root, current.WorkingCopyRoot, out _))
                    return new WorkingCopyActionResult("WorkingCopyChanged", "The working-copy root changed while cleanup was being prepared.");

                var arguments = new List<string> { "cleanup", "--non-interactive", "--no-auth-cache" };
                AppendConfigArgument(arguments, configDirectory, effectiveConfigDirectory);
                arguments.Add("--");
                arguments.Add(WithPegEscape(root));
                cancellationToken.ThrowIfCancellationRequested();
                mutationStarted = true;
                var command = await RunFileCommandAsync(executable, arguments, null, MaxOperationOutputBytes,
                    cancellationToken, s_operationTimeout, killOnOutputLimit: false).ConfigureAwait(false);
                if (command.Status != "Ready")
                {
                    return new WorkingCopyActionResult(command.Status,
                        AppendPartialOperationNotice(command.StandardError, "cleanup"));
                }
                if (command.OutputTruncated)
                    return new WorkingCopyActionResult("TooLarge",
                        AppendPartialOperationNotice("SVN cleanup output exceeded the 128 KiB limit.", "cleanup"));
                if (command.ExitCode != 0)
                    return new WorkingCopyActionResult("Failed",
                        AppendPartialOperationNotice(FormatFileCommandFailure(command, null), "cleanup"));
                return new WorkingCopyActionResult("Ready");
            }
            catch (OperationCanceledException)
            {
                return new WorkingCopyActionResult("Cancelled", mutationStarted
                    ? "SVN cleanup was cancelled after it started; refresh working-copy status before retrying."
                    : "SVN cleanup was cancelled before it started.");
            }
        }

        /// <summary>
        /// Restores selected missing regular files from their working-copy BASE state. A local
        /// property edit on a selected file is reverted together with its missing text state.
        /// </summary>
        public static Task<WorkingCopyActionResult> RestoreMissingFilesAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            IReadOnlyList<string> relativePaths,
            CancellationToken cancellationToken = default)
        {
            return RunMissingFilesActionAsync(executable, configDirectory, workingCopyPath,
                relativePaths, restore: true, cancellationToken);
        }

        /// <summary>
        /// Schedules selected missing regular files for deletion while retaining their working-copy
        /// paths. The operation never removes local filesystem content.
        /// </summary>
        public static Task<WorkingCopyActionResult> RemoveMissingFilesAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            IReadOnlyList<string> relativePaths,
            CancellationToken cancellationToken = default)
        {
            return RunMissingFilesActionAsync(executable, configDirectory, workingCopyPath,
                relativePaths, restore: false, cancellationToken);
        }

        /// <summary>
        /// Returns a fresh, file-only preview for repairing a rename recorded as a missing source
        /// and an unversioned destination. The destination contents are bound by size and SHA-256.
        /// </summary>
        public static async Task<RenameRepairSnapshotResult> GetRenameRepairSnapshotAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string sourceRelativePath,
            string destinationRelativePath,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await ReadRenameRepairSnapshotAsync(executable, configDirectory, workingCopyPath,
                    sourceRelativePath, destinationRelativePath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new RenameRepairSnapshotResult("Cancelled", "The rename repair preview was cancelled.");
            }
        }

        /// <summary>
        /// Repairs a file rename after matching a current preview. It temporarily protects the
        /// unversioned destination, restores the missing BASE file, schedules a normal SVN move,
        /// then restores the original destination bytes without overwriting an unexpected file.
        /// </summary>
        public static async Task<WorkingCopyActionResult> RepairRenameAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            RenameRepairSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            if (snapshot == null)
                return new WorkingCopyActionResult("InvalidArgument", "Refresh the rename repair preview before continuing.");

            var fresh = await GetRenameRepairSnapshotAsync(executable, configDirectory, workingCopyPath,
                snapshot.SourceRelativePath, snapshot.DestinationRelativePath, cancellationToken).ConfigureAwait(false);
            if (fresh.Status != "Ready")
                return new WorkingCopyActionResult(fresh.Status, fresh.Details);
            if (!string.Equals(snapshot.Token, fresh.Snapshot.Token, StringComparison.Ordinal))
                return new WorkingCopyActionResult("StaleSelection",
                    "The working copy or destination contents changed after the preview. Refresh the preview before retrying.");

            return await ExecuteRenameRepairAsync(executable, configDirectory, workingCopyPath,
                fresh.Snapshot, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Returns whether a missing file is a candidate for history-preserving rename repair.</summary>
        public static bool CanPrepareRenameRepair(SvnWorkingCopyEntry entry)
        {
            return CanRepairMissingFile(entry) && entry.Properties is "none" or "normal";
        }

        private sealed record PreparedRenameRepair(
            string Root,
            string Source,
            string Destination,
            SvnWorkingCopyEntry SourceEntry,
            SvnWorkingCopyEntry DestinationEntry,
            WorkingCopyResult SourceInfo,
            IReadOnlyList<SvnProperty> SourceProperties,
            long DestinationLength,
            string DestinationSha256);

        private static async Task<RenameRepairSnapshotResult> ReadRenameRepairSnapshotAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string sourceRelativePath,
            string destinationRelativePath,
            CancellationToken cancellationToken)
        {
            var state = await PrepareRenameRepairAsync(executable, configDirectory, workingCopyPath,
                sourceRelativePath, destinationRelativePath, cancellationToken).ConfigureAwait(false);
            if (state.Status != "Ready")
                return new RenameRepairSnapshotResult(state.Status, state.Details);

            var prepared = state.Prepared;
            string token;
            try
            {
                var tokenPayload = BuildRenameRepairTokenPayload(prepared);
                var tokenBytes = new UTF8Encoding(false, true).GetBytes(tokenPayload);
                token = Convert.ToHexString(SHA256.HashData(tokenBytes));
            }
            catch (System.Text.EncoderFallbackException ex)
            {
                return new RenameRepairSnapshotResult("InvalidPath", $"The selected paths contain invalid Unicode: {ex.Message}");
            }
            return new RenameRepairSnapshotResult("Ready", Snapshot: new RenameRepairSnapshot(
                sourceRelativePath: Path.GetRelativePath(prepared.Root, prepared.Source),
                destinationRelativePath: Path.GetRelativePath(prepared.Root, prepared.Destination),
                token,
                prepared.Root,
                prepared.SourceInfo,
                prepared.SourceEntry,
                prepared.SourceProperties,
                prepared.DestinationLength,
                prepared.DestinationSha256));
        }

        private static string BuildRenameRepairTokenPayload(PreparedRenameRepair prepared)
        {
            var builder = new StringBuilder(2048);
            AppendRenameTokenField(builder, prepared.Root);
            AppendRenameTokenField(builder, Path.GetRelativePath(prepared.Root, prepared.Source));
            AppendRenameTokenField(builder, Path.GetRelativePath(prepared.Root, prepared.Destination));
            AppendRenameTokenEntry(builder, prepared.SourceEntry);
            AppendRenameTokenEntry(builder, prepared.DestinationEntry);
            AppendRenameTokenField(builder, prepared.SourceInfo.Status);
            AppendRenameTokenField(builder, prepared.SourceInfo.WorkingCopyRoot);
            AppendRenameTokenField(builder, prepared.SourceInfo.Url);
            AppendRenameTokenField(builder, prepared.SourceInfo.RepositoryRoot);
            AppendRenameTokenField(builder, prepared.SourceInfo.RepositoryUuid);
            AppendRenameTokenField(builder, prepared.SourceInfo.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendRenameTokenField(builder, prepared.SourceInfo.Details);
            AppendRenameTokenField(builder, prepared.SourceProperties.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var property in prepared.SourceProperties)
            {
                AppendRenameTokenField(builder, property.Name);
                AppendRenameTokenField(builder, property.Value);
                AppendRenameTokenField(builder, property.IsBinary);
            }
            AppendRenameTokenField(builder, prepared.DestinationLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendRenameTokenField(builder, prepared.DestinationSha256);
            return builder.ToString();
        }

        private static void AppendRenameTokenEntry(StringBuilder builder, SvnWorkingCopyEntry entry)
        {
            AppendRenameTokenField(builder, entry.Path);
            AppendRenameTokenField(builder, entry.Item);
            AppendRenameTokenField(builder, entry.Properties);
            AppendRenameTokenField(builder, entry.Revision?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendRenameTokenField(builder, entry.IsCopied);
            AppendRenameTokenField(builder, entry.IsSwitched);
            AppendRenameTokenField(builder, entry.IsTreeConflicted);
            AppendRenameTokenField(builder, entry.IsFileExternal);
            AppendRenameTokenField(builder, entry.MovedFrom);
            AppendRenameTokenField(builder, entry.MovedTo);
            AppendRenameTokenField(builder, entry.Changelist);
            AppendRenameTokenField(builder, entry.Kind);
        }

        private static void AppendRenameTokenField(StringBuilder builder, string value)
        {
            if (value == null)
            {
                builder.Append("-1:");
                return;
            }

            builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(':')
                .Append(value);
        }

        private static void AppendRenameTokenField(StringBuilder builder, bool value)
        {
            AppendRenameTokenField(builder, value ? "1" : "0");
        }

        private sealed record RenamePreparationResult(string Status, PreparedRenameRepair Prepared = null, string Details = "");

        private static async Task<RenamePreparationResult> PrepareRenameRepairAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            string sourceRelativePath,
            string destinationRelativePath,
            CancellationToken cancellationToken)
        {
            var resolved = await GetStatusCoreAsync(executable, configDirectory, workingCopyPath,
                cancellationToken, includeExternals: true).ConfigureAwait(false);
            if (resolved.Result.Status != "Ready")
                return new RenamePreparationResult(resolved.Result.Status, Details: resolved.Result.Details);
            if (!TryPrepareWorkingCopyRoot(resolved.WorkingCopyRoot, out var root, out var rootError))
                return new RenamePreparationResult("InvalidPath", Details: rootError);
            if (!TryValidateCleanupRoot(root, out var rootSafetyError))
                return new RenamePreparationResult("UnsafePath", Details: rootSafetyError);
            if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                return new RenamePreparationResult("InvalidConfigDirectory", Details: "The Subversion configuration directory is invalid.");
            if (!TryResolvePathOperationPath(root, sourceRelativePath, out var sourceRelative, out var source, out var sourcePathError))
                return new RenamePreparationResult("InvalidPath", Details: sourcePathError);
            if (!TryResolvePathOperationPath(root, destinationRelativePath, out var destinationRelative, out var destination, out var destinationPathError))
                return new RenamePreparationResult("InvalidPath", Details: destinationPathError);
            if (sourceRelative.Equals(destinationRelative, StringComparison.OrdinalIgnoreCase))
                return new RenamePreparationResult("NotEligible", Details: "A rename repair cannot use the same path or a case-only path pair.");

            var sourceEntry = resolved.Result.Entries?.FirstOrDefault(entry => PathEquals(entry.Path, sourceRelative));
            var destinationEntry = resolved.Result.Entries?.FirstOrDefault(entry => PathEquals(entry.Path, destinationRelative));
            if (!CanPrepareRenameRepair(sourceEntry))
                return new RenamePreparationResult("NotEligible", Details: "The source must be a missing, clean-property regular file without a schedule, switch, conflict, external, or move.");
            if (destinationEntry == null || !IsSafeActionEntry(destinationEntry) || destinationEntry.Item != "unversioned" ||
                destinationEntry.Kind != "file" || destinationEntry.Properties is not ("none" or "normal") ||
                destinationEntry.IsCopied || destinationEntry.IsSwitched || destinationEntry.IsTreeConflicted || destinationEntry.IsFileExternal ||
                !string.IsNullOrEmpty(destinationEntry.MovedFrom) || !string.IsNullOrEmpty(destinationEntry.MovedTo))
                return new RenamePreparationResult("NotEligible", Details: "The destination must be an existing unversioned regular file in the same working copy.");
            if (HasUnsafePathOperationAncestor(sourceRelative, resolved.Result.Entries) ||
                HasUnsafePathOperationAncestor(destinationRelative, resolved.Result.Entries))
                return new RenamePreparationResult("NotEligible", Details: "The source or destination is beneath a switched, external, scheduled, copied, moved, missing, replaced, or conflicted path.");
            if (!TryValidateActionPath(root, source, sourceEntry, add: false, cancellationToken, out var sourceSafetyError) ||
                GetLocalPathState(source, out _, out _) != LocalPathState.Missing)
                return new RenamePreparationResult("UnsafePath", Details: sourceSafetyError);
            if (!TryValidatePathOperationPath(root, destination, allowMissingFinal: false,
                    cancellationToken, out var destinationState, out var destinationSafetyError) ||
                destinationState != LocalPathState.File)
                return new RenamePreparationResult("UnsafePath", Details: destinationSafetyError);

            var sourceInfo = await GetPathInfoAsync(executable, configDirectory, effectiveConfigDirectory,
                root, source, cancellationToken).ConfigureAwait(false);
            if (sourceInfo.Status != "Ready")
                return new RenamePreparationResult(sourceInfo.Status, Details: sourceInfo.Details);
            if (!sourceInfo.IsVersioned || sourceInfo.Kind != "file" || !PathEquals(sourceInfo.WorkingCopyRoot, root))
                return new RenamePreparationResult("NotEligible", Details: "SVN metadata does not confirm that the missing source is a regular file in this working copy.");

            var sourceIdentity = await GetWorkingCopyMetadataAtPathAsync(executable, configDirectory,
                effectiveConfigDirectory, source,
                cancellationToken).ConfigureAwait(false);
            if (sourceIdentity.Status != "Ready")
                return new RenamePreparationResult(sourceIdentity.Status, Details: sourceIdentity.Details);
            var baseIdentity = await GetWorkingCopyAsync(executable, configDirectory, root,
                cancellationToken).ConfigureAwait(false);
            if (baseIdentity.Status != "Ready")
                return new RenamePreparationResult(baseIdentity.Status, Details: baseIdentity.Details);
            if (!PathEquals(sourceIdentity.WorkingCopyRoot, root) || !SameRepositoryIdentity(baseIdentity, sourceIdentity))
                return new RenamePreparationResult("NotEligible", Details: "The source is switched or belongs to a different working-copy or repository identity.");

            var propertyResult = await ReadLocalPropertiesAsync(executable, configDirectory,
                effectiveConfigDirectory, source, cancellationToken).ConfigureAwait(false);
            if (propertyResult.Status != "Ready")
                return new RenamePreparationResult(propertyResult.Status, Details: propertyResult.Details);
            if (propertyResult.Properties.Any(property => property.Name.Equals("svn:special", StringComparison.Ordinal)))
                return new RenamePreparationResult("NotEligible", Details: "Symbolic-link working-copy nodes cannot be repaired by this file-only operation.");

            if (!TryHashRenameRepairFile(destination, out var destinationLength, out var destinationSha256, out var hashError,
                    cancellationToken))
                return new RenamePreparationResult(hashError.StartsWith("The destination exceeds", StringComparison.Ordinal)
                    ? "TooLarge" : "UnsafePath", Details: hashError);

            return new RenamePreparationResult("Ready", new PreparedRenameRepair(root, source, destination,
                sourceEntry, destinationEntry, sourceIdentity, propertyResult.Properties,
                destinationLength, destinationSha256));
        }

        private static bool SameRepositoryIdentity(WorkingCopyResult expected, WorkingCopyResult actual)
        {
            return expected != null && actual != null &&
                !string.IsNullOrWhiteSpace(expected.RepositoryUuid) &&
                string.Equals(expected.RepositoryRoot, actual.RepositoryRoot, StringComparison.Ordinal) &&
                string.Equals(expected.RepositoryUuid, actual.RepositoryUuid, StringComparison.Ordinal);
        }

        private static async Task<WorkingCopyResult> GetWorkingCopyMetadataAtPathAsync(
            string executable,
            string configDirectory,
            string effectiveConfigDirectory,
            string target,
            CancellationToken cancellationToken)
        {
            var arguments = new List<string> { "info", "--xml", "--depth", "empty", "--non-interactive" };
            AppendConfigArgument(arguments, configDirectory, effectiveConfigDirectory);
            arguments.Add("--no-auth-cache");
            arguments.Add("--");
            arguments.Add(WithPegEscape(target));
            var command = await RunFileCommandAsync(executable, arguments, null, MaxFileMetadataBytes,
                cancellationToken).ConfigureAwait(false);
            if (command.Status != "Ready")
                return new WorkingCopyResult(command.Status, Details: command.StandardError);
            if (command.OutputTruncated)
                return new WorkingCopyResult("TooLarge", Details: "SVN info XML exceeded the metadata output limit.");
            if (command.ExitCode != 0)
                return new WorkingCopyResult("NotVersioned", Details: FormatFileCommandFailure(command, null));

            try
            {
                var info = ParseWorkingCopyInfo(DecodeXml(command.StandardOutput));
                var urlStatus = ValidateUrl(info.Url, out _, out _);
                var repositoryStatus = ValidateUrl(info.RepositoryRoot, out _, out _);
                if (urlStatus != null || repositoryStatus != null ||
                    !Path.IsPathFullyQualified(info.WorkingCopyRoot) ||
                    !long.TryParse(info.Revision, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var revision))
                    return new WorkingCopyResult("Failed", Details: "SVN returned invalid or incomplete working-copy path metadata.");
                return new WorkingCopyResult("Ready", Path.GetFullPath(info.WorkingCopyRoot), info.Url,
                    info.RepositoryRoot, revision, RepositoryUuid: info.RepositoryUuid);
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or System.Text.DecoderFallbackException or ArgumentException or IOException)
            {
                return new WorkingCopyResult("Failed", Details: $"SVN returned malformed working-copy path metadata: {ex.Message}");
            }
        }

        private static bool TryHashRenameRepairFile(
            string path,
            out long length,
            out string sha256,
            out string error,
            CancellationToken cancellationToken = default)
        {
            length = 0;
            sha256 = string.Empty;
            error = "The destination must be a regular, seekable file that can be safely inspected.";
            try
            {
                var state = GetLocalPathState(path, out var attributes, out var stateError);
                if (state != LocalPathState.File || (attributes & FileAttributes.Device) != 0)
                {
                    error = state == LocalPathState.Error ? stateError : "The destination is not a regular file.";
                    return false;
                }
                using var stream = OpenBoundedLocalFileNoHang(path);
                if (!stream.CanSeek)
                    return false;
                length = stream.Length;
                if (length > MaxRenameRepairFileBytes)
                {
                    error = $"The destination exceeds the {MaxRenameRepairFileBytes / (1024 * 1024)} MiB rename-repair limit.";
                    return false;
                }
                if (!TryComputeBoundedRenameHash(stream, length, cancellationToken, out var firstHash) || stream.Length != length)
                {
                    error = "The destination changed while its contents were being inspected.";
                    return false;
                }
                stream.Position = 0;
                if (!TryComputeBoundedRenameHash(stream, length, cancellationToken, out var secondHash) || stream.Length != length)
                {
                    error = "The destination changed while its contents were being inspected.";
                    return false;
                }
                if (!string.Equals(firstHash, secondHash, StringComparison.Ordinal))
                {
                    error = "The destination changed while its contents were being inspected.";
                    return false;
                }
                sha256 = secondHash;
                error = string.Empty;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = $"The destination could not be read safely: {ex.Message}";
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        private static bool TryComputeBoundedRenameHash(
            Stream stream,
            long expectedLength,
            CancellationToken cancellationToken,
            out string sha256)
        {
            sha256 = string.Empty;
            if (!stream.CanSeek || expectedLength < 0 || expectedLength > MaxRenameRepairFileBytes || stream.Length != expectedLength)
                return false;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (total <= expectedLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var allowed = (int)Math.Min(buffer.Length, expectedLength - total + 1);
                var read = stream.Read(buffer, 0, allowed);
                if (read == 0)
                    break;
                if (total + read > expectedLength)
                    return false;
                hash.AppendData(buffer, 0, read);
                total += read;
            }
            if (total != expectedLength || stream.ReadByte() != -1 || stream.Length != expectedLength)
                return false;
            sha256 = Convert.ToHexString(hash.GetHashAndReset());
            return true;
        }

        private static async Task<WorkingCopyActionResult> ExecuteRenameRepairAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            RenameRepairSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            var root = snapshot.WorkingCopyRoot;
            var source = Path.GetFullPath(snapshot.SourceRelativePath, root);
            var destination = Path.GetFullPath(snapshot.DestinationRelativePath, root);
            var destinationParent = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(destinationParent))
                return new WorkingCopyActionResult("InvalidPath", "The destination parent directory is invalid.");

            var backupDirectory = string.Empty;
            var originalBackup = string.Empty;
            var generatedBackup = string.Empty;
            var originalWasBackedUp = false;
            var mutationStarted = false;
            var result = new WorkingCopyActionResult("Failed", "The rename repair did not complete.");
            var recoveryNotes = new List<string>();
            var operationSucceeded = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var configValid = TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory);
                if (!configValid)
                    return new WorkingCopyActionResult("InvalidConfigDirectory", "The Subversion configuration directory is invalid.");

                // Create a protected same-filesystem location, then re-read the source and
                // destination immediately before moving either file.
                backupDirectory = CreateRenameRepairBackupDirectory(destinationParent);
                originalBackup = Path.Combine(backupDirectory, "original-destination");
                generatedBackup = Path.Combine(backupDirectory, "svn-move-destination");
                var beforeMutation = await GetRenameRepairSnapshotAsync(executable, configDirectory, workingCopyPath,
                    snapshot.SourceRelativePath, snapshot.DestinationRelativePath, cancellationToken).ConfigureAwait(false);
                if (beforeMutation.Status != "Ready")
                    throw new RenameRepairAbortException(beforeMutation.Status, beforeMutation.Details);
                if (!string.Equals(snapshot.Token, beforeMutation.Snapshot.Token, StringComparison.Ordinal))
                    throw new RenameRepairAbortException("StaleSelection",
                        "The working copy or destination contents changed after the preview. Refresh the preview before retrying.");
                if (!TryHashRenameRepairFile(destination, out var currentLength, out var currentHash, out var hashError, cancellationToken) ||
                    currentLength != snapshot.DestinationLength || !string.Equals(currentHash, snapshot.DestinationSha256, StringComparison.Ordinal))
                    throw new RenameRepairAbortException("StaleSelection",
                        string.IsNullOrEmpty(hashError) ? "The destination changed after the preview." : hashError);

                // Read pristine BASE independently of the mutable working file. Default cat
                // translation includes the EOL style and keyword expansion used by revert.
                var baseArguments = new List<string> { "cat", "--revision", "BASE", "--non-interactive", "--no-auth-cache" };
                AppendConfigArgument(baseArguments, configDirectory, effectiveConfigDirectory);
                baseArguments.Add("--");
                baseArguments.Add(WithPegEscape(source));
                var baseContent = await RunFileCommandAsync(executable, baseArguments, null, (int)MaxRenameRepairFileBytes,
                    cancellationToken, s_operationTimeout).ConfigureAwait(false);
                if (baseContent.Status != "Ready")
                    throw new RenameRepairAbortException(baseContent.Status, FormatPartialRenameCommandFailure(baseContent, "cat BASE"));
                if (baseContent.OutputTruncated || baseContent.ExitCode != 0)
                    throw new RenameRepairAbortException(baseContent.OutputTruncated ? "TooLarge" : "Failed",
                        FormatPartialRenameCommandFailure(baseContent, "cat BASE"));
                var sourceLength = baseContent.StandardOutput.LongLength;
                var sourceHash = Convert.ToHexString(SHA256.HashData(baseContent.StandardOutput));

                File.Move(destination, originalBackup);
                originalWasBackedUp = true;
                mutationStarted = true;
                if (!TryHashRenameRepairFile(originalBackup, out var backupLength, out var backupHash, out var backupError,
                        CancellationToken.None) ||
                    backupLength != snapshot.DestinationLength || !string.Equals(backupHash, snapshot.DestinationSha256, StringComparison.Ordinal))
                    throw new RenameRepairAbortException("Failed", string.IsNullOrEmpty(backupError)
                        ? "The protected destination backup did not match the preview." : backupError);

                var revertArguments = new List<string> { "revert", "--depth", "empty", "--non-interactive", "--no-auth-cache" };
                AppendConfigArgument(revertArguments, configDirectory, effectiveConfigDirectory);
                revertArguments.Add("--");
                revertArguments.Add(WithPegEscape(source));
                var revert = await RunFileCommandAsync(executable, revertArguments, null, MaxOperationOutputBytes,
                    cancellationToken, s_operationTimeout, killOnOutputLimit: false).ConfigureAwait(false);
                if (revert.Status != "Ready")
                    throw new RenameRepairAbortException(revert.Status, FormatPartialRenameCommandFailure(revert, "revert"));
                if (revert.OutputTruncated || revert.ExitCode != 0)
                    throw new RenameRepairAbortException(revert.OutputTruncated ? "TooLarge" : "Failed",
                        FormatPartialRenameCommandFailure(revert, "revert"));

                // A concurrent source write must remain in place rather than become output
                // that is subsequently deleted as disposable SVN-created content.
                if (!TryHashRenameRepairFile(source, out var restoredSourceLength, out var restoredSourceHash,
                        out var sourceHashError, cancellationToken))
                    throw new RenameRepairAbortException("UnsafePath", sourceHashError);
                if (restoredSourceLength != sourceLength || !string.Equals(restoredSourceHash, sourceHash, StringComparison.Ordinal))
                    throw new RenameRepairAbortException("PostconditionFailed",
                        $"The restored source did not match SVN BASE content and was preserved at {source}. Review its contents before retrying.");

                var moveArguments = new List<string> { "move", "--non-interactive", "--no-auth-cache" };
                AppendConfigArgument(moveArguments, configDirectory, effectiveConfigDirectory);
                moveArguments.Add("--");
                moveArguments.Add(WithPegEscape(source));
                // svn move destinations are literal operands; do not append a peg-escape @.
                moveArguments.Add(destination);
                var move = await RunFileCommandAsync(executable, moveArguments, null, MaxOperationOutputBytes,
                    cancellationToken, s_operationTimeout, killOnOutputLimit: false).ConfigureAwait(false);
                if (move.Status != "Ready")
                    throw new RenameRepairAbortException(move.Status, FormatPartialRenameCommandFailure(move, "move"));
                if (move.OutputTruncated || move.ExitCode != 0)
                    throw new RenameRepairAbortException(move.OutputTruncated ? "TooLarge" : "Failed",
                        FormatPartialRenameCommandFailure(move, "move"));

                if (!TryHashRenameRepairFile(destination, out var generatedLength, out var generatedHash, out var generatedHashError, cancellationToken))
                    throw new RenameRepairAbortException("UnsafePath", generatedHashError);
                if (generatedLength != sourceLength || !string.Equals(generatedHash, sourceHash, StringComparison.Ordinal))
                    throw new RenameRepairAbortException("PostconditionFailed",
                        "The destination bytes after svn move did not match the restored source file. The original destination remains protected.");
                var movedStatus = await GetStatusCoreAsync(executable, configDirectory, root,
                    cancellationToken, includeExternals: true).ConfigureAwait(false);
                if (movedStatus.Result.Status != "Ready" || !PathEquals(movedStatus.WorkingCopyRoot, root))
                    throw new RenameRepairAbortException(movedStatus.Result.Status == "Ready" ? "WorkingCopyChanged" : movedStatus.Result.Status,
                        movedStatus.Result.Details);
                if (!HasRenameMovePair(snapshot, movedStatus.Result.Entries, out var pairError))
                    throw new RenameRepairAbortException("PostconditionFailed", pairError);
                if (!await HasExpectedMovedPropertiesAsync(executable, configDirectory, effectiveConfigDirectory,
                        root, destination, snapshot.SourceProperties, cancellationToken).ConfigureAwait(false))
                    throw new RenameRepairAbortException("PostconditionFailed", "The moved file properties did not match the source snapshot.");

                // Move the verified SVN-created file out of the way, then restore the original
                // unversioned file with a no-overwrite rename. Any unexpected collision preserves
                // both protected files and reports their exact recovery location.
                if (!TryValidatePathOperationPath(root, destination, allowMissingFinal: false,
                        cancellationToken, out var generatedState, out var generatedPathError) || generatedState != LocalPathState.File)
                    throw new RenameRepairAbortException("UnsafePath", generatedPathError);
                File.Move(destination, generatedBackup);
                if (!TryHashRenameRepairFile(generatedBackup, out var heldLength, out var heldHash, out var heldError, cancellationToken) ||
                    heldLength != sourceLength || !string.Equals(heldHash, sourceHash, StringComparison.Ordinal))
                {
                    RestoreOriginalDestinationIfAbsent(destination, originalBackup, snapshot, recoveryNotes);
                    throw new RenameRepairAbortException("PostconditionFailed", string.IsNullOrEmpty(heldError)
                        ? "The SVN-created destination changed while it was being protected." : heldError);
                }
                try
                {
                    File.Move(originalBackup, destination);
                    originalWasBackedUp = false;
                }
                catch (IOException ex)
                {
                    throw new RenameRepairAbortException("RecoveryRequired",
                        $"The original destination is protected at {originalBackup}; a file appeared at the destination before it could be restored. {ex.Message}");
                }

                if (!TryHashRenameRepairFile(destination, out var restoredLength, out var restoredHash, out var restoredError, cancellationToken) ||
                    restoredLength != snapshot.DestinationLength || !string.Equals(restoredHash, snapshot.DestinationSha256, StringComparison.Ordinal))
                    throw new RenameRepairAbortException("PostconditionFailed", string.IsNullOrEmpty(restoredError)
                        ? "The restored destination did not match its original bytes." : restoredError);

                var finalStatus = await GetStatusCoreAsync(executable, configDirectory, root,
                    cancellationToken, includeExternals: true).ConfigureAwait(false);
                if (finalStatus.Result.Status != "Ready" || !PathEquals(finalStatus.WorkingCopyRoot, root))
                    throw new RenameRepairAbortException(finalStatus.Result.Status == "Ready" ? "WorkingCopyChanged" : finalStatus.Result.Status,
                        finalStatus.Result.Details);
                if (!HasRenameMovePair(snapshot, finalStatus.Result.Entries, out pairError))
                    throw new RenameRepairAbortException("PostconditionFailed", pairError);
                if (!await HasExpectedMovedPropertiesAsync(executable, configDirectory, effectiveConfigDirectory,
                        root, destination, snapshot.SourceProperties, cancellationToken).ConfigureAwait(false))
                    throw new RenameRepairAbortException("PostconditionFailed", "The moved file properties did not match the source snapshot.");
                if (!HasExpectedDestinationChangelist(snapshot, finalStatus.Result.Entries))
                    throw new RenameRepairAbortException("PostconditionFailed", "The moved file changelist did not match the source snapshot.");

                if (!TryDeleteVerifiedGeneratedBackup(backupDirectory, generatedBackup,
                        generatedLength, generatedHash, out var cleanupError))
                    throw new RenameRepairAbortException("RecoveryRequired", cleanupError);
                operationSucceeded = true;
                result = new WorkingCopyActionResult("Ready");
            }
            catch (RenameRepairAbortException ex)
            {
                result = new WorkingCopyActionResult(ex.Status, ex.Message);
            }
            catch (OperationCanceledException)
            {
                result = new WorkingCopyActionResult("Cancelled",
                    mutationStarted
                        ? "The rename repair was cancelled after it started; refresh status and check the protected recovery path before retrying."
                        : "The rename repair was cancelled before it changed the working copy.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                result = new WorkingCopyActionResult("Failed", $"The rename repair failed: {ex.Message}");
            }
            finally
            {
                if (!operationSucceeded && originalWasBackedUp)
                    RestoreOriginalDestinationIfAbsent(destination, originalBackup, snapshot, recoveryNotes);
                if (!operationSucceeded && !string.IsNullOrEmpty(backupDirectory))
                    PreserveOrDeleteEmptyBackupDirectory(backupDirectory, recoveryNotes);
            }

            if (recoveryNotes.Count > 0)
            {
                result = result with
                {
                    Details = string.IsNullOrWhiteSpace(result.Details)
                        ? string.Join(Environment.NewLine, recoveryNotes)
                        : result.Details + Environment.NewLine + string.Join(Environment.NewLine, recoveryNotes),
                };
            }
            return result;
        }

        private sealed class RenameRepairAbortException : Exception
        {
            public string Status { get; }

            public RenameRepairAbortException(string status, string message)
                : base(string.IsNullOrWhiteSpace(message) ? "The rename repair could not be completed." : message)
            {
                Status = status;
            }
        }

        private static bool HasRenameMovePair(
            RenameRepairSnapshot snapshot,
            IReadOnlyList<SvnWorkingCopyEntry> entries,
            out string error)
        {
            error = "SVN did not record the expected reciprocal file move. Refresh status and preserve the recovery files before retrying.";
            var source = entries?.FirstOrDefault(entry => PathEquals(entry.Path, snapshot.SourceRelativePath));
            var destination = entries?.FirstOrDefault(entry => PathEquals(entry.Path, snapshot.DestinationRelativePath));
            return source != null && destination != null &&
                source.Item == "deleted" && PathEquals(source.MovedTo, snapshot.DestinationRelativePath) &&
                destination.Item is "added" or "modified" && PathEquals(destination.MovedFrom, snapshot.SourceRelativePath) &&
                destination.Kind == "file" && destination.IsCopied && !destination.IsSwitched &&
                !destination.IsTreeConflicted && !destination.IsFileExternal;
        }

        private static bool HasExpectedDestinationChangelist(
            RenameRepairSnapshot snapshot,
            IReadOnlyList<SvnWorkingCopyEntry> entries)
        {
            var destination = entries?.FirstOrDefault(entry => PathEquals(entry.Path, snapshot.DestinationRelativePath));
            return destination != null && string.Equals(snapshot.SourceEntry.Changelist, destination.Changelist, StringComparison.Ordinal);
        }

        private static async Task<bool> HasExpectedMovedPropertiesAsync(
            string executable,
            string configDirectory,
            string effectiveConfigDirectory,
            string root,
            string destination,
            IReadOnlyList<SvnProperty> expected,
            CancellationToken cancellationToken)
        {
            var actual = await ReadLocalPropertiesAsync(executable, configDirectory,
                effectiveConfigDirectory, destination, cancellationToken).ConfigureAwait(false);
            return actual.Status == "Ready" && actual.Properties.SequenceEqual(expected);
        }

        private static string CreateRenameRepairBackupDirectory(string parent)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var path = Path.Combine(parent, ".sourcegit-svn-repair-" + Guid.NewGuid().ToString("N"));
                if (Directory.Exists(path) || File.Exists(path))
                    continue;
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(path);
                else
                    Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return path;
            }
            throw new IOException("Could not create a unique protected rename-repair directory.");
        }

        private static void RestoreOriginalDestinationIfAbsent(
            string destination,
            string originalBackup,
            RenameRepairSnapshot snapshot,
            List<string> recoveryNotes)
        {
            if (string.IsNullOrEmpty(originalBackup) || !File.Exists(originalBackup))
                return;
            if (!TryHashRenameRepairFile(originalBackup, out var backupLength, out var backupHash, out var backupError,
                    CancellationToken.None) ||
                backupLength != snapshot.DestinationLength || !string.Equals(backupHash, snapshot.DestinationSha256, StringComparison.Ordinal))
            {
                recoveryNotes.Add($"The original destination backup was preserved at {originalBackup}; it could not be verified: {backupError}");
                return;
            }
            if (GetLocalPathState(destination, out _, out _) != LocalPathState.Missing)
            {
                recoveryNotes.Add($"The original destination bytes are preserved at {originalBackup}; the destination is occupied, so it was not overwritten.");
                return;
            }
            try
            {
                File.Move(originalBackup, destination);
                if (!TryHashRenameRepairFile(destination, out var restoredLength, out var restoredHash, out var restoredError,
                        CancellationToken.None) ||
                    restoredLength != snapshot.DestinationLength || !string.Equals(restoredHash, snapshot.DestinationSha256, StringComparison.Ordinal))
                    recoveryNotes.Add($"The original destination was moved back to {destination}, but byte verification failed: {restoredError}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                recoveryNotes.Add($"The original destination bytes remain protected at {originalBackup}; restoration failed: {ex.Message}");
            }
        }

        private static bool TryDeleteVerifiedGeneratedBackup(
            string backupDirectory,
            string generatedBackup,
            long expectedLength,
            string expectedHash,
            out string error)
        {
            error = string.Empty;
            if (!File.Exists(generatedBackup) ||
                !TryHashRenameRepairFile(generatedBackup, out var length, out var hash, out error,
                    CancellationToken.None) ||
                length != expectedLength || !string.Equals(hash, expectedHash, StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(error))
                    error = $"The verified SVN move file was retained at {generatedBackup}; its contents changed during recovery.";
                else
                    error = $"The verified SVN move file was retained at {generatedBackup}: {error}";
                return false;
            }
            try
            {
                var entries = Directory.EnumerateFileSystemEntries(backupDirectory).ToArray();
                if (entries.Length != 1 || !PathEquals(entries[0], generatedBackup))
                {
                    error = $"The rename repair directory contains unexpected data and was preserved at {backupDirectory}.";
                    return false;
                }
                File.Delete(generatedBackup);
                Directory.Delete(backupDirectory);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = $"The verified recovery file or directory could not be removed and remains at {backupDirectory}: {ex.Message}";
                return false;
            }
        }

        private static void PreserveOrDeleteEmptyBackupDirectory(string backupDirectory, List<string> recoveryNotes)
        {
            if (!Directory.Exists(backupDirectory))
                return;
            try
            {
                if (Directory.EnumerateFileSystemEntries(backupDirectory).Any())
                {
                    recoveryNotes.Add($"Protected rename-repair data was preserved at {backupDirectory}.");
                    return;
                }
                Directory.Delete(backupDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                recoveryNotes.Add($"The empty rename-repair directory could not be removed: {backupDirectory}. {ex.Message}");
            }
        }

        private static string FormatPartialRenameCommandFailure(FileCommandResult command, string action)
        {
            if (command.Status != "Ready")
                return AppendPartialOperationNotice(command.StandardError, action);
            var details = command.OutputTruncated
                ? "SVN command output exceeded the 128 KiB limit."
                : FormatFileCommandFailure(command, null);
            return AppendPartialOperationNotice(details, action);
        }

        private sealed record MissingFileSelection(string RelativePath, string Target, SvnWorkingCopyEntry Entry);

        private static async Task<WorkingCopyActionResult> RunMissingFilesActionAsync(
            string executable,
            string configDirectory,
            string workingCopyPath,
            IReadOnlyList<string> relativePaths,
            bool restore,
            CancellationToken cancellationToken)
        {
            var action = restore ? "revert" : "delete";
            var mutationStarted = false;
            try
            {
                var status = await GetStatusCoreAsync(executable, configDirectory, workingCopyPath,
                    cancellationToken, includeExternals: true).ConfigureAwait(false);
                if (status.Result.Status != "Ready")
                    return new WorkingCopyActionResult(status.Result.Status, status.Result.Details);
                if (!TryPrepareWorkingCopyRoot(status.WorkingCopyRoot, out var root, out var rootError))
                    return new WorkingCopyActionResult("InvalidPath", rootError);
                if (!TryValidateCleanupRoot(root, out var rootSafetyError))
                    return new WorkingCopyActionResult("UnsafePath", rootSafetyError);
                if (!TryResolveConfigDirectory(configDirectory, out var effectiveConfigDirectory))
                    return new WorkingCopyActionResult("InvalidConfigDirectory", "The Subversion configuration directory is invalid.");
                if (!TryPrepareMissingFileSelections(root, relativePaths, status.Result.Entries,
                        out var selections, out var selectionError))
                    return new WorkingCopyActionResult("NotEligible", selectionError);

                foreach (var selection in selections)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!CanRepairMissingFile(selection.Entry) ||
                        HasUnsafePathOperationAncestor(selection.RelativePath, status.Result.Entries))
                        return new WorkingCopyActionResult("NotEligible",
                            "Each selection must be a missing regular file outside switched, external, scheduled, copied, moved, or conflicted paths.");
                    if (!TryValidateActionPath(root, selection.Target, selection.Entry, add: false,
                            cancellationToken, out var pathError) ||
                        GetLocalPathState(selection.Target, out _, out _) != LocalPathState.Missing)
                        return new WorkingCopyActionResult("UnsafePath", string.IsNullOrEmpty(pathError)
                            ? "A selected file is no longer missing. Refresh status and try again."
                            : pathError);
                    var info = await GetPathInfoAsync(executable, configDirectory, effectiveConfigDirectory,
                        root, selection.Target, cancellationToken).ConfigureAwait(false);
                    if (info.Status != "Ready")
                        return new WorkingCopyActionResult(info.Status, info.Details);
                    if (!info.IsVersioned || info.Kind != "file" || !PathEquals(info.WorkingCopyRoot, root))
                        return new WorkingCopyActionResult("NotEligible",
                            "SVN metadata does not confirm that the missing path is a regular file in this working copy.");
                    var properties = await ReadLocalPropertiesAsync(executable, configDirectory,
                        effectiveConfigDirectory, selection.Target, cancellationToken).ConfigureAwait(false);
                    if (properties.Status != "Ready")
                        return new WorkingCopyActionResult(properties.Status, properties.Details);
                    if (properties.Properties.Any(property => property.Name.Equals("svn:special", StringComparison.Ordinal)))
                        return new WorkingCopyActionResult("NotEligible", "Missing symbolic-link nodes are not supported by the file recovery aliases.");
                    var baseProperties = await HasBaseSpecialPropertyAsync(executable, configDirectory,
                        effectiveConfigDirectory, selection.Target, cancellationToken).ConfigureAwait(false);
                    if (baseProperties.Status != "Ready")
                        return new WorkingCopyActionResult(baseProperties.Status, baseProperties.Details);
                    if (baseProperties.HasSpecial)
                        return new WorkingCopyActionResult("NotEligible", "Missing symbolic-link nodes are not supported by the file recovery aliases.");
                }

                // Check the complete selection again immediately before starting the command.
                var freshStatus = await GetStatusCoreAsync(executable, configDirectory, root,
                    cancellationToken, includeExternals: true).ConfigureAwait(false);
                if (freshStatus.Result.Status != "Ready" || !PathEquals(freshStatus.WorkingCopyRoot, root))
                    return new WorkingCopyActionResult(freshStatus.Result.Status == "Ready" ? "WorkingCopyChanged" : freshStatus.Result.Status,
                        freshStatus.Result.Details);
                foreach (var selection in selections)
                {
                    if (!TryResolveSelectedPath(root, selection.RelativePath, freshStatus.Result.Entries,
                            out var freshTarget, out var freshEntry, out _) ||
                        !Equals(selection.Entry, freshEntry) || !CanRepairMissingFile(freshEntry) ||
                        HasUnsafePathOperationAncestor(selection.RelativePath, freshStatus.Result.Entries))
                        return new WorkingCopyActionResult("StaleSelection",
                            "A selected missing file changed after it was inspected. Refresh status before retrying.");
                    if (!TryValidateActionPath(root, freshTarget, freshEntry, add: false,
                            cancellationToken, out var pathError) ||
                        GetLocalPathState(freshTarget, out _, out _) != LocalPathState.Missing)
                        return new WorkingCopyActionResult("UnsafePath", string.IsNullOrEmpty(pathError)
                            ? "A selected file is no longer missing. Refresh status and try again."
                            : pathError);
                    var freshInfo = await GetPathInfoAsync(executable, configDirectory, effectiveConfigDirectory,
                        root, freshTarget, cancellationToken).ConfigureAwait(false);
                    if (freshInfo.Status != "Ready")
                        return new WorkingCopyActionResult(freshInfo.Status, freshInfo.Details);
                    if (!freshInfo.IsVersioned || freshInfo.Kind != "file" || !PathEquals(freshInfo.WorkingCopyRoot, root))
                        return new WorkingCopyActionResult("StaleSelection",
                            "SVN no longer confirms a missing regular file in this working copy.");
                    var freshProperties = await ReadLocalPropertiesAsync(executable, configDirectory,
                        effectiveConfigDirectory, freshTarget, cancellationToken).ConfigureAwait(false);
                    if (freshProperties.Status != "Ready")
                        return new WorkingCopyActionResult(freshProperties.Status, freshProperties.Details);
                    if (freshProperties.Properties.Any(property => property.Name.Equals("svn:special", StringComparison.Ordinal)))
                        return new WorkingCopyActionResult("StaleSelection", "The selected missing path is a symbolic-link node.");
                    var freshBaseProperties = await HasBaseSpecialPropertyAsync(executable, configDirectory,
                        effectiveConfigDirectory, freshTarget, cancellationToken).ConfigureAwait(false);
                    if (freshBaseProperties.Status != "Ready")
                        return new WorkingCopyActionResult(freshBaseProperties.Status, freshBaseProperties.Details);
                    if (freshBaseProperties.HasSpecial)
                        return new WorkingCopyActionResult("StaleSelection", "The selected missing path is a symbolic-link node in its working-copy BASE.");
                }

                var arguments = new List<string> { action, "--non-interactive", "--no-auth-cache" };
                if (restore)
                {
                    arguments.Add("--depth");
                    arguments.Add("empty");
                }
                else
                    arguments.Add("--keep-local");
                AppendConfigArgument(arguments, configDirectory, effectiveConfigDirectory);
                arguments.Add("--");
                arguments.AddRange(selections.Select(selection => WithPegEscape(selection.Target)));
                cancellationToken.ThrowIfCancellationRequested();
                mutationStarted = true;
                var command = await RunFileCommandAsync(executable, arguments, null, MaxOperationOutputBytes,
                    cancellationToken, s_operationTimeout, killOnOutputLimit: false).ConfigureAwait(false);
                if (command.Status != "Ready")
                    return new WorkingCopyActionResult(command.Status,
                        AppendPartialOperationNotice(command.StandardError, action));
                if (command.ExitCode != 0)
                    return new WorkingCopyActionResult("Failed",
                        AppendPartialOperationNotice(FormatFileCommandFailure(command, null), action));
                return new WorkingCopyActionResult("Ready");
            }
            catch (OperationCanceledException)
            {
                return new WorkingCopyActionResult("Cancelled", mutationStarted
                    ? $"SVN {action} was cancelled after it started; the selected working-copy paths may have changed partially. Refresh status before retrying."
                    : $"SVN {action} was cancelled before it started.");
            }
        }

        private static bool TryPrepareMissingFileSelections(
            string root,
            IReadOnlyList<string> relativePaths,
            IReadOnlyList<SvnWorkingCopyEntry> entries,
            out List<MissingFileSelection> selections,
            out string error)
        {
            selections = new List<MissingFileSelection>();
            error = "Select at least one missing regular file from the current working-copy status.";
            if (relativePaths == null || relativePaths.Count == 0)
                return false;

            foreach (var relativePath in relativePaths)
            {
                if (!TryResolvePathOperationPath(root, relativePath, out var normalized, out var target, out error) ||
                    !TryResolveSelectedPath(root, normalized, entries, out _, out var entry, out error))
                    return false;
                if (!CanRepairMissingFile(entry))
                {
                    error = "Only missing regular files without pending schedules, conflicts, switches, externals, or moves can be repaired.";
                    return false;
                }
                if (selections.Any(selection => PathEquals(selection.RelativePath, normalized)))
                {
                    error = "The selection contains the same path more than once.";
                    return false;
                }
                selections.Add(new MissingFileSelection(normalized, target, entry));
            }

            error = string.Empty;
            return true;
        }

        private sealed record BaseSpecialPropertyResult(string Status, bool HasSpecial = false, string Details = "");

        private static async Task<BaseSpecialPropertyResult> HasBaseSpecialPropertyAsync(
            string executable,
            string configDirectory,
            string effectiveConfigDirectory,
            string target,
            CancellationToken cancellationToken)
        {
            var arguments = new List<string>
            {
                "proplist", "--xml", "--verbose", "--depth", "empty", "--revision", "BASE", "--non-interactive",
            };
            AppendConfigArgument(arguments, configDirectory, effectiveConfigDirectory);
            arguments.Add("--no-auth-cache");
            arguments.Add("--");
            arguments.Add(WithPegEscape(target));
            var command = await RunFileCommandAsync(executable, arguments, null, MaxPropertyXmlBytes,
                cancellationToken).ConfigureAwait(false);
            if (command.Status != "Ready")
                return new BaseSpecialPropertyResult(command.Status, Details: command.StandardError);
            if (command.OutputTruncated)
                return new BaseSpecialPropertyResult("TooLarge", Details: "SVN BASE property XML exceeded the 8 MiB output limit.");
            if (command.ExitCode != 0)
                return new BaseSpecialPropertyResult("Failed", Details: FormatFileCommandFailure(command, null));

            try
            {
                var document = LoadPropertyXml(DecodeXml(command.StandardOutput), MaxPropertyXmlBytes);
                var root = document.DocumentElement;
                var targets = root?.SelectNodes("target");
                if (root == null || root.Name != "properties" || targets == null || targets.Count > 1)
                    return new BaseSpecialPropertyResult("Failed", Details: "SVN returned incomplete BASE property XML.");
                if (targets.Count == 0)
                    return new BaseSpecialPropertyResult("Ready");
                if (targets[0] is not System.Xml.XmlElement targetElement ||
                    !PropertyXmlPathMatches(targetElement.GetAttribute("path"), target))
                    return new BaseSpecialPropertyResult("Failed", Details: "SVN returned BASE properties for an unexpected path.");
                foreach (System.Xml.XmlNode node in targetElement.ChildNodes)
                {
                    if (node is System.Xml.XmlElement property && property.Name == "property")
                    {
                        if (property.GetAttribute("name").Equals("svn:special", StringComparison.Ordinal))
                            return new BaseSpecialPropertyResult("Ready", HasSpecial: true);
                    }
                    else if (node.NodeType is not (System.Xml.XmlNodeType.Whitespace or System.Xml.XmlNodeType.SignificantWhitespace))
                        return new BaseSpecialPropertyResult("Failed", Details: "SVN returned an invalid BASE property entry.");
                }
                return new BaseSpecialPropertyResult("Ready");
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or System.Text.DecoderFallbackException or ArgumentException or IOException)
            {
                return new BaseSpecialPropertyResult("Failed", Details: $"SVN returned malformed BASE property XML: {ex.Message}");
            }
        }

        private static bool SameWorkingCopyIdentity(WorkingCopyResult expected, WorkingCopyResult actual)
        {
            return expected != null && actual != null &&
                PathEquals(expected.WorkingCopyRoot, actual.WorkingCopyRoot) &&
                string.Equals(expected.Url, actual.Url, StringComparison.Ordinal) &&
                string.Equals(expected.RepositoryRoot, actual.RepositoryRoot, StringComparison.Ordinal) &&
                string.Equals(expected.RepositoryUuid, actual.RepositoryUuid, StringComparison.Ordinal);
        }

        private static bool TryIsExactSafeRoot(string requestedPath, string resolvedRoot, out string error)
        {
            error = "Cleanup is allowed only for the exact working-copy root directory.";
            if (string.IsNullOrWhiteSpace(requestedPath) || !Path.IsPathFullyQualified(requestedPath) ||
                HasUnsafeLineBreak(requestedPath))
                return false;
            try
            {
                var fullRequested = Path.GetFullPath(requestedPath);
                if (!PathEquals(fullRequested, resolvedRoot))
                    return false;
                if (!TryValidateCleanupRoot(resolvedRoot, out error))
                    return false;
                error = string.Empty;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = "The working-copy root path could not be resolved safely.";
                return false;
            }
        }

        private static bool TryValidateCleanupRoot(string root, out string error)
        {
            error = "The working-copy root or one of its parent directories is a symbolic link or reparse point.";
            try
            {
                var fullRoot = Path.GetFullPath(root);
                var volumeRoot = Path.GetPathRoot(fullRoot);
                if (string.IsNullOrEmpty(volumeRoot))
                    return false;
                var state = GetLocalPathState(volumeRoot, out _, out var stateError);
                if (state != LocalPathState.Directory)
                {
                    error = state == LocalPathState.Error
                        ? $"The working-copy root could not be inspected: {stateError}"
                        : "The working-copy root is not a regular directory.";
                    return false;
                }

                var relative = Path.GetRelativePath(volumeRoot, fullRoot);
                var current = volumeRoot;
                if (relative != ".")
                {
                    foreach (var component in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                                 StringSplitOptions.RemoveEmptyEntries))
                    {
                        current = Path.Combine(current, component);
                        state = GetLocalPathState(current, out _, out stateError);
                        if (state != LocalPathState.Directory)
                        {
                            error = state == LocalPathState.SymbolicLink
                                ? "The working-copy root or one of its parent directories is a symbolic link or reparse point."
                                : state == LocalPathState.Error
                                    ? $"The working-copy root could not be inspected: {stateError}"
                                    : "The working-copy root is not a regular directory.";
                            return false;
                        }
                    }
                }
                error = string.Empty;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = "The working-copy root could not be inspected safely.";
                return false;
            }
        }
    }
}
