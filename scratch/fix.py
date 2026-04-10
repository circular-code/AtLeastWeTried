import re

with open(r'backend\Gateway\Flattiverse.Gateway\Services\ManeuveringService.cs', 'r', encoding='utf-8') as f:
    ms_code = f.read()

# 1. Update PathfindingService.cs
with open(r'backend\Gateway\Flattiverse.Gateway\Services\Navigation\PathfindingService.cs', 'r', encoding='utf-8') as f:
    pf_code = f.read()

pf_code = pf_code.replace('(float)followResult.TargetTangent.X,\n            (float)followResult.TargetTangent.Y,', '(float)followResult.ClosestTangent.X,\n            (float)followResult.ClosestTangent.Y,')

pf_code = re.sub(
    r'return Math.Clamp\(ship.Size \* 7d, MinimumLookahead, MaximumLookahead\);',
    r'return Math.Clamp(ship.Movement.Length * 1.6d + ship.Size * 1.5d, MinimumLookahead * 0.4d, MaximumLookahead);',
    pf_code
)

with open(r'backend\Gateway\Flattiverse.Gateway\Services\Navigation\PathfindingService.cs', 'w', encoding='utf-8') as f:
    f.write(pf_code)

# 2. Add CalculateTotalGravity method
calc_gravity = '''
    private static Vector CalculateTotalGravity(ClassicShipControllable ship, Vector pos)
    {
        var totalGravity = new Vector();
        if (ship.Cluster != null && ship.Cluster.Units != null)
        {
            foreach (var unit in ship.Cluster.Units)
            {
                if (unit.Gravity <= 0f) continue;
                if (string.Equals(unit.Name, ship.Name, StringComparison.Ordinal)) continue;
                var toUnit = new Vector(unit.Position.X - pos.X, unit.Position.Y - pos.Y);
                if (IsNearZero(toUnit)) continue;
                totalGravity += BuildGravityDelta(toUnit, unit.Gravity);
            }
        }
        return totalGravity;
    }

    public void TrackShip'''

ms_code = ms_code.replace('    public void TrackShip', calc_gravity)


# 3. Replace gravity usages carefully
old_grav1 = 'var gravityTowardGoal = BuildGravityDelta(goalVector, stats.Gravity);'
new_grav1 = '''var g1 = CalculateTotalGravity(ship, new Vector(ship.Position.X, ship.Position.Y));
        var g2 = CalculateTotalGravity(ship, new Vector(goalX, goalY));
        var gravityTowardGoal = g1.Length > g2.Length ? g1 : g2;'''
ms_code = ms_code.replace(old_grav1, new_grav1)

old_grav2 = 'var gravityTowardGoal = BuildGravityDelta(desiredVector, stats.Gravity);'
new_grav2 = '''var g1 = CalculateTotalGravity(ship, new Vector(ship.Position.X, ship.Position.Y));
        var g2 = CalculateTotalGravity(ship, new Vector(state.TargetX, state.TargetY));
        var gravityTowardGoal = g1.Length > g2.Length ? g1 : g2;'''
ms_code = ms_code.replace(old_grav2, new_grav2)

old_grav3 = 'var gravityTowardGoal = BuildGravityDelta(goalVector, shipStats.Gravity);'
new_grav3 = '''var g1 = CalculateTotalGravity(ship, new Vector(ship.Position.X, ship.Position.Y));
        var g2 = CalculateTotalGravity(ship, new Vector(state.GoalX, state.GoalY));
        var gravityTowardGoal = g1.Length > g2.Length ? g1 : g2;'''
ms_code = ms_code.replace(old_grav3, new_grav3)

# 4. Turn Penalty
old_turn = '''var turnPenalty = targetDistance <= 0.01f
            ? 0f
            : Clamp01(Math.Abs(crossError) / (targetDistance + 1f));'''
new_turn = '''var curveDot = 1f;
        if (targetDistance > 0.01f && pathTangent.Length > 0.001f) {
            var dir = desiredVector / targetDistance;
            curveDot = Dot(dir, pathTangent);
        }
        var turnPenalty = Math.Clamp(1f - curveDot, 0f, 1f) * 1.5f + Clamp01(Math.Abs(crossError) / (targetDistance + 1f));
        turnPenalty = Math.Clamp(turnPenalty, 0f, 1f);'''
ms_code = ms_code.replace(old_turn, new_turn)

with open(r'backend\Gateway\Flattiverse.Gateway\Services\ManeuveringService.cs', 'w', encoding='utf-8') as f:
    f.write(ms_code)
