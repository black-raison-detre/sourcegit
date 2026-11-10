using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;

using SourceGit;
using SourceGit.ViewModels;
using SvnOperationDialog = SourceGit.Views.SvnOperationDialog;

internal static partial class Program
{
    private static async Task<int> RunRepairOnly(string temporary, string config)
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(Program));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var screenshots = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_ARTIFACTS") ??
                Path.Combine(Path.GetTempPath(), "sourcegit-svn-ui-artifacts");
            Directory.CreateDirectory(screenshots);
            await session.Dispatch<bool>(async () =>
            {
                Preferences.Instance.SvnInstallPath = SourceGit.Native.Svn.ResolveExecutable(string.Empty);
                Preferences.Instance.GitInstallPath = SourceGit.Native.OS.FindGitExecutable();
                Preferences.Instance.SvnConfigDirectory = config;
                Preferences.Instance.SetCanModify();
                var window = new Window { Width = 1024, Height = 600 };
                window.Show();
                await VerifyRepairUi(window, temporary, config, screenshots);
                window.Close();
                return true;
            }, timeout.Token);
            Check(s_checks >= 20, "focused repair workflow completed its checks");
            Console.WriteLine($"{s_checks} focused SVN repair UI checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            await Task.Run(session.Dispose).ConfigureAwait(false);
        }
    }

    private static async Task VerifyRepairUi(Window window, string temporary, string config, string screenshots)
    {
        var fixture = await CreateRepairFixture(temporary, config);
        using var repository = new SvnRepository(fixture.WorkingCopy);
        window.Width = 1024;
        window.Height = 600;
        window.Content = new SourceGit.Views.LauncherPage
        {
            DataContext = new LauncherPage(new RepositoryNode
            {
                Id = fixture.WorkingCopy,
                Name = "SVN repair fixture",
                IsRepository = true,
                IsSvn = true,
            }, repository),
        };
        await repository.RefreshAsync();
        await WaitForPaths(repository);
        var workingCopy = repository.WorkingCopy;
        await WaitForWorkingCopy(workingCopy);
        var tabs = FindNamedControl<TabControl>(window, "RepositoryTabs");
        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var grid = FindNamedControl<DataGrid>(window, "WorkingCopyEntries");
        var cleanup = FindNamedControl<Button>(window, "WorkingCopyCleanup");
        Check(workingCopy.IsEmpty && workingCopy.CanCleanup && cleanup.IsEnabled,
            "root Cleanup is available for an empty, refreshed working copy");

        await OpenCleanupConfirmation(window, cleanup);
        var cleanupConfirmation = await WaitForOperationDialog(window);
        var cleanTextPath = Path.Combine(fixture.WorkingCopy, "restore-missing.txt");
        var cleanupMessage = FindNamedControl<TextBlock>(cleanupConfirmation, "ConfirmationMessage").Text;
        Check(cleanupMessage.Contains(fixture.WorkingCopy, StringComparison.Ordinal) &&
            cleanupMessage.Contains("Close other SVN clients", StringComparison.Ordinal) &&
            cleanupMessage.Contains("Unversioned and ignored files are preserved", StringComparison.Ordinal) &&
            cleanupMessage.Contains("No changes are committed", StringComparison.Ordinal),
            "root Cleanup warns about other clients, preserves unversioned files, and does not commit");
        FindNamedControl<Button>(cleanupConfirmation, "CancelButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !cleanupConfirmation.IsVisible);
        Check(workingCopy.IsEmpty && File.ReadAllText(cleanTextPath) == "restore baseline\n",
            "cancelling root Cleanup leaves the clean working copy unchanged");

        await OpenCleanupConfirmation(window, cleanup);
        cleanupConfirmation = await WaitForOperationDialog(window);
        FindNamedControl<Button>(cleanupConfirmation, "AcceptButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForWorkingCopy(workingCopy);
        Check(!workingCopy.HasActionError && workingCopy.IsEmpty &&
            File.ReadAllText(cleanTextPath) == "restore baseline\n",
            "confirmed root Cleanup succeeds without deleting tracked working-copy files; " +
            workingCopy.ActionErrorMessage + " " + workingCopy.ActionErrorDetails);

        var realSvn = Preferences.Instance.SvnInstallPath;
        var cleanupArguments = Path.Combine(temporary, "cleanup-arguments");
        if (!OperatingSystem.IsWindows())
            Preferences.Instance.SvnInstallPath = CreateFailingStatusSvn(temporary, realSvn, cleanupArguments);
        else
            Preferences.Instance.SvnInstallPath = Path.Combine(temporary, "missing-svn-executable");
        await workingCopy.RefreshAsync();
        Check(workingCopy.HasError && workingCopy.CanCleanup && cleanup.IsEnabled,
            "root Cleanup remains available after working-copy status fails");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(window, theme,
                Path.Combine(screenshots, "svn-repair-cleanup-status-error-" + theme + ".png"),
                "Cleanup toolbar with a failed status");
        if (!OperatingSystem.IsWindows())
        {
            await OpenCleanupConfirmation(window, cleanup);
            var failedStatusConfirmation = await WaitForOperationDialog(window);
            FindNamedControl<Button>(failedStatusConfirmation, "AcceptButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForWorkingCopy(workingCopy);
            Check(workingCopy.HasError && workingCopy.CanCleanup && !workingCopy.HasActionError,
                "root Cleanup still runs successfully when its fresh status command fails");
            var arguments = File.ReadAllLines(cleanupArguments);
            Check(arguments.Length > 0 && arguments[0] == "cleanup" &&
                !arguments.Any(argument => argument is "--vacuum-pristines" or "--remove-unversioned" or
                    "--remove-ignored" or "--include-externals" or "--break-locks"),
                "root Cleanup runs standard cleanup without extra file-removal or vacuum options");
        }
        Preferences.Instance.SvnInstallPath = realSvn;
        await workingCopy.RefreshAsync();
        await WaitForWorkingCopy(workingCopy);
        Check(!workingCopy.HasError && workingCopy.IsEmpty,
            "working-copy status recovers after restoring the SVN executable");

        var confirmationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishConfirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var busyCleanup = workingCopy.CleanupAsync(_ =>
        {
            confirmationStarted.TrySetResult(true);
            return finishConfirmation.Task;
        });
        await confirmationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Dispatcher.UIThread.RunJobs();
        Check(workingCopy.IsBusy && !workingCopy.CanCleanup && !cleanup.IsEnabled,
            "root Cleanup disables itself while a cleanup confirmation is pending");
        finishConfirmation.SetResult(false);
        Check(!await busyCleanup && !workingCopy.IsBusy && workingCopy.IsEmpty,
            "declining a pending root Cleanup returns to the empty ready state");

        await Run("svn", "changelist", "RepairScope", "--", cleanTextPath + "@");
        var removePath = Path.Combine(fixture.WorkingCopy, "remove-missing.txt");
        var renameSource = Path.Combine(fixture.WorkingCopy, "external-source @ 文件.txt");
        var renameDestination = Path.Combine(fixture.WorkingCopy, "external-result @ 文件.txt");
        var modifiedPath = Path.Combine(fixture.WorkingCopy, "ordinary-modified.txt");
        var unversionedPath = Path.Combine(fixture.WorkingCopy, "unversioned.txt");
        await Run("svn", "changelist", "RepairScope", "--", removePath + "@");
        File.WriteAllText(cleanTextPath, "local text before restore\n");
        await Run("svn", "propset", "fixture:local", "discard with missing-file restore", "--", cleanTextPath + "@");
        File.Delete(cleanTextPath);
        File.Delete(removePath);
        File.WriteAllText(renameSource, "local text before external rename\n");
        File.Move(renameSource, renameDestination);
        File.WriteAllText(modifiedPath, "ordinary modified\n");
        File.WriteAllText(unversionedPath, "unversioned bytes\n");
        await workingCopy.RefreshAsync();
        await WaitForWorkingCopy(workingCopy);
        Dispatcher.UIThread.RunJobs();

        var restoreEntry = FindWorkingCopyEntry(workingCopy, "restore-missing.txt");
        var removeEntry = FindWorkingCopyEntry(workingCopy, "remove-missing.txt");
        var modifiedEntry = FindWorkingCopyEntry(workingCopy, "ordinary-modified.txt");
        var unversionedEntry = FindWorkingCopyEntry(workingCopy, "unversioned.txt");
        workingCopy.SetSelectedEntries([restoreEntry]);
        Check(restoreEntry.Entry.Item == "missing" && restoreEntry.Entry.Properties == "modified" &&
            removeEntry.Entry.Item == "missing" &&
            workingCopy.CanRestoreMissingFiles && workingCopy.CanRemoveMissingFiles,
            "missing regular files remain eligible with local property edits");
        workingCopy.SetSelectedEntries([restoreEntry, modifiedEntry]);
        Check(!workingCopy.CanRestoreMissingFiles && !workingCopy.CanRemoveMissingFiles,
            "a mixed missing and ordinary-modified selection is ineligible as a whole");
        workingCopy.SetSelectedEntries([unversionedEntry]);
        Check(!workingCopy.CanRestoreMissingFiles && !workingCopy.CanRemoveMissingFiles,
            "an unversioned regular file is ineligible for missing-file recovery");
        workingCopy.SetSelectedEntries([restoreEntry]);
        SetChangelistFilter(FindNamedControl<ComboBox>(window, "WorkingCopyChangelistFilter"), workingCopy, "RepairScope");
        Check(workingCopy.SelectedEntries.Count == 1 && workingCopy.SelectedEntries[0].Path == restoreEntry.Path &&
            workingCopy.VisibleEntries.All(entry => entry.Entry.Changelist == "RepairScope"),
            "changing to a repair changelist drops hidden selections and scopes visible rows");

        await OpenSelectedAction(window, "RestoreMissingSvnFiles");
        var restoreConfirmation = await WaitForOperationDialog(window);
        FindNamedControl<Button>(restoreConfirmation, "CancelButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !restoreConfirmation.IsVisible);
        Check(!File.Exists(cleanTextPath) &&
            await GetProperty(fixture.WorkingCopy, "restore-missing.txt", "fixture:local", config) == "discard with missing-file restore",
            "cancelling missing-file restore leaves both the absent file and local property edit unchanged");

        await OpenSelectedAction(window, "RestoreMissingSvnFiles");
        restoreConfirmation = await WaitForOperationDialog(window);
        FindNamedControl<Button>(restoreConfirmation, "AcceptButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForWorkingCopy(workingCopy);
        Check(File.ReadAllText(cleanTextPath) == "restore baseline\n" &&
            await GetProperty(fixture.WorkingCopy, "restore-missing.txt", "fixture:label", config) == "restore baseline property" &&
            !await HasProperty(fixture.WorkingCopy, "restore-missing.txt", "fixture:local", config),
            "confirmed missing-file restore recovers BASE bytes and properties and discards its local property edit");
        Check(workingCopy.HasChangelistFilter && workingCopy.SelectedChangelistFilter.Name == "RepairScope" &&
            workingCopy.VisibleEntries.All(entry => entry.Entry.Changelist == "RepairScope") &&
            workingCopy.SelectedEntries.All(workingCopy.VisibleEntries.Contains),
            "refresh after restore preserves the active changelist scope without hidden selection");

        SetChangelistFilter(FindNamedControl<ComboBox>(window, "WorkingCopyChangelistFilter"), workingCopy, null);
        await SelectWorkingCopyEntry(workingCopy, grid, FindWorkingCopyEntry(workingCopy, "remove-missing.txt"), false);
        await OpenSelectedAction(window, "RemoveMissingSvnFiles");
        var removeConfirmation = await WaitForOperationDialog(window);
        FindNamedControl<Button>(removeConfirmation, "CancelButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !removeConfirmation.IsVisible);
        Check(!File.Exists(removePath) && FindWorkingCopyEntry(workingCopy, "remove-missing.txt").Entry.Item == "missing",
            "cancelling missing-file removal leaves the missing state unscheduled");
        await OpenSelectedAction(window, "RemoveMissingSvnFiles");
        removeConfirmation = await WaitForOperationDialog(window);
        FindNamedControl<Button>(removeConfirmation, "AcceptButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForWorkingCopy(workingCopy);
        Check(FindWorkingCopyEntry(workingCopy, "remove-missing.txt").Entry.Item == "deleted" &&
            !File.Exists(removePath) && File.ReadAllText(unversionedPath) == "unversioned bytes\n" && workingCopy.CanCommit,
            "confirmed removal schedules the missing path for deletion without recreating it");

        // External rename review, stale-snapshot failure/reload, and history-preserving repair follow.
        await VerifySvnRenameRepair(window, repository, workingCopy, grid, fixture, renameSource, renameDestination, screenshots);
    }

    private static async Task<(string Repository, string WorkingCopy)> CreateRepairFixture(string temporary, string config)
    {
        var repository = Path.Combine(temporary, "repair-repository");
        var workingCopy = Path.Combine(temporary, "repair 工作 copy");
        await Run("svnadmin", "create", repository);
        await Run("svn", "checkout", "--config-dir", config, new Uri(repository + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/'), workingCopy);
        foreach (var name in new[]
        {
            "restore-missing.txt", "remove-missing.txt", "external-source @ 文件.txt", "ordinary-modified.txt",
        })
            File.WriteAllText(Path.Combine(workingCopy, name), name == "external-source @ 文件.txt"
                ? "external rename BASE\n" : name == "restore-missing.txt" ? "restore baseline\n" : name + " baseline\n");
        await Run("svn", "add", "--force", "--", workingCopy + "@");
        await Run("svn", "propset", "fixture:label", "restore baseline property", "--", Path.Combine(workingCopy, "restore-missing.txt") + "@");
        await Run("svn", "propset", "fixture:label", "remove baseline property", "--", Path.Combine(workingCopy, "remove-missing.txt") + "@");
        await Run("svn", "propset", "fixture:label", "external rename property", "--", Path.Combine(workingCopy, "external-source @ 文件.txt") + "@");
        await Run("svn", "commit", "--config-dir", config, "-m", "repair fixture baseline", "--", workingCopy + "@");
        return (repository, workingCopy);
    }

    private static async Task OpenCleanupConfirmation(Window window, Button cleanup)
    {
        Check(cleanup.IsEnabled, "root Cleanup is enabled before opening its confirmation");
        cleanup.Focus();
        cleanup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => window.OwnedWindows.OfType<SvnOperationDialog>().Any(dialog => dialog.IsVisible));
    }

    private static string CreateFailingStatusSvn(string directory, string realSvn, string cleanupArguments)
    {
        var wrapper = Path.Combine(directory, "failing-status-svn");
        File.WriteAllText(wrapper,
            "#!/bin/sh\n" +
            "if [ \"$1\" = status ]; then echo 'fixture status failure' >&2; exit 17; fi\n" +
            "if [ \"$1\" = cleanup ]; then printf '%s\\n' \"$@\" > " + QuoteShellArgument(cleanupArguments) + "; fi\n" +
            "exec " + QuoteShellArgument(realSvn) + " \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return wrapper;
    }

    private static async Task<bool> HasProperty(string workingCopy, string relativePath, string name, string config)
    {
        var xml = await RunCapture("svn", "proplist", "--xml", "--config-dir", config, "--", Path.Combine(workingCopy, relativePath) + "@");
        return XDocument.Parse(xml).Descendants("property").Any(property => (string)property.Attribute("name") == name);
    }

    private static async Task VerifySvnRenameRepair(Window window, SvnRepository repository,
        SvnWorkingCopy workingCopy, DataGrid grid, (string Repository, string WorkingCopy) fixture,
        string source, string destination, string screenshots)
    {
        SetChangelistFilter(FindNamedControl<ComboBox>(window, "WorkingCopyChangelistFilter"), workingCopy, null);
        var sourceEntry = FindWorkingCopyEntry(workingCopy, "external-source @ 文件.txt");
        await SelectWorkingCopyEntry(workingCopy, grid, sourceEntry, false);
        Check(sourceEntry.Entry.Item == "missing" && workingCopy.CanRepairRename,
            "one eligible externally renamed missing file enables Rename repair");

        await OpenSelectedAction(window, "RepairSvnRename");
        var cancelled = await WaitForRepairDialog(window);
        var cancelledTarget = FindNamedControl<TextBox>(cancelled, "TargetInput");
        cancelledTarget.Text = "external-result @ 文件.txt";
        Dispatcher.UIThread.RunJobs();
        Check(await cancelled.PrepareAsync() && cancelled.CanApply,
            "Rename repair reviews the existing destination before enabling Apply");
        cancelled.Width = cancelled.MinWidth;
        cancelled.Height = cancelled.MinHeight;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(cancelled, theme,
                Path.Combine(screenshots, "svn-repair-rename-compact-" + theme + ".png"),
                "compact Rename repair review");
        FindNamedControl<Button>(cancelled, "CancelRepairButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !cancelled.IsVisible);
        Check(!File.Exists(source) && File.ReadAllText(destination) == "local text before external rename\n",
            "cancelling Rename repair leaves the external rename and destination bytes unchanged");

        if (!OperatingSystem.IsWindows())
            await VerifyBusyRenameRepairCancellation(window, workingCopy, grid, fixture, source, destination);

        await SelectWorkingCopyEntry(workingCopy, grid, FindWorkingCopyEntry(workingCopy, "external-source @ 文件.txt"), false);
        await OpenSelectedAction(window, "RepairSvnRename");
        var dialog = await WaitForRepairDialog(window);
        var targetInput = FindNamedControl<TextBox>(dialog, "TargetInput");
        targetInput.Text = "external-result @ 文件.txt";
        Dispatcher.UIThread.RunJobs();
        Check(await dialog.PrepareAsync() && dialog.CanApply &&
            FindNamedControl<TextBlock>(dialog, "DestinationPreview").Text.Contains("external-result @ 文件.txt", StringComparison.Ordinal),
            "Rename repair captures a reviewed source and destination snapshot");

        const string changedAfterReview = "external destination changed after review\n";
        File.WriteAllText(destination, changedAfterReview);
        Check(!await dialog.ApplyAsync() && dialog.IsVisible && !dialog.CanApply &&
            targetInput.Text == "external-result @ 文件.txt" && File.ReadAllText(destination) == changedAfterReview &&
            FindNamedControl<Control>(dialog, "RepairErrorPanel").IsVisible,
            "stale destination content rejects repair, preserves the typed target, and leaves newer bytes untouched");
        dialog.Width = dialog.MinWidth;
        dialog.Height = dialog.MinHeight;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(dialog, theme,
                Path.Combine(screenshots, "svn-repair-rename-stale-" + theme + ".png"),
                "stale Rename repair recovery");

        await dialog.ReloadAsync();
        Check(dialog.CanApply && targetInput.Text == "external-result @ 文件.txt" &&
            File.ReadAllText(destination) == changedAfterReview &&
            !FindNamedControl<Control>(dialog, "RepairErrorPanel").IsVisible,
            "explicit reload refreshes the destination snapshot without losing the target draft");
        FindNamedControl<Button>(dialog, "ApplyRepairButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !dialog.IsVisible && !workingCopy.IsBusy);
        Check(!File.Exists(source) && File.ReadAllText(destination) == changedAfterReview &&
            !workingCopy.HasActionError,
            "history-preserving repair applies the refreshed snapshot and keeps the external destination content");
        Check(workingCopy.Entries.Any(entry => entry.Path == "external-source @ 文件.txt" && entry.Entry.Item == "deleted") &&
            workingCopy.Entries.Any(entry => entry.Path == "external-result @ 文件.txt" && entry.Entry.IsCopied) &&
            await GetProperty(fixture.WorkingCopy, "external-result @ 文件.txt", "fixture:label", Preferences.Instance.SvnConfigDirectory) ==
                "external rename property",
            "the repaired pair retains both SVN endpoints and the source BASE property");

        var repairedDestination = FindWorkingCopyEntry(workingCopy, "external-result @ 文件.txt");
        await SelectWorkingCopyEntry(workingCopy, grid, repairedDestination, false);
        Check(!workingCopy.CanCommit,
            "Commit rejects a selected external-rename destination without its deleted source");
        await SelectWorkingCopyEntries(workingCopy, grid,
            FindWorkingCopyEntry(workingCopy, "external-source @ 文件.txt"), repairedDestination);
        Check(workingCopy.CanCommit,
            "the repaired source and destination form a valid paired Commit selection");

        var commitTask = SvnOperationDialog.ShowCommitAsync(repository, window);
        var commit = await WaitForOperationDialog(window);
        var choices = FindNamedControl<StackPanel>(commit, "CommitCandidates").Children.OfType<CheckBox>().ToArray();
        var sourceChoice = choices.Single(choice => Avalonia.Automation.AutomationProperties.GetName(choice)
            .StartsWith("external-source @ 文件.txt,", StringComparison.Ordinal));
        var destinationChoice = choices.Single(choice => Avalonia.Automation.AutomationProperties.GetName(choice)
            .StartsWith("external-result @ 文件.txt,", StringComparison.Ordinal));
        Check(sourceChoice.IsChecked == true && destinationChoice.IsChecked == true,
            "the Commit form selects both endpoints of the repaired rename pair");
        destinationChoice.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Check(!FindNamedControl<Button>(commit, "AcceptButton").IsEnabled,
            "Commit cannot submit just one side of the repaired rename pair");
        destinationChoice.IsChecked = true;
        FindNamedControl<TextBox>(commit, "CommitMessage").Text = "Repair external SVN rename";
        Dispatcher.UIThread.RunJobs();
        Check(FindNamedControl<Button>(commit, "AcceptButton").IsEnabled,
            "Commit accepts both rename endpoints with a message");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(commit, theme,
                Path.Combine(screenshots, "svn-repair-rename-commit-" + theme + ".png"),
                "paired Rename repair Commit");
        FindNamedControl<Button>(commit, "AcceptButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await commitTask.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForWorkingCopy(workingCopy);
        Check(!workingCopy.HasActionError &&
            await RunCapture("svnlook", "cat", fixture.Repository, "external-result @ 文件.txt") == changedAfterReview &&
            (await RunCapture("svn", "log", "--xml", "--verbose", "--revision", "HEAD", "--limit", "1", "--", fixture.WorkingCopy + "@"))
                .Contains("copyfrom-path", StringComparison.Ordinal),
            "paired Commit stores the reviewed bytes and SVN copy ancestry in repository history");
        Console.WriteLine("SVN cleanup and repair UI checks passed.");
    }

    private static async Task VerifyBusyRenameRepairCancellation(Window window, SvnWorkingCopy workingCopy,
        DataGrid grid, (string Repository, string WorkingCopy) fixture, string source, string destination)
    {
        var realSvn = Preferences.Instance.SvnInstallPath;
        var directory = Path.GetDirectoryName(fixture.WorkingCopy);
        var gate = Path.Combine(directory, "repair-revert-gate");
        var started = Path.Combine(directory, "repair-revert-started");
        var wrapper = CreateBlockingRevertSvn(directory, realSvn, gate, started);
        File.WriteAllText(gate, string.Empty);
        Preferences.Instance.SvnInstallPath = wrapper;
        try
        {
            await SelectWorkingCopyEntry(workingCopy, grid,
                FindWorkingCopyEntry(workingCopy, "external-source @ 文件.txt"), false);
            await OpenSelectedAction(window, "RepairSvnRename");
            var dialog = await WaitForRepairDialog(window);
            var targetInput = FindNamedControl<TextBox>(dialog, "TargetInput");
            targetInput.Text = "external-result @ 文件.txt";
            Dispatcher.UIThread.RunJobs();
            Check(await dialog.PrepareAsync() && dialog.CanApply,
                "busy cancellation begins from an explicit Rename repair review");

            var applyTask = dialog.ApplyAsync();
            await WaitUntil(() => File.Exists(started));
            Check(!File.Exists(destination) && !File.Exists(source),
                "the synchronized cancel point occurs after the destination is protected and before SVN revert");
            FindNamedControl<Button>(dialog, "CancelRepairButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!await applyTask.WaitAsync(TimeSpan.FromSeconds(30)) && dialog.IsVisible &&
                targetInput.Text == "external-result @ 文件.txt" && !dialog.CanApply &&
                FindNamedControl<Control>(dialog, "RepairErrorPanel").IsVisible,
                "busy Cancel interrupts repair, keeps the dialog draft, and requires explicit reload");
            Check(!File.Exists(source) && File.ReadAllText(destination) == "local text before external rename\n",
                "cancelled repair restores the protected destination bytes and leaves the SVN source missing");
            Check(File.ReadAllText(Path.Combine(fixture.WorkingCopy, "ordinary-modified.txt")) == "ordinary modified\n" &&
                File.ReadAllText(Path.Combine(fixture.WorkingCopy, "unversioned.txt")) == "unversioned bytes\n",
                "busy cancellation leaves unrelated modified and unversioned files unchanged");

            Preferences.Instance.SvnInstallPath = realSvn;
            var reloadButton = FindNamedControl<Button>(dialog, "ReloadRepairButton");
            await WaitUntil(() => reloadButton.IsEnabled && !workingCopy.IsBusy);
            await dialog.ReloadAsync();
            Check(dialog.CanApply && targetInput.Text == "external-result @ 文件.txt" &&
                File.ReadAllText(destination) == "local text before external rename\n",
                "explicit reload recovers the canceled preview while preserving the typed target; error=" +
                (dialog.FindControl<TextBlock>("RepairErrorMessage")?.Text ?? "<message unavailable>") + "; details=" +
                (dialog.FindControl<TextBox>("RepairErrorDetails")?.Text ?? "<details unavailable>"));
            FindNamedControl<Button>(dialog, "CancelRepairButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => !dialog.IsVisible);
        }
        finally
        {
            Preferences.Instance.SvnInstallPath = realSvn;
            if (File.Exists(gate))
                File.Delete(gate);
        }
    }

    private static async Task<SourceGit.Views.SvnRepairDialog> WaitForRepairDialog(Window owner)
    {
        await WaitUntil(() => owner.OwnedWindows.OfType<SourceGit.Views.SvnRepairDialog>()
            .Any(dialog => dialog.IsVisible));
        return owner.OwnedWindows.OfType<SourceGit.Views.SvnRepairDialog>()
            .Single(dialog => dialog.IsVisible);
    }

    private static string CreateBlockingRevertSvn(string directory, string realSvn, string gate, string started)
    {
        var wrapper = Path.Combine(directory, "blocking-revert-svn");
        File.WriteAllText(wrapper,
            "#!/bin/sh\n" +
            "if [ \"$1\" = revert ]; then\n" +
            ": > " + QuoteShellArgument(started) + "\n" +
            "while [ -e " + QuoteShellArgument(gate) + " ]; do /bin/sleep 0.02; done\n" +
            "fi\nexec " + QuoteShellArgument(realSvn) + " \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return wrapper;
    }
}
