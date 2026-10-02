namespace Cxpm.Core.Models;

internal sealed class CxProjectPackageSection
{
    public List<string> Sources { get; set; } = [];
    public List<string> Binary { get; set; } = [];
    // Kept only to give an actionable error for this deferred manifest field.
    public List<string> Assets { get; set; } = [];
}
