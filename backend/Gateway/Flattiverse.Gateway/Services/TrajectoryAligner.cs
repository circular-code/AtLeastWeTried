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
    /// Minimum sine of turn angle to trigger curvature braking.
    /// Arc discretization produces many vertices with tiny turn angles (1-5°).
    /// These smooth curves are handled by Phase 1 cross-track correction, so
    /// curvature braking only kicks in for sharper turns above this threshold.
    /// sin(10°) ≈ 0.17.
    /// </summary>
    private const double MinTurnSinAngle = 0.17d;

    /// <summary>
    /// Proportional rate for the final approach: maxSpeed = distance × rate.
    /// This linear limit dominates near the endpoint where the sqrt braking curve
    /// is too generous for discrete-time control.  Chosen so the ship reaches
    /// ≈0.5 speed at typical arrival thresholds (~14 units).
    /// </summary>
    private const double StopApproachRate = 0.035d;

    /// <summary>
    /// Distance threshold (in world units) at which destination-stop braking activates.
    /// Beyond this distance the ship only obeys curvature braking and the speed limit
    /// so it can maintain enough velocity to survive gravity wells mid-path.
    /// Roughly 3× the ideal stopping distance from speed limit.
    /// </summary>
    private const double StopBrakeActivationDistance = 500d;

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

        // Predict cumulative gravity over a lookahead window.
        // When gravity magnitude approaches or exceeds engine thrust, reduce the
        // compensation window so the engine keeps enough budget for along-track
        // thrust.  Without this, the ship stalls near strong gravity bodies and
        // spirals in.
        var (gx, gy) = ComputeGravityAcceleration(shipX, shipY, sources);
        var gravMag = Math.Sqrt(gx * gx + gy * gy);
        var effectiveLookahead = GravityLookaheadTicks;
        if (gravMag > 1e-9d)
        {
            // Limit compensation to at most half the engine budget
            var maxCompMag = maxThrust * 0.5d;
            var maxTicks = maxCompMag / gravMag;
            effectiveLookahead = Math.Min(effectiveLookahead, (int)Math.Max(1d, maxTicks));
        }

        // Decompose gravity into along-track and cross-track components.
        // Cross-track gravity that matches the upcoming turn direction acts as
        // free centripetal force — don't waste engine thrust fighting it.
        var gravAlongTrack = gx * toTargetX + gy * toTargetY;
        var gravCrossX = gx - gravAlongTrack * toTargetX;
        var gravCrossY = gy - gravAlongTrack * toTargetY;

        var gravCompCrossX = gravCrossX * effectiveLookahead;
        var gravCompCrossY = gravCrossY * effectiveLookahead;

        if (remainingPath is not null && remainingPath.Count >= 1)
        {
            // Determine the upcoming turn direction from the next path vertex.
            var nextDx = remainingPath[0].X - targetX;
            var nextDy = remainingPath[0].Y - targetY;
            // Cross-track component of the next-segment direction
            var nextAlong = nextDx * toTargetX + nextDy * toTargetY;
            var nextCrossX = nextDx - nextAlong * toTargetX;
            var nextCrossY = nextDy - nextAlong * toTargetY;
            var nextCrossMag = Math.Sqrt(nextCrossX * nextCrossX + nextCrossY * nextCrossY);
            var gravCrossMag = Math.Sqrt(gravCrossX * gravCrossX + gravCrossY * gravCrossY);

            if (nextCrossMag > 1e-9d && gravCrossMag > 1e-9d)
            {
                // cosine between cross-track gravity and turn direction:
                // +1 = perfectly aligned (gravity does the turn), -1 = opposes
                var alignment = (gravCrossX * nextCrossX + gravCrossY * nextCrossY) / (gravCrossMag * nextCrossMag);

                if (alignment > 0d)
                {
                    // Scale down cross-track gravity compensation proportional to alignment.
                    // At perfect alignment (1.0) → keep only 10% compensation (use gravity).
                    // At weak alignment (0.0) → keep 100% compensation.
                    var keepFraction = 1d - alignment * 0.9d;
                    gravCompCrossX *= keepFraction;
                    gravCompCrossY *= keepFraction;
                }
            }
        }

        // Always compensate cross-track gravity (possibly reduced by gravity assist above).
        // For along-track gravity: when it decelerates the ship and speed is above half the
        // limit, skip compensation — let the ship use its inertia to coast through gravity
        // wells.  This frees engine budget for cross-track control and acceleration.
        var alongTrackGravComp = gravAlongTrack * effectiveLookahead;
        var currentSpeed = Math.Sqrt(velX * velX + velY * velY);
        if (gravAlongTrack < 0d && currentSpeed > speedLimit * 0.5d)
        {
            // Gravity decelerates the ship, but we have enough speed — don't fight it,
            // prioritize path following.
            alongTrackGravComp = 0d;
        }

        var gravCompX = alongTrackGravComp * toTargetX + gravCompCrossX;
        var gravCompY = alongTrackGravComp * toTargetY + gravCompCrossY;

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

        // --- Combine with gravity-aware priority ---
        // Gravity compensation and cross-track correction (cancelX/Y) take priority
        // over along-track thrust.  When the engine budget is tight (gravity is a
        // large fraction of max thrust), uniform clamping would scale down gravity
        // compensation during braking, letting the ship drift into the gravity source.
        var cancelMag = Math.Sqrt(cancelX * cancelX + cancelY * cancelY);
        if (cancelMag >= maxThrust)
        {
            // Safety corrections saturate the engine — no budget for along-track.
            var s = maxThrust / Math.Max(cancelMag, 1e-9d);
            return (cancelX * s, cancelY * s);
        }

        // Limit along-track thrust to budget remaining after safety corrections.
        var thrustAlongMag = Math.Sqrt(thrustAlongX * thrustAlongX + thrustAlongY * thrustAlongY);
        var availableForAlong = maxThrust - cancelMag;
        if (thrustAlongMag > availableForAlong && thrustAlongMag > 1e-9d)
        {
            var sf = availableForAlong / thrustAlongMag;
            thrustAlongX *= sf;
            thrustAlongY *= sf;
        }

        var correctionX = cancelX + thrustAlongX;
        var correctionY = cancelY + thrustAlongY;

        // Final safety clamp — vectors can add constructively.
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
        // Compute total remaining distance (ship → target → all remaining path vertices).
        var totalRemainingDist = distToTarget;
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
        }

        // Only apply destination-stop braking when close enough to matter.
        // Far from the destination, the ship must maintain speed to survive
        // gravity wells along the path.  Curvature braking still applies.
        var vMaxForStop = speedLimit;
        if (totalRemainingDist < StopBrakeActivationDistance)
        {
            // Use gravity at the ship's current position (what it's fighting right now)
            // rather than gravity at the far-away endpoint.
            var (egx, egy) = ComputeGravityAcceleration(shipX, shipY, sources);
            var gravMag = Math.Sqrt(egx * egx + egy * egy);

            // Use the same adaptive lookahead as the engine vector computation
            // so the braking estimate matches actual gravity-compensation cost.
            var adaptiveLookahead = GravityLookaheadTicks;
            if (gravMag > 1e-9d)
            {
                var maxCompMag = maxThrust * 0.5d;
                var maxTicks = maxCompMag / gravMag;
                adaptiveLookahead = Math.Min(adaptiveLookahead, (int)Math.Max(1d, maxTicks));
            }

            var gravCompCost = Math.Min(gravMag * adaptiveLookahead, maxThrust * 0.7d);

            // Effective braking = thrust left after gravity compensation.
            var effectiveBraking = Math.Max(maxThrust - gravCompCost, maxThrust * 0.15d);

            // Two braking limits, take the tighter one:
            // 1) Physics sqrt curve:  v = sqrt(2 · a_eff · d)
            // 2) Proportional stop:   v = d · StopApproachRate  (dominates near the endpoint)
            var vMaxSqrt = Math.Sqrt(2d * effectiveBraking * totalRemainingDist);
            var vMaxProp = totalRemainingDist * StopApproachRate;
            vMaxForStop = Math.Min(vMaxSqrt, vMaxProp);

            // Gravity-based minimum speed floor: when gravity is a significant
            // fraction of engine thrust, the ship must maintain enough speed to
            // resist cross-track drift.  Without this floor, braking near gravity
            // wells leaves the ship too slow and gravity pulls it off-path.
            if (gravMag > maxThrust * 0.15d)
            {
                var gravityRatio = gravMag / maxThrust;
                var minGravSpeed = Math.Clamp(gravityRatio * speedLimit * 0.4d, 0.5d, speedLimit * 0.5d);
                vMaxForStop = Math.Max(vMaxForStop, minGravSpeed);
            }
        }

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

                    if (sinAngle > MinTurnSinAngle)
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
