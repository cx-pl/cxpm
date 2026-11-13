using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Cxpm.Core.Models;

namespace Cxpm.Core;

public sealed partial class PackageManager
{
    private const string LockFileName = "cxpm.lock";
    private const string ManifestFileName = "package.cxpm";
    private const string PackagesDirectoryName = ".packages";

    public static IReadOnlyList<string> ResolveFeeds(string projectPath, string? commandLineFeed = null)
    {
        if (!string.IsNullOrWhiteSpace(commandLineFeed))
            return [NormalizeRepositoryLocation(commandLineFeed)];

        var fullProjectPath = FindProject(projectPath);
        var project = CxProject.Load(fullProjectPath);
        if (project.Feeds.Count == 0)
            throw new CxpmException("No package feeds were specified. Pass --feed <path-or-url> or set a root-level 'feeds' list in the .cxproj file.");

        var baseDirectory = Path.GetDirectoryName(fullProjectPath);
        return project.Feeds
            .Select(feed => string.IsNullOrWhiteSpace(feed)
                ? throw new CxpmException("The .cxproj 'feeds' list cannot contain an empty entry.")
                : NormalizeRepositoryLocation(feed, baseDirectory))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static string ResolvePublishFeed(string projectPath, string? commandLineFeed = null)
    {
        var feeds = ResolveFeeds(projectPath, commandLineFeed);
        if (feeds.Count != 1)
            throw new CxpmException("Publishing requires one destination feed. Pass --feed <path-or-url> when the project has multiple feeds.");
        return feeds[0];
    }

    public static string Initialize(string directory, string? requestedName = null)
    {
        directory = Path.GetFullPath(directory);
        var name = string.IsNullOrWhiteSpace(requestedName)
            ? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : requestedName.Trim();
        ValidatePackageId(name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.cxproj");
        if (File.Exists(path))
            throw new CxpmException($"'{path}' already exists.");
        File.WriteAllText(path,
            $"name: {name}\nversion: 0.1.0\ntargets: []\nfeeds: []\ndependencies: []\n");
        return path;
    }

    public static string Publish(string projectPath, string repositoryPath)
    {
        var archivePath = Build(projectPath);
        projectPath = FindProject(projectPath);
        var project = CxProject.Load(projectPath);
        var normalizedId = project.Name.ToLowerInvariant();
        var normalizedVersion = SemVersion.Parse(project.Version).ToString();
        return CreatePackageRepository(repositoryPath).Publish(archivePath, project.Name, normalizedId, normalizedVersion);
    }

    public static RestoreSummary Restore(string projectPath, string repositoryPath, bool update = false,
        string? runtimeIdentifier = null, bool locked = false)
        => Restore(projectPath, [repositoryPath], update, runtimeIdentifier, locked);

    public static RestoreSummary Restore(string projectPath, IReadOnlyList<string> feedPaths, bool update = false,
        string? runtimeIdentifier = null, bool locked = false)
    {
        projectPath = FindProject(projectPath);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var project = CxProject.Load(projectPath);
        ValidatePackageId(project.Name);
        var runtimes = runtimeIdentifier is not null
            ? [runtimeIdentifier]
            : project.Targets.Count > 0
                ? project.Targets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : [RuntimeInformation.RuntimeIdentifier];
        foreach (var runtime in runtimes)
            ValidateRid(runtime);
        if (feedPaths.Count == 0)
            throw new CxpmException("At least one package feed must be configured.");
        var repository = CreatePackageRepository(feedPaths);
        repository.EnsureExists();
        var manifestHash = HashFile(projectPath);
        var lockPath = Path.Combine(projectDirectory, LockFileName);
        var lockFile = update ? null : ReadLock(lockPath);
        var previousLock = update ? TryReadLockForCleanup(lockPath) : lockFile;
        var useLock = !update && lockFile is not null && lockFile.ProjectHash == manifestHash;
        if (locked && update)
            throw new CxpmException("The --locked option cannot be used with 'cxpm update'.");
        if (locked && !useLock)
            throw new CxpmException(lockFile is null
                ? $"Locked restore requires '{lockPath}'. Run 'cxpm restore' to create it."
                : $"Locked restore cannot continue because '{lockPath}' does not match the current .cxproj file. Run 'cxpm update' to recalculate dependencies.");

        Dictionary<string, ResolvedPackage> resolved;
        if (useLock)
        {
            resolved = LoadLockedPackages(repository, lockFile!, project);
        }
        else
        {
            resolved = ResolveGraph(project, repository);
            WriteLock(lockPath, manifestHash, resolved);
        }

        var packagesRoot = Path.Combine(projectDirectory, PackagesDirectoryName);
        Directory.CreateDirectory(packagesRoot);
        foreach (var package in resolved.Values.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
        {
            ValidateNativeCompatibility(ReadArchiveProject(package.ArchivePath), runtimes);
            var packageDirectory = Path.Combine(packagesRoot, package.Id.ToLowerInvariant(), package.Version.ToString());
            ReplacePackageDirectory(packagesRoot, packageDirectory, package.ArchivePath, runtimes);
        }
        PrunePackagesFromPreviousLock(packagesRoot, previousLock, resolved);

        return new RestoreSummary(resolved.Count, useLock, packagesRoot, runtimes);
    }

    public static void Clean(string projectPath)
    {
        var fullProjectPath = FindProject(projectPath);
        var projectDirectory = Path.GetDirectoryName(fullProjectPath)!;
        var packagesRoot = Path.Combine(projectDirectory, PackagesDirectoryName);
        if (!Directory.Exists(packagesRoot))
            return;

        var lockFile = ReadLock(Path.Combine(projectDirectory, LockFileName));
        PrunePackagesFromPreviousLock(packagesRoot, lockFile, new Dictionary<string, ResolvedPackage>());

        if (!Directory.EnumerateFileSystemEntries(packagesRoot).Any())
            Directory.Delete(packagesRoot);
    }

    public static IReadOnlyList<string> List(string projectPath)
    {
        projectPath = FindProject(projectPath);
        var project = CxProject.Load(projectPath);
        var lockFile = ReadLock(Path.Combine(Path.GetDirectoryName(projectPath)!, LockFileName));
        var results = new List<string>();
        results.AddRange(project.Dependencies.Select(value => $"direct  {value}"));
        if (lockFile is not null)
            results.AddRange(lockFile.Packages.Values.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(value => $"resolved {value.Id}@{value.Version}"));
        return results;
    }

    private static string FindProject(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            var project = Directory.GetFiles(fullPath, "*.cxproj", SearchOption.TopDirectoryOnly).SingleOrDefault();
            var sourceDirectory = Path.Combine(fullPath, "src");
            if (project is null && Directory.Exists(sourceDirectory))
                project = Directory.GetFiles(sourceDirectory, "*.cxproj", SearchOption.TopDirectoryOnly).SingleOrDefault();
            return project ?? throw new CxpmException($"No .cxproj file found in '{fullPath}'.");
        }
        if (!File.Exists(fullPath))
            throw new CxpmException($"Project file '{fullPath}' does not exist.");
        return fullPath;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void ValidatePackageId(string value)
    {
        if (value.Contains('_'))
            throw new CxpmException($"Project and package names must not contain underscores: '{value}'.");
        if (!Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9.-]*$"))
            throw new CxpmException($"Invalid package ID '{value}'. Use letters, digits, '.' or '-'.");
    }

    internal static void ValidateRid(string value)
    {
        if (!Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]*$"))
            throw new CxpmException($"Invalid RID '{value}'. Use a RID from Microsoft's RID catalog.");
    }

}

public sealed record RestoreSummary(int PackageCount, bool UsedLockFile, string PackagesDirectory,
    IReadOnlyList<string> RuntimeIdentifiers);
