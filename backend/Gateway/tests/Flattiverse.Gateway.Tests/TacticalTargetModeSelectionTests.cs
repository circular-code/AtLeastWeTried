using System.Reflection;
using System.Runtime.CompilerServices;
using Flattiverse.Connector.Units;
using Flattiverse.Gateway.Services;

namespace Flattiverse.Gateway.Tests;

public sealed class TacticalTargetModeSelectionTests
{
    private static readonly Type TacticalType = typeof(TacticalService);
    private static readonly MethodInfo ShouldUseMovingTargetPredictionMethod = TacticalType.GetMethod(
        "ShouldUseMovingTargetPrediction",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ShouldUseMovingTargetPrediction method not found.");
    private static readonly MethodInfo ComputeEnemyTargetPriorityScoreMethod = TacticalType.GetMethod(
        "ComputeEnemyTargetPriorityScore",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ComputeEnemyTargetPriorityScore method not found.");

    [Fact]
    public void Moving_prediction_is_only_used_for_target_mode_and_moving_targets()
    {
        Unit staticTarget = CreateUnitWithMovement(0f, 0f);
        Unit movingTarget = CreateUnitWithMovement(0.3f, -0.1f);

        bool staticInTargetMode = InvokeShouldUseMovingTargetPrediction(TacticalService.TacticalMode.Target, staticTarget);
        bool movingInTargetMode = InvokeShouldUseMovingTargetPrediction(TacticalService.TacticalMode.Target, movingTarget);
        bool movingInEnemyMode = InvokeShouldUseMovingTargetPrediction(TacticalService.TacticalMode.Enemy, movingTarget);

        Assert.False(staticInTargetMode);
        Assert.True(movingInTargetMode);
        Assert.False(movingInEnemyMode);
    }

    [Fact]
    public void Enemy_target_priority_prefers_close_non_lateral_and_not_fleeing_targets()
    {
        float baselineScore = InvokeEnemyTargetPriorityScore(distance: 120f, lateralSpeed: 0.2f, closingSpeed: -0.5f);
        float farScore = InvokeEnemyTargetPriorityScore(distance: 220f, lateralSpeed: 0.2f, closingSpeed: -0.5f);
        float lateralScore = InvokeEnemyTargetPriorityScore(distance: 120f, lateralSpeed: 2f, closingSpeed: -0.5f);
        float fleeingScore = InvokeEnemyTargetPriorityScore(distance: 120f, lateralSpeed: 0.2f, closingSpeed: 1.5f);

        Assert.True(baselineScore < farScore, $"Expected closer target to score better. near={baselineScore:0.###}, far={farScore:0.###}");
        Assert.True(baselineScore < lateralScore, $"Expected lower lateral speed to score better. baseline={baselineScore:0.###}, lateral={lateralScore:0.###}");
        Assert.True(baselineScore < fleeingScore, $"Expected non-fleeing target to score better. baseline={baselineScore:0.###}, fleeing={fleeingScore:0.###}");
    }

    private static Unit CreateUnitWithMovement(float movementX, float movementY)
    {
        var unit = (SteadyUnit)RuntimeHelpers.GetUninitializedObject(typeof(SteadyUnit));
        SetInstanceField(unit, "_movement", new Vector(movementX, movementY));
        return unit;
    }

    private static bool InvokeShouldUseMovingTargetPrediction(TacticalService.TacticalMode mode, Unit target)
    {
        return (bool)(ShouldUseMovingTargetPredictionMethod.Invoke(null, new object[] { mode, target })
            ?? throw new InvalidOperationException("ShouldUseMovingTargetPrediction returned null."));
    }

    private static float InvokeEnemyTargetPriorityScore(float distance, float lateralSpeed, float closingSpeed)
    {
        return (float)(ComputeEnemyTargetPriorityScoreMethod.Invoke(null, new object[] { distance, lateralSpeed, closingSpeed })
            ?? throw new InvalidOperationException("ComputeEnemyTargetPriorityScore returned null."));
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
