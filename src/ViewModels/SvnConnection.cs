using System;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class SvnConnection : ObservableObject, IDisposable
    {
        public string Url
        {
            get => _url;
            set
            {
                if (IsConnecting || !SetProperty(ref _url, value ?? string.Empty))
                    return;

                Username = string.Empty;
                Password = string.Empty;
                Remember = false;
                NeedsCredentials = false;
                HasConnected = false;
                _hasAttempted = false;
                Details = string.Empty;
                RepositoryRoot = string.Empty;
                Revision = string.Empty;
                SetStatus("EnterUrl");
                OnPropertyChanged(nameof(ServerHint));
                NotifyActions();
            }
        }

        public string Username
        {
            get => _username;
            set
            {
                if (SetProperty(ref _username, value ?? string.Empty))
                    NotifyActions();
            }
        }

        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value ?? string.Empty);
        }

        public bool Remember
        {
            get => _remember;
            set => SetProperty(ref _remember, value && CanRemember);
        }

        public bool CanRemember => !string.IsNullOrWhiteSpace(_configDirectory);
        public string RememberHint => App.Text(CanRemember ? "SVN.Connection.RememberHint" : "SVN.Connection.RememberRequiresConfig");

        public bool IsConnecting
        {
            get => _isConnecting;
            private set
            {
                if (SetProperty(ref _isConnecting, value))
                    NotifyActions();
            }
        }

        public bool NeedsCredentials
        {
            get => _needsCredentials;
            private set => SetProperty(ref _needsCredentials, value);
        }

        public bool HasConnected
        {
            get => _hasConnected;
            private set
            {
                if (SetProperty(ref _hasConnected, value))
                    NotifyActions();
            }
        }

        public string Status => App.Text($"SVN.Connection.{_status}");
        public string ServerHint => App.Text("SVN.Connection.SignInTo", Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) ? uri.Host : string.Empty);
        public string ActionText => App.Text($"SVN.Connection.{(HasConnected ? "Close" : IsConnecting ? "ConnectingAction" : _hasAttempted ? "Retry" : "Connect")}");
        public bool CanConnect => !_disposed && !IsConnecting && (HasConnected || (IsUrlValid() && (!NeedsCredentials || !string.IsNullOrWhiteSpace(Username))));

        public string Details
        {
            get => _details;
            private set => SetProperty(ref _details, value);
        }

        public string RepositoryRoot
        {
            get => _repositoryRoot;
            private set => SetProperty(ref _repositoryRoot, value);
        }

        public string Revision
        {
            get => _revision;
            private set => SetProperty(ref _revision, value);
        }

        public SvnConnection(string executable, string configDirectory)
        {
            _executable = executable;
            _configDirectory = configDirectory;
        }

        public async Task ConnectAsync()
        {
            if (!CanConnect || HasConnected)
                return;

            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            var credentials = NeedsCredentials ? new Native.Svn.Credentials(Username, Password, Remember) : null;
            Password = string.Empty;
            _hasAttempted = true;
            IsConnecting = true;
            Details = string.Empty;
            SetStatus("Connecting");

            try
            {
                var result = await Task.Run(() => Native.Svn.TestConnectionAsync(_executable, _configDirectory, Url.Trim(), credentials, cancellation.Token), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_disposed)
                    return;

                Details = result.Details;
                RepositoryRoot = result.RepositoryRoot;
                Revision = result.Revision;
                HasConnected = result.Status == "Ready";
                NeedsCredentials = result.Status is "AuthenticationRequired" or "AuthenticationFailed" or "CredentialStorageUnavailable";
                SetStatus(result.Status);
            }
            catch (OperationCanceledException)
            {
                // Closing this dialog cancels the operation and discards its result.
            }
            catch
            {
                // Unexpected exceptions may include server-supplied content; don't expose secrets.
                if (!_disposed)
                    SetStatus("Failed");
            }
            finally
            {
                Password = string.Empty;
                _cancellation = null;
                if (!_disposed)
                    IsConnecting = false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Password = string.Empty;
            Remember = false;
            _cancellation?.Cancel();
        }

        private bool IsUrlValid()
        {
            if (Url.Contains('\r') || Url.Contains('\n') || !Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo.Contains(':'))
                return false;

            return uri.Scheme is "http" or "https" or "svn" or "svn+ssh" or "file";
        }

        private void SetStatus(string status)
        {
            _status = status;
            OnPropertyChanged(nameof(Status));
        }

        private void NotifyActions()
        {
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(ActionText));
        }

        private readonly string _executable;
        private readonly string _configDirectory;
        private CancellationTokenSource _cancellation;
        private bool _disposed = false;
        private bool _hasAttempted = false;
        private bool _isConnecting = false;
        private bool _needsCredentials = false;
        private bool _hasConnected = false;
        private bool _remember = false;
        private string _url = string.Empty;
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _status = "EnterUrl";
        private string _details = string.Empty;
        private string _repositoryRoot = string.Empty;
        private string _revision = string.Empty;
    }
}
