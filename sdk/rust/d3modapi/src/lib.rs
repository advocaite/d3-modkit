//! The API between D3ModHost.dll (the host, loaded into OUR modded 2.8.0.99920 client copy) and client mod plugins.
//!
//! Compatibility rule: fields are only ever APPENDED to HostApi (with API_VERSION + 1); nothing is removed or reordered.
//! Documentation: ModKit\docs (built to ModKit\docs\index.html) - keep it in sync with every change here.
//!
//! A client mod is a DLL (<game>\Mods\<id>\client-code\*.dll, named in mod.json "client_code": {"dll": ...}) exporting
//!     #[no_mangle] pub unsafe extern "C" fn d3mod_init(api: *const HostApi, mod_id: *const c_char, mod_dir: *const c_char) -> i32
//! The host calls it once, on the game thread, before the game resolves any UI handler; a plugin registers its handlers there
//! (return 0 = ok). Everything a plugin does to the game goes through the HostApi table - plugins contain no exe offsets.
//! Plugins written in Rust call `d3modapi::set_api(api, mod_id)` in d3mod_init and then use the safe functions below.
//! mod_dir is the mod's own folder: a plugin reads its own data/config files from there (the mod's game data - layouts,
//! textures, strings, new SNOs - goes into the client copy through `D3ModTool mod-build`, its server code into the server).
//!
//! UI handlers: the game binds layout handler names (style 41 click, 99 update, 101 selection...) to functions by name.
//!   register_handler(name, f): f answers a name the exe does NOT have (2.0.1 names, or new ones a mod's layout uses).
//!   register_wrapper(name, w): w wraps a name the exe DOES have: w(original, args...) runs instead and may call original.
//! Handlers get (rcx, rdx, r8, r9); for click handlers rcx = the clicked control's handle = control + 0x30 (see is_control).

use std::ffi::{c_char, c_void, CStr, CString};
use std::sync::atomic::{AtomicUsize, Ordering};

pub const API_VERSION: u32 = 7;
pub const INIT_SYMBOL: &[u8] = b"d3mod_init\0";

/// A game UI control (opaque).
pub type Control = *mut c_void;
/// A UI handler: rcx, rdx, r8, r9 as the game passes them.
pub type Handler = unsafe extern "system" fn(usize, usize, usize, usize) -> usize;
/// A wrapper around an existing handler: (original, rcx, rdx, r8, r9).
pub type Wrapper = unsafe extern "system" fn(Handler, usize, usize, usize, usize) -> usize;
/// A draw hook (layout style 44 on a type-17 control), called every frame while the control is shown: (flag, depth).
/// `depth` is the 2D layer counter: draw at z = 0.9999 - depth * 1e-4 and increase it (draw_item_icon does both).
pub type DrawFn = unsafe extern "system" fn(usize, *mut f32) -> usize;
/// A wrapper around a stock draw hook: (original, flag, depth).
pub type DrawWrapper = unsafe extern "system" fn(DrawFn, usize, *mut f32) -> usize;
/// The stock draw-hook names. 2.8 resolves style-44 names only from this fixed table (not through LookupUIHandler), so a
/// mod's layout uses one of these names (one whose window does not run when the mod's control is shown, e.g. UIHireling_Draw
/// at the main menu) and wraps it.
pub const DRAW_HOOK_NAMES: [&str; 9] = [
    "UIShop_Draw", "UIInventory_Draw", "UIInspect_Draw", "UIEquipmentManager_DrawInventory", "PlayerInspect_InventoryDraw",
    "UITransmute_DrawGrid", "UIHireling_Draw", "UIGameBase_Draw", "UIStash_Draw",
];
pub type InitFn =unsafe extern "C" fn(api: *const HostApi, mod_id: *const c_char, mod_dir: *const c_char) -> i32;

#[repr(C)]
pub struct HostApi {
    pub version: u32,
    /// One line to D3ModHost.log.
    pub log: unsafe extern "C" fn(*const c_char),
    pub register_handler: unsafe extern "C" fn(*const c_char, Handler) -> i32,
    pub register_wrapper: unsafe extern "C" fn(*const c_char, Wrapper) -> i32,
    /// A control by full path ("Root.NormalLayer.<Layout>_main.LayoutRoot..."), or null.
    pub find_control: unsafe extern "C" fn(*const c_char) -> Control,
    /// A child by relative path (".Child.Grandchild") - also works for list rows and template instances.
    pub child_of: unsafe extern "C" fn(Control, *const c_char) -> Control,
    pub set_visible: unsafe extern "C" fn(Control, i32),
    pub set_text: unsafe extern "C" fn(Control, *const c_char),
    /// Copies the control's text (UTF-8, NUL-terminated) into buf; returns its length, or -1 if it has none.
    pub get_text: unsafe extern "C" fn(Control, *mut u8, usize) -> isize,
    /// Pins a UI SNO (a layout that another layout pulls in as a template must be pinned before that layout is built).
    pub pin_sno: unsafe extern "C" fn(i32),
    /// Builds a UI layout SNO into Root.NormalLayer (registered as "Root.NormalLayer.<Name>_main"); returns its root.
    pub create_layout: unsafe extern "C" fn(i32) -> Control,
    /// List box (type 13) rows from its template: add one (returns its index), get one by index.
    pub list_add_item: unsafe extern "C" fn(Control) -> i32,
    pub list_get_item: unsafe extern "C" fn(Control, i32) -> Control,
    /// Whether a handler's rcx is this control (rcx = control + 0x30).
    pub is_control: unsafe extern "C" fn(usize, Control) -> i32,
    /// The player's login token (contains the password: only ever send it in a POST body). Length, or -1.
    pub login_token: unsafe extern "C" fn(*mut u8, usize) -> isize,
    /// Diagnostics: logs registered controls whose path contains the text.
    pub dump_controls: unsafe extern "C" fn(*const c_char),
    // ---- version 2: drawing
    /// Wraps a stock draw hook (one of DRAW_HOOK_NAMES); one wrapper per name.
    pub register_draw: unsafe extern "C" fn(*const c_char, DrawWrapper) -> i32,
    /// The control's screen rectangle {left, top, right, bottom} (UI units, the same the draw calls take). 0 = ok.
    pub control_rect: unsafe extern "C" fn(Control, *mut [f32; 4]) -> i32,
    /// The mouse position (UI units).
    pub mouse_pos: unsafe extern "C" fn(*mut [f32; 2]),
    /// Draws the inventory icon of an item (by GBID) fitted into the rectangle (UI units, like control_rect: the host maps
    /// them to window pixels); call only inside a draw hook.
    pub draw_item_icon: unsafe extern "C" fn(i32, *const [f32; 4], *mut f32),
    /// Draws a UI texture by name ("UI:Stash_Slot_Empty", "UI:Quality_Large_Inventory_Legendary", ...) stretched over the
    /// rectangle (UI units), tinted argb (0xFFFFFFFF = as is); inside a draw hook only. 0 = drawn, 1 = no such texture.
    pub draw_texture: unsafe extern "C" fn(*const c_char, *const [f32; 4], u32, *mut f32) -> i32,
    // ---- version 4: item tooltips
    /// From a tooltip handler (layout style 105; called every frame while its control is hovered, rcx = the hover handle):
    /// shows the item tooltip for a serialized D3.Items.Generator. (hover, blob, len, gbid, class index or -1, male,
    /// anchor rect in UI units or null). 0 = shown.
    pub show_item_tooltip: unsafe extern "C" fn(usize, *const u8, usize, i32, i32, i32, *const [f32; 4]) -> i32,
    // ---- version 5
    /// The localized display name of an item (serialized D3.Items.Generator), as its tooltip shows it (rare names
    /// included): (blob, len, class or -1, male, out UTF-8 buf, buf len) -> length, or -1. Game thread only.
    pub item_name: unsafe extern "C" fn(*const u8, usize, i32, i32, *mut u8, usize) -> isize,
    // ---- version 6
    /// Line `index` of the item's tooltip text (0 = name, 1 = type line like "Magic Chest Armor", ...): (blob, len, class,
    /// male, index, out buf, buf len) -> length, or -1. Game thread only.
    pub item_line: unsafe extern "C" fn(*const u8, usize, i32, i32, i32, *mut u8, usize) -> isize,
    // ---- version 7: dropdowns (layout control type 20, UIComboBox). 0 = ok, else not a dropdown / unavailable.
    /// Replaces the entries: n texts (UTF-8) with a u64 of our own each (read back with combo_selected).
    pub combo_set_items: unsafe extern "C" fn(Control, *const *const c_char, *const u64, usize) -> i32,
    /// Selects entry idx (-1 = none: the placeholder text); fire != 0 also runs the selection-changed handler.
    pub combo_select: unsafe extern "C" fn(Control, i32, i32) -> i32,
    /// The selected entry: index (-1 = none) and its u64.
    pub combo_selected: unsafe extern "C" fn(Control, *mut i32, *mut u64) -> i32,
    /// The text shown while nothing is selected.
    pub combo_set_placeholder: unsafe extern "C" fn(Control, *const c_char) -> i32,
}

// ------------------------------------------------------------------------------------------- safe wrappers for plugins

static API: AtomicUsize = AtomicUsize::new(0);
static MOD_ID: std::sync::OnceLock<String> = std::sync::OnceLock::new();

/// Call once in d3mod_init with its arguments. Returns false (and the plugin should return non-zero) when the host is older
/// than this SDK (the table only ever grows at its end, so a newer host serves older plugins).
/// `mod_id` prefixes every log line of this plugin.
pub unsafe fn set_api(api: *const HostApi, mod_id: *const c_char) -> bool {
    if api.is_null() || (*api).version < API_VERSION {
        return false;
    }
    let _ = MOD_ID.set(c_str(mod_id));
    API.store(api as usize, Ordering::Release);
    true
}

fn api() -> &'static HostApi {
    unsafe { &*(API.load(Ordering::Acquire) as *const HostApi) }
}

fn c(s: &str) -> CString {
    CString::new(s.replace('\0', " ")).unwrap_or_default()
}

pub fn log(line: &str) {
    let z = c(&format!("[{}] {line}", MOD_ID.get().map(String::as_str).unwrap_or("?")));
    unsafe { (api().log)(z.as_ptr()) }
}

pub fn register_handler(name: &str, f: Handler) -> bool {
    let z = c(name);
    unsafe { (api().register_handler)(z.as_ptr(), f) == 0 }
}

pub fn register_wrapper(name: &str, w: Wrapper) -> bool {
    let z = c(name);
    unsafe { (api().register_wrapper)(z.as_ptr(), w) == 0 }
}

pub fn find(path: &str) -> Control {
    let z = c(path);
    unsafe { (api().find_control)(z.as_ptr()) }
}

pub fn child(control: Control, suffix: &str) -> Control {
    if control.is_null() {
        return std::ptr::null_mut();
    }
    let z = c(suffix);
    unsafe { (api().child_of)(control, z.as_ptr()) }
}

/// Null-safe show/hide.
pub fn show(control: Control, visible: bool) {
    if !control.is_null() {
        unsafe { (api().set_visible)(control, visible as i32) }
    }
}

/// Null-safe SetText.
pub fn set_text(control: Control, text: &str) {
    if !control.is_null() {
        let z = c(text);
        unsafe { (api().set_text)(control, z.as_ptr()) }
    }
}

pub fn get_text(control: Control) -> Option<String> {
    if control.is_null() {
        return None;
    }
    let mut buf = vec![0u8; 4096];
    let n = unsafe { (api().get_text)(control, buf.as_mut_ptr(), buf.len()) };
    (n >= 0).then(|| String::from_utf8_lossy(&buf[..n as usize]).into_owned())
}

pub fn pin_sno(sno: i32) {
    unsafe { (api().pin_sno)(sno) }
}

pub fn create_layout(sno: i32) -> Control {
    unsafe { (api().create_layout)(sno) }
}

pub fn list_add(list: Control) -> i32 {
    unsafe { (api().list_add_item)(list) }
}

pub fn list_get(list: Control, index: i32) -> Control {
    unsafe { (api().list_get_item)(list, index) }
}

pub fn is_control(handler_arg: usize, control: Control) -> bool {
    !control.is_null() && unsafe { (api().is_control)(handler_arg, control) != 0 }
}

pub fn login_token() -> Option<String> {
    let mut buf = vec![0u8; 1024];
    let n = unsafe { (api().login_token)(buf.as_mut_ptr(), buf.len()) };
    (n >= 0).then(|| String::from_utf8_lossy(&buf[..n as usize]).into_owned())
}

pub fn dump_controls(needle: &str) {
    let z = c(needle);
    unsafe { (api().dump_controls)(z.as_ptr()) }
}

pub fn register_draw(name: &str, w: DrawWrapper) -> bool {
    let z = c(name);
    unsafe { (api().register_draw)(z.as_ptr(), w) == 0 }
}

pub fn control_rect(control: Control) -> Option<[f32; 4]> {
    if control.is_null() {
        return None;
    }
    let mut r = [0f32; 4];
    (unsafe { (api().control_rect)(control, &mut r) } == 0).then_some(r)
}

pub fn mouse_pos() -> [f32; 2] {
    let mut p = [0f32; 2];
    unsafe { (api().mouse_pos)(&mut p) };
    p
}

/// Fills a dropdown with (text, our id) entries. False if `control` is not a dropdown.
pub fn combo_set_items(control: Control, items: &[(&str, u64)]) -> bool {
    let texts: Vec<CString> = items.iter().map(|(t, _)| c(t)).collect();
    let ptrs: Vec<*const c_char> = texts.iter().map(|t| t.as_ptr()).collect();
    let data: Vec<u64> = items.iter().map(|(_, d)| *d).collect();
    unsafe { (api().combo_set_items)(control, ptrs.as_ptr(), data.as_ptr(), items.len()) == 0 }
}

/// Selects entry `index` (-1 = none); `fire` also runs the dropdown's selection-changed handler.
pub fn combo_select(control: Control, index: i32, fire: bool) -> bool {
    unsafe { (api().combo_select)(control, index, fire as i32) == 0 }
}

/// (selected index, its id); None if not a dropdown. Index -1 = nothing selected.
pub fn combo_selected(control: Control) -> Option<(i32, u64)> {
    let (mut i, mut d) = (-1i32, 0u64);
    (unsafe { (api().combo_selected)(control, &mut i, &mut d) } == 0).then_some((i, d))
}

pub fn combo_set_placeholder(control: Control, text: &str) -> bool {
    let z = c(text);
    unsafe { (api().combo_set_placeholder)(control, z.as_ptr()) == 0 }
}

/// From a tooltip handler (style 105) only: `hover` is the handler's rcx. True when the tooltip is shown.
pub fn show_item_tooltip(hover: usize, generator: &[u8], gbid: i32, class: i32, male: bool, anchor: Option<[f32; 4]>) -> bool {
    let a = anchor.as_ref().map_or(std::ptr::null(), |r| r as *const [f32; 4]);
    unsafe { (api().show_item_tooltip)(hover, generator.as_ptr(), generator.len(), gbid, class, male as i32, a) == 0 }
}

/// The item's localized name (game thread only: UI handlers / draw hooks).
pub fn item_name(generator: &[u8], class: i32, male: bool) -> Option<String> {
    let mut buf = vec![0u8; 512];
    let n = unsafe { (api().item_name)(generator.as_ptr(), generator.len(), class, male as i32, buf.as_mut_ptr(), buf.len()) };
    (n > 0).then(|| String::from_utf8_lossy(&buf[..n as usize]).into_owned())
}

/// Line `index` of the item's tooltip text (0 = name, 1 = type line). Game thread only.
pub fn item_line(generator: &[u8], class: i32, male: bool, index: i32) -> Option<String> {
    let mut buf = vec![0u8; 1024];
    let n = unsafe { (api().item_line)(generator.as_ptr(), generator.len(), class, male as i32, index, buf.as_mut_ptr(), buf.len()) };
    (n > 0).then(|| String::from_utf8_lossy(&buf[..n as usize]).into_owned())
}

/// Inside a draw hook only. False when the texture does not exist.
pub unsafe fn draw_texture(name: &str, rect: [f32; 4], argb: u32, depth: *mut f32) -> bool {
    let z = c(name);
    (api().draw_texture)(z.as_ptr(), &rect, argb, depth) == 0
}

/// Inside a draw hook only.
pub unsafe fn draw_item_icon(gbid: i32, rect: [f32; 4], depth: *mut f32) {
    (api().draw_item_icon)(gbid, &rect, depth)
}

/// Logs (once per path) a control path that does not exist, with the deepest existing prefix - for layout debugging.
pub fn report_missing(path: &str) {
    use std::collections::HashSet;
    use std::sync::Mutex;
    static SEEN: Mutex<Option<HashSet<String>>> = Mutex::new(None);
    if let Ok(mut s) = SEEN.lock() {
        if !s.get_or_insert_with(HashSet::new).insert(path.to_string()) {
            return;
        }
    }
    let mut cut = path.len();
    let mut deepest = "(nothing)".to_string();
    while let Some(dot) = path[..cut].rfind('.') {
        cut = dot;
        if !find(&path[..cut]).is_null() {
            deepest = path[..cut].to_string();
            break;
        }
    }
    log(&format!("control not found: {path} (deepest existing: {deepest})"));
}

/// Copies a C string argument (e.g. d3mod_init's mod_id).
pub unsafe fn c_str(p: *const c_char) -> String {
    if p.is_null() { String::new() } else { CStr::from_ptr(p).to_string_lossy().into_owned() }
}
