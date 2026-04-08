# AtLeastWeTried

## Requirements

- Node.js `^20.19.0 || >=22.12.0`
- npm
- .NET SDK 10

## Prototype Stack

The current end-to-end MVP is:

- backend gateway in `backend/Gateway`
- prototype browser client in `prototype/webui`

The older Vue GUI in `gui/vue` still exists, but it is not the client wired to the new gateway MVP.

## Prototype Web UI

The prototype UI lives in `prototype/webui` and talks to the gateway over `ws://127.0.0.1:5260/ws` by default.

### Install

```powershell
cd C:\Users\LeSch\Documents\Projects\Flattiverse-AtLeastWeTried\prototype\webui
npm install
```

### Run Development Server

```powershell
cd C:\Users\LeSch\Documents\Projects\Flattiverse-AtLeastWeTried\prototype\webui
npm run dev
```

### Build

```powershell
cd C:\Users\LeSch\Documents\Projects\Flattiverse-AtLeastWeTried\prototype\webui
npm run build
```

## Gateway Backend

The gateway host exposes:

- `GET /api/health`
- `GET /ws`

### Run

```powershell
cd C:\Users\LeSch\Documents\Projects\Flattiverse-AtLeastWeTried\backend\Gateway\Flattiverse.Gateway.Host
dotnet run
```

Configure `Connector:GalaxyEndpoint` for the gateway before attaching a browser session. The gateway now leases real player sessions against a live Flattiverse galaxy instead of using the old local-only stub runtime.

### Test

```powershell
cd C:\Users\LeSch\Documents\Projects\Flattiverse-AtLeastWeTried\backend
dotnet test Flattiverse.Gateway.slnx
```

## Legacy GUI

The GUI lives in [gui/vue](/C:/flattiverse/AtLeastWeTried/gui/vue) and is a minimal Vue 3 + TypeScript + Vite app with a basic Three.js scene.

### Install

```powershell
cd C:\flattiverse\AtLeastWeTried\gui\vue
npm install
```

### Run Development Server

```powershell
cd C:\flattiverse\AtLeastWeTried\gui\vue
npm run dev
```

This starts the local Vite development server.

### Build

```powershell
cd C:\flattiverse\AtLeastWeTried\gui\vue
npm run build
```

This runs Vue type-checking and creates a production build in `gui/vue/dist`.

## Client-Server

Documentation for `client-server` will be added later.
