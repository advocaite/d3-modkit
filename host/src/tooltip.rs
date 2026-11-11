//! Item tooltips for items that are not in a game (static analysis: D3Sniff\reports\item_draw_2.8.0.md §4.5 and §7 "Tooltip
//! recipe"; the leaderboard inspect window does the same at the main menu).
//!
//! Called from a tooltip handler (layout style 105, resolved through LookupUIHandler): the game calls it every frame while
//! its control is hovered, with rcx = &control->handle, and hides item tooltips itself when nothing is hovered.
//! Steps: D3.Items.Generator from the serialized blob -> a client-local ACD (0x272350) -> tooltip data (0x2709D0) -> tooltip
//! params (0x272500) -> show (0x297970) -> anchor (0x2970D0). The Generator and ACD stay cached while the same item is hovered.

use super::{log, rva};
use std::sync::Mutex;

const RVA_GEN_CTOR: usize = 0x7480E0; // D3::Items::Generator::Generator(this), object 0xF0 bytes
const RVA_GEN_PARSE: usize = 0xAD2430; // ParseFromArray(msg, data, len) -> bool
const RVA_GEN_DTOR: usize = 0x7485C0; // ~Generator(this) (no free)
const RVA_HANDLE_INIT: usize = 0x26D5B0; // u32 h[4] = -1
const RVA_LOCAL_ITEM: usize = 0x272350; // (u32 h[4], const Generator*, u32 tag) -> 1 = ok; h[0] = ACD id, h[1..3] gems
const RVA_LOCAL_ITEM_FREE: usize = 0x26DB00; // (u32 h[4])
const RVA_TD_INIT: usize = 0x1BFAF0; // TooltipItemData ctor (~0x260 B)
const RVA_TD_FILL: usize = 0x2709D0; // (td, &acdId, &info{cls, gender, hero, cmp}, 0)
const RVA_TD_DTOR: usize = 0x1BFFA0;
const RVA_TP_INIT: usize = 0x1FEE90; // TooltipParams ctor (~0x110 B, no dtor)
const RVA_TP_BUILD: usize = 0x272500; // (p, -1, lastGbid, td, 0, 0, 0)
const RVA_TIP_SHOW: usize = 0x297970; // (mgr, hoverHandle*, p, 1, changed, 0, 1) -> Tooltip*
const RVA_TIP_ANCHOR: usize = 0x2970D0; // (mgr, float rect[4] (1600x1200 units), tip)
const RVA_TIP_MANAGER: usize = 0x166EE98; // the item tooltip manager
const RVA_LAST_GBID: usize = 0x13F5A8C; // u32: GBID of the last tooltip (the "changed" test)
/// ACD network-id tag for our local items: free in the client-local range (0x2E0E is the tooltip builder's, 0x2E38 inspect's).
const LOCAL_ITEM_TAG: u32 = 0x2E80;

const PROLOGUES: [(usize, &[u8]); 13] = [
    (RVA_GEN_CTOR, &[0x48, 0x89, 0x4C, 0x24, 0x08, 0x53]),
    (RVA_GEN_PARSE, &[0x41, 0x8B, 0xC0, 0x4C, 0x8B, 0xCA]),
    (RVA_GEN_DTOR, &[0x48, 0x89, 0x4C, 0x24, 0x08, 0x53]),
    (RVA_HANDLE_INIT, &[0x48, 0xC7, 0x01, 0xFF, 0xFF, 0xFF]),
    (RVA_LOCAL_ITEM, &[0x40, 0x55, 0x56, 0x57, 0x48, 0x83]),
    (RVA_LOCAL_ITEM_FREE, &[0x48, 0x89, 0x5C, 0x24, 0x08, 0x57]),
    (RVA_TD_INIT, &[0x48, 0x89, 0x4C, 0x24, 0x08, 0x53]),
    (RVA_TD_FILL, &[0x48, 0x89, 0x5C, 0x24, 0x08, 0x48]),
    (RVA_TD_DTOR, &[0x48, 0x89, 0x4C, 0x24, 0x08, 0x53]),
    (RVA_TP_INIT, &[0x48, 0xC7, 0x41, 0x1C, 0xFF, 0xFF]),
    (RVA_TP_BUILD, &[0x48, 0x89, 0x5C, 0x24, 0x08, 0x48]),
    (RVA_TIP_SHOW, &[0x48, 0x89, 0x5C, 0x24, 0x08, 0x48]),
    (RVA_TIP_ANCHOR, &[0x8B, 0x02, 0x41, 0x89, 0x80, 0x74]),
];

pub unsafe fn check() -> Result<(), String> {
    for (at, bytes) in PROLOGUES {
        super::expect_bytes(rva(at), bytes)?;
    }
    Ok(())
}

type Fn1 = unsafe extern "system" fn(*mut u8) -> usize;
type ParseFn = unsafe extern "system" fn(*mut u8, *const u8, i32) -> u8;
type LocalItemFn = unsafe extern "system" fn(*mut [u32; 4], *const u8, u32) -> i32;
type TdFillFn = unsafe extern "system" fn(*mut u8, *const u32, *const TooltipInfo, usize) -> usize;
type TpBuildFn = unsafe extern "system" fn(*mut u8, i32, u32, *mut u8, usize, usize, usize) -> usize;
type ShowFn = unsafe extern "system" fn(*mut u8, *const u8, *mut u8, i32, i32, *const u8, i32) -> *mut u8;
type AnchorFn = unsafe extern "system" fn(*mut u8, *const [f32; 4], *mut u8) -> usize;

#[repr(C)]
struct TooltipInfo {
    class: i32,
    male: i32,
    hero: usize,
    compare: usize,
}

/// The hovered item's Generator (0xF0 bytes, 8-aligned: 30 u64) and local ACD, kept while the same blob is hovered.
struct Cached {
    blob: Vec<u8>,
    generator: Box<[u64; 30]>,
    handle: [u32; 4],
}
unsafe impl Send for Cached {}

static CACHE: Mutex<Option<Cached>> = Mutex::new(None);

unsafe fn f<T>(at: usize) -> T {
    std::mem::transmute_copy(&rva(at))
}

unsafe fn release(c: &mut Cached) {
    f::<unsafe extern "system" fn(*mut [u32; 4]) -> usize>(RVA_LOCAL_ITEM_FREE)(&mut c.handle);
    f::<Fn1>(RVA_GEN_DTOR)(c.generator.as_mut_ptr() as *mut u8);
}

/// The local item for `blob` in the cache (created when it is a different item). 0 = ok, else show()'s error codes.
unsafe fn ensure_item(cache: &mut Option<Cached>, blob: &[u8]) -> i32 {
    if cache.as_ref().map(|c| c.blob != blob).unwrap_or(true) {
        if let Some(mut old) = cache.take() {
            release(&mut old);
        }
        let mut generator = Box::new([0u64; 30]);
        let gen = generator.as_mut_ptr() as *mut u8;
        f::<Fn1>(RVA_GEN_CTOR)(gen);
        if f::<ParseFn>(RVA_GEN_PARSE)(gen, blob.as_ptr(), blob.len() as i32) == 0 {
            f::<Fn1>(RVA_GEN_DTOR)(gen);
            log::line(&format!("tooltip: generator ({} bytes) did not parse", blob.len()));
            return 2;
        }
        let mut handle = [u32::MAX; 4];
        f::<unsafe extern "system" fn(*mut [u32; 4]) -> usize>(RVA_HANDLE_INIT)(&mut handle);
        if f::<LocalItemFn>(RVA_LOCAL_ITEM)(&mut handle, gen, LOCAL_ITEM_TAG) == 0 {
            f::<Fn1>(RVA_GEN_DTOR)(gen);
            log::line("tooltip: the local item could not be created");
            return 3;
        }
        *cache = Some(Cached { blob: blob.to_vec(), generator, handle });
    }
    0
}

/// Fills a TooltipItemData (0x400 B buffer, 8-aligned) for the cached local item; the caller runs RVA_TD_DTOR on it.
unsafe fn fill_td(c: &Cached, td: *mut u8, class: i32, male: bool) {
    f::<Fn1>(RVA_TD_INIT)(td);
    let info = TooltipInfo { class, male: male as i32, hero: 0, compare: 0 };
    f::<TdFillFn>(RVA_TD_FILL)(td, &c.handle[0], &info, 0);
}

/// TooltipItemData holds a run of 0x18-byte strings from +0x20 (ctor 0x1BFAF0), char* at +8 of each (c_str 0x17DCE0 =
/// `mov rax,[rcx+8]`). The first one is the item's display name.
const TD_NAME: usize = 0x20;
const TD_STRING_SIZE: usize = 0x18;

unsafe fn td_string(td: *const u8, at: usize) -> Option<String> {
    let p = *(td.add(at + 8) as *const *const u8);
    if !super::win::readable(p as usize, 1) {
        return None;
    }
    Some(std::ffi::CStr::from_ptr(p as *const std::ffi::c_char).to_string_lossy().into_owned())
}

/// Removes the game's text markup ("{c:ffff8000}Name{/c}", "{icon:...}").
fn strip_markup(s: &str) -> String {
    let mut out = String::new();
    let mut depth = 0;
    for ch in s.chars() {
        match ch {
            '{' => depth += 1,
            '}' if depth > 0 => depth -= 1,
            _ if depth == 0 => out.push(ch),
            _ => {}
        }
    }
    out.trim().to_string()
}

/// Line `index` of the item's tooltip text (0 = the localized display name, rare names included; 1 = the type line such as
/// "Magic Chest Armor"; 2.. further tooltip strings), markup stripped.
pub unsafe fn item_line(blob: &[u8], class: i32, male: bool, index: usize) -> Option<String> {
    if blob.is_empty() {
        return None;
    }
    let mut cache = CACHE.lock().unwrap_or_else(|e| e.into_inner());
    if ensure_item(&mut cache, blob) != 0 {
        return None;
    }
    let c = cache.as_ref().unwrap();
    let mut td = vec![0u64; 0x400 / 8];
    let td_ptr = td.as_mut_ptr() as *mut u8;
    fill_td(c, td_ptr, class, male);
    static LOGGED: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
    if !LOGGED.swap(true, std::sync::atomic::Ordering::Relaxed) {
        let strings: Vec<String> = (0..10).filter_map(|i| td_string(td_ptr, TD_NAME + i * TD_STRING_SIZE).map(|s| format!("+{:#x} {s:?}", TD_NAME + i * TD_STRING_SIZE))).collect();
        log::line(&format!("tooltip strings: {}", strings.join(" | ")));
    }
    let name = if index < 10 { td_string(td_ptr, TD_NAME + index * TD_STRING_SIZE) } else { None }
        .map(|s| strip_markup(&s)).filter(|s| !s.is_empty());
    f::<Fn1>(RVA_TD_DTOR)(td_ptr);
    name
}

/// 0 = shown, 1 = bad arguments, 2 = the blob did not parse, 3 = no local item, 4 = the tooltip manager refused.
pub unsafe fn show(hover: usize, blob: &[u8], gbid: i32, class: i32, male: bool, anchor: Option<[f32; 4]>) -> i32 {
    if hover == 0 || blob.is_empty() {
        return 1;
    }
    let mut cache = CACHE.lock().unwrap_or_else(|e| e.into_inner());
    let r = ensure_item(&mut cache, blob);
    if r != 0 {
        return r;
    }
    let c = cache.as_ref().unwrap();

    let mut td = vec![0u64; 0x400 / 8];
    let td_ptr = td.as_mut_ptr() as *mut u8;
    fill_td(c, td_ptr, class, male);

    let last = rva(RVA_LAST_GBID) as *mut u32;
    let changed = *last != gbid as u32;
    if changed {
        *last = gbid as u32;
    }
    let mut p = vec![0u64; 0x400 / 8];
    let p_ptr = p.as_mut_ptr() as *mut u8;
    f::<Fn1>(RVA_TP_INIT)(p_ptr);
    f::<TpBuildFn>(RVA_TP_BUILD)(p_ptr, -1, *last, td_ptr, 0, 0, 0);
    let manager = rva(RVA_TIP_MANAGER) as *mut u8;
    let tip = f::<ShowFn>(RVA_TIP_SHOW)(manager, hover as *const u8, p_ptr, 1, changed as i32, std::ptr::null(), 1);
    if !tip.is_null() {
        if let Some(rect) = anchor {
            f::<AnchorFn>(RVA_TIP_ANCHOR)(manager, &rect, tip);
        }
    }
    f::<Fn1>(RVA_TD_DTOR)(td_ptr);
    if tip.is_null() { 4 } else { 0 }
}
