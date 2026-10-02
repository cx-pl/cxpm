using System.Reflection;
using System.Text.Json;

namespace Cxpm.Core.Models;

internal static class RuntimeIdentifierGraph
{
    private const string ResourceName = "Cxpm.Core.PortableRuntimeIdentifierGraph.json";
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> Imports = new(Load);

    public static IReadOnlyList<string> GetFallbacks(string runtimeIdentifier)
    {
        var imports = Imports.Value;
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(runtimeIdentifier);

        while (queue.TryDequeue(out var current))
        {
            if (!visited.Add(current))
                continue;

            result.Add(current);
            if (!imports.TryGetValue(current, out var parents))
                continue;
            foreach (var parent in parents)
                queue.Enqueue(parent);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string[]> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded RID graph resource '{ResourceName}' is missing.");
        using var document = JsonDocument.Parse(stream);
        var runtimes = document.RootElement.GetProperty("runtimes");
        var graph = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var runtime in runtimes.EnumerateObject())
        {
            var parents = runtime.Value.TryGetProperty("#import", out var imports)
                ? imports.EnumerateArray().Select(item => item.GetString()!).ToArray()
                : [];
            graph.Add(runtime.Name, parents);
        }
        return graph;
    }
}
