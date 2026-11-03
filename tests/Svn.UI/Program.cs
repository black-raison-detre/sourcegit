using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SourceGit;
using SourceGit.ViewModels;

internal static class Program
{
    private static int s_checks;

    public static AppBuilder BuildAvaloniaApp() => App.BuildAvaloniaApp()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "info" or "log")
            return await RunFakeSvn(args);

        var temporary = Path.Combine(Path.GetTempPath(), "sourcegit-svn-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        SourceGit.Native.OS.BasicDirectories.ConfigDir = temporary;
        SourceGit.Native.OS.BasicDirectories.CacheDir = temporary;
        try
        {
            var repository = Path.Combine(temporary, "repository");
            var workingCopy = Path.Combine(temporary, "工作 copy@fixture ");
            var config = Path.Combine(temporary, "svn-config");
            Directory.CreateDirectory(config);
            await Run("svnadmin", "create", repository);
            var url = new Uri(repository + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
            await Run("svn", "checkout", "--config-dir", config, url, workingCopy);
            var file = Path.Combine(workingCopy, "notes.txt");
            File.WriteAllText(file, "initial");
            Directory.CreateDirectory(Path.Combine(workingCopy, "nested"));
            await Run("svn", "add", "--", file + "@", Path.Combine(workingCopy, "nested") + "@");
            for (var revision = 1; revision <= 104; revision++)
            {
                File.WriteAllText(file, "Revision " + revision);
                var message = revision == 104 ? string.Empty : $"Revision {revision}: 中文 & <markup>\n\nFull message for revision {revision}.\nFinal line.";
                await Run("svn", "commit", "--config-dir", config, "-m", message, "--", workingCopy + "@");
                if (revision % 25 == 0)
                    Console.WriteLine($"Created {revision}/104 fixture revisions.");
            }

            Console.WriteLine("Fixture ready; starting headless application.");
            using var session = HeadlessUnitTestSession.StartNew(typeof(Program));
            using var uiTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await session.Dispatch<bool>(async () =>
            {
                Console.WriteLine("Headless application initialized; loading history.");
                var preferences = Preferences.Instance;
                preferences.SvnInstallPath = SourceGit.Native.Svn.ResolveExecutable(string.Empty);
                preferences.SvnConfigDirectory = config;
                preferences.SetCanModify();

                using var vm = new SvnRepository(workingCopy);
                await vm.RefreshAsync();
                Console.WriteLine("Initial history loaded.");
                Check(!vm.HasError, "initial history: " + vm.ErrorDetails);
                Check(vm.Revisions.Count == 100 && vm.HasMore, "initial page");
                Check(vm.SelectedRevision.Number == 104 && vm.SelectedMessage.Length == 0, "empty message preserved");
                vm.SelectedRevision = vm.Revisions[1];
                var selection = vm.SelectedRevision;
                Check(vm.SelectedMessage.Contains("\n\nFull message") && vm.SelectedMessage.Contains("中文 & <markup>"), "full multiline message preserved");

                var window = new Window
                {
                    Width = 1024,
                    Height = 600,
                    Content = new SourceGit.Views.LauncherPage { DataContext = new LauncherPage(new RepositoryNode { Id = workingCopy, Name = "SVN fixture", IsRepository = true, IsSvn = true }, vm) },
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
                Check(ReferenceEquals(grid.ItemsSource, vm.Revisions) && ReferenceEquals(grid.SelectedItem, selection), "actual view bindings");
                grid.Focus();
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                Check(vm.SelectedRevision.Number == 102, "keyboard revision navigation");
                vm.SelectedRevision = selection;

                var messageBox = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.IsVisible && box.IsReadOnly && box.Text == vm.SelectedMessage);
                messageBox.Focus();
                messageBox.SelectAll();
                Check(messageBox.SelectedText == vm.SelectedMessage, "select complete message for copying");
                messageBox.SelectionStart = 0;
                messageBox.SelectionEnd = 0;
                grid.Focus();

                await vm.LoadMoreAsync();
                Check(vm.Revisions.Count == 104 && !vm.HasMore, "pagination exhaustion");
                Check(ReferenceEquals(vm.SelectedRevision, selection), "paging preserves selection");
                Check(vm.Revisions.Select(revision => revision.Number).Distinct().Count() == 104, "no duplicate revisions");
                Check(vm.Revisions[^1].Number == 1, "oldest revision present");

                var screenshotDirectory = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), "sourcegit-svn-ui-artifacts");
                Directory.CreateDirectory(screenshotDirectory);
                foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    Application.Current.RequestedThemeVariant = theme;
                    Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame();
                    Check(frame != null, "render " + theme);
                    frame.Save(Path.Combine(screenshotDirectory, "history-" + theme + ".png"));
                }
                window.Width = 1440;
                window.Height = 900;
                Dispatcher.UIThread.RunJobs();
                using (var frame = window.CaptureRenderedFrame())
                    frame.Save(Path.Combine(screenshotDirectory, "history-wide.png"));

                await vm.RefreshAsync();
                Check(vm.Revisions.Count == 100 && vm.SelectedRevision.Number == selection.Number, "refresh replaces page and preserves selected revision");

                var launcher = new Launcher(null);
                typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Application.Current, launcher);
                var popup = new OpenLocalRepository(launcher.ActivePage.Node.Id, null)
                {
                    RepoPath = Path.Combine(workingCopy, "nested"),
                };
                Check(await popup.Sure(), "folder opener accepts nested SVN path");
                Check(launcher.ActivePage.Data is SvnRepository, "SVN tab dispatch");
                var opened = (SvnRepository)launcher.ActivePage.Data;
                await WaitUntil(() => !opened.IsLoading);
                Check(opened.FullPath == workingCopy.Replace('\\', '/'), "canonical working-copy root");
                Check(launcher.ActiveWorkspace.Repositories.Contains(opened.FullPath), "saved workspace path");
                var node = launcher.ActivePage.Node;
                Check(node.IsSvn, "repository type preserved");
                preferences.Save();
                using (var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(temporary, "preference.json"))))
                    Check(saved.RootElement.GetProperty("RepositoryNodes")[0].GetProperty("IsSvn").GetBoolean(), "SVN repository type serialized");
                Check(!launcher.TryOpenRepositoryFromPath(Path.Combine(temporary, "absent")), "missing folder ignored");
                Check(launcher.TryOpenRepositoryFromPath(Path.Combine(workingCopy, "nested")), "startup path accepts nested SVN path");
                Check(launcher.Pages.Count == 1, "opening same root deduplicates tabs");

                // Reconstruct the launcher using its actual saved-workspace path dispatch.
                launcher.CloseAll();
                Check(!opened.CanRefresh, "closing cancels and disposes SVN tab");
                var restored = new Launcher(null);
                typeof(App).GetField("_launcher", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(Application.Current, restored);
                Check(restored.ActivePage.Data is SvnRepository, "saved tab restoration");
                restored.CloseTab(restored.ActivePage);
                Check(restored.ActivePage.Data is Welcome && restored.ActiveWorkspace.Repositories.Count == 0, "last repository tab returns to welcome");

                preferences.SvnInstallPath = Path.Combine(temporary, "missing-svn");
                await vm.RefreshAsync();
                Check(vm.HasError && vm.Revisions.Count == 100 && vm.SelectedRevision.Number == selection.Number, "failed refresh preserves loaded history");
                Check(!vm.ErrorMessage.StartsWith("Text.", StringComparison.Ordinal), "localized error text");
                window.Width = 1024;
                window.Height = 600;
                Dispatcher.UIThread.RunJobs();
                using (var frame = window.CaptureRenderedFrame())
                    frame.Save(Path.Combine(screenshotDirectory, "history-error.png"));
                preferences.SvnInstallPath = SourceGit.Native.Svn.ResolveExecutable(string.Empty);
                await vm.RetryAsync();
                Check(!vm.HasError, "retry recovers");

                // The real local SVN fixture covers data and paths; the apphost fixture makes
                // authentication and external switches deterministic for the actual view model.
                var realExecutable = preferences.SvnInstallPath;
                preferences.SvnInstallPath = Environment.ProcessPath;
                var fakeCalls = Path.Combine(temporary, "fake-log-calls");
                Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_ROOT", workingCopy);
                Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_URL", "svn://fixture.test/trunk");
                Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_CALLS", fakeCalls);
                using (var authenticated = new SvnRepository(workingCopy))
                {
                    window.Content = new SourceGit.Views.LauncherPage { DataContext = new LauncherPage(node, authenticated) };
                    await authenticated.RefreshAsync();
                    Check(authenticated.NeedsCredentials && authenticated.HasError, "VM requests credentials");
                    Dispatcher.UIThread.RunJobs();
                    using (var frame = window.CaptureRenderedFrame())
                        frame.Save(Path.Combine(screenshotDirectory, "history-authentication.png"));
                    authenticated.Username = "fixture";
                    authenticated.Password = "incorrect-secret";
                    await authenticated.SignInAsync();
                    Check(authenticated.NeedsCredentials && !authenticated.ErrorDetails.Contains("incorrect-secret"), "wrong sign-in redacted and retryable");
                    Check(authenticated.Password.Length == 0, "password field cleared after attempt");
                    authenticated.Password = "ui-fixture-password";
                    await authenticated.SignInAsync();
                    Check(!authenticated.HasError && !authenticated.NeedsCredentials && authenticated.Revisions.Count == 100, "VM sign-in loads history");
                    await authenticated.LoadMoreAsync();
                    Check(!authenticated.HasError && authenticated.Revisions.Count == 101, "VM retains session credentials for paging");
                    var callsBeforeSwitch = File.ReadAllLines(fakeCalls).Length;
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_URL", "svn://other.test/trunk");
                    await authenticated.RefreshAsync();
                    Check(authenticated.NeedsCredentials && authenticated.Revisions.Count == 0, "switch clears credentials and old history");
                    Check(File.ReadAllLines(fakeCalls).Length == callsBeforeSwitch + 1, "new URL attempted once without session credentials");
                    Check(File.ReadAllLines(fakeCalls)[^1] == "anonymous", "switched URL receives no explicit credentials");
                }
                Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_ROOT", null);
                Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_URL", null);
                Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_CALLS", null);
                preferences.SvnInstallPath = realExecutable;
                Console.WriteLine("Authentication and credential-switch checks completed.");

                // Saved SVN metadata must not override current Git ownership of the path.
                var converted = Path.Combine(temporary, "converted-working-copy");
                await Run("svn", "checkout", "--config-dir", config, url, converted);
                var convertedNode = preferences.FindOrAddNodeByRepositoryPath(converted, null, false, false);
                convertedNode.IsSvn = true;
                await Run("git", "-C", converted, "init");
                await Run("git", "-C", converted, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "commit", "--allow-empty", "-m", "Git fixture");
                restored.OpenRepositoryInTab(convertedNode, null);
                try
                {
                    Check(restored.ActivePage.Data is Repository && !convertedNode.IsSvn, "saved SVN node becomes Git when a Git marker takes precedence");
                }
                finally
                {
                    if (restored.ActivePage.Data is Repository or SvnRepository)
                        restored.CloseTab(restored.ActivePage);
                }
                Directory.Delete(Path.Combine(converted, ".git"), true);
                restored.OpenRepositoryInTab(convertedNode, null);
                Check(restored.ActivePage.Data is SvnRepository && convertedNode.IsSvn, "saved node returns to SVN after Git marker removal");
                restored.CloseTab(restored.ActivePage);
                window.Close();
                Console.WriteLine("Screenshots: " + screenshotDirectory);
                return true;
            }, uiTimeout.Token);

            Check(s_checks >= 25, "headless workflow executed all checks");
            Console.WriteLine($"{s_checks} SVN UI and working-copy workflow checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
        s_checks++;
    }

    private static async Task<int> RunFakeSvn(string[] args)
    {
        var root = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_ROOT");
        var url = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_URL");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(url))
            return 2;
        if (args[0] == "info")
        {
            Console.WriteLine($"<info><entry revision=\"104\"><url>{SecurityElement.Escape(url)}</url><repository><root>{SecurityElement.Escape(url)}</root></repository><wc-info><wcroot-abspath>{SecurityElement.Escape(root)}</wcroot-abspath></wc-info></entry></info>");
            return 0;
        }

        File.AppendAllText(Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_CALLS"), args.Contains("--username") ? "authenticated\n" : "anonymous\n");
        if (!args.Contains("--no-auth-cache"))
            return 3;
        if (!args.Contains("--username"))
        {
            Console.Error.WriteLine("svn: E170001: Authentication required");
            return 1;
        }
        var password = await Console.In.ReadLineAsync();
        if (password != "ui-fixture-password" || url != "svn://fixture.test/trunk")
        {
            Console.Error.WriteLine($"svn: E170001: Authentication failed {password}");
            return 1;
        }
        var range = args[Array.IndexOf(args, "--revision") + 1];
        Console.WriteLine("<log>");
        foreach (var revision in range == "HEAD:0" ? Enumerable.Range(5, 100).Reverse() : new[] { 1 })
            Console.WriteLine($"<logentry revision=\"{revision}\"><author>fixture</author><msg>Revision {revision}</msg></logentry>");
        Console.WriteLine("</log>");
        return 0;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static async Task Run(string executable, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(executable + ": " + error.Result);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(true);
        }
    }
}
