using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PKGMerge.Core;

public enum PackagePartKind
{
    Root,
    Piece,
    Sc,
}

public sealed record PackagePart(int Index, string Path, long Size, PackagePartKind Kind = PackagePartKind.Piece)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public sealed record SplitPackage(
    string TitleId,
    PkgPlatform Platform,
    PackagePart Root,
    IReadOnlyList<PackagePart> Pieces,
    PackagePart? Sc = null,
    string? TitleName = null)
{
    public IReadOnlyList<PackagePart> Parts => Sc is null ? [Root, .. Pieces] : [Root, .. Pieces, Sc];

    public long TotalSize => Parts.Sum(p => p.Size);

    public string SourceDirectory => Path.GetDirectoryName(Root.Path)!;

    public bool IsSingleFile => !PkgFile.TryParsePartName(Root.Path, out _, out _);

    public PackagePart? MetadataPart => Platform == PkgPlatform.Ps4 ? Root : Sc;

    public string OutputFileName =>
        PkgFile.GetMergedFileName(Sc is not null && PkgFile.GetScId(Sc.Path) is { } scId ? scId : TitleId);

    public string DefaultOutputPath => Path.Combine(SourceDirectory, OutputFileName);
}

public enum ScanWarningKind
{
    NotPkg,
    InvalidPartNumber,
    BadMagic,
    OrphanPiece,
    MissingPiece,
    DuplicatePiece,
    EmptyPiece,
    NoPieces,
    ScMissing,
    ScAmbiguous,
    ScAutoSelected,
    AlreadyMerged,
    IncompleteFile,
}

public sealed record ScanWarning(ScanWarningKind Kind, string Message, string? TitleId = null)
{
    public bool IsBlocking => Kind is not (ScanWarningKind.NotPkg
        or ScanWarningKind.InvalidPartNumber
        or ScanWarningKind.NoPieces
        or ScanWarningKind.ScAutoSelected
        or ScanWarningKind.AlreadyMerged);
}

public sealed record ScanResult(IReadOnlyList<SplitPackage> Packages, IReadOnlyList<ScanWarning> Warnings)
{
    public bool HasBlockingIssues => Warnings.Any(w => w.IsBlocking);

    public IEnumerable<ScanWarning> WarningsFor(SplitPackage package) =>
        Warnings.Where(w => w.TitleId == package.TitleId);

    public bool CanMerge(SplitPackage package) => !WarningsFor(package).Any(w => w.IsBlocking);
}

public sealed record MergeProgress(
    int PartPosition,
    int PartCount,
    long TotalCopied,
    long TotalSize)
{
    public double TotalPercent => TotalSize == 0 ? 100 : TotalCopied * 100.0 / TotalSize;
}

public static class PkgPlatformExtensions
{
    public static string ToDisplayName(this PkgPlatform platform) => platform == PkgPlatform.Ps5 ? "PS5" : "PS4";
}
