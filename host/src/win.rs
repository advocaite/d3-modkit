//! The few Win32 calls we need, declared by hand (no crate dependencies).
#![allow(dead_code, clippy::upper_case_acronyms)]

use std::ffi::c_void;

pub type HANDLE = *mut c_void;
pub const MEM_COMMIT: u32 = 0x1000;
pub const MEM_RESERVE: u32 = 0x2000;
pub const PAGE_EXECUTE_READWRITE: u32 = 0x40;
pub const PAGE_READWRITE: u32 = 0x04;

#[link(name = "kernel32")]
extern "system" {
    pub fn GetModuleHandleW(name: *const u16) -> HANDLE;
    pub fn GetModuleHandleA(name: *const u8) -> HANDLE;
    pub fn GetProcAddress(module: HANDLE, name: *const u8) -> *mut c_void;
    pub fn VirtualAlloc(addr: *mut c_void, size: usize, kind: u32, protect: u32) -> *mut c_void;
    pub fn VirtualProtect(addr: *mut c_void, size: usize, protect: u32, old: *mut u32) -> i32;
    pub fn FlushInstructionCache(process: HANDLE, addr: *const c_void, size: usize) -> i32;
    pub fn GetCurrentProcess() -> HANDLE;
    pub fn GetModuleFileNameW(module: HANDLE, buf: *mut u16, size: u32) -> u32;
    pub fn GetCurrentThreadId() -> u32;
    pub fn VirtualQuery(addr: *const c_void, info: *mut MEMORY_BASIC_INFORMATION, len: usize) -> usize;
    pub fn LoadLibraryW(name: *const u16) -> HANDLE;
    pub fn GetLastError() -> u32;
}

#[link(name = "kernel32")]
extern "system" {
    pub fn LocalFree(mem: *mut c_void) -> *mut c_void;
}

pub const HKEY_CURRENT_USER: isize = 0x8000_0001u32 as i32 as isize;
pub const RRF_RT_REG_BINARY: u32 = 0x0000_0008;

#[link(name = "advapi32")]
extern "system" {
    pub fn RegGetValueW(key: isize, subkey: *const u16, value: *const u16, flags: u32, kind: *mut u32, data: *mut c_void, len: *mut u32) -> i32;
}

#[repr(C)]
pub struct DATA_BLOB {
    pub len: u32,
    pub data: *mut u8,
}

#[link(name = "crypt32")]
extern "system" {
    pub fn CryptUnprotectData(input: *const DATA_BLOB, description: *mut *mut u16, entropy: *const DATA_BLOB, reserved: *mut c_void,
                              prompt: *mut c_void, flags: u32, output: *mut DATA_BLOB) -> i32;
}

#[repr(C)]
pub struct MEMORY_BASIC_INFORMATION {
    pub base: *mut c_void,
    pub allocation_base: *mut c_void,
    pub allocation_protect: u32,
    pub partition_id: u16,
    pub region_size: usize,
    pub state: u32,
    pub protect: u32,
    pub kind: u32,
}

#[repr(C)]
pub struct RECT {
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
}

type WndEnumProc = unsafe extern "system" fn(HANDLE, isize) -> i32;

#[link(name = "user32")]
extern "system" {
    pub fn EnumWindows(f: WndEnumProc, param: isize) -> i32;
    pub fn GetWindowThreadProcessId(hwnd: HANDLE, pid: *mut u32) -> u32;
    pub fn IsWindowVisible(hwnd: HANDLE) -> i32;
    pub fn GetClientRect(hwnd: HANDLE, rect: *mut RECT) -> i32;
}

#[link(name = "kernel32")]
extern "system" {
    pub fn GetCurrentProcessId() -> u32;
}

/// Client size of this process's largest visible top-level window (the game window).
pub fn main_window_client_size() -> Option<(i32, i32)> {
    unsafe extern "system" fn each(hwnd: HANDLE, param: isize) -> i32 {
        let best = &mut *(param as *mut (u32, i32, i32));
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, &mut pid);
        if pid == best.0 && IsWindowVisible(hwnd) != 0 {
            let mut r = RECT { left: 0, top: 0, right: 0, bottom: 0 };
            if GetClientRect(hwnd, &mut r) != 0 && (r.right - r.left) * (r.bottom - r.top) > best.1 * best.2 {
                best.1 = r.right - r.left;
                best.2 = r.bottom - r.top;
            }
        }
        1
    }
    let mut best: (u32, i32, i32) = (unsafe { GetCurrentProcessId() }, 0, 0);
    unsafe { EnumWindows(each, &mut best as *mut _ as isize) };
    (best.1 > 0 && best.2 > 0).then_some((best.1, best.2))
}

/// True when [addr, addr+len) lies in one committed, readable region.
pub unsafe fn readable(addr: usize, len: usize) -> bool {
    if addr < 0x10000 {
        return false;
    }
    let mut mbi: MEMORY_BASIC_INFORMATION = std::mem::zeroed();
    if VirtualQuery(addr as *const c_void, &mut mbi, std::mem::size_of::<MEMORY_BASIC_INFORMATION>()) == 0 {
        return false;
    }
    const MEM_COMMIT: u32 = 0x1000;
    const READABLE: u32 = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80; // R, RW, WC, XR, XRW, XWC
    mbi.state == MEM_COMMIT
        && mbi.protect & READABLE != 0
        && mbi.protect & 0x100 == 0 // PAGE_GUARD
        && addr + len <= mbi.base as usize + mbi.region_size
}
