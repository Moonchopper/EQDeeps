using EQDeeps.Core.Maps;
using Xunit;

namespace EQDeeps.Core.Tests;

public class ZoneErasTests
{
    [Fact]
    public void ListsExpansionsInReleaseOrder()
    {
        var ids = ZoneEras.All.Select(e => e.Id).ToList();

        Assert.Equal("classic", ids[0]);
        Assert.True(ids.IndexOf("kunark") < ids.IndexOf("velious"));
        Assert.True(ids.IndexOf("velious") < ids.IndexOf("luclin"));
        Assert.True(ids.IndexOf("luclin") < ids.IndexOf("pop"));
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Years never go backwards, which is the cheap check that the list
        // was typed in order.
        Assert.Equal(ZoneEras.All.Select(e => e.Year), ZoneEras.All.Select(e => e.Year).OrderBy(y => y));
    }

    /// <summary>
    /// A zone exists on a server that has reached its era or a later one, and
    /// anything unknown — on either side — is not a reason to hide it.
    /// </summary>
    [Fact]
    public void WithinComparesByReleaseOrderAndKeepsTheUnknown()
    {
        Assert.True(ZoneEras.Within("classic", "classic"));
        Assert.True(ZoneEras.Within("classic", "velious"));
        Assert.False(ZoneEras.Within("velious", "classic"));
        Assert.False(ZoneEras.Within("pop", "kunark"));

        // A zone the table could not place is shown under every filter.
        Assert.True(ZoneEras.Within(null, "classic"));

        // No filter, or one this build does not recognise, is no filter.
        Assert.True(ZoneEras.Within("tob", null));
        Assert.True(ZoneEras.Within("tob", "atlantis"));

        // A zone era this build does not recognise is treated as unknown.
        Assert.True(ZoneEras.Within("atlantis", "classic"));
    }

    [Fact]
    public void FindsByIdRegardlessOfCase()
    {
        Assert.Equal("The Planes of Power", ZoneEras.Find("pop")!.Name);
        Assert.Equal("pop", ZoneEras.Find("PoP")!.Id);
        Assert.Null(ZoneEras.Find("atlantis"));
        Assert.Null(ZoneEras.Find(null));
    }
}
