using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SourceGit.Native;

internal static partial class Program
{
    private const string RepairSourceVariable = "SOURCEGIT_SVN_REGRESSION_REPAIR_SOURCE";
    private const string RepairDestinationVariable = "SOURCEGIT_SVN_REGRESSION_REPAIR_DESTINATION";
    private const string RepairItemVariable = "SOURCEGIT_SVN_REGRESSION_REPAIR_ITEM";
    private const string RepairPropertiesVariable = "SOURCEGIT_SVN_REGRESSION_REPAIR_PROPERTIES";
    private const string RepairKindVariable = "SOURCEGIT_SVN_REGRESSION_REPAIR_KIND";
    private const string RepairFlagsVariable = "SOURCEGIT_SVN_REGRESSION_REPAIR_FLAGS";

    private static async Task RunRepairFakeChecksAsync()
    {
        using var temp = new TemporaryDirectory("sourcegit-svn-repair-fake");
        var executable = GetFixtureExecutable();
        var config = Path.Combine(temp.Path, "config with spaces");
        Directory.CreateDirectory(config);
        var workingCopyPath = CreateFakeWorkingCopy(temp.Path);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(workingCopyPath))!;
        const string missingName = "missing @ 文档🌱.txt";

        await CheckAsync("missing-file candidate rules allow unknown kind pending metadata verification", () =>
        {
            var candidate = new Svn.SvnWorkingCopyEntry(missingName, "missing", "none");
            True(Svn.CanRepairMissingFile(candidate), "an unknown-kind missing file was not a candidate");
            True(Svn.CanPrepareRenameRepair(candidate), "a clean missing file was not a rename candidate");
            True(Svn.CanRepairMissingFile(candidate with { Properties = "modified" }),
                "a missing file with local property edits was not eligible for explicit restore/delete");
            True(!Svn.CanPrepareRenameRepair(candidate with { Properties = "modified" }),
                "a property-edited missing file was eligible for rename repair");
            foreach (var refused in new[]
            {
                candidate with { Item = "deleted" },
                candidate with { Properties = "conflicted" },
                candidate with { Kind = "dir" },
                candidate with { IsCopied = true },
                candidate with { IsSwitched = true },
                candidate with { IsTreeConflicted = true },
                candidate with { IsFileExternal = true },
                candidate with { MovedFrom = "old.txt" },
                candidate with { MovedTo = "new.txt" },
            })
                True(!Svn.CanRepairMissingFile(refused), $"unsafe missing-file status was eligible: {refused}");
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await CheckAsync("cleanup targets only the exact working-copy root and never asks status", async () =>
        {
            var calls = PrepareRepairScenario(temp.Path, "wc_repair_ready", workingCopyPath,
                missingName, item: "missing", properties: "none", kind: "file");
            var cleaned = await Svn.CleanupAsync(executable, config, root).ConfigureAwait(false);
            Equal("Ready", cleaned.Status);
            var invocations = ReadInvocations(calls);
            var cleanup = invocations.Single(call => call.Arguments[0] == "cleanup");
            SequenceEqual(new[]
            {
                "cleanup", "--non-interactive", "--no-auth-cache", "--config-dir", Path.GetFullPath(config),
                "--", ExpectedPegOperand(root),
            }, cleanup.Arguments);
            True(!cleanup.Arguments.Any(argument => argument is "--break-locks" or "--remove-unversioned" or
                "--remove-ignored" or "--vacuum-pristines" or "--include-externals"),
                "cleanup requested a destructive or recursive option");
            True(!invocations.Any(call => call.Arguments[0] == "status"),
                "cleanup depended on status even though a locked working copy can fail status");

            var nested = await Svn.CleanupAsync(executable, config, Path.GetDirectoryName(workingCopyPath)!).ConfigureAwait(false);
            Equal("InvalidPath", nested.Status);
            True(!ReadInvocations(calls).Any(call => call.Arguments[0] == "cleanup" &&
                call.Arguments[^1] != ExpectedPegOperand(root)), "nested-path cleanup reached svn cleanup");
        }).ConfigureAwait(false);

        await CheckAsync("cleanup refuses a changed working-copy identity before mutation", async () =>
        {
            var calls = PrepareRepairScenario(temp.Path, "wc_repair_identity_changed", workingCopyPath,
                missingName, item: "missing", properties: "none", kind: "file");
            var changed = await Svn.CleanupAsync(executable, config, root).ConfigureAwait(false);
            Equal("WorkingCopyChanged", changed.Status);
            True(!ReadInvocations(calls).Any(call => call.Arguments[0] == "cleanup"),
                "identity change launched cleanup");
        }).ConfigureAwait(false);

        await CheckAsync("restore and remove use verified missing files and exact file-only arguments", async () =>
        {
            var target = Path.Combine(root, missingName);
            if (File.Exists(target))
                File.Delete(target);
            var calls = PrepareRepairScenario(temp.Path, "wc_repair_ready", workingCopyPath,
                missingName, item: "missing", properties: "modified", kind: "unknown");
            var restored = await Svn.RestoreMissingFilesAsync(executable, config, root, new[] { missingName }).ConfigureAwait(false);
            Equal("Ready", restored.Status);
            var revert = ReadInvocations(calls).Single(call => call.Arguments[0] == "revert");
            SequenceEqual(new[]
            {
                "revert", "--non-interactive", "--no-auth-cache", "--depth", "empty", "--config-dir",
                Path.GetFullPath(config), "--", ExpectedPegOperand(target),
            }, revert.Arguments);
            True(!File.Exists(target), "fake restore unexpectedly wrote local file contents");

            calls = PrepareRepairScenario(temp.Path, "wc_repair_ready", workingCopyPath,
                missingName, item: "missing", properties: "none", kind: "file");
            var removed = await Svn.RemoveMissingFilesAsync(executable, config, root, new[] { missingName }).ConfigureAwait(false);
            Equal("Ready", removed.Status);
            var delete = ReadInvocations(calls).Single(call => call.Arguments[0] == "delete");
            SequenceEqual(new[]
            {
                "delete", "--non-interactive", "--no-auth-cache", "--keep-local", "--config-dir",
                Path.GetFullPath(config), "--", ExpectedPegOperand(target),
            }, delete.Arguments);
            True(!File.Exists(target), "remove-missing changed filesystem content");
        }).ConfigureAwait(false);

        await CheckAsync("missing-file actions reject stale, switched, and non-file metadata before mutation", async () =>
        {
            var target = Path.Combine(root, missingName);
            var calls = PrepareRepairScenario(temp.Path, "wc_repair_stale", workingCopyPath,
                missingName, item: "missing", properties: "none", kind: "file");
            var stale = await Svn.RemoveMissingFilesAsync(executable, config, root, new[] { missingName }).ConfigureAwait(false);
            Equal("StaleSelection", stale.Status);
            True(!ReadInvocations(calls).Any(call => call.Arguments[0] == "delete"), "stale missing-file status launched delete");

            calls = PrepareRepairScenario(temp.Path, "wc_repair_switched", workingCopyPath,
                missingName, item: "missing", properties: "none", kind: "file");
            var switched = await Svn.RestoreMissingFilesAsync(executable, config, root, new[] { missingName }).ConfigureAwait(false);
            Equal("NotEligible", switched.Status);
            True(!ReadInvocations(calls).Any(call => call.Arguments[0] == "revert"), "switched missing file launched revert");

            calls = PrepareRepairScenario(temp.Path, "wc_repair_info_dir", workingCopyPath,
                missingName, item: "missing", properties: "none", kind: "unknown");
            var directory = await Svn.RestoreMissingFilesAsync(executable, config, root, new[] { missingName }).ConfigureAwait(false);
            Equal("NotEligible", directory.Status);
            True(!ReadInvocations(calls).Any(call => call.Arguments[0] == "revert"), "directory metadata launched revert");
            True(!File.Exists(target), "guard checks created or replaced the selected path");
        }).ConfigureAwait(false);

        await CheckAsync("both missing-file actions refuse working and BASE svn:special nodes", async () =>
        {
            foreach (var scenario in new[] { "wc_repair_special_working", "wc_repair_special_base" })
            {
                foreach (var restore in new[] { true, false })
                {
                    var calls = PrepareRepairScenario(temp.Path, scenario, workingCopyPath,
                        missingName, item: "missing", properties: "none", kind: "file");
                    var result = restore
                        ? await Svn.RestoreMissingFilesAsync(executable, config, root, new[] { missingName }).ConfigureAwait(false)
                        : await Svn.RemoveMissingFilesAsync(executable, config, root, new[] { missingName }).ConfigureAwait(false);
                    Equal("NotEligible", result.Status);
                    True(!ReadInvocations(calls).Any(call => call.Arguments[0] is "revert" or "delete"),
                        $"{(restore ? "restore" : "remove")} mutated a missing symlink for {scenario}");
                }
            }
        }).ConfigureAwait(false);

        await CheckAsync("cleanup and missing-file failures report partial work and cancellation stops SVN", async () =>
        {
            var calls = PrepareRepairScenario(temp.Path, "wc_repair_cleanup_fail", workingCopyPath,
                missingName, item: "missing", properties: "none", kind: "file");
            var failedCleanup = await Svn.CleanupAsync(executable, config, root).ConfigureAwait(false);
            Equal("Failed", failedCleanup.Status);
            True(failedCleanup.Details.Contains("may have changed partially", StringComparison.OrdinalIgnoreCase),
                "failed cleanup omitted its partial-operation notice");

            calls = PrepareRepairScenario(temp.Path, "wc_repair_fail", workingCopyPath,
                missingName, item: "missing", properties: "none", kind: "file");
            var failedRestore = await Svn.RestoreMissingFilesAsync(executable, config, root, new[] { missingName }).ConfigureAwait(false);
            Equal("Failed", failedRestore.Status);
            True(failedRestore.Details.Contains("may have changed partially", StringComparison.OrdinalIgnoreCase),
                "failed restore omitted its partial-operation notice");

            calls = PrepareRepairScenario(temp.Path, "wc_repair_slow", workingCopyPath,
                missingName, item: "missing", properties: "none", kind: "file");
            using var cancellation = new CancellationTokenSource();
            var action = Svn.RemoveMissingFilesAsync(executable, config, root,
                new[] { missingName }, cancellation.Token);
            var mutationStarted = calls + ".repair-mutation-started";
            var waitUntilStart = Stopwatch.StartNew();
            while (!File.Exists(mutationStarted) && waitUntilStart.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(20).ConfigureAwait(false);
            True(File.Exists(mutationStarted), "the fake delete did not reach its mutation stage before cancellation");
            cancellation.Cancel();
            var cancelled = await action.ConfigureAwait(false);
            Equal("Cancelled", cancelled.Status);
            True(cancelled.Details.Contains("may have changed partially", StringComparison.OrdinalIgnoreCase),
                "cancelled delete omitted its partial-operation notice");
            var mutation = ReadInvocations(calls).Last(call => call.Arguments[0] == "delete");
            var waitUntil = Stopwatch.StartNew();
            while (IsProcessRunning(mutation.ProcessId) && waitUntil.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(20).ConfigureAwait(false);
            True(!IsProcessRunning(mutation.ProcessId), "cancelled missing-file SVN process remained alive");
        }).ConfigureAwait(false);

        await CheckAsync("rename preview binds the unversioned destination bytes and stale previews do not mutate", async () =>
        {
            const string sourceName = "missing rename source.txt";
            const string destinationName = "unversioned rename destination.txt";
            var source = Path.Combine(root, sourceName);
            var destination = Path.Combine(root, destinationName);
            if (File.Exists(source))
                File.Delete(source);
            await File.WriteAllBytesAsync(destination, new byte[] { 0, 1, 2, 0xFE, 0xFF }).ConfigureAwait(false);
            var calls = PrepareRepairScenario(temp.Path, "wc_repair_rename", workingCopyPath,
                sourceName, item: "missing", properties: "none", kind: "file", destinationRelativePath: destinationName);
            var preview = await Svn.GetRenameRepairSnapshotAsync(executable, config, root,
                sourceName, destinationName).ConfigureAwait(false);
            True(preview.Status == "Ready", $"rename preview returned {preview.Status}: {preview.Details}");
            Equal(sourceName, preview.Snapshot.SourceRelativePath);
            Equal(destinationName, preview.Snapshot.DestinationRelativePath);

            var changedBytes = new byte[] { 9, 8, 7, 0, 6 };
            await File.WriteAllBytesAsync(destination, changedBytes).ConfigureAwait(false);
            var stale = await Svn.RepairRenameAsync(executable, config, root, preview.Snapshot).ConfigureAwait(false);
            Equal("StaleSelection", stale.Status);
            SequenceEqual(changedBytes, await File.ReadAllBytesAsync(destination).ConfigureAwait(false));
            True(!ReadInvocations(calls).Any(call => call.Arguments[0] is "revert" or "move"),
                "stale rename preview launched a mutation");
        }).ConfigureAwait(false);

        await CheckAsync("rename preview accepts path-specific file URLs and revisions within the same repository", async () =>
        {
            const string sourceName = "same repository source.txt";
            const string destinationName = "same repository destination.txt";
            var destination = Path.Combine(root, destinationName);
            await File.WriteAllTextAsync(destination, "destination bytes\n").ConfigureAwait(false);
            var calls = PrepareRepairScenario(temp.Path, "wc_repair_rename", workingCopyPath,
                sourceName, item: "missing", properties: "none", kind: "file", destinationRelativePath: destinationName);
            var preview = await Svn.GetRenameRepairSnapshotAsync(executable, config, root,
                sourceName, destinationName).ConfigureAwait(false);
            True(preview.Status == "Ready", $"same-repository preview returned {preview.Status}: {preview.Details}");
            var infos = ReadInvocations(calls).Where(call => call.Arguments[0] == "info").ToArray();
            var sourceInfos = infos.Where(call => call.Arguments[^1].Contains("same repository source.txt", StringComparison.Ordinal)).ToArray();
            True(sourceInfos.Any(call => call.Arguments.Contains("--depth", StringComparer.Ordinal)),
                "depth-empty source metadata was not queried");
            True(infos.Any(call => call.Arguments[^1] == ExpectedPegOperand(root) &&
                !call.Arguments.Contains("--depth", StringComparer.Ordinal)),
                "the working-copy root identity query was not exercised");

            calls = PrepareRepairScenario(temp.Path, "wc_repair_cross_repository", workingCopyPath,
                sourceName, item: "missing", properties: "none", kind: "file", destinationRelativePath: destinationName);
            var crossRepository = await Svn.GetRenameRepairSnapshotAsync(executable, config, root,
                sourceName, destinationName).ConfigureAwait(false);
            Equal("NotEligible", crossRepository.Status);
            True(!ReadInvocations(calls).Any(call => call.Arguments[0] is "revert" or "move"),
                "a cross-repository source launched a mutation");
        }).ConfigureAwait(false);
    }

    private static string PrepareRepairScenario(
        string directory,
        string scenario,
        string workingCopyPath,
        string sourceRelativePath,
        string item,
        string properties,
        string kind,
        string destinationRelativePath = null)
    {
        var calls = PrepareHistoryScenario(directory, scenario, workingCopyPath);
        var root = Environment.GetEnvironmentVariable(WorkingCopyRootVariable) ?? string.Empty;
        var source = Path.GetFullPath(sourceRelativePath, root);
        var destination = string.IsNullOrEmpty(destinationRelativePath) ? string.Empty : Path.GetFullPath(destinationRelativePath, root);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        if (!File.Exists(source) && !Directory.Exists(source))
            File.WriteAllText(source, "fake repair target\n");
        if (item == "missing" && File.Exists(source))
            File.Delete(source);
        if (!string.IsNullOrEmpty(destination))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
                File.WriteAllText(destination, "fake unversioned destination\n");
        }
        Environment.SetEnvironmentVariable(RepairSourceVariable, source);
        Environment.SetEnvironmentVariable(RepairDestinationVariable, destination);
        Environment.SetEnvironmentVariable(RepairItemVariable, item);
        Environment.SetEnvironmentVariable(RepairPropertiesVariable, properties);
        Environment.SetEnvironmentVariable(RepairKindVariable, kind);
        Environment.SetEnvironmentVariable(RepairFlagsVariable, scenario == "wc_repair_switched" ? "switched=\"true\"" : string.Empty);
        return calls;
    }

    private static void WriteRepairStatus(string scenario)
    {
        var root = Environment.GetEnvironmentVariable(WorkingCopyRootVariable) ?? string.Empty;
        var source = Environment.GetEnvironmentVariable(RepairSourceVariable) ?? string.Empty;
        var destination = Environment.GetEnvironmentVariable(RepairDestinationVariable) ?? string.Empty;
        var item = Environment.GetEnvironmentVariable(RepairItemVariable) ?? "missing";
        var properties = Environment.GetEnvironmentVariable(RepairPropertiesVariable) ?? "none";
        var flags = Environment.GetEnvironmentVariable(RepairFlagsVariable) ?? string.Empty;
        var kind = Environment.GetEnvironmentVariable(RepairKindVariable) ?? "unknown";
        if (scenario == "wc_repair_stale" && ReadInvocations(Environment.GetEnvironmentVariable(InvocationVariable)).Count(call => call.Arguments[0] == "status") > 1)
            item = "modified";
        var kindAttribute = kind == "unknown" ? string.Empty : $" kind=\"{System.Security.SecurityElement.Escape(kind)}\"";
        var entries = new List<string>
        {
            $"<entry path=\"{System.Security.SecurityElement.Escape(source)}\"{kindAttribute}><wc-status item=\"{item}\" revision=\"12\" props=\"{properties}\" {flags}/></entry>",
        };
        if (!string.IsNullOrEmpty(destination))
            entries.Add($"<entry path=\"{System.Security.SecurityElement.Escape(destination)}\" kind=\"file\"><wc-status item=\"unversioned\" revision=\"0\" props=\"none\"/></entry>");
        Console.WriteLine($"<status><target path=\"{System.Security.SecurityElement.Escape(root)}\">{string.Join(string.Empty, entries)}</target></status>");
    }

    private static void WriteRepairPathInfo(string[] args)
    {
        var root = Environment.GetEnvironmentVariable(WorkingCopyRootVariable) ?? string.Empty;
        var target = DecodeFakePegPath(args[^1]);
        var scenario = Environment.GetEnvironmentVariable(ScenarioVariable) ?? string.Empty;
        var kind = scenario == "wc_repair_info_dir" ? "dir" : "file";
        var isCrossRepository = scenario == "wc_repair_cross_repository";
        var repositoryRoot = isCrossRepository ? "file:///other-repository" : "file:///fixture/repository";
        var repositoryUuid = isCrossRepository ? "other-repair-fixture-uuid" : "repair-fixture-uuid";
        var entryUrl = repositoryRoot + EscapeRepairRelativePath(root, target);
        Console.WriteLine($"<info><entry kind=\"{kind}\" path=\"{System.Security.SecurityElement.Escape(target)}\" revision=\"7\"><url>{entryUrl}</url><repository><root>{repositoryRoot}</root><uuid>{repositoryUuid}</uuid></repository><wc-info><wcroot-abspath>{System.Security.SecurityElement.Escape(root)}</wcroot-abspath><schedule>normal</schedule></wc-info></entry></info>");
    }

    private static void WriteRepairWorkingCopyInfo(string[] args, string scenario, string invocationPath)
    {
        var root = Environment.GetEnvironmentVariable(WorkingCopyRootVariable) ?? string.Empty;
        var target = DecodeFakePegPath(args[^1]);
        var isRoot = PathEqualsForFixture(root, target);
        var relative = isRoot ? "." : Path.GetRelativePath(root, target);
        var escapedRelative = EscapeRepairRelativePath(root, target);
        var repositoryRoot = scenario == "wc_repair_cross_repository" && !isRoot
            ? "file:///other-repository"
            : "file:///fixture/repository";
        var repositoryUuid = scenario == "wc_repair_cross_repository" && !isRoot
            ? "other-repair-fixture-uuid"
            : "repair-fixture-uuid";
        var url = scenario == "wc_repair_identity_changed" &&
            ReadInvocations(invocationPath).Count(call => call.Arguments[0] == "info") > 1
                ? "file:///fixture/other-working-copy"
                : repositoryRoot + escapedRelative;
        var revision = isRoot ? "12" : "7";
        Console.WriteLine($"<info><entry kind=\"{(isRoot ? "dir" : "file")}\" path=\"{System.Security.SecurityElement.Escape(target)}\" revision=\"{revision}\"><url>{url}</url><repository><root>{repositoryRoot}</root><uuid>{repositoryUuid}</uuid></repository><wc-info><wcroot-abspath>{System.Security.SecurityElement.Escape(root)}</wcroot-abspath></wc-info></entry></info>");
    }

    private static string EscapeRepairRelativePath(string root, string target)
    {
        var relative = Path.GetRelativePath(root, target);
        if (relative == ".")
            return string.Empty;
        var escaped = string.Join("/", relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
        return "/" + escaped;
    }

    private static void WriteRepairProperties(string[] args)
    {
        var target = DecodeFakePegPath(args[^1]);
        var scenario = Environment.GetEnvironmentVariable(ScenarioVariable) ?? string.Empty;
        var hasSpecial = args.Contains("--revision", StringComparer.Ordinal)
            ? scenario == "wc_repair_special_base"
            : scenario == "wc_repair_special_working";
        var property = hasSpecial
            ? "<property name=\"svn:special\">*</property>"
            : scenario == "wc_repair_rename"
                ? "<property name=\"custom:repair\">source property</property>"
                : string.Empty;
        Console.WriteLine($"<properties><target path=\"{System.Security.SecurityElement.Escape(target.TrimEnd('@'))}\">{property}</target></properties>");
    }

    private static async Task RunRepairIntegrationAsync(string svn, string svnadmin, string tempDirectory)
    {
        var root = Path.Combine(tempDirectory, "working-copy-repair");
        var repository = Path.Combine(root, "repository");
        var seed = Path.Combine(root, "seed");
        var workingCopy = Path.Combine(root, "working copy @ local");
        var config = Path.Combine(root, "isolated config");
        Directory.CreateDirectory(seed);
        Directory.CreateDirectory(config);

        const string renameSourceName = "rename source @ 文件.txt";
        const string renameDestinationName = "rename destination @ 保持.txt";
        const string restoreName = "restore property @ file.txt";
        const string removeName = "remove missing @ file.txt";
        const string modifiedName = "modified remains.txt";
        const string ignoreName = "ignored remains.tmp";
        const string propertyName = "custom:repair-regression";
        const string sourceProperty = "source property survives move";
        const string restoreProperty = "restore BASE property";
        var sourceSeed = Path.Combine(seed, renameSourceName);
        var restoreSeed = Path.Combine(seed, restoreName);
        await File.WriteAllTextAsync(sourceSeed, "tracked source bytes\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(restoreSeed, "restore BASE bytes\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(seed, removeName), "scheduled deletion BASE bytes\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(seed, modifiedName), "original modified bytes\n").ConfigureAwait(false);

        var specialSeedPath = Path.Combine(seed, "versioned missing symlink.txt");
        var specialLinkSeeded = false;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.CreateSymbolicLink(specialSeedPath, "versioned symlink target");
                specialLinkSeeded = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
            {
                // BASE svn:special recovery checks run where the fixture filesystem supports symlinks.
            }
        }

        await RunRequiredAsync(svnadmin, "create", repository).ConfigureAwait(false);
        var repositoryUrl = new Uri(repository).AbsoluteUri;
        await RunRequiredAsync(svn, "import", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            "--message", "Create cleanup and repair regression fixture", "--", seed, repositoryUrl).ConfigureAwait(false);
        await RunRequiredAsync(svn, "checkout", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            "--", repositoryUrl, workingCopy).ConfigureAwait(false);

        if (specialLinkSeeded)
            File.Delete(Path.Combine(workingCopy, Path.GetFileName(specialSeedPath)));

        var sourcePath = Path.Combine(workingCopy, renameSourceName);
        var restorePath = Path.Combine(workingCopy, restoreName);
        var removePath = Path.Combine(workingCopy, removeName);
        var modifiedPath = Path.Combine(workingCopy, modifiedName);
        await RunRequiredAsync(svn, "propset", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            propertyName, sourceProperty, "--", sourcePath + "@").ConfigureAwait(false);
        await RunRequiredAsync(svn, "propset", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            propertyName, restoreProperty, "--", restorePath + "@").ConfigureAwait(false);
        await RunRequiredAsync(svn, "propset", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            "svn:ignore", ignoreName, "--", workingCopy + "@").ConfigureAwait(false);
        await RunRequiredAsync(svn, "commit", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            "--message", "Commit repair test properties", "--", workingCopy + "@").ConfigureAwait(false);

        const string modifiedBytes = "local modified bytes survive cleanup\n";
        const string unversionedBytes = "unversioned bytes survive cleanup\n";
        const string ignoredBytes = "ignored bytes survive cleanup\n";
        await File.WriteAllTextAsync(modifiedPath, modifiedBytes).ConfigureAwait(false);
        var unversionedPath = Path.Combine(workingCopy, "unversioned remains.txt");
        var ignoredPath = Path.Combine(workingCopy, ignoreName);
        await File.WriteAllTextAsync(unversionedPath, unversionedBytes).ConfigureAwait(false);
        await File.WriteAllTextAsync(ignoredPath, ignoredBytes).ConfigureAwait(false);

        await CheckAsync("real file URL cleanup preserves modified, unversioned, and ignored files", async () =>
        {
            var cleanup = await Svn.CleanupAsync(svn, config, workingCopy).ConfigureAwait(false);
            True(cleanup.Status == "Ready", $"cleanup returned {cleanup.Status}: {cleanup.Details}");
            Equal(modifiedBytes, await File.ReadAllTextAsync(modifiedPath).ConfigureAwait(false));
            Equal(unversionedBytes, await File.ReadAllTextAsync(unversionedPath).ConfigureAwait(false));
            Equal(ignoredBytes, await File.ReadAllTextAsync(ignoredPath).ConfigureAwait(false));
        }).ConfigureAwait(false);

        await CheckAsync("real restore reverts the selected missing file and its local property edit", async () =>
        {
            await RunRequiredAsync(svn, "propset", "--non-interactive", "--no-auth-cache", "--config-dir", config,
                propertyName, "local property edit to revert", "--", restorePath + "@").ConfigureAwait(false);
            File.Delete(restorePath);
            var restored = await Svn.RestoreMissingFilesAsync(svn, config, workingCopy, new[] { restoreName }).ConfigureAwait(false);
            True(restored.Status == "Ready", $"restore returned {restored.Status}: {restored.Details}");
            Equal("restore BASE bytes\n", await File.ReadAllTextAsync(restorePath).ConfigureAwait(false));
            Equal(restoreProperty, (await RunCaptureAsync(svn, "propget", "--strict", "--non-interactive", "--no-auth-cache",
                "--config-dir", config, propertyName, "--", restorePath + "@").ConfigureAwait(false)).StandardOutput);
        }).ConfigureAwait(false);

        await CheckAsync("real remove schedules only the selected missing file and retains modified siblings", async () =>
        {
            File.Delete(removePath);
            var removed = await Svn.RemoveMissingFilesAsync(svn, config, workingCopy, new[] { removeName }).ConfigureAwait(false);
            True(removed.Status == "Ready", $"remove-missing returned {removed.Status}: {removed.Details}");
            True(!File.Exists(removePath), "remove-missing recreated or deleted another local file");
            Equal(modifiedBytes, await File.ReadAllTextAsync(modifiedPath).ConfigureAwait(false));
            var status = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
            True(status.Entries.Any(entry => entry.Path == removeName && entry.Item == "deleted"),
                "selected missing file was not scheduled for deletion");
        }).ConfigureAwait(false);

        if (specialLinkSeeded)
        {
            await CheckAsync("real restore and remove aliases refuse a missing BASE svn:special link", async () =>
            {
                const string specialName = "versioned missing symlink.txt";
                var restore = await Svn.RestoreMissingFilesAsync(svn, config, workingCopy, new[] { specialName }).ConfigureAwait(false);
                Equal("NotEligible", restore.Status);
                var remove = await Svn.RemoveMissingFilesAsync(svn, config, workingCopy, new[] { specialName }).ConfigureAwait(false);
                Equal("NotEligible", remove.Status);
                True(!File.Exists(Path.Combine(workingCopy, specialName)), "a rejected special-file action recreated its missing node");
            }).ConfigureAwait(false);
        }

        await CheckAsync("real rename repair preserves move ancestry, source properties, changelist, and destination bytes", async () =>
        {
            const string destinationBytes = "unversioned destination bytes remain byte-identical\n";
            var destinationPath = Path.Combine(workingCopy, renameDestinationName);
            await File.WriteAllTextAsync(destinationPath, destinationBytes).ConfigureAwait(false);
            await RunRequiredAsync(svn, "changelist", "--quiet", "--depth", "empty", "--",
                "repair rename list", sourcePath + "@").ConfigureAwait(false);
            File.Delete(sourcePath);
            var preview = await Svn.GetRenameRepairSnapshotAsync(svn, config, workingCopy,
                renameSourceName, renameDestinationName).ConfigureAwait(false);
            True(preview.Status == "Ready", $"rename preview returned {preview.Status}: {preview.Details}");
            var repaired = await Svn.RepairRenameAsync(svn, config, workingCopy, preview.Snapshot).ConfigureAwait(false);
            True(repaired.Status == "Ready", $"rename repair returned {repaired.Status}: {repaired.Details}");
            SequenceEqual(System.Text.Encoding.UTF8.GetBytes(destinationBytes), await File.ReadAllBytesAsync(destinationPath).ConfigureAwait(false));
            var status = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
            var sourceEntry = status.Entries.Single(entry => entry.Path == renameSourceName);
            var destinationEntry = status.Entries.Single(entry => entry.Path == renameDestinationName);
            Equal("deleted", sourceEntry.Item);
            Equal(renameDestinationName, sourceEntry.MovedTo);
            True(destinationEntry.Item is "added" or "modified", $"destination item was {destinationEntry.Item}");
            Equal(renameSourceName, destinationEntry.MovedFrom);
            Equal("repair rename list", destinationEntry.Changelist);
            Equal(sourceProperty, (await RunCaptureAsync(svn, "propget", "--strict", "--non-interactive", "--no-auth-cache",
                "--config-dir", config, propertyName, "--", destinationPath + "@").ConfigureAwait(false)).StandardOutput);
            await RunRequiredAsync(svn, "commit", "--non-interactive", "--no-auth-cache", "--config-dir", config,
                "--message", "Commit repaired rename", "--", workingCopy + "@").ConfigureAwait(false);
            var log = await RunCaptureAsync(svn, "log", "--xml", "--verbose", "--limit", "1", "--non-interactive", "--no-auth-cache",
                "--config-dir", config, "--", repositoryUrl + "/" + Uri.EscapeDataString(renameDestinationName) + "@").ConfigureAwait(false);
            var logDocument = XDocument.Parse(log.StandardOutput);
            var movedPath = logDocument.Root?.Element("logentry")?.Element("paths")?.Elements("path")
                .SingleOrDefault(path => path.Value == "/" + renameDestinationName);
            True(movedPath != null, $"committed history omitted the exact destination path. SVN log XML: {log.StandardOutput}");
            True(string.Equals(movedPath.Attribute("copyfrom-path")?.Value, "/" + renameSourceName, StringComparison.Ordinal),
                $"committed history did not record the exact source path. SVN log XML: {log.StandardOutput}");
            True(long.TryParse(movedPath.Attribute("copyfrom-rev")?.Value,
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var copyFromRevision) &&
                 copyFromRevision > 0,
                $"committed history omitted a valid copy-from revision. SVN log XML: {log.StandardOutput}");
        }).ConfigureAwait(false);

        foreach (var eolStyle in new[] { "CRLF", "native" })
        {
            await CheckAsync($"real rename repair verifies translated {eolStyle} BASE bytes and expanded keywords", async () =>
            {
                var translatedSourceName = "translated " + eolStyle + " source.txt";
                var translatedDestinationName = "translated " + eolStyle + " destination.txt";
                var translatedSource = Path.Combine(workingCopy, translatedSourceName);
                var translatedDestination = Path.Combine(workingCopy, translatedDestinationName);
                await File.WriteAllTextAsync(translatedSource, "$Id$\n$Rev$\n").ConfigureAwait(false);
                await RunRequiredAsync(svn, "add", "--config-dir", config, "--", translatedSource + "@").ConfigureAwait(false);
                await RunRequiredAsync(svn, "propset", "--config-dir", config, "--", "svn:eol-style", eolStyle,
                    translatedSource + "@").ConfigureAwait(false);
                await RunRequiredAsync(svn, "propset", "--config-dir", config, "--", "svn:keywords", "Id Rev",
                    translatedSource + "@").ConfigureAwait(false);
                await RunRequiredAsync(svn, "commit", "--non-interactive", "--no-auth-cache", "--config-dir", config,
                    "--message", "Add translated repair source", "--", workingCopy + "@").ConfigureAwait(false);
                // Install the translated working form before simulating an external rename.
                File.Delete(translatedSource);
                await RunRequiredAsync(svn, "revert", "--config-dir", config, "--", translatedSource + "@").ConfigureAwait(false);
                var originalBytes = await File.ReadAllBytesAsync(translatedSource).ConfigureAwait(false);
                True(Encoding.UTF8.GetString(originalBytes).Contains("$Id: ", StringComparison.Ordinal),
                    "the translation fixture did not expand its keyword");
                if (eolStyle == "CRLF")
                    True(Encoding.UTF8.GetString(originalBytes).Contains("\r\n", StringComparison.Ordinal),
                        "the translation fixture did not install CRLF line endings");
                File.Move(translatedSource, translatedDestination);
                var preview = await Svn.GetRenameRepairSnapshotAsync(svn, config, workingCopy,
                    translatedSourceName, translatedDestinationName).ConfigureAwait(false);
                True(preview.Status == "Ready", $"translated preview returned {preview.Status}: {preview.Details}");
                var repaired = await Svn.RepairRenameAsync(svn, config, workingCopy, preview.Snapshot).ConfigureAwait(false);
                True(repaired.Status == "Ready", $"translated repair returned {repaired.Status}: {repaired.Details}");
                SequenceEqual(originalBytes, await File.ReadAllBytesAsync(translatedDestination).ConfigureAwait(false));
                await RunRequiredAsync(svn, "commit", "--non-interactive", "--no-auth-cache", "--config-dir", config,
                    "--message", "Commit translated rename repair", "--", workingCopy + "@").ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        await CheckAsync("real rename preview refuses changed destination bytes and unsafe paths", async () =>
        {
            const string staleSourceName = "stale rename source.txt";
            const string staleDestinationName = "stale rename destination.txt";
            var staleSourcePath = Path.Combine(workingCopy, staleSourceName);
            var staleDestinationPath = Path.Combine(workingCopy, staleDestinationName);
            await File.WriteAllTextAsync(staleSourcePath, "stale source baseline\n").ConfigureAwait(false);
            await RunRequiredAsync(svn, "add", "--non-interactive", "--no-auth-cache", "--config-dir", config,
                "--", staleSourcePath + "@").ConfigureAwait(false);
            await RunRequiredAsync(svn, "commit", "--non-interactive", "--no-auth-cache", "--config-dir", config,
                "--message", "Add stale repair source", "--", workingCopy + "@").ConfigureAwait(false);
            await File.WriteAllTextAsync(staleDestinationPath, "unversioned initial destination\n").ConfigureAwait(false);
            File.Delete(staleSourcePath);
            var preview = await Svn.GetRenameRepairSnapshotAsync(svn, config, workingCopy,
                staleSourceName, staleDestinationName).ConfigureAwait(false);
            True(preview.Status == "Ready", $"stale fixture preview returned {preview.Status}: {preview.Details}");
            const string changedBytes = "destination changed after preview\n";
            await File.WriteAllTextAsync(staleDestinationPath, changedBytes).ConfigureAwait(false);
            var stale = await Svn.RepairRenameAsync(svn, config, workingCopy, preview.Snapshot).ConfigureAwait(false);
            Equal("StaleSelection", stale.Status);
            Equal(changedBytes, await File.ReadAllTextAsync(staleDestinationPath).ConfigureAwait(false));

            const string unsafeSourceName = "unsafe symlink source.txt";
            var unsafeSourcePath = Path.Combine(workingCopy, unsafeSourceName);
            await File.WriteAllTextAsync(unsafeSourcePath, "unsafe source baseline\n").ConfigureAwait(false);
            await RunRequiredAsync(svn, "add", "--non-interactive", "--no-auth-cache", "--config-dir", config,
                "--", unsafeSourcePath + "@").ConfigureAwait(false);
            await RunRequiredAsync(svn, "commit", "--non-interactive", "--no-auth-cache", "--config-dir", config,
                "--message", "Add unsafe repair source", "--", workingCopy + "@").ConfigureAwait(false);
            File.Delete(unsafeSourcePath);
            var outside = Path.Combine(root, "outside repair sentinel.txt");
            await File.WriteAllTextAsync(outside, "outside stays intact\n").ConfigureAwait(false);
            var symlinkPath = Path.Combine(workingCopy, "unsafe symlink destination.txt");
            var created = false;
            try
            {
                File.CreateSymbolicLink(symlinkPath, outside);
                created = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
            {
                // Symlink guard is exercised where the working filesystem allows creation.
            }
            if (created)
            {
                var unsafePreview = await Svn.GetRenameRepairSnapshotAsync(svn, config, workingCopy,
                    unsafeSourceName, Path.GetFileName(symlinkPath)).ConfigureAwait(false);
                True(unsafePreview.Status is "UnsafePath" or "NotEligible",
                    $"symlink destination returned {unsafePreview.Status}");
                Equal("outside stays intact\n", await File.ReadAllTextAsync(outside).ConfigureAwait(false));
            }
        }).ConfigureAwait(false);

        if (!OperatingSystem.IsWindows())
        {
            await CheckAsync("rename repair preserves a concurrent source write after svn revert", async () =>
            {
                const string sourceName = "post-revert source.txt";
                const string destinationName = "post-revert destination.txt";
                const string originalDestination = "original external rename bytes\n";
                const string injectedSource = "concurrent edit after svn revert\n";
                var sourcePath = Path.Combine(workingCopy, sourceName);
                var destinationPath = Path.Combine(workingCopy, destinationName);
                await File.WriteAllTextAsync(sourcePath, "tracked pristine source\n").ConfigureAwait(false);
                await RunRequiredAsync(svn, "add", "--non-interactive", "--no-auth-cache", "--config-dir", config,
                    "--", sourcePath + "@").ConfigureAwait(false);
                await RunRequiredAsync(svn, "commit", "--non-interactive", "--no-auth-cache", "--config-dir", config,
                    "--message", "Add post-revert repair source", "--", sourcePath + "@").ConfigureAwait(false);
                await File.WriteAllTextAsync(destinationPath, originalDestination).ConfigureAwait(false);
                File.Delete(sourcePath);
                var preview = await Svn.GetRenameRepairSnapshotAsync(svn, config, workingCopy,
                    sourceName, destinationName).ConfigureAwait(false);
                True(preview.Status == "Ready", $"post-revert fixture preview returned {preview.Status}: {preview.Details}");

                var wrapper = Path.Combine(root, "svn revert wrapper");
                var wrapperScript = "#!/bin/sh\n" +
                    "real_svn=" + ShellQuote(svn) + "\n" +
                    "\"$real_svn\" \"$@\"\n" +
                    "result=$?\n" +
                    "if [ \"$result\" -eq 0 ] && [ \"$1\" = revert ]; then\n" +
                    "  printf '%s' " + ShellQuote(injectedSource) + " > " + ShellQuote(sourcePath) + "\n" +
                    "fi\n" +
                    "exit \"$result\"\n";
                await File.WriteAllTextAsync(wrapper, wrapperScript).ConfigureAwait(false);
                SetUnixExecutable(wrapper);

                var repaired = await Svn.RepairRenameAsync(wrapper, config, workingCopy, preview.Snapshot).ConfigureAwait(false);
                Equal("PostconditionFailed", repaired.Status);
                Equal(injectedSource, await File.ReadAllTextAsync(sourcePath).ConfigureAwait(false));
                Equal(originalDestination, await File.ReadAllTextAsync(destinationPath).ConfigureAwait(false));
                True(repaired.Details.Contains(sourcePath, StringComparison.Ordinal),
                    "the preserved concurrent source path was omitted from diagnostics");
            }).ConfigureAwait(false);

            await CheckAsync("rename repair retains and reports the chosen destination if svn move writes foreign bytes", async () =>
            {
                const string sourceName = "foreign-write source.txt";
                const string destinationName = "foreign-write destination.txt";
                const string originalDestination = "original chosen destination bytes\n";
                const string injectedDestination = "foreign bytes written after svn move\n";
                var sourcePath = Path.Combine(workingCopy, sourceName);
                var destinationPath = Path.Combine(workingCopy, destinationName);
                await File.WriteAllTextAsync(sourcePath, "tracked source baseline\n").ConfigureAwait(false);
                await RunRequiredAsync(svn, "add", "--non-interactive", "--no-auth-cache", "--config-dir", config,
                    "--", sourcePath + "@").ConfigureAwait(false);
                await RunRequiredAsync(svn, "commit", "--non-interactive", "--no-auth-cache", "--config-dir", config,
                    "--message", "Add foreign-write repair source", "--", workingCopy + "@").ConfigureAwait(false);
                await File.WriteAllTextAsync(destinationPath, originalDestination).ConfigureAwait(false);
                File.Delete(sourcePath);
                var preview = await Svn.GetRenameRepairSnapshotAsync(svn, config, workingCopy,
                    sourceName, destinationName).ConfigureAwait(false);
                True(preview.Status == "Ready", $"foreign-write fixture preview returned {preview.Status}: {preview.Details}");

                var wrapper = Path.Combine(root, "svn move wrapper");
                var wrapperScript = "#!/bin/sh\n" +
                    "real_svn=" + ShellQuote(svn) + "\n" +
                    "\"$real_svn\" \"$@\"\n" +
                    "result=$?\n" +
                    "if [ \"$result\" -eq 0 ] && [ \"$1\" = move ]; then\n" +
                    "  for argument do destination=$argument; done\n" +
                    "  printf '%s' " + ShellQuote(injectedDestination) + " > \"$destination\"\n" +
                    "fi\n" +
                    "exit \"$result\"\n";
                await File.WriteAllTextAsync(wrapper, wrapperScript).ConfigureAwait(false);
                SetUnixExecutable(wrapper);

                var repaired = await Svn.RepairRenameAsync(wrapper, config, workingCopy, preview.Snapshot).ConfigureAwait(false);
                Equal("PostconditionFailed", repaired.Status);
                Equal(injectedDestination, await File.ReadAllTextAsync(destinationPath).ConfigureAwait(false));
                var recoveryDirectories = Directory.GetDirectories(workingCopy, ".sourcegit-svn-repair-*", SearchOption.TopDirectoryOnly);
                Equal(1, recoveryDirectories.Length);
                var originalBackup = Path.Combine(recoveryDirectories[0], "original-destination");
                True(File.Exists(originalBackup), "the chosen destination bytes were not retained in recovery");
                Equal(originalDestination, await File.ReadAllTextAsync(originalBackup).ConfigureAwait(false));
                True(repaired.Details.Contains(originalBackup, StringComparison.Ordinal),
                    "the exact protected destination path was omitted from diagnostics");
            }).ConfigureAwait(false);
        }
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static void SetUnixExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The SVN mutation wrapper is only used on Unix.");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
