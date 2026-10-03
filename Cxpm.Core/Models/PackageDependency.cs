using System.Text.RegularExpressions;

namespace Cxpm.Core.Models;

internal sealed record PackageDependency(
    string Id,
    string RangeText)
{
    private static readonly Regex PackageIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    public static PackageDependency Parse(string value)
    {
        var separator = value.LastIndexOf('@');
        if (separator <= 0 || separator == value.Length - 1)
            throw new CxpmException($"Invalid dependency '{value}'. Use package-id@version-range.");

        var id = value[..separator].Trim();
        var range = value[(separator + 1)..].Trim();
        if (!PackageIdPattern.IsMatch(id))
            throw new CxpmException($"Invalid package ID '{id}'. Use letters, digits, '.', '_' or '-'.");
        _ = PackageVersionRange.Parse(range);
        return new PackageDependency(id, range);
    }

    public PackageVersionRange GetVersionRange() =>
        PackageVersionRange.Parse(RangeText);
}
