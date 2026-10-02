using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cxpm.Core.Models;

internal sealed class CxProject
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public string? Licence { get; set; }
    public string? Website { get; set; }
    public string? Author { get; set; }
    public List<string> Feeds { get; set; } = [];
    public List<string> Targets { get; set; } = [];
    public CxProjectPackageSection? Package { get; set; }
    public List<string> Dependencies { get; set; } = [];

    public static CxProject Load(string path)
    {
        return Parse(File.ReadAllText(path), path);
    }

    public static CxProject Parse(string yaml, string sourceName = "project")
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var project = deserializer.Deserialize<CxProject>(yaml)
            ?? throw new CxpmException($"Could not read project file '{sourceName}'.");

        if (string.IsNullOrWhiteSpace(project.Name))
            throw new CxpmException($"Project file '{sourceName}' must specify a root-level 'name'.");
        if (string.IsNullOrWhiteSpace(project.Version) || !SemVersion.TryParse(project.Version, out _))
            throw new CxpmException($"Project file '{sourceName}' must specify a valid root-level SemVer 'version'.");

        project.Dependencies ??= [];
        project.Feeds ??= [];
        project.Targets ??= [];
        foreach (var target in project.Targets)
        {
            if (string.IsNullOrWhiteSpace(target))
                throw new CxpmException($"Project file '{sourceName}' cannot contain an empty 'targets' entry.");
            PackageManager.ValidateRid(target);
        }
        if (project.Package is not null)
        {
            project.Package.Sources ??= [];
            project.Package.Binary ??= [];
            project.Package.Assets ??= [];
            if (project.Package.Assets.Count > 0)
                throw new CxpmException("The 'package.assets' selection is deferred to a future release and is not supported yet.");
        }
        foreach (var dependency in project.Dependencies)
            PackageDependency.Parse(dependency);

        return project;
    }
}
