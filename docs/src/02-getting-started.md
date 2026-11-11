# Getting started

This walks through the `gold-rate` example: a mod that multiplies gold drops on the server and shows the rate on the main menu.

## 1. Copy the example

Copy `examples/gold-rate` to a folder of your own and give it a new id in `mod.json`. Ids are lower-case words with dashes, and they must be unique, because they name the installed folder.

```json
{
  "id": "my-gold-mod",
  "name": "My Gold Mod",
  "version": "1.0.0",
  "build": "2.8.0.99920",
  "type": "both",
  "server": { "assembly": "server/GoldRate.Server.dll", "source": "server-src/" },
  "client_code": { "dll": "client-code/gold_rate_client.dll", "source": "client-src/" }
}
```

## 2. The server part

`server-src/GoldRateMod.cs` has one class that implements `IServerMod`. The server creates it at startup and calls `Initialize`:

```csharp
public void Initialize(ServerModContext context)
{
    // read settings.json from the installed folder, then:
    context.Hooks.OnGoldDrop(e => e.Amount = (int)Math.Round(e.Amount * Multiplier));
}
```

The same assembly also defines a `[CommandGroup("goldrate", ...)]`, so `!goldrate` and `!goldrate set 5` work in game chat.

Build it with `dotnet build -c Release`. The project references `Blizzless.dll` from your server folder: set the environment variable `D3_SERVER_DIR`, or pass `/p:D3ServerDir=C:\path\to\server`.

## 3. The client part

`client-src/src/lib.rs` exports `d3mod_init`. It reads `settings.json`, then wraps the main menu's per-frame update handler:

```rust
#[no_mangle]
pub unsafe extern "C" fn d3mod_init(host: *const api::HostApi, mod_id: *const c_char, mod_dir: *const c_char) -> i32 {
    if !api::set_api(host, mod_id) { return 1; }
    api::register_wrapper("UIBattlenet_Campaign_OnUpdate", menu_update);
    0
}

unsafe extern "system" fn menu_update(original: api::Handler, a: usize, b: usize, c: usize, d: usize) -> usize {
    let result = original(a, b, c, d);          // the game's own update first
    api::set_text(api::find(START_BUTTON), "Start Game (Gold x3)");
    result
}
```

Build it with `cargo build --release`, then copy `target/release/gold_rate_client.dll` to `client-code/`.

## 4. Install and run

```bat
D3ModTool mod-install C:\mods\my-gold-mod --server C:\D3Server --client F:\D3Mods\client
D3ModTool modhost-install C:\ModKit\bin --client F:\D3Mods\client
```

The second command is only needed once per game copy.

Then:

1. Start (or restart) the server. Its console shows `Mod loaded: My Gold Mod 1.0.0 (my-gold-mod) - server code: GoldRate.Server.dll`.
2. Start the game copy through the host:
   ```bat
   F:\D3Mods\client\ModHost\d3inject.exe "F:\D3Mods\client\x64\Diablo III64.exe" F:\D3Mods\client\ModHost\D3ModHost.dll OnlineService.SSO=true -launch -uid diablo3
   ```
   A launcher can do this step for you.
3. The main menu now reads **START GAME (GOLD X3)**, and `D3ModHost.log` lists `client mod loaded: My Gold Mod ...`.

## 5. Iterate

- Close the game before rebuilding client code. The game keeps the DLL loaded.
- Stop the server before rebuilding server code, for the same reason.
- `D3ModTool mod-install` replaces the installed folder as a whole every time.
- Use `api::log(...)` freely. Lines go to `D3ModHost.log`, prefixed with your mod id.
- To find a control's path, call `api::dump_controls("SomeText")`. It logs every registered control whose path contains the text. See [Finding controls](05-ui.md#finding-controls).
