# Prototype Web UI

This app is the fast iteration client for the gateway MVP.

It connects to the local gateway over a single WebSocket and expects the checked-in generated protocol types in `src/types/generated.ts`.

## Requirements

- Node.js `^20.19.0 || >=22.12.0`
- npm

## Install

```powershell
cd C:\Users\LeSch\Documents\Projects\Flattiverse-AtLeastWeTried\prototype\webui
npm install
```

## Run

The gateway host should be running on `http://127.0.0.1:5260`.

Optional environment override:

```powershell
Copy-Item .env.example .env
```

Start the Vite dev server:

```powershell
cd C:\Users\LeSch\Documents\Projects\Flattiverse-AtLeastWeTried\prototype\webui
npm run dev
```

Default gateway WebSocket URL:

- `ws://127.0.0.1:5260/ws`

## Build

```powershell
cd C:\Users\LeSch\Documents\Projects\Flattiverse-AtLeastWeTried\prototype\webui
npm run build
```

The current `generate:types` step is a no-op that keeps the checked-in generated file in place until a type generator project is reintroduced.