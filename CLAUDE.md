# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A web interface for YuE Studio (a native macOS app around the YuE2 music model), meant to be used remotely through Tailscale. An ASP.NET Core Minimal API runs YuE Studio's own Python worker as a child process and serves a Vue frontend under `/ui`. README.md (German) is the user documentation. The structure follows `~/repos/YuE_To_Logic` (same SpaProxy/Vite setup, `ClientAppEndpoints`, test style).

## Commands

Requires the .NET 10 SDK and Node.js 22.12+.

```sh
dotnet build                     # whole solution (YueUI.slnx)
dotnet test                      # xUnit, with a scripted fake worker; no YuE Studio needed
dotnet run --project src/YueUI.Api                      # API on 127.0.0.1:5090, SpaProxy starts Vite on 127.0.0.1:5174/ui/
dotnet publish src/YueUI.Api -c Release -o publish      # runs npm ci + npm run build, ships wwwroot/ui
deploy/install.sh                # publish + LaunchAgent (de.bvelop.yueui); deploy/uninstall.sh removes it
```

Frontend only, from `src/YueUI.Api/ClientApp`: `npm run dev`, `npm run build` (vue-tsc, then Vite), `npm run type-check`. Pass `-p:SkipClientAppBuild=true` to skip every npm step.

## Architecture

### The worker (`Worker/`)

`yue2_worker.py` lives in YuE Studio's installation (`~/Library/Application Support/YuE Studio/src/tools/`), not here. Its protocol is documented at the top of that file: JSON lines in on stdin (`generate`, `render`, `cancel`, `stop`, `quit`, `ping`), JSON events out on stdout (`ready`, `started`, `stage`, `progress`, `song`, `failed`, `idle`, `log`, `error`). Songs are addressed by the absolute path of their `audio.flac`; this app maps that to `run/songN` ids (`SongLibrary.IdFor`). Check that file when YuE Studio updates (its version is in `installed.json`).

- `YuePaths` – install root, output dir and the environment the worker needs (`YUE2_OUTPUT_DIR`, `HF_HOME`, `YUE2_ANE_CACHE`, …), copied from what the app passes its own worker.
- `PythonWorkerLauncher` / `IWorkerConnection` – process plumbing; tests replace `IWorkerLauncher` with `FakeLauncher` (`tests/.../TestApp.cs`).
- `WorkerHost` – singleton and hosted service. Starts the worker lazily on the first command, turns events into `SongState`s and a log, and fans changes out to subscribers (bounded channels; a slow subscriber is dropped and its EventSource reconnects with a fresh snapshot). Progress events are throttled per song. If the process exits, running songs are marked failed and the next command starts a new worker.
- YuE Studio runs its own worker (and model) when open; `IStudioDetector` reports that so the UI can warn. Two generating workers can exhaust memory.

### API

Minimal API, endpoint groups per file: `WorkerEndpoints` (status, SSE via `TypedResults.ServerSentEvents`, generate, render, cancel, stop, worker shutdown) and `LibraryEndpoints` (library listing, FLAC with range processing, score, ZIPs per song and per run built in a self-deleting temp file). `SongLibrary` reads run folders and only accepts names matching the worker's patterns, which is also the path-traversal guard. Commands to the worker answer 202; the result arrives as events. JSON is camelCase with enums as camelCase strings. The server binds to 127.0.0.1 only; remote access is `tailscale serve --https=8443` (443 on this Mac already proxies another service), there is no login.

### Frontend (`src/YueUI.Api/ClientApp`)

Vue 3 + TypeScript + Vite, no router, no state library, no component framework. `App.vue` holds the state and the event subscription; `api.ts` is the only place that talks to `/api`; `types.ts` mirrors the API's records by hand; `i18n.ts` holds every German and English string; `form.ts` maps the form to `GenerateRequest` and keeps it in `localStorage`. Mobile first (the main client is a phone): inputs are 16px to avoid iOS zoom, audio uses `preload="none"`.

## Conventions

- Commit messages and branch names are German; code, comments and API messages are English. UI strings exist in both languages in `i18n.ts`.
- Comments explain why. New API fields go into the C# record, `types.ts` and, if user-facing, `i18n.ts` together.
