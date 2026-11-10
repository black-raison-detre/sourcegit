using System;
using System.ComponentModel;
using System.IO;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class SvnRepository : UserControl
    {
        public SvnRepository()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => ObserveWorkingCopy();
            AttachedToVisualTree += (_, _) => ObserveWorkingCopy();
            DetachedFromVisualTree += (_, _) =>
            {
                if (_workingCopy != null)
                    _workingCopy.PropertyChanged -= OnWorkingCopyPropertyChanged;
                _workingCopy = null;
            };
            WorkingCopyEntries.AddHandler(PointerPressedEvent, OnWorkingCopyPointerPressed, RoutingStrategies.Tunnel);
            HistoryRevisions.AddHandler(PointerPressedEvent, OnHistoryPointerPressed, RoutingStrategies.Tunnel);
        }

        private ViewModels.SvnWorkingCopy _workingCopy;
        private bool _syncingSelection;

        private void ObserveWorkingCopy()
        {
            if (_workingCopy != null)
                _workingCopy.PropertyChanged -= OnWorkingCopyPropertyChanged;
            _workingCopy = (DataContext as ViewModels.SvnRepository)?.WorkingCopy;
            if (_workingCopy != null)
            {
                _workingCopy.PropertyChanged += OnWorkingCopyPropertyChanged;
                SynchronizeSelection();
            }
        }

        private void OnWorkingCopyPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModels.SvnWorkingCopy.SelectedEntries))
                SynchronizeSelection();
        }

        private void SynchronizeSelection()
        {
            if (_workingCopy == null || _syncingSelection)
                return;

            _syncingSelection = true;
            try
            {
                var selected = _workingCopy.SelectedEntries.ToArray();
                foreach (var item in WorkingCopyEntries.SelectedItems.Cast<ViewModels.SvnWorkingCopyEntry>().ToArray())
                {
                    if (!selected.Contains(item))
                        WorkingCopyEntries.SelectedItems.Remove(item);
                }
                foreach (var item in selected)
                {
                    if (!WorkingCopyEntries.SelectedItems.Contains(item))
                        WorkingCopyEntries.SelectedItems.Add(item);
                }
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        private void OnWorkingCopySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection || DataContext is not ViewModels.SvnRepository repo)
                return;

            _syncingSelection = true;
            try
            {
                repo.WorkingCopy.SetSelectedEntries(WorkingCopyEntries.SelectedItems.Cast<ViewModels.SvnWorkingCopyEntry>());
                if (e.AddedItems.Count > 0 && e.AddedItems[e.AddedItems.Count - 1] is ViewModels.SvnWorkingCopyEntry entry)
                    repo.WorkingCopy.SelectedEntry = entry;
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        private void OnWorkingCopyPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (DataContext is not ViewModels.SvnRepository repo || repo.WorkingCopy.IsBusy)
                return;

            var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(true);
            if (row?.DataContext is not ViewModels.SvnWorkingCopyEntry entry)
                return;

            if (e.GetCurrentPoint(WorkingCopyEntries).Properties.IsRightButtonPressed)
            {
                _syncingSelection = true;
                try
                {
                    if (!WorkingCopyEntries.SelectedItems.Contains(entry))
                    {
                        WorkingCopyEntries.SelectedItems.Clear();
                        WorkingCopyEntries.SelectedItems.Add(entry);
                    }
                    repo.WorkingCopy.SetSelectedEntries(WorkingCopyEntries.SelectedItems.Cast<ViewModels.SvnWorkingCopyEntry>());
                    repo.WorkingCopy.SelectedEntry = entry;
                }
                finally
                {
                    _syncingSelection = false;
                }
                WorkingCopyEntries.Focus();
            }
            else if (WorkingCopyEntries.SelectedItems.Contains(entry))
            {
                repo.WorkingCopy.SelectedEntry = entry;
            }
        }

        private void OnSelectedActions(object sender, RoutedEventArgs e)
        {
            OpenSelectedActions(sender as Control);
            e.Handled = true;
        }

        private void OnWorkingCopyContextRequested(object sender, ContextRequestedEventArgs e)
        {
            OpenSelectedActions(WorkingCopyEntries);
            e.Handled = true;
        }

        private void OpenSelectedActions(Control target)
        {
            if (DataContext is not ViewModels.SvnRepository repo || !repo.WorkingCopy.CanSelectedActions)
                return;

            var vm = repo.WorkingCopy;
            var menu = new ContextMenu();
            if (vm.SelectedEntries.Count == 1)
            {
                var entry = vm.SelectedEntries[0];
                var path = Path.GetFullPath(Path.Combine(repo.FullPath, entry.Path));
                var explore = new MenuItem { Header = App.Text("RevealFile"), IsEnabled = Path.Exists(path) };
                explore.Click += (_, _) => Native.OS.OpenInFileManager(path);
                menu.Items.Add(explore);
            }
            var copy = new MenuItem { Header = App.Text("SVN.WorkingCopy.CopyPaths") };
            copy.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(string.Join(Environment.NewLine, vm.SelectedEntries.Select(entry => entry.Path)));
            };
            menu.Items.Add(copy);
            var inspectConflict = new MenuItem
            {
                Name = "InspectSvnConflict",
                Header = App.Text("SVN.Conflict.Inspect"),
                IsEnabled = vm.CanInspectConflict,
            };
            inspectConflict.Click += async (_, _) =>
            {
                if (vm.SelectedEntries.Count == 1)
                    await SvnConflictDialog.ShowAsync(vm, vm.SelectedEntries[0], TopLevel.GetTopLevel(this) as Window);
            };
            ExplainDisabledAction(inspectConflict, "SVN.WorkingCopy.ConflictInspectEligibility");
            menu.Items.Add(inspectConflict);
            menu.Items.Add(CreatePathOperationItem("RenameSvnPath", "Rename", "rename", vm));
            menu.Items.Add(CreatePathOperationItem("MoveSvnPath", "Move", "move", vm));
            menu.Items.Add(CreatePathOperationItem("CopySvnPath", "Copy", "copy", vm));
            menu.Items.Add(new MenuItem { Header = "-" });
            var restoreMissing = new MenuItem
            {
                Name = "RestoreMissingSvnFiles",
                Header = App.Text("SVN.Repair.RestoreMenu"),
                IsEnabled = vm.CanRestoreMissingFiles,
            };
            ExplainDisabledAction(restoreMissing, "SVN.Repair.MissingEligibility");
            restoreMissing.Click += async (_, _) => await vm.RestoreMissingFilesAsync(message =>
                SvnOperationDialog.ShowConfirmationAsync(App.Text("SVN.Repair.RestoreTitle"),
                    App.Text("SVN.Repair.Restore"), message, TopLevel.GetTopLevel(this) as Window));
            menu.Items.Add(restoreMissing);
            var removeMissing = new MenuItem
            {
                Name = "RemoveMissingSvnFiles",
                Header = App.Text("SVN.Repair.RemoveMenu"),
                IsEnabled = vm.CanRemoveMissingFiles,
            };
            ExplainDisabledAction(removeMissing, "SVN.Repair.MissingEligibility");
            removeMissing.Click += async (_, _) => await vm.RemoveMissingFilesAsync(message =>
                SvnOperationDialog.ShowConfirmationAsync(App.Text("SVN.Repair.RemoveTitle"),
                    App.Text("SVN.Repair.Remove"), message, TopLevel.GetTopLevel(this) as Window));
            menu.Items.Add(removeMissing);
            var repairRename = new MenuItem
            {
                Name = "RepairSvnRename",
                Header = App.Text("SVN.Repair.RenameMenu"),
                IsEnabled = vm.CanRepairRename,
            };
            ExplainDisabledAction(repairRename, "SVN.Repair.RenameEligibility");
            repairRename.Click += async (_, _) =>
            {
                if (vm.CanRepairRename)
                    await SvnRepairDialog.ShowAsync(vm, vm.SelectedEntries[0].Path, TopLevel.GetTopLevel(this) as Window);
            };
            menu.Items.Add(repairRename);
            menu.Items.Add(new Separator());
            var add = new MenuItem { Header = App.Text("SVN.WorkingCopy.Add"), IsEnabled = vm.CanAdd };
            add.Click += OnAddWorkingCopy;
            ExplainDisabledAction(add, "SVN.WorkingCopy.SelectionEligibility");
            menu.Items.Add(add);
            var remove = new MenuItem { Header = App.Text("SVN.WorkingCopy.Remove"), IsEnabled = vm.CanRemove };
            remove.Click += OnRemoveWorkingCopy;
            ExplainDisabledAction(remove, "SVN.WorkingCopy.SelectionEligibility");
            menu.Items.Add(remove);
            var undo = new MenuItem { Header = App.Text("SVN.WorkingCopy.Undo"), IsEnabled = vm.CanUndo };
            undo.Click += OnUndoWorkingCopy;
            ExplainDisabledAction(undo, "SVN.WorkingCopy.SelectionEligibility");
            menu.Items.Add(undo);
            menu.Items.Add(new MenuItem { Header = "-" });
            var commit = new MenuItem { Header = App.Text("SVN.Commit"), IsEnabled = vm.CanCommit && repo.CanMutateWorkingCopy };
            commit.Click += async (_, _) => await SvnOperationDialog.ShowCommitAsync(repo, TopLevel.GetTopLevel(this) as Window);
            ExplainDisabledAction(commit, "SVN.WorkingCopy.SelectionEligibility");
            menu.Items.Add(commit);
            var revert = new MenuItem { Header = App.Text("SVN.WorkingCopy.Revert"), IsEnabled = vm.CanRevert };
            revert.Click += async (_, _) => await vm.RevertAsync(message => SvnOperationDialog.ShowConfirmationAsync(
                App.Text("SVN.WorkingCopy.RevertTitle"), App.Text("SVN.WorkingCopy.RevertTitle"), message, TopLevel.GetTopLevel(this) as Window));
            ExplainDisabledAction(revert, "SVN.WorkingCopy.RevertEligibility");
            menu.Items.Add(revert);
            menu.Items.Add(new MenuItem { Header = "-" });
            var selectedProperty = vm.SelectedEntries.Count == 1 ? vm.SelectedEntries[0] : null;
            var properties = new MenuItem
            {
                Name = "PropertiesSvnPath",
                Header = App.Text("SVN.Properties.Menu"),
                IsEnabled = selectedProperty != null && vm.CanEditProperties(selectedProperty),
            };
            ExplainDisabledAction(properties, "SVN.WorkingCopy.PropertiesEligibility");
            properties.Click += async (_, _) =>
            {
                if (selectedProperty != null)
                    await SvnPropertiesDialog.ShowAsync(vm, selectedProperty.Path, TopLevel.GetTopLevel(this) as Window);
            };
            menu.Items.Add(properties);

            var ignore = new MenuItem
            {
                Name = "IgnoreSvnPaths",
                Header = App.Text("SVN.Ignore.Menu"),
                IsEnabled = vm.CanIgnoreSelectedPaths,
            };
            ExplainDisabledAction(ignore, "SVN.WorkingCopy.IgnoreEligibility");
            ignore.Click += async (_, _) => await SvnIgnoreDialog.ShowAsync(vm,
                vm.SelectedEntries.Select(entry => entry.Path).ToArray(), TopLevel.GetTopLevel(this) as Window);
            menu.Items.Add(ignore);

            menu.Items.Add(new Separator());
            var assignChangelist = new MenuItem
            {
                Name = "AssignSvnChangelist",
                Header = App.Text("SVN.Changelist.AssignMenu"),
                IsEnabled = vm.CanAssignChangelist,
            };
            ExplainDisabledAction(assignChangelist, "SVN.Changelist.Eligibility");
            assignChangelist.Click += async (_, _) => await SvnChangelistDialog.ShowAsync(vm, TopLevel.GetTopLevel(this) as Window);
            menu.Items.Add(assignChangelist);
            var removeChangelist = new MenuItem
            {
                Name = "RemoveSvnChangelist",
                Header = App.Text("SVN.Changelist.RemoveMenu"),
                IsEnabled = vm.CanRemoveFromChangelist,
            };
            ExplainDisabledAction(removeChangelist, "SVN.Changelist.RemoveEligibility");
            removeChangelist.Click += async (_, _) => await vm.RemoveFromChangelistAsync(message =>
                SvnOperationDialog.ShowConfirmationAsync(App.Text("SVN.Changelist.RemoveMenu"),
                    App.Text("SVN.Changelist.RemoveMenu"), message, TopLevel.GetTopLevel(this) as Window));
            menu.Items.Add(removeChangelist);
            menu.Open(target);
        }

        private MenuItem CreatePathOperationItem(string name, string headerKey, string operation, ViewModels.SvnWorkingCopy workingCopy)
        {
            var entry = workingCopy.SelectedEntries.Count == 1 ? workingCopy.SelectedEntries[0] : null;
            var item = new MenuItem
            {
                Name = name,
                Header = App.Text("SVN.PathOperation." + headerKey + "Menu"),
                IsEnabled = entry != null && workingCopy.CanMoveOrCopy(entry),
            };
            if (!item.IsEnabled)
                ToolTip.SetTip(item, App.Text("SVN.WorkingCopy.PathOperationEligibility"));
            item.Click += async (_, _) =>
            {
                if (entry != null)
                    await SvnPathOperationDialog.ShowAsync(workingCopy, operation, entry.Path, TopLevel.GetTopLevel(this) as Window);
            };
            return item;
        }

        private static void ExplainDisabledAction(MenuItem item, string resource)
        {
            if (item.IsEnabled)
                return;

            ToolTip.SetTip(item, new TextBlock
            {
                Text = App.Text(resource),
                MaxWidth = 340,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            ToolTip.SetShowOnDisabled(item, true);
        }

        private void OnHistoryPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(HistoryRevisions).Properties.IsRightButtonPressed)
                return;

            var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(true);
            if (row?.DataContext is Native.SvnRevision revision)
            {
                HistoryRevisions.SelectedItem = revision;
                HistoryRevisions.Focus();
            }
        }

        private void OnHistoryContextRequested(object sender, ContextRequestedEventArgs e)
        {
            if (DataContext is not ViewModels.SvnRepository { SelectedRevision: { } revision } repo)
                return;

            var item = new MenuItem
            {
                Header = App.Text("SVN.Update.ToSelectedRevision", revision.Number),
                IsEnabled = repo.CanMutateWorkingCopy,
            };
            item.Click += async (_, _) => await SvnOperationDialog.ShowUpdateAsync(repo,
                revision.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), TopLevel.GetTopLevel(this) as Window);
            var menu = new ContextMenu();
            menu.Items.Add(item);
            menu.Open(HistoryRevisions);
            e.Handled = true;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await repo.WorkingCopy.RefreshAsync();
        }

        private async void OnRefreshWorkingCopy(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await repo.WorkingCopy.RefreshAsync();

            e.Handled = true;
        }

        private async void OnCleanupWorkingCopy(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await repo.WorkingCopy.CleanupAsync(message => SvnOperationDialog.ShowConfirmationAsync(
                    App.Text("SVN.Cleanup.Title"), App.Text("SVN.Cleanup.Run"), message, TopLevel.GetTopLevel(this) as Window));
            e.Handled = true;
        }

        private void OnShowAllChangelists(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                repo.WorkingCopy.ShowAllChangelists();
            e.Handled = true;
        }

        private void OnFileOperations(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo && repo.WorkingCopy.CanFileOperations)
            {
                var workingCopy = repo.WorkingCopy;
                var menu = new ContextMenu();
                menu.Items.Add(CreateFilePathOperationItem("FileOperationsRename", "Rename", "rename", workingCopy));
                menu.Items.Add(CreateFilePathOperationItem("FileOperationsMove", "Move", "move", workingCopy));
                menu.Items.Add(CreateFilePathOperationItem("FileOperationsCopy", "Copy", "copy", workingCopy));
                menu.Items.Add(new MenuItem { Header = "-" });

                var selected = workingCopy.SelectedEntries.Count == 1 && workingCopy.CanEditProperties(workingCopy.SelectedEntries[0])
                    ? workingCopy.SelectedEntries[0] : null;
                var path = selected?.Path ?? ".";
                var properties = new MenuItem
                {
                    Name = "FileOperationsProperties",
                    Header = App.Text("SVN.Properties.Menu"),
                    IsEnabled = workingCopy.CanEditPropertiesPath(path),
                };
                ExplainDisabledAction(properties, "SVN.WorkingCopy.PropertiesEligibility");
                properties.Click += async (_, _) => await SvnPropertiesDialog.ShowAsync(workingCopy,
                    path, TopLevel.GetTopLevel(this) as Window);
                menu.Items.Add(properties);
                menu.Open(WorkingCopyFileOperations);
            }

            e.Handled = true;
        }

        private MenuItem CreateFilePathOperationItem(string name, string headerKey, string operation,
            ViewModels.SvnWorkingCopy workingCopy)
        {
            var entry = workingCopy.SelectedEntries.Count == 1 && workingCopy.CanMoveOrCopy(workingCopy.SelectedEntries[0])
                ? workingCopy.SelectedEntries[0] : null;
            var item = new MenuItem
            {
                Name = name,
                Header = App.Text("SVN.PathOperation." + headerKey + "Menu"),
                IsEnabled = workingCopy.CanFileOperations,
            };
            item.Click += async (_, _) => await SvnPathOperationDialog.ShowAsync(workingCopy,
                operation, entry?.Path, TopLevel.GetTopLevel(this) as Window);
            return item;
        }

        private async void OnAddWorkingCopy(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await repo.WorkingCopy.AddAsync();

            e.Handled = true;
        }

        private async void OnRemoveWorkingCopy(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await repo.WorkingCopy.RemoveAsync(message => SvnOperationDialog.ShowConfirmationAsync(
                    App.Text("SVN.WorkingCopy.Remove"), App.Text("SVN.WorkingCopy.Remove"), message, TopLevel.GetTopLevel(this) as Window));

            e.Handled = true;
        }

        private async void OnUndoWorkingCopy(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await repo.WorkingCopy.UndoAsync();

            e.Handled = true;
        }

        private async void OnRetryWorkingCopyDiff(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await repo.WorkingCopy.RetryDiffAsync();

            e.Handled = true;
        }

        private void OnCancelWorkingCopyDiff(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                repo.WorkingCopy.CancelDiff();

            e.Handled = true;
        }

        private void OnWorkingCopyGotoFirstChange(object sender, RoutedEventArgs e)
        {
            WorkingCopyDiffPresenter.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.First);
            e.Handled = true;
        }

        private void OnWorkingCopyGotoPrevChange(object sender, RoutedEventArgs e)
        {
            WorkingCopyDiffPresenter.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Prev);
            e.Handled = true;
        }

        private void OnWorkingCopyGotoNextChange(object sender, RoutedEventArgs e)
        {
            WorkingCopyDiffPresenter.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Next);
            e.Handled = true;
        }

        private void OnWorkingCopyGotoLastChange(object sender, RoutedEventArgs e)
        {
            WorkingCopyDiffPresenter.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Last);
            e.Handled = true;
        }

        private async void OnRetry(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await repo.RetryAsync();

            e.Handled = true;
        }

        private async void OnLoadMore(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await repo.LoadMoreAsync();

            e.Handled = true;
        }

        private async void OnSignIn(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await repo.SignInAsync();

            e.Handled = true;
        }

        private async void OnRetryPaths(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await repo.RetryChangedPathsAsync();

            e.Handled = true;
        }

        private void OnCancelPaths(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                repo.CancelChangedPaths();

            e.Handled = true;
        }

        private async void OnRetryDiff(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await repo.RetryDiffAsync();

            e.Handled = true;
        }

        private void OnCancelDiff(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                repo.CancelDiff();

            e.Handled = true;
        }

        private void OnGotoFirstChange(object sender, RoutedEventArgs e)
        {
            HistoryDiffPresenter.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.First);
            e.Handled = true;
        }

        private void OnGotoPrevChange(object sender, RoutedEventArgs e)
        {
            HistoryDiffPresenter.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Prev);
            e.Handled = true;
        }

        private void OnGotoNextChange(object sender, RoutedEventArgs e)
        {
            HistoryDiffPresenter.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Next);
            e.Handled = true;
        }

        private void OnGotoLastChange(object sender, RoutedEventArgs e)
        {
            HistoryDiffPresenter.FindDescendantOfType<ThemedTextDiffPresenter>()?.GotoChange(ViewModels.BlockNavigationDirection.Last);
            e.Handled = true;
        }
    }
}
