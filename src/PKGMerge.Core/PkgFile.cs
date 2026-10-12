using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace PKGMerge.Core;

public enum PkgMagic
{
    // Not a PKG header
    None,

    // `7F 43 4E 54` ("\x7FCNT") — PS4 packages and the PS5 `_sc.pkg`
    Cnt,

    // `7F 46 49 48` ("\x7FFIH") — PS5 packages
    Fih,
}

public enum PkgPlatform
{
    Ps4,
    Ps5,
}

public static class PkgFile
{
    public const string Extension = ".pkg";
    public const string MergedSuffix = "-merged";
    public const string ScSuffix = "_sc";

    public static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    // Offset of the FIH header field holding the package's size without the `_sc.pkg` file
    private const int FihPackageSizeOffset = 0x58;

    private static ReadOnlySpan<byte> CntMagic => [0x7F, 0x43, 0x4E, 0x54];
    private static ReadOnlySpan<byte> FihMagic => [0x7F, 0x46, 0x49, 0x48];

    public static bool HasPkgExtension(string path) =>
        string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    public static bool IsMergedOutput(string path) =>
        Path.GetFileName(path).Contains(MergedSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool IsScFile(string path) =>
        HasPkgExtension(path)
        && Path.GetFileNameWithoutExtension(path).EndsWith(ScSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool IsSingleFileName(string path) =>
        HasPkgExtension(path) && !IsScFile(path) && !IsMergedOutput(path) && !TryParsePartName(path, out _, out _);

    public static string GetMergedFileName(string titleId) => titleId + MergedSuffix + Extension;

    public static string? GetScId(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Length > ScSuffix.Length ? name[..^ScSuffix.Length] : null;
    }

    public static PkgMagic ReadMagic(string path)
    {
        Span<byte> header = stackalloc byte[4];
        return ReadHeader(path, header) ? GetMagic(header) : PkgMagic.None;
    }

    public static long? ReadFihPackageSize(string path)
    {
        Span<byte> header = stackalloc byte[FihPackageSizeOffset + sizeof(long)];
        if (!ReadHeader(path, header) || GetMagic(header) != PkgMagic.Fih)
            return null;

        long size = BinaryPrimitives.ReadInt64LittleEndian(header[FihPackageSizeOffset..]);
        return size > 0 ? size : null;
    }

    private static bool ReadHeader(string path, Span<byte> header)
    {
        using SafeFileHandle handle = File.OpenHandle(path);
        return ReadExactly(handle, header, 0);
    }

    // False when the file ends before buffer is full
    internal static bool ReadExactly(SafeFileHandle file, Span<byte> buffer, long offset)
    {
        int read = 0;
        while (read < buffer.Length && RandomAccess.Read(file, buffer[read..], offset + read) is var count and > 0)
            read += count;

        return read == buffer.Length;
    }

    internal static PkgMagic GetMagic(ReadOnlySpan<byte> header) =>
        header.StartsWith(CntMagic) ? PkgMagic.Cnt
        : header.StartsWith(FihMagic) ? PkgMagic.Fih
        : PkgMagic.None;

    internal static string NotPkgMessage(string fileName) =>
        $"'{fileName}' doesn't start with a PS4 or PS5 PKG magic, it's not a valid PKG file.";

    internal static string NotScMessage(string fileName) =>
        $"'{fileName}' is not a valid _sc.pkg file, it doesn't start with the PKG magic (7F 43 4E 54).";

    public static PkgPlatform? GetRootPlatform(PkgMagic magic) => magic switch
    {
        PkgMagic.Cnt => PkgPlatform.Ps4,
        PkgMagic.Fih => PkgPlatform.Ps5,
        _ => null,
    };

    public static bool TryGetTitleId(string path, out string titleId)
    {
        titleId = string.Empty;

        if (!HasPkgExtension(path))
            return false;

        string name = Path.GetFileNameWithoutExtension(path);
        int separator = name.LastIndexOf('_');
        if (separator <= 0)
            return false;

        titleId = name[..separator];
        return true;
    }

    public static bool TryParsePartName(string path, out string titleId, out int index)
    {
        index = -1;

        if (!TryGetTitleId(path, out titleId))
            return false;

        string name = Path.GetFileNameWithoutExtension(path);
        string part = name[(titleId.Length + 1)..];

        return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }
}
