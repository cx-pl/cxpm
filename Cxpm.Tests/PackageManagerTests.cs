using System.IO.Compression;
using System.Text.Json.Nodes;
using Cxpm.Core;
using Cxpm.Core.Models;
using Xunit;

namespace Cxpm.Tests;

public sealed class PackageManagerTests
{
    [Fact]
    public void PublishRejectsOverwritingAnExistingPackageVersion()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-immutable-test-").FullName;
        try
        {
            var projectDirectory = Path.Combine(root, "project");
            var repository = Path.Combine(root, "feed");
            CreatePackageProject(projectDirectory);
            var publishedPath = PackageManager.Publish(projectDirectory, repository);
            var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(publishedPath)));
            File.WriteAllText(Path.Combine(projectDirectory, "main.cx"), "changed package content");

            var error = Assert.Throws<CxpmException>(() => PackageManager.Publish(projectDirectory, repository));

            Assert.Contains("already published", error.Message, StringComparison.OrdinalIgnoreCase);
            var currentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(publishedPath)));
            Assert.Equal(originalHash, currentHash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildCopiesTheProjectManifestAndSelectedPayloads()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-build-test-").FullName;
        try
        {
            var projectDirectory = Path.Combine(root, "project");
            CreatePackageProject(projectDirectory);
            var projectFile = Path.Combine(projectDirectory, "sample.cxproj");
            var projectText = File.ReadAllText(projectFile);

            var archivePath = PackageManager.Build(projectDirectory);

            Assert.Equal(Path.Combine(projectDirectory, ".dist", "sample.1.0.0.zip"), archivePath);
            using var archive = ZipFile.OpenRead(archivePath);
            var entries = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("package.cxpm", entries);
            Assert.Contains("source/main.cx", entries);
            Assert.Contains("bin/win-x64/sample.dll", entries);
            Assert.Contains("bin/linux-x64/sample.so", entries);
            Assert.DoesNotContain("source/ignored.txt", entries);

            using var reader = new StreamReader(archive.GetEntry("package.cxpm")!.Open());
            Assert.Equal(projectText, reader.ReadToEnd());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreSelectsExactRidAndReusesLockFileAcrossRuntimeSwitch()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-restore-test-").FullName;
        try
        {
            var projectDirectory = Path.Combine(root, "library");
            var consumerDirectory = Path.Combine(root, "consumer");
            var repository = Path.Combine(root, "feed");
            CreatePackageProject(projectDirectory);
            Directory.CreateDirectory(consumerDirectory);
            PackageManager.Publish(projectDirectory, repository);

            File.WriteAllText(Path.Combine(consumerDirectory, "consumer.cxproj"), """
                name: consumer
                version: 0.1.0
                dependencies:
                - sample@1.0.0
                """);

            var firstRestore = PackageManager.Restore(consumerDirectory, repository,
                runtimeIdentifier: "linux-x64");
            var packageDirectory = Path.Combine(firstRestore.PackagesDirectory, "sample", "1.0.0");

            Assert.False(firstRestore.UsedLockFile);
            Assert.True(File.Exists(Path.Combine(packageDirectory, "bin", "linux-x64", "sample.so")));
            Assert.False(File.Exists(Path.Combine(packageDirectory, "bin", "win-x64", "sample.dll")));
            Assert.True(File.Exists(Path.Combine(packageDirectory, "source", "main.cx")));

            var secondRestore = PackageManager.Restore(consumerDirectory, repository,
                runtimeIdentifier: "win-x64");
            Assert.True(secondRestore.UsedLockFile);
            Assert.True(File.Exists(Path.Combine(packageDirectory, "bin", "win-x64", "sample.dll")));
            Assert.False(File.Exists(Path.Combine(packageDirectory, "bin", "linux-x64", "sample.so")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreUsesMicrosoftRidFallbackAndExtractsOnlyTheClosestAvailableBinary()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-rid-fallback-test-").FullName;
        try
        {
            var projectDirectory = Path.Combine(root, "library");
            var consumerDirectory = Path.Combine(root, "consumer");
            var repository = Path.Combine(root, "feed");
            CreatePackageProject(projectDirectory);
            Directory.CreateDirectory(consumerDirectory);
            PackageManager.Publish(projectDirectory, repository);
            File.WriteAllText(Path.Combine(consumerDirectory, "consumer.cxproj"), """
                name: consumer
                version: 0.1.0
                dependencies:
                - sample@1.0.0
                """);

            var restore = PackageManager.Restore(consumerDirectory, repository,
                runtimeIdentifier: "linux-musl-x64");
            var packageDirectory = Path.Combine(restore.PackagesDirectory, "sample", "1.0.0");

            Assert.True(File.Exists(Path.Combine(packageDirectory, "bin", "linux-x64", "sample.so")));
            Assert.False(File.Exists(Path.Combine(packageDirectory, "bin", "win-x64", "sample.dll")));
            Assert.True(File.Exists(Path.Combine(packageDirectory, "source", "main.cx")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreSkipsBinariesWhenNoCompatibleRidExists()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-rid-no-match-test-").FullName;
        try
        {
            var projectDirectory = Path.Combine(root, "library");
            var consumerDirectory = Path.Combine(root, "consumer");
            var repository = Path.Combine(root, "feed");
            CreatePackageProject(projectDirectory);
            Directory.CreateDirectory(consumerDirectory);
            PackageManager.Publish(projectDirectory, repository);
            File.WriteAllText(Path.Combine(consumerDirectory, "consumer.cxproj"), """
                name: consumer
                version: 0.1.0
                dependencies:
                - sample@1.0.0
                """);

            var restore = PackageManager.Restore(consumerDirectory, repository,
                runtimeIdentifier: "win-arm64");
            var packageDirectory = Path.Combine(restore.PackagesDirectory, "sample", "1.0.0");

            Assert.False(Directory.Exists(Path.Combine(packageDirectory, "bin")));
            Assert.True(File.Exists(Path.Combine(packageDirectory, "source", "main.cx")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RestoreIncludesNearestBinaryForEveryProjectTarget()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-multi-target-restore-test-").FullName;
        try
        {
            var packageDirectory = Path.Combine(root, "library");
            var consumerDirectory = Path.Combine(root, "consumer");
            var repository = Path.Combine(root, "feed");
            CreatePackageProject(packageDirectory);
            PackageManager.Publish(packageDirectory, repository);
            Directory.CreateDirectory(consumerDirectory);
            File.WriteAllText(Path.Combine(consumerDirectory, "consumer.cxproj"), """
                name: consumer
                version: 0.1.0
                targets:
                - linux-musl-x64
                - win-x64
                dependencies:
                - sample@1.0.0
                """);

            var restore = PackageManager.Restore(consumerDirectory, repository);
            var packagePath = Path.Combine(restore.PackagesDirectory, "sample", "1.0.0");

            Assert.Equal(["linux-musl-x64", "win-x64"], restore.RuntimeIdentifiers);
            Assert.True(File.Exists(Path.Combine(packagePath, "bin", "linux-x64", "sample.so")));
            Assert.True(File.Exists(Path.Combine(packagePath, "bin", "win-x64", "sample.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LockedRestoreRequiresAnExistingMatchingLockFile()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-locked-restore-test-").FullName;
        try
        {
            var packageDirectory = Path.Combine(root, "library");
            var consumerDirectory = Path.Combine(root, "consumer");
            var repository = Path.Combine(root, "feed");
            CreatePackageProject(packageDirectory);
            PackageManager.Publish(packageDirectory, repository);
            Directory.CreateDirectory(consumerDirectory);
            var projectPath = Path.Combine(consumerDirectory, "consumer.cxproj");
            File.WriteAllText(projectPath, """
                name: consumer
                version: 0.1.0
                dependencies:
                - sample@1.0.0
                """);

            var missingLockError = Assert.Throws<CxpmException>(() =>
                PackageManager.Restore(consumerDirectory, repository, runtimeIdentifier: "win-x64", locked: true));
            Assert.Contains("requires", missingLockError.Message, StringComparison.OrdinalIgnoreCase);

            PackageManager.Restore(consumerDirectory, repository, update: true, runtimeIdentifier: "win-x64");
            Assert.True(PackageManager.Restore(consumerDirectory, repository,
                runtimeIdentifier: "win-x64", locked: true).UsedLockFile);

            var lockPath = Path.Combine(consumerDirectory, "cxpm.lock");
            var lockJson = JsonNode.Parse(File.ReadAllText(lockPath))!;
            lockJson["Packages"]!["sample"]!["Dependencies"] = new JsonArray("ghost@1.0.0");
            File.WriteAllText(lockPath, lockJson.ToJsonString());
            var edgeError = Assert.Throws<CxpmException>(() =>
                PackageManager.Restore(consumerDirectory, repository, runtimeIdentifier: "win-x64", locked: true));
            Assert.Contains("dependency metadata", edgeError.Message, StringComparison.OrdinalIgnoreCase);

            PackageManager.Restore(consumerDirectory, repository, update: true, runtimeIdentifier: "win-x64");
            lockJson = JsonNode.Parse(File.ReadAllText(lockPath))!;
            lockJson["Packages"]!.AsObject().Clear();
            File.WriteAllText(lockPath, lockJson.ToJsonString());
            var incompleteLockError = Assert.Throws<CxpmException>(() =>
                PackageManager.Restore(consumerDirectory, repository, runtimeIdentifier: "win-x64", locked: true));
            Assert.Contains("direct dependency", incompleteLockError.Message, StringComparison.OrdinalIgnoreCase);

            PackageManager.Restore(consumerDirectory, repository, update: true, runtimeIdentifier: "win-x64");

            File.WriteAllText(projectPath, """
                name: consumer
                version: 0.1.0
                description: changed
                dependencies:
                - sample@1.0.0
                """);
            var staleLockError = Assert.Throws<CxpmException>(() =>
                PackageManager.Restore(consumerDirectory, repository, runtimeIdentifier: "win-x64", locked: true));
            Assert.Contains("does not match", staleLockError.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LockedRestoreRejectsTransitiveVersionOutsideItsConstraint()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-locked-transitive-test-").FullName;
        try
        {
            var repository = Path.Combine(root, "feed");
            var consumerDirectory = Path.Combine(root, "consumer");
            var bridgeDirectory = Path.Combine(root, "bridge");
            PublishSimplePackage(repository, root, "inner", "1.0.0", []);
            var innerV2Directory = Path.Combine(root, "inner-v2");
            PublishSimplePackage(repository, innerV2Directory, "inner", "2.0.0", []);
            PublishSimplePackage(repository, bridgeDirectory, "bridge", "1.0.0", ["inner@^1.0.0"]);
            Directory.CreateDirectory(consumerDirectory);
            File.WriteAllText(Path.Combine(consumerDirectory, "consumer.cxproj"), """
                name: consumer
                version: 0.1.0
                dependencies:
                - bridge@1.0.0
                """);

            PackageManager.Restore(consumerDirectory, repository, runtimeIdentifier: "win-x64");
            var innerV2Archive = Path.Combine(repository, "inner", "2.0.0", "inner.2.0.0.zip");
            var innerV2Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(innerV2Archive)));
            var lockPath = Path.Combine(consumerDirectory, "cxpm.lock");
            var lockJson = JsonNode.Parse(File.ReadAllText(lockPath))!;
            lockJson["Packages"]!["inner"]!["Version"] = "2.0.0";
            lockJson["Packages"]!["inner"]!["Sha256"] = innerV2Hash;
            File.WriteAllText(lockPath, lockJson.ToJsonString());

            var error = Assert.Throws<CxpmException>(() =>
                PackageManager.Restore(consumerDirectory, repository, runtimeIdentifier: "win-x64", locked: true));
            Assert.Contains("does not satisfy", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("inner@^1.0.0", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RemoveDependencyKeepsOtherYamlContentAndComments()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-remove-dependency-test-").FullName;
        try
        {
            var projectPath = Path.Combine(root, "consumer.cxproj");
            var original = """
                name: consumer
                version: 0.1.0
                # Keep this project note.
                dependencies: # Dependency refs are kept here.
                - "alpha@^1.0.0" # remove alpha
                - beta@~2.0.0
                """;
            File.WriteAllText(projectPath, original);

            PackageManager.RemoveDependency(projectPath, "ALPHA");

            var updated = File.ReadAllText(projectPath);
            Assert.Contains("# Keep this project note.", updated, StringComparison.Ordinal);
            Assert.Contains("- beta@~2.0.0", updated, StringComparison.Ordinal);
            Assert.DoesNotContain("alpha@", updated, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(["direct  beta@~2.0.0"], PackageManager.List(projectPath));

            PackageManager.RemoveDependency(projectPath, "beta");

            Assert.Contains("dependencies: [] # Dependency refs are kept here.", File.ReadAllText(projectPath), StringComparison.Ordinal);
            Assert.Empty(PackageManager.List(projectPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AddAndRemoveDependencySupportInlineYamlList()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-inline-dependency-test-").FullName;
        try
        {
            var projectPath = Path.Combine(root, "consumer.cxproj");
            File.WriteAllText(projectPath, """
                name: consumer
                version: 0.1.0
                dependencies: [alpha@^1.0.0, beta@~2.0.0] # keep this note
                """);

            PackageManager.AddDependency(projectPath, "gamma@3.0.0");
            PackageManager.RemoveDependency(projectPath, "alpha");

            var updated = File.ReadAllText(projectPath);
            Assert.Contains("dependencies: # keep this note", updated, StringComparison.Ordinal);
            Assert.Equal(
                ["direct  beta@~2.0.0", "direct  gamma@3.0.0"],
                PackageManager.List(projectPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CleanRemovesLockedPackagesFromHiddenFolderAndPreservesUntrackedFiles()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-clean-test-").FullName;
        try
        {
            var repository = Path.Combine(root, "feed");
            var packageDirectory = Path.Combine(root, "library");
            var consumerDirectory = Path.Combine(root, "consumer");
            PublishSimplePackage(repository, packageDirectory, "library", "1.0.0", []);
            Directory.CreateDirectory(consumerDirectory);
            File.WriteAllText(Path.Combine(consumerDirectory, "consumer.cxproj"), """
                name: consumer
                version: 0.1.0
                dependencies:
                - library@1.0.0
                """);

            var restored = PackageManager.Restore(consumerDirectory, repository);
            var packagePath = Path.Combine(restored.PackagesDirectory, "library", "1.0.0");
            var untrackedPath = Path.Combine(restored.PackagesDirectory, "notes.txt");
            File.WriteAllText(untrackedPath, "keep me");

            Assert.Equal(Path.Combine(consumerDirectory, ".packages"), restored.PackagesDirectory);
            Assert.True(Directory.Exists(packagePath));

            PackageManager.Clean(consumerDirectory);

            Assert.False(Directory.Exists(packagePath));
            Assert.Equal("keep me", File.ReadAllText(untrackedPath));
            Assert.True(Directory.Exists(restored.PackagesDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string PublishSimplePackage(string repository, string directory, string name, string version,
        string[] dependencies)
    {
        Directory.CreateDirectory(directory);
        var dependencyYaml = dependencies.Length == 0
            ? "dependencies: []"
            : "dependencies:\n" + string.Join("\n", dependencies.Select(dependency => $"- {dependency}"));
        File.WriteAllText(Path.Combine(directory, $"{name}.cxproj"), $"""
            name: {name}
            version: {version}
            package:
              sources: []
              binary: []
            {dependencyYaml}
            """);
        return PackageManager.Publish(directory, repository);
    }

    private static void CreatePackageProject(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "sample.cxproj"), """
            name: sample
            version: 1.0.0
            package:
              sources:
              - '*.cx'
              binary:
              - win-x64
              - linux-x64
            dependencies: []
            """);
        File.WriteAllText(Path.Combine(directory, "main.cx"), "source");
        File.WriteAllText(Path.Combine(directory, "ignored.txt"), "not selected");
        Directory.CreateDirectory(Path.Combine(directory, "binary", "win-x64"));
        Directory.CreateDirectory(Path.Combine(directory, "binary", "linux-x64"));
        File.WriteAllText(Path.Combine(directory, "binary", "win-x64", "sample.dll"), "windows binary");
        File.WriteAllText(Path.Combine(directory, "binary", "linux-x64", "sample.so"), "linux binary");
    }
}
