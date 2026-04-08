# Flattiverse Gateway — Backend Architecture

## Purpose

This document defines the internal architecture of the ASP.NET Core gateway that sits between the browser-facing WebSocket API and the upstream Flattiverse Connector.

It should be read alongside:

- `docs/gateway-webui-communication.md` — the full browser-facing protocol specification
- `docs/production-client-architecture.md` — the browser client architecture

## Constraints

- Use the official Flattiverse C# Connector (`Flattiverse.Connector`)
- Expose a WebSocket interface to the web GUI as documented in `gateway-webui-communication.md`
- The Connector's `Galaxy.NextEvent()` is **not concurrent-safe** — a single consumer loop per Galaxy connection is mandatory
- The gateway must support multiple simultaneous browser connections sharing the same upstream Connector sessions
- The gateway tick interval is 40 ms
- JSON serialization follows ASP.NET Core web defaults (camelCase, camelCase enums, null omission)

## High-Level Architecture

```
┌──────────────────────────────────────────────────────────┐
│                    ASP.NET Core Host                      │
│                                                          │
│  ┌────────────────────────────────────────────────────┐  │
│  │              HTTP / WebSocket Layer                 │  │
│  │  GET /api/health          GET /ws (upgrade)        │  │
│  └──────────────────┬─────────────────────────────────┘  │
│                     │                                    │
│  ┌──────────────────▼─────────────────────────────────┐  │
│  │           Browser Session Manager                  │  │
│  │  • BrowserConnection per WebSocket                 │  │
│  │  • tracks attached PlayerSessions per connection   │  │
│  │  • routes inbound commands to selected session     │  │
│  │  • fans out world/overlay/chat messages            │  │
│  └──────────────────┬─────────────────────────────────┘  │
│                     │                                    │
│  ┌──────────────────▼─────────────────────────────────┐  │
│  │              Player Session Pool                   │  │
│  │  • one PlayerSession per attached API key          │  │
│  │  • pooled: multiple browser connections can share  │  │
│  │  • owns the Controllable command surface           │  │
│  │  • bridges browser commands → Connector calls      │  │
│  └──────────────────┬─────────────────────────────────┘  │
│                     │                                    │
│  ┌──────────────────▼─────────────────────────────────┐  │
│  │           Galaxy Connection Manager                │  │
│  │  • one Galaxy instance per upstream connection     │  │
│  │  • single NextEvent() consumer loop per Galaxy     │  │
│  │  • dispatches Connector events → services          │  │
│  └──────────────────┬─────────────────────────────────┘  │
│                     │                                    │
│  ┌──────────────────▼─────────────────────────────────┐  │
│  │                  Services                          │  │
│  │  Mapping · Scanning · Maneuvering · Pathfinding    │  │
│  │  Tactical · Chat · Tick                            │  │
│  └────────────────────────────────────────────────────┘  │
│                                                          │
└──────────────────────────────────────────────────────────┘
```

## Solution Structure

```
backend/
├── Flattiverse.Gateway.slnx
├── Connector/                          # git submodule — official Connector
│   └── Flattiverse.Connector/
│       └── Flattiverse.Connector/
│           └── Flattiverse.Connector.csproj
└── Gateway/
    ├── Flattiverse.Gateway/            # core library (no host dependency)
    │   ├── Flattiverse.Gateway.csproj
    │   ├── Protocol/                   # browser-facing DTOs and message types
    │   │   ├── ServerMessages/         # session.ready, snapshot.full, world.delta, ...
    │   │   ├── ClientMessages/         # connection.attach, command.*, pong, ...
    │   │   └── Dtos/                   # shared DTOs (UnitSnapshotDto, etc.)
    │   ├── Sessions/                   # browser connection and player session management
    │   │   ├── BrowserConnection.cs
    │   │   ├── BrowserSessionManager.cs
    │   │   ├── PlayerSession.cs
    │   │   └── PlayerSessionPool.cs
    │   ├── Connector/                  # Galaxy connection lifecycle
    │   │   ├── GalaxyConnectionManager.cs
    │   │   └── ConnectorEventLoop.cs
    │   ├── Services/                   # game-logic services
    │   │   ├── MappingService.cs
    │   │   ├── ScanningService.cs
    │   │   ├── ManeuveringService.cs
    │   │   ├── PathfindingService.cs
    │   │   ├── TacticalService.cs
    │   │   ├── ChatService.cs
    │   │   └── TickService.cs
    │   └── State/                      # shared world state containers
    │       ├── WorldSnapshot.cs
    │       └── OverlayState.cs
    ├── Flattiverse.Gateway.Host/       # ASP.NET Core host (thin entry point)
    │   ├── Flattiverse.Gateway.Host.csproj
    │   ├── Program.cs
    │   ├── HealthEndpoint.cs
    │   └── WebSocketEndpoint.cs
    └── Flattiverse.Gateway.Tests/      # unit and integration tests
        └── Flattiverse.Gateway.Tests.csproj
```

### Project Separation Rationale

| Project | Responsibility |
|---|---|
| `Flattiverse.Gateway` | All domain logic, protocol types, services, session management. No ASP.NET hosting dependency. Testable in isolation. |
| `Flattiverse.Gateway.Host` | ASP.NET Core entry point. Wires DI, maps HTTP endpoints, accepts WebSocket upgrades, delegates to the core library. |
| `Flattiverse.Gateway.Tests` | Tests against both projects. |

## Component Detail

### 1. HTTP / WebSocket Layer

Thin ASP.NET Core minimal-API surface.

**`GET /api/health`** — returns `{ status, protocolVersion, serverTimeUtc }`.

**`GET /ws`** — accepts WebSocket upgrade. On accept:
1. Creates a `BrowserConnection` via `BrowserSessionManager`.
2. Starts a read loop that deserializes JSON frames → `ClientMessage` discriminated union.
3. Routes messages to the `BrowserConnection` for processing.
4. On close, disposes the `BrowserConnection`.

The host project has **no game logic**. It deserializes, dispatches, and serializes.

### 2. Browser Session Manager

`BrowserSessionManager` is a singleton service that owns the set of active `BrowserConnection` instances.

**`BrowserConnection`** represents one WebSocket lifetime:
- has a stable `connectionId` (guid, hex-formatted)
- tracks zero or more attached `PlayerSession` references
- tracks at most one selected `PlayerSession`
- owns the outbound write channel (thread-safe `ChannelWriter<ServerMessage>`)
- enforces ordering: `session.ready` → `snapshot.full` → `owner.delta` on attach/select flows

**Attach flow:**
1. Validate API key format (64 hex chars).
2. Ask `PlayerSessionPool` for a `PlayerSession` matching the key (create or reuse).
3. Record attachment on the `BrowserConnection`.
4. If this is the first attachment, auto-select it.
5. Emit `session.ready`, `snapshot.full`, `owner.delta(overlay.snapshot)`, `status`.

**Select flow:**
1. Validate the `playerSessionId` is attached to this connection.
2. Update selected reference.
3. Emit `session.ready`, `snapshot.full`, `owner.delta(overlay.snapshot)`, `status`.

**Detach flow:**
1. Remove the session reference.
2. If the detached session was selected, auto-select the next attached session or clear selection.
3. Emit `session.ready`, then conditionally `snapshot.full` + `owner.delta` if a new session is selected.

**Command routing:**
- Every inbound `command.*` message is forwarded to the currently selected `PlayerSession`.
- If no session is selected, immediately reply with `command.reply { status: "rejected" }`.

### 3. Player Session Pool

`PlayerSessionPool` manages the set of active upstream Connector sessions.

A `PlayerSession`:
- is keyed by API key
- wraps one `Galaxy` Connector connection (via `GalaxyConnectionManager`)
- reference-counted by attached `BrowserConnection`s
- can be shared across multiple browser connections (spectators, multi-window)
- owns the set of `Controllable` references for that session
- translates browser command DTOs into Connector API calls

**Lifecycle:**
- Created on first `connection.attach` for a given API key.
- Kept alive while at least one `BrowserConnection` references it.
- Disposed (Galaxy connection closed) when the last reference detaches, after a configurable grace period.

### 4. Galaxy Connection Manager

`GalaxyConnectionManager` owns the Connector `Galaxy` instance for a player session.

**Event loop** (`ConnectorEventLoop`):
- Runs a single `Task` per `Galaxy` that calls `galaxy.NextEvent()` in a tight loop.
- Dispatches each `FlattiverseEvent` to the registered services.
- On `ConnectionTerminatedEvent`, triggers reconnect or session teardown.
- **Critical**: only one consumer per Galaxy — this loop is the single reader.

```csharp
while (galaxy.Active)
{
    var @event = await galaxy.NextEvent();
    foreach (var handler in _eventHandlers)
        handler.Handle(@event);
}
```

**Event dispatch** uses a simple handler interface:

```csharp
interface IConnectorEventHandler
{
    void Handle(FlattiverseEvent @event);
}
```

Services register as handlers. The loop calls them synchronously on the event-loop task so they can safely mutate their state without locking.

### 5. Services

Each service is instanced **per player session**. Services own their own state and expose query methods for snapshot generation.

#### MappingService

- Maintains a dictionary of known units keyed by entity ID.
- Processes unit-lifecycle events (created, updated, removed) from the Connector.
- Produces `UnitSnapshotDto` collections for `snapshot.full`.
- Produces `world.delta` event batches for incremental delivery.
- Tracks cluster and team state from Connector events.

#### ScanningService

- Manages scanner subsystem state per controllable.
- Translates `command.scanner.configure` / `command.scanner.set_active` into Connector `DynamicScannerSubsystem` calls.
- Reflects scanner state (active, geometry, target geometry) into overlay payloads.

#### ManeuveringService

- Translates `command.engine.set` into Connector `Engine.Set()` calls.
- Translates `command.navigation.set_target` / `clear_target` into Connector navigation calls.
- Reflects engine and navigation state into overlay payloads.

#### PathfindingService

- Uses mapping state to compute paths between points.
- Avoids static obstacles (suns, black holes, planets).
- Accounts for current fields, nebulae, and storms.
- Exposes path queries for navigation target planning.
- Not directly browser-facing in the initial version — consumed by `ManeuveringService` and `TacticalService`.

#### TacticalService

- Manages tactical targeting state (selected target, lock mode).
- Manages strategy state (active strategy, parameters, engagement profile).
- Translates `command.tactical.*` messages into combinations of Connector calls.
- Coordinates with `ManeuveringService`, `ScanningService`, and weapon firing.
- Reflects tactical state into overlay payloads.

#### ChatService

- Translates `command.chat.send` into `Galaxy.Chat()` calls.
- Receives chat events from the Connector event loop.
- Fans out `chat.received` messages to all attached browser connections.

#### TickService

- Runs a 40 ms periodic timer.
- On each tick:
  1. Collects pending world deltas from `MappingService`.
  2. Collects pending overlay updates from subsystem services.
  3. Batches and delivers `world.delta` and `owner.delta` to each `BrowserConnection`.
  4. Clears pending buffers.

### 6. State Containers

#### WorldSnapshot

Authoritative shared world state for a Galaxy connection:
- Galaxy metadata (name, description, game mode)
- Teams list
- Clusters list
- Public units (from `MappingService`)
- Public controllable summaries

Produced on demand for `snapshot.full`. Updated incrementally by services.

#### OverlayState

Per-controllable detail state scoped to a player session:
- Position, movement, alive/dead status
- Subsystem states (engine, scanner, weapon, fabricator, shield, hull, battery)
- Navigation state
- Tactical state (target, strategy, engagement profile)
- Upgrade progress per subsystem slot

Produced on demand for `owner.delta(overlay.snapshot)`. Updated per-tick.

## Data Flow

### Inbound (Browser → Gateway → Connector)

```
Browser WebSocket
  → JSON deserialize → ClientMessage
  → BrowserConnection.HandleMessage()
  → route by message type:
      session control  → BrowserSessionManager
      command.*        → selected PlayerSession
                         → appropriate Service
                         → Connector API call
  → command.reply sent back to originating BrowserConnection
```

### Outbound (Connector → Gateway → Browser)

```
Galaxy.NextEvent()
  → ConnectorEventLoop dispatches to Services
  → Services update internal state, queue pending deltas
  → TickService fires every 40 ms
  → TickService collects deltas from services
  → TickService delivers to BrowserSessionManager
  → BrowserSessionManager fans out to each BrowserConnection:
      world.delta  → all connections attached to this Galaxy
      owner.delta  → connections with a selected session that owns the controllable
      chat.received → all connections attached to this Galaxy
```

### Snapshot Flow (on attach/select)

```
BrowserConnection triggers snapshot request
  → MappingService.BuildWorldSnapshot()   → snapshot.full
  → OverlayState.BuildOverlaySnapshot()   → owner.delta(overlay.snapshot)
  → messages queued to connection's write channel
  → written to WebSocket in order
```

## Concurrency Model

| Concern | Strategy |
|---|---|
| Connector event loop | Single task per Galaxy. Services called synchronously on this task — no locking needed inside services. |
| Browser WebSocket reads | One read task per connection. Dispatches to `BrowserConnection` which may cross to the event-loop task for command execution. |
| Browser WebSocket writes | `Channel<ServerMessage>` per connection. Single writer task drains and serializes. |
| Tick timer | Single timer fires on a thread-pool thread. Acquires a lightweight lock or posts to a channel to collect and deliver deltas. |
| Player session pool | `ConcurrentDictionary` for session lookup. Creation/disposal behind a `SemaphoreSlim` to avoid double-create races. |

### Thread Safety Rules

1. All Connector calls for a given Galaxy happen on the event-loop task or are posted to it via a channel.
2. Service state is only mutated on the event-loop task.
3. Browser-facing writes go through a channel, never directly into the WebSocket.
4. Shared read-only data (snapshot DTOs) is produced as immutable snapshots — safe to read from any thread.

## Error Handling

### Browser Protocol Errors

- Invalid JSON → `status { kind: "error", code: "invalid_json", recoverable: true }`
- Missing `type` → `status { code: "missing_type", recoverable: true }`
- Unknown message type → `status { code: "unknown_message_type", recoverable: true }`
- Invalid payload shape → `status { code: "invalid_message_shape", recoverable: true }`
- Command without selected session → `command.reply { status: "rejected" }`

All recoverable — the WebSocket stays open.

### Connector Errors

- `GameException` from Connector calls → `command.reply { status: "rejected", error: { code, message } }`
- `ConnectionTerminatedEvent` → attempt reconnect with backoff; notify attached connections via `status { kind: "error" }`
- Unrecoverable Galaxy failure → detach all browser connections from that session, emit `status { code: "session_lost" }`

### Internal Errors

- Unhandled exceptions in the event loop → log, attempt Galaxy reconnect.
- Unhandled exceptions in WebSocket read/write → close that connection cleanly.
- Startup failures → fail fast, report in health endpoint.

## Configuration

```json
{
  "Gateway": {
    "TickIntervalMs": 40,
    "SessionGracePeriodSeconds": 30,
    "MaxBrowserConnections": 100,
    "MaxPlayerSessions": 20,
    "FlattiverseGalaxyUrl": "wss://www.flattiverse.com/galaxies/0/api"
  }
}
```

## Dependency Injection Registrations

```
Singleton:
  BrowserSessionManager
  PlayerSessionPool
  TickService (hosted service)

Scoped (per player session, via factory):
  GalaxyConnectionManager
  ConnectorEventLoop
  MappingService
  ScanningService
  ManeuveringService
  PathfindingService
  TacticalService
  ChatService

Transient:
  (none expected)
```

`PlayerSession` and its services are not DI-scoped in the ASP.NET sense. They are created and managed by `PlayerSessionPool` using a factory pattern. The "scoped per player session" label means one instance per `PlayerSession` lifetime.

## Testing Strategy

| Layer | Approach |
|---|---|
| Protocol DTOs | Serialization round-trip tests (JSON ↔ C# types) |
| BrowserConnection | Unit tests with a mock WebSocket and mock PlayerSession |
| Services | Unit tests with synthetic Connector events (no real Galaxy) |
| PlayerSessionPool | Integration tests for create/reuse/dispose lifecycle |
| End-to-end | Integration test: real WebSocket client → Gateway host → mock Connector |

## Implementation Priorities

1. **Host skeleton** — `Program.cs`, health endpoint, WebSocket accept, empty message loop.
2. **Protocol types** — `ServerMessage`/`ClientMessage` discriminated unions, all DTOs with JSON serialization tests.
3. **BrowserConnection + BrowserSessionManager** — connection lifecycle, attach/select/detach, message fan-out.
4. **PlayerSessionPool + GalaxyConnectionManager** — Connector integration, event loop, session pooling.
5. **MappingService + TickService** — world state tracking, snapshot generation, delta delivery.
6. **ManeuveringService** — engine and navigation command translation.
7. **ScanningService** — scanner configuration and state reflection.
8. **ChatService** — chat routing.
9. **TacticalService** — target selection, strategy management.
10. **PathfindingService** — obstacle-aware path computation.