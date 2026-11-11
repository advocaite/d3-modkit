# Changelog

API changes, newest first. The client `HostApi` only ever grows: a plugin built for version N runs on any host with version ≥ N.

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
- Asset import: PNG → game textures, so mods can bring new art.
