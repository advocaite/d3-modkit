// ModKit example: a client plugin in C#.
//   1. d3mod_init is exported with [UnmanagedCallersOnly(EntryPoint = "d3mod_init")];
//   2. D3.Init checks the host's API version;
//   3. a wrapper around a UI handler the game has (UIBattlenet_Campaign_OnUpdate = the main menu's per-frame update) is a
//      static [UnmanagedCallersOnly] method, registered as a function pointer; it calls the game's original first;
//   4. D3.DumpControls logs every control whose path contains a text - use it to find the paths your mod needs.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using D3ModApi;

namespace HelloCSharp;

public static unsafe class HelloMod
{
    static int _frames;
    static bool _dumped;

    [UnmanagedCallersOnly(EntryPoint = "d3mod_init", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Init(D3HostApi* api, byte* modId, byte* modDir)
    {
        if (!D3.Init(api, modId)) return 1; // the host is older than this SDK
        D3.RegisterWrapper("UIBattlenet_Campaign_OnUpdate", &MenuUpdate);
        D3.Log($"hello from C# (mod folder {D3.Str(modDir)})");
        return 0;
    }

    [UnmanagedCallersOnly]
    static nuint MenuUpdate(delegate* unmanaged<nuint, nuint, nuint, nuint, nuint> original, nuint a, nuint b, nuint c, nuint d)
    {
        var result = original(a, b, c, d);
        // once, a while after the menu is up: every control of the menu, with its full path, into D3ModHost.log
        if (!_dumped && ++_frames == 120)
        {
            _dumped = true;
            D3.Log("the main menu's controls:");
            D3.DumpControls("BattleNetCampaign_main");
        }
        return result;
    }
}
