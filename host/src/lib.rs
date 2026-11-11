//! D3ModHost.dll: the client mod host. `d3inject` loads it into OUR modded client copy only (never the official install).
//!
//! The host knows the exe (addresses = RVAs in x64\Diablo III64.exe 2.8.0.99920, found by static analysis, see MODDING.md
//! "AH2 findings"); client mods do not. It:
//! - detours `LookupUIHandler(const char* name)` (0x1BB8C0). The UI loader asks it for every handler name a layout uses;
//!   stock returns NULL for names the exe no longer has (dead controls). Names registered by mods are answered by the mod.
//! - loads the client mods (mods\<id>\mod.json "client_code": {"dll": ...}) on the first lookup (game thread, after the
//!   loader lock) and gives each the `d3modapi::HostApi` table: controls by path, show/hide, text, layouts, list rows, token.
//! Refuses to do anything unless the exe's PE timestamp and the bytes at every called address are the expected ones.

#![allow(non_snake_case, clippy::missing_safety_doc)]

mod combo;
mod draw;
mod log;
mod mods;
mod token;
mod tooltip;
mod win;

use d3modapi::{Control, Handler, HostApi, Wrapper};
use std::ffi::{c_char, c_void, CStr};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::Mutex;
use win::*;

const EXPECTED_TIMESTAMP: u32 = 1_738_215_365; // PE TimeDateStamp of Diablo III64.exe 2.8.0.99920
const RVA_LOOKUP_UI_HANDLER: usize = 0x1BB8C0;
const LOOKUP_PROLOGUE: [u8; 10] = [0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20];
const RVA_MAKE_HANDLE: usize = 0x1BA580; // fn(buf: *mut u8 /*0x210*/, path: *const c_char) -> *mut u8
const MAKE_HANDLE_PROLOGUE: [u8; 4] = [0x40, 0x53, 0x48, 0x83]; // push rbx; sub rsp,..
const RVA_FIND_CONTROL: usize = 0x19D820; // fn(handle: *const u8, kind: u32) -> *mut Control (NULL if absent; kind unused)
const CONTROL_KIND_BUTTON: u32 = 4; // what GetControl (0x198140) passes
const RVA_MAKE_CHILD_HANDLE: usize = 0x1BA5A0; // fn(out: *mut u8, parent_handle: *const u8, suffix: *const c_char) -> *mut u8
const MAKE_CHILD_HANDLE_PROLOGUE: [u8; 5] = [0x48, 0x89, 0x5C, 0x24, 0x08];
// Opening a layout the way the front-end loader (0x198AF0) does for its hardcoded screen table:
const RVA_SNO_ADDREF: usize = 0x254010; // fn(sno: i32): pins the SNO in the resource table
const SNO_ADDREF_PROLOGUE: [u8; 5] = [0x8B, 0xD1, 0x48, 0x8D, 0x0D];
const RVA_CREATE_LAYOUT: usize = 0x1E3F10; // fn(layer: *mut c_void, sno: i32) -> *mut Control (root of the built tree)
const CREATE_LAYOUT_PROLOGUE: [u8; 5] = [0x48, 0x89, 0x5C, 0x24, 0x08];
// Layer objects the front-end loader creates screens in: 0x1482550 = Root.NormalLayer (Campaign, GameSettings, ...),
// 0x1482970 = Root.TopLayer (a tree built there registers as "Root.TopLayer.<name>_main" and did not show).
const RVA_FRONTEND_LAYER: usize = 0x1482550;
const RVA_CONTROL_REGISTRY: usize = 0x194DC0; // fn() -> **Map; Map { u32 mask @0, Node** buckets @0x10 }; Node { next, u64 key @8, control @0x10 }

const VTBL_SET_VISIBLE: usize = 0x48; // fn(control, u32)
const VTBL_SET_TEXT: usize = 0x120; // fn(control, const char* utf8, u32 0)
const VTBL_LIST_ADD_ITEM: usize = 0x138; // list box (type 13): fn(list, 0, 0) -> index (2.8's own lists, e.g. vanity pets 0x3E5EE0)
const VTBL_LIST_GET_ITEM: usize = 0x148; // fn(list, index) -> row control
/// A control's own handle lives at control + 0x30; it is also what click handlers receive in rcx (measured in 7 sessions).
const CONTROL_HANDLE_OFFSET: usize = 0x30;
/// SetText (vtbl+0x120, 0x1FB6A0) strcmp's the new text against the char* at control + 0xC28 and replaces it, so that field
/// holds the current text (also what is typed into a text box).
const CONTROL_TEXT_PTR: usize = 0xC28;

type LookupFn = unsafe extern "system" fn(*const c_char) -> *const c_void;
type MakeHandleFn = unsafe extern "system" fn(*mut u8, *const c_char) -> *mut u8;
type MakeChildHandleFn = unsafe extern "system" fn(*mut u8, *const u8, *const c_char) -> *mut u8;
type FindControlFn = unsafe extern "system" fn(*const u8, u32) -> *mut c_void;
type SetVisibleFn = unsafe extern "system" fn(*mut c_void, u32) -> usize;
type SetTextFn = unsafe extern "system" fn(*mut c_void, *const c_char, u32) -> usize;
type SnoAddRefFn = unsafe extern "system" fn(i32) -> usize;
type CreateLayoutFn = unsafe extern "system" fn(*mut c_void, i32) -> *mut c_void;
type ListAddItemFn = unsafe extern "system" fn(*mut c_void, usize, usize) -> i32;
type ListGetItemFn = unsafe extern "system" fn(*mut c_void, i32) -> *mut c_void;

static BASE: AtomicUsize = AtomicUsize::new(0);
static TRAMPOLINE: AtomicUsize = AtomicUsize::new(0); // original LookupUIHandler
static MODS_LOADED: AtomicBool = AtomicBool::new(false);

fn rva(r: usize) -> usize { BASE.load(Ordering::Relaxed) + r }

// ---------------------------------------------------------------------------------------------------- game primitives

unsafe fn vfn<T: Copy>(control: *mut c_void, offset: usize) -> T {
    let vtbl = *(control as *const *const usize);
    std::mem::transmute_copy(&*vtbl.add(offset / 8))
}

unsafe fn find_by_path(path: &str) -> *mut c_void {
    let mut buf = vec![0u8; 0x400]; // the game uses 0x210; generous margin
    let mut z = path.as_bytes().to_vec();
    z.push(0);
    let make: MakeHandleFn = std::mem::transmute(rva(RVA_MAKE_HANDLE));
    make(buf.as_mut_ptr(), z.as_ptr() as *const c_char);
    let find: FindControlFn = std::mem::transmute(rva(RVA_FIND_CONTROL));
    let c = find(buf.as_ptr(), CONTROL_KIND_BUTTON);
    std::mem::forget(buf); // the handle may hold game-owned data; never free it from our allocator
    c
}

unsafe fn child_of(control: *mut c_void, suffix: &[u8]) -> *mut c_void {
    let mut out = vec![0u8; 0x400];
    let mut z = suffix.to_vec();
    z.push(0);
    let make: MakeChildHandleFn = std::mem::transmute(rva(RVA_MAKE_CHILD_HANDLE));
    make(out.as_mut_ptr(), (control as usize + CONTROL_HANDLE_OFFSET) as *const u8, z.as_ptr() as *const c_char);
    let find: FindControlFn = std::mem::transmute(rva(RVA_FIND_CONTROL));
    let c = find(out.as_ptr(), CONTROL_KIND_BUTTON);
    std::mem::forget(out);
    c
}

/// The printable NUL-terminated string around the first `needle` in `mem`, if any.
fn string_with(mem: &[u8], needle: &[u8]) -> Option<String> {
    let p = mem.windows(needle.len()).position(|w| w == needle)?;
    let at = mem[..p].iter().rposition(|&c| c == 0 || !(32..127).contains(&c)).map_or(0, |z| z + 1);
    let tail = &mem[at..];
    let n = tail.iter().position(|&c| c == 0).unwrap_or(tail.len());
    if n < 4 || !tail[..n].iter().all(|c| (32..127).contains(c)) {
        return None;
    }
    let s = String::from_utf8_lossy(&tail[..n]).into_owned();
    // printable bytes in front of a heap string (allocator data) are not part of a control path
    Some(match s.find("Root.") { Some(r) if r <= 4 => s[r..].to_string(), _ => s })
}

/// Diagnostics: logs every registered control whose path (or another string it holds) contains `needle`. Short strings are
/// stored inline in the control object; long ones (most full paths) on the heap behind a pointer in the object, so pointers
/// in the object's first 0x200 bytes are followed too.
unsafe fn dump_registry(needle: &str) {
    if needle.is_empty() {
        return;
    }
    type RegFn = unsafe extern "system" fn() -> *const *const u8;
    let reg: RegFn = std::mem::transmute(rva(RVA_CONTROL_REGISTRY));
    let map = *reg();
    let mask = *(map as *const u32) as usize;
    let buckets = *(map.add(0x10) as *const *const usize);
    let (mut total, mut hits) = (0usize, 0usize);
    let n = needle.as_bytes();
    for i in 0..=mask {
        let mut node = *buckets.add(i);
        while node != 0 {
            total += 1;
            let control = *((node + 0x10) as *const usize);
            if win::readable(control, 0x400) {
                let mem = std::slice::from_raw_parts(control as *const u8, 0x400);
                let mut found = string_with(mem, n);
                if found.is_none() {
                    for off in (0..0x200).step_by(8) {
                        let p = *((control + off) as *const usize);
                        if p > 0x10000 && p % 8 == 0 && p != control && win::readable(p, 0x100) {
                            if let Some(s) = string_with(std::slice::from_raw_parts(p as *const u8, 0x100), n) {
                                found = Some(s);
                                break;
                            }
                        }
                    }
                }
                if let Some(s) = found {
                    hits += 1;
                    if hits <= 80 {
                        log::line(&format!("registry: {s}"));
                    }
                }
            }
            node = *(node as *const usize);
        }
    }
    log::line(&format!("registry: {total} controls, {hits} containing '{needle}'"));
}

// ---------------------------------------------------------------------------------------------------- the HostApi table

unsafe fn cstr<'a>(p: *const c_char) -> &'a str {
    if p.is_null() { "" } else { CStr::from_ptr(p).to_str().unwrap_or("") }
}

unsafe fn copy_out(text: &[u8], buf: *mut u8, len: usize) -> isize {
    if buf.is_null() || len == 0 {
        return -1;
    }
    let n = text.len().min(len - 1);
    std::ptr::copy_nonoverlapping(text.as_ptr(), buf, n);
    *buf.add(n) = 0;
    n as isize
}

unsafe extern "C" fn api_log(text: *const c_char) {
    log::line(cstr(text));
}

unsafe extern "C" fn api_register_handler(name: *const c_char, f: Handler) -> i32 {
    mods::register_handler(cstr(name), f)
}

unsafe extern "C" fn api_register_wrapper(name: *const c_char, w: Wrapper) -> i32 {
    mods::register_wrapper(cstr(name), w)
}

unsafe extern "C" fn api_find_control(path: *const c_char) -> Control {
    find_by_path(cstr(path))
}

unsafe extern "C" fn api_child_of(control: Control, suffix: *const c_char) -> Control {
    if control.is_null() || suffix.is_null() {
        return std::ptr::null_mut();
    }
    child_of(control, CStr::from_ptr(suffix).to_bytes())
}

unsafe extern "C" fn api_set_visible(control: Control, visible: i32) {
    if !control.is_null() {
        vfn::<SetVisibleFn>(control, VTBL_SET_VISIBLE)(control, (visible != 0) as u32);
    }
}

unsafe extern "C" fn api_set_text(control: Control, text: *const c_char) {
    // on a dropdown (UIComboBox) vtbl+0x120 is SetEnabled: never call it there (combo_set_placeholder sets its text)
    if !control.is_null() && !text.is_null() && *(control as *const usize) != rva(0x103BE58) {
        vfn::<SetTextFn>(control, VTBL_SET_TEXT)(control, text, 0);
    }
}

unsafe extern "C" fn api_get_text(control: Control, buf: *mut u8, len: usize) -> isize {
    if control.is_null() || !win::readable(control as usize + CONTROL_TEXT_PTR, 8) {
        return -1;
    }
    let p = *((control as usize + CONTROL_TEXT_PTR) as *const usize);
    if !win::readable(p, 1) {
        return -1;
    }
    copy_out(CStr::from_ptr(p as *const c_char).to_bytes(), buf, len)
}

unsafe extern "C" fn api_pin_sno(sno: i32) {
    let addref: SnoAddRefFn = std::mem::transmute(rva(RVA_SNO_ADDREF));
    addref(sno);
}

unsafe extern "C" fn api_create_layout(sno: i32) -> Control {
    api_pin_sno(sno);
    let create: CreateLayoutFn = std::mem::transmute(rva(RVA_CREATE_LAYOUT));
    let root = create(rva(RVA_FRONTEND_LAYER) as *mut c_void, sno);
    log::line(&format!("created layout SNO {sno}: root {:#x}", root as usize));
    root
}

unsafe extern "C" fn api_list_add_item(list: Control) -> i32 {
    if list.is_null() {
        return -1;
    }
    vfn::<ListAddItemFn>(list, VTBL_LIST_ADD_ITEM)(list, 0, 0)
}

unsafe extern "C" fn api_list_get_item(list: Control, index: i32) -> Control {
    if list.is_null() || index < 0 {
        return std::ptr::null_mut();
    }
    vfn::<ListGetItemFn>(list, VTBL_LIST_GET_ITEM)(list, index)
}

unsafe extern "C" fn api_is_control(arg: usize, control: Control) -> i32 {
    (!control.is_null() && (arg == control as usize + CONTROL_HANDLE_OFFSET || arg == control as usize)) as i32
}

unsafe extern "C" fn api_login_token(buf: *mut u8, len: usize) -> isize {
    match token::read() {
        Some(t) => copy_out(t.as_bytes(), buf, len),
        None => -1,
    }
}

unsafe extern "C" fn api_dump_controls(needle: *const c_char) {
    dump_registry(cstr(needle));
}

/// Set when the drawing functions passed their checks in install().
static DRAW_OK: AtomicBool = AtomicBool::new(false);

unsafe extern "C" fn api_register_draw(name: *const c_char, w: d3modapi::DrawWrapper) -> i32 {
    if !DRAW_OK.load(Ordering::Relaxed) {
        log::line(&format!("[{}] draw hook {}: drawing is disabled (exe check failed)", mods::current(), cstr(name)));
        return 4;
    }
    draw::register(cstr(name), &mods::current(), w)
}

unsafe extern "C" fn api_control_rect(control: Control, out: *mut [f32; 4]) -> i32 {
    draw::control_rect(control, out)
}

unsafe extern "C" fn api_mouse_pos(out: *mut [f32; 2]) {
    if DRAW_OK.load(Ordering::Relaxed) {
        draw::mouse_pos(out)
    }
}

unsafe extern "C" fn api_draw_item_icon(gbid: i32, rect: *const [f32; 4], depth: *mut f32) {
    if DRAW_OK.load(Ordering::Relaxed) {
        draw::draw_item_icon(gbid, rect, depth)
    }
}

unsafe extern "C" fn api_draw_texture(name: *const c_char, rect: *const [f32; 4], argb: u32, depth: *mut f32) -> i32 {
    if !DRAW_OK.load(Ordering::Relaxed) {
        return 1;
    }
    draw::draw_texture(name, rect, argb, depth)
}

static TOOLTIP_OK: AtomicBool = AtomicBool::new(false);

unsafe extern "C" fn api_show_item_tooltip(hover: usize, blob: *const u8, len: usize, gbid: i32, class: i32, male: i32, anchor: *const [f32; 4]) -> i32 {
    if !TOOLTIP_OK.load(Ordering::Relaxed) || blob.is_null() {
        return 1;
    }
    let anchor = if anchor.is_null() { None } else { Some(*anchor) };
    tooltip::show(hover, std::slice::from_raw_parts(blob, len), gbid, class, male != 0, anchor)
}

unsafe extern "C" fn api_item_name(blob: *const u8, len: usize, class: i32, male: i32, buf: *mut u8, buf_len: usize) -> isize {
    api_item_line(blob, len, class, male, 0, buf, buf_len)
}

unsafe extern "C" fn api_item_line(blob: *const u8, len: usize, class: i32, male: i32, index: i32, buf: *mut u8, buf_len: usize) -> isize {
    if !TOOLTIP_OK.load(Ordering::Relaxed) || blob.is_null() || index < 0 {
        return -1;
    }
    match tooltip::item_line(std::slice::from_raw_parts(blob, len), class, male != 0, index as usize) {
        Some(name) => copy_out(name.as_bytes(), buf, buf_len),
        None => -1,
    }
}

static COMBO_OK: AtomicBool = AtomicBool::new(false);

unsafe extern "C" fn api_combo_set_items(c: Control, texts: *const *const c_char, data: *const u64, n: usize) -> i32 {
    if !COMBO_OK.load(Ordering::Relaxed) || (n > 0 && (texts.is_null() || data.is_null())) {
        return 1;
    }
    let items: Vec<(&CStr, u64)> = (0..n).filter_map(|i| {
        let t = *texts.add(i);
        (!t.is_null()).then(|| (CStr::from_ptr(t), *data.add(i)))
    }).collect();
    (!combo::set_items(c, &items)) as i32
}

unsafe extern "C" fn api_combo_select(c: Control, index: i32, fire: i32) -> i32 {
    if !COMBO_OK.load(Ordering::Relaxed) {
        return 1;
    }
    (!combo::select(c, index, fire != 0)) as i32
}

unsafe extern "C" fn api_combo_selected(c: Control, index: *mut i32, data: *mut u64) -> i32 {
    if !COMBO_OK.load(Ordering::Relaxed) || index.is_null() || data.is_null() {
        return 1;
    }
    match combo::selected(c) {
        Some((i, d)) => {
            *index = i;
            *data = d;
            0
        }
        None => 1,
    }
}

unsafe extern "C" fn api_combo_set_placeholder(c: Control, text: *const c_char) -> i32 {
    if !COMBO_OK.load(Ordering::Relaxed) || text.is_null() {
        return 1;
    }
    (!combo::set_placeholder(c, text)) as i32
}

pub(crate) static HOST_API: HostApi = HostApi {
    version: d3modapi::API_VERSION,
    log: api_log,
    register_handler: api_register_handler,
    register_wrapper: api_register_wrapper,
    find_control: api_find_control,
    child_of: api_child_of,
    set_visible: api_set_visible,
    set_text: api_set_text,
    get_text: api_get_text,
    pin_sno: api_pin_sno,
    create_layout: api_create_layout,
    list_add_item: api_list_add_item,
    list_get_item: api_list_get_item,
    is_control: api_is_control,
    login_token: api_login_token,
    dump_controls: api_dump_controls,
    register_draw: api_register_draw,
    control_rect: api_control_rect,
    mouse_pos: api_mouse_pos,
    draw_item_icon: api_draw_item_icon,
    draw_texture: api_draw_texture,
    show_item_tooltip: api_show_item_tooltip,
    item_name: api_item_name,
    item_line: api_item_line,
    combo_set_items: api_combo_set_items,
    combo_select: api_combo_select,
    combo_selected: api_combo_selected,
    combo_set_placeholder: api_combo_set_placeholder,
};

// ---------------------------------------------------------------------------------------------------- the detour

static UNKNOWN: Mutex<Vec<Vec<u8>>> = Mutex::new(Vec::new());

unsafe extern "system" fn lookup_ui_handler_detour(name: *const c_char) -> *const c_void {
    if !MODS_LOADED.swap(true, Ordering::AcqRel) {
        mods::load_all(); // first lookup: the game thread, long after the loader lock
    }
    let original: LookupFn = std::mem::transmute(TRAMPOLINE.load(Ordering::Relaxed));
    let found = original(name);
    if name.is_null() {
        return found;
    }
    let text = CStr::from_ptr(name).to_bytes();
    if let Some(f) = mods::resolve(text, found) {
        return f;
    }
    if found.is_null() && !text.is_empty() {
        let mut seen = UNKNOWN.lock().unwrap_or_else(|e| e.into_inner());
        if !seen.iter().any(|s| s == text) {
            seen.push(text.to_vec());
            log::line(&format!("unknown UI handler (dead control): {}", String::from_utf8_lossy(text)));
        }
    }
    found
}

// ---------------------------------------------------------------------------------------------------- install

unsafe fn install() -> Result<(), String> {
    let base = GetModuleHandleW(std::ptr::null()) as usize;
    BASE.store(base, Ordering::Relaxed);
    let nt = base + *((base + 0x3C) as *const u32) as usize;
    let stamp = *((nt + 8) as *const u32);
    if stamp != EXPECTED_TIMESTAMP {
        return Err(format!("exe timestamp {stamp} is not 2.8.0.99920's {EXPECTED_TIMESTAMP}; doing nothing"));
    }
    expect_bytes(rva(RVA_LOOKUP_UI_HANDLER), &LOOKUP_PROLOGUE)?;
    expect_bytes(rva(RVA_MAKE_HANDLE), &MAKE_HANDLE_PROLOGUE)?;
    expect_bytes(rva(RVA_SNO_ADDREF), &SNO_ADDREF_PROLOGUE)?;
    expect_bytes(rva(RVA_CREATE_LAYOUT), &CREATE_LAYOUT_PROLOGUE)?;
    expect_bytes(rva(RVA_MAKE_CHILD_HANDLE), &MAKE_CHILD_HANDLE_PROLOGUE)?;
    match draw::check() {
        Ok(()) => DRAW_OK.store(true, Ordering::Relaxed),
        Err(e) => log::line(&format!("drawing disabled: {e}")),
    }
    match combo::check() {
        Ok(()) => COMBO_OK.store(true, Ordering::Relaxed),
        Err(e) => log::line(&format!("dropdowns disabled: {e}")),
    }
    match tooltip::check() {
        Ok(()) => TOOLTIP_OK.store(true, Ordering::Relaxed),
        Err(e) => log::line(&format!("item tooltips disabled: {e}")),
    }

    let target = rva(RVA_LOOKUP_UI_HANDLER);
    let stub = alloc_near(target).ok_or("no executable page within 2 GB of the exe")?;
    // stub+0x00: trampoline = the 5 stolen bytes + jmp [rip+0] -> target+5
    // stub+0x20: relay       = jmp [rip+0] -> our detour (reachable from the rel32 jmp we write at the target)
    let tramp = stub as *mut u8;
    std::ptr::copy_nonoverlapping(LOOKUP_PROLOGUE.as_ptr(), tramp, 5);
    write_abs_jmp(tramp.add(5), target + 5);
    let relay = tramp.add(0x20);
    write_abs_jmp(relay, lookup_ui_handler_detour as *const () as usize);
    TRAMPOLINE.store(stub, Ordering::Relaxed);

    let rel = (relay as isize - (target as isize + 5)) as i32;
    let mut patch = [0xE9u8, 0, 0, 0, 0];
    patch[1..].copy_from_slice(&rel.to_le_bytes());
    write_code(target, &patch)?;
    log::line(&format!("hooked LookupUIHandler at {target:#x} (base {base:#x}), stub {stub:#x}"));
    Ok(())
}

unsafe fn expect_bytes(at: usize, expected: &[u8]) -> Result<(), String> {
    let actual = std::slice::from_raw_parts(at as *const u8, expected.len());
    if actual != expected {
        return Err(format!("unexpected bytes at {at:#x}: {actual:02X?}, expected {expected:02X?}"));
    }
    Ok(())
}

unsafe fn write_abs_jmp(at: *mut u8, to: usize) {
    let code = [0xFFu8, 0x25, 0, 0, 0, 0];
    std::ptr::copy_nonoverlapping(code.as_ptr(), at, 6);
    std::ptr::copy_nonoverlapping(to.to_le_bytes().as_ptr(), at.add(6), 8);
}

unsafe fn write_code(at: usize, bytes: &[u8]) -> Result<(), String> {
    let mut old = 0u32;
    if VirtualProtect(at as *mut c_void, bytes.len(), PAGE_EXECUTE_READWRITE, &mut old) == 0 {
        return Err(format!("VirtualProtect failed at {at:#x}"));
    }
    std::ptr::copy_nonoverlapping(bytes.as_ptr(), at as *mut u8, bytes.len());
    VirtualProtect(at as *mut c_void, bytes.len(), old, &mut old);
    FlushInstructionCache(GetCurrentProcess(), at as *const c_void, bytes.len());
    Ok(())
}

/// An RWX page within +-2 GB of `near` (searched downward from just below the image, then upward).
unsafe fn alloc_near(near: usize) -> Option<usize> {
    const STEP: usize = 0x10000;
    let lo = near.saturating_sub(0x7000_0000);
    let mut addr = (near & !(STEP - 1)).saturating_sub(STEP);
    while addr > lo {
        let p = VirtualAlloc(addr as *mut c_void, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if !p.is_null() {
            return Some(p as usize);
        }
        addr -= STEP;
    }
    let mut addr = (near & !(STEP - 1)) + 0x0400_0000; // past the image (~0x1E96000)
    while addr < near + 0x7000_0000 {
        let p = VirtualAlloc(addr as *mut c_void, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        if !p.is_null() {
            return Some(p as usize);
        }
        addr += STEP;
    }
    None
}

static SELF_MODULE: AtomicUsize = AtomicUsize::new(0);

/// This DLL's own path (the default mods folder is found relative to it).
pub(crate) fn self_path() -> Option<std::path::PathBuf> {
    let mut buf = [0u16; 1024];
    let n = unsafe { GetModuleFileNameW(SELF_MODULE.load(Ordering::Relaxed) as HANDLE, buf.as_mut_ptr(), buf.len() as u32) } as usize;
    (n > 0).then(|| std::path::PathBuf::from(String::from_utf16_lossy(&buf[..n])))
}

#[no_mangle]
pub unsafe extern "system" fn DllMain(module: *mut c_void, reason: u32, _reserved: *mut c_void) -> i32 {
    const DLL_PROCESS_ATTACH: u32 = 1;
    if reason == DLL_PROCESS_ATTACH {
        SELF_MODULE.store(module as usize, Ordering::Relaxed);
        log::line("D3ModHost loaded");
        if std::env::var_os("D3MODHOST_NOHOOK").is_some() {
            log::line("D3MODHOST_NOHOOK set: loaded only, nothing patched");
            return 1;
        }
        if let Err(e) = install() {
            log::line(&format!("NOT installed: {e}"));
        }
    }
    1
}
