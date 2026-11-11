# D3 ModKit

> **Not affiliated with Blizzard Entertainment.** This is an independent fan project. It is not made, endorsed, supported or approved by Blizzard Entertainment in any way. Diablo and Diablo III are trademarks or registered trademarks of Blizzard Entertainment, Inc. The kit contains no Blizzard game data or code; it works on your own copy of the game.

Mod Diablo III **2.8.0.99920** running against a DiIiS/Blizzless private server: server rules, client UI and game data, in any language that can build a DLL.

**Open `docs/index.html`** for the full documentation: getting started, the `mod.json` reference, the client and server API references, UI layouts and drawing, game data, tools, other languages and examples.

## Repository

| Path | What |
|---|---|
| `docs/` | The documentation: `src/*.md`, built into the offline page `index.html`. |
| `sdk/rust/d3modapi` | The client API crate (the canonical definition). |
| `sdk/c/d3modapi.h` | The C/C++ header, and the reference for other languages. |
| `sdk/csharp/D3ModApi.cs` | The C# binding for NativeAOT plugins. |
| `host/` | **D3ModHost** (Rust): `D3ModHost.dll`, the client mod host, and `d3inject.exe`, which starts the game with it. Only the host knows game addresses. |
| `tools/D3ModTool` | **D3ModTool** (.NET 8): installs mods, builds mod data into a game copy (CASC writer), edits UI layouts, builds the docs. |
| `tools/D3Casc.Lib` | Reads the game's CASC storage (on top of the `WiseOwl.Casc` NuGet package). |
| `examples/gold-rate` | Server hook (gold drop ×N) with a command, plus a Rust client part. |
| `examples/hello-csharp` | A client plugin written in C#. |
| `scripts/` | `build.ps1` (host, tool, docs) and `package.ps1` (a binary release in `dist/`). |

The **server side** of the mod API (`DiIiS_NA.Mods`: `IServerMod`, `ServerModContext`, hooks, REST routes) lives in the server, so server mods reference the server's `Blizzless.dll`. See `docs`, "Server API reference".

## Build

```bat
powershell -File scripts\build.ps1
```

This needs Rust (`x86_64-pc-windows-msvc`) and the .NET 8 SDK. C# client plugins (NativeAOT) also need the MSVC build tools.

## Quick start

```bat
set D3_CLIENT_DIR=F:\D3Mods\client
set D3_SERVER_DIR=C:\D3Server

rem once per game copy: the client mod host
tools\D3ModTool\out\D3ModTool.exe modhost-install host\target\release

rem build the example (server part: .NET 8 SDK, client part: Rust), then install it
cd examples\gold-rate\server-src && dotnet build -c Release && cd ..\..\..
cd examples\gold-rate\client-src && cargo build --release && copy target\release\gold_rate_client.dll ..\client-code\ && cd ..\..\..
tools\D3ModTool\out\D3ModTool.exe mod-install examples\gold-rate --server %D3_SERVER_DIR% --client %D3_CLIENT_DIR%
```

| Variable | Meaning |
|---|---|
| `D3_CLIENT_DIR` | your **copy** of the game, which mods are built into |
| `D3_SERVER_DIR` | your server folder (the one with `Blizzless.dll`) |
| `D3_INSTALL_DIR` | the official install, which is never written to; found automatically when Windows has it registered |

Then restart the server, and start the game copy through `%D3_CLIENT_DIR%\ModHost\d3inject.exe` (see the docs, "Getting started"). The main menu shows **Start Game (Gold x3)**.

> Only mod a **copy** of the game, and only play it on a private server.

## License

MIT, see `LICENSE`. This project contains no Blizzard game data and is not affiliated with Blizzard Entertainment in any way.
