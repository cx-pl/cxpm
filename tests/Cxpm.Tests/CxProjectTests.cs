using Cxpm.Core;
using Cxpm.Core.Models;
using Xunit;

namespace Cxpm.Tests;

public sealed class CxProjectTests
{
    [Theory]
    [InlineData("bad_name")]
    [InlineData("_Leading")]
    [InlineData("Trailing_")]
    public void RejectsUnderscoresInProjectNames(string name)
    {
        var error = Assert.Throws<CxpmException>(() =>
            CxProject.Parse($"name: {name}\nversion: 1.0.0\n", "sample.cxproj"));
        Assert.Contains(name, error.Message);
        Assert.Contains("underscores", error.Message);
    }

    [Fact]
    public void PreservesStructuredNativeLibraryRequirements()
    {
        var project = CxProject.Parse("""
            name: cxx11
            version: 1.0.0
            native_libraries:
              x11:
                provider: sdk
                visibility: public
                linux:
                  architectures: [x64]
                  pkg_config: x11
                  headers: [X11/Xlib.h]
                  runtime_artifacts: []
                  linkage: shared
            """);
        var library = project.NativeLibraries["x11"];
        Assert.Equal("x11", library.Linux!.PkgConfig);
        Assert.Equal("X11/Xlib.h", Assert.Single(library.Linux.Headers));
        Assert.Equal("x64", Assert.Single(library.Linux.Architectures));
    }
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
