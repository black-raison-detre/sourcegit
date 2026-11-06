using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SourceGit.Native;

internal static partial class Program
{
    private static async Task<int> RunPropertiesFakeInvocationAsync(string[] args, string scenario, string invocationPath)
    {
        switch (args[0])
        {
            case "info":
                if (args.Contains("--depth", StringComparer.Ordinal))
                    return WritePathOperationInfo(args);
                WriteWorkingCopyInfo();
                return 0;
            case "status":
                WritePropertiesStatus(args, scenario);
                return 0;
            case "proplist":
                if (scenario == "wc_properties_slow")
                {
                    await File.WriteAllTextAsync(invocationPath + ".slow-started", "started").ConfigureAwait(false);
                    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                }
                if (scenario == "wc_properties_fail_read")
                {
                    Console.Error.WriteLine("svn: E200000: synthetic property read failure");
                    return 1;
                }
                WritePropertiesXml(args, scenario);
                return 0;
            case "propset":
                if (scenario == "wc_properties_slow_mutation")
                    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                if (scenario == "wc_properties_fail_mutation")
                {
                    Console.Error.WriteLine("svn: E200000: synthetic property mutation failure");
                    return 1;
                }
                var fileIndex = Array.IndexOf(args, "--file");
                if (fileIndex < 0 || fileIndex + 1 >= args.Length)
                    return 2;
                var propertyValueFile = args[fileIndex + 1];
                if (!OperatingSystem.IsWindows())
                    await File.WriteAllTextAsync(invocationPath + ".mode", File.GetUnixFileMode(propertyValueFile).ToString()).ConfigureAwait(false);
                await File.WriteAllBytesAsync(invocationPath + ".value",
                    await File.ReadAllBytesAsync(propertyValueFile).ConfigureAwait(false)).ConfigureAwait(false);
                return 0;
            case "propdel":
                if (scenario == "wc_properties_slow_mutation")
                    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                if (scenario == "wc_properties_fail_mutation")
                {
                    Console.Error.WriteLine("svn: E200000: synthetic property mutation failure");
                    return 1;
                }
                return 0;
            default:
                Console.Error.WriteLine("svn: E200000: unexpected fake properties command");
                return 2;
        }
    }

    private static void WritePropertiesStatus(string[] args, string scenario)
    {
        var root = Environment.GetEnvironmentVariable(WorkingCopyRootVariable) ?? string.Empty;
        var propertyTarget = Environment.GetEnvironmentVariable(ActionPathVariable) ?? string.Empty;
        var hasDepth = args.Contains("--depth", StringComparer.Ordinal);
        if (!hasDepth)
        {
            var entries = string.Empty;
            if (scenario == "wc_properties_switched_ancestor")
            {
                var ancestor = Path.GetDirectoryName(propertyTarget) ?? root;
                entries = $"<entry path=\"{System.Security.SecurityElement.Escape(ancestor)}\" kind=\"dir\"><wc-status item=\"normal\" revision=\"12\" props=\"none\" switched=\"true\"/></entry>";
            }
            else if (scenario == "wc_properties_conflicted_ancestor")
            {
                var ancestor = Path.GetDirectoryName(propertyTarget) ?? root;
                entries = $"<entry path=\"{System.Security.SecurityElement.Escape(ancestor)}\" kind=\"dir\"><wc-status item=\"conflicted\" revision=\"12\" props=\"conflicted\"/></entry>";
            }
            Console.WriteLine($"<status><target path=\"{System.Security.SecurityElement.Escape(root)}\">{entries}</target></status>");
            return;
        }

        var target = DecodeFakePegPath(args[^1]);
        var kind = Directory.Exists(target) ? "dir" : "file";
        string item;
        string properties = "none";
        if (PathEqualsForFixture(target, propertyTarget))
        {
            item = scenario switch
            {
                "wc_properties_target_added" => "added",
                "wc_properties_target_missing" => "missing",
                "wc_properties_target_conflicted" => "conflicted",
                "wc_properties_target_switched" => "normal",
                _ => "normal",
            };
            if (scenario == "wc_properties_target_conflicted")
                properties = "conflicted";
        }
        else
        {
            item = scenario switch
            {
                "wc_properties_selected_tracked" => "normal",
                "wc_properties_selected_scheduled" => "added",
                "wc_properties_selected_ignored" => "ignored",
                "wc_properties_selected_conflicted" => "conflicted",
                _ => "unversioned",
            };
            if (item == "conflicted")
                properties = "conflicted";
        }

        var switched = PathEqualsForFixture(target, propertyTarget) && scenario == "wc_properties_target_switched"
            ? " switched=\"true\""
            : string.Empty;
        Console.WriteLine($"<status><target path=\"{System.Security.SecurityElement.Escape(root)}\"><entry path=\"{System.Security.SecurityElement.Escape(target)}\" kind=\"{kind}\"><wc-status item=\"{item}\" revision=\"12\" props=\"{properties}\"{switched}/></entry></target></status>");
    }

    private static void WritePropertiesXml(string[] args, string scenario)
    {
        if (scenario == "wc_properties_malformed")
        {
            Console.WriteLine("<properties><target></properties>");
            return;
        }
        if (scenario == "wc_properties_dtd")
        {
            Console.WriteLine("<!DOCTYPE properties [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><properties>&xxe;</properties>");
            return;
        }
        if (scenario == "wc_properties_large")
        {
            Console.Write("<properties><target path='x'><property name='custom:large'>");
            Console.Write(new string('x', 8 * 1024 * 1024 + 32));
            Console.WriteLine("</property></target></properties>");
            return;
        }

        var target = DecodeFakePegPath(args[^1]);
        if (scenario is "wc_properties_empty" or "wc_properties_target_empty")
        {
            Console.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?><properties />");
            return;
        }

        var multiline = scenario == "wc_properties_stale"
            ? "new snapshot value"
            : "first&#13;\nsecond\n Unicode 🌱 @\n";
        var whitespace = scenario == "wc_properties_whitespace_stale" ? "\t  \n" : "  \t \n";
        var ignore = scenario == "wc_properties_changed_ignore"
            ? "old*.tmp\r\nexternal-change\n"
            : "old*.tmp\r\nalready\n";
        Console.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?><properties><target path=\"" +
            System.Security.SecurityElement.Escape(target) + "\"><property name=\"svn:ignore\">" +
            ignore.Replace("\r", "&#13;", StringComparison.Ordinal).Replace("\n", "\n", StringComparison.Ordinal) +
            "</property><property name=\"custom:multi\">" + multiline +
            "</property><property name=\"custom:whitespace\">" + whitespace +
            "</property><property name=\"custom:binary\" encoding=\"base64\">AAH/</property>" +
            "<property name=\"svn:special\">&lt;*</property></target></properties>");
    }

    private static async Task RunPropertiesFakeChecksAsync()
    {
        using var temp = new TemporaryDirectory("sourcegit-svn-properties-fake");
        var executable = GetFixtureExecutable();
        var config = Path.Combine(temp.Path, "config");
        Directory.CreateDirectory(config);
        var workingCopyPath = CreateFakeWorkingCopy(temp.Path);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(workingCopyPath))!;
        var directoryRelative = Path.Combine("nested @ folder");
        var directory = Path.Combine(root, directoryRelative);
        var child = Path.Combine(directory, "new @ child.txt");
        await File.WriteAllTextAsync(child, "unversioned\n").ConfigureAwait(false);

        var createPropertyFile = typeof(Svn).GetMethod("CreateProtectedPropertyFile", BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, types: new[] { typeof(byte[]), typeof(Action<Stream, byte[]>) }, modifiers: null);
        True(createPropertyFile != null, "protected property-file writer seam was unavailable");
        string partialPropertyFile = null;
        Action<Stream, byte[]> failAfterPartialWrite = (stream, bytes) =>
        {
            partialPropertyFile = ((FileStream)stream).Name;
            stream.Write(bytes, 0, 1);
            throw new IOException("synthetic protected property-file write failure");
        };
        try
        {
            createPropertyFile.Invoke(null, new object[] { new byte[] { 42, 43 }, failAfterPartialWrite });
            throw new TestFailureException("protected property-file failure injection unexpectedly succeeded");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException)
        {
        }
        True(!string.IsNullOrEmpty(partialPropertyFile) && !File.Exists(partialPropertyFile),
            "a partial protected property file remained after an injected write failure");

        var invocations = PreparePropertiesScenario(temp.Path, "wc_properties_main", workingCopyPath, directory);
        var read = await Svn.GetPropertiesAsync(executable, config, workingCopyPath, directoryRelative).ConfigureAwait(false);
        Equal("Ready", read.Status);
        True(read.Snapshot != null && read.Snapshot.Token.Length == 64, "property snapshot did not expose a fresh SHA-256 token");
        Equal("dir", read.Snapshot.Kind);
        Equal("first\r\nsecond\n Unicode 🌱 @\n", read.Snapshot.Properties.Single(property => property.Name == "custom:multi").Value);
        var binary = read.Snapshot.Properties.Single(property => property.Name == "custom:binary");
        True(binary.IsBinary, "base64 property was exposed as editable text");
        Equal("AAH/", binary.Value);
        Equal("  \t \n", read.Snapshot.Properties.Single(property => property.Name == "custom:whitespace").Value);
        True(ReadInvocations(invocations).Any(call => call.Arguments[0] == "proplist" &&
            ArgumentAfter(call, "--depth") == "empty" && call.Arguments[^1].EndsWith("@", StringComparison.Ordinal)),
            "property read omitted depth-empty or terminal peg escaping");

        var setValue = "exact value\r\n🌱\n trailing spaces  \t";
        var set = await Svn.SetPropertyAsync(executable, config, workingCopyPath, read.Snapshot,
            "sourcegit:local", setValue).ConfigureAwait(false);
        Equal("Ready", set.Status);
        var mutation = ReadInvocations(invocations).Single(call => call.Arguments[0] == "propset");
        var filePath = ArgumentAfter(mutation, "--file");
        True(!File.Exists(filePath), "protected property value file was not removed after propset");
        True(!mutation.Arguments.Contains(setValue, StringComparer.Ordinal), "property content leaked into argv");
        True(mutation.Arguments.Contains("--depth", StringComparer.Ordinal) &&
             mutation.Arguments.Contains("empty", StringComparer.Ordinal) &&
             mutation.Arguments[^1].EndsWith("@", StringComparison.Ordinal), "propset was not depth-empty or peg-safe");
        True(!mutation.Arguments.Contains("--force", StringComparer.Ordinal), "propset used --force");
        Equal(setValue, await File.ReadAllTextAsync(invocations + ".value").ConfigureAwait(false));
        if (!OperatingSystem.IsWindows())
            Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                Enum.Parse<UnixFileMode>(await File.ReadAllTextAsync(invocations + ".mode").ConfigureAwait(false)));

        var beforeStale = read.Snapshot;
        var mutationsBeforeStale = ReadInvocations(invocations).Count(call => call.Arguments[0] is "propset" or "propdel");
        Environment.SetEnvironmentVariable(ScenarioVariable, "wc_properties_stale");
        var stale = await Svn.SetPropertyAsync(executable, config, workingCopyPath, beforeStale,
            "sourcegit:local", "must not be written").ConfigureAwait(false);
        Equal("StaleSnapshot", stale.Status);
        Equal(mutationsBeforeStale, ReadInvocations(invocations).Count(call => call.Arguments[0] is "propset" or "propdel"));

        Environment.SetEnvironmentVariable(ScenarioVariable, "wc_properties_main");
        var whitespaceSnapshot = await Svn.GetPropertiesAsync(executable, config, workingCopyPath, directoryRelative).ConfigureAwait(false);
        Equal("  \t \n", whitespaceSnapshot.Snapshot.Properties.Single(property => property.Name == "custom:whitespace").Value);
        var mutationsBeforeWhitespaceChange = ReadInvocations(invocations).Count(call => call.Arguments[0] is "propset" or "propdel");
        Environment.SetEnvironmentVariable(ScenarioVariable, "wc_properties_whitespace_stale");
        var whitespaceStale = await Svn.SetPropertyAsync(executable, config, workingCopyPath, whitespaceSnapshot.Snapshot,
            "sourcegit:local", "must not overwrite whitespace-only change").ConfigureAwait(false);
        Equal("StaleSnapshot", whitespaceStale.Status);
        Equal(mutationsBeforeWhitespaceChange, ReadInvocations(invocations).Count(call => call.Arguments[0] is "propset" or "propdel"));

        Environment.SetEnvironmentVariable(ScenarioVariable, "wc_properties_main");
        var current = await Svn.GetPropertiesAsync(executable, config, workingCopyPath, directoryRelative).ConfigureAwait(false);
        var binarySet = await Svn.SetPropertyAsync(executable, config, workingCopyPath, current.Snapshot,
            "custom:binary", "replace binary").ConfigureAwait(false);
        Equal("NotEligible", binarySet.Status);
        var binaryDelete = await Svn.DeletePropertyAsync(executable, config, workingCopyPath, current.Snapshot,
            "custom:binary").ConfigureAwait(false);
        Equal("NotEligible", binaryDelete.Status);

        var names = OperatingSystem.IsWindows()
            ? new[] { "literal[1] @.txt@" }
            : new[] { "wild*literal?.txt", "literal[1].txt", "back\\slash*.txt" };
        var combined = "old*.tmp\r\nalready\n";
        var ignoreSnapshot = new Svn.SvnPropertiesSnapshot(directoryRelative, "dir",
            Array.AsReadOnly(new[] { new Svn.SvnProperty("svn:ignore", combined, false) }), "test-token");
        True(Svn.TryCreateIgnoreValue(ignoreSnapshot, names, out var ignoreValue, out var ignoreError), ignoreError);
        True(ignoreValue.StartsWith(combined, StringComparison.Ordinal), "ignore composition changed existing bytes or order");
        if (!OperatingSystem.IsWindows())
        {
            True(ignoreValue.Contains("wild\\*literal\\?.txt", StringComparison.Ordinal), "ignore composition did not escape glob stars and question marks");
            True(ignoreValue.Contains("literal\\[1\\].txt", StringComparison.Ordinal), "ignore composition did not escape glob brackets");
            True(ignoreValue.Contains("back\\\\slash\\*.txt", StringComparison.Ordinal), "ignore composition did not escape backslash before wildcard");
        }
        else
        {
            True(ignoreValue.Contains("literal\\[1\\] @.txt@", StringComparison.Ordinal), "ignore composition did not escape glob brackets");
        }
        True(Svn.TryCreateIgnoreValue(ignoreSnapshot, new[] { "already" }, out var duplicateValue, out ignoreError), ignoreError);
        Equal(combined, duplicateValue);
        True(!Svn.TryCreateIgnoreValue(ignoreSnapshot, new[] { "bad\nname" }, out _, out _), "line break in filename was accepted for Ignore");

        Environment.SetEnvironmentVariable(ScenarioVariable, "wc_properties_main");
        invocations = PreparePropertiesScenario(temp.Path, "wc_properties_main", workingCopyPath, directory);
        var parentSnapshot = await Svn.GetPropertiesAsync(executable, config, workingCopyPath, directoryRelative).ConfigureAwait(false);
        var ignoredChild = Path.Combine(directory, OperatingSystem.IsWindows() ? "literal[1] @.txt@" : "wild*literal?.txt");
        await File.WriteAllTextAsync(ignoredChild, "unversioned\n").ConfigureAwait(false);
        var ignoreResult = await Svn.IgnoreAsync(executable, config, workingCopyPath, parentSnapshot.Snapshot,
            new[] { Path.GetRelativePath(root, ignoredChild) }).ConfigureAwait(false);
        Equal("Ready", ignoreResult.Status);
        True(ReadInvocations(invocations).Any(call => call.Arguments[0] == "propset" && call.Arguments[^1].EndsWith("@", StringComparison.Ordinal)),
            "Ignore did not use the shared depth-empty propset path");
        var ignoredSetter = ReadInvocations(invocations).Last(call => call.Arguments[0] == "propset");
        True(!ignoredSetter.Arguments.Contains("--force", StringComparer.Ordinal), "Ignore used --force");

        invocations = PreparePropertiesScenario(temp.Path, "wc_properties_selected_tracked", workingCopyPath, directory);
        parentSnapshot = await Svn.GetPropertiesAsync(executable, config, workingCopyPath, directoryRelative).ConfigureAwait(false);
        var trackedSelection = await Svn.IgnoreAsync(executable, config, workingCopyPath, parentSnapshot.Snapshot,
            new[] { Path.GetRelativePath(root, ignoredChild) }).ConfigureAwait(false);
        Equal("NotEligible", trackedSelection.Status);
        True(!ReadInvocations(invocations).Any(call => call.Arguments[0] == "propset"), "tracked Ignore selection mutated properties");

        invocations = PreparePropertiesScenario(temp.Path, "wc_properties_switched_ancestor", workingCopyPath, directory);
        var switched = await Svn.GetPropertiesAsync(executable, config, workingCopyPath, directoryRelative).ConfigureAwait(false);
        Equal("NotEligible", switched.Status);
        True(!ReadInvocations(invocations).Any(call => call.Arguments[0] == "proplist"), "switched ancestor reached property read");

        invocations = PreparePropertiesScenario(temp.Path, "wc_properties_slow", workingCopyPath, directory);
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = Svn.GetPropertiesAsync(executable, config, workingCopyPath, directoryRelative,
                cancellation.Token);
            for (var attempt = 0; attempt < 200 && !File.Exists(invocations + ".slow-started"); attempt++)
                await Task.Delay(25).ConfigureAwait(false);
            True(File.Exists(invocations + ".slow-started"), "cancellation fixture never reached proplist");
            cancellation.Cancel();
            var cancelled = await pending.ConfigureAwait(false);
            Equal("Cancelled", cancelled.Status);
        }
        True(ReadInvocations(invocations).Any(call => call.Arguments[0] == "proplist"), "cancellation fixture never reached proplist");

        invocations = PreparePropertiesScenario(temp.Path, "wc_properties_malformed", workingCopyPath, directory);
        Equal("Failed", (await Svn.GetPropertiesAsync(executable, config, workingCopyPath, directoryRelative).ConfigureAwait(false)).Status);
        invocations = PreparePropertiesScenario(temp.Path, "wc_properties_dtd", workingCopyPath, directory);
        Equal("Failed", (await Svn.GetPropertiesAsync(executable, config, workingCopyPath, directoryRelative).ConfigureAwait(false)).Status);
        invocations = PreparePropertiesScenario(temp.Path, "wc_properties_large", workingCopyPath, directory);
        Equal("TooLarge", (await Svn.GetPropertiesAsync(executable, config, workingCopyPath, directoryRelative).ConfigureAwait(false)).Status);

        s_passed += 31;
    }

    private static async Task RunPropertiesIntegrationAsync(string svn, string svnadmin, string tempDirectory)
    {
        var directory = Path.Combine(tempDirectory, "properties file-url fixture");
        var repository = Path.Combine(directory, "repository");
        var seed = Path.Combine(directory, "seed");
        var workingCopy = Path.Combine(directory, "working copy @ root");
        var nested = Path.Combine(seed, "nested @ folder");
        Directory.CreateDirectory(nested);
        const string fileRelative = "nested @ folder/tracked @ note.txt@";
        await File.WriteAllTextAsync(Path.Combine(seed, fileRelative.Replace('/', Path.DirectorySeparatorChar)), "initial\n").ConfigureAwait(false);
        await RunRequiredAsync(svnadmin, "create", repository).ConfigureAwait(false);
        var repositoryUrl = new Uri(repository).AbsoluteUri;
        await RunRequiredAsync(svn, "import", "--non-interactive", "--no-auth-cache", "--message", "Create properties fixture",
            seed, repositoryUrl).ConfigureAwait(false);
        await RunRequiredAsync(svn, "checkout", "--non-interactive", "--no-auth-cache", repositoryUrl, workingCopy).ConfigureAwait(false);

        var rootProperties = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, ".").ConfigureAwait(false);
        Equal("Ready", rootProperties.Status);
        Equal("dir", rootProperties.Snapshot.Kind);
        Equal(0, rootProperties.Snapshot.Properties.Count);
        var rootSet = await Svn.SetPropertyAsync(svn, string.Empty, workingCopy, rootProperties.Snapshot,
            "sourcegit:root", "root value\r\n🌱").ConfigureAwait(false);
        Equal("Ready", rootSet.Status);
        var rootRead = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, ".").ConfigureAwait(false);
        Equal("root value\r\n🌱", rootRead.Snapshot.Properties.Single(property => property.Name == "sourcegit:root").Value);
        Equal("Ready", (await Svn.DeletePropertyAsync(svn, string.Empty, workingCopy, rootRead.Snapshot,
            "sourcegit:root").ConfigureAwait(false)).Status);

        var file = Path.Combine(workingCopy, fileRelative.Replace('/', Path.DirectorySeparatorChar));
        var fileSnapshot = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, fileRelative).ConfigureAwait(false);
        Equal("Ready", fileSnapshot.Status);
        Equal("file", fileSnapshot.Snapshot.Kind);
        const string exactText = "one\r\n two 🌱\nlast line  \t";
        Equal("Ready", (await Svn.SetPropertyAsync(svn, string.Empty, workingCopy, fileSnapshot.Snapshot,
            "sourcegit:multi", exactText).ConfigureAwait(false)).Status);
        var exactRead = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, fileRelative).ConfigureAwait(false);
        Equal(exactText, exactRead.Snapshot.Properties.Single(property => property.Name == "sourcegit:multi").Value);

        var binaryPath = Path.Combine(tempDirectory, "binary property bytes");
        var binaryBytes = new byte[] { 0, 1, 255, 10 };
        await File.WriteAllBytesAsync(binaryPath, binaryBytes).ConfigureAwait(false);
        await RunRequiredAsync(svn, "propset", "--file", binaryPath, "custom:binary", "--", file + "@").ConfigureAwait(false);
        var withBinary = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, fileRelative).ConfigureAwait(false);
        var binary = withBinary.Snapshot.Properties.Single(property => property.Name == "custom:binary");
        True(binary.IsBinary, "binary local property was not marked read-only");
        Equal(Convert.ToBase64String(binaryBytes), binary.Value);
        Equal("NotEligible", (await Svn.DeletePropertyAsync(svn, string.Empty, workingCopy, withBinary.Snapshot,
            "custom:binary").ConfigureAwait(false)).Status);

        var staleSnapshot = exactRead.Snapshot;
        await RunRequiredAsync(svn, "propset", "sourcegit:multi", "outside change", "--", file + "@").ConfigureAwait(false);
        Equal("StaleSnapshot", (await Svn.SetPropertyAsync(svn, string.Empty, workingCopy, staleSnapshot,
            "sourcegit:multi", "must not overwrite").ConfigureAwait(false)).Status);
        var fresh = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, fileRelative).ConfigureAwait(false);
        Equal("Ready", (await Svn.DeletePropertyAsync(svn, string.Empty, workingCopy, fresh.Snapshot,
            "sourcegit:multi").ConfigureAwait(false)).Status);

        fresh = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, fileRelative).ConfigureAwait(false);
        const string whitespaceOnly = " \t \n";
        Equal("Ready", (await Svn.SetPropertyAsync(svn, string.Empty, workingCopy, fresh.Snapshot,
            "sourcegit:whitespace", whitespaceOnly).ConfigureAwait(false)).Status);
        var whitespaceSnapshot = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, fileRelative).ConfigureAwait(false);
        Equal(whitespaceOnly, whitespaceSnapshot.Snapshot.Properties.Single(property => property.Name == "sourcegit:whitespace").Value);
        const string externalWhitespaceOnly = "\t\n  ";
        var externalWhitespaceFile = Path.Combine(tempDirectory, "external whitespace property bytes");
        await File.WriteAllTextAsync(externalWhitespaceFile, externalWhitespaceOnly, new UTF8Encoding(false)).ConfigureAwait(false);
        await RunRequiredAsync(svn, "propset", "--file", externalWhitespaceFile, "sourcegit:whitespace", "--", file + "@").ConfigureAwait(false);
        Equal("StaleSnapshot", (await Svn.SetPropertyAsync(svn, string.Empty, workingCopy, whitespaceSnapshot.Snapshot,
            "sourcegit:whitespace", "must not overwrite whitespace-only change").ConfigureAwait(false)).Status);
        var afterWhitespaceRace = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, fileRelative).ConfigureAwait(false);
        Equal(externalWhitespaceOnly, afterWhitespaceRace.Snapshot.Properties.Single(property => property.Name == "sourcegit:whitespace").Value);

        var ignoreRelative = "nested @ folder";
        var ignoreDirectory = Path.Combine(workingCopy, ignoreRelative);
        var ignoreSnapshot = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, ignoreRelative).ConfigureAwait(false);
        Equal("Ready", (await Svn.SetPropertyAsync(svn, string.Empty, workingCopy, ignoreSnapshot.Snapshot,
            "sourcegit:local", "preserve me").ConfigureAwait(false)).Status);
        ignoreSnapshot = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, ignoreRelative).ConfigureAwait(false);
        Equal("Ready", (await Svn.SetPropertyAsync(svn, string.Empty, workingCopy, ignoreSnapshot.Snapshot,
            "svn:ignore", "existing*.tmp\r\n").ConfigureAwait(false)).Status);
        ignoreSnapshot = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, ignoreRelative).ConfigureAwait(false);
        var originalIgnore = ignoreSnapshot.Snapshot.Properties.Single(property => property.Name == "svn:ignore").Value;
        var propgetBeforeIgnore = await RunCaptureAsync(svn, "propget", "--strict", "svn:ignore", "--non-interactive",
            "--no-auth-cache", "--", ignoreDirectory + "@").ConfigureAwait(false);
        Equal(originalIgnore, propgetBeforeIgnore.StandardOutput);

        var selectedNames = new List<string> { "literal @ trail.txt@" };
        var unselectedName = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            selectedNames.Add("wild*literal?.txt");
            selectedNames.Add("literal[1].txt");
            selectedNames.Add("back\\slash*.txt");
            unselectedName = "wildXliteralA.txt";
        }
        var selectedPaths = new List<string>();
        foreach (var name in selectedNames)
        {
            var path = Path.Combine(ignoreDirectory, name);
            await File.WriteAllTextAsync(path, "unversioned bytes\n").ConfigureAwait(false);
            selectedPaths.Add(Path.GetRelativePath(workingCopy, path));
        }
        if (!string.IsNullOrEmpty(unselectedName))
            await File.WriteAllTextAsync(Path.Combine(ignoreDirectory, unselectedName), "must stay unversioned\n").ConfigureAwait(false);

        var ignored = await Svn.IgnoreAsync(svn, string.Empty, workingCopy, ignoreSnapshot.Snapshot,
            selectedPaths).ConfigureAwait(false);
        True(ignored.Status == "Ready", $"Ignore returned {ignored.Status}: {ignored.Details}");
        var updatedIgnore = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, ignoreRelative).ConfigureAwait(false);
        var ignoreValue = updatedIgnore.Snapshot.Properties.Single(property => property.Name == "svn:ignore").Value;
        True(ignoreValue.StartsWith(originalIgnore, StringComparison.Ordinal),
            $"Ignore changed the exact preexisting bytes reported by svn propget; before={EscapePropertyValue(originalIgnore)}, after={EscapePropertyValue(ignoreValue)}");
        Equal("preserve me", updatedIgnore.Snapshot.Properties.Single(property => property.Name == "sourcegit:local").Value);
        foreach (var name in selectedNames)
            Equal("ignored", await GetLocalStatusItemAsync(svn, Path.Combine(ignoreDirectory, name)).ConfigureAwait(false));
        if (!string.IsNullOrEmpty(unselectedName))
            Equal("unversioned", await GetLocalStatusItemAsync(svn, Path.Combine(ignoreDirectory, unselectedName)).ConfigureAwait(false));

        var addedPath = Path.Combine(ignoreDirectory, "now tracked.txt");
        await File.WriteAllTextAsync(addedPath, "tracked now\n").ConfigureAwait(false);
        await RunRequiredAsync(svn, "add", "--depth", "empty", "--non-interactive", "--no-auth-cache", "--", addedPath + "@").ConfigureAwait(false);
        var beforeRejectedIgnore = updatedIgnore.Snapshot;
        var rejected = await Svn.IgnoreAsync(svn, string.Empty, workingCopy, beforeRejectedIgnore,
            new[] { Path.GetRelativePath(workingCopy, addedPath) }).ConfigureAwait(false);
        Equal("NotEligible", rejected.Status);
        var afterRejectedIgnore = await Svn.GetPropertiesAsync(svn, string.Empty, workingCopy, ignoreRelative).ConfigureAwait(false);
        Equal(ignoreValue, afterRejectedIgnore.Snapshot.Properties.Single(property => property.Name == "svn:ignore").Value);

        s_passed += 22;
    }

    private static string PreparePropertiesScenario(string directory, string scenario, string workingCopyPath, string propertyTarget)
    {
        var invocations = PrepareHistoryScenario(directory, scenario, workingCopyPath);
        Environment.SetEnvironmentVariable(ActionPathVariable, Path.GetFullPath(propertyTarget));
        return invocations;
    }

    private static async Task<string> GetLocalStatusItemAsync(string svn, string path)
    {
        var output = await RunCaptureAsync(svn, "status", "--xml", "--no-ignore", "--ignore-externals", "--depth", "empty",
            "--non-interactive", "--no-auth-cache", "--", path + "@").ConfigureAwait(false);
        return XDocument.Parse(output.StandardOutput).Descendants("wc-status").Single().Attribute("item")?.Value ?? string.Empty;
    }

    private static string EscapePropertyValue(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
