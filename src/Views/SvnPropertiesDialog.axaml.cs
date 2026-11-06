using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    public partial class SvnPropertiesDialog : ChromelessWindow
    {
        public ViewModels.SvnPropertiesDialogViewModel Model { get; private set; }

        public SvnPropertiesDialog()
        {
            InitializeComponent();
            Title = App.Text("SVN.Properties.Title");
            DialogTitle.Text = Title;
            Closing += OnClosing;
        }

        public SvnPropertiesDialog(ViewModels.SvnWorkingCopy workingCopy, string relativePath = null) : this()
        {
            Model = new ViewModels.SvnPropertiesDialogViewModel(workingCopy, relativePath);
            DataContext = Model;
            Model.PropertyChanged += (_, _) => UpdateForm();
            WorkingCopyPath.Text = Model.WorkingCopyPath;
            PropertiesPathInput.Text = Model.PathInput;
            Opened += OnOpened;
            UpdateForm();
        }

        public static async Task<bool> ShowAsync(ViewModels.SvnWorkingCopy workingCopy,
            string relativePath = null, Window owner = null)
        {
            owner ??= (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner == null || workingCopy == null || !workingCopy.CanFileOperations)
                return false;

            var dialog = new SvnPropertiesDialog(workingCopy, relativePath);
            return await dialog.ShowDialog<bool>(owner);
        }

        public async Task<bool> LoadAsync(string relativePath)
        {
            if (Model == null)
                return false;

            Model.PathInput = relativePath;
            PropertiesPathInput.Text = relativePath;
            _pendingTask = Model.LoadAsync(relativePath);
            var loaded = await _pendingTask;
            _pendingTask = null;
            UpdateForm();
            return loaded;
        }

        public async Task<bool> SaveAsync()
        {
            if (Model == null)
                return false;

            _pendingTask = Model.SaveAsync();
            var saved = await _pendingTask;
            _pendingTask = null;
            _hasSaved |= saved;
            UpdateForm();
            return saved;
        }

        public void AddProperty()
        {
            if (Model == null)
                return;
            Model.BeginAddProperty();
            UpdateForm();
            PropertyNameInput.Focus();
        }

        public async Task<bool> DeleteProperty()
        {
            if (Model == null)
                return false;

            _pendingTask = Model.DeleteAsync();
            var deleted = await _pendingTask;
            _pendingTask = null;
            _hasSaved |= deleted;
            UpdateForm();
            return deleted;
        }

        private bool _updatingControls;
        private bool _allowClose;
        private bool _closeRequestRunning;
        private bool _hasSaved;
        private bool _cancelRequested;
        private Task<bool> _pendingTask;

        private void UpdateForm()
        {
            if (Model == null)
                return;

            _updatingControls = true;
            try
            {
                if (PropertiesPathInput.Text != Model.PathInput)
                    PropertiesPathInput.Text = Model.PathInput;
                var selected = Model.SelectedPropertyName;
                var selectedItem = Model.Properties.FirstOrDefault(property => property.Name == selected);
                if (!ReferenceEquals(PropertyNames.SelectedItem, selectedItem))
                    PropertyNames.SelectedItem = selectedItem;
                if (PropertyNameInput.Text != Model.CurrentName)
                    PropertyNameInput.Text = Model.CurrentName;
                if (PropertyValueInput.Text != Model.CurrentValue)
                    PropertyValueInput.Text = Model.CurrentValue;

                PropertyNames.IsEnabled = !Model.IsBusy && Model.IsLoaded && Model.PathMatchesSnapshot && !Model.RequiresReload;
                PropertyNameInput.IsEnabled = !Model.IsBusy && Model.HasSelection && Model.PathMatchesSnapshot && !Model.RequiresReload;
                PropertyNameInput.IsReadOnly = !Model.IsNewProperty;
                PropertyValueInput.IsEnabled = !Model.IsBusy && Model.HasSelection && Model.PathMatchesSnapshot && !Model.RequiresReload;
                PropertyValueInput.IsReadOnly = Model.IsBusy || Model.IsValueReadOnly || !Model.PathMatchesSnapshot || Model.RequiresReload;
                AddPropertyButton.IsEnabled = Model.CanAdd;
                DeletePropertyButton.IsEnabled = Model.CanDelete;
                SavePropertyButton.IsEnabled = Model.CanSave;
                LoadPropertiesButton.IsEnabled = Model.CanLoad;
                ReloadPropertiesButton.IsEnabled = Model.CanReload;
                BrowsePropertiesPathButton.IsEnabled = !Model.IsBusy;
                PropertiesPathInput.IsEnabled = !Model.IsBusy;
                PropertyValueHint.Text = Model.IsValueReadOnly ? Model.ReadOnlyNotice : Model.ValueEndingHint;
                PropertiesValidationMessage.Text = Model.ValidationMessage;
                PropertiesValidationMessage.IsVisible = !string.IsNullOrEmpty(Model.ValidationMessage);
                PropertyReadOnlyNotice.Text = Model.ReadOnlyNotice;
                PropertyReadOnlyNotice.IsVisible = Model.HasReadOnlyNotice;
                PropertiesErrorPanel.IsVisible = Model.HasError;
                PropertiesErrorMessage.Text = Model.ErrorMessage;
                PropertiesErrorDetails.Text = Model.ErrorDetails;
                PropertiesErrorDetailsExpander.IsVisible = !string.IsNullOrWhiteSpace(Model.ErrorDetails);
                PropertiesListEmptyMessage.Text = App.Text(Model.IsLoading ? "SVN.Properties.Loading" : "SVN.Properties.EmptyList");
                PropertiesListEmptyMessage.IsVisible = Model.IsLoading || (Model.IsLoaded && Model.Properties.Count == 0);
                PropertyNames.IsVisible = !Model.IsLoading && Model.Properties.Count > 0;
                CancelPropertiesButton.Content = App.Text(Model.IsBusy ? "SVN.Properties.CancelOperation" : "Close");
                CancelPropertiesButton.IsEnabled = !Model.IsBusy || !_cancelRequested;
                SavePropertyButton.Content = App.Text(Model.IsSaving ? "SVN.Properties.Saving" : "SVN.Properties.Save");
            }
            finally
            {
                _updatingControls = false;
            }
        }

        private async void OnOpened(object sender, EventArgs e)
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                var scaling = RenderScaling;
                Width = Math.Min(Width, Math.Max(MinWidth, screen.WorkingArea.Width / scaling - 48));
                Height = Math.Min(Height, Math.Max(MinHeight, screen.WorkingArea.Height / scaling - 48));
            }

            await LoadAsync(Model.PathInput);
            PropertiesPathInput.Focus();
            PropertiesPathInput.CaretIndex = PropertiesPathInput.Text?.Length ?? 0;
        }

        private void OnPathTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_updatingControls && Model != null)
                Model.PathInput = PropertiesPathInput.Text;
        }

        private void OnPropertyNameTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_updatingControls && Model?.IsNewProperty == true)
                Model.CurrentName = PropertyNameInput.Text;
        }

        private void OnPropertyValueTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_updatingControls && Model?.HasSelection == true)
                Model.CurrentValue = PropertyValueInput.Text;
        }

        private async void OnPropertySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingControls || Model == null || PropertyNames.SelectedItem is not ViewModels.SvnPropertyItem property)
                return;

            if (!await ConfirmDiscardDraftAsync())
            {
                UpdateForm();
                return;
            }

            Model.CancelDraft();
            Model.SelectProperty(property.Name);
            UpdateForm();
        }

        private void OnAddProperty(object sender, RoutedEventArgs e)
        {
            _ = AddPropertyWithPromptAsync();
            e.Handled = true;
        }

        private async Task AddPropertyWithPromptAsync()
        {
            if (await ConfirmDiscardDraftAsync())
            {
                Model.CancelDraft();
                AddProperty();
            }
        }

        private async void OnDeleteProperty(object sender, RoutedEventArgs e)
        {
            if (Model?.CanDelete == true && await SvnOperationDialog.ShowConfirmationAsync(
                App.Text("SVN.Properties.DeleteTitle"), App.Text("SVN.Properties.Delete"),
                App.Text("SVN.Properties.DeleteConfirm", Model.CurrentName, Model.LoadedPath), this))
                await DeleteProperty();
            e.Handled = true;
        }

        private async void OnLoadProperties(object sender, RoutedEventArgs e)
        {
            if (await ConfirmDiscardDraftAsync())
            {
                Model.CancelDraft();
                await LoadAsync(PropertiesPathInput.Text);
            }
            e.Handled = true;
        }

        private async void OnReloadProperties(object sender, RoutedEventArgs e)
        {
            if (await ConfirmDiscardDraftAsync())
            {
                Model.CancelDraft();
                await LoadAsync(PropertiesPathInput.Text);
            }
            e.Handled = true;
        }

        private void OnBrowsePath(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            var root = new MenuItem { Header = App.Text("SVN.Properties.WorkingCopyRoot") };
            root.Click += (_, _) =>
            {
                if (Model != null)
                    Model.PathInput = PropertiesPathInput.Text = ".";
            };
            var file = new MenuItem { Header = App.Text("SVN.PathOperation.BrowseFile") };
            file.Click += async (_, _) => await SelectPathAsync(selectFolder: false);
            var folder = new MenuItem { Header = App.Text("SVN.PathOperation.BrowseFolder") };
            folder.Click += async (_, _) => await SelectPathAsync(selectFolder: true);
            menu.Items.Add(root);
            menu.Items.Add(file);
            menu.Items.Add(folder);
            menu.Open(BrowsePropertiesPathButton);
            e.Handled = true;
        }

        private async Task SelectPathAsync(bool selectFolder)
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
                    var selected = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                    {
                        AllowMultiple = false,
                        SuggestedStartLocation = start,
                    });
                    if (selected.Count == 1)
                        selectedPath = GetLocalPath(selected[0]);
                }
                else
                {
                    var selected = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        AllowMultiple = false,
                        SuggestedStartLocation = start,
                    });
                    if (selected.Count == 1)
                        selectedPath = GetLocalPath(selected[0]);
                }

                if (!string.IsNullOrEmpty(selectedPath))
                {
                    var relative = Path.GetRelativePath(Model.WorkingCopy.FullPath, selectedPath);
                    PropertiesPathInput.Text = OperatingSystem.IsWindows() ? relative.Replace('\\', '/') : relative;
                }
            }
            catch (Exception ex)
            {
                Model.ReportPickerError(ex);
                UpdateForm();
            }
        }

        private async void OnSaveProperty(object sender, RoutedEventArgs e)
        {
            await SaveAsync();
            e.Handled = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            _ = RequestCloseAsync();
            e.Handled = true;
        }

        private async Task<bool> ConfirmDiscardDraftAsync()
        {
            if (Model?.HasDraft != true)
                return true;

            return await SvnOperationDialog.ShowConfirmationAsync(App.Text("SVN.Properties.DiscardTitle"),
                App.Text("SVN.Properties.DiscardTitle"), App.Text("SVN.Properties.DiscardDraft"), this);
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
                    if (Model.IsSaving)
                        Model.CancelOperation();
                    else
                        Model.CancelLoad();
                    UpdateForm();
                    if (_pendingTask != null)
                        await _pendingTask;
                    _cancelRequested = false;
                }

                if (!await ConfirmDiscardDraftAsync())
                    return;

                _allowClose = true;
                Close(_hasSaved);
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

        private static string GetLocalPath(IStorageItem item) => item?.TryGetLocalPath() ??
            throw new InvalidOperationException(App.Text("SVN.PathOperation.BrowseLocalPathUnavailable"));
    }
}
