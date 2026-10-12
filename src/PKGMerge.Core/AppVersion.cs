using System;
using System.Reflection;

namespace PKGMerge.Core;

public static class AppVersion
{
    public static string Display { get; } = GetDisplay();

    private static string GetDisplay()
    {
        string? version = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (version is null)
            return "Unknown";

        // Nightly builds are versioned 0.0.0-nightly, the numeric part is only a placeholder
        version = version.Replace("0.0.0-nightly", "nightly", StringComparison.Ordinal);

        int separator = version.IndexOf('+');
        if (separator < 0)
            return version;

        string revision = version[(separator + 1)..];
        return $"{version[..separator]} ({revision[..Math.Min(7, revision.Length)]})";
    }
}
