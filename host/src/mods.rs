//! Client mods: finds mods\<id>\mod.json files with "client_code": {"dll": "..."}, loads each DLL, calls its `d3mod_init`,
//! and keeps what they register: handlers (for UI handler names the exe does not have) and wrappers (around names it has).
//!
//! Mods folder: %D3MODS_ROOT%, else <game>\Mods (the installed layout, `D3ModTool mod-install --client`), else the first
//! "mods" folder found walking up from D3ModHost.dll (a development build next to the mod sources). A mod is skipped when its mod.json has "enabled": false or its
//! id is listed in %D3MODS_DISABLE% (comma-separated). Mods load in id order, a mod's "requires" first.

use super::{log, win, HOST_API};
use d3modapi::{Handler, InitFn, Wrapper};
use std::collections::HashMap;
use std::ffi::{c_void, CString};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Mutex;

struct Named {
    name: Vec<u8>,
    f: usize,
    owner: String,
}

static HANDLERS: Mutex<Vec<Named>> = Mutex::new(Vec::new());
/// (name, slot, owner) in registration order: the first wrapper registered is the innermost.
static WRAPPERS: Mutex<Vec<(Vec<u8>, usize, String)>> = Mutex::new(Vec::new());
static CURRENT: Mutex<String> = Mutex::new(String::new());
static REPORTED: Mutex<Vec<Vec<u8>>> = Mutex::new(Vec::new());

pub fn current() -> String {
    CURRENT.lock().map(|c| c.clone()).unwrap_or_default()
}

fn report_once(name: &[u8], text: impl FnOnce() -> String) {
    let mut r = REPORTED.lock().unwrap_or_else(|e| e.into_inner());
    if !r.iter().any(|n| n == name) {
        r.push(name.to_vec());
        drop(r);
        log::line(&text());
    }
}

pub fn register_handler(name: &str, f: Handler) -> i32 {
    let owner = current();
    let mut h = HANDLERS.lock().unwrap_or_else(|e| e.into_inner());
    if let Some(other) = h.iter().find(|n| n.name == name.as_bytes()) {
        log::line(&format!("[{owner}] handler {name} is already registered by {}; ignored", other.owner));
        return 1;
    }
    h.push(Named { name: name.as_bytes().to_vec(), f: f as usize, owner });
    0
}

// ------------------------------------------------------------------------------------------------- wrapper slots
// The game calls a plain function pointer, so each wrapper gets a fixed entry function (slot) that knows its own index.

const MAX_WRAPPERS: usize = 32;

struct Slot {
    original: AtomicUsize,
    wrapper: AtomicUsize,
}

#[allow(clippy::declare_interior_mutable_const)]
const EMPTY_SLOT: Slot = Slot { original: AtomicUsize::new(0), wrapper: AtomicUsize::new(0) };
static SLOTS: [Slot; MAX_WRAPPERS] = [EMPTY_SLOT; MAX_WRAPPERS];
static SLOTS_USED: AtomicUsize = AtomicUsize::new(0);

unsafe fn call_slot(i: usize, a: usize, b: usize, c: usize, d: usize) -> usize {
    let wrapper: Wrapper = std::mem::transmute(SLOTS[i].wrapper.load(Ordering::Relaxed));
    let original: Handler = std::mem::transmute(SLOTS[i].original.load(Ordering::Relaxed));
    wrapper(original, a, b, c, d)
}

macro_rules! slot_fns {
    ($($i:literal)*) => {
        const SLOT_FNS: [Handler; MAX_WRAPPERS] = [$({
            unsafe extern "system" fn entry(a: usize, b: usize, c: usize, d: usize) -> usize { call_slot($i, a, b, c, d) }
            entry
        }),*];
    };
}
slot_fns!(0 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29 30 31);

pub fn register_wrapper(name: &str, w: Wrapper) -> i32 {
    let owner = current();
    let slot = SLOTS_USED.fetch_add(1, Ordering::Relaxed);
    if slot >= MAX_WRAPPERS {
        log::line(&format!("[{owner}] wrapper {name}: all {MAX_WRAPPERS} wrapper slots are used; ignored"));
        return 2;
    }
    SLOTS[slot].wrapper.store(w as usize, Ordering::Relaxed);
    WRAPPERS.lock().unwrap_or_else(|e| e.into_inner()).push((name.as_bytes().to_vec(), slot, owner));
    0
}

/// What the detour returns for `name` instead of the exe's answer `found`, if a mod registered something for it.
pub unsafe fn resolve(name: &[u8], found: *const c_void) -> Option<*const c_void> {
    let shown = || String::from_utf8_lossy(name).into_owned();
    if found.is_null() {
        let h = HANDLERS.lock().unwrap_or_else(|e| e.into_inner());
        let n = h.iter().find(|n| n.name == name)?;
        report_once(name, || format!("bound {} -> {}", shown(), n.owner));
        return Some(n.f as *const c_void);
    }
    let w = WRAPPERS.lock().unwrap_or_else(|e| e.into_inner());
    let chain: Vec<&(Vec<u8>, usize, String)> = w.iter().filter(|(n, ..)| n == name).collect();
    if chain.is_empty() {
        if let Some(n) = HANDLERS.lock().unwrap_or_else(|e| e.into_inner()).iter().find(|n| n.name == name) {
            report_once(name, || format!("{} exists in the exe; {}'s handler is not used (register a wrapper to replace it)", shown(), n.owner));
        }
        return None;
    }
    let mut inner = found as usize;
    for (_, slot, _) in &chain {
        SLOTS[*slot].original.store(inner, Ordering::Relaxed);
        inner = SLOT_FNS[*slot] as usize;
    }
    report_once(name, || {
        let owners: Vec<&str> = chain.iter().map(|(_, _, o)| o.as_str()).collect();
        format!("wrapped {} ({})", shown(), owners.join(", "))
    });
    Some(inner as *const c_void)
}

// ------------------------------------------------------------------------------------------------- loading

struct ClientMod {
    id: String,
    dir: PathBuf,
    name: String,
    version: String,
    dll: Option<String>,
    enabled: bool,
    requires: Vec<String>,
}

/// %D3MODS_ROOT%, else the game folder's Mods\ (`D3ModTool mod-install --client`; ...\client\x64\Diablo III64.exe ->
/// ...\client\Mods), else the first "mods" folder above D3ModHost.dll (a development build next to the mod sources).
fn mods_root() -> Option<PathBuf> {
    if let Some(r) = std::env::var_os("D3MODS_ROOT") {
        return Some(PathBuf::from(r));
    }
    let mut buf = [0u16; 1024];
    let n = unsafe { win::GetModuleFileNameW(std::ptr::null_mut(), buf.as_mut_ptr(), buf.len() as u32) } as usize;
    let exe = PathBuf::from(String::from_utf16_lossy(&buf[..n]));
    if let Some(game) = exe.parent().and_then(|x64| x64.parent()) {
        let installed = game.join("Mods");
        if installed.is_dir() {
            return Some(installed);
        }
    }
    let me = super::self_path()?;
    me.ancestors().skip(1).map(|a| a.join("mods")).find(|m| m.is_dir())
}

fn read_mod(dir: &Path) -> Result<ClientMod, String> {
    let text = std::fs::read_to_string(dir.join("mod.json")).map_err(|e| format!("mod.json: {e}"))?;
    let json = json::parse(&text).map_err(|e| format!("mod.json: {e}"))?;
    let folder = dir.file_name().map(|f| f.to_string_lossy().into_owned()).unwrap_or_default();
    let s = |k: &str| json.get(k).and_then(|v| v.as_str()).map(str::to_string);
    Ok(ClientMod {
        id: s("id").unwrap_or(folder),
        dir: dir.to_path_buf(),
        name: s("name").unwrap_or_default(),
        version: s("version").unwrap_or_default(),
        dll: json.get("client_code").and_then(|c| c.get("dll")).and_then(|v| v.as_str()).map(str::to_string),
        enabled: !matches!(json.get("enabled"), Some(json::Value::Bool(false))),
        requires: json.get("requires").and_then(|r| r.as_array()).map(|a| a.iter().filter_map(|v| v.as_str().map(str::to_string)).collect()).unwrap_or_default(),
    })
}

pub fn load_all() {
    let Some(root) = mods_root() else {
        log::line("no mods folder found (set D3MODS_ROOT); no client mods loaded");
        return;
    };
    log::line(&format!("loading client mods from {}", root.display()));
    let disabled: Vec<String> = std::env::var("D3MODS_DISABLE").unwrap_or_default().split(',').map(|s| s.trim().to_string()).filter(|s| !s.is_empty()).collect();
    let mut found: Vec<ClientMod> = Vec::new();
    let mut dirs: Vec<PathBuf> = std::fs::read_dir(&root).map(|r| r.filter_map(|e| e.ok()).map(|e| e.path()).filter(|p| p.join("mod.json").is_file()).collect()).unwrap_or_default();
    dirs.sort();
    for dir in dirs {
        match read_mod(&dir) {
            Ok(m) if !m.enabled => log::line(&format!("mod {} is disabled (mod.json)", m.id)),
            Ok(m) if disabled.contains(&m.id) => log::line(&format!("mod {} is disabled (D3MODS_DISABLE)", m.id)),
            Ok(m) if m.dll.is_none() => log::line(&format!("mod {} has no client code (data/server only)", m.id)),
            Ok(m) => found.push(m),
            Err(e) => log::line(&format!("mod folder {}: {e}", dir.display())),
        }
    }
    // a mod's "requires" load before it (only among client-code mods; data-only requirements need no ordering here)
    let index: HashMap<String, usize> = found.iter().enumerate().map(|(i, m)| (m.id.clone(), i)).collect();
    let mut order = Vec::new();
    fn visit(i: usize, found: &[ClientMod], index: &HashMap<String, usize>, state: &mut Vec<u8>, order: &mut Vec<usize>) {
        if state[i] != 0 {
            return; // done, or a cycle (then the original order decides)
        }
        state[i] = 1;
        for r in &found[i].requires {
            if let Some(&j) = index.get(r) {
                visit(j, found, index, state, order);
            }
        }
        state[i] = 2;
        order.push(i);
    }
    let mut state = vec![0u8; found.len()];
    for i in 0..found.len() {
        visit(i, &found, &index, &mut state, &mut order);
    }
    let mut loaded = 0;
    for i in order {
        let m = &found[i];
        match unsafe { load_one(m) } {
            Ok(()) => {
                loaded += 1;
                let handlers = HANDLERS.lock().map(|h| h.iter().filter(|n| n.owner == m.id).count()).unwrap_or(0);
                let wrappers = WRAPPERS.lock().map(|w| w.iter().filter(|(_, _, o)| o == &m.id).count()).unwrap_or(0);
                log::line(&format!("client mod loaded: {} {} ({}) - {handlers} handler(s), {wrappers} wrapper(s)", m.name, m.version, m.id));
            }
            Err(e) => log::line(&format!("client mod {} NOT loaded: {e}", m.id)),
        }
    }
    log::line(&format!("{loaded} client mod(s) loaded"));
}

unsafe fn load_one(m: &ClientMod) -> Result<(), String> {
    let dll = m.dir.join(m.dll.as_deref().unwrap_or_default());
    if !dll.is_file() {
        return Err(format!("{} does not exist (build the mod's client code)", dll.display()));
    }
    let wide: Vec<u16> = dll.as_os_str().to_string_lossy().encode_utf16().chain(std::iter::once(0)).collect();
    let module = win::LoadLibraryW(wide.as_ptr());
    if module.is_null() {
        return Err(format!("LoadLibrary {} failed ({})", dll.display(), win::GetLastError()));
    }
    let init = win::GetProcAddress(module, d3modapi::INIT_SYMBOL.as_ptr());
    if init.is_null() {
        return Err(format!("{} has no d3mod_init export", dll.display()));
    }
    let init: InitFn = std::mem::transmute(init);
    *CURRENT.lock().unwrap_or_else(|e| e.into_inner()) = m.id.clone();
    let id = CString::new(m.id.clone()).unwrap_or_default();
    let dir = CString::new(m.dir.to_string_lossy().into_owned()).unwrap_or_default();
    let code = init(&HOST_API, id.as_ptr(), dir.as_ptr());
    CURRENT.lock().unwrap_or_else(|e| e.into_inner()).clear();
    if code != 0 {
        return Err(format!("d3mod_init returned {code}"));
    }
    Ok(())
}

// ------------------------------------------------------------------------------------------------- a small JSON reader

mod json {
    #[allow(dead_code)]
    pub enum Value {
        Null,
        Bool(bool),
        Num(f64),
        Str(String),
        Arr(Vec<Value>),
        Obj(Vec<(String, Value)>),
    }

    impl Value {
        pub fn get(&self, key: &str) -> Option<&Value> {
            match self {
                Value::Obj(o) => o.iter().find(|(k, _)| k == key).map(|(_, v)| v),
                _ => None,
            }
        }
        pub fn as_str(&self) -> Option<&str> {
            match self { Value::Str(s) => Some(s), _ => None }
        }
        pub fn as_array(&self) -> Option<&Vec<Value>> {
            match self { Value::Arr(a) => Some(a), _ => None }
        }
    }

    pub fn parse(text: &str) -> Result<Value, String> {
        let mut p = Parser { s: text.as_bytes(), i: 0 };
        let v = p.value()?;
        p.ws();
        if p.i != p.s.len() {
            return Err(format!("unexpected data at byte {}", p.i));
        }
        Ok(v)
    }

    struct Parser<'a> {
        s: &'a [u8],
        i: usize,
    }

    impl Parser<'_> {
        fn ws(&mut self) {
            while self.i < self.s.len() && self.s[self.i].is_ascii_whitespace() {
                self.i += 1;
            }
        }
        fn err<T>(&self, what: &str) -> Result<T, String> {
            Err(format!("{what} at byte {}", self.i))
        }
        fn value(&mut self) -> Result<Value, String> {
            self.ws();
            match self.s.get(self.i) {
                Some(b'{') => {
                    self.i += 1;
                    let mut o = Vec::new();
                    loop {
                        self.ws();
                        if self.s.get(self.i) == Some(&b'}') {
                            self.i += 1;
                            return Ok(Value::Obj(o));
                        }
                        if !o.is_empty() {
                            if self.s.get(self.i) != Some(&b',') {
                                return self.err("expected ','");
                            }
                            self.i += 1;
                            self.ws();
                        }
                        let Value::Str(k) = self.value()? else { return self.err("expected a key") };
                        self.ws();
                        if self.s.get(self.i) != Some(&b':') {
                            return self.err("expected ':'");
                        }
                        self.i += 1;
                        o.push((k, self.value()?));
                    }
                }
                Some(b'[') => {
                    self.i += 1;
                    let mut a = Vec::new();
                    loop {
                        self.ws();
                        if self.s.get(self.i) == Some(&b']') {
                            self.i += 1;
                            return Ok(Value::Arr(a));
                        }
                        if !a.is_empty() {
                            if self.s.get(self.i) != Some(&b',') {
                                return self.err("expected ','");
                            }
                            self.i += 1;
                        }
                        a.push(self.value()?);
                    }
                }
                Some(b'"') => {
                    self.i += 1;
                    let mut out = String::new();
                    loop {
                        match self.s.get(self.i) {
                            None => return self.err("unterminated string"),
                            Some(b'"') => {
                                self.i += 1;
                                return Ok(Value::Str(out));
                            }
                            Some(b'\\') => {
                                let c = *self.s.get(self.i + 1).ok_or("bad escape")?;
                                self.i += 2;
                                match c {
                                    b'n' => out.push('\n'),
                                    b't' => out.push('\t'),
                                    b'r' => out.push('\r'),
                                    b'b' => out.push('\u{8}'),
                                    b'f' => out.push('\u{c}'),
                                    b'u' => {
                                        let hex = std::str::from_utf8(self.s.get(self.i..self.i + 4).ok_or("bad \\u")?).map_err(|e| e.to_string())?;
                                        out.push(char::from_u32(u32::from_str_radix(hex, 16).map_err(|e| e.to_string())?).unwrap_or('?'));
                                        self.i += 4;
                                    }
                                    c => out.push(c as char),
                                }
                            }
                            Some(_) => {
                                let start = self.i;
                                while self.i < self.s.len() && self.s[self.i] != b'"' && self.s[self.i] != b'\\' {
                                    self.i += 1;
                                }
                                out.push_str(&String::from_utf8_lossy(&self.s[start..self.i]));
                            }
                        }
                    }
                }
                Some(b't') if self.s[self.i..].starts_with(b"true") => {
                    self.i += 4;
                    Ok(Value::Bool(true))
                }
                Some(b'f') if self.s[self.i..].starts_with(b"false") => {
                    self.i += 5;
                    Ok(Value::Bool(false))
                }
                Some(b'n') if self.s[self.i..].starts_with(b"null") => {
                    self.i += 4;
                    Ok(Value::Null)
                }
                Some(c) if *c == b'-' || c.is_ascii_digit() => {
                    let start = self.i;
                    while self.i < self.s.len() && matches!(self.s[self.i], b'-' | b'+' | b'.' | b'e' | b'E' | b'0'..=b'9') {
                        self.i += 1;
                    }
                    std::str::from_utf8(&self.s[start..self.i]).ok().and_then(|n| n.parse().ok()).map(Value::Num).ok_or_else(|| format!("bad number at byte {start}"))
                }
                _ => self.err("unexpected character"),
            }
        }
    }
}
