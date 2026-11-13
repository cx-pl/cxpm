using System.Text.Json;
using System.Text.RegularExpressions;
using Cxpm.Core.Models;
using YamlDotNet.Serialization;

namespace Cxpm.Core;

public sealed partial class PackageManager
{
    public static void AddDependency(string projectPath, string dependencyText)
    {
        projectPath = FindProject(projectPath);
        var dependency = PackageDependency.Parse(dependencyText);
        var yaml = File.ReadAllText(projectPath);
        var project = CxProject.Parse(yaml, projectPath);
        if (project.Dependencies.Any(existing =>
                PackageDependency.Parse(existing).Id.Equals(dependency.Id, StringComparison.OrdinalIgnoreCase)))
            throw new CxpmException($"Project already declares a dependency on '{dependency.Id}'. Edit its range directly in '{projectPath}'.");

        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var section = FindDependencySection(lines);
        if (section < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0)
                lines.Add("");
            lines.Add("dependencies:");
            lines.Add(FormatDependencyLine(dependencyText));
        }
        else if (IsInlineDependencyList(lines[section]))
        {
            var existing = project.Dependencies.Select(FormatDependencyLine).ToArray();
            lines[section] = FormatDependencyHeader(lines[section]);
            lines.InsertRange(section + 1, existing);
            lines.Insert(section + 1 + existing.Length, FormatDependencyLine(dependencyText));
        }
        else
        {
            if (Regex.IsMatch(lines[section], "^dependencies\\s*:\\s*\\[\\]"))
                lines[section] = FormatDependencyHeader(lines[section]);
            var insertAt = section + 1;
            while (insertAt < lines.Count &&
                   (string.IsNullOrWhiteSpace(lines[insertAt]) || lines[insertAt].StartsWith(' ') || lines[insertAt].StartsWith('\t')))
                insertAt++;
            lines.Insert(insertAt, FormatDependencyLine(dependencyText));
        }
        File.WriteAllText(projectPath, string.Join(Environment.NewLine, lines));
    }

    public static void RemoveDependency(string projectPath, string packageId)
    {
        projectPath = FindProject(projectPath);
        ValidatePackageId(packageId);
        var yaml = File.ReadAllText(projectPath);
        var project = CxProject.Parse(yaml, projectPath);
        var remainingDependencies = project.Dependencies
            .Where(existing => !PackageDependency.Parse(existing).Id
                .Equals(packageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (remainingDependencies.Length == project.Dependencies.Count)
            throw new CxpmException($"Project does not declare a dependency on '{packageId}'.");

        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var section = FindDependencySection(lines);
        if (section < 0)
            throw new CxpmException("Cannot locate the root-level 'dependencies' list in the project file.");

        if (IsInlineDependencyList(lines[section]))
        {
            if (remainingDependencies.Length == 0)
                lines[section] = FormatDependencyHeader(lines[section], empty: true);
            else
            {
                lines[section] = FormatDependencyHeader(lines[section]);
                lines.InsertRange(section + 1, remainingDependencies.Select(FormatDependencyLine));
            }
            File.WriteAllText(projectPath, string.Join(Environment.NewLine, lines));
            return;
        }

        var remove = new List<int>();
        for (var index = section + 1; index < lines.Count; index++)
        {
            var line = lines[index];
            if (!string.IsNullOrWhiteSpace(line) && !char.IsWhiteSpace(line[0])
                && !line.StartsWith('#') && !line.StartsWith('-'))
                break;
            var match = Regex.Match(line, "^\\s*-\\s*(.*?)\\s*(?:#.*)?$");
            if (!match.Success || !TryDeserializeYamlString(match.Groups[1].Value, out var value))
                continue;
            if (PackageDependency.Parse(value!).Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                remove.Add(index);
        }

        if (remove.Count == 0)
            throw new CxpmException($"Could not locate dependency '{packageId}' in the root-level block-style dependency list.");
        foreach (var index in remove.OrderDescending())
            lines.RemoveAt(index);
        if (remainingDependencies.Length == 0)
        {
            lines[section] = FormatDependencyHeader(lines[section], empty: true);
        }
        File.WriteAllText(projectPath, string.Join(Environment.NewLine, lines));
    }

    private static bool TryDeserializeYamlString(string text, out string? value)
    {
        try
        {
            value = new DeserializerBuilder().Build().Deserialize<string>(text);
            return value is not null;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            value = null;
            return false;
        }
    }

    private static int FindDependencySection(IReadOnlyList<string> lines) =>
        Enumerable.Range(0, lines.Count).FirstOrDefault(index =>
            Regex.IsMatch(lines[index], "^dependencies\\s*:"), -1);

    private static bool IsInlineDependencyList(string header) =>
        Regex.IsMatch(header, "^dependencies\\s*:\\s*\\[.*\\]\\s*(?:#.*)?$");

    private static string FormatDependencyHeader(string header, bool empty = false)
    {
        var comment = Regex.Match(header, "(?:\\[[^]]*\\]|:)\\s*(?<comment>#.*)$").Groups["comment"];
        return $"dependencies:{(empty ? " []" : "")}{(comment.Success ? $" {comment.Value}" : "")}";
    }

    private static string FormatDependencyLine(string dependency) =>
        $"- {JsonSerializer.Serialize(dependency)}";
}
