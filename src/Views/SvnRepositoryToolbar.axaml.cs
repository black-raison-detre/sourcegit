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
