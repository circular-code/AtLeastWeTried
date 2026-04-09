namespace Flattiverse.Gateway.Options;

/// <summary>
/// Configuration for navigation / circular path planning (see appsettings <c>Gateway:Pathfinding</c>).
/// </summary>
public sealed class PathfindingOptions
{
    public const string SectionPath = "Gateway:Pathfinding";

    /// <summary>
    /// When true, emits structured Information logs for replan decisions, planner results, and navigation goals.
    /// </summary>
    public bool EnableLogging { get; set; }
}
