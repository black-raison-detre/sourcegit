using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SourceGit;
using SourceGit.ViewModels;
using SvnOperationDialog = SourceGit.Views.SvnOperationDialog;

internal static partial class Program
{
    private static int s_checks;

    public static AppBuilder BuildAvaloniaApp() => App.BuildAvaloniaApp()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "info" or "log" or "proplist" or "cat" or "status" or "diff" or "propget")
            return await RunFakeSvn(args);

        var temporary = Path.Combine(ResolvePhysicalDirectory(Path.GetTempPath()),
            "sourcegit-svn-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        SourceGit.Native.OS.BasicDirectories.ConfigDir = temporary;
        SourceGit.Native.OS.BasicDirectories.CacheDir = temporary;
        try
        {
            var repository = Path.Combine(temporary, "repository");
            var workingCopy = Path.Combine(temporary, "工作 copy@fixture ");
            var config = Path.Combine(temporary, "svn-config");
            Directory.CreateDirectory(config);
            if (args.Contains("--conflicts-only", StringComparer.Ordinal))
                return await RunConflictsOnly(temporary, config);
            if (args.Contains("--path-operations-only", StringComparer.Ordinal))
                return await RunPathOperationsOnly(temporary, config);
            if (args.Contains("--properties-only", StringComparer.Ordinal))
                return await RunPropertiesOnly(temporary, config);
            if (args.Contains("--changelists-only", StringComparer.Ordinal))
                return await RunChangelistsOnly(temporary, config);
            if (args.Contains("--repair-only", StringComparer.Ordinal))
                return await RunRepairOnly(temporary, config);
            var localWorkingCopy = await CreateLocalWorkingCopyFixture(temporary, config);
            if (args.Contains("--local-only", StringComparer.Ordinal))
                return await RunLocalOnly(temporary, config, localWorkingCopy);

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
            var session = HeadlessUnitTestSession.StartNew(typeof(Program));
            using var uiTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            try
            {
                await session.Dispatch<bool>(async () =>
                {
                    Console.WriteLine("Headless application initialized; loading history.");
                    var preferences = Preferences.Instance;
                    preferences.SvnInstallPath = SourceGit.Native.Svn.ResolveExecutable(string.Empty);
                    preferences.GitInstallPath = SourceGit.Native.OS.FindGitExecutable();
                    preferences.SvnConfigDirectory = config;
                    preferences.SetCanModify();
                    await VerifySnapshotDiffs(temporary);
                    await VerifyOrdinaryGitDiff(temporary);

                    using var vm = new SvnRepository(workingCopy);
                    await vm.RefreshAsync();
                    Console.WriteLine("Initial history loaded.");
                    await WaitForPaths(vm);
                    Check(!vm.HasError, "initial history: " + vm.ErrorDetails);
                    Check(vm.Revisions.Count == 100 && vm.HasMore, "initial page");
                    Check(vm.SelectedRevision.Number == 104 && vm.SelectedMessage.Length == 0, "empty message preserved");
                    Check(!vm.HasPathsError && vm.ChangedPaths.Count == 1, "initial revision paths loaded");
                    var initialPath = vm.ChangedPaths[0];
                    Check(initialPath.Path == "/notes.txt" && initialPath.RelativePath.EndsWith("notes.txt", StringComparison.Ordinal), "working-copy changed path");
                    Check(initialPath.Action == "M" && initialPath.Kind == "file", "changed path action and kind");
                    Check(string.IsNullOrEmpty(initialPath.CopyFromPath) && initialPath.CopyFromRevision == null, "ordinary path has no copy source");
                    vm.SelectedRevision = vm.Revisions[1];
                    var selection = vm.SelectedRevision;
                    await WaitForPaths(vm);
                    Check(vm.SelectedMessage.Contains("\n\nFull message") && vm.SelectedMessage.Contains("中文 & <markup>"), "full multiline message preserved");

                    var window = new Window
                    {
                        Width = 1024,
                        Height = 600,
                        Content = new SourceGit.Views.LauncherPage { DataContext = new LauncherPage(new RepositoryNode { Id = workingCopy, Name = "SVN fixture", IsRepository = true, IsSvn = true }, vm) },
                    };
                    var screenshotDirectory = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_ARTIFACTS") ?? Path.Combine(Path.GetTempPath(), "sourcegit-svn-ui-artifacts");
                    Directory.CreateDirectory(screenshotDirectory);
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    var grids = window.GetVisualDescendants().OfType<DataGrid>().ToArray();
                    var grid = grids.Single(candidate => ReferenceEquals(candidate.ItemsSource, vm.Revisions));
                    var pathsGrid = grids.Single(candidate => ReferenceEquals(candidate.ItemsSource, vm.ChangedPaths));
                    var contentTabs = window.GetVisualDescendants().OfType<TabControl>()
                        .Single(control => control.Name == "HistoryDetailsTabs");
                    var repositoryTabs = window.GetVisualDescendants().OfType<TabControl>()
                        .Single(control => control.Name == "RepositoryTabs");
                    Check(repositoryTabs.SelectedIndex == 0 && vm.SelectedTabIndex == 0, "repository opens on the history tab");
                    Check(ReferenceEquals(grid.SelectedItem, selection), "actual history view bindings");
                    Check(ReferenceEquals(pathsGrid.ItemsSource, vm.ChangedPaths) && pathsGrid.GetVisualDescendants().OfType<DataGridRow>().Any(), "actual changed paths view bindings and rows");
                    grid.Focus();
                    window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                    window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                    Dispatcher.UIThread.RunJobs();
                    Check(vm.SelectedRevision.Number == 102, "keyboard revision navigation");
                    await WaitForPaths(vm);
                    Check(vm.SelectedChangedPath == null, "revision change clears the selected file");
                    vm.SelectedRevision = selection;
                    await WaitForPaths(vm);

                    Check(vm.SelectedChangedPath == null, "loading changed paths does not auto-select a file");
                    var selectedPath = vm.ChangedPaths.Single(path => path.Path == "/notes.txt");
                    pathsGrid.SelectedItem = selectedPath;
                    Dispatcher.UIThread.RunJobs();
                    await WaitForDiff(vm);
                    Check(ReferenceEquals(vm.SelectedChangedPath, selectedPath), "actual changed-file selection binding");
                    Check(!vm.IsLoadingDiff && !vm.HasDiffError && vm.IsTextDiff, "selected historical file loads a text diff");
                    Check(vm.DiffBeforeText.Contains("102", StringComparison.Ordinal) && vm.DiffAfterText.Contains("103", StringComparison.Ordinal), "historical diff endpoint labels");
                    Check(vm.DiffTitle.Contains("notes.txt", StringComparison.Ordinal), "historical diff title");
                    Check(vm.DiffContent is TextDiffContext textDiff &&
                        textDiff.Data.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Deleted && line.Content.Contains("Revision 102", StringComparison.Ordinal)) &&
                        textDiff.Data.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Added && line.Content.Contains("Revision 103", StringComparison.Ordinal)),
                        "historical diff has before and after lines");
                    Dispatcher.UIThread.RunJobs();
                    using (var initialDiffFrame = window.CaptureRenderedFrame())
                        Check(initialDiffFrame != null, "initial historical diff frame rendered");
                    Check(window.GetVisualDescendants().Any(control => control.IsVisible && control.GetType().Name == "TextDiffView"), "historical diff uses the shared text-diff view");

                    var historicalDiff = (TextDiffContext)vm.DiffContent;
                    historicalDiff.SelectedChunk = new TextDiffSelectedChunk(0, 24, 0, 2, true, false);
                    Dispatcher.UIThread.RunJobs();
                    var textDiffView = window.GetVisualDescendants().OfType<SourceGit.Views.TextDiffView>().Single();
                    var hunkActions = textDiffView.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "Popup");
                    Check(!historicalDiff.Option.IsLocalChange && !hunkActions.IsVisible,
                        "historical diff does not expose local stage, unstage, or discard actions");
                    historicalDiff.SelectedChunk = null;
                    Dispatcher.UIThread.RunJobs();

                    var modeToggle = window.GetVisualDescendants().OfType<ToggleButton>()
                        .Single(toggle => ReferenceEquals(toggle.DataContext, vm));
                    foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                    {
                        Application.Current.RequestedThemeVariant = theme;
                        modeToggle.IsChecked = false;
                        Dispatcher.UIThread.RunJobs();
                        using (var frame = window.CaptureRenderedFrame())
                        {
                            Check(frame != null, "render combined historical diff " + theme);
                            frame.Save(Path.Combine(screenshotDirectory, "revision-diff-combined-" + theme + ".png"));
                        }

                        modeToggle.IsChecked = true;
                        Dispatcher.UIThread.RunJobs();
                        Check(vm.DiffModeSideBySide && vm.DiffContent is TwoSideTextDiff, "switch historical diff to side-by-side");
                        using (var frame = window.CaptureRenderedFrame())
                        {
                            Check(frame != null, "render side-by-side historical diff " + theme);
                            frame.Save(Path.Combine(screenshotDirectory, "revision-diff-side-by-side-" + theme + ".png"));
                        }
                        modeToggle.IsChecked = false;
                        Dispatcher.UIThread.RunJobs();
                    }
                    window.Width = 1440;
                    window.Height = 900;
                    Dispatcher.UIThread.RunJobs();
                    using (var frame = window.CaptureRenderedFrame())
                        frame.Save(Path.Combine(screenshotDirectory, "revision-diff-wide.png"));
                    window.Width = 1024;
                    window.Height = 600;
                    Dispatcher.UIThread.RunJobs();

                    var loadedDiff = vm.DiffContent;
                    contentTabs.SelectedIndex = 1;
                    Dispatcher.UIThread.RunJobs();
                    var messageBox = window.GetVisualDescendants().OfType<TextBox>()
                        .Single(box => box.IsVisible && box.IsReadOnly && box.Text == vm.SelectedMessage);
                    messageBox.Focus();
                    messageBox.SelectAll();
                    Check(messageBox.SelectedText == vm.SelectedMessage, "select complete message for copying from Message tab");
                    Check(ReferenceEquals(vm.SelectedChangedPath, selectedPath) && ReferenceEquals(vm.DiffContent, loadedDiff), "Message tab preserves file selection and diff");
                    messageBox.SelectionStart = 0;
                    messageBox.SelectionEnd = 0;
                    contentTabs.SelectedIndex = 0;
                    grid.Focus();
                    Dispatcher.UIThread.RunJobs();
                    Check(ReferenceEquals(pathsGrid.SelectedItem, selectedPath), "returning to Changes preserves selected file");
                    Check(File.ReadAllText(file) == "Revision 104", "historical diff leaves the working copy unchanged");

                    await vm.LoadMoreAsync();
                    await WaitForPaths(vm);
                    Check(vm.Revisions.Count == 104 && !vm.HasMore, "pagination exhaustion");
                    Check(ReferenceEquals(vm.SelectedRevision, selection), "paging preserves selection");
                    Check(vm.Revisions.Select(revision => revision.Number).Distinct().Count() == 104, "no duplicate revisions");
                    Check(vm.Revisions[^1].Number == 1, "oldest revision present");

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
                    await WaitForPaths(vm);
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
                    await WaitForPaths(vm);
                    Check(!vm.HasError, "retry recovers");

                    // The real local SVN fixture covers data and paths; the apphost fixture makes
                    // authentication and external switches deterministic for the actual view model.
                    var realExecutable = preferences.SvnInstallPath;
                    preferences.SvnInstallPath = Environment.ProcessPath;
                    var fakeCalls = Path.Combine(temporary, "fake-log-calls");
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_ROOT", workingCopy);
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_URL", "svn://fixture.test/trunk");
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_CALLS", fakeCalls);
                    await VerifyMetadataCaching(workingCopy, fakeCalls);
                    using (var authenticated = new SvnRepository(workingCopy))
                    {
                        window.Content = new SourceGit.Views.LauncherPage { DataContext = new LauncherPage(node, authenticated) };
                        await authenticated.RefreshAsync();
                        Check(authenticated.NeedsCredentials && authenticated.HasError, "VM requests credentials");
                        await WaitForWorkingCopy(authenticated.WorkingCopy);
                        File.WriteAllText(Path.Combine(workingCopy, "offline-a.txt"), "offline baseline A\n");
                        File.WriteAllText(Path.Combine(workingCopy, "offline-b.txt"), "offline baseline B\n");
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_STATUS_ENTRIES", "1");
                        var authenticatedRepositoryTabs = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "RepositoryTabs");
                        authenticatedRepositoryTabs.SelectedIndex = 1;
                        Dispatcher.UIThread.RunJobs();
                        Check(authenticated.SelectedTabIndex == 1, "authenticated fixture selects its local working-copy tab");
                        await authenticated.WorkingCopy.RefreshAsync();
                        await WaitForWorkingCopy(authenticated.WorkingCopy);
                        Check(authenticated.NeedsCredentials && authenticated.HasError && !authenticated.WorkingCopy.HasError &&
                            authenticated.WorkingCopy.Entries.Count == 2, "offline local status remains available while SVN history requests credentials");

                        Dispatcher.UIThread.RunJobs();
                        var toolbar = window.GetVisualDescendants().OfType<SourceGit.Views.SvnRepositoryToolbar>().Single();
                        var toolbarTitle = toolbar.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == authenticated.ActiveTitle);
                        var toolbarRefresh = toolbar.GetVisualDescendants().OfType<Button>()
                            .Single(button => button.Content?.ToString() == App.Text("SVN.Refresh"));
                        Check(toolbarTitle.Text == App.Text("SVN.WorkingCopy") && authenticated.CanRefreshActive && toolbarRefresh.IsEnabled,
                            "working-copy toolbar title and refresh stay active while history needs credentials");
                        var fakeWorkingCopyGrid = window.GetVisualDescendants().OfType<DataGrid>().Single(control => control.Name == "WorkingCopyEntries");
                        var offlineA = FindWorkingCopyEntry(authenticated.WorkingCopy, "offline-a.txt");
                        var offlineB = FindWorkingCopyEntry(authenticated.WorkingCopy, "offline-b.txt");
                        await SelectWorkingCopyEntry(authenticated.WorkingCopy, fakeWorkingCopyGrid, offlineA);
                        var historySelectionBeforeToolbarRefresh = authenticated.SelectedRevision;
                        var historyCallsBeforeToolbarRefresh = CountHistoryCalls(fakeCalls);
                        var statusCallsBeforeToolbarRefresh = CountStatusCalls(fakeCalls);
                        toolbarRefresh.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                        await WaitForWorkingCopy(authenticated.WorkingCopy);
                        await WaitForLocalDiff(authenticated.WorkingCopy);
                        Check(CountHistoryCalls(fakeCalls) == historyCallsBeforeToolbarRefresh && authenticated.NeedsCredentials && authenticated.HasError &&
                            ReferenceEquals(authenticated.SelectedRevision, historySelectionBeforeToolbarRefresh),
                            "toolbar refresh leaves the history authentication error and selection untouched");
                        Check(CountStatusCalls(fakeCalls) > statusCallsBeforeToolbarRefresh &&
                            authenticated.WorkingCopy.SelectedEntry?.Path == offlineA.Path && authenticated.WorkingCopy.IsTextDiff,
                            "toolbar click reloads local status and preserves its selected diff");
                        offlineA = FindWorkingCopyEntry(authenticated.WorkingCopy, "offline-a.txt");
                        offlineB = FindWorkingCopyEntry(authenticated.WorkingCopy, "offline-b.txt");
                        await SelectWorkingCopyEntry(authenticated.WorkingCopy, fakeWorkingCopyGrid, offlineB);

                        var cancelledLocalDiffStarted = Path.Combine(temporary, "cancelled-local-diff-started");
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_LOCAL_DIFF_DELAY_PATH", "offline-a.txt");
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_LOCAL_DIFF_DELAY_STARTED", cancelledLocalDiffStarted);
                        await SelectWorkingCopyEntry(authenticated.WorkingCopy, fakeWorkingCopyGrid, offlineA, false);
                        await WaitUntil(() => File.Exists(cancelledLocalDiffStarted));
                        authenticated.WorkingCopy.CancelDiff();
                        await WaitForLocalDiff(authenticated.WorkingCopy);
                        Check(authenticated.WorkingCopy.HasDiffError && authenticated.WorkingCopy.CanRetryDiff &&
                            ReferenceEquals(authenticated.WorkingCopy.SelectedEntry, offlineA), "cancelled local diff is retryable for the same row");
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_LOCAL_DIFF_DELAY_PATH", null);
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_LOCAL_DIFF_DELAY_STARTED", null);
                        await authenticated.WorkingCopy.RetryDiffAsync();
                        await WaitForLocalDiff(authenticated.WorkingCopy);
                        Check(!authenticated.WorkingCopy.HasDiffError && authenticated.WorkingCopy.IsTextDiff &&
                            authenticated.WorkingCopy.DiffContent.Data.Lines.Any(line => line.Content.Contains("offline baseline A", StringComparison.Ordinal)),
                            "retry loads the same selected local file diff");

                        await SelectWorkingCopyEntry(authenticated.WorkingCopy, fakeWorkingCopyGrid, offlineB);
                        var staleLocalDiffStarted = Path.Combine(temporary, "stale-local-diff-started");
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_LOCAL_DIFF_DELAY_PATH", "offline-a.txt");
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_LOCAL_DIFF_DELAY_STARTED", staleLocalDiffStarted);
                        await SelectWorkingCopyEntry(authenticated.WorkingCopy, fakeWorkingCopyGrid, offlineA, false);
                        await WaitUntil(() => File.Exists(staleLocalDiffStarted));
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_LOCAL_DIFF_DELAY_PATH", null);
                        await SelectWorkingCopyEntry(authenticated.WorkingCopy, fakeWorkingCopyGrid, offlineB);
                        Check(ReferenceEquals(authenticated.WorkingCopy.SelectedEntry, offlineB) && authenticated.WorkingCopy.IsTextDiff &&
                            authenticated.WorkingCopy.DiffContent.Data.Lines.Any(line => line.Content.Contains("offline baseline B", StringComparison.Ordinal)),
                            "stale local diff cannot replace the newer file selection");
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_LOCAL_DIFF_DELAY_STARTED", null);
                        authenticatedRepositoryTabs.SelectedIndex = 0;
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
                        await WaitForPaths(authenticated);
                        Check(!authenticated.HasError && !authenticated.NeedsCredentials && authenticated.Revisions.Count == 100, "VM sign-in loads history");
                        Check(authenticated.ChangedPaths.Count == 2 && authenticated.ChangedPaths.Any(path => path.Kind == "dir" && path.Path.Contains("目录", StringComparison.Ordinal)), "directory and long Unicode changed path");
                        var copiedPath = authenticated.ChangedPaths.Single(path => path.Kind == "file");
                        Check(copiedPath.Action == "A" && copiedPath.Path.Contains("文件", StringComparison.Ordinal), "copied file path");
                        Check(copiedPath.CopyFromPath == "/archive/old name.txt" && Convert.ToString(copiedPath.CopyFromRevision) == "103", "copy source metadata");
                        Check(copiedPath.RelativePath.Contains("长文件名", StringComparison.Ordinal), "relative Unicode path");

                        Dispatcher.UIThread.RunJobs();
                        var fakePathsGrid = window.GetVisualDescendants().OfType<DataGrid>()
                            .Single(candidate => ReferenceEquals(candidate.ItemsSource, authenticated.ChangedPaths));
                        var fakeCopyPath = await SelectChangedPath(authenticated, fakePathsGrid, 104, path => path.Kind == "file");
                        Check(ReferenceEquals(fakeCopyPath, copiedPath) && !authenticated.HasDiffError && authenticated.IsTextDiff,
                            "selecting the copied path loads its historical text diff");
                        Check(authenticated.DiffBeforeText.Contains("old name.txt", StringComparison.Ordinal) && authenticated.DiffBeforeText.Contains("103", StringComparison.Ordinal) &&
                            authenticated.DiffAfterText.Contains("长文件名", StringComparison.Ordinal) && authenticated.DiffAfterText.Contains("104", StringComparison.Ordinal),
                            "copy diff compares copy source r103 with destination r104");
                        Check(authenticated.DiffContent is TextDiffContext copiedDiff &&
                            copiedDiff.Data.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Deleted && line.Content.Contains("Copied baseline at revision 103", StringComparison.Ordinal)) &&
                            copiedDiff.Data.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Added && line.Content.Contains("Copied target at revision 104", StringComparison.Ordinal)),
                            "copy diff content uses the historical copy baseline");

                        var emptyPath = await SelectChangedPath(authenticated, fakePathsGrid, 101, path => path.Path == "/trunk/empty.txt");
                        Check(emptyPath.Action == "A" && authenticated.DiffBeforeText.Contains("Absent", StringComparison.Ordinal) &&
                            authenticated.DiffAfterText.Contains("empty.txt", StringComparison.Ordinal) && authenticated.DiffNotice.Length > 0,
                            "empty added file keeps absent and present endpoints distinct");
                        var binaryPath = await SelectChangedPath(authenticated, fakePathsGrid, 100, path => path.Path == "/trunk/binary.dat");
                        Check(binaryPath.Kind == "file" && !authenticated.IsTextDiff && authenticated.DiffNotice.Contains("3 B", StringComparison.Ordinal) &&
                            authenticated.DiffNotice.Contains("4 B", StringComparison.Ordinal), "binary diff notice reports before and after sizes");
                        var callsBeforeDirectory = File.ReadAllLines(fakeCalls).Length;
                        var directoryPath = await SelectChangedPath(authenticated, fakePathsGrid, 99, path => path.Kind == "dir");
                        var directoryCalls = File.ReadAllLines(fakeCalls).Skip(callsBeforeDirectory);
                        Check(directoryPath.Kind == "dir" && !authenticated.HasDiffError && !authenticated.IsTextDiff && authenticated.DiffNotice.Length > 0,
                            "directory changes show a notice without requesting file content");
                        Check(!directoryCalls.Any(line => line.StartsWith("cat ", StringComparison.Ordinal)), "directory selection skips svn cat");
                        await SelectChangedPath(authenticated, fakePathsGrid, 97, path => path.Path == "/trunk/same.txt");
                        Check(!authenticated.HasDiffError && !authenticated.IsTextDiff && authenticated.DiffNotice.Length > 0 &&
                            authenticated.DiffBeforeText.Contains("96", StringComparison.Ordinal) && authenticated.DiffAfterText.Contains("97", StringComparison.Ordinal),
                            "identical snapshots have an explicit no-content-change state");
                        await SelectChangedPath(authenticated, fakePathsGrid, 96, path => path.Path == "/trunk/large.txt");
                        Check(!authenticated.HasDiffError && !authenticated.IsTextDiff && authenticated.DiffNotice.Length > 0,
                            "large snapshot content produces a size-limit notice");

                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_DELAY_REVISION", "104");
                        var staleDiffStarted = Path.Combine(temporary, "stale-diff-response-started");
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_DELAY_STARTED", staleDiffStarted);
                        await SelectChangedPath(authenticated, fakePathsGrid, 104, path => path.Kind == "file", false);
                        await WaitUntil(() => File.Exists(staleDiffStarted));
                        var staleDirectory = await SelectChangedPath(authenticated, fakePathsGrid, 99, path => path.Kind == "dir");
                        Check(ReferenceEquals(authenticated.SelectedChangedPath, staleDirectory) && !authenticated.IsTextDiff && authenticated.DiffNotice.Length > 0,
                            "stale file content cannot replace a newer directory selection");

                        var cancelledDiffStarted = Path.Combine(temporary, "cancelled-diff-response-started");
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_DELAY_STARTED", cancelledDiffStarted);
                        var retryPath = await SelectChangedPath(authenticated, fakePathsGrid, 104, path => path.Kind == "file", false);
                        await WaitUntil(() => File.Exists(cancelledDiffStarted));
                        authenticated.CancelDiff();
                        await WaitForDiff(authenticated);
                        Check(authenticated.HasDiffError && authenticated.CanRetryDiff && ReferenceEquals(authenticated.SelectedChangedPath, retryPath),
                            "cancelled file diff remains retryable for the same selection");
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_DELAY_REVISION", null);
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_DELAY_STARTED", null);
                        await authenticated.RetryDiffAsync();
                        await WaitForDiff(authenticated);
                        Check(!authenticated.HasDiffError && authenticated.IsTextDiff && ReferenceEquals(authenticated.SelectedChangedPath, retryPath),
                            "same selected file diff retries successfully");

                        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                        {
                            Application.Current.RequestedThemeVariant = theme;
                            Dispatcher.UIThread.RunJobs();
                            using var frame = window.CaptureRenderedFrame();
                            Check(frame != null, "render changed paths " + theme);
                            frame.Save(Path.Combine(screenshotDirectory, "changed-paths-" + theme + ".png"));
                        }

                        await authenticated.LoadMoreAsync();
                        await WaitForPaths(authenticated);
                        Check(!authenticated.HasError && authenticated.Revisions.Count == 101, "VM retains session credentials for paging");

                        authenticated.SelectedRevision = authenticated.Revisions.Single(revision => revision.Number == 5);
                        await WaitForPaths(authenticated);
                        Check(authenticated.IsPathsEmpty && authenticated.ChangedPaths.Count == 0, "empty changed-path result state");
                        authenticated.SelectedRevision = authenticated.Revisions[0];
                        await WaitForPaths(authenticated);

                        // Delay one response, change selection, and ensure the cancelled result cannot
                        // replace the paths belonging to the newer selection.
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_DELAY_REVISION", "103");
                        var delayStarted = Path.Combine(temporary, "stale-path-response-started");
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_DELAY_STARTED", delayStarted);
                        authenticated.SelectedRevision = authenticated.Revisions[1];
                        await WaitUntil(() => File.Exists(delayStarted));
                        authenticated.SelectedRevision = authenticated.Revisions[2];
                        await WaitForPaths(authenticated);
                        Check(authenticated.SelectedRevision.Number == 102 && authenticated.ChangedPaths.Single().Path == "/trunk/revision-102.txt", "stale changed-path response ignored");

                        // Explicit cancellation leaves a retryable state; retrying the same selection
                        // must repopulate the list.
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_DELAY_REVISION", "101");
                        delayStarted = Path.Combine(temporary, "cancelled-path-response-started");
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_DELAY_STARTED", delayStarted);
                        authenticated.SelectedRevision = authenticated.Revisions.Single(revision => revision.Number == 101);
                        await WaitUntil(() => File.Exists(delayStarted));
                        authenticated.CancelChangedPaths();
                        await WaitForPaths(authenticated);
                        Check(authenticated.HasPathsError && authenticated.CanRetryPaths && authenticated.ChangedPaths.Count == 0, "cancelled changed-path query is retryable");
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_DELAY_REVISION", null);
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_DELAY_STARTED", null);
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_DELAY_REVISION", null);
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_DELAY_STARTED", null);
                        await authenticated.RetryChangedPathsAsync();
                        await WaitForPaths(authenticated);
                        Check(!authenticated.HasPathsError && authenticated.ChangedPaths.Single().Path == "/trunk/empty.txt", "changed-path retry recovers");

                        var callsBeforeSwitch = CountHistoryCalls(fakeCalls);
                        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_URL", "svn://other.test/trunk");
                        await authenticated.RefreshAsync();
                        Check(authenticated.NeedsCredentials && authenticated.Revisions.Count == 0, "switch clears credentials and old history");
                        Check(CountHistoryCalls(fakeCalls) == callsBeforeSwitch + 1, "new URL attempted once without session credentials");
                        Check(File.ReadAllLines(fakeCalls)[^1] == "history anonymous", "switched URL receives no explicit credentials");
                    }

                    // A history endpoint can be public while a selected revision's changed-path
                    // endpoint requires credentials. Sign-in retries only that path request.
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_URL", "svn://fixture.test/trunk");
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_AUTH_HISTORY", "0");
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_AUTH", "1");
                    using (var pathAuthenticated = new SvnRepository(workingCopy))
                    {
                        await pathAuthenticated.RefreshAsync();
                        await WaitForPaths(pathAuthenticated);
                        Check(pathAuthenticated.HasError && pathAuthenticated.HasPathsError && pathAuthenticated.NeedsCredentials && pathAuthenticated.Revisions.Count == 100, "selected paths request credentials while preserving history");
                        var historyCallsBeforeSignIn = CountHistoryCalls(fakeCalls);
                        pathAuthenticated.Username = "fixture";
                        pathAuthenticated.Password = "ui-fixture-password";
                        await pathAuthenticated.SignInAsync();
                        await WaitForPaths(pathAuthenticated);
                        Check(!pathAuthenticated.HasError && !pathAuthenticated.NeedsCredentials && !pathAuthenticated.HasPathsError, "sign-in retries selected paths");
                        Check(pathAuthenticated.ChangedPaths.Count == 2, "authenticated changed paths loaded");
                        Check(CountHistoryCalls(fakeCalls) == historyCallsBeforeSignIn, "path sign-in preserves the loaded history page");
                    }

                    // Public history and changed paths can still require credentials for file content.
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_AUTH", "0");
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_AUTH", "1");
                    using (var diffAuthenticated = new SvnRepository(workingCopy))
                    {
                        window.Content = new SourceGit.Views.LauncherPage { DataContext = new LauncherPage(node, diffAuthenticated) };
                        await diffAuthenticated.RefreshAsync();
                        await WaitForPaths(diffAuthenticated);
                        Dispatcher.UIThread.RunJobs();
                        var diffAuthPathsGrid = window.GetVisualDescendants().OfType<DataGrid>()
                            .Single(candidate => ReferenceEquals(candidate.ItemsSource, diffAuthenticated.ChangedPaths));
                        var diffAuthPath = await SelectChangedPath(diffAuthenticated, diffAuthPathsGrid, 104, path => path.Kind == "file");
                        Check(diffAuthenticated.NeedsCredentials && diffAuthenticated.HasDiffError && ReferenceEquals(diffAuthenticated.SelectedChangedPath, diffAuthPath),
                            "file-content request prompts for credentials without losing the selected file");
                        var historyCallsBeforeSignIn = CountHistoryCalls(fakeCalls);
                        diffAuthenticated.Username = "fixture";
                        diffAuthenticated.Password = "ui-fixture-password";
                        await diffAuthenticated.SignInAsync();
                        await WaitForDiff(diffAuthenticated);
                        Check(!diffAuthenticated.NeedsCredentials && !diffAuthenticated.HasDiffError && diffAuthenticated.IsTextDiff &&
                            ReferenceEquals(diffAuthenticated.SelectedChangedPath, diffAuthPath), "sign-in retries the same selected-file diff");
                        Check(diffAuthenticated.DiffBeforeText.Contains("103", StringComparison.Ordinal) && diffAuthenticated.DiffAfterText.Contains("104", StringComparison.Ordinal),
                            "authenticated selected-file diff retains its historical endpoints");
                        Check(CountHistoryCalls(fakeCalls) == historyCallsBeforeSignIn, "file sign-in preserves the loaded history page");
                    }
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_AUTH_HISTORY", null);
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_AUTH", null);
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_AUTH", null);
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_ROOT", null);
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_URL", null);
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_CALLS", null);
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_STATUS_ENTRIES", null);
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_LOCAL_DIFF_DELAY_PATH", null);
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_LOCAL_DIFF_DELAY_STARTED", null);
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_DELAY_REVISION", null);
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_DELAY_STARTED", null);
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_DELAY_REVISION", null);
                    Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_DELAY_STARTED", null);
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
                    await VerifyLocalWorkingCopyUi(window, localWorkingCopy, screenshotDirectory);
                    window.Close();
                    Console.WriteLine("Screenshots: " + screenshotDirectory);
                    return true;
                }, uiTimeout.Token);
            }
            finally
            {
                // Dispatch may complete inline on the session thread. Dispose joins that
                // thread, so it must run elsewhere even when a check fails.
                await Task.Run(session.Dispose).ConfigureAwait(false);
            }


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

    private static async Task<int> RunLocalOnly(string temporary, string config, string workingCopy)
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(Program));
        using var uiTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var screenshotDirectory = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_ARTIFACTS") ??
                Path.Combine(Path.GetTempPath(), "sourcegit-svn-ui-artifacts");
            Directory.CreateDirectory(screenshotDirectory);
            await session.Dispatch<bool>(async () =>
            {
                var preferences = Preferences.Instance;
                preferences.SvnInstallPath = SourceGit.Native.Svn.ResolveExecutable(string.Empty);
                preferences.GitInstallPath = SourceGit.Native.OS.FindGitExecutable();
                preferences.SvnConfigDirectory = config;
                preferences.SetCanModify();

                var window = new Window { Width = 1024, Height = 600 };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                await VerifyLocalWorkingCopyUi(window, workingCopy, screenshotDirectory);
                window.Close();
                Console.WriteLine("Screenshots: " + screenshotDirectory);
                return true;
            }, uiTimeout.Token);

            Check(s_checks >= 25, "focused local working-copy UI workflow executed all checks");
            Console.WriteLine($"{s_checks} focused SVN working-copy UI checks passed.");
            return 0;
        }
        finally
        {
            await Task.Run(session.Dispose).ConfigureAwait(false);
        }
    }

    private static async Task<string> CreateLocalWorkingCopyFixture(string temporary, string config)
    {
        var repository = Path.Combine(temporary, "local-status-repository");
        var workingCopy = Path.Combine(temporary, "local status 工作 copy");
        var remoteCopy = Path.Combine(temporary, "local-status-remote-copy");
        await Run("svnadmin", "create", repository);
        var url = new Uri(repository + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        await Run("svn", "checkout", "--config-dir", config, url, workingCopy);

        foreach (var name in new[]
        {
            "modified.txt", "added-source.txt", "deleted.txt", "missing.txt", "property-only.txt",
            "conflicted.txt", "replacement.txt", "copy-source.txt", "move-source.txt", "nested/inside.txt",
            "update-target.txt", "commit-selected.txt", "commit-unchecked.txt", "commit-stale.txt", "remove-multi-a.txt",
            "remove-multi-b.txt", "revert-content.txt",
        })
        {
            var path = Path.Combine(workingCopy, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, name switch
            {
                "conflicted.txt" => "base conflict line\n",
                "update-target.txt" => "update revision one\n",
                _ => "base line\n",
            });
        }
        await Run("svn", "add", "--force", "--", workingCopy);
        await Run("svn", "commit", "--config-dir", config, "-m", "working copy baseline", "--", workingCopy);

        // Create a genuine text conflict entirely against the local file:// repository.
        await Run("svn", "checkout", "--config-dir", config, url, remoteCopy);
        File.WriteAllText(Path.Combine(workingCopy, "conflicted.txt"), "local conflict line\n");
        File.WriteAllText(Path.Combine(remoteCopy, "conflicted.txt"), "remote conflict line\n");
        File.WriteAllText(Path.Combine(remoteCopy, "update-target.txt"), "update revision two\n");
        await Run("svn", "commit", "--config-dir", config, "-m", "remote conflict edit", "--", remoteCopy);
        await Run("svn", "update", "--config-dir", config, "--", workingCopy);

        File.WriteAllText(Path.Combine(workingCopy, "modified.txt"), "modified local line\n");
        await Run("svn", "delete", "--", Path.Combine(workingCopy, "deleted.txt"));
        await Run("svn", "propset", "fixture:label", "local-only", "--", Path.Combine(workingCopy, "property-only.txt"));
        await Run("svn", "delete", "--", Path.Combine(workingCopy, "replacement.txt"));
        File.WriteAllText(Path.Combine(workingCopy, "replacement.txt"), "replacement local line\n");
        await Run("svn", "add", "--", Path.Combine(workingCopy, "replacement.txt"));
        await Run("svn", "copy", "--", Path.Combine(workingCopy, "copy-source.txt"), Path.Combine(workingCopy, "copied.txt"));
        await Run("svn", "move", "--", Path.Combine(workingCopy, "move-source.txt"), Path.Combine(workingCopy, "moved.txt"));
        var addedFile = Path.Combine(workingCopy, "added.txt");
        File.WriteAllText(addedFile, "added local line\n");
        await Run("svn", "add", "--", addedFile);
        var addedDirectory = Path.Combine(workingCopy, "added-directory");
        Directory.CreateDirectory(addedDirectory);
        File.WriteAllText(Path.Combine(addedDirectory, "child.txt"), "added child\n");
        await Run("svn", "add", "--force", "--", addedDirectory);
        File.WriteAllText(Path.Combine(workingCopy, "remove-multi-a.txt"), "remove multi local A\n");
        File.WriteAllText(Path.Combine(workingCopy, "remove-multi-b.txt"), "remove multi local B\n");
        File.WriteAllText(Path.Combine(workingCopy, "revert-content.txt"), "revert local contents\n");
        File.WriteAllText(Path.Combine(workingCopy, "unversioned.txt"), "unversioned local line\n");
        File.Delete(Path.Combine(workingCopy, "missing.txt"));
        return workingCopy;
    }

    private static async Task VerifyLocalWorkingCopyUi(Window window, string workingCopy, string screenshotDirectory)
    {
        window.Width = 1024;
        window.Height = 600;
        using var repository = new SvnRepository(workingCopy);
        var node = new RepositoryNode { Id = workingCopy, Name = "SVN local status fixture", IsRepository = true, IsSvn = true };
        window.Content = new SourceGit.Views.LauncherPage { DataContext = new LauncherPage(node, repository) };
        Dispatcher.UIThread.RunJobs();
        await repository.RefreshAsync();
        await WaitForPaths(repository);
        await WaitUntil(() => !repository.IsLoading && !repository.WorkingCopy.IsLoading);
        if (repository.WorkingCopy.IsEmpty || repository.WorkingCopy.HasError)
            await repository.WorkingCopy.RefreshAsync();
        await WaitForWorkingCopy(repository.WorkingCopy);
        Check(!repository.IsEmpty && !repository.HasError && repository.Revisions.Count > 0,
            "history loads while the working-copy status view initializes");

        var repositoryTabs = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "RepositoryTabs");
        repositoryTabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        await WaitForWorkingCopy(repository.WorkingCopy);
        Check(repository.SelectedTabIndex == 1, "working-copy tab selection is bound to repository state");
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single(control => control.Name == "WorkingCopyEntries");
        Check(ReferenceEquals(grid.ItemsSource, repository.WorkingCopy.VisibleEntries) && grid.IsReadOnly && grid.GetVisualDescendants().OfType<DataGridRow>().Any(),
            "working-copy status rows are displayed read-only");

        var entries = repository.WorkingCopy;
        Check(!entries.HasError && !entries.IsEmpty && entries.CanRefresh && entries.Title.Length > 0,
            "local status loads independently with a populated title");
        var modified = FindWorkingCopyEntry(entries, "modified.txt");
        var added = FindWorkingCopyEntry(entries, "added.txt");
        var deleted = FindWorkingCopyEntry(entries, "deleted.txt");
        var missing = FindWorkingCopyEntry(entries, "missing.txt");
        var unversioned = FindWorkingCopyEntry(entries, "unversioned.txt");
        var propertyOnly = FindWorkingCopyEntry(entries, "property-only.txt");
        var conflicted = FindWorkingCopyEntry(entries, "conflicted.txt");
        var directory = FindWorkingCopyEntry(entries, "added-directory");
        var copied = FindWorkingCopyEntry(entries, "copied.txt");
        var moveSource = FindWorkingCopyEntry(entries, "move-source.txt");
        var moved = FindWorkingCopyEntry(entries, "moved.txt");
        var replacement = FindWorkingCopyEntry(entries, "replacement.txt");
        Check(modified.Entry.Item == "modified" && added.Entry.Item == "added" && deleted.Entry.Item == "deleted" &&
            unversioned.Entry.Item == "unversioned", "modified, scheduled add/delete, and unversioned states are distinct");
        Check(missing.Entry.Item == "missing" && missing.HasWarning && conflicted.Entry.Item == "conflicted" && conflicted.HasWarning &&
            entries.SelectionNotice.Length == 0, "missing and conflicted states carry warning presentation");
        Check(propertyOnly.Entry.Item == "normal" && propertyOnly.Entry.Properties == "modified",
            "property-only changes remain distinct from text changes");
        Check(directory.Entry.Item == "added" && directory.Entry.Kind == "dir" &&
            FindWorkingCopyEntry(entries, "child.txt").Entry.Item == "added", "added directory and child are retained");
        Check(copied.Entry.Item == "added" && copied.Entry.IsCopied && !string.IsNullOrEmpty(moveSource.Entry.MovedTo) &&
            moved.Entry.IsCopied && !string.IsNullOrEmpty(moved.Entry.MovedFrom), "copy and move ancestry is preserved");
        Check(replacement.Entry.Item == "replaced", "delete-and-add replacement state is retained");

        var modifiedPath = Path.Combine(workingCopy, "modified.txt");
        var originalModifiedContent = File.ReadAllText(modifiedPath);
        await SelectWorkingCopyEntry(entries, grid, modified);
        Check(entries.IsTextDiff && !entries.HasDiffError && entries.DiffContent is TextDiffContext modifiedDiff &&
            modifiedDiff.Data.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Deleted && line.Content.Contains("base line", StringComparison.Ordinal)) &&
            modifiedDiff.Data.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Added && line.Content.Contains("modified local line", StringComparison.Ordinal)),
            "modified working-copy diff compares BASE with the local file");
        Check(!entries.DiffContent.Option.IsLocalChange, "working-copy diff does not expose Git hunk mutation actions");
        repository.DiffModeSideBySide = false;
        Check(entries.DiffContent is CombinedTextDiff, "repository diff mode selects the combined local layout");
        repository.DiffModeSideBySide = true;
        Check(entries.DiffContent is TwoSideTextDiff && entries.IsTextDiff,
            "repository diff mode updates an already loaded local diff without reloading it");
        repository.DiffModeSideBySide = false;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        await WaitUntil(() => window.GetVisualDescendants().OfType<SourceGit.Views.TextDiffView>()
            .Any(control => control.IsEffectivelyVisible));
        var localDiffView = window.GetVisualDescendants().OfType<SourceGit.Views.TextDiffView>()
            .Single(control => control.IsEffectivelyVisible);
        await WaitUntil(() => localDiffView.GetVisualDescendants().OfType<StackPanel>()
            .Any(panel => panel.Name == "Popup"));
        var localHunkActions = localDiffView.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "Popup");
        Check(!localHunkActions.IsVisible, "working-copy diff hides Git stage, unstage, and discard actions");

        await SelectWorkingCopyEntry(entries, grid, added);
        Check(entries.IsTextDiff && entries.DiffContent.Data.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Added &&
            line.Content.Contains("added local line", StringComparison.Ordinal)), "scheduled addition diff includes the new file contents");
        await SelectWorkingCopyEntry(entries, grid, deleted);
        Check(entries.IsTextDiff && entries.DiffContent.Data.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Deleted &&
            line.Content.Contains("base line", StringComparison.Ordinal)), "scheduled deletion diff includes removed contents");
        await SelectWorkingCopyEntry(entries, grid, unversioned);
        Check(entries.IsTextDiff && entries.DiffContent.Data.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Added &&
            line.Content.Contains("unversioned local line", StringComparison.Ordinal)), "unversioned file receives an empty-to-local preview");
        await SelectWorkingCopyEntry(entries, grid, missing);
        Check(!entries.HasDiffError && entries.IsTextDiff && entries.DiffContent.Data.Lines.Any(line =>
            line.Type == SourceGit.Models.TextDiffLineType.Deleted && line.Content.Contains("base line", StringComparison.Ordinal)) &&
            entries.SelectionNotice.Contains("not scheduled for deletion", StringComparison.Ordinal) && missing.Entry.Item == "missing",
            "missing file shows BASE contents as absent without scheduling deletion");
        await SelectWorkingCopyEntry(entries, grid, directory);
        Check(!entries.HasDiffError && !entries.IsTextDiff && entries.DiffNotice.Length > 0,
            "directory selection reports that it has no text diff");
        await SelectWorkingCopyEntry(entries, grid, copied);
        Check(!entries.HasDiffError && entries.IsTextDiff && entries.DiffContent.Data.Lines.Any(line =>
            line.Type == SourceGit.Models.TextDiffLineType.Added && line.Content.Contains("base line", StringComparison.Ordinal)),
            "copy diff shows the copied source as a local addition");
        await SelectWorkingCopyEntry(entries, grid, moved);
        Check(!entries.HasDiffError && entries.IsTextDiff && entries.DiffContent.Data.Lines.Any(line =>
            line.Type == SourceGit.Models.TextDiffLineType.Added && line.Content.Contains("base line", StringComparison.Ordinal)),
            "moved destination diff shows the copied source contents");
        await SelectWorkingCopyEntry(entries, grid, replacement);
        Check(entries.IsTextDiff && entries.DiffContent.Data.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Deleted &&
            line.Content.Contains("base line", StringComparison.Ordinal)) && entries.DiffContent.Data.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Added &&
            line.Content.Contains("replacement local line", StringComparison.Ordinal)), "replacement diff compares old and replacement contents");
        await SelectWorkingCopyEntry(entries, grid, conflicted);
        Check(conflicted.HasWarning && entries.SelectionNotice.Contains("BASE", StringComparison.Ordinal) &&
            !entries.HasDiffError && entries.IsTextDiff,
            "conflicted file keeps a local preview and explicit conflict notice");

        await SelectWorkingCopyEntry(entries, grid, propertyOnly);
        Check(entries.HasPropertyDiff && entries.PropertyDiffContent.Contains("fixture:label", StringComparison.Ordinal) &&
            entries.PropertyDiffContent.Contains("local-only", StringComparison.Ordinal), "property-only change has a readable property diff");
        var diffTabs = window.GetVisualDescendants().OfType<TabControl>().Single(control => control.Name == "WorkingCopyDiffTabs");
        diffTabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(window, theme,
                Path.Combine(screenshotDirectory, "working-copy-property-" + theme + ".png"), "working-copy property diff");

        diffTabs.SelectedIndex = 0;
        await SelectWorkingCopyEntry(entries, grid, conflicted);
        Check(entries.SelectedEntry == conflicted && entries.SelectionNotice.Length > 0,
            "conflict selection presents the conflict state in the local diff pane");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(window, theme,
                Path.Combine(screenshotDirectory, "working-copy-conflict-" + theme + ".png"), "working-copy conflict state");

        repositoryTabs.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Check(repository.SelectedTabIndex == 0, "switching back to history preserves its active tab state");
        await WaitUntil(() => window.GetVisualDescendants().OfType<DataGrid>()
            .Any(control => ReferenceEquals(control.ItemsSource, repository.ChangedPaths)));
        var historicalPathsGrid = window.GetVisualDescendants().OfType<DataGrid>()
            .Single(control => ReferenceEquals(control.ItemsSource, repository.ChangedPaths));
        await WaitForPaths(repository);
        const string expectedHistoricalPath = "/conflicted.txt";
        var changedPathDetails = string.Join(", ", repository.ChangedPaths.Select(path => $"{path.Path} ({path.Kind})"));
        Check(!repository.HasPathsError && repository.ChangedPaths.Any(path => path.Path == expectedHistoricalPath),
            $"expected historical path {expectedHistoricalPath}; HasPathsError={repository.HasPathsError}; Details={repository.PathsErrorDetails}; ChangedPaths=[{changedPathDetails}]");
        var historicalPath = repository.ChangedPaths.Single(path => path.Path == expectedHistoricalPath);
        historicalPathsGrid.SelectedItem = historicalPath;
        Dispatcher.UIThread.RunJobs();
        await WaitForDiff(repository);
        var selection = repository.SelectedRevision;
        Check(ReferenceEquals(repository.SelectedChangedPath, historicalPath) && repository.IsTextDiff,
            "history tab retains an independently selected historical diff");
        var beforeSideBySide = DescribeSharedDiffMode(repository);
        repository.DiffModeSideBySide = true;
        var afterSideBySide = DescribeSharedDiffMode(repository);
        Check(repository.DiffContent is TwoSideTextDiff && entries.DiffContent is TwoSideTextDiff,
            $"shared diff mode updates both historical and local views; Before=[{beforeSideBySide}] After=[{afterSideBySide}]");
        var beforeCombined = DescribeSharedDiffMode(repository);
        repository.DiffModeSideBySide = false;
        var afterCombined = DescribeSharedDiffMode(repository);
        Check(repository.DiffContent is CombinedTextDiff && entries.DiffContent is CombinedTextDiff,
            $"shared diff mode switches both views back without reloading; Before=[{beforeCombined}] After=[{afterCombined}]");
        repositoryTabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Check(repository.SelectedTabIndex == 1 && ReferenceEquals(entries.SelectedEntry, conflicted) &&
            ReferenceEquals(repository.SelectedChangedPath, historicalPath) && ReferenceEquals(repository.SelectedRevision, selection),
            "switching tabs preserves both history and local selections");

        await entries.RefreshAsync();
        await WaitForWorkingCopy(entries);
        await WaitForLocalDiff(entries);
        Check(entries.SelectedEntry?.Path == conflicted.Path && !entries.HasError && !entries.HasDiffError &&
            File.ReadAllText(modifiedPath) == originalModifiedContent, "local refresh and diff leave the working copy unchanged and preserve selection");
        await repository.RefreshActiveAsync();
        await WaitForWorkingCopy(entries);
        Check(ReferenceEquals(repository.SelectedRevision, selection), "active working-copy refresh leaves SVN history selection unchanged");

        var selectedActions = window.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "WorkingCopySelectedActions");
        entries.SelectedEntry = null;
        Dispatcher.UIThread.RunJobs();
        Check(!selectedActions.IsEnabled && entries.SelectedEntries.Count == 0,
            "selected actions require at least one working-copy row");
        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "conflicted.txt"));
        Check(!entries.CanAdd && !entries.CanRemove && !entries.CanRevert && !entries.CanCommit,
            "conflicted rows are excluded from all selected operations");
        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "copied.txt"));
        Check(!entries.CanAdd && !entries.CanRemove && !entries.CanRevert && entries.CanCommit,
            "copy ancestry can be committed while discard and scheduling actions remain excluded");
        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "unversioned.txt"));
        Dispatcher.UIThread.RunJobs();
        Check(selectedActions.IsEnabled && entries.CanAdd && !entries.CanRemove && !entries.CanUndo &&
            !entries.CanRevert && !entries.CanCommit, "unversioned selection enables only Add among selected actions");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(window, theme,
                Path.Combine(screenshotDirectory, "working-copy-selected-actions-" + theme + ".png"), "working-copy selected actions");

        // Keyboard activation must open the selected-actions menu without changing status.
        selectedActions.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.Escape, RawInputModifiers.None);
        window.KeyRelease(Key.Escape, RawInputModifiers.None);
        Check(!entries.IsBusy && entries.SelectedEntry?.Entry.Item == "unversioned",
            "keyboard opens and dismisses selected actions without running a command");
        await entries.AddAsync();
        await WaitForWorkingCopy(entries);
        Check(!entries.HasActionError && entries.SelectedEntry.Path == "unversioned.txt" &&
            File.ReadAllText(Path.Combine(workingCopy, "unversioned.txt")) == "unversioned local line\n",
            "eligible Add schedules the selected file and refreshes status without changing content");

        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "modified.txt"));
        var confirmation = new TaskCompletionSource<bool>();
        var pendingRemoval = entries.RemoveAsync(message =>
        {
            Check(message.Contains("modified.txt", StringComparison.Ordinal) && message.Contains("kept", StringComparison.Ordinal),
                "remove confirmation names the selected path and explains local preservation");
            return confirmation.Task;
        });
        Check(entries.IsBusy && !selectedActions.IsEnabled && !entries.CanAdd && !entries.CanRemove && !entries.CanUndo &&
            !entries.CanRefresh && !repository.CanRefreshActive,
            "pending confirmation disables selected actions and refresh");
        await entries.AddAsync();
        confirmation.SetResult(false);
        await pendingRemoval;
        Check(!entries.IsBusy && !entries.HasActionError && entries.SelectedEntry.Entry.Item == "modified" &&
            File.ReadAllText(modifiedPath) == originalModifiedContent, "declining removal leaves scheduling and edited bytes unchanged");

        await entries.RemoveAsync(_ => Task.FromResult(true));
        await WaitForLocalDiff(entries);
        Check(!entries.HasActionError && entries.SelectedEntry?.Entry.Item == "deleted" &&
            File.ReadAllText(modifiedPath) == originalModifiedContent && !entries.CanRemove &&
            entries.ActionNotice.Contains("kept", StringComparison.Ordinal),
            "confirmed removal schedules deletion, preserves local edits, and refreshes eligibility");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(window, theme,
                Path.Combine(screenshotDirectory, "working-copy-remove-" + theme + ".png"), "working-copy removal result");

        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "missing.txt"));
        Check(entries.CanRemove && !entries.CanAdd, "missing versioned paths can be scheduled for removal");
        await entries.RemoveAsync(_ => Task.FromResult(true));
        Check(!entries.HasActionError && entries.SelectedEntry?.Entry.Item == "deleted" &&
            !File.Exists(Path.Combine(workingCopy, "missing.txt")), "missing-path removal refreshes to scheduled deletion");

        var actionDirectory = Path.Combine(workingCopy, "action @ directory");
        Directory.CreateDirectory(actionDirectory);
        File.WriteAllText(Path.Combine(actionDirectory, "child.txt"), "unscheduled child\n");
        await entries.RefreshAsync();
        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "action @ directory"));
        await entries.AddAsync();
        Check(!entries.HasActionError && entries.SelectedEntry?.Entry.Item == "added" &&
            entries.Entries.Any(entry => entry.Path == Path.Combine("action @ directory", "child.txt") && entry.Entry.Item == "unversioned"),
            "adding a directory exposes its unversioned children without recursively scheduling them");

        var stalePath = Path.Combine(workingCopy, "stale @ action.txt");
        File.WriteAllText(stalePath, "stale selection bytes\n");
        await entries.RefreshAsync();
        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "stale @ action.txt"));
        await Run("svn", "add", "--", stalePath + "@");
        await entries.AddAsync();
        Check(entries.HasActionError && !entries.HasError && entries.Entries.Count > 0 && entries.CanRefresh &&
            entries.SelectedEntry?.Entry.Item == "added" && File.ReadAllText(stalePath) == "stale selection bytes\n",
            "stale scheduling is rejected with a separate action error while refreshed status remains usable");
        await WaitForLocalDiff(entries);
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(window, theme,
                Path.Combine(screenshotDirectory, "working-copy-action-error-" + theme + ".png"), "working-copy action error");

        Dispatcher.UIThread.RunJobs();
        Check(entries.CanUndo && selectedActions.IsEnabled, "plain scheduled addition enables the selected-actions menu");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(window, theme,
                Path.Combine(screenshotDirectory, "working-copy-undo-add-" + theme + ".png"), "Undo addition selected-actions state");
        await entries.UndoAsync();
        await WaitForWorkingCopy(entries);
        Check(!entries.HasActionError && entries.CanAdd && !entries.CanUndo &&
            File.ReadAllText(stalePath) == "stale selection bytes\n",
            "Undo addition keeps local contents and refreshes to unversioned");

        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "modified.txt"));
        Dispatcher.UIThread.RunJobs();
        Check(selectedActions.IsEnabled && entries.CanUndo, "scheduled removal enables Undo in selected actions");
        await entries.UndoAsync();
        await WaitForWorkingCopy(entries);
        await WaitForLocalDiff(entries);
        Check(!entries.HasActionError && !entries.CanUndo && entries.CanRemove &&
            File.ReadAllText(modifiedPath) == originalModifiedContent && entries.IsTextDiff &&
            entries.DiffContent.Data.Lines.Any(line => line.Content.Contains("modified local line", StringComparison.Ordinal)),
            "Undo removal preserves retained edits and shows the local diff instead of discarding it");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(window, theme,
                Path.Combine(screenshotDirectory, "working-copy-undo-remove-" + theme + ".png"), "Undo removal retained edits");

        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "missing.txt"));
        await entries.UndoAsync();
        Check(!entries.HasActionError && entries.SelectedEntry == null && !entries.CanUndo &&
            File.ReadAllText(Path.Combine(workingCopy, "missing.txt")) == "base line\n" &&
            !entries.Entries.Any(entry => entry.Path == "missing.txt"),
            "Undo removal of an absent file restores BASE and removes its clean row");

        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "action @ directory"));
        await entries.UndoAsync();
        Check(!entries.HasActionError && entries.SelectedEntry?.Entry.Item == "unversioned" &&
            File.ReadAllText(Path.Combine(actionDirectory, "child.txt")) == "unscheduled child\n",
            "Undo added directory leaves its unversioned contents intact");
        await entries.AddAsync();
        await SelectWorkingCopyEntry(entries, grid,
            entries.Entries.Single(entry => entry.Path == Path.Combine("action @ directory", "child.txt")));
        await entries.AddAsync();
        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "action @ directory"));
        await entries.UndoAsync();
        Check(!entries.HasActionError && entries.SelectedEntry?.Entry.Item == "unversioned" &&
            File.ReadAllText(Path.Combine(actionDirectory, "child.txt")) == "unscheduled child\n",
            "Undo a directory also unschedules its added children while retaining their bytes");

        foreach (var name in new[] { "modified.txt", "conflicted.txt", "copied.txt", "replacement.txt", "property-only.txt" })
        {
            await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, name));
            Check(!entries.CanUndo, "Undo does not offer generic discard or ancestry changes: " + name);
        }

        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "stale @ action.txt"));
        await entries.AddAsync();
        await Run("svn", "revert", "--config-dir", Preferences.Instance.SvnConfigDirectory, "--depth", "empty", "--", stalePath + "@");
        await entries.UndoAsync();
        Check(entries.HasActionError && !entries.HasError && entries.SelectedEntry?.Entry.Item == "unversioned" &&
            File.ReadAllText(stalePath) == "stale selection bytes\n",
            "stale Undo is rejected and refreshes status without discarding local contents");

        await entries.AddAsync();
        await WaitForWorkingCopy(entries);
        Check(!entries.HasActionError && entries.SelectedEntry?.Entry.Item == "added",
            "a successful action clears the stale action error");
        await entries.UndoAsync();
        await WaitForWorkingCopy(entries);
        Check(!entries.HasActionError && entries.SelectedEntry?.Entry.Item == "unversioned" &&
            File.ReadAllText(stalePath) == "stale selection bytes\n",
            "recovery leaves the row usable and retains its bytes");

        await VerifyCoreWorkingCopyOperations(window, repositoryTabs, repository, grid, entries,
            workingCopy, Path.Combine(Path.GetDirectoryName(workingCopy), "local-status-repository"),
            Path.Combine(Path.GetDirectoryName(workingCopy), "local-status-remote-copy"),
            Preferences.Instance.SvnConfigDirectory, screenshotDirectory, selectedActions);
        await VerifyConflictUi(window, Path.GetDirectoryName(workingCopy),
            Preferences.Instance.SvnConfigDirectory, screenshotDirectory);
        await VerifyPathOperationUi(window, Path.GetDirectoryName(workingCopy),
            Preferences.Instance.SvnConfigDirectory, screenshotDirectory);
        var propertiesFixture = await CreatePropertiesFixture(Path.GetDirectoryName(workingCopy),
            Preferences.Instance.SvnConfigDirectory);
        await VerifyPropertiesUi(window, propertiesFixture.WorkingCopy,
            Preferences.Instance.SvnConfigDirectory, screenshotDirectory);
        var changelistFixture = await CreateChangelistFixture(Path.GetDirectoryName(workingCopy),
            Preferences.Instance.SvnConfigDirectory);
        await VerifyChangelistUi(window, changelistFixture,
            Preferences.Instance.SvnConfigDirectory, screenshotDirectory);
        await VerifyRepairUi(window, Path.GetDirectoryName(workingCopy),
            Preferences.Instance.SvnConfigDirectory, screenshotDirectory);
    }

    private static async Task VerifyCoreWorkingCopyOperations(Window window, TabControl repositoryTabs,
        SvnRepository repository, DataGrid grid, SvnWorkingCopy entries, string workingCopy,
        string repositoryPath, string remoteCopy, string config, string screenshotDirectory, Button selectedActions)
    {
        var previouslyAddedPath = Path.Combine(workingCopy, "unversioned.txt");
        await Run("svn", "revert", "--config-dir", config, "--depth", "empty", "--", previouslyAddedPath + "@");
        var missingPath = Path.Combine(workingCopy, "missing.txt");
        File.Delete(missingPath);
        await entries.RefreshAsync();
        await WaitForWorkingCopy(entries);
        window.Height = 760;
        window.UpdateLayout();
        using (var layoutFrame = window.CaptureRenderedFrame())
            Check(layoutFrame != null, "render working-copy rows before pointer input");
        Dispatcher.UIThread.RunJobs();
        var removeA = FindWorkingCopyEntry(entries, "remove-multi-a.txt");
        var removeB = FindWorkingCopyEntry(entries, "remove-multi-b.txt");
        var modified = FindWorkingCopyEntry(entries, "modified.txt");
        var unversioned = FindWorkingCopyEntry(entries, "unversioned.txt");
        Check(unversioned.Entry.Item == "unversioned" && FindWorkingCopyEntry(entries, "missing.txt").Entry.Item == "missing",
            "core operation fixture starts with an unscheduled unversioned file and a missing versioned file");
        var mixedBytes = File.ReadAllText(Path.Combine(workingCopy, "modified.txt"));

        ScrollGridToTop(window, grid);
        var contextRows = grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(row => row.DataContext is SvnWorkingCopyEntry).Take(3).ToArray();
        Check(contextRows.Length == 3, "three working-copy status rows are realized for pointer selection");
        var contextA = (SvnWorkingCopyEntry)contextRows[0].DataContext;
        var contextB = (SvnWorkingCopyEntry)contextRows[1].DataContext;
        var contextC = (SvnWorkingCopyEntry)contextRows[2].DataContext;
        await SelectWorkingCopyEntries(entries, grid, contextA, contextB);
        Check(entries.SelectedEntries.Count == 2, "visible status rows create an extended selection");

        var workingCopyContextRequests = 0;
        grid.AddHandler(Control.ContextRequestedEvent, (_, _) => workingCopyContextRequests++,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        await RightClickWorkingCopyRow(window, grid, contextA);
        Check(workingCopyContextRequests > 0 && entries.SelectedEntries.Count == 2 &&
            ReferenceEquals(entries.SelectedEntry, contextA),
            $"right-click on selected row preserves multiselection; ContextRequested={workingCopyContextRequests}; " +
            $"Selected=[{string.Join(", ", entries.SelectedEntries.Select(entry => entry.Path))}]; Primary={entries.SelectedEntry?.Path}");
        await RightClickWorkingCopyRow(window, grid, contextC);
        Check(workingCopyContextRequests > 1 && entries.SelectedEntries.Count == 1 &&
            ReferenceEquals(entries.SelectedEntries[0], contextC),
            $"right-click on unselected row selects it; ContextRequested={workingCopyContextRequests}; " +
            $"Selected=[{string.Join(", ", entries.SelectedEntries.Select(entry => entry.Path))}]; Primary={entries.SelectedEntry?.Path}");

        repositoryTabs.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        await WaitForPaths(repository);
        await WaitUntil(() => window.GetVisualDescendants().OfType<DataGrid>()
            .Any(control => control.Name == "HistoryRevisions"));
        var historyGrid = window.GetVisualDescendants().OfType<DataGrid>().Single(control => control.Name == "HistoryRevisions");
        ScrollGridToTop(window, historyGrid);
        await WaitUntil(() => historyGrid.GetVisualDescendants().OfType<DataGridRow>()
            .Count(row => row.DataContext is SourceGit.Native.SvnRevision) >= 2);
        var historyRows = historyGrid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(row => row.DataContext is SourceGit.Native.SvnRevision).ToArray();
        Check(historyRows.Length >= 2, "history rows are available for pointer-context checks");
        var selectedRevision = (SourceGit.Native.SvnRevision)historyRows[0].DataContext;
        var otherRevision = (SourceGit.Native.SvnRevision)historyRows[1].DataContext;
        historyGrid.SelectedItem = selectedRevision;
        Dispatcher.UIThread.RunJobs();
        var historyContextRequests = 0;
        historyGrid.AddHandler(Control.ContextRequestedEvent, (_, _) => historyContextRequests++,
            Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        await RightClickRow(window, historyGrid, selectedRevision);
        Check(historyContextRequests > 0 && ReferenceEquals(repository.SelectedRevision, selectedRevision),
            $"right-click selected history row raises context; ContextRequested={historyContextRequests}; " +
            $"Selected={repository.SelectedRevision?.Number}; Expected={selectedRevision.Number}");
        await RightClickRow(window, historyGrid, otherRevision);
        Check(historyContextRequests > 1 && ReferenceEquals(repository.SelectedRevision, otherRevision),
            $"right-click unselected history row selects it; ContextRequested={historyContextRequests}; " +
            $"Selected={repository.SelectedRevision?.Number}; Expected={otherRevision.Number}");
        repositoryTabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();

        await SelectWorkingCopyEntries(entries, grid, removeA, unversioned);
        Check(!entries.CanRemove && !entries.CanRevert && !entries.CanCommit && !entries.CanAdd &&
            !entries.CanUndo && selectedActions.IsEnabled,
            "a mixed selection disables each mutating action instead of applying to its eligible subset");
        await entries.RevertAsync(_ => Task.FromResult(true));
        await WaitForWorkingCopy(entries);
        Check(File.ReadAllText(Path.Combine(workingCopy, "modified.txt")) == mixedBytes &&
            File.ReadAllText(Path.Combine(workingCopy, "remove-multi-a.txt")) == "remove multi local A\n",
            "an ineligible mixed Revert does not change any selected bytes");

        await SelectWorkingCopyEntries(entries, grid, removeA, removeB);
        var removeAPath = Path.Combine(workingCopy, "remove-multi-a.txt");
        var removeBPath = Path.Combine(workingCopy, "remove-multi-b.txt");
        var removeABytes = File.ReadAllText(removeAPath);
        var removeBBytes = File.ReadAllText(removeBPath);
        var removeConfirmation = new TaskCompletionSource<bool>();
        var removeTask = entries.RemoveAsync(message =>
        {
            Check(message.Contains("remove-multi-a.txt", StringComparison.Ordinal) &&
                message.Contains("remove-multi-b.txt", StringComparison.Ordinal),
                "multi-path Remove confirmation lists every selected target");
            return removeConfirmation.Task;
        });
        Check(entries.IsBusy && !selectedActions.IsEnabled && !entries.CanRefresh,
            "multi-path confirmation disables the action menu and refresh while pending");
        removeConfirmation.SetResult(true);
        await removeTask;
        await WaitForWorkingCopy(entries);
        Check(entries.SelectedEntries.Count == 2 && entries.CanUndo &&
            entries.SelectedEntries.All(entry => entry.Entry.Item == "deleted") &&
            File.ReadAllText(removeAPath) == removeABytes && File.ReadAllText(removeBPath) == removeBBytes,
            "multi-path Remove keeps both local files and exposes Undo for the complete selection");
        await entries.UndoAsync();
        await WaitForWorkingCopy(entries);
        Check(entries.SelectedEntries.Count == 2 && entries.SelectedEntries.All(entry => entry.Entry.Item == "modified") &&
            File.ReadAllText(removeAPath) == removeABytes && File.ReadAllText(removeBPath) == removeBBytes,
            "multi-path Undo restores scheduling state while retaining both edited files");

        var revertContentPath = Path.Combine(workingCopy, "revert-content.txt");
        var propertyPath = Path.Combine(workingCopy, "property-only.txt");
        var localContent = File.ReadAllText(revertContentPath);
        var unversionedPath = Path.Combine(workingCopy, "unversioned.txt");
        var unversionedBytes = File.ReadAllText(unversionedPath);
        await SelectWorkingCopyEntries(entries, grid,
            FindWorkingCopyEntry(entries, "revert-content.txt"),
            FindWorkingCopyEntry(entries, "property-only.txt"),
            FindWorkingCopyEntry(entries, "missing.txt"));
        Check(entries.CanRevert && !entries.CanAdd && !entries.CanRemove && !entries.CanUndo,
            "content, property, and missing paths qualify together for Revert");

        var rejectTask = entries.RevertAsync(message =>
        {
            Check(message.Contains("revert-content.txt", StringComparison.Ordinal) &&
                message.Contains("property-only.txt", StringComparison.Ordinal) &&
                message.Contains("missing.txt", StringComparison.Ordinal),
                "Revert confirmation names each selected target");
            return SourceGit.Views.SvnOperationDialog.ShowConfirmationAsync(
                App.Text("SVN.WorkingCopy.RevertTitle"), App.Text("SVN.WorkingCopy.RevertTitle"), message, window);
        });
        var rejectDialog = await WaitForOperationDialog(window);
        Check(entries.IsBusy && !selectedActions.IsEnabled && FindNamedControl<Button>(rejectDialog, "AcceptButton").IsEnabled,
            "Revert confirmation is bounded in a modal while selected actions are busy");
        rejectDialog.Width = 500;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(rejectDialog, theme,
                Path.Combine(screenshotDirectory, "working-copy-revert-confirm-" + theme + ".png"),
                "bounded multi-path Revert confirmation");
        FindNamedControl<Button>(rejectDialog, "CancelButton")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await rejectTask;
        Check(!entries.IsBusy && !entries.HasActionError && entries.CanRevert &&
            File.ReadAllText(revertContentPath) == localContent && !File.Exists(missingPath) &&
            File.ReadAllText(unversionedPath) == unversionedBytes,
            "declining multi-path Revert preserves edited, missing, and unversioned paths");

        var acceptTask = entries.RevertAsync(message => SourceGit.Views.SvnOperationDialog.ShowConfirmationAsync(
            App.Text("SVN.WorkingCopy.RevertTitle"), App.Text("SVN.WorkingCopy.RevertTitle"), message, window));
        var acceptDialog = await WaitForOperationDialog(window);
        FindNamedControl<Button>(acceptDialog, "AcceptButton")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await acceptTask;
        await WaitForWorkingCopy(entries);
        Check(!entries.HasActionError && File.ReadAllText(revertContentPath) == "base line\n" &&
            File.Exists(missingPath) && File.ReadAllText(missingPath) == "base line\n" &&
            File.ReadAllText(unversionedPath) == unversionedBytes,
            "accepted multi-path Revert restores BASE content and missing files while preserving unversioned files");
        var properties = await RunCapture("svn", "proplist", "--verbose", "--xml", "--config-dir", config, "--", propertyPath + "@");
        Check(!properties.Contains("fixture:label", StringComparison.Ordinal),
            "accepted Revert removes the local-only property change");

        // Earlier UI checks intentionally created a conflict, copy/move ancestry, and scheduled
        // changes. Reset only this disposable working copy before root Update and Commit so those
        // workflows start from a deterministic clean base; the unversioned test files remain local.
        await Run("svn", "revert", "--config-dir", config, "--depth", "infinity", "--", workingCopy + "@");
        await entries.RefreshAsync();
        await WaitForWorkingCopy(entries);
        Check(!entries.Entries.Any(entry => entry.HasWarning ||
                entry.Entry.Item is "added" or "deleted" or "modified" or "missing" or "replaced" or "conflicted" ||
                entry.Entry.Properties == "modified" || entry.Entry.IsCopied ||
                !string.IsNullOrEmpty(entry.Entry.MovedFrom) || !string.IsNullOrEmpty(entry.Entry.MovedTo)) &&
            File.ReadAllText(unversionedPath) == unversionedBytes,
            "Update and Commit fixture is clean while keeping unversioned files");

        var updateTargetPath = Path.Combine(workingCopy, "update-target.txt");
        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "unversioned.txt"));
        var updateTask = SourceGit.Views.SvnOperationDialog.ShowUpdateAsync(repository, "1", window);
        var updateDialog = await WaitForOperationDialog(window);
        var specificRevision = updateDialog.GetVisualDescendants().OfType<RadioButton>().Single(control => control.Name == "SpecificRevision");
        var revisionInput = updateDialog.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "RevisionInput");
        var updateAccept = updateDialog.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "AcceptButton");
        Check(specificRevision.IsChecked == true && revisionInput.Text == "1" && updateAccept.IsEnabled,
            "specific-revision Update dialog opens with the requested revision");
        revisionInput.Text = "invalid";
        Dispatcher.UIThread.RunJobs();
        await WaitUntil(() => !updateAccept.IsEnabled);
        Check(!updateAccept.IsEnabled, "Update dialog rejects an invalid revision before execution");
        revisionInput.Text = "1";
        Dispatcher.UIThread.RunJobs();
        await WaitUntil(() => updateAccept.IsEnabled);
        updateDialog.Width = 500;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(updateDialog, theme,
                Path.Combine(screenshotDirectory, "working-copy-update-dialog-" + theme + ".png"),
                "specific-revision Update dialog");
        updateAccept.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await updateTask;
        await WaitForWorkingCopy(entries);
        Check(File.ReadAllText(updateTargetPath) == "update revision one\n" && !entries.HasActionError && !repository.HasError,
            "Update to a specific revision changes the local file and refreshes status and history");
        await repository.UpdateWorkingCopyAsync("HEAD");
        await WaitForWorkingCopy(entries);
        Check(File.ReadAllText(updateTargetPath) == "update revision two\n" && !entries.HasActionError,
            "updating back to HEAD restores the newer base without leaving a stale action error");

        var selectedCommitPath = Path.Combine(workingCopy, "commit-selected.txt");
        var uncheckedCommitPath = Path.Combine(workingCopy, "commit-unchecked.txt");
        var staleCommitPath = Path.Combine(workingCopy, "commit-stale.txt");
        File.WriteAllText(selectedCommitPath, "selected local commit\n");
        File.WriteAllText(uncheckedCommitPath, "unchecked local edit\n");
        File.WriteAllText(staleCommitPath, "out-of-band candidate\n");
        await entries.RefreshAsync();
        await WaitForWorkingCopy(entries);
        var selectedCommitEntry = FindWorkingCopyEntry(entries, "commit-selected.txt");
        var uncheckedCommitEntry = FindWorkingCopyEntry(entries, "commit-unchecked.txt");
        var staleCommitEntry = FindWorkingCopyEntry(entries, "commit-stale.txt");
        Check(entries.GetCommitCandidates().Contains(selectedCommitEntry) && entries.GetCommitCandidates().Contains(uncheckedCommitEntry) &&
            entries.GetCommitCandidates().Contains(staleCommitEntry),
            "Commit dialog candidates include each independently changed path");
        await SelectWorkingCopyEntry(entries, grid, selectedCommitEntry);

        var cancelledCommitTask = SourceGit.Views.SvnOperationDialog.ShowCommitAsync(repository, window);
        var cancelledCommitDialog = await WaitForOperationDialog(window);
        var cancelledMessage = cancelledCommitDialog.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "CommitMessage");
        var cancelledAccept = FindNamedControl<Button>(cancelledCommitDialog, "AcceptButton");
        Check(!cancelledAccept.IsEnabled, "Commit starts disabled until its message is entered");
        cancelledMessage.Text = "cancelled commit draft";
        Dispatcher.UIThread.RunJobs();
        await WaitUntil(() => cancelledAccept.IsEnabled);
        FindNamedControl<Button>(cancelledCommitDialog, "CancelButton")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await cancelledCommitTask;
        Check(repository.CommitMessageDraft == "cancelled commit draft",
            "cancelling Commit retains the typed message as a retry draft");

        var staleCommitTask = SourceGit.Views.SvnOperationDialog.ShowCommitAsync(repository, window);
        var staleCommitDialog = await WaitForOperationDialog(window);
        var staleChecks = staleCommitDialog.GetVisualDescendants().OfType<CheckBox>().ToArray();
        var staleSelectedCheck = staleChecks.Single(check =>
            Avalonia.Automation.AutomationProperties.GetName(check).StartsWith("commit-selected.txt,", StringComparison.Ordinal));
        var staleCandidateCheck = staleChecks.Single(check =>
            Avalonia.Automation.AutomationProperties.GetName(check).StartsWith("commit-stale.txt,", StringComparison.Ordinal));
        var staleUncheckedCheck = staleChecks.Single(check =>
            Avalonia.Automation.AutomationProperties.GetName(check).StartsWith("commit-unchecked.txt,", StringComparison.Ordinal));
        var staleMessage = staleCommitDialog.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "CommitMessage");
        var staleAccept = FindNamedControl<Button>(staleCommitDialog, "AcceptButton");
        Dispatcher.UIThread.RunJobs();
        await WaitUntil(() => staleAccept.IsEnabled);
        Check(staleMessage.Text == "cancelled commit draft" && staleSelectedCheck.IsChecked == true,
            "reopening Commit restores the draft and selected path");
        staleCandidateCheck.IsChecked = true;
        staleUncheckedCheck.IsChecked = false;
        staleMessage.Text = "stale candidate retry";
        Dispatcher.UIThread.RunJobs();
        await WaitUntil(() => staleAccept.IsEnabled);
        await Run("svn", "commit", "--config-dir", config, "-m", "out-of-band commit", "--", staleCommitPath);
        Dispatcher.UIThread.RunJobs();
        staleAccept.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await staleCommitTask;
        await WaitForWorkingCopy(entries);
        Check(entries.HasActionError && repository.CommitMessageDraft == "stale candidate retry" &&
            File.ReadAllText(selectedCommitPath) == "selected local commit\n",
            "a stale selected Commit candidate fails safely and preserves the message for retry");

        var commitTask = SourceGit.Views.SvnOperationDialog.ShowCommitAsync(repository, window);
        var commitDialog = await WaitForOperationDialog(window);
        var commitChecks = commitDialog.GetVisualDescendants().OfType<CheckBox>().ToArray();
        var selectedCheck = commitChecks.Single(check =>
            Avalonia.Automation.AutomationProperties.GetName(check).StartsWith("commit-selected.txt,", StringComparison.Ordinal));
        var uncheckedCheck = commitChecks.Single(check =>
            Avalonia.Automation.AutomationProperties.GetName(check).StartsWith("commit-unchecked.txt,", StringComparison.Ordinal));
        var commitMessage = commitDialog.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "CommitMessage");
        var commitAccept = commitDialog.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "AcceptButton");
        Dispatcher.UIThread.RunJobs();
        await WaitUntil(() => commitAccept.IsEnabled);
        Check(selectedCheck.IsChecked == true && uncheckedCheck.IsChecked != true &&
            commitMessage.Text == "stale candidate retry" && commitAccept.IsEnabled,
            "Commit retry preselects eligible paths, restores the draft, and permits a valid message");
        selectedCheck.IsChecked = false;
        selectedCheck.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        await WaitUntil(() => commitAccept.IsEnabled);
        Check(commitAccept.IsEnabled, "Commit dialog enables submission for a selected path and message");
        commitDialog.Width = 500;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(commitDialog, theme,
                Path.Combine(screenshotDirectory, "working-copy-commit-dialog-" + theme + ".png"),
                "selective Commit dialog");
        commitAccept.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await commitTask;
        await WaitForWorkingCopy(entries);
        var localRepositoryUrl = new Uri(repositoryPath + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        var committedContent = await RunCapture("svn", "cat", "--config-dir", config,
            "--revision", "HEAD", localRepositoryUrl + "/commit-selected.txt");
        await Run("svn", "update", "--config-dir", config, "--revision", "HEAD", "--", remoteCopy);
        Check(committedContent == "selected local commit\n" &&
            File.ReadAllText(Path.Combine(remoteCopy, "commit-selected.txt")) == "selected local commit\n" &&
            File.ReadAllText(Path.Combine(remoteCopy, "commit-unchecked.txt")) == "base line\n" &&
            File.ReadAllText(Path.Combine(remoteCopy, "commit-stale.txt")) == "out-of-band candidate\n" &&
            File.ReadAllText(uncheckedCommitPath) == "unchecked local edit\n" && !entries.HasActionError && !repository.HasError,
            "selected Commit reaches the repository while preserving the unchecked local edit");
        var latestRevision = repository.Revisions.Max(revision => revision.Number);
        var latestCommit = repository.Revisions.Single(revision => revision.Number == latestRevision);
        Check(latestRevision >= 4 && latestCommit.Message == "stale candidate retry" &&
            string.IsNullOrEmpty(repository.CommitMessageDraft),
            $"successful Commit refreshes history and clears the retry draft; Latest={latestRevision}; " +
            $"Message={latestCommit.Message}; Draft={repository.CommitMessageDraft}; Selected={repository.SelectedRevision?.Number}");

        window.Width = 800;
        window.Height = 540;
        Dispatcher.UIThread.RunJobs();
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(window, theme,
                Path.Combine(screenshotDirectory, "working-copy-narrow-" + theme + ".png"),
                "working-copy screen at a narrow window size");
        window.Width = 1024;
        window.Height = 600;
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<SvnOperationDialog> WaitForOperationDialog(Window owner)
    {
        await WaitUntil(() => owner.OwnedWindows.OfType<SvnOperationDialog>().Any());
        return owner.OwnedWindows.OfType<SvnOperationDialog>().Single();
    }

    private static T FindNamedControl<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void ScrollGridToTop(Window window, DataGrid grid)
    {
        window.UpdateLayout();
        grid.UpdateLayout();
        var vertical = grid.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(scrollBar => scrollBar.Orientation == Avalonia.Layout.Orientation.Vertical);
        if (vertical != null)
            vertical.Value = vertical.Minimum;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        grid.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
    }

    private static async Task SelectWorkingCopyEntries(SvnWorkingCopy workingCopy, DataGrid grid,
        params SvnWorkingCopyEntry[] entries)
    {
        grid.SelectedItems.Clear();
        foreach (var entry in entries)
            grid.SelectedItems.Add(entry);
        Dispatcher.UIThread.RunJobs();
        Check(grid.SelectedItems.Count == entries.Length && workingCopy.SelectedEntries.Count == entries.Length,
            "extended working-copy selection binding: " + string.Join(", ", entries.Select(entry => entry.Path)));
        if (entries.Length > 0)
            Check(entries.Any(entry => ReferenceEquals(workingCopy.SelectedEntry, entry)),
                "primary row remains inside the extended selection");
        await WaitForLocalDiff(workingCopy);
    }

    private static Task RightClickWorkingCopyRow(Window window, DataGrid grid, SvnWorkingCopyEntry entry) =>
        RightClickRow(window, grid, entry);

    private static async Task RightClickRow(Window window, DataGrid grid, object item)
    {
        Check(grid.IsEffectivelyVisible && grid.IsEffectivelyEnabled,
            $"context grid is active before pointer input; Name={grid.Name}; Bounds={grid.Bounds}; " +
            $"Visible={grid.IsVisible}/{grid.IsEffectivelyVisible}; Enabled={grid.IsEnabled}/{grid.IsEffectivelyEnabled}");
        window.UpdateLayout();
        grid.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        window.UpdateLayout();
        grid.UpdateLayout();
        await WaitUntil(() => grid.GetVisualDescendants().OfType<DataGridRow>()
            .Any(candidate => ReferenceEquals(candidate.DataContext, item) && candidate.Bounds.Width > 0 && candidate.Bounds.Height > 0));
        window.UpdateLayout();
        grid.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var row = grid.GetVisualDescendants().OfType<DataGridRow>().Single(candidate => ReferenceEquals(candidate.DataContext, item));
        Point? point = null;
        Point? lastPoint = null;
        Visual lastHit = null;
        DataGridRow lastHitRow = null;
        for (var y = 3.0; y < row.Bounds.Height - 3 && point == null; y += 4)
        {
            for (var x = 6.0; x < row.Bounds.Width - 6; x += 8)
            {
                var candidate = row.TranslatePoint(new Point(x, y), window);
                if (candidate == null)
                    continue;
                var candidateHit = window.InputHitTest(candidate.Value) as Visual;
                var candidateRow = candidateHit?.FindAncestorOfType<DataGridRow>(true);
                lastPoint = candidate;
                lastHit = candidateHit;
                lastHitRow = candidateRow;
                if (ReferenceEquals(candidateRow?.DataContext, item))
                {
                    point = candidate;
                    break;
                }
            }
        }
        if (!point.HasValue)
        {
            var artifactDirectory = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_ARTIFACTS") ??
                Path.Combine(Path.GetTempPath(), "sourcegit-svn-ui-artifacts");
            Directory.CreateDirectory(artifactDirectory);
            using var missFrame = window.CaptureRenderedFrame();
            missFrame?.Save(Path.Combine(artifactDirectory, "rightclick-hit-miss-" + grid.Name + ".png"));
        }
        Check(point.HasValue,
            $"visible pointer target resolves to the intended row; RowOrigin={row.TranslatePoint(new Point(0, 0), window)}; " +
            $"RowBounds={row.Bounds}; GridBounds={grid.Bounds}; GridEnabled={grid.IsEnabled}/{grid.IsEffectivelyEnabled}; " +
            $"GridVisible={grid.IsVisible}/{grid.IsEffectivelyVisible}; " +
            $"Window={window.IsVisible}/{window.IsEnabled}/{window.ClientSize}; LastPoint={lastPoint}; " +
            $"LastHit={lastHit?.GetType().Name}; LastHitRow={lastHitRow?.DataContext}; Expected={item}; " +
            $"ScrollOffsets=[{string.Join(", ", grid.GetVisualDescendants().OfType<ScrollViewer>().Select(view => view.Offset.ToString()))}]");
        window.MouseMove(point.Value, RawInputModifiers.None);
        window.MouseDown(point.Value, MouseButton.Right, RawInputModifiers.None);
        window.MouseUp(point.Value, MouseButton.Right, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.Escape, RawInputModifiers.None);
        window.KeyRelease(Key.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static SvnWorkingCopyEntry FindWorkingCopyEntry(SvnWorkingCopy workingCopy, string fileName) =>
        workingCopy.Entries.Single(entry => entry.Path.Replace('\\', '/').Split('/').Last() == fileName);

    private static async Task SelectWorkingCopyEntry(SvnWorkingCopy workingCopy, DataGrid grid, SvnWorkingCopyEntry entry, bool waitForDiff = true)
    {
        grid.SelectedItem = entry;
        Dispatcher.UIThread.RunJobs();
        Check(ReferenceEquals(workingCopy.SelectedEntry, entry), "working-copy row selection binding: " + entry.Path);
        if (waitForDiff)
            await WaitForLocalDiff(workingCopy);
    }

    private static async Task SaveSettledScreenshot(Window window, ThemeVariant theme, string path, string description)
    {
        Application.Current.RequestedThemeVariant = theme;
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        using (var warmup = window.CaptureRenderedFrame())
            Check(warmup != null, "warm render " + description + " " + theme);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Check(window.ActualThemeVariant == theme, "actual theme applied for " + description + " " + theme);
        using var frame = window.CaptureRenderedFrame();
        Check(frame != null, "render " + description + " " + theme);
        frame.Save(path);
    }

    private static string DescribeSharedDiffMode(SvnRepository repository)
    {
        var workingCopy = repository.WorkingCopy;
        return $"UseSideBySideDiff={Preferences.Instance.UseSideBySideDiff}; " +
            $"HistoryDiff={repository.DiffContent?.GetType().Name ?? "null"}; " +
            $"LocalDiff={workingCopy.DiffContent?.GetType().Name ?? "null"}; " +
            $"LocalLoading={workingCopy.IsLoading}; LocalDiffLoading={workingCopy.IsLoadingDiff}; " +
            $"LocalHasDiffError={workingCopy.HasDiffError}; LocalSelection={workingCopy.SelectedEntry?.Path ?? "<none>"}; " +
            $"LocalDiffNotice={workingCopy.DiffNotice}; LocalDiffErrorDetails={workingCopy.DiffErrorDetails}";
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
        s_checks++;
    }

    private static async Task VerifySnapshotDiffs(string temporary)
    {
        var submoduleBefore = Encoding.UTF8.GetBytes("Subproject commit 1111111111111111111111111111111111111111\n");
        var submoduleAfter = Encoding.UTF8.GetBytes("Subproject commit 2222222222222222222222222222222222222222\n");
        var submodule = await SourceGit.Commands.SnapshotDiff.ReadAsync(submoduleBefore, submoduleAfter);
        Check(submodule.Status == "Ready" && !submodule.IsBinary && submodule.Diff != null,
            "snapshot diff keeps submodule-looking text as text");
        Check(submodule.Diff.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Deleted && line.Content.StartsWith("Subproject commit", StringComparison.Ordinal)) &&
            submodule.Diff.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Added && line.Content.StartsWith("Subproject commit", StringComparison.Ordinal)),
            "snapshot diff parses submodule-looking text lines");

        var lfsBefore = Encoding.UTF8.GetBytes("version https://git-lfs.github.com/spec/v1\noid sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nsize 11\n");
        var lfsAfter = Encoding.UTF8.GetBytes("version https://git-lfs.github.com/spec/v1\noid sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\nsize 22\n");
        var lfs = await SourceGit.Commands.SnapshotDiff.ReadAsync(lfsBefore, lfsAfter);
        Check(lfs.Status == "Ready" && !lfs.IsBinary && lfs.Diff != null,
            "snapshot diff keeps Git LFS pointer-looking text as text");
        Check(lfs.Diff.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Deleted && line.Content.StartsWith("oid sha256:", StringComparison.Ordinal)) &&
            lfs.Diff.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Added && line.Content.StartsWith("oid sha256:", StringComparison.Ordinal)),
            "snapshot diff parses LFS pointer content lines");

        var large = Encoding.UTF8.GetBytes(new string('x', 2 * 1024 * 1024 + 1));
        var tooLarge = await SourceGit.Commands.SnapshotDiff.ReadAsync(Array.Empty<byte>(), large);
        Check(tooLarge.Status == "TooLarge", "snapshot diff enforces its text-size limit");

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("UTF16 before\n")).ToArray();
        var utf16After = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("UTF16 after\n")).ToArray();
        var unicodeDiff = await SourceGit.Commands.SnapshotDiff.ReadAsync(utf16, utf16After);
        Check(unicodeDiff.Status == "Ready" && !unicodeDiff.IsBinary && unicodeDiff.Diff?.AddedLines == 1,
            "UTF16 snapshots render as text");

        var inherited = new System.Collections.Generic.Dictionary<string, string>
        {
            ["GIT_DIR"] = Path.Combine(temporary, "missing.git"),
            ["GIT_INDEX_FILE"] = Path.Combine(temporary, "untouched.index"),
            ["GIT_EXTERNAL_DIFF"] = Path.Combine(temporary, "must-not-execute"),
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "diff.external",
            ["GIT_CONFIG_VALUE_0"] = Path.Combine(temporary, "must-not-execute"),
        };
        var originalEnvironment = inherited.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var entry in inherited)
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            var isolated = await SourceGit.Commands.SnapshotDiff.ReadAsync(Encoding.UTF8.GetBytes("before\n"), Encoding.UTF8.GetBytes("after\n"));
            Check(isolated.Status == "Ready" && isolated.Diff != null && !File.Exists(inherited["GIT_INDEX_FILE"]),
                "snapshot diff isolates repository and external-driver environment");
        }
        finally
        {
            foreach (var entry in originalEnvironment)
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }

    private static async Task VerifyOrdinaryGitDiff(string temporary)
    {
        var repository = Path.Combine(temporary, "git-diff-fixture");
        Directory.CreateDirectory(repository);
        var file = Path.Combine(repository, "tracked.txt");
        await Run("git", "-C", repository, "init");
        await Run("git", "-C", repository, "config", "user.name", "Fixture");
        await Run("git", "-C", repository, "config", "user.email", "fixture@example.test");
        File.WriteAllText(file, "before from Git\n");
        await Run("git", "-C", repository, "add", "--", file);
        await Run("git", "-C", repository, "commit", "-m", "diff fixture");
        File.WriteAllText(file, "after from Git\n");

        var change = new SourceGit.Models.Change { Path = "tracked.txt", WorkTree = SourceGit.Models.ChangeState.Modified };
        var result = await new SourceGit.Commands.Diff(repository,
            new SourceGit.Models.DiffOption(change, true), 3, false, false).ReadAsync();
        Check(!result.IsBinary && result.TextDiff != null, "ordinary Git diff still returns parsed text");
        Check(result.TextDiff.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Deleted && line.Content.Contains("before from Git", StringComparison.Ordinal)) &&
            result.TextDiff.Lines.Any(line => line.Type == SourceGit.Models.TextDiffLineType.Added && line.Content.Contains("after from Git", StringComparison.Ordinal)),
            "ordinary Git diff retains before and after lines");
    }

    private static async Task VerifyMetadataCaching(string workingCopy, string calls)
    {
        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_CACHE_UUID", Guid.NewGuid().ToString());
        Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_AUTH_HISTORY", "0");
        try
        {
            using (var first = new SvnRepository(workingCopy))
            {
                await first.InitializeAsync();
                await WaitForPaths(first);
                Check(!first.HasError && !first.HasPathsError, "cache fixture initially loads from SVN");
                await first.LoadMoreAsync();
                await WaitForPaths(first);
                first.SelectedRevision = first.Revisions[1];
                await WaitForPaths(first);
                var beforeRevisit = MetadataCallCount(calls);
                first.SelectedRevision = first.Revisions[0];
                await WaitForPaths(first);
                Check(!first.HasPathsError && MetadataCallCount(calls) == beforeRevisit, "revisiting a revision reuses cached changed paths");
            }

            var beforeReopen = MetadataCallCount(calls);
            using (var reopened = new SvnRepository(workingCopy))
            {
                await reopened.InitializeAsync();
                await WaitForPaths(reopened);
                await reopened.LoadMoreAsync();
                await WaitForPaths(reopened);
                Check(!reopened.HasError && !reopened.HasPathsError && reopened.Revisions.Count == 101 &&
                    MetadataCallCount(calls) == beforeReopen, "reopening reuses persisted history pages and changed paths");

                Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_MESSAGE_SUFFIX", " edited");
                await reopened.RefreshAsync();
                await WaitForPaths(reopened);
                Check(!reopened.HasError && !reopened.HasPathsError && reopened.SelectedMessage.EndsWith(" edited", StringComparison.Ordinal) &&
                    MetadataCallCount(calls) == beforeReopen + 2, "explicit Refresh refetches history and selected paths despite cache");
            }

            var afterRefresh = MetadataCallCount(calls);
            using var refreshed = new SvnRepository(workingCopy);
            await refreshed.InitializeAsync();
            await WaitForPaths(refreshed);
            Check(refreshed.SelectedMessage.EndsWith(" edited", StringComparison.Ordinal) && MetadataCallCount(calls) == afterRefresh,
                "refreshed commit messages persist across view models");
            Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_AUTH_HISTORY", "1");
            await refreshed.RefreshAsync();
            Check(refreshed.HasError && refreshed.NeedsCredentials, "Refresh reports authentication failure instead of hiding it behind cache");
            var afterFailedRefresh = MetadataCallCount(calls);
            Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_AUTH_HISTORY", "0");
            using (var recovered = new SvnRepository(workingCopy))
            {
                await recovered.InitializeAsync();
                await WaitForPaths(recovered);
                Check(!recovered.HasError && !recovered.HasPathsError && MetadataCallCount(calls) == afterFailedRefresh + 2,
                    "reopening after failed Refresh refetches both history and selected paths");
            }
            Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_AUTH_HISTORY", "1");
            await refreshed.RefreshAsync();
            refreshed.Username = "cache-fixture";
            refreshed.Password = "ui-fixture-password";
            await refreshed.SignInAsync();
            await WaitForPaths(refreshed);
            Check(!refreshed.HasError && !refreshed.HasPathsError, "explicit sign-in populates its separate metadata cache");

            using var signedOut = new SvnRepository(workingCopy);
            await signedOut.InitializeAsync();
            Check(signedOut.NeedsCredentials, "native authentication does not reuse explicit-user cached metadata");
            signedOut.Username = "cache-fixture";
            signedOut.Password = "incorrect-password";
            await signedOut.SignInAsync();
            Check(signedOut.HasError && signedOut.NeedsCredentials, "explicit sign-in validates credentials despite a cached result for the username");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_CACHE_UUID", null);
            Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_AUTH_HISTORY", null);
            Environment.SetEnvironmentVariable("SOURCEGIT_SVN_UI_MESSAGE_SUFFIX", null);
            File.WriteAllText(calls, string.Empty);
        }
    }

    private static int MetadataCallCount(string calls) => File.ReadAllLines(calls)
        .Count(line => line.StartsWith("history ", StringComparison.Ordinal) || line.StartsWith("paths ", StringComparison.Ordinal));

    private static async Task<int> RunFakeSvn(string[] args)
    {
        var root = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_ROOT");
        var url = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_URL");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(url))
            return 2;
        if (args[0] == "status")
        {
            File.AppendAllText(Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_CALLS"), "status\n");
            if (Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_STATUS_ENTRIES") == "1")
            {
                var offlineA = SecurityElement.Escape(Path.Combine(root, "offline-a.txt"));
                var offlineB = SecurityElement.Escape(Path.Combine(root, "offline-b.txt"));
                Console.WriteLine($"<status><target path=\"{SecurityElement.Escape(root)}\">" +
                    $"<entry path=\"{offlineA}\"><wc-status item=\"modified\" props=\"none\" revision=\"104\"/></entry>" +
                    $"<entry path=\"{offlineB}\"><wc-status item=\"modified\" props=\"none\" revision=\"104\"/></entry></target></status>");
            }
            else
            {
                Console.WriteLine($"<status><target path=\"{SecurityElement.Escape(root)}\" /></status>");
            }
            return 0;
        }

        if (args[0] == "diff")
        {
            var target = GetLocalCommandTarget(args);
            File.AppendAllText(Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_CALLS"), $"localdiff {target}\n");
            await DelayLocalDiffResponse(target);
            var fileName = Path.GetFileName(target.Replace('\\', '/'));
            var body = fileName == "offline-a.txt" ? "offline baseline A" : "offline baseline B";
            var patch = $"Index: {target}\n===================================================================\n--- {target}\t(revision 104)\n+++ {target}\t(working copy)\n@@ -1 +1 @@\n-{body}\n+updated {body}\n";
            await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes(patch));
            return 0;
        }

        if (args[0] == "propget")
            return 0;

        if (args[0] == "info")
        {
            var uuid = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_CACHE_UUID");
            var uuidXml = string.IsNullOrEmpty(uuid) ? string.Empty : $"<uuid>{SecurityElement.Escape(uuid)}</uuid>";
            if (args.Contains("--revision", StringComparer.Ordinal))
            {
                var password = args.Contains("--password-from-stdin", StringComparer.Ordinal)
                    ? await Console.In.ReadLineAsync()
                    : null;
                if (Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_AUTH") == "1" && !CheckFakeCredentials(args, password, url))
                    return 1;

                var revision = GetRequestedRevision(args);
                await DelayPathResponse(revision);
                Console.WriteLine($"<info><entry revision=\"{revision}\"><url>{SecurityElement.Escape(url)}</url><repository><root>svn://fixture.test</root></repository></entry></info>");
                return 0;
            }

            Console.WriteLine($"<info><entry revision=\"104\"><url>{SecurityElement.Escape(url)}</url><repository><root>svn://fixture.test</root>{uuidXml}</repository><wc-info><wcroot-abspath>{SecurityElement.Escape(root)}</wcroot-abspath></wc-info></entry></info>");
            return 0;
        }

        if (args[0] is "proplist" or "cat")
        {
            var command = args[0];
            var revision = GetRequestedRevision(args);
            var target = GetCommandTarget(args);
            File.AppendAllText(Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_CALLS"), $"{command} {revision} {target}\n");
            await DelayDiffResponse(revision);
            var filePasswordFromStdin = args.Contains("--password-from-stdin", StringComparer.Ordinal)
                ? await Console.In.ReadLineAsync()
                : null;
            if (Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_AUTH") == "1" &&
                !CheckFakeCredentials(args, filePasswordFromStdin, url))
                return 1;

            if (command == "proplist")
            {
                var mimeType = target.Contains("binary.dat", StringComparison.OrdinalIgnoreCase)
                    ? "application/octet-stream"
                    : null;
                var property = mimeType == null
                    ? string.Empty
                    : $"<property name=\"svn:mime-type\">{mimeType}</property>";
                Console.WriteLine($"<properties><target path=\"{SecurityElement.Escape(target)}\">{property}</target></properties>");
                return 0;
            }

            var content = GetFakeFileContent(target, revision);
            var standardOutput = Console.OpenStandardOutput();
            await standardOutput.WriteAsync(content);
            await standardOutput.FlushAsync();
            return 0;
        }

        var isPathsQuery = args.Contains("--verbose", StringComparer.Ordinal);
        File.AppendAllText(Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_CALLS"),
            (isPathsQuery ? "paths " : "history ") + (args.Contains("--username", StringComparer.Ordinal) ? "authenticated\n" : "anonymous\n"));
        var historyNeedsAuth = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_AUTH_HISTORY") != "0";
        var pathsNeedAuth = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_AUTH") == "1";
        var passwordFromStdin = args.Contains("--password-from-stdin", StringComparer.Ordinal)
            ? await Console.In.ReadLineAsync()
            : null;
        if (!args.Contains("--no-auth-cache", StringComparer.Ordinal))
            return 3;
        if ((isPathsQuery && pathsNeedAuth) || (!isPathsQuery && historyNeedsAuth))
        {
            if (!CheckFakeCredentials(args, passwordFromStdin, url))
                return 1;
        }

        if (isPathsQuery)
        {
            var pathRevision = GetRequestedRevision(args);
            await DelayPathResponse(pathRevision);
            Console.WriteLine(CreateFakeChangedPathsXml(pathRevision));
            return 0;
        }

        var range = args[Array.IndexOf(args, "--revision") + 1];
        var messageSuffix = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_MESSAGE_SUFFIX");
        Console.WriteLine("<log>");
        foreach (var revision in range == "HEAD:0" ? Enumerable.Range(5, 100).Reverse() : new[] { 1 })
            Console.WriteLine($"<logentry revision=\"{revision}\"><author>fixture</author><msg>Revision {revision}{SecurityElement.Escape(messageSuffix)}</msg></logentry>");
        Console.WriteLine("</log>");
        return 0;
    }

    private static bool CheckFakeCredentials(string[] args, string password, string url)
    {
        if (!args.Contains("--username", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("svn: E170001: Authentication required");
            return false;
        }

        if (password != "ui-fixture-password" || url != "svn://fixture.test/trunk")
        {
            Console.Error.WriteLine($"svn: E170001: Authentication failed {password}");
            return false;
        }

        return true;
    }

    private static long GetRequestedRevision(string[] args)
    {
        var revisionIndex = Array.IndexOf(args, "--revision");
        if (revisionIndex < 0 || revisionIndex + 1 >= args.Length)
            return 0;

        var range = args[revisionIndex + 1];
        var separator = range.IndexOf(':');
        var value = separator >= 0 ? range[..separator] : range;
        return long.TryParse(value, out var revision) ? revision : 0;
    }

    private static string GetCommandTarget(string[] args)
    {
        var target = args[^1];
        var peg = target.LastIndexOf('@');
        return peg >= 0 ? target[..peg] : target;
    }

    private static string GetLocalCommandTarget(string[] args)
    {
        var target = args[^1];
        return target.EndsWith('@') ? target[..^1] : target;
    }

    private static byte[] GetFakeFileContent(string target, long revision)
    {
        var path = Uri.UnescapeDataString(target);
        if (path.Contains("old name.txt", StringComparison.Ordinal))
            return System.Text.Encoding.UTF8.GetBytes("Copied baseline at revision 103\nline baseline\n");
        if (path.Contains("长文件名", StringComparison.Ordinal))
            return System.Text.Encoding.UTF8.GetBytes("Copied target at revision 104\nline updated\n");
        if (path.Contains("empty.txt", StringComparison.Ordinal))
            return Array.Empty<byte>();
        if (path.Contains("binary.dat", StringComparison.Ordinal))
            return revision == 100 ? [0x00, 0x01, 0x02, 0x03] : [0x00, 0x01, 0x02];
        if (path.Contains("same.txt", StringComparison.Ordinal))
            return System.Text.Encoding.UTF8.GetBytes("unchanged across these revisions\n");
        if (path.Contains("large.txt", StringComparison.Ordinal))
        {
            var content = new byte[2 * 1024 * 1024 + 1];
            Array.Fill(content, (byte)'x');
            return content;
        }

        return System.Text.Encoding.UTF8.GetBytes($"Revision {revision}\n");
    }

    private static async Task DelayPathResponse(long revision)
    {
        if (!long.TryParse(Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_DELAY_REVISION"), out var delayedRevision) ||
            delayedRevision != revision)
            return;

        var marker = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_PATH_DELAY_STARTED");
        if (!string.IsNullOrEmpty(marker))
            File.WriteAllText(marker, revision.ToString());
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    private static async Task DelayDiffResponse(long revision)
    {
        if (!long.TryParse(Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_DELAY_REVISION"), out var delayedRevision) ||
            delayedRevision != revision)
            return;

        var marker = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_DIFF_DELAY_STARTED");
        if (!string.IsNullOrEmpty(marker))
            File.WriteAllText(marker, revision.ToString());
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    private static async Task DelayLocalDiffResponse(string target)
    {
        var delayedPath = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_LOCAL_DIFF_DELAY_PATH");
        if (string.IsNullOrEmpty(delayedPath) || !target.Contains(delayedPath, StringComparison.Ordinal))
            return;

        var marker = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_LOCAL_DIFF_DELAY_STARTED");
        if (!string.IsNullOrEmpty(marker))
            File.WriteAllText(marker, target);
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    private static string CreateFakeChangedPathsXml(long revision)
    {
        if (revision == 104)
        {
            const string longDirectory = "/trunk/目录/非常深的子目录名称/用于验证窄窗口文字裁切和工具提示行为的长路径/";
            var longFile = longDirectory + "由历史修订复制而来的长文件名-保持Unicode-源文件.txt";
            return "<log><logentry revision=\"104\"><paths>" +
                "<path action=\"A\" kind=\"dir\">/trunk/目录/非常深的子目录名称</path>" +
                $"<path action=\"A\" kind=\"file\" copyfrom-path=\"/archive/old name.txt\" copyfrom-rev=\"103\">{SecurityElement.Escape(longFile)}</path>" +
                "</paths></logentry></log>";
        }

        if (revision == 102)
            return "<log><logentry revision=\"102\"><paths><path action=\"A\" kind=\"file\" copyfrom-path=\"/source.txt\" copyfrom-rev=\"101\">/trunk/revision-102.txt</path></paths></logentry></log>";

        if (revision == 5)
            return "<log><logentry revision=\"5\"><paths /></logentry></log>";

        if (revision == 101)
            return "<log><logentry revision=\"101\"><paths><path action=\"A\" kind=\"file\">/trunk/empty.txt</path></paths></logentry></log>";

        if (revision == 100)
            return "<log><logentry revision=\"100\"><paths><path action=\"M\" kind=\"file\">/trunk/binary.dat</path></paths></logentry></log>";

        if (revision == 99)
            return "<log><logentry revision=\"99\"><paths><path action=\"A\" kind=\"dir\">/trunk/new-directory</path><path action=\"A\" kind=\"file\">/trunk/binary.dat</path></paths></logentry></log>";

        if (revision == 97)
            return "<log><logentry revision=\"97\"><paths><path action=\"M\" kind=\"file\">/trunk/same.txt</path></paths></logentry></log>";

        if (revision == 96)
            return "<log><logentry revision=\"96\"><paths><path action=\"A\" kind=\"file\">/trunk/large.txt</path></paths></logentry></log>";

        return $"<log><logentry revision=\"{revision}\"><paths><path action=\"M\" kind=\"file\">/trunk/revision-{revision}.txt</path></paths></logentry></log>";
    }

    private static int CountHistoryCalls(string path) =>
        File.ReadAllLines(path).Count(line => line.StartsWith("history ", StringComparison.Ordinal));

    private static int CountStatusCalls(string path) =>
        File.ReadAllLines(path).Count(line => line == "status");

    private static Task WaitForPaths(SvnRepository repository) => WaitUntil(() => !repository.IsLoadingPaths);
    private static Task WaitForDiff(SvnRepository repository) => WaitUntil(() => !repository.IsLoadingDiff);
    private static Task WaitForWorkingCopy(SvnWorkingCopy workingCopy) => WaitUntil(() => !workingCopy.IsBusy);
    private static Task WaitForLocalDiff(SvnWorkingCopy workingCopy) => WaitUntil(() => !workingCopy.IsLoadingDiff);

    private static async Task<SourceGit.Native.SvnChangedPath> SelectChangedPath(SvnRepository repository, DataGrid pathsGrid,
        long revision, Func<SourceGit.Native.SvnChangedPath, bool> matches, bool waitForDiff = true)
    {
        repository.SelectedRevision = repository.Revisions.Single(item => item.Number == revision);
        await WaitForPaths(repository);
        Check(repository.SelectedChangedPath == null, "revision change clears the selected file");
        Dispatcher.UIThread.RunJobs();
        var path = repository.ChangedPaths.Single(matches);
        pathsGrid.SelectedItem = path;
        Dispatcher.UIThread.RunJobs();
        if (waitForDiff)
            await WaitForDiff(repository);
        return path;
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

    private static string ResolvePhysicalDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var component in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(current, component));
            current = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }
        return Path.GetFullPath(current);
    }

    private static async Task<string> RunCapture(string executable, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
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
            return output.Result;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(true);
        }
    }
}
