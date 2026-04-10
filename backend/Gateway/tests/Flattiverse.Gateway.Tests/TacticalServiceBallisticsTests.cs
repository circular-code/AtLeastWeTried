using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Flattiverse.Connector.GalaxyHierarchy;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Services;

namespace Flattiverse.Gateway.Tests;

public sealed class TacticalServiceBallisticsTests
{
    private static readonly Type TacticalType = typeof(TacticalService);
    private static readonly Type GravitySourceType = TacticalType.GetNestedType("GravitySource", BindingFlags.NonPublic)
                                                    ?? throw new InvalidOperationException("GravitySource nested type not found.");
    private static readonly MethodInfo ComputeProjectedLaunchOriginMethod = TacticalType.GetMethod(
        "ComputeProjectedLaunchOrigin",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ComputeProjectedLaunchOrigin method not found.");
    private static readonly MethodInfo TryPredictToPointMethod = TacticalType.GetMethod(
        "TryPredictRelativeMovementToPointWithGravity",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("TryPredictRelativeMovementToPointWithGravity method not found.");
    private static readonly MethodInfo SimulateShotMethod = TacticalType.GetMethod(
        "SimulateShotWithGravity",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("SimulateShotWithGravity method not found.");
    private static readonly MethodInfo ComputeGravityAccelerationMethod = TacticalType.GetMethod(
        "ComputeGravityAcceleration",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ComputeGravityAcceleration method not found.");
    private static readonly ConstructorInfo GravitySourceCtor = GravitySourceType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                                                  .Single();

    [Fact]
    public void ComputeProjectedLaunchOrigin_uses_ship_size_plus_padding()
    {
        ClassicShipControllable ship = CreateShip(positionX: 100f, positionY: 200f, movementX: 0f, movementY: 0f);

        object?[] args = { ship, 3f, 4f, 0f, 0f };
        ComputeProjectedLaunchOriginMethod.Invoke(null, args);

        float startX = (float)args[3]!;
        float startY = (float)args[4]!;

        // Classic ship size is 14, tactical launch padding is 2 => launch distance 16.
        Assert.Equal(109.6f, startX, 3);
        Assert.Equal(212.8f, startY, 3);
    }

    [Fact]
    public void Point_solver_without_gravity_hits_requested_point()
    {
        ClassicShipControllable ship = CreateShip(positionX: 0f, positionY: 0f, movementX: 0f, movementY: 0f);
        object gravitySources = CreateGravitySourceList();
        const float targetX = 160f;
        const float targetY = 0f;
        const int ticks = 20;

        object?[] solveArgs = { ship, targetX, targetY, gravitySources, ticks, null, float.MaxValue };
        bool solved = (bool)TryPredictToPointMethod.Invoke(null, solveArgs)!;

        Assert.True(solved);
        Vector relativeMovement = Assert.IsType<Vector>(solveArgs[5]);
        float missDistance = (float)solveArgs[6]!;
        Assert.True(missDistance < 0.01f, $"Expected near-perfect hit, miss={missDistance:0.#####}");

        object?[] simulateArgs = { ship, relativeMovement.X, relativeMovement.Y, ticks, gravitySources, 0f, 0f };
        SimulateShotMethod.Invoke(null, simulateArgs);

        float finalX = (float)simulateArgs[5]!;
        float finalY = (float)simulateArgs[6]!;
        Assert.Equal(targetX, finalX, 2);
        Assert.Equal(targetY, finalY, 2);
    }

    [Fact]
    public void Point_solver_with_gravity_source_still_converges_precisely()
    {
        ClassicShipControllable ship = CreateShip(positionX: 0f, positionY: 0f, movementX: 0.15f, movementY: -0.05f);
        object gravitySources = CreateGravitySourceList(
            CreateGravitySource(
                positionX: 120f,
                positionY: -70f,
                movementX: 0f,
                movementY: 0f,
                gravity: 0.7f,
                radius: 10f,
                gravityWellRadius: 0f,
                gravityWellForce: 0f));

        const float targetX = 220f;
        const float targetY = 35f;
        const int ticks = 70;

        object?[] solveArgs = { ship, targetX, targetY, gravitySources, ticks, null, float.MaxValue };
        bool solved = (bool)TryPredictToPointMethod.Invoke(null, solveArgs)!;
        Assert.True(solved);

        Vector relativeMovement = Assert.IsType<Vector>(solveArgs[5]);
        float missDistance = (float)solveArgs[6]!;
        Assert.True(missDistance < 1.25f, $"Expected low miss under gravity, miss={missDistance:0.###}");

        object?[] simulateArgs = { ship, relativeMovement.X, relativeMovement.Y, ticks, gravitySources, 0f, 0f };
        SimulateShotMethod.Invoke(null, simulateArgs);
        float finalX = (float)simulateArgs[5]!;
        float finalY = (float)simulateArgs[6]!;
        float residualMiss = MathF.Sqrt((targetX - finalX) * (targetX - finalX) + (targetY - finalY) * (targetY - finalY));
        Assert.True(residualMiss < 1.25f, $"Residual miss too high: {residualMiss:0.###}");
    }

    [Fact]
    public void Gravity_well_softening_keeps_edge_influence_small_but_strengthens_deep_inside()
    {
        object noWell = CreateGravitySource(
            positionX: 0f,
            positionY: 0f,
            movementX: 0f,
            movementY: 0f,
            gravity: 1f,
            radius: 5f,
            gravityWellRadius: 100f,
            gravityWellForce: 0f);
        object withWell = CreateGravitySource(
            positionX: 0f,
            positionY: 0f,
            movementX: 0f,
            movementY: 0f,
            gravity: 1f,
            radius: 5f,
            gravityWellRadius: 100f,
            gravityWellForce: 9f);

        float edgeBaseline = ComputeAccelerationMagnitude(positionX: 99f, positionY: 0f, CreateGravitySourceList(noWell));
        float edgeWithWell = ComputeAccelerationMagnitude(positionX: 99f, positionY: 0f, CreateGravitySourceList(withWell));
        float deepBaseline = ComputeAccelerationMagnitude(positionX: 50f, positionY: 0f, CreateGravitySourceList(noWell));
        float deepWithWell = ComputeAccelerationMagnitude(positionX: 50f, positionY: 0f, CreateGravitySourceList(withWell));

        // At 99% well radius, softened contribution should be tiny.
        Assert.InRange(edgeWithWell / edgeBaseline, 1.0f, 1.01f);
        // Deeper in the well, the additional pull should be clearly noticeable.
        Assert.True(deepWithWell > deepBaseline * 1.5f);
    }

    private static ClassicShipControllable CreateShip(float positionX, float positionY, float movementX, float movementY)
    {
        var ship = (ClassicShipControllable)RuntimeHelpers.GetUninitializedObject(typeof(ClassicShipControllable));
        SetInstanceField(ship, "_position", new Vector(positionX, positionY));
        SetInstanceField(ship, "_movement", new Vector(movementX, movementY));
        return ship;
    }

    private static object CreateGravitySource(float positionX, float positionY, float movementX, float movementY, float gravity, float radius,
        float gravityWellRadius, float gravityWellForce)
    {
        var unit = (Unit)RuntimeHelpers.GetUninitializedObject(typeof(Unit));
        return GravitySourceCtor.Invoke(new object[]
        {
            unit,
            positionX,
            positionY,
            movementX,
            movementY,
            gravity,
            radius,
            gravityWellRadius,
            gravityWellForce,
        });
    }

    private static object CreateGravitySourceList(params object[] sources)
    {
        Type listType = typeof(List<>).MakeGenericType(GravitySourceType);
        var list = (IList)(Activator.CreateInstance(listType)
                    ?? throw new InvalidOperationException("Failed to create gravity source list."));

        foreach (object source in sources)
            list.Add(source);

        return list;
    }

    private static float ComputeAccelerationMagnitude(float positionX, float positionY, object gravitySources)
    {
        object?[] args = { positionX, positionY, 0, gravitySources, null, 0f, 0f };
        ComputeGravityAccelerationMethod.Invoke(null, args);
        float ax = (float)args[5]!;
        float ay = (float)args[6]!;
        return MathF.Sqrt(ax * ax + ay * ay);
    }

    private static void SetInstanceField(object target, string fieldName, object value)
    {
        Type? type = target.GetType();
        while (type is not null)
        {
            FieldInfo? field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field is not null)
            {
                field.SetValue(target, value);
                return;
            }

            type = type.BaseType;
        }

        throw new InvalidOperationException($"Field '{fieldName}' not found on '{target.GetType().FullName}'.");
    }
}
