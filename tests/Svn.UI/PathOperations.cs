using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;

using SourceGit;
using SourceGit.ViewModels;

internal static partial class Program
{
    private static async Task<int> RunPathOperationsOnly(string temporary, string config)
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(Program));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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
                await VerifyPathOperationUi(window, temporary, config, screenshots);
                window.Close();
                return true;
            }, timeout.Token);
            Console.WriteLine($"{s_checks} focused SVN path-operation UI checks passed.");
            return 0;
        }
        finally
        {
            await Task.Run(session.Dispose).ConfigureAwait(false);
        }
    }

    private static async Task VerifyPathOperationUi(Window window, string temporary, string config, string screenshots)
    {
        var root = await CreatePathOperationUiFixture(temporary, config);
        using var repository = new SvnRepository(root);
        window.Content = new SourceGit.Views.LauncherPage
        {
            DataContext = new LauncherPage(new RepositoryNode
            {
                Id = root, Name = "SVN file operations", IsRepository = true, IsSvn = true,
            }, repository),
        };
        await repository.RefreshAsync();
        await WaitForPaths(repository);
        var workingCopy = repository.WorkingCopy;
        await WaitForWorkingCopy(workingCopy);
        FindNamedControl<TabControl>(window, "RepositoryTabs").SelectedIndex = 1;
        if (workingCopy.IsEmpty)
            await workingCopy.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var grid = FindNamedControl<DataGrid>(window, "WorkingCopyEntries");
        var operations = FindNamedControl<Button>(window, "WorkingCopyFileOperations");
        var selected = FindNamedControl<Button>(window, "WorkingCopySelectedActions");
        Check(!workingCopy.GetCommitCandidates().Any(entry => entry.Path.StartsWith("guarded" + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
            "Commit excludes a known switched subtree while allowing ordinary file operations elsewhere");
        workingCopy.SetSelectedEntries([]);
        Dispatcher.UIThread.RunJobs();
        Check(operations.IsEnabled && !selected.IsEnabled && !workingCopy.Entries.Any(entry => entry.Path == "clean @ 文件.txt"),
            "file operations can choose clean tracked sources absent from the changed-only grid");
        Check(operations.Margin.Top >= 4 && selected.Margin.Top >= 4,
            "both working-copy action controls retain spacing below the border");

        operations.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        var blank = await WaitForPathOperationDialog(window);
        Check(string.IsNullOrEmpty(FindNamedControl<TextBox>(blank, "SourcePath").Text) &&
            !FindNamedControl<Button>(blank, "PrimaryButton").IsEnabled,
            "toolbar opens an empty source form without scheduling a change");
        blank.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitUntil(() => !blank.IsVisible);
        Check(File.Exists(Path.Combine(root, "clean @ 文件.txt")), "Escape before execution preserves the clean source");

        var renameTask = SourceGit.Views.SvnPathOperationDialog.ShowAsync(workingCopy, "rename", "clean @ 文件.txt", window);
        var rename = await WaitForPathOperationDialog(window);
        var source = FindNamedControl<TextBox>(rename, "SourcePath");
        var target = FindNamedControl<TextBox>(rename, "TargetInput");
        var primary = FindNamedControl<Button>(rename, "PrimaryButton");
        Check(source.Text == "clean @ 文件.txt" && !primary.IsEnabled,
            "Rename prefills the exact source and rejects its unchanged name");
        target.Text = "../escape.txt";
        Dispatcher.UIThread.RunJobs();
        Check(!primary.IsEnabled, "Rename rejects a folder path in the new-name field");
        target.Text = "renamed @ 文件.txt";
        Dispatcher.UIThread.RunJobs();
        Check(primary.IsEnabled && FindNamedControl<TextBlock>(rename, "DestinationPreview").Text.Contains("renamed @ 文件.txt", StringComparison.Ordinal),
            "Rename previews the complete exact destination before submission");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            rename.Width = rename.MinWidth;
            rename.Height = rename.MinHeight;
            await SaveSettledScreenshot(rename, theme, Path.Combine(screenshots, "svn-path-rename-" + theme + ".png"), "Rename dialog");
            var origin = primary.TranslatePoint(new Point(), rename);
            Check(origin.HasValue && origin.Value.Y + primary.Bounds.Height <= rename.ClientSize.Height,
                "Rename action remains visible at minimum dialog size");
        }
        primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await renameTask.WaitAsync(TimeSpan.FromSeconds(30));
        Check(File.Exists(Path.Combine(root, "renamed @ 文件.txt")),
            "Rename creates the exact destination; files=" + string.Join(", ", Directory.GetFiles(root).Select(Path.GetFileName)) +
            "; output=" + workingCopy.OperationOutput + "; error=" + workingCopy.ActionErrorDetails);
        Check(!File.Exists(Path.Combine(root, "clean @ 文件.txt")) &&
            File.ReadAllText(Path.Combine(root, "renamed @ 文件.txt")) == "clean source\n" && !workingCopy.HasActionError,
            "Rename uses the production mutation, preserves bytes and refreshes status");
        Check(FindWorkingCopyEntry(workingCopy, "renamed @ 文件.txt").Entry.IsCopied &&
            workingCopy.SelectedEntry?.Path == "renamed @ 文件.txt",
            "Rename retains ancestry and selects the resulting status row");
        await SelectWorkingCopyEntry(workingCopy, grid, FindWorkingCopyEntry(workingCopy, "renamed @ 文件.txt"));
        Check(!workingCopy.CanCommit, "a move destination alone cannot enable Commit");
        await SelectWorkingCopyEntries(workingCopy, grid, FindWorkingCopyEntry(workingCopy, "clean @ 文件.txt"),
            FindWorkingCopyEntry(workingCopy, "renamed @ 文件.txt"));
        Check(workingCopy.CanCommit, "both Rename endpoints can be committed together");

        await SelectWorkingCopyEntry(workingCopy, grid, FindWorkingCopyEntry(workingCopy, "modified.txt"));
        selected.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Home, RawInputModifiers.None);
        // Explore, Copy paths, Rename, Move; disabled conflict inspection is skipped.
        for (var index = 0; index < 3; index++)
        {
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        }
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        var move = await WaitForPathOperationDialog(window);
        Check(move.Model.Operation == "move" && FindNamedControl<TextBox>(move, "SourcePath").Text == "modified.txt",
            "the selected-actions keyboard shortcut opens Move with the exact selected source");
        var moveTarget = FindNamedControl<TextBox>(move, "TargetInput");
        moveTarget.Text = "destination";
        Dispatcher.UIThread.RunJobs();
        Check(!FindNamedControl<Button>(move, "PrimaryButton").IsEnabled,
            "an existing folder is rejected instead of implicitly appending the source name");
        moveTarget.Text = Path.Combine("destination", "moved @ 文件.txt");
        Dispatcher.UIThread.RunJobs();
        Check(FindNamedControl<Button>(move, "PrimaryButton").IsEnabled, "Move accepts a full new path in an existing parent");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(move, theme, Path.Combine(screenshots, "svn-path-move-" + theme + ".png"), "Move dialog");
        moveTarget.Focus();
        move.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitUntil(() => !move.IsVisible);
        var movedPath = Path.Combine(root, "destination", "moved @ 文件.txt");
        Check(!File.Exists(Path.Combine(root, "modified.txt")) && File.ReadAllText(movedPath) == "local text edit\n",
            "Move preserves an uncommitted text edit");
        Check((await RunCapture("svn", "propget", "fixture:label", "--", movedPath + "@")).TrimEnd() == "local property",
            "Move preserves an uncommitted property edit");

        var copyTask = SourceGit.Views.SvnPathOperationDialog.ShowAsync(workingCopy, "copy", "folder", window);
        var copy = await WaitForPathOperationDialog(window);
        FindNamedControl<TextBox>(copy, "TargetInput").Text = Path.Combine("folder", "inside");
        Dispatcher.UIThread.RunJobs();
        Check(!FindNamedControl<Button>(copy, "PrimaryButton").IsEnabled, "Copy rejects placing a folder inside itself");
        FindNamedControl<TextBox>(copy, "TargetInput").Text = Path.Combine("destination", "copied folder");
        Dispatcher.UIThread.RunJobs();
        FindNamedControl<Button>(copy, "PrimaryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await copyTask.WaitAsync(TimeSpan.FromSeconds(30));
        Check(File.ReadAllText(Path.Combine(root, "folder", "child.txt")) == "folder child\n" &&
            File.ReadAllText(Path.Combine(root, "destination", "copied folder", "child.txt")) == "folder child\n" &&
            workingCopy.Entries.Single(entry => entry.Path == Path.Combine("destination", "copied folder")).Entry.IsCopied,
            "Copy preserves the source folder and schedules a history-preserving destination");
        Check(workingCopy.GetCommitCandidates().Any(entry => entry.Path == Path.Combine("destination", "copied folder")),
            "the copied directory is available in the Commit form");

        // A source can become ineligible while the form is open. Preserve its draft and allow an explicit corrected retry.
        var retryTask = SourceGit.Views.SvnPathOperationDialog.ShowAsync(workingCopy, "copy", "retry.txt", window);
        var retry = await WaitForPathOperationDialog(window);
        var retryTarget = FindNamedControl<TextBox>(retry, "TargetInput");
        retryTarget.Text = "retry result.txt";
        await Run("svn", "delete", "--keep-local", "--", Path.Combine(root, "retry.txt") + "@");
        await retry.SubmitAsync();
        Check(retry.IsVisible && retry.HasError && retryTarget.Text == "retry result.txt" && !File.Exists(Path.Combine(root, "retry result.txt")),
            "fresh backend validation rejects a newly scheduled source and retains the dialog draft");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            await SaveSettledScreenshot(retry, theme, Path.Combine(screenshots, "svn-path-error-" + theme + ".png"), "File operation failure");
            var errorText = FindNamedControl<TextBlock>(retry, "ErrorMessage");
            var errorOrigin = errorText.TranslatePoint(new Point(), retry);
            var footerOrigin = FindNamedControl<Button>(retry, "CancelButton").TranslatePoint(new Point(), retry);
            Check(errorOrigin.HasValue && footerOrigin.HasValue && errorOrigin.Value.Y >= 28 &&
                errorOrigin.Value.Y + errorText.Bounds.Height < footerOrigin.Value.Y,
                "operation failure scrolls its full error summary above the fixed footer");
        }
        await Run("svn", "revert", "--depth", "empty", "--", Path.Combine(root, "retry.txt") + "@");
        File.WriteAllText(Path.Combine(root, "retry.txt"), "restored retry source\n");
        await workingCopy.RefreshAsync();
        retryTarget.Text = "explicit retry.txt";
        Dispatcher.UIThread.RunJobs();
        FindNamedControl<Button>(retry, "PrimaryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await retryTask.WaitAsync(TimeSpan.FromSeconds(30));
        Check(!workingCopy.HasActionError && File.ReadAllText(Path.Combine(root, "explicit retry.txt")) == "restored retry source\n",
            "an explicit corrected retry recovers and clears the action error");
        Check(File.ReadAllText(Path.Combine(root, "unversioned.txt")) == "leave these bytes\n",
            "file operations preserve unrelated unversioned contents");
        if (!OperatingSystem.IsWindows())
        {
            var literalTask = SourceGit.Views.SvnPathOperationDialog.ShowAsync(workingCopy, "copy", "literal\\source.txt", window);
            var literal = await WaitForPathOperationDialog(window);
            Check(FindNamedControl<TextBox>(literal, "SourcePath").Text == "literal\\source.txt",
                "a literal Unix backslash is never normalized into a different source");
            FindNamedControl<TextBox>(literal, "TargetInput").Text = "literal copy.txt";
            Dispatcher.UIThread.RunJobs();
            FindNamedControl<Button>(literal, "PrimaryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await literalTask.WaitAsync(TimeSpan.FromSeconds(30));
            Check(File.ReadAllText(Path.Combine(root, "literal copy.txt")) == "literal backslash source\n" &&
                File.ReadAllText(Path.Combine(root, "literal", "source.txt")) == "different nested source\n",
                "Copy uses the exact Unix source instead of a similarly named nested file");
        }
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(window, theme, Path.Combine(screenshots, "svn-path-results-" + theme + ".png"), "File operation status results");

        workingCopy.SetSelectedEntries([]);
        var commitTask = SourceGit.Views.SvnOperationDialog.ShowCommitAsync(repository, window);
        var commit = await WaitForOperationDialog(window);
        var choices = FindNamedControl<StackPanel>(commit, "CommitCandidates").Children.OfType<CheckBox>().ToArray();
        Check(choices.Any(check => Avalonia.Automation.AutomationProperties.GetName(check).StartsWith("clean @ 文件.txt,", StringComparison.Ordinal)) &&
            choices.Any(check => Avalonia.Automation.AutomationProperties.GetName(check).StartsWith("renamed @ 文件.txt,", StringComparison.Ordinal)),
            "Commit offers both endpoints of a scheduled Rename");
        foreach (var check in choices)
            check.IsChecked = true;
        FindNamedControl<TextBox>(commit, "CommitMessage").Text = "Commit history-preserving file operations";
        Dispatcher.UIThread.RunJobs();
        Check(FindNamedControl<Button>(commit, "AcceptButton").IsEnabled,
            "Commit accepts complete move pairs and directory copies with a message");
        var renameSourceChoice = choices.Single(check => Avalonia.Automation.AutomationProperties.GetName(check)
            .StartsWith("clean @ 文件.txt,", StringComparison.Ordinal));
        renameSourceChoice.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Check(!FindNamedControl<Button>(commit, "AcceptButton").IsEnabled,
            "Commit prevents submitting only one end of a Rename while other paths remain selected");
        renameSourceChoice.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(commit, theme, Path.Combine(screenshots, "svn-path-commit-" + theme + ".png"), "Commit file operations");
        FindNamedControl<Button>(commit, "AcceptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await commitTask.WaitAsync(TimeSpan.FromSeconds(30));
        Check(!workingCopy.HasActionError && workingCopy.Entries.All(entry => entry.Entry.Item == "unversioned" ||
            entry.Path == "guarded" || entry.Path.StartsWith("guarded" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) &&
            File.ReadAllText(Path.Combine(root, "guarded", "child.txt")) == "unrelated switched edit\n",
            "Commit saves new operations while preserving unversioned files and an unrelated switched edit: " + workingCopy.ActionErrorDetails);
        var committedLog = await RunCapture("svn", "log", "--xml", "--verbose", "--revision", "HEAD", "--limit", "1", "--", root + "@");
        Check(committedLog.Contains("copyfrom-path=", StringComparison.Ordinal) &&
            committedLog.Contains("clean @ 文件.txt", StringComparison.Ordinal) &&
            committedLog.Contains("copied folder", StringComparison.Ordinal),
            "committed Rename and directory Copy expose their SVN copy ancestry");
        Console.WriteLine("SVN Rename/Move/Copy UI checks passed.");
    }

    private static async Task<SourceGit.Views.SvnPathOperationDialog> WaitForPathOperationDialog(Window owner)
    {
        await WaitUntil(() => owner.OwnedWindows.OfType<SourceGit.Views.SvnPathOperationDialog>().Count(dialog => dialog.IsVisible) == 1);
        return owner.OwnedWindows.OfType<SourceGit.Views.SvnPathOperationDialog>().Single(dialog => dialog.IsVisible);
    }

    private static async Task<string> CreatePathOperationUiFixture(string temporary, string config)
    {
        var repository = Path.Combine(temporary, "path-operation-repository");
        var root = Path.Combine(temporary, "path operation 工作 copy@");
        await Run("svnadmin", "create", repository);
        await Run("svn", "checkout", "--config-dir", config, new Uri(repository).AbsoluteUri, root);
        File.WriteAllText(Path.Combine(root, "clean @ 文件.txt"), "clean source\n");
        File.WriteAllText(Path.Combine(root, "modified.txt"), "base text\n");
        File.WriteAllText(Path.Combine(root, "retry.txt"), "retry source\n");
        Directory.CreateDirectory(Path.Combine(root, "folder"));
        Directory.CreateDirectory(Path.Combine(root, "destination"));
        File.WriteAllText(Path.Combine(root, "folder", "child.txt"), "folder child\n");
        Directory.CreateDirectory(Path.Combine(root, "guarded"));
        Directory.CreateDirectory(Path.Combine(root, "other"));
        File.WriteAllText(Path.Combine(root, "guarded", "child.txt"), "guarded base\n");
        File.WriteAllText(Path.Combine(root, "other", "child.txt"), "other base\n");
        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(root, "literal\\source.txt"), "literal backslash source\n");
            Directory.CreateDirectory(Path.Combine(root, "literal"));
            File.WriteAllText(Path.Combine(root, "literal", "source.txt"), "different nested source\n");
        }
        await Run("svn", "add", "--force", "--", root + "@");
        await Run("svn", "commit", "--config-dir", config, "-m", "initial path-operation fixture", "--", root + "@");
        await Run("svn", "switch", "--ignore-ancestry", "--config-dir", config,
            new Uri(repository).AbsoluteUri + "/other", Path.Combine(root, "guarded"));
        File.WriteAllText(Path.Combine(root, "guarded", "child.txt"), "unrelated switched edit\n");
        File.WriteAllText(Path.Combine(root, "modified.txt"), "local text edit\n");
        await Run("svn", "propset", "fixture:label", "local property", "--", Path.Combine(root, "modified.txt") + "@");
        File.WriteAllText(Path.Combine(root, "unversioned.txt"), "leave these bytes\n");
        return root;
    }
}
