using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class SvnPreferences : ObservableObject, IDisposable
    {
        public string InstallPath
        {
            get => _preferences.SvnInstallPath;
            set => _preferences.SvnInstallPath = value ?? string.Empty;
        }

        public string ConfigDirectory
        {
            get => _preferences.SvnConfigDirectory;
            set => _preferences.SvnConfigDirectory = value ?? string.Empty;
        }

        public string ResolvedConfigDirectory
        {
            get
            {
                try
                {
                    return Native.Svn.ResolveConfigDirectory(ConfigDirectory);
                }
                catch
                {
                    return ConfigDirectory;
                }
            }
        }

        public bool CanOpenConfigDirectory => _canOpenConfigDirectory;
        public bool CanTestConnection => !_disposed && _status == "Ready" && !string.IsNullOrEmpty(Version);

        public string CredentialStores => _credentialStores switch
        {
            null => App.Text($"Preferences.SVN.Storage.{(_status == "Checking" ? "Checking" : "Unknown")}"),
            "" => App.Text("Preferences.SVN.Storage.None"),
            _ => _credentialStores,
        };

        public string Executable
        {
            get => _executable;
            private set => SetProperty(ref _executable, value);
        }

        public string Version
        {
            get => _version;
            private set => SetProperty(ref _version, value);
        }

        public string Status => App.Text($"Preferences.SVN.{_status}");

        public string Details
        {
            get => _details;
            private set => SetProperty(ref _details, value);
        }

        public SvnPreferences(Preferences preferences)
        {
            _preferences = preferences;
            _preferences.PropertyChanged += OnPreferenceChanged;
        }

        public async Task RefreshAsync()
        {
            if (_disposed)
                return;

            _cancellation?.Cancel();
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            var configuredPath = InstallPath;
            var configDirectory = ConfigDirectory;
            var resolvedConfigDirectory = ResolvedConfigDirectory;
            Executable = string.Empty;
            Version = string.Empty;
            Details = string.Empty;
            _credentialStores = null;
            _canOpenConfigDirectory = false;
            OnPropertyChanged(nameof(CanOpenConfigDirectory));
            SetStatus("Checking");

            try
            {
                // Debounce typing and keep file-system discovery off the UI thread.
                await Task.Delay(250, cancellation.Token);
                var executable = await Task.Run(() => Native.Svn.ResolveExecutable(configuredPath), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                Executable = executable;

                var result = await Task.Run(() => Native.Svn.ProbeAsync(executable, configDirectory, cancellation.Token), cancellation.Token);
                var configExists = await Task.Run(() => Path.IsPathFullyQualified(resolvedConfigDirectory) && Directory.Exists(resolvedConfigDirectory), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                Version = result.Version;
                Details = result.Details;
                _credentialStores = result.CredentialStores;
                _canOpenConfigDirectory = configExists;
                SetStatus(result.Status);
                OnPropertyChanged(nameof(CanOpenConfigDirectory));
            }
            catch (OperationCanceledException)
            {
                // A newer path or a closed Preferences window supersedes this result.
            }
            catch (Exception ex)
            {
                if (!cancellation.IsCancellationRequested)
                {
                    Details = ex.Message;
                    SetStatus("FailedToStart");
                }
            }
            finally
            {
                if (_cancellation == cancellation)
                    _cancellation = null;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _preferences.PropertyChanged -= OnPreferenceChanged;
            _cancellation?.Cancel();
            OnPropertyChanged(nameof(CanTestConnection));
        }

        private void OnPreferenceChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Preferences.SvnInstallPath) || e.PropertyName == nameof(Preferences.SvnConfigDirectory))
            {
                OnPropertyChanged(nameof(InstallPath));
                OnPropertyChanged(nameof(ConfigDirectory));
                OnPropertyChanged(nameof(ResolvedConfigDirectory));
                OnPropertyChanged(nameof(CanOpenConfigDirectory));
                _ = RefreshAsync();
            }
            else if (e.PropertyName == nameof(Preferences.Locale))
            {
                // The preference notification precedes App.SetLocale updating the resources.
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed)
                    {
                        OnPropertyChanged(nameof(Status));
                        OnPropertyChanged(nameof(CredentialStores));
                    }
                });
            }
        }

        private void SetStatus(string status)
        {
            _status = status;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(CanTestConnection));
            OnPropertyChanged(nameof(CredentialStores));
        }

        private readonly Preferences _preferences;
        private CancellationTokenSource _cancellation;
        private bool _disposed = false;
        private bool _canOpenConfigDirectory = false;
        private string _executable = string.Empty;
        private string _version = string.Empty;
        private string _details = string.Empty;
        private string _status = "Checking";
        private string _credentialStores = null;
    }
}
