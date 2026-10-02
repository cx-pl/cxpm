using Cxpm.Core;
using Xunit;

namespace Cxpm.Tests;

public sealed class RepositoryConfigurationTests
{
    [Fact]
    public void RelativeFeedPathResolvesFromProjectDirectory()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-repository-config-test-").FullName;
        try
        {
            var projectDirectory = Path.Combine(root, "project");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "app.cxproj"), """
                name: app
                version: 0.1.0
                feeds:
                  - ../feed
                dependencies: []
                """);

            var feeds = PackageManager.ResolveFeeds(projectDirectory);

            Assert.Equal(new[] { Path.Combine(root, "feed") }, feeds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CommandLineFeedOverridesProjectFeeds()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-repository-override-test-").FullName;
        try
        {
            var projectDirectory = Path.Combine(root, "project");
            Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, "app.cxproj"), """
                name: app
                version: 0.1.0
                feeds:
                  - ../configured-feed
                dependencies: []
                """);
            var overridePath = Path.Combine(root, "override-feed");

            var feeds = PackageManager.ResolveFeeds(projectDirectory, overridePath);

            Assert.Equal(new[] { overridePath }, feeds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HttpFeedIsKeptAsNormalizedUrl()
    {
        var root = Directory.CreateTempSubdirectory("cxpm-http-repository-config-test-").FullName;
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "app.cxproj"), """
                name: app
                version: 0.1.0
                feeds:
                  - https://packages.example/cxpm/
                dependencies: []
                """);

            var feeds = PackageManager.ResolveFeeds(root);

            Assert.Equal(new[] { "https://packages.example/cxpm" }, feeds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
