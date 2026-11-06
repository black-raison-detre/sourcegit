using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public sealed record SvnChangelistChoice(string Name)
    {
        public string Label => Name == null ? App.Text("SVN.Changelist.New") : App.Text("SVN.Changelist.Named", Name);
    }

    public partial class SvnChangelistDialog : ChromelessWindow
    {
        public bool CanApply => _workingCopy?.CanFileOperations == true && !_busy && !_cancelRequested && !_requiresReload &&
            _selections.Count > 0 && Native.Svn.TryValidateChangelistName(TargetName, out _);

        public SvnChangelistDialog()
        {
            InitializeComponent();
            Title = App.Text("SVN.Changelist.AssignTitle");
            DialogTitle.Text = Title;
            Closing += OnClosing;
        }

        public SvnChangelistDialog(ViewModels.SvnWorkingCopy workingCopy) : this()
        {
            _workingCopy = workingCopy;
            _paths = workingCopy.SelectedEntries.Select(entry => entry.Path).ToArray();
            ChangelistWorkingCopyPath.Text = workingCopy.FullPath;
            ChangelistSelectedPaths.ItemsSource = _paths;
            ChangelistSelectedCount.Text = App.Text("SVN.Changelist.SelectedCount", _paths.Length);
            CaptureSelections();
            UpdateChoices(true);
            UpdateForm();
            Opened += (_, _) =>
            {
                if (NewChangelistForm.IsVisible)
                    ChangelistNameInput.Focus();
                else
                    ExistingChangelists.Focus();
            };
        }

        public static async Task<bool> ShowAsync(ViewModels.SvnWorkingCopy workingCopy, Window owner = null)
        {
            owner ??= (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner == null || workingCopy?.CanAssignChangelist != true)
                return false;
            return await new SvnChangelistDialog(workingCopy).ShowDialog<bool>(owner);
        }

        public async Task<bool> ApplyAsync()
        {
            if (!CanApply)
                return false;

            _busy = true;
            _isApplying = true;
            UpdateForm();
            _pendingTask = _workingCopy.SetChangelistAsync(_selections, TargetName);
            var succeeded = false;
            try
            {
                succeeded = await (Task<bool>)_pendingTask;
                if (!succeeded)
                {
                    _requiresReload = true;
                    ShowError(App.Text("SVN.Changelist.ReloadRequired"), _workingCopy.ActionErrorDetails);
                }
            }
            finally
            {
                _pendingTask = null;
                _busy = false;
                _isApplying = false;
                UpdateForm();
            }
            if (succeeded && IsVisible && !_closeRequestRunning && !_cancelRequested && !_requiresReload)
            {
                _allowClose = true;
                Close(true);
            }
            return succeeded;
        }

        public async Task ReloadAsync()
        {
            if (_busy || _cancelRequested || _workingCopy == null || !_workingCopy.CanRefresh)
                return;

            _busy = true;
            UpdateForm();
            _pendingTask = _workingCopy.RefreshAsync();
            try
            {
                await _pendingTask;
                CaptureSelections();
                UpdateChoices(false);
                if (!_requiresReload)
                    ShowError(string.Empty, string.Empty);
            }
            finally
            {
                _pendingTask = null;
                _busy = false;
                UpdateForm();
            }
        }

        private string TargetName => (ExistingChangelists.SelectedItem as SvnChangelistChoice)?.Name ?? ChangelistNameInput.Text ?? string.Empty;

        private void CaptureSelections()
        {
            var entries = _paths.Select(path => _workingCopy.Entries.FirstOrDefault(entry => entry.Path == path)).ToArray();
            _requiresReload = _workingCopy.HasError || entries.Any(entry => entry == null ||
                !Native.Svn.CanChangeChangelist(entry.Entry) || Native.Svn.HasUnsafeChangelistAncestor(entry.Entry,
                    _workingCopy.Entries.Select(item => item.Entry).ToArray()));
            _selections = _requiresReload ? [] : entries.Select(entry =>
                new Native.Svn.ChangelistSelection(entry.Path, entry.Entry.Changelist)).ToArray();
            if (_requiresReload)
                ShowError(App.Text("SVN.Changelist.SelectionChanged"), _workingCopy.ErrorDetails);
        }

        private void UpdateChoices(bool initial)
        {
            var previousName = (ExistingChangelists.SelectedItem as SvnChangelistChoice)?.Name;
            var choices = new List<SvnChangelistChoice> { new(null) };
            choices.AddRange(_workingCopy.ChangelistNames.Select(name => new SvnChangelistChoice(name)));
            if (initial && _selections.Count > 0 && _selections.Select(selection => selection.Changelist).Distinct(StringComparer.Ordinal).Count() == 1)
                previousName = _selections[0].Changelist;
            // Keep a disappeared name as an explicit assignment choice after reload.
            if (previousName != null && !choices.Any(choice => choice.Name == previousName))
                choices.Add(new(previousName));
            ExistingChangelists.ItemsSource = choices;
            ExistingChangelists.SelectedItem = choices.FirstOrDefault(choice => choice.Name == previousName) ?? choices[0];
        }

        private void UpdateForm()
        {
            if (AssignChangelistButton == null)
                return;
            AssignChangelistButton.IsEnabled = CanApply;
            ReloadChangelistButton.IsEnabled = !_busy && !_cancelRequested && _workingCopy?.CanRefresh == true;
            ExistingChangelists.IsEnabled = !_busy;
            ChangelistNameInput.IsEnabled = !_busy;
            NewChangelistForm.IsVisible = (ExistingChangelists.SelectedItem as SvnChangelistChoice)?.Name == null;
            ChangelistBusyMessage.IsVisible = _busy;
            CancelChangelistButton.Content = App.Text(_busy ? "SVN.Changelist.CancelOperation" : "Cancel");
            CancelChangelistButton.IsEnabled = !_cancelRequested;
            ChangelistValidationMessage.IsVisible = !Native.Svn.TryValidateChangelistName(TargetName, out _);
            ChangelistValidationMessage.Text = App.Text("SVN.Changelist.NameHint");
        }

        private void ShowError(string message, string details)
        {
            ChangelistErrorPanel.IsVisible = !string.IsNullOrEmpty(message);
            ChangelistErrorMessage.Text = message;
            ChangelistErrorDetails.Text = details ?? string.Empty;
            ChangelistErrorDetailsExpander.IsVisible = !string.IsNullOrEmpty(details);
        }

        private void OnNameChanged(object sender, TextChangedEventArgs e) => UpdateForm();
        private void OnExistingChangelistChanged(object sender, SelectionChangedEventArgs e) => UpdateForm();
        private async void OnAssign(object sender, RoutedEventArgs e)
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
            _ = _busy ? CancelPendingOperationAsync() : RequestCloseAsync();
            e.Handled = true;
        }

        private async Task CancelPendingOperationAsync()
        {
            if (!_busy)
                return;
            _cancelRequested = true;
            _requiresReload = true;
            try
            {
                if (_isApplying)
                    _workingCopy?.CancelAction();
                else
                    _workingCopy?.Cancel();
                UpdateForm();
                if (_pendingTask != null)
                    await _pendingTask;
                _requiresReload = true;
                ShowError(App.Text("SVN.Changelist.ReloadRequired"), _workingCopy?.ActionErrorDetails);
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
                    await CancelPendingOperationAsync();
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
            if (e.Key == Key.Enter && CanApply)
            {
                _ = ApplyAsync();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private ViewModels.SvnWorkingCopy _workingCopy;
        private string[] _paths = [];
        private IReadOnlyList<Native.Svn.ChangelistSelection> _selections = [];
        private bool _busy;
        private bool _isApplying;
        private bool _requiresReload;
        private bool _allowClose;
        private bool _closeRequestRunning;
        private bool _cancelRequested;
        private Task _pendingTask;
    }
}
