using System;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public partial class SvnPathOperationDialog : ChromelessWindow
    {
        public ViewModels.SvnPathOperationDialogViewModel Model { get; private set; }
        public bool IsExecuting => Model?.IsExecuting == true;
        public bool HasError => Model?.HasError == true;

        public SvnPathOperationDialog()
        {
            InitializeComponent();
            Title = App.Text("SVN.PathOperation.Title");
            DialogTitle.Text = Title;
            Closing += OnClosing;
            Opened += OnOpened;
        }

        public SvnPathOperationDialog(ViewModels.SvnWorkingCopy workingCopy,
            string operation = "rename", string source = null) : this()
        {
            Model = new ViewModels.SvnPathOperationDialogViewModel(workingCopy, operation, source);
            Model.PropertyChanged += (_, _) => UpdateForm();
            WorkingCopyPath.Text = workingCopy?.FullPath ?? string.Empty;
            OperationSelector.SelectedIndex = Model.Operation switch { "move" => 1, "copy" => 2, _ => 0 };
            SourcePath.Text = Model.SourcePath;
            TargetInput.Text = Model.TargetInput;
            PendingCommitNotice.Text = Model.PendingCommitNotice;
            UpdateForm();
        }

        public static async Task<bool> ShowAsync(ViewModels.SvnWorkingCopy workingCopy,
            string operation = "rename", string source = null, Window owner = null)
        {
            owner ??= (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner == null || workingCopy == null || !workingCopy.CanFileOperations)
                return false;

            var dialog = new SvnPathOperationDialog(workingCopy, operation, source);
            return await dialog.ShowDialog<bool>(owner);
        }

        public async Task<bool> SubmitAsync()
        {
            if (Model == null)
                return false;

            var succeeded = await Model.SubmitAsync();
            _cancelRequested = false;
            UpdateForm();
            if (succeeded && IsVisible)
                Close(true);
            return succeeded;
        }

        private bool _updatingControls;
        private bool _cancelRequested;
        private bool _errorScrollScheduled;
        private bool _wasErrorVisible;

        private void UpdateForm()
        {
            if (Model == null)
                return;

            var hasNewError = Model.HasError && !_wasErrorVisible;
            _wasErrorVisible = Model.HasError;
            _updatingControls = true;
            try
            {
                var operationIndex = Model.Operation switch { "move" => 1, "copy" => 2, _ => 0 };
                if (OperationSelector.SelectedIndex != operationIndex)
                    OperationSelector.SelectedIndex = operationIndex;
                if (SourcePath.Text != Model.SourcePath)
                    SourcePath.Text = Model.SourcePath;
                if (TargetInput.Text != Model.TargetInput)
                    TargetInput.Text = Model.TargetInput;
                TargetLabel.Text = Model.FieldLabel;
                DestinationPreview.Text = Model.DestinationPreview;
                PendingCommitNotice.Text = Model.PendingCommitNotice;
                ValidationMessage.Text = Model.ValidationMessage;
                ValidationMessage.IsVisible = !string.IsNullOrEmpty(Model.ValidationMessage);
                ErrorMessage.Text = Model.ErrorMessage;
                ErrorPanel.IsVisible = Model.HasError;
                ErrorDetails.Text = Model.ErrorDetails;
                ErrorDetailsExpander.IsVisible = !string.IsNullOrWhiteSpace(Model.ErrorDetails);
                PrimaryButton.Content = Model.PrimaryLabel;
                PrimaryButton.IsEnabled = Model.CanSubmit;
                ExecutionStatus.IsVisible = Model.IsExecuting;
                ExecutionMessage.Text = App.Text(_cancelRequested ? "SVN.PathOperation.Canceling" : "SVN.PathOperation.Applying");

                var inputsEnabled = !Model.IsExecuting;
                OperationSelector.IsEnabled = inputsEnabled;
                SourcePath.IsEnabled = inputsEnabled;
                TargetInput.IsEnabled = inputsEnabled;
                BrowseButton.IsEnabled = inputsEnabled;
                CancelButton.IsEnabled = !Model.IsExecuting || !_cancelRequested;
                CancelButton.Content = App.Text(_cancelRequested ? "SVN.PathOperation.Canceling" : "Cancel");
            }
            finally
            {
                _updatingControls = false;
            }

            if (hasNewError && IsVisible && !_errorScrollScheduled)
                ScheduleErrorScroll();
        }

        private void OnOpened(object sender, EventArgs e)
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                var scaling = RenderScaling;
                Width = Math.Min(Width, Math.Max(MinWidth, screen.WorkingArea.Width / scaling - 48));
                Height = Math.Min(Height, Math.Max(MinHeight, screen.WorkingArea.Height / scaling - 48));
            }

            if (string.IsNullOrEmpty(Model?.SourcePath))
                SourcePath.Focus();
            else
            {
                TargetInput.Focus();
                if (Model.Operation == "rename")
                    TargetInput.SelectAll();
            }
        }

        private void OnOperationChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingControls || Model == null || OperationSelector.SelectedItem is not ComboBoxItem item ||
                item.Tag is not string operation)
                return;

            Model.Operation = operation;
        }

        private void OnSourceTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_updatingControls && Model != null)
                Model.SourcePath = SourcePath.Text;
        }

        private void OnTargetTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_updatingControls && Model != null)
                Model.TargetInput = TargetInput.Text;
        }

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            var file = new MenuItem { Header = App.Text("SVN.PathOperation.BrowseFile") };
            file.Click += async (_, _) => await SelectSourceAsync(selectFolder: false);
            var folder = new MenuItem { Header = App.Text("SVN.PathOperation.BrowseFolder") };
            folder.Click += async (_, _) => await SelectSourceAsync(selectFolder: true);
            menu.Items.Add(file);
            menu.Items.Add(folder);
            menu.Open(BrowseButton);
            e.Handled = true;
        }

        private async Task SelectSourceAsync(bool selectFolder)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null || Model?.WorkingCopy == null)
                return;

            try
            {
                var start = await topLevel.StorageProvider.TryGetFolderFromPathAsync(Model.WorkingCopy.FullPath);
                string selectedPath = null;
                if (selectFolder)
                {
                    var options = new FolderPickerOpenOptions
                    {
                        AllowMultiple = false,
                        SuggestedStartLocation = start,
                    };
                    var selected = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                    if (selected.Count == 1)
                        selectedPath = GetLocalPath(selected[0]);
                }
                else
                {
                    var options = new FilePickerOpenOptions
                    {
                        AllowMultiple = false,
                        SuggestedStartLocation = start,
                    };
                    var selected = await topLevel.StorageProvider.OpenFilePickerAsync(options);
                    if (selected.Count == 1)
                        selectedPath = GetLocalPath(selected[0]);
                }

                if (!string.IsNullOrEmpty(selectedPath))
                {
                    var relative = Path.GetRelativePath(Model.WorkingCopy.FullPath, selectedPath);
                    Model.SourcePath = relative;
                    SourcePath.Focus();
                    SourcePath.CaretIndex = SourcePath.Text?.Length ?? 0;
                }
            }
            catch (Exception ex)
            {
                Model.ReportPickerError(ex);
                UpdateForm();
            }
        }

        private static string GetLocalPath(IStorageItem item) => item?.TryGetLocalPath() ??
            throw new InvalidOperationException(App.Text("SVN.PathOperation.BrowseLocalPathUnavailable"));

        private async void OnSubmit(object sender, RoutedEventArgs e)
        {
            await SubmitAsync();
            e.Handled = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            if (Model?.IsExecuting == true)
            {
                if (!_cancelRequested)
                {
                    _cancelRequested = true;
                    Model.WorkingCopy.CancelAction();
                    UpdateForm();
                }
            }
            else
            {
                Close(false);
            }

            e.Handled = true;
        }

        private void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (Model?.IsExecuting == true)
                e.Cancel = true;
        }

        private void ScheduleErrorScroll()
        {
            _errorScrollScheduled = true;
            BodyScrollViewer.LayoutUpdated -= OnBodyLayoutUpdated;
            BodyScrollViewer.LayoutUpdated += OnBodyLayoutUpdated;
            BodyScrollViewer.InvalidateMeasure();
        }

        private void OnBodyLayoutUpdated(object sender, EventArgs e)
        {
            if (!_errorScrollScheduled)
                return;

            _errorScrollScheduled = false;
            BodyScrollViewer.LayoutUpdated -= OnBodyLayoutUpdated;
            Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible && Model?.HasError == true)
                    ErrorPanel.BringIntoView();
            }, DispatcherPriority.Background);
        }

        protected override void OnClosed(EventArgs e)
        {
            BodyScrollViewer.LayoutUpdated -= OnBodyLayoutUpdated;
            base.OnClosed(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && Model?.IsExecuting != true)
            {
                Close(false);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && Model?.IsExecuting == true)
            {
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }
    }
}
