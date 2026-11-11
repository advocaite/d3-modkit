//! D3ModHost.log next to the client folder's root (x64\..\D3ModHost.log), appended, one timestamped line per event.

use std::collections::HashSet;
use std::io::Write;
use std::sync::Mutex;

static SEEN: Mutex<Option<HashSet<Vec<u8>>>> = Mutex::new(None);
static FILE: Mutex<Option<std::fs::File>> = Mutex::new(None);

fn path() -> std::path::PathBuf {
    let mut buf = [0u16; 1024];
    let n = unsafe { super::win::GetModuleFileNameW(std::ptr::null_mut(), buf.as_mut_ptr(), buf.len() as u32) } as usize;
    let exe = std::path::PathBuf::from(String::from_utf16_lossy(&buf[..n]));
    // ...\client\x64\Diablo III64.exe -> ...\client\D3ModHost.log
    exe.parent().and_then(|p| p.parent()).map(|p| p.join("D3ModHost.log")).unwrap_or_else(|| "D3ModHost.log".into())
}

pub fn line(text: &str) {
    let mut guard = match FILE.lock() { Ok(g) => g, Err(_) => return };
    if guard.is_none() {
        *guard = std::fs::OpenOptions::new().create(true).append(true).open(path()).ok();
    }
    if let Some(f) = guard.as_mut() {
        let t = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
        let _ = writeln!(f, "[{}.{:03}] [tid {}] {}", t.as_secs(), t.subsec_millis(), unsafe { super::win::GetCurrentThreadId() }, text);
        let _ = f.flush();
    }
}

/// Logs `what: key` only the first time a key is seen.
#[allow(dead_code)]
pub fn once(key: &[u8], what: &str) {
    let first = match SEEN.lock() {
        Ok(mut g) => g.get_or_insert_with(HashSet::new).insert(key.to_vec()),
        Err(_) => false,
    };
    if first {
        line(&format!("{what}: {}", String::from_utf8_lossy(key)));
    }
}
