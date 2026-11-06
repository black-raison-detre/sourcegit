using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class SvnRepositoryToolbar : UserControl
    {
        public SvnRepositoryToolbar()
        {
            InitializeComponent();
        }

        private async void OnUpdate(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await SvnOperationDialog.ShowUpdateAsync(repo, owner: TopLevel.GetTopLevel(this) as Window);

            e.Handled = true;
        }

        private void OnUpdateMenu(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
            {
                var item = new MenuItem { Header = App.Text("SVN.Update.ToRevision") };
                item.Click += async (_, _) => await SvnOperationDialog.ShowUpdateAsync(repo, string.Empty, TopLevel.GetTopLevel(this) as Window);
                var menu = new ContextMenu();
                menu.Items.Add(item);
                menu.Open(sender as Control);
            }

            e.Handled = true;
        }

        private async void OnCommit(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await SvnOperationDialog.ShowCommitAsync(repo, TopLevel.GetTopLevel(this) as Window);

            e.Handled = true;
        }

        private async void OnRefresh(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                await repo.RefreshActiveAsync();

            e.Handled = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                repo.CancelActive();

            e.Handled = true;
        }

        private void OnOpenInFileManager(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                repo.OpenInFileManager();

            e.Handled = true;
        }

        private void OnOpenTerminal(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                repo.OpenTerminal();

            e.Handled = true;
        }
    }
}
