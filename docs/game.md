# Core game value calculations

Symbols
L_raw: raw structural load from all installed subsystems.

L_eff: effective structural load after the structure optimizer.

p: normalized power or rate, usually value / maximum.

d: distance, usually center distance or surface distance as stated below.

visibleShare: currently unmasked visible arc fraction of a scanned sun.

fullCost: the tier-dependent energy constant of the active subsystem.

Structure And Ship Stats
L_eff = L_raw * (1 - r_opt)
shipRadius = 1 + 47 * L_eff / 100
shipGravity = 0.01 + 0.11 * L_eff / 100
classicSpeedLimit = 6 - 2 * (L_eff / 100)0.8
modernSpeedLimit = 6.5 - 2 * (L_eff / 100)0.8
engineEfficiency = 1.2 - 0.45 * (L_eff / 100)0.85
r_opt is the current structure-optimizer reduction percent.

World Gravity And Soft Caps
dx = sourceX - targetX
dy = sourceY - targetY
d2 = dx * dx + dy * dy
if d2 > 3600: delta = (dx, dy) * gravity * 60 / d2
else if d2 > 0: delta = normalize(dx, dy) * gravity
else: delta = (gravity, 0)
if speed > speedLimit: newSpeed = speedLimit + 0.9 * (speed - speedLimit)
The gravity rule is used by normal gravity sources in the current runtime. The soft cap is the global post-movement limiter used for ships and projectiles with a speed limit.

Sun Transfer And Environment
surfaceDistance = max(0, centerDistance - sunRadius - shipRadius)
distanceFactor = 1 / (1 + surfaceDistance / 60)sqrt(2)
transferFactor = visibleShare * distanceFactor
if transferFactor < 0.01: no passive transfer
receivedEnergy = sunEnergy * transferFactor
receivedIons = sunIons * transferFactor
receivedNeutrinos = sunNeutrinos * transferFactor
receivedHeat = sunHeat * transferFactor
receivedDrain = sunDrain * transferFactor
heatEnergyCost = receivedHeat * 15
overflowHeat = max(0, heatEnergyCost - availableEnergy) / 15
radiationDamageBeforeArmor = (receivedDrain + overflowHeat) * 0.125
radiationHullDamage = max(0, radiationDamageBeforeArmor - armorReduction)
Cells, Batteries And Cargo
cellCollected = offeredResource * cellEfficiency
batteryDelta = min(batteryFree, cellCollected)
resourceStored = min(resourceFree, requestedResource)
nebulaStored = min(nebulaFree, requestedNebula)
Energy, ions and neutrinos are first filtered through their matching cell efficiency, then clamped by the matching battery free space. Cargo storage is clamped per resource channel.

Scanner
widthCost = 0.141176 * width - 0.705882
rangeCost = 0.3926 * length0.5 + 2.76e-10 * length4 - 0.617
scannerEnergy = max(0, widthCost + rangeCost)
tier5ScannerNeutrinos = scannerEnergy / 100
Runtime limits are width in [5, MaximumWidth] and length in [20, MaximumLength]. Classic scanners use an absolute world angle. Modern scanners use an angle offset around their fixed hull mount.

Engine-Like Cost Curve
p = value / maximum
cost = fullCost * (0.30 * p + 0.70 * p3)