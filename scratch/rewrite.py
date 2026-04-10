import re
import os

path = r"C:\Users\LeSch\Documents\Projects\temp\AtLeastWeTried\backend\Gateway\Flattiverse.Gateway\Services\ManeuveringService.cs"

with open(path, 'r', encoding='utf-8') as f:
    code = f.read()

# Replace IsSettledAtTarget
settle_pattern = r"public bool IsSettledAtTarget\(.*?\).*?\n    }\n"
settle_repl = """public bool IsSettledAtTarget(
        ClassicShipControllable ship,
        float goalX,
        float goalY,
        float arrivalThreshold)
    {
        var goalVector = new Vector(goalX - ship.Position.X, goalY - ship.Position.Y);
        var goalDistance = goalVector.Length;
        if (goalDistance > arrivalThreshold)
        {
            return false;
        }

        var stats = BuildShipStats(ship);
        var totalGravity = CalculateTotalGravity(ship, new Vector(ship.Position.X, ship.Position.Y));
        var movementDir = IsNearZero(ship.Movement) ? new Vector(1, 0) : ship.Movement / ship.Movement.Length;
        var gravityAlongMotion = Math.Max(0f, Dot(totalGravity, movementDir));
        var thrustCap = ship.Engine.Maximum * Math.Clamp(stats.EngineEfficiency, 0.55f, 1.25f);
        var brakingAcceleration = Math.Max(thrustCap - gravityAlongMotion, ship.Engine.Maximum * 0.3f);
        
        var stoppingDistance = EstimateStoppingDistance(ship.Movement.Length, brakingAcceleration);
        var settleSpeed = Math.Max(0.18f, Math.Min(stats.SpeedLimit * 0.2f, 0.65f));
        return ship.Movement.Length <= settleSpeed && stoppingDistance <= arrivalThreshold * 0.6f;
    }

    public static Vector CalculateTotalGravity(ClassicShipControllable ship, Vector pos)
    {
        var totalGravity = new Vector();
        if (ship.Cluster is null || ship.Cluster.Units is null) return totalGravity;
        foreach (var unit in ship.Cluster.Units)
        {
            if (unit.Gravity <= 0f) continue;
            if (string.Equals(unit.Name, ship.Name, StringComparison.Ordinal)) continue;
            var toUnit = new Vector(unit.Position.X - pos.X, unit.Position.Y - pos.Y);
            if (IsNearZero(toUnit)) continue;
            totalGravity += BuildGravityDelta(toUnit, unit.Gravity);
        }
        return totalGravity;
    }
"""
code = re.sub(settle_pattern, settle_repl, code, flags=re.DOTALL)

# Replace BuildPointerVector through BuildVelocityMatchingVector
pointer_pattern = r"private Vector BuildPointerVector\(ClassicShipControllable ship, ManeuverState state, float deltaSeconds, bool updateState\).*?private static Vector BuildVelocityMatchingVector.*?return command;\n    }\n"
pointer_repl = """private Vector BuildPointerVector(ClassicShipControllable ship, ManeuverState state, float deltaSeconds, bool updateState)
    {
        var targetPos = state.HasPathTangent ? new Vector(state.TargetX, state.TargetY) : new Vector(state.GoalX, state.GoalY);
        var goalPos = new Vector(state.GoalX, state.GoalY);
        var shipPos = new Vector(ship.Position.X, ship.Position.Y);
        var toTarget = targetPos - shipPos;
        var toGoal = goalPos - shipPos;
        
        var targetDist = toTarget.Length;
        var goalDist = toGoal.Length;

        if (IsNearZero(toTarget) && IsNearZero(toGoal)) return new Vector();

        var desiredDirection = targetDist > 0.001f ? toTarget / targetDist : (goalDist > 0.001f ? toGoal / goalDist : new Vector(1, 0));

        var maximumVectorLength = ship.Engine.Maximum;
        if (maximumVectorLength <= 0f) return new Vector();

        var stats = BuildShipStats(ship);
        var totalGravity = CalculateTotalGravity(ship, shipPos);
        
        var movementDir = ship.Movement.Length > 0.001f ? ship.Movement / ship.Movement.Length : desiredDirection;
        var gravityAlongMotion = Math.Max(0f, Dot(totalGravity, movementDir));
        var thrustCap = maximumVectorLength * Math.Clamp(stats.EngineEfficiency, 0.55f, 1.25f);
        var brakingAcceleration = Math.Max(thrustCap - gravityAlongMotion, maximumVectorLength * 0.3f);

        float distanceToBrake = state.HasPathTangent ? state.RemainingDistance : goalDist;
        var physicsSpeedCap = ComputeApproachSpeed(distanceToBrake, brakingAcceleration, ApproachSafetyFactor * 0.90f); 
        
        float turnPenalty = 0f;
        if (state.HasPathTangent) {
             var pathTangent = new Vector(state.PathTangentX, state.PathTangentY);
             if (pathTangent.Length > 0.001f) {
                 float dotAngle = Dot(desiredDirection, pathTangent / pathTangent.Length);
                 turnPenalty = Math.Clamp(1f - dotAngle, 0f, 1f) * 0.8f; 
             }
        } else {
             float dotAngle = Dot(desiredDirection, movementDir);
             turnPenalty = Math.Clamp(1f - dotAngle, 0f, 1f) * 0.5f;
        }

        var tuning = GetStrategyTuning(_motionStrategy);

        float desiredSpeed = stats.SpeedLimit * tuning.CruiseSpeedFactor * Lerp(1.0f, tuning.TurnSpeedFactor, turnPenalty);
        desiredSpeed = Math.Min(desiredSpeed, stats.SpeedLimit);
        desiredSpeed = Math.Min(desiredSpeed, physicsSpeedCap);
        
        if (state.TerminalApproach && goalDist < ship.Size * 1.5f) {
           desiredSpeed = Math.Min(desiredSpeed, stats.SpeedLimit * 0.15f);
        }

        Vector desiredVelocity = desiredDirection * desiredSpeed;
        
        if (state.HasPathTangent)
        {
            var pathTangent = new Vector(state.PathTangentX, state.PathTangentY);
            if (pathTangent.Length > 0.001f)
            {
                pathTangent = pathTangent / pathTangent.Length;
                var pathNormal = new Vector(-pathTangent.Y, pathTangent.X);
                var anchorVector = new Vector(state.AnchorX - ship.Position.X, state.AnchorY - ship.Position.Y);
                var crossError = Dot(anchorVector, pathNormal);

                var normalGain = Lerp(tuning.NormalGainMin, tuning.NormalGainMax, Clamp01(state.ThrustPercentage));
                normalGain *= 2.0f; 

                var desiredNormalSpeed = Math.Clamp(
                    crossError * normalGain,
                    -stats.SpeedLimit * tuning.NormalSpeedLimitFactor,
                    stats.SpeedLimit * tuning.NormalSpeedLimitFactor);

                desiredVelocity += pathNormal * desiredNormalSpeed;
                
                if (desiredVelocity.Length > stats.SpeedLimit) {
                    desiredVelocity = (desiredVelocity / desiredVelocity.Length) * stats.SpeedLimit;
                }
            }
        }

        Vector velocityError = desiredVelocity - ship.Movement;

        float responseGain = Lerp(tuning.ResponseGainMin, tuning.ResponseGainMax, Clamp01(state.ThrustPercentage));
        
        var alongSpeed = Dot(ship.Movement, desiredDirection);
        if (desiredSpeed < alongSpeed - 0.035f)
        {
            responseGain *= 2.5f; 
        }

        Vector command = velocityError * responseGain;
        
        float desiredVectorLengthLimit = maximumVectorLength * Clamp01(state.ThrustPercentage);
        if (command.Length > desiredVectorLengthLimit) {
             command = command / command.Length * desiredVectorLengthLimit;
        }

        return command;
    }
"""
code = re.sub(pointer_pattern, pointer_repl, code, flags=re.DOTALL)

# Remove unused braking methods
brake_pattern = r"    /// <summary>Usable deceleration along the brake axis using engine thrust.*?\n    private static float GoalGravityAlong\(.*?\n    }\n"
code = re.sub(brake_pattern, "", code, flags=re.DOTALL)

# Replace ApplyManeuver
apply_pattern = r"private void ApplyManeuver\(ManeuverState state, float deltaSeconds\).*?_ = ship.Engine.Set\(pointerVector\);\n    }\n"
apply_repl = """private void ApplyManeuver(ManeuverState state, float deltaSeconds)
    {
        var ship = state.Ship;
        if (ship is null || !ship.Active || !ship.Engine.Exists)
            return;

        if (!ship.Alive)
        {
            state.LastAppliedVector = new Vector(float.NaN, float.NaN);
            return;
        }

        var desiredVector = state.HasTarget
            ? new Vector(state.TargetX - ship.Position.X, state.TargetY - ship.Position.Y)
            : new Vector();
        var pathTangent = state.HasPathTangent
            ? new Vector(state.PathTangentX, state.PathTangentY)
            : (IsNearZero(desiredVector) ? new Vector(1f, 0f) : desiredVector / desiredVector.Length);
        var undesiredMovement = BuildLateralMovement(ship.Movement, pathTangent);
        
        if (!state.HasTarget || IsNearZero(desiredVector))
        {
            _ = ship.Engine.Off();
            return;
        }

        var pointerVector = BuildPointerVector(ship, state, deltaSeconds, true);

        var totalGravity = CalculateTotalGravity(ship, new Vector(ship.Position.X, ship.Position.Y));

        pointerVector -= totalGravity;
        if (pointerVector.Length > ship.Engine.Maximum)
        {
            pointerVector.Length = ship.Engine.Maximum;
        }

        LogManeuver(ship, state, desiredVector, undesiredMovement, pointerVector);
        if (VectorEquals(pointerVector, state.LastAppliedVector))
            return;

        state.LastAppliedVector = new Vector(pointerVector);

        if (IsNearZero(pointerVector))
        {
            _ = ship.Engine.Off();
            return;
        }

        _ = ship.Engine.Set(pointerVector);
    }
"""
code = re.sub(apply_pattern, apply_repl, code, flags=re.DOTALL)

with open(path, 'w', encoding='utf-8') as f:
    f.write(code)
print("Rewrite done")
