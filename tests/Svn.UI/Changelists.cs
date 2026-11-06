using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SourceGit;
using SourceGit.Native;
using SourceGit.ViewModels;
using Preferences = SourceGit.ViewModels.Preferences;
using SvnChangelistChoice = SourceGit.Views.SvnChangelistChoice;
using SvnChangelistDialog = SourceGit.Views.SvnChangelistDialog;
using SvnOperationDialog = SourceGit.Views.SvnOperationDialog;
using SvnRepository = SourceGit.ViewModels.SvnRepository;

internal static partial class Program
{
    private static async Task<int> RunChangelistsOnly(string temporary, string config)
    {
        var fixture = await CreateChangelistFixture(temporary, config);
        var session = HeadlessUnitTestSession.StartNew(typeof(Program));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var screenshots = Environment.GetEnvironmentVariable("SOURCEGIT_SVN_UI_ARTIFACTS") ??
                Path.Combine(Path.GetTempPath(), "sourcegit-svn-ui-artifacts");
            Directory.CreateDirectory(screenshots);
            await session.Dispatch<bool>(async () =>
            {
                Preferences.Instance.SvnInstallPath = Svn.ResolveExecutable(string.Empty);
                Preferences.Instance.GitInstallPath = OS.FindGitExecutable();
                Preferences.Instance.SvnConfigDirectory = config;
                Preferences.Instance.SetCanModify();

                var window = new Window { Width = 1024, Height = 600 };
                window.Show();
                await VerifyChangelistUi(window, fixture, config, screenshots);
                window.Close();
                Console.WriteLine("Screenshots: " + screenshots);
                return true;
            }, timeout.Token);

            Check(s_checks >= 35, "focused changelist workflow completed its checks");
            Console.WriteLine($"{s_checks} focused SVN changelist UI checks passed.");
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

    private static async Task VerifyChangelistUi(Window window,
        (string Repository, string WorkingCopy) fixture, string config, string screenshots)
    {
        using var repository = new SvnRepository(fixture.WorkingCopy);
        var node = new RepositoryNode
        {
            Id = fixture.WorkingCopy,
            Name = "SVN changelist fixture",
            IsRepository = true,
            IsSvn = true,
        };
        window.Width = 1024;
        window.Height = 600;
        window.Content = new SourceGit.Views.LauncherPage { DataContext = new SourceGit.ViewModels.LauncherPage(node, repository) };
        Dispatcher.UIThread.RunJobs();
        await repository.RefreshAsync();
        await WaitForPaths(repository);
        await WaitUntil(() => !repository.IsLoading && !repository.WorkingCopy.IsLoading);

        var workingCopy = repository.WorkingCopy;
        var tabs = FindNamedControl<TabControl>(window, "RepositoryTabs");
        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        await WaitForWorkingCopy(workingCopy);
        var grid = FindNamedControl<DataGrid>(window, "WorkingCopyEntries");
        var filter = FindNamedControl<ComboBox>(window, "WorkingCopyChangelistFilter");
        Check(ReferenceEquals(grid.ItemsSource, workingCopy.VisibleEntries),
            "working-copy list binds to the active filtered entries");
        Check(workingCopy.ChangelistNames.Contains("Existing", StringComparer.Ordinal) &&
            FindWorkingCopyEntry(workingCopy, "clean-member.txt").Entry.Item == "normal" &&
            FindWorkingCopyEntry(workingCopy, "clean-member.txt").Entry.Changelist == "Existing",
            "status includes a clean assigned member in its existing changelist");
        Check(workingCopy.VisibleEntries.Any(entry => entry.Path == "modified-beta.txt") &&
            !workingCopy.VisibleEntries.Any(entry => entry.Path == "clean-unassigned.txt"),
            "status includes changed unassigned paths and omits ordinary clean unassigned files");
        Check(workingCopy.ChangelistFilters.Any(item => item.Name == "All" && !item.IsUngrouped) &&
            workingCopy.ChangelistFilters.Any(item => item.Name == "Ungrouped" && !item.IsUngrouped) &&
            workingCopy.ChangelistFilters[0].Name == null && workingCopy.ChangelistFilters[1].IsUngrouped,
            "literal All and Ungrouped changelist names stay distinct from special filters");
        Check(workingCopy.ChangelistFilters.Single(item => item.Name == "All").Label != workingCopy.ChangelistFilters[0].Label &&
            workingCopy.ChangelistFilters.Single(item => item.Name == "Ungrouped" && !item.IsUngrouped).Label !=
            workingCopy.ChangelistFilters[1].Label,
            "named filter labels visually disambiguate literal names from All and Ungrouped options");
        SetChangelistFilter(filter, workingCopy, "All");
        Check(workingCopy.VisibleEntries.Count == 1 && workingCopy.VisibleEntries[0].Path == "modified-unassigned.txt" &&
            workingCopy.HasChangelistFilter,
            "a real changelist named All filters to its member rather than expanding to all files");
        SetChangelistFilter(filter, workingCopy, "Ungrouped");
        Check(workingCopy.VisibleEntries.Count == 1 && workingCopy.VisibleEntries[0].Path == "clean-ungrouped-member.txt",
            "a real changelist named Ungrouped stays distinct from the unassigned filter");
        filter.SelectedItem = workingCopy.ChangelistFilters.Single(item => item.IsUngrouped);
        Dispatcher.UIThread.RunJobs();
        Check(workingCopy.SelectedChangelistFilter.IsUngrouped && workingCopy.VisibleEntries.All(entry =>
            string.IsNullOrEmpty(entry.Entry.Changelist)),
            "the special Ungrouped filter contains only entries without membership");
        SetChangelistFilter(filter, workingCopy, null);

        var beta = FindWorkingCopyEntry(workingCopy, "modified-beta.txt");
        var unversioned = FindWorkingCopyEntry(workingCopy, "unversioned.txt");
        workingCopy.SetSelectedEntries([beta, unversioned]);
        Check(!workingCopy.CanAssignChangelist && !workingCopy.CanRemoveFromChangelist,
            "an unversioned path makes assignment and removal ineligible in a mixed selection");
        workingCopy.SetSelectedEntries([beta]);
        await OpenSelectedAction(window, "AssignSvnChangelist");
        var assign = await WaitForChangelistDialog(window);
        var existing = FindNamedControl<ComboBox>(assign, "ExistingChangelists");
        existing.SelectedItem = existing.Items.Cast<SvnChangelistChoice>()
            .Single(choice => choice.Name == "Existing");
        Dispatcher.UIThread.RunJobs();
        Check(assign.CanApply && !FindNamedControl<StackPanel>(assign, "NewChangelistForm").IsVisible,
            "the actual assignment menu opens a form that can select an existing changelist");
        Check(existing.Items.Cast<SvnChangelistChoice>().Single(choice => choice.Name == "Existing").Label !=
            existing.Items.Cast<SvnChangelistChoice>().Single(choice => choice.Name == null).Label,
            "an existing name is visually distinct from the New changelist option");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(assign, theme,
                Path.Combine(screenshots, "svn-changelist-assignment-" + theme + ".png"), "changelist assignment");
        await ApplyChangelistDialog(assign, workingCopy);
        Check(await GetChangelist(fixture.WorkingCopy, "modified-beta.txt", config) == "Existing",
            "assigning to an existing changelist updates real working-copy membership");
        Check(File.ReadAllText(Path.Combine(fixture.WorkingCopy, "modified-beta.txt")) == "beta edit\n" &&
            await GetProperty(fixture.WorkingCopy, "modified-beta.txt", "fixture:preserve", config) == "keep this property",
            "assignment preserves a file's local text and property edits");

        var cleanMember = FindWorkingCopyEntry(workingCopy, "clean-member.txt");
        workingCopy.SetSelectedEntries([cleanMember]);
        await OpenSelectedAction(window, "AssignSvnChangelist");
        assign = await WaitForChangelistDialog(window);
        existing = FindNamedControl<ComboBox>(assign, "ExistingChangelists");
        existing.SelectedItem = existing.Items.Cast<SvnChangelistChoice>()
            .Single(choice => choice.Name == "All");
        Dispatcher.UIThread.RunJobs();
        await ApplyChangelistDialog(assign, workingCopy);
        Check(await GetChangelist(fixture.WorkingCopy, "clean-member.txt", config) == "All" &&
            File.ReadAllText(Path.Combine(fixture.WorkingCopy, "clean-member.txt")) == "base line\n",
            "a clean regular file can be assigned without changing its bytes");
        cleanMember = FindWorkingCopyEntry(workingCopy, "clean-member.txt");
        workingCopy.SetSelectedEntries([cleanMember]);
        await OpenSelectedAction(window, "AssignSvnChangelist");
        assign = await WaitForChangelistDialog(window);
        existing = FindNamedControl<ComboBox>(assign, "ExistingChangelists");
        existing.SelectedItem = existing.Items.Cast<SvnChangelistChoice>()
            .Single(choice => choice.Name == "Existing");
        Dispatcher.UIThread.RunJobs();
        await ApplyChangelistDialog(assign, workingCopy);
        Check(await GetChangelist(fixture.WorkingCopy, "clean-member.txt", config) == "Existing",
            "a clean assigned file can be reassigned to another existing group");

        var alpha = FindWorkingCopyEntry(workingCopy, "modified-alpha.txt");
        var added = FindWorkingCopyEntry(workingCopy, "added.txt");
        workingCopy.SetSelectedEntries([alpha, added]);
        await OpenSelectedAction(window, "AssignSvnChangelist");
        assign = await WaitForChangelistDialog(window);
        var nameInput = FindNamedControl<TextBox>(assign, "ChangelistNameInput");
        nameInput.Text = "Review";
        Dispatcher.UIThread.RunJobs();
        Check(assign.CanApply, "modified and scheduled-added regular files can receive a new named changelist");
        assign.Width = assign.MinWidth;
        assign.Height = assign.MinHeight;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(assign, theme,
                Path.Combine(screenshots, "svn-changelist-new-compact-" + theme + ".png"), "compact new changelist");
        await ApplyChangelistDialog(assign, workingCopy);
        Check(await GetChangelist(fixture.WorkingCopy, "modified-alpha.txt", config) == "Review" &&
            await GetChangelist(fixture.WorkingCopy, "added.txt", config) == "Review",
            "creating one changelist assigns every selected eligible file");

        alpha = FindWorkingCopyEntry(workingCopy, "modified-alpha.txt");
        workingCopy.SetSelectedEntries([alpha]);
        await OpenSelectedAction(window, "AssignSvnChangelist");
        assign = await WaitForChangelistDialog(window);
        existing = FindNamedControl<ComboBox>(assign, "ExistingChangelists");
        existing.SelectedItem = existing.Items.Cast<SvnChangelistChoice>()
            .Single(choice => choice.Name == "Existing");
        Dispatcher.UIThread.RunJobs();
        await ApplyChangelistDialog(assign, workingCopy);
        Check(await GetChangelist(fixture.WorkingCopy, "modified-alpha.txt", config) == "Existing" &&
            await GetChangelist(fixture.WorkingCopy, "added.txt", config) == "Review",
            "reassigning a file moves only that file between changelists");

        alpha = FindWorkingCopyEntry(workingCopy, "modified-alpha.txt");
        workingCopy.SetSelectedEntries([alpha]);
        await OpenSelectedAction(window, "AssignSvnChangelist");
        assign = await WaitForChangelistDialog(window);
        existing = FindNamedControl<ComboBox>(assign, "ExistingChangelists");
        existing.SelectedItem = existing.Items.Cast<SvnChangelistChoice>()
            .Single(choice => choice.Name == "Review");
        Dispatcher.UIThread.RunJobs();
        await ApplyChangelistDialog(assign, workingCopy);
        Check(await GetChangelist(fixture.WorkingCopy, "modified-alpha.txt", config) == "Review",
            "an assigned file can be reassigned back to a prior group");

        var stale = FindWorkingCopyEntry(workingCopy, "modified-stale.txt");
        workingCopy.SetSelectedEntries([stale]);
        await OpenSelectedAction(window, "AssignSvnChangelist");
        assign = await WaitForChangelistDialog(window);
        nameInput = FindNamedControl<TextBox>(assign, "ChangelistNameInput");
        nameInput.Text = "Stale";
        Dispatcher.UIThread.RunJobs();
        FindNamedControl<Button>(assign, "CancelChangelistButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !assign.IsVisible);
        Check(await GetChangelist(fixture.WorkingCopy, "modified-stale.txt", config) == null,
            "cancelling assignment leaves the file unassigned");

        if (!OperatingSystem.IsWindows())
        {
            var realSvn = Preferences.Instance.SvnInstallPath;
            var fixtureDirectory = Path.GetDirectoryName(fixture.WorkingCopy);
            var gate = Path.Combine(fixtureDirectory, "changelist-command-gate");
            var started = Path.Combine(fixtureDirectory, "changelist-command-started");
            var wrapper = CreateBlockingChangelistSvn(fixtureDirectory, realSvn, gate, started);
            Preferences.Instance.SvnInstallPath = wrapper;
            workingCopy.SetSelectedEntries([FindWorkingCopyEntry(workingCopy, "modified-stale.txt")]);
            await OpenSelectedAction(window, "AssignSvnChangelist");
            assign = await WaitForChangelistDialog(window);
            nameInput = FindNamedControl<TextBox>(assign, "ChangelistNameInput");
            nameInput.Text = "BusyCancelled";
            Dispatcher.UIThread.RunJobs();
            var applyTask = assign.ApplyAsync();
            await WaitUntil(() => File.Exists(started));
            FindNamedControl<Button>(assign, "CancelChangelistButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!await applyTask.WaitAsync(TimeSpan.FromSeconds(30)) && assign.IsVisible &&
                nameInput.Text == "BusyCancelled" &&
                FindNamedControl<Control>(assign, "ChangelistErrorPanel").IsVisible &&
                await GetChangelist(fixture.WorkingCopy, "modified-stale.txt", config) == null,
                "busy Cancel interrupts a confirmed in-flight local command, keeps the dialog open, and preserves its draft");
            await assign.ReloadAsync();
            FindNamedControl<Button>(assign, "CancelChangelistButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => !assign.IsVisible);
            Preferences.Instance.SvnInstallPath = realSvn;
            await workingCopy.RefreshAsync();
            await WaitForWorkingCopy(workingCopy);
        }

        stale = FindWorkingCopyEntry(workingCopy, "modified-stale.txt");
        workingCopy.SetSelectedEntries([stale]);
        await OpenSelectedAction(window, "AssignSvnChangelist");
        assign = await WaitForChangelistDialog(window);
        nameInput = FindNamedControl<TextBox>(assign, "ChangelistNameInput");
        nameInput.Text = "Stale";
        Dispatcher.UIThread.RunJobs();
        await ApplyChangelistDialog(assign, workingCopy);
        Check(await GetChangelist(fixture.WorkingCopy, "modified-stale.txt", config) == "Stale",
            "a new single-member group is persisted before testing external membership changes");

        SetChangelistFilter(filter, workingCopy, "Stale");
        stale = FindWorkingCopyEntry(workingCopy, "modified-stale.txt");
        await SelectWorkingCopyEntry(workingCopy, grid, stale);
        var staleDialog = new SvnChangelistDialog(workingCopy);
        var staleDialogTask = staleDialog.ShowDialog<bool>(window);
        await WaitUntil(() => staleDialog.IsVisible);
        nameInput = FindNamedControl<TextBox>(staleDialog, "ChangelistNameInput");
        var staleExisting = FindNamedControl<ComboBox>(staleDialog, "ExistingChangelists");
        staleExisting.SelectedItem = staleExisting.Items.Cast<SvnChangelistChoice>()
            .Single(choice => choice.Name == null);
        Dispatcher.UIThread.RunJobs();
        nameInput.Text = "Review";
        Dispatcher.UIThread.RunJobs();
        await Run("svn", "changelist", "External", "--", Path.Combine(fixture.WorkingCopy, "modified-stale.txt") + "@");
        Check(!await staleDialog.ApplyAsync() && nameInput.Text == "Review" &&
            FindNamedControl<Control>(staleDialog, "ChangelistErrorPanel").IsVisible,
            "stale membership rejects assignment, preserves the typed target, and asks for an explicit reload");
        staleDialog.Width = staleDialog.MinWidth;
        staleDialog.Height = staleDialog.MinHeight;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(staleDialog, theme,
                Path.Combine(screenshots, "svn-changelist-stale-compact-" + theme + ".png"), "compact stale changelist");
        FindNamedControl<Button>(staleDialog, "ReloadChangelistButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !workingCopy.IsBusy && staleDialog.CanApply);
        Dispatcher.UIThread.RunJobs();
        Check(nameInput.Text == "Review" && staleDialog.CanApply &&
            await GetChangelist(fixture.WorkingCopy, "modified-stale.txt", config) == "External",
            "explicit reload captures external membership while retaining the form input");
        FindNamedControl<Button>(staleDialog, "CancelChangelistButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await staleDialogTask;
        Check(workingCopy.SelectedChangelistFilter.Name == "Stale" && workingCopy.IsFilteredEmpty &&
            workingCopy.VisibleEntries.Count == 0 && workingCopy.ChangelistFilters.Any(item => item.Name == "Stale") &&
            filter.SelectedItem is SvnChangelistFilter activeFilter && activeFilter.Name == "Stale",
            "a vanished active changelist remains selected as a visible empty filter");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(window, theme,
                Path.Combine(screenshots, "svn-changelist-filter-empty-" + theme + ".png"), "empty changelist filter");
        var showAll = FindNamedControl<Button>(window, "ShowAllChangelists");
        Check(showAll.IsVisible && showAll.IsEnabled, "the filtered-empty state offers an enabled Show All action");
        showAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Check(!workingCopy.HasChangelistFilter && workingCopy.VisibleEntries.Count == workingCopy.Entries.Count,
            "Show All returns to the complete working-copy status list");

        alpha = FindWorkingCopyEntry(workingCopy, "modified-alpha.txt");
        var unassigned = FindWorkingCopyEntry(workingCopy, "unversioned.txt");
        workingCopy.SetSelectedEntries([alpha, unassigned]);
        Check(!workingCopy.CanRemoveFromChangelist,
            "removal is disabled for a selection that mixes assigned and unassigned files");

        SetChangelistFilter(filter, workingCopy, "Review");
        Check(workingCopy.VisibleEntries.Select(entry => entry.Path).OrderBy(path => path, StringComparer.Ordinal)
            .SequenceEqual(new[] { "added.txt", "modified-alpha.txt" }.OrderBy(path => path, StringComparer.Ordinal)),
            "named filter contains only the two Review members");
        alpha = FindWorkingCopyEntry(workingCopy, "modified-alpha.txt");
        await SelectWorkingCopyEntry(workingCopy, grid, alpha);
        SetChangelistFilter(filter, workingCopy, "Existing");
        await WaitForLocalDiff(workingCopy);
        Check(workingCopy.SelectedEntries.Count == 0 && workingCopy.SelectedEntry == null &&
            workingCopy.DiffTitle.Length == 0 && workingCopy.DiffContent == null,
            "filtering out the selected file clears its selection and local diff");
        workingCopy.SetSelectedEntries([alpha]);
        Check(workingCopy.SelectedEntries.Count == 0 && workingCopy.SelectedEntry == null,
            "the view model rejects attempts to select a row hidden by the active filter");

        SetChangelistFilter(filter, workingCopy, null);
        alpha = FindWorkingCopyEntry(workingCopy, "modified-alpha.txt");
        beta = FindWorkingCopyEntry(workingCopy, "modified-beta.txt");
        workingCopy.SetSelectedEntries([beta, alpha]);
        SetChangelistFilter(filter, workingCopy, "Review");
        Check(workingCopy.SelectedEntries.Count == 1 && ReferenceEquals(workingCopy.SelectedEntries[0], alpha),
            "filtering a mixed selection retains the visible selected row and drops the hidden row");

        SetChangelistFilter(filter, workingCopy, "Review");
        workingCopy.SetSelectedEntries([alpha]);
        var commitTask = SvnOperationDialog.ShowCommitAsync(repository, window);
        var commit = await WaitForOperationDialog(window);
        var choices = FindNamedControl<StackPanel>(commit, "CommitCandidates").Children.OfType<CheckBox>().ToArray();
        var selectedChoice = choices.Single(choice => Avalonia.Automation.AutomationProperties.GetName(choice)
            .StartsWith("modified-alpha.txt,", StringComparison.Ordinal));
        var addedChoice = choices.Single(choice => Avalonia.Automation.AutomationProperties.GetName(choice)
            .StartsWith("added.txt,", StringComparison.Ordinal));
        Check(choices.Length == 2 && selectedChoice.IsChecked == true && addedChoice.IsChecked != true &&
                    FindNamedControl<Control>(commit, "CommitChangelistScope").IsVisible &&
                    FindNamedControl<TextBlock>(commit, "CommitChangelistScope").Text == workingCopy.ChangelistScopeText,
            "Commit candidates and the explicit checklist are scoped to the active changelist");
        var message = FindNamedControl<TextBox>(commit, "CommitMessage");
        message.Text = "Review scoped commit";
        Dispatcher.UIThread.RunJobs();
        var accept = FindNamedControl<Button>(commit, "AcceptButton");
        Check(accept.IsEnabled, "scoped Commit requires and accepts an explicit message and checked file");

        var hook = InstallRejectingPreCommitHook(fixture.Repository);
        accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await commitTask;
        await WaitForWorkingCopy(workingCopy);
        Check(workingCopy.HasActionError && repository.CommitMessageDraft == "Review scoped commit" &&
            await GetChangelist(fixture.WorkingCopy, "modified-alpha.txt", config) == "Review" &&
            await GetChangelist(fixture.WorkingCopy, "added.txt", config) == "Review",
            "failed Commit retains its message draft and all changelist memberships");
        File.Delete(hook);

        workingCopy.SetSelectedEntries([FindWorkingCopyEntry(workingCopy, "modified-alpha.txt")]);
        commitTask = SvnOperationDialog.ShowCommitAsync(repository, window);
        commit = await WaitForOperationDialog(window);
        choices = FindNamedControl<StackPanel>(commit, "CommitCandidates").Children.OfType<CheckBox>().ToArray();
        selectedChoice = choices.Single(choice => Avalonia.Automation.AutomationProperties.GetName(choice)
            .StartsWith("modified-alpha.txt,", StringComparison.Ordinal));
        addedChoice = choices.Single(choice => Avalonia.Automation.AutomationProperties.GetName(choice)
            .StartsWith("added.txt,", StringComparison.Ordinal));
        message = FindNamedControl<TextBox>(commit, "CommitMessage");
        Check(choices.Length == 2 && selectedChoice.IsChecked == true && addedChoice.IsChecked != true &&
            message.Text == "Review scoped commit",
            "Commit retry restores the message and leaves the unchecked Review member out of scope");
        accept = FindNamedControl<Button>(commit, "AcceptButton");
        accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await commitTask;
        await WaitForWorkingCopy(workingCopy);
        Check(!workingCopy.HasActionError && repository.CommitMessageDraft.Length == 0,
            "successful scoped Commit clears the message draft");
        Check(await RunCapture("svnlook", "cat", fixture.Repository, "modified-alpha.txt") == "alpha edit\n" &&
            await RunCapture("svnlook", "cat", fixture.Repository, "modified-beta.txt") == "base line\n" &&
            !(await RunCapture("svnlook", "tree", fixture.Repository))
                .Split('\n').Any(line => line.Trim() == "added.txt"),
            "Commit updates only the checked Review path in repository HEAD");
        Check(File.ReadAllText(Path.Combine(fixture.WorkingCopy, "modified-alpha.txt")) == "alpha edit\n" &&
            File.ReadAllText(Path.Combine(fixture.WorkingCopy, "added.txt")) == "added local line\n" &&
            await GetChangelist(fixture.WorkingCopy, "modified-alpha.txt", config) == null &&
            await GetChangelist(fixture.WorkingCopy, "added.txt", config) == "Review",
            "normal Commit preserves local bytes, clears the committed member, and retains unchecked membership");

        SetChangelistFilter(filter, workingCopy, null);
        added = FindWorkingCopyEntry(workingCopy, "added.txt");
        unassigned = FindWorkingCopyEntry(workingCopy, "unversioned.txt");
        workingCopy.SetSelectedEntries([added, unassigned]);
        Check(!workingCopy.CanRemoveFromChangelist,
            "remove-from-changelist requires every selected eligible file to be assigned");
        SetChangelistFilter(filter, workingCopy, "Review");
        workingCopy.SetSelectedEntries([added]);
        Check(workingCopy.CanRemoveFromChangelist, "an assigned regular file is eligible for membership removal");
        await OpenContextAction(window, grid, added, "RemoveSvnChangelist");
        var confirmation = await WaitForOperationDialog(window);
        Check(FindNamedControl<TextBlock>(confirmation, "ConfirmationMessage").Text.Contains("added.txt", StringComparison.Ordinal),
            "the working-copy row context menu opens a path-specific membership confirmation");
        FindNamedControl<Button>(confirmation, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !confirmation.IsVisible);
        Check(await GetChangelist(fixture.WorkingCopy, "added.txt", config) == "Review",
            "cancelling membership removal leaves the assignment unchanged");
        workingCopy.SetSelectedEntries([FindWorkingCopyEntry(workingCopy, "added.txt")]);
        await OpenSelectedAction(window, "RemoveSvnChangelist");
        confirmation = await WaitForOperationDialog(window);
        FindNamedControl<Button>(confirmation, "AcceptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForWorkingCopy(workingCopy);
        Check(await GetChangelist(fixture.WorkingCopy, "added.txt", config) == null &&
            File.ReadAllText(Path.Combine(fixture.WorkingCopy, "added.txt")) == "added local line\n",
            "confirmed removal clears only changelist membership and keeps the scheduled addition intact");

        SetChangelistFilter(filter, workingCopy, "Existing");
        beta = FindWorkingCopyEntry(workingCopy, "modified-beta.txt");
        var clean = FindWorkingCopyEntry(workingCopy, "clean-member.txt");
        workingCopy.SetSelectedEntries([beta, clean]);
        Check(workingCopy.CanRemoveFromChangelist,
            "multiple assigned modified and clean regular files can be removed together");
        await OpenSelectedAction(window, "RemoveSvnChangelist");
        confirmation = await WaitForOperationDialog(window);
        var removalMessage = FindNamedControl<TextBlock>(confirmation, "ConfirmationMessage").Text;
        Check(removalMessage.Contains("modified-beta.txt", StringComparison.Ordinal) &&
            removalMessage.Contains("clean-member.txt", StringComparison.Ordinal),
            "multi-file removal confirmation names the entire selected membership scope");
        FindNamedControl<Button>(confirmation, "AcceptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForWorkingCopy(workingCopy);
        Check(await GetChangelist(fixture.WorkingCopy, "modified-beta.txt", config) == null &&
            await GetChangelist(fixture.WorkingCopy, "clean-member.txt", config) == null &&
            File.ReadAllText(Path.Combine(fixture.WorkingCopy, "modified-beta.txt")) == "beta edit\n" &&
            await GetProperty(fixture.WorkingCopy, "modified-beta.txt", "fixture:preserve", config) == "keep this property" &&
            File.ReadAllText(Path.Combine(fixture.WorkingCopy, "clean-member.txt")) == "base line\n",
            "confirmed multi-file removal preserves local text and property edits");
    }

    private static async Task<(string Repository, string WorkingCopy)> CreateChangelistFixture(string temporary, string config)
    {
        var repository = Path.Combine(temporary, "changelists-repository");
        var workingCopy = Path.Combine(temporary, "changelists 工作 copy");
        await Run("svnadmin", "create", repository);
        var url = new Uri(repository + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        await Run("svn", "checkout", "--config-dir", config, url, workingCopy);
        foreach (var name in new[]
        {
            "modified-alpha.txt", "modified-beta.txt", "modified-stale.txt", "modified-unassigned.txt",
            "clean-member.txt", "clean-unassigned.txt", "clean-ungrouped-member.txt",
        })
            File.WriteAllText(Path.Combine(workingCopy, name), "base line\n");
        await Run("svn", "add", "--force", "--", workingCopy + "@");
        await Run("svn", "commit", "--config-dir", config, "-m", "changelist fixture baseline", "--", workingCopy + "@");
        await Run("svn", "update", "--config-dir", config, "--", workingCopy + "@");
        await Run("svn", "changelist", "Existing", "--", Path.Combine(workingCopy, "clean-member.txt") + "@");
        await Run("svn", "changelist", "Ungrouped", "--", Path.Combine(workingCopy, "clean-ungrouped-member.txt") + "@");
        File.WriteAllText(Path.Combine(workingCopy, "modified-alpha.txt"), "alpha edit\n");
        File.WriteAllText(Path.Combine(workingCopy, "modified-beta.txt"), "beta edit\n");
        await Run("svn", "propset", "fixture:preserve", "keep this property", "--", Path.Combine(workingCopy, "modified-beta.txt") + "@");
        File.WriteAllText(Path.Combine(workingCopy, "modified-stale.txt"), "stale edit\n");
        File.WriteAllText(Path.Combine(workingCopy, "modified-unassigned.txt"), "unassigned edit\n");
        File.WriteAllText(Path.Combine(workingCopy, "added.txt"), "added local line\n");
        await Run("svn", "add", "--", Path.Combine(workingCopy, "added.txt") + "@");
        await Run("svn", "changelist", "All", "--", Path.Combine(workingCopy, "modified-unassigned.txt") + "@");
        File.WriteAllText(Path.Combine(workingCopy, "unversioned.txt"), "unversioned local line\n");
        return (repository, workingCopy);
    }

    private static async Task ApplyChangelistDialog(SvnChangelistDialog dialog, SvnWorkingCopy workingCopy)
    {
        Check(dialog.CanApply, "changelist assignment form is valid before Apply");
        FindNamedControl<Button>(dialog, "AssignChangelistButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !dialog.IsVisible && !workingCopy.IsBusy);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<SvnChangelistDialog> WaitForChangelistDialog(Window owner)
    {
        await WaitUntil(() => owner.OwnedWindows.OfType<SvnChangelistDialog>().Any(dialog => dialog.IsVisible));
        return owner.OwnedWindows.OfType<SvnChangelistDialog>().Single(dialog => dialog.IsVisible);
    }

    private static void SetChangelistFilter(ComboBox filter, SvnWorkingCopy workingCopy, string name)
    {
        var selected = workingCopy.ChangelistFilters.Single(item => item.Name == name && !item.IsUngrouped);
        filter.SelectedItem = selected;
        Dispatcher.UIThread.RunJobs();
        Check(workingCopy.SelectedChangelistFilter == selected,
            "visible changelist filter selection updates the working-copy scope to " + (name ?? "All"));
        Check(filter.SelectedItem is SvnChangelistFilter visibleFilter && visibleFilter == selected,
            "changelist selector displays the active scope after changing filters");
    }

    private static async Task OpenSelectedAction(Window window, string name)
    {
        var button = FindNamedControl<Button>(window, "WorkingCopySelectedActions");
        Check(button.IsEnabled, "selected actions is enabled before opening " + name);
        button.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        await FocusAndInvokeMenuItem(window, name);
    }

    private static async Task OpenContextAction(Window window, DataGrid grid, SvnWorkingCopyEntry entry, string name)
    {
        window.UpdateLayout();
        grid.UpdateLayout();
        await WaitUntil(() => grid.GetVisualDescendants().OfType<DataGridRow>()
            .Any(row => ReferenceEquals(row.DataContext, entry) && row.IsVisible && row.Bounds.Width > 0 && row.Bounds.Height > 0));
        // DataGrid can retain recycled rows with the same data context after filtering.
        var row = grid.GetVisualDescendants().OfType<DataGridRow>().First(candidate =>
            ReferenceEquals(candidate.DataContext, entry) && candidate.IsVisible && candidate.Bounds.Width > 0 && candidate.Bounds.Height > 0);
        var point = row.TranslatePoint(new Point(12, row.Bounds.Height / 2), window);
        Check(point.HasValue, "visible row has a context-menu target: " + entry.Path);
        window.MouseMove(point.Value, RawInputModifiers.None);
        window.MouseDown(point.Value, MouseButton.Right, RawInputModifiers.None);
        window.MouseUp(point.Value, MouseButton.Right, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        await FocusAndInvokeMenuItem(window, name);
    }

    private static async Task FocusAndInvokeMenuItem(Window window, string name)
    {
        var topLevel = TopLevel.GetTopLevel(window);
        window.KeyPress(Key.Home, RawInputModifiers.None);
        window.KeyRelease(Key.Home, RawInputModifiers.None);
        for (var index = 0; index < 32; index++)
        {
            if (topLevel?.FocusManager?.GetFocusedElement() is MenuItem focused && focused.Name == name)
            {
                Check(focused.IsEnabled, "actual working-copy menu item is enabled: " + name);
                window.KeyPress(Key.Enter, RawInputModifiers.None);
                window.KeyRelease(Key.Enter, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                return;
            }
            window.KeyPress(Key.Down, RawInputModifiers.None);
            window.KeyRelease(Key.Down, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }
        throw new InvalidOperationException($"Could not focus actual menu action {name}; focused={topLevel?.FocusManager?.GetFocusedElement()?.GetType().Name}.");
    }

    private static async Task<string> GetChangelist(string workingCopy, string relativePath, string config)
    {
        var xml = await RunCapture("svn", "status", "--xml", "--config-dir", config, "--", workingCopy + "@");
        var expectedPath = Path.GetFullPath(Path.Combine(workingCopy, relativePath)).Replace('\\', '/');
        var entry = XDocument.Parse(xml).Descendants("entry")
            .SingleOrDefault(candidate =>
            {
                var path = (string)candidate.Attribute("path");
                if (string.IsNullOrEmpty(path))
                    return false;
                path = path.Replace('\\', '/');
                var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingCopy, path)).Replace('\\', '/');
                return string.Equals(fullPath, expectedPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            });
        return entry?.Parent?.Name.LocalName == "changelist" ? (string)entry.Parent.Attribute("name") : null;
    }

    private static string InstallRejectingPreCommitHook(string repository)
    {
        var hook = Path.Combine(repository, "hooks", OperatingSystem.IsWindows() ? "pre-commit.bat" : "pre-commit");
        File.WriteAllText(hook, OperatingSystem.IsWindows()
            ? "@echo off\r\n@echo blocked by changelist regression fixture 1>&2\r\n@exit /b 1\r\n"
            : "#!/bin/sh\necho 'blocked by changelist regression fixture' >&2\nexit 1\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return hook;
    }

    private static string CreateBlockingChangelistSvn(string directory, string realSvn, string gate, string started)
    {
        var wrapper = Path.Combine(directory, "blocking-svn");
        File.WriteAllText(wrapper,
            "#!/bin/sh\n" +
            "if [ \"$1\" = changelist ]; then\n" +
            ": > " + QuoteShellArgument(started) + "\n" +
            "while [ -e " + QuoteShellArgument(gate) + " ]; do /bin/sleep 0.02; done\n" +
            "fi\nexec " + QuoteShellArgument(realSvn) + " \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(gate, string.Empty);
        return wrapper;
    }

    private static string QuoteShellArgument(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
