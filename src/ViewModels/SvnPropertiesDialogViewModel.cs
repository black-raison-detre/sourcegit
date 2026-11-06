using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public sealed class SvnPropertyItem
    {
        public string Name { get; }
        public string Value { get; }
        public bool IsBinary { get; }

        public SvnPropertyItem(Native.Svn.SvnProperty property)
        {
            Name = property.Name;
            Value = property.Value ?? string.Empty;
            IsBinary = property.IsBinary;
        }
    }

    public partial class SvnPropertiesDialogViewModel : ObservableObject
    {
        public SvnWorkingCopy WorkingCopy { get; }
        public ObservableCollection<SvnPropertyItem> Properties { get; } = new();
        public string WorkingCopyPath => WorkingCopy?.FullPath ?? string.Empty;
        public string LoadedPath => _snapshot?.RelativePath ?? string.Empty;
        public bool PathMatchesSnapshot => _snapshot != null && NormalizePath(PathInput).Equals(
            NormalizePath(_snapshot.RelativePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        public bool IsLoading => _isLoading;
        public bool IsSaving => _isSaving;
        public bool IsBusy => IsLoading || IsSaving;
        public bool IsLoaded => _snapshot != null;
        public bool HasError => !string.IsNullOrEmpty(_errorMessage);
        public string ErrorMessage => _errorMessage;
        public string ErrorDetails => _errorDetails;
        public string ReadOnlyNotice => _snapshot != null && (_requiresReload || !PathMatchesSnapshot)
            ? App.Text("SVN.Properties.ReloadRequired") : CurrentName == "svn:special" && _isNewProperty
                ? App.Text("SVN.Properties.SpecialReadOnly") : _readOnlyNotice;
        public bool HasReadOnlyNotice => !string.IsNullOrEmpty(ReadOnlyNotice);
        public bool HasSelection => _selectedProperty != null || _isNewProperty;
        public string SelectedPropertyName => _selectedProperty?.Name ?? string.Empty;
        public bool IsNewProperty => _isNewProperty;
        public bool IsValueReadOnly => CurrentName == "svn:special" ||
            (!_isNewProperty && (_selectedProperty?.IsBinary == true || _selectedProperty?.Value.Contains('\r') == true));
        public string PathInput
        {
            get => _pathInput;
            set
            {
                if (SetProperty(ref _pathInput, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(PathMatchesSnapshot));
                    NotifyState();
                }
            }
        }

        public string CurrentName
        {
            get => _currentName;
            set
            {
                if (!SetProperty(ref _currentName, value ?? string.Empty))
                    return;
                NotifyEditorState();
            }
        }

        public string CurrentValue
        {
            get => _currentValue;
            set
            {
                if (!SetProperty(ref _currentValue, value ?? string.Empty))
                    return;
                NotifyEditorState();
            }
        }

        public string ValueEndingHint => CurrentValue.Length == 0 ? App.Text("SVN.Properties.EmptyValue") :
            CurrentValue.EndsWith("\n", StringComparison.Ordinal) || CurrentValue.EndsWith("\r", StringComparison.Ordinal)
                ? App.Text("SVN.Properties.FinalNewline") : App.Text("SVN.Properties.NoFinalNewline");
        public string ValidationMessage => !_isNewProperty ? string.Empty :
            string.IsNullOrWhiteSpace(CurrentName) ? App.Text("SVN.Properties.NameRequired") :
            Properties.Any(property => property.Name == CurrentName) ? App.Text("SVN.Properties.NameExists") : string.Empty;

        public bool CanLoad => !IsBusy && WorkingCopy?.CanFileOperations == true && !string.IsNullOrWhiteSpace(PathInput);
        public bool CanAdd => !IsBusy && IsLoaded && PathMatchesSnapshot && !_requiresReload && WorkingCopy?.CanFileOperations == true;
        public bool CanDelete => !IsBusy && IsLoaded && PathMatchesSnapshot && !_requiresReload && WorkingCopy?.CanFileOperations == true && !_isNewProperty &&
            _selectedProperty != null && !_selectedProperty.IsBinary && _selectedProperty.Name != "svn:special";
        public bool CanReload => !IsBusy && WorkingCopy?.CanFileOperations == true && !string.IsNullOrWhiteSpace(PathInput);
        public bool CanSave => !IsBusy && IsLoaded && PathMatchesSnapshot && !_requiresReload && WorkingCopy?.CanFileOperations == true &&
            HasSelection && !IsValueReadOnly && CurrentName != "svn:special" && !string.IsNullOrWhiteSpace(CurrentName) &&
            (_isNewProperty ? !Properties.Any(property => property.Name == CurrentName) : _selectedProperty?.Name == CurrentName);
        public bool HasDraft => _isNewProperty || (_selectedProperty != null && CurrentValue != _selectedProperty.Value);
        public bool RequiresReload => _requiresReload;

        public SvnPropertiesDialogViewModel(SvnWorkingCopy workingCopy, string relativePath = null)
        {
            WorkingCopy = workingCopy;
            _pathInput = string.IsNullOrWhiteSpace(relativePath) ? "." : relativePath;
        }

        public async Task<bool> LoadAsync(string relativePath)
        {
            if (IsBusy || WorkingCopy == null || !WorkingCopy.CanFileOperations || string.IsNullOrWhiteSpace(relativePath))
            {
                SetError(App.Text("SVN.Properties.WorkingCopyNotReady"), string.Empty);
                return false;
            }

            _loadCancellation?.Cancel();
            using var cancellation = new CancellationTokenSource();
            _loadCancellation = cancellation;
            var generation = ++_loadGeneration;
            _isLoading = true;
            _requiresReload = true;
            _errorMessage = string.Empty;
            _errorDetails = string.Empty;
            OnPropertyChanged(nameof(IsLoading));
            NotifyState();
            try
            {
                var result = await WorkingCopy.ReadPropertiesAsync(relativePath, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_loadGeneration != generation)
                    return false;

                if (result?.Status != "Ready" || result.Snapshot == null)
                {
                    SetError(App.Text("SVN.Properties.LoadFailed"), result?.Details ?? string.Empty);
                    OnPropertyChanged(nameof(ReadOnlyNotice));
                    return false;
                }

                _snapshot = result.Snapshot;
                PathInput = result.Snapshot.RelativePath;
                Properties.Clear();
                foreach (var property in result.Snapshot.Properties.OrderBy(property => property.Name, StringComparer.Ordinal))
                    Properties.Add(new SvnPropertyItem(property));
                _selectedProperty = null;
                _isNewProperty = false;
                _currentName = string.Empty;
                _currentValue = string.Empty;
                _requiresReload = false;
                _readOnlyNotice = string.Empty;
                NotifyEditorState();
                return true;
            }
            catch (OperationCanceledException)
            {
                if (_loadGeneration == generation)
                    SetError(App.Text("SVN.Properties.LoadCancelled"), string.Empty);
                return false;
            }
            catch (Exception ex)
            {
                if (_loadGeneration == generation)
                    SetError(App.Text("SVN.Properties.LoadFailed"), ex.Message);
                return false;
            }
            finally
            {
                if (_loadGeneration == generation)
                {
                    _loadCancellation = null;
                    _isLoading = false;
                    OnPropertyChanged(nameof(IsLoading));
                    NotifyState();
                }
            }
        }

        public void SelectProperty(string name)
        {
            if (IsBusy || _snapshot == null)
                return;

            var property = Properties.FirstOrDefault(item => item.Name == name);
            if (property == null)
                return;

            _selectedProperty = property;
            _isNewProperty = false;
            _currentName = property.Name;
            _currentValue = property.Value;
            _readOnlyNotice = property.Name == "svn:special" ? App.Text("SVN.Properties.SpecialReadOnly") :
                property.IsBinary ? App.Text("SVN.Properties.BinaryReadOnly") :
                property.Value.Contains('\r') ? App.Text("SVN.Properties.CarriageReturnReadOnly") : string.Empty;
            NotifyEditorState();
        }

        public void BeginAddProperty()
        {
            if (!CanAdd)
                return;

            _selectedProperty = null;
            _isNewProperty = true;
            _currentName = string.Empty;
            _currentValue = string.Empty;
            _readOnlyNotice = string.Empty;
            NotifyEditorState();
        }

        public void CancelDraft()
        {
            if (_selectedProperty != null)
            {
                _isNewProperty = false;
                _currentName = _selectedProperty.Name;
                _currentValue = _selectedProperty.Value;
            }
            else
            {
                _isNewProperty = false;
                _currentName = string.Empty;
                _currentValue = string.Empty;
            }
            _readOnlyNotice = string.Empty;
            NotifyEditorState();
        }

        public void CancelLoad() => _loadCancellation?.Cancel();
        public void CancelOperation() => WorkingCopy?.CancelAction();

        public void ReportPickerError(Exception exception) =>
            SetError(App.Text("SVN.Properties.BrowseFailed"), exception?.Message ?? string.Empty);

        public async Task<bool> SaveAsync()
        {
            if (!CanSave)
                return false;

            _isSaving = true;
            _errorMessage = string.Empty;
            _errorDetails = string.Empty;
            NotifyState();
            var name = CurrentName;
            var succeeded = false;
            try
            {
                succeeded = await WorkingCopy.SetPropertyAsync(_snapshot, name, CurrentValue);
                if (!succeeded)
                {
                    _requiresReload = true;
                    SetError(WorkingCopy.ActionErrorMessage.Length == 0 ? App.Text("SVN.Properties.SaveFailed") : WorkingCopy.ActionErrorMessage,
                        WorkingCopy.ActionErrorDetails);
                    return false;
                }
            }
            finally
            {
                _isSaving = false;
                NotifyState();
            }

            if (!await LoadAsync(_snapshot.RelativePath))
                return false;
            var saved = Properties.FirstOrDefault(property => property.Name == name);
            if (saved != null)
                SelectProperty(saved.Name);
            return true;
        }

        public async Task<bool> DeleteAsync()
        {
            if (!CanDelete)
                return false;

            _isSaving = true;
            _errorMessage = string.Empty;
            _errorDetails = string.Empty;
            NotifyState();
            var deletedName = _selectedProperty.Name;
            var succeeded = false;
            try
            {
                succeeded = await WorkingCopy.DeletePropertyAsync(_snapshot, deletedName);
                if (!succeeded)
                {
                    _requiresReload = true;
                    SetError(WorkingCopy.ActionErrorMessage.Length == 0 ? App.Text("SVN.Properties.DeleteFailed") : WorkingCopy.ActionErrorMessage,
                        WorkingCopy.ActionErrorDetails);
                    return false;
                }
            }
            finally
            {
                _isSaving = false;
                NotifyState();
            }

            return await LoadAsync(_snapshot.RelativePath);
        }

        private void SetError(string message, string details)
        {
            _errorMessage = message ?? string.Empty;
            _errorDetails = details ?? string.Empty;
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(ErrorDetails));
            OnPropertyChanged(nameof(HasError));
        }

        private void NotifyState()
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(LoadedPath));
            OnPropertyChanged(nameof(PathMatchesSnapshot));
            OnPropertyChanged(nameof(CanLoad));
            OnPropertyChanged(nameof(CanAdd));
            OnPropertyChanged(nameof(CanDelete));
            OnPropertyChanged(nameof(CanReload));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(RequiresReload));
            OnPropertyChanged(nameof(ReadOnlyNotice));
            OnPropertyChanged(nameof(HasReadOnlyNotice));
            OnPropertyChanged(nameof(HasDraft));
            OnPropertyChanged(nameof(HasError));
        }

        private void NotifyEditorState()
        {
            OnPropertyChanged(nameof(CurrentName));
            OnPropertyChanged(nameof(CurrentValue));
            OnPropertyChanged(nameof(ValueEndingHint));
            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedPropertyName));
            OnPropertyChanged(nameof(IsNewProperty));
            OnPropertyChanged(nameof(IsValueReadOnly));
            OnPropertyChanged(nameof(ReadOnlyNotice));
            OnPropertyChanged(nameof(HasReadOnlyNotice));
            OnPropertyChanged(nameof(HasDraft));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanDelete));
        }

        private Native.Svn.SvnPropertiesSnapshot _snapshot;
        private SvnPropertyItem _selectedProperty;
        private CancellationTokenSource _loadCancellation;
        private string _pathInput;
        private string _currentName = string.Empty;
        private string _currentValue = string.Empty;
        private string _errorMessage = string.Empty;
        private string _errorDetails = string.Empty;
        private string _readOnlyNotice = string.Empty;
        private bool _isLoading;
        private bool _isSaving;
        private bool _isNewProperty;
        private bool _requiresReload;
        private int _loadGeneration;

        private static string NormalizePath(string path)
        {
            var normalized = OperatingSystem.IsWindows() ? (path ?? string.Empty).Replace('\\', '/') : path ?? string.Empty;
            while (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized[2..];
            return normalized.Length == 0 ? "." : normalized;
        }
    }
}
