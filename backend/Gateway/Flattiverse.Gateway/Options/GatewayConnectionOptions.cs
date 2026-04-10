using Flattiverse.Connector.GalaxyHierarchy;

namespace Flattiverse.Gateway.Options;

public sealed class GatewayConnectionOptions
{
    public const string SectionPath = "Gateway";

    public string FlattiverseGalaxyUrl { get; set; } = "wss://www.flattiverse.com/galaxies/2/api";
    public string? RuntimeDisclosure { get; set; }
    public string? BuildDisclosure { get; set; }

    public RuntimeDisclosure? CreateRuntimeDisclosure()
    {
        if (string.IsNullOrWhiteSpace(RuntimeDisclosure))
            return null;

        var normalized = RuntimeDisclosure.Trim();
        if (normalized.Length != RuntimeDisclosureSize)
            throw new InvalidOperationException(
                $"Gateway runtime disclosure must be a {RuntimeDisclosureSize}-character hexadecimal string.");

        return new RuntimeDisclosure(
            ParseRuntimeLevel(normalized[0]),
            ParseRuntimeLevel(normalized[1]),
            ParseRuntimeLevel(normalized[2]),
            ParseRuntimeLevel(normalized[3]),
            ParseRuntimeLevel(normalized[4]),
            ParseRuntimeLevel(normalized[5]),
            ParseRuntimeLevel(normalized[6]),
            ParseRuntimeLevel(normalized[7]),
            ParseRuntimeLevel(normalized[8]),
            ParseRuntimeLevel(normalized[9]));
    }

    public BuildDisclosure? CreateBuildDisclosure()
    {
        if (string.IsNullOrWhiteSpace(BuildDisclosure))
            return null;

        var normalized = BuildDisclosure.Trim();
        if (normalized.Length != BuildDisclosureSize)
            throw new InvalidOperationException(
                $"Gateway build disclosure must be a {BuildDisclosureSize}-character hexadecimal string.");

        return new BuildDisclosure(
            ParseBuildLevel(normalized[0]),
            ParseBuildLevel(normalized[1]),
            ParseBuildLevel(normalized[2]),
            ParseBuildLevel(normalized[3]),
            ParseBuildLevel(normalized[4]),
            ParseBuildLevel(normalized[5]),
            ParseBuildLevel(normalized[6]),
            ParseBuildLevel(normalized[7]),
            ParseBuildLevel(normalized[8]),
            ParseBuildLevel(normalized[9]),
            ParseBuildLevel(normalized[10]),
            ParseBuildLevel(normalized[11]));
    }

    private const int RuntimeDisclosureSize = 10;
    private const int BuildDisclosureSize = 12;

    private static RuntimeDisclosureLevel ParseRuntimeLevel(char value)
    {
        var nibble = ParseHexNibble(value);
        if (nibble > (int)RuntimeDisclosureLevel.AiControlled)
            throw new InvalidOperationException($"Unsupported runtime disclosure nibble '{value}'.");

        return (RuntimeDisclosureLevel)nibble;
    }

    private static BuildDisclosureLevel ParseBuildLevel(char value)
    {
        var nibble = ParseHexNibble(value);
        if (nibble > (int)BuildDisclosureLevel.AgenticTool)
            throw new InvalidOperationException($"Unsupported build disclosure nibble '{value}'.");

        return (BuildDisclosureLevel)nibble;
    }

    private static int ParseHexNibble(char value)
    {
        if (value >= '0' && value <= '9')
            return value - '0';

        if (value >= 'a' && value <= 'f')
            return value - 'a' + 10;

        if (value >= 'A' && value <= 'F')
            return value - 'A' + 10;

        throw new InvalidOperationException($"Invalid disclosure nibble '{value}'.");
    }
}
