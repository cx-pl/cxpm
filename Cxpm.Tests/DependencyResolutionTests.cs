using Cxpm.Core;
using Cxpm.Core.Models;
using Xunit;

namespace Cxpm.Tests;

public sealed class DependencyResolutionTests
{
    [Fact]
    public void NonFloatingRangeSelectsLowestApplicableVersion()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-lowest-test-").FullName;
        try
        {
            var repository = Path.Combine(root, "feed");
            PublishPackage(repository, "shared", "1.0.0");
            PublishPackage(repository, "shared", "1.5.0");
            PublishPackage(repository, "shared", "2.0.0");
            var consumer = CreateConsumer(root, "shared@^1.0.0");

            PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64");

            Assert.Contains("resolved shared@1.0.0", PackageManager.List(consumer));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FloatingRangeSelectsHighestMatchingVersion()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-floating-test-").FullName;
        try
        {
            var repository = Path.Combine(root, "feed");
            PublishPackage(repository, "shared", "1.0.0");
            PublishPackage(repository, "shared", "1.9.0");
            PublishPackage(repository, "shared", "1.9.1-beta.1");
            PublishPackage(repository, "shared", "2.0.0");
            var consumer = CreateConsumer(root, "shared@1.*");

            PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64");

            Assert.Contains("resolved shared@1.9.0", PackageManager.List(consumer));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FloatingPrereleasePatternSelectsHighestMatchingPrerelease()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-floating-prerelease-test-").FullName;
        try
        {
            var repository = Path.Combine(root, "feed");
            PublishPackage(repository, "shared", "1.8.0");
            PublishPackage(repository, "shared", "1.9.1-beta.1");
            PublishPackage(repository, "shared", "2.0.0-beta.1");
            var consumer = CreateConsumer(root, "shared@1.*-*");

            PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64");

            Assert.Contains("resolved shared@1.9.1-beta.1", PackageManager.List(consumer));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PrereleaseLabelFloatCanSelectMatchingStableVersion()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-prerelease-label-test-").FullName;
        try
        {
            var repository = Path.Combine(root, "feed");
            PublishPackage(repository, "shared", "1.2.0-rc.2");
            PublishPackage(repository, "shared", "1.2.0-rc.3");
            PublishPackage(repository, "shared", "1.2.0");
            PublishPackage(repository, "shared", "1.2.1-rc.1");
            var consumer = CreateConsumer(root, "shared@1.2.0-rc.*");

            PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64");

            Assert.Contains("resolved shared@1.2.0", PackageManager.List(consumer));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PrereleaseRequestInGraphAllowsMatchingPrereleaseForCousinRange()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-prerelease-graph-test-").FullName;
        try
        {
            var repository = Path.Combine(root, "feed");
            PublishPackage(repository, "shared", "1.1.0");
            PublishPackage(repository, "shared", "1.2.0-beta.1");
            PublishPackage(repository, "alpha", "1.0.0", "shared@^1.0.0");
            PublishPackage(repository, "beta", "1.0.0", "shared@1.2.0-beta.1");
            var consumer = CreateConsumer(root, "alpha@1.0.0", "beta@1.0.0");

            PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64");

            Assert.Contains("resolved shared@1.2.0-beta.1", PackageManager.List(consumer));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DirectDependencyWinsOverTransitiveRange()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-direct-wins-test-").FullName;
        try
        {
            var repository = Path.Combine(root, "feed");
            PublishPackage(repository, "shared", "1.0.0");
            PublishPackage(repository, "shared", "2.0.0");
            PublishPackage(repository, "bridge", "1.0.0", "shared@^1.0.0");
            var consumer = CreateConsumer(root, "bridge@1.0.0", "shared@2.0.0");

            PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64");

            var resolved = PackageManager.List(consumer);
            Assert.Contains("resolved shared@2.0.0", resolved);
            Assert.DoesNotContain("resolved shared@1.0.0", resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CousinDependencyConflictReportsBothPaths()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-cousin-conflict-test-").FullName;
        try
        {
            var repository = Path.Combine(root, "feed");
            PublishPackage(repository, "shared", "1.0.0");
            PublishPackage(repository, "shared", "2.0.0");
            PublishPackage(repository, "alpha", "1.0.0", "shared@^1.0.0");
            PublishPackage(repository, "beta", "1.0.0", "shared@^2.0.0");
            var consumer = CreateConsumer(root, "alpha@1.0.0", "beta@1.0.0");

            var error = Assert.Throws<CxpmException>(() =>
                PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64"));

            Assert.Contains("consumer -> alpha@1.0.0 -> shared@^1.0.0", error.Message);
            Assert.Contains("consumer -> beta@1.0.0 -> shared@^2.0.0", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DependenciesOfSupersededPackageVersionDoNotBlockRestore()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-superseded-version-test-").FullName;
        try
        {
            var repository = Path.Combine(root, "feed");
            PublishPackage(repository, "shared", "1.0.0", "obsolete@1.0.0");
            PublishPackage(repository, "shared", "2.0.0");
            PublishPackage(repository, "alpha", "1.0.0", "shared@[1.0.0,3.0.0)");
            PublishPackage(repository, "beta", "1.0.0", "shared@2.0.0");
            var consumer = CreateConsumer(root, "alpha@1.0.0", "beta@1.0.0");

            PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64");

            var resolved = PackageManager.List(consumer);
            Assert.Contains("resolved shared@2.0.0", resolved);
            Assert.DoesNotContain(resolved, entry => entry.StartsWith("resolved obsolete@", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConstraintsFromSupersededVersionsDoNotConflictWithActiveBranch()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-stale-constraint-test-").FullName;
        try
        {
            var repository = Path.Combine(root, "feed");
            PublishPackage(repository, "shared", "1.0.0", "ghost@2.0.0");
            PublishPackage(repository, "shared", "2.0.0");
            PublishPackage(repository, "ghost", "1.0.0");
            PublishPackage(repository, "ghost", "2.0.0");
            PublishPackage(repository, "alpha", "1.0.0", "shared@[1.0.0,3.0.0)");
            PublishPackage(repository, "beta", "1.0.0", "shared@2.0.0", "ghost@1.0.0");
            var consumer = CreateConsumer(root, "alpha@1.0.0", "beta@1.0.0");

            PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64");

            var resolved = PackageManager.List(consumer);
            Assert.Contains("resolved shared@2.0.0", resolved);
            Assert.Contains("resolved ghost@1.0.0", resolved);
            Assert.DoesNotContain("resolved ghost@2.0.0", resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdatePrunesPackageVersionNoLongerInTheLockFile()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-prune-test-").FullName;
        try
        {
            var repository = Path.Combine(root, "feed");
            PublishPackage(repository, "shared", "1.0.0");
            PublishPackage(repository, "shared", "2.0.0");
            var consumer = CreateConsumer(root, "shared@^1.0.0");

            var firstRestore = PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64");
            var oldVersionDirectory = Path.Combine(firstRestore.PackagesDirectory, "shared", "1.0.0");
            Assert.True(Directory.Exists(oldVersionDirectory));

            var projectFile = Path.Combine(consumer, "consumer.cxproj");
            File.WriteAllText(projectFile, File.ReadAllText(projectFile).Replace("^1.0.0", "^2.0.0", StringComparison.Ordinal));
            PackageManager.Restore(consumer, repository, update: true, runtimeIdentifier: "win-x64");

            Assert.False(Directory.Exists(oldVersionDirectory));
            Assert.True(Directory.Exists(Path.Combine(firstRestore.PackagesDirectory, "shared", "2.0.0")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingTransitivePackageReportsTheDependencyPath()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-resolution-test-").FullName;
        try
        {
            var repository = Path.Combine(root, "feed");
            var library = Path.Combine(root, "library");
            var consumer = Path.Combine(root, "consumer");
            Directory.CreateDirectory(library);
            Directory.CreateDirectory(consumer);

            File.WriteAllText(Path.Combine(library, "library.cxproj"), """
                name: library
                version: 1.0.0
                dependencies:
                - missing@^2.0.0
                """);
            PackageManager.Publish(library, repository);

            File.WriteAllText(Path.Combine(consumer, "consumer.cxproj"), """
                name: consumer
                version: 0.1.0
                dependencies:
                - library@1.0.0
                """);

            var error = Assert.Throws<CxpmException>(() =>
                PackageManager.Restore(consumer, repository, runtimeIdentifier: "win-x64"));

            Assert.Contains("consumer -> library@1.0.0 -> missing@^2.0.0", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PublishPackage(string repository, string name, string version,
        params string[] dependencies)
    {
        var projectDirectory = Path.Combine(Path.GetDirectoryName(repository)!, $"{name}-{version}");
        Directory.CreateDirectory(projectDirectory);
        var dependencyYaml = dependencies.Length == 0
            ? "dependencies: []"
            : "dependencies:\n" + string.Join("\n", dependencies.Select(dependency => $"- {dependency}"));
        File.WriteAllText(Path.Combine(projectDirectory, $"{name}.cxproj"),
            $"name: {name}\nversion: {version}\n{dependencyYaml}\n");
        PackageManager.Publish(projectDirectory, repository);
    }

    private static string CreateConsumer(string root, params string[] dependencies)
    {
        var directory = Path.Combine(root, "consumer");
        Directory.CreateDirectory(directory);
        var dependencyYaml = "dependencies:\n" + string.Join("\n", dependencies.Select(dependency => $"- {dependency}"));
        File.WriteAllText(Path.Combine(directory, "consumer.cxproj"),
            $"name: consumer\nversion: 0.1.0\n{dependencyYaml}\n");
        return directory;
    }
}
