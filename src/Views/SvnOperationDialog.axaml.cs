using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace SourceGit.Views
{
    public partial class SvnOperationDialog : ChromelessWindow
    {
        public SvnOperationDialog()
        {
            InitializeComponent();
            Opened += (_, _) =>
            {
                if (Screens.ScreenFromWindow(this) is { } screen)
                    Height = Math.Min(Height, Math.Max(MinHeight, screen.WorkingArea.Height / RenderScaling - 48));
            };
        }

        public static async Task ShowUpdateAsync(ViewModels.SvnRepository repo, string initialRevision = "HEAD", Window owner = null)
        {
            owner ??= (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (!repo.CanMutateWorkingCopy || owner == null)
                return;

            var dialog = new SvnOperationDialog();
            dialog.ConfigureUpdate(repo.FullPath, initialRevision);
            if (await dialog.ShowDialog<bool>(owner) && repo.CanMutateWorkingCopy)
                await repo.UpdateWorkingCopyAsync(dialog._revision);
        }

        public static async Task ShowCommitAsync(ViewModels.SvnRepository repo, Window owner = null)
        {
            owner ??= (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (!repo.CanMutateWorkingCopy || owner == null)
                return;

            var candidates = repo.WorkingCopy.GetCommitCandidates();
            var selected = repo.WorkingCopy.SelectedEntries.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
            var dialog = new SvnOperationDialog();
            dialog.ConfigureCommit(repo.FullPath, candidates, selected, repo.WorkingCopy);
            dialog.CommitMessage.Text = repo.CommitMessageDraft;
            var accepted = await dialog.ShowDialog<bool>(owner);
            repo.CommitMessageDraft = dialog.CommitMessage.Text ?? string.Empty;
            if (accepted && repo.CanMutateWorkingCopy)
            {
                repo.WorkingCopy.SetSelectedEntries(candidates.Where(entry => dialog._paths.Contains(entry.Path)));
                await repo.CommitWorkingCopyAsync(dialog._paths, dialog.CommitMessage.Text.Trim());
            }
        }

        public static async Task<bool> ShowConfirmationAsync(string title, string action, string message, Window owner = null)
        {
            owner ??= (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner == null)
                return false;

            var dialog = new SvnOperationDialog();
            dialog.Title = title;
            dialog.DialogTitle.Text = title;
            dialog.WorkingCopyHeading.IsVisible = false;
            dialog.UpdateForm.IsVisible = false;
            dialog.ConfirmationForm.IsVisible = true;
            dialog.ConfirmationMessage.Text = message;
            dialog.AcceptButton.Content = action;
            dialog.Height = 480;
            dialog._isConfirmation = true;
            dialog._ready = true;
            dialog.ValidateForm();
            dialog.Opened += (_, _) => dialog.CancelButton.Focus();
            return await dialog.ShowDialog<bool>(owner);
        }

        private readonly List<(ViewModels.SvnWorkingCopyEntry Entry, CheckBox CheckBox)> _candidates = new();
        private bool _isCommit;
        private bool _isConfirmation;
        private bool _ready;
        private string _revision = "HEAD";
        private IReadOnlyList<string> _paths = Array.Empty<string>();
        private ViewModels.SvnWorkingCopy _workingCopy;

        private void ConfigureUpdate(string path, string revision)
        {
            Height = 340;
            Title = App.Text("SVN.Update.ToRevision");
            DialogTitle.Text = Title;
            WorkingCopyPath.Text = path;
            AcceptButton.Content = App.Text("SVN.Update");
            LatestRevision.IsChecked = revision.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
            SpecificRevision.IsChecked = LatestRevision.IsChecked != true;
            RevisionInput.Text = LatestRevision.IsChecked == true ? string.Empty : revision;
            _ready = true;
            ValidateForm();
            Opened += (_, _) =>
            {
                if (SpecificRevision.IsChecked == true)
                    RevisionInput.Focus();
                else
                    CancelButton.Focus();
            };
        }

        private void ConfigureCommit(string path, IReadOnlyList<ViewModels.SvnWorkingCopyEntry> candidates,
            HashSet<string> selected, ViewModels.SvnWorkingCopy workingCopy)
        {
            _isCommit = true;
            _workingCopy = workingCopy;
            Title = App.Text("SVN.Commit");
            DialogTitle.Text = Title;
            WorkingCopyPath.Text = path;
            AcceptButton.Content = App.Text("SVN.Commit.Submit");
            UpdateForm.IsVisible = false;
            CommitForm.IsVisible = true;
            CommitChangelistScope.IsVisible = workingCopy?.HasChangelistFilter == true;
            CommitChangelistScope.Text = workingCopy?.ChangelistScopeText ?? string.Empty;
            foreach (var entry in candidates)
            {
                var check = new CheckBox
                {
                    Content = new TextBlock { Text = entry.Path + " — " + entry.StatusText, TextWrapping = TextWrapping.Wrap },
                    IsChecked = selected.Count == 0 || selected.Contains(entry.Path),
                };
                Avalonia.Automation.AutomationProperties.SetName(check, entry.Path + ", " + entry.StatusText);
                check.IsCheckedChanged += OnFormChanged;
                _candidates.Add((entry, check));
                CommitCandidates.Children.Add(check);
            }
            var excludedCount = selected.Count(path => !candidates.Any(entry => entry.Path == path));
            CommitSelectionNotice.IsVisible = excludedCount > 0;
            CommitSelectionNotice.Text = App.Text("SVN.Commit.ExcludedSelection", excludedCount);
            _ready = true;
            ValidateForm();
            Opened += (_, _) => CommitMessage.Focus();
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e) => ValidateForm();

        private void OnFormChanged(object sender, RoutedEventArgs e) => ValidateForm();

        private bool ValidateForm()
        {
            if (!_ready)
                return false;

            var error = string.Empty;
            if (_isConfirmation)
            {
                AcceptButton.IsEnabled = true;
                ValidationMessage.IsVisible = false;
                return true;
            }
            if (_isCommit)
            {
                _paths = _candidates.Where(candidate => candidate.CheckBox.IsChecked == true)
                    .Select(candidate => candidate.Entry.Path).ToArray();
                CandidateCount.Text = App.Text("SVN.Commit.SelectedCount", _paths.Count, _candidates.Count);
                if (_candidates.Count == 0)
                    error = App.Text("SVN.Commit.Empty");
                else if (_paths.Count == 0)
                    error = App.Text("SVN.Commit.SelectPaths");
                else
                {
                    foreach (var candidate in _candidates)
                    {
                        var entry = candidate.Entry;
                        if (entry.Entry.Item == "added" && !_paths.Contains(entry.Path) &&
                            _paths.Any(path => path.StartsWith(entry.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                        {
                            error = App.Text("SVN.Commit.AddedParent", entry.Path);
                            break;
                        }
                        if (entry.Entry.Item == "deleted" && entry.Entry.Kind is "dir" or "unknown" && _paths.Contains(entry.Path) &&
                            _candidates.Any(child => child.CheckBox.IsChecked != true && child.Entry.Path.StartsWith(
                                entry.Path + System.IO.Path.DirectorySeparatorChar,
                                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                        {
                            error = App.Text("SVN.Commit.DeletedChildren", entry.Path);
                            break;
                        }
                    }
                    if (string.IsNullOrEmpty(error) && _workingCopy != null &&
                        !_workingCopy.CanCommitSelection(_candidates.Where(candidate => candidate.CheckBox.IsChecked == true)
                            .Select(candidate => candidate.Entry).ToArray(), out var selectionError))
                        error = selectionError;
                    if (string.IsNullOrEmpty(error) && string.IsNullOrWhiteSpace(CommitMessage.Text))
                        error = App.Text("SVN.Commit.MessageRequired");
                }
            }
            else
            {
                RevisionInput.IsEnabled = SpecificRevision.IsChecked == true;
                var value = LatestRevision.IsChecked == true ? "HEAD" : RevisionInput.Text?.Trim() ?? string.Empty;
                if (value.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                    _revision = "HEAD";
                else if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0)
                    _revision = number.ToString(CultureInfo.InvariantCulture);
                else
                    error = App.Text("SVN.Update.InvalidRevision");
            }
            ValidationMessage.Text = error;
            ValidationMessage.IsVisible = !string.IsNullOrEmpty(error);
            AcceptButton.IsEnabled = string.IsNullOrEmpty(error);
            return AcceptButton.IsEnabled;
        }

        private void OnAccept(object sender, RoutedEventArgs e)
        {
            if (ValidateForm())
                Close(true);
            e.Handled = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Close(false);
            e.Handled = true;
        }
    }
}
