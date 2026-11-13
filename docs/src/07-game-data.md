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

- **UI art:** the designer's **Import PNG…** (above) makes a new `2DUI_` texture.
- **Static props (3D models):** `import-gltf` (below) turns a glTF model into a new actor.
- **Animated monsters:** the same command turns a *skinned* glTF (an armature with animations) into a monster with a skeleton, animations and an AnimSet; see [Animated monsters](07-game-data.md#animated-monsters-skinned-gltf).
- A mod can also draw any **stock** texture by name with `draw_texture`, and replace stock files with edited copies.

## Models: `import-gltf`

```
D3ModTool import-gltf <model.glb|.gltf> <modDir> --name <Name> [--like <actor>] [--sno <first id>] [--scale <s>] [--actor-scale <s>]
                      [--forward +z|-z|+x|-x] [--static yes] [--fps <n>] [--install DIR]
         skinned:     [--monster new|new:<sno|name>|<sno|name>] [--display-name <text>] [--monster-type <type>]
                      [--root-motion auto|off] [--speed walk=<ft/s>,run=<ft/s>]
```

A model **without a skin** becomes a static prop (this section). A model **with a skin** becomes an animated monster (next section), unless `--static yes` imports its rest pose as a prop.

It turns a glTF 2.0 model into a **static prop** and writes three kinds of SNO files into the mod's `data\Base\` folder:

| File | What |
|---|---|
| `Textures\<Name>_diff.tex` | One texture per material. It is DXT1, or DXT5 when the image has transparency, with a full mip chain down to a 4-pixel side. |
| `Appearance\<Name>.app` | The mesh, with one sub-object per glTF primitive and one material per glTF material. The texture goes in the diffuse slot. |
| `Actor\<Name>.acr` | An actor that uses the appearance. Spawn this one in the game. |

The new ids are added to `mod.json` `"adds"`.

**Template.** Every field the tool does not understand is copied from a stock prop, so the new files differ from a known-good file only where they have to.

- `--like` names the template actor, by SNO id or name. The default is `61743` (`a2dun_Aqd_Act_Post_B_02_Base`): a non-interactive **ServerProp** with one mesh, no bones and one diffuse texture.
- The template must be a type the server can spawn, such as ServerProp. **Environment props don't work:** the client places those itself from the scene data and ignores the server's copy, so they never appear. That's why `433` (`Skeleton_1H_bonePile`) can't be used. Confirmed in game on 2026-11-13: an obelisk built on `61743` shows up both with `!spawn` and when placed with the world editor.
- The template must be that simple: one look, one material, one unskinned mesh, no bones, no collision or physics data. The tool refuses any other template.
- Templates are read from the official install, which is only ever read.
- The actor keeps the template's type, tags and collision settings. Its bounds are recomputed. Its PhysMesh is cleared. The `Scale` tag is set to `--actor-scale` (default 1).

**Input rules.**

- glTF space: Y up, metres, counter-clockwise front faces, as Blender's exporter writes it.
- The game is Z up, in feet, and a model faces +X. `--scale` defaults to 3.28084 (metres to feet). Use `--scale 1` for a model made in feet.
- `--forward` names the glTF axis the model faces. Props default to `+x`, the mapping of the first version, so re-imports stay identical. Skinned models default to `+z`, the glTF and Blender convention for characters.
- Node transforms are applied. Morph targets and texture transforms are ignored, with a warning; so are skins and animations when `--static yes` is given.
- Each primitive needs `POSITION`. `NORMAL` is optional (smooth normals are computed when it is missing), and so is `TEXCOORD_0`. A sub-object holds at most 65,535 vertices.
- The base color texture must be a PNG, either embedded or a file next to a `.gltf`. Other sizes are resized to powers of two, up to 2048. `baseColorFactor` is multiplied in. A material without a texture becomes a flat colour.
- Only the base colour is used. Normal, emissive and metallic/roughness maps are ignored.

**Ids.** A re-import keeps the ids the mod's files already have, and new files of that mod get the first free ids from its lowest id. Otherwise the tool takes the first ids from `--sno` (default 600000) that are free in the game and in every mod folder next to this one. The names must not already exist in the game.

**Getting it into the game:**

1. `mod-install <modDir> --client <game copy>` builds it into the game copy.
2. Point the server's `[Storage] GameInstallPath` at the same game copy, so it knows the new actor.
3. Spawn the actor, e.g. GM command `!spawn <actor id>`, or place it with the DevHub world editor.

The new actor is not interactive and has no collision mesh.

## Animated monsters (skinned glTF)

When the glTF has a skin, `import-gltf` writes a whole monster:

| File | What |
|---|---|
| `Textures\<Name>_diff.tex`, `<Name>_glow.tex` | The base colour and, when the template's materials use a glow map, the glow map from the glTF **emissive texture** (× `emissiveFactor`). Without an emissive texture the glow map is flat `emissiveFactor`, black by default. |
| `Appearance\<Name>.app` | The meshes with the skeleton: bone names, parents, bind pose, inverse bind pose, local transforms, and up to 3 bone weights per vertex. |
| `Anim\<Name>_<clip>.ani` | One animation per glTF animation, resampled at `--fps` (default 30) and keyframe-reduced. |
| `AnimSet\<Name>.ans` | Which animation the monster plays for each animation tag (idle, walk, attack, death …). |
| `Monster\<Name>.mon` | The monster's **own Monster SNO**: a copy of the template's (stats, AI, powers, treasure) with your type. Not written with `--monster <sno>`. |
| `enUS\StringList\Monsters.stl` | The stock list of monster names plus yours (`--display-name`); see [Monster name and type](#monster-name-and-type). |
| `Actor\<Name>.acr` | A **Monster** actor using all of the above. Spawn this one. |

**Template.** The default is `5187` (`Sandling_A`): a plain hostile melee monster with one skinned mesh, one material, 13 looks and 22 bones. `--like` picks another animated actor.

The new actor keeps the template's:
- type, tags, sounds and collision, and (through its copy of the Monster SNO) stats, AI and powers;
- looks: normal, champion, rare, invisible, and the fire, poison, holy, … deaths. Every look uses the template's shader and its generic overlay textures with your textures put in.

Your model replaces its mesh, skeleton, bounds and animations, and the `Scale` tag is set to `--actor-scale`.

Texture slots that belong to the template's own UV layout are dropped: normal map, specular and masks. Overlay textures named after the template model are swapped for the stock generic ones.

**Model rules.**

- **One armature:** one glTF skin for all skinned meshes. A mesh without a skin that hangs under a bone follows that bone rigidly.
- **Bones:**
  - Each bone needs a unique name, because animations find bones by name.
  - The rest pose is the bind pose.
  - Scale must be uniform; a non-uniform scale is averaged, with a warning.
- **Weights:** D3 keeps **3 influences** per vertex. When a vertex has 4, the smallest is dropped and the rest renormalised, with a warning.
- **Hardpoints** are the attachment points for effects, the health bar and weapons. Every hardpoint of the template is kept:
  - An empty node under a bone, named `HP_…` or like a template hardpoint, places it exactly.
  - Otherwise it goes on your bone with the same name as the template's bone. Trailing digits are ignored, so `Spine3` finds `Spine`.
  - Otherwise it goes on the nearest bone, scaled to your model's height.
  - Using the usual names (`Pelvis`, `Spine`, `Head`, `LUpArm`, `RHand`, `LFoot`, …) helps.
- **Facing:** the model faces +Z in glTF (the default `--forward +z`), with Y up.
- Moving clips (`walk`, `run`): animate them **with root motion**, the body travelling forward over the cycle while each foot stays planted. The importer takes the straight-line travel out of the root bone and stores it as the anim's travel; see [Walking and running](#walking-and-running).

**Clip names → animation tags.** The last word of the clip name counts, so `Armature|idle` and `golem_attack` both work. Case is ignored.

| Clip name | Plays for | Loops |
|---|---|---|
| `idle`, `neutral`, `stand` | Neutral, Idle, and every tag that has no clip of its own | yes |
| `walk` | Walk, TownWalk, WalkBack. Falls back to `run`, then idle. | yes |
| `run`, `sprint` | Run, TownRun, Flee. Falls back to `walk`, then idle. | yes |
| `attack`, `swing`, `slam`, `cast` | Attack, Attack2, RangedAttack, SpecialAttack, Cast, GenericCast, Parry | no |
| `hit`, `gethit`, `hurt` | GetHit and the KnockBack tags | no |
| `stunned`, `stun` | Stunned | yes |
| `death`, `die` | Death and all Death* variants (fire, cold, holy, …) | no |
| `dead`, `corpse` | Dead, Dead1–8: the corpse pose | yes |
| `spawn`, `intro` | Spawn, Spawn2, Ambush | no |
| a tag name (`Attack2`, `DeathFire`, `TownWalk` …) or number (`69792`) | exactly that tag | by role |

- A word `loop` or `once` in the name overrides the loop column.
- Without a `dead` clip, a 2-frame `<Name>_dead` is made from the last frame of `death`, like stock corpse animations.
- Without an `idle` clip, the first clip is used. A model with no animations gets a 2-frame idle in the bind pose.
- The AnimSet fills every tag the template's AnimSet has, plus the core tags (Neutral, Idle, Walk, Run, Attack, GetHit, Stunned, Death, Dead, Spawn), plus every tag of a role you provide a clip for.
  - So the client never misses an animation, and a walking monster with no `walk` clip plays its idle. 28 stock AnimSets do the same.
- A clip that matches nothing is still written as an Anim, and the import warns that no tag plays it.

**Example:** a test mod `custom-monster-test` holds an original stone golem: 15 bones and `idle` / `attack` / `death` clips, all made by a script.

```
D3ModTool import-gltf golem.glb mods\custom-monster-test --name Mod_StoneGolem --display-name "Rune Golem" --monster-type Beast
```

This writes actor 600120, appearance 600121, textures 600122/600123, AnimSet 600124, anims 600125–600130 (idle, attack, death, dead, walk, run), its own Monster SNO 600131 (type Beast) and the name "Rune Golem". In the game, spawn it with `!spawn 600120`. It fights with the Sandling's AI and melee attack.

### Monster name and type

The game shows a monster's **name** and, below it, its **type**. Both come from the monster's Monster SNO, not from the actor:

- **Name:** the client takes the actor's Monster SNO, gets that SNO's **name** (`Mod_StoneGolem`), and looks it up as a key in the StringList `Monsters` of the player's language. The stock Sandling shows "Dust Imp" because `Monsters.stl` has `Sandling_A = Dust Imp`. A missing key shows an empty name.
- **Type:** the Monster SNO's type number. Its name is the key in the StringList `MonsterTypes`:

| `--monster-type` | Shows | Notes |
|---|---|---|
| `Undead` (0), `Demon` (1), `Beast` (2), `Human` (3) | the same word | the normal hostile types; "damage against" item affixes use them |
| `CorruptedAngel` (9) | Reaper | |
| `Pandemonium` (10) | Ancient Beast | |
| `Blood Golem` (12) | Blood Golem | |
| `Breakable` (4), `Scenery` (5), `Ally` (6), `Team` (7), `Helper` (8), `Adria` (11) | | not for enemies: the server makes Breakable a gizmo and Ally, Helper and Scenery an NPC; Adria has no text |

So a monster of its own needs its own Monster SNO. That is the default, `--monster new`: a copy of the template's Monster SNO with a new id, named like the actor, pointing back at the new actor, with your `--monster-type` and without the template's bestiary (lore) entry. HP, speed, AI, powers and loot stay the template's. `--monster new:<sno|name>` copies another stock monster's instead. `--monster <sno|name>` uses a stock Monster SNO as it is; the monster then shows that monster's name and type, and `--display-name` / `--monster-type` are refused.

`--display-name` adds the key to a copy of `Monsters.stl` in the mod (see [Game texts](#game-texts-stringlists)). Without it, players see the SNO name.

### Walking and running

Stock walk and run animations are **in place**: the skeleton does not travel. The file stores the travel separately: the root displacement for every frame and the average velocity. The Sandling's walk travels 3.9 ft over a 1 s cycle (3.9 ft/s), its run 9 ft/s. The game moves the monster, and the stored velocity is what lets it pace the steps to the movement.

- With `--root-motion auto` (the default), the importer looks at every `walk` / `run` clip. When the root bone travels in it, the importer takes the straight-line travel (first frame to last) out of the root bone and stores it as that displacement and velocity. Sway and bob stay in the animation.
- So animate a walk the natural way: the body moves forward, and each foot stays planted on the ground while it carries the weight. A looping cycle ends one stride further on, in the same pose.
- `--speed walk=3.9,run=9` sets the stored speed (ft/s) by clip name or role, for example for a clip made in place. A speed that doesn't match the steps makes the feet slide.
- A walk or run clip with no travel and no `--speed` gets a warning: velocity 0 gives the game nothing to pace the steps with.
- Match the template's speeds, which the import prints (the golem: walk 3.94 ft/s, run 9.0 ft/s, like the Sandling), so the steps play at about the stock rate.

## Game texts (StringLists)

Every text the game shows lives in a StringList SNO (group 42), one file per list and language, in the language's folder: `enUS\StringList\Monsters.stl`. An entry is a key and a text. The client finds an entry by a hash of its key (`h = h × 33 + byte`, case-sensitive), so keys must match exactly.

- To change or add a text, the mod carries the **whole list** with your entries added; it replaces the stock list (same SNO id). `D3ModTool string-set <modDir> <List> <Key> <Text>` does this. It starts from the mod's copy when there is one (so several texts add up), else from the stock list. Every stock entry stays byte-identical, and it refuses a key whose hash equals another key's. `import-gltf --display-name` uses it.
- The reader and writer rebuild all 5,508 stock lists byte for byte (`string-roundtrip`).
- **Languages:** the file is written for every language of the install that has the list. A US install has only `enUS`. A player whose game runs in another language reads that language's list, which a mod built on a US install doesn't have, so the name is empty for that player. Run the import or `string-set` against an install with that language (`--install`), or add the language's file.
- Two mods that change the same list don't merge: the later mod's file wins. Put all texts of one list in one mod.
