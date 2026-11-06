using System;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class SvnConflictDialog : ChromelessWindow
    {
        public ViewModels.SvnConflictDialogViewModel Model { get; private set; }

        public SvnConflictDialog()
        {
            InitializeComponent();
            Title = App.Text("SVN.Conflict.Title");
            DialogTitle.Text = Title;
            Opened += OnOpened;
            Closing += OnClosing;
            Closed += OnClosed;
        }

        public SvnConflictDialog(ViewModels.SvnWorkingCopy workingCopy, ViewModels.SvnWorkingCopyEntry entry) : this()
        {
            Model = new ViewModels.SvnConflictDialogViewModel(workingCopy, entry);
            DataContext = Model;
        }

        public static async Task<bool> ShowAsync(ViewModels.SvnWorkingCopy workingCopy,
            ViewModels.SvnWorkingCopyEntry entry, Window owner = null)
        {
            owner ??= (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner == null || workingCopy == null || !workingCopy.CanInspectConflictEntry(entry))
                return false;

            var dialog = new SvnConflictDialog(workingCopy, entry);
            return await dialog.ShowDialog<bool>(owner);
        }

        private async void OnOpened(object sender, EventArgs e)
        {
            if (Model == null)
                return;

            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                var scaling = RenderScaling;
                Width = Math.Min(Width, Math.Max(MinWidth, screen.WorkingArea.Width / scaling - 48));
                Height = Math.Min(Height, Math.Max(MinHeight, screen.WorkingArea.Height / scaling - 48));
            }

            ConflictTabs.Focus();
            await Model.LoadAsync();
            SelectFirstVisibleTab();
        }

        private async void OnReload(object sender, RoutedEventArgs e)
        {
            await Model.LoadAsync();
            SelectFirstVisibleTab();
            if (Model.HasTextConflict)
                ConflictVersionFrom.Focus();
            else
                ConflictTabs.Focus();
        }

        private async void OnResolve(object sender, RoutedEventArgs e)
        {
            var succeeded = await Model.ResolveAsync(message => SvnOperationDialog.ShowConfirmationAsync(
                App.Text("SVN.Conflict.ConfirmTitle"), App.Text("SVN.Conflict.Resolve"), message, this));
            if (succeeded)
                Close(true);
        }

        private async void OnCopyPath(object sender, RoutedEventArgs e)
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(Model.Path);
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            if (Model == null)
            {
                Close(false);
                return;
            }
            if (Model.IsResolving)
                Model.CancelOperation();
            else
                Close(false);
        }

        private void SelectFirstVisibleTab()
        {
            if (Model.HasTextConflict)
                ConflictTabs.SelectedItem = TextConflictTab;
            else if (Model.HasPropertyConflict)
                ConflictTabs.SelectedItem = PropertyConflictTab;
            else if (Model.HasTreeConflict)
                ConflictTabs.SelectedItem = TreeConflictTab;
        }

        private void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (Model?.IsResolving == true)
                e.Cancel = true;
        }

        private void OnClosed(object sender, EventArgs e) => Model?.Dispose();
    }
}
