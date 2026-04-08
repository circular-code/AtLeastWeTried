# Flattiverse Gateway

This folder contains the browser-facing ASP.NET Core gateway used by the prototype web UI.

The gateway is still aligned with the longer-term production architecture, and now leases real player sessions against a live Flattiverse galaxy through the C# connector.

Projects:

- `Flattiverse.Gateway.Host`: HTTP and WebSocket transport boundary
- `Flattiverse.Gateway.Contracts`: browser-facing DTOs and protocol message shapes
- `Flattiverse.Gateway.Application`: session orchestration and runtime abstractions
- `Flattiverse.Gateway.Domain`: gateway session domain models
- `Flattiverse.Gateway.Infrastructure`: in-memory browser-session store, connector-backed player-session leasing, live runtime projection, JSON transport, and tick worker
- `tests/*`: unit and integration tests for orchestration and host endpoints

## Implemented in the MVP

- `GET /api/health`
- `GET /ws` WebSocket endpoint with `400` for non-upgrade requests
- browser-session open, attach, select, detach, and live player-session leasing
- 40 ms runtime tick worker
- typed JSON transport for client and server messages
- snapshot projection from the connector's live galaxy mirror
- owner overlay projection for controllable position, movement, engine, shield, hull, battery, scanner, and gateway-managed navigation state
- command handling for:
	- `command.chat`
	- `command.create_ship`
	- `command.destroy_ship`
	- `command.continue_ship`
	- `command.remove_ship`
	- `command.set_engine`
	- `command.set_navigation_target`
	- `command.clear_navigation_target`
	- `command.fire_weapon`
	- `command.set_subsystem_mode`

These commands are now routed to actual upstream connector APIs. `command.set_navigation_target` remains a gateway-side helper that steers the upstream engine subsystem over time.

## Run locally

Default host URL from `launchSettings.json`:

- `http://127.0.0.1:5260`

Start the gateway:

```powershell
cd C:\Users\LeSch\Documents\Projects\Flattiverse-AtLeastWeTried\backend\Gateway\Flattiverse.Gateway.Host
dotnet run
```

Before attaching a browser session, configure `Connector:GalaxyEndpoint` in `appsettings.json`, user secrets, or environment configuration to a live galaxy websocket endpoint such as `wss://www.flattiverse.com/galaxies/0/api`.

The prototype web UI should connect to:

- `ws://127.0.0.1:5260/ws`

## Validate

```powershell
cd C:\Users\LeSch\Documents\Projects\Flattiverse-AtLeastWeTried\backend
dotnet test Flattiverse.Gateway.slnx
```

## Still not implemented

- snapshot and delta projection from live connector events
- shared observer runtime and multi-client fan-out across independent browser connections