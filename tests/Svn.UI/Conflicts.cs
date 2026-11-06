using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SourceGit;
using SourceGit.ViewModels;

internal static partial class Program
{
    private static async Task<int> RunConflictsOnly(string temporary, string config)
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(Program));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
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
                var window = new Window { Width = 800, Height = 540 };
                window.Show();
                await VerifyConflictUi(window, temporary, config, screenshotDirectory);
                window.Close();
                return true;
            }, timeout.Token);
            Console.WriteLine($"{s_checks} focused SVN conflict UI checks passed.");
            return 0;
        }
        finally
        {
            await Task.Run(session.Dispose).ConfigureAwait(false);
        }
    }

    private static async Task VerifyConflictUi(Window window, string temporary, string config, string screenshotDirectory)
    {
        var workingCopy = await CreateConflictUiFixture(temporary, config);
        using var repository = new SvnRepository(workingCopy);
        var node = new RepositoryNode { Id = workingCopy, Name = "SVN conflicts", IsRepository = true, IsSvn = true };
        window.Content = new SourceGit.Views.LauncherPage { DataContext = new LauncherPage(node, repository) };
        window.Width = 800;
        window.Height = 540;
        await repository.RefreshAsync();
        await WaitForPaths(repository);
        await WaitUntil(() => !repository.IsLoading && !repository.WorkingCopy.IsLoading);
        var entries = repository.WorkingCopy;
        if (entries.IsEmpty)
            await entries.RefreshAsync();
        var tabs = FindNamedControl<TabControl>(window, "RepositoryTabs");
        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var grid = FindNamedControl<DataGrid>(window, "WorkingCopyEntries");
        var actions = FindNamedControl<Button>(window, "WorkingCopySelectedActions");
        var textEntry = FindWorkingCopyEntry(entries, "text @ 文件.txt");
        var propertyEntry = FindWorkingCopyEntry(entries, "property.txt");
        var treeEntry = FindWorkingCopyEntry(entries, "tree.txt");
        Check(textEntry.Entry.Item == "conflicted" && propertyEntry.Entry.Properties == "conflicted" && treeEntry.Entry.IsTreeConflicted,
            "conflict UI fixture contains genuine text, property and tree conflicts");
        await SelectWorkingCopyEntries(entries, grid, textEntry, propertyEntry);
        Check(!entries.CanInspectConflict, "conflict inspection requires one exact path");
        await SelectWorkingCopyEntry(entries, grid, FindWorkingCopyEntry(entries, "keep-unversioned.txt"));
        Check(!entries.CanInspectConflict, "ordinary rows do not enable conflict inspection");
        await SelectWorkingCopyEntry(entries, grid, textEntry);
        Check(entries.CanInspectConflict && !entries.CanRevert && !entries.CanCommit,
            "a conflicted file enables inspection while discard and commit remain disabled");

        // Exercise the shared menu with keyboard input, then cancel using Escape.
        actions.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Home, RawInputModifiers.None);
        for (var index = 0; index < 2; index++)
        {
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        }
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        var preview = await WaitForConflictDialog(window);
        await WaitForConflictLoaded(preview);
        Console.WriteLine("Conflict text inspection loaded.");
        Check(!preview.Model.HasError && preview.Model.Path == textEntry.Path && preview.Model.HasTextConflict,
            "selected actions keyboard flow loads the selected conflict");
        Check(preview.Model.SelectedResolution == null && !preview.Model.CanResolve,
            "inspection never preselects a resolution");
        Check(preview.Model.FromVersions.Count >= 3 && preview.Model.ToVersions.Count >= 3,
            "text conflict comparison offers available versions");
        FindNamedControl<ComboBox>(preview, "ConflictVersionTo").SelectedItem =
            preview.Model.ToVersions.Single(version => version.Id == "base");
        await WaitUntil(() => preview.Model.CompareNotice == App.Text("SVN.Diff.Unchanged"));
        Check(preview.Model.DiffContent == null, "equal nonempty versions show no changes rather than an empty-file notice");
        FindNamedControl<ComboBox>(preview, "ConflictVersionTo").SelectedItem =
            preview.Model.ToVersions.Single(version => version.Id == "working");
        await WaitUntil(() => preview.Model.DiffContent != null || !string.IsNullOrEmpty(preview.Model.CompareNotice));
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            preview.Width = 760;
            preview.Height = 480;
            await SaveSettledScreenshot(preview, theme,
                Path.Combine(screenshotDirectory, "svn-conflict-text-" + theme + ".png"), "conflict text inspection");
            var resolve = FindNamedControl<Button>(preview, "ResolveConflictButton");
            Check(!string.IsNullOrWhiteSpace(resolve.Content?.ToString()), "disabled Resolve button retains a visible label");
            Check(FindNamedControl<ComboBox>(preview, "ConflictResolution").Bounds.Width >= 400,
                "resolution selector uses the available dialog width");
            var origin = resolve.TranslatePoint(new Point(), preview);
            Check(origin.HasValue && origin.Value.Y >= 0 && origin.Value.Y + resolve.Bounds.Height <= preview.ClientSize.Height,
                "conflict resolution footer stays visible in a compact window");
        }
        var originalBytes = File.ReadAllBytes(Path.Combine(workingCopy, textEntry.Path));
        preview.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitUntil(() => !preview.IsVisible);
        Check(File.ReadAllBytes(Path.Combine(workingCopy, textEntry.Path)).SequenceEqual(originalBytes) &&
            (await SourceGit.Native.Svn.GetStatusAsync(SourceGit.Native.Svn.ResolveExecutable(string.Empty), config, workingCopy))
                .Entries.Any(entry => entry.Path == textEntry.Path && entry.Item == "conflicted"),
            "cancelling conflict inspection preserves both content and conflict status");

        // A replacement requires a second, explicit confirmation; cancelling it preserves the conflict.
        var incomingTask = SourceGit.Views.SvnConflictDialog.ShowAsync(entries, textEntry, window);
        var incoming = await WaitForConflictDialog(window);
        await WaitForConflictLoaded(incoming);
        Console.WriteLine("Testing incoming resolution confirmation.");
        SelectConflictResolution(incoming, "theirs-full");
        var incomingButton = FindNamedControl<Button>(incoming, "ResolveConflictButton");
        Check(incomingButton.IsEnabled, "an explicit incoming choice enables resolution");
        incomingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var declined = await WaitForOperationDialog(incoming);
        var confirmationText = FindNamedControl<TextBlock>(declined, "ConfirmationMessage").Text;
        Check(confirmationText.Contains(textEntry.Path, StringComparison.Ordinal), "resolution confirmation identifies the exact path");
        FindNamedControl<Button>(declined, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !incoming.Model.IsResolving);
        Check(incoming.IsVisible && File.ReadAllBytes(Path.Combine(workingCopy, textEntry.Path)).SequenceEqual(originalBytes),
            "declining a replacement leaves inspection open and bytes unchanged");
        incomingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var accepted = await WaitForOperationDialog(incoming);
        FindNamedControl<Button>(accepted, "AcceptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !incoming.Model.IsResolving);
        Check(!incoming.Model.HasError,
            "incoming resolution succeeds: " + incoming.Model.ErrorMessage + " " + incoming.Model.ErrorDetails);
        await incomingTask.WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine("Incoming resolution completed.");
        Check(File.ReadAllText(Path.Combine(workingCopy, textEntry.Path)) == "incoming value\n" &&
            !entries.Entries.Any(entry => entry.Path == textEntry.Path && entry.Entry.Item == "conflicted") &&
            FindWorkingCopyEntry(entries, "mixed.txt").Entry.Item == "conflicted",
            "incoming resolution replaces only the reviewed file and refreshes conflict status");

        // Editing a reviewed file externally invalidates its snapshot; reload is required before acknowledging it.
        var manualEntry = FindWorkingCopyEntry(entries, "manual.txt");
        var manualTask = SourceGit.Views.SvnConflictDialog.ShowAsync(entries, manualEntry, window);
        var manual = await WaitForConflictDialog(window);
        await WaitForConflictLoaded(manual);
        Console.WriteLine("Testing cancellation and stale conflict recovery.");
        SelectConflictResolution(manual, "working");
        await manual.Model.ResolveAsync(_ =>
        {
            entries.Cancel();
            return Task.FromResult(true);
        });
        Check(manual.Model.HasError && manual.Model.NeedsReload && !entries.IsBusy &&
            FindWorkingCopyEntry(entries, "manual.txt").Entry.Item == "conflicted",
            "cancellation before resolution refreshes status and requires review before retrying");
        await ReloadConflictDetails(manual);
        File.WriteAllText(Path.Combine(workingCopy, "manual.txt"), "manually repaired value\n");
        SelectConflictResolution(manual, "working");
        await manual.Model.ResolveAsync(_ => Task.FromResult(true));
        Check(manual.Model.HasError && manual.Model.NeedsReload && !manual.Model.CanResolve &&
            FindWorkingCopyEntry(entries, "manual.txt").Entry.Item == "conflicted",
            "stale reviewed content cannot be resolved without reload");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(manual, theme,
                Path.Combine(screenshotDirectory, "svn-conflict-stale-" + theme + ".png"), "stale conflict recovery");
        await ReloadConflictDetails(manual);
        Check(!manual.Model.HasError && manual.Model.SelectedResolution == null,
            "reload recovers from a stale conflict snapshot and requires a new choice");
        SelectConflictResolution(manual, "working");
        await manual.Model.ResolveAsync(_ => Task.FromResult(true));
        Check(!manual.Model.HasError && File.ReadAllText(Path.Combine(workingCopy, "manual.txt")) == "manually repaired value\n" &&
            !entries.Entries.Any(entry => entry.Path == "manual.txt" && entry.Entry.Item == "conflicted"),
            "manual resolution preserves prepared content and clears conflict bookkeeping");
        manual.Close();
        await manualTask;

        // Property reports show the actual competing values; path-level working resolution keeps local values.
        propertyEntry = FindWorkingCopyEntry(entries, "property.txt");
        var propertyTask = SourceGit.Views.SvnConflictDialog.ShowAsync(entries, propertyEntry, window);
        var properties = await WaitForConflictDialog(window);
        await WaitForConflictLoaded(properties);
        Console.WriteLine("Testing property conflict resolution.");
        Check(properties.Model.HasPropertyConflict && !properties.Model.HasTextConflict &&
            properties.Model.PropertyRejectText.Contains("fixture:conflict", StringComparison.Ordinal) &&
            properties.Model.PropertyRejectText.Contains("incoming property", StringComparison.Ordinal) &&
            properties.Model.PropertyDiffText.Contains("local property", StringComparison.Ordinal),
            "property conflict inspection shows the real reject report and current values");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(properties, theme,
                Path.Combine(screenshotDirectory, "svn-conflict-property-" + theme + ".png"), "property conflict inspection");
        var propertyTab = FindNamedControl<TabItem>(properties, "PropertyConflictTab");
        var reportHeading = FindNamedControl<TextBlock>(properties, "PropertyReportHeading");
        var tabBottom = propertyTab.TranslatePoint(new Point(0, propertyTab.Bounds.Height), properties);
        var headingTop = reportHeading.TranslatePoint(new Point(), properties);
        var propertyContent = propertyTab.Content as Control;
        var propertyScroll = reportHeading.FindAncestorOfType<ScrollViewer>();
        Check(tabBottom.HasValue && headingTop.HasValue && headingTop.Value.Y >= tabBottom.Value.Y,
            $"the property report starts below the tab header without overlapping it; TabBottom={tabBottom}; HeadingTop={headingTop}; " +
            $"TabBounds={propertyTab.Bounds}; ContentBounds={propertyContent?.Bounds}; ContentOrigin={propertyContent?.TranslatePoint(new Point(), properties)}; " +
            $"ScrollOffset={propertyScroll?.Offset}; Viewport={propertyScroll?.Viewport}; Extent={propertyScroll?.Extent}");
        SelectConflictResolution(properties, "working");
        await properties.Model.ResolveAsync(_ => Task.FromResult(true));
        var localProperty = await RunCapture("svn", "propget", "fixture:conflict", "--", Path.Combine(workingCopy, "property.txt") + "@");
        Check(!properties.Model.HasError && localProperty.TrimEnd() == "local property" &&
            FindWorkingCopyEntry(entries, "property.txt").Entry.Properties == "modified",
            "marking a property conflict resolved retains the local property value");
        properties.Close();
        await propertyTask;

        var mixedTask = SourceGit.Views.SvnConflictDialog.ShowAsync(entries, FindWorkingCopyEntry(entries, "mixed.txt"), window);
        var mixed = await WaitForConflictDialog(window);
        await WaitForConflictLoaded(mixed);
        Check(mixed.Model.HasTextConflict && mixed.Model.HasPropertyConflict,
            "simultaneous text and property conflicts are identified together");
        File.WriteAllText(Path.Combine(workingCopy, "mixed.txt"), "repaired combined text\n");
        await ReloadConflictDetails(mixed);
        SelectConflictResolution(mixed, "working");
        await mixed.Model.ResolveAsync(message =>
        {
            Check(message.Contains("mixed.txt", StringComparison.Ordinal), "combined resolution confirms its path-level scope");
            return Task.FromResult(true);
        });
        var mixedStatus = FindWorkingCopyEntry(entries, "mixed.txt").Entry;
        Check(!mixed.Model.HasError && mixedStatus.Item == "modified" && mixedStatus.Properties == "modified" &&
            File.ReadAllText(Path.Combine(workingCopy, "mixed.txt")) == "repaired combined text\n",
            "one path-level working resolution clears both conflicts and preserves prepared content");
        mixed.Close();
        await mixedTask;

        treeEntry = FindWorkingCopyEntry(entries, "tree.txt");
        var treeTask = SourceGit.Views.SvnConflictDialog.ShowAsync(entries, treeEntry, window);
        var tree = await WaitForConflictDialog(window);
        await WaitForConflictLoaded(tree);
        Console.WriteLine("Testing manual tree conflict acknowledgment.");
        Check(tree.Model.HasTreeConflict && tree.Model.TreeDetails.Contains("delete", StringComparison.OrdinalIgnoreCase) &&
            tree.Model.ResolutionChoices.Count == 1, "tree inspection shows incoming deletion and only offers manual acknowledgment");
        SelectConflictResolution(tree, "working");
        Check(!tree.Model.CanResolve, "tree resolution requires a separate manual-repair acknowledgment");
        FindNamedControl<CheckBox>(tree, "ManualTreeChangesConfirmed").IsChecked = true;
        Check(tree.Model.CanResolve, "tree acknowledgment enables the explicit chosen resolution");
        await ReloadConflictDetails(tree);
        Check(FindNamedControl<CheckBox>(tree, "ManualTreeChangesConfirmed").IsChecked != true &&
            tree.Model.SelectedResolution == null && !tree.Model.CanResolve,
            "reloading tree details clears both the visible acknowledgment and the chosen resolution");
        SelectConflictResolution(tree, "working");
        FindNamedControl<CheckBox>(tree, "ManualTreeChangesConfirmed").IsChecked = true;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(tree, theme,
                Path.Combine(screenshotDirectory, "svn-conflict-tree-" + theme + ".png"), "tree conflict details");
        tree.Width = tree.MinWidth;
        tree.Height = tree.MinHeight;
        await SaveSettledScreenshot(tree, ThemeVariant.Dark,
            Path.Combine(screenshotDirectory, "svn-conflict-tree-minimum-dark.png"), "minimum-size tree conflict details");
        var acknowledgment = FindNamedControl<CheckBox>(tree, "ManualTreeChangesConfirmed");
        var cancel = FindNamedControl<Button>(tree, "CancelButton");
        var acknowledgmentOrigin = acknowledgment.TranslatePoint(new Point(), tree);
        var cancelOrigin = cancel.TranslatePoint(new Point(), tree);
        var inspection = FindNamedControl<TabControl>(tree, "ConflictTabs");
        var resolutionPanel = FindNamedControl<ComboBox>(tree, "ConflictResolution").FindAncestorOfType<StackPanel>();
        var inspectionBottom = inspection.TranslatePoint(new Point(0, inspection.Bounds.Height), tree);
        var resolutionTop = resolutionPanel?.TranslatePoint(new Point(), tree);
        Check(inspectionBottom.HasValue && resolutionTop.HasValue && inspectionBottom.Value.Y <= resolutionTop.Value.Y,
            "inspection content and resolution controls do not overlap at the minimum dialog size");
        Check(acknowledgmentOrigin.HasValue && cancelOrigin.HasValue &&
            acknowledgmentOrigin.Value.Y + acknowledgment.Bounds.Height <= cancelOrigin.Value.Y,
            "the minimum-size dialog keeps the tree acknowledgment above its footer");
        await tree.Model.ResolveAsync(_ => Task.FromResult(true));
        Check(!tree.Model.HasError && !FindWorkingCopyEntry(entries, "tree.txt").Entry.IsTreeConflicted &&
            File.ReadAllText(Path.Combine(workingCopy, "tree.txt")) == "local tree edit\n" &&
            File.ReadAllText(Path.Combine(workingCopy, "keep-unversioned.txt")) == "keep these bytes\n",
            "tree acknowledgment clears the marker while preserving the chosen structure and unrelated bytes");
        tree.Close();
        await treeTask;

        // A missing SVN artifact must be presented as unavailable, not as an empty version.
        var artifactPath = Path.Combine(workingCopy, "artifact.txt");
        var artifactInfo = XDocument.Parse(await RunCapture("svn", "info", "--xml", "--", artifactPath + "@"));
        var minePath = artifactInfo.Descendants("prev-wc-file").Single().Value;
        File.Delete(minePath);
        File.WriteAllText(artifactPath, "manually repaired without artifact\n");
        var unavailableTask = SourceGit.Views.SvnConflictDialog.ShowAsync(entries, FindWorkingCopyEntry(entries, "artifact.txt"), window);
        var unavailable = await WaitForConflictDialog(window);
        await WaitForConflictLoaded(unavailable);
        Check(!unavailable.Model.HasError && unavailable.Model.Snapshot.Mine.Content == null &&
            !unavailable.Model.FromVersions.Any(version => version.Id == "mine") &&
            !unavailable.Model.ResolutionChoices.Any(choice => choice.Accept == "mine-full"),
            "a missing artifact cannot be compared or selected as an empty local version");
        SelectConflictResolution(unavailable, "working");
        await unavailable.Model.ResolveAsync(_ => Task.FromResult(true));
        Check(!unavailable.Model.HasError && File.ReadAllText(artifactPath) == "manually repaired without artifact\n" &&
            !FindWorkingCopyEntry(entries, "artifact.txt").HasWarning,
            "manual resolution remains available when an optional conflict artifact is missing");
        unavailable.Close();
        await unavailableTask;
        Check(entries.CanRefresh && !entries.IsBusy && !entries.HasActionError,
            "working-copy controls recover after conflict operations");
        Console.WriteLine("Conflict inspection and resolution UI checks passed.");
    }

    private static async Task<SourceGit.Views.SvnConflictDialog> WaitForConflictDialog(Window owner)
    {
        await WaitUntil(() => owner.OwnedWindows.OfType<SourceGit.Views.SvnConflictDialog>().Any());
        return owner.OwnedWindows.OfType<SourceGit.Views.SvnConflictDialog>().Single();
    }

    private static Task WaitForConflictLoaded(SourceGit.Views.SvnConflictDialog dialog) =>
        WaitUntil(() => !dialog.Model.IsLoading && (dialog.Model.Snapshot != null || dialog.Model.HasError));

    private static async Task ReloadConflictDetails(SourceGit.Views.SvnConflictDialog dialog)
    {
        var reload = FindNamedControl<Button>(dialog, "ReloadConflictButton");
        Check(reload.IsEffectivelyVisible && reload.IsEnabled, "reload is available before resolving externally edited content");
        reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForConflictLoaded(dialog);
    }

    private static void SelectConflictResolution(SourceGit.Views.SvnConflictDialog dialog, string accept)
    {
        var choice = dialog.Model.ResolutionChoices.Single(item => item.Accept == accept);
        FindNamedControl<ComboBox>(dialog, "ConflictResolution").SelectedItem = choice;
        Dispatcher.UIThread.RunJobs();
        Check(!FindNamedControl<TextBlock>(dialog, "ResolutionPlaceholderText").IsVisible,
            "choosing a resolution immediately hides the placeholder");
    }

    private static async Task<string> CreateConflictUiFixture(string temporary, string config)
    {
        var repository = Path.Combine(temporary, "conflict-ui-repository");
        var workingCopy = Path.Combine(temporary, "conflict UI 工作 copy");
        var otherCopy = Path.Combine(temporary, "conflict-ui-other-copy");
        await Run("svnadmin", "create", repository);
        var url = new Uri(repository + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        await Run("svn", "checkout", "--config-dir", config, url, workingCopy);
        foreach (var name in new[] { "text @ 文件.txt", "manual.txt", "property.txt", "mixed.txt", "tree.txt", "artifact.txt" })
            File.WriteAllText(Path.Combine(workingCopy, name), "base value\n");
        await Run("svn", "add", "--force", "--", workingCopy + "@");
        foreach (var name in new[] { "property.txt", "mixed.txt" })
            await Run("svn", "propset", "fixture:conflict", "base property", "--", Path.Combine(workingCopy, name) + "@");
        await Run("svn", "commit", "--config-dir", config, "-m", "conflict UI baseline", "--", workingCopy + "@");
        await Run("svn", "checkout", "--config-dir", config, url, otherCopy);
        foreach (var name in new[] { "text @ 文件.txt", "manual.txt", "mixed.txt", "artifact.txt" })
        {
            File.WriteAllText(Path.Combine(workingCopy, name), "local value\n");
            File.WriteAllText(Path.Combine(otherCopy, name), "incoming value\n");
        }
        foreach (var name in new[] { "property.txt", "mixed.txt" })
        {
            await Run("svn", "propset", "fixture:conflict", "local property", "--", Path.Combine(workingCopy, name) + "@");
            await Run("svn", "propset", "fixture:conflict", "incoming property", "--", Path.Combine(otherCopy, name) + "@");
        }
        File.WriteAllText(Path.Combine(workingCopy, "tree.txt"), "local tree edit\n");
        await Run("svn", "delete", "--", Path.Combine(otherCopy, "tree.txt") + "@");
        await Run("svn", "commit", "--config-dir", config, "-m", "create incoming conflicts", "--", otherCopy + "@");
        await Run("svn", "update", "--config-dir", config, "--accept", "postpone", "--", workingCopy + "@");
        File.WriteAllText(Path.Combine(workingCopy, "keep-unversioned.txt"), "keep these bytes\n");
        return workingCopy;
    }
}
