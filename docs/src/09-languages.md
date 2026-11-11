# Languages

A client plugin is a native 64-bit Windows DLL that exports `d3mod_init`. Any language that can produce one works. A server part is a .NET 8 assembly, so C# (or F#, or VB) works there.

## Rust (reference)

```toml
[lib]
crate-type = ["cdylib"]

[dependencies]
d3modapi = { path = "<ModKit>/sdk/rust/d3modapi" }

[profile.release]
panic = "abort"
```

```rust
use d3modapi as api;

#[no_mangle]
pub unsafe extern "C" fn d3mod_init(host: *const api::HostApi, id: *const std::ffi::c_char, dir: *const std::ffi::c_char) -> i32 {
    if !api::set_api(host, id) { return 1; }
    api::log("hello");
    0
}
```

Handlers are `unsafe extern "system" fn(usize, usize, usize, usize) -> usize`. Use `panic = "abort"`: a panic must never unwind into the game.

## C / C++

Include `sdk/c/d3modapi.h` and export the entry point:

```c
#include "d3modapi.h"

static const D3HostApi *api;

static uintptr_t on_click(uintptr_t rcx, uintptr_t rdx, uintptr_t r8, uintptr_t r9) {
    api->log("[my-mod] clicked");
    return 0;
}

__declspec(dllexport) int32_t d3mod_init(const D3HostApi *a, const char *mod_id, const char *mod_dir) {
    if (!a || a->version < D3MODAPI_VERSION) return 1;
    api = a;
    api->register_handler("MyMod_OnClick", on_click);
    return 0;
}
```

Build a 64-bit DLL, for example `cl /LD /O2 mymod.c /Fe:mymod.dll`.

## C# (NativeAOT)

NativeAOT compiles C# to a native DLL with real exports. The game needs no .NET runtime. Add `sdk/csharp/D3ModApi.cs` to the project:

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
</PropertyGroup>
<ItemGroup>
  <Compile Include="<ModKit>\sdk\csharp\D3ModApi.cs" />
</ItemGroup>
```

```csharp
[UnmanagedCallersOnly(EntryPoint = "d3mod_init", CallConvs = new[] { typeof(CallConvCdecl) })]
public static unsafe int Init(D3HostApi* api, byte* modId, byte* modDir)
{
    if (!D3.Init(api, modId)) return 1;
    D3.RegisterWrapper("UIBattlenet_Campaign_OnUpdate", &MenuUpdate);
    return 0;
}

[UnmanagedCallersOnly]
static nuint MenuUpdate(delegate* unmanaged<nuint, nuint, nuint, nuint, nuint> original, nuint a, nuint b, nuint c, nuint d)
    => original(a, b, c, d);
```

Build with `dotnet publish -c Release -r win-x64`; the DLL is in the publish folder. The NativeAOT linker needs the MSVC build tools, and `vswhere.exe` (in `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`) on PATH.

Rules for C# handlers:

- They must be `static` and `[UnmanagedCallersOnly]`.
- They must not throw: catch everything.
- They must not block.

The DLL is about 1 MB, because it carries its own small runtime.

## Other languages

Zig, Go (`-buildmode=c-shared`), Nim, Odin and Delphi all work if they can:

1. export a C function named `d3mod_init`, and
2. mirror the `D3HostApi` struct from `d3modapi.h` (field order matters; every field is a pointer-sized function pointer, except the leading `uint32_t version` followed by padding to 8 bytes).

Callbacks use the Windows x64 calling convention.
