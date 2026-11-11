//! d3inject: starts OUR modded client copy suspended, loads D3ModHost.dll into it, then lets it run.
//!   d3inject <game exe> <dll> [game arguments...]
//! Refuses the official install (a path under Program Files): mods only ever run in a copy of the game folder.
#![allow(non_snake_case, clippy::upper_case_acronyms)]

use std::ffi::c_void;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;

type HANDLE = *mut c_void;

#[repr(C)]
struct STARTUPINFOW {
    cb: u32, reserved: *mut u16, desktop: *mut u16, title: *mut u16,
    x: u32, y: u32, x_size: u32, y_size: u32, x_chars: u32, y_chars: u32, fill: u32, flags: u32,
    show_window: u16, reserved2_len: u16, reserved2: *mut u8, std_in: HANDLE, std_out: HANDLE, std_err: HANDLE,
}

#[repr(C)]
struct PROCESS_INFORMATION { process: HANDLE, thread: HANDLE, process_id: u32, thread_id: u32 }

const CREATE_SUSPENDED: u32 = 0x4;
const MEM_COMMIT: u32 = 0x1000;
const MEM_RESERVE: u32 = 0x2000;
const PAGE_READWRITE: u32 = 0x04;
const INFINITE: u32 = 0xFFFF_FFFF;

#[link(name = "kernel32")]
extern "system" {
    fn CreateProcessW(app: *const u16, cmd: *mut u16, pa: *mut c_void, ta: *mut c_void, inherit: i32, flags: u32,
                      env: *mut c_void, cwd: *const u16, si: *mut STARTUPINFOW, pi: *mut PROCESS_INFORMATION) -> i32;
    fn VirtualAllocEx(p: HANDLE, addr: *mut c_void, size: usize, kind: u32, protect: u32) -> *mut c_void;
    fn WriteProcessMemory(p: HANDLE, addr: *mut c_void, buf: *const c_void, size: usize, written: *mut usize) -> i32;
    fn GetModuleHandleW(name: *const u16) -> HANDLE;
    fn GetProcAddress(m: HANDLE, name: *const u8) -> *mut c_void;
    fn CreateRemoteThread(p: HANDLE, sa: *mut c_void, stack: usize, start: *mut c_void, param: *mut c_void, flags: u32, id: *mut u32) -> HANDLE;
    fn WaitForSingleObject(h: HANDLE, ms: u32) -> u32;
    fn GetExitCodeThread(h: HANDLE, code: *mut u32) -> i32;
    fn ResumeThread(h: HANDLE) -> u32;
    fn TerminateProcess(p: HANDLE, code: u32) -> i32;
    fn CloseHandle(h: HANDLE) -> i32;
    fn GetLastError() -> u32;
}

fn wide(s: &std::ffi::OsStr) -> Vec<u16> { s.encode_wide().chain(std::iter::once(0)).collect() }

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 3 {
        eprintln!("usage: d3inject <game exe> <D3ModHost.dll> [game arguments...]");
        std::process::exit(1);
    }
    // NOT fs::canonicalize: that returns a verbatim "\\?\F:\..." path, and the game derives its install folder from its own
    // path; started that way, tact::ClientHandler::Create() fails with error 1 (seen 2026-11-11).
    let absolute = |p: &str| -> std::path::PathBuf {
        let path = std::path::absolute(p).unwrap_or_else(|e| fail(&format!("{p}: {e}")));
        if !path.exists() { fail(&format!("{} does not exist", path.display())); }
        path
    };
    let exe = absolute(&args[1]);
    let dll = absolute(&args[2]);
    let exe_text = exe.to_string_lossy().to_lowercase();
    if exe_text.contains("program files") {
        fail("refusing to inject into the official install; mods run only in a copy of the game folder (outside Program Files)");
    }
    let cwd = exe.parent().unwrap_or(Path::new("."));
    let mut command = format!("\"{}\"", exe.display());
    for a in &args[3..] {
        command.push(' ');
        command.push_str(a);
    }

    unsafe {
        let mut si: STARTUPINFOW = std::mem::zeroed();
        si.cb = std::mem::size_of::<STARTUPINFOW>() as u32;
        let mut pi: PROCESS_INFORMATION = std::mem::zeroed();
        let mut cmd = wide(std::ffi::OsStr::new(&command));
        let exe_w = wide(exe.as_os_str());
        let cwd_w = wide(cwd.as_os_str());
        if CreateProcessW(exe_w.as_ptr(), cmd.as_mut_ptr(), std::ptr::null_mut(), std::ptr::null_mut(), 0, CREATE_SUSPENDED,
                          std::ptr::null_mut(), cwd_w.as_ptr(), &mut si, &mut pi) == 0 {
            fail(&format!("CreateProcess failed ({})", GetLastError()));
        }
        let abort = |why: &str| -> ! {
            TerminateProcess(pi.process, 1);
            fail(why)
        };

        let dll_w = wide(dll.as_os_str());
        let bytes = dll_w.len() * 2;
        let remote = VirtualAllocEx(pi.process, std::ptr::null_mut(), bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if remote.is_null() { abort("VirtualAllocEx failed"); }
        if WriteProcessMemory(pi.process, remote, dll_w.as_ptr() as *const c_void, bytes, std::ptr::null_mut()) == 0 {
            abort("WriteProcessMemory failed");
        }
        // kernel32 is mapped at the same address in every process of a boot session
        let k32 = GetModuleHandleW(wide(std::ffi::OsStr::new("kernel32.dll")).as_ptr());
        let load_library = GetProcAddress(k32, b"LoadLibraryW\0".as_ptr());
        let thread = CreateRemoteThread(pi.process, std::ptr::null_mut(), 0, load_library, remote, 0, std::ptr::null_mut());
        if thread.is_null() { abort(&format!("CreateRemoteThread failed ({})", GetLastError())); }
        WaitForSingleObject(thread, INFINITE);
        let mut code = 0u32;
        GetExitCodeThread(thread, &mut code);
        CloseHandle(thread);
        if code == 0 { abort("LoadLibraryW returned NULL inside the game (see D3ModHost.log / the DLL path)"); }

        ResumeThread(pi.thread);
        println!("started {} (pid {}) with {}", exe.display(), pi.process_id, dll.display());
        CloseHandle(pi.thread);
        CloseHandle(pi.process);
    }
}

fn fail(why: &str) -> ! {
    eprintln!("d3inject: {why}");
    std::process::exit(2);
}
