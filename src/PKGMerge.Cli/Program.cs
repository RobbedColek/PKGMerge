using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PKGMerge.Core;

namespace PKGMerge.Cli;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitBadInput = 1;
    private const int ExitMergeFailed = 2;

    private const int MaxBufferSizeMiB = PackageMerger.MaxBufferSize / (1024 * 1024);

    private const string Usage =
        """
        Usage: PKGMergeCli <root_0.pkg | directory> [-o|--output <path>] [--sc <file_sc.pkg>] [-y|--overwrite]
                           [--in-place] [--buffer-size <MiB>]
               PKGMergeCli -V|--version

          <root_0.pkg>      Merge this root file with the <ID>_1.pkg .. <ID>_N.pkg pieces next to it
                            (and for PS5, the _sc.pkg file after the last piece). A PS5 package that
                            isn't split (<ID>.pkg) is merged with its _sc.pkg file alone.
          <directory>       Merge every split package found in the directory.
          -o, --output      Output file or directory (root file input) or output directory (directory input).
                            A directory gets the default file name in it.
                            Default: <ID>-merged.pkg next to the root file, where a PS5 package takes
                            the <ID> of its <ID>_sc.pkg file.
          --sc              PS5 only, with a root file input: the _sc.pkg file to append.
                            Default: the only _sc.pkg file in the root file's folder.
          -y, --overwrite   Replace existing output files without asking.
          --in-place        Turn the root file into the merged file and delete each piece once it's merged,
                            so only about one piece of free space is needed. The output must be in the
                            source folder. If interrupted, the root file keeps the pieces merged so far and
                            the remaining pieces are renumbered; run the same command again to finish.
          --buffer-size     Read/write buffer size in MiB, 1-256. Default: 64.
          -V, --version     Print the version and exit.
        """;

    private sealed record Options(string Input, string? Output, string? Sc, bool Overwrite, bool InPlace, int BufferSize);

    public static async Task<int> Main(string[] args)
    {
        if (ParseArguments(args, out string? error, out bool showVersion) is not { } options)
        {
            if (error is not null)
            {
                Console.Error.WriteLine($"[error] {error}");
                Console.Error.WriteLine();
                Console.Error.WriteLine(Usage);
                return ExitBadInput;
            }

            Console.WriteLine($"PKGMergeCli {AppVersion.Display}");
            if (!showVersion)
            {
                Console.WriteLine();
                Console.WriteLine(Usage);
            }

            return ExitSuccess;
        }

        using CancelOnSignal cancel = new();
        try
        {
            return await RunAsync(options, cancel);
        }
        finally
        {
            cancel.Finished();
        }
    }

    private static async Task<int> RunAsync(Options options, CancelOnSignal cancel)
    {
        ScanResult scan;
        try
        {
            scan = PackageScanner.Scan(options.Input, options.Sc);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[error] {ex.Message}");
            return ExitBadInput;
        }

        foreach (SplitPackage package in scan.Packages)
        {
            string name = package.TitleName is { } titleName ? $" ({titleName})" : string.Empty;
            string sc = package.Sc is { } scPart ? $" and '{scPart.FileName}'" : string.Empty;
            Console.WriteLine(package.IsSingleFile
                ? $"[info] found {package.Platform.ToDisplayName()} PKG file '{package.Root.FileName}'{name} that isn't split{sc}"
                : $"[info] found {package.Platform.ToDisplayName()} root PKG file for {package.TitleId}{name} with {Plural.Pieces(package.Pieces.Count)}{sc}");
        }

        bool isRootInput = File.Exists(options.Input);
        foreach (ScanWarning warning in scan.Warnings)
            Console.Error.WriteLine(Describe(warning, scan, isRootInput, options.Sc is not null));

        if (scan.Packages.Count == 0)
        {
            Console.Error.WriteLine("[error] no split PKG packages found");
            return ExitBadInput;
        }

        int failed = 0;

        foreach (SplitPackage package in scan.Packages)
        {
            if (cancel.IsCancellationRequested)
                break;

            if (!scan.CanMerge(package))
            {
                Console.Error.WriteLine($"[error] skipping {package.TitleId}, see the errors above");
                failed++;
                continue;
            }

            string outputPath = OutputPathFor(package, options, isRootInput);
            if (File.Exists(outputPath) && !options.Overwrite && !ConfirmOverwrite(outputPath))
            {
                Console.Error.WriteLine($"[error] skipping {package.TitleId}, '{outputPath}' already exists (use -y to overwrite)");
                failed++;
                continue;
            }

            if (!await MergeAsync(package, outputPath, options, cancel.Token))
                failed++;
        }

        if (cancel.IsCancellationRequested)
        {
            Console.WriteLine("\n[cancelled]");
            return ExitMergeFailed;
        }

        bool otherErrors = scan.Warnings.Any(w => w.IsBlocking && !scan.Packages.Any(p => p.TitleId == w.TitleId));

        if (failed == 0 && !otherErrors)
        {
            Console.WriteLine("\n[success] completed");
            return ExitSuccess;
        }

        Console.Error.WriteLine(failed > 0
            ? $"\n[error] {failed} of {scan.Packages.Count} package(s) failed"
            : "\n[error] completed, but some files were skipped because of the errors above");
        return ExitMergeFailed;
    }

    private static async Task<bool> MergeAsync(SplitPackage package, string outputPath, Options options, CancellationToken cancellationToken)
    {
        string how = options.InPlace ? " in place" : string.Empty;
        string what = package.IsSingleFile ? "the _sc.pkg file" : Plural.Pieces(package.Pieces.Count);
        Console.WriteLine($"[work] beginning to merge {what} for package {package.TitleId}{how} into '{outputPath}'...");

        PackageMerger merger = new() { BufferSize = options.BufferSize };
        ConsoleProgress progress = new();
        try
        {
            if (options.InPlace)
                await merger.MergeInPlaceAsync(package, outputPath, progress, cancellationToken);
            else
                await merger.MergeAsync(package, outputPath, progress, cancellationToken);

            progress.Complete();
            return true;
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine();
            if (options.InPlace)
                Console.WriteLine($"[info] {ex.Message}");

            return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"\n[error] merging {package.TitleId} failed: {ex.Message}");
            return false;
        }
    }

    private static string Describe(ScanWarning warning, ScanResult scan, bool isRootInput, bool scGiven)
    {
        string root = scan.Packages.FirstOrDefault(p => p.TitleId == warning.TitleId)?.Root.FileName ?? $"{warning.TitleId}_0.pkg";
        string hint = warning.Kind is ScanWarningKind.ScMissing or ScanWarningKind.ScAmbiguous && !scGiven
            ? isRootInput
                ? " Use --sc <file_sc.pkg>."
                : $" Merge it on its own: PKGMergeCli {root} --sc <file_sc.pkg>."
            : string.Empty;

        return $"[{(warning.IsBlocking ? "error" : "warn")}] {warning.Message}{hint}";
    }

    private static string OutputPathFor(SplitPackage package, Options options, bool isRootInput)
    {
        if (options.Output is null)
            return package.DefaultOutputPath;

        return isRootInput
            ? PackageMerger.ResolveOutputPath(package, options.Output)
            : Path.Combine(Path.GetFullPath(options.Output), package.OutputFileName);
    }

    private static bool ConfirmOverwrite(string path)
    {
        if (Console.IsInputRedirected)
            return false;

        Console.Error.Write($"[warn] '{path}' already exists. Overwrite? [y/N] ");
        string? answer = Console.ReadLine();
        return answer?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static Options? ParseArguments(string[] args, out string? error, out bool showVersion)
    {
        error = null;
        showVersion = false;
        string? input = null;
        string? output = null;
        string? sc = null;
        bool overwrite = false;
        bool inPlace = false;
        int bufferSize = PackageMerger.DefaultBufferSize;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg is "-o" or "--output" or "--sc" or "--buffer-size" && i + 1 == args.Length)
            {
                error = $"'{arg}' needs a value.";
                return null;
            }

            switch (arg)
            {
                case "-o" or "--output":
                    output = args[++i];
                    break;
                case "--sc":
                    sc = args[++i];
                    break;
                case "--buffer-size":
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out int mib)
                        || mib is < 1 or > MaxBufferSizeMiB)
                    {
                        error = $"--buffer-size takes a whole number of MiB from 1 to {MaxBufferSizeMiB}, not '{args[i]}'.";
                        return null;
                    }

                    bufferSize = mib * 1024 * 1024;
                    break;
                case "-y" or "--overwrite":
                    overwrite = true;
                    break;
                case "--in-place":
                    inPlace = true;
                    break;
                case "-h" or "--help" or "/?":
                    return null;
                case "-V" or "--version":
                    showVersion = true;
                    return null;
                default:
                    if (arg.StartsWith('-'))
                    {
                        error = $"Unknown option '{arg}'.";
                        return null;
                    }

                    if (input is not null)
                    {
                        error = $"Only one root file or directory can be merged at a time, got '{input}' and '{arg}'.";
                        return null;
                    }

                    input = arg;
                    break;
            }
        }

        if (input is null)
        {
            error = "No root file or directory given.";
            return null;
        }

        return new Options(input, output, sc, overwrite, inPlace, bufferSize);
    }

    private sealed class CancelOnSignal : IDisposable
    {
        private static readonly TimeSpan WindowsCleanupTimeout = TimeSpan.FromSeconds(4.5);

        private readonly CancellationTokenSource _cts = new();
        private readonly ManualResetEventSlim _finished = new();

        private readonly PosixSignalRegistration[] _registrations;
        private int _signalled;

        public CancelOnSignal()
        {
            _registrations =
            [
                PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal),
                PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnSignal),
                PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal),
                PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnSignal),
            ];
        }

        public CancellationToken Token => _cts.Token;

        public bool IsCancellationRequested => _cts.IsCancellationRequested;

        public void Finished() => _finished.Set();

        public void Dispose()
        {
            foreach (PosixSignalRegistration registration in _registrations)
                registration.Dispose();
        }

        private void OnSignal(PosixSignalContext context)
        {
            if (Interlocked.Exchange(ref _signalled, 1) == 0)
            {
                Console.WriteLine("\n[info] cancelling...");
                _cts.Cancel();
            }
            else
            {
                Console.WriteLine("\n[info] still cancelling, please wait");
            }

            if (OperatingSystem.IsWindows() && context.Signal is PosixSignal.SIGTERM or PosixSignal.SIGHUP)
                _finished.Wait(WindowsCleanupTimeout);
            else
                context.Cancel = true;
        }
    }

    private sealed class ConsoleProgress : IProgress<MergeProgress>
    {
        private static readonly TimeSpan RedrawInterval = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly TransferRateMeter _meter = new();
        private TimeSpan? _lastDraw;
        private TimeSpan? _lastSample;
        private MergeProgress? _last;

        public void Report(MergeProgress value)
        {
            _last = value;
            TimeSpan now = _stopwatch.Elapsed;

            if (_lastSample is not { } lastSample || now - lastSample >= SampleInterval)
            {
                _meter.AddSample(now, value.TotalCopied);
                _lastSample = now;
            }

            if (now - _lastDraw < RedrawInterval)
                return;

            _lastDraw = now;
            Draw(value);
        }

        public void Complete()
        {
            if (_last is null)
                return;

            Draw(_last);
            double average = _stopwatch.Elapsed.TotalSeconds > 0 ? _last.TotalSize / _stopwatch.Elapsed.TotalSeconds : 0;
            Console.WriteLine($"\n[success] done in {SizeFormat.Duration(_stopwatch.Elapsed)}, average {SizeFormat.Speed(average)}");
        }

        private void Draw(MergeProgress p)
        {
            TimeSpan? eta = _meter.EstimateRemaining(p.TotalSize - p.TotalCopied);
            string line = $"    [work] {SizeFormat.Progress(p.TotalCopied, p.TotalSize)} ({p.TotalPercent:0}%)"
                          + $" part {p.PartPosition}/{p.PartCount}"
                          + $" {SizeFormat.Speed(_meter.BytesPerSecond)}"
                          + $" ETA {(eta is { } e ? SizeFormat.Duration(e) : "--:--")}";
            Console.Write("\r" + line.PadRight(Math.Max(line.Length, SafeWindowWidth() - 1)));
        }

        private static int SafeWindowWidth()
        {
            try
            {
                return Console.IsOutputRedirected ? 0 : Console.WindowWidth;
            }
            catch (IOException)
            {
                return 0;
            }
        }
    }
}
