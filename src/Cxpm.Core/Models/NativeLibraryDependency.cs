using YamlDotNet.Serialization;

namespace Cxpm.Core.Models;

internal sealed class NativeLibraryDependency
{
    public NativeLibraryTarget? Windows { get; set; }
    public NativeLibraryTarget? Linux { get; set; }
    public string Visibility { get; set; } = "public";
    public string Provider { get; set; } = "sdk";
}

internal sealed class NativeLibraryTarget
{
    public List<string> Architectures { get; set; } = [];
    public List<string> Toolchains { get; set; } = [];
    public List<string> Headers { get; set; } = [];
    [YamlMember(Alias = "link_libraries", ApplyNamingConventions = false)] public List<string> LinkLibraries { get; set; } = [];
    [YamlMember(Alias = "include_directories", ApplyNamingConventions = false)] public List<string> IncludeDirectories { get; set; } = [];
    public List<string> Definitions { get; set; } = [];
    [YamlMember(Alias = "runtime_artifacts", ApplyNamingConventions = false)] public List<string> RuntimeArtifacts { get; set; } = [];
    [YamlMember(Alias = "runtime_search_paths", ApplyNamingConventions = false)] public List<string> RuntimeSearchPaths { get; set; } = [];
    [YamlMember(Alias = "pkg_config", ApplyNamingConventions = false)] public string? PkgConfig { get; set; }
    [YamlMember(Alias = "cmake_package", ApplyNamingConventions = false)] public string? CmakePackage { get; set; }
    [YamlMember(Alias = "imported_targets", ApplyNamingConventions = false)] public List<string> ImportedTargets { get; set; } = [];
    [YamlMember(Alias = "sdk_root", ApplyNamingConventions = false)] public string? SdkRoot { get; set; }
    public string? Version { get; set; }
    public string Linkage { get; set; } = "shared";
}
