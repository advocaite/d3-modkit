//! Draw hooks and 2D drawing for mods (static analysis: D3Sniff\reports\item_draw_2.8.0.md, MODDING.md "Item drawing").
//!
//! Layout draw hooks (style 44, control type 17) are NOT resolved through LookupUIHandler: 0x514160 matches the name against a
//! fixed table of 9 {const char* name, fn} entries at 0x105D8B0, and the control's draw (vtbl+0xA8, 0x5140D0) calls
//! table[index].fn(flag, float* depth) every frame. register_draw swaps one entry's fn for a dispatcher that calls the mod's
//! wrapper with the original.

use super::{log, rva, win};
use d3modapi::{DrawFn, DrawWrapper, DRAW_HOOK_NAMES};
use std::ffi::{c_char, c_void, CStr};
use std::sync::atomic::{AtomicUsize, Ordering};

const RVA_DRAW_HOOK_TABLE: usize = 0x105D8B0; // 9 x {name, fn}
const RVA_ITEM_ICON: usize = 0x4A5E10; // fn(gbid, TextureRef* out, int mode 0, int class (-1 none), int gender) - icon from the item's actor
const ITEM_ICON_PROLOGUE: [u8; 6] = [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20];
const RVA_DRAW_QUAD: usize = 0x378820; // fn(tex*, tex2*, blend, float rect[4]*, u32* argb, colours*, float z, float scale, p0, -1, 0)
const DRAW_QUAD_PROLOGUE: [u8; 5] = [0x40, 0x55, 0x53, 0x56, 0x57];
const RVA_BLEND_MODE: usize = 0x701FE0; // fn(10) -> the blend mode the item icons use
const BLEND_MODE_PROLOGUE: [u8; 3] = [0x83, 0xF9, 0xFF];
const RVA_MOUSE_POS: usize = 0x1BA8E0; // fn(float out[2])
const MOUSE_POS_PROLOGUE: [u8; 6] = [0x40, 0x53, 0x48, 0x83, 0xEC, 0x20];
const RVA_TEXTURE_BY_NAME: usize = 0x3A67E0; // fn(const char* "UI:...", TextureRef* out) - leaves out untouched if unknown
const TEXTURE_BY_NAME_PROLOGUE: [u8; 9] = [0x40, 0x53, 0x48, 0x81, 0xEC, 0xB0, 0x04, 0x00, 0x00];
const RVA_TEXTURE_SIZE: usize = 0x2799B0; // fn(const TextureRef*, float out[2]) - the frame's native size in pixels
const TEXTURE_SIZE_PROLOGUE: [u8; 6] = [0x40, 0x53, 0x48, 0x83, 0xEC, 0x30];
const RVA_TEXTURE_NONE: usize = 0x13E1FE0; // TextureRef {-1, -1}
const RVA_QUAD_COLOURS: usize = 0x142639C; // what the inventory passes as the 6th argument
/// A control's screen rectangle {left, top, right, bottom} (floats) - 0x271D50 reads it.
const CONTROL_RECT: usize = 0x498;

type ItemIconFn = unsafe extern "system" fn(i32, *mut u64, i32, i32, i32) -> usize;
type TextureSizeFn = unsafe extern "system" fn(*const u64, *mut [f32; 2]) -> usize;
type TextureByNameFn =unsafe extern "system" fn(*const c_char, *mut u64) -> usize;
type BlendFn =unsafe extern "system" fn(i32) -> i32;
type DrawQuadFn = unsafe extern "system" fn(*const u64, *const u64, i32, *const [f32; 4], *const u32, *const c_void, f32, f32, *const c_void, i32, i32) -> usize;
type MousePosFn = unsafe extern "system" fn(*mut [f32; 2]) -> usize;

pub unsafe fn check() -> Result<(), String> {
    super::expect_bytes(rva(RVA_ITEM_ICON), &ITEM_ICON_PROLOGUE)?;
    super::expect_bytes(rva(RVA_DRAW_QUAD), &DRAW_QUAD_PROLOGUE)?;
    super::expect_bytes(rva(RVA_BLEND_MODE), &BLEND_MODE_PROLOGUE)?;
    super::expect_bytes(rva(RVA_MOUSE_POS), &MOUSE_POS_PROLOGUE)?;
    super::expect_bytes(rva(RVA_TEXTURE_BY_NAME), &TEXTURE_BY_NAME_PROLOGUE)?;
    super::expect_bytes(rva(RVA_TEXTURE_SIZE), &TEXTURE_SIZE_PROLOGUE)?;
    for (i, expected) in DRAW_HOOK_NAMES.iter().enumerate() {
        let name = *((rva(RVA_DRAW_HOOK_TABLE) + i * 16) as *const *const c_char);
        if !win::readable(name as usize, 1) || CStr::from_ptr(name).to_bytes() != expected.as_bytes() {
            return Err(format!("draw hook table entry {i} is not {expected}"));
        }
    }
    Ok(())
}

static ORIGINALS: [AtomicUsize; 9] = [const { AtomicUsize::new(0) }; 9];
static WRAPPERS: [AtomicUsize; 9] = [const { AtomicUsize::new(0) }; 9];

unsafe fn dispatch(i: usize, flag: usize, depth: *mut f32) -> usize {
    let original: DrawFn = std::mem::transmute(ORIGINALS[i].load(Ordering::Relaxed));
    match WRAPPERS[i].load(Ordering::Relaxed) {
        0 => original(flag, depth),
        w => std::mem::transmute::<usize, DrawWrapper>(w)(original, flag, depth),
    }
}

macro_rules! dispatchers {
    ($($i:literal)*) => {
        const DISPATCH: [DrawFn; 9] = [$({
            unsafe extern "system" fn entry(flag: usize, depth: *mut f32) -> usize { dispatch($i, flag, depth) }
            entry
        }),*];
    };
}
dispatchers!(0 1 2 3 4 5 6 7 8);

pub unsafe fn register(name: &str, owner: &str, w: DrawWrapper) -> i32 {
    let Some(i) = DRAW_HOOK_NAMES.iter().position(|n| n.eq_ignore_ascii_case(name)) else {
        log::line(&format!("[{owner}] draw hook {name} is not one of the 9 stock names; ignored"));
        return 1;
    };
    if WRAPPERS[i].load(Ordering::Relaxed) != 0 {
        log::line(&format!("[{owner}] draw hook {name} is already wrapped; ignored"));
        return 2;
    }
    let slot = (rva(RVA_DRAW_HOOK_TABLE) + i * 16 + 8) as *mut usize;
    ORIGINALS[i].store(*slot, Ordering::Relaxed);
    WRAPPERS[i].store(w as usize, Ordering::Relaxed);
    let mut old = 0u32;
    if win::VirtualProtect(slot as *mut c_void, 8, win::PAGE_READWRITE, &mut old) == 0 {
        WRAPPERS[i].store(0, Ordering::Relaxed);
        return 3;
    }
    *slot = DISPATCH[i] as usize;
    win::VirtualProtect(slot as *mut c_void, 8, old, &mut old);
    log::line(&format!("[{owner}] wrapped draw hook {name}"));
    0
}

pub unsafe fn control_rect(control: *mut c_void, out: *mut [f32; 4]) -> i32 {
    if control.is_null() || out.is_null() || !win::readable(control as usize + CONTROL_RECT, 16) {
        return 1;
    }
    *out = *((control as usize + CONTROL_RECT) as *const [f32; 4]);
    0
}

pub unsafe fn mouse_pos(out: *mut [f32; 2]) {
    if !out.is_null() {
        let f: MousePosFn = std::mem::transmute(rva(RVA_MOUSE_POS));
        f(out);
    }
}

/// UI layouts are authored for 1600 x 1200 ("Stretch" roots) and control rects (+0x498) are in those units, while the quad
/// draw (0x378820) takes window pixels: the UI is scaled to fit and centred (measured 2026-11-11 on 1920x1080: x' = 240 + 0.9x,
/// y' = 0.9y). The window's client size comes from the process's largest visible top-level window, refreshed now and then.
unsafe fn ui_to_screen(r: [f32; 4]) -> [f32; 4] {
    static SIZE: std::sync::Mutex<(u32, f32, f32)> = std::sync::Mutex::new((0, 0.0, 0.0));
    let (w, h) = {
        let mut s = SIZE.lock().unwrap_or_else(|e| e.into_inner());
        if s.0 % 600 == 0 {
            if let Some((w, h)) = win::main_window_client_size() {
                s.1 = w as f32;
                s.2 = h as f32;
            }
        }
        s.0 = s.0.wrapping_add(1);
        (s.1, s.2)
    };
    if w <= 0.0 || h <= 0.0 {
        return r;
    }
    let scale = (w / 1600.0).min(h / 1200.0);
    let (ox, oy) = ((w - 1600.0 * scale) / 2.0, (h - 1200.0 * scale) / 2.0);
    [ox + r[0] * scale, oy + r[1] * scale, ox + r[2] * scale, oy + r[3] * scale]
}

/// Draws a texture over a UI-unit rectangle at the next depth layer.
unsafe fn quad(tex: &u64, rect: [f32; 4], argb: u32, depth: *mut f32) {
    let screen = ui_to_screen(rect);
    let blend: BlendFn = std::mem::transmute(rva(RVA_BLEND_MODE));
    let quad: DrawQuadFn = std::mem::transmute(rva(RVA_DRAW_QUAD));
    let z = 0.9999 - *depth * 1e-4;
    *depth += 1.0;
    quad(tex, rva(RVA_TEXTURE_NONE) as *const u64, blend(10), &screen, &argb, rva(RVA_QUAD_COLOURS) as *const c_void, z, 1.0, std::ptr::null(), -1, 0);
}

/// `rect` in UI units (as control_rect returns).
pub unsafe fn draw_item_icon(gbid: i32, rect: *const [f32; 4], depth: *mut f32) {
    if rect.is_null() || depth.is_null() {
        return;
    }
    let icon: ItemIconFn = std::mem::transmute(rva(RVA_ITEM_ICON));
    let mut tex: u64 = u64::MAX;
    icon(gbid, &mut tex, 0, -1, 0);
    // like 0x278E00: keep the icon's aspect (native size from 0x2799B0) and centre it in the box
    let size: TextureSizeFn = std::mem::transmute(rva(RVA_TEXTURE_SIZE));
    let mut native = [0f32; 2];
    size(&tex, &mut native);
    let [l, t, r, b] = *rect;
    let (w, h) = (r - l, b - t);
    let fitted = if native[0] > 0.0 && native[1] > 0.0 && w > 0.0 && h > 0.0 {
        let s = (w / native[0]).min(h / native[1]);
        let (fw, fh) = (native[0] * s, native[1] * s);
        [l + (w - fw) / 2.0, t + (h - fh) / 2.0, l + (w + fw) / 2.0, t + (h + fh) / 2.0]
    } else {
        *rect
    };
    quad(&tex, fitted, 0xFFFF_FFFF, depth);
}

/// A UI texture by name (0x3A67E0, what the inventory uses for "UI:Quality_..." item backgrounds).
pub unsafe fn draw_texture(name: *const c_char, rect: *const [f32; 4], argb: u32, depth: *mut f32) -> i32 {
    if name.is_null() || rect.is_null() || depth.is_null() {
        return 1;
    }
    let lookup: TextureByNameFn = std::mem::transmute(rva(RVA_TEXTURE_BY_NAME));
    let mut tex: u64 = u64::MAX;
    lookup(name, &mut tex);
    if tex == u64::MAX {
        return 1;
    }
    quad(&tex, *rect, argb, depth);
    0
}
