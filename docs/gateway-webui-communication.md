# Flattiverse Gateway-Web Client Communication

## Purpose

This document specifies how the production web client should communicate with the Flattiverse gateway.

It covers:

- transport endpoints
- serialization rules
- message directions and payloads
- connection lifecycle and message ordering
- shared world state, detail overlays, and command routing
- tactical targeting and strategy-selection requirements for the production command surface
- error handling and recovery behavior
- production client requirements for consuming the protocol safely

This is a communication specification for the browser-facing gateway API. It is intended to be read alongside the broader client architecture document, but it should be usable on its own.

## Audience

This document is for engineers building:

- the production web client in `gui`
- gateway-to-browser reducers and selectors
- reconnect and session restoration behavior
- diagnostics and testing around the websocket contract

## Communication Model

The production web client communicates with the gateway over a single browser-facing WebSocket connection.

The gateway acts as the only realtime backend boundary for the browser. The web client does not communicate directly with upstream Flattiverse connector sessions.

The communication model is:

- HTTP for lightweight health and bootstrapping concerns
- WebSocket for realtime state, command routing, and server push

## Endpoints

### Health Endpoint

`GET /api/health`

Purpose:

- liveness check
- protocol-version discovery
- server-time reference for diagnostics

Response shape:

```json
{
  "status": "ok",
  "protocolVersion": "0.2.0",
  "serverTimeUtc": "2026-04-08T12:34:56.789Z"
}
```

### WebSocket Endpoint

`GET /ws`

Purpose:

- browser session establishment
- realtime state delivery
- player-session attach and selection
- command delivery and acknowledgement

If the request is not a WebSocket upgrade request, the gateway returns HTTP `400`.

## Serialization Rules

The gateway serializes JSON using the web defaults configured in ASP.NET Core.

Production client assumptions:

- property names are camelCase
- enum values are camelCase strings
- null properties may be omitted from serialized output
- every gateway message includes a top-level `type` discriminator

Example:

```json
{
  "type": "status",
  "kind": "warning",
  "code": "attach_required",
  "message": "Attach a Flattiverse API key to open or reuse a pooled player session.",
  "recoverable": true
}
```

## Protocol Versioning

The current gateway protocol version is `0.2.0`.

The web client should:

- surface the reported protocol version in diagnostics
- tolerate additive fields where possible
- treat unknown message types as unsupported protocol behavior
- avoid assuming property presence where null or omission is legal

## Message Directions

### Client to Gateway

Connection and session control:

- `connection.attach`
- `connection.detach`
- `player.select`
- `pong`

Production command namespace:

- `command.chat.send`
- `command.ship.create`
- `command.ship.destroy`
- `command.ship.continue`
- `command.ship.remove`
- `command.engine.set`
- `command.navigation.set_target`
- `command.navigation.clear_target`
- `command.scanner.configure`
- `command.scanner.set_active`
- `command.weapon.fire`
- `command.fabricator.set_rate`
- `command.fabricator.set_active`
- `command.subsystem.set_mode`
- `command.upgrade.begin`
- `command.upgrade.cancel`
- `command.upgrade.downgrade`
- `command.tactical.set_target`
- `command.tactical.clear_target`
- `command.tactical.set_strategy`
- `command.tactical.set_engagement_profile`
- `command.subsystem.set_target`
- `command.group.set_strategy`

### Gateway to Client

- `session.ready`
- `snapshot.full`
- `world.delta`
- `owner.delta`
- `chat.received`
- `command.reply`
- `status`
- `ping`

## Session Model

One browser WebSocket connection maps to one gateway browser session.

Within that browser session:

- multiple player sessions may be attached
- one attached player session may be selected at a time for command routing
- shared world state is delivered independently of which attached player session is selected
- detail overlay state is delivered in the current contract through `owner.delta`, scoped to a player-session context

Important distinction:

- visibility of shared gameplay information is a data-delivery concern
- command authority is a routing and permission concern

The web client must not infer command permission from visibility alone.

For the production system, command routing is not limited to simple direct actions. The communication layer should also support richer tactical intent such as:

- selecting hostile or allied targets
- assigning strategy or behavior modes
- choosing subsystem behavior per target or context
- issuing engagement decisions without requiring every UI action to map to a low-level actuator toggle

## Production Command Namespace

For the production system, command names should be grouped by subsystem instead of using a flat `command.*` list.

Recommended namespaces:

- `command.chat.*`
- `command.ship.*`
- `command.engine.*`
- `command.navigation.*`
- `command.scanner.*`
- `command.weapon.*`
- `command.fabricator.*`
- `command.subsystem.*`
- `command.upgrade.*`
- `command.tactical.*`
- `command.group.*`

This naming scheme makes the protocol easier to evolve because:

- related commands are grouped together
- the subsystem boundary is visible in the message name
- new functionality can be added without overloading generic names
- client reducer and command-dispatch code can be organized around the same namespaces

### Current-to-Production Command Mapping

The currently implemented gateway contract uses a flat command naming scheme. The production protocol should move to the following structure.

| Current contract | Production namespace |
| --- | --- |
| `command.chat` | `command.chat.send` |
| `command.create_ship` | `command.ship.create` |
| `command.set_engine` | `command.engine.set` |
| `command.set_navigation_target` | `command.navigation.set_target` |
| `command.clear_navigation_target` | `command.navigation.clear_target` |
| connector `DynamicScannerSubsystem.Set/On/Off` | `command.scanner.configure`, `command.scanner.set_active` |
| `command.fire_weapon` | `command.weapon.fire` |
| connector `DynamicShotFabricatorSubsystem.Set/On/Off` | `command.fabricator.set_rate`, `command.fabricator.set_active` |
| connector `DynamicInterceptorFabricatorSubsystem.Set/On/Off` | `command.fabricator.set_rate`, `command.fabricator.set_active` |
| `command.set_subsystem_mode` | `command.subsystem.set_mode` |
| connector `Subsystem.Upgrade/Downgrade` | `command.upgrade.begin`, `command.upgrade.downgrade` |
| `command.destroy_ship` | `command.ship.destroy` |
| `command.continue_ship` | `command.ship.continue` |
| `command.remove_ship` | `command.ship.remove` |

## Connection Lifecycle

### 1. Open WebSocket

On successful connection:

- the gateway creates a browser connection ID
- the gateway opens the runtime session
- the gateway immediately sends initial messages

Initial message ordering:

1. `session.ready`
2. if a selected attached player session already exists for the connection: `snapshot.full`
3. if a selected attached player session already exists for the connection: `owner.delta` with `overlay.snapshot`
4. otherwise: `status` with code `attach_required`

### 2. Idle Before Attach

If the browser has not attached a player session yet:

- the gateway can still report status
- no command routing is possible
- periodic ticks do not emit gameplay messages for that connection

### 3. Attach a Player Session

The browser sends `connection.attach` with a Flattiverse Player API key.

Successful attach response ordering:

1. `session.ready`
2. `snapshot.full`
3. `owner.delta` with `overlay.snapshot`
4. `status` with either `player_session_created` or `player_session_attached`

### 4. Select a Player Session

The browser sends `player.select` to change which attached player session receives command routing.

Successful select response ordering:

1. `session.ready`
2. `snapshot.full`
3. `owner.delta` with `overlay.snapshot`
4. `status` with `player_session_selected`

### 5. Detach a Player Session

The browser sends `connection.detach`.

Successful detach response ordering:

1. `session.ready`
2. `status` with `player_session_detached`
3. if another attached player session becomes selected: `snapshot.full`
4. if another attached player session becomes selected: `owner.delta` with `overlay.snapshot`

### 6. Close WebSocket

If the browser closes the socket:

- the gateway closes the WebSocket normally
- runtime state remains governed by the gateway runtime lifecycle

## Server Tick Model

The gateway runs a periodic tick every 40 ms.

On each tick, the gateway may:

- emit queued world and overlay updates
- emit command-related status messages
- emit navigation status updates

The web client must treat WebSocket delivery as asynchronous and bursty. Multiple messages may arrive back-to-back in a single short time window.

## State Channels

The gateway-to-web client protocol has three main gameplay channels plus diagnostics.

### 1. Session Channel

Message:

- `session.ready`

Purpose:

- identify the browser connection
- report protocol version
- report attached player sessions
- report which attached player session is currently selected

Example:

```json
{
  "type": "session.ready",
  "connectionId": "7fb6dc2285414b4ba0a31fa1cf0a9e5d",
  "protocolVersion": "0.2.0",
  "observerOnly": false,
  "playerSessions": [
    {
      "playerSessionId": "player-01",
      "displayName": "Aurora Wing",
      "connected": true,
      "selected": true,
      "teamName": "Blue"
    }
  ]
}
```

Client rules:

- replace session metadata on every `session.ready`
- recompute selected authority scope from this message
- do not keep stale selected-session assumptions after a new `session.ready`

### 2. Shared World Channel

Messages:

- `snapshot.full`
- `world.delta`

#### `snapshot.full`

Purpose:

- provide the authoritative full shared world snapshot
- hydrate the world store
- reset world state after attach, select, or recovery flows

Example:

```json
{
  "type": "snapshot.full",
  "snapshot": {
    "name": "Beginner Galaxy",
    "description": "A starter galaxy.",
    "gameMode": "classic",
    "teams": [],
    "clusters": [],
    "units": [],
    "controllables": []
  }
}
```

Client rules:

- replace the entire shared-world store on receipt
- discard assumptions from prior snapshots
- rebuild derived indexes from the new snapshot

#### `world.delta`

Purpose:

- incrementally update the shared world after the initial snapshot

Current event types emitted by the gateway runtime:

- `unit.created`
- `unit.updated`
- `unit.removed`

Example:

```json
{
  "type": "world.delta",
  "events": [
    {
      "eventType": "unit.updated",
      "entityId": "unit-42",
      "changes": {
        "clusterId": 2,
        "kind": "ship",
        "x": 124.5,
        "y": -88.2,
        "angle": 90,
        "radius": 6,
        "teamName": "Blue"
      }
    }
  ]
}
```

Client rules:

- apply deltas only after a world snapshot exists
- update normalized entities by ID
- treat omitted properties as unchanged unless the event contract explicitly says otherwise
- remove entities on `unit.removed`

### 3. Detail Overlay Channel

Message:

- `owner.delta`

Purpose:

- deliver higher-detail controllable state for a specific player-session context

Envelope shape:

```json
{
  "type": "owner.delta",
  "playerSessionId": "player-01",
  "events": []
}
```

Current runtime behavior:

- the envelope name is `owner.delta`
- the payload is a list of controllable-specific events
- the runtime frequently emits full-controllable snapshots inside that envelope, not just tiny field patches

Common event types currently emitted:

- `overlay.snapshot`
- `overlay.tick`
- `engine.updated`
- `weapon.updated`
- `navigation.updated`
- `scanner.updated`
- `fabricator.updated`
- `upgrade.updated`
- `subsystem.updated`

Example:

```json
{
  "type": "owner.delta",
  "playerSessionId": "player-01",
  "events": [
    {
      "eventType": "overlay.snapshot",
      "controllableId": "ship-01",
      "changes": {
        "displayName": "Aurora Wing",
        "kind": "classic_ship",
        "alive": true,
        "clusterId": 1,
        "clusterName": "Home",
        "position": {
          "x": 10,
          "y": 20,
          "angle": 45
        },
        "navigation": {
          "active": true,
          "targetX": 100,
          "targetY": 50,
          "status": "active"
        }
      }
    }
  ]
}
```

Client rules:

- maintain detail overlay state keyed by `playerSessionId` and `controllableId`
- treat `overlay.snapshot` as authoritative replacement for that player-session overlay set
- treat later non-snapshot events as authoritative upserts for the addressed controllables
- clear or recompute authority-scoped detail state when the selected player-session context changes

### 4. Chat Channel

Message:

- `chat.received`

Purpose:

- deliver chat entries routed through the gateway

Example:

```json
{
  "type": "chat.received",
  "entry": {
    "messageId": "chat-a1b2c3",
    "scope": "galaxy",
    "senderDisplayName": "Pilot One",
    "playerSessionId": null,
    "message": "Hello galaxy",
    "sentAtUtc": "2026-04-08T12:34:56.789Z"
  }
}
```

Client rules:

- append to chat history in arrival order or desired UI order
- do not use chat as authority for gameplay state

### 5. Diagnostics and Command Feedback Channel

Messages:

- `status`
- `command.reply`
- `ping`

#### `status`

Purpose:

- report recoverable or fatal runtime and protocol conditions

Example:

```json
{
  "type": "status",
  "kind": "warning",
  "code": "invalid_api_key",
  "message": "API keys must be exactly 64 hexadecimal characters.",
  "recoverable": true
}
```

Observed status categories include:

- `attach_required`
- `invalid_api_key`
- `team_mismatch`
- `player_session_unavailable`
- `player_session_created`
- `player_session_attached`
- `player_session_detached`
- `player_session_selected`
- `unsupported_message_type`
- `invalid_json`
- `missing_type`
- `invalid_message_shape`
- `unknown_message_type`
- runtime connection termination and navigation status codes

Client rules:

- treat `status` as UX diagnostics, not as world state
- surface `recoverable` vs non-recoverable conditions clearly
- preserve important status events in diagnostics history

#### `command.reply`

Purpose:

- acknowledge command acceptance, completion, or rejection

Example successful completion:

```json
{
  "type": "command.reply",
  "commandId": "cmd-123",
  "status": "completed",
  "result": {
    "controllableId": "ship-01",
    "action": "navigation_target_set",
    "targetX": 100,
    "targetY": 50
  }
}
```

Example rejection:

```json
{
  "type": "command.reply",
  "commandId": "cmd-124",
  "status": "rejected",
  "error": {
    "code": "missing_player_session",
    "message": "Attach and select a player session before sending commands.",
    "recoverable": true
  }
}
```

Client rules:

- correlate replies by `commandId`
- keep command lifecycle state separate from world state
- do not assume a completed command reply is the final authority on world state
- wait for subsequent snapshot or overlay updates to update rendered gameplay state

#### `ping` and `pong`

Purpose:

- keep the websocket session healthy

Client behavior:

- when the gateway sends `ping`, the client should respond with `pong`
- `pong` has no additional payload

Example:

```json
{
  "type": "pong"
}
```

## Client-to-Gateway Messages

### `connection.attach`

Purpose:

- attach or reuse a pooled player session using a Player API key

Payload:

```json
{
  "type": "connection.attach",
  "payload": {
    "apiKey": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    "teamName": "Blue"
  }
}
```

Requirements:

- `apiKey` must be a 64-character hexadecimal string
- `teamName` is optional

### `connection.detach`

Purpose:

- detach a previously attached player session from the browser connection

```json
{
  "type": "connection.detach",
  "playerSessionId": "player-01"
}
```

### `player.select`

Purpose:

- select which attached player session receives command routing

```json
{
  "type": "player.select",
  "playerSessionId": "player-01"
}
```

### Command Messages

All command messages share these rules:

- they include a `commandId`
- they are routed through the currently selected attached player session
- if no selected attached player session exists, the gateway rejects the command

For the production system, the command surface should support two layers of intent:

- direct control commands for immediate low-level actions
- tactical intent commands for target selection, strategy selection, and engagement behavior

The current contract already contains some hooks for richer behavior through fields such as `targetId`, `mode`, and `value`. The production protocol should build on that rather than forcing all advanced behavior into client-only heuristics.

The connector also exposes direct subsystem control surfaces that the gateway can wrap without leaking connector-specific packet handling into the browser contract. In particular:

- scanner subsystems expose active state plus target width, length, and angle
- fabricator subsystems expose active state plus configurable fabrication rate
- subsystem slots expose tier, target tier, and remaining upgrade ticks for upgrade workflows

The following command catalog uses the recommended production namespace.

### Chat Commands

#### `command.chat.send`

```json
{
  "type": "command.chat.send",
  "commandId": "cmd-chat-1",
  "payload": {
    "scope": "galaxy",
    "message": "Hello galaxy",
    "recipientPlayerSessionId": null
  }
}
```

### Ship Commands

#### `command.ship.create`

```json
{
  "type": "command.ship.create",
  "commandId": "cmd-create-1",
  "payload": {
    "shipClass": "modern",
    "name": "Aurora Wing",
    "crystalNames": []
  }
}
```

#### `command.ship.destroy`

```json
{
  "type": "command.ship.destroy",
  "commandId": "cmd-destroy-1",
  "payload": {
    "controllableId": "ship-01"
  }
}
```

#### `command.ship.continue`

```json
{
  "type": "command.ship.continue",
  "commandId": "cmd-continue-1",
  "payload": {
    "controllableId": "ship-01"
  }
}
```

#### `command.ship.remove`

```json
{
  "type": "command.ship.remove",
  "commandId": "cmd-remove-1",
  "payload": {
    "controllableId": "ship-01"
  }
}
```

### Engine Commands

#### `command.engine.set`

```json
{
  "type": "command.engine.set",
  "commandId": "cmd-engine-1",
  "payload": {
    "controllableId": "ship-01",
    "engineId": "main_engine",
    "thrust": 0.5,
    "x": 0.5,
    "y": 0.0
  }
}
```

### Navigation Commands

#### `command.navigation.set_target`

```json
{
  "type": "command.navigation.set_target",
  "commandId": "cmd-nav-1",
  "payload": {
    "controllableId": "ship-01",
    "targetX": 120.0,
    "targetY": -48.0
  }
}
```

#### `command.navigation.clear_target`

```json
{
  "type": "command.navigation.clear_target",
  "commandId": "cmd-nav-clear-1",
  "payload": {
    "controllableId": "ship-01"
  }
}
```

### Scanner Commands

#### `command.scanner.configure`

Purpose:

- configure a scanner subsystem's target scan cone and orientation
- allow the gateway to abstract underlying scanner IDs or connector command opcodes behind stable browser-facing names

```json
{
  "type": "command.scanner.configure",
  "commandId": "cmd-scanner-config-1",
  "payload": {
    "controllableId": "ship-01",
    "scannerId": "primary_scanner",
    "width": 45,
    "length": 180,
    "angle": 90
  }
}
```

Production interpretation:

- `scannerId` identifies the logical scanner instance such as primary or secondary scanner
- `width`, `length`, and `angle` represent the target scan configuration rather than a transient camera gesture
- the gateway may translate `scannerId` to connector-specific subsystem IDs internally

#### `command.scanner.set_active`

Purpose:

- turn a scanner subsystem on or off without changing its stored target geometry

```json
{
  "type": "command.scanner.set_active",
  "commandId": "cmd-scanner-active-1",
  "payload": {
    "controllableId": "ship-01",
    "scannerId": "primary_scanner",
    "active": true
  }
}
```

### Weapon Commands

#### `command.weapon.fire`

```json
{
  "type": "command.weapon.fire",
  "commandId": "cmd-fire-1",
  "payload": {
    "controllableId": "ship-01",
    "weaponId": "main_weapon",
    "relativeAngle": 0,
    "targetId": null
  }
}
```

Production interpretation:

- `targetId` should be treated as a first-class targeting input when the weapon system supports guided or target-aware behavior
- when `targetId` is omitted, the gateway may interpret the command as manual fire, directional fire, or weapon-default behavior depending on the selected strategy model

### Fabricator Commands

#### `command.fabricator.set_rate`

Purpose:

- configure the production rate of a fabricator subsystem
- support gateway abstractions for shot, interceptor, or future shipyard-style fabricators through a common command family

```json
{
  "type": "command.fabricator.set_rate",
  "commandId": "cmd-fabricator-rate-1",
  "payload": {
    "controllableId": "ship-01",
    "fabricatorId": "shot_fabricator",
    "rate": 0.02
  }
}
```

Production interpretation:

- `fabricatorId` identifies the logical fabricator instance, for example `shot_fabricator` or `interceptor_fabricator`
- the gateway may clamp or reject invalid rates based on the addressed subsystem capabilities
- the same browser-facing command family may later wrap higher-level fabrication stations that contribute to ship construction or refit flows

#### `command.fabricator.set_active`

Purpose:

- turn a fabricator subsystem on or off without changing the requested fabrication rate

```json
{
  "type": "command.fabricator.set_active",
  "commandId": "cmd-fabricator-active-1",
  "payload": {
    "controllableId": "ship-01",
    "fabricatorId": "shot_fabricator",
    "active": true
  }
}
```

### Subsystem Commands

#### `command.subsystem.set_mode`

```json
{
  "type": "command.subsystem.set_mode",
  "commandId": "cmd-subsystem-1",
  "payload": {
    "controllableId": "ship-01",
    "subsystemId": "primary_scanner",
    "mode": "off",
    "value": null,
    "targetId": null
  }
}
```

Production interpretation:

- `mode` should support more than simple on or off semantics
- `targetId` should allow subsystem behavior to focus on a specific enemy, ally, or objective when relevant
- `value` should be used for weighted or parameterized strategy choices rather than only binary toggles

### Upgrade Commands

#### `command.upgrade.begin`

Purpose:

- start an upgrade step for a subsystem slot on a controllable
- allow the gateway to expose connector tier-change behavior as a typed browser command

```json
{
  "type": "command.upgrade.begin",
  "commandId": "cmd-upgrade-1",
  "payload": {
    "controllableId": "ship-01",
    "subsystemId": "primary_scanner",
    "targetTier": 2
  }
}
```

Production interpretation:

- the gateway may translate `subsystemId` to a concrete subsystem slot before calling the connector
- if the underlying backend supports only single-step upgrades, the gateway may interpret `targetTier` as the desired terminal tier and schedule repeated single-step operations internally, or reject requests that skip tiers
- the browser should treat upgrade start as a workflow request, not as immediate tier authority

#### `command.upgrade.cancel`

Purpose:

- cancel or pause a pending gateway-managed upgrade workflow when the gateway chooses to expose that abstraction

```json
{
  "type": "command.upgrade.cancel",
  "commandId": "cmd-upgrade-cancel-1",
  "payload": {
    "controllableId": "ship-01",
    "subsystemId": "primary_scanner"
  }
}
```

#### `command.upgrade.downgrade`

Purpose:

- start one downgrade step for a subsystem slot when supported by the gateway and gameplay rules

```json
{
  "type": "command.upgrade.downgrade",
  "commandId": "cmd-upgrade-down-1",
  "payload": {
    "controllableId": "ship-01",
    "subsystemId": "primary_scanner"
  }
}
```

## Command Routing and Authority Rules

The gateway runtime enforces these rules:

- a command requires a selected attached player session
- the selected session must still exist and be connected
- the addressed controllable must exist in that selected session
- unsupported controllable or subsystem types produce command rejection

The production client must enforce these same constraints at the UX layer where possible, but gateway enforcement remains authoritative.

One user must never be able to issue commands through another user's authority scope.

For scanner, fabricator, and upgrade workflows, the gateway should additionally enforce these rules:

- the addressed subsystem must exist on the selected controllable
- requested geometry, rate, and target-tier values must be validated against current subsystem capabilities
- a pending upgrade must be reflected from gateway state before the browser treats the new tier as active
- gateway abstractions must prevent one subsystem command from mutating unrelated subsystem slots implicitly unless the command contract says so

## Production Tactical Command Model

For the production client, the gateway protocol should support a richer tactical layer than the minimal current command set.

The desired interaction model is:

- the user can select one or more enemy targets
- the user can choose an engagement or behavior strategy
- the user can direct weapons, navigation, scanners, shields, and other subsystems using tactical intent instead of only low-level direct commands

This should be reflected in the communication protocol, not only in client-side UI state.

### Tactical Concepts

The protocol should support these concepts explicitly.

#### 1. Target Selection

The client should be able to communicate:

- selected target unit
- target controllable
- target cluster or point of interest
- cleared target state
- multi-target priority lists where the gameplay model supports them

Target selection should be durable state, not just a transient click event.

#### 2. Strategy Selection

The client should be able to communicate named or typed strategies such as:

- attack nearest enemy
- focus fire selected target
- maintain range
- orbit target
- evade and disengage
- escort ally
- defend objective
- scout and observe
- conserve energy
- prioritize shield recovery

The gateway should own the authoritative interpretation of these strategies.

#### 3. Parameterized Tactical Intent

Strategies often need additional parameters. The communication model should support:

- preferred target ID
- preferred engagement range
- aggression level
- threat-priority weighting
- subsystem preference
- retreat threshold
- fire authorization rules

These values should be explicit in the protocol rather than hidden in frontend-only logic.

## Recommended Production Protocol Extensions

The current contract can carry some advanced intent already, but the production system should consider adding explicit tactical messages.

Recommended additions:

- `command.tactical.set_target`
- `command.tactical.clear_target`
- `command.tactical.set_strategy`
- `command.tactical.set_engagement_profile`
- `command.scanner.configure`
- `command.scanner.set_active`
- `command.fabricator.set_rate`
- `command.fabricator.set_active`
- `command.upgrade.begin`
- `command.upgrade.cancel`
- `command.upgrade.downgrade`
- `command.subsystem.set_target`
- `command.group.set_strategy`

These names are recommended design directions, not statements about the currently implemented gateway surface.

### Example: `command.tactical.set_target`

Purpose:

- set the primary target for a controllable or command group

```json
{
  "type": "command.tactical.set_target",
  "commandId": "cmd-target-1",
  "payload": {
    "controllableId": "ship-01",
    "targetId": "enemy-ship-22",
    "targetKind": "unit",
    "lockMode": "soft"
  }
}
```

### Example: `command.tactical.clear_target`

Purpose:

- clear an existing tactical target assignment

```json
{
  "type": "command.tactical.clear_target",
  "commandId": "cmd-target-clear-1",
  "payload": {
    "controllableId": "ship-01"
  }
}
```

### Example: `command.tactical.set_strategy`

Purpose:

- assign a named strategy to a controllable or tactical controller

```json
{
  "type": "command.tactical.set_strategy",
  "commandId": "cmd-strategy-1",
  "payload": {
    "controllableId": "ship-01",
    "strategy": "focus_fire",
    "targetId": "enemy-ship-22",
    "parameters": {
      "preferredRange": 120,
      "aggression": 0.8,
      "breakOffHullRatio": 0.25
    }
  }
}
```

### Example: `command.tactical.set_engagement_profile`

Purpose:

- set reusable tactical behavior without changing the currently selected target

```json
{
  "type": "command.tactical.set_engagement_profile",
  "commandId": "cmd-engagement-1",
  "payload": {
    "controllableId": "ship-01",
    "profile": "defensive_kiting",
    "parameters": {
      "minimumRange": 80,
      "maximumRange": 160,
      "shieldRetreatRatio": 0.35
    }
  }
}
```

## Strategy and Target State Delivery

If the gateway accepts tactical target or strategy commands, the resulting state should be reflected back to the client through detail overlay updates or explicit tactical-state messages.

The browser should never assume that a requested target or strategy is active until the gateway reflects that state back.

Recommended reflected state includes:

- current target ID
- target lock state
- active strategy name
- strategy parameters
- reason a strategy was refused or downgraded
- strategy execution status such as active, blocked, suspended, or completed
- scanner active state, current geometry, and target geometry
- fabricator active state, configured rate, and current consumption
- subsystem tier, target tier, and remaining upgrade ticks for upgrade workflows

Example reflected tactical state in an overlay payload:

```json
{
  "tactical": {
    "targetId": "enemy-ship-22",
    "strategy": "focus_fire",
    "status": "active",
    "parameters": {
      "preferredRange": 120,
      "aggression": 0.8
    }
  }
}
```

Example reflected subsystem-control state in an overlay payload:

```json
{
  "scanner": {
    "primaryScanner": {
      "active": true,
      "currentWidth": 40,
      "currentLength": 160,
      "currentAngle": 85,
      "targetWidth": 45,
      "targetLength": 180,
      "targetAngle": 90,
      "status": "active"
    }
  },
  "fabricator": {
    "shotFabricator": {
      "active": true,
      "rate": 0.02,
      "status": "active"
    }
  },
  "upgrades": {
    "primaryScanner": {
      "tier": 1,
      "targetTier": 2,
      "remainingTicks": 84,
      "pending": true
    }
  }
}
```

## Production Client Requirements for Tactical UX

The production client should support these interaction patterns:

- select a visible entity as the current tactical target
- clear or change target without losing unrelated world state
- choose a strategy from an explicit strategy catalog
- display the currently active strategy and target as acknowledged by the gateway
- distinguish requested strategy from active strategy when the gateway has not yet confirmed the change
- disable or warn on strategy choices that the current controllable cannot support
- configure scanner search geometry without relying on frontend-only local state
- toggle scanner and fabricator activity explicitly through gateway commands
- display upgrade progress per subsystem slot using reflected target-tier and remaining-ticks state

## Additional Ordering Rules for Tactical State

### Rule 7: Requested Tactical State Is Not Active Until Reflected

Sending `command.tactical.set_target`, `command.tactical.set_strategy`, or other tactical intent does not make that state authoritative immediately.

The client should wait for:

- a `command.reply` indicating acceptance or completion
- and the resulting reflected tactical state in overlay or tactical-state updates

### Rule 8: Strategy and Target State Must Survive UI Reordering

If the user changes panels, selections, or camera state, the active tactical target and strategy should remain anchored in the underlying authority-scoped state rather than ephemeral UI state.

### Rule 9: Subsystem Control Is Only Active After Reflection

Sending `command.scanner.configure`, `command.scanner.set_active`, `command.fabricator.set_rate`, `command.fabricator.set_active`, or `command.upgrade.begin` does not immediately change authoritative subsystem state.

The client should wait for:

- a `command.reply` indicating acceptance or completion
- and the resulting reflected subsystem state in `owner.delta` updates

### Rule 10: Upgrade State Is Workflow State

Tier upgrades and downgrades should be modeled as workflow state, not as instantaneous property replacement.

The client should preserve and display:

- current tier
- target tier
- remaining upgrade ticks
- pending, active, failed, or completed upgrade status

## Ordering and State Application Rules

The production client should implement these rules exactly.

### Rule 1: `session.ready` Replaces Session Metadata

Treat every `session.ready` as authoritative for:

- attached player sessions
- current selected player session
- connection metadata
- observer-only state

### Rule 2: `snapshot.full` Replaces Shared World State

Do not merge full snapshots into prior world state. Replace the shared-world model and rebuild indexes.

### Rule 3: `world.delta` Applies Incrementally

Apply `world.delta` only to an initialized shared-world store.

### Rule 4: `overlay.snapshot` Replaces Detail Overlay State

When an `owner.delta` batch contains `overlay.snapshot` events, the client should treat that batch as authoritative replacement for that player-session detail overlay set.

### Rule 5: Command Replies Do Not Replace World State

`command.reply` is for command lifecycle and UX feedback. It does not replace later world or overlay updates.

### Rule 6: Visibility Does Not Imply Authority

A user may see shared entity state without being allowed to issue commands for that entity.

## Error Handling

The web client should handle these protocol-level problems explicitly:

- invalid JSON sent by the browser
- missing `type` field
- unknown message type
- unsupported WebSocket frame type
- invalid message shape
- invalid API key
- selecting or detaching an unknown player session
- sending commands without an attached and selected session
- sending scanner, fabricator, or upgrade commands for unsupported subsystem slots
- requesting scanner geometry or fabrication rates outside subsystem limits
- requesting upgrades that violate gateway progression rules

Recommended client behavior:

- show recoverable errors in activity or diagnostics UI
- keep the socket open when the gateway marks the issue recoverable
- block or disable actions that the client already knows will be rejected
- surface non-recoverable configuration issues clearly

## Reconnect Behavior

The protocol does not itself guarantee automatic session restoration at the browser level.

The production client should define reconnect behavior explicitly:

- reopen the WebSocket
- wait for fresh `session.ready`
- restore persisted attach intents if the product requires that workflow
- rebuild session, world, overlay, and authority stores from fresh gateway messages
- avoid replaying stale pending commands automatically

Recommended reconnect rule:

- restore session attachment intent
- do not replay gameplay commands automatically

## Production Client Implementation Requirements

The production web client should:

- generate TypeScript contract types from the gateway C# contracts
- keep transport code separate from reducers and selectors
- maintain normalized stores for world, detail overlay, authority, commands, activity, and UI state
- use `commandId` correlation for command lifecycle tracking
- respond to `ping` with `pong`
- treat `overlay.snapshot` as replacement, not merge-only state
- distinguish shared informational state from control authority
- preserve raw API key persistence only as an explicit product decision, not as an accidental side effect
- model scanner, fabricator, and upgrade flows as authority-scoped subsystem workflows rather than transient widget state

## Testing Checklist

### Protocol Correctness

- websocket open emits `session.ready`
- unattached connection receives `attach_required`
- attach emits `session.ready`, `snapshot.full`, `owner.delta`, and success status
- select emits `session.ready`, `snapshot.full`, `owner.delta`, and selection status
- detach updates `session.ready` and selection state correctly
- `ping` is answered with `pong`

### State Semantics

- `snapshot.full` replaces world state
- `world.delta` mutates world state incrementally
- `overlay.snapshot` replaces overlay state
- later overlay updates upsert addressed controllables
- command replies do not directly mutate world state

### Authority and Isolation

- commands are rejected without a selected session
- commands are rejected for unknown controllables
- one browser client cannot command another browser client's unattached session
- shared informational state may remain visible while command authority stays isolated
- target and strategy selections remain scoped to the issuing user's authority context
- scanner, fabricator, and upgrade commands remain scoped to subsystem instances owned by the issuing authority context

### Tactical Behavior

- target selection is reflected back from the gateway before the UI treats it as active
- strategy selection is reflected back from the gateway before the UI treats it as active
- target-aware weapon or subsystem commands honor `targetId` when supported
- unsupported strategy or targeting combinations produce explicit command rejection or downgrade feedback

### Subsystem Control Behavior

- scanner configuration is reflected back from the gateway before the UI treats the new scan cone as active
- scanner on or off transitions are reflected back from the gateway before the UI treats the scanner state as authoritative
- fabricator rate and active-state changes are reflected back from the gateway before the UI treats them as active
- upgrade start requests expose reflected tier-progress state instead of silently mutating the displayed tier
- invalid subsystem identifiers, rates, geometry, or target tiers produce explicit command rejection

### Recovery

- reconnect rebuilds state from gateway messages
- stale local command state does not leak across reconnect
- diagnostics remain available after recoverable failures

## Summary

For the production client, gateway-web communication should be implemented as a typed, state-driven websocket protocol with four separate concerns:

- shared world synchronization
- detail overlay synchronization
- command authority and routing
- diagnostics and command lifecycle feedback

For the full production system, the command layer should also support tactical intent:

- target selection
- strategy selection
- reflected tactical state from the gateway

It should also support direct control of higher-value ship systems through typed subsystem command families:

- scanner configuration and activation
- fabricator rate and activation control
- subsystem-tier upgrade and downgrade workflows

The most important implementation rule is that the browser must separate what it can see from what it is allowed to control.