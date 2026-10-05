using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cxpm.Core.Models;
using YamlDotNet.Serialization;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Cxpm.Core;

public sealed class PackageManager
{
    private const string LockFileName = "cxpm.lock";
    private const string ManifestFileName = "package.cxpm";
    private const string PackagesDirectoryName = ".packages";
    private const int MaxArchiveEntryCount = 100_000;
    private const int MaxArchivePathLength = 1024;
    private const long MaxCompressedArchiveBytes = 512L * 1024 * 1024;
    private const long MaxUncompressedEntryBytes = 512L * 1024 * 1024;
    private const long MaxUncompressedPackageBytes = 2L * 1024 * 1024 * 1024;
    private static readonly DateTimeOffset ZipEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly HttpClient RepositoryHttpClient = new();

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
        Directory.CreateDirectory(directory);
        var name = string.IsNullOrWhiteSpace(requestedName)
            ? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : requestedName.Trim();
        ValidatePackageId(name);
        var path = Path.Combine(directory, $"{name}.cxproj");
        if (File.Exists(path))
            throw new CxpmException($"'{path}' already exists.");
        File.WriteAllText(path,
            $"name: {name}\nversion: 0.1.0\ntargets: []\nfeeds: []\ndependencies: []\n");
        return path;
    }

    public static string Build(string projectPath)
    {
        projectPath = FindProject(projectPath);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var project = CxProject.Load(projectPath);
        ValidatePackageId(project.Name);
        var version = SemVersion.Parse(project.Version).ToString();
        var outputDirectory = Path.Combine(projectDirectory, ".dist");
        Directory.CreateDirectory(outputDirectory);
        var archivePath = Path.Combine(outputDirectory, $"{project.Name}.{version}.zip");
        var temporaryArchivePath = Path.Combine(outputDirectory, $".{Guid.NewGuid():N}.tmp");

        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        entries[ManifestFileName] = projectPath;
        var selection = project.Package ?? new CxProjectPackageSection();
        AddGlobSelections(entries, projectDirectory, selection.Sources, "source", excludeBuildOutput: true);

        foreach (var rid in selection.Binary.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ValidateRid(rid);
            var binaryDirectory = Path.Combine(projectDirectory, "binary", rid);
            if (!Directory.Exists(binaryDirectory))
                throw new CxpmException($"Selected binary RID '{rid}' has no input folder at '{binaryDirectory}'.");
            AddDirectoryFiles(entries, binaryDirectory, $"bin/{rid}");
        }

        if (entries.Count > MaxArchiveEntryCount)
            throw new CxpmException($"Package contains more than {MaxArchiveEntryCount} files.");
        long inputBytes = 0;
        var packageEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (archiveName, sourcePath) in entries)
        {
            ValidateArchivePath(archiveName, packageEntryNames);
            var length = new FileInfo(sourcePath).Length;
            if (length > MaxUncompressedEntryBytes)
                throw new CxpmException($"Package file '{archiveName}' exceeds the {MaxUncompressedEntryBytes / (1024 * 1024)} MiB file limit.");
            inputBytes = checked(inputBytes + length);
            if (inputBytes > MaxUncompressedPackageBytes)
                throw new CxpmException($"Package contents exceed the {MaxUncompressedPackageBytes / (1024 * 1024)} MiB total size limit.");
        }

        using (var archive = ZipFile.Open(temporaryArchivePath, ZipArchiveMode.Create))
        {
            foreach (var (archiveName, sourcePath) in entries)
            {
                var entry = archive.CreateEntry(archiveName, CompressionLevel.Optimal);
                entry.LastWriteTime = ZipEpoch;
                using var input = File.OpenRead(sourcePath);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        if (new FileInfo(temporaryArchivePath).Length > MaxCompressedArchiveBytes)
        {
            File.Delete(temporaryArchivePath);
            throw new CxpmException($"Package archive exceeds the {MaxCompressedArchiveBytes / (1024 * 1024)} MiB size limit.");
        }
        File.Move(temporaryArchivePath, archivePath, overwrite: true);
        return archivePath;
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
        if (!project.Dependencies.Any(existing => PackageDependency.Parse(existing).Id
                .Equals(packageId, StringComparison.OrdinalIgnoreCase)))
            throw new CxpmException($"Project does not declare a dependency on '{packageId}'.");

        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var section = FindDependencySection(lines);
        if (section < 0)
            throw new CxpmException("Cannot locate the root-level 'dependencies' list in the project file.");

        if (IsInlineDependencyList(lines[section]))
        {
            var remaining = project.Dependencies.Where(existing => !PackageDependency.Parse(existing).Id
                .Equals(packageId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (remaining.Length == 0)
                lines[section] = FormatDependencyHeader(lines[section], empty: true);
            else
            {
                lines[section] = FormatDependencyHeader(lines[section]);
                lines.InsertRange(section + 1, remaining.Select(FormatDependencyLine));
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
        if (!project.Dependencies.Any(existing => !PackageDependency.Parse(existing).Id
                .Equals(packageId, StringComparison.OrdinalIgnoreCase)))
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

    private static Dictionary<string, ResolvedPackage> ResolveGraph(CxProject root, IPackageRepository repository)
    {
        var direct = root.Dependencies.Select(PackageDependency.Parse).ToArray();
        var selected = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        for (var pass = 0; pass < 32; pass++)
        {
            var requirements = new Dictionary<string, List<DependencyRequirement>>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<DependencyRequirement>();
            foreach (var dependency in direct)
                queue.Enqueue(new DependencyRequirement(dependency, true, root.Name, []));

            var current = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unresolved = new List<(string Id, DependencyRequirement[] Constraints)>();
            while (queue.Count > 0)
            {
                var requirement = queue.Dequeue();
                if (HasSupersededAncestor(requirement, current))
                    continue;
                if (!requirements.TryGetValue(requirement.Dependency.Id, out var list))
                    requirements[requirement.Dependency.Id] = list = [];
                else
                    list.RemoveAll(existing => HasSupersededAncestor(existing, current));
                if (!list.Any(existing => existing.Dependency == requirement.Dependency
                        && existing.Path.Equals(requirement.Path, StringComparison.OrdinalIgnoreCase)))
                    list.Add(requirement);

                var directRequirements = list.Where(x => x.Direct).ToArray();
                var activeRequirements = directRequirements.Length > 0 ? directRequirements : list.ToArray();
                var chosen = ChooseVersion(repository, requirement.Dependency.Id, activeRequirements);
                if (chosen is null)
                {
                    unresolved.Add((requirement.Dependency.Id, activeRequirements));
                    continue;
                }
                current[requirement.Dependency.Id] = chosen;

                var processKey = $"{chosen.Id}@{chosen.Version}";
                if (!processed.Add(processKey))
                    continue;
                foreach (var child in chosen.Dependencies)
                    queue.Enqueue(new DependencyRequirement(child, false,
                        $"{requirement.Path} -> {chosen.Id}@{chosen.Version}",
                        requirement.Ancestors.Append((chosen.Id, chosen.Version)).ToArray()));
            }

            foreach (var failure in unresolved)
            {
                var activeConstraints = failure.Constraints.Where(constraint => constraint.Ancestors.All(ancestor =>
                    !current.TryGetValue(ancestor.Id, out var selectedPackage)
                    || selectedPackage.Version.Equals(ancestor.Version))).ToArray();
                if (activeConstraints.Length > 0)
                    throw CreateVersionConflict(failure.Id, activeConstraints);
            }

            if (SameSelection(selected, current))
                return current;
            selected = current;
        }
        throw new CxpmException("Dependency resolution did not converge after 32 passes. Check for conflicting or cyclic package constraints.");
    }

    private static bool SameSelection(Dictionary<string, ResolvedPackage> left, Dictionary<string, ResolvedPackage> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value)
            && pair.Value.Version.Equals(value.Version));

    private static bool HasSupersededAncestor(DependencyRequirement requirement,
        IReadOnlyDictionary<string, ResolvedPackage> current) => requirement.Ancestors.Any(ancestor =>
        current.TryGetValue(ancestor.Id, out var selectedPackage)
        && !selectedPackage.Version.Equals(ancestor.Version));

    private static ResolvedPackage? ChooseVersion(IPackageRepository repository, string id,
        DependencyRequirement[] constraints)
    {
        var versions = repository.GetVersions(id);
        var ranges = constraints.Select(constraint => (constraint.Dependency,
            Range: constraint.Dependency.GetVersionRange())).ToArray();
        var includePrerelease = ranges.Any(constraint => constraint.Range.IncludesPrerelease);
        var matching = versions.Where(candidate => ranges.All(constraint =>
            constraint.Range.Contains(candidate, includePrerelease))).ToList();
        if (matching.Count == 0)
            return null;

        var floating = ranges.Any(x => x.Range.IsFloating);
        var orderedMatches = matching.OrderBy(x => x)
            .ThenBy(x => x.ToString(), StringComparer.Ordinal).ToArray();
        var chosen = floating ? orderedMatches[^1] : orderedMatches[0];
        var archivePath = repository.GetPackageArchive(id, chosen);
        var manifest = ReadArchiveProject(archivePath);
        ValidateArchiveIdentity(manifest, id, chosen);
        return new ResolvedPackage(id, chosen, archivePath, HashFile(archivePath),
            manifest.Dependencies.Select(PackageDependency.Parse).ToArray());
    }

    private static CxpmException CreateVersionConflict(string id, DependencyRequirement[] constraints)
    {
        var paths = string.Join(Environment.NewLine, constraints.Select(constraint =>
            $"  {constraint.Path} -> {constraint.Dependency.Id}@{constraint.Dependency.RangeText}"));
        return new CxpmException($"No published version of '{id}' satisfies the dependency constraint(s):{Environment.NewLine}{paths}");
    }

    private static CxProject ReadArchiveProject(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry(ManifestFileName)
            ?? throw new CxpmException($"Package archive '{archivePath}' has no '{ManifestFileName}' manifest.");
        using var reader = new StreamReader(entry.Open());
        return CxProject.Parse(reader.ReadToEnd(), $"{archivePath}:{ManifestFileName}");
    }

    private static void ValidateArchiveIdentity(CxProject manifest, string expectedId, SemVersion expectedVersion)
    {
        if (!manifest.Name.Equals(expectedId, StringComparison.OrdinalIgnoreCase)
            || !SemVersion.Parse(manifest.Version).Equals(expectedVersion))
            throw new CxpmException($"Package archive identity does not match its repository location: expected '{expectedId}@{expectedVersion}', got '{manifest.Name}@{manifest.Version}'.");
    }

    private static Dictionary<string, ResolvedPackage> LoadLockedPackages(IPackageRepository repository, LockFile lockFile,
        CxProject project)
    {
        var result = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in lockFile.Packages.Values)
        {
            ValidatePackageId(item.Id);
            var version = SemVersion.Parse(item.Version);
            var publishedVersion = repository.GetVersions(item.Id).FirstOrDefault(candidate => candidate.Equals(version));
            if (publishedVersion is null)
                throw new CxpmException($"Locked package '{item.Id}@{item.Version}' is missing from repository '{repository.Location}'.");
            var archivePath = repository.GetPackageArchive(item.Id, version);
            var hash = HashFile(archivePath);
            if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CxpmException($"Hash mismatch for locked package '{item.Id}@{item.Version}'. The repository package changed after the lock was written.");
            var manifest = ReadArchiveProject(archivePath);
            ValidateArchiveIdentity(manifest, item.Id, version);
            var archiveDependencies = manifest.Dependencies.Select(PackageDependency.Parse).Select(dependency =>
                $"{dependency.Id}@{dependency.RangeText}").ToArray();
            if (item.Dependencies is null || !item.Dependencies.SequenceEqual(archiveDependencies, StringComparer.Ordinal))
                throw new CxpmException($"Lock file dependency metadata for '{item.Id}@{item.Version}' does not match its package manifest.");
            result[item.Id] = new ResolvedPackage(item.Id, version, archivePath, hash,
                manifest.Dependencies.Select(PackageDependency.Parse).ToArray());
        }
        ValidateLockedGraph(project, result);
        return result;
    }

    private static void ValidateLockedGraph(CxProject project,
        IReadOnlyDictionary<string, ResolvedPackage> packages)
    {
        var direct = project.Dependencies.Select(PackageDependency.Parse).ToArray();
        var directById = direct.GroupBy(dependency => dependency.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in direct)
        {
            if (!packages.ContainsKey(dependency.Id))
                throw new CxpmException($"Lock file is incomplete: direct dependency '{dependency.Id}' is not locked.");
        }
        var transitiveById = new Dictionary<string, List<PackageDependency>>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages.Values)
        {
            foreach (var dependency in package.Dependencies)
            {
                if (!transitiveById.TryGetValue(dependency.Id, out var constraints))
                    transitiveById[dependency.Id] = constraints = [];
                constraints.Add(dependency);
            }
        }

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(direct.Select(dependency => dependency.Id));
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!reachable.Add(id))
                continue;
            if (!packages.TryGetValue(id, out var package))
                throw new CxpmException($"Lock file is incomplete: dependency '{id}' is not locked.");
            foreach (var dependency in package.Dependencies)
                queue.Enqueue(dependency.Id);
        }

        var unreferenced = packages.Keys.Where(id => !reachable.Contains(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        if (unreferenced.Length > 0)
            throw new CxpmException($"Lock file contains package(s) that are not reachable from the project dependencies: {string.Join(", ", unreferenced)}.");

        foreach (var package in packages.Values)
        {
            var constraints = directById.TryGetValue(package.Id, out var directConstraints)
                ? directConstraints
                : transitiveById[package.Id].ToArray();
            var ranges = constraints.Select(dependency => dependency.GetVersionRange()).ToArray();
            var includePrerelease = ranges.Any(range => range.IncludesPrerelease);
            if (ranges.Any(range => !range.Contains(package.Version, includePrerelease)))
            {
                var descriptions = string.Join(", ", constraints.Select(dependency => $"{dependency.Id}@{dependency.RangeText}"));
                throw new CxpmException($"Lock file selects '{package.Id}@{package.Version}', which does not satisfy its active dependency constraint(s): {descriptions}.");
            }
        }
    }

    private static void WriteLock(string path, string projectHash, Dictionary<string, ResolvedPackage> packages)
    {
        var lockFile = new LockFile
        {
            LockVersion = 1,
            ProjectHash = projectHash,
            Packages = packages.Values.ToDictionary(
                x => x.Id.ToLowerInvariant(),
                x => new LockedPackage
                {
                    Id = x.Id,
                    Version = x.Version.ToString(),
                    Sha256 = x.Sha256,
                    Dependencies = x.Dependencies.Select(d => $"{d.Id}@{d.RangeText}").ToList(),
                }, StringComparer.OrdinalIgnoreCase),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(lockFile, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static LockFile? ReadLock(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var lockFile = JsonSerializer.Deserialize<LockFile>(File.ReadAllText(path));
            if (lockFile is null || lockFile.LockVersion != 1 || lockFile.Packages is null)
                throw new CxpmException($"Lock file '{path}' has an unsupported format.");
            return lockFile;
        }
        catch (JsonException exception)
        {
            throw new CxpmException($"Lock file '{path}' is invalid: {exception.Message}");
        }
    }

    private static LockFile? TryReadLockForCleanup(string path)
    {
        try
        {
            return ReadLock(path);
        }
        catch (CxpmException)
        {
            // `update` must be able to recover from an invalid old lock file.
            return null;
        }
    }

    private static void PrunePackagesFromPreviousLock(string packagesRoot, LockFile? previousLock,
        IReadOnlyDictionary<string, ResolvedPackage> resolved)
    {
        if (previousLock?.Packages is null)
            return;

        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in resolved.Values)
            retained.Add($"{package.Id}/{package.Version}");

        foreach (var package in previousLock.Packages.Values)
        {
            try
            {
                ValidatePackageId(package.Id);
                var version = SemVersion.Parse(package.Version);
                if (retained.Contains($"{package.Id}/{version}"))
                    continue;
                var packageDirectory = Path.GetFullPath(Path.Combine(packagesRoot,
                    package.Id.ToLowerInvariant(), version.ToString()));
                var fullRoot = Path.GetFullPath(packagesRoot) + Path.DirectorySeparatorChar;
                var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (packageDirectory.StartsWith(fullRoot, pathComparison) && Directory.Exists(packageDirectory))
                    Directory.Delete(packageDirectory, recursive: true);
            }
            catch (FormatException)
            {
                // Ignore malformed entries from an obsolete lock during cleanup.
            }
            catch (CxpmException)
            {
                // Never let untrusted lock-file identities escape the packages folder.
            }
        }
    }

    private static void ReplacePackageDirectory(string packagesRoot, string destination, string archivePath,
        IReadOnlyList<string> runtimeIdentifiers)
    {
        var fullRoot = Path.GetFullPath(packagesRoot) + Path.DirectorySeparatorChar;
        var fullDestination = Path.GetFullPath(destination);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullDestination.StartsWith(fullRoot, pathComparison))
            throw new CxpmException("Refusing to extract a package outside the project packages folder.");

        Directory.CreateDirectory(packagesRoot);
        var stagingDirectory = Path.Combine(packagesRoot, $".cxpm-{Guid.NewGuid():N}.tmp");
        var backupDirectory = Path.Combine(packagesRoot, $".cxpm-{Guid.NewGuid():N}.bak");
        var destinationExisted = Directory.Exists(fullDestination);
        var movedOldDirectory = false;
        var archiveFileLength = new FileInfo(archivePath).Length;
        if (archiveFileLength > MaxCompressedArchiveBytes)
            throw new CxpmException($"Package archive exceeds the {MaxCompressedArchiveBytes / (1024 * 1024)} MiB size limit.");
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxArchiveEntryCount)
            throw new CxpmException($"Package archive contains more than {MaxArchiveEntryCount} entries.");
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredUncompressedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            ValidateArchiveEntry(entry, entryNames, ref declaredUncompressedBytes);
        }

        var selectedRids = SelectPackageRids(archive.Entries, runtimeIdentifiers);

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            var fullPackageDirectory = Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;
            long extractedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (!ShouldExtractEntry(entry.FullName, selectedRids))
                    continue;
                var entryPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var outputPath = Path.GetFullPath(Path.Combine(stagingDirectory, entryPath));
                if (!outputPath.StartsWith(fullPackageDirectory, pathComparison))
                    throw new CxpmException($"Package archive contains an unsafe path '{entry.FullName}'.");
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(outputPath);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                ExtractArchiveEntry(entry, outputPath, ref extractedBytes);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
            if (destinationExisted)
            {
                MoveDirectoryWithRetry(fullDestination, backupDirectory);
                movedOldDirectory = true;
            }
            MoveDirectoryWithRetry(stagingDirectory, fullDestination);
            if (movedOldDirectory)
                Directory.Delete(backupDirectory, recursive: true);
        }
        catch
        {
            if (movedOldDirectory && !Directory.Exists(fullDestination) && Directory.Exists(backupDirectory))
                MoveDirectoryWithRetry(backupDirectory, fullDestination);
            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
            if (Directory.Exists(backupDirectory) && Directory.Exists(fullDestination))
                Directory.Delete(backupDirectory, recursive: true);
        }
    }

    private static void MoveDirectoryWithRetry(string source, string destination)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException exception) when (attempt < maxAttempts && IsTransientWindowsFileLock(exception))
            {
                Thread.Sleep(25 * (1 << (attempt - 1)));
            }
        }
    }

    private static bool IsTransientWindowsFileLock(IOException exception)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        var windowsError = exception.HResult & 0xFFFF;
        return windowsError is 5 or 32 or 33;
    }

    internal interface IPackageRepository
    {
        string Location { get; }
        void EnsureExists();
        List<SemVersion> GetVersions(string packageId);
        string GetPackageArchive(string packageId, SemVersion version);
        string Publish(string archivePath, string displayId, string normalizedId, string version);
    }

    internal sealed class FolderPackageRepository(string path) : IPackageRepository
    {
        public string Location { get; } = Path.GetFullPath(path);

        public void EnsureExists()
        {
            if (!Directory.Exists(Location))
                throw new CxpmException($"Repository folder '{Location}' does not exist.");
        }

        public List<SemVersion> GetVersions(string packageId)
        {
            var packageRoot = Path.Combine(Location, packageId.ToLowerInvariant());
            if (!Directory.Exists(packageRoot))
                return [];
            var result = new List<SemVersion>();
            foreach (var versionDirectory in Directory.EnumerateDirectories(packageRoot))
            {
                var versionName = Path.GetFileName(versionDirectory);
                if (!SemVersion.TryParse(versionName, out var version))
                    continue;
                if (Directory.EnumerateFiles(versionDirectory, "*.zip").Any())
                    result.Add(version);
            }
            return result;
        }

        public string GetPackageArchive(string packageId, SemVersion version)
        {
            var versionDirectory = Path.Combine(Location, packageId.ToLowerInvariant(), version.ToString());
            if (!Directory.Exists(versionDirectory))
                throw new CxpmException($"Package '{packageId}@{version}' is missing from repository '{Location}'.");
            return Directory.EnumerateFiles(versionDirectory, "*.zip")
                .OrderBy(candidate => candidate, StringComparer.Ordinal).FirstOrDefault()
                ?? throw new CxpmException($"Package '{packageId}@{version}' has no ZIP archive in repository '{Location}'.");
        }

        public string Publish(string archivePath, string displayId, string normalizedId, string version)
        {
            var destinationDirectory = Path.Combine(Location, normalizedId, version);
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(archivePath));
            if (File.Exists(destination))
                throw new CxpmException($"Package '{displayId}@{version}' is already published; package versions are immutable.");
            File.Copy(archivePath, destination);
            return destination;
        }
    }

    internal sealed class MultiPackageRepository(IReadOnlyList<IPackageRepository> feeds) : IPackageRepository
    {
        private readonly Dictionary<(string PackageId, string Version), IPackageRepository> _sources = [];

        public string Location => string.Join("; ", feeds.Select(feed => feed.Location));

        public void EnsureExists()
        {
            foreach (var feed in feeds)
                feed.EnsureExists();
        }

        public List<SemVersion> GetVersions(string packageId)
        {
            var versions = new List<SemVersion>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var feed in feeds)
            {
                foreach (var version in feed.GetVersions(packageId))
                {
                    var key = version.ToString();
                    if (!seen.Add(key))
                        continue;
                    versions.Add(version);
                    _sources[(packageId.ToLowerInvariant(), key)] = feed;
                }
            }
            return versions;
        }

        public string GetPackageArchive(string packageId, SemVersion version)
        {
            var key = (packageId.ToLowerInvariant(), version.ToString());
            if (_sources.TryGetValue(key, out var source))
                return source.GetPackageArchive(packageId, version);

            foreach (var feed in feeds)
            {
                if (!feed.GetVersions(packageId).Any(candidate => candidate.Equals(version)))
                    continue;
                _sources[key] = feed;
                return feed.GetPackageArchive(packageId, version);
            }

            throw new CxpmException($"Package '{packageId}@{version}' was not found in any configured feed: {Location}.");
        }

        public string Publish(string archivePath, string displayId, string normalizedId, string version) =>
            throw new CxpmException("Publishing requires one destination feed. Pass --feed <path-or-url> to select it.");
    }

    internal sealed class HttpPackageRepository(Uri baseUri, HttpClient httpClient, string? cacheDirectory = null)
        : IPackageRepository
    {
        private readonly Uri _baseUri = new(baseUri.AbsoluteUri.TrimEnd('/') + "/");
        private readonly HttpClient _httpClient = httpClient;
        private readonly string _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cxpm", "http-cache",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseUri.AbsoluteUri.TrimEnd('/')))).ToLowerInvariant());

        public string Location => _baseUri.AbsoluteUri.TrimEnd('/');

        public void EnsureExists()
        {
            // Static feeds have no root index; availability is checked on package lookup.
        }

        public List<SemVersion> GetVersions(string packageId)
        {
            try
            {
                var indexResponse = Send(new HttpRequestMessage(HttpMethod.Get, GetIndexUri(packageId)));
                using (indexResponse)
                {
                    if (indexResponse.StatusCode == HttpStatusCode.NotFound)
                        return [];
                    EnsureSuccess(indexResponse);
                    var index = JsonSerializer.Deserialize<PackageVersionIndex>(indexResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult())
                        ?? throw new CxpmException($"Package index for '{packageId}' at '{Location}' is empty.");
                    if (index.Versions is null)
                        throw new CxpmException($"Package index for '{packageId}' at '{Location}' has no 'versions' list.");

                    var result = new List<SemVersion>();
                    foreach (var text in index.Versions.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!SemVersion.TryParse(text, out var version))
                            continue;
                        result.Add(version);
                    }
                    return result;
                }
            }
            catch (JsonException exception)
            {
                throw new CxpmException($"Package index for '{packageId}' at '{Location}' is invalid: {exception.Message}");
            }
        }

        public string GetPackageArchive(string packageId, SemVersion version) =>
            DownloadPackage(packageId, version);

        public string Publish(string archivePath, string displayId, string normalizedId, string version)
        {
            var versionValue = SemVersion.Parse(version);
            var packageUri = GetPackageUri(normalizedId, versionValue);
            var initialIndex = ReadVersionIndex(normalizedId);
            if (initialIndex.Versions.Contains(version, StringComparer.OrdinalIgnoreCase))
            {
                VerifyExistingPackage(packageUri, archivePath, displayId, version);
                return packageUri.AbsoluteUri;
            }
            if (initialIndex.Exists && initialIndex.ETag is null)
                throw new CxpmException($"Repository '{Location}' must return an ETag for existing package indexes to publish safely.");

            using (var request = CreateRequest(HttpMethod.Put, packageUri))
            {
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                request.Content = new StreamContent(File.OpenRead(archivePath));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                using var response = Send(request);
                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                    VerifyExistingPackage(packageUri, archivePath, displayId, version);
                else
                    EnsureSuccess(response);
            }

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var (versions, etag, indexExists) = ReadVersionIndex(normalizedId);
                if (versions.Contains(version, StringComparer.OrdinalIgnoreCase))
                    return packageUri.AbsoluteUri;
                versions.Add(version);
                versions = versions.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(item => SemVersion.TryParse(item, out _))
                    .OrderBy(item => SemVersion.Parse(item)).Select(item => SemVersion.Parse(item).ToString()).ToList();

                using var request = CreateRequest(HttpMethod.Put, GetIndexUri(normalizedId));
                if (!indexExists)
                    request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                else if (etag is not null)
                    request.Headers.IfMatch.Add(etag);
                else
                    throw new CxpmException($"Repository '{Location}' must return an ETag for existing package indexes to publish safely.");
                request.Content = JsonContent.Create(new PackageVersionIndex { Versions = versions });
                using var response = Send(request);
                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                    continue;
                EnsureSuccess(response);
                return packageUri.AbsoluteUri;
            }
            throw new CxpmException($"Could not update the package index for '{normalizedId}' at '{Location}' because it changed during publication.");
        }

        private void VerifyExistingPackage(Uri packageUri, string archivePath, string displayId, string version)
        {
            using var request = CreateRequest(HttpMethod.Get, packageUri);
            using var response = Send(request);
            EnsureSuccess(response);
            using var content = response.Content.ReadAsStream();
            var existingHash = Convert.ToHexString(SHA256.HashData(content));
            var requestedHash = HashFile(archivePath);
            if (!existingHash.Equals(requestedHash, StringComparison.OrdinalIgnoreCase))
                throw new CxpmException($"Package '{displayId}@{version}' is already published with different content; package versions are immutable.");
        }

        private (List<string> Versions, EntityTagHeaderValue? ETag, bool Exists) ReadVersionIndex(string packageId)
        {
            using var response = Send(new HttpRequestMessage(HttpMethod.Get, GetIndexUri(packageId)));
            if (response.StatusCode == HttpStatusCode.NotFound)
                return ([], null, false);
            EnsureSuccess(response);
            PackageVersionIndex index;
            try
            {
                index = JsonSerializer.Deserialize<PackageVersionIndex>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
                    ?? throw new CxpmException($"Package index for '{packageId}' at '{Location}' is empty.");
            }
            catch (JsonException exception)
            {
                throw new CxpmException($"Package index for '{packageId}' at '{Location}' is invalid: {exception.Message}");
            }
            return (index.Versions ?? throw new CxpmException($"Package index for '{packageId}' at '{Location}' has no 'versions' list."),
                response.Headers.ETag, true);
        }

        private string DownloadPackage(string packageId, SemVersion version)
        {
            var normalizedId = packageId.ToLowerInvariant();
            var fileName = $"{normalizedId}.{version}.zip";
            var destinationDirectory = Path.Combine(_cacheDirectory, normalizedId, version.ToString());
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(destinationDirectory, fileName);
            if (File.Exists(destination))
                return destination;

            using var response = Send(new HttpRequestMessage(HttpMethod.Get, GetPackageUri(normalizedId, version)));
            EnsureSuccess(response);
            if (response.Content.Headers.ContentLength > MaxCompressedArchiveBytes)
                throw new CxpmException($"Package archive from '{Location}' exceeds the {MaxCompressedArchiveBytes / (1024 * 1024)} MiB download limit.");
            var temporaryPath = Path.Combine(destinationDirectory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                using var input = response.Content.ReadAsStream();
                using (var output = File.Create(temporaryPath))
                    CopyStreamWithLimit(input, output, MaxCompressedArchiveBytes,
                        $"Package archive from '{Location}'");
                File.Move(temporaryPath, destination, overwrite: true);
                return destination;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        private Uri GetIndexUri(string packageId) => new(_baseUri,
            $"{Uri.EscapeDataString(packageId.ToLowerInvariant())}/index.json");

        private Uri GetPackageUri(string packageId, SemVersion version) => new(_baseUri,
            $"{Uri.EscapeDataString(packageId.ToLowerInvariant())}/{Uri.EscapeDataString(version.ToString())}/{Uri.EscapeDataString(packageId.ToLowerInvariant())}.{Uri.EscapeDataString(version.ToString())}.zip");

        private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
        {
            var request = new HttpRequestMessage(method, uri);
            var token = Environment.GetEnvironmentVariable("CXPM_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            return request;
        }

        private HttpResponseMessage Send(HttpRequestMessage request)
        {
            try
            {
                return _httpClient.Send(request);
            }
            catch (HttpRequestException exception)
            {
                throw new CxpmException($"Could not contact repository '{Location}': {exception.Message}");
            }
            catch (TaskCanceledException exception)
            {
                throw new CxpmException($"Request to repository '{Location}' timed out: {exception.Message}");
            }
        }

        private static void EnsureSuccess(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
                throw new CxpmException($"Repository request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) for '{response.RequestMessage?.RequestUri}'.");
        }

        private sealed class PackageVersionIndex
        {
            [JsonPropertyName("versions")]
            public List<string>? Versions { get; set; }
        }
    }

    private static void ValidateArchiveEntry(ZipArchiveEntry entry, ISet<string> entryNames,
        ref long declaredUncompressedBytes)
    {
        var name = entry.FullName;
        var path = ValidateArchivePath(name, entryNames);
        if (entry.Length > MaxUncompressedEntryBytes)
            throw new CxpmException($"Package archive entry '{name}' exceeds the {MaxUncompressedEntryBytes / (1024 * 1024)} MiB size limit.");
        if (name.EndsWith("/", StringComparison.Ordinal) && entry.Length != 0)
            throw new CxpmException($"Package archive directory entry '{name}' contains data.");
        declaredUncompressedBytes = checked(declaredUncompressedBytes + entry.Length);
        if (declaredUncompressedBytes > MaxUncompressedPackageBytes)
            throw new CxpmException($"Package archive expands beyond the {MaxUncompressedPackageBytes / (1024 * 1024)} MiB package limit.");

        // Unix ZIP archives can mark an entry as a symbolic link in the upper mode bits.
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixFileType == 0xA000)
            throw new CxpmException($"Package archive contains a symbolic link '{name}'.");
    }

    private static string ValidateArchivePath(string name, ISet<string> entryNames)
    {
        var path = name.EndsWith("/", StringComparison.Ordinal) ? name[..^1] : name;
        var segments = path.Split('/');
        if (string.IsNullOrWhiteSpace(path) || name.Length > MaxArchivePathLength
            || name.Contains('\\') || Path.IsPathRooted(name)
            || segments.Any(segment => segment.Length == 0 || segment.Length > 255
                || segment is "." or ".." || segment.Contains(':')))
            throw new CxpmException($"Package archive contains an unsafe path '{name}'.");
        if (!entryNames.Add(path))
            throw new CxpmException($"Package archive contains duplicate paths that differ only by case or directory notation: '{name}'.");
        return path;
    }

    private static void ExtractArchiveEntry(ZipArchiveEntry entry, string outputPath, ref long extractedBytes)
    {
        using var input = entry.Open();
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long entryBytes = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            entryBytes += read;
            extractedBytes += read;
            if (entryBytes > MaxUncompressedEntryBytes || extractedBytes > MaxUncompressedPackageBytes)
                throw new CxpmException("Package archive expands beyond the configured extraction size limit.");
            output.Write(buffer, 0, read);
        }
        if (entryBytes != entry.Length)
            throw new CxpmException($"Package archive entry '{entry.FullName}' expanded to an unexpected size.");
    }

    private static IReadOnlySet<string> SelectPackageRids(IEnumerable<ZipArchiveEntry> entries,
        IEnumerable<string> runtimeIdentifiers)
    {
        const string binaryPrefix = "bin/";
        var availableRids = entries
            .Select(entry => entry.FullName)
            .Where(path => path.StartsWith(binaryPrefix, StringComparison.Ordinal))
            .Select(path =>
            {
                var ridEnd = path.IndexOf('/', binaryPrefix.Length);
                return ridEnd > 0 ? path[binaryPrefix.Length..ridEnd] : null;
            })
            .Where(rid => rid is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var runtimeIdentifier in runtimeIdentifiers)
        {
            var packageRid = RuntimeIdentifierGraph.GetFallbacks(runtimeIdentifier)
                .FirstOrDefault(rid => availableRids.Contains(rid));
            if (packageRid is not null)
                selected.Add(packageRid);
        }
        return selected;
    }

    private static bool ShouldExtractEntry(string entryPath, IReadOnlySet<string> selectedRids)
    {
        const string binaryPrefix = "bin/";
        if (entryPath == ManifestFileName || entryPath.StartsWith("source/", StringComparison.Ordinal))
            return true;
        if (!entryPath.StartsWith(binaryPrefix, StringComparison.Ordinal))
            throw new CxpmException($"Package archive entry '{entryPath}' is not supported. The first release supports source/ and bin/<rid>/ payloads.");
        var ridEnd = entryPath.IndexOf('/', binaryPrefix.Length);
        if (ridEnd < 0)
            return false;
        var packageRid = entryPath[binaryPrefix.Length..ridEnd];
        return selectedRids.Contains(packageRid);
    }

    private static void AddGlobSelections(IDictionary<string, string> entries, string root,
        IEnumerable<string> patterns, string destinationPrefix, bool excludeBuildOutput)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern) || Path.IsPathRooted(pattern) || pattern.Contains("..", StringComparison.Ordinal))
                throw new CxpmException($"Invalid package file pattern '{pattern}'. Patterns must be relative to the project.");
            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(pattern);
            if (!pattern.Contains('/') && !pattern.Contains('\\'))
                matcher.AddInclude($"**/{pattern}");
            if (excludeBuildOutput)
            {
                matcher.AddExclude(".packages/**");
                matcher.AddExclude("packages/**");
                matcher.AddExclude(".dist/**");
                matcher.AddExclude("dist/**");
                matcher.AddExclude("bin/**");
                matcher.AddExclude("obj/**");
                matcher.AddExclude("binary/**");
                matcher.AddExclude("assets/**");
            }
            var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(root)));
            foreach (var file in result.Files)
            {
                var relativePath = file.Path.Replace('\\', '/');
                var source = Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar));
                entries[$"{destinationPrefix}/{relativePath}"] = source;
            }
        }
    }

    private static void AddDirectoryFiles(IDictionary<string, string> entries, string root, string prefix)
    {
        foreach (var source in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(root, source).Replace('\\', '/');
            entries[$"{prefix}/{relativePath}"] = source;
        }
    }

    private static string FindProject(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            var project = Directory.GetFiles(fullPath, "*.cxproj", SearchOption.TopDirectoryOnly).SingleOrDefault();
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

    private static void CopyStreamWithLimit(Stream input, Stream output, long limit, string description)
    {
        var buffer = new byte[81920];
        long copied = 0;
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
                return;
            copied += read;
            if (copied > limit)
                throw new CxpmException($"{description} exceeds the {limit / (1024 * 1024)} MiB download limit.");
            output.Write(buffer, 0, read);
        }
    }

    private static IPackageRepository CreatePackageRepository(string location)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
            return new HttpPackageRepository(uri, RepositoryHttpClient);
        return new FolderPackageRepository(location);
    }

    private static IPackageRepository CreatePackageRepository(IReadOnlyList<string> feedPaths) =>
        feedPaths.Count == 1
            ? CreatePackageRepository(feedPaths[0])
            : new MultiPackageRepository(feedPaths.Select(CreatePackageRepository).ToArray());

    private static string NormalizeRepositoryLocation(string location, string? baseDirectory = null)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
                throw new CxpmException("Repository URLs cannot contain credentials, a query, or a fragment. Set CXPM_TOKEN for bearer authentication.");
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }
        if (location.Contains("://", StringComparison.Ordinal))
            throw new CxpmException("Repository URLs must use HTTP or HTTPS.");
        return baseDirectory is null
            ? Path.GetFullPath(location)
            : Path.GetFullPath(location, baseDirectory);
    }

    private static void ValidatePackageId(string value)
    {
        if (!Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]*$"))
            throw new CxpmException($"Invalid package ID '{value}'. Use letters, digits, '.', '_' or '-'.");
    }

    internal static void ValidateRid(string value)
    {
        if (!Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]*$"))
            throw new CxpmException($"Invalid RID '{value}'. Use a RID from Microsoft's RID catalog.");
    }

    private sealed record DependencyRequirement(PackageDependency Dependency, bool Direct, string Path,
        IReadOnlyList<(string Id, SemVersion Version)> Ancestors);
    private sealed record ResolvedPackage(string Id, SemVersion Version, string ArchivePath, string Sha256,
        IReadOnlyList<PackageDependency> Dependencies);

    private sealed class LockFile
    {
        public int LockVersion { get; set; }
        public string ProjectHash { get; set; } = "";
        public Dictionary<string, LockedPackage> Packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LockedPackage
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public List<string> Dependencies { get; set; } = [];
    }
}

public sealed record RestoreSummary(int PackageCount, bool UsedLockFile, string PackagesDirectory,
    IReadOnlyList<string> RuntimeIdentifiers);
