using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class Par2RepairConfigTests
{
    private static ConfigManager ConfigWith(params (string Name, string Value)[] items)
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(items
            .Select(x => new ConfigItem { ConfigName = x.Name, ConfigValue = x.Value })
            .ToList());
        return configManager;
    }

    [Fact]
    public void Par2RepairIsDisabledWhenUnconfigured()
    {
        // the master toggle ships OFF: an existing install must not start
        // spending bandwidth on par2 downloads because it upgraded.
        Assert.False(ConfigWith().IsPar2RepairEnabled());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("garbage", false)]
    public void Par2RepairTogglesOnConfiguredValue(string configured, bool expected)
    {
        var configManager = ConfigWith(("repair.par2.enable", configured));
        Assert.Equal(expected, configManager.IsPar2RepairEnabled());
    }

    [Fact]
    public void Par2FallbackDefaultsToArrResearchWhenUnconfigured()
    {
        // arr-research is today's behaviour: an unhealthy item is removed and
        // Radarr/Sonarr searches for a replacement. Anything else would change
        // what existing installs do the moment Phase 4 starts consuming this.
        Assert.Equal(Par2Fallback.ArrResearch, ConfigWith().GetPar2Fallback());
    }

    [Theory]
    [InlineData("arr-research", Par2Fallback.ArrResearch)]
    [InlineData("mark-only", Par2Fallback.MarkOnly)]
    [InlineData("delete", Par2Fallback.Delete)]
    [InlineData("ARR-RESEARCH", Par2Fallback.ArrResearch)]
    [InlineData("", Par2Fallback.ArrResearch)]
    [InlineData("nonsense", Par2Fallback.ArrResearch)]
    public void Par2FallbackParsesConfiguredValue(string configured, Par2Fallback expected)
    {
        var configManager = ConfigWith(("repair.par2.fallback", configured));
        Assert.Equal(expected, configManager.GetPar2Fallback());
    }

    [Theory]
    [InlineData(null, 0L)]           // absent → unlimited
    [InlineData("", 0L)]
    [InlineData("0", 0L)]            // explicit unlimited
    [InlineData("1073741824", 1073741824L)]
    [InlineData("-5", 0L)]           // negative is meaningless → unlimited
    [InlineData("not-a-number", 0L)]
    public void Par2MaxStorageBytesFallsBackToUnlimited(string? configured, long expected)
    {
        var configManager = configured == null
            ? ConfigWith()
            : ConfigWith(("repair.par2.max-storage-bytes", configured));
        Assert.Equal(expected, configManager.GetPar2MaxStorageBytes());
    }

    [Theory]
    [InlineData(null, 1)]            // absent → one repair at a time
    [InlineData("", 1)]
    [InlineData("3", 3)]
    [InlineData("0", 1)]             // zero would stall repairs entirely
    [InlineData("-2", 1)]
    [InlineData("not-a-number", 1)]
    public void Par2MaxConcurrentRepairsNeverDropsBelowOne(string? configured, int expected)
    {
        var configManager = configured == null
            ? ConfigWith()
            : ConfigWith(("repair.par2.max-concurrent", configured));
        Assert.Equal(expected, configManager.GetPar2MaxConcurrentRepairs());
    }

}
