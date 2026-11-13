# Changelog

API changes, newest first. The client `HostApi` only ever grows: a plugin built for version N runs on any host with version ≥ N.

## 2026-11-14: monster names, own Monster SNOs, walk / run root motion, game texts

- `import-gltf` (skinned) now gives the monster **its own Monster SNO** by default (`--monster new`): a copy of the template's (stats, AI, powers, loot) named like the actor. `--monster new:<sno>` copies another one; `--monster <sno>` keeps the old behaviour (use a stock one as it is). See [Monster name and type](07-game-data.md#monster-name-and-type).
  - `--display-name <text>`: the name players see. The client looks the **Monster SNO's name** up in the StringList `Monsters`, so the import writes a copy of `enUS\StringList\Monsters.stl` with the new entry (`mod.json` `replaces`).
  - `--monster-type <type>`: Undead, Demon, Beast, Human, … (the type line under the name, from StringList `MonsterTypes`).
  - The copy drops the template's bestiary (lore) entry and points back at the new actor.
- Walk / run clips: `--root-motion auto` (default) takes the root bone's travel out of the clip and stores it as the anim's root displacement and velocity, like stock walks, so the game can pace the steps. `--speed walk=<ft/s>` sets it by hand. See [Walking and running](07-game-data.md#walking-and-running).
- Re-imports: new files of a mod get ids right after the mod's own block.
- New commands `string-set`, `string-get`, `string-roundtrip` and the `StringListFile` reader / writer: all 5,508 stock lists rebuild byte for byte. See [Game texts](07-game-data.md#game-texts-stringlists).
- The test golem (`custom-monster-test`) is now "Rune Golem", type Beast, with walk and run clips. Checked offline only (DevHub readers, renders, emulator parsers); **not yet tried in the game**.

## 2026-11-13: animated monsters (skinned glTF import)

- `import-gltf` now imports **skinned** glTF models: skeleton, bone weights, every animation clip as an Anim SNO, an AnimSet, and a Monster actor cloned from a stock monster. The default template is `5187` `Sandling_A`. See [Animated monsters](07-game-data.md#animated-monsters-skinned-gltf).
  - Clip names map to animation tags by a fixed convention (`idle`, `walk`, `run`, `attack`, `hit`, `stunned`, `death`, `dead`, `spawn`, or a tag name). Every tag the template uses is filled.
  - The glTF emissive texture becomes the glow map.
  - Hardpoints are taken from glTF `HP_…` nodes or mapped from the template by bone name.
  - New options: `--forward`, `--static yes`, `--monster`, `--fps`.
  - Checked offline: DevHub's readers parse all files with no warnings, an animated render deforms cleanly, and the emulator's own parsers load the actor, AnimSet and anims. **Not yet tried in the game.**
- Static props are unchanged: re-importing the rune obelisk gives byte-identical files.
- The glTF reader now keeps the node tree, skins (JOINTS_0 / WEIGHTS_0, inverse bind matrices), animations (LINEAR, STEP and CUBICSPLINE) and the emissive texture.

## 2026-11-13: model import (first version)

- New command `D3ModTool import-gltf`: it turns a glTF model into a new static prop (texture, appearance and actor SNOs). See [Game data](07-game-data.md#models-import-gltf).
  - It reads the model (`.glb` or `.gltf`) and PNG textures, encodes DXT1/DXT5 with mips, and rebuilds a stock template appearance and actor around the new mesh.
  - **Works in the game:** an original test prop shows up both with `!spawn` and when placed with the world editor. The template must be a ServerProp; the default is now `61743`. The client ignores Environment actors sent by the server.
- Server: the startup log now also lists mods that have game data only, installed in the game copy the server reads (see [Server API](06-server-api.md)).
- `TexFile.Create`: writes a texture SNO in any format the reader knows, with a mip chain. `CreateBgra8` now uses it, and its output is unchanged.

## 2026-11-11: UI designer (first version)

- `D3ModTool ui-designer`: a visual layout editor (a local web app); see [Game data](07-game-data.md#ui-designer).
  - It places controls with the game's rules (`UiLayout.cs`): checked against the running game's control rectangle, and all 258 stock layouts resolve.
  - It draws real textures and text: `TexFile.cs` decodes the DXT formats and resolves `UI:` frame names, and texts are read from the StringLists.
  - The Art tab browses and extracts the game's art as PNG.
- Designer: forms-designer layout (toolbox, outline with eye toggles and drag-to-reparent, categorized property grid, 8 resize handles, multi-select, zoom/pan, Preview), Group, New layout, and **Import PNG** as a new `2DUI_` texture (uncompressed BGRA8; loading in the game is still to be confirmed).
- `UiDocument`: style strings are at entry +8 (an earlier build read and wrote them at +4, so redo any layout saved after editing a string value). Moved and new controls keep the stock record order (a parent before its children).
- The [UI page](05-ui.md) now documents the real value model: one id space, value lookup order, control types, positioning and anchors, looks and animation.
- `UiDocument`: a `.ui` reader and writer that is byte-exact on all 258 stock layouts. It understands every array in the format: the header array, 9 per-control arrays, and the arrays nested in 656- and 608-byte blocks.
- `D3ModTool ui-roundtrip <dir>`.
- Known issue: the older `ui-clone` and `ui-copy-subtree` commands only relocate three of the per-control arrays. Use them only on layouts without the other arrays, or use the designer's Duplicate instead.

## 2026-11-11: standalone repository

- The ModKit is its own repository: the sources of the host (`host/`), D3ModTool and D3Casc.Lib (`tools/`), plus `scripts/build.ps1` and `scripts/package.ps1`. MIT license.
- D3ModTool has no built-in paths any more:
  - `--client` defaults to `%D3_CLIENT_DIR%`;
  - the official install comes from `%D3_INSTALL_DIR%`, or the Windows registration;
  - the 2.0.1 MPQ folder comes from `--mpq` or `%D3_201_MPQ_DIR%`.
- Example server projects find the server through `%D3_SERVER_DIR%`.

## 2026-11-11: ModKit

- **Installed layout:**
  - the server loads mods from `<server>\Mods` (`[Mods] Root`, default `Mods`);
  - the client host loads them from `<game>\Mods`, and lives in `<game>\ModHost`.
- **D3ModTool** has new commands: `mod-install`, `mod-uninstall`, `mod-data`, `modhost-install` and `docs-build`.
- **Server hooks:** `ServerModContext.Hooks`, with the first hook `OnGoldDrop`.
- **SDK:**
  - `d3modapi` moved into the kit (`sdk/rust`);
  - the version check is now "host ≥ plugin" instead of an exact match;
  - new C header (`sdk/c/d3modapi.h`) and C# binding (`sdk/csharp/D3ModApi.cs`).
- **Examples:** `gold-rate` and `hello-csharp`.

## Client API versions

| Version | Added |
|---|---|
| 7 | dropdowns: `combo_set_items`, `combo_select`, `combo_selected`, `combo_set_placeholder` |
| 6 | `item_line` |
| 5 | `item_name` |
| 4 | `show_item_tooltip` |
| 3 | `draw_texture` |
| 2 | `register_draw`, `control_rect`, `mouse_pos`, `draw_item_icon` |
| 1 | logging, handlers and wrappers, controls, text, layouts, list boxes, `is_control`, `login_token`, `dump_controls` |

## Planned

- More server hooks: item drops, experience, monster spawns, player events.
- A UI designer: open, edit and create `.ui` layouts visually, with texture preview.
- Asset import: skinned and animated models, more material slots (normal, glow), collision meshes.
