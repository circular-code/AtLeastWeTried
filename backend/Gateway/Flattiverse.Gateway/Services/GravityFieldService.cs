using System.Numerics;
using Flattiverse.Connector.Events;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Connector;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Maintains a per-scope gravity field derived from known gravitating map units.
/// Other gateway services can query gravity at arbitrary points.
/// </summary>
public sealed class GravityFieldService : IConnectorEventHandler
{
    private const float MinimumGravityDistanceSquared = 1f;
    private const float MinimumGravityFalloffDistance = 64f;
    private const float MinimumExactSourceRadius = 320f;
    private const float ExactSourceCellCoverage = 1.5f;
    private const float MinimumApproximateContribution = 0.00015f;

    private const float MinimumSpatialCellSize = 128f;
    private const float MaximumSpatialCellSize = 384f;
    private const float SpatialCellPadding = 48f;
    private const int MaximumSpatialCells = 32768;

    private readonly Func<MappingService.MappingScopeContext?> _scopeResolver;
    private readonly Dictionary<GravityScopeKey, ScopeState> _scopeStates = new();
    private readonly object _stateLock = new();

    public GravityFieldService(Func<MappingService.MappingScopeContext?> scopeResolver)
    {
        _scopeResolver = scopeResolver;
    }

    public readonly record struct GravitySample(
        Vector2 Total,
        string? PrimarySourceUnitId,
        Vector2 PrimaryContribution,
        float PrimarySourceDistance,
        int NearbySourceCount,
        int ApproximatedCellCount);

    public void Handle(FlattiverseEvent @event)
    {
        switch (@event)
        {
            case AppearedUnitEvent appeared:
                UpsertSource(appeared.Unit, appeared.Unit.Cluster?.Id ?? 0);
                break;
            case UpdatedUnitEvent updated:
                UpsertSource(updated.Unit, updated.Unit.Cluster?.Id ?? 0);
                break;
            case RemovedUnitEvent removed:
                HandleOutOfScannerRange(removed.Unit, removed.Unit.Cluster?.Id ?? 0);
                break;
        }
    }

    public bool TrySampleGravity(float x, float y, out GravitySample sample)
    {
        var scopeKey = ResolveScopeKey();
        if (scopeKey is null)
        {
            sample = default;
            return false;
        }

        lock (_stateLock)
        {
            var scopeState = GetOrCreateScopeStateUnsafe(scopeKey.Value);
            EnsureFieldBuiltUnsafe(scopeState);
            sample = SampleGravityUnsafe(new Vector2(x, y), scopeState);
            return true;
        }
    }

    public bool TrySampleGravity(int clusterId, float x, float y, out GravitySample sample)
    {
        var scopeKey = ResolveScopeKey(clusterId);
        if (scopeKey is null)
        {
            sample = default;
            return false;
        }

        lock (_stateLock)
        {
            var scopeState = GetOrCreateScopeStateUnsafe(scopeKey.Value);
            EnsureFieldBuiltUnsafe(scopeState);
            sample = SampleGravityUnsafe(new Vector2(x, y), scopeState);
            return true;
        }
    }

    public Vector2 EstimateGravity(float x, float y)
    {
        return TrySampleGravity(x, y, out var sample)
            ? sample.Total
            : Vector2.Zero;
    }

    private void UpsertSource(Unit unit, int clusterId)
    {
        var scopeKey = ResolveScopeKey(clusterId);
        if (scopeKey is null)
            return;

        lock (_stateLock)
        {
            var scopeState = GetOrCreateScopeStateUnsafe(scopeKey.Value);
            var gravityStrength = Math.Max(0f, unit.Gravity);
            if (gravityStrength <= 0f)
            {
                if (scopeState.SourcesByUnitId.Remove(unit.Name))
                    InvalidateScope(scopeState);

                return;
            }

            var radius = Math.Max(0f, unit.Radius);
            var source = new GravitySource(
                unit.Name,
                new Vector2(unit.Position.X, unit.Position.Y),
                radius,
                gravityStrength);

            if (scopeState.SourcesByUnitId.TryGetValue(source.UnitId, out var current) &&
                current.Position == source.Position &&
                current.Radius == source.Radius &&
                current.Strength == source.Strength)
            {
                return;
            }

            scopeState.SourcesByUnitId[source.UnitId] = source;
            InvalidateScope(scopeState);
        }
    }

    private void HandleOutOfScannerRange(Unit unit, int clusterId)
    {
        // RemovedUnitEvent is scanner-visibility based, not authoritative deletion.
        // Keep the last known gravitating source in the field until an authoritative
        // update arrives (for example gravity becomes zero or a new position update).
        _ = unit;
        _ = clusterId;
    }

    private GravityScopeKey? ResolveScopeKey(int? clusterIdOverride = null)
    {
        var scope = _scopeResolver();
        if (scope is null || string.IsNullOrWhiteSpace(scope.Value.GalaxyId))
            return null;

        var clusterId = clusterIdOverride ?? scope.Value.ClusterId;
        return new GravityScopeKey(scope.Value.GalaxyId, clusterId);
    }

    private static void InvalidateScope(ScopeState scopeState)
    {
        scopeState.SourceVersion++;
        scopeState.FieldDirty = true;
    }

    private ScopeState GetOrCreateScopeStateUnsafe(GravityScopeKey scopeKey)
    {
        if (_scopeStates.TryGetValue(scopeKey, out var scopeState))
            return scopeState;

        scopeState = new ScopeState();
        _scopeStates[scopeKey] = scopeState;
        return scopeState;
    }

    private static void EnsureFieldBuiltUnsafe(ScopeState scopeState)
    {
        if (!scopeState.FieldDirty && scopeState.Field.BuiltSourceVersion == scopeState.SourceVersion)
            return;

        scopeState.SourceBuffer.Clear();
        foreach (var source in scopeState.SourcesByUnitId.Values)
            scopeState.SourceBuffer.Add(source);

        BuildField(scopeState.Field, scopeState.SourceBuffer, scopeState.SourceVersion);
        scopeState.FieldDirty = false;
    }

    private static void BuildField(GravityFieldMap field, List<GravitySource> sources, long sourceVersion)
    {
        field.SourceEntries = sources.ToArray();
        field.SourceCount = field.SourceEntries.Length;

        if (field.SourceCount <= 0)
        {
            ResetField(field, sourceVersion);
            return;
        }

        float maximumSourceRadius = 0f;
        float minX = field.SourceEntries[0].Position.X;
        float minY = field.SourceEntries[0].Position.Y;
        float maxX = minX;
        float maxY = minY;
        for (var sourceIndex = 0; sourceIndex < field.SourceCount; sourceIndex++)
        {
            var source = field.SourceEntries[sourceIndex];
            maximumSourceRadius = Math.Max(maximumSourceRadius, source.Radius);
            minX = Math.Min(minX, source.Position.X);
            minY = Math.Min(minY, source.Position.Y);
            maxX = Math.Max(maxX, source.Position.X);
            maxY = Math.Max(maxY, source.Position.Y);
        }

        var cellSize = Math.Clamp((maximumSourceRadius * 1.25f) + SpatialCellPadding, MinimumSpatialCellSize, MaximumSpatialCellSize);
        var expandedForCellBudget = false;
        int minCellX;
        int minCellY;
        int width;
        int height;

        while (true)
        {
            minCellX = (int)MathF.Floor(minX / cellSize);
            minCellY = (int)MathF.Floor(minY / cellSize);
            var maxCellX = (int)MathF.Floor(maxX / cellSize);
            var maxCellY = (int)MathF.Floor(maxY / cellSize);
            width = Math.Max(1, (maxCellX - minCellX) + 1);
            height = Math.Max(1, (maxCellY - minCellY) + 1);
            if (((long)width * height) <= MaximumSpatialCells || cellSize >= 8192f)
                break;

            cellSize *= 1.5f;
            expandedForCellBudget = true;
        }

        _ = expandedForCellBudget;

        var totalCellCount = Math.Max(1, width * height);
        EnsureCellCapacity(field, totalCellCount);
        EnsureSourceLinkCapacity(field, field.SourceCount);
        EnsureActiveCellCapacity(field, totalCellCount);

        Array.Fill(field.SpatialCellHeads, -1, 0, totalCellCount);
        Array.Fill(field.SpatialNextIndices, -1, 0, field.SourceCount);
        Array.Clear(field.CellCenters, 0, totalCellCount);
        Array.Clear(field.CellStrengthSums, 0, totalCellCount);
        Array.Clear(field.CellRadiusSums, 0, totalCellCount);
        Array.Clear(field.CellSourceCounts, 0, totalCellCount);

        field.SpatialCellSize = cellSize;
        field.SpatialMinCellX = minCellX;
        field.SpatialMinCellY = minCellY;
        field.SpatialWidth = width;
        field.SpatialHeight = height;
        field.ActiveCellCount = 0;
        field.MaximumSourceRadius = maximumSourceRadius;

        for (var sourceIndex = 0; sourceIndex < field.SourceCount; sourceIndex++)
        {
            var source = field.SourceEntries[sourceIndex];
            var cellX = (int)MathF.Floor(source.Position.X / cellSize) - minCellX;
            var cellY = (int)MathF.Floor(source.Position.Y / cellSize) - minCellY;
            var cellOffset = (cellY * width) + cellX;

            if (field.CellSourceCounts[cellOffset] == 0)
                field.ActiveCellOffsets[field.ActiveCellCount++] = cellOffset;

            field.CellCenters[cellOffset] += source.Position * source.Strength;
            field.CellStrengthSums[cellOffset] += source.Strength;
            field.CellRadiusSums[cellOffset] += source.Radius;
            field.CellSourceCounts[cellOffset]++;

            field.SpatialNextIndices[sourceIndex] = field.SpatialCellHeads[cellOffset];
            field.SpatialCellHeads[cellOffset] = sourceIndex;
        }

        for (var activeCellIndex = 0; activeCellIndex < field.ActiveCellCount; activeCellIndex++)
        {
            var cellOffset = field.ActiveCellOffsets[activeCellIndex];
            var strength = field.CellStrengthSums[cellOffset];
            if (strength > 0f)
                field.CellCenters[cellOffset] /= strength;
        }

        field.BuiltSourceVersion = sourceVersion;
    }

    private static void ResetField(GravityFieldMap field, long sourceVersion)
    {
        field.ActiveCellCount = 0;
        field.MaximumSourceRadius = 0f;
        field.SpatialCellSize = 0f;
        field.SpatialMinCellX = 0;
        field.SpatialMinCellY = 0;
        field.SpatialWidth = 0;
        field.SpatialHeight = 0;
        field.SpatialCellHeads = Array.Empty<int>();
        field.SpatialNextIndices = Array.Empty<int>();
        field.ActiveCellOffsets = Array.Empty<int>();
        field.CellCenters = Array.Empty<Vector2>();
        field.CellStrengthSums = Array.Empty<float>();
        field.CellRadiusSums = Array.Empty<float>();
        field.CellSourceCounts = Array.Empty<int>();
        field.BuiltSourceVersion = sourceVersion;
    }

    private static GravitySample SampleGravityUnsafe(Vector2 position, ScopeState scopeState)
    {
        var field = scopeState.Field;
        if (!HasGravityField(field))
        {
            var totalFallback = Vector2.Zero;
            string? primaryFallbackUnitId = null;
            var primaryFallbackContribution = Vector2.Zero;
            var primaryFallbackMagnitude = 0f;
            var primaryFallbackDistance = 0f;

            foreach (var source in field.SourceEntries)
            {
                var contribution = ComputeGravityContribution(position, source.Position, source.Radius, source.Strength, out var sourceDistance);
                totalFallback += contribution;

                var contributionMagnitude = contribution.LengthSquared();
                if (contributionMagnitude <= primaryFallbackMagnitude)
                    continue;

                primaryFallbackMagnitude = contributionMagnitude;
                primaryFallbackContribution = contribution;
                primaryFallbackUnitId = source.UnitId;
                primaryFallbackDistance = sourceDistance;
            }

            return new GravitySample(
                totalFallback,
                primaryFallbackUnitId,
                primaryFallbackContribution,
                primaryFallbackDistance,
                field.SourceCount,
                0);
        }

        var exactRadius = MathF.Max(MinimumExactSourceRadius, field.SpatialCellSize * ExactSourceCellCoverage);
        var hasNearbyWindow = TryGetSpatialQueryRange(position, exactRadius, field, out var minCellX, out var minCellY, out var maxCellX, out var maxCellY);

        var total = Vector2.Zero;
        var primaryContribution = Vector2.Zero;
        string? primarySourceUnitId = null;
        var primaryContributionMagnitude = 0f;
        var primarySourceDistance = 0f;
        var nearbySourceCount = 0;

        if (hasNearbyWindow)
        {
            for (var cellY = minCellY; cellY <= maxCellY; cellY++)
            {
                for (var cellX = minCellX; cellX <= maxCellX; cellX++)
                {
                    var cellOffset = (cellY * field.SpatialWidth) + cellX;
                    var sourceIndex = field.SpatialCellHeads[cellOffset];
                    while (sourceIndex >= 0)
                    {
                        var source = field.SourceEntries[sourceIndex];
                        var contribution = ComputeGravityContribution(position, source.Position, source.Radius, source.Strength, out var sourceDistance);
                        total += contribution;
                        nearbySourceCount++;

                        var contributionMagnitude = contribution.LengthSquared();
                        if (contributionMagnitude > primaryContributionMagnitude)
                        {
                            primaryContributionMagnitude = contributionMagnitude;
                            primaryContribution = contribution;
                            primarySourceUnitId = source.UnitId;
                            primarySourceDistance = sourceDistance;
                        }

                        sourceIndex = field.SpatialNextIndices[sourceIndex];
                    }
                }
            }
        }
        else
        {
            for (var sourceIndex = 0; sourceIndex < field.SourceCount; sourceIndex++)
            {
                var source = field.SourceEntries[sourceIndex];
                var contribution = ComputeGravityContribution(position, source.Position, source.Radius, source.Strength, out var sourceDistance);
                total += contribution;

                var contributionMagnitude = contribution.LengthSquared();
                if (contributionMagnitude <= primaryContributionMagnitude)
                    continue;

                primaryContributionMagnitude = contributionMagnitude;
                primaryContribution = contribution;
                primarySourceUnitId = source.UnitId;
                primarySourceDistance = sourceDistance;
            }

            return new GravitySample(
                total,
                primarySourceUnitId,
                primaryContribution,
                primarySourceDistance,
                field.SourceCount,
                0);
        }

        var approximatedCellCount = 0;
        for (var activeCellIndex = 0; activeCellIndex < field.ActiveCellCount; activeCellIndex++)
        {
            var cellOffset = field.ActiveCellOffsets[activeCellIndex];
            var cellX = cellOffset % field.SpatialWidth;
            var cellY = cellOffset / field.SpatialWidth;
            if (cellX >= minCellX && cellX <= maxCellX && cellY >= minCellY && cellY <= maxCellY)
                continue;

            var cellStrength = field.CellStrengthSums[cellOffset];
            if (cellStrength <= 0f)
                continue;

            var cellSourceCount = field.CellSourceCounts[cellOffset];
            if (cellSourceCount <= 0)
                continue;

            var averageRadius = field.CellRadiusSums[cellOffset] / cellSourceCount;
            var contribution = ComputeGravityContribution(position, field.CellCenters[cellOffset], averageRadius, cellStrength, out _);
            if (contribution.LengthSquared() <= MinimumApproximateContribution * MinimumApproximateContribution)
                continue;

            total += contribution;
            approximatedCellCount++;
        }

        return new GravitySample(
            total,
            primarySourceUnitId,
            primaryContribution,
            primarySourceDistance,
            nearbySourceCount,
            approximatedCellCount);
    }

    private static bool HasGravityField(GravityFieldMap field)
    {
        return field.SourceCount > 0 &&
               field.SourceEntries.Length >= field.SourceCount &&
               field.SpatialCellHeads.Length > 0 &&
               field.SpatialNextIndices.Length >= field.SourceCount &&
               field.ActiveCellOffsets.Length > 0 &&
               field.CellCenters.Length > 0 &&
               field.CellStrengthSums.Length > 0 &&
               field.CellRadiusSums.Length > 0 &&
               field.CellSourceCounts.Length > 0 &&
               field.SpatialWidth > 0 &&
               field.SpatialHeight > 0 &&
               field.SpatialCellSize > 0f;
    }

    private static bool TryGetSpatialQueryRange(
        Vector2 position,
        float radius,
        GravityFieldMap field,
        out int minCellX,
        out int minCellY,
        out int maxCellX,
        out int maxCellY)
    {
        minCellX = 0;
        minCellY = 0;
        maxCellX = -1;
        maxCellY = -1;

        if (field.SpatialWidth <= 0 || field.SpatialHeight <= 0 || field.SpatialCellSize <= 0f)
            return false;

        var minX = position.X - radius;
        var minY = position.Y - radius;
        var maxX = position.X + radius;
        var maxY = position.Y + radius;

        minCellX = Math.Max(0, (int)MathF.Floor(minX / field.SpatialCellSize) - field.SpatialMinCellX);
        minCellY = Math.Max(0, (int)MathF.Floor(minY / field.SpatialCellSize) - field.SpatialMinCellY);
        maxCellX = Math.Min(field.SpatialWidth - 1, (int)MathF.Floor(maxX / field.SpatialCellSize) - field.SpatialMinCellX);
        maxCellY = Math.Min(field.SpatialHeight - 1, (int)MathF.Floor(maxY / field.SpatialCellSize) - field.SpatialMinCellY);
        return minCellX <= maxCellX && minCellY <= maxCellY;
    }

    private static Vector2 ComputeGravityContribution(
        Vector2 position,
        Vector2 sourcePosition,
        float sourceRadius,
        float gravityStrength,
        out float sourceDistance)
    {
        sourceDistance = 0f;
        if (gravityStrength <= 0f)
            return Vector2.Zero;

        var delta = sourcePosition - position;
        var distanceSquared = delta.LengthSquared();
        if (distanceSquared <= MinimumGravityDistanceSquared)
            return Vector2.Zero;

        sourceDistance = MathF.Sqrt(distanceSquared);
        var safeDistance = MathF.Max(sourceDistance - sourceRadius, sourceRadius * 0.35f);
        var direction = delta / sourceDistance;
        var pull = gravityStrength / MathF.Max(MinimumGravityFalloffDistance, safeDistance * safeDistance);
        return direction * pull;
    }

    private static void EnsureSourceLinkCapacity(GravityFieldMap field, int desiredCapacity)
    {
        if (field.SpatialNextIndices.Length >= desiredCapacity)
            return;

        var capacity = Math.Max(8, desiredCapacity);
        field.SpatialNextIndices = new int[capacity];
    }

    private static void EnsureCellCapacity(GravityFieldMap field, int desiredCapacity)
    {
        if (field.SpatialCellHeads.Length >= desiredCapacity &&
            field.CellCenters.Length >= desiredCapacity &&
            field.CellStrengthSums.Length >= desiredCapacity &&
            field.CellRadiusSums.Length >= desiredCapacity &&
            field.CellSourceCounts.Length >= desiredCapacity)
        {
            return;
        }

        var capacity = Math.Max(8, desiredCapacity);
        field.SpatialCellHeads = new int[capacity];
        field.CellCenters = new Vector2[capacity];
        field.CellStrengthSums = new float[capacity];
        field.CellRadiusSums = new float[capacity];
        field.CellSourceCounts = new int[capacity];
    }

    private static void EnsureActiveCellCapacity(GravityFieldMap field, int desiredCapacity)
    {
        if (field.ActiveCellOffsets.Length >= desiredCapacity)
            return;

        var capacity = Math.Max(8, desiredCapacity);
        field.ActiveCellOffsets = new int[capacity];
    }

    private readonly record struct GravityScopeKey(string GalaxyId, int ClusterId);

    private readonly record struct GravitySource(string UnitId, Vector2 Position, float Radius, float Strength);

    private sealed class ScopeState
    {
        public Dictionary<string, GravitySource> SourcesByUnitId { get; } = new();
        public List<GravitySource> SourceBuffer { get; } = new();
        public GravityFieldMap Field { get; } = new();
        public long SourceVersion { get; set; }
        public bool FieldDirty { get; set; } = true;
    }

    private sealed class GravityFieldMap
    {
        public long BuiltSourceVersion { get; set; }
        public int SourceCount { get; set; }
        public int ActiveCellCount { get; set; }
        public float MaximumSourceRadius { get; set; }
        public float SpatialCellSize { get; set; }
        public int SpatialMinCellX { get; set; }
        public int SpatialMinCellY { get; set; }
        public int SpatialWidth { get; set; }
        public int SpatialHeight { get; set; }
        public GravitySource[] SourceEntries { get; set; } = Array.Empty<GravitySource>();
        public int[] SpatialCellHeads { get; set; } = Array.Empty<int>();
        public int[] SpatialNextIndices { get; set; } = Array.Empty<int>();
        public int[] ActiveCellOffsets { get; set; } = Array.Empty<int>();
        public Vector2[] CellCenters { get; set; } = Array.Empty<Vector2>();
        public float[] CellStrengthSums { get; set; } = Array.Empty<float>();
        public float[] CellRadiusSums { get; set; } = Array.Empty<float>();
        public int[] CellSourceCounts { get; set; } = Array.Empty<int>();
    }
}
