# Examples

## gold-rate (server hook + Rust client)

`examples/gold-rate`: multiplies every gold drop by `settings.json`'s `multiplier` (default 3).

| File | Shows |
|---|---|
| `mod.json` | a mod with both code parts |
| `settings.json` | settings shared by both parts, installed next to `mod.json` |
| `server-src/GoldRateMod.cs` | `IServerMod`, reading settings from `context.Directory`, `context.Hooks.OnGoldDrop`, a `[CommandGroup]` (`!goldrate`, `!goldrate set 5`) |
| `client-src/src/lib.rs` | `d3mod_init`, reading `mod_dir`, wrapping `UIBattlenet_Campaign_OnUpdate`, `find` + `set_text` on the Start Game button |

## hello-csharp (C# client)

`examples/hello-csharp`: a client-only plugin in C#, built with NativeAOT. It wraps the main menu's update and, once, logs the menu's controls with `dump_controls`. That's how you discover the control paths your mod needs.

## auction-house (everything)

The workspace's `mods/auction-house` is the 2.0.1 Auction House ported to 2.8. It is the large, real-world example:

- **Data:** 23 converted 2.0.1 layouts, plus a changed main menu with an AH button.
- **Client (Rust):**
  - tab switching, list boxes with templates, dropdowns;
  - drawing item icons and quality art in draw hooks;
  - item tooltips;
  - an HTTP client on its own thread, with results applied on the game thread;
  - rebuilding its window after the screens are torn down.
- **Server (C#):**
  - REST routes (`/ah/...`) authenticated with the login token;
  - its own database tables (listings, credits);
  - escrowed items, item Generators for tooltips;
  - admin commands (`!ah`).

Its source is not part of the kit, because its data is Blizzard's 2.0.1 layouts. Ask for it if you want to study it.
