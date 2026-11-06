using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public partial class SvnPathOperationDialogViewModel : ObservableObject
    {
        public SvnWorkingCopy WorkingCopy { get; }

        public string Operation
        {
            get => _operation;
            set
            {
                var normalized = NormalizeOperation(value);
                if (_operation == normalized)
                    return;

                _hasInteracted = true;
                if (_targetEdited)
                    _targetDrafts[_operation] = _targetInput;
                else
                    _targetDrafts.Remove(_operation);
                SetProperty(ref _operation, normalized);
                _targetEdited = _targetDrafts.TryGetValue(_operation, out var draft);
                SetProperty(ref _targetInput, _targetEdited ? draft : DefaultTarget(_operation), nameof(TargetInput));
                NotifyFormChanged();
                OnPropertyChanged(nameof(OperationLabel));
                OnPropertyChanged(nameof(PrimaryLabel));
                OnPropertyChanged(nameof(PendingCommitNotice));
            }
        }

        public string SourcePath
        {
            get => _sourcePath;
            set
            {
                var normalized = Canonicalize(value);
                if (!SetProperty(ref _sourcePath, normalized))
                    return;

                _hasInteracted = true;
                if (!_targetEdited)
                    SetProperty(ref _targetInput, DefaultTarget(_operation), nameof(TargetInput));

                NotifyFormChanged();
            }
        }

        public string TargetInput
        {
            get => _targetInput;
            set
            {
                if (!SetProperty(ref _targetInput, value ?? string.Empty))
                    return;

                _hasInteracted = true;
                _targetEdited = true;
                NotifyFormChanged();
            }
        }

        public bool IsExecuting
        {
            get => _isExecuting;
            private set
            {
                if (SetProperty(ref _isExecuting, value))
                {
                    OnPropertyChanged(nameof(CanSubmit));
                    OnPropertyChanged(nameof(PrimaryLabel));
                }
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);
        public string ErrorMessage => _errorMessage;
        public string ErrorDetails => _errorDetails;
        public string OperationLabel => App.Text("SVN.PathOperation." + OperationTitleKey);
        public string PrimaryLabel => App.Text(HasError ? "SVN.PathOperation.Retry" : "SVN.PathOperation." + OperationTitleKey);
        public string FieldLabel => App.Text(_operation == "rename" ? "SVN.PathOperation.NewName" : "SVN.PathOperation.Destination");
        public string DestinationPreview => TryGetPaths(out _, out var destination, out _) ? destination : string.Empty;
        public string PendingCommitNotice => App.Text("SVN.PathOperation.PendingCommit." + OperationTitleKey);
        public string ValidationMessage
        {
            get
            {
                var validation = GetValidationError();
                if (!_hasInteracted && validation is
                    {
                        Key: "SVN.PathOperation.SourceRequired" or
                            "SVN.PathOperation.NameRequired" or "SVN.PathOperation.DestinationRequired"
                    })
                    return string.Empty;
                return validation == null ? string.Empty : App.Text(validation.Value.Key, validation.Value.Arguments);
            }
        }

        public bool CanSubmit => !IsExecuting && GetValidationError() == null;

        public SvnPathOperationDialogViewModel(SvnWorkingCopy workingCopy, string operation = "rename", string source = null)
        {
            WorkingCopy = workingCopy;
            _operation = NormalizeOperation(operation);
            _sourcePath = Canonicalize(source);
            _targetInput = DefaultTarget(_operation);
        }

        public async Task<bool> SubmitAsync()
        {
            if (!CanSubmit || !TryGetPaths(out var source, out var destination, out _))
                return false;

            _errorMessage = string.Empty;
            _errorDetails = string.Empty;
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(ErrorDetails));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(PrimaryLabel));
            IsExecuting = true;
            try
            {
                var succeeded = await WorkingCopy.RunPathOperationAsync(_operation, source, destination);
                if (succeeded)
                {
                    WorkingCopy.SelectPathAfterOperation(destination);
                    return true;
                }

                _errorMessage = WorkingCopy.ActionErrorMessage;
                _errorDetails = WorkingCopy.ActionErrorDetails;
                if (string.IsNullOrWhiteSpace(_errorMessage))
                    _errorMessage = App.Text("SVN.PathOperation.Failed", source, destination);
                OnPropertyChanged(nameof(ErrorMessage));
                OnPropertyChanged(nameof(ErrorDetails));
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(PrimaryLabel));
                return false;
            }
            finally
            {
                IsExecuting = false;
            }
        }

        public void ReportPickerError(Exception exception)
        {
            _errorMessage = App.Text("SVN.PathOperation.BrowseFailed");
            _errorDetails = exception?.Message ?? string.Empty;
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(ErrorDetails));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(PrimaryLabel));
        }

        private string OperationTitleKey => _operation switch
        {
            "move" => "Move",
            "copy" => "Copy",
            _ => "Rename",
        };

        private void NotifyFormChanged()
        {
            OnPropertyChanged(nameof(DestinationPreview));
            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(CanSubmit));
            OnPropertyChanged(nameof(FieldLabel));
        }

        private string DefaultTarget(string operation)
        {
            if (string.IsNullOrWhiteSpace(_sourcePath))
                return string.Empty;

            if (operation == "rename")
                return Path.GetFileName(_sourcePath.TrimEnd('/'));

            return _sourcePath;
        }

        private bool TryGetPaths(out string source, out string destination, out bool sourceIsDirectory)
        {
            source = Canonicalize(_sourcePath);
            destination = string.Empty;
            sourceIsDirectory = false;
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(_targetInput))
                return false;

            if (_operation == "rename")
            {
                var parent = ParentPath(source);
                destination = parent == "." ? Canonicalize(_targetInput) : parent + "/" + Canonicalize(_targetInput);
            }
            else
            {
                destination = Canonicalize(_targetInput);
            }

            if (TryGetFullPath(source, out var sourceFull))
                sourceIsDirectory = Directory.Exists(sourceFull);
            return true;
        }

        private (string Key, object[] Arguments)? GetValidationError()
        {
            if (WorkingCopy == null || !WorkingCopy.CanFileOperations)
                return ("SVN.PathOperation.WorkingCopyNotReady", []);

            var source = Canonicalize(_sourcePath);
            if (string.IsNullOrWhiteSpace(source))
                return ("SVN.PathOperation.SourceRequired", []);
            if (source == ".")
                return ("SVN.PathOperation.RootNotAllowed", []);
            if (!IsSafeRelativePath(source, allowSeparators: true))
                return ("SVN.PathOperation.InvalidSource", []);
            if (!TryGetFullPath(source, out var sourceFull))
                return ("SVN.PathOperation.OutsideWorkingCopy", []);
            if (!File.Exists(sourceFull) && !Directory.Exists(sourceFull))
                return ("SVN.PathOperation.SourceMissing", []);
            if (!WorkingCopy.IsKnownPathOperationSourceEligible(source))
                return ("SVN.PathOperation.SourceNotEligible", []);

            var rawTarget = _targetInput ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawTarget))
                return (_operation == "rename" ? "SVN.PathOperation.NameRequired" : "SVN.PathOperation.DestinationRequired", []);

            if (_operation == "rename")
            {
                if (rawTarget.Contains('/') || (OperatingSystem.IsWindows() && rawTarget.Contains('\\')))
                    return ("SVN.PathOperation.NameOnly", []);
                if (!IsSafePathSegment(rawTarget))
                    return ("SVN.PathOperation.InvalidDestination", []);
            }
            else if (!IsSafeRelativePath(rawTarget, allowSeparators: true))
            {
                return ("SVN.PathOperation.InvalidDestination", []);
            }

            if (!TryGetPaths(out _, out var destination, out var sourceIsDirectory))
                return ("SVN.PathOperation.InvalidDestination", []);
            if (destination == ".")
                return ("SVN.PathOperation.RootNotAllowed", []);

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(source, destination, comparison))
                return ("SVN.PathOperation.DestinationMustDiffer", []);

            if (sourceIsDirectory && destination.StartsWith(source.TrimEnd('/') + "/", comparison))
                return ("SVN.PathOperation.SelfDescendant", []);

            if (!TryGetFullPath(destination, out var destinationFull))
                return ("SVN.PathOperation.OutsideWorkingCopy", []);
            if (File.Exists(destinationFull) || Directory.Exists(destinationFull))
                return ("SVN.PathOperation.DestinationExists", []);

            var parent = ParentPath(destination);
            if (!TryGetFullPath(parent, out var parentFull) || !Directory.Exists(parentFull))
                return ("SVN.PathOperation.ParentMissing", []);
            if (!WorkingCopy.IsKnownPathOperationParentEligible(parent))
                return ("SVN.PathOperation.ParentNotVersioned", []);

            return null;
        }

        private bool TryGetFullPath(string relativePath, out string fullPath)
        {
            fullPath = string.Empty;
            if (WorkingCopy == null || (relativePath != "." && !IsSafeRelativePath(relativePath, allowSeparators: true)))
                return false;

            try
            {
                var root = Path.GetFullPath(WorkingCopy.FullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (root.Length == 0)
                    root = Path.DirectorySeparatorChar.ToString();
                var candidate = relativePath == "." ? root :
                    Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!candidate.Equals(root, comparison) && !candidate.StartsWith(rootPrefix, comparison))
                    return false;

                fullPath = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSafeRelativePath(string path, bool allowSeparators)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') ||
                (OperatingSystem.IsWindows() && path.StartsWith('\\')) ||
                Path.IsPathRooted(path) || (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':'))
                return false;

            var canonical = Canonicalize(path);
            if (!allowSeparators && canonical.Contains('/'))
                return false;

            var parts = canonical.Split('/');
            return parts.Length > 0 && parts.All(IsSafePathSegment);
        }

        private static bool IsSafePathSegment(string segment) => !string.IsNullOrWhiteSpace(segment) &&
            segment is not "." and not ".." && !segment.Equals(".svn", StringComparison.OrdinalIgnoreCase) &&
            !segment.Any(char.IsControl) && !segment.EndsWith('.') && !segment.EndsWith(' ') &&
            !segment.Any(character => "<>:\"|?*".Contains(character)) && !IsWindowsReservedName(segment);

        private static bool IsWindowsReservedName(string segment)
        {
            var name = segment.Split('.')[0];
            if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
                return true;

            return name.Length == 4 &&
                (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                name[3] is >= '1' and <= '9';
        }

        private static string ParentPath(string path)
        {
            var separator = path.LastIndexOf('/');
            return separator < 0 ? "." : separator == 0 ? "." : path[..separator];
        }

        private static string Canonicalize(string path) => OperatingSystem.IsWindows()
            ? (path ?? string.Empty).Replace('\\', '/')
            : path ?? string.Empty;

        private static string NormalizeOperation(string operation) => operation?.ToLowerInvariant() switch
        {
            "move" => "move",
            "copy" => "copy",
            _ => "rename",
        };

        private bool _isExecuting;
        private string _operation = "rename";
        private string _sourcePath = string.Empty;
        private string _targetInput = string.Empty;
        private string _errorMessage = string.Empty;
        private string _errorDetails = string.Empty;
        private bool _targetEdited;
        private bool _hasInteracted;
        private readonly Dictionary<string, string> _targetDrafts = new(StringComparer.Ordinal);
    }
}
