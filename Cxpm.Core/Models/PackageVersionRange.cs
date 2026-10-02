using System.Numerics;

namespace Cxpm.Core.Models;

internal sealed class PackageVersionRange
{
    private SemVersion? _min;
    private SemVersion? _max;
    private bool _includeMin;
    private bool _includeMax;
    private bool _floating;
    private bool _floatingPrerelease;
    private BigInteger? _floatingMajor;
    private BigInteger? _floatingMinor;
    private BigInteger? _floatingPatch;
    private string? _floatingPrereleasePrefix;

    public bool IncludesPrerelease { get; private set; }

    public static PackageVersionRange Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw InvalidRange(text);

        var range = new PackageVersionRange();
        var floatingText = text;
        if (floatingText.EndsWith("-*", StringComparison.Ordinal))
        {
            range._floatingPrerelease = true;
            range.IncludesPrerelease = true;
            floatingText = floatingText[..^2];
        }

        if (floatingText == "*")
        {
            range._floating = true;
            return range;
        }

        if (floatingText.EndsWith(".*", StringComparison.Ordinal)
            && floatingText[..^2].Contains("-", StringComparison.Ordinal))
        {
            var prereleasePattern = floatingText[..^2];
            var separator = prereleasePattern.IndexOf('-');
            var core = prereleasePattern[..separator];
            var prereleasePrefix = prereleasePattern[(separator + 1)..];
            if (!SemVersion.TryParse($"{core}-0", out var baseVersion)
                || !SemVersion.TryParse($"{core}-{prereleasePrefix}.0", out _))
                throw InvalidRange(text);
            range._floating = true;
            range._floatingPrerelease = true;
            range._floatingMajor = baseVersion.Major;
            range._floatingMinor = baseVersion.Minor;
            range._floatingPatch = baseVersion.Patch;
            range._floatingPrereleasePrefix = prereleasePrefix;
            range.IncludesPrerelease = true;
            return range;
        }

        if (floatingText.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = floatingText[..^2].Split('.');
            if (prefix.Length is < 1 or > 2
                || !SemVersion.TryParse($"{prefix[0]}.0.0", out var majorVersion)
                || (prefix.Length == 2 && !SemVersion.TryParse($"0.{prefix[1]}.0", out _)))
                throw InvalidRange(text);
            range._floating = true;
            range._floatingMajor = majorVersion.Major;
            if (prefix.Length == 2)
            {
                if (!SemVersion.TryParse($"0.{prefix[1]}.0", out var minorVersion))
                    throw InvalidRange(text);
                range._floatingMinor = minorVersion.Minor;
            }
            return range;
        }

        if (range._floatingPrerelease)
            throw InvalidRange(text);

        if (text.StartsWith('^') || text.StartsWith('~'))
        {
            if (!SemVersion.TryParse(text[1..], out var baseVersion))
                throw InvalidRange(text);
            range._min = baseVersion;
            range._includeMin = true;
            range.IncludesPrerelease = baseVersion.IsPrerelease;
            if (text[0] == '~')
                range._max = SemVersion.Parse($"{baseVersion.Major}.{baseVersion.Minor + 1}.0");
            else if (baseVersion.Major > 0)
                range._max = SemVersion.Parse($"{baseVersion.Major + 1}.0.0");
            else if (baseVersion.Minor > 0)
                range._max = SemVersion.Parse($"0.{baseVersion.Minor + 1}.0");
            else
                range._max = SemVersion.Parse($"0.0.{baseVersion.Patch + 1}");
            return range;
        }

        if (text[0] is '[' or '(')
        {
            if (text.Length < 3 || text[^1] is not (']' or ')'))
                throw InvalidRange(text);
            var parts = text[1..^1].Split(',');
            if (parts.Length == 1)
            {
                if (text[0] != '[' || text[^1] != ']' || !TryParseRangeVersion(parts[0].Trim(), out var exact))
                    throw InvalidRange(text);
                range._min = range._max = exact;
                range._includeMin = range._includeMax = true;
                range.IncludesPrerelease = exact.IsPrerelease;
                return range;
            }
            if (parts.Length != 2)
                throw InvalidRange(text);
            if (!string.IsNullOrWhiteSpace(parts[0]))
            {
                if (!TryParseRangeVersion(parts[0].Trim(), out var min))
                    throw InvalidRange(text);
                range._min = min;
                range._includeMin = text[0] == '[';
                range.IncludesPrerelease |= min.IsPrerelease;
            }
            if (!string.IsNullOrWhiteSpace(parts[1]))
            {
                if (!TryParseRangeVersion(parts[1].Trim(), out var max))
                    throw InvalidRange(text);
                range._max = max;
                range._includeMax = text[^1] == ']';
                range.IncludesPrerelease |= max.IsPrerelease;
            }
            if (range._min is null && range._max is null)
                throw InvalidRange(text);
            if (range._min is not null && range._max is not null)
            {
                var comparison = range._min.CompareTo(range._max);
                if (comparison > 0 || (comparison == 0 && (!range._includeMin || !range._includeMax)))
                    throw InvalidRange(text);
            }
            return range;
        }

        if (!SemVersion.TryParse(text, out var exactVersion))
            throw InvalidRange(text);
        range._min = range._max = exactVersion;
        range._includeMin = range._includeMax = true;
        range.IncludesPrerelease = exactVersion.IsPrerelease;
        return range;
    }

    public bool Contains(SemVersion version, bool includePrerelease = false)
    {
        if (version.IsPrerelease
            && ((!IncludesPrerelease && !includePrerelease)
                || (_floating && !_floatingPrerelease)))
            return false;
        if (_floatingMajor is not null && version.Major != _floatingMajor)
            return false;
        if (_floatingMinor is not null && version.Minor != _floatingMinor)
            return false;
        if (_floatingPatch is not null && version.Patch != _floatingPatch)
            return false;
        if (_floatingPrereleasePrefix is not null && version.IsPrerelease
            && !version.Prerelease.StartsWith(_floatingPrereleasePrefix + ".", StringComparison.Ordinal))
            return false;
        if (_floating)
            return true;
        if (_min is not null)
        {
            var comparison = version.CompareTo(_min);
            if (comparison < 0 || (comparison == 0 && !_includeMin))
                return false;
        }
        if (_max is not null)
        {
            var comparison = version.CompareTo(_max);
            if (comparison > 0 || (comparison == 0 && !_includeMax))
                return false;
        }
        return true;
    }

    public bool IsFloating => _floating;

    private static bool TryParseRangeVersion(string text, out SemVersion version)
    {
        if (SemVersion.TryParse(text, out version))
            return true;

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 2 || parts.Any(part => part.Length == 0
                || part.Any(character => character is < '0' or > '9')
                || (part.Length > 1 && part[0] == '0')))
        {
            version = null!;
            return false;
        }

        var normalized = parts.Length == 1 ? $"{parts[0]}.0.0" : $"{parts[0]}.{parts[1]}.0";
        return SemVersion.TryParse(normalized, out version);
    }

    private static CxpmException InvalidRange(string text) =>
        new($"Invalid version range '{text}'. Use an exact SemVer, ^range, ~range, bracket range, or wildcard.");
}
