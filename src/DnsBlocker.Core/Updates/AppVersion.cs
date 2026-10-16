using System.Reflection;

namespace DnsBlocker.Core.Updates;

/// <summary>
/// Application SemVer from <see cref="AssemblyInformationalVersionAttribute"/>
/// (falls back to assembly version). Strip build metadata (+...) for comparisons.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return StripMetadata(informational);

        var version = assembly.GetName().Version;
        if (version is not null)
            return $"{version.Major}.{version.Minor}.{version.Build}";

        return "0.0.0";
    }

    /// <summary>Removes SemVer build metadata (+commit) and keeps prerelease tags.</summary>
    public static string StripMetadata(string version)
    {
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version.Trim();
    }

    /// <summary>
    /// Compares two SemVer-like strings (major.minor.patch[-prerelease]).
    /// Returns &gt;0 if <paramref name="left"/> is newer than <paramref name="right"/>.
    /// </summary>
    public static int CompareSemVer(string left, string right)
    {
        var a = Parse(StripMetadata(left));
        var b = Parse(StripMetadata(right));

        var core = a.Core.CompareTo(b.Core);
        if (core != 0)
            return core;

        // No prerelease > any prerelease (1.0.0 > 1.0.0-beta)
        if (a.Prerelease is null && b.Prerelease is null)
            return 0;
        if (a.Prerelease is null)
            return 1;
        if (b.Prerelease is null)
            return -1;

        return string.Compare(a.Prerelease, b.Prerelease, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNewerThan(string candidate, string current) =>
        CompareSemVer(candidate, current) > 0;

    private static (Version Core, string? Prerelease) Parse(string version)
    {
        version = version.Trim();
        if (version.Length == 0)
            return (new Version(0, 0, 0), null);

        string? prerelease = null;
        var dash = version.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = version[(dash + 1)..];
            version = version[..dash];
        }

        if (!Version.TryParse(version, out var parsed))
        {
            // Accept "1.2" → 1.2.0
            var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var major = parts.Length > 0 && int.TryParse(parts[0], out var maj) ? maj : 0;
            var minor = parts.Length > 1 && int.TryParse(parts[1], out var min) ? min : 0;
            var patch = parts.Length > 2 && int.TryParse(parts[2], out var pat) ? pat : 0;
            parsed = new Version(major, minor, patch);
        }
        else if (parsed.Build < 0)
        {
            parsed = new Version(parsed.Major, parsed.Minor, 0);
        }

        return (parsed, prerelease);
    }
}
