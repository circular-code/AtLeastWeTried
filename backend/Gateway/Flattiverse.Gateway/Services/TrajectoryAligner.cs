using static Flattiverse.Gateway.Services.GravitySimulator;

namespace Flattiverse.Gateway.Services;

/// <summary>
/// Computes the engine thrust vector that steers a ship along a navigation path,
/// compensating for current velocity and gravitational acceleration.
/// When a remaining path is provided, it anticipates upcoming turns and brakes
/// so the ship can follow curvature without overshooting.
/// </summary>
public static class TrajectoryAligner
{
    /// <summary>Ticks of gravity to anticipate for compensation.</summary>
    private const int GravityLookaheadTicks = 6;

    /// <summary>
    /// Fraction of cross-track velocity to cancel per tick. Higher values steer more
    /// aggressively toward the path at the cost of along-track speed.
    /// </summary>
    private const double CrossTrackCorrectionGain = 1.0d;

    /// <summary>
    /// Approach deceleration factor: desired along-track speed = min(speedLimit, dist * factor).
    /// Lower values start braking earlier. Used when no path is available.
    /// </summary>
    private const double ApproachDecelerationFactor = 0.10d;

    /// <summary>
    /// How far ahead (in world units) along the remaining path to scan for upcoming turns.
    /// </summary>
    private const double CurvatureScanDistance = 200d;

    /// <summary>
    /// Safety factor for turn-speed calculation. Lower = more conservative braking.
    /// </summary>
    private const double TurnSpeedSafetyFactor = 0.85d;

    /// <summary>
    /// Proportional rate for the final approach: maxSpeed = distance × rate.
    /// This linear limit dominates near the endpoint where the sqrt braking curve
    /// is too generous for discrete-time control.  Chosen so the ship reaches
    /// ≈0.5 speed at typical arrival thresholds (~14 units).
    /// </summary>
    private const double StopApproachRate = 0.035d;

    /// <summary>
    /// Compute the engine vector that best steers the ship from its current position
    /// toward (<paramref name="targetX"/>, <paramref name="targetY"/>), accounting for
    /// its current velocity, gravitational pull, and upcoming turns in the remaining path.
    /// </summary>
    public static (double EngineX, double EngineY) ComputeEngineVector(
        double shipX,
        double shipY,
        double velX,
        double velY,
        double targetX,
        double targetY,
        IReadOnlyList<GravitySource> sources,
        double engineMax,
        double thrustPercentage,
        double speedLimit,
        IReadOnlyList<(double X, double Y)>? remainingPath = null)
    {
        var maxThrust = engineMax * Math.Clamp(thrustPercentage, 0d, 1d);
        if (maxThrust <= 0d)
            return (0d, 0d);

        // Direction and distance to target
        var dx = targetX - shipX;
        var dy = targetY - shipY;
        var dist = Math.Sqrt(dx * dx + dy * dy);

        if (dist <= 0d)
            return (0d, 0d);

        // Unit vector toward target
        var toTargetX = dx / dist;
        var toTargetY = dy / dist;

        // Decompose current velocity into along-track and cross-track components
        var alongTrackSpeed = velX * toTargetX + velY * toTargetY;
        var crossTrackVx = velX - alongTrackSpeed * toTargetX;
        var crossTrackVy = velY - alongTrackSpeed * toTargetY;

        // Predict cumulative gravity over the lookahead window
        var (gx, gy) = ComputeGravityAcceleration(shipX, shipY, sources);
        var gravCompX = gx * GravityLookaheadTicks;
        var gravCompY = gy * GravityLookaheadTicks;

        // --- Phase 1: Cancel cross-track drift + gravity drift ---
        var cancelX = -crossTrackVx * CrossTrackCorrectionGain - gravCompX;
        var cancelY = -crossTrackVy * CrossTrackCorrectionGain - gravCompY;

        // --- Phase 2: Along-track speed regulation ---
        // Compute maximum safe speed considering upcoming path curvature
        var maxSafeSpeed = ComputeMaxSafeSpeed(
            shipX, shipY, targetX, targetY, dist,
            remainingPath, sources, maxThrust, speedLimit);

        var desiredAlongTrack = Math.Min(maxSafeSpeed, dist * ApproachDecelerationFactor);
        // Ensure we don't request negative speed (would reverse)
        desiredAlongTrack = Math.Max(0d, desiredAlongTrack);

        var alongTrackError = desiredAlongTrack - alongTrackSpeed;
        var thrustAlongX = alongTrackError * toTargetX;
        var thrustAlongY = alongTrackError * toTargetY;

        // --- Combine both phases ---
        var correctionX = cancelX + thrustAlongX;
        var correctionY = cancelY + thrustAlongY;

        // Clamp to maximum thrust
        var correctionMag = Math.Sqrt(correctionX * correctionX + correctionY * correctionY);
        if (correctionMag > maxThrust)
        {
            var scale = maxThrust / correctionMag;
            correctionX *= scale;
            correctionY *= scale;
        }

        return (correctionX, correctionY);
    }

    /// <summary>
    /// Scan the remaining path polyline for upcoming turns and compute the maximum safe
    /// along-track speed so the ship can decelerate before each turn.
    ///
    /// Physics model:
    /// At a path vertex with turn angle θ, the velocity component perpendicular to the
    /// new direction is v·sin(θ). To cancel that in one tick requires thrust ≥ v·sin(θ).
    /// So v_max_at_turn = maxThrust / sin(θ).
    ///
    /// To decelerate from current speed to v_max_at_turn over distance d:
    ///   v_now² = v_turn² + 2·a·d  →  v_now = sqrt(v_turn² + 2·maxThrust·d)
    ///
    /// We scan all turns within CurvatureScanDistance and return the minimum
    /// constrained speed.
    /// </summary>
    private static double ComputeMaxSafeSpeed(
        double shipX, double shipY,
        double targetX, double targetY,
        double distToTarget,
        IReadOnlyList<(double X, double Y)>? path,
        IReadOnlyList<GravitySource> sources,
        double maxThrust,
        double speedLimit)
    {
        // Compute total remaining distance (ship → target → all remaining path vertices)
        // and locate the path endpoint for gravity calculation.
        var totalRemainingDist = distToTarget;
        var endX = targetX;
        var endY = targetY;
        if (path is not null)
        {
            var tpx = targetX;
            var tpy = targetY;
            for (var i = 0; i < path.Count; i++)
            {
                var sdx = path[i].X - tpx;
                var sdy = path[i].Y - tpy;
                totalRemainingDist += Math.Sqrt(sdx * sdx + sdy * sdy);
                tpx = path[i].X;
                tpy = path[i].Y;
            }
            endX = tpx;
            endY = tpy;
        }

        // Compute gravity at the path endpoint to determine effective braking thrust.
        // The engine must counteract gravity while braking.  The gravity compensation
        // in Phase 1 uses GravityLookaheadTicks, so the actual thrust consumed per tick
        // for gravity is gravMag * GravityLookaheadTicks (clamped to maxThrust).
        var (egx, egy) = ComputeGravityAcceleration(endX, endY, sources);
        var gravMag = Math.Sqrt(egx * egx + egy * egy);
        var gravCompCost = Math.Min(gravMag * GravityLookaheadTicks, maxThrust * 0.9d);

        // Effective braking = thrust left after gravity compensation.
        var effectiveBraking = Math.Max(maxThrust - gravCompCost, maxThrust * 0.05d);

        // Two braking limits, take the tighter one:
        // 1) Physics sqrt curve:  v = sqrt(2 · a_eff · d)
        // 2) Proportional stop:   v = d · StopApproachRate  (dominates near the endpoint)
        var vMaxSqrt = Math.Sqrt(2d * effectiveBraking * totalRemainingDist);
        var vMaxProp = totalRemainingDist * StopApproachRate;
        var vMaxForStop = Math.Min(vMaxSqrt, vMaxProp);

        if (path is null || path.Count < 2)
        {
            // No path — use braking curve and distance-based proportional control
            return Math.Min(speedLimit, Math.Min(vMaxForStop, distToTarget * ApproachDecelerationFactor));
        }

        var limit = Math.Min(speedLimit, vMaxForStop);

        // Build cumulative distance from ship → target → path[0] → path[1] → ...
        // path[0] should be approximately the target point; remaining path follows.
        // Walk the polyline accumulating distance and checking turn angles.
        var prevX = shipX;
        var prevY = shipY;
        var cumulativeDist = 0d;

        // First segment: ship → target (lookahead)
        cumulativeDist += distToTarget;
        prevX = targetX;
        prevY = targetY;

        for (var i = 0; i < path.Count && cumulativeDist < CurvatureScanDistance; i++)
        {
            var px = path[i].X;
            var py = path[i].Y;
            var segDx = px - prevX;
            var segDy = py - prevY;
            var segLen = Math.Sqrt(segDx * segDx + segDy * segDy);

            if (segLen < 0.1d)
            {
                prevX = px;
                prevY = py;
                continue;
            }

            // Check turn angle at this vertex (angle between incoming and outgoing)
            if (i > 0 || distToTarget > 0.1d)
            {
                // Incoming direction — from previous point to this vertex's predecessor
                double inDx, inDy;
                if (i == 0)
                {
                    inDx = targetX - shipX;
                    inDy = targetY - shipY;
                }
                else
                {
                    inDx = prevX - (i == 1 ? targetX : path[i - 2].X);
                    inDy = prevY - (i == 1 ? targetY : path[i - 2].Y);
                }

                var inLen = Math.Sqrt(inDx * inDx + inDy * inDy);
                if (inLen > 0.1d && segLen > 0.1d)
                {
                    // Cosine of turn angle
                    var cosAngle = (inDx * segDx + inDy * segDy) / (inLen * segLen);
                    cosAngle = Math.Clamp(cosAngle, -1d, 1d);

                    // sin(θ) where θ is the turn angle (deviation from straight)
                    var sinAngle = Math.Sqrt(1d - cosAngle * cosAngle);

                    if (sinAngle > 0.01d)
                    {
                        // Maximum speed at the turn vertex: v_turn = maxThrust / sin(θ)
                        var vMaxAtTurn = maxThrust * TurnSpeedSafetyFactor / sinAngle;
                        vMaxAtTurn = Math.Min(vMaxAtTurn, speedLimit);

                        // Maximum speed now to decelerate to vMaxAtTurn over cumulativeDist:
                        // v_now² = v_turn² + 2·maxThrust·dist
                        var vMaxNow = Math.Sqrt(vMaxAtTurn * vMaxAtTurn + 2d * maxThrust * cumulativeDist);
                        limit = Math.Min(limit, vMaxNow);
                    }
                }
            }

            cumulativeDist += segLen;
            prevX = px;
            prevY = py;
        }

        return limit;
    }
}
