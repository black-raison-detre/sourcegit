using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Collections;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class SvnRepository : ObservableObject, IDisposable
    {
        public string FullPath { get; }
        public AvaloniaList<Native.SvnRevision> Revisions { get; } = new();

        public string Url
        {
            get => _url;
            private set => SetProperty(ref _url, value);
        }

        public Native.SvnRevision SelectedRevision
        {
            get => _selectedRevision;
            set
            {
                if (SetProperty(ref _selectedRevision, value))
                {
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(SelectionTitle));
                    OnPropertyChanged(nameof(SelectedMessage));
                }
            }
        }

        public bool IsLoading => _isLoading;
        public bool CanRefresh => !_disposed && !IsLoading;
        public bool HasMore => _hasMore;
        public bool CanLoadMore => CanRefresh && HasMore && Revisions.Count > 0;
        public bool IsEmpty => !IsLoading && !HasError && Revisions.Count == 0;
        public bool HasSelection => SelectedRevision != null;
        public string SelectionTitle => SelectedRevision == null ? string.Empty : $"{SelectedRevision.RevisionText}   {SelectedRevision.Author}   {SelectedRevision.DateText}";
        public string SelectedMessage => SelectedRevision?.Message ?? string.Empty;
        public string LoadedText => App.Text("SVN.Loaded", Revisions.Count);

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (SetProperty(ref _errorMessage, value))
                    OnPropertyChanged(nameof(HasError));
            }
        }

        public string ErrorDetails
        {
            get => _errorDetails;
            private set => SetProperty(ref _errorDetails, value);
        }

        public bool NeedsCredentials
        {
            get => _needsCredentials;
            private set => SetProperty(ref _needsCredentials, value);
        }

        public string Username
        {
            get => _username;
            set
            {
                if (SetProperty(ref _username, value ?? string.Empty))
                    OnPropertyChanged(nameof(CanSignIn));
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

        public bool CanRemember => !string.IsNullOrWhiteSpace(Preferences.Instance.SvnConfigDirectory);
        public string RememberHint => App.Text(CanRemember ? "SVN.Connection.RememberHint" : "SVN.Connection.RememberRequiresConfig");
        public bool CanSignIn => CanRefresh && !string.IsNullOrWhiteSpace(Username);

        public SvnRepository(string fullPath)
        {
            FullPath = fullPath;
        }

        public Task RefreshAsync() => LoadAsync(false);
        public Task RetryAsync() => LoadAsync(_retryPage);
        public Task LoadMoreAsync() => CanLoadMore ? LoadAsync(true) : Task.CompletedTask;

        public Task SignInAsync()
        {
            if (!CanSignIn)
                return Task.CompletedTask;

            _credentials = new Native.Svn.Credentials(Username, Password, Remember);
            _credentialsConfigDirectory = Preferences.Instance.SvnConfigDirectory;
            Password = string.Empty;
            return LoadAsync(_retryPage);
        }

        public void Cancel() => _cancellation?.Cancel();
        public void OpenInFileManager() => Native.OS.OpenInFileManager(FullPath);
        public void OpenTerminal() => Native.OS.OpenTerminal(FullPath);

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _credentials = null;
            Password = string.Empty;
            _cancellation?.Cancel();
        }

        private async Task LoadAsync(bool append)
        {
            if (!CanRefresh)
                return;

            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _isLoading = true;
            _retryPage = append;
            ErrorMessage = string.Empty;
            ErrorDetails = string.Empty;
            NotifyState();

            // Capture settings once so an operation cannot mix configuration directories.
            var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
            var configDirectory = Preferences.Instance.SvnConfigDirectory;
            if (!string.Equals(configDirectory, _credentialsConfigDirectory, StringComparison.Ordinal))
                _credentials = null;
            try
            {
                if (!append)
                {
                    var info = await Task.Run(() => Native.Svn.GetWorkingCopyAsync(executable, configDirectory, FullPath, cancellation.Token), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (info.Status != "Ready")
                    {
                        SetFailure(info.Status, info.Details);
                        return;
                    }

                    if (!string.IsNullOrEmpty(Url) && !string.Equals(Url, info.Url, StringComparison.Ordinal))
                    {
                        _credentials = null;
                        Revisions.Clear();
                        SelectedRevision = null;
                        _hasMore = false;
                    }

                    Url = info.Url;
                }

                long? before = append && Revisions.Count > 0 ? Revisions[^1].Number : null;
                var result = await Task.Run(() => Native.Svn.GetHistoryAsync(executable, configDirectory, FullPath,
                    beforeRevision: before, limit: PageSize, credentials: _credentials,
                    cancellationToken: cancellation.Token, expectedUrl: Url), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (result.Status != "Ready")
                {
                    SetFailure(result.Status, result.Details);
                    return;
                }

                var selectedNumber = SelectedRevision?.Number;
                if (!append)
                    Revisions.Clear();
                Revisions.AddRange(result.Revisions);
                _hasMore = result.Revisions.Count == PageSize && result.Revisions[^1].Number > 0;
                if (!append)
                    SelectedRevision = Revisions.FirstOrDefault(revision => revision.Number == selectedNumber) ?? Revisions.FirstOrDefault();
                NeedsCredentials = false;
            }
            catch (OperationCanceledException)
            {
                // Closing a tab or cancelling a query leaves already loaded revisions intact.
            }
            catch
            {
                if (!_disposed)
                    SetFailure("Failed", string.Empty);
            }
            finally
            {
                _cancellation = null;
                _isLoading = false;
                if (!_disposed)
                    NotifyState();
            }
        }

        private void SetFailure(string status, string details)
        {
            ErrorMessage = App.Text(status switch
            {
                "InvalidWorkingCopy" or "NotWorkingCopy" => "SVN.InvalidWorkingCopy",
                "WorkingCopyChanged" => "SVN.WorkingCopyChanged",
                _ => $"SVN.Connection.{status}",
            });
            ErrorDetails = details;
            NeedsCredentials = status is "AuthenticationRequired" or "AuthenticationFailed" or "CredentialStorageUnavailable";
            if (NeedsCredentials)
                _credentials = null;
        }

        private void NotifyState()
        {
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(CanRefresh));
            OnPropertyChanged(nameof(HasMore));
            OnPropertyChanged(nameof(CanLoadMore));
            OnPropertyChanged(nameof(CanSignIn));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(LoadedText));
            OnPropertyChanged(nameof(CanRemember));
            OnPropertyChanged(nameof(RememberHint));
        }

        private const int PageSize = 100;
        private CancellationTokenSource _cancellation;
        private Native.Svn.Credentials _credentials;
        private Native.SvnRevision _selectedRevision;
        private bool _disposed;
        private bool _isLoading;
        private bool _hasMore;
        private bool _retryPage;
        private bool _needsCredentials;
        private bool _remember;
        private string _url = string.Empty;
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _errorMessage = string.Empty;
        private string _errorDetails = string.Empty;
        private string _credentialsConfigDirectory = string.Empty;
    }
}
