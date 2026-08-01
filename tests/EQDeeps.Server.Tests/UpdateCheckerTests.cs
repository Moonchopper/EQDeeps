using EQDeeps.Server;
using Xunit;

namespace EQDeeps.Server.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("v1.0.0", "0.9.9", true)]
    [InlineData("v0.1.0", "0.1.0", false)]
    [InlineData("v0.1.0", "0.2.0", false)]
    [InlineData("v0.2.0-rc.1", "0.1.0", true)]
    [InlineData("v0.1.0+build5", "0.1.0", false)]
    [InlineData("garbage", "0.1.0", false)]
    [InlineData("v2", "1.0.0", true)]
    public void IsNewerComparesSemverishTags(string tag, string current, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(tag, current));
    }
}
