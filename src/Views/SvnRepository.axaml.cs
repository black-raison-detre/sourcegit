using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class SvnRepository : UserControl
    {
        public SvnRepository()
        {
            InitializeComponent();
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
    }
}
