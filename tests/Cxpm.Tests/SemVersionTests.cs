using Cxpm.Core.Models;
using Xunit;

namespace Cxpm.Tests;

public sealed class SemVersionTests
{
    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.0.0-1", "1.0.0-alpha")]
    public void ComparisonFollowsSemVerPrereleasePrecedence(string lower, string higher)
    {
        Assert.True(SemVersion.Parse(lower).CompareTo(SemVersion.Parse(higher)) < 0);
        Assert.True(SemVersion.Parse(higher).CompareTo(SemVersion.Parse(lower)) > 0);
    }

    [Fact]
    public void NumericPrereleaseIdentifiersCompareNumerically()
    {
        Assert.True(SemVersion.Parse("1.0.0-alpha.2").CompareTo(SemVersion.Parse("1.0.0-alpha.10")) < 0);
    }

    [Fact]
    public void BuildMetadataDoesNotChangePrecedenceButIsPreserved()
    {
        var left = SemVersion.Parse("1.2.3+build.1");
        var right = SemVersion.Parse("1.2.3+build.2");

        Assert.Equal(0, left.CompareTo(right));
        Assert.NotEqual(left, right);
        Assert.Equal("1.2.3+build.1", left.ToString());
    }

    [Fact]
    public void CoreNumbersAreNotLimitedToMachineIntegerSize()
    {
        var lower = SemVersion.Parse("999999999999999999999999.0.0");
        var higher = SemVersion.Parse("1000000000000000000000000.0.0");

        Assert.True(lower.CompareTo(higher) < 0);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-alpha..beta")]
    [InlineData("1.2.3-alpha.01")]
    [InlineData("1.2.3+build..id")]
    [InlineData("v1.2.3")]
    public void InvalidSemVerStringsAreRejected(string text)
    {
        Assert.False(SemVersion.TryParse(text, out _));
    }
}
