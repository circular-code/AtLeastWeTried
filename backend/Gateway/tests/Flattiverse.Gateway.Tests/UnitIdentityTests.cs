using Flattiverse.Gateway.Connector;

namespace Flattiverse.Gateway.Tests;

public sealed class UnitIdentityTests
{
    [Fact]
    public void Controllable_ids_round_trip_through_the_shared_parser()
    {
        var controllableId = UnitIdentity.BuildControllableId(playerId: 7, controllableId: 12);

        Assert.Equal("p7-c12", controllableId);
        Assert.True(UnitIdentity.TryParseControllableId(controllableId, out var playerId, out var parsedControllableId));
        Assert.Equal(7, playerId);
        Assert.Equal(12, parsedControllableId);
    }

    [Fact]
    public void Legacy_named_units_are_normalized_into_cluster_scoped_ids()
    {
        var normalized = UnitIdentity.NormalizeUnitId("Ancient Sun", clusterId: 5);

        Assert.Equal("cluster/5/unit/Ancient Sun", normalized);
    }

    [Fact]
    public void Canonical_unit_ids_are_preserved_during_normalization()
    {
        Assert.Equal("p2-c4", UnitIdentity.NormalizeUnitId("p2-c4", clusterId: 9));
        Assert.Equal("cluster/3/unit/Nebula", UnitIdentity.NormalizeUnitId("cluster/3/unit/Nebula", clusterId: 9));
    }
}
