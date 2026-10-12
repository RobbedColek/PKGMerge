using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace PKGMerge.Core;

public static class PkgMetadata
{
    private const uint ParamSfoId = 0x1000;
    private const uint ParamJsonId = 0x2000;
    private const uint Icon0PngId = 0x1200;
    private const uint EncryptedFlag = 0x80000000;

    private const int HeaderSize = 0x20;
    private const int EntrySize = 32;
    private const int MaxEntries = 4096;
    private const int MaxMetadataSize = 1024 * 1024;
    private const int MaxIconSize = 4 * 1024 * 1024;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static string? ReadTitleName(string path)
    {
        try
        {
            using SafeFileHandle file = File.OpenHandle(path);

            if (ReadEntry(file, ParamSfoId, MaxMetadataSize) is { } paramSfo)
                return Clean(ReadSfoString(paramSfo, "TITLE"));

            if (ReadEntry(file, ParamJsonId, MaxMetadataSize) is { } paramJson)
                return Clean(ReadJsonTitleName(paramJson));

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static byte[]? ReadIcon(string path)
    {
        try
        {
            using SafeFileHandle file = File.OpenHandle(path);
            return ReadEntry(file, Icon0PngId, MaxIconSize) is { } png && png.AsSpan().StartsWith(PngSignature) ? png : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[]? ReadEntry(SafeFileHandle file, uint id, int maxSize)
    {
        long length = RandomAccess.GetLength(file);

        Span<byte> header = stackalloc byte[HeaderSize];
        if (!PkgFile.ReadExactly(file, header, 0) || PkgFile.GetMagic(header) != PkgMagic.Cnt)
            return null;

        uint count = BinaryPrimitives.ReadUInt32BigEndian(header[0x10..]);
        uint tableOffset = BinaryPrimitives.ReadUInt32BigEndian(header[0x18..]);
        if (count > MaxEntries || tableOffset + (long)count * EntrySize > length)
            return null;

        byte[] table = new byte[count * EntrySize];
        if (!PkgFile.ReadExactly(file, table, tableOffset))
            return null;

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = table.AsSpan(i * EntrySize, EntrySize);
            if (BinaryPrimitives.ReadUInt32BigEndian(entry) != id)
                continue;

            uint flags = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry[16..]);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(entry[20..]);
            if ((flags & EncryptedFlag) != 0 || size > maxSize || offset + (long)size > length)
                return null;

            byte[] data = new byte[size];
            return PkgFile.ReadExactly(file, data, offset) ? data : null;
        }

        return null;
    }

    private static string? ReadSfoString(ReadOnlySpan<byte> sfo, string key)
    {
        if (sfo.Length < 0x14 || !sfo[..4].SequenceEqual("\0PSF"u8))
            return null;

        int keyTable = BinaryPrimitives.ReadInt32LittleEndian(sfo[0x08..]);
        int dataTable = BinaryPrimitives.ReadInt32LittleEndian(sfo[0x0C..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(sfo[0x10..]);
        if (keyTable < 0 || keyTable > sfo.Length || dataTable < 0 || dataTable > sfo.Length || count < 0
            || 0x14 + (long)count * 16 > sfo.Length)
            return null;

        byte[] keyBytes = Encoding.ASCII.GetBytes(key);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> index = sfo.Slice(0x14 + i * 16, 16);
            int keyStart = keyTable + BinaryPrimitives.ReadUInt16LittleEndian(index);
            if (keyStart >= sfo.Length || !NullTerminated(sfo[keyStart..]).SequenceEqual(keyBytes))
                continue;

            ushort format = BinaryPrimitives.ReadUInt16LittleEndian(index[2..]);
            int valueLength = BinaryPrimitives.ReadInt32LittleEndian(index[4..]);
            long valueStart = dataTable + (long)BinaryPrimitives.ReadUInt32LittleEndian(index[12..]);

            if (format is not (0x0204 or 0x0004) || valueLength < 0 || valueStart + valueLength > sfo.Length)
                return null;

            return Encoding.UTF8.GetString(NullTerminated(sfo.Slice((int)valueStart, valueLength)));
        }

        return null;
    }

    private static string? ReadJsonTitleName(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("localizedParameters", out JsonElement localized)
            || localized.ValueKind != JsonValueKind.Object
            || !localized.TryGetProperty("defaultLanguage", out JsonElement language)
            || language.ValueKind != JsonValueKind.String
            || !localized.TryGetProperty(language.GetString()!, out JsonElement parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("titleName", out JsonElement titleName)
            || titleName.ValueKind != JsonValueKind.String)
            return null;

        return titleName.GetString();
    }

    private static ReadOnlySpan<byte> NullTerminated(ReadOnlySpan<byte> bytes) =>
        bytes.IndexOf((byte)0) is var end and >= 0 ? bytes[..end] : bytes;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
