//! The login token of the current player: the same WEB_TOKEN the launcher (tools\launch_d3.ps1 / D3Launcher) writes for the
//! client (HKCU\Software\Blizzard Entertainment\Battle.net\Launch Options\D3, DPAPI current user, fixed Battle.net entropy).
//! The server resolves it with AccountManager.GetAccountBySaltTicket. It contains the password: only ever sent in POST bodies.

use super::win::*;
use std::ffi::c_void;

const KEY: &str = r"Software\Blizzard Entertainment\Battle.net\Launch Options\D3";
const ENTROPY: [u8; 16] = [0xc8, 0x76, 0xf4, 0xae, 0x4c, 0x95, 0x2e, 0xfe, 0xf2, 0xfa, 0x0f, 0x54, 0x19, 0xc0, 0x9c, 0x43];

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

pub fn read() -> Option<String> {
    unsafe {
        let (key, value) = (wide(KEY), wide("WEB_TOKEN"));
        let mut len = 0u32;
        if RegGetValueW(HKEY_CURRENT_USER, key.as_ptr(), value.as_ptr(), RRF_RT_REG_BINARY, std::ptr::null_mut(), std::ptr::null_mut(), &mut len) != 0 || len == 0 {
            return None;
        }
        let mut buf = vec![0u8; len as usize];
        if RegGetValueW(HKEY_CURRENT_USER, key.as_ptr(), value.as_ptr(), RRF_RT_REG_BINARY, std::ptr::null_mut(), buf.as_mut_ptr() as *mut c_void, &mut len) != 0 {
            return None;
        }
        let input = DATA_BLOB { len, data: buf.as_mut_ptr() };
        let mut ent = ENTROPY;
        let entropy = DATA_BLOB { len: ent.len() as u32, data: ent.as_mut_ptr() };
        let mut out = DATA_BLOB { len: 0, data: std::ptr::null_mut() };
        if CryptUnprotectData(&input, std::ptr::null_mut(), &entropy, std::ptr::null_mut(), std::ptr::null_mut(), 0, &mut out) == 0 {
            return None;
        }
        let text = String::from_utf8_lossy(std::slice::from_raw_parts(out.data, out.len as usize)).trim_end_matches('\0').to_string();
        LocalFree(out.data as *mut c_void);
        Some(text)
    }
}
