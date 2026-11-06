using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class SvnIgnoreDialog : ChromelessWindow
    {
        public ViewModels.SvnIgnoreDialogViewModel Model { get; private set; }

        public SvnIgnoreDialog()
        {
            InitializeComponent();
            Title = App.Text("SVN.Ignore.Title");
            DialogTitle.Text = Title;
            Closing += OnClosing;
        }

        public SvnIgnoreDialog(ViewModels.SvnWorkingCopy workingCopy, IReadOnlyList<string> selectedPaths) : this()
        {
            Model = new ViewModels.SvnIgnoreDialogViewModel(workingCopy, selectedPaths);
            DataContext = Model;
            Model.PropertyChanged += (_, _) => UpdateForm();
            IgnoreParentPath.Text = Model.ParentPath;
            Opened += OnOpened;
            UpdateForm();
        }

        public static async Task<bool> ShowAsync(ViewModels.SvnWorkingCopy workingCopy,
            IReadOnlyList<string> selectedPaths, Window owner = null)
        {
            owner ??= (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner == null || workingCopy == null || !workingCopy.CanIgnoreSelectedPaths)
                return false;

            var dialog = new SvnIgnoreDialog(workingCopy, selectedPaths);
            return await dialog.ShowDialog<bool>(owner);
        }

        public async Task LoadAsync()
        {
            if (Model == null)
                return;
            _pendingTask = Model.LoadAsync();
            await _pendingTask;
            _pendingTask = null;
            UpdateForm();
        }

        public async Task<bool> ApplyAsync()
        {
            if (Model == null)
                return false;
            _pendingTask = Model.ApplyAsync();
            var succeeded = await _pendingTask;
            _pendingTask = null;
            UpdateForm();
            if (succeeded && IsVisible)
            {
                _allowClose = true;
                Close(true);
            }
            return succeeded;
        }

        private bool _allowClose;
        private bool _closeRequestRunning;
        private bool _cancelRequested;
        private Task<bool> _pendingTask;

        private void UpdateForm()
        {
            if (Model == null)
                return;

            IgnorePatternsPreview.Text = Model.PatternsToAdd;
            IgnoreButton.IsEnabled = Model.CanApply;
            ReloadIgnoreButton.IsEnabled = !Model.IsBusy;
            CancelIgnoreButton.Content = App.Text(Model.IsBusy ? "SVN.Ignore.CancelOperation" : "Close");
            CancelIgnoreButton.IsEnabled = !Model.IsBusy || !_cancelRequested;
            IgnoreBusyMessage.IsVisible = Model.IsBusy;
            IgnoreBusyMessage.Text = App.Text(Model.IsExecuting ? "SVN.Ignore.Applying" : "SVN.Ignore.Loading");
            IgnoreNoNewPatternsNotice.IsVisible = Model.HasNoNewPatterns;
            IgnoreErrorPanel.IsVisible = Model.HasError;
            IgnoreErrorMessage.Text = Model.ErrorMessage;
            IgnoreErrorDetails.Text = Model.ErrorDetails;
            IgnoreErrorDetailsExpander.IsVisible = !string.IsNullOrWhiteSpace(Model.ErrorDetails);
        }

        private async void OnOpened(object sender, EventArgs e) => await LoadAsync();

        private async void OnReload(object sender, RoutedEventArgs e)
        {
            await LoadAsync();
            e.Handled = true;
        }

        private async void OnApply(object sender, RoutedEventArgs e)
        {
            await ApplyAsync();
            e.Handled = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            _ = RequestCloseAsync();
            e.Handled = true;
        }

        private async Task RequestCloseAsync()
        {
            if (_closeRequestRunning)
                return;
            _closeRequestRunning = true;
            try
            {
                if (Model?.IsBusy == true)
                {
                    _cancelRequested = true;
                    if (Model.IsExecuting)
                        Model.CancelOperation();
                    else
                        Model.CancelLoad();
                    UpdateForm();
                    if (_pendingTask != null)
                        await _pendingTask;
                    _cancelRequested = false;
                }

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
                _ = RequestCloseAsync();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
