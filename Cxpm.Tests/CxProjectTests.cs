using Cxpm.Core;
using Cxpm.Core.Models;
using Xunit;

namespace Cxpm.Tests;

public sealed class CxProjectTests
{
    [Fact]
    public void ParsesMultipleRuntimeTargets()
    {
        var project = CxProject.Parse("""
            name: app
            version: 1.0.0
            targets:
            - win-x64
            - linux-arm64
            """);

        Assert.Equal(["win-x64", "linux-arm64"], project.Targets);
    }

    [Fact]
    public void RejectsMalformedRuntimeTargets()
    {
        var error = Assert.Throws<CxpmException>(() => CxProject.Parse("""
            name: app
            version: 1.0.0
            targets:
            - linux/x64
            """));

        Assert.Contains("Invalid RID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAssetSelectionsUntilAssetSupportIsReleased()
    {
        var error = Assert.Throws<CxpmException>(() => CxProject.Parse("""
            name: app
            version: 1.0.0
            package:
              assets:
              - images/*.png
            """));

        Assert.Contains("package.assets", error.Message, StringComparison.Ordinal);
    }
}
