using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SourceGit.Native;

internal static partial class Program
{
    private const string ChangelistStateVariable = "SOURCEGIT_SVN_REGRESSION_CHANGELIST_STATE";
    private const string ChangelistSecondPathVariable = "SOURCEGIT_SVN_REGRESSION_CHANGELIST_SECOND_PATH";
    private const string ChangelistFlagsVariable = "SOURCEGIT_SVN_REGRESSION_CHANGELIST_FLAGS";

    private static async Task RunChangelistFakeChecksAsync()
    {
        using var temp = new TemporaryDirectory("sourcegit-svn-changelists");
        var executable = GetFixtureExecutable();
        var config = Path.Combine(temp.Path, "config with spaces");
        Directory.CreateDirectory(config);
        var workingCopyPath = CreateFakeWorkingCopy(temp.Path);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(workingCopyPath))!;
        const string targetName = "-- target @ 文档🌱.txt";
        await File.WriteAllTextAsync(Path.Combine(root, targetName), "bytes remain unchanged\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(root, "second target.txt"), "second bytes remain unchanged\n").ConfigureAwait(false);

        await CheckAsync("changelist names preserve Unicode and spaces while rejecting blank, control, malformed, and oversized input", () =>
        {
            const string validName = " -- list @ 🌱 ";
            var valid = Svn.TryValidateChangelistName(validName, out var validError);
            True(valid, validError);
            foreach (var invalid in new string[] { null, "", " \t ", "line\nbreak", new string('x', 256), "bad\uD800" })
                True(!Svn.TryValidateChangelistName(invalid, out _), $"invalid changelist name was accepted: {invalid}");

            var eligible = new Svn.SvnWorkingCopyEntry("target.txt", "normal", "none", Kind: "file");
            True(Svn.CanChangeChangelist(eligible), "a clean regular file was not eligible");
            True(Svn.CanChangeChangelist(eligible with { Item = "modified", Properties = "modified" }),
                "a text/property-modified file was not eligible");
            True(Svn.CanChangeChangelist(eligible with { Item = "added" }), "an added file was not eligible");
            foreach (var refused in new[]
            {
                eligible with { Kind = "dir" },
                eligible with { Item = "deleted" },
                eligible with { Item = "conflicted" },
                eligible with { Item = "modified", Properties = "conflicted" },
                eligible with { IsCopied = true },
                eligible with { IsSwitched = true },
                eligible with { IsTreeConflicted = true },
                eligible with { IsFileExternal = true },
                eligible with { MovedFrom = "old.txt" },
                eligible with { MovedTo = "new.txt" },
            })
                True(!Svn.CanChangeChangelist(refused), $"unsafe entry was eligible: {refused}");
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        await CheckAsync("full status retains clean changelist members for UI snapshots", async () =>
        {
            var calls = PrepareChangelistScenario(temp.Path, "wc_changelist_clean", workingCopyPath,
                "clean member.txt", item: "normal", properties: "none", membership: "clean list");
            var result = await Svn.GetStatusAsync(executable, config, root).ConfigureAwait(false);
            Equal("Ready", result.Status);
            var entry = result.Entries.Single(candidate => candidate.Path == "clean member.txt");
            Equal("normal", entry.Item);
            Equal("clean list", entry.Changelist);
            Equal(2, ReadInvocations(calls).Count);
        }).ConfigureAwait(false);

        await CheckAsync("set and reassign use exact option-safe name and file operands", async () =>
        {
            var before = await File.ReadAllBytesAsync(Path.Combine(root, targetName)).ConfigureAwait(false);
            var calls = PrepareChangelistScenario(temp.Path, "wc_changelist_assign", workingCopyPath,
                targetName, item: "modified", properties: "modified", membership: "old list");
            const string newName = "-- new list @ 配置 🌱";
            var result = await Svn.SetChangelistAsync(executable, config, root,
                new[] { new Svn.ChangelistSelection(targetName, "old list") }, newName).ConfigureAwait(false);
            Equal("Ready", result.Status);

            var invocations = ReadInvocations(calls);
            var mutation = invocations.Single(call => call.Arguments[0] == "changelist");
            var separator = Array.IndexOf(mutation.Arguments, "--");
            True(separator >= 0, "changelist command omitted the option terminator");
            True(Array.IndexOf(mutation.Arguments, "--depth") < separator, "depth option followed the option terminator");
            Equal("empty", ArgumentAfter(new Invocation(mutation.Arguments, mutation.StdinSha256,
                mutation.StdinReceived, mutation.PasswordArgumentPresent, mutation.ProcessId), "--depth"));
            Equal(newName, mutation.Arguments[separator + 1]);
            SequenceEqual(new[] { ExpectedPegOperand(Path.Combine(root, targetName)) }, mutation.Arguments.Skip(separator + 2));
            True(mutation.Arguments.Contains("--quiet", StringComparer.Ordinal), "changelist mutation was not quiet");
            True(mutation.Arguments.Contains("--no-auth-cache", StringComparer.Ordinal), "local mutation omitted the no-auth-cache guard");
            AssertConfigDirectory(mutation, config);
            var statuses = invocations.Where(call => call.Arguments[0] == "status").ToArray();
            Equal(2, statuses.Length);
            True(statuses.All(call => call.Arguments.Contains("--ignore-externals", StringComparer.Ordinal)),
                "fresh status did not retain the external-boundary guard");
            SequenceEqual(before, await File.ReadAllBytesAsync(Path.Combine(root, targetName)).ConfigureAwait(false));
        }).ConfigureAwait(false);

        await CheckAsync("remove clears clean membership without touching the selected file", async () =>
        {
            var calls = PrepareChangelistScenario(temp.Path, "wc_changelist_remove", workingCopyPath,
                "clean remove @ file.txt", item: "normal", properties: "none", membership: "existing list");
            var result = await Svn.RemoveFromChangelistAsync(executable, config, root,
                new[] { new Svn.ChangelistSelection("clean remove @ file.txt", "existing list") }).ConfigureAwait(false);
            Equal("Ready", result.Status);
            var mutation = ReadInvocations(calls).Single(call => call.Arguments[0] == "changelist");
            True(mutation.Arguments.Contains("--remove", StringComparer.Ordinal), "remove command omitted --remove");
            Equal("empty", ArgumentAfter(new Invocation(mutation.Arguments, mutation.StdinSha256,
                mutation.StdinReceived, mutation.PasswordArgumentPresent, mutation.ProcessId), "--depth"));
            var separator = Array.IndexOf(mutation.Arguments, "--");
            SequenceEqual(new[] { ExpectedPegOperand(Path.Combine(root, "clean remove @ file.txt")) }, mutation.Arguments.Skip(separator + 1));
        }).ConfigureAwait(false);

        await CheckAsync("stale membership and ineligible multi-selection abort before any write", async () =>
        {
            var staleCalls = PrepareChangelistScenario(temp.Path, "wc_changelist_stale", workingCopyPath,
                targetName, item: "modified", properties: "none", membership: "current list");
            var stale = await Svn.SetChangelistAsync(executable, config, root,
                new[] { new Svn.ChangelistSelection(targetName, "dialog snapshot list") }, "new list").ConfigureAwait(false);
            Equal("StaleSelection", stale.Status);
            True(!ReadInvocations(staleCalls).Any(call => call.Arguments[0] == "changelist"), "stale membership launched a mutation");

            var multiCalls = PrepareChangelistScenario(temp.Path, "wc_changelist_multi_ineligible", workingCopyPath,
                targetName, item: "modified", properties: "none", membership: null, secondPath: "second target.txt");
            var multi = await Svn.SetChangelistAsync(executable, config, root,
                new[]
                {
                    new Svn.ChangelistSelection(targetName, null),
                    new Svn.ChangelistSelection("second target.txt", null),
                }, "new list").ConfigureAwait(false);
            Equal("NotEligible", multi.Status);
            True(!ReadInvocations(multiCalls).Any(call => call.Arguments[0] == "changelist"), "partial eligibility launched a mutation");

            var emptyCalls = PrepareChangelistScenario(temp.Path, "wc_changelist_empty_snapshot", workingCopyPath,
                targetName, item: "modified", properties: "none", membership: null);
            var emptySnapshot = await Svn.SetChangelistAsync(executable, config, root,
                new[] { new Svn.ChangelistSelection(targetName, string.Empty) }, "new list").ConfigureAwait(false);
            Equal("Ready", emptySnapshot.Status);
            True(ReadInvocations(emptyCalls).Any(call => call.Arguments[0] == "changelist"),
                "an empty expected membership did not match an unassigned file");

            var sameCalls = PrepareChangelistScenario(temp.Path, "wc_changelist_same_membership", workingCopyPath,
                targetName, item: "modified", properties: "none", membership: "same list");
            var sameMembership = await Svn.SetChangelistAsync(executable, config, root,
                new[] { new Svn.ChangelistSelection(targetName, "same list") }, "same list").ConfigureAwait(false);
            Equal("Ready", sameMembership.Status);
            True(!ReadInvocations(sameCalls).Any(call => call.Arguments[0] == "changelist"),
                "an already-correct membership triggered a needless write");
        }).ConfigureAwait(false);

        await CheckAsync("external ancestors and nested symlinks are refused", async () =>
        {
            var externalRelative = Path.Combine("external folder", "external child.txt");
            var externalCalls = PrepareChangelistScenario(temp.Path, "wc_changelist_external_ancestor", workingCopyPath,
                externalRelative, item: "modified", properties: "none", membership: null);
            var external = await Svn.SetChangelistAsync(executable, config, root,
                new[] { new Svn.ChangelistSelection(externalRelative, null) }, "new list").ConfigureAwait(false);
            Equal("NotEligible", external.Status);
            True(!ReadInvocations(externalCalls).Any(call => call.Arguments[0] == "changelist"), "external-boundary selection launched a mutation");

            var outside = Path.Combine(temp.Path, "changelist symlink sentinel.txt");
            await File.WriteAllTextAsync(outside, "outside data\n").ConfigureAwait(false);
            var linkRelative = "symlink target.txt";
            var linkCalls = PrepareChangelistScenario(temp.Path, "wc_changelist_symlink", workingCopyPath,
                linkRelative, item: "modified", properties: "none", membership: null);
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(Path.Combine(root, linkRelative));
                var linkCreated = false;
                try
                {
                    File.CreateSymbolicLink(Path.Combine(root, linkRelative), outside);
                    linkCreated = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
                {
                    // Symlink safety runs where the current test filesystem supports symlinks.
                }
                if (linkCreated)
                {
                    var link = await Svn.SetChangelistAsync(executable, config, root,
                        new[] { new Svn.ChangelistSelection(linkRelative, null) }, "new list").ConfigureAwait(false);
                    Equal("UnsafePath", link.Status);
                    True(!ReadInvocations(linkCalls).Any(call => call.Arguments[0] == "changelist"), "symlink target launched a mutation");
                }
            }
        }).ConfigureAwait(false);

        await CheckAsync("failed, noisy, and cancelled mutations report possible partial changes", async () =>
        {
            var failureCalls = PrepareChangelistScenario(temp.Path, "wc_changelist_fail", workingCopyPath,
                targetName, item: "modified", properties: "none", membership: null);
            var failure = await Svn.SetChangelistAsync(executable, config, root,
                new[] { new Svn.ChangelistSelection(targetName, null) }, "new list").ConfigureAwait(false);
            Equal("Failed", failure.Status);
            True(failure.Details.Contains("may have changed partially", StringComparison.Ordinal), "failed mutation omitted partial-change notice");
            True(ReadInvocations(failureCalls).Any(call => call.Arguments[0] == "changelist"), "failure fixture did not reach mutation");

            var noisyCalls = PrepareChangelistScenario(temp.Path, "wc_changelist_noisy", workingCopyPath,
                targetName, item: "modified", properties: "none", membership: null);
            var noisy = await Svn.SetChangelistAsync(executable, config, root,
                new[] { new Svn.ChangelistSelection(targetName, null) }, "new list").ConfigureAwait(false);
            Equal("Failed", noisy.Status);
            True(noisy.Details.Contains("exceeded the 128 KiB", StringComparison.Ordinal), "truncated success output was accepted");
            True(File.Exists(noisyCalls + ".completed"), "large SVN output was not drained after the output limit");

            var cancelCalls = PrepareChangelistScenario(temp.Path, "wc_changelist_slow", workingCopyPath,
                targetName, item: "modified", properties: "none", membership: null);
            using var cancellation = new CancellationTokenSource();
            var operation = Svn.SetChangelistAsync(executable, config, root,
                new[] { new Svn.ChangelistSelection(targetName, null) }, "new list", cancellation.Token);
            var mutationStarted = false;
            for (var attempt = 0; attempt < 300; attempt++)
            {
                if (ReadInvocations(cancelCalls).Any(call => call.Arguments[0] == "changelist"))
                {
                    mutationStarted = true;
                    break;
                }
                await Task.Delay(10).ConfigureAwait(false);
            }
            cancellation.Cancel();
            var cancelled = await operation.ConfigureAwait(false);
            True(mutationStarted, "cancellation fixture did not reach mutation");
            Equal("Cancelled", cancelled.Status);
            True(cancelled.Details.Contains("may have changed partially", StringComparison.Ordinal), "post-start cancellation omitted partial-change notice");
        }).ConfigureAwait(false);
    }

    private static string PrepareChangelistScenario(
        string directory,
        string scenario,
        string workingCopyPath,
        string relativePath,
        string item,
        string properties,
        string membership,
        string secondPath = null)
    {
        var calls = PrepareHistoryScenario(directory, scenario, workingCopyPath);
        var root = Environment.GetEnvironmentVariable(WorkingCopyRootVariable) ?? string.Empty;
        var target = Path.GetFullPath(relativePath, root);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target))
            File.WriteAllText(target, "fake changelist file\n");
        Environment.SetEnvironmentVariable(ActionPathVariable, target);
        Environment.SetEnvironmentVariable(ActionItemVariable, item);
        Environment.SetEnvironmentVariable(ActionPropertiesVariable, properties);
        Environment.SetEnvironmentVariable(ActionKindVariable, "file");
        Environment.SetEnvironmentVariable(ChangelistFlagsVariable, scenario switch
        {
            "wc_changelist_copied" => "copied=\"true\"",
            "wc_changelist_switched" => "switched=\"true\"",
            "wc_changelist_tree_conflict" => "tree-conflicted=\"true\"",
            "wc_changelist_external" => "file-external=\"true\"",
            "wc_changelist_multi_ineligible" => string.Empty,
            _ => string.Empty,
        });
        Environment.SetEnvironmentVariable(ChangelistSecondPathVariable,
            secondPath == null ? null : Path.GetFullPath(secondPath, root));
        var statePath = calls + ".membership";
        File.WriteAllText(statePath, membership ?? string.Empty);
        Environment.SetEnvironmentVariable(ChangelistStateVariable, statePath);
        return calls;
    }

    private static void WriteWorkingCopyChangelistStatus(string scenario)
    {
        var root = Environment.GetEnvironmentVariable(WorkingCopyRootVariable) ?? string.Empty;
        var target = Environment.GetEnvironmentVariable(ActionPathVariable) ?? string.Empty;
        var secondTarget = Environment.GetEnvironmentVariable(ChangelistSecondPathVariable);
        var statePath = Environment.GetEnvironmentVariable(ChangelistStateVariable);
        var membership = File.Exists(statePath) ? File.ReadAllText(statePath) : string.Empty;
        var item = Environment.GetEnvironmentVariable(ActionItemVariable) ?? "modified";
        var properties = Environment.GetEnvironmentVariable(ActionPropertiesVariable) ?? "none";
        var flags = Environment.GetEnvironmentVariable(ChangelistFlagsVariable) ?? string.Empty;
        if (scenario == "wc_changelist_clean")
        {
            item = "normal";
            membership = "clean list";
        }

        string Entry(string path, string entryItem, string entryProperties, string entryFlags)
        {
            return $"<entry path=\"{System.Security.SecurityElement.Escape(path)}\" kind=\"file\"><wc-status item=\"{System.Security.SecurityElement.Escape(entryItem)}\" revision=\"12\" props=\"{System.Security.SecurityElement.Escape(entryProperties)}\" {entryFlags}/></entry>";
        }

        var targetEntries = new List<string>();
        var changelistEntries = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void AddEntry(string path, string entryItem, string entryProperties, string entryFlags, string entryMembership)
        {
            var xml = Entry(path, entryItem, entryProperties, entryFlags);
            if (string.IsNullOrEmpty(entryMembership))
                targetEntries.Add(xml);
            else
            {
                if (!changelistEntries.TryGetValue(entryMembership, out var entries))
                {
                    entries = new List<string>();
                    changelistEntries.Add(entryMembership, entries);
                }
                entries.Add(xml);
            }
        }
        AddEntry(target, item,
            scenario == "wc_changelist_multi_ineligible" ? "none" : properties,
            scenario == "wc_changelist_multi_ineligible" ? string.Empty : flags,
            membership);
        if (!string.IsNullOrEmpty(secondTarget))
        {
            var secondFlags = scenario == "wc_changelist_multi_ineligible" ? "copied=\"true\"" : string.Empty;
            targetEntries.Add(Entry(secondTarget, "modified", "none", secondFlags));
        }
        if (scenario == "wc_changelist_external_ancestor")
        {
            var ancestor = Path.GetDirectoryName(target) ?? root;
            targetEntries.Add($"<entry path=\"{System.Security.SecurityElement.Escape(ancestor)}\" kind=\"dir\"><wc-status item=\"external\" revision=\"12\" props=\"none\"/></entry>");
        }
        var changelistXml = string.Join(string.Empty, changelistEntries.Select(group =>
            $"<changelist name=\"{System.Security.SecurityElement.Escape(group.Key)}\">{string.Join(string.Empty, group.Value)}</changelist>"));
        Console.WriteLine($"<status><target path=\"{System.Security.SecurityElement.Escape(root)}\">{string.Join(string.Empty, targetEntries)}</target>{changelistXml}</status>");
    }

    private static void WriteChangelistMutationState(string[] args, string invocationPath)
    {
        var statePath = Environment.GetEnvironmentVariable(ChangelistStateVariable);
        if (string.IsNullOrEmpty(statePath))
            return;
        var separator = Array.IndexOf(args, "--");
        var membership = args.Contains("--remove", StringComparer.Ordinal) || separator < 0 || separator + 1 >= args.Length
            ? string.Empty
            : args[separator + 1];
        File.WriteAllText(statePath, membership);
    }

    private static async Task RunChangelistIntegrationAsync(string svn, string svnadmin, string tempDirectory)
    {
        var root = Path.Combine(tempDirectory, "working-copy-changelists");
        var repository = Path.Combine(root, "repository");
        var seed = Path.Combine(root, "seed tree");
        var workingCopy = Path.Combine(root, "working copy @ local");
        var config = Path.Combine(root, "isolated config");
        Directory.CreateDirectory(seed);
        Directory.CreateDirectory(config);

        const string modifiedName = "-- option @ 文档🌱.txt";
        const string cleanName = "clean member @ file.txt";
        const string addedName = "-- added @ file.txt";
        const string propertyName = "custom:changelist-regression";
        const string originalProperty = "preserve property value";
        var modifiedSeedPath = Path.Combine(seed, modifiedName);
        await File.WriteAllTextAsync(modifiedSeedPath, "repository contents\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(seed, cleanName), "clean file contents\n").ConfigureAwait(false);

        await RunRequiredAsync(svnadmin, "create", repository).ConfigureAwait(false);
        var repositoryUrl = new Uri(repository).AbsoluteUri;
        await RunRequiredAsync(svn, "import", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            "--message", "Create changelist regression fixture", "--", seed, repositoryUrl).ConfigureAwait(false);
        await RunRequiredAsync(svn, "checkout", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            "--", repositoryUrl, workingCopy).ConfigureAwait(false);

        var modifiedPath = Path.Combine(workingCopy, modifiedName);
        var cleanPath = Path.Combine(workingCopy, cleanName);
        await File.AppendAllTextAsync(modifiedPath, "local text edit\n").ConfigureAwait(false);
        await RunRequiredAsync(svn, "propset", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            propertyName, originalProperty, "--", modifiedPath + "@").ConfigureAwait(false);
        var originalBytes = await File.ReadAllBytesAsync(modifiedPath).ConfigureAwait(false);
        var initialStatus = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
        Equal("Ready", initialStatus.Status);
        var initialModified = initialStatus.Entries.Single(entry => entry.Path == modifiedName);
        Equal("modified", initialModified.Item);
        Equal("modified", initialModified.Properties);

        await CheckAsync("assign and reassign preserve local content, properties, and schedule", async () =>
        {
            const string firstName = "-- first list @ 工作 🌱";
            const string secondName = "second list 保持";
            var assigned = await Svn.SetChangelistAsync(svn, config, workingCopy,
                new[] { new Svn.ChangelistSelection(modifiedName, initialModified.Changelist) }, firstName).ConfigureAwait(false);
            True(assigned.Status == "Ready", $"assignment returned {assigned.Status}: {assigned.Details}");
            var firstStatus = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
            Equal(firstName, firstStatus.Entries.Single(entry => entry.Path == modifiedName).Changelist);

            var reassigned = await Svn.SetChangelistAsync(svn, config, workingCopy,
                new[] { new Svn.ChangelistSelection(modifiedName, firstName) }, secondName).ConfigureAwait(false);
            True(reassigned.Status == "Ready", $"reassignment returned {reassigned.Status}: {reassigned.Details}");
            var secondStatus = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
            var modifiedEntry = secondStatus.Entries.Single(entry => entry.Path == modifiedName);
            Equal(secondName, modifiedEntry.Changelist);
            Equal("modified", modifiedEntry.Item);
            Equal("modified", modifiedEntry.Properties);
            SequenceEqual(originalBytes, await File.ReadAllBytesAsync(modifiedPath).ConfigureAwait(false));
            Equal(originalProperty, (await RunCaptureAsync(svn, "propget", "--strict", "--non-interactive", "--no-auth-cache",
                "--config-dir", config, propertyName, "--", modifiedPath + "@").ConfigureAwait(false)).StandardOutput);

            var removed = await Svn.RemoveFromChangelistAsync(svn, config, workingCopy,
                new[] { new Svn.ChangelistSelection(modifiedName, secondName) }).ConfigureAwait(false);
            True(removed.Status == "Ready", $"removal returned {removed.Status}: {removed.Details}");
            var removedStatus = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
            var removedEntry = removedStatus.Entries.Single(entry => entry.Path == modifiedName);
            True(string.IsNullOrEmpty(removedEntry.Changelist), "removing membership left the file in a changelist");
            Equal("modified", removedEntry.Item);
            Equal("modified", removedEntry.Properties);
            SequenceEqual(originalBytes, await File.ReadAllBytesAsync(modifiedPath).ConfigureAwait(false));
        }).ConfigureAwait(false);

        await CheckAsync("clean and added files are explicit changelist targets and retain their schedules", async () =>
        {
            const string cleanList = "clean list @ 保持";
            const string seededCleanList = "existing clean membership";
            await RunRequiredAsync(svn, "changelist", "--quiet", "--depth", "empty", "--",
                seededCleanList, cleanPath + "@").ConfigureAwait(false);
            var cleanBefore = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
            var cleanEntryBefore = cleanBefore.Entries.Single(entry => entry.Path == cleanName);
            Equal("normal", cleanEntryBefore.Item);
            Equal(seededCleanList, cleanEntryBefore.Changelist);
            var clean = await Svn.SetChangelistAsync(svn, config, workingCopy,
                new[] { new Svn.ChangelistSelection(cleanName, cleanEntryBefore.Changelist) }, cleanList).ConfigureAwait(false);
            True(clean.Status == "Ready", $"clean file assignment returned {clean.Status}: {clean.Details}");
            var cleanStatus = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
            var cleanEntry = cleanStatus.Entries.Single(entry => entry.Path == cleanName);
            Equal("normal", cleanEntry.Item);
            Equal(cleanList, cleanEntry.Changelist);
            Equal("clean file contents\n", await File.ReadAllTextAsync(cleanPath).ConfigureAwait(false));
            var cleanRemoved = await Svn.RemoveFromChangelistAsync(svn, config, workingCopy,
                new[] { new Svn.ChangelistSelection(cleanName, cleanList) }).ConfigureAwait(false);
            True(cleanRemoved.Status == "Ready", $"clean membership removal returned {cleanRemoved.Status}: {cleanRemoved.Details}");
            var cleanAfterRemoval = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
            True(!cleanAfterRemoval.Entries.Any(entry => entry.Path == cleanName), "removing a clean membership left a changelist row");
            Equal("clean file contents\n", await File.ReadAllTextAsync(cleanPath).ConfigureAwait(false));

            var addedPath = Path.Combine(workingCopy, addedName);
            const string addedContents = "scheduled addition stays added\n";
            await File.WriteAllTextAsync(addedPath, addedContents).ConfigureAwait(false);
            await RunRequiredAsync(svn, "add", "--non-interactive", "--no-auth-cache", "--config-dir", config,
                "--", addedPath + "@").ConfigureAwait(false);
            var addedBefore = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
            var addedEntryBefore = addedBefore.Entries.Single(entry => entry.Path == addedName);
            Equal("added", addedEntryBefore.Item);
            const string addedList = "new files";
            var added = await Svn.SetChangelistAsync(svn, config, workingCopy,
                new[] { new Svn.ChangelistSelection(addedName, addedEntryBefore.Changelist) }, addedList).ConfigureAwait(false);
            True(added.Status == "Ready", $"added file assignment returned {added.Status}: {added.Details}");
            var addedStatus = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
            var addedEntry = addedStatus.Entries.Single(entry => entry.Path == addedName);
            Equal("added", addedEntry.Item);
            Equal(addedList, addedEntry.Changelist);
            Equal(addedContents, await File.ReadAllTextAsync(addedPath).ConfigureAwait(false));

            var removed = await Svn.RemoveFromChangelistAsync(svn, config, workingCopy,
                new[] { new Svn.ChangelistSelection(addedName, addedList) }).ConfigureAwait(false);
            True(removed.Status == "Ready", $"added-file membership removal returned {removed.Status}: {removed.Details}");
            var afterRemoval = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
            var afterRemovalEntry = afterRemoval.Entries.Single(entry => entry.Path == addedName);
            Equal("added", afterRemovalEntry.Item);
            True(string.IsNullOrEmpty(afterRemovalEntry.Changelist), "removing a changelist changed the added schedule");
            Equal(addedContents, await File.ReadAllTextAsync(addedPath).ConfigureAwait(false));
        }).ConfigureAwait(false);
    }
}
