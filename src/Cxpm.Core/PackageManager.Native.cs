using Cxpm.Core.Models;

namespace Cxpm.Core;

public sealed partial class PackageManager
{
    private static void ValidateNativeCompatibility(CxProject project, IEnumerable<string> runtimeIdentifiers)
    {
        foreach (var rid in runtimeIdentifiers)
            foreach (var (id, library) in project.NativeLibraries)
            {
                var os = rid.StartsWith("win-", StringComparison.Ordinal) ? "windows"
                    : rid.StartsWith("linux-", StringComparison.Ordinal) ? "linux" : "unsupported";
                var target = os == "windows" ? library.Windows : os == "linux" ? library.Linux : null;
                var architecture = rid[(rid.LastIndexOf('-') + 1)..];
                if (target is null || target.Architectures.Count > 0 && !target.Architectures.Contains(architecture, StringComparer.OrdinalIgnoreCase))
                    throw new CxpmException($"Package '{project.Name}' native library '{id}' does not support target '{rid}'.");
            }
    }
}
