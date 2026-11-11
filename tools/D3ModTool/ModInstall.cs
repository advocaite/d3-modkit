using System.Text.Json;

namespace D3ModTool;

/// <summary>
/// Installs mods the way a player or server owner has them (MODDING.md "Installed layout"):
///   server: &lt;serverDir&gt;\Mods\&lt;id&gt;\ = mod.json + the server part (the folder of "server.assembly", default server\)
///   client: &lt;clientDir&gt;\Mods\&lt;id&gt;\ = mod.json + the client part (the folder of "client_code.dll") + data\, then every
///           installed mod's data is built into the client's CASC storage (ModBuild, in mod id order, "requires" first).
/// The source tree (sources, build scripts, notes) is never copied. A mod folder is replaced as a whole on reinstall.
/// </summary>
public static class ModInstall
{
    public const string ModsFolder = "Mods";
    public const string HostFolder = "ModHost";

    sealed record Manifest(string Id, string Type, string? ServerAssembly, string? ClientDll, bool HasData, List<string> Requires);

    static Manifest Read(string modDir)
    {
        var path = Path.Combine(modDir, "mod.json");
        if (!File.Exists(path)) throw new FileNotFoundException("no mod.json in " + modDir, path);
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var m = doc.RootElement;
        string? Str(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var id = Str(m, "id") ?? Path.GetFileName(Path.GetFullPath(modDir).TrimEnd('\\', '/'));
        var server = m.TryGetProperty("server", out var s) ? Str(s, "assembly") : null;
        if (server == null && Directory.Exists(Path.Combine(modDir, "server"))) server = "server/";
        var client = m.TryGetProperty("client_code", out var c) ? Str(c, "dll") : null;
        var requires = m.TryGetProperty("requires", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : new List<string>();
        return new Manifest(id, Str(m, "type") ?? "both", server, client, Directory.Exists(Path.Combine(modDir, "data")), requires);
    }

    /// <summary>The folder a part lives in ("server/X.dll" -> "server").</summary>
    static string PartFolder(string relative)
    {
        var rel = relative.Replace('\\', '/').TrimEnd('/');
        return relative.EndsWith('/') || relative.EndsWith('\\') ? rel : (Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "");
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, true);
        }
    }

    static void Replace(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
    }

    /// <summary>mod.json plus the mod's other top-level .json files (its settings, e.g. settings.json) into an installed folder.</summary>
    static void CopyManifestAndSettings(string modDir, string dst)
    {
        foreach (var f in Directory.GetFiles(modDir, "*.json"))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
    }

    public static int Install(List<string> modDirs, string? serverDir, string? clientDir, bool buildData, Action<string> log)
    {
        if (serverDir == null && clientDir == null) { log("give --server DIR and/or --client DIR"); return 1; }
        foreach (var modDir in modDirs)
        {
            var m = Read(modDir);
            if (serverDir != null && m.Type != "client")
            {
                var dst = Path.Combine(serverDir, ModsFolder, m.Id);
                Replace(dst);
                CopyManifestAndSettings(modDir, dst);
                if (m.ServerAssembly != null)
                {
                    var part = PartFolder(m.ServerAssembly);
                    var src = Path.Combine(modDir, part);
                    if (!Directory.Exists(src)) { log($"{m.Id}: server part {src} is missing (build the mod's server code first)"); return 2; }
                    CopyTree(src, Path.Combine(dst, part));
                }
                log($"{m.Id}: server part -> {dst}");
            }
            if (clientDir != null && m.Type != "server")
            {
                var dst = Path.Combine(clientDir, ModsFolder, m.Id);
                Replace(dst);
                CopyManifestAndSettings(modDir, dst);
                if (m.ClientDll != null)
                {
                    var part = PartFolder(m.ClientDll);
                    var src = Path.Combine(modDir, part);
                    if (!File.Exists(Path.Combine(modDir, m.ClientDll))) { log($"{m.Id}: client code {m.ClientDll} is missing (build the mod's client code first)"); return 2; }
                    CopyTree(src, Path.Combine(dst, part));
                }
                if (m.HasData) CopyTree(Path.Combine(modDir, "data"), Path.Combine(dst, "data"));
                log($"{m.Id}: client part -> {dst}{(m.HasData ? " (with data)" : "")}");
            }
        }
        if (clientDir != null && buildData) return BuildClientData(clientDir, log);
        return 0;
    }

    /// <summary>Builds the data of every mod installed in the client (mod id order, "requires" first) into its CASC storage.</summary>
    public static int BuildClientData(string clientDir, Action<string> log)
    {
        var root = Path.Combine(clientDir, ModsFolder);
        var mods = Directory.Exists(root)
            ? Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "mod.json"))).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => (Dir: d, M: Read(d))).Where(x => x.M.HasData && IsEnabled(x.Dir)).ToList()
            : new();
        var order = new List<string>();
        var state = new Dictionary<string, int>();
        void Visit((string Dir, Manifest M) x)
        {
            if (state.GetValueOrDefault(x.M.Id) != 0) return;
            state[x.M.Id] = 1;
            foreach (var r in x.M.Requires)
                foreach (var dep in mods.Where(d => d.M.Id == r)) Visit(dep);
            state[x.M.Id] = 2;
            order.Add(x.Dir);
        }
        foreach (var x in mods) Visit(x);
        if (order.Count == 0) { log("no installed mod has data; restoring the stock storage"); return Casc.ModBuild.Restore(clientDir, log); }
        log($"building data of {order.Count} mod(s): {string.Join(", ", order.Select(Path.GetFileName))}");
        return Casc.ModBuild.Build(clientDir, order, log);
    }

    static bool IsEnabled(string modDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modDir, "mod.json")), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return !(doc.RootElement.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False);
    }

    /// <summary>Removes installed mods (by id) from a server and/or client; the client's data is rebuilt without them.</summary>
    public static int Uninstall(List<string> ids, string? serverDir, string? clientDir, Action<string> log)
    {
        foreach (var id in ids)
            foreach (var dir in new[] { serverDir, clientDir }.Where(d => d != null).Select(d => Path.Combine(d!, ModsFolder, id)))
                if (Directory.Exists(dir)) { Directory.Delete(dir, true); log($"removed {dir}"); }
        return clientDir != null ? BuildClientData(clientDir, log) : 0;
    }

    /// <summary>Copies the client mod host (D3ModHost.dll + d3inject.exe) into &lt;clientDir&gt;\ModHost.</summary>
    public static int InstallHost(string hostBuildDir, string clientDir, Action<string> log)
    {
        var dst = Path.Combine(clientDir, HostFolder);
        Directory.CreateDirectory(dst);
        foreach (var f in new[] { "D3ModHost.dll", "d3inject.exe" })
        {
            var src = Path.Combine(hostBuildDir, f);
            if (!File.Exists(src)) { log($"{src} is missing (cargo build --release in D3ModHost)"); return 2; }
            File.Copy(src, Path.Combine(dst, f), true);
        }
        log($"mod host -> {dst}");
        return 0;
    }
}
