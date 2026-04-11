using Flattiverse.Connector.Units;

namespace Flattiverse.Gateway.Connector;

internal static class UnitIdentity
{
    private const string ClusterUnitPrefix = "cluster/";
    private const string ClusterUnitSeparator = "/unit/";

    public static string BuildUnitId(Unit unit, int? clusterIdOverride = null)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (unit is PlayerUnit playerUnit)
            return BuildControllableId(playerUnit.Player.Id, playerUnit.ControllableInfo.Id);

        return BuildClusterUnitId(clusterIdOverride ?? unit.Cluster?.Id ?? 0, unit.Name);
    }

    public static string BuildControllableId(int playerId, int controllableId)
    {
        return $"p{playerId}-c{controllableId}";
    }

    public static string BuildClusterUnitId(int clusterId, string unitName)
    {
        if (string.IsNullOrWhiteSpace(unitName))
            return string.Empty;

        return $"{ClusterUnitPrefix}{clusterId}{ClusterUnitSeparator}{unitName}";
    }

    public static string NormalizeUnitId(string unitIdOrLegacyName, int clusterId)
    {
        if (string.IsNullOrWhiteSpace(unitIdOrLegacyName))
            return string.Empty;

        return IsCanonicalUnitId(unitIdOrLegacyName)
            ? unitIdOrLegacyName
            : BuildClusterUnitId(clusterId, unitIdOrLegacyName);
    }

    public static bool TryParseControllableId(string value, out int playerId, out int controllableId)
    {
        playerId = 0;
        controllableId = 0;

        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('p'))
            return false;

        var separatorIndex = value.IndexOf("-c", StringComparison.Ordinal);
        if (separatorIndex <= 1 || separatorIndex + 2 >= value.Length)
            return false;

        if (!int.TryParse(value.AsSpan(1, separatorIndex - 1), out playerId))
            return false;

        return int.TryParse(value.AsSpan(separatorIndex + 2), out controllableId);
    }

    private static bool IsCanonicalUnitId(string value)
    {
        return TryParseControllableId(value, out _, out _) || IsClusterUnitId(value);
    }

    private static bool IsClusterUnitId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(ClusterUnitPrefix, StringComparison.Ordinal))
            return false;

        var separatorIndex = value.IndexOf(ClusterUnitSeparator, ClusterUnitPrefix.Length, StringComparison.Ordinal);
        if (separatorIndex <= ClusterUnitPrefix.Length || separatorIndex + ClusterUnitSeparator.Length >= value.Length)
            return false;

        return int.TryParse(value.AsSpan(ClusterUnitPrefix.Length, separatorIndex - ClusterUnitPrefix.Length), out _);
    }
}
