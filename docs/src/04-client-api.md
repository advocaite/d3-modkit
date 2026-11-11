# Client API reference

The host passes plugins a `HostApi` table (C: `D3HostApi`, C#: `D3HostApi`). This page lists every entry, with the Rust wrapper from `d3modapi` in brackets. **API version: 7.**

> **Compatibility.** Entries are only ever appended to the end of the table, with the version raised by one. Nothing is removed or reordered. A plugin checks `api->version >= the version it was built for`, so newer hosts run older plugins.

## Entry point

```c
int32_t d3mod_init(const D3HostApi *api, const char *mod_id, const char *mod_dir);
```

- The host calls it **once**, on the game thread, before the game resolves any UI handler.
- `mod_id` is your id from `mod.json`.
- `mod_dir` is your installed folder: read your settings and files from there.
- Return `0` on success. Any other value means "not loaded", and the host logs the code.
- Rust: call `d3modapi::set_api(api, mod_id)` first; it returns `false` if the host is too old.
- C#: call `D3.Init(api, modId)` first.

**Threading.** Every API function must be called on the game thread: from `d3mod_init`, a UI handler, a wrapper or a draw hook. To do slow work (network, files), use your own thread, then apply the results from a handler that runs every frame, such as a wrapped `..._OnUpdate`.

## Types

| Type | Meaning |
|---|---|
| `Control` | An opaque pointer to a game UI control. `null` = not found. Every Rust wrapper is null-safe. |
| `Handler` | `fn(rcx, rdx, r8, r9) -> usize`: a UI handler as the game calls it. For click handlers, `rcx` is the clicked control's handle (compare it with `is_control`). |
| `Wrapper` | `fn(original, rcx, rdx, r8, r9) -> usize`: wraps a handler the game has. Call `original` to keep the game's behaviour. |
| `DrawFn` / `DrawWrapper` | `fn(flag, depth) -> usize` / `fn(original, flag, depth)`: draw hooks, see [Drawing](05-ui.md#drawing). |
| rect | `[left, top, right, bottom]` in **UI units**: the screen is 1600 × 1200 at any resolution; the host maps them to pixels. |

## Version 1: handlers and controls

### log
`void log(const char *line)` [`log(&str)`]

Writes one line to `<game>\D3ModHost.log`. The Rust and C# wrappers prefix your mod id.

### register_handler
`int32_t register_handler(const char *name, Handler f)` [`register_handler(name, f) -> bool`]

Provides a UI handler name that the exe **does not** have. Use it for handlers your own layouts name, or 2.0.1 names in ported layouts. Returns 0 on success, or 1 if another mod already registered the name; the first registration wins. If the exe *does* have the name, your handler is not used and the host logs a hint to use a wrapper.

### register_wrapper
`int32_t register_wrapper(const char *name, Wrapper w)` [`register_wrapper(name, w) -> bool`]

Wraps a handler the exe **does** have. Your wrapper runs instead and receives the original. Several mods can wrap the same name: they chain in load order, and the first one registered is the innermost. There are 32 wrapper slots in total.

### find_control
`Control find_control(const char *full_path)` [`find(path)`]

A control by its full path, for example `Root.NormalLayer.BattleNetCampaign_main.LayoutRoot.Menu.PlayGameButton`. Returns null when it does not exist, for example when the screen is not built yet.

### child_of
`Control child_of(Control c, const char *relative)` [`child(c, ".Child.Grandchild")`]

A descendant by relative path, starting with a dot. Works for list rows and template instances, where full paths are not registered.

### set_visible / set_text / get_text
- `void set_visible(Control, int32_t)` [`show(c, bool)`]
- `void set_text(Control, const char *utf8)` [`set_text(c, &str)`]
- `intptr_t get_text(Control, uint8_t *buf, size_t len)` [`get_text(c) -> Option<String>`]: returns the length, or -1.

Text can carry the game's markup, for example `{icon:gold}`, `{c:ff99bbff}colored{/c}` and `\n`. `set_text` leaves dropdowns alone; use the combo functions for those.

### pin_sno / create_layout
- `void pin_sno(int32_t sno)` [`pin_sno`]
- `Control create_layout(int32_t sno)` [`create_layout(sno)`]

Builds a UI layout SNO (yours from `data/`, or a stock one) into `Root.NormalLayer`, registered as `Root.NormalLayer.<LayoutName>_main`. A layout that another layout uses as a template must be pinned before the outer one is built.

**Screens are torn down when the player joins or leaves a game.** Check with `find(...)` whether your root still exists, and rebuild it if not. Reset any cached row counts when you do.

### list_add_item / list_get_item
- `int32_t list_add_item(Control list)` [`list_add`]: adds a row from the list box's template and returns its index.
- `Control list_get_item(Control list, int32_t index)` [`list_get`]

Works on list boxes (control type 13). **Only address rows you created:** `list_get_item` on a missing index returns garbage, which crashes when used. Keep your own row count.

### is_control
`int32_t is_control(uintptr_t handler_rcx, Control c)` [`is_control(rcx, c) -> bool`]

Whether a click handler's `rcx` is this control. Use it to tell which list row's button was clicked.

### login_token
`intptr_t login_token(uint8_t *buf, size_t len)` [`login_token()`]

The player's login token, used to authenticate your mod's REST calls to its server part. **It contains the password: only send it in a POST body** to your own server, and never log it.

### dump_controls
`void dump_controls(const char *needle)` [`dump_controls(&str)`]

Diagnostics: logs up to 80 registered controls whose stored path contains `needle`, plus a total. See [Finding controls](05-ui.md#finding-controls).

## Version 2: drawing

These work only **inside a draw hook**; see [Drawing](05-ui.md#drawing).

| Entry | Rust | Notes |
|---|---|---|
| `register_draw(name, DrawWrapper)` | `register_draw` | Wraps one of the 9 stock draw-hook names (`DRAW_HOOK_NAMES`). One wrapper per name. Returns 4 if drawing is disabled because the exe check failed. |
| `control_rect(Control, float[4]) -> 0 ok` | `control_rect(c) -> Option<[f32;4]>` | The control's rectangle in UI units. |
| `mouse_pos(float[2])` | `mouse_pos()` | The mouse position in UI units. |
| `draw_item_icon(gbid, rect, depth)` | `draw_item_icon` | An item's inventory icon (by GBID), aspect-fitted into the rectangle. |
| `draw_texture(name, rect, argb, depth) -> 0 drawn / 1 missing` | `draw_texture(...) -> bool` | Any UI texture by name, for example `UI:Stash_Slot_Empty` or `UI:Quality_Large_Inventory_Legendary`, stretched over the rectangle and tinted by `argb` (`0xFFFFFFFF` = unchanged). |

## Version 4: item tooltips

`int32_t show_item_tooltip(hover, generator, len, gbid, class, male, anchor)` [`show_item_tooltip(hover, &[u8], gbid, class, male, Option<rect>) -> bool`]

Call it from a **tooltip handler**: a layout style 105 handler, called every frame while its control is hovered; `hover` is that handler's `rcx`. It shows the game's real item tooltip for a serialized `D3.Items.Generator` protobuf (the item's saved form, which the server can produce).

- `class` is the class index for the "equipped" comparison, or -1.
- `male` picks the gender for gendered item names.
- `anchor` is an optional rectangle to place the tooltip next to.

The Generator's `flags` field sets, among other things, ancient (`0x400`) and primal (`0x4000`).

## Version 5 and 6: item text

- `item_name(generator, len, class, male, buf, buf_len) -> len or -1` [`item_name(&[u8], class, male)`]: the localized display name, including rare names.
- `item_line(generator, len, class, male, index, buf, buf_len)` [`item_line(..., index)`]: one string of the tooltip. 0 = name, 1 = type line ("Legendary Two-Handed Mace"), 3 = the big number (armor or DPS). Markup is stripped.

## Version 7: dropdowns

Dropdowns are layout control type 20 (UIComboBox). Every function returns 0, or non-zero if the control is not a dropdown.

| Entry | Rust |
|---|---|
| `combo_set_items(combo, texts[], data[], n)`: replaces the entries; each has a `u64` of yours | `combo_set_items(c, &[(&str, u64)])` |
| `combo_select(combo, index, fire)`: -1 = none (the placeholder shows); `fire` also runs the selection-changed handler (style 101) | `combo_select` |
| `combo_selected(combo, &index, &data)` | `combo_selected(c) -> Option<(i32, u64)>` |
| `combo_set_placeholder(combo, text)` | `combo_set_placeholder` |

The number of visible rows comes from layout property `0x87`.

## Helpers (Rust only)

- `report_missing(path)` logs, once per path, a control path that does not exist, together with the deepest prefix that does.
- `c_str(ptr)` copies a C string.
