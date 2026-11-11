//! ModKit example, client part (Rust): shows the gold rate on the main menu's Start Game button.
//!
//! What it shows:
//!   1. the entry point `d3mod_init` and `d3modapi::set_api`;
//!   2. reading the mod's own files from `mod_dir` (settings.json, shared with the server part);
//!   3. wrapping a UI handler the game already has (`UIBattlenet_Campaign_OnUpdate`, the main menu's per-frame update):
//!      the wrapper runs the game's original first, then does its own thing;
//!   4. finding a control by its full path and setting its text.
//! Build: `cargo build --release` (or tools\build_mods.ps1), install: `D3ModTool mod-install <this mod> --client <game>`.

use d3modapi as api;
use std::ffi::c_char;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::OnceLock;

/// The main menu's Start Game button (found with api::dump_controls("LayoutRoot.Menu")).
const START_BUTTON: &str = "Root.NormalLayer.BattleNetCampaign_main.LayoutRoot.Menu.PlayGameButton";

static LABEL: OnceLock<String> = OnceLock::new();
static FRAMES: AtomicU32 = AtomicU32::new(0);

#[no_mangle]
pub unsafe extern "C" fn d3mod_init(host: *const api::HostApi, mod_id: *const c_char, mod_dir: *const c_char) -> i32 {
    if !api::set_api(host, mod_id) {
        return 1; // the host is older than this SDK
    }
    let dir = api::c_str(mod_dir);
    let multiplier = read_multiplier(&format!("{dir}\\settings.json")).unwrap_or(1.0);
    let _ = LABEL.set(format!("Start Game (Gold x{multiplier})"));
    api::register_wrapper("UIBattlenet_Campaign_OnUpdate", menu_update);
    api::log(&format!("gold rate x{multiplier} - the Start Game button shows it"));
    0
}

/// Wraps the main menu's update: the game's own update first, then (every 30th frame, cheap enough) our label.
unsafe extern "system" fn menu_update(original: api::Handler, a: usize, b: usize, c: usize, d: usize) -> usize {
    let result = original(a, b, c, d);
    if FRAMES.fetch_add(1, Ordering::Relaxed) % 30 == 0 {
        if let Some(label) = LABEL.get() {
            api::set_text(api::find(START_BUTTON), label); // set_text is null-safe: nothing happens if the menu is not built
        }
    }
    result
}

/// `{"multiplier": 3.0}` - a tiny reader, so the example needs no JSON crate.
fn read_multiplier(path: &str) -> Option<f64> {
    let text = std::fs::read_to_string(path).ok()?;
    let after = text.split("\"multiplier\"").nth(1)?.split(':').nth(1)?;
    after.trim_start().split(|c: char| !(c.is_ascii_digit() || c == '.' || c == '-')).next()?.parse().ok()
}
