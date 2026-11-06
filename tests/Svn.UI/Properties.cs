using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;

using SourceGit;
using SourceGit.Native;
using SourceGit.ViewModels;

using SvnPropertiesDialog = SourceGit.Views.SvnPropertiesDialog;
using SvnIgnoreDialog = SourceGit.Views.SvnIgnoreDialog;
using SvnPropertyItem = SourceGit.ViewModels.SvnPropertyItem;

internal static partial class Program
{
    private static async Task<int> RunPropertiesOnly(string temporary, string config)
    {
        var (_, workingCopyPath) = await CreatePropertiesFixture(temporary, config);
        var session = HeadlessUnitTestSession.StartNew(typeof(Program));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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
                await VerifyPropertiesUi(window, workingCopyPath, config, screenshots);
                window.Close();
                return true;
            }, timeout.Token);

            Check(s_checks >= 35, "focused property and ignore workflow executed its checks");
            Console.WriteLine($"{s_checks} focused SVN property and ignore UI checks passed.");
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

    private static async Task<(string Repository, string WorkingCopy)> CreatePropertiesFixture(string temporary, string config)
    {
        var repository = Path.Combine(temporary, "properties-repository");
        var workingCopy = Path.Combine(temporary,
            OperatingSystem.IsWindows() ? "properties 工作 copy @" : "properties 工作 copy @ ");
        await Run("svnadmin", "create", repository);
        var url = new Uri(repository + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
        await Run("svn", "checkout", "--config-dir", config, url, workingCopy);

        var cleanPath = Path.Combine(workingCopy, "clean @ 文件.txt");
        var emptyPath = Path.Combine(workingCopy, "empty.txt");
        var binaryPath = Path.Combine(workingCopy, "binary.dat");
        var nestedDirectory = Path.Combine(workingCopy, "nested");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(cleanPath, "clean source bytes\n");
        File.WriteAllText(emptyPath, string.Empty);
        File.WriteAllBytes(binaryPath, [0, 1, 2, 255]);
        File.WriteAllText(Path.Combine(nestedDirectory, "tracked.txt"), "tracked nested source\n");
        var binaryProperty = Path.Combine(temporary, "binary-property-value");
        File.WriteAllBytes(binaryProperty, [0x01, 0xFE, 0xFF, 0x00]);

        await Run("svn", "add", "--force", "--", workingCopy + "@");
        await Run("svn", "propset", "fixture:keep", "keep file property", "--", cleanPath + "@");
        await Run("svn", "propset", "fixture:remove", "delete this property", "--", cleanPath + "@");
        await Run("svn", "propset", "--file", binaryProperty, "fixture:binary", "--", binaryPath + "@");
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(workingCopy, "special-link"), "clean @ 文件.txt");
            await Run("svn", "add", "--", Path.Combine(workingCopy, "special-link") + "@");
        }

        await Run("svn", "commit", "--config-dir", config, "-m", "property fixture baseline", "--", workingCopy + "@");
        await Run("svn", "update", "--config-dir", config, "--", workingCopy + "@");

        File.WriteAllText(Path.Combine(workingCopy, LiteralStarName), "literal star name\n");
        File.WriteAllText(Path.Combine(workingCopy, LiteralStarNeighbor), "wildcard neighbor\n");
        File.WriteAllText(Path.Combine(workingCopy, LiteralQuestionName), "literal question mark name\n");
        File.WriteAllText(Path.Combine(workingCopy, LiteralQuestionNeighbor), "question wildcard neighbor\n");
        File.WriteAllText(Path.Combine(workingCopy, "literal[name] @ 文件.txt"), "literal bracket name\n");
        File.WriteAllText(Path.Combine(workingCopy, "literaln @ 文件.txt"), "bracket wildcard neighbor\n");
        if (!OperatingSystem.IsWindows())
            File.WriteAllText(Path.Combine(workingCopy, "literal\\backslash @ 文件.txt"), "literal backslash name\n");
        Directory.CreateDirectory(Path.Combine(workingCopy, "ignored folder @"));
        File.WriteAllText(Path.Combine(workingCopy, "ignored folder @", "local.txt"), "ignored folder contents\n");
        File.WriteAllText(Path.Combine(nestedDirectory, "nested-unversioned.txt"), "nested unversioned bytes\n");
        return (repository, workingCopy);
    }

    private static async Task VerifyPropertiesUi(Window window, string workingCopyPath, string config, string screenshots)
    {
        using var repository = new SvnRepository(workingCopyPath);
        var node = new RepositoryNode { Id = workingCopyPath, Name = "SVN property fixture", IsRepository = true, IsSvn = true };
        window.Content = new SourceGit.Views.LauncherPage { DataContext = new LauncherPage(node, repository) };
        await repository.RefreshAsync();
        await WaitForPaths(repository);
        await WaitForWorkingCopy(repository.WorkingCopy);
        var workingCopy = repository.WorkingCopy;
        var tabControl = FindNamedControl<TabControl>(window, "RepositoryTabs");
        tabControl.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        await WaitForWorkingCopy(workingCopy);
        if (workingCopy.IsEmpty)
        {
            await workingCopy.RefreshAsync();
            await WaitForWorkingCopy(workingCopy);
        }
        var grid = FindNamedControl<DataGrid>(window, "WorkingCopyEntries");
        var fileOperations = FindNamedControl<Button>(window, "WorkingCopyFileOperations");
        Check(workingCopy.CanFileOperations && fileOperations.IsEnabled,
            "working-copy status is ready before opening its File operations menu");

        Check(workingCopy.SelectedEntries.Count == 0, "root Properties menu test begins with no selected rows");
        fileOperations.Focus();
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
        window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Console.WriteLine("File Operations focus after open: " + DescribeFocus(window));
        Check(!window.OwnedWindows.OfType<SvnPropertiesDialog>().Any(dialog => dialog.IsVisible),
            "opening the File operations menu does not open Properties before selection");
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Home, Avalonia.Input.RawInputModifiers.None);
        window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Home, Avalonia.Input.RawInputModifiers.None);
        for (var index = 0; index < 3; index++)
        {
            window.KeyPressQwerty(Avalonia.Input.PhysicalKey.ArrowDown, Avalonia.Input.RawInputModifiers.None);
            window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.ArrowDown, Avalonia.Input.RawInputModifiers.None);
        }
        var focusedMenuItem = TopLevel.GetTopLevel(window)?.FocusManager.GetFocusedElement() as MenuItem;
        Check(focusedMenuItem?.Name == "FileOperationsProperties",
            "keyboard navigation focuses the named Properties menu item; focused=" + DescribeFocus(window));
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
        window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
        var dialog = await WaitForPropertiesDialog(window);
        await WaitUntil(() => !dialog.Model.IsLoading && dialog.Model.IsLoaded);
        Check(FindNamedControl<TextBox>(dialog, "PropertiesPathInput").Text == "." &&
            FindNamedControl<TextBlock>(dialog, "WorkingCopyPath").Text == workingCopyPath,
            "Properties opens the working-copy root and displays its actual local path");
        var rootProperties = FindProperties(dialog);
        Check(rootProperties.Count == 0 && FindNamedControl<Control>(dialog, "PropertiesListEmptyMessage").IsVisible &&
            FindNamedControl<Button>(dialog, "AddPropertyButton").IsEnabled,
            "an initially property-free root loads as a successful empty snapshot and enables Add");

        dialog.Width = dialog.MinWidth;
        dialog.Height = dialog.MinHeight;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            await SaveSettledScreenshot(dialog, theme, Path.Combine(screenshots, "svn-properties-root-" + theme + ".png"),
                "Properties root dialog");
            var footer = FindNamedControl<Button>(dialog, "SavePropertyButton").TranslatePoint(new Point(), dialog);
            Check(footer.HasValue && footer.Value.Y >= 0 &&
                footer.Value.Y + FindNamedControl<Button>(dialog, "SavePropertyButton").Bounds.Height <= dialog.ClientSize.Height,
                "Properties action remains visible at minimum size in " + theme);
        }

        await dialog.LoadAsync(".");
        FindNamedControl<Button>(dialog, "AddPropertyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var rootName = FindNamedControl<TextBox>(dialog, "PropertyNameInput");
        var rootValue = FindNamedControl<TextBox>(dialog, "PropertyValueInput");
        rootName.Text = "fixture:empty";
        Dispatcher.UIThread.RunJobs();
        rootValue.Text = string.Empty;
        Dispatcher.UIThread.RunJobs();
        var savedEmpty = await dialog.SaveAsync();
        Check(savedEmpty,
            $"an empty property value is saved as a present property; CanSave={dialog.Model.CanSave}; " +
            $"IsNew={dialog.Model.IsNewProperty}; Name='{dialog.Model.CurrentName}'; Error={dialog.Model.ErrorMessage}; Details={dialog.Model.ErrorDetails}");
        var emptyPropertyNames = await ListPropertyNames(workingCopyPath, ".", config);
        var emptyPropertyValue = await GetProperty(workingCopyPath, ".", "fixture:empty", config);
        Check(emptyPropertyNames.Contains("fixture:empty", StringComparer.Ordinal) && emptyPropertyValue.Length == 0,
            $"empty property remains distinct from deletion; names=[{string.Join(", ", emptyPropertyNames)}], " +
            $"value={System.Text.Json.JsonSerializer.Serialize(emptyPropertyValue)}, utf8={Convert.ToHexString(Encoding.UTF8.GetBytes(emptyPropertyValue))}");
        await SelectProperty(dialog, "fixture:empty");
        FindNamedControl<Button>(dialog, "DeletePropertyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var deleteConfirmation = await WaitForOperationDialog(dialog);
        Check(dialog.IsVisible && (await ListPropertyNames(workingCopyPath, ".", config))
            .Contains("fixture:empty", StringComparer.Ordinal) &&
            FindNamedControl<TextBlock>(deleteConfirmation, "ConfirmationMessage").Text.Contains("fixture:empty", StringComparison.Ordinal) &&
            FindNamedControl<TextBlock>(deleteConfirmation, "ConfirmationMessage").Text.Contains("“.”", StringComparison.Ordinal),
            "Delete confirms the exact property name and path before removing the selected property");
        FindNamedControl<Button>(deleteConfirmation, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !deleteConfirmation.IsVisible);
        Check((await ListPropertyNames(workingCopyPath, ".", config)).Contains("fixture:empty", StringComparer.Ordinal),
            "cancelling property deletion leaves the empty property unchanged");
        FindNamedControl<Button>(dialog, "DeletePropertyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        deleteConfirmation = await WaitForOperationDialog(dialog);
        FindNamedControl<Button>(deleteConfirmation, "AcceptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !FindProperties(dialog).Any(property => property.Name == "fixture:empty"));
        Check(!(await ListPropertyNames(workingCopyPath, ".", config)).Contains("fixture:empty", StringComparer.Ordinal),
            "confirmed Delete removes the empty property instead of writing another empty value");

        var busyLoad = dialog.LoadAsync(".");
        Check(!FindNamedControl<TextBox>(dialog, "PropertiesPathInput").IsEnabled,
            "the path input is disabled while a property snapshot is loading");
        await busyLoad;
        Check(FindNamedControl<TextBox>(dialog, "PropertiesPathInput").IsEnabled,
            "the path input returns after the property snapshot finishes loading");

        await Run("svn", "propset", "fixture:root", "keep root property", "--", workingCopyPath + "@");
        await Run("svn", "propset", "svn:ignore", "*.cache\nkeep.tmp\n", "--", workingCopyPath + "@");
        await dialog.LoadAsync(".");
        Check(FindProperties(dialog).Any(property => property.Name == "fixture:root") &&
            FindProperties(dialog).Any(property => property.Name == "svn:ignore"),
            "root property reload shows unrelated properties and existing ignore patterns");

        const string cleanRelative = "clean @ 文件.txt";
        var cleanPath = Path.Combine(workingCopyPath, cleanRelative);
        await dialog.LoadAsync(cleanRelative);
        Check(FindNamedControl<TextBox>(dialog, "PropertiesPathInput").Text == cleanRelative &&
            FindProperties(dialog).Any(property => property.Name == "fixture:keep") &&
            FindProperties(dialog).Any(property => property.Name == "fixture:remove"),
            "a clean-source entry loads its own property snapshot with a literal @ path");
        await SelectProperty(dialog, "fixture:keep");
        var nameInput = FindNamedControl<TextBox>(dialog, "PropertyNameInput");
        Check(nameInput.IsReadOnly || !nameInput.IsEnabled,
            "an existing property name cannot be changed while editing its value");
        FindNamedControl<TextBox>(dialog, "PropertyValueInput").Text = "updated @ property value";
        Dispatcher.UIThread.RunJobs();
        Check(await dialog.SaveAsync(), "an existing property value can be updated");
        Check(await GetProperty(workingCopyPath, cleanRelative, "fixture:keep", config) == "updated @ property value",
            "the updated property value round-trips without changing its name");

        FindNamedControl<Button>(dialog, "AddPropertyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var customName = FindNamedControl<TextBox>(dialog, "PropertyNameInput");
        var customValue = FindNamedControl<TextBox>(dialog, "PropertyValueInput");
        customName.Text = "fixture:multiline";
        Dispatcher.UIThread.RunJobs();
        customValue.Focus();
        customValue.Text = "  leading whitespace\nline @ Unicode 終\n";
        Dispatcher.UIThread.RunJobs();
        customValue.CaretIndex = customValue.Text.Length;
        customValue.Focus();
        Dispatcher.UIThread.RunJobs();
        Check(customValue.IsFocused,
            "multiline Return targets the value editor; focus=" + DescribeFocus(dialog));
        dialog.KeyPressQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
        dialog.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Enter, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Check(dialog.IsVisible && !(await ListPropertyNames(workingCopyPath, cleanRelative, config))
            .Contains("fixture:multiline", StringComparer.Ordinal),
            "Enter in the multiline value editor inserts a newline without saving the draft");
        var exactValue = "  leading whitespace\nline @ Unicode 終\n\n";
        var multilineText = customValue.Text;
        var multilineName = dialog.Model.CurrentName;
        var multilineModelValue = dialog.Model.CurrentValue;
        var multilineCanSave = dialog.Model.CanSave;
        var savedMultiline = await dialog.SaveAsync();
        Check(multilineText == exactValue && savedMultiline,
            $"multiline input preserves leading whitespace and an explicit trailing newline; text={System.Text.Json.JsonSerializer.Serialize(multilineText)}; " +
            $"modelName={multilineName}; modelValue={System.Text.Json.JsonSerializer.Serialize(multilineModelValue)}; " +
            $"CanSave={multilineCanSave}; Error={dialog.Model.ErrorMessage}; Details={dialog.Model.ErrorDetails}");
        Check(await GetProperty(workingCopyPath, cleanRelative, "fixture:multiline", config) == exactValue,
            "SVN stores the exact Unicode, @, whitespace, and newline property bytes");

        await SelectProperty(dialog, "fixture:remove");
        Check(await dialog.DeleteProperty(), "an existing custom property can be deleted");
        Check(!(await ListPropertyNames(workingCopyPath, cleanRelative, config)).Contains("fixture:remove", StringComparer.Ordinal) &&
            await GetProperty(workingCopyPath, cleanRelative, "fixture:keep", config) == "updated @ property value",
            "deleting one property preserves unrelated properties on the same path");

        await SelectProperty(dialog, "fixture:keep");
        var pathValue = FindNamedControl<TextBox>(dialog, "PropertyValueInput");
        pathValue.Text = "attempted write to another path";
        Dispatcher.UIThread.RunJobs();
        Check(FindNamedControl<Button>(dialog, "AddPropertyButton").IsEnabled &&
            FindNamedControl<Button>(dialog, "SavePropertyButton").IsEnabled &&
            FindNamedControl<Button>(dialog, "DeletePropertyButton").IsEnabled,
            "the loaded source enables its selected property editor before a target switch");
        var pathInput = FindNamedControl<TextBox>(dialog, "PropertiesPathInput");
        pathInput.Text = "missing-target.txt";
        Dispatcher.UIThread.RunJobs();
        Check(!FindNamedControl<Button>(dialog, "AddPropertyButton").IsEnabled &&
            !FindNamedControl<Button>(dialog, "SavePropertyButton").IsEnabled &&
            !FindNamedControl<Button>(dialog, "DeletePropertyButton").IsEnabled,
            "editing the target path disables mutations until its property snapshot loads");
        Check(!await dialog.SaveAsync() &&
            await GetProperty(workingCopyPath, cleanRelative, "fixture:keep", config) == "updated @ property value",
            "an old source snapshot cannot be written under a newly typed target path");
        Check(!await dialog.LoadAsync("missing-target.txt") && dialog.Model.HasError &&
            !FindNamedControl<Button>(dialog, "AddPropertyButton").IsEnabled &&
            !FindNamedControl<Button>(dialog, "SavePropertyButton").IsEnabled &&
            !FindNamedControl<Button>(dialog, "DeletePropertyButton").IsEnabled,
            "failed target loading retains its error and keeps all stale-snapshot mutations disabled");
        await dialog.LoadAsync(cleanRelative);
        Check(!dialog.Model.HasError && FindProperties(dialog).Any(property => property.Name == "fixture:keep"),
            "explicitly reloading the original target restores only its fresh properties");

        await dialog.LoadAsync("binary.dat");
        var binary = await SelectProperty(dialog, "fixture:binary");
        Check(binary.IsBinary && FindNamedControl<TextBox>(dialog, "PropertyValueInput").IsReadOnly &&
            !FindNamedControl<Button>(dialog, "SavePropertyButton").IsEnabled &&
            !FindNamedControl<Button>(dialog, "DeletePropertyButton").IsEnabled &&
            FindNamedControl<Control>(dialog, "PropertyReadOnlyNotice").IsVisible,
            "binary property bytes are displayed read-only and cannot be rewritten or deleted as text");

        if (!OperatingSystem.IsWindows())
        {
            Check(!await dialog.LoadAsync("special-link") && dialog.Model.HasError &&
                dialog.Model.ErrorDetails.Contains("symbolic link", StringComparison.OrdinalIgnoreCase),
                "the Properties editor refuses to follow a working-copy symlink target");
            await dialog.LoadAsync(cleanRelative);
            dialog.AddProperty();
            FindNamedControl<TextBox>(dialog, "PropertyNameInput").Text = "svn:special";
            Dispatcher.UIThread.RunJobs();
            FindNamedControl<TextBox>(dialog, "PropertyValueInput").Text = "*";
            Dispatcher.UIThread.RunJobs();
            Check(!FindNamedControl<Button>(dialog, "SavePropertyButton").IsEnabled && !await dialog.SaveAsync() &&
                !(await ListPropertyNames(workingCopyPath, cleanRelative, config)).Contains("svn:special", StringComparer.Ordinal),
                "the editor refuses to create svn:special as an ordinary custom property");
        }

        // The dialog must retain a stale draft and its bounded diagnostic until the user reloads it.
        await dialog.LoadAsync(cleanRelative);
        FindNamedControl<Button>(dialog, "AddPropertyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        customName = FindNamedControl<TextBox>(dialog, "PropertyNameInput");
        customValue = FindNamedControl<TextBox>(dialog, "PropertyValueInput");
        customName.Text = "fixture:stale";
        Dispatcher.UIThread.RunJobs();
        customValue.Text = "draft preserved until reload";
        Dispatcher.UIThread.RunJobs();
        await Run("svn", "propset", "fixture:external", "changed outside the dialog", "--", cleanPath + "@");
        Check(!await dialog.SaveAsync() && dialog.Model.HasError && customName.Text == "fixture:stale" &&
            customValue.Text == "draft preserved until reload" &&
            FindNamedControl<Control>(dialog, "PropertiesErrorDetailsExpander").IsVisible,
            "stale snapshot failure keeps the draft and exposes bounded error details");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            await SaveSettledScreenshot(dialog, theme, Path.Combine(screenshots, "svn-properties-stale-" + theme + ".png"),
                "stale Properties error");
            var error = FindNamedControl<Control>(dialog, "PropertiesErrorDetailsExpander").TranslatePoint(new Point(), dialog);
            var footer = FindNamedControl<Button>(dialog, "ReloadPropertiesButton").TranslatePoint(new Point(), dialog);
            Check(error.HasValue && footer.HasValue && error.Value.Y >= 24 && error.Value.Y < footer.Value.Y,
                "stale error details remain above the dialog footer at minimum size in " + theme);
        }

        FindNamedControl<Button>(dialog, "ReloadPropertiesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var discard = await WaitForOperationDialog(dialog);
        Check(dialog.IsVisible && customValue.Text == "draft preserved until reload",
            "reload asks before discarding an unsaved stale draft");
        FindNamedControl<Button>(discard, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !discard.IsVisible);
        Check(dialog.IsVisible && customValue.Text == "draft preserved until reload" && dialog.Model.HasError,
            "declining discard retains the stale error and the exact draft");
        FindNamedControl<Button>(dialog, "ReloadPropertiesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        discard = await WaitForOperationDialog(dialog);
        FindNamedControl<Button>(discard, "AcceptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !discard.IsVisible);
        await WaitUntil(() => FindProperties(dialog).Any(property => property.Name == "fixture:external"));
        Check(!dialog.Model.HasError && !FindProperties(dialog).Any(property => property.Name == "fixture:stale"),
            "explicit reload fetches the external change, clears the stale error, and discards only the confirmed draft");

        FindNamedControl<Button>(dialog, "CancelPropertiesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntil(() => !dialog.IsVisible);

        await workingCopy.RefreshAsync();
        await WaitForWorkingCopy(workingCopy);
        var changed = FindWorkingCopyEntry(workingCopy, cleanRelative);
        Check(changed.Entry.Properties == "modified", "property edits appear in the production working-copy status");
        await SelectWorkingCopyEntry(workingCopy, grid, changed);
        Check(workingCopy.HasPropertyDiff && workingCopy.PropertyDiffContent.Contains("fixture:multiline", StringComparison.Ordinal) &&
            workingCopy.PropertyDiffContent.Contains("fixture:keep", StringComparison.Ordinal),
            "the selected source exposes the production property diff after edits");

        var rootUnversioned = FindWorkingCopyEntry(workingCopy, LiteralStarName);
        var literalNeighbor = FindWorkingCopyEntry(workingCopy, LiteralStarNeighbor);
        var questionUnversioned = FindWorkingCopyEntry(workingCopy, LiteralQuestionName);
        var questionNeighbor = FindWorkingCopyEntry(workingCopy, LiteralQuestionNeighbor);
        var bracketUnversioned = FindWorkingCopyEntry(workingCopy, "literal[name] @ 文件.txt");
        var bracketNeighbor = FindWorkingCopyEntry(workingCopy, "literaln @ 文件.txt");
        var backslashUnversioned = OperatingSystem.IsWindows() ? null :
            FindWorkingCopyPath(workingCopy, "literal\\backslash @ 文件.txt");
        var ignoredFolder = FindWorkingCopyEntry(workingCopy, "ignored folder @");
        var nestedUnversioned = FindWorkingCopyEntry(workingCopy, "nested-unversioned.txt");
        Check(rootUnversioned.Entry.Item == "unversioned" && literalNeighbor.Entry.Item == "unversioned" &&
            questionUnversioned.Entry.Item == "unversioned" && questionNeighbor.Entry.Item == "unversioned" &&
            bracketUnversioned.Entry.Item == "unversioned" && bracketNeighbor.Entry.Item == "unversioned" &&
            (backslashUnversioned == null || backslashUnversioned.Entry.Item == "unversioned") &&
            ignoredFolder.Entry.Item == "unversioned" && nestedUnversioned.Entry.Item == "unversioned",
            "ignore fixture starts with literal wildcard, neighbor, folder, and nested unversioned entries");

        workingCopy.SetSelectedEntries(new[] { rootUnversioned, questionUnversioned, bracketUnversioned, backslashUnversioned, ignoredFolder }
            .Where(entry => entry != null).ToArray());
        Check(workingCopy.CanIgnoreSelectedPaths, "same-parent unversioned selections enable Ignore");
        var ignoreTask = SvnIgnoreDialog.ShowAsync(workingCopy,
            new[] { rootUnversioned.Path, questionUnversioned.Path, bracketUnversioned.Path,
                backslashUnversioned?.Path, ignoredFolder.Path }.Where(path => path != null).ToArray(), window);
        var ignore = await WaitForIgnoreDialog(window);
        await WaitUntil(() => !ignore.Model.IsLoading && ignore.Model.IsLoaded);
        var selectedIgnorePaths = (FindNamedControl<ListBox>(ignore, "IgnoreSelectedPaths").ItemsSource as IEnumerable<string>)?.ToArray() ?? [];
        Check(FindNamedControl<TextBlock>(ignore, "IgnoreParentPath").Text == "." &&
            selectedIgnorePaths.Contains(rootUnversioned.Path, StringComparer.Ordinal) &&
            selectedIgnorePaths.Contains(ignoredFolder.Path, StringComparer.Ordinal),
            "Ignore reviews the exact selected unversioned file and folder under their shared parent");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            ignore.Width = ignore.MinWidth;
            ignore.Height = ignore.MinHeight;
            await SaveSettledScreenshot(ignore, theme, Path.Combine(screenshots, "svn-ignore-preview-" + theme + ".png"),
                "Ignore preview dialog");
            var button = FindNamedControl<Button>(ignore, "IgnoreButton").TranslatePoint(new Point(), ignore);
            Check(button.HasValue && button.Value.Y + FindNamedControl<Button>(ignore, "IgnoreButton").Bounds.Height <= ignore.ClientSize.Height,
                "Ignore action remains visible at minimum size in " + theme);
        }
        Check(await ignore.ApplyAsync(), "Ignore applies its reviewed patterns through the production backend");
        await ignoreTask.WaitAsync(TimeSpan.FromSeconds(10));
        await workingCopy.RefreshAsync();
        await WaitForWorkingCopy(workingCopy);
        Check(!workingCopy.Entries.Any(entry => entry.Path == rootUnversioned.Path || entry.Path == ignoredFolder.Path) &&
            workingCopy.Entries.Any(entry => entry.Path == literalNeighbor.Path) &&
            !workingCopy.Entries.Any(entry => entry.Path == questionUnversioned.Path || entry.Path == bracketUnversioned.Path) &&
            workingCopy.Entries.Any(entry => entry.Path == questionNeighbor.Path || entry.Path == bracketNeighbor.Path),
            (OperatingSystem.IsWindows()
                ? "exact basename and bracket escaping ignores only each selected basename and folder"
                : "literal *, ?, and [] escaping ignores only each selected basename and folder"));
        Check(File.ReadAllText(Path.Combine(workingCopyPath, rootUnversioned.Path)) == "literal star name\n" &&
            File.ReadAllText(Path.Combine(workingCopyPath, questionUnversioned.Path)) == "literal question mark name\n" &&
            File.ReadAllText(Path.Combine(workingCopyPath, bracketUnversioned.Path)) == "literal bracket name\n" &&
            File.ReadAllText(Path.Combine(workingCopyPath, ignoredFolder.Path, "local.txt")) == "ignored folder contents\n",
            "Ignore leaves selected local files and folder contents unchanged");
        if (backslashUnversioned != null)
            Check(File.ReadAllText(Path.Combine(workingCopyPath, backslashUnversioned.Path)) == "literal backslash name\n",
                "Unix literal backslash name remains byte-for-byte unchanged after Ignore");
        var ignoreValue = await GetProperty(workingCopyPath, ".", "svn:ignore", config);
        Check(ignoreValue.Contains("*.cache", StringComparison.Ordinal) && ignoreValue.Contains("keep.tmp", StringComparison.Ordinal) &&
            ignoreValue.Contains("literal", StringComparison.Ordinal),
            "Ignore appends escaped exact-name patterns while preserving existing ignore lines");
        Check(await GetProperty(workingCopyPath, ".", "fixture:root", config) == "keep root property",
            "Ignore preserves unrelated root properties");

        var selectedLiteral = FindWorkingCopyPath(workingCopy, literalNeighbor.Path);
        var selectedNested = FindWorkingCopyPath(workingCopy, nestedUnversioned.Path);
        workingCopy.SetSelectedEntries([selectedLiteral, selectedNested]);
        Dispatcher.UIThread.RunJobs();
        Check(workingCopy.SelectedEntries.Count == 2 && workingCopy.SelectedEntries.All(entry => entry.Entry.Item == "unversioned") &&
            !workingCopy.CanIgnoreSelectedPaths,
            "different-parent unversioned selections are rejected by the Ignore eligibility guard");
        var selectedActions = FindNamedControl<Button>(window, "WorkingCopySelectedActions");
        selectedActions.Focus();
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
        window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Check(!window.OwnedWindows.OfType<SvnIgnoreDialog>().Any(dialog => dialog.IsVisible),
            "the different-parent menu remains closed while its Ignore action is ineligible");
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
        window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
        var trackedClean = FindWorkingCopyPath(workingCopy, cleanRelative);
        workingCopy.SetSelectedEntries([selectedLiteral, trackedClean]);
        Dispatcher.UIThread.RunJobs();
        Check(!workingCopy.CanIgnoreSelectedPaths, "a tracked path makes the Ignore selection ineligible");
        selectedActions.Focus();
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
        window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Space, Avalonia.Input.RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Check(!window.OwnedWindows.OfType<SvnIgnoreDialog>().Any(dialog => dialog.IsVisible),
            "the mixed tracked selection leaves Ignore closed and ineligible");
        window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
        window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Escape, Avalonia.Input.RawInputModifiers.None);
        workingCopy.SetSelectedEntries([]);

        var commitTask = SourceGit.Views.SvnOperationDialog.ShowCommitAsync(repository, window);
        var commit = await WaitForOperationDialog(window);
        var commitChoices = FindNamedControl<StackPanel>(commit, "CommitCandidates").Children.OfType<CheckBox>().ToArray();
        foreach (var choice in commitChoices)
            choice.IsChecked = true;
        FindNamedControl<TextBox>(commit, "CommitMessage").Text = "Commit SVN property and ignore edits";
        Dispatcher.UIThread.RunJobs();
        Check(commitChoices.Any(check => Avalonia.Automation.AutomationProperties.GetName(check)
                .StartsWith(cleanRelative + ",", StringComparison.Ordinal)) &&
            FindNamedControl<Button>(commit, "AcceptButton").IsEnabled,
            "Commit explicitly offers the property-edited source and accepts the reviewed local changes");
        FindNamedControl<Button>(commit, "AcceptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await commitTask.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForWorkingCopy(workingCopy);
        var statusAfterCommit = await RunCapture("svn", "status", "--xml", "--config-dir", config, "--", workingCopyPath + "@");
        var commitEntriesAfter = string.Join("; ", workingCopy.Entries.Select(entry =>
            $"{entry.Path}[{entry.Entry.Item}/{entry.Entry.Properties}]"));
        Check(!workingCopy.HasActionError && workingCopy.Entries.All(entry => entry.Entry.Properties is not "modified" and not "conflicted"),
            $"explicit Commit clears local property changes; error={workingCopy.ActionErrorMessage}; details={workingCopy.ActionErrorDetails}; " +
            $"output={workingCopy.OperationOutput}; remaining=[{commitEntriesAfter}]; raw-status={statusAfterCommit}");
        Check(await GetProperty(workingCopyPath, cleanRelative, "fixture:multiline", config, revision: "HEAD") == exactValue &&
            await GetProperty(workingCopyPath, cleanRelative, "fixture:keep", config, revision: "HEAD") == "updated @ property value" &&
            await GetProperty(workingCopyPath, ".", "fixture:root", config, revision: "HEAD") == "keep root property" &&
            (await GetProperty(workingCopyPath, ".", "svn:ignore", config, revision: "HEAD")).Contains("literal", StringComparison.Ordinal),
            "committed file, root, and Ignore properties are present at repository HEAD");
        Check(File.ReadAllText(cleanPath) == "clean source bytes\n" && File.ReadAllText(Path.Combine(workingCopyPath, "empty.txt")).Length == 0,
            "property management and Commit preserve tracked file contents");

        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            await SaveSettledScreenshot(window, theme, Path.Combine(screenshots, "svn-property-results-" + theme + ".png"),
                "property and Ignore status results");
        Console.WriteLine("Screenshots: " + screenshots);
    }

    private static async Task<SvnPropertiesDialog> WaitForPropertiesDialog(Window owner)
    {
        await WaitUntil(() => owner.OwnedWindows.OfType<SvnPropertiesDialog>().Count(dialog => dialog.IsVisible) == 1);
        return owner.OwnedWindows.OfType<SvnPropertiesDialog>().Single(dialog => dialog.IsVisible);
    }

    private static async Task<SvnIgnoreDialog> WaitForIgnoreDialog(Window owner)
    {
        await WaitUntil(() => owner.OwnedWindows.OfType<SvnIgnoreDialog>().Count(dialog => dialog.IsVisible) == 1);
        return owner.OwnedWindows.OfType<SvnIgnoreDialog>().Single(dialog => dialog.IsVisible);
    }

    private static IReadOnlyList<SvnPropertyItem> FindProperties(SvnPropertiesDialog dialog) =>
        (FindNamedControl<ListBox>(dialog, "PropertyNames").ItemsSource as IEnumerable<SvnPropertyItem>)?.ToArray() ?? [];

    private static async Task<SvnPropertyItem> SelectProperty(SvnPropertiesDialog dialog, string name)
    {
        var property = FindProperties(dialog).Single(item => item.Name == name);
        FindNamedControl<ListBox>(dialog, "PropertyNames").SelectedItem = property;
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        return property;
    }

    private static async Task<string[]> ListPropertyNames(string workingCopy, string relativePath, string config)
    {
        var target = relativePath == "." ? workingCopy : Path.Combine(workingCopy, relativePath);
        var xml = await RunCapture("svn", "proplist", "--xml", "--config-dir", config, "--", target + "@");
        return XDocument.Parse(xml).Descendants("property")
            .Select(property => (string)property.Attribute("name"))
            .Where(name => name != null).ToArray();
    }

    private static SvnWorkingCopyEntry FindWorkingCopyPath(SvnWorkingCopy workingCopy, string exactPath) =>
        workingCopy.Entries.Single(entry => entry.Path.Equals(exactPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    private static string LiteralStarName => OperatingSystem.IsWindows()
        ? "literal-star-name @ 文件.txt" : "literal*name @ 文件.txt";

    private static string LiteralStarNeighbor => OperatingSystem.IsWindows()
        ? "literal-star-neighbor @ 文件.txt" : "literalXname @ 文件.txt";

    private static string LiteralQuestionName => OperatingSystem.IsWindows()
        ? "literal-question-name @ 文件.txt" : "literal?name @ 文件.txt";

    private static string LiteralQuestionNeighbor => OperatingSystem.IsWindows()
        ? "literal-question-neighbor @ 文件.txt" : "literalXquestion @ 文件.txt";

    private static string DescribeFocus(Control control)
    {
        var focused = TopLevel.GetTopLevel(control)?.FocusManager.GetFocusedElement();
        return focused is Control focusedControl
            ? $"{focusedControl.GetType().Name}#{focusedControl.Name ?? "<unnamed>"}"
            : focused?.GetType().Name ?? "<none>";
    }

    private static async Task<string> GetProperty(string workingCopy, string relativePath, string name, string config,
        string revision = null)
    {
        var target = relativePath == "." ? workingCopy : Path.Combine(workingCopy, relativePath);
        var args = new List<string> { "propget", "--strict", "--config-dir", config };
        if (revision != null)
        {
            args.Add("-r");
            args.Add(revision);
        }
        args.Add(name);
        args.Add("--");
        args.Add(target + "@");
        return await RunCapture("svn", args.ToArray());
    }
}
