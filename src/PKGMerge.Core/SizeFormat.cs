using System;
using System.Globalization;

namespace PKGMerge.Core;

public static class SizeFormat
{
    private const double BytesPerMegabyte = 1_000_000;
    private const double BytesPerGigabyte = 1_000_000_000;

    public static string Size(long bytes) =>
        bytes >= BytesPerGigabyte ? Gigabytes(bytes) + " GB" : Megabytes(bytes) + " MB";

    public static string Progress(long copied, long total) =>
        total >= BytesPerGigabyte
            ? $"{Gigabytes(copied)} / {Gigabytes(total)} GB"
            : $"{Megabytes(copied)} / {Megabytes(total)} MB";

    public static string Speed(double bytesPerSecond) =>
        (bytesPerSecond / BytesPerMegabyte).ToString("N1", CultureInfo.CurrentCulture) + " MB/s";

    public static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";

    private static string Megabytes(long bytes) => (bytes / BytesPerMegabyte).ToString("N0", CultureInfo.CurrentCulture);

    private static string Gigabytes(long bytes) => (bytes / BytesPerGigabyte).ToString("N1", CultureInfo.CurrentCulture);
}
