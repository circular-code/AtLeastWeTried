import json, math

with open(r"c:\Users\LeSch\Documents\Projects\temp\AtLeastWeTried\backend\Gateway\Flattiverse.Gateway.Host\data\world-state.wss-www-flattiverse-com-galaxies-0-api-b.fe66663304e80502.json") as f:
    data = json.load(f)

cx, cy = 657.45, 446.9  # last position before crash

print("Units within 80 units of crash position:")
for scope in data["scopes"]:
    for u in scope["staticUnits"]:
        dist = math.sqrt((u["x"] - cx) ** 2 + (u["y"] - cy) ** 2)
        if dist < 80:
            r = u["radius"]
            print(f"  {u['unitId']:30s} kind={u['kind']:12s} x={u['x']:8.1f} y={u['y']:8.1f} r={r:5.0f} dist={dist:6.1f} collide_at={r+14:.0f}")

# Also check along the actual ship path from (643,670) to (657,447)
print("\nUnits within 50 of the ship trajectory line (643,670)→(657,447):")
ax, ay = 643.0, 670.0
bx, by = 657.45, 446.9
dx, dy = bx - ax, by - ay
lenSq = dx * dx + dy * dy
for scope in data["scopes"]:
    for u in scope["staticUnits"]:
        px, py = u["x"] - ax, u["y"] - ay
        t = max(0, min(1, (px * dx + py * dy) / lenSq))
        closest_x = ax + t * dx
        closest_y = ay + t * dy
        dist = math.sqrt((u["x"] - closest_x) ** 2 + (u["y"] - closest_y) ** 2)
        if dist < 50:
            r = u["radius"]
            print(f"  {u['unitId']:30s} kind={u['kind']:12s} x={u['x']:8.1f} y={u['y']:8.1f} r={r:5.0f} seg_dist={dist:6.1f} collide_at={r+14:.0f}")
