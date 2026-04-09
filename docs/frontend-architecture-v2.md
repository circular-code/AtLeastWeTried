# Flattiverse Frontend Architecture

## Purpose

This document defines a simpler production architecture for the Flattiverse frontend.


## Design Direction

The frontend should follow five rules.

1. Split by responsibility, not by every message type.
2. Keep state together when it changes together.
3. Put complexity at the boundary, not in every component.
4. Extract shared modules only after real reuse appears.
5. Add new stores later only when there is clear pressure to split.

In practice, that means three stores are enough for the initial production client:

- `session` for connection and authority state
- `game` for realtime gameplay data and command lifecycle state
- `ui` for local client state and preferences

## Recommended Project Structure

```text
gui/
├── index.html
├── package.json
├── tsconfig.json
├── vite.config.ts
└── src/
    ├── main.ts
    ├── App.vue
    ├── styles/
    │   ├── reset.css
    │   ├── tokens.css
    │   ├── base.css
    │   └── utilities.css
    │
    ├── types/
    │   ├── generated.ts
    │   └── client.ts
    │
    ├── transport/
    │   ├── gateway.ts
    │   └── commands.ts
    │
    ├── stores/
    │   ├── session.ts
    │   ├── game.ts
    │   └── ui.ts
    │
    ├── renderer/
    │   ├── WorldScene.ts
    │   ├── unitBody.ts
    │   ├── unitVisuals.ts
    │   ├── scannerCone.ts
    │   ├── selectionRing.ts
    │   ├── navigationMarker.ts
    │   ├── grid.ts
    │   ├── constants.ts
    │   └── shaders/
    │
    ├── features/
    │   ├── viewport/
    │   │   └── WorldViewport.vue
    │   ├── overlay/
    │   │   ├── OverlayPanel.vue
    │   │   └── ShipCreator.vue
    │   ├── selection/
    │   │   └── SelectionPanel.vue
    │   ├── commands/
    │   │   └── CommandDock.vue
    │   ├── status/
    │   │   └── StatusBar.vue
    │   ├── session/
    │   │   └── SessionManagerModal.vue
    │   ├── chat/
    │   │   └── ChatModal.vue
    │   ├── activity/
    │   │   ├── ActivityToasts.vue
    │   │   └── ActivityHistoryModal.vue
    │   └── debug/
    │       └── DebugLogPanel.vue
    │
    ├── shared/
    │   ├── GaugeMeter.vue
    │   └── ModalBackdrop.vue
    │
    └── lib/
        ├── savedConnections.ts
        ├── formatting.ts
        └── validation.ts
```

Notes:

- `shared/` should stay small. Do not move components there until they are genuinely reused.
- View-specific helpers can stay inside feature folders instead of becoming top-level composables immediately.
- The renderer layout does not need major simplification.

## State Model

### `stores/session.ts`

This store owns connection lifecycle and command authority.

State:

- `gatewayUrl: string`
- `connectionState: 'idle' | 'connecting' | 'open' | 'closed' | 'error'`
- `connectionId: string`
- `protocolVersion: string`
- `observerOnly: boolean`
- `playerSessions: PlayerSessionSummaryDto[]`

Getters:

- `selectedPlayerSession`
- `attachedPlayerSessions`
- `attachedSessionIds`

Actions:

- `setConnectionState(state)`
- `applySessionReady(message)`
- `clearSession()`

Why this stays separate:

- connection/session state has a different reset lifecycle than game data
- authority and attachment rules should not be mixed into the world reducer
- reconnect and attach flows read naturally from one store

### `stores/game.ts`

This store owns all realtime gameplay data that comes from the gateway and is rendered or surfaced to the user.

State:

- `galaxy: GalaxySnapshotDto | null`
- `unitsById: Map<string, UnitSnapshotDto>`
- `controllablesById: Map<string, PublicControllableSnapshotDto>`
- `teamsById: Map<number, TeamSnapshotDto>`
- `clustersById: Map<number, ClusterSnapshotDto>`
- `overlayById: Map<string, ControllableOverlayState>`
- `chatEntries: ChatEntryDto[]`
- `pendingCommands: Map<string, PendingCommandDescriptor>`
- `activityEntries: ActivityEntry[]`

Actions:

- `applySnapshot(snapshot)`
- `applyWorldDelta(message)`
- `applyOwnerDelta(message)`
- `addChatEntry(entry)`
- `trackCommand(commandId, descriptor)`
- `resolveCommand(message)`
- `recordStatus(message)`
- `clearOverlay()`
- `clearAll()`

Getters and selectors:

- `worldStats`
- `ownedControllables`
- `activeControllable(controllableId)`
- `overlayEntry(controllableId)`
- `selectionEntry(unitId)`
- `recentActivity(lifetimeMs)`

Why these concerns belong together right now:

- `snapshot.full`, `world.delta`, `owner.delta`, `chat.received`, `command.reply`, and `status` all arrive through the same socket pipeline
- the same screens usually need world data, overlay data, and command feedback together
- chat, pending commands, and activity are currently small append-only collections, not large subsystems

What still matters inside this larger store:

- keep the world normalized in maps
- parse overlay payloads into typed `ControllableOverlayState` at the store boundary
- keep command tracking separate from scene truth even though it lives in the same store file

### `stores/ui.ts`

This store owns client-local interaction state.

State:

- `selectedControllableId: string`
- `lastSelection: WorldSceneSelection | null`
- `isManagerPopupOpen: boolean`
- `isChatPopupOpen: boolean`
- `isActivityHistoryOpen: boolean`
- `isDebugLogOpen: boolean`
- `isDebugLogIngame: boolean`
- `debugLogEntries: DebugLogEntry[]`
- `debugLogLimit: number`
- `debugLogSearch: string`
- `debugLogExclude: string`
- `showClientDebugMessages: boolean`
- `showServerDebugMessages: boolean`

Actions:

- `setSelectedControllable(id)`
- `setLastSelection(selection)`
- `closeAllPopups()`
- `recordDebugMessage(direction, message)`
- `clearDebugLog()`
- `restorePreferences()`
- `persistPreferences()`

Why this stays separate:

- this state is local to the browser tab
- it has different persistence rules than session/game state
- it should be easy to reset or persist without touching reducers

## Why Not More Stores

State should be grouped by runtime responsibility, not by every message family or panel.

The main issue is not correctness. The issue is cost:

- more files to touch for one feature
- more cross-store wiring for one message
- more selector and synchronization code
- more architectural rules for contributors to memorize

For this project, the simpler split is better.

| Concern | Store |
| --- | --- |
| connection and authority | `session` |
| world, overlay, chat, and command feedback | `game` |
| local interaction state and preferences | `ui` |

This is the intended boundary:

- `session` answers "what session is this tab connected to and what can it control?"
- `game` answers "what is happening in the world right now?"
- `ui` answers "what is this tab currently showing and how is it behaving?"

## Transport Layer

### `transport/gateway.ts`

This module keeps the raw socket concerns small and mechanical.

Responsibilities:

- open and close the WebSocket
- translate socket events into connection state callbacks
- parse inbound JSON into `ServerMessage`
- serialize outbound `ClientMessage`
- auto-respond to `ping` with `pong`

It should not know about Pinia stores or UI components.

### `transport/commands.ts`

This module builds typed outbound commands.

Each helper should:

1. create a `commandId`
2. build the message payload
3. return `{ commandId, message }`

That keeps command formatting consistent without introducing an extra orchestration layer.

## Integration Layer

Use one main composable: `useGateway()`.

Responsibilities:

- create and own the `gateway.ts` client
- update `sessionStore`, `gameStore`, and `uiStore`
- expose high-level user actions such as connect, attach, select player, create ship, set engine, set navigation target, configure scanner, fire weapon, and send chat
- track pending commands before sending them

The important simplification is this: the frontend does not need a separate `messageRouter.ts` file yet.

An internal `applyServerMessage(message)` function inside `useGateway()` is enough:

```ts
function applyServerMessage(message: ServerMessage) {
  switch (message.type) {
    case 'session.ready':
      sessionStore.applySessionReady(message);
      break;
    case 'snapshot.full':
      gameStore.applySnapshot(message.snapshot);
      break;
    case 'world.delta':
      gameStore.applyWorldDelta(message);
      break;
    case 'owner.delta':
      gameStore.applyOwnerDelta(message);
      break;
    case 'chat.received':
      gameStore.addChatEntry(message.entry);
      break;
    case 'command.reply':
      gameStore.resolveCommand(message);
      break;
    case 'status':
      gameStore.recordStatus(message);
      break;
  }
}
```

If that switch later becomes too large or starts requiring isolated tests, it can be extracted then. It does not need its own file on day one.

## Selectors and Composables

This architecture should be strict about extraction.

Default approach:

- keep reusable selectors as Pinia getters or plain functions inside the store file
- keep feature-specific computed values inside the feature component
- add a composable only when logic is reused across multiple features or needs its own lifecycle

Recommended shared composables at the start:

- `useGateway()`

Optional later extractions:

- camera or viewport interaction helpers
- keyboard shortcut management
- large reusable modal workflows

## Renderer Architecture

The renderer should stay isolated from the Vue application layer.

Keep these rules:

- `renderer/` stays independent from Vue and Pinia
- `WorldViewport.vue` is the bridge between stores and `WorldScene`
- the renderer receives plain render-ready data, not reactive store objects

The main simplification here is organizational, not technical:

- keep the renderer isolated
- do not create extra abstraction around it unless multiple scene implementations appear

## Feature Structure

Features may read stores directly. There is no need to force every read through a composable or a presentation-only wrapper.

Practical rules:

- feature components can import stores and `useGateway()` directly
- small subcomponents can stay next to the feature that uses them
- move a subcomponent to `shared/` only after real reuse
- avoid a separate top-level `components/` folder unless it starts carrying clearly shared UI primitives

This keeps the implementation closer to how the screens are actually built.

## App Shell

`App.vue` should stay thin.

Responsibilities:

- assemble the major feature regions
- register global keyboard shortcuts
- kick off auto-connect if saved connections exist
- host modal and overlay layout

It should not:

- reduce gateway messages
- hold world state
- build command payloads
- compute detailed view models for every panel

## Data Flow

```text
WebSocket
  -> gateway.ts
  -> useGateway().applyServerMessage()
  -> sessionStore / gameStore / uiStore
  -> feature components and WorldViewport
  -> WorldScene
```

Outbound flow:

```text
User action
  -> feature component
  -> useGateway()
  -> commands.ts
  -> gameStore.trackCommand()
  -> gateway.send()
```

## Implementation Order

Build the implementation in this order.

1. Extract the socket code into `transport/gateway.ts`.
2. Create `session`, `game`, and `ui` stores.
3. Move the current `App.vue` state into those three stores without over-refactoring selectors.
4. Extract `useGateway()` to own message application and outbound commands.
5. Move the Three.js code behind `WorldViewport.vue` and `renderer/`.
6. Split UI into feature folders only where it helps readability.

This order keeps implementation incremental and avoids inventing abstractions before the state move is stable.

## When To Split Later

This architecture is intentionally conservative. It should become more modular only when one of these is true:

- a store becomes hard to navigate even with internal sections
- a slice needs different persistence or reset behavior
- a slice is owned by a clearly separate subsystem
- tests are awkward because too many unrelated concerns share one file
- performance work requires a more explicit boundary

If that happens, the likely first extraction is `game` into either:

- `world` and `activity`, or
- `world` and `authority-overlay`

That split should be driven by implementation pressure, not by a desire to make the file tree look more enterprise.

## Summary

This architecture is intentionally compact.

It keeps:

- isolated transport
- isolated renderer
- typed commands
- normalized realtime state
- clear session and UI boundaries

It simplifies:

- the state model to three stores
- many selector composables down to one required shared composable
- extra routing layers down to one integration point
- component rules from strict layering to feature-first pragmatism

That should be enough structure for the production client without paying abstraction cost too early.