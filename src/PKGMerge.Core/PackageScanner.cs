using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PKGMerge.Core;

public static class PackageScanner
{
    private sealed record Candidate(string TitleId, int Index, string Path, long Size);

    public static ScanResult Scan(string path, string? scPath = null)
    {
        if (File.Exists(path))
            return ScanRoot(path, scPath);

        if (Directory.Exists(path))
        {
            if (scPath is not null)
                throw new ArgumentException("An _sc.pkg file can only be specified together with a root _0.pkg file, not a directory.");

            return ScanDirectory(path);
        }

        throw new FileNotFoundException($"'{path}' is neither a file nor a directory.", path);
    }

    public static ScanResult ScanRoot(string rootPkgPath, string? scPath = null)
    {
        string rootPath = Path.GetFullPath(rootPkgPath);
        string rootName = Path.GetFileName(rootPath);
        string directory = Path.GetDirectoryName(rootPath)!;

        if (!File.Exists(rootPath))
            throw new FileNotFoundException($"File '{rootPath}' doesn't exist.", rootPath);

        List<ScanWarning> warnings = [];
        SplitPackage? single = null;
        PkgMagic magic = PkgFile.ReadMagic(rootPath);

        if (!PkgFile.TryParsePartName(rootPath, out string titleId, out int index) || index != 0)
        {
            if (!PkgFile.IsSingleFileName(rootPath) || magic != PkgMagic.Fih)
                throw new ArgumentException($"'{rootName}' is not a root PKG file (expected <ID>_0.pkg, or a PS5 package that isn't split).");

            single = BuildSingle(new FileInfo(rootPath), warnings)
                     ?? throw new InvalidDataException(warnings[^1].Message);
            titleId = single.TitleId;
        }

        if (PkgFile.GetRootPlatform(magic) is not { } platform)
            throw new InvalidDataException(PkgFile.NotPkgMessage(rootName));

        List<Candidate> siblings = [];
        List<FileInfo> scFiles = [];

        foreach (FileInfo info in EnumerateFiles(directory))
        {
            string file = info.FullName;
            if (string.Equals(file, rootPath, PkgFile.PathComparison)
                || !PkgFile.HasPkgExtension(file)
                || PkgFile.IsMergedOutput(file))
                continue;

            if (PkgFile.IsScFile(file))
            {
                scFiles.Add(info);
                continue;
            }

            if (single is not null || !HasTitleId(file, titleId))
                continue;

            if (TryClassify(info, warnings) is { } candidate)
                siblings.Add(candidate);
        }

        Candidate root = new(titleId, 0, rootPath, new FileInfo(rootPath).Length);
        SplitPackage package = single ?? BuildPackage(titleId, platform, root, [root, .. siblings], warnings);

        if (platform == PkgPlatform.Ps5)
        {
            package = WithSc(package, scPath is not null
                ? ResolveExplicitSc(titleId, scPath, warnings)
                : ResolveSc(titleId, scFiles, allowOtherNames: true, warnings));
        }

        return new ScanResult([package], warnings);
    }

    public static ScanResult ScanDirectory(string directory)
    {
        string fullDirectory = Path.GetFullPath(directory);

        if (!Directory.Exists(fullDirectory))
            throw new DirectoryNotFoundException($"Directory '{fullDirectory}' doesn't exist.");

        List<ScanWarning> warnings = [];
        List<Candidate> candidates = [];
        List<FileInfo> scFiles = [];
        List<SplitPackage> packages = [];

        foreach (FileInfo info in EnumerateFiles(fullDirectory))
        {
            string file = info.FullName;
            if (PkgFile.IsMergedOutput(file))
                continue;

            if (!PkgFile.HasPkgExtension(file))
            {
                warnings.Add(new ScanWarning(ScanWarningKind.NotPkg, $"'{info.Name}' is not a PKG file, skipping."));
                continue;
            }

            if (PkgFile.IsScFile(file))
            {
                scFiles.Add(info);
                continue;
            }

            if (PkgFile.IsSingleFileName(file) && PkgFile.ReadMagic(file) == PkgMagic.Fih)
            {
                if (BuildSingle(info, warnings) is { } single)
                    packages.Add(single);

                continue;
            }

            if (TryClassify(info, warnings) is { } candidate)
                candidates.Add(candidate);
        }

        foreach (IGrouping<string, Candidate> group in candidates
                     .GroupBy(c => c.TitleId, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            string titleId = group.First().TitleId;
            Candidate? root = group.Where(c => c.Index == 0).OrderBy(c => c.Path, StringComparer.Ordinal).FirstOrDefault();

            if (root is null)
            {
                warnings.Add(new ScanWarning(ScanWarningKind.OrphanPiece,
                    $"Found {group.Count()} piece(s) of {titleId}, but no root file {titleId}_0.pkg.", titleId));
                continue;
            }

            if (PkgFile.GetRootPlatform(PkgFile.ReadMagic(root.Path)) is not { } platform)
            {
                warnings.Add(new ScanWarning(ScanWarningKind.BadMagic,
                    $"Root file '{Path.GetFileName(root.Path)}' doesn't start with a PS4 or PS5 PKG magic.", titleId));
                continue;
            }

            packages.Add(BuildPackage(titleId, platform, root, group.ToList(), warnings));
        }

        packages.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.TitleId, b.TitleId));

        // With a single PS5 package - the folder's only _sc.pkg belongs to it (even if its name differs).
        // With several - each one needs an _sc.pkg named after it.
        int ps5Count = packages.Count(p => p.Platform == PkgPlatform.Ps5);
        for (int i = 0; i < packages.Count; i++)
        {
            if (packages[i].Platform != PkgPlatform.Ps5)
                continue;

            PackagePart? sc = ResolveSc(packages[i].TitleId, scFiles, allowOtherNames: ps5Count == 1, warnings);
            packages[i] = WithSc(packages[i], sc);
        }

        return new ScanResult(packages, warnings);
    }

    private static IEnumerable<FileInfo> EnumerateFiles(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles().OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase);

    private static bool HasTitleId(string file, string titleId) =>
        PkgFile.TryGetTitleId(file, out string id) && string.Equals(id, titleId, StringComparison.OrdinalIgnoreCase);

    private static Candidate? TryClassify(FileInfo file, List<ScanWarning> warnings)
    {
        if (PkgFile.TryParsePartName(file.FullName, out string titleId, out int index))
            return new Candidate(titleId, index, file.FullName, file.Length);

        PkgFile.TryGetTitleId(file.FullName, out titleId);
        warnings.Add(new ScanWarning(ScanWarningKind.InvalidPartNumber,
            $"'{file.Name}' doesn't end with a valid part number (<ID>_<number>.pkg), skipping.",
            titleId.Length > 0 ? titleId : null));
        return null;
    }

    // A PS5 package that isn't split: .pkg with a FIH magic.
    // Its' header gives the size it must have without the `_sc.pkg` file, which tells
    // a whole download from an incomplete or an already merged one.
    private static SplitPackage? BuildSingle(FileInfo file, List<ScanWarning> warnings)
    {
        string name = file.Name;
        string titleId = Path.GetFileNameWithoutExtension(name);
        long? declared = PkgFile.ReadFihPackageSize(file.FullName);

        if (declared < file.Length)
        {
            warnings.Add(new ScanWarning(ScanWarningKind.AlreadyMerged,
                $"'{name}' is larger than its header says (expected {declared} bytes, found {file.Length}), it already holds its _sc.pkg file."));
            return null;
        }

        if (declared > file.Length)
        {
            warnings.Add(new ScanWarning(ScanWarningKind.IncompleteFile,
                $"'{name}' is smaller than its header says (expected {declared} bytes, found {file.Length}), the download is probably incomplete.",
                titleId));
        }

        PackagePart root = new(0, file.FullName, file.Length, PackagePartKind.Root);
        return new SplitPackage(titleId, PkgPlatform.Ps5, root, []);
    }

    // PS5 PKG keeps its metadata in the `_sc.pkg` file
    private static SplitPackage WithSc(SplitPackage package, PackagePart? sc) =>
        package with { Sc = sc, TitleName = sc is null ? null : PkgMetadata.ReadTitleName(sc.Path) };

    private static PackagePart? ResolveExplicitSc(string titleId, string scPath, List<ScanWarning> warnings)
    {
        string path = Path.GetFullPath(scPath);

        if (!File.Exists(path))
        {
            warnings.Add(new ScanWarning(ScanWarningKind.ScMissing, $"The _sc.pkg file '{path}' doesn't exist.", titleId));
            return null;
        }

        return CheckSc(titleId, new FileInfo(path), warnings);
    }

    private static PackagePart? ResolveSc(string titleId, IReadOnlyList<FileInfo> scFiles, bool allowOtherNames, List<ScanWarning> warnings)
    {
        List<FileInfo> named = scFiles.Where(f => HasTitleId(f.FullName, titleId)).ToList();
        if (named.Count == 1)
            return CheckSc(titleId, named[0], warnings);

        IReadOnlyList<FileInfo> candidates = allowOtherNames ? scFiles : named;
        string where = allowOtherNames ? "in the folder" : $"named {titleId}_sc.pkg";

        switch (candidates.Count)
        {
            case 0:
                warnings.Add(new ScanWarning(ScanWarningKind.ScMissing,
                    $"{titleId} is a PS5 package and needs its _sc.pkg file, but there is none {where}.",
                    titleId));
                return null;

            case > 1:
                string names = string.Join(", ", candidates.Select(f => $"'{f.Name}'"));
                warnings.Add(new ScanWarning(ScanWarningKind.ScAmbiguous,
                    $"Found {candidates.Count} _sc.pkg files {where} ({names}), it's not clear which one belongs to {titleId}.",
                    titleId));
                return null;
        }

        PackagePart? sc = CheckSc(titleId, candidates[0], warnings);

        if (sc is not null)
        {
            warnings.Add(new ScanWarning(ScanWarningKind.ScAutoSelected,
                $"Using '{sc.FileName}', the only _sc.pkg file {where}. Make sure it belongs to {titleId}.", titleId));
        }

        return sc;
    }

    private static PackagePart? CheckSc(string titleId, FileInfo file, List<ScanWarning> warnings)
    {
        if (PkgFile.ReadMagic(file.FullName) != PkgMagic.Cnt)
        {
            warnings.Add(new ScanWarning(ScanWarningKind.BadMagic, PkgFile.NotScMessage(file.Name), titleId));
            return null;
        }

        return new PackagePart(-1, file.FullName, file.Length, PackagePartKind.Sc);
    }

    private static SplitPackage BuildPackage(
        string titleId, PkgPlatform platform, Candidate root, IReadOnlyList<Candidate> group, List<ScanWarning> warnings)
    {
        List<PackagePart> pieces = [];

        foreach (IGrouping<int, Candidate> byIndex in group.GroupBy(c => c.Index).OrderBy(g => g.Key))
        {
            List<Candidate> files = byIndex.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();

            if (files.Count > 1)
            {
                string names = string.Join(", ", files.Select(f => $"'{Path.GetFileName(f.Path)}'"));
                warnings.Add(new ScanWarning(ScanWarningKind.DuplicatePiece,
                    $"Part {byIndex.Key} of {titleId} is ambiguous: {names}.", titleId));
            }

            if (byIndex.Key == 0)
                continue;

            Candidate piece = files[0];
            long size = piece.Size;

            if (size == 0)
            {
                warnings.Add(new ScanWarning(ScanWarningKind.EmptyPiece,
                    $"Piece '{Path.GetFileName(piece.Path)}' is empty (0 bytes), the download is probably incomplete.", titleId));
            }

            pieces.Add(new PackagePart(byIndex.Key, piece.Path, size));
        }

        int expected = 1;
        foreach (PackagePart piece in pieces)
        {
            if (piece.Index > expected)
            {
                string missing = piece.Index - expected == 1
                    ? $"{titleId}_{expected}.pkg is"
                    : $"{titleId}_{expected}.pkg to {titleId}_{piece.Index - 1}.pkg are";
                warnings.Add(new ScanWarning(ScanWarningKind.MissingPiece,
                    $"Piece {missing} missing, the merged PKG would be corrupt.", titleId));
            }

            expected = piece.Index + 1;
        }

        if (pieces.Count == 0)
        {
            warnings.Add(new ScanWarning(ScanWarningKind.NoPieces,
                $"No pieces found next to '{Path.GetFileName(root.Path)}', the output would be a plain copy of it.", titleId));
        }

        PackagePart rootPart = new(0, root.Path, root.Size, PackagePartKind.Root);
        string? titleName = platform == PkgPlatform.Ps4 ? PkgMetadata.ReadTitleName(root.Path) : null;
        return new SplitPackage(titleId, platform, rootPart, pieces, TitleName: titleName);
    }
}
