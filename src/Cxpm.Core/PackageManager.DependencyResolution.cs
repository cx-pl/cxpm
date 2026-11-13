using System.IO.Compression;
using System.Text.Json;
using Cxpm.Core.Models;

namespace Cxpm.Core;

public sealed partial class PackageManager
{
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
