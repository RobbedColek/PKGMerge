using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ColekUI.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PKGMerge.Core;
using PKGMerge.Models;
using PKGMerge.Repositories.Settings;
using PKGMerge.Services.Files;

namespace PKGMerge.ViewModels.Merge;

public sealed partial class MergeViewModel : ViewModelBase
{
    private const string LastFolderKey = "LastFolder";
    private const string Placeholder = "—";
    private const string NoSourceTitle = "No root file selected";

    private const int IconDecodeWidth = 256;

    private readonly IFilePickerService _filePicker;
    private readonly IDialogService _dialogs;
    private readonly ISettingsRepository _settings;

    private readonly TransferRateMeter _meter = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private int _scanVersion;
    private bool _settingOutput;
    private bool _isOutputCustomized;
    private bool _settingSc;
    private bool _isScUserSpecified;
    private string? _lastScannedRoot;

    private string? _outputBeforeInPlace;
    private string? _rootBeforeInPlace;

    private MergeProgress? _latestProgress;

    public MergeViewModel(IFilePickerService filePicker, IDialogService dialogs, ISettingsRepository settings)
    {
        _filePicker = filePicker;
        _dialogs = dialogs;
        _settings = settings;

        _refreshTimer.Tick += (_, _) => RefreshProgress();
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMessages));

        ResetProgress();
    }

    public ObservableCollection<StatusMessage> Messages { get; } = [];

    public bool HasMessages => Messages.Count > 0;

    public string Version => AppVersion.Display;

    [ObservableProperty] public partial string SourcePath { get; set; } = string.Empty;

    [ObservableProperty] public partial string ScPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(BrowseScCommand))]
    public partial bool IsPs5 { get; set; }

    public string ScPlaceholder =>
        IsPs5 ? "Select the _sc.pkg file that belongs to this package"
        : Package is not null ? "PS4 packages don't use an _sc.pkg file"
        : "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial string OutputPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutputEnabled))]
    public partial bool MergeInPlace { get; set; }

    public bool IsOutputEnabled => Package is not null && !MergeInPlace;

    public int[] BufferSizes { get; } = [1, 2, 4, 8, 16, 32, 64, 128, PackageMerger.MaxBufferSize / (1024 * 1024)];

    [ObservableProperty] public partial int BufferSizeMiB { get; set; } = PackageMerger.DefaultBufferSize / (1024 * 1024);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsSourceConsumed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPackage), nameof(ScPlaceholder), nameof(IsOutputEnabled))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(BrowseOutputCommand))]
    public partial SplitPackage? Package { get; set; }

    public bool HasPackage => Package is not null;

    [ObservableProperty] public partial string PackageTitle { get; set; } = NoSourceTitle;

    [ObservableProperty] public partial string PackagePlatform { get; set; } = string.Empty;

    [ObservableProperty] public partial string PackagePieces { get; set; } = string.Empty;

    [ObservableProperty] public partial string PackageSize { get; set; } = string.Empty;

    [ObservableProperty] public partial Bitmap? Icon { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool HasBlockingIssue { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(BrowseSourceCommand), nameof(BrowseScCommand), nameof(BrowseOutputCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial double ProgressPercent { get; set; }

    [ObservableProperty] public partial bool IsCompleted { get; set; }

    [ObservableProperty] public partial string ProcessedText { get; set; } = Placeholder;

    [ObservableProperty] public partial string SpeedLabel { get; set; } = "Speed";

    [ObservableProperty] public partial string SpeedText { get; set; } = Placeholder;

    [ObservableProperty] public partial string EtaLabel { get; set; } = "ETA";

    [ObservableProperty] public partial string EtaText { get; set; } = Placeholder;

    [ObservableProperty] public partial string PartText { get; set; } = string.Empty;

    public void OpenSource(string path)
    {
        if (IsRunning)
            return;

        if (string.Equals(SourcePath, path, StringComparison.Ordinal))
            _ = ScanAsync();
        else
            SourcePath = path;
    }

    partial void OnSourcePathChanged(string value)
    {
        _isScUserSpecified = false;
        SetScPath(string.Empty);
        _ = ScanAsync();
    }

    partial void OnScPathChanged(string value)
    {
        if (_settingSc)
            return;

        _isScUserSpecified = CleanPath(value).Length > 0;
        _ = ScanAsync();
    }

    partial void OnMergeInPlaceChanged(bool value)
    {
        if (value)
        {
            _outputBeforeInPlace = _isOutputCustomized ? OutputPath : null;
            _rootBeforeInPlace = Package?.Root.Path;
            SetOutputPath(Package?.DefaultOutputPath ?? string.Empty);
            return;
        }

        if (_outputBeforeInPlace is { } output && string.Equals(_rootBeforeInPlace, Package?.Root.Path, PkgFile.PathComparison))
            OutputPath = output;

        _outputBeforeInPlace = null;
        _rootBeforeInPlace = null;
    }

    partial void OnOutputPathChanged(string value)
    {
        if (!_settingOutput)
            _isOutputCustomized = !string.IsNullOrWhiteSpace(value);
    }

    private string? StartFolder => Package?.SourceDirectory ?? _settings.Get<string>(LastFolderKey);

    private bool CanBrowse() => !IsRunning;

    private bool CanBrowseSc() => !IsRunning && IsPs5;

    private bool CanBrowseOutput() => !IsRunning && Package is not null;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseSourceAsync()
    {
        string? path = await _filePicker.PickRootPackageAsync(StartFolder);

        if (path is not null)
            OpenSource(path);
    }

    [RelayCommand(CanExecute = nameof(CanBrowseSc))]
    private async Task BrowseScAsync()
    {
        string? path = await _filePicker.PickScFileAsync(StartFolder);

        if (path is null)
            return;

        if (string.Equals(ScPath, path, StringComparison.Ordinal))
        {
            _isScUserSpecified = true;
            _ = ScanAsync();
        }
        else
            ScPath = path;
    }

    [RelayCommand(CanExecute = nameof(CanBrowseOutput))]
    private async Task BrowseOutputAsync()
    {
        string suggested = string.IsNullOrWhiteSpace(OutputPath) ? Package?.DefaultOutputPath ?? string.Empty : OutputPath;
        string? path = await _filePicker.PickOutputFileAsync(suggested);

        if (path is not null)
            OutputPath = path;
    }

    private bool CanStart() =>
        !IsRunning && !IsScanning && !IsSourceConsumed && Package is not null && !HasBlockingIssue
        && !string.IsNullOrWhiteSpace(OutputPath);

    [RelayCommand(CanExecute = nameof(CanStart), IncludeCancelCommand = true)]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Package is not { } package)
            return;

        RemoveMergeMessages();

        string output;
        bool isFolder;
        try
        {
            string given = Path.GetFullPath(CleanPath(OutputPath));
            output = PackageMerger.ResolveOutputPath(package, given);
            isFolder = !string.Equals(output, given, PkgFile.PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AddMergeMessage(StatusSeverity.Error, $"The output path is not valid: {ex.Message}");
            return;
        }

        if (isFolder)
        {
            OutputPath = output;
            AddMergeMessage(StatusSeverity.Info, $"The output path is a folder, merging into '{Path.GetFileName(output)}' in it.");
        }

        bool inPlace = MergeInPlace;
        int pieces = package.Pieces.Count;

        if (inPlace
            && !await _dialogs.ConfirmAsync(
                pieces == 0 ? "Merge in place?" : "Delete the pieces?",
                pieces == 0
                    ? $"'{package.Root.FileName}' becomes '{Path.GetFileName(output)}' with the _sc.pkg file appended. If the merge is interrupted, "
                      + "it gets its name and content back."
                    : $"'{package.Root.FileName}' becomes '{Path.GetFileName(output)}' and the {Plural.Pieces(pieces)} {(pieces == 1 ? "is" : "are")} deleted "
                      + "one by one as they're merged. If the merge is interrupted, the root file keeps what was merged and the source can be merged again.",
                pieces == 0 ? "Merge" : "Merge and delete",
                "Cancel"))
            return;

        if (File.Exists(output)
            && !await _dialogs.ConfirmAsync(
                "Replace file?",
                $"'{Path.GetFileName(output)}' already exists in '{Path.GetDirectoryName(output)}'.\nDo you want to replace it?",
                "Replace",
                "Cancel"))
            return;

        IsRunning = true;
        Volatile.Write(ref _latestProgress, null);
        _meter.Reset();
        _stopwatch.Restart();
        ResetProgress();
        RefreshProgress();
        _refreshTimer.Start();

        try
        {
            PackageMerger merger = new() { BufferSize = BufferSizeMiB * 1024 * 1024 };
            SynchronousProgress progress = new(p => Volatile.Write(ref _latestProgress, p));
            await Task.Run(() => inPlace
                ? merger.MergeInPlaceAsync(package, output, progress, cancellationToken)
                : merger.MergeAsync(package, output, progress, cancellationToken), cancellationToken);

            _refreshTimer.Stop();
            _stopwatch.Stop();
            ShowCompleted();
            AddMergeMessage(StatusSeverity.Success,
                $"Merged {package.Parts.Count} parts into '{output}' in {SizeFormat.Duration(_stopwatch.Elapsed)}"
                + (inPlace ? ", the pieces were deleted." : "."));

            IsSourceConsumed = inPlace;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            if (inPlace)
                await ScanAsync();
            else
                ResetProgress();

            if (ex is OperationCanceledException)
                AddMergeMessage(StatusSeverity.Info,
                    inPlace ? $"Merge cancelled. {ex.Message}" : "Merge cancelled, the incomplete output file was removed.");
            else
                AddMergeMessage(StatusSeverity.Error, $"Merge failed: {ex.Message}");
        }
        finally
        {
            _refreshTimer.Stop();
            _stopwatch.Stop();
            IsRunning = false;
        }
    }

    public Task<bool> ConfirmStopAsync() => _dialogs.ConfirmAsync(
        "Cancel the merge?",
        MergeInPlace
            ? "A merge is running, closing the app cancels it. The pieces merged so far stay in the root file and the source can be merged again."
            : "A merge is running, closing the app cancels it and removes the incomplete output file.",
        "Cancel and close",
        "Keep merging");

    public async Task StopAsync()
    {
        if (StartCommand.ExecutionTask is not { } merge)
            return;

        StartCommand.Cancel();
        await merge;
    }

    private async Task ScanAsync()
    {
        int version = ++_scanVersion;

        Messages.Clear();
        Package = null;
        Icon = null;
        IsSourceConsumed = false;
        HasBlockingIssue = false;
        ResetProgress();

        string path = CleanPath(SourcePath);
        string? scPath = _isScUserSpecified ? CleanPath(ScPath) : null;
        ShowPackage(path.Length == 0 ? NoSourceTitle : "Scanning…", null);

        if (path.Length == 0)
        {
            IsPs5 = false;
            IsScanning = false;
            return;
        }

        IsScanning = true;
        try
        {
            ScanResult result = await Task.Run(() => PackageScanner.ScanRoot(path, scPath));
            if (version != _scanVersion)
                return;

            SplitPackage package = result.Packages[0];
            Package = package;
            IsPs5 = package.Platform == PkgPlatform.Ps5;
            HasBlockingIssue = result.HasBlockingIssues;

            ShowPackage(package.TitleName ?? package.TitleId, package);

            if (!_isScUserSpecified)
                SetScPath(package.Sc?.Path ?? string.Empty);

            foreach (ScanWarning warning in result.Warnings)
                Messages.Add(new StatusMessage(warning.IsBlocking ? StatusSeverity.Error : StatusSeverity.Warning, Describe(warning)));

            if (MergeInPlace || !_isOutputCustomized || !string.Equals(_lastScannedRoot, package.Root.Path, PkgFile.PathComparison))
                SetOutputPath(package.DefaultOutputPath);

            _lastScannedRoot = package.Root.Path;

            _ = LoadIconAsync(package, version);

            _settings.Set(LastFolderKey, package.SourceDirectory);
            ResetProgress();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            if (version != _scanVersion)
                return;

            IsPs5 = false;
            ShowPackage("Not a split package, see the message below", null);
            Messages.Add(new StatusMessage(StatusSeverity.Error, ex.Message));
        }
        finally
        {
            if (version == _scanVersion)
                IsScanning = false;
        }
    }

    private void ShowPackage(string title, SplitPackage? package)
    {
        PackageTitle = title;

        if (package is null)
        {
            PackagePlatform = string.Empty;
            PackagePieces = string.Empty;
            PackageSize = string.Empty;
            return;
        }

        PackagePlatform = $"{package.Platform.ToDisplayName()} package";
        PackagePieces = (package.IsSingleFile ? "Not split" : Plural.Pieces(package.Pieces.Count)) + (package.Sc is not null ? " + _sc.pkg" : string.Empty);
        PackageSize = $"{SizeFormat.Size(package.TotalSize)} total";
    }

    partial void OnIconChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    private async Task LoadIconAsync(SplitPackage package, int version)
    {
        if (package.MetadataPart is not { } part)
            return;

        Bitmap? icon = await Task.Run(() => DecodeIcon(part.Path));
        if (version != _scanVersion)
        {
            icon?.Dispose();
            return;
        }

        Icon = icon;
    }

    private static Bitmap? DecodeIcon(string path)
    {
        if (PkgMetadata.ReadIcon(path) is not { } png)
            return null;

        try
        {
            using MemoryStream stream = new(png);
            return Bitmap.DecodeToWidth(stream, IconDecodeWidth);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void RefreshProgress()
    {
        MergeProgress? progress = Volatile.Read(ref _latestProgress);
        long total = progress?.TotalSize ?? Package?.TotalSize ?? 0;
        long copied = progress?.TotalCopied ?? 0;

        _meter.AddSample(_stopwatch.Elapsed, copied);

        ProgressPercent = total == 0 ? 0 : copied * 100.0 / total;
        ProcessedText = SizeFormat.Progress(copied, total);
        SpeedText = _meter.BytesPerSecond > 0 ? SizeFormat.Speed(_meter.BytesPerSecond) : Placeholder;
        EtaText = _meter.EstimateRemaining(total - copied) is { } eta ? SizeFormat.Duration(eta) : Placeholder;
        PartText = StartCommand.IsCancellationRequested ? "Cancelling…"
            : progress is null ? "Starting…"
            : $"Part {progress.PartPosition} of {progress.PartCount}";
    }

    private void ShowCompleted()
    {
        long copied = Volatile.Read(ref _latestProgress)?.TotalSize ?? 0;
        TimeSpan elapsed = _stopwatch.Elapsed;
        double average = elapsed.TotalSeconds > 0 ? copied / elapsed.TotalSeconds : 0;

        ProgressPercent = 100;
        IsCompleted = true;
        ProcessedText = SizeFormat.Progress(copied, copied);
        SpeedLabel = "Average speed";
        SpeedText = average > 0 ? SizeFormat.Speed(average) : Placeholder;
        EtaLabel = "Elapsed";
        EtaText = SizeFormat.Duration(elapsed);
        PartText = "Completed";
    }

    private void ResetProgress()
    {
        long total = Package?.TotalSize ?? 0;

        ProgressPercent = 0;
        IsCompleted = false;
        ProcessedText = SizeFormat.Progress(0, total);
        SpeedLabel = "Speed";
        SpeedText = Placeholder;
        EtaLabel = "ETA";
        EtaText = Placeholder;
        PartText = Package is { } package ? $"{package.Parts.Count} parts ready" : string.Empty;
    }

    private static string Describe(ScanWarning warning) => warning.Kind switch
    {
        ScanWarningKind.ScMissing or ScanWarningKind.ScAmbiguous => warning.Message + " Select the _sc.pkg file above.",
        _ => warning.Message,
    };

    private void SetScPath(string path)
    {
        _settingSc = true;
        try
        {
            ScPath = path;
        }
        finally
        {
            _settingSc = false;
        }
    }

    private void SetOutputPath(string path)
    {
        _settingOutput = true;
        try
        {
            OutputPath = path;
            _isOutputCustomized = false;
        }
        finally
        {
            _settingOutput = false;
        }
    }

    private void AddMergeMessage(StatusSeverity severity, string text) =>
        Messages.Add(new StatusMessage(severity, text, FromMerge: true));

    private void RemoveMergeMessages()
    {
        foreach (StatusMessage message in Messages.Where(m => m.FromMerge).ToList())
            Messages.Remove(message);
    }

    private static string CleanPath(string? path) => (path ?? string.Empty).Trim().Trim('"').Trim();

    private sealed class SynchronousProgress(Action<MergeProgress> report) : IProgress<MergeProgress>
    {
        public void Report(MergeProgress value) => report(value);
    }
}
