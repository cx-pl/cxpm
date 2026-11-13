using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Cxpm.Core.Models;

/// <summary>A SemVer 2.0.0 version. Build metadata is preserved but does not affect precedence.</summary>
internal sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private static readonly Regex CoreNumber = new("^(0|[1-9][0-9]*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Identifier = new("^[0-9A-Za-z-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private SemVersion(BigInteger major, BigInteger minor, BigInteger patch, string prerelease, string metadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Metadata = metadata;
    }

    public BigInteger Major { get; }
    public BigInteger Minor { get; }
    public BigInteger Patch { get; }
    public string Prerelease { get; }
    public string Metadata { get; }
    public bool IsPrerelease => Prerelease.Length != 0;

    public static SemVersion Parse(string value)
    {
        if (!TryParse(value, out var version))
            throw new FormatException($"'{value}' is not a valid SemVer 2.0.0 version.");
        return version;
    }

    public static bool TryParse(string? value, out SemVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        var plusIndex = text.IndexOf('+');
        var metadata = plusIndex >= 0 ? text[(plusIndex + 1)..] : "";
        if (plusIndex >= 0)
            text = text[..plusIndex];
        if (text.Contains('+'))
            return false;

        var dashIndex = text.IndexOf('-');
        var prerelease = dashIndex >= 0 ? text[(dashIndex + 1)..] : "";
        var core = dashIndex >= 0 ? text[..dashIndex] : text;
        var components = core.Split('.');
        if (components.Length != 3 || components.Any(part => !CoreNumber.IsMatch(part)))
            return false;
        if (dashIndex >= 0 && !ValidIdentifiers(prerelease, rejectLeadingZeroNumeric: true))
            return false;
        if (plusIndex >= 0 && !ValidIdentifiers(metadata, rejectLeadingZeroNumeric: false))
            return false;
        if (!BigInteger.TryParse(components[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !BigInteger.TryParse(components[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !BigInteger.TryParse(components[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return false;

        version = new SemVersion(major, minor, patch, prerelease, metadata);
        return true;
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null)
            return 1;
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        if (!IsPrerelease)
            return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease)
            return -1;

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftNumeric = IsNumericIdentifier(left[index]);
            var rightNumeric = IsNumericIdentifier(right[index]);
            if (leftNumeric && rightNumeric)
            {
                result = CompareNumericIdentifiers(left[index], right[index]);
            }
            else if (leftNumeric != rightNumeric)
            {
                result = leftNumeric ? -1 : 1;
            }
            else
            {
                result = string.CompareOrdinal(left[index], right[index]);
            }
            if (result != 0)
                return result;
        }
        return left.Length.CompareTo(right.Length);
    }

    public bool Equals(SemVersion? other) => other is not null
        && Major == other.Major && Minor == other.Minor && Patch == other.Patch
        && Prerelease == other.Prerelease && Metadata == other.Metadata;

    public override bool Equals(object? obj) => obj is SemVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease, Metadata);

    public override string ToString()
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        if (Prerelease.Length != 0)
            text += $"-{Prerelease}";
        if (Metadata.Length != 0)
            text += $"+{Metadata}";
        return text;
    }

    private static bool ValidIdentifiers(string value, bool rejectLeadingZeroNumeric)
    {
        var identifiers = value.Split('.');
        if (identifiers.Length == 0 || identifiers.Any(identifier => !Identifier.IsMatch(identifier)))
            return false;
        return !rejectLeadingZeroNumeric || identifiers.All(identifier =>
            !IsNumericIdentifier(identifier) || identifier == "0" || identifier[0] != '0');
    }

    private static bool IsNumericIdentifier(string value) => value.All(character => character is >= '0' and <= '9');

    private static int CompareNumericIdentifiers(string left, string right)
    {
        var length = left.Length.CompareTo(right.Length);
        return length != 0 ? length : string.CompareOrdinal(left, right);
    }
}
