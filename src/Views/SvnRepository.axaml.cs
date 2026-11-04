using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class SvnRepository : UserControl
    {
        public SvnRepository()
        {
            InitializeComponent();
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

        private void OnCancelWorkingCopy(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.SvnRepository repo)
                repo.WorkingCopy.Cancel();

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
