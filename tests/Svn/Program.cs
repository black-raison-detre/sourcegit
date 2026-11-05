using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using SourceGit.Native;

internal static class Program
{
    private const string ScenarioVariable = "SOURCEGIT_SVN_REGRESSION_SCENARIO";
    private const string InvocationVariable = "SOURCEGIT_SVN_REGRESSION_INVOCATIONS";
    private const string WorkingCopyRootVariable = "SOURCEGIT_SVN_REGRESSION_WC_ROOT";
    private const string ActionPathVariable = "SOURCEGIT_SVN_REGRESSION_ACTION_PATH";
    private const string ActionItemVariable = "SOURCEGIT_SVN_REGRESSION_ACTION_ITEM";
    private const string ActionPropertiesVariable = "SOURCEGIT_SVN_REGRESSION_ACTION_PROPERTIES";
    private const string ActionKindVariable = "SOURCEGIT_SVN_REGRESSION_ACTION_KIND";
    private const string UndoSentinelVariable = "SOURCEGIT_SVN_REGRESSION_UNDO_SENTINEL";
    private const string RepositoryUuidVariable = "SOURCEGIT_SVN_REGRESSION_REPOSITORY_UUID";
    private const string BaseRevisionVariable = "SOURCEGIT_SVN_REGRESSION_BASE_REVISION";
    private static readonly string[] s_knownFixturePasswords =
    {
        "regression-secret-42",
        "remember-only-in-stdin",
        "embedded-secret",
        "1",
        "9",
        "secret-boundary-password-unique",
        "short-user-test-secret",
        "blank-config-secret",
        "policy-secret",
    };
    private static readonly string s_escapedSnapshotName = OperatingSystem.IsWindows() ? "raw % @ Unicode🌱.txt" : "raw % @\\Unicode🌱.txt";
    private static int s_passed;
    private static bool s_workingCopyLinksAvailable;

    private static async Task<int> Main(string[] args)
    {
        if (IsFakeInvocation(args))
            return await RunFakeClientAsync(args).ConfigureAwait(false);

        try
        {
            if (args.Length == 1 && args[0] == "--integration")
            {
                await RunIntegrationAsync().ConfigureAwait(false);
                Console.WriteLine("SVN authenticated local-server integration checks passed.");
                return 0;
            }

            if (args.Length != 0)
                throw new TestFailureException($"Unknown argument: {args[0]}");

            await RunFakeClientChecksAsync().ConfigureAwait(false);
            Console.WriteLine($"{s_passed} SVN backend regression checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static bool IsFakeInvocation(string[] args)
    {
        return args.Length > 0 && (args[0] is "info" or "log" or "proplist" or "cat" or "status" or "diff" or "add" or "delete" or "revert" || args.Contains("--version", StringComparer.Ordinal));
    }

    private static async Task<int> RunFakeClientAsync(string[] args)
    {
        var scenario = Environment.GetEnvironmentVariable(ScenarioVariable) ?? "ready";
        var invocationPath = Environment.GetEnvironmentVariable(InvocationVariable);
        if (scenario == "stdin_backpressure")
        {
            await RecordInvocationAsync(invocationPath, args, input: null).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }

        var input = args.Contains("--password-from-stdin", StringComparer.Ordinal)
            ? await Console.In.ReadLineAsync().ConfigureAwait(false)
            : null;
        await RecordInvocationAsync(invocationPath, args, input).ConfigureAwait(false);

        if ((args[0] is "add" or "delete" or "revert") && scenario.StartsWith("wc_action_", StringComparison.Ordinal))
        {
            if (args[0] == "revert" && scenario.StartsWith("wc_action_undo_", StringComparison.Ordinal))
            {
                var target = args[^1];
                if (target.EndsWith("@", StringComparison.Ordinal))
                    target = target[..^1];

                if (scenario is "wc_action_undo_pristine_fail" or "wc_action_undo_pristine_slow" or "wc_action_undo_cleanup_fail")
                    await File.WriteAllTextAsync(target, "fake pristine replacement\n").ConfigureAwait(false);
                if (scenario == "wc_action_undo_cleanup_fail")
                {
                    var recovery = Directory.GetDirectories(Path.GetDirectoryName(target)!, ".sourcegit-svn-undo-*").Single();
                    await File.WriteAllTextAsync(Path.Combine(recovery, "unrecognized-recovery-data.txt"), "preserve this data\n").ConfigureAwait(false);
                    return 0;
                }
                if (scenario is "wc_action_undo_symlink_fail" or "wc_action_undo_symlink_slow")
                {
                    var sentinel = Environment.GetEnvironmentVariable(UndoSentinelVariable) ?? string.Empty;
                    File.CreateSymbolicLink(target, sentinel);
                    if (scenario == "wc_action_undo_symlink_slow")
                        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                    Console.Error.WriteLine("svn: E200000: synthetic revert failure after replacing the target with a symlink");
                    return 1;
                }
                if (scenario == "wc_action_undo_pristine_slow")
                    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                if (scenario == "wc_action_undo_pristine_fail")
                {
                    Console.Error.WriteLine("svn: E200000: synthetic revert failure after writing pristine bytes");
                    return 1;
                }
            }

            if (scenario == "wc_action_slow")
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            if (scenario == "wc_action_fail")
            {
                Console.Error.WriteLine("svn: E200000: synthetic working-copy action failure");
                return 1;
            }
            return 0;
        }

        if (args.Contains("--version", StringComparer.Ordinal))
        {
            switch (scenario)
            {
                case "probe_fail":
                    Console.Error.WriteLine("version command failed");
                    return 2;
                case "probe_invalid_version":
                    Console.WriteLine("not an svn version");
                    return 0;
                case "probe_unknown_stores":
                    Console.WriteLine("svn, version 1.14.3 (r123)\ncompiled for regression testing");
                    return 0;
                case "probe_empty_stores":
                    Console.WriteLine("svn, version 1.14.3 (r123)\n\nThe following authentication credential caches are available:\n(none)\n");
                    return 0;
                default:
                    Console.WriteLine("svn, version 1.14.3 (r123)\n\nThe following authentication credential caches are available:\n\n* Test keyring\n* Test encrypted disk cache\n\n");
                    return 0;
            }
        }

        if (args[0] == "info" && scenario.StartsWith("wc_status_", StringComparison.Ordinal))
        {
            WriteWorkingCopyInfo();
            return 0;
        }

        if (args[0] == "info" && scenario.StartsWith("wc_action_", StringComparison.Ordinal))
        {
            WriteWorkingCopyInfo();
            return 0;
        }

        if (args[0] == "status" && scenario.StartsWith("wc_status_", StringComparison.Ordinal))
        {
            if (scenario == "wc_status_slow")
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            if (scenario == "wc_status_malformed")
            {
                Console.WriteLine("<status><target></status>");
                return 0;
            }
            if (scenario == "wc_status_dtd")
            {
                Console.WriteLine("<!DOCTYPE status [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><status><target path='x'><entry path='&xxe;'><wc-status item='modified' props='none'/></entry></target></status>");
                return 0;
            }
            if (scenario == "wc_status_large")
            {
                Console.Write("<status><target path='x'><!--");
                await Console.Out.WriteAsync(new string('x', 8 * 1024 * 1024 + 64)).ConfigureAwait(false);
                Console.WriteLine("--></target></status>");
                return 0;
            }
            if (scenario == "wc_status_bad_revision")
            {
                var root = Environment.GetEnvironmentVariable(WorkingCopyRootVariable) ?? string.Empty;
                Console.WriteLine($"<status><target path=\"{System.Security.SecurityElement.Escape(root)}\"><entry path=\"{System.Security.SecurityElement.Escape(Path.Combine(root, "bad.txt"))}\"><wc-status item=\"added\" revision=\"-2\" props=\"none\"/></entry></target></status>");
                return 0;
            }
            if (scenario is "wc_status_missing_binary" or "wc_status_missing_special")
            {
                var root = Environment.GetEnvironmentVariable(WorkingCopyRootVariable) ?? string.Empty;
                var name = scenario == "wc_status_missing_binary" ? "missing @ binary.txt" : "missing @ special.txt";
                Console.WriteLine($"<status><target path=\"{System.Security.SecurityElement.Escape(root)}\"><entry path=\"{System.Security.SecurityElement.Escape(Path.Combine(root, name))}\"><wc-status item=\"missing\" revision=\"10\" props=\"none\"/></entry></target></status>");
                return 0;
            }

            WriteWorkingCopyStatus();
            return 0;
        }

        if (args[0] == "status" && scenario.StartsWith("wc_action_", StringComparison.Ordinal))
        {
            WriteWorkingCopyActionStatus();
            return 0;
        }

        if (args[0] == "diff" && scenario.StartsWith("wc_status_", StringComparison.Ordinal))
        {
            if (scenario == "wc_status_diff_slow")
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            if (scenario == "wc_status_patch_large")
            {
                var output = Console.OpenStandardOutput();
                var block = new byte[64 * 1024];
                for (var i = 0; i < 129; i++)
                    await output.WriteAsync(block.AsMemory()).ConfigureAwait(false);
                return 0;
            }
            if (args.Contains("--properties-only", StringComparer.Ordinal))
                Console.Write("Property changes on: modified @ file.txt\n___________________________________________________________________\nAdded: custom:local\n   + value\n");
            else if (args[^1].Contains("binary-lookalike", StringComparison.Ordinal))
                Console.Write("Index: binary-lookalike @ file.txt\n===================================================================\n--- binary-lookalike @ file.txt\t(BASE)\n+++ binary-lookalike @ file.txt\t(WORKING)\n@@ -1,3 +1,3 @@\n-Binary files are ordinary source text\n+Cannot display: file marked as a binary type.\n Binary files also appear in this context\n");
            else if (args[^1].Contains("binary", StringComparison.Ordinal))
                await Console.OpenStandardOutput().WriteAsync(new byte[] { 0xC3, 0x28 }).ConfigureAwait(false);
            else
                Console.Write("Index: modified @ file.txt\n===================================================================\n--- modified @ file.txt\t(BASE)\n+++ modified @ file.txt\t(WORKING)\n@@ -1 +1 @@\n-old\n+local edit\n");
            return 0;
        }

        if (args[0] == "proplist" && scenario.StartsWith("wc_status_", StringComparison.Ordinal))
        {
            var target = args[^1];
            if (scenario == "wc_status_missing_binary")
            {
                Console.WriteLine("<properties><target path='fixture'><property name='svn:mime-type'>application/octet-stream</property></target></properties>");
                return 0;
            }
            var special = target.Contains("special", StringComparison.Ordinal);
            Console.WriteLine(special
                ? "<properties><target path='fixture'><property name='svn:special'>*</property></target></properties>"
                : "<properties><target path='fixture'/></properties>");
            return 0;
        }

        if (args[0] == "cat" && scenario.StartsWith("wc_status_", StringComparison.Ordinal))
        {
            if (scenario == "wc_status_cat_slow")
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes("BASE missing contents\n")).ConfigureAwait(false);
            return 0;
        }

        if (args[0] == "log")
        {
            switch (scenario)
            {
                case "changed_paths_file_diff":
                case "changed_paths_file_diff_auth":
                case "changed_paths_file_diff_slow":
                case "changed_paths_file_diff_overflow":
                    if (scenario == "changed_paths_file_diff_auth" && !args.Contains("--username", StringComparer.Ordinal))
                    {
                        Console.Error.WriteLine("svn: E170001: Authentication required");
                        return 1;
                    }
                    Console.WriteLine("<log><logentry revision=\"6\"><paths>" +
                        "<path action=\"M\" kind=\"file\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/Unicode @ %\\slash.txt</path>" +
                        "<path action=\"A\" kind=\"file\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/empty @ file.txt</path>" +
                        "<path action=\"A\" kind=\"file\" copyfrom-path=\"/repos/archive/来源 @%/baseline.txt\" copyfrom-rev=\"3\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/copied @ file.txt</path>" +
                        "<path action=\"D\" kind=\"file\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/deleted.bin</path>" +
                        "<path action=\"R\" kind=\"file\" copyfrom-path=\"/repos/archive/ignored-copy\" copyfrom-rev=\"4\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/replaced @ file.txt</path>" +
                        "<path action=\"M\" kind=\"file\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/binary @ file.dat</path>" +
                        "<path action=\"M\" kind=\"file\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/invalid-utf8.bin</path>" +
                        "<path action=\"A\" kind=\"file\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/utf16.txt</path>" +
                        "<path action=\"M\" kind=\"file\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/special-link</path>" +
                        "<path action=\"A\" kind=\"file\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/overflow.dat</path>" +
                        "<path action=\"D\" kind=\"unknown\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/unknown-deleted.txt</path>" +
                        "<path action=\"M\" kind=\"dir\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/directory</path>" +
                        "</paths></logentry></log>");
                    return 0;
                case "changed_paths_main":
                case "changed_paths_auth":
                    Console.WriteLine("<log><logentry revision=\"6\"><paths><path action=\"A\" kind=\"file\" copyfrom-path=\"/repos/archive/来源 @ 文件.txt\" copyfrom-rev=\"4\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/文档.txt</path><path action=\"D\" kind=\"file\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/old.txt</path><path action=\"R\" kind=\"dir\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/replaced</path><path action=\"M\" kind=\"file\">/repos/branches/历史 @ scope/child @ nested/deeper @ scope/slash\\name.txt</path><path action=\"A\" kind=\"dir\" copyfrom-path=\"/repos/archive/祖先 @\" copyfrom-rev=\"5\">/repos/branches/历史 @ scope</path><path action=\"M\" kind=\"dir\">/repos/branches/历史 @ scope/child @ nested</path><path action=\"M\" kind=\"file\">/repos/branches/历史 @ scope-neighbor/child @ nested/outside.txt</path><path action=\"M\" kind=\"file\">/repos/other/unrelated.txt</path></paths></logentry></log>");
                    return 0;
                case "changed_paths_gap":
                    Console.WriteLine("<log><logentry revision=\"8\"><paths><path action=\"M\" kind=\"file\">/repos/other/unrelated.txt</path><path action=\"M\" kind=\"file\">/repos/branches/历史 @ scope-neighbor/file.txt</path></paths></logentry></log>");
                    return 0;
                case "changed_paths_dtd":
                    Console.WriteLine("<!DOCTYPE log [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><log><logentry revision=\"6\"><paths><path action=\"M\" kind=\"file\">&xxe;</path></paths></logentry></log>");
                    return 0;
                case "changed_paths_malformed":
                    Console.WriteLine("<log><logentry revision=\"not-a-number\"><paths><path action=\"M\" kind=\"file\">/repos/branches/path</path></paths></logentry></log>");
                    return 0;
                case "changed_paths_large":
                    Console.Write("<log><logentry revision=\"6\"><paths><path action=\"M\" kind=\"file\">/repos/");
                    await Console.Out.WriteAsync(new string('x', 12 * 1024 * 1024 + 8)).ConfigureAwait(false);
                    Console.WriteLine("</path></paths></logentry></log>");
                    return 0;
                case "changed_paths_slow":
                    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                    return 0;
                case "changed_paths_auth_leak":
                    Console.Error.WriteLine($"svn: E170001: Authentication failed: {input}");
                    return 1;
                case "history_paged":
                    var revisionIndex = Array.IndexOf(args, "--revision");
                    if (revisionIndex >= 0 && revisionIndex + 1 < args.Length && args[revisionIndex + 1] == "7:0")
                        Console.WriteLine("<log><logentry revision=\"3\"><msg>earlier\nrevision</msg></logentry></log>");
                    else
                        Console.WriteLine("<log><logentry revision=\"12\"><author>alice</author><date>2026-10-28T09:30:00.000000Z</date><msg>Unicode add\nSecond line: 保持 Unicode 🌱\nthird line</msg></logentry><logentry revision=\"8\"><author></author><date></date><msg></msg></logentry></log>");
                    return 0;
                case "history_auth_retry":
                    if (!args.Contains("--username", StringComparer.Ordinal))
                    {
                        Console.Error.WriteLine("svn: E170001: Authentication required");
                        return 1;
                    }
                    Console.WriteLine("<log><logentry revision=\"12\"><author>alice</author><date>2026-10-28T09:30:00.000000Z</date><msg>authenticated</msg></logentry></log>");
                    return 0;
                case "history_auth_leak":
                    Console.Error.WriteLine($"svn: E170001: Authentication failed: {input}");
                    return 1;
                case "history_dtd":
                    Console.WriteLine("<!DOCTYPE log [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><log><logentry revision=\"12\"><msg>&xxe;</msg></logentry></log>");
                    return 0;
                case "history_malformed":
                    Console.WriteLine("<log><logentry revision=\"not-a-number\"></logentry></log>");
                    return 0;
                case "history_large":
                    Console.Write("<log><logentry revision=\"12\"><msg>");
                    await Console.Out.WriteAsync(new string('x', 12 * 1024 * 1024 + 8)).ConfigureAwait(false);
                    Console.WriteLine("</msg></logentry></log>");
                    return 0;
                case "history_slow":
                    await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                    return 0;
                case "history_failure":
                    Console.Error.WriteLine("svn: E200000: synthetic log failure");
                    return 1;
                default:
                    Console.WriteLine("<log></log>");
                    return 0;
            }
        }

        if (args[0] == "info" && scenario.StartsWith("history_", StringComparison.Ordinal))
        {
            if (scenario == "history_missing_wcinfo")
                Console.WriteLine("<info><entry revision=\"12\"><url>svn://fixture/repo</url><repository><root>svn://fixture</root></repository></entry></info>");
            else
                WriteWorkingCopyInfo();
            return 0;
        }

        if (args[0] == "info" && scenario.StartsWith("changed_paths_", StringComparison.Ordinal))
        {
            if (!args.Contains("--revision", StringComparer.Ordinal))
            {
                WriteWorkingCopyInfo();
                return 0;
            }

            if (scenario == "changed_paths_auth" && !args.Contains("--username", StringComparer.Ordinal))
            {
                Console.Error.WriteLine("svn: E170001: Authentication required");
                return 1;
            }

            var endpoint = args[^1];
            if (args.Contains("--revision", StringComparer.Ordinal) && endpoint.Contains("/repos/branches/", StringComparison.Ordinal))
            {
                var peg = endpoint.LastIndexOf('@');
                var url = peg > 0 ? endpoint[..peg] : endpoint;
                Console.WriteLine($"<info><entry kind=\"file\" path=\"fixture\" revision=\"6\"><url>{System.Security.SecurityElement.Escape(url)}</url><repository><root>svn://fixture</root></repository></entry></info>");
                return 0;
            }

            Console.WriteLine("<info><entry kind=\"file\" path=\"fixture\" revision=\"6\"><url>svn://fixture/repos/branches/%E5%8E%86%E5%8F%B2%20%40%20scope/child%20%40%20nested/deeper%20%40%20scope</url><repository><root>svn://fixture</root></repository></entry></info>");
            return 0;
        }

        if (args[0] == "proplist" && scenario.StartsWith("changed_paths_file_diff", StringComparison.Ordinal))
        {
            var target = args[^1];
            if (target.Contains("binary%20%40%20file.dat", StringComparison.Ordinal))
                Console.WriteLine("<properties><target path=\"fixture\"><property name=\"svn:mime-type\">application/octet-stream</property></target></properties>");
            else if (target.Contains("deleted.bin", StringComparison.Ordinal))
                Console.WriteLine("<properties><target path=\"fixture\"><property name=\"svn:mime-type\">application/octet-stream</property></target></properties>");
            else if (target.Contains("special-link", StringComparison.Ordinal))
                Console.WriteLine("<properties><target path=\"fixture\"><property name=\"svn:special\">*</property></target></properties>");
            else
                Console.WriteLine("<properties><target path=\"fixture\" /></properties>");
            return 0;
        }

        if (args[0] == "cat" && scenario.StartsWith("changed_paths_file_diff", StringComparison.Ordinal))
        {
            if (scenario == "changed_paths_file_diff_slow")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                return 0;
            }

            var target = args[^1];
            if (scenario == "changed_paths_file_diff_overflow" && target.Contains("overflow.dat", StringComparison.Ordinal))
            {
                var output = Console.OpenStandardOutput();
                var block = new byte[64 * 1024];
                while (true)
                    await output.WriteAsync(block.AsMemory()).ConfigureAwait(false);
            }

            byte[] bytes;
            if (target.Contains("empty%20%40%20file.txt", StringComparison.Ordinal))
                bytes = Array.Empty<byte>();
            else if (target.Contains("binary%20%40%20file.dat", StringComparison.Ordinal))
                bytes = new byte[] { 0, 255, 1, 2 };
            else if (target.Contains("invalid-utf8.bin", StringComparison.Ordinal))
                bytes = new byte[] { 0xC3, 0x28 };
            else if (target.Contains("utf16.txt", StringComparison.Ordinal))
                bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("BOM text 🌱\n")).ToArray();
            else if (target.Contains("special-link", StringComparison.Ordinal))
                bytes = Encoding.UTF8.GetBytes("link safe target");
            else if (target.Contains("baseline.txt", StringComparison.Ordinal))
                bytes = Encoding.UTF8.GetBytes("copy source baseline\n");
            else if (target.Contains("copied%20%40%20file.txt", StringComparison.Ordinal))
                bytes = Encoding.UTF8.GetBytes(target.EndsWith("@3", StringComparison.Ordinal) ? "copy source baseline\n" : "copied destination\n");
            else if (target.Contains("Unicode%20%40%20%25%5Cslash.txt", StringComparison.Ordinal))
                bytes = Encoding.UTF8.GetBytes(target.EndsWith("@5", StringComparison.Ordinal) ? "before Unicode @ % \\ slash\n" : "after Unicode @ % \\ slash\n");
            else if (target.Contains("deleted.bin", StringComparison.Ordinal))
                bytes = new byte[] { 3, 0, 4 };
            else if (target.Contains("replaced%20%40%20file.txt", StringComparison.Ordinal))
                bytes = Encoding.UTF8.GetBytes(target.EndsWith("@5", StringComparison.Ordinal) ? "old replacement target\n" : "new replacement target\n");
            else if (target.Contains("unknown-deleted.txt", StringComparison.Ordinal))
                bytes = Encoding.UTF8.GetBytes("unknown-kind deleted baseline\n");
            else
                bytes = Encoding.UTF8.GetBytes("overflow test\n");

            await Console.OpenStandardOutput().WriteAsync(bytes.AsMemory()).ConfigureAwait(false);
            return 0;
        }

        switch (scenario)
        {
            case "slow":
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                return 0;
            case "stderr_flood":
                await Console.Error.WriteAsync(new string('x', 1024 * 1024)).ConfigureAwait(false);
                Console.Error.WriteLine(" E200000: synthetic failure");
                return 1;
            case "stderr_secret_at_limit":
                await Console.Error.WriteAsync(new string('x', 128 * 1024 - 8)).ConfigureAwait(false);
                await Console.Error.WriteAsync("secret-boundary-password-unique").ConfigureAwait(false);
                await Console.Error.WriteLineAsync(" E170001: synthetic failure").ConfigureAwait(false);
                return 1;
            case "auth_fail_secret":
                if (args.Contains("--username", StringComparer.Ordinal))
                {
                    var username = args[Array.IndexOf(args, "--username") + 1];
                    Console.Error.WriteLine($"Authorization failed for {username}: {input} E170001");
                    return 1;
                }

                Console.Error.WriteLine("svn: E170001: Authentication required");
                return 1;
            case "auth_succeed":
                if (!args.Contains("--username", StringComparer.Ordinal))
                {
                    Console.Error.WriteLine("svn: E170001: Authentication required");
                    return 1;
                }

                WriteValidInfo(args);
                return 0;
            case "auth_wrong":
                if (args.Contains("--username", StringComparer.Ordinal))
                {
                    Console.Error.WriteLine("svn: E170001: Authentication failed");
                    return 1;
                }

                Console.Error.WriteLine("svn: E170001: Authentication required");
                return 1;
            case "access_denied":
                if (args.Contains("--username", StringComparer.Ordinal))
                {
                    Console.Error.WriteLine("svn: E175013: Access to this resource is forbidden");
                    return 1;
                }

                Console.Error.WriteLine("svn: E170001: Authentication required");
                return 1;
            case "incomplete_xml":
                Console.WriteLine("<info><entry revision=\"9\"><url>svn://fixture/repo</url></entry></info>");
                return 0;
            case "untrusted_xml":
                Console.WriteLine("<!DOCTYPE info [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><info><entry revision=\"9\"><url>&xxe;</url><repository><root>svn://fixture</root></repository></entry></info>");
                return 0;
            case "userinfo_xml":
                Console.WriteLine("<info><entry revision=\"9\"><url>https://xml-user:xml-metadata-secret@svn.example.test/repo</url><repository><root>https://svn.example.test</root></repository></entry></info>");
                return 0;
            default:
                WriteValidInfo(args);
                return 0;
        }
    }

    private static async Task RecordInvocationAsync(string invocationPath, string[] args, string input)
    {
        if (string.IsNullOrEmpty(invocationPath))
            return;

        var containsPasswordArgument = args.Contains("--password", StringComparer.Ordinal) ||
            args.Any(argument => argument.Length > 1024 || s_knownFixturePasswords.Contains(argument, StringComparer.Ordinal) ||
                (argument.StartsWith("--password", StringComparison.Ordinal) &&
                 s_knownFixturePasswords.Any(password => argument.EndsWith("=" + password, StringComparison.Ordinal))));
        var invocation = new Invocation(SanitizeArguments(args), input == null ? null : Hash(input), input != null,
            containsPasswordArgument, Environment.ProcessId);
        await File.AppendAllTextAsync(invocationPath, JsonSerializer.Serialize(invocation) + "\n").ConfigureAwait(false);
    }

    private static void WriteValidInfo(string[] args)
    {
        var separator = Array.IndexOf(args, "--");
        var url = separator >= 0 && separator + 1 < args.Length ? args[separator + 1] : "svn://fixture/repo@";
        Console.WriteLine($"<info><entry revision=\"9\"><url>{System.Security.SecurityElement.Escape(url)}</url><repository><root>svn://fixture</root></repository></entry></info>");
    }

    private static void WriteWorkingCopyInfo()
    {
        var workingCopyRoot = Environment.GetEnvironmentVariable(WorkingCopyRootVariable) ?? string.Empty;
        var scenario = Environment.GetEnvironmentVariable(ScenarioVariable);
        var baseRevision = Environment.GetEnvironmentVariable(BaseRevisionVariable) ?? "12";
        var repositoryUuid = Environment.GetEnvironmentVariable(RepositoryUuidVariable);
        var uuidElement = string.IsNullOrEmpty(repositoryUuid)
            ? string.Empty
            : $"<uuid>{System.Security.SecurityElement.Escape(repositoryUuid)}</uuid>";
        var url = scenario == "history_ssh"
            ? "svn+ssh://svn.example.test/repository/path"
            : "svn://fixture/repos/path%20with%20space%20%40";
        Console.WriteLine($"<info><entry kind=\"file\" path=\"fixture\" revision=\"{baseRevision}\"><url>{url}</url><repository><root>svn://fixture</root>{uuidElement}</repository><wc-info><wcroot-abspath>{System.Security.SecurityElement.Escape(workingCopyRoot)}</wcroot-abspath></wc-info></entry></info>");
    }

    private static void WriteWorkingCopyStatus()
    {
        var root = Environment.GetEnvironmentVariable(WorkingCopyRootVariable) ?? string.Empty;
        string Entry(string relative, string statusAttributes) =>
            $"<entry path=\"{System.Security.SecurityElement.Escape(Path.Combine(root, relative))}\"><wc-status {statusAttributes}/></entry>";

        var movedFrom = System.Security.SecurityElement.Escape(Path.Combine(root, "old @ file.txt"));
        var movedTo = System.Security.SecurityElement.Escape(Path.Combine(root, "new @ file.txt"));
        var escapedRoot = System.Security.SecurityElement.Escape(root);
        var external = System.Security.SecurityElement.Escape(Path.GetFullPath(Path.Combine(root, "..", "external wc")));
        var linkEntries = HasWorkingCopyLinkFixtures(root)
            ? Entry(Path.Combine("escape", "outside.txt"), "item=\"unversioned\" props=\"none\"") +
                Entry("special @ link", "item=\"unversioned\" props=\"none\"")
            : string.Empty;
        Console.WriteLine("<status><target path=\"" + escapedRoot + "\">" +
            Entry("modified @ file.txt", $"item=\"modified\" revision=\"12\" props=\"modified\" copied=\"false\" switched=\"true\" tree-conflicted=\"true\" moved-from=\"{movedFrom}\" moved-to=\"{movedTo}\"") +
            Entry("copied @ file.txt", "item=\"added\" revision=\"-1\" props=\"none\" copied=\"true\"") +
            Entry("unversioned @ file.txt", "item=\"unversioned\" props=\"none\"") +
            Entry("missing @ file.txt", "item=\"missing\" revision=\"11\" props=\"none\"") +
            Entry("directory change", "item=\"modified\" revision=\"12\" props=\"none\"") +
            Entry("unversioned directory", "item=\"unversioned\" props=\"none\"") +
            Entry("conflicted @ file.txt", "item=\"conflicted\" revision=\"12\" props=\"conflicted\"") +
            Entry("binary @ file.dat", "item=\"modified\" revision=\"12\" props=\"none\"") +
            Entry("binary-lookalike @ file.txt", "item=\"modified\" revision=\"12\" props=\"none\"") +
            Entry("switched.txt", "item=\"normal\" revision=\"12\" props=\"none\" switched=\"true\"") +
            Entry("deleted @ file.txt", "item=\"deleted\" revision=\"12\" props=\"none\"") +
            linkEntries +
            $"<entry path=\"{external}\"><wc-status item=\"external\" props=\"none\" file-external=\"true\"/></entry>" +
            "</target><changelist name=\"pending work\">" +
            Entry("changelist @ file.txt", "item=\"modified\" revision=\"12\" props=\"modified\"") +
            "</changelist></status>");
    }

    private static void WriteWorkingCopyActionStatus()
    {
        var root = Environment.GetEnvironmentVariable(WorkingCopyRootVariable) ?? string.Empty;
        var target = Environment.GetEnvironmentVariable(ActionPathVariable) ?? string.Empty;
        var item = Environment.GetEnvironmentVariable(ActionItemVariable) ?? "unversioned";
        var properties = Environment.GetEnvironmentVariable(ActionPropertiesVariable) ?? "none";
        var kind = Environment.GetEnvironmentVariable(ActionKindVariable) ?? "unknown";
        var kindAttribute = kind == "unknown" ? string.Empty : $" kind=\"{System.Security.SecurityElement.Escape(kind)}\"";
        var externalChild = Environment.GetEnvironmentVariable(ScenarioVariable) == "wc_action_external"
            ? $"<entry path=\"{System.Security.SecurityElement.Escape(Path.Combine(target, "linked.txt"))}\" kind=\"file\"><wc-status item=\"missing\" revision=\"12\" props=\"none\" file-external=\"true\"/></entry>"
            : string.Empty;
        Console.WriteLine($"<status><target path=\"{System.Security.SecurityElement.Escape(root)}\"><entry path=\"{System.Security.SecurityElement.Escape(target)}\"{kindAttribute}><wc-status item=\"{System.Security.SecurityElement.Escape(item)}\" revision=\"12\" props=\"{System.Security.SecurityElement.Escape(properties)}\"/></entry>{externalChild}</target></status>");
    }

    private static bool HasWorkingCopyLinkFixtures(string root)
    {
        try
        {
            return (File.GetAttributes(Path.Combine(root, "escape")) & FileAttributes.ReparsePoint) != 0 &&
                (File.GetAttributes(Path.Combine(root, "special @ link")) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task RunFakeClientChecksAsync()
    {
        using var temp = new TemporaryDirectory("sourcegit-svn-regression");
        var executable = GetFixtureExecutable();
        var config = Path.Combine(temp.Path, "a config & cache @ fixture");
        Directory.CreateDirectory(config);
        var workingCopyPath = CreateFakeWorkingCopy(temp.Path);

        await CheckAsync("working-copy root discovery handles nested repositories and Git precedence", async () =>
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(workingCopyPath));
            Equal(root, Svn.FindWorkingCopyRoot(workingCopyPath));

            var nestedGit = Path.Combine(root, "nested-git");
            Directory.CreateDirectory(Path.Combine(nestedGit, ".git"));
            Equal(string.Empty, Svn.FindWorkingCopyRoot(nestedGit));

            var outerGit = Path.Combine(temp.Path, "outer-git");
            Directory.CreateDirectory(Path.Combine(outerGit, ".git"));
            var nestedSvn = Path.Combine(outerGit, "nested-svn");
            Directory.CreateDirectory(Path.Combine(nestedSvn, ".svn"));
            await File.WriteAllTextAsync(Path.Combine(nestedSvn, ".svn", "wc.db"), "fixture").ConfigureAwait(false);
            Equal(nestedSvn, Svn.FindWorkingCopyRoot(nestedSvn));

            var sameFolder = Path.Combine(temp.Path, "same-folder");
            Directory.CreateDirectory(Path.Combine(sameFolder, ".git"));
            Directory.CreateDirectory(Path.Combine(sameFolder, ".svn"));
            await File.WriteAllTextAsync(Path.Combine(sameFolder, ".svn", "wc.db"), "fixture").ConfigureAwait(false);
            Equal(string.Empty, Svn.FindWorkingCopyRoot(sameFolder));
        }).ConfigureAwait(false);

        await CheckAsync("working-copy info preserves canonical root text, uses XML, --, and an @ terminator", async () =>
        {
            var invocations = PrepareHistoryScenario(temp.Path, "history_trailing_wcroot", workingCopyPath);
            var result = await Svn.GetWorkingCopyAsync(executable, config, workingCopyPath).ConfigureAwait(false);
            Equal("Ready", result.Status);
            Equal(Environment.GetEnvironmentVariable(WorkingCopyRootVariable), result.WorkingCopyRoot);
            Equal("svn://fixture/repos/path%20with%20space%20%40", result.Url);
            Equal("svn://fixture", result.RepositoryRoot);
            Equal(12L, result.Revision);

            var call = SingleInvocation(invocations);
            SequenceEqual(new[] { "info", "--xml", "--non-interactive", "--config-dir", Path.GetFullPath(config), "--no-auth-cache", "--", Path.GetFullPath(workingCopyPath) + "@" }, call.Arguments);
            True(!call.StdinReceived, "local WC info must not receive credentials");
        }).ConfigureAwait(false);

        await CheckAsync("server-only info without wc-info is rejected before history starts", async () =>
        {
            var invocations = PrepareHistoryScenario(temp.Path, "history_missing_wcinfo", workingCopyPath);
            var result = await Svn.GetWorkingCopyAsync(executable, config, workingCopyPath).ConfigureAwait(false);
            Equal("Failed", result.Status);
            Equal(1, ReadInvocations(invocations).Count);
        }).ConfigureAwait(false);

        await CheckAsync("svn+ssh working copies are rejected before log can launch a tunnel", async () =>
        {
            var invocations = PrepareHistoryScenario(temp.Path, "history_ssh", workingCopyPath);
            var workingCopy = await Svn.GetWorkingCopyAsync(executable, config, workingCopyPath).ConfigureAwait(false);
            Equal("Ready", workingCopy.Status);
            Equal("svn+ssh://svn.example.test/repository/path", workingCopy.Url);

            invocations = PrepareHistoryScenario(temp.Path, "history_ssh", workingCopyPath);
            var result = await Svn.GetHistoryAsync(executable, config, workingCopyPath).ConfigureAwait(false);
            Equal("UnsupportedTransport", result.Status);
            Equal(1, ReadInvocations(invocations).Count);
        }).ConfigureAwait(false);

        await CheckAsync("history pages keep gaps, complete Unicode messages, metadata blanks, and immutable targets", async () =>
        {
            const string expectedUrl = "svn://fixture/repos/path%20with%20space%20%40";
            var originalContents = await File.ReadAllTextAsync(workingCopyPath).ConfigureAwait(false);
            var firstCalls = PrepareHistoryScenario(temp.Path, "history_paged", workingCopyPath);
            var first = await Svn.GetHistoryAsync(executable, config, workingCopyPath, limit: 2, expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", first.Status);
            SequenceEqual(new long[] { 12, 8 }, first.Revisions.ConvertAll(revision => revision.Number));
            Equal("r12", first.Revisions[0].RevisionText);
            Equal("Unicode add", first.Revisions[0].Subject);
            Equal("Unicode add\nSecond line: 保持 Unicode 🌱\nthird line", first.Revisions[0].Message);
            Equal("alice", first.Revisions[0].Author);
            True(first.Revisions[0].Date.HasValue, "timestamp was not parsed");
            Equal(string.Empty, first.Revisions[1].Author);
            Equal(null, first.Revisions[1].Date);
            Equal(string.Empty, first.Revisions[1].Message);

            var calls = ReadInvocations(firstCalls);
            Equal(2, calls.Count);
            True(calls[0].Arguments.Contains("info", StringComparer.Ordinal), "working-copy info did not run first");
            var log = calls[1];
            True(log.Arguments.Contains("--no-auth-cache", StringComparer.Ordinal), "history may write an auth cache without Remember");
            Equal("HEAD:0", ArgumentAfter(log, "--revision"));
            Equal("2", ArgumentAfter(log, "--limit"));
            var target = log.Arguments[Array.IndexOf(log.Arguments, "--") + 1];
            Equal(expectedUrl + "@12", target);
            Equal(originalContents, await File.ReadAllTextAsync(workingCopyPath).ConfigureAwait(false));

            var nextCalls = PrepareHistoryScenario(temp.Path, "history_paged", workingCopyPath);
            var next = await Svn.GetHistoryAsync(executable, config, workingCopyPath, beforeRevision: 8, limit: 2, expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", next.Status);
            SequenceEqual(new long[] { 3 }, next.Revisions.ConvertAll(revision => revision.Number));
            Equal("7:0", ArgumentAfter(ReadInvocations(nextCalls)[1], "--revision"));
            Equal("earlier\nrevision", next.Revisions[0].Message);
            Equal(originalContents, await File.ReadAllTextAsync(workingCopyPath).ConfigureAwait(false));
        }).ConfigureAwait(false);

        await CheckAsync("metadata cache reuses parsed history, isolates identity, expires safely, and force refreshes", async () =>
        {
            const string expectedUrl = "svn://fixture/repos/path%20with%20space%20%40";
            const string password = "cache-password-must-not-be-written";
            var oldUuid = Environment.GetEnvironmentVariable(RepositoryUuidVariable);
            var oldBaseRevision = Environment.GetEnvironmentVariable(BaseRevisionVariable);
            var cache = Path.Combine(temp.Path, "history-metadata-cache");
            var credentials = new Svn.Credentials("cache-alice", password, false);
            try
            {
                Environment.SetEnvironmentVariable(RepositoryUuidVariable, "cache-repository-a");
                Environment.SetEnvironmentVariable(BaseRevisionVariable, "12");

                var firstCalls = PrepareHistoryScenario(temp.Path, "history_paged", workingCopyPath);
                var first = await Svn.GetHistoryAsync(executable, config, workingCopyPath, limit: 2,
                    credentials: credentials, expectedUrl: expectedUrl, cacheDirectory: cache).ConfigureAwait(false);
                Equal("Ready", first.Status);
                Equal(2, ReadInvocations(firstCalls).Count);
                Equal("cache-repository-a", (await Svn.GetWorkingCopyAsync(executable, config, workingCopyPath).ConfigureAwait(false)).RepositoryUuid);

                var cacheFile = Directory.GetFiles(cache, "*.svnmeta.json").Single();
                True(!File.ReadAllText(cacheFile).Contains(password, StringComparison.Ordinal), "cached metadata contains a password");
                if (!OperatingSystem.IsWindows())
                {
                    Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(cacheFile));
                    Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(cache));
                }

                var hitCalls = PrepareHistoryScenario(temp.Path, "history_paged", workingCopyPath);
                var hit = await Svn.GetHistoryAsync(executable, config, workingCopyPath, limit: 2,
                    credentials: credentials, expectedUrl: expectedUrl, cacheDirectory: cache).ConfigureAwait(false);
                Equal("Ready", hit.Status);
                Equal(1, ReadInvocations(hitCalls).Count);
                SequenceEqual(first.Revisions.ConvertAll(item => item.Number), hit.Revisions.ConvertAll(item => item.Number));

                async Task AssertIdentityMissAsync(string scenario, string configPath, Svn.Credentials identityCredentials, string uuid, string baseRevision)
                {
                    Environment.SetEnvironmentVariable(RepositoryUuidVariable, uuid);
                    Environment.SetEnvironmentVariable(BaseRevisionVariable, baseRevision);
                    var calls = PrepareHistoryScenario(temp.Path, scenario, workingCopyPath);
                    var result = await Svn.GetHistoryAsync(executable, configPath, workingCopyPath, limit: 2,
                        credentials: identityCredentials, expectedUrl: expectedUrl, cacheDirectory: cache).ConfigureAwait(false);
                    Equal("Ready", result.Status);
                    Equal(2, ReadInvocations(calls).Count);
                }

                await AssertIdentityMissAsync("history_paged", config, new Svn.Credentials("cache-bob", password, false),
                    "cache-repository-a", "12").ConfigureAwait(false);
                var otherConfig = Path.Combine(temp.Path, "other-svn-config");
                Directory.CreateDirectory(otherConfig);
                await AssertIdentityMissAsync("history_paged", otherConfig, credentials, "cache-repository-a", "12").ConfigureAwait(false);
                await AssertIdentityMissAsync("history_paged", config, credentials, "cache-repository-b", "12").ConfigureAwait(false);
                await AssertIdentityMissAsync("history_paged", config, credentials, "cache-repository-a", "13").ConfigureAwait(false);

                Environment.SetEnvironmentVariable(RepositoryUuidVariable, "cache-repository-a");
                Environment.SetEnvironmentVariable(BaseRevisionVariable, "12");
                var sameKeyCalls = PrepareHistoryScenario(temp.Path, "history_paged", workingCopyPath);
                var sameKey = await Svn.GetHistoryAsync(executable, config, workingCopyPath, limit: 2,
                    credentials: credentials, expectedUrl: expectedUrl, cacheDirectory: cache).ConfigureAwait(false);
                Equal("Ready", sameKey.Status);
                Equal(1, ReadInvocations(sameKeyCalls).Count);

                await File.WriteAllTextAsync(cacheFile, "{broken json").ConfigureAwait(false);
                var corruptCalls = PrepareHistoryScenario(temp.Path, "history_paged", workingCopyPath);
                Equal("Ready", (await Svn.GetHistoryAsync(executable, config, workingCopyPath, limit: 2,
                    credentials: credentials, expectedUrl: expectedUrl, cacheDirectory: cache).ConfigureAwait(false)).Status);
                Equal(2, ReadInvocations(corruptCalls).Count);

                await File.WriteAllTextAsync(cacheFile,
                    "{\"version\":1,\"kind\":\"history\",\"expiresUtcTicks\":1,\"xml\":\"<log></log>\"}").ConfigureAwait(false);
                var expiredCalls = PrepareHistoryScenario(temp.Path, "history_paged", workingCopyPath);
                Equal("Ready", (await Svn.GetHistoryAsync(executable, config, workingCopyPath, limit: 2,
                    credentials: credentials, expectedUrl: expectedUrl, cacheDirectory: cache).ConfigureAwait(false)).Status);
                Equal(2, ReadInvocations(expiredCalls).Count);

                var invalidXmlExpiry = DateTimeOffset.UtcNow.AddMinutes(4).Ticks.ToString(CultureInfo.InvariantCulture);
                await File.WriteAllTextAsync(cacheFile,
                    "{\"version\":1,\"kind\":\"history\",\"expiresUtcTicks\":" + invalidXmlExpiry + ",\"xml\":\"<log><logentry revision='bad'/></log>\"}").ConfigureAwait(false);
                var invalidXmlCalls = PrepareHistoryScenario(temp.Path, "history_paged", workingCopyPath);
                Equal("Ready", (await Svn.GetHistoryAsync(executable, config, workingCopyPath, limit: 2,
                    credentials: credentials, expectedUrl: expectedUrl, cacheDirectory: cache).ConfigureAwait(false)).Status);
                Equal(2, ReadInvocations(invalidXmlCalls).Count);

                var refreshCalls = PrepareHistoryScenario(temp.Path, "history_paged", workingCopyPath);
                var refreshed = await Svn.GetHistoryAsync(executable, config, workingCopyPath, limit: 2,
                    credentials: credentials, expectedUrl: expectedUrl, cacheDirectory: cache, refresh: true).ConfigureAwait(false);
                Equal("Ready", refreshed.Status);
                Equal(2, ReadInvocations(refreshCalls).Count);

                var failedRefreshCalls = PrepareHistoryScenario(temp.Path, "history_failure", workingCopyPath);
                var failedRefresh = await Svn.GetHistoryAsync(executable, config, workingCopyPath, limit: 2,
                    credentials: credentials, expectedUrl: expectedUrl, cacheDirectory: cache, refresh: true).ConfigureAwait(false);
                Equal("Failed", failedRefresh.Status);
                Equal(2, ReadInvocations(failedRefreshCalls).Count);

                var afterFailedRefreshCalls = PrepareHistoryScenario(temp.Path, "history_paged", workingCopyPath);
                var afterFailedRefresh = await Svn.GetHistoryAsync(executable, config, workingCopyPath, limit: 2,
                    credentials: credentials, expectedUrl: expectedUrl, cacheDirectory: cache).ConfigureAwait(false);
                Equal("Ready", afterFailedRefresh.Status);
                Equal(2, ReadInvocations(afterFailedRefreshCalls).Count);
            }
            finally
            {
                Environment.SetEnvironmentVariable(RepositoryUuidVariable, oldUuid);
                Environment.SetEnvironmentVariable(BaseRevisionVariable, oldBaseRevision);
            }
        }).ConfigureAwait(false);

        await CheckAsync("changed-path metadata cache avoids historical remote queries and refreshes on demand", async () =>
        {
            const string expectedUrl = "svn://fixture/repos/path%20with%20space%20%40";
            var oldUuid = Environment.GetEnvironmentVariable(RepositoryUuidVariable);
            var oldBaseRevision = Environment.GetEnvironmentVariable(BaseRevisionVariable);
            var cache = Path.Combine(temp.Path, "changed-path-metadata-cache");
            try
            {
                Environment.SetEnvironmentVariable(RepositoryUuidVariable, "cache-paths-repository");
                Environment.SetEnvironmentVariable(BaseRevisionVariable, "12");

                var firstCalls = PrepareHistoryScenario(temp.Path, "changed_paths_main", workingCopyPath);
                var first = await Svn.GetChangedPathsAsync(executable, config, workingCopyPath, 6,
                    expectedUrl: expectedUrl, cacheDirectory: cache).ConfigureAwait(false);
                Equal("Ready", first.Status);
                Equal(3, ReadInvocations(firstCalls).Count);

                var hitCalls = PrepareHistoryScenario(temp.Path, "changed_paths_main", workingCopyPath);
                var hit = await Svn.GetChangedPathsAsync(executable, config, workingCopyPath, 6,
                    expectedUrl: expectedUrl, cacheDirectory: cache).ConfigureAwait(false);
                Equal("Ready", hit.Status);
                Equal(1, ReadInvocations(hitCalls).Count);
                SequenceEqual(first.Paths.ConvertAll(path => path.Path), hit.Paths.ConvertAll(path => path.Path));

                var refreshCalls = PrepareHistoryScenario(temp.Path, "changed_paths_main", workingCopyPath);
                var refreshed = await Svn.GetChangedPathsAsync(executable, config, workingCopyPath, 6,
                    expectedUrl: expectedUrl, cacheDirectory: cache, refresh: true).ConfigureAwait(false);
                Equal("Ready", refreshed.Status);
                Equal(3, ReadInvocations(refreshCalls).Count);

                var failedHistoryRefreshCalls = PrepareHistoryScenario(temp.Path, "history_failure", workingCopyPath);
                var failedHistoryRefresh = await Svn.GetHistoryAsync(executable, config, workingCopyPath,
                    expectedUrl: expectedUrl, cacheDirectory: cache, refresh: true, refreshChangedPathsRevision: 6).ConfigureAwait(false);
                Equal("Failed", failedHistoryRefresh.Status);
                Equal(2, ReadInvocations(failedHistoryRefreshCalls).Count);

                var afterFailedHistoryRefreshCalls = PrepareHistoryScenario(temp.Path, "changed_paths_main", workingCopyPath);
                var afterFailedHistoryRefresh = await Svn.GetChangedPathsAsync(executable, config, workingCopyPath, 6,
                    expectedUrl: expectedUrl, cacheDirectory: cache).ConfigureAwait(false);
                Equal("Ready", afterFailedHistoryRefresh.Status);
                Equal(3, ReadInvocations(afterFailedHistoryRefreshCalls).Count);
            }
            finally
            {
                Environment.SetEnvironmentVariable(RepositoryUuidVariable, oldUuid);
                Environment.SetEnvironmentVariable(BaseRevisionVariable, oldBaseRevision);
            }
        }).ConfigureAwait(false);

        await CheckAsync("unusable metadata-cache directory fails open to live SVN", async () =>
        {
            var oldUuid = Environment.GetEnvironmentVariable(RepositoryUuidVariable);
            var oldBaseRevision = Environment.GetEnvironmentVariable(BaseRevisionVariable);
            var unusableCache = Path.Combine(temp.Path, "cache-path-is-a-file");
            await File.WriteAllTextAsync(unusableCache, "preserve this file").ConfigureAwait(false);
            try
            {
                Environment.SetEnvironmentVariable(RepositoryUuidVariable, "cache-unusable-directory-repository");
                Environment.SetEnvironmentVariable(BaseRevisionVariable, "12");
                var calls = PrepareHistoryScenario(temp.Path, "history_paged", workingCopyPath);
                var result = await Svn.GetHistoryAsync(executable, config, workingCopyPath,
                    limit: 2, expectedUrl: "svn://fixture/repos/path%20with%20space%20%40", cacheDirectory: unusableCache).ConfigureAwait(false);
                Equal("Ready", result.Status);
                Equal(2, ReadInvocations(calls).Count);
                Equal("preserve this file", await File.ReadAllTextAsync(unusableCache).ConfigureAwait(false));
            }
            finally
            {
                Environment.SetEnvironmentVariable(RepositoryUuidVariable, oldUuid);
                Environment.SetEnvironmentVariable(BaseRevisionVariable, oldBaseRevision);
            }
        }).ConfigureAwait(false);

        await CheckAsync("history auth can be retried with stdin-only credentials and no auth-cache writes", async () =>
        {
            const string password = "regression-secret-42";
            const string expectedUrl = "svn://fixture/repos/path%20with%20space%20%40";
            var firstCalls = PrepareHistoryScenario(temp.Path, "history_auth_retry", workingCopyPath);
            var unauthenticated = await Svn.GetHistoryAsync(executable, config, workingCopyPath, expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("AuthenticationRequired", unauthenticated.Status);
            var firstLog = ReadInvocations(firstCalls)[1];
            True(firstLog.Arguments.Contains("--no-auth-cache", StringComparer.Ordinal), "unauthenticated history may write an auth cache");
            True(!firstLog.Arguments.Contains("--username", StringComparer.Ordinal), "credentials were sent on the unauthenticated attempt");

            var retryCalls = PrepareHistoryScenario(temp.Path, "history_auth_retry", workingCopyPath);
            var authenticated = await Svn.GetHistoryAsync(executable, config, workingCopyPath,
                credentials: new Svn.Credentials("alice", password, false), expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", authenticated.Status);
            var retryLog = ReadInvocations(retryCalls)[1];
            AssertCredentialArguments(retryLog, "alice", password);
            True(retryLog.Arguments.Contains("--no-auth-cache", StringComparer.Ordinal), "Remember=false must disable native auth-cache writes");
            True(!retryLog.PasswordArgumentPresent, "password appeared in process arguments");

            var rememberCalls = PrepareHistoryScenario(temp.Path, "history_auth_retry", workingCopyPath);
            var remembered = await Svn.GetHistoryAsync(executable, config, workingCopyPath,
                credentials: new Svn.Credentials("alice", password, true), expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", remembered.Status);
            var rememberLog = ReadInvocations(rememberCalls)[1];
            AssertCredentialArguments(rememberLog, "alice", password);
            True(!rememberLog.Arguments.Contains("--no-auth-cache", StringComparer.Ordinal), "Remember=true unexpectedly disabled native auth caching");
            True(rememberLog.Arguments.Contains("--config-option", StringComparer.Ordinal), "Remember=true did not apply plaintext-storage safeguards");
        }).ConfigureAwait(false);

        await CheckAsync("Remember=true without an explicit config directory fails before log", async () =>
        {
            var invocations = PrepareHistoryScenario(temp.Path, "history_auth_retry", workingCopyPath);
            var result = await Svn.GetHistoryAsync(executable, string.Empty, workingCopyPath,
                credentials: new Svn.Credentials("alice", "remember-only-in-stdin", true),
                expectedUrl: "svn://fixture/repos/path%20with%20space%20%40").ConfigureAwait(false);
            Equal("CredentialStorageUnavailable", result.Status);
            Equal(1, ReadInvocations(invocations).Count);
        }).ConfigureAwait(false);

        await CheckAsync("changed working-copy URL refuses explicit credentials before log", async () =>
        {
            var invocations = PrepareHistoryScenario(temp.Path, "history_paged", workingCopyPath);
            var result = await Svn.GetHistoryAsync(executable, config, workingCopyPath,
                credentials: new Svn.Credentials("alice", "regression-secret-42", false),
                expectedUrl: "svn://fixture/previous-url").ConfigureAwait(false);
            Equal("WorkingCopyChanged", result.Status);
            Equal(1, ReadInvocations(invocations).Count);
        }).ConfigureAwait(false);

        await CheckAsync("history rejects DTD, malformed XML, and oversized XML", async () =>
        {
            foreach (var scenario in new[] { "history_dtd", "history_malformed", "history_large" })
            {
                var invocations = PrepareHistoryScenario(temp.Path, scenario, workingCopyPath);
                var result = await Svn.GetHistoryAsync(executable, config, workingCopyPath,
                    expectedUrl: "svn://fixture/repos/path%20with%20space%20%40").ConfigureAwait(false);
                Equal("Failed", result.Status);
                Equal(2, ReadInvocations(invocations).Count);
            }
        }).ConfigureAwait(false);

        await CheckAsync("history cancellation terminates its child process", async () =>
        {
            var invocations = PrepareHistoryScenario(temp.Path, "history_slow", workingCopyPath);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var cancelled = false;
            try
            {
                await Svn.GetHistoryAsync(executable, config, workingCopyPath,
                    expectedUrl: "svn://fixture/repos/path%20with%20space%20%40", cancellationToken: cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            True(cancelled, "caller cancellation was converted to a normal result");
            var calls = ReadInvocations(invocations);
            Equal(2, calls.Count);
            True(!IsProcessRunning(calls[1].ProcessId), "cancelled history child process remained alive");
        }).ConfigureAwait(false);

        await CheckAsync("selected revision paths preserve actions, copy ancestry, Unicode, and boundary-aware scope", async () =>
        {
            const string expectedUrl = "svn://fixture/repos/path%20with%20space%20%40";
            var originalContents = await File.ReadAllTextAsync(workingCopyPath).ConfigureAwait(false);
            var invocations = PrepareHistoryScenario(temp.Path, "changed_paths_main", workingCopyPath);
            var result = await Svn.GetChangedPathsAsync(executable, config, workingCopyPath, 6, expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", result.Status);
            Equal(6, result.Paths.Count);
            var ancestorCopy = result.Paths.Single(path => path.Path == "/repos/branches/历史 @ scope");
            Equal(".", ancestorCopy.RelativePath);
            Equal("A", ancestorCopy.Action);
            Equal("dir", ancestorCopy.Kind);
            Equal("/repos/archive/祖先 @", ancestorCopy.CopyFromPath);
            Equal(5L, ancestorCopy.CopyFromRevision);

            var added = result.Paths.Single(path => path.Action == "A" && path.Kind == "file");
            Equal("文档.txt", added.RelativePath);
            Equal("/repos/archive/来源 @ 文件.txt", added.CopyFromPath);
            Equal(4L, added.CopyFromRevision);
            var deleted = result.Paths.Single(path => path.Action == "D");
            Equal("old.txt", deleted.RelativePath);
            var replaced = result.Paths.Single(path => path.Action == "R");
            Equal("replaced", replaced.RelativePath);
            var ancestorModification = result.Paths.Single(path => path.Path == "/repos/branches/历史 @ scope/child @ nested");
            Equal(".", ancestorModification.RelativePath);
            Equal("M", ancestorModification.Action);
            var backslashName = result.Paths.Single(path => path.Path.EndsWith("/slash\\name.txt", StringComparison.Ordinal));
            Equal("slash\\name.txt", backslashName.RelativePath);

            var calls = ReadInvocations(invocations);
            Equal(3, calls.Count);
            var historicalInfo = calls[1];
            Equal("6", ArgumentAfter(historicalInfo, "--revision"));
            Equal("svn://fixture/repos/path%20with%20space%20%40@12", historicalInfo.Arguments[^1]);
            var log = calls[2];
            Equal("6:6", ArgumentAfter(log, "--revision"));
            True(log.Arguments.Contains("--verbose", StringComparer.Ordinal), "selected-revision log did not request verbose paths");
            Equal("svn://fixture/repos/branches/%E5%8E%86%E5%8F%B2%20%40%20scope/child%20%40%20nested/deeper%20%40%20scope@6", log.Arguments[^1]);
            Equal(originalContents, await File.ReadAllTextAsync(workingCopyPath).ConfigureAwait(false));

            var gapCalls = PrepareHistoryScenario(temp.Path, "changed_paths_gap", workingCopyPath);
            var gap = await Svn.GetChangedPathsAsync(executable, config, workingCopyPath, 8, expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", gap.Status);
            Equal(0, gap.Paths.Count);
            Equal("8:8", ArgumentAfter(ReadInvocations(gapCalls)[2], "--revision"));
        }).ConfigureAwait(false);

        await CheckAsync("changed paths enforce the expected URL before explicit credentials are sent", async () =>
        {
            const string password = "regression-secret-42";
            var invocations = PrepareHistoryScenario(temp.Path, "changed_paths_auth", workingCopyPath);
            var result = await Svn.GetChangedPathsAsync(executable, config, workingCopyPath, 6,
                new Svn.Credentials("alice", password, false), expectedUrl: "svn://fixture/previous-url").ConfigureAwait(false);
            Equal("WorkingCopyChanged", result.Status);
            Equal(1, ReadInvocations(invocations).Count);

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_auth", workingCopyPath);
            result = await Svn.GetChangedPathsAsync(executable, config, workingCopyPath, 6,
                new Svn.Credentials("alice", password, false), expectedUrl: "svn://fixture/repos/path%20with%20space%20%40").ConfigureAwait(false);
            Equal("Ready", result.Status);
            var calls = ReadInvocations(invocations);
            Equal(3, calls.Count);
            True(!calls[0].StdinReceived, "local working-copy info must not receive credentials");
            AssertCredentialArguments(calls[1], "alice", password);
            AssertCredentialArguments(calls[2], "alice", password);
            True(calls.All(call => !call.PasswordArgumentPresent), "password appeared in changed-path process arguments");

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_auth_leak", workingCopyPath);
            result = await Svn.GetChangedPathsAsync(executable, config, workingCopyPath, 6,
                new Svn.Credentials("alice", password, false), expectedUrl: "svn://fixture/repos/path%20with%20space%20%40").ConfigureAwait(false);
            Equal("AuthenticationFailed", result.Status);
            True(!result.Details.Contains(password, StringComparison.Ordinal), "password leaked in changed-path diagnostics");
        }).ConfigureAwait(false);

        await CheckAsync("changed-path XML rejects DTD, malformed, and oversized documents", async () =>
        {
            foreach (var scenario in new[] { "changed_paths_dtd", "changed_paths_malformed", "changed_paths_large" })
            {
                var invocations = PrepareHistoryScenario(temp.Path, scenario, workingCopyPath);
                var result = await Svn.GetChangedPathsAsync(executable, config, workingCopyPath, 6,
                    expectedUrl: "svn://fixture/repos/path%20with%20space%20%40").ConfigureAwait(false);
                Equal("Failed", result.Status);
                Equal(3, ReadInvocations(invocations).Count);
            }
        }).ConfigureAwait(false);

        await CheckAsync("changed-path cancellation terminates its selected-revision log child", async () =>
        {
            var invocations = PrepareHistoryScenario(temp.Path, "changed_paths_slow", workingCopyPath);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var cancelled = false;
            try
            {
                await Svn.GetChangedPathsAsync(executable, config, workingCopyPath, 6,
                    cancellationToken: cancellation.Token,
                    expectedUrl: "svn://fixture/repos/path%20with%20space%20%40").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            True(cancelled, "caller cancellation was converted to a normal changed-path result");
            var calls = ReadInvocations(invocations);
            Equal(3, calls.Count);
            True(!IsProcessRunning(calls[2].ProcessId), "cancelled changed-path log child process remained alive");
        }).ConfigureAwait(false);

        await CheckAsync("file snapshots follow changed-path actions and preserve raw endpoint bytes", async () =>
        {
            const string expectedUrl = "svn://fixture/repos/path%20with%20space%20%40";
            const string scope = "/repos/branches/历史 @ scope/child @ nested/deeper @ scope/";
            var workingCopyContents = await File.ReadAllBytesAsync(workingCopyPath).ConfigureAwait(false);

            var invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var modifiedPath = scope + "Unicode @ %\\slash.txt";
            var modified = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, modifiedPath,
                expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", modified.Status);
            Equal(modifiedPath, modified.Before.Path);
            Equal(5L, modified.Before.Revision);
            Equal(modifiedPath, modified.After.Path);
            Equal(6L, modified.After.Revision);
            Equal("before Unicode @ % \\ slash\n", Encoding.UTF8.GetString(modified.Before.Content));
            Equal("after Unicode @ % \\ slash\n", Encoding.UTF8.GetString(modified.After.Content));
            var modifiedCalls = ReadInvocations(invocations);
            var modifiedCats = modifiedCalls.Where(call => call.Arguments[0] == "cat").ToList();
            Equal(2, modifiedCats.Count);
            True(modifiedCats.All(call => call.Arguments.Contains("--ignore-keywords", StringComparer.Ordinal)),
                "svn cat did not disable keyword expansion");
            Equal("svn://fixture/repos/branches/%E5%8E%86%E5%8F%B2%20%40%20scope/child%20%40%20nested/deeper%20%40%20scope/Unicode%20%40%20%25%5Cslash.txt@5",
                ArgumentAfter(modifiedCats[0], "--"));
            Equal("svn://fixture/repos/branches/%E5%8E%86%E5%8F%B2%20%40%20scope/child%20%40%20nested/deeper%20%40%20scope/Unicode%20%40%20%25%5Cslash.txt@6",
                ArgumentAfter(modifiedCats[1], "--"));

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var emptyPath = scope + "empty @ file.txt";
            var empty = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, emptyPath, expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", empty.Status);
            Equal(null, empty.Before);
            True(empty.After != null, "an empty file was confused with an absent endpoint");
            Equal(0, empty.After.Content.Length);

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var copiedPath = scope + "copied @ file.txt";
            var copied = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, copiedPath, expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", copied.Status);
            Equal("/repos/archive/来源 @%/baseline.txt", copied.Before.Path);
            Equal(3L, copied.Before.Revision);
            Equal("copy source baseline\n", Encoding.UTF8.GetString(copied.Before.Content));
            Equal(copiedPath, copied.After.Path);
            Equal("copied destination\n", Encoding.UTF8.GetString(copied.After.Content));

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var deletedPath = scope + "deleted.bin";
            var deleted = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, deletedPath, expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", deleted.Status);
            True(deleted.Before != null && deleted.After == null, "deleted-file endpoints did not preserve absence");
            True(deleted.Before.IsBinary, "binary deleted snapshot was not marked binary");
            SequenceEqual(new byte[] { 3, 0, 4 }, deleted.Before.Content);

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var replacedPath = scope + "replaced @ file.txt";
            var replaced = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, replacedPath, expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", replaced.Status);
            Equal(replacedPath, replaced.Before.Path);
            Equal(5L, replaced.Before.Revision);
            Equal("old replacement target\n", Encoding.UTF8.GetString(replaced.Before.Content));
            Equal(replacedPath, replaced.After.Path);
            Equal("new replacement target\n", Encoding.UTF8.GetString(replaced.After.Content));
            True(ReadInvocations(invocations).Where(call => call.Arguments[0] == "cat")
                .All(call => !ArgumentAfter(call, "--").Contains("ignored-copy", StringComparison.Ordinal)),
                "replacement incorrectly used its copy-from metadata as the old endpoint");

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var binaryPath = scope + "binary @ file.dat";
            var binary = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, binaryPath, expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", binary.Status);
            True(binary.Before.IsBinary && binary.After.IsBinary, "non-text MIME metadata was not carried to both snapshots");
            SequenceEqual(new byte[] { 0, 255, 1, 2 }, binary.After.Content);

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var invalidUtf8 = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, scope + "invalid-utf8.bin", expectedUrl: expectedUrl).ConfigureAwait(false);
            True(invalidUtf8.Before.IsBinary && invalidUtf8.After.IsBinary, "invalid UTF-8 without NUL was not marked binary");

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var utf16 = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, scope + "utf16.txt", expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", utf16.Status);
            True(!utf16.After.IsBinary, "valid UTF-16 BOM text was classified as binary");

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var special = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, scope + "special-link", expectedUrl: expectedUrl).ConfigureAwait(false);
            True(special.Before.IsSpecial && special.After.IsSpecial, "svn:special metadata was not carried to both snapshots");

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var unknownPath = scope + "unknown-deleted.txt";
            var unknown = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, unknownPath, expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", unknown.Status);
            Equal("unknown-kind deleted baseline\n", Encoding.UTF8.GetString(unknown.Before.Content));
            True(unknown.After == null, "unknown deleted path unexpectedly had an after snapshot");
            var unknownInfo = ReadInvocations(invocations).Single(call => call.Arguments[0] == "info" && call.Arguments.Contains("--revision", StringComparer.Ordinal) &&
                ArgumentAfter(call, "--").Contains("unknown-deleted.txt", StringComparison.Ordinal));
            Equal("5", ArgumentAfter(unknownInfo, "--revision"));

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var directory = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, scope + "directory", expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Directory", directory.Status);
            True(directory.IsDirectory, "directory change was not marked as a directory notice");
            True(directory.Details.Contains("recursive expansion is not supported", StringComparison.Ordinal), "directory notice lost its scope");
            True(!ReadInvocations(invocations).Any(call => call.Arguments[0] is "cat" or "proplist"), "directory diff attempted to read recursive file contents");

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var notChanged = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, "/repos/other/not-changed.txt", expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("PathNotChanged", notChanged.Status);
            True(!ReadInvocations(invocations).Any(call => call.Arguments[0] is "cat" or "proplist"), "a path outside the authoritative changed-path list was read");

            SequenceEqual(workingCopyContents, await File.ReadAllBytesAsync(workingCopyPath).ConfigureAwait(false));
        }).ConfigureAwait(false);

        await CheckAsync("file snapshots retain auth, identity, config, overflow, and cancellation safeguards", async () =>
        {
            const string expectedUrl = "svn://fixture/repos/path%20with%20space%20%40";
            const string password = "regression-secret-42";
            const string scope = "/repos/branches/历史 @ scope/child @ nested/deeper @ scope/";

            var invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff_auth", workingCopyPath);
            var unauthenticated = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, scope + "Unicode @ %\\slash.txt",
                expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("AuthenticationRequired", unauthenticated.Status);
            Equal(3, ReadInvocations(invocations).Count);

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff_auth", workingCopyPath);
            var authenticated = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, scope + "Unicode @ %\\slash.txt",
                new Svn.Credentials("alice", password, false), expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("Ready", authenticated.Status);
            var calls = ReadInvocations(invocations);
            True(calls.All(call => !call.PasswordArgumentPresent), "snapshot password appeared in process arguments");
            True(!calls[0].StdinReceived, "working-copy identity lookup received credentials");
            foreach (var call in calls.Where(call => call.Arguments[0] != "info" || call.Arguments.Contains("--revision", StringComparer.Ordinal)))
                AssertCredentialArguments(call, "alice", password);

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var stale = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, scope + "Unicode @ %\\slash.txt",
                new Svn.Credentials("alice", password, false), expectedUrl: "svn://fixture/previous-url").ConfigureAwait(false);
            Equal("WorkingCopyChanged", stale.Status);
            Equal(1, ReadInvocations(invocations).Count);
            True(!ReadInvocations(invocations)[0].StdinReceived, "credentials were sent before the expected URL was checked");

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff", workingCopyPath);
            var rememberedWithoutConfig = await Svn.GetFileDiffAsync(executable, string.Empty, workingCopyPath, 6, scope + "Unicode @ %\\slash.txt",
                new Svn.Credentials("alice", password, true), expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("CredentialStorageUnavailable", rememberedWithoutConfig.Status);
            Equal(1, ReadInvocations(invocations).Count);

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff_overflow", workingCopyPath);
            var oversized = await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, scope + "overflow.dat", expectedUrl: expectedUrl).ConfigureAwait(false);
            Equal("TooLarge", oversized.Status);
            True(oversized.Before == null && oversized.After == null, "partial oversized content escaped as a snapshot");

            invocations = PrepareHistoryScenario(temp.Path, "changed_paths_file_diff_slow", workingCopyPath);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var cancelled = false;
            try
            {
                await Svn.GetFileDiffAsync(executable, config, workingCopyPath, 6, scope + "overflow.dat",
                    cancellationToken: cancellation.Token, expectedUrl: expectedUrl).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            True(cancelled, "caller cancellation was converted to a normal file-diff result");
            var cancelledCalls = ReadInvocations(invocations);
            Equal("cat", cancelledCalls[^1].Arguments[0]);
            True(!IsProcessRunning(cancelledCalls[^1].ProcessId), "cancelled cat process remained alive");
        }).ConfigureAwait(false);

        await CheckAsync("history failures redact passwords and classify authentication", async () =>
        {
            var invocations = PrepareHistoryScenario(temp.Path, "history_auth_leak", workingCopyPath);
            const string password = "regression-secret-42";
            var result = await Svn.GetHistoryAsync(executable, config, workingCopyPath,
                credentials: new Svn.Credentials("alice", password, false),
                expectedUrl: "svn://fixture/repos/path%20with%20space%20%40").ConfigureAwait(false);
            Equal("AuthenticationFailed", result.Status);
            True(!result.Details.Contains(password, StringComparison.Ordinal), "password leaked in history diagnostics");
            var log = ReadInvocations(invocations)[1];
            AssertCredentialArguments(log, "alice", password);
        }).ConfigureAwait(false);

        await CheckAsync("probe arguments and available credential stores", async () =>
        {
            var invocations = PrepareScenario(temp.Path, "probe_ready");
            var result = await Svn.ProbeAsync(executable, config).ConfigureAwait(false);
            Equal("Ready", result.Status);
            Equal("1.14.3", result.Version);
            Equal("Test keyring, Test encrypted disk cache", result.CredentialStores);
            var call = SingleInvocation(invocations);
            SequenceEqual(new[] { "--config-dir", Path.GetFullPath(config), "--version" }, call.Arguments);
            True(!call.StdinReceived, "version probe must not receive stdin");
        }).ConfigureAwait(false);

        await CheckAsync("unknown and explicitly empty credential-store reports remain distinct", async () =>
        {
            var unknownPath = PrepareScenario(temp.Path, "probe_unknown_stores");
            var unknown = await Svn.ProbeAsync(executable, config).ConfigureAwait(false);
            Equal("Ready", unknown.Status);
            Equal(null, unknown.CredentialStores);
            True(File.Exists(unknownPath), "fake invocation log was not created");

            PrepareScenario(temp.Path, "probe_empty_stores");
            var empty = await Svn.ProbeAsync(executable, config).ConfigureAwait(false);
            Equal("Ready", empty.Status);
            Equal(string.Empty, empty.CredentialStores);
        }).ConfigureAwait(false);

        await CheckAsync("malformed version output is rejected", async () =>
        {
            PrepareScenario(temp.Path, "probe_invalid_version");
            var result = await Svn.ProbeAsync(executable, config).ConfigureAwait(false);
            Equal("InvalidVersion", result.Status);
        }).ConfigureAwait(false);

        await CheckAsync("credentials travel only on stdin and are redacted from diagnostics", async () =>
        {
            var invocations = PrepareScenario(temp.Path, "auth_fail_secret");
            const string password = "regression-secret-42";
            var result = await Svn.TestConnectionAsync(
                executable, config, "https://svn.example.test/private", new Svn.Credentials("alice-test", password, false)).ConfigureAwait(false);
            Equal("AuthenticationFailed", result.Status);
            True(!result.Details.Contains(password, StringComparison.Ordinal), "password leaked into returned diagnostics");

            var call = SingleInvocation(invocations);
            AssertCredentialArguments(call, "alice-test", password);
            AssertConfigDirectory(call, config);
            True(!call.PasswordArgumentPresent, "password appeared in the child argv");
            True(call.Arguments.Contains("--no-auth-cache"), "Remember=false must disable native auth-cache writes");
            True(!call.Arguments.Any(argument => argument.Contains("store-passwords=no", StringComparison.Ordinal)), "cache reads must not be disabled");
        }).ConfigureAwait(false);

        await CheckAsync("one-character passwords do not corrupt authentication classification", async () =>
        {
            var invocations = PrepareScenario(temp.Path, "auth_fail_secret");
            var result = await Svn.TestConnectionAsync(
                executable, config, "https://svn.example.test/private", new Svn.Credentials("alice", "1", false)).ConfigureAwait(false);
            Equal("AuthenticationFailed", result.Status);
            True(!result.Details.Contains("1 E170001", StringComparison.Ordinal), "password leaked before the server's authentication diagnostic");
            var call = SingleInvocation(invocations);
            AssertCredentialArguments(call, "alice", "1");
            True(!call.PasswordArgumentPresent, "one-character password appeared in the child argv");
        }).ConfigureAwait(false);

        await CheckAsync("short usernames do not alter returned repository metadata", async () =>
        {
            PrepareScenario(temp.Path, "auth_succeed");
            const string url = "https://svn.example.test/data";
            var result = await Svn.TestConnectionAsync(
                executable, config, url, new Svn.Credentials("a", "short-user-test-secret", false)).ConfigureAwait(false);
            Equal("Ready", result.Status);
            Equal(url + "@", result.Url);
            Equal("svn://fixture", result.RepositoryRoot);
            Equal("9", result.Revision);
        }).ConfigureAwait(false);

        await CheckAsync("short passwords do not alter validated URL or revision metadata", async () =>
        {
            const string revisionPassword = "9";
            var revisionUrl = "https://svn.example.test/repository";
            var revisionCalls = PrepareScenario(temp.Path, "auth_succeed");
            var revisionResult = await Svn.TestConnectionAsync(executable, config, revisionUrl,
                new Svn.Credentials("revision-user", revisionPassword, false)).ConfigureAwait(false);
            Equal("Ready", revisionResult.Status);
            Equal(revisionUrl + "@", revisionResult.Url);
            Equal("svn://fixture", revisionResult.RepositoryRoot);
            Equal("9", revisionResult.Revision);
            AssertCredentialArguments(SingleInvocation(revisionCalls), "revision-user", revisionPassword);

            const string urlPassword = "1";
            var url = "https://svn.example.test/repository1";
            var urlCalls = PrepareScenario(temp.Path, "auth_succeed");
            var urlResult = await Svn.TestConnectionAsync(executable, config, url,
                new Svn.Credentials("url-user", urlPassword, false)).ConfigureAwait(false);
            Equal("Ready", urlResult.Status);
            Equal(url + "@", urlResult.Url);
            Equal("svn://fixture", urlResult.RepositoryRoot);
            Equal("9", urlResult.Revision);
            AssertCredentialArguments(SingleInvocation(urlCalls), "url-user", urlPassword);
        }).ConfigureAwait(false);

        await CheckAsync("Remember=true permits native caching without passing a password argument", async () =>
        {
            var invocations = PrepareScenario(temp.Path, "auth_succeed");
            const string password = "remember-only-in-stdin";
            var result = await Svn.TestConnectionAsync(
                executable, config, "https://svn.example.test/private", new Svn.Credentials("alice-test", password, true)).ConfigureAwait(false);
            Equal("Ready", result.Status);
            var call = SingleInvocation(invocations);
            AssertCredentialArguments(call, "alice-test", password);
            AssertConfigDirectory(call, config);
            True(!call.PasswordArgumentPresent, "password appeared in the child argv");
            True(!call.Arguments.Contains("--no-auth-cache"), "Remember=true must leave native cache writes available");
            True(call.Arguments.Contains("--config-option"), "plaintext-password policy override missing");
            True(call.Arguments.Contains("servers:global:store-plaintext-passwords=no"), "plaintext-password policy was not disabled");
        }).ConfigureAwait(false);

        await CheckAsync("cached-auth flow does not inject credentials or stdin", async () =>
        {
            var invocations = PrepareScenario(temp.Path, "ready");
            var result = await Svn.TestConnectionAsync(executable, config, "svn://fixture/repo").ConfigureAwait(false);
            Equal("Ready", result.Status);
            var call = SingleInvocation(invocations);
            True(!call.Arguments.Contains("--username"), "cached-auth attempt must not force a username");
            True(!call.PasswordArgumentPresent, "default attempt included a password argument");
            AssertConfigDirectory(call, config);
            True(call.Arguments.Contains("--no-auth-cache"), "default attempt must not save newly issued credentials");
            True(!call.StdinReceived, "cached-auth attempt unexpectedly received stdin");
        }).ConfigureAwait(false);

        await CheckAsync("blank config override preserves native config resolution and Remember=false cache policy", async () =>
        {
            var invocations = PrepareScenario(temp.Path, "auth_succeed");
            var result = await Svn.TestConnectionAsync(
                executable, string.Empty, "svn://fixture/repo",
                new Svn.Credentials("blank-config-user", "blank-config-secret", false)).ConfigureAwait(false);
            Equal("Ready", result.Status);
            var call = SingleInvocation(invocations);
            True(!call.Arguments.Contains("--config-dir"), "blank override must leave SVN's native config directory resolution enabled");
            True(call.Arguments.Contains("--no-auth-cache"), "Remember=false must prevent saving new credentials with a blank override");
            AssertCredentialArguments(call, "blank-config-user", "blank-config-secret");
        }).ConfigureAwait(false);

        await CheckAsync("Remember=true with a blank config override fails before launching SVN", async () =>
        {
            var invocations = PrepareScenario(temp.Path, "auth_succeed");
            var result = await Svn.TestConnectionAsync(
                executable, string.Empty, "svn://fixture/repo",
                new Svn.Credentials("blank-config-user", "blank-config-secret", true)).ConfigureAwait(false);
            Equal("CredentialStorageUnavailable", result.Status);
            Equal(0, ReadInvocations(invocations).Count);
        }).ConfigureAwait(false);

        await CheckAsync("explicit config overrides every matching plaintext-store setting without rewriting servers", async () =>
        {
            var policyConfig = Path.Combine(temp.Path, "policy-config");
            Directory.CreateDirectory(policyConfig);
            var serversPath = Path.Combine(policyConfig, "servers");
            const string servers = "[groups]\nfixture = svn.example.test\n\n[global]\nstore-plaintext-passwords = yes\nhttp-proxy-host = proxy.example.test\nhttp-proxy-port = 3128\nssl-authority-files = /tmp/sourcegit-fixture-ca.pem\n\n[fixture]\nstore-plaintext-passwords = yes\nhttp-proxy-host = group-proxy.example.test\nssl-authority-files = /tmp/sourcegit-group-ca.pem\n";
            await File.WriteAllTextAsync(serversPath, servers).ConfigureAwait(false);
            var invocations = PrepareScenario(temp.Path, "auth_succeed");
            var result = await Svn.TestConnectionAsync(
                executable, policyConfig, "https://svn.example.test/repository",
                new Svn.Credentials("policy-user", "policy-secret", true)).ConfigureAwait(false);
            Equal("Ready", result.Status);

            var call = SingleInvocation(invocations);
            AssertConfigDirectory(call, policyConfig);
            AssertCredentialArguments(call, "policy-user", "policy-secret");
            True(!call.Arguments.Contains("--no-auth-cache"), "Remember=true must leave native cache writes available");
            var configOptions = new List<string>();
            for (var index = 0; index + 1 < call.Arguments.Length; index++)
            {
                if (call.Arguments[index] == "--config-option")
                    configOptions.Add(call.Arguments[++index]);
            }
            True(configOptions.Contains("servers:global:store-plaintext-passwords=no"), "global plaintext policy override is missing");
            True(configOptions.Contains("servers:fixture:store-plaintext-passwords=no"), "matching group plaintext policy override is missing");
            True(!configOptions.Any(option => option.Contains("http-proxy-", StringComparison.Ordinal) || option.Contains("ssl-authority-files", StringComparison.Ordinal)),
                "proxy or TLS settings were replaced by the policy overrides");
            Equal(servers, await File.ReadAllTextAsync(serversPath).ConfigureAwait(false));
        }).ConfigureAwait(false);

        await CheckAsync("unrepresentable server section names fail closed when Remember=true", async () =>
        {
            var invalidConfig = Path.Combine(temp.Path, "invalid-policy-config");
            Directory.CreateDirectory(invalidConfig);
            await File.WriteAllTextAsync(Path.Combine(invalidConfig, "servers"), "[unrepresentable:section]\nstore-plaintext-passwords = yes\n").ConfigureAwait(false);
            var invocations = PrepareScenario(temp.Path, "auth_succeed");
            var result = await Svn.TestConnectionAsync(
                executable, invalidConfig, "svn://fixture/repo",
                new Svn.Credentials("policy-user", "policy-secret", true)).ConfigureAwait(false);
            Equal("CredentialStorageUnavailable", result.Status);
            Equal(0, ReadInvocations(invocations).Count);
        }).ConfigureAwait(false);

        await CheckAsync("plaintext storage in the groups section fails closed without rewriting mappings", async () =>
        {
            var groupsConfig = Path.Combine(temp.Path, "groups-policy-config");
            Directory.CreateDirectory(groupsConfig);
            var serversPath = Path.Combine(groupsConfig, "servers");
            const string servers = "[groups]\ngroups = svn.example.test\nstore-plaintext-passwords = yes\n";
            await File.WriteAllTextAsync(serversPath, servers).ConfigureAwait(false);
            var invocations = PrepareScenario(temp.Path, "auth_succeed");
            var result = await Svn.TestConnectionAsync(executable, groupsConfig, "https://svn.example.test/repository",
                new Svn.Credentials("groups-user", "policy-secret", true)).ConfigureAwait(false);
            Equal("CredentialStorageUnavailable", result.Status);
            Equal(0, ReadInvocations(invocations).Count);
            Equal(servers, await File.ReadAllTextAsync(serversPath).ConfigureAwait(false));
        }).ConfigureAwait(false);

        await CheckAsync("URL terminator protects option-like URLs and preserves embedded @", async () =>
        {
            var invocations = PrepareScenario(temp.Path, "ready");
            const string url = "https://svn.example.test/repos/name@revision?view=tree";
            var result = await Svn.TestConnectionAsync(executable, config, url).ConfigureAwait(false);
            Equal("Ready", result.Status);
            var call = SingleInvocation(invocations);
            var separator = Array.IndexOf(call.Arguments, "--");
            True(separator >= 0, "SVN arguments lack the option terminator");
            Equal(url + "@", call.Arguments[separator + 1]);
            Equal(separator + 2, call.Arguments.Length);
        }).ConfigureAwait(false);

        await CheckAsync("unsafe URLs are rejected before launching SVN", async () =>
        {
            foreach (var url in new[]
            {
                "https://alice:embedded-secret@svn.example.test/repo",
                "https://alice%3Aembedded-secret@svn.example.test/repo",
                "--config-dir=/tmp/attacker",
                "https://svn.example.test/repo\r\n--config-dir=/tmp/attacker",
            })
            {
                var invocations = PrepareScenario(temp.Path, "ready");
                var result = await Svn.TestConnectionAsync(executable, config, url).ConfigureAwait(false);
                Equal("InvalidUrl", result.Status);
                Equal(0, ReadInvocations(invocations).Count);
            }
        }).ConfigureAwait(false);

        await CheckAsync("incomplete and DTD-bearing info XML fail closed", async () =>
        {
            foreach (var scenario in new[] { "incomplete_xml", "untrusted_xml" })
            {
                PrepareScenario(temp.Path, scenario);
                var result = await Svn.TestConnectionAsync(executable, config, "svn://fixture/repo").ConfigureAwait(false);
                Equal("Failed", result.Status);
            }
        }).ConfigureAwait(false);

        await CheckAsync("metadata URLs with embedded passwords are rejected without disclosure", async () =>
        {
            PrepareScenario(temp.Path, "userinfo_xml");
            var result = await Svn.TestConnectionAsync(executable, config, "https://svn.example.test/repository").ConfigureAwait(false);
            True(result.Status != "Ready", "SVN metadata containing embedded URL credentials was accepted");
            var returnedText = string.Join("\n", result.Url, result.RepositoryRoot, result.Revision, result.Details);
            True(!returnedText.Contains("xml-metadata-secret", StringComparison.Ordinal), "metadata URL password leaked in the result");
        }).ConfigureAwait(false);

        await CheckAsync("large stderr is drained without unbounded diagnostics", async () =>
        {
            var invocations = PrepareScenario(temp.Path, "stderr_flood");
            var result = await Svn.TestConnectionAsync(executable, config, "svn://fixture/repo").ConfigureAwait(false);
            Equal("Failed", result.Status);
            True(result.Details.Length <= 128 * 1024 + 128, $"stderr details exceeded the bound: {result.Details.Length}");
            Equal(1, ReadInvocations(invocations).Count);
        }).ConfigureAwait(false);

        await CheckAsync("a password crossing the stderr limit does not leak its captured prefix", async () =>
        {
            PrepareScenario(temp.Path, "stderr_secret_at_limit");
            const string password = "secret-boundary-password-unique";
            var result = await Svn.TestConnectionAsync(
                executable, config, "svn://fixture/repo", new Svn.Credentials("boundary-user", password, false)).ConfigureAwait(false);
            True(result.Status != "Ready", "synthetic failure unexpectedly succeeded");
            True(!result.Details.Contains(password[..8], StringComparison.Ordinal), "a partial password prefix leaked from truncated stderr");
            True(result.Details.Length <= 128 * 1024 + 128, $"stderr details exceeded the bound: {result.Details.Length}");
        }).ConfigureAwait(false);

        await CheckAsync("caller cancellation is propagated and the child is terminated", async () =>
        {
            var invocations = PrepareScenario(temp.Path, "slow");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var cancelled = false;
            try
            {
                await Svn.TestConnectionAsync(executable, config, "svn://fixture/repo", cancellationToken: cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            True(cancelled, "caller cancellation was converted to a normal result");
            var call = SingleInvocation(invocations);
            var waitUntil = Stopwatch.StartNew();
            while (IsProcessRunning(call.ProcessId) && waitUntil.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(20).ConfigureAwait(false);
            True(!IsProcessRunning(call.ProcessId), "cancelled SVN child process remained alive");
        }).ConfigureAwait(false);

        await CheckAsync("blocked large-password stdin writes cancel promptly and terminate the child", async () =>
        {
            var invocations = PrepareScenario(temp.Path, "stdin_backpressure");
            var password = new string('P', 1024 * 1024);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var timer = Stopwatch.StartNew();
            var cancelled = false;
            try
            {
                await Svn.TestConnectionAsync(executable, config, "svn://fixture/repo",
                    new Svn.Credentials("backpressure-user", password, false), cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            True(cancelled, "blocked password write did not propagate caller cancellation");
            True(timer.Elapsed < TimeSpan.FromSeconds(4), $"blocked password write took too long to cancel: {timer.Elapsed}");
            var call = SingleInvocation(invocations);
            True(!call.StdinReceived, "backpressure fixture unexpectedly read standard input");
            True(call.Arguments.Contains("--password-from-stdin"), "large password was not configured for stdin");
            True(!call.Arguments.Contains(password), "large password appeared in argv");
            True(!call.PasswordArgumentPresent, "fixture found a password in argv");
            var invocationLog = await File.ReadAllTextAsync(invocations).ConfigureAwait(false);
            True(!invocationLog.Contains(password, StringComparison.Ordinal), "large password appeared in the fixture log");

            var waitUntil = Stopwatch.StartNew();
            while (IsProcessRunning(call.ProcessId) && waitUntil.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(20).ConfigureAwait(false);
            True(!IsProcessRunning(call.ProcessId), "backpressure fixture child process remained alive");
        }).ConfigureAwait(false);

        var statusRoot = CreateWorkingCopyStatusFiles(workingCopyPath, temp.Path);
        await CheckAsync("working-copy status parses local flags and changelist entries and filters externals/normal rows", async () =>
        {
            var invocations = PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
            var result = await Svn.GetStatusAsync(executable, config, workingCopyPath).ConfigureAwait(false);
            Equal("Ready", result.Status);
            Equal(s_workingCopyLinksAvailable ? 14 : 12, result.Entries.Count);

            var modified = result.Entries.Single(entry => entry.Path == "modified @ file.txt");
            Equal("modified", modified.Item);
            Equal("modified", modified.Properties);
            Equal(12L, modified.Revision);
            True(modified.IsSwitched && modified.IsTreeConflicted, "status flags were not parsed");
            Equal("old @ file.txt", modified.MovedFrom);
            Equal("new @ file.txt", modified.MovedTo);
            Equal("file", modified.Kind);

            var copied = result.Entries.Single(entry => entry.Path == "copied @ file.txt");
            True(copied.IsCopied, "copied flag was lost");
            Equal(null, copied.Revision); // SVN reports -1 for an uncommitted add.
            Equal("dir", result.Entries.Single(entry => entry.Path == "directory change").Kind);
            Equal("dir", result.Entries.Single(entry => entry.Path == "unversioned directory").Kind);
            Equal("pending work", result.Entries.Single(entry => entry.Path == "changelist @ file.txt").Changelist);
            True(result.Entries.Any(entry => entry.Path == "switched.txt" && entry.IsSwitched), "meaningful normal switched row was filtered");
            True(!result.Entries.Any(entry => entry.Item == "external" || entry.IsFileExternal), "external status leaked into the working-copy list");

            var statusCall = ReadInvocations(invocations).Single(call => call.Arguments[0] == "status");
            True(statusCall.Arguments.Contains("--ignore-externals", StringComparer.Ordinal), "status did not ignore externals");
            True(statusCall.Arguments.Contains("--no-auth-cache", StringComparer.Ordinal), "local status may write an auth cache");
            True(!statusCall.Arguments.Contains("-u", StringComparer.Ordinal) &&
                !statusCall.Arguments.Contains("--show-updates", StringComparer.Ordinal) &&
                !statusCall.Arguments.Contains("--revision", StringComparer.Ordinal), "status requested repository data");
            Equal(Path.GetFullPath(statusRoot) + "@", statusCall.Arguments[^1]);
        }).ConfigureAwait(false);

        await CheckAsync("working-copy add/remove eligibility excludes stale, conflicting, and unsafe entries", async () =>
        {
            True(Svn.CanAdd(new Svn.SvnWorkingCopyEntry("new.txt", "unversioned", "none", Kind: "file")), "unversioned file was not addable");
            True(Svn.CanAdd(new Svn.SvnWorkingCopyEntry("new directory", "unversioned", "none", Kind: "dir")), "unversioned directory was not addable");
            True(!Svn.CanAdd(null), "null add entry was accepted");
            True(!Svn.CanAdd(new Svn.SvnWorkingCopyEntry(".", "unversioned", "none", Kind: "dir")), "working-copy root was addable");
            True(!Svn.CanAdd(new Svn.SvnWorkingCopyEntry("../outside.txt", "unversioned", "none", Kind: "file")), "traversal add entry was accepted");
            True(!Svn.CanAdd(new Svn.SvnWorkingCopyEntry("new.txt", "modified", "none", Kind: "file")), "versioned file was addable");

            True(Svn.CanRemove(new Svn.SvnWorkingCopyEntry("modified.txt", "modified", "none", Kind: "file")), "text-modified file was not removable");
            True(Svn.CanRemove(new Svn.SvnWorkingCopyEntry("missing.txt", "missing", "none", Kind: "unknown")), "missing file with unknown kind was not removable");
            True(!Svn.CanRemove(null), "null remove entry was accepted");
            True(!Svn.CanRemove(new Svn.SvnWorkingCopyEntry("property.txt", "modified", "modified", Kind: "file")), "property-modified file was removable");
            True(!Svn.CanRemove(new Svn.SvnWorkingCopyEntry("copy.txt", "added", "none", IsCopied: true, Kind: "file")), "copied addition was removable");
            True(!Svn.CanRemove(new Svn.SvnWorkingCopyEntry("conflict.txt", "modified", "none", IsTreeConflicted: true, Kind: "file")), "tree-conflicted file was removable");

            True(Svn.CanUndo(new Svn.SvnWorkingCopyEntry("auto-prop.txt", "added", "modified", Kind: "file")), "auto-property edits disabled undo for an added file");
            True(Svn.CanUndo(new Svn.SvnWorkingCopyEntry("missing.txt", "deleted", "none", Kind: "unknown")), "missing deleted path with unknown kind was not undoable");
            True(!Svn.CanUndo(null), "null undo entry was accepted");
            True(!Svn.CanUndo(new Svn.SvnWorkingCopyEntry("edited.txt", "modified", "none", Kind: "file")), "tracked text edit was undoable");
            True(!Svn.CanUndo(new Svn.SvnWorkingCopyEntry("property.txt", "deleted", "modified", Kind: "file")), "property-edited deletion was undoable");
            True(!Svn.CanUndo(new Svn.SvnWorkingCopyEntry("copy.txt", "added", "none", IsCopied: true, Kind: "file")), "copied addition was undoable");
            True(!Svn.CanUndo(new Svn.SvnWorkingCopyEntry("move.txt", "deleted", "none", MovedTo: "new.txt", Kind: "file")), "moved deletion was undoable");
            True(!Svn.CanUndo(new Svn.SvnWorkingCopyEntry(".svn/wc.db", "deleted", "none", Kind: "file")), "administrative path was undoable");
            True(!Svn.CanUndo(new Svn.SvnWorkingCopyEntry(".", "added", "none", Kind: "dir")), "working-copy root was undoable");
        }).ConfigureAwait(false);

        await CheckAsync("local add/remove commands revalidate status and safely frame special paths", async () =>
        {
            var addName = "-- option @ 文档🌱.txt";
            var addPath = Path.Combine(statusRoot, addName);
            const string addContents = "unversioned bytes stay local\n";
            await File.WriteAllTextAsync(addPath, addContents).ConfigureAwait(false);
            var invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_ok", workingCopyPath,
                addName, "unversioned", "none", "file");
            var added = await Svn.AddAsync(executable, config, workingCopyPath, addName).ConfigureAwait(false);
            Equal("Ready", added.Status);
            var addCalls = ReadInvocations(invocations);
            Equal("info", addCalls[0].Arguments[0]);
            Equal("status", addCalls[1].Arguments[0]);
            var addCall = addCalls[2];
            Equal("add", addCall.Arguments[0]);
            True(addCall.Arguments.Contains("--depth", StringComparer.Ordinal), "add did not use explicit depth");
            Equal("empty", ArgumentAfter(addCall, "--depth"));
            True(addCall.Arguments.Contains("--non-interactive", StringComparer.Ordinal), "add was not non-interactive");
            True(addCall.Arguments.Contains("--no-auth-cache", StringComparer.Ordinal), "add could persist auth credentials");
            AssertConfigDirectory(addCall, config);
            True(!addCall.Arguments.Contains("--force", StringComparer.Ordinal), "add forced recursive or ignored paths");
            var addSeparator = Array.IndexOf(addCall.Arguments, "--");
            True(addSeparator >= 0, "add lacks an option terminator");
            Equal(Path.GetFullPath(addPath) + "@", addCall.Arguments[addSeparator + 1]);
            Equal(addSeparator + 2, addCall.Arguments.Length);
            Equal(addContents, await File.ReadAllTextAsync(addPath).ConfigureAwait(false));

            var directoryRelative = "unversioned directory";
            var childPath = Path.Combine(statusRoot, directoryRelative, "child @ file.txt");
            await File.WriteAllTextAsync(childPath, "directory child stays unversioned\n").ConfigureAwait(false);
            invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_ok", workingCopyPath,
                directoryRelative, "unversioned", "none", "dir");
            var addedDirectory = await Svn.AddAsync(executable, config, workingCopyPath, directoryRelative).ConfigureAwait(false);
            Equal("Ready", addedDirectory.Status);
            addCall = ReadInvocations(invocations)[2];
            Equal("empty", ArgumentAfter(addCall, "--depth"));
            Equal("directory child stays unversioned\n", await File.ReadAllTextAsync(childPath).ConfigureAwait(false));

            var modifiedPath = Path.Combine(statusRoot, "modified @ file.txt");
            var modifiedBytes = await File.ReadAllBytesAsync(modifiedPath).ConfigureAwait(false);
            invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_ok", workingCopyPath,
                "modified @ file.txt", "modified", "none", "file");
            var removed = await Svn.RemoveAsync(executable, config, workingCopyPath, "modified @ file.txt").ConfigureAwait(false);
            Equal("Ready", removed.Status);
            var removeCall = ReadInvocations(invocations)[2];
            Equal("delete", removeCall.Arguments[0]);
            True(removeCall.Arguments.Contains("--keep-local", StringComparer.Ordinal), "delete did not retain local contents");
            True(!removeCall.Arguments.Contains("--force", StringComparer.Ordinal), "delete forced through unrelated obstructions");
            True(removeCall.Arguments.Contains("--non-interactive", StringComparer.Ordinal), "delete was not non-interactive");
            True(removeCall.Arguments.Contains("--no-auth-cache", StringComparer.Ordinal), "delete could persist auth credentials");
            AssertConfigDirectory(removeCall, config);
            var removeSeparator = Array.IndexOf(removeCall.Arguments, "--");
            True(removeSeparator >= 0, "delete lacks an option terminator");
            Equal(Path.GetFullPath(modifiedPath) + "@", removeCall.Arguments[removeSeparator + 1]);
            Equal(removeSeparator + 2, removeCall.Arguments.Length);
            SequenceEqual(modifiedBytes, await File.ReadAllBytesAsync(modifiedPath).ConfigureAwait(false));

            const string missingRelative = "missing @ removed.txt";
            invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_ok", workingCopyPath,
                missingRelative, "missing", "none", "unknown");
            var missing = await Svn.RemoveAsync(executable, config, workingCopyPath, missingRelative).ConfigureAwait(false);
            Equal("Ready", missing.Status);
            True(!File.Exists(Path.Combine(statusRoot, missingRelative)), "removing a missing path recreated local content");
            True(ReadInvocations(invocations)[2].Arguments.Contains("--keep-local", StringComparer.Ordinal), "missing path did not use keep-local semantics");
        }).ConfigureAwait(false);

        await CheckAsync("local undo uses safe revert arguments and preserves added and deleted contents", async () =>
        {
            var addedName = "unversioned @ file.txt";
            var addedPath = Path.Combine(statusRoot, addedName);
            var addedBytes = await File.ReadAllBytesAsync(addedPath).ConfigureAwait(false);
            var invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_ok", workingCopyPath,
                addedName, "added", "modified", "file");
            var undoneAdd = await Svn.UndoAsync(executable, config, workingCopyPath, addedName).ConfigureAwait(false);
            Equal("Ready", undoneAdd.Status);
            var revertCall = ReadInvocations(invocations)[2];
            Equal("revert", revertCall.Arguments[0]);
            Equal("infinity", ArgumentAfter(revertCall, "--depth"));
            True(!revertCall.Arguments.Contains("--remove-added", StringComparer.Ordinal), "undo add requested removal of local contents");
            True(revertCall.Arguments.Contains("--non-interactive", StringComparer.Ordinal), "undo was not non-interactive");
            True(revertCall.Arguments.Contains("--no-auth-cache", StringComparer.Ordinal), "undo could persist auth credentials");
            AssertConfigDirectory(revertCall, config);
            var separator = Array.IndexOf(revertCall.Arguments, "--");
            True(separator >= 0, "undo lacks an option terminator");
            Equal(Path.GetFullPath(addedPath) + "@", revertCall.Arguments[separator + 1]);
            Equal(separator + 2, revertCall.Arguments.Length);
            SequenceEqual(addedBytes, await File.ReadAllBytesAsync(addedPath).ConfigureAwait(false));

            var deletedName = "modified @ file.txt";
            var deletedPath = Path.Combine(statusRoot, deletedName);
            var deletedBytes = await File.ReadAllBytesAsync(deletedPath).ConfigureAwait(false);
            invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_ok", workingCopyPath,
                deletedName, "deleted", "none", "file");
            var undoneDelete = await Svn.UndoAsync(executable, config, workingCopyPath, deletedName).ConfigureAwait(false);
            Equal("Ready", undoneDelete.Status);
            SequenceEqual(deletedBytes, await File.ReadAllBytesAsync(deletedPath).ConfigureAwait(false));
            True(!Directory.EnumerateDirectories(statusRoot, ".sourcegit-svn-undo-*").Any(), "successful undo left its recovery directory behind");
            Equal("revert", ReadInvocations(invocations)[2].Arguments[0]);

            const string missingName = "missing @ removed.txt";
            invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_ok", workingCopyPath,
                missingName, "deleted", "none", "unknown");
            var undoneMissing = await Svn.UndoAsync(executable, config, workingCopyPath, missingName).ConfigureAwait(false);
            Equal("Ready", undoneMissing.Status);
            Equal("revert", ReadInvocations(invocations)[2].Arguments[0]);
            True(!Directory.EnumerateDirectories(statusRoot, ".sourcegit-svn-undo-*").Any(), "missing-path undo created a recovery directory");
        }).ConfigureAwait(false);

        await CheckAsync("local undo recovers a post-revert replacement after command failure and cancellation", async () =>
        {
            var missingDirectory = "missing external parent";
            var invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_external", workingCopyPath,
                missingDirectory, "deleted", "none", "unknown");
            var external = await Svn.UndoAsync(executable, config, workingCopyPath, missingDirectory).ConfigureAwait(false);
            Equal("NotEligible", external.Status);
            True(!ReadInvocations(invocations).Any(call => call.Arguments[0] == "revert"), "external descendant launched svn revert");

            var deletedName = "modified @ file.txt";
            var deletedPath = Path.Combine(statusRoot, deletedName);
            var originalBytes = await File.ReadAllBytesAsync(deletedPath).ConfigureAwait(false);
            invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_undo_pristine_fail", workingCopyPath,
                deletedName, "deleted", "none", "file");
            var failed = await Svn.UndoAsync(executable, config, workingCopyPath, deletedName).ConfigureAwait(false);
            Equal("Failed", failed.Status);
            True(failed.Details.Contains("original local contents were restored", StringComparison.Ordinal), "undo failure omitted the restored-content result");
            SequenceEqual(originalBytes, await File.ReadAllBytesAsync(deletedPath).ConfigureAwait(false));
            True(!Directory.EnumerateDirectories(statusRoot, ".sourcegit-svn-undo-*").Any(), "failed undo left its recovery directory behind");

            invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_undo_pristine_slow", workingCopyPath,
                deletedName, "deleted", "none", "file");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
            var cancelled = false;
            try
            {
                await Svn.UndoAsync(executable, config, workingCopyPath, deletedName, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            True(cancelled, "undo cancellation was converted to a result");
            SequenceEqual(originalBytes, await File.ReadAllBytesAsync(deletedPath).ConfigureAwait(false));
            True(!Directory.EnumerateDirectories(statusRoot, ".sourcegit-svn-undo-*").Any(), "cancelled undo left its recovery directory behind");
            var revertCall = ReadInvocations(invocations).Last(call => call.Arguments[0] == "revert");
            var waitUntil = Stopwatch.StartNew();
            while (IsProcessRunning(revertCall.ProcessId) && waitUntil.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(20).ConfigureAwait(false);
            True(!IsProcessRunning(revertCall.ProcessId), "cancelled SVN revert process remained alive");
        }).ConfigureAwait(false);

        await CheckAsync("local undo reports recovery files when restoration is refused", async () =>
        {
            var invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_undo_cleanup_fail", workingCopyPath,
                "modified @ file.txt", "deleted", "none", "file");
            var modifiedPath = Path.Combine(statusRoot, "modified @ file.txt");
            var originalBytes = await File.ReadAllBytesAsync(modifiedPath).ConfigureAwait(false);
            var cleanupFailure = await Svn.UndoAsync(executable, config, workingCopyPath, "modified @ file.txt").ConfigureAwait(false);
            Equal("Failed", cleanupFailure.Status);
            SequenceEqual(originalBytes, await File.ReadAllBytesAsync(modifiedPath).ConfigureAwait(false));
            var cleanupDirectory = Directory.GetDirectories(statusRoot, ".sourcegit-svn-undo-*").Single();
            Equal("preserve this data\n", await File.ReadAllTextAsync(Path.Combine(cleanupDirectory, "unrecognized-recovery-data.txt")).ConfigureAwait(false));
            True(cleanupFailure.Details.Contains(cleanupDirectory, StringComparison.Ordinal), "cleanup failure omitted its recovery directory");

            if (!s_workingCopyLinksAvailable)
                return;

            var sentinel = Path.Combine(temp.Path, "undo external sentinel.txt");
            await File.WriteAllTextAsync(sentinel, "outside bytes stay intact\n").ConfigureAwait(false);
            Environment.SetEnvironmentVariable(UndoSentinelVariable, sentinel);
            try
            {
                var refusedPath = Path.Combine(statusRoot, "undo refused @ file.txt");
                var refusedBytes = Encoding.UTF8.GetBytes("edited bytes need recovery\n");
                await File.WriteAllBytesAsync(refusedPath, refusedBytes).ConfigureAwait(false);
                invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_undo_symlink_fail", workingCopyPath,
                    "undo refused @ file.txt", "deleted", "none", "file");
                var refused = await Svn.UndoAsync(executable, config, workingCopyPath, "undo refused @ file.txt").ConfigureAwait(false);
                Equal("Failed", refused.Status);
                var refusedRecovery = Directory.GetDirectories(statusRoot, ".sourcegit-svn-undo-*")
                    .Single(path => File.Exists(Path.Combine(path, "original")) &&
                        File.ReadAllBytes(Path.Combine(path, "original")).SequenceEqual(refusedBytes));
                True(File.Exists(Path.Combine(refusedRecovery, "RECOVERY.txt")), "restore refusal lost its recovery manifest");
                True(refused.Details.Contains(refusedRecovery, StringComparison.Ordinal) &&
                    refused.Details.Contains("RECOVERY.txt", StringComparison.Ordinal), "restore failure omitted recovery instructions");
                Equal("outside bytes stay intact\n", await File.ReadAllTextAsync(sentinel).ConfigureAwait(false));

                var cancelledPath = Path.Combine(statusRoot, "undo cancelled @ file.txt");
                var cancelledBytes = Encoding.UTF8.GetBytes("cancelled bytes need recovery\n");
                await File.WriteAllBytesAsync(cancelledPath, cancelledBytes).ConfigureAwait(false);
                invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_undo_symlink_slow", workingCopyPath,
                    "undo cancelled @ file.txt", "deleted", "none", "file");
                using var cancelRecovery = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
                OperationCanceledException cancellationError = null;
                try
                {
                    await Svn.UndoAsync(executable, config, workingCopyPath, "undo cancelled @ file.txt", cancelRecovery.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    cancellationError = ex;
                }
                True(cancellationError != null, "cancelled undo did not throw cancellation");
                var cancelledRecovery = Directory.GetDirectories(statusRoot, ".sourcegit-svn-undo-*")
                    .Single(path => File.Exists(Path.Combine(path, "original")) &&
                        File.ReadAllBytes(Path.Combine(path, "original")).SequenceEqual(cancelledBytes));
                True(File.Exists(Path.Combine(cancelledRecovery, "RECOVERY.txt")), "cancelled restore refusal lost its recovery manifest");
                True(cancellationError.Message.Contains(cancelledRecovery, StringComparison.Ordinal) &&
                    cancellationError.Message.Contains("RECOVERY.txt", StringComparison.Ordinal), "cancellation omitted its recovery location");
                Equal("outside bytes stay intact\n", await File.ReadAllTextAsync(sentinel).ConfigureAwait(false));
            }
            finally
            {
                Environment.SetEnvironmentVariable(UndoSentinelVariable, null);
            }
        }).ConfigureAwait(false);

        await CheckAsync("local add/remove refuse stale, conflicting, symlinked, and nested-working-copy paths", async () =>
        {
            var invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_ok", workingCopyPath,
                "unversioned @ file.txt", "unversioned", "none", "file");
            var stale = await Svn.AddAsync(executable, config, workingCopyPath, "stale @ file.txt").ConfigureAwait(false);
            Equal("PathNotChanged", stale.Status);
            True(!ReadInvocations(invocations).Any(call => call.Arguments[0] == "add"), "stale selection launched svn add");

            invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_ok", workingCopyPath,
                "modified @ file.txt", "modified", "modified", "file");
            var propertyEdit = await Svn.RemoveAsync(executable, config, workingCopyPath, "modified @ file.txt").ConfigureAwait(false);
            Equal("NotEligible", propertyEdit.Status);
            True(!ReadInvocations(invocations).Any(call => call.Arguments[0] == "delete"), "property edit launched svn delete");

            var traversalPath = "../outside.txt";
            invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_ok", workingCopyPath,
                "unversioned @ file.txt", "unversioned", "none", "file");
            var traversal = await Svn.AddAsync(executable, config, workingCopyPath, traversalPath).ConfigureAwait(false);
            Equal("PathNotChanged", traversal.Status);
            True(!ReadInvocations(invocations).Any(call => call.Arguments[0] == "add"), "traversal launched svn add");

            if (s_workingCopyLinksAvailable)
            {
                invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_ok", workingCopyPath,
                    Path.Combine("escape", "outside.txt"), "unversioned", "none", "file");
                var escape = await Svn.AddAsync(executable, config, workingCopyPath,
                    Path.Combine("escape", "outside.txt")).ConfigureAwait(false);
                Equal("UnsafePath", escape.Status);
                True(!ReadInvocations(invocations).Any(call => call.Arguments[0] == "add"), "symlinked ancestor launched svn add");
            }

            var nestedRoot = Path.Combine(statusRoot, "directory change", "nested", ".svn");
            Directory.CreateDirectory(nestedRoot);
            try
            {
                invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_ok", workingCopyPath,
                    "directory change", "modified", "none", "dir");
                var nested = await Svn.RemoveAsync(executable, config, workingCopyPath, "directory change").ConfigureAwait(false);
                Equal("UnsafePath", nested.Status);
                True(!ReadInvocations(invocations).Any(call => call.Arguments[0] == "delete"), "nested working copy launched svn delete");
            }
            finally
            {
                Directory.Delete(Path.Combine(statusRoot, "directory change", "nested"), recursive: true);
            }

            var externalParent = "missing external parent";
            invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_external", workingCopyPath,
                externalParent, "missing", "none", "dir");
            var external = await Svn.RemoveAsync(executable, config, workingCopyPath, externalParent).ConfigureAwait(false);
            Equal("NotEligible", external.Status);
            var externalCalls = ReadInvocations(invocations);
            True(!externalCalls.Any(call => call.Arguments[0] == "delete"), "file external descendant launched svn delete");
            var actionStatusCall = externalCalls.Single(call => call.Arguments[0] == "status");
            True(actionStatusCall.Arguments.Contains("--ignore-externals", StringComparer.Ordinal),
                "action status changed the local status scope while checking externals");
        }).ConfigureAwait(false);

        await CheckAsync("local action failures and cancellation preserve diagnostics and terminate SVN", async () =>
        {
            var invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_fail", workingCopyPath,
                "unversioned @ file.txt", "unversioned", "none", "file");
            var failed = await Svn.AddAsync(executable, config, workingCopyPath, "unversioned @ file.txt").ConfigureAwait(false);
            Equal("Failed", failed.Status);
            True(failed.Details.Contains("partially changed", StringComparison.Ordinal), "failed add did not report possible partial scheduling");

            invocations = PrepareWorkingCopyActionScenario(temp.Path, "wc_action_slow", workingCopyPath,
                "unversioned @ file.txt", "unversioned", "none", "file");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            var cancelled = false;
            try
            {
                await Svn.AddAsync(executable, config, workingCopyPath, "unversioned @ file.txt", cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            True(cancelled, "action cancellation was converted to a result");
            var addCall = ReadInvocations(invocations).Last(call => call.Arguments[0] == "add");
            var waitUntil = Stopwatch.StartNew();
            while (IsProcessRunning(addCall.ProcessId) && waitUntil.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(20).ConfigureAwait(false);
            True(!IsProcessRunning(addCall.ProcessId), "cancelled SVN add process remained alive");
        }).ConfigureAwait(false);

        await CheckAsync("local text and property diffs use bounded internal commands and native copied-add semantics", async () =>
        {
            var invocations = PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
            var result = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "modified @ file.txt").ConfigureAwait(false);
            Equal("Ready", result.Status);
            True(Encoding.UTF8.GetString(result.Patch).Contains("+local edit", StringComparison.Ordinal), "local patch was not returned");
            True(result.PropertyDiff.Contains("custom:local", StringComparison.Ordinal), "property patch was not returned");
            True(result.Details.Contains("conflict", StringComparison.OrdinalIgnoreCase), "tree conflict warning was omitted");

            var calls = ReadInvocations(invocations);
            var statusCall = calls.Single(call => call.Arguments[0] == "status");
            True(statusCall.Arguments.Contains("--ignore-externals", StringComparer.Ordinal), "status must ignore externals");
            var textDiff = calls.Last(call => call.Arguments[0] == "diff" && !call.Arguments.Contains("--properties-only", StringComparer.Ordinal));
            True(textDiff.Arguments.Contains("--internal-diff", StringComparer.Ordinal), "configured external diff command could run");
            True(textDiff.Arguments.Contains("--ignore-properties", StringComparer.Ordinal), "text and property diffs were not separated");
            True(textDiff.Arguments.Contains("--show-copies-as-adds", StringComparer.Ordinal), "copy additions were not requested");
            True(textDiff.Arguments.Contains("--extensions", StringComparer.Ordinal), "native diff preferences were not passed");
            Equal("-U 3", ArgumentAfter(textDiff, "--extensions"));
            True(!textDiff.Arguments.Contains("--ignore-externals", StringComparer.Ordinal) &&
                !textDiff.Arguments.Contains("--revision", StringComparer.Ordinal), "local diff requested unsupported external or revision options");
            Equal(Path.Combine(statusRoot, "modified @ file.txt"), textDiff.Arguments[^1]);

            var propertyCalls = PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
            var propertyOnly = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "modified @ file.txt",
                propertiesOnly: true).ConfigureAwait(false);
            Equal("Ready", propertyOnly.Status);
            True(!string.IsNullOrEmpty(propertyOnly.PropertyDiff), "properties-only output was empty");
            True(propertyOnly.Patch == null, "properties-only mode returned a text patch");
            var propertyDiff = ReadInvocations(propertyCalls).Single(call => call.Arguments[0] == "diff");
            True(propertyDiff.Arguments.Contains("--properties-only", StringComparer.Ordinal), "properties-only option was not selected");
            True(!propertyDiff.Arguments.Contains("--ignore-properties", StringComparer.Ordinal), "properties-only diff discarded properties");
        }).ConfigureAwait(false);

        await CheckAsync("local diff handles copied, missing, unversioned, binary, directory, special, and scheduled-delete states", async () =>
        {
            var copiedCalls = PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
            var copied = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "copied @ file.txt").ConfigureAwait(false);
            Equal("Ready", copied.Status);
            Equal("absent", copied.BeforeLabel);
            True(ReadInvocations(copiedCalls).Any(call => call.Arguments[0] == "diff" && call.Arguments.Contains("--show-copies-as-adds", StringComparer.Ordinal)),
                "copied add did not use native show-copies-as-adds");

            PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
            var missing = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "missing @ file.txt").ConfigureAwait(false);
            Equal("Ready", missing.Status);
            True(missing.IsMissing, "missing status was not explicit");
            Equal("BASE", missing.BeforeLabel);
            Equal("absent", missing.AfterLabel);
            Equal("BASE missing contents\n", Encoding.UTF8.GetString(missing.MissingBaseContent));

            PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
            var unversioned = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "unversioned @ file.txt").ConfigureAwait(false);
            True(unversioned.Status == "Ready", $"unversioned content returned {unversioned.Status}: {unversioned.Details}");
            Equal("absent", unversioned.BeforeLabel);
            Equal("new local text\n", Encoding.UTF8.GetString(unversioned.UnversionedContent));

            PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
            var directory = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "unversioned directory").ConfigureAwait(false);
            Equal("Directory", directory.Status);
            True(directory.IsDirectory, "unversioned directory was not identified");

            if (s_workingCopyLinksAvailable)
            {
                PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
                var symbolicLink = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "special @ link").ConfigureAwait(false);
                Equal("Special", symbolicLink.Status);
                True(symbolicLink.IsSpecial, "symlink was not shown as special");
                Equal("must not be read\n", await File.ReadAllTextAsync(Path.Combine(temp.Path, "outside wc data", "outside.txt")).ConfigureAwait(false));
            }

            var binaryCalls = PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
            var binary = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "binary @ file.dat").ConfigureAwait(false);
            Equal("Ready", binary.Status);
            True(binary.IsBinary, "invalid UTF-8 patch was not classified as binary");
            Equal(null, binary.Patch);
            True(ReadInvocations(binaryCalls).Any(call => call.Arguments[0] == "diff"), "binary detection did not exercise the native patch path");

            PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
            var binaryLookalike = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "binary-lookalike @ file.txt").ConfigureAwait(false);
            Equal("Ready", binaryLookalike.Status);
            True(!binaryLookalike.IsBinary, "ordinary added/deleted/context lines were mistaken for binary diagnostics");
            var lookalikePatch = Encoding.UTF8.GetString(binaryLookalike.Patch);
            True(lookalikePatch.Contains("-Binary files are ordinary source text", StringComparison.Ordinal), "deleted text line was not retained");
            True(lookalikePatch.Contains("+Cannot display: file marked as a binary type.", StringComparison.Ordinal), "added text line was not retained");
            True(lookalikePatch.Contains(" Binary files also appear in this context", StringComparison.Ordinal), "context text line was not retained");

            PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
            var deleted = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "deleted @ file.txt").ConfigureAwait(false);
            Equal("Ready", deleted.Status);
            True(!deleted.IsMissing, "scheduled deletion was confused with an unscheduled missing file");
            Equal("absent", deleted.AfterLabel);
            True(deleted.Patch?.Length > 0, "scheduled deletion did not return native diff output");
        }).ConfigureAwait(false);

        await CheckAsync("local path validation rejects traversal and symlinked ancestors without launching diff", async () =>
        {
            var invocations = PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
            var traversal = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "../outside.txt").ConfigureAwait(false);
            Equal("PathNotChanged", traversal.Status);
            True(!ReadInvocations(invocations).Any(call => call.Arguments[0] == "diff"), "path traversal launched svn diff");

            if (s_workingCopyLinksAvailable)
            {
                invocations = PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
                var escape = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, Path.Combine("escape", "outside.txt")).ConfigureAwait(false);
                Equal("PathOutsideWorkingCopy", escape.Status);
                True(!ReadInvocations(invocations).Any(call => call.Arguments[0] == "diff"), "symlinked ancestor launched svn diff");
                Equal("must not be read\n", await File.ReadAllTextAsync(Path.Combine(temp.Path, "outside wc data", "outside.txt")).ConfigureAwait(false));
            }
        }).ConfigureAwait(false);

        var mkfifo = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? FindExecutable("mkfifo") : string.Empty;
        if (!string.IsNullOrEmpty(mkfifo))
        {
            await CheckAsync("Unix FIFO status paths are rejected without blocking", async () =>
            {
                var fifoPath = Path.Combine(statusRoot, "unversioned @ file.txt");
                var backupPath = fifoPath + ".regular-backup";
                File.Move(fifoPath, backupPath);
                try
                {
                    await RunRequiredAsync(mkfifo, fifoPath).ConfigureAwait(false);
                    PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
                    var diffTask = Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "unversioned @ file.txt");
                    var timer = Stopwatch.StartNew();
                    var result = await diffTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    True(result.Status is "Failed" or "Unsupported", $"FIFO returned unexpected status '{result.Status}'");
                    True(timer.Elapsed < TimeSpan.FromSeconds(3), $"FIFO status took too long: {timer.Elapsed}");
                }
                finally
                {
                    if (File.Exists(fifoPath))
                        File.Delete(fifoPath);
                    File.Move(backupPath, fifoPath);
                }
            }).ConfigureAwait(false);
        }
        else
            Console.WriteLine("SKIP Unix FIFO check: mkfifo is unavailable on this platform.");

        await CheckAsync("missing BASE metadata preserves binary MIME and special-file states", async () =>
        {
            PrepareHistoryScenario(temp.Path, "wc_status_missing_binary", workingCopyPath);
            var binary = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "missing @ binary.txt").ConfigureAwait(false);
            Equal("Ready", binary.Status);
            True(binary.IsMissing && binary.IsBinary, "ASCII BASE bytes with binary MIME were treated as text");

            var calls = PrepareHistoryScenario(temp.Path, "wc_status_missing_special", workingCopyPath);
            var special = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "missing @ special.txt").ConfigureAwait(false);
            Equal("Special", special.Status);
            True(special.IsMissing && special.IsSpecial, "BASE svn:special was not surfaced");
            True(!ReadInvocations(calls).Any(call => call.Arguments[0] == "cat"), "BASE content for a special path was read as text");
        }).ConfigureAwait(false);

        await CheckAsync("working-copy status rejects bad revisions, malformed/DTD XML, oversize output, and cancellation", async () =>
        {
            foreach (var (scenario, expected) in new[]
            {
                ("wc_status_bad_revision", "Failed"),
                ("wc_status_malformed", "Failed"),
                ("wc_status_dtd", "Failed"),
                ("wc_status_large", "TooLarge"),
            })
            {
                PrepareHistoryScenario(temp.Path, scenario, workingCopyPath);
                var result = await Svn.GetStatusAsync(executable, config, workingCopyPath).ConfigureAwait(false);
                Equal(expected, result.Status);
            }

            var invocations = PrepareHistoryScenario(temp.Path, "wc_status_slow", workingCopyPath);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var cancelled = false;
            try
            {
                await Svn.GetStatusAsync(executable, config, workingCopyPath, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            True(cancelled, "status cancellation was converted to a normal result");
            var processCall = ReadInvocations(invocations).Last(call => call.Arguments[0] == "status");
            var waitUntil = Stopwatch.StartNew();
            while (IsProcessRunning(processCall.ProcessId) && waitUntil.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(20).ConfigureAwait(false);
            True(!IsProcessRunning(processCall.ProcessId), "cancelled status child remained alive");
        }).ConfigureAwait(false);

        await CheckAsync("local diff cancellation and output/content limits are bounded", async () =>
        {
            var patchCalls = PrepareHistoryScenario(temp.Path, "wc_status_patch_large", workingCopyPath);
            var tooLargePatch = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "binary @ file.dat").ConfigureAwait(false);
            Equal("TooLarge", tooLargePatch.Status);
            True(ReadInvocations(patchCalls).Any(call => call.Arguments[0] == "diff"), "large patch fixture did not launch diff");

            var hugePath = Path.Combine(statusRoot, "unversioned @ file.txt");
            await File.WriteAllBytesAsync(hugePath, new byte[8 * 1024 * 1024 + 1]).ConfigureAwait(false);
            PrepareHistoryScenario(temp.Path, "wc_status_main", workingCopyPath);
            var tooLargeFile = await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "unversioned @ file.txt").ConfigureAwait(false);
            Equal("TooLarge", tooLargeFile.Status);

            var invocations = PrepareHistoryScenario(temp.Path, "wc_status_diff_slow", workingCopyPath);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            var cancelled = false;
            try
            {
                await Svn.GetLocalDiffAsync(executable, config, workingCopyPath, "modified @ file.txt", cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            True(cancelled, "local diff cancellation was converted to a normal result");
            var processCall = ReadInvocations(invocations).Last(call => call.Arguments[0] == "diff");
            var waitUntil = Stopwatch.StartNew();
            while (IsProcessRunning(processCall.ProcessId) && waitUntil.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(20).ConfigureAwait(false);
            True(!IsProcessRunning(processCall.ProcessId), "cancelled local diff child remained alive");
        }).ConfigureAwait(false);

        await CheckAsync("invalid configuration paths are rejected before launching SVN", async () =>
        {
            var invocations = PrepareScenario(temp.Path, "ready");
            var relative = await Svn.TestConnectionAsync(executable, "relative/config", "svn://fixture/repo").ConfigureAwait(false);
            Equal("InvalidConfigDirectory", relative.Status);
            var file = Path.Combine(temp.Path, "not-a-directory");
            await File.WriteAllTextAsync(file, "fixture").ConfigureAwait(false);
            var notDirectory = await Svn.TestConnectionAsync(executable, file, "svn://fixture/repo").ConfigureAwait(false);
            Equal("InvalidConfigDirectory", notDirectory.Status);
            Equal(0, ReadInvocations(invocations).Count);
        }).ConfigureAwait(false);
    }

    private static async Task RunIntegrationAsync()
    {
        var svn = FindExecutable("svn");
        var svnadmin = FindExecutable("svnadmin");
        var svnserve = FindExecutable("svnserve");
        RequireTool(svn, "svn");
        RequireTool(svnadmin, "svnadmin");
        RequireTool(svnserve, "svnserve");

        using var temp = new TemporaryDirectory("sourcegit-svn-integration");
        var root = Path.Combine(temp.Path, "repositories");
        Directory.CreateDirectory(root);
        var publicRepository = Path.Combine(root, "public");
        var privateRepository = Path.Combine(root, "private");
        await RunRequiredAsync(svnadmin, "create", publicRepository).ConfigureAwait(false);
        await RunRequiredAsync(svnadmin, "create", privateRepository).ConfigureAwait(false);

        var privateFileUrl = new Uri(privateRepository).AbsoluteUri.TrimEnd('/');
        var integrationTargetRelativePath = Path.Combine("nested @ folder", "renamed @ note.txt");
        await CreateLocalHistoryAsync(svn, privateFileUrl, temp.Path).ConfigureAwait(false);

        WriteServerConfig(publicRepository, anonymousAccess: "read");
        WriteServerConfig(privateRepository, anonymousAccess: "none");
        var port = GetFreePort();
        var server = StartSvnServe(svnserve, root, port);
        try
        {
            await WaitForServerAsync(server, port).ConfigureAwait(false);
            var isolatedConfig = Path.Combine(temp.Path, "client-config-配置");
            Directory.CreateDirectory(isolatedConfig);
            await File.WriteAllTextAsync(Path.Combine(isolatedConfig, "config"),
                "[auth]\npassword-stores =\nstore-passwords = no\nstore-plaintext-passwords = no\n").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(isolatedConfig, "servers"),
                "[global]\nstore-plaintext-passwords = no\n").ConfigureAwait(false);

            await RunWorkingCopyActionIntegrationAsync(svn, svnadmin, temp.Path).ConfigureAwait(false);

            var publicUrl = $"svn://127.0.0.1:{port}/public";
            var privateUrl = $"svn://127.0.0.1:{port}/private";
            Equal("Ready", (await Svn.TestConnectionAsync(svn, isolatedConfig, publicUrl).ConfigureAwait(false)).Status);

            var missing = await Svn.TestConnectionAsync(svn, isolatedConfig, privateUrl).ConfigureAwait(false);
            Equal("AuthenticationRequired", missing.Status);

            var wrong = await Svn.TestConnectionAsync(svn, isolatedConfig, privateUrl,
                new Svn.Credentials("alice", "wrong-password", false)).ConfigureAwait(false);
            Equal("AuthenticationFailed", wrong.Status);
            True(!wrong.Details.Contains("wrong-password", StringComparison.Ordinal), "wrong password leaked to diagnostics");

            var valid = await Svn.TestConnectionAsync(svn, isolatedConfig, privateUrl,
                new Svn.Credentials("alice", "integration-correct-password", false)).ConfigureAwait(false);
            Equal("Ready", valid.Status);
            True(!valid.Details.Contains("integration-correct-password", StringComparison.Ordinal), "password leaked to diagnostics");

            var forbidden = await Svn.TestConnectionAsync(svn, isolatedConfig, privateUrl,
                new Svn.Credentials("blocked", "integration-blocked-password", false)).ConfigureAwait(false);
            True(forbidden.Status is "AccessDenied" or "AuthenticationFailed" or "Failed",
                $"forbidden credentials unexpectedly succeeded or were misclassified: {forbidden.Status}");
            True(!string.IsNullOrWhiteSpace(forbidden.Details), "forbidden response lost the server diagnostic");
            True(forbidden.Details.Contains("E175013", StringComparison.OrdinalIgnoreCase) ||
                 forbidden.Details.Contains("E170001", StringComparison.OrdinalIgnoreCase) ||
                 forbidden.Details.Contains("access", StringComparison.OrdinalIgnoreCase),
                "forbidden response did not preserve an access/authentication diagnostic");
            True(!forbidden.Details.Contains("integration-blocked-password", StringComparison.Ordinal), "password leaked to diagnostics");
            var authDirectory = Path.Combine(isolatedConfig, "auth");
            var authFiles = Directory.Exists(authDirectory)
                ? Directory.GetFiles(authDirectory, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
            Equal(0, authFiles.Length);
            s_passed += 6;

            await CheckAsync("authenticated svnserve history preserves copy ancestry and pages over gaps", async () =>
            {
                var integrationConfig = Path.Combine(temp.Path, "integration client config");
                Directory.CreateDirectory(integrationConfig);
                await File.WriteAllTextAsync(Path.Combine(integrationConfig, "config"),
                    "[auth]\npassword-stores =\nstore-passwords = no\nstore-plaintext-passwords = no\n").ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(integrationConfig, "servers"),
                    "[global]\nstore-plaintext-passwords = no\n").ConfigureAwait(false);

                var integrationWorkingCopy = Path.Combine(temp.Path, "authenticated working copy");
                await RunRequiredWithPasswordAsync(svn, "integration-correct-password",
                    "checkout", "--non-interactive", "--no-auth-cache", "--config-dir", integrationConfig,
                    "--username", "alice", "--password-from-stdin", privateUrl + "/trunk", integrationWorkingCopy).ConfigureAwait(false);
                var targetPath = Path.Combine(integrationWorkingCopy, integrationTargetRelativePath);
                var localContents = await File.ReadAllTextAsync(targetPath).ConfigureAwait(false);
                await File.AppendAllTextAsync(targetPath, "uncommitted local edit\n").ConfigureAwait(false);
                var modifiedContents = await File.ReadAllTextAsync(targetPath).ConfigureAwait(false);
                var statusBefore = await RunCaptureAsync(svn, "status", "--xml", targetPath + "@").ConfigureAwait(false);
                True(statusBefore.StandardOutput.Contains("item=\"modified\"", StringComparison.Ordinal), "fixture failed to create a local modification");

                var info = await Svn.GetWorkingCopyAsync(svn, integrationConfig, targetPath).ConfigureAwait(false);
                Equal("Ready", info.Status);
                True(Uri.UnescapeDataString(info.Url).EndsWith("/private/trunk/nested @ folder/renamed @ note.txt", StringComparison.Ordinal),
                    $"working-copy URL did not identify the renamed @ path: {info.Url}");
                var unauthenticated = await Svn.GetHistoryAsync(svn, integrationConfig, targetPath, limit: 2, expectedUrl: info.Url).ConfigureAwait(false);
                Equal("AuthenticationRequired", unauthenticated.Status);

                var wrong = await Svn.GetHistoryAsync(svn, integrationConfig, targetPath, limit: 2,
                    credentials: new Svn.Credentials("alice", "wrong-history-password", false), expectedUrl: info.Url).ConfigureAwait(false);
                Equal("AuthenticationFailed", wrong.Status);
                True(!wrong.Details.Contains("wrong-history-password", StringComparison.Ordinal), "history password leaked to diagnostics");

                var firstPage = await Svn.GetHistoryAsync(svn, integrationConfig, targetPath, limit: 2,
                    credentials: new Svn.Credentials("alice", "integration-correct-password", false), expectedUrl: info.Url).ConfigureAwait(false);
                Equal("Ready", firstPage.Status);
                SequenceEqual(new long[] { 4, 3 }, firstPage.Revisions.ConvertAll(revision => revision.Number));
                Equal("Rename target to @ path", firstPage.Revisions[0].Subject);
                Equal("Update target\nSecond line: 保持 Unicode 🌱", firstPage.Revisions[1].Message);

                var secondPage = await Svn.GetHistoryAsync(svn, integrationConfig, targetPath, beforeRevision: 3, limit: 2,
                    credentials: new Svn.Credentials("alice", "integration-correct-password", false), expectedUrl: info.Url).ConfigureAwait(false);
                Equal("Ready", secondPage.Status);
                SequenceEqual(new long[] { 1 }, secondPage.Revisions.ConvertAll(revision => revision.Number));
                Equal("Add target\nSecond line: 保持 Unicode 🌱\nthird line", secondPage.Revisions[0].Message);
                True(!string.IsNullOrEmpty(secondPage.Revisions[0].Author) && secondPage.Revisions[0].Date.HasValue,
                    "real svnserve history omitted authored revision metadata unexpectedly");

                Equal(modifiedContents, await File.ReadAllTextAsync(targetPath).ConfigureAwait(false));
                Equal(localContents + "uncommitted local edit\n", await File.ReadAllTextAsync(targetPath).ConfigureAwait(false));
                var statusAfter = await RunCaptureAsync(svn, "status", "--xml", targetPath + "@").ConfigureAwait(false);
                True(statusAfter.StandardOutput.Contains("item=\"modified\"", StringComparison.Ordinal), "history changed the local modification state");
                Equal(NormalizeXmlAttributes(statusBefore.StandardOutput), NormalizeXmlAttributes(statusAfter.StandardOutput));
                var historyAuthFiles = Directory.Exists(authDirectory)
                    ? Directory.GetFiles(authDirectory, "*", SearchOption.AllDirectories)
                    : Array.Empty<string>();
                Equal(0, historyAuthFiles.Length);
            }).ConfigureAwait(false);

            await CheckAsync("authenticated svnserve changed paths follow copied scope and preserve local edits", async () =>
            {
                var integrationConfig = Path.Combine(temp.Path, "integration changed paths config");
                Directory.CreateDirectory(integrationConfig);
                await File.WriteAllTextAsync(Path.Combine(integrationConfig, "config"),
                    "[auth]\npassword-stores =\nstore-passwords = no\nstore-plaintext-passwords = no\n").ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(integrationConfig, "servers"),
                    "[global]\nstore-plaintext-passwords = no\n").ConfigureAwait(false);

                var integrationWorkingCopy = Path.Combine(temp.Path, "changed paths working copy");
                await RunRequiredWithPasswordAsync(svn, "integration-correct-password",
                    "checkout", "--non-interactive", "--no-auth-cache", "--config-dir", integrationConfig,
                    "--username", "alice", "--password-from-stdin", privateUrl + "/trunk", integrationWorkingCopy).ConfigureAwait(false);
                var copiedTargetRelativePath = Path.Combine("copied @ scope", "renamed @ note.txt");
                var targetPath = Path.Combine(integrationWorkingCopy, copiedTargetRelativePath);
                var localContents = await File.ReadAllTextAsync(targetPath).ConfigureAwait(false);
                await File.AppendAllTextAsync(targetPath, "uncommitted changed-path edit\n").ConfigureAwait(false);
                var modifiedContents = await File.ReadAllTextAsync(targetPath).ConfigureAwait(false);
                var statusBefore = await RunCaptureAsync(svn, "status", "--xml", targetPath + "@").ConfigureAwait(false);
                True(statusBefore.StandardOutput.Contains("item=\"modified\"", StringComparison.Ordinal), "fixture failed to create a changed-path local modification");

                var info = await Svn.GetWorkingCopyAsync(svn, integrationConfig, targetPath).ConfigureAwait(false);
                Equal("Ready", info.Status);
                True(Uri.UnescapeDataString(info.Url).EndsWith("/trunk/copied @ scope/renamed @ note.txt", StringComparison.Ordinal),
                    $"working-copy URL did not identify the copied historical scope: {info.Url}");

                var unauthenticated = await Svn.GetChangedPathsAsync(svn, integrationConfig, targetPath, 5, expectedUrl: info.Url).ConfigureAwait(false);
                Equal("AuthenticationRequired", unauthenticated.Status);
                var wrong = await Svn.GetChangedPathsAsync(svn, integrationConfig, targetPath, 5,
                    new Svn.Credentials("alice", "wrong-changed-path-password", false), expectedUrl: info.Url).ConfigureAwait(false);
                Equal("AuthenticationFailed", wrong.Status);
                True(!wrong.Details.Contains("wrong-changed-path-password", StringComparison.Ordinal), "changed-path password leaked to diagnostics");

                var copyRevision = await Svn.GetChangedPathsAsync(svn, integrationConfig, targetPath, 5,
                    new Svn.Credentials("alice", "integration-correct-password", false), expectedUrl: info.Url).ConfigureAwait(false);
                Equal("Ready", copyRevision.Status);
                Equal(1, copyRevision.Paths.Count);
                var directoryCopy = copyRevision.Paths[0];
                Equal("/trunk/copied @ scope", directoryCopy.Path);
                Equal(".", directoryCopy.RelativePath);
                Equal("A", directoryCopy.Action);
                Equal("dir", directoryCopy.Kind);
                Equal("/trunk/nested @ folder", directoryCopy.CopyFromPath);
                Equal(4L, directoryCopy.CopyFromRevision);

                var preCopyRevision = await Svn.GetChangedPathsAsync(svn, integrationConfig, targetPath, 4,
                    new Svn.Credentials("alice", "integration-correct-password", false), expectedUrl: info.Url).ConfigureAwait(false);
                Equal("Ready", preCopyRevision.Status);
                Equal(1, preCopyRevision.Paths.Count);
                Equal("/trunk/nested @ folder/renamed @ note.txt", preCopyRevision.Paths[0].Path);
                Equal(".", preCopyRevision.Paths[0].RelativePath);
                Equal("A", preCopyRevision.Paths[0].Action);
                Equal("/trunk/nested @ folder/target @ note.txt", preCopyRevision.Paths[0].CopyFromPath);
                Equal(3L, preCopyRevision.Paths[0].CopyFromRevision);

                var gap = await Svn.GetChangedPathsAsync(svn, integrationConfig, targetPath, 2,
                    new Svn.Credentials("alice", "integration-correct-password", false), expectedUrl: info.Url).ConfigureAwait(false);
                Equal("Ready", gap.Status);
                Equal(0, gap.Paths.Count);

                Equal(modifiedContents, await File.ReadAllTextAsync(targetPath).ConfigureAwait(false));
                Equal(localContents + "uncommitted changed-path edit\n", await File.ReadAllTextAsync(targetPath).ConfigureAwait(false));
                var statusAfter = await RunCaptureAsync(svn, "status", "--xml", targetPath + "@").ConfigureAwait(false);
                True(statusAfter.StandardOutput.Contains("item=\"modified\"", StringComparison.Ordinal), "changed-path loading changed the local modification state");
                Equal(NormalizeXmlAttributes(statusBefore.StandardOutput), NormalizeXmlAttributes(statusAfter.StandardOutput));
                var changedPathsAuthDirectory = Path.Combine(integrationConfig, "auth");
                var changedPathsAuthFiles = Directory.Exists(changedPathsAuthDirectory)
                    ? Directory.GetFiles(changedPathsAuthDirectory, "*", SearchOption.AllDirectories)
                    : Array.Empty<string>();
                var authFiles = Directory.Exists(authDirectory)
                    ? Directory.GetFiles(authDirectory, "*", SearchOption.AllDirectories).Concat(changedPathsAuthFiles).ToArray()
                    : changedPathsAuthFiles;
                Equal(0, authFiles.Length);
            }).ConfigureAwait(false);

            await CheckAsync("authenticated svnserve snapshots read copied text, escaped paths, empty files, and binary bytes without touching local edits", async () =>
            {
                var snapshotConfig = Path.Combine(temp.Path, "snapshot client config");
                Directory.CreateDirectory(snapshotConfig);
                await File.WriteAllTextAsync(Path.Combine(snapshotConfig, "config"),
                    "[auth]\npassword-stores =\nstore-passwords = no\nstore-plaintext-passwords = no\n").ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(snapshotConfig, "servers"),
                    "[global]\nstore-plaintext-passwords = no\n").ConfigureAwait(false);

                var workingCopy = Path.Combine(temp.Path, "snapshot @ working copy");
                await RunRequiredWithPasswordAsync(svn, "integration-correct-password",
                    "checkout", "--non-interactive", "--no-auth-cache", "--config-dir", snapshotConfig,
                    "--username", "alice", "--password-from-stdin", privateUrl + "/trunk", workingCopy).ConfigureAwait(false);
                var credentials = new Svn.Credentials("alice", "integration-correct-password", false);
                var targetPath = Path.Combine(workingCopy, "nested @ folder", "renamed @ note.txt");
                var targetInfo = await Svn.GetWorkingCopyAsync(svn, snapshotConfig, targetPath).ConfigureAwait(false);
                Equal("Ready", targetInfo.Status);
                var originalContents = await File.ReadAllBytesAsync(targetPath).ConfigureAwait(false);
                await File.AppendAllTextAsync(targetPath, "local snapshot preservation edit\n").ConfigureAwait(false);
                var modifiedContents = await File.ReadAllBytesAsync(targetPath).ConfigureAwait(false);
                var statusBefore = await RunCaptureAsync(svn, "status", "--xml", targetPath + "@").ConfigureAwait(false);
                True(statusBefore.StandardOutput.Contains("item=\"modified\"", StringComparison.Ordinal), "snapshot fixture did not create a local modification");

                var modification = await Svn.GetFileDiffAsync(svn, snapshotConfig, targetPath, 3,
                    "/trunk/nested @ folder/target @ note.txt", credentials, expectedUrl: targetInfo.Url).ConfigureAwait(false);
                Equal("Ready", modification.Status);
                Equal(2L, modification.Before.Revision);
                Equal("/trunk/nested @ folder/target @ note.txt", modification.Before.Path);
                Equal("initial contents\n", Encoding.UTF8.GetString(modification.Before.Content));
                Equal(3L, modification.After.Revision);
                Equal("initial contents\nupdated contents\n", Encoding.UTF8.GetString(modification.After.Content));

                var copied = await Svn.GetFileDiffAsync(svn, snapshotConfig, targetPath, 4,
                    "/trunk/nested @ folder/renamed @ note.txt", credentials, expectedUrl: targetInfo.Url).ConfigureAwait(false);
                Equal("Ready", copied.Status);
                Equal("/trunk/nested @ folder/target @ note.txt", copied.Before.Path);
                Equal(3L, copied.Before.Revision);
                Equal("/trunk/nested @ folder/renamed @ note.txt", copied.After.Path);
                Equal(4L, copied.After.Revision);
                SequenceEqual(copied.Before.Content, copied.After.Content);

                var escapedRelative = Path.Combine("nested @ folder", s_escapedSnapshotName);
                var escapedPath = Path.Combine(workingCopy, escapedRelative);
                var escapedInfo = await Svn.GetWorkingCopyAsync(svn, snapshotConfig, escapedPath).ConfigureAwait(false);
                Equal("Ready", escapedInfo.Status);
                var escaped = await Svn.GetFileDiffAsync(svn, snapshotConfig, escapedPath, 3,
                    "/trunk/nested @ folder/" + s_escapedSnapshotName, credentials, expectedUrl: escapedInfo.Url).ConfigureAwait(false);
                Equal("Ready", escaped.Status);
                Equal("before escaped path\n", Encoding.UTF8.GetString(escaped.Before.Content));
                Equal("before escaped path\nafter escaped path\n", Encoding.UTF8.GetString(escaped.After.Content));

                var emptyPath = Path.Combine(workingCopy, "nested @ folder", "empty @ file.txt");
                var emptyInfo = await Svn.GetWorkingCopyAsync(svn, snapshotConfig, emptyPath).ConfigureAwait(false);
                var empty = await Svn.GetFileDiffAsync(svn, snapshotConfig, emptyPath, 1,
                    "/trunk/nested @ folder/empty @ file.txt", credentials, expectedUrl: emptyInfo.Url).ConfigureAwait(false);
                Equal("Ready", empty.Status);
                True(empty.Before == null && empty.After != null, "real SVN empty add confused absent and empty snapshots");
                Equal(0, empty.After.Content.Length);

                var binaryPath = Path.Combine(workingCopy, "nested @ folder", "binary @ file.bin");
                var binaryInfo = await Svn.GetWorkingCopyAsync(svn, snapshotConfig, binaryPath).ConfigureAwait(false);
                var binary = await Svn.GetFileDiffAsync(svn, snapshotConfig, binaryPath, 3,
                    "/trunk/nested @ folder/binary @ file.bin", credentials, expectedUrl: binaryInfo.Url).ConfigureAwait(false);
                Equal("Ready", binary.Status);
                True(binary.Before.IsBinary && binary.After.IsBinary, "real SVN MIME metadata did not mark binary snapshots");
                SequenceEqual(new byte[] { 0, 255, 1, 2 }, binary.Before.Content);
                SequenceEqual(new byte[] { 0, 255, 1, 2, 0, 9 }, binary.After.Content);

                SequenceEqual(modifiedContents, await File.ReadAllBytesAsync(targetPath).ConfigureAwait(false));
                True(!originalContents.SequenceEqual(modifiedContents), "local modification fixture was not different from BASE");
                var statusAfter = await RunCaptureAsync(svn, "status", "--xml", targetPath + "@").ConfigureAwait(false);
                True(statusAfter.StandardOutput.Contains("item=\"modified\"", StringComparison.Ordinal), "snapshot loading changed local modified status");
                Equal(NormalizeXmlAttributes(statusBefore.StandardOutput), NormalizeXmlAttributes(statusAfter.StandardOutput));
                var authFiles = Directory.Exists(Path.Combine(snapshotConfig, "auth"))
                    ? Directory.GetFiles(Path.Combine(snapshotConfig, "auth"), "*", SearchOption.AllDirectories)
                    : Array.Empty<string>();
                Equal(0, authFiles.Length);

                if (!server.HasExited)
                    server.Kill(true);
                await server.WaitForExitAsync().ConfigureAwait(false);
                True(server.HasExited, "local svnserve remained active for the offline working-copy checks");

                var unversionedPath = Path.Combine(workingCopy, "offline @ unversioned.txt");
                await File.WriteAllTextAsync(unversionedPath, "offline local content\n").ConfigureAwait(false);
                var missingPath = Path.Combine(workingCopy, "nested @ folder", "empty @ file.txt");
                File.Delete(missingPath);
                var statusBeforeOffline = await RunCaptureAsync(svn, "status", "--xml", "--ignore-externals", "--non-interactive", "--no-auth-cache", "--", workingCopy + "@").ConfigureAwait(false);
                var modifiedBeforeOffline = await File.ReadAllBytesAsync(targetPath).ConfigureAwait(false);

                var offlineStatus = await Svn.GetStatusAsync(svn, snapshotConfig, workingCopy).ConfigureAwait(false);
                Equal("Ready", offlineStatus.Status);
                True(offlineStatus.Entries.Any(entry => entry.Item == "modified" && entry.Path.EndsWith(Path.Combine("nested @ folder", "renamed @ note.txt"), StringComparison.Ordinal)),
                    "offline status did not show the existing local edit");
                True(offlineStatus.Entries.Any(entry => entry.Item == "unversioned" && entry.Path == "offline @ unversioned.txt"),
                    "offline status did not show the unversioned file");
                True(offlineStatus.Entries.Any(entry => entry.Item == "missing" && entry.Path.EndsWith(Path.Combine("nested @ folder", "empty @ file.txt"), StringComparison.Ordinal)),
                    "offline status did not show the missing tracked file");

                var localDiff = await Svn.GetLocalDiffAsync(svn, snapshotConfig, workingCopy, Path.Combine("nested @ folder", "renamed @ note.txt")).ConfigureAwait(false);
                True(localDiff.Status == "Ready", $"offline local diff returned {localDiff.Status}: {localDiff.Details}");
                True(Encoding.UTF8.GetString(localDiff.Patch).Contains("local snapshot preservation edit", StringComparison.Ordinal),
                    "offline local diff omitted the existing edit");
                var unversionedDiff = await Svn.GetLocalDiffAsync(svn, snapshotConfig, workingCopy, "offline @ unversioned.txt").ConfigureAwait(false);
                Equal("offline local content\n", Encoding.UTF8.GetString(unversionedDiff.UnversionedContent));
                var missingDiff = await Svn.GetLocalDiffAsync(svn, snapshotConfig, workingCopy,
                    Path.Combine("nested @ folder", "empty @ file.txt")).ConfigureAwait(false);
                Equal("Ready", missingDiff.Status);
                True(missingDiff.IsMissing, "offline missing diff lost its missing state");
                Equal(0, missingDiff.MissingBaseContent.Length);

                SequenceEqual(modifiedBeforeOffline, await File.ReadAllBytesAsync(targetPath).ConfigureAwait(false));
                True(!File.Exists(missingPath), "local diff recreated the missing tracked file");
                var statusAfterOffline = await RunCaptureAsync(svn, "status", "--xml", "--ignore-externals", "--non-interactive", "--no-auth-cache", "--", workingCopy + "@").ConfigureAwait(false);
                Equal(NormalizeXmlAttributes(statusBeforeOffline.StandardOutput), NormalizeXmlAttributes(statusAfterOffline.StandardOutput));
            }).ConfigureAwait(false);

            var probe = await Svn.ProbeAsync(svn, isolatedConfig).ConfigureAwait(false);
            Equal("Ready", probe.Status);
            if (OperatingSystem.IsMacOS())
                True(probe.CredentialStores?.Contains("keychain", StringComparison.OrdinalIgnoreCase) == true,
                    $"installed SVN did not report a parsed Keychain provider: {probe.CredentialStores ?? "unknown"}");
            s_passed++;
        }
        finally
        {
            try
            {
                if (!server.HasExited)
                    server.Kill(true);
                await server.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Server already exited.
            }
            finally
            {
                server.Dispose();
            }
        }
    }

    private static async Task RunWorkingCopyActionIntegrationAsync(
        string svn,
        string svnadmin,
        string tempDirectory)
    {
        var root = Path.Combine(tempDirectory, "working-copy-actions");
        var repository = Path.Combine(root, "repository");
        var seed = Path.Combine(root, "seed");
        var workingCopy = Path.Combine(root, "working copy @ local");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(Path.Combine(seed, "nested @ folder"));
        Directory.CreateDirectory(Path.Combine(seed, "retained directory", "nested"));
        Directory.CreateDirectory(Path.Combine(seed, "safe missing directory", "nested"));
        Directory.CreateDirectory(Path.Combine(seed, "property missing directory", "nested"));
        Directory.CreateDirectory(Path.Combine(seed, "external missing directory"));
        await File.WriteAllTextAsync(Path.Combine(seed, "modified @ file.txt"), "base contents\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(seed, "missing @ file.txt"), "missing base contents\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(seed, "nested @ folder", "retained @ data.txt"), "nested base contents\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(seed, "retained directory", "nested", "data.txt"), "directory contents stay local\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(seed, "safe missing directory", "nested", "data.txt"), "missing directory bytes stay local\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(seed, "property missing directory", "nested", "data.txt"), "property-edited child bytes stay local\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(seed, "external missing directory", "placeholder.txt"), "file external holder\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(seed, "external-source.txt"), "external file bytes\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[miscellany]\nenable-auto-props = yes\n[auto-props]\n*.txt = svn:eol-style=native\n").ConfigureAwait(false);

        await RunRequiredAsync(svnadmin, "create", repository).ConfigureAwait(false);
        var repositoryUrl = new Uri(repository).AbsoluteUri;
        await RunRequiredAsync(svn, "import", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            "--message", "Create isolated local action fixture", seed, repositoryUrl).ConfigureAwait(false);

        var stagingWorkingCopy = Path.Combine(root, "external staging");
        await RunRequiredAsync(svn, "checkout", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            repositoryUrl, stagingWorkingCopy).ConfigureAwait(false);
        await RunRequiredAsync(svn, "propset", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            "svn:externals", "^/external-source.txt linked.txt",
            Path.Combine(stagingWorkingCopy, "external missing directory")).ConfigureAwait(false);
        await RunRequiredAsync(svn, "commit", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            "--message", "Add local file external fixture", stagingWorkingCopy).ConfigureAwait(false);
        await RunRequiredAsync(svn, "update", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            stagingWorkingCopy).ConfigureAwait(false);

        await RunRequiredAsync(svn, "checkout", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            repositoryUrl, workingCopy).ConfigureAwait(false);

        var safeMissingDirectory = Path.Combine(workingCopy, "safe missing directory");
        var safeMissingBackup = Path.Combine(root, "safe missing directory backup");
        var safeMissingBytes = await File.ReadAllBytesAsync(Path.Combine(safeMissingDirectory, "nested", "data.txt")).ConfigureAwait(false);
        Directory.Move(safeMissingDirectory, safeMissingBackup);
        var removeMissingDirectory = await Svn.RemoveAsync(svn, config, workingCopy, "safe missing directory").ConfigureAwait(false);
        True(removeMissingDirectory.Status == "Ready", $"missing directory removal returned {removeMissingDirectory.Status}: {removeMissingDirectory.Details}");
        SequenceEqual(safeMissingBytes, await File.ReadAllBytesAsync(Path.Combine(safeMissingBackup, "nested", "data.txt")).ConfigureAwait(false));
        var safeMissingStatus = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
        True(safeMissingStatus.Entries.Any(entry => entry.Path == "safe missing directory" && entry.Item == "deleted"),
            "missing directory removal did not schedule a delete");

        var propertyChild = Path.Combine(workingCopy, "property missing directory", "nested", "data.txt");
        await RunRequiredAsync(svn, "propset", "--non-interactive", "--no-auth-cache", "--config-dir", config,
            "svn:mime-type", "application/octet-stream", propertyChild).ConfigureAwait(false);
        var propertyDirectory = Path.Combine(workingCopy, "property missing directory");
        var propertyBackup = Path.Combine(root, "property missing directory backup");
        var propertyBytes = await File.ReadAllBytesAsync(propertyChild).ConfigureAwait(false);
        Directory.Move(propertyDirectory, propertyBackup);
        var removePropertyDirectory = await Svn.RemoveAsync(svn, config, workingCopy, "property missing directory").ConfigureAwait(false);
        Equal("NotEligible", removePropertyDirectory.Status);
        SequenceEqual(propertyBytes, await File.ReadAllBytesAsync(Path.Combine(propertyBackup, "nested", "data.txt")).ConfigureAwait(false));

        var externalDirectory = Path.Combine(workingCopy, "external missing directory");
        var externalBackup = Path.Combine(root, "external missing directory backup");
        var externalBytes = await File.ReadAllBytesAsync(Path.Combine(externalDirectory, "linked.txt")).ConfigureAwait(false);
        Directory.Move(externalDirectory, externalBackup);
        var removeExternalDirectory = await Svn.RemoveAsync(svn, config, workingCopy, "external missing directory").ConfigureAwait(false);
        Equal("NotEligible", removeExternalDirectory.Status);
        SequenceEqual(externalBytes, await File.ReadAllBytesAsync(Path.Combine(externalBackup, "linked.txt")).ConfigureAwait(false));

        var addedName = "-- option @ 文档🌱.txt";
        var addedPath = Path.Combine(workingCopy, addedName);
        const string addedContents = "auto properties remain enabled\n";
        await File.WriteAllTextAsync(addedPath, addedContents).ConfigureAwait(false);
        var addFile = await Svn.AddAsync(svn, config, workingCopy, addedName).ConfigureAwait(false);
        True(addFile.Status == "Ready", $"file add returned {addFile.Status}: {addFile.Details}");
        Equal(addedContents, await File.ReadAllTextAsync(addedPath).ConfigureAwait(false));
        var autoProperties = await RunCaptureAsync(svn, "proplist", "--xml", "--verbose", "--non-interactive", "--no-auth-cache",
            "--config-dir", config, "--", addedPath + "@").ConfigureAwait(false);
        True(autoProperties.StandardOutput.Contains("svn:eol-style", StringComparison.Ordinal), "svn add disabled configured auto-properties");
        var addStatus = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
        True(addStatus.Entries.Any(entry => entry.Path == addedName && entry.Item == "added"), "local SVN did not schedule the Unicode/@ file addition");
        var undoAddFile = await Svn.UndoAsync(svn, config, workingCopy, addedName).ConfigureAwait(false);
        True(undoAddFile.Status == "Ready", $"file-add undo returned {undoAddFile.Status}: {undoAddFile.Details}");
        Equal(addedContents, await File.ReadAllTextAsync(addedPath).ConfigureAwait(false));
        var undoAddStatus = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
        True(!undoAddStatus.Entries.Any(entry => entry.Path == addedName && entry.Item == "added"), "file-add undo left the path scheduled for addition");

        var directoryName = "new directory @";
        var childRelative = Path.Combine(directoryName, "child @ file.txt");
        var childPath = Path.Combine(workingCopy, childRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(childPath)!);
        await File.WriteAllTextAsync(childPath, "child remains unversioned\n").ConfigureAwait(false);
        var addDirectory = await Svn.AddAsync(svn, config, workingCopy, directoryName).ConfigureAwait(false);
        True(addDirectory.Status == "Ready", $"directory add returned {addDirectory.Status}: {addDirectory.Details}");
        Equal("child remains unversioned\n", await File.ReadAllTextAsync(childPath).ConfigureAwait(false));
        var directoryStatus = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
        True(directoryStatus.Entries.Any(entry => entry.Path == directoryName && entry.Item == "added"), "directory was not scheduled for addition");
        True(directoryStatus.Entries.Any(entry => entry.Path == childRelative && entry.Item == "unversioned"), "directory add recursively scheduled its child");
        var addChild = await Svn.AddAsync(svn, config, workingCopy, childRelative).ConfigureAwait(false);
        True(addChild.Status == "Ready", $"child add returned {addChild.Status}: {addChild.Details}");
        var undoChild = await Svn.UndoAsync(svn, config, workingCopy, childRelative).ConfigureAwait(false);
        True(undoChild.Status == "Ready", $"child-add undo returned {undoChild.Status}: {undoChild.Details}");
        Equal("child remains unversioned\n", await File.ReadAllTextAsync(childPath).ConfigureAwait(false));
        var childUndoStatus = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
        True(childUndoStatus.Entries.Any(entry => entry.Path == directoryName && entry.Item == "added"), "undoing an added child changed its added parent");
        True(childUndoStatus.Entries.Any(entry => entry.Path == childRelative && entry.Item == "unversioned"), "undoing an added child did not leave its bytes unversioned");
        var undoAddDirectory = await Svn.UndoAsync(svn, config, workingCopy, directoryName).ConfigureAwait(false);
        True(undoAddDirectory.Status == "Ready", $"directory-add undo returned {undoAddDirectory.Status}: {undoAddDirectory.Details}");
        Equal("child remains unversioned\n", await File.ReadAllTextAsync(childPath).ConfigureAwait(false));
        var directoryUndoStatus = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
        True(directoryUndoStatus.Entries.Any(entry => entry.Path == directoryName && entry.Item == "unversioned") &&
            !directoryUndoStatus.Entries.Any(entry => entry.Path.StartsWith(directoryName + Path.DirectorySeparatorChar, StringComparison.Ordinal) && entry.Item == "added"),
            "directory-add undo did not leave the preserved tree unversioned");

        var modifiedPath = Path.Combine(workingCopy, "modified @ file.txt");
        await File.AppendAllTextAsync(modifiedPath, "local modification\n").ConfigureAwait(false);
        var modifiedBytes = await File.ReadAllBytesAsync(modifiedPath).ConfigureAwait(false);
        var removeModified = await Svn.RemoveAsync(svn, config, workingCopy, "modified @ file.txt").ConfigureAwait(false);
        True(removeModified.Status == "Ready", $"modified file removal returned {removeModified.Status}: {removeModified.Details}");
        SequenceEqual(modifiedBytes, await File.ReadAllBytesAsync(modifiedPath).ConfigureAwait(false));
        var removedStatus = await Svn.GetStatusAsync(svn, config, workingCopy).ConfigureAwait(false);
        True(removedStatus.Entries.Any(entry => entry.Path == "modified @ file.txt" && entry.Item == "deleted"), "modified removal did not schedule an SVN delete");

        var nestedPath = Path.Combine(workingCopy, "nested @ folder", "retained @ data.txt");
        await File.AppendAllTextAsync(nestedPath, "nested local modification\n").ConfigureAwait(false);
        var nestedBytes = await File.ReadAllBytesAsync(nestedPath).ConfigureAwait(false);
        var removeNested = await Svn.RemoveAsync(svn, config, workingCopy,
            Path.Combine("nested @ folder", "retained @ data.txt")).ConfigureAwait(false);
        True(removeNested.Status == "Ready", $"nested file removal returned {removeNested.Status}: {removeNested.Details}");
        SequenceEqual(nestedBytes, await File.ReadAllBytesAsync(nestedPath).ConfigureAwait(false));

        var missingPath = Path.Combine(workingCopy, "missing @ file.txt");
        File.Delete(missingPath);
        var removeMissing = await Svn.RemoveAsync(svn, config, workingCopy, "missing @ file.txt").ConfigureAwait(false);
        True(removeMissing.Status == "Ready", $"missing file removal returned {removeMissing.Status}: {removeMissing.Details}");
        True(!File.Exists(missingPath), "removing a missing entry recreated local contents");
        var undoMissing = await Svn.UndoAsync(svn, config, workingCopy, "missing @ file.txt").ConfigureAwait(false);
        True(undoMissing.Status == "Ready", $"missing-file undo returned {undoMissing.Status}: {undoMissing.Details}");
        Equal("missing base contents\n", await File.ReadAllTextAsync(missingPath).ConfigureAwait(false));

        var retainedDirectory = Path.Combine(workingCopy, "retained directory");
        var retainedTrackedPath = Path.Combine(retainedDirectory, "nested", "data.txt");
        await File.AppendAllTextAsync(retainedTrackedPath, "local tracked edit\n").ConfigureAwait(false);
        var retainedTrackedBytes = await File.ReadAllBytesAsync(retainedTrackedPath).ConfigureAwait(false);
        var unversionedRetainedPath = Path.Combine(retainedDirectory, "local-only @ bytes.txt");
        const string unversionedRetainedContents = "unversioned directory bytes stay local\n";
        await File.WriteAllTextAsync(unversionedRetainedPath, unversionedRetainedContents).ConfigureAwait(false);

        var outsideSentinel = Path.Combine(root, "external symlink sentinel.txt");
        const string sentinelContents = "outside target remains untouched\n";
        await File.WriteAllTextAsync(outsideSentinel, sentinelContents).ConfigureAwait(false);
        var retainedLinkPath = Path.Combine(retainedDirectory, "external link @ sentinel.txt");
        var retainedLinkCreated = false;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.CreateSymbolicLink(retainedLinkPath, outsideSentinel);
                retainedLinkCreated = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
            {
                // Symlink preservation is exercised where the current filesystem allows it.
            }
        }

        await RunRequiredAsync(svn, "delete", "--keep-local", "--force", "--non-interactive", "--no-auth-cache",
            "--config-dir", config, "--", retainedDirectory + "@").ConfigureAwait(false);
        var undoRetainedDirectory = await Svn.UndoAsync(svn, config, workingCopy, "retained directory").ConfigureAwait(false);
        True(undoRetainedDirectory.Status == "Ready", $"directory-delete undo returned {undoRetainedDirectory.Status}: {undoRetainedDirectory.Details}");
        SequenceEqual(retainedTrackedBytes, await File.ReadAllBytesAsync(retainedTrackedPath).ConfigureAwait(false));
        Equal(unversionedRetainedContents, await File.ReadAllTextAsync(unversionedRetainedPath).ConfigureAwait(false));
        Equal(sentinelContents, await File.ReadAllTextAsync(outsideSentinel).ConfigureAwait(false));
        if (retainedLinkCreated)
        {
            True((File.GetAttributes(retainedLinkPath) & FileAttributes.ReparsePoint) != 0, "nested symlink was replaced during directory undo");
            Equal(Path.GetFullPath(outsideSentinel), Path.GetFullPath(new FileInfo(retainedLinkPath).LinkTarget ?? string.Empty));
        }

        var retainedNestedContents = Path.Combine(workingCopy, "retained directory", "nested", "data.txt");
        Equal("directory contents stay local\nlocal tracked edit\n", await File.ReadAllTextAsync(retainedNestedContents).ConfigureAwait(false));
        True(!Directory.EnumerateDirectories(workingCopy, ".sourcegit-svn-undo-*", SearchOption.AllDirectories).Any(), "successful undo left a recovery directory in the working copy");
        s_passed++;
    }

    private static void WriteServerConfig(string repository, string anonymousAccess)
    {
        var conf = Path.Combine(repository, "conf");
        File.WriteAllText(Path.Combine(conf, "svnserve.conf"),
            $"[general]\nanon-access = {anonymousAccess}\nauth-access = write\npassword-db = passwd\nauthz-db = authz\nrealm = SourceGit regression realm\n");
        File.WriteAllText(Path.Combine(conf, "passwd"),
            "[users]\nalice = integration-correct-password\nblocked = integration-blocked-password\n");
        File.WriteAllText(Path.Combine(conf, "authz"),
            $"[{Path.GetFileName(repository)}:/]\nalice = rw\n* = {(anonymousAccess == "read" ? "r" : string.Empty)}\n");
    }

    private static async Task CreateLocalHistoryAsync(string svn, string repositoryUrl, string directory)
    {
        var seed = Path.Combine(directory, "history seed");
        var initialRelativePath = Path.Combine("nested @ folder", "target @ note.txt");
        var initialPath = Path.Combine(seed, initialRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(initialPath)!);
        await File.WriteAllTextAsync(initialPath, "initial contents\n").ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(seed, "nested @ folder", "empty @ file.txt"), Array.Empty<byte>()).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(seed, "nested @ folder", "binary @ file.bin"), new byte[] { 0, 255, 1, 2 }).ConfigureAwait(false);
        var escapedName = s_escapedSnapshotName;
        var escapedPath = Path.Combine(seed, "nested @ folder", escapedName);
        await File.WriteAllTextAsync(escapedPath, "before escaped path\n").ConfigureAwait(false);

        var trunkUrl = repositoryUrl + "/trunk";
        await RunRequiredAsync(svn, "import", "--non-interactive", "--message",
            "Add target\nSecond line: 保持 Unicode 🌱\nthird line", seed, trunkUrl).ConfigureAwait(false);
        await RunRequiredAsync(svn, "mkdir", "--non-interactive", "--message", "unrelated revision gap",
            trunkUrl + "/unrelated").ConfigureAwait(false);

        var staging = Path.Combine(directory, "history staging");
        await RunRequiredAsync(svn, "checkout", "--non-interactive", trunkUrl, staging).ConfigureAwait(false);
        var stagedPath = Path.Combine(staging, initialRelativePath);
        await File.AppendAllTextAsync(stagedPath, "updated contents\n").ConfigureAwait(false);
        await File.AppendAllTextAsync(Path.Combine(staging, "nested @ folder", escapedName), "after escaped path\n").ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(staging, "nested @ folder", "binary @ file.bin"), new byte[] { 0, 255, 1, 2, 0, 9 }).ConfigureAwait(false);
        await RunRequiredAsync(svn, "propset", "svn:mime-type", "application/octet-stream",
            Path.Combine(staging, "nested @ folder", "binary @ file.bin") + "@").ConfigureAwait(false);
        await RunRequiredAsync(svn, "commit", "--non-interactive", "--message",
            "Update target\nSecond line: 保持 Unicode 🌱", staging).ConfigureAwait(false);

        var renamedPath = Path.Combine(Path.GetDirectoryName(stagedPath)!, "renamed @ note.txt");
        await RunRequiredAsync(svn, "move", "--non-interactive", stagedPath + "@", renamedPath).ConfigureAwait(false);
        await RunRequiredAsync(svn, "commit", "--non-interactive", "--message", "Rename target to @ path", staging).ConfigureAwait(false);
        await RunRequiredAsync(svn, "update", "--non-interactive", staging).ConfigureAwait(false);

        var sourceDirectory = Path.Combine(staging, "nested @ folder");
        var copiedDirectory = Path.Combine(staging, "copied @ scope");
        await RunRequiredAsync(svn, "copy", "--non-interactive", sourceDirectory + "@", copiedDirectory + "@").ConfigureAwait(false);
        await RunRequiredAsync(svn, "commit", "--non-interactive", "--message", "Copy directory to historical scope", staging).ConfigureAwait(false);
    }

    private static Process StartSvnServe(string executable, string root, int port)
    {
        var start = NewToolStartInfo(executable);
        foreach (var argument in new[] { "--daemon", "--foreground", "--listen-host", "127.0.0.1", "--listen-port", port.ToString(), "--root", root })
            start.ArgumentList.Add(argument);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANG"] = "C";
        return Process.Start(start) ?? throw new TestFailureException("svnserve did not start.");
    }

    private static async Task WaitForServerAsync(Process server, int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!timeout.IsCancellationRequested)
        {
            if (server.HasExited)
                throw new TestFailureException($"svnserve exited early with code {server.ExitCode}.");
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            }
        }

        throw new TestFailureException("svnserve did not begin listening within 8 seconds.");
    }

    private static async Task RunRequiredAsync(string executable, params string[] args)
    {
        var start = NewToolStartInfo(executable);
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new TestFailureException($"Could not start {executable}.");
        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new TestFailureException($"{Path.GetFileName(executable)} exited {process.ExitCode}: {stdout}\n{stderr}");
    }

    private static async Task RunRequiredWithPasswordAsync(string executable, string password, params string[] args)
    {
        var start = NewToolStartInfo(executable);
        start.RedirectStandardInput = true;
        start.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new TestFailureException($"Could not start {executable}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(password).ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
        process.StandardInput.Close();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new TestFailureException($"{Path.GetFileName(executable)} exited {process.ExitCode}: {stdout}\n{stderr.Replace(password, "[redacted]", StringComparison.Ordinal)}");
    }

    private static async Task<(string StandardOutput, string StandardError)> RunCaptureAsync(string executable, params string[] args)
    {
        var start = NewToolStartInfo(executable);
        foreach (var argument in args)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new TestFailureException($"Could not start {executable}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new TestFailureException($"{Path.GetFileName(executable)} exited {process.ExitCode}: {stdout}\n{stderr}");
        return (stdout, stderr);
    }

    private static string NormalizeXmlAttributes(string xml)
    {
        var document = XDocument.Parse(xml);
        return string.Join("\n", document.Root!.DescendantsAndSelf().Select(element =>
            $"{element.Name}|{string.Join(",", element.Attributes()
                .OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal)
                .Select(attribute => $"{attribute.Name}={attribute.Value}"))}|{element.Value}"));
    }

    private static ProcessStartInfo NewToolStartInfo(string executable) => new()
    {
        FileName = executable,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(entry.Trim().Trim('"'), OperatingSystem.IsWindows() ? name + ".exe" : name);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return string.Empty;
    }

    private static void RequireTool(string executable, string name)
    {
        if (string.IsNullOrEmpty(executable))
            throw new TestFailureException($"Integration mode requires '{name}' on PATH.");
    }

    private static string GetFixtureExecutable()
    {
        var processPath = Environment.ProcessPath ?? throw new TestFailureException("Cannot locate the current test process.");
        var executableName = Path.GetFileNameWithoutExtension(processPath);
        if (executableName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new TestFailureException("The regression harness must run through its apphost; use `dotnet run --project tests/Svn/SourceGit.Svn.Regression.csproj`.");
        if (!Path.IsPathFullyQualified(processPath) || !File.Exists(processPath))
            throw new TestFailureException("The regression apphost path is invalid.");
        return processPath;
    }

    private static string PrepareScenario(string directory, string scenario)
    {
        var invocationPath = Path.Combine(directory, $"calls-{Guid.NewGuid():N}.jsonl");
        Environment.SetEnvironmentVariable(ScenarioVariable, scenario);
        Environment.SetEnvironmentVariable(InvocationVariable, invocationPath);
        Environment.SetEnvironmentVariable(WorkingCopyRootVariable, null);
        return invocationPath;
    }

    private static string PrepareHistoryScenario(string directory, string scenario, string workingCopyPath)
    {
        var calls = PrepareScenario(directory, scenario);
        var workingCopyRoot = Path.GetDirectoryName(Path.GetDirectoryName(workingCopyPath));
        if (scenario == "history_trailing_wcroot")
            workingCopyRoot += " ";
        Environment.SetEnvironmentVariable(WorkingCopyRootVariable, workingCopyRoot);
        return calls;
    }

    private static string PrepareWorkingCopyActionScenario(
        string directory,
        string scenario,
        string workingCopyPath,
        string relativePath,
        string item,
        string properties,
        string kind)
    {
        var calls = PrepareHistoryScenario(directory, scenario, workingCopyPath);
        var root = Environment.GetEnvironmentVariable(WorkingCopyRootVariable) ?? string.Empty;
        Environment.SetEnvironmentVariable(ActionPathVariable, Path.GetFullPath(relativePath, root));
        Environment.SetEnvironmentVariable(ActionItemVariable, item);
        Environment.SetEnvironmentVariable(ActionPropertiesVariable, properties);
        Environment.SetEnvironmentVariable(ActionKindVariable, kind);
        return calls;
    }

    private static string CreateFakeWorkingCopy(string directory)
    {
        var root = Path.Combine(directory, "working copy @ fixture");
        var nested = Path.Combine(root, "nested @ folder");
        Directory.CreateDirectory(Path.Combine(root, ".svn"));
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(root, ".svn", "wc.db"), "fixture");
        var path = Path.Combine(nested, "target @ file.txt");
        File.WriteAllText(path, "local working copy contents\n");
        return path;
    }

    private static string CreateWorkingCopyStatusFiles(string workingCopyPath, string tempDirectory)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(workingCopyPath))!;
        File.WriteAllText(Path.Combine(root, "modified @ file.txt"), "local text\n");
        File.WriteAllText(Path.Combine(root, "copied @ file.txt"), "copied text\n");
        File.WriteAllText(Path.Combine(root, "unversioned @ file.txt"), "new local text\n");
        File.WriteAllText(Path.Combine(root, "conflicted @ file.txt"), "conflict markers\n");
        File.WriteAllBytes(Path.Combine(root, "binary @ file.dat"), new byte[] { 0, 1, 2 });
        File.WriteAllText(Path.Combine(root, "binary-lookalike @ file.txt"), "lookalike source text\n");
        File.WriteAllText(Path.Combine(root, "switched.txt"), "switched local text\n");
        File.WriteAllText(Path.Combine(root, "changelist @ file.txt"), "changelist text\n");
        Directory.CreateDirectory(Path.Combine(root, "directory change"));
        Directory.CreateDirectory(Path.Combine(root, "unversioned directory"));

        var externalRoot = Path.Combine(tempDirectory, "outside wc data");
        Directory.CreateDirectory(externalRoot);
        File.WriteAllText(Path.Combine(externalRoot, "outside.txt"), "must not be read\n");
        var directoryLink = Path.Combine(root, "escape");
        var fileLink = Path.Combine(root, "special @ link");
        try
        {
            Directory.CreateSymbolicLink(directoryLink, externalRoot);
            File.CreateSymbolicLink(fileLink, Path.Combine(externalRoot, "outside.txt"));
            s_workingCopyLinksAvailable = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            s_workingCopyLinksAvailable = false;
            if (Directory.Exists(directoryLink))
                Directory.Delete(directoryLink);
            if (File.Exists(fileLink))
                File.Delete(fileLink);
        }
        return root;
    }

    private static string ArgumentAfter(Invocation invocation, string argument)
    {
        var index = Array.IndexOf(invocation.Arguments, argument);
        True(index >= 0 && index + 1 < invocation.Arguments.Length, $"missing value after {argument}");
        return invocation.Arguments[index + 1];
    }

    private static Invocation SingleInvocation(string path)
    {
        var calls = ReadInvocations(path);
        Equal(1, calls.Count);
        return calls[0];
    }

    private static List<Invocation> ReadInvocations(string path)
    {
        if (!File.Exists(path))
            return new List<Invocation>();
        return File.ReadAllLines(path)
            .Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<Invocation>(line) ?? throw new TestFailureException("Malformed fake-client invocation record."))
            .ToList();
    }

    private static void AssertCredentialArguments(Invocation invocation, string username, string password)
    {
        var args = invocation.Arguments;
        var usernameIndex = Array.IndexOf(args, "--username");
        True(usernameIndex >= 0 && usernameIndex + 1 < args.Length, "explicit username was not passed");
        Equal(username, args[usernameIndex + 1]);
        True(args.Contains("--password-from-stdin"), "password-from-stdin flag is missing");
        True(!args.Contains(password), "password appeared in argv");
        True(invocation.StdinReceived, "password was not received via stdin");
        Equal(Hash(password), invocation.StdinSha256);
    }

    private static void AssertConfigDirectory(Invocation invocation, string expectedDirectory)
    {
        var index = Array.IndexOf(invocation.Arguments, "--config-dir");
        True(index >= 0 && index + 1 < invocation.Arguments.Length, "--config-dir argument is missing");
        Equal(Path.GetFullPath(expectedDirectory), invocation.Arguments[index + 1]);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string[] SanitizeArguments(string[] args)
    {
        var sanitized = (string[])args.Clone();
        for (var i = 0; i < sanitized.Length; i++)
        {
            if (sanitized[i].Length > 1024)
            {
                sanitized[i] = "[redacted-large-argument]";
                continue;
            }

            foreach (var password in s_knownFixturePasswords)
            {
                if (password.Length == 1)
                {
                    if (sanitized[i] == password)
                        sanitized[i] = "[redacted]";
                    else if (sanitized[i].StartsWith("--password", StringComparison.Ordinal) &&
                        sanitized[i].EndsWith("=" + password, StringComparison.Ordinal))
                        sanitized[i] = sanitized[i][..^password.Length] + "[redacted]";
                }
                else
                {
                    sanitized[i] = sanitized[i].Replace(password, "[redacted]", StringComparison.Ordinal);
                }
            }
            if (sanitized[i] == "--password" && i + 1 < sanitized.Length)
                sanitized[++i] = "[redacted]";
        }

        return sanitized;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task CheckAsync(string name, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            s_passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            throw new TestFailureException($"FAIL {name}: {ex.Message}", ex);
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new TestFailureException($"Expected '{expected}', got '{actual}'.");
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new TestFailureException($"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    private static void True(bool value, string message)
    {
        if (!value)
            throw new TestFailureException(message);
    }

    private sealed record Invocation(string[] Arguments, string StdinSha256, bool StdinReceived, bool PasswordArgumentPresent, int ProcessId);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; }

        public TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class TestFailureException : Exception
    {
        public TestFailureException(string message) : base(message) { }
        public TestFailureException(string message, Exception innerException) : base(message, innerException) { }
    }
}
