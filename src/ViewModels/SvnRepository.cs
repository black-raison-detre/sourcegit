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
        public AvaloniaList<Native.SvnChangedPath> ChangedPaths { get; } = new();

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
                    InvalidateChangedPaths();
                    _ = RetryChangedPathsAsync();
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
        public bool IsLoadingPaths => _isLoadingPaths;
        public bool CanRetryPaths => !_disposed && !IsLoading && !IsLoadingPaths && HasSelection;
        public bool HasPathsError => !string.IsNullOrEmpty(_pathsStatus) && _pathsStatus != "Ready";
        public bool IsPathsEmpty => _pathsStatus == "Ready" && ChangedPaths.Count == 0;
        public string PathsTitle => App.Text("SVN.ChangedPaths", ChangedPaths.Count);
        public string PathsErrorMessage => !HasPathsError ? string.Empty :
            _pathsStatus == "Cancelled" ? App.Text("SVN.PathsCancelled") : FailureMessage(_pathsStatus);
        public string PathsErrorDetails => _pathsErrorDetails;

        public Native.SvnChangedPath SelectedChangedPath
        {
            get => _selectedChangedPath;
            set
            {
                if (SetProperty(ref _selectedChangedPath, value))
                {
                    InvalidateDiff();
                    _ = RetryDiffAsync();
                }
            }
        }

        public bool IsLoadingDiff => _isLoadingDiff;
        public bool CanRetryDiff => !_disposed && !IsLoading && !IsLoadingPaths && !IsLoadingDiff && SelectedChangedPath != null;
        public bool HasDiffError => !string.IsNullOrEmpty(_diffStatus) && _diffStatus != "Ready";
        public string DiffErrorMessage => !HasDiffError ? string.Empty :
            _diffStatus == "Cancelled" ? App.Text("SVN.Diff.Cancelled") : FailureMessage(_diffStatus);
        public string DiffErrorDetails => _diffErrorDetails;
        public string DiffNotice => SelectedChangedPath == null ? App.Text("SVN.Diff.SelectFile") : _diffNotice;
        public string DiffTitle => SelectedChangedPath?.Path ?? string.Empty;
        public string DiffBeforeText => _diffBeforeText;
        public string DiffAfterText => _diffAfterText;
        public TextDiffContext DiffContent => _diffContent;
        public bool IsTextDiff => DiffContent != null;

        public bool DiffModeSideBySide
        {
            get => Preferences.Instance.UseSideBySideDiff;
            set
            {
                if (value == DiffModeSideBySide)
                    return;

                Preferences.Instance.UseSideBySideDiff = value;
                if (_diffContent != null && _diffContent.IsSideBySide() != value)
                    _diffContent = _diffContent.SwitchMode();
                NotifyDiffState();
            }
        }

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
        public bool CanSignIn => CanRefresh && !IsLoadingPaths && !IsLoadingDiff && !string.IsNullOrWhiteSpace(Username);

        public SvnRepository(string fullPath)
        {
            FullPath = fullPath;
        }

        public Task RefreshAsync() => LoadAsync(false);
        public Task RetryAsync() => _retryFileDiff ? RetryDiffAsync() : _retryPaths ? RetryChangedPathsAsync() : LoadAsync(_retryPage);
        public Task LoadMoreAsync() => CanLoadMore ? LoadAsync(true) : Task.CompletedTask;

        public Task SignInAsync()
        {
            if (!CanSignIn)
                return Task.CompletedTask;

            _credentials = new Native.Svn.Credentials(Username, Password, Remember);
            _credentialsConfigDirectory = Preferences.Instance.SvnConfigDirectory;
            Password = string.Empty;
            return RetryAsync();
        }

        public void Cancel()
        {
            _pathsReloadPending = false;
            _diffReloadPending = false;
            _cancellation?.Cancel();
            CancelChangedPaths();
            CancelDiff();
        }

        public void CancelChangedPaths() => _pathsCancellation?.Cancel();
        public void CancelDiff() => _diffCancellation?.Cancel();

        public Task RetryDiffAsync()
        {
            if (_disposed || SelectedRevision == null || SelectedChangedPath == null)
                return Task.CompletedTask;
            if (IsLoading)
            {
                _diffReloadPending = true;
                return Task.CompletedTask;
            }
            return IsLoadingDiff || IsLoadingPaths ? Task.CompletedTask : LoadDiffAsync();
        }

        public Task RetryChangedPathsAsync()
        {
            if (_disposed || !HasSelection)
                return Task.CompletedTask;
            if (IsLoading)
            {
                _pathsReloadPending = true;
                return Task.CompletedTask;
            }
            return IsLoadingPaths ? Task.CompletedTask : LoadChangedPathsAsync();
        }

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
            _pathsCancellation?.Cancel();
            _diffCancellation?.Cancel();
        }

        private void InvalidateDiff()
        {
            _diffCancellation?.Cancel();
            _diffCancellation = null;
            _isLoadingDiff = false;
            _diffStatus = string.Empty;
            _diffErrorDetails = string.Empty;
            _diffNotice = string.Empty;
            _diffBeforeText = string.Empty;
            _diffAfterText = string.Empty;
            _diffContent = null;
            ClearDiffAuthenticationError();
            NotifyDiffState();
        }

        private void ClearDiffAuthenticationError()
        {
            if (!_retryFileDiff)
                return;

            _retryFileDiff = false;
            ErrorMessage = string.Empty;
            ErrorDetails = string.Empty;
            NeedsCredentials = false;
        }

        private async Task LoadDiffAsync()
        {
            using var cancellation = new CancellationTokenSource();
            _diffCancellation = cancellation;
            var revision = SelectedRevision.Number;
            var path = SelectedChangedPath.Path;
            _isLoadingDiff = true;
            _diffStatus = string.Empty;
            _diffErrorDetails = string.Empty;
            _diffNotice = string.Empty;
            _diffContent = null;
            ClearDiffAuthenticationError();
            NotifyDiffState();

            var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
            var configDirectory = Preferences.Instance.SvnConfigDirectory;
            if (!string.Equals(configDirectory, _credentialsConfigDirectory, StringComparison.Ordinal))
                _credentials = null;
            var credentials = _credentials;
            var expectedUrl = Url;
            var contextLines = Preferences.Instance.UseFullTextDiff ? 50000 : 3;
            var ignoreWhitespace = Preferences.Instance.IgnoreWhitespaceChangesInDiff;
            var ignoreCRAtEOL = Preferences.Instance.IgnoreCRAtEOLInDiff;
            try
            {
                var result = await Task.Run(() => Native.Svn.GetFileDiffAsync(executable, configDirectory, FullPath,
                    revision, path, credentials, cancellation.Token, expectedUrl), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_disposed || _diffCancellation != cancellation)
                    return;

                _diffStatus = result.Status;
                _diffErrorDetails = result.Details;
                if (result.Status == "TooLarge")
                {
                    _diffStatus = "Ready";
                    _diffNotice = App.Text("SVN.Diff.TooLarge");
                }
                else if (result.Status == "Directory" && result.IsDirectory)
                {
                    _diffStatus = "Ready";
                    _diffNotice = App.Text("SVN.Diff.Directory");
                }
                else if (result.Status == "Ready")
                {
                    if (result.IsDirectory)
                    {
                        _diffNotice = App.Text("SVN.Diff.Directory");
                    }
                    else
                    {
                        _diffBeforeText = SnapshotTitle("Before", result.Before);
                        _diffAfterText = SnapshotTitle("After", result.After);
                        if (result.Before?.IsSpecial == true || result.After?.IsSpecial == true)
                            _diffNotice = App.Text("SVN.Diff.Special");
                        else if (result.Before?.IsBinary == true || result.After?.IsBinary == true)
                            _diffNotice = BinaryNotice(result.Before, result.After);
                        else
                        {
                            var comparison = await Task.Run(() => Commands.SnapshotDiff.ReadAsync(
                                result.Before?.Content ?? [], result.After?.Content ?? [], cancellation.Token,
                                contextLines, ignoreWhitespace, ignoreCRAtEOL), cancellation.Token);
                            cancellation.Token.ThrowIfCancellationRequested();
                            if (_disposed || _diffCancellation != cancellation)
                                return;

                            _diffStatus = comparison.Status;
                            _diffErrorDetails = comparison.Details;
                            if (comparison.Status == "TooLarge")
                            {
                                _diffStatus = "Ready";
                                _diffNotice = App.Text("SVN.Diff.TooLarge");
                            }
                            else if (comparison.Status == "Ready")
                            {
                                if (comparison.IsBinary)
                                    _diffNotice = BinaryNotice(result.Before, result.After);
                                else if (comparison.Diff != null)
                                {
                                    var option = new Models.DiffOption(result.Before?.Revision.ToString() ?? "absent",
                                        result.After?.Revision.ToString() ?? "absent", new Models.Change
                                        {
                                            Path = path,
                                            OriginalPath = result.Before?.Path ?? string.Empty,
                                        });
                                    _diffContent = DiffModeSideBySide
                                        ? new TwoSideTextDiff(option, comparison.Diff)
                                        : new CombinedTextDiff(option, comparison.Diff);
                                }
                                else
                                {
                                    var isEmpty = (result.Before?.Content.Length ?? 0) == 0 && (result.After?.Content.Length ?? 0) == 0;
                                    _diffNotice = App.Text(isEmpty ? "SVN.Diff.Empty" : "SVN.Diff.Unchanged");
                                }
                            }
                        }
                    }
                }
                else if (result.Status is "AuthenticationRequired" or "AuthenticationFailed" or "CredentialStorageUnavailable")
                {
                    _retryFileDiff = true;
                    SetFailure(result.Status, result.Details);
                }
            }
            catch (OperationCanceledException)
            {
                if (_diffCancellation == cancellation)
                    _diffStatus = "Cancelled";
            }
            catch
            {
                if (_diffCancellation == cancellation)
                    _diffStatus = "Failed";
            }
            finally
            {
                if (_diffCancellation == cancellation)
                {
                    _diffCancellation = null;
                    _isLoadingDiff = false;
                    if (!_disposed)
                        NotifyDiffState();
                }
            }
        }

        private static string SnapshotTitle(string side, Native.Svn.SvnFileSnapshot snapshot) => App.Text("SVN.Diff." + side,
            snapshot == null ? App.Text("SVN.Diff.Absent") : $"{snapshot.Path} @ r{snapshot.Revision}");

        private static string BinaryNotice(Native.Svn.SvnFileSnapshot before, Native.Svn.SvnFileSnapshot after) => App.Text("SVN.Diff.Binary",
            before == null ? App.Text("SVN.Diff.Absent") : before.Content.LongLength.ToString("N0") + " B",
            after == null ? App.Text("SVN.Diff.Absent") : after.Content.LongLength.ToString("N0") + " B");

        private void InvalidateChangedPaths()
        {
            SetProperty(ref _selectedChangedPath, null, nameof(SelectedChangedPath));
            InvalidateDiff();
            _pathsCancellation?.Cancel();
            _pathsCancellation = null;
            _isLoadingPaths = false;
            _pathsStatus = string.Empty;
            _pathsErrorDetails = string.Empty;
            ChangedPaths.Clear();
            ClearPathsAuthenticationError();
            NotifyPathsState();
        }

        private void ClearPathsAuthenticationError()
        {
            if (!_retryPaths)
                return;

            _retryPaths = false;
            ErrorMessage = string.Empty;
            ErrorDetails = string.Empty;
            NeedsCredentials = false;
        }

        private async Task LoadChangedPathsAsync()
        {
            SetProperty(ref _selectedChangedPath, null, nameof(SelectedChangedPath));
            InvalidateDiff();
            using var cancellation = new CancellationTokenSource();
            _pathsCancellation = cancellation;
            var revision = SelectedRevision.Number;
            _isLoadingPaths = true;
            _pathsStatus = string.Empty;
            _pathsErrorDetails = string.Empty;
            ChangedPaths.Clear();
            ClearPathsAuthenticationError();
            NotifyPathsState();

            var executable = Native.Svn.ResolveExecutable(Preferences.Instance.SvnInstallPath);
            var configDirectory = Preferences.Instance.SvnConfigDirectory;
            if (!string.Equals(configDirectory, _credentialsConfigDirectory, StringComparison.Ordinal))
                _credentials = null;
            var credentials = _credentials;
            var expectedUrl = Url;
            try
            {
                var result = await Task.Run(() => Native.Svn.GetChangedPathsAsync(executable, configDirectory, FullPath,
                    revision, credentials, cancellation.Token, expectedUrl), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_disposed || _pathsCancellation != cancellation)
                    return;

                _pathsStatus = result.Status;
                _pathsErrorDetails = result.Details;
                if (result.Status == "Ready")
                    ChangedPaths.AddRange(result.Paths);
                else if (result.Status is "AuthenticationRequired" or "AuthenticationFailed" or "CredentialStorageUnavailable")
                {
                    _retryPaths = true;
                    SetFailure(result.Status, result.Details);
                }
            }
            catch (OperationCanceledException)
            {
                if (_pathsCancellation == cancellation)
                    _pathsStatus = "Cancelled";
            }
            catch
            {
                if (_pathsCancellation == cancellation)
                    _pathsStatus = "Failed";
            }
            finally
            {
                if (_pathsCancellation == cancellation)
                {
                    _pathsCancellation = null;
                    _isLoadingPaths = false;
                    if (!_disposed)
                        NotifyPathsState();
                }
            }
        }

        private async Task LoadAsync(bool append)
        {
            if (!CanRefresh)
                return;

            var reloadPaths = !append || IsLoadingPaths;
            var reloadDiff = append && IsLoadingDiff;
            if (IsLoadingDiff)
                InvalidateDiff();
            if (reloadPaths)
                InvalidateChangedPaths();
            ClearDiffAuthenticationError();
            ClearPathsAuthenticationError();
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _isLoading = true;
            _retryPage = append;
            _pathsReloadPending = reloadPaths;
            _diffReloadPending = reloadDiff;
            ErrorMessage = string.Empty;
            ErrorDetails = string.Empty;
            NotifyState();
            NotifyPathsState();

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
                _pathsReloadPending = false;
                _diffReloadPending = false;
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
                {
                    NotifyState();
                    NotifyPathsState();
                    if (_pathsReloadPending && !HasError)
                    {
                        _pathsReloadPending = false;
                        _ = RetryChangedPathsAsync();
                    }
                    else if (_diffReloadPending && !HasError)
                    {
                        _diffReloadPending = false;
                        _ = RetryDiffAsync();
                    }
                }
            }
        }

        private static string FailureMessage(string status) => App.Text(status switch
        {
            "InvalidWorkingCopy" or "NotWorkingCopy" => "SVN.InvalidWorkingCopy",
            "WorkingCopyChanged" => "SVN.WorkingCopyChanged",
            "PathNotChanged" => "SVN.WorkingCopyChanged",
            "InvalidArgument" => "SVN.Connection.Failed",
            _ => $"SVN.Connection.{status}",
        });

        private void SetFailure(string status, string details)
        {
            ErrorMessage = FailureMessage(status);
            ErrorDetails = details;
            NeedsCredentials = status is "AuthenticationRequired" or "AuthenticationFailed" or "CredentialStorageUnavailable";
            if (NeedsCredentials)
                _credentials = null;
        }

        private void NotifyPathsState()
        {
            OnPropertyChanged(nameof(IsLoadingPaths));
            OnPropertyChanged(nameof(CanRetryPaths));
            OnPropertyChanged(nameof(HasPathsError));
            OnPropertyChanged(nameof(IsPathsEmpty));
            OnPropertyChanged(nameof(PathsTitle));
            OnPropertyChanged(nameof(PathsErrorMessage));
            OnPropertyChanged(nameof(PathsErrorDetails));
            OnPropertyChanged(nameof(CanSignIn));
            NotifyDiffState();
        }

        private void NotifyDiffState()
        {
            OnPropertyChanged(nameof(IsLoadingDiff));
            OnPropertyChanged(nameof(CanRetryDiff));
            OnPropertyChanged(nameof(HasDiffError));
            OnPropertyChanged(nameof(DiffErrorMessage));
            OnPropertyChanged(nameof(DiffErrorDetails));
            OnPropertyChanged(nameof(DiffNotice));
            OnPropertyChanged(nameof(DiffTitle));
            OnPropertyChanged(nameof(DiffBeforeText));
            OnPropertyChanged(nameof(DiffAfterText));
            OnPropertyChanged(nameof(DiffContent));
            OnPropertyChanged(nameof(IsTextDiff));
            OnPropertyChanged(nameof(DiffModeSideBySide));
            OnPropertyChanged(nameof(CanSignIn));
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
        private CancellationTokenSource _pathsCancellation;
        private CancellationTokenSource _diffCancellation;
        private Native.SvnChangedPath _selectedChangedPath;
        private TextDiffContext _diffContent;
        private bool _isLoadingDiff;
        private bool _retryFileDiff;
        private bool _diffReloadPending;
        private string _diffStatus = string.Empty;
        private string _diffNotice = string.Empty;
        private string _diffErrorDetails = string.Empty;
        private string _diffBeforeText = string.Empty;
        private string _diffAfterText = string.Empty;
        private Native.Svn.Credentials _credentials;
        private Native.SvnRevision _selectedRevision;
        private bool _disposed;
        private bool _isLoading;
        private bool _hasMore;
        private bool _retryPage;
        private bool _needsCredentials;
        private bool _remember;
        private bool _isLoadingPaths;
        private bool _pathsReloadPending;
        private bool _retryPaths;
        private string _pathsStatus = string.Empty;
        private string _pathsErrorDetails = string.Empty;
        private string _url = string.Empty;
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _errorMessage = string.Empty;
        private string _errorDetails = string.Empty;
        private string _credentialsConfigDirectory = string.Empty;
    }
}
