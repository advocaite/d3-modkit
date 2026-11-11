//! Dropdowns (layout control type 20 = UIComboBox, vtable 0x103BE58). Static analysis: D3Sniff\reports\dropdown_2.8.0.md.
//! The list functions are plain functions (no virtuals); every call checks the control's vtable first, so a wrong path never
//! reaches them. NOTE: on a combo box vtbl+0x120 is SetEnabled, not SetText (host set_text must not be used on one).
//! The style-101 (selection changed) handler gets only rcx = &combo->handle; it reads the new selection itself.

use super::rva;
use std::ffi::{c_char, c_void};

const RVA_VTABLE: usize = 0x103BE58;
const RVA_ADD: usize = 0x2686A0; // (c, const char* utf8, u64 data, i32 enabled) - text copied
const RVA_CLEAR: usize = 0x268F80; // (c) - selection -1, label not refreshed
const RVA_SET_SELECTED: usize = 0x268FD0; // (c, i32 idx, i32 fireChange, i32 fireSelChanged); -1 = placeholder
const RVA_GET_SELECTED: usize = 0x2689F0; // (c) -> i32 (c+0xE9C)
const RVA_GET_COUNT: usize = 0x2689E0; // (c) -> i32 (c+0xEA8)
const RVA_GET_DATA: usize = 0x2689C0; // (c, i32 idx) -> u64
const RVA_SET_PLACEHOLDER: usize = 0x2695F0; // (c, const char*)
const RVA_SET_OPEN: usize = 0x2690D0; // (c, i32)
const OFF_IS_OPEN: usize = 0xA70;

const PROLOGUES: [(usize, &[u8]); 8] = [
    (RVA_ADD, &[0x48, 0x89, 0x5C, 0x24, 0x10, 0x55]),
    (RVA_CLEAR, &[0x40, 0x53, 0x48, 0x83, 0xEC, 0x20]),
    (RVA_SET_SELECTED, &[0x48, 0x89, 0x5C, 0x24, 0x08, 0x48]),
    (RVA_GET_SELECTED, &[0x8B, 0x81, 0x9C, 0x0E, 0x00, 0x00]),
    (RVA_GET_COUNT, &[0x8B, 0x81, 0xA8, 0x0E, 0x00, 0x00]),
    (RVA_GET_DATA, &[0x4C, 0x8B, 0xC1, 0x83, 0xFA, 0xFF]),
    (RVA_SET_PLACEHOLDER, &[0x48, 0x89, 0x74, 0x24, 0x10, 0x57]),
    (RVA_SET_OPEN, &[0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58]),
];

pub unsafe fn check() -> Result<(), String> {
    for (at, bytes) in PROLOGUES {
        super::expect_bytes(rva(at), bytes)?;
    }
    Ok(())
}

/// The control, if it really is a UIComboBox.
unsafe fn combo(c: *mut c_void) -> Option<*mut c_void> {
    (!c.is_null() && super::win::readable(c as usize, 8) && *(c as *const usize) == rva(RVA_VTABLE)).then_some(c)
}

unsafe fn f<T>(at: usize) -> T {
    std::mem::transmute_copy(&rva(at))
}

/// Replaces the entries: (text, data) pairs; closes the list first if it is open. Returns false if not a combo box.
pub unsafe fn set_items(c: *mut c_void, items: &[(&std::ffi::CStr, u64)]) -> bool {
    let Some(c) = combo(c) else { return false };
    if *((c as usize + OFF_IS_OPEN) as *const i32) != 0 {
        f::<unsafe extern "system" fn(*mut c_void, i32)>(RVA_SET_OPEN)(c, 0);
    }
    f::<unsafe extern "system" fn(*mut c_void)>(RVA_CLEAR)(c);
    let add: unsafe extern "system" fn(*mut c_void, *const c_char, u64, i32) = f(RVA_ADD);
    for (text, data) in items {
        add(c, text.as_ptr(), *data, 1);
    }
    true
}

/// Selects entry `index` (-1 = none / placeholder); `fire` also runs the selection-changed handlers.
pub unsafe fn select(c: *mut c_void, index: i32, fire: bool) -> bool {
    let Some(c) = combo(c) else { return false };
    f::<unsafe extern "system" fn(*mut c_void, i32, i32, i32)>(RVA_SET_SELECTED)(c, index, fire as i32, fire as i32);
    true
}

/// (selected index or -1, its data) - None if not a combo box.
pub unsafe fn selected(c: *mut c_void) -> Option<(i32, u64)> {
    let c = combo(c)?;
    let idx = f::<unsafe extern "system" fn(*mut c_void) -> i32>(RVA_GET_SELECTED)(c);
    let count = f::<unsafe extern "system" fn(*mut c_void) -> i32>(RVA_GET_COUNT)(c);
    let data = if idx >= 0 && idx < count { f::<unsafe extern "system" fn(*mut c_void, i32) -> u64>(RVA_GET_DATA)(c, idx) } else { 0 };
    Some((idx, data))
}

pub unsafe fn set_placeholder(c: *mut c_void, text: *const c_char) -> bool {
    let Some(c) = combo(c) else { return false };
    f::<unsafe extern "system" fn(*mut c_void, *const c_char)>(RVA_SET_PLACEHOLDER)(c, text);
    true
}
