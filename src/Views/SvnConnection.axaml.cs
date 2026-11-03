using System;

using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class SvnConnection : ChromelessWindow
    {
        public SvnConnection()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            RepositoryUrlBox.Focus();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (DataContext is ViewModels.SvnConnection connection)
                connection.Dispose();
            base.OnClosed(e);
        }

        private async void Connect(object _, RoutedEventArgs e)
        {
            e.Handled = true;
            if (DataContext is not ViewModels.SvnConnection connection)
                return;

            if (connection.HasConnected)
            {
                Close();
                return;
            }

            await connection.ConnectAsync();
            if (IsVisible && connection.NeedsCredentials)
            {
                if (string.IsNullOrWhiteSpace(connection.Username))
                    UsernameBox.Focus();
                else
                    PasswordBox.Focus();
            }
        }

        private void Cancel(object _, RoutedEventArgs e)
        {
            e.Handled = true;
            Close();
        }
    }
}
