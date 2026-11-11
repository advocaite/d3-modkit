# Introduction

> **Not affiliated with Blizzard Entertainment.** The D3 ModKit is an independent fan project, not made, endorsed, supported or approved by Blizzard Entertainment in any way. Diablo and Diablo III are trademarks or registered trademarks of Blizzard Entertainment, Inc. The kit contains no Blizzard game data or code.

The **D3 ModKit** lets you change Diablo III (client **2.8.0.99920**) running against a DiIiS/Blizzless private server, D2-mod style. You can change game rules on the server, change or add UI in the client, and ship new game data (layouts, strings, art) with your mod.

> Everything here works on a **copy** of the client (for example `F:\D3Mods\client`), never on the official install, and only against a private server. Do not use the mod host on Blizzard's servers.

## What a mod is

A mod is a folder with a `mod.json` manifest and up to three parts. Each part is optional:

| Part | Folder | Runs in | What it can do |
|---|---|---|---|
| **Server code** | `server/` | the server (C# / .NET 8 assembly) | game rules through [server hooks](06-server-api.md#server-hooks), chat/console commands, REST routes, its own database tables |
| **Client code** | `client-code/` | the game (a native 64-bit DLL loaded by **D3ModHost**) | UI handlers, new screens, drawing item icons and art, tooltips, dropdowns ([client API](04-client-api.md)) |
| **Data** | `data/` | the game's data storage (CASC), built by `D3ModTool` | new or replaced UI layouts, strings, textures and other SNO files ([game data](07-game-data.md)) |

A mod may also carry its own settings files, for example `settings.json`, which both code parts can read from their installed folder.

The **Auction House** mod (the 2.0.1 AH ported to 2.8) uses all three parts. The [examples](10-examples.md) show each part on its own.

## Installed layout

The server and the players install mods; mod authors never edit the game or the server.

```
<server>\Blizzless.dll
<server>\Mods\<mod id>\mod.json, settings.json, server\*.dll

<game>\x64\Diablo III64.exe
<game>\ModHost\D3ModHost.dll, d3inject.exe       the client mod host
<game>\Mods\<mod id>\mod.json, settings.json, client-code\*.dll, data\...
```

`D3ModTool mod-install <modDir> --server <server> --client <game>` creates this layout from a mod's source folder, and builds every installed mod's `data` into the game copy. See [Tools](08-tools.md).

## How the client part works

`d3inject.exe` starts the game suspended, loads `D3ModHost.dll` into it, then lets it run. The host:

1. hooks the game's UI handler lookup, so a layout can name a handler that only a mod provides;
2. loads every mod DLL in `<game>\Mods\*\client-code\` and calls its `d3mod_init`;
3. hands each plugin the **HostApi** table: find controls, set text, wrap handlers, draw icons and art, show item tooltips, fill dropdowns, and more.

Plugins contain **no game addresses**. Only the host knows the exe, so a client patch needs a new host, not new mods.

The host writes a log to `<game>\D3ModHost.log`. Every plugin line there is prefixed with its mod id.

## What's in the kit

| Folder | Contents |
|---|---|
| `sdk/rust/d3modapi` | the API crate (the canonical definition) |
| `sdk/c/d3modapi.h` | C/C++ header, and the reference layout for other languages |
| `sdk/csharp/D3ModApi.cs` | C# binding for NativeAOT plugins |
| `examples/` | `gold-rate` (server hook + Rust client), `hello-csharp` (C# client) |
| `docs/` | these docs (`src/*.md`, built to `index.html` with `D3ModTool docs-build`) |
| `host/` | source of the client mod host (`D3ModHost.dll`, `d3inject.exe`) |
| `tools/` | source of `D3ModTool` and `D3Casc.Lib` |
| `scripts/` | `build.ps1` builds the host, the tool and the docs; `package.ps1` makes a binary release |
| `bin/` | in a binary release only: `D3ModHost.dll`, `d3inject.exe`, `D3ModTool` |

## Requirements

- The Diablo III 2.8.0.99920 client, as a separate copy for modding.
- A DiIiS/Blizzless server with the mod loader (the `Mods` folder support and `DiIiS_NA.Mods` API).
- To build mods:
  - the .NET 8 SDK for server parts and C# client parts;
  - Rust (`x86_64-pc-windows-msvc`) for Rust client parts;
  - the MSVC build tools for C/C++ client parts and for C# NativeAOT.
