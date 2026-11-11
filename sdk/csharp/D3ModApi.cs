// D3ModApi.cs - the D3ModHost client mod API for C# plugins (NativeAOT DLLs). Mirrors ModKit/sdk/rust/d3modapi (canonical)
// and ModKit/sdk/c/d3modapi.h. Docs: ModKit/docs/index.html.
//
// Add this file to a NativeAOT class library (see ModKit/examples/hello-csharp) and export the entry point:
//     [UnmanagedCallersOnly(EntryPoint = "d3mod_init")]
//     public static unsafe int Init(D3HostApi* api, byte* modId, byte* modDir) { if (!D3.Init(api, modId)) return 1; ... return 0; }
// Handlers you register must be static methods marked [UnmanagedCallersOnly] (the game calls them directly), passed as
// function pointers (&MyHandler). They run on the game thread: no blocking, no exceptions out of them.
//
// Compatibility: fields are only ever APPENDED to D3HostApi (with Version + 1); nothing is removed or reordered.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace D3ModApi;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct D3HostApi
{
    public uint Version;
    // --- version 1: handlers and controls
    public delegate* unmanaged<byte*, void> Log;
    public delegate* unmanaged<byte*, delegate* unmanaged<nuint, nuint, nuint, nuint, nuint>, int> RegisterHandler;
    public delegate* unmanaged<byte*, delegate* unmanaged<delegate* unmanaged<nuint, nuint, nuint, nuint, nuint>, nuint, nuint, nuint, nuint, nuint>, int> RegisterWrapper;
    public delegate* unmanaged<byte*, nint> FindControl;
    public delegate* unmanaged<nint, byte*, nint> ChildOf;
    public delegate* unmanaged<nint, int, void> SetVisible;
    public delegate* unmanaged<nint, byte*, void> SetText;
    public delegate* unmanaged<nint, byte*, nuint, nint> GetText;
    public delegate* unmanaged<int, void> PinSno;
    public delegate* unmanaged<int, nint> CreateLayout;
    public delegate* unmanaged<nint, int> ListAddItem;
    public delegate* unmanaged<nint, int, nint> ListGetItem;
    public delegate* unmanaged<nuint, nint, int> IsControl;
    public delegate* unmanaged<byte*, nuint, nint> LoginToken;
    public delegate* unmanaged<byte*, void> DumpControls;
    // --- version 2: drawing
    public delegate* unmanaged<byte*, delegate* unmanaged<delegate* unmanaged<nuint, float*, nuint>, nuint, float*, nuint>, int> RegisterDraw;
    public delegate* unmanaged<nint, float*, int> ControlRect;
    public delegate* unmanaged<float*, void> MousePos;
    public delegate* unmanaged<int, float*, float*, void> DrawItemIcon;
    public delegate* unmanaged<byte*, float*, uint, float*, int> DrawTexture;
    // --- version 4: item tooltips
    public delegate* unmanaged<nuint, byte*, nuint, int, int, int, float*, int> ShowItemTooltip;
    // --- version 5
    public delegate* unmanaged<byte*, nuint, int, int, byte*, nuint, nint> ItemName;
    // --- version 6
    public delegate* unmanaged<byte*, nuint, int, int, int, byte*, nuint, nint> ItemLine;
    // --- version 7: dropdowns
    public delegate* unmanaged<nint, byte**, ulong*, nuint, int> ComboSetItems;
    public delegate* unmanaged<nint, int, int, int> ComboSelect;
    public delegate* unmanaged<nint, int*, ulong*, int> ComboSelected;
    public delegate* unmanaged<nint, byte*, int> ComboSetPlaceholder;
}

/// <summary>Convenience wrappers over the table (UTF-8 marshalling, null checks). Call <see cref="Init"/> first.</summary>
public static unsafe class D3
{
    public const uint ApiVersion = 7;
    static D3HostApi* _api;
    static string _modId = "?";

    /// <summary>From d3mod_init. False when the host is older than this SDK (then return non-zero).</summary>
    public static bool Init(D3HostApi* api, byte* modId)
    {
        if (api == null || api->Version < ApiVersion) return false;
        _api = api;
        _modId = Str(modId);
        return true;
    }

    public static string Str(byte* p) => p == null ? "" : Marshal.PtrToStringUTF8((nint)p) ?? "";

    static byte[] Z(string s) => Encoding.UTF8.GetBytes((s ?? "").Replace('\0', ' ') + "\0");

    /// <summary>One line to D3ModHost.log, prefixed with the mod id.</summary>
    public static void Log(string line) { fixed (byte* z = Z($"[{_modId}] {line}")) _api->Log(z); }

    public static bool RegisterHandler(string name, delegate* unmanaged<nuint, nuint, nuint, nuint, nuint> f)
    { fixed (byte* z = Z(name)) return _api->RegisterHandler(z, f) == 0; }

    public static bool RegisterWrapper(string name, delegate* unmanaged<delegate* unmanaged<nuint, nuint, nuint, nuint, nuint>, nuint, nuint, nuint, nuint, nuint> w)
    { fixed (byte* z = Z(name)) return _api->RegisterWrapper(z, w) == 0; }

    public static nint Find(string fullPath) { fixed (byte* z = Z(fullPath)) return _api->FindControl(z); }

    public static nint Child(nint control, string relativePath)
    { if (control == 0) return 0; fixed (byte* z = Z(relativePath)) return _api->ChildOf(control, z); }

    public static void Show(nint control, bool visible) { if (control != 0) _api->SetVisible(control, visible ? 1 : 0); }

    public static void SetText(nint control, string text) { if (control == 0) return; fixed (byte* z = Z(text)) _api->SetText(control, z); }

    public static string? GetText(nint control)
    {
        if (control == 0) return null;
        var buf = new byte[4096];
        fixed (byte* b = buf)
        {
            var n = _api->GetText(control, b, (nuint)buf.Length);
            return n < 0 ? null : Encoding.UTF8.GetString(buf, 0, (int)n);
        }
    }

    public static bool IsControl(nuint handlerArg, nint control) => control != 0 && _api->IsControl(handlerArg, control) != 0;

    public static void DumpControls(string needle) { fixed (byte* z = Z(needle)) _api->DumpControls(z); }

    public static void PinSno(int sno) => _api->PinSno(sno);
    public static nint CreateLayout(int sno) => _api->CreateLayout(sno);
    public static int ListAdd(nint list) => _api->ListAddItem(list);
    public static nint ListGet(nint list, int index) => _api->ListGetItem(list, index);

    public static bool ControlRect(nint control, out (float Left, float Top, float Right, float Bottom) rect)
    {
        var r = stackalloc float[4];
        var ok = control != 0 && _api->ControlRect(control, r) == 0;
        rect = (r[0], r[1], r[2], r[3]);
        return ok;
    }

    public static (float X, float Y) MousePos() { var p = stackalloc float[2]; _api->MousePos(p); return (p[0], p[1]); }
}
