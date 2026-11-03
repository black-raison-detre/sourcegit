using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private static int s_passed;

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
        return args.Length > 0 && (args[0] == "info" || args[0] == "log" || args.Contains("--version", StringComparer.Ordinal));
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

        if (args[0] == "log")
        {
            switch (scenario)
            {
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
        var url = scenario == "history_ssh"
            ? "svn+ssh://svn.example.test/repository/path"
            : "svn://fixture/repos/path%20with%20space%20%40";
        Console.WriteLine($"<info><entry kind=\"file\" path=\"fixture\" revision=\"12\"><url>{url}</url><repository><root>svn://fixture</root></repository><wc-info><wcroot-abspath>{System.Security.SecurityElement.Escape(workingCopyRoot)}</wcroot-abspath></wc-info></entry></info>");
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

        var trunkUrl = repositoryUrl + "/trunk";
        await RunRequiredAsync(svn, "import", "--non-interactive", "--message",
            "Add target\nSecond line: 保持 Unicode 🌱\nthird line", seed, trunkUrl).ConfigureAwait(false);
        await RunRequiredAsync(svn, "mkdir", "--non-interactive", "--message", "unrelated revision gap",
            trunkUrl + "/unrelated").ConfigureAwait(false);

        var staging = Path.Combine(directory, "history staging");
        await RunRequiredAsync(svn, "checkout", "--non-interactive", trunkUrl, staging).ConfigureAwait(false);
        var stagedPath = Path.Combine(staging, initialRelativePath);
        await File.AppendAllTextAsync(stagedPath, "updated contents\n").ConfigureAwait(false);
        await RunRequiredAsync(svn, "commit", "--non-interactive", "--message",
            "Update target\nSecond line: 保持 Unicode 🌱", staging).ConfigureAwait(false);

        var renamedPath = Path.Combine(Path.GetDirectoryName(stagedPath)!, "renamed @ note.txt");
        await RunRequiredAsync(svn, "move", "--non-interactive", stagedPath + "@", renamedPath).ConfigureAwait(false);
        await RunRequiredAsync(svn, "commit", "--non-interactive", "--message", "Rename target to @ path", staging).ConfigureAwait(false);
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
