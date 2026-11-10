using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    public partial class SvnRepairDialog : ChromelessWindow
    {
        public bool CanApply => _workingCopy?.CanFileOperations == true && !_busy && !_cancelRequested &&
            !_requiresReload && _snapshot != null;

        public SvnRepairDialog()
        {
            InitializeComponent();
            Title = App.Text("SVN.Repair.RenameTitle");
            DialogTitle.Text = Title;
            Closing += OnClosing;
            Opened += (_, _) => TargetInput.Focus();
            UpdateForm();
        }

        public SvnRepairDialog(ViewModels.SvnWorkingCopy workingCopy, string sourcePath) : this()
        {
            _workingCopy = workingCopy;
            _sourcePath = sourcePath;
            WorkingCopyPath.Text = workingCopy.FullPath;
            SourcePath.Text = sourcePath;
            UpdateForm();
        }

        public static async Task<bool> ShowAsync(ViewModels.SvnWorkingCopy workingCopy, string sourcePath, Window owner = null)
        {
            owner ??= (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner == null || workingCopy?.CanRepairRename != true)
                return false;
            return await new SvnRepairDialog(workingCopy, sourcePath).ShowDialog<bool>(owner);
        }

        public async Task<bool> PrepareAsync()
        {
            if (_busy || _cancelRequested || _requiresReload || _workingCopy?.CanFileOperations != true ||
                string.IsNullOrWhiteSpace(TargetInput.Text))
                return false;

            _snapshot = null;
            _busy = true;
            ShowError(string.Empty, string.Empty);
            using var cancellation = new CancellationTokenSource();
            _readCancellation = cancellation;
            UpdateForm();
            var target = TargetInput.Text;
            var task = Task.Run(() => Native.Svn.GetRenameRepairSnapshotAsync(
                Native.Svn.ResolveExecutable(ViewModels.Preferences.Instance.SvnInstallPath),
                ViewModels.Preferences.Instance.SvnConfigDirectory, _workingCopy.FullPath,
                _sourcePath, target, cancellation.Token), cancellation.Token);
            _pendingTask = task;
            try
            {
                var result = await task;
                if (result.Status == "Ready" && result.Snapshot != null && !_cancelRequested)
                {
                    _snapshot = result.Snapshot;
                    DestinationPreview.Text = _snapshot.SourceRelativePath + " → " + _snapshot.DestinationRelativePath;
                    return true;
                }
                ShowError(App.Text("SVN.Repair.ReviewFailed"), result.Details);
            }
            catch (OperationCanceledException)
            {
                ShowError(App.Text("SVN.Repair.ReloadRequired"), string.Empty);
            }
            catch (Exception ex)
            {
                ShowError(App.Text("SVN.Repair.ReviewFailed"), ex.Message);
            }
            finally
            {
                _readCancellation = null;
                _pendingTask = null;
                _busy = false;
                UpdateForm();
            }
            return false;
        }

        public async Task<bool> ApplyAsync()
        {
            if (!CanApply)
                return false;

            _busy = true;
            _isApplying = true;
            UpdateForm();
            var task = _workingCopy.RepairRenameAsync(_snapshot);
            _pendingTask = task;
            var succeeded = false;
            try
            {
                succeeded = await task;
                if (!succeeded)
                {
                    _requiresReload = true;
                    ShowError(App.Text("SVN.Repair.ReloadRequired"), _workingCopy.ActionErrorDetails);
                }
            }
            finally
            {
                _pendingTask = null;
                _busy = false;
                _isApplying = false;
                UpdateForm();
            }
            if (succeeded && IsVisible && !_cancelRequested && !_requiresReload && !_closeRequestRunning)
            {
                _allowClose = true;
                Close(true);
            }
            return succeeded;
        }

        public async Task ReloadAsync()
        {
            if (_busy || _cancelRequested || _workingCopy?.CanRefresh != true)
                return;

            _snapshot = null;
            _requiresReload = true;
            _busy = true;
            UpdateForm();
            _pendingTask = _workingCopy.RefreshAsync();
            try
            {
                await _pendingTask;
                if (!_workingCopy.HasError && !_cancelRequested)
                    _requiresReload = false;
                else
                    ShowError(App.Text("SVN.Repair.ReloadRequired"), _workingCopy.ErrorDetails);
            }
            finally
            {
                _pendingTask = null;
                _busy = false;
                UpdateForm();
            }
            if (!_requiresReload)
                await PrepareAsync();
        }

        private void UpdateForm()
        {
            if (ApplyRepairButton == null)
                return;
            ApplyRepairButton.IsEnabled = CanApply;
            PrepareRepairButton.IsEnabled = !_busy && !_cancelRequested && !_requiresReload &&
                _workingCopy?.CanFileOperations == true && !string.IsNullOrWhiteSpace(TargetInput.Text);
            ReloadRepairButton.IsEnabled = !_busy && !_cancelRequested && _workingCopy?.CanRefresh == true;
            TargetInput.IsEnabled = !_busy;
            BrowseButton.IsEnabled = !_busy;
            CancelRepairButton.Content = App.Text(_cancelRequested ? "SVN.PathOperation.Canceling" : _busy ? "SVN.Changelist.CancelOperation" : "Cancel");
            CancelRepairButton.IsEnabled = !_cancelRequested;
            RepairBusyMessage.IsVisible = _busy;
            RepairBusyMessage.Text = App.Text(_isApplying ? "SVN.Repair.Applying" : "SVN.Repair.Reviewing");
            if (_snapshot == null)
                DestinationPreview.Text = App.Text("SVN.Repair.ReviewRequired");
        }

        private void ShowError(string message, string details)
        {
            RepairErrorPanel.IsVisible = !string.IsNullOrEmpty(message);
            RepairErrorMessage.Text = message;
            RepairErrorDetails.Text = details ?? string.Empty;
            RepairErrorDetailsExpander.IsVisible = !string.IsNullOrEmpty(details);
        }

        private void OnTargetChanged(object sender, TextChangedEventArgs e)
        {
            _snapshot = null;
            UpdateForm();
        }

        private async void OnBrowse(object sender, RoutedEventArgs e)
        {
            if (_busy || _workingCopy == null)
                return;
            try
            {
                var start = await StorageProvider.TryGetFolderFromPathAsync(_workingCopy.FullPath);
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    AllowMultiple = false,
                    SuggestedStartLocation = start,
                    Title = App.Text("SVN.Repair.ExistingTarget"),
                });
                if (files.Count == 1 && files[0].TryGetLocalPath() is { } selected)
                {
                    var path = Path.GetRelativePath(_workingCopy.FullPath, selected);
                    TargetInput.Text = OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
                }
            }
            catch (Exception ex)
            {
                ShowError(App.Text("SVN.Repair.ReviewFailed"), ex.Message);
            }
            e.Handled = true;
        }

        private async void OnPrepare(object sender, RoutedEventArgs e)
        {
            await PrepareAsync();
            e.Handled = true;
        }

        private async void OnApply(object sender, RoutedEventArgs e)
        {
            await ApplyAsync();
            e.Handled = true;
        }

        private async void OnReload(object sender, RoutedEventArgs e)
        {
            await ReloadAsync();
            e.Handled = true;
        }
        private void OnCancel(object sender, RoutedEventArgs e)
        {
            _ = _busy ? CancelPendingAsync() : RequestCloseAsync();
            e.Handled = true;
        }

        private async Task CancelPendingAsync()
        {
            if (_cancelRequested)
            {
                if (_pendingTask != null)
                {
                    try
                    {
                        await _pendingTask;
                    }
                    catch (Exception)
                    {
                        // The operation owner displays its failure; closing still waits for it.
                    }
                }
                return;
            }
            _cancelRequested = true;
            _requiresReload = true;
            _snapshot = null;
            UpdateForm();
            try
            {
                if (_isApplying)
                    _workingCopy.CancelAction();
                else if (_readCancellation != null)
                    _readCancellation.Cancel();
                else
                    _workingCopy.Cancel();
                if (_pendingTask != null)
                {
                    try
                    {
                        await _pendingTask;
                    }
                    catch (Exception)
                    {
                        // Preparation may be cancelled before its worker starts.
                    }
                }
                ShowError(App.Text("SVN.Repair.ReloadRequired"), _workingCopy.ActionErrorDetails);
            }
            finally
            {
                _cancelRequested = false;
                UpdateForm();
            }
        }

        private async Task RequestCloseAsync()
        {
            if (_closeRequestRunning)
                return;
            _closeRequestRunning = true;
            try
            {
                if (_busy)
                    await CancelPendingAsync();
                _allowClose = true;
                Close(false);
            }
            finally
            {
                _closeRequestRunning = false;
            }
        }

        private async void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (_allowClose)
                return;
            e.Cancel = true;
            await RequestCloseAsync();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _ = _busy ? CancelPendingAsync() : RequestCloseAsync();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private ViewModels.SvnWorkingCopy _workingCopy;
        private string _sourcePath;
        private Native.Svn.RenameRepairSnapshot _snapshot;
        private bool _busy;
        private bool _isApplying;
        private bool _requiresReload;
        private bool _cancelRequested;
        private bool _allowClose;
        private bool _closeRequestRunning;
        private CancellationTokenSource _readCancellation;
        private Task _pendingTask;
    }
}
