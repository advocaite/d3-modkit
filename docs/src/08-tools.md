# Tools

## Building the kit

`scripts\build.ps1` builds the host (`host\target\release`), D3ModTool (`tools\D3ModTool\out`) and these docs. It needs Rust and the .NET 8 SDK. `scripts\package.ps1 [-Zip]` assembles a binary release in `dist\`.

## Paths

| Variable | Used for |
|---|---|
| `D3_CLIENT_DIR` | The game copy mods are built into: the default for every `--client` option. |
| `D3_SERVER_DIR` | The server folder (with `Blizzless.dll`) that server mod projects reference. |
| `D3_INSTALL_DIR` | The official install, which the tools read and never write. Default: the install Windows has registered (the "Diablo III" uninstall entry), else `C:\Program Files (x86)\Diablo III`. |

## D3ModTool

The ModKit's command line tool (`D3ModTool.exe`, .NET 8). Options take the form `--name value`. Every `--client DIR` defaults to `%D3_CLIENT_DIR%`.

### Mods

| Command | Does |
|---|---|
| `mod-install <modDir>... [--server DIR] [--client DIR] [--data no]` | Installs mods into a server and/or game copy (`Mods\<id>`), then rebuilds the game's data from every installed mod (skip that with `--data no`). |
| `mod-uninstall <id>... [--server DIR] [--client DIR]` | Removes installed mods; the game's data is rebuilt without them. |
| `mod-data [--client DIR]` | Rebuilds the game copy's data from `<game>\Mods\*\data`. |
| `modhost-install <hostDir> [--client DIR]` | Copies `D3ModHost.dll` and `d3inject.exe` to `<game>\ModHost`. |
| `mod-build <modDir>... [--client DIR]` | Low level: builds the given mod source folders' data into the game copy, in argument order. |
| `mod-verify [--client DIR]` | Checks the built storage. |
| `mod-restore [--client DIR]` | Returns the game copy's storage to stock. |
| `casc-selftest [client]` | Proves the storage writer rebuilds stock files byte for byte. |

### Game data

| Command | Does |
|---|---|
| `casc-extract <group> <nameRegex> <outDir>` | Extracts stock SNO files from the installed game, e.g. `casc-extract UI "^BattleNetCampaign$" out`. |
| `import-gltf <model.glb\|.gltf> <modDir> --name <Name> [--like <actor>] [--sno <id>] [--scale <s>] [--actor-scale <s>] [--forward +z\|-z\|+x\|-x] [--static yes] [--fps <n>] [--install DIR]` `[--monster new\|new:<sno>\|<sno>] [--display-name <text>] [--monster-type <type>] [--root-motion auto\|off] [--speed walk=<ft/s>,run=<ft/s>]` | Turns a glTF model into game data in the mod's `data\`. A static model becomes a prop (texture, appearance, actor). A skinned model becomes an animated monster (textures, appearance with skeleton, one Anim per clip, AnimSet, its own Monster SNO, its name in `enUS\StringList\Monsters.stl`, Monster actor). `--forward`: the glTF axis the model faces (default `+z` for skinned models, `+x` for props). `--static yes`: import a skinned model as a prop. `--monster`: `new` (default) = an own copy of the template's Monster SNO, `new:<sno>` = an own copy of another one, `<sno>` = use a stock one as it is. `--display-name`: the name players see. `--monster-type`: Undead, Demon, Beast, Human, … (default: the source's). `--root-motion`: take the root bone's travel out of walk / run clips and store it as the anim's velocity (default `auto`). `--speed`: set a clip's stored speed in ft/s. `--fps`: animation sample rate (default 30). See [Game data](07-game-data.md#models-import-gltf). |
| `string-set <modDir> <List> <Key> <Text> [--locale enUS]` | Adds or changes one game text: writes `data\<locale>\StringList\<List>.stl` into the mod (from the mod's copy, else the stock list), for every locale of the install that has the list. Stock entries stay byte-identical. See [Game data](07-game-data.md#game-texts-stringlists). |
| `string-get <List\|file.stl> <Key\|re:regex> [--locale enUS]` | Prints texts of a stock list or of a mod's `.stl`. |
| `string-roundtrip` | Checks that every StringList of the install rebuilds byte for byte (all 5,508 enUS lists do). |
| `ui-*` | Layout editing; see [Game data](07-game-data.md#ui-layout-tools). |
| `ui-designer [projectDir] [--port N] [--install DIR] [--browser no]` | The visual UI designer; see [Game data](07-game-data.md#ui-designer). |
| `ui-roundtrip <dir>` | Checks that every `.ui` under the folder writes back byte-exact. |

### Docs

| Command | Does |
|---|---|
| `docs-build [srcDir] [out.html]` | Builds these docs from `ModKit\docs\src\*.md` into one HTML page (default `ModKit\docs\index.html`). |

## d3inject

```
d3inject.exe <game exe> <D3ModHost.dll> [game arguments...]
```

Starts the game suspended, loads the host DLL into it, then resumes it. Launchers call this; the arguments after the DLL go to the game (`OnlineService.SSO=true -launch -uid diablo3`).

## Host environment variables

| Variable | Effect |
|---|---|
| `D3MODS_ROOT` | Load client mods from this folder instead of `<game>\Mods`. |
| `D3MODS_DISABLE` | Comma-separated mod ids not to load. |
| `D3MODHOST_NOHOOK` | Load the host but patch nothing (diagnostics). |

## Log files

| File | Contents |
|---|---|
| `<game>\D3ModHost.log` | host: mods loaded, handlers bound or wrapped, every plugin's `log` lines |
| `<game>\x64\D3Debug.txt` | the game's own log |
| `<server>\logs\<time>\` | the server's logs, including `[Mods]` and your mod logger's lines |
