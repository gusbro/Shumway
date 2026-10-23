using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class AddressMapTests
{
    [Fact]
    public void FindsWhatWasAdded_ThroughGrowth()
    {
        var map = new AddressMap();
        for (int k = 0; k < 10_000; k++) map.Add(k * 7, k);
        Assert.Equal(10_000, map.Count);
        for (int k = 0; k < 10_000; k++)
        {
            Assert.True(map.TryGetValue(k * 7, out int v));
            Assert.Equal(k, v);
        }
        Assert.False(map.TryGetValue(3, out _));
    }

    [Fact]
    public void KeysMayBeNegative()
    {
        var map = new AddressMap();
        map.Add(~5, 1);
        map.Add(5, 2);
        Assert.True(map.TryGetValue(~5, out int a));
        Assert.True(map.TryGetValue(5, out int b));
        Assert.Equal((1, 2), (a, b));
    }

    [Fact]
    public void ClearEmptiesIt()
    {
        var map = new AddressMap();
        for (int round = 0; round < 3; round++)
        {
            for (int k = 0; k < 100; k++) map.Add(k, round);
            Assert.True(map.TryGetValue(42, out int v));
            Assert.Equal(round, v);
            map.Clear();
            Assert.Equal(0, map.Count);
            Assert.False(map.TryGetValue(42, out _));
        }
    }
}
