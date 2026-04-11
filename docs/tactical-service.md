# TacticalService — Design Documentation

## Purpose

`TacticalService` is a **per-player-session** service that:

1. Stores the tactical intent (mode + target) for each controllable ship the session owns.
2. On every `GalaxyTickEvent`, evaluates whether any ship should auto-fire and queues up `AutoFireRequest` instances for the session handler to execute.
3. Exposes an API for setting modes, setting/clearing targets, building on-demand shot requests (burst fire, manual point shots), and producing UI overlay data.

---

## Tactical Modes

| Mode | Behaviour |
|---|---|
| `Off` | No auto-fire. |
| `Enemy` | Automatically selects the best enemy `PlayerUnit` visible in the same cluster. |
| `Target` | Fires at a specific pinned target identified by `TargetId`. |

---

## State Per Controllable

Each controllable is tracked by a string key with the format `p{playerId}-c{controllableId}` (both IDs are `byte`).

`TacticalState` holds:

| Field | Type | Description |
|---|---|---|
| `Mode` | `TacticalMode` | Current mode (Off / Enemy / Target). |
| `TargetId` | `string?` | Pinned target — either `p{id}-c{id}` for a player ship or a plain unit name. |
| `Ship` | `ClassicShipControllable?` | Live reference to the ship. Set to `null` when destroyed. |
| `LastFireTick` | `uint` | Tick of the last confirmed fire. |
| `HasLastFireTick` | `bool` | Whether `LastFireTick` is valid (avoids uint underflow on first check). |

---

## Concurrency

All mutable state is guarded by a single `object` lock (`_sync`).  
- `Handle(FlattiverseEvent)` is called on the **connector event loop**.  
- `SetMode`, `SetTarget`, `ClearTarget`, `AttachControllable`, etc. are called from **command / overlay paths**.

---

## Public API

| Method | Description |
|---|---|
| `Handle(FlattiverseEvent)` | Routes connector events. Destroyed → mark ship unavailable. Closed → remove controllable. GalaxyTick → run auto-fire evaluation. |
| `AttachControllable(id, ship)` | Registers or refreshes the ship reference for a controllable. |
| `DequeuePendingAutoFireRequests()` | Drains and returns all queued `AutoFireRequest` instances accumulated since the last call. |
| `SetMode(id, mode)` | Sets the tactical mode. Setting `Off` also clears `TargetId`. |
| `SetTarget(id, targetId)` | Sets the pinned target id for Target mode. |
| `ClearTarget(id)` | Clears the pinned target without changing mode. |
| `IsTargetAllowedForTargetMode(ship, targetId)` | Returns `true` if the target is a valid enemy (different team, or non-player unit). |
| `ShouldAutoFire(id, tick, out request)` | On-demand check: is a shot currently viable? Enforces cooldown. |
| `TryBuildTargetBurstRequest(id, tick, out request)` | Like `ShouldAutoFire` but skips the cooldown and requires Target mode. |
| `TryBuildPointShotRequest(id, ship, x, y, tick, out request)` | Builds a shot aimed at a specific world-space point. Ignores mode/target. |
| `RegisterSuccessfulFire(id, tick)` | Call after a shot command is accepted by the server to update the cooldown tracker. |
| `BuildOverlay(id)` | Returns `{ mode, targetId, hasTarget }` for the UI overlay. |
| `Remove(id)` | Removes all state for a controllable (e.g., when the session ends). |

---

## Auto-Fire Evaluation Flow (per GalaxyTick)

```
EvaluateAutoFire(tick)
  For each controllable in state dictionary:
    ShouldAutoFireCore(id, tick, gravityCache, enforceCooldown=true, requireTargetMode=false)
      → If viable: enqueue AutoFireRequest
```

Gravity sources are built once per cluster per tick and cached for the duration of the evaluation pass.

---

## Shot Viability Guard Checks (ShouldAutoFireCore)

The following must all pass before any trajectory computation begins:

1. State exists, mode != `Off`, `Ship` != null.
2. `requireTargetMode` → mode must be `Target`.
3. `Ship.Active && Ship.Alive`.
4. Ship is not in a rebuild state (`ControllableRebuildState.IsRebuilding`).
5. `ShotLauncher.Exists && ShotMagazine.Exists`.
6. Neither `ShotLauncher` nor `ShotMagazine` is `Upgrading`.
7. `ShotMagazine.CurrentShots >= 1`.
8. Cooldown: if enforced, `tick - LastFireTick >= 2` (skipped for burst/point shots).
9. A target can be resolved and passes team filter and basic range checks.

---

## Target Resolution

### Enemy Mode — `FindBestEnemyTarget`

Scans all units in the ship's cluster for enemy `PlayerUnit`s that are alive and scores each by:

```
score = distance
      + lateralSpeed * 22
      + max(0, closingSpeed) * 48
      - max(0, -closingSpeed) * 18
```

Prefers targets that are **close**, **not crossing laterally**, and **approaching**. Returns the unit with the lowest score.

### Target Mode — `ResolvePinnedTarget`

`TargetId` is interpreted as:
- `p{playerId}-c{controllableId}` → looks up the matching `PlayerUnit` in the cluster by player and controllable IDs.
- Plain string → looks up any unit in the cluster by `Name`.

### Target Validation (`IsTargetAllowedForTargetMode`)

If the target ID parses as a player controllable ID, the player's team is checked against the galaxy player list (even if not currently in cluster). Non-player units are always allowed. If the unit resolves in cluster, the team filter is applied.

---

## Ballistic Prediction Strategy

The tactical service needs to predict the ballistic missile, this includes the gravity of the location as well as the current velocity of the unit.

---

## Lifecycle Events

| Event | Action |
|---|---|
| `DestroyedControllableInfoEvent` | `Ship` reference is set to `null`. Pending requests for that controllable are purged from the queue. State record is kept (mode/target preserved). |
| `ClosedControllableInfoEvent` | State record is removed entirely. Pending requests are purged. |
| `AttachControllable` | Called when a new or respawned ship is registered. Updates the `Ship` reference. |
| `Remove` | Explicit teardown (session close). State and pending requests removed. |
