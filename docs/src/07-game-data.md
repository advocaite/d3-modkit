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

A UI designer (open, edit and create `.ui` files visually) is planned; until then, these commands plus `ui-info --full` are the way.

## Art and other assets

New textures and models need the game's own formats. Converting from PNG and common model formats is planned (see the [changelog](11-changelog.md)).

Until then, a mod can draw any **stock** texture by name with `draw_texture`, and replace stock files with edited copies.
