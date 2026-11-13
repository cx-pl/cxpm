using Cxpm.Core.Models;
using Xunit;

namespace Cxpm.Tests;

public sealed class PackageVersionRangeTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.2", false)]
    [InlineData("1.2.3", "1.2.4", false)]
    [InlineData("1.2.3", "1.2.3-alpha.1", false)]
    public void ExactVersionMatchesOnlyThatVersion(string rangeText, string versionText, bool expected)
    {
        Assert.Equal(expected, ParseRange(rangeText).Contains(SemVersion.Parse(versionText)));
    }

    [Theory]
    [InlineData("^1.2.3", "1.2.3", true)]
    [InlineData("^1.2.3", "1.9.9", true)]
    [InlineData("^1.2.3", "1.2.2", false)]
    [InlineData("^1.2.3", "2.0.0", false)]
    [InlineData("^0.2.3", "0.2.9", true)]
    [InlineData("^0.2.3", "0.3.0", false)]
    [InlineData("^0.0.3", "0.0.3", true)]
    [InlineData("^0.0.3", "0.0.4", false)]
    public void CaretRangeUsesSemVerCompatibleUpperBound(string rangeText, string versionText, bool expected)
    {
        Assert.Equal(expected, ParseRange(rangeText).Contains(SemVersion.Parse(versionText)));
    }

    [Theory]
    [InlineData("~1.2.3", "1.2.3", true)]
    [InlineData("~1.2.3", "1.2.99", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData("~1.2.3", "1.2.2", false)]
    public void TildeRangeKeepsTheMajorAndMinor(string rangeText, string versionText, bool expected)
    {
        Assert.Equal(expected, ParseRange(rangeText).Contains(SemVersion.Parse(versionText)));
    }

    [Theory]
    [InlineData("[1.2,2.0)", "1.2.0", true)]
    [InlineData("[1.2,2.0)", "1.9.9", true)]
    [InlineData("[1.2,2.0)", "2.0.0", false)]
    [InlineData("(1.2.3,2.0.0]", "1.2.3", false)]
    [InlineData("(1.2.3,2.0.0]", "1.2.4", true)]
    [InlineData("(1.2.3,2.0.0]", "2.0.0", true)]
    [InlineData("[1.2]", "1.2.0", true)]
    [InlineData("[1.2,)", "9.0.0", true)]
    public void BracketRangesRespectBoundsAndInclusivity(string rangeText, string versionText, bool expected)
    {
        Assert.Equal(expected, ParseRange(rangeText).Contains(SemVersion.Parse(versionText)));
    }

    [Theory]
    [InlineData("*", "0.0.1", true)]
    [InlineData("*", "99.0.0", true)]
    [InlineData("1.*", "1.0.0", true)]
    [InlineData("1.*", "1.99.0", true)]
    [InlineData("1.*", "2.0.0", false)]
    [InlineData("1.2.*", "1.2.99", true)]
    [InlineData("1.2.*", "1.3.0", false)]
    [InlineData("1.1.*", "1.1.2-alpha", false)]
    [InlineData("1.1.*-*", "1.1.2-alpha", true)]
    [InlineData("1.1.*-*", "1.2.0-beta", false)]
    [InlineData("1.1.*-*", "1.1.1", true)]
    [InlineData("*-*", "9.9.9-beta.1", true)]
    [InlineData("1.2.0-rc.*", "1.2.0-rc.1", true)]
    [InlineData("1.2.0-rc.*", "1.2.0-rc.2.1", true)]
    [InlineData("1.2.0-rc.*", "1.2.0-beta.1", false)]
    [InlineData("1.2.0-rc.*", "1.2.1-rc.1", false)]
    [InlineData("1.2.0-rc.*", "1.2.0", true)]
    public void FloatingRangesMatchTheirVersionPrefix(string rangeText, string versionText, bool expected)
    {
        Assert.True(ParseRange(rangeText).IsFloating);
        Assert.Equal(expected, ParseRange(rangeText).Contains(SemVersion.Parse(versionText)));
    }

    [Fact]
    public void PrereleaseIsIncludedOnlyWhenRangeMentionsPrerelease()
    {
        Assert.False(ParseRange("1.2.3").Contains(SemVersion.Parse("1.2.3-alpha.1")));
        Assert.True(ParseRange("^1.2.3-alpha.1").Contains(SemVersion.Parse("1.2.3-alpha.2")));
        Assert.False(ParseRange("^1.2.3-alpha.1").Contains(SemVersion.Parse("1.2.3-alpha.0")));
        Assert.True(ParseRange("^1.2.3").Contains(SemVersion.Parse("1.2.4-alpha.1"), includePrerelease: true));
    }

    [Theory]
    [InlineData("[1.2,2.0")]
    [InlineData("[2.0,1.0]")]
    [InlineData("(1.0,1.0]")]
    [InlineData("(,)")]
    [InlineData("1.2")]
    [InlineData("^1.2")]
    public void InvalidRangesAreRejected(string rangeText)
    {
        Assert.Throws<CxpmException>(() => PackageVersionRange.Parse(rangeText));
    }

    private static PackageVersionRange ParseRange(string text) => PackageVersionRange.Parse(text);
}
