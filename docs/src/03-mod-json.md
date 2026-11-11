# mod.json reference

Every mod folder has a `mod.json`. Comments (`//`) and trailing commas are allowed. Unknown keys are ignored, so you can use `_comment` keys for notes.

## Fields

| Key | Type | Meaning |
|---|---|---|
| `id` | string | Unique id: lower-case, dashes. Names the installed folder `Mods\<id>`. Defaults to the folder name. |
| `name` | string | Display name (logs, lists). |
| `version` | string | Your version, e.g. `1.2.0`. |
| `build` | string | The client build the mod is made for: `2.8.0.99920`. |
| `type` | `"both"` \| `"server"` \| `"client"` | Which sides install it. `server` mods are not copied to the game; `client` mods are not copied to the server. Default `both`. |
| `enabled` | bool | `false` makes both loaders skip the mod, and the data build leave it out. |
| `description` | string | Free text. |
| `requires` | string[] | Ids of mods that must load first (client plugins and data are ordered by this). |
| `server` | object | `{"assembly": "server/X.dll", "source": "server-src/"}`: the server assembly, and where its project is. Without `assembly`, every `server\*.dll` is loaded. |
| `client_code` | object | `{"dll": "client-code/x.dll", "source": "client-src/"}`: the client plugin DLL, and its project (a `Cargo.toml` or a `.csproj` for NativeAOT). |
| `adds`, `replaces` | arrays | Optional notes listing the data files the mod adds or replaces. The build decides this from each file's SNO id; see [Game data](07-game-data.md). |

## Files that get installed

| Into | What |
|---|---|
| server `Mods\<id>\` | `mod.json`, every top-level `*.json` (settings), the folder of `server.assembly` |
| game `Mods\<id>\` | `mod.json`, every top-level `*.json`, the folder of `client_code.dll`, `data\` |

Sources, build scripts and notes are never installed.

## Settings

Put mod settings in a top-level JSON file, for example `settings.json`. Both parts read it from their installed folder:

- server: `ServerModContext.Directory`;
- client: the `mod_dir` argument of `d3mod_init`.

A server owner edits `<server>\Mods\<id>\settings.json` and restarts the server.

## Disabling a mod

Use any of these:

- set `"enabled": false` in the installed `mod.json`;
- remove the folder with `D3ModTool mod-uninstall <id> --server ... --client ...`;
- client only: set the environment variable `D3MODS_DISABLE=id1,id2`.
