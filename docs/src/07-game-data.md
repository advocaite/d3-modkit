# Game data

A mod's `data/` folder holds game files: UI layouts, string lists, textures and other SNO files. They are built into the game copy's storage (CASC), next to the stock files.

## Layout

```
data\<RootDir>\<Group>\<Name><ext>
data\Base\UI\MyWindow.ui                 a UI layout
data\enUS\StringList\MyStrings.stl       English strings
```

- `<RootDir>` is the storage's root directory: `Base` for most files, and the locale (`enUS`, …) for localized ones.
- `<Group>` is the SNO group folder: `UI`, `StringList`, `Textures`, ….
- `<Name>` becomes the SNO name.

## Add or replace

Every SNO file carries its id in its header (at offset 16):

- **An id that already exists** in the game's table of contents **replaces** that SNO. Example: a changed `BattleNetCampaign.ui`.
- **Any other id adds a new SNO.** Its group comes from the folder and its name from the file name.

Pick ids for new SNOs that the game does not use. The Auction House mod reuses the 2.0.1 ids of its layouts, which 2.8 no longer has.

`mod.json`'s `adds` and `replaces` lists are notes for readers only; the build decides from the file itself.

## Building

`D3ModTool mod-install ... --client <game>` copies `data\` into `<game>\Mods\<id>\data`, then rebuilds the storage from every installed and enabled mod. Mods are ordered by id, with `requires` first, so a later mod's file wins.

- `D3ModTool mod-data --client <game>` rebuilds without installing anything.
- `mod-verify` reads every mod file back through two independent readers, and compares 64 random stock files with the official install.
- `mod-restore` returns the copy to stock.

The build writes one archive of its own (`data.NNN`) plus new index and config files. It never changes stock archives, so a rebuild or a restore is always clean.

The writer refuses to touch the official install.

## UI layout tools

All of these edit `.ui` files in place unless an output file is given:

| Command | Does |
|---|---|
| `ui-info <file> [--full]` | tree of controls (types, counts); `--full` adds every property and style |
| `ui-check <file\|dir> [version\|any]` | parses and validates layouts |
| `ui-convert <in201Dir\|file> <outDir>` | converts 2.0.1 layouts (format 463) to 2.8 (471) |
| `ui-prop <file> <control> <id> <value>` | sets or adds an int property, e.g. `0x07` x |
| `ui-style <file> <control> <id> <string>` | sets or adds a style, e.g. `41` click handler |
| `ui-style-remove <file> <control> <id>` | removes a style |
| `ui-type <file> <control> <type>` | changes a control's type |
| `ui-move <file> <from> <to>` | moves or renames a control (subtree) |
| `ui-clone <file> <control> <copies> [out]` | copies a control; the copies are `<name>1`, `<name>2`, …, each with its own properties |
| `ui-copy-subtree <dst> <src> <src root> <new root>` | copies a subtree from another layout |
| `ui-inline <main> <ref control> <template.ui> <template root> [out]` | inlines a template layout |
| `ui-make-listbox <file> <container> <row> <templateName>` | turns a static row into a list box with a row template |

Control paths in these commands are full layout paths such as `MyWindow_main.LayoutRoot.Panel.OkButton`.

## UI designer

`D3ModTool ui-designer [projectDir] [--port 5190] [--install DIR]` opens the visual designer in your browser. It runs on `http://127.0.0.1:5190`, is only reachable from this PC, and is laid out like a forms designer:

- **Left**
  - **Toolbox:** panel, image, button, text, check box, radio button, dropdown, list box and stack panel. Drag one onto the surface (it goes into the panel under the mouse), or click it to add it to the selected panel.
  - **Layouts:** the game's layouts, which open read only (use **Save as…** into your mod to replace one), and the `.ui` files of your project.
  - **Art:** every UI, item and minimap texture, plus the textures in your project. Click a frame to copy its name; **Extract to project** writes PNGs into `<project>\Art\<Texture>\`.
    - **Import PNG…** turns a picture (PNG, JPEG or WebP, up to 4096×4096) into a new UI texture of a mod: `<project>\<mod>\data\Base\Textures\2DUI_<name>.tex`, with one frame `UI:<frame>` covering the picture. A new mod folder gets a `mod.json`.
    - Frame names must be unique among all UI frames, because the game registers them by name, so a clash with the game or another project texture is refused.
    - The texture is stored uncompressed, which is large: 1600×1200 is 7.7 MB.
    - When you started from a property's **…** button, the new frame is assigned to it straight away. Project textures show on the canvas immediately; in the game they appear after `mod-install` / `build_mods`.
- **Centre: the design surface.** It draws the layout as the game places it, with real textures and text, on the chosen screen shape (the dashed box is the 4:3 design area).
  - Click to select (Alt+click picks the control below; Ctrl+click or a drag on empty space selects several).
  - Drag to move, and drag one of the 8 handles to resize. Arrow keys nudge by 1, or by 10 with Shift.
  - Wheel zooms; middle mouse or Space+drag pans; `0` fits the view.
  - **Preview** draws the screen like the game: no outlines, and controls the layout hides stay hidden. The window itself is always shown.
- **Right**
  - **Outline:** the control tree. The eye on each row hides a control in the designer only, so you can work on what's below it (Alt+click shows only that one among its siblings, e.g. one act of the loading screen); the file doesn't change. It collapses, drag a control onto another to move it inside, and it has **Group** (Ctrl+G: the selected controls go into a new panel and keep their place on screen), **Dup** (Ctrl+D) and **Del**.
  - **Properties:** a grid in categories (Layout, Appearance, Text, Behavior, Template & style, Stack panel, Other values), with editors for each kind: anchors and alignment as lists, colours, check boxes, and a texture picker (**…**) that opens the Art browser.
    - Inherited values are grey; editing one makes it the control's own, and × removes the own value again.
    - Text keys show the text the player reads.
- **New…** creates an empty layout (`<Name>_main.LayoutRoot`) with a SNO id you choose, for a screen of your own.
- Undo / redo: Ctrl+Z, Ctrl+Y. Save: Ctrl+S.
- Deep links: `…/#stock/Vendor` or `…/#custom/MyMod/data/Base/UI/X.ui`, with an optional `!<control path>` to select a control.

Saving writes the whole layout through the byte-exact writer: data the designer does not understand is kept as it was. Every save is validated first. New, grouped and moved controls are written in the same order as stock layouts (a parent before its children).

> Not modelled: positions the game sets in code at runtime (some buttons are moved by handlers) and list rows, which are created at runtime. Controls inside a stack panel are placed by the panel.
## Round trip

`D3ModTool ui-roundtrip <dir>` reads and rewrites every `.ui` under a folder and checks the bytes are identical. All 258 stock 2.8 layouts pass.

## Art and other assets

New textures and models need the game's own formats. Converting from PNG and common model formats is planned (see the [changelog](11-changelog.md)).

Until then, a mod can draw any **stock** texture by name with `draw_texture`, and replace stock files with edited copies.
