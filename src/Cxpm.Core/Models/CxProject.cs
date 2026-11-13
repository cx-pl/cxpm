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
    [YamlMember(Alias = "native_libraries", ApplyNamingConventions = false)]
    public Dictionary<string, NativeLibraryDependency> NativeLibraries { get; set; } = new(StringComparer.Ordinal);

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
        if (project.Name.Contains('_'))
            throw new CxpmException($"Project name '{project.Name}' in '{sourceName}' must not contain underscores.");
        if (string.IsNullOrWhiteSpace(project.Version) || !SemVersion.TryParse(project.Version, out _))
            throw new CxpmException($"Project file '{sourceName}' must specify a valid root-level SemVer 'version'.");

        project.Dependencies ??= [];
        project.Feeds ??= [];
        project.Targets ??= [];
        project.NativeLibraries ??= [];
        foreach (var (id, library) in project.NativeLibraries)
        {
            if (string.IsNullOrWhiteSpace(id) || library.Windows is null && library.Linux is null ||
                library.Visibility is not ("public" or "private") || library.Provider is not ("os" or "sdk" or "package"))
                throw new CxpmException($"Invalid native library requirement '{id}'.");
            foreach (var target in new[] { library.Windows, library.Linux }.Where(target => target is not null))
                if (target!.Linkage is not ("static" or "shared"))
                    throw new CxpmException($"Native library '{id}' linkage must be static or shared.");
        }
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
