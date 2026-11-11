using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using D3Casc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace D3ModTool.Designer;

/// <summary>
/// `D3ModTool ui-designer`: the UI designer, a local web app (http://127.0.0.1:PORT, opened in the default browser). The C#
/// side owns the files: it opens stock layouts from the installed game (read only) and custom ones from the project folder,
/// keeps each open layout as a <see cref="UiDocument"/> (byte-exact, so untouched data is never re-encoded), applies the edit
/// operations the page sends, keeps an undo history and saves. The page (Designer/wwwroot, embedded) draws and edits.
/// Only listens on 127.0.0.1.
/// </summary>
public static class DesignerServer
{
    const int UiGroup = 46;

    sealed class OpenDoc
    {
        public required string Id;
        public required string Title;
        /// <summary>Where Save writes (null for a stock layout until "save as").</summary>
        public string? Path;
        public required UiDocument Doc;
        public readonly Stack<byte[]> Undo = new();
        public readonly Stack<byte[]> Redo = new();
        public bool Dirty;
    }

    static readonly ConcurrentDictionary<string, OpenDoc> Docs = new();
    static D3Storage? _storage;
    static readonly object StorageLock = new();
    static TexFrameResolver? _resolver;
    static TexFrameResolver Resolver => _resolver ??= new TexFrameResolver(_storage!);
    static readonly ConcurrentDictionary<string, byte[]?> FramePngCache = new();

    static byte[]? LockedFramePng(string reference) { lock (StorageLock) return Resolver.FramePng(reference); }

    static string Clean(string name) => string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
    static string _projectDir = ".";
    /// <summary>The screen the layout is placed for (anchored controls move with the aspect ratio); the page can change it.</summary>
    static UiScreen _screen = new(1920, 1080);

    public static int Run(string projectDir, string? install, int port, bool openBrowser, Action<string> log)
    {
        _projectDir = Path.GetFullPath(projectDir);
        Directory.CreateDirectory(_projectDir);
        try
        {
            log($"opening the game's data ({D3Storage.ResolveInstall(install)}) for the stock layouts...");
            _storage = D3Storage.Open(D3Storage.ResolveInstall(install));
        }
        catch (Exception e) { log($"stock layouts unavailable: {e.Message} (custom layouts still work)"); }
        if (_storage != null) PrepareStockCache(log);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();

        app.MapGet("/", () => Asset("index.html", "text/html; charset=utf-8"));
        app.MapGet("/{file}", (string file) => file is "app.js" or "app.css"
            ? Asset(file, file.EndsWith(".js") ? "text/javascript; charset=utf-8" : "text/css; charset=utf-8") : Results.NotFound());

        app.MapGet("/api/catalog", () =>
        {
            var stock = _storage?.Toc.InGroup(UiGroup).Select(e => e.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? new();
            var custom = Directory.GetFiles(_projectDir, "*.ui", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(_projectDir, f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            return Results.Json(new { project = _projectDir, stock, custom, open = Docs.Values.Select(Summary) });
        });

        app.MapPost("/api/open", async (HttpRequest req) =>
        {
            var body = await Body(req);
            string source = body["source"]?.GetValue<string>() ?? "";
            string name = body["name"]?.GetValue<string>() ?? "";
            byte[] bytes;
            string title;
            string? path = null;
            if (source == "stock")
            {
                if (_storage == null) return Results.BadRequest("the game's data is not available");
                bytes = ReadStock(name) ?? throw new FileNotFoundException(name);
                title = name;
            }
            else
            {
                path = SafePath(name);
                bytes = await File.ReadAllBytesAsync(path);
                title = Path.GetRelativePath(_projectDir, path);
            }
            var doc = new OpenDoc { Id = Guid.NewGuid().ToString("n")[..8], Title = title, Path = path, Doc = UiDocument.Parse(bytes) };
            Docs[doc.Id] = doc;
            return Results.Json(Model(doc));
        });

        // every property/style id with its name, kind, meaning and how sure we are (UiProps, from the exe's table)
        app.MapGet("/api/props", () => Results.Json(UiProps.All().Select(p => new { p.Id, p.Name, kind = p.Kind.ToString(), p.Meaning, certainty = p.Certainty.ToString(), p.Default })));
        app.MapGet("/api/types", () => Results.Json(Enumerable.Range(0, 64).Select(t => new { type = t, cls = UiControlTypes.ClassOf(t), what = UiControlTypes.Describe(t) }).Where(x => !x.cls.StartsWith("Type"))));

        app.MapPost("/api/screen", async (HttpRequest req) =>
        {
            var body = await Body(req);
            _screen = new UiScreen(Math.Clamp(Int(body, "width"), 320, 8192), Math.Clamp(Int(body, "height"), 240, 8192));
            return Results.Ok();
        });

        // a new, empty layout ("<name>_main.LayoutRoot") to build a screen from scratch; saved like any other
        app.MapPost("/api/new", async (HttpRequest req) =>
        {
            var body = await Body(req);
            var name = body["name"]?.GetValue<string>()?.Trim() ?? "";
            if (name.Length == 0 || name.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_')) return Results.BadRequest("a layout name is letters, digits and _");
            int sno = body["sno"]?.GetValue<int>() ?? 0;
            var headerFrom = Directory.GetFiles(_stockCache, "*.ui").OrderBy(f => new FileInfo(f).Length).FirstOrDefault()
                ?? throw new InvalidOperationException("no stock layouts cached");
            var doc = new OpenDoc { Id = Guid.NewGuid().ToString("n")[..8], Title = name + ".ui", Path = null,
                Doc = UiDocument.CreateEmpty(File.ReadAllBytes(headerFrom), Prototype(2), name, sno) };
            Docs[doc.Id] = doc;
            return Results.Json(Model(doc));
        });

        app.MapGet("/api/doc/{id}", (string id) => Docs.TryGetValue(id, out var d) ? Results.Json(Model(d)) : Results.NotFound());

        app.MapPost("/api/doc/{id}/op", async (string id, HttpRequest req) =>
        {
            if (!Docs.TryGetValue(id, out var d)) return Results.NotFound();
            var op = await Body(req);
            var before = d.Doc.Write();
            try { Apply(d.Doc, op); }
            catch (Exception e) { d.Doc = UiDocument.Parse(before); return Results.BadRequest(e.Message); }
            d.Undo.Push(before);
            d.Redo.Clear();
            d.Dirty = true;
            return Results.Json(Model(d));
        });

        app.MapPost("/api/doc/{id}/undo", (string id) => History(id, undo: true));
        app.MapPost("/api/doc/{id}/redo", (string id) => History(id, undo: false));

        app.MapPost("/api/doc/{id}/save", async (string id, HttpRequest req) =>
        {
            if (!Docs.TryGetValue(id, out var d)) return Results.NotFound();
            var body = await Body(req);
            var target = body["path"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(target)) d.Path = SafePath(target);
            if (d.Path == null) return Results.BadRequest("choose where to save (a path inside the project)");
            Directory.CreateDirectory(Path.GetDirectoryName(d.Path)!);
            var bytes = d.Doc.Write();
            var errors = new UiFile(bytes).Validate(null);
            if (errors.Count > 0) return Results.BadRequest("not saved, the layout is invalid: " + string.Join("; ", errors.Take(5)));
            await File.WriteAllBytesAsync(d.Path, bytes);
            d.Title = Path.GetRelativePath(_projectDir, d.Path);
            d.Dirty = false;
            return Results.Json(Model(d));
        });

        app.MapPost("/api/doc/{id}/close", (string id) => { Docs.TryRemove(id, out _); return Results.Ok(); });

        // ---- art: the game's 2D textures and their named frames ("UI:Name", "Items:Name", "Minimap:Name"; TexFile.cs)
        app.MapGet("/api/frame", (string @ref) =>
        {
            var png = FramePngCache.GetOrAdd(@ref, r => ProjectFramePng(r) ?? (_storage == null ? null : LockedFramePng(r)));
            return png == null ? Results.NotFound() : Results.Bytes(png, "image/png");
        });

        app.MapGet("/api/textures", (string? ns) =>
        {
            var project = ProjectTextures().Select(kv => new { sno = kv.Key, name = kv.Value.Name, ns = kv.Value.Namespace, project = true });
            if (_storage == null) return Results.Json(project);
            var prefixes = TexFrameResolver.Namespaces.Where(n => ns == null || n.Namespace.Equals(ns, StringComparison.OrdinalIgnoreCase)).ToList();
            var list = _storage.Toc.InGroup(TexFrameResolver.TextureGroup)
                .Select(e => (e, n: prefixes.FirstOrDefault(p => e.Name.StartsWith(p.TexturePrefix, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.n.Namespace != null)
                .Select(x => new { sno = x.e.Id, name = x.e.Name, ns = x.n.Namespace, project = false })
                .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase);
            return Results.Json(project.Concat(list));
        });

        app.MapGet("/api/texture/{sno:int}.png", (int sno) =>
        {
            if (ProjectTextures().TryGetValue(sno, out var pt) && TexFile.CanDecode(pt.Tex.Format))
                return Results.Bytes(Png.Encode(pt.Tex.Width, pt.Tex.Height, pt.Tex.DecodeMip(0)), "image/png");
            if (_storage == null) return Results.NotFound();
            lock (StorageLock)
            {
                var tex = Resolver.Texture(sno);
                if (tex == null || !TexFile.CanDecode(tex.Format)) return Results.NotFound();
                return Results.Bytes(Png.Encode(tex.Width, tex.Height, Resolver.Mip0(sno)), "image/png");
            }
        });

        app.MapGet("/api/texture/{sno:int}/frames", (int sno) =>
        {
            if (_storage == null && !ProjectTextures().ContainsKey(sno)) return Results.NotFound();
            lock (StorageLock)
            {
                var tex = ProjectTextures().TryGetValue(sno, out var pt) ? pt.Tex : Resolver.Texture(sno);
                if (tex == null) return Results.NotFound();
                return Results.Json(new
                {
                    sno, width = tex.Width, height = tex.Height, format = tex.Format.ToString(), decodable = TexFile.CanDecode(tex.Format),
                    frames = tex.Frames.Select(f => { var r = f.PixelRect(tex.Width, tex.Height); return new { f.Index, f.Name, x = r.X, y = r.Y, w = r.W, h = r.H }; }),
                });
            }
        });

        app.MapPost("/api/import-art", async (HttpRequest req) =>
        {
            try { return Results.Json(ImportArt(await Body(req))); }
            catch (Exception e) when (e is ArgumentException or FormatException or IOException) { return Results.BadRequest(e.Message); }
        });

        // extract a texture (whole sheet + every frame) as PNGs into <project>\Art\<TextureName>\
        app.MapPost("/api/extract-art", async (HttpRequest req) =>
        {
            if (_storage == null) return Results.BadRequest("the game's data is not available");
            var body = await Body(req);
            int sno = Int(body, "sno");
            lock (StorageLock)
            {
                var tex = Resolver.Texture(sno);
                if (tex == null || !TexFile.CanDecode(tex.Format)) return Results.BadRequest($"texture {sno} cannot be decoded");
                _storage.Toc.TryGetName(TexFrameResolver.TextureGroup, sno, out var name);
                var dir = Path.Combine(_projectDir, "Art", Clean(name ?? sno.ToString()));
                Directory.CreateDirectory(dir);
                var px = Resolver.Mip0(sno);
                File.WriteAllBytes(Path.Combine(dir, "_sheet.png"), Png.Encode(tex.Width, tex.Height, px));
                int n = 0;
                foreach (var f in tex.Frames.Where(f => f.Name.Length > 0))
                {
                    var (rgba, w, h) = tex.DecodeFrame(f, px);
                    if (w > 0 && h > 0) { File.WriteAllBytes(Path.Combine(dir, Clean(f.Name) + ".png"), Png.Encode(w, h, rgba)); n++; }
                }
                return Results.Json(new { folder = Path.GetRelativePath(_projectDir, dir), frames = n });
            }
        });

        var url = $"http://127.0.0.1:{port}/";
        log($"UI designer: {url}  (project folder {_projectDir}; Ctrl+C to stop)");
        if (openBrowser)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        app.Run();
        return 0;
    }

    static IResult History(string id, bool undo)
    {
        if (!Docs.TryGetValue(id, out var d)) return Results.NotFound();
        var (from, to) = undo ? (d.Undo, d.Redo) : (d.Redo, d.Undo);
        if (from.Count == 0) return Results.Json(Model(d));
        to.Push(d.Doc.Write());
        d.Doc = UiDocument.Parse(from.Pop());
        d.Dirty = true;
        return Results.Json(Model(d));
    }

    /// <summary>
    /// Every stock layout as a file in %LOCALAPPDATA%\D3ModKit\ui-cache\&lt;build&gt; (written once): the layout engine resolves
    /// cross-layout template (0x9E) and style (0x9D) references by file name, the project folder first.
    /// </summary>
    static string _stockCache = "";
    static void PrepareStockCache(Action<string> log)
    {
        var entries = _storage!.Toc.InGroup(UiGroup);
        _stockCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3ModKit", "ui-cache", $"{entries.Count}");
        Directory.CreateDirectory(_stockCache);
        int written = 0;
        foreach (var e in entries)
        {
            var file = Path.Combine(_stockCache, Clean(e.Name) + ".ui");
            if (File.Exists(file)) continue;
            var bytes = ReadStock(e.Name);
            if (bytes != null) { File.WriteAllBytes(file, bytes); written++; }
        }
        if (written > 0) log($"cached {written} stock layouts in {_stockCache}");
    }

    // ------------------------------------------------------------------------------------------------- project art
    // Textures in the project (a mod's data\Base\Textures\*.tex): drawn and listed like the game's, and they win over a game
    // frame of the same name (as they will in the modded client, where they are added as new SNOs).

    sealed record ProjectTexture(string Path, string Name, TexFile Tex, string Namespace);
    static Dictionary<int, ProjectTexture>? _projectTextures;
    static readonly object ProjectLock = new();

    static Dictionary<int, ProjectTexture> ProjectTextures()
    {
        lock (ProjectLock)
        {
            if (_projectTextures != null) return _projectTextures;
            var map = new Dictionary<int, ProjectTexture>();
            foreach (var f in Directory.GetFiles(_projectDir, "*.tex", SearchOption.AllDirectories))
            {
                try
                {
                    var tex = new TexFile(File.ReadAllBytes(f));
                    var name = System.IO.Path.GetFileNameWithoutExtension(f);
                    var ns = TexFrameResolver.Namespaces.FirstOrDefault(n => name.StartsWith(n.TexturePrefix, StringComparison.OrdinalIgnoreCase)).Namespace;
                    if (ns != null) map[tex.SnoId] = new ProjectTexture(f, name, tex, ns);
                }
                catch { /* not a texture we can read */ }
            }
            return _projectTextures = map;
        }
    }

    /// <summary>A "UI:Frame" reference served from the project's textures, or null.</summary>
    static byte[]? ProjectFramePng(string reference)
    {
        if (!TexFrameResolver.TrySplit(reference, out var ns, out var frame)) return null;
        foreach (var t in ProjectTextures().Values.Where(t => t.Namespace == ns))
        {
            var f = t.Tex.FindFrame(frame);
            if (f == null || !TexFile.CanDecode(t.Tex.Format)) continue;
            var (rgba, w, h) = t.Tex.DecodeFrame(f.Value);
            return Png.Encode(w, h, rgba);
        }
        return null;
    }

    /// <summary>
    /// A PNG (sent as RGBA pixels by the page) as a new UI texture of a mod: &lt;project&gt;\&lt;mod&gt;\data\Base\Textures\2DUI_&lt;name&gt;.tex,
    /// uncompressed BGRA8 with one frame per rectangle (default: the whole image, named like the texture). Its SNO id is derived
    /// from the name (stable, outside the stock id range) and must not clash; frame names must not clash with any UI frame.
    /// </summary>
    static object ImportArt(JsonObject body)
    {
        string mod = body["mod"]?.GetValue<string>()?.Trim() ?? "";
        string name = body["name"]?.GetValue<string>()?.Trim() ?? "";
        int width = Int(body, "width"), height = Int(body, "height");
        if (mod.Length == 0 || mod.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("give the mod folder (inside the project)");
        if (name.Length == 0 || name.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_')) throw new ArgumentException("a texture name is letters, digits and _");
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096) throw new ArgumentException("images up to 4096×4096");
        var rgba = Convert.FromBase64String(body["rgba"]?.GetValue<string>() ?? "");
        if (rgba.Length != width * height * 4) throw new ArgumentException("pixel data does not match the size");
        string texName = name.StartsWith("2DUI", StringComparison.OrdinalIgnoreCase) ? name : "2DUI_" + name;
        string frameDefault = name.StartsWith("2DUI", StringComparison.OrdinalIgnoreCase) ? name[4..].TrimStart('_') : name;
        var frames = body["frames"]?.AsArray().Select(j => j!.AsObject()).Select(o =>
            (o["name"]!.GetValue<string>(), o["x"]!.GetValue<int>(), o["y"]!.GetValue<int>(), o["w"]!.GetValue<int>(), o["h"]!.GetValue<int>())).ToList()
            ?? new List<(string, int, int, int, int)> { (frameDefault, 0, 0, width, height) };
        var dir = System.IO.Path.Combine(_projectDir, mod, "data", "Base", "Textures");
        var file = System.IO.Path.Combine(dir, texName + ".tex");

        // the SNO id: FNV of the name folded into 900,000,000..999,999,999 (stock ids are far below), free in the game and project
        var others = ProjectTextures().Values.Where(t => !t.Path.Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
        uint h = 2166136261;
        foreach (var ch in texName.ToLowerInvariant()) { h ^= ch; h *= 16777619; }
        int sno = 900_000_000 + (int)(h % 100_000_000);
        while ((_storage != null && _storage.Toc.TryGetName(TexFrameResolver.TextureGroup, sno, out _)) || others.Any(t => t.Tex.SnoId == sno)) sno++;
        if (_storage != null && _storage.Toc.InGroup(TexFrameResolver.TextureGroup).Any(e => e.Name.Equals(texName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"the game already has a texture {texName}; pick another name");
        foreach (var (fname, _, _, _, _) in frames)
        {
            if (fname.Length == 0 || fname.Length > 63) throw new ArgumentException("frame names are 1..63 characters");
            if (_storage != null) lock (StorageLock) if (Resolver.TryResolve("UI:" + fname, out var hit)) throw new ArgumentException($"UI:{fname} exists in the game ({hit.TextureName}); pick another frame name");
            if (others.Any(t => t.Tex.FindFrame(fname) != null)) throw new ArgumentException($"UI:{fname} exists in another project texture");
        }
        TexFile? template = null;
        if (_storage != null) lock (StorageLock) template = Resolver.Texture(37047);   // 2DUILoadScreen: the unknown header fields of a stock UI texture
        var bytes = TexFile.CreateBgra8(sno, width, height, rgba, frames, template);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(file, bytes);
        // a mod.json for a new mod folder, so build_mods / mod-install pick it up
        var manifest = System.IO.Path.Combine(_projectDir, mod, "mod.json");
        if (!File.Exists(manifest))
            File.WriteAllText(manifest, JsonSerializer.Serialize(new { id = mod.ToLowerInvariant(), name = mod, version = "0.1.0", build = "2.8.0.99920", type = "client" },
                new JsonSerializerOptions { WriteIndented = true }));
        lock (ProjectLock) _projectTextures = null;
        foreach (var (fname, _, _, _, _) in frames) FramePngCache.TryRemove("UI:" + fname, out _);
        return new { texture = texName, sno, file = System.IO.Path.GetRelativePath(_projectDir, file), frames = frames.Select(f => "UI:" + f.Item1) };
    }

    // ------------------------------------------------------------------------------------------------- strings
    const int StringListGroup = 42;
    static readonly ConcurrentDictionary<string, Dictionary<string, string>?> StringLists = new(StringComparer.OrdinalIgnoreCase);
    static string _locale = "enUS";

    /// <summary>
    /// A text key "List:Label" (style 0x41) as the player sees it. StringList SNO (group 42, in the locale's part of the
    /// storage): header @40 entries offset (+16), @44 byte size; entry 40 B = {0, 0, label offset, label size, 0, 0, text
    /// offset, text size, hash, 0} (offsets +16, sizes include the NUL). Null when the key does not resolve.
    /// </summary>
    static string? ResolveText(string key)
    {
        int colon = key.IndexOf(':');
        if (_storage == null || colon <= 0) return null;
        var list = StringLists.GetOrAdd(key[..colon], name =>
        {
            lock (StorageLock)
            {
                var toc = _storage.Toc.InGroup(StringListGroup).FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (toc.Name == null) return null;
                var file = _storage.SnoEntries(StringListGroup, toc.Id).FirstOrDefault(e => e.Directory == _locale) ??
                           _storage.SnoEntries(StringListGroup, toc.Id).FirstOrDefault();
                if (file == null) return null;
                var b = _storage.Read(file);
                int I(int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
                string S(int off, int size) => size <= 1 || off + 16 + size > b.Length ? "" : System.Text.Encoding.UTF8.GetString(b, off + 16, size - 1);
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int at = I(40) + 16, n = I(44) / 40;
                for (int k = 0; k < n && at + (k + 1) * 40 <= b.Length; k++)
                {
                    int e = at + k * 40;
                    map[S(I(e + 8), I(e + 12))] = S(I(e + 24), I(e + 28));
                }
                return map;
            }
        });
        return list != null && list.TryGetValue(key[(colon + 1)..], out var text) ? text : null;
    }

    /// <summary>The layout's name for template references: the stock name, or a custom file's name without .ui.</summary>
    static string LayoutName(OpenDoc d) => System.IO.Path.GetFileNameWithoutExtension(d.Title);

    /// <summary>The laid-out view of a document (UiLayout.cs: the game's placement rules, templates, styles), for the canvas.</summary>
    static object? Layout(OpenDoc d, UiScreen screen)
    {
        try
        {
            var layout = new UiLayout(new UiFile(d.Doc.Write()), LayoutName(d));
            var set = new UiLayoutSet(_stockCache.Length > 0 ? new[] { _projectDir, _stockCache } : new[] { _projectDir });
            var engine = new UiLayoutEngine(set, screen);
            var roots = engine.Build(layout);
            var dto = UiLayoutDtos.ToDto(layout, roots, engine);
            // the text keys of every node, as the player reads them (the page draws these; unresolved keys stay as keys)
            var strings = dto.Nodes.Select(n => n.Text).Where(k => !string.IsNullOrEmpty(k)).Distinct()
                .Select(k => (k, v: ResolveText(k!))).Where(x => x.v != null).ToDictionary(x => x.k!, x => x.v!);
            return new { dto.Name, dto.Sno, dto.Version, dto.ScreenWidth, dto.ScreenHeight, dto.MarginX, dto.MarginY, dto.Nodes, dto.Warnings, strings };
        }
        catch (Exception e) { return new { error = e.Message }; }
    }

    static byte[]? ReadStock(string name)
    {
        lock (StorageLock)
        {
            var entry = _storage!.Toc.InGroup(UiGroup).FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry.Name == null) return null;
            var file = _storage.SnoEntries(UiGroup, entry.Id).FirstOrDefault(e => e.Type == 0);
            return file == null ? null : _storage.Read(file);
        }
    }

    /// <summary>A path inside the project folder (relative or absolute); anything outside is refused.</summary>
    static string SafePath(string path)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_projectDir, path));
        if (!full.StartsWith(_projectDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("only files inside the project folder");
        if (!full.EndsWith(".ui", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("layouts are .ui files");
        return full;
    }

    static async Task<JsonObject> Body(HttpRequest req) =>
        (await JsonNode.ParseAsync(req.Body))?.AsObject() ?? new JsonObject();

    static int Int(JsonObject o, string key) => o[key]?.GetValue<int>() ?? throw new ArgumentException($"missing {key}");

    /// <summary>One edit from the page.</summary>
    static void Apply(UiDocument doc, JsonObject op)
    {
        string kind = op["op"]?.GetValue<string>() ?? "";
        if (kind == "batch")
        {
            // several edits as one undo step (e.g. a drag that sets x and y)
            foreach (var sub in op["ops"]?.AsArray() ?? new JsonArray()) Apply(doc, sub!.AsObject());
            return;
        }
        if (kind == "add")
        {
            // a new control: {parent: full path or "", type, name?, x, y, w, h, texture?, text?}
            string parent = op["parent"]?.GetValue<string>() ?? "";
            int type = Int(op, "type");
            string name = doc.FreeName(parent, op["name"]?.GetValue<string>() is { Length: > 0 } n ? n : BaseName(type));
            int idx = doc.AddControl(Prototype(type), parent, name, type);
            foreach (var (key, id) in new[] { ("x", 0x07), ("y", 0x08), ("w", 0x09), ("h", 0x0A) })
                if (op[key] != null) doc.SetProperty(idx, UiDocument.IntsField, id, op[key]!.GetValue<double>());
            if (op["texture"]?.GetValue<string>() is { Length: > 0 } tex) doc.SetStyle(idx, 0x10, tex);
            if (op["text"]?.GetValue<string>() is { Length: > 0 } text) doc.SetStyle(idx, 0x41, text);
            return;
        }
        if (kind == "group")
        {
            // {children: [{path, x?, y?}], name?, x, y, w, h}: a new window under the children's common parent, the children
            // moved into it; x/y = a child's new position relative to the group (null: it is placed by absolute 0x03/0x04)
            var children = op["children"]!.AsArray().Select(j => j!.AsObject()).ToList();
            if (children.Count == 0) throw new ArgumentException("nothing to group");
            var first = doc.IndexOf(children[0]["path"]!.GetValue<string>());
            if (first < 0) throw new ArgumentException("unknown control");
            string parent = UiDocument.ParentOf(doc.Controls[first]);
            foreach (var ch in children)
            {
                int i = doc.IndexOf(ch["path"]!.GetValue<string>());
                if (i < 0 || UiDocument.ParentOf(doc.Controls[i]) != parent) throw new ArgumentException("grouped controls must have the same parent");
            }
            string name = doc.FreeName(parent, op["name"]?.GetValue<string>() is { Length: > 0 } n ? n : "Group");
            int firstChild = children.Min(ch => doc.IndexOf(ch["path"]!.GetValue<string>()));
            int g = doc.AddControl(Prototype(2), parent, name, 2, firstChild);   // before its children, like stock layouts
            foreach (var (key, id) in new[] { ("x", 0x07), ("y", 0x08), ("w", 0x09), ("h", 0x0A) })
                doc.SetProperty(g, UiDocument.IntsField, id, op[key]?.GetValue<double>() ?? 0);
            string groupFull = parent.Length == 0 ? name : parent + "." + name;
            foreach (var ch in children)
            {
                var path = ch["path"]!.GetValue<string>();
                doc.Reparent(doc.IndexOf(path), doc.IndexOf(groupFull));
                int moved = doc.IndexOf(groupFull + "." + path[(path.LastIndexOf('.') + 1)..]);
                if (ch["x"] != null) doc.SetProperty(moved, UiDocument.IntsField, 0x07, ch["x"]!.GetValue<double>());
                if (ch["y"] != null) doc.SetProperty(moved, UiDocument.IntsField, 0x08, ch["y"]!.GetValue<double>());
            }
            return;
        }
        int c = Int(op, "control");
        if (c < 0 || c >= doc.Controls.Count) throw new ArgumentException($"no control {c}");
        switch (kind)
        {
            case "setInt": doc.SetProperty(c, UiDocument.IntsField, Int(op, "id"), op["value"]!.GetValue<double>()); break;
            case "setFloat": doc.SetProperty(c, UiDocument.FloatsField, Int(op, "id"), op["value"]!.GetValue<double>()); break;
            case "setStyle": doc.SetStyle(c, Int(op, "id"), op["value"]?.GetValue<string>() ?? ""); break;
            case "removeInt": doc.Remove(c, UiDocument.IntsField, Int(op, "id")); break;
            case "removeFloat": doc.Remove(c, UiDocument.FloatsField, Int(op, "id")); break;
            case "removeStyle": doc.Remove(c, UiDocument.StylesField, Int(op, "id")); break;
            case "setType": doc.SetType(c, Int(op, "value")); break;
            case "rename": doc.Rename(c, op["value"]?.GetValue<string>() ?? ""); break;
            case "reparent": doc.Reparent(c, Int(op, "parent")); break;
            case "duplicate": doc.Duplicate(c, op["value"]?.GetValue<string>()); break;
            case "delete": doc.Delete(c); break;
            case "move":
            {
                // drag in the canvas: add a delta to x (0x07) and y (0x08)
                int dx = Int(op, "dx"), dy = Int(op, "dy");
                doc.SetProperty(c, UiDocument.IntsField, 0x07, IntProp(doc, c, 0x07) + dx);
                doc.SetProperty(c, UiDocument.IntsField, 0x08, IntProp(doc, c, 0x08) + dy);
                break;
            }
            case "resize":
            {
                int dw = Int(op, "dw"), dh = Int(op, "dh");
                doc.SetProperty(c, UiDocument.IntsField, 0x09, Math.Max(1, IntProp(doc, c, 0x09) + dw));
                doc.SetProperty(c, UiDocument.IntsField, 0x0A, Math.Max(1, IntProp(doc, c, 0x0A) + dh));
                break;
            }
            default: throw new ArgumentException($"unknown op '{kind}'");
        }
    }

    static string BaseName(int type) => type switch
    {
        3 => "Button", 4 => "Text", 13 => "List", 18 => "CheckBox", 20 => "Dropdown", 22 => "Radio", 38 => "Stack", _ => "Panel",
    };

    static readonly ConcurrentDictionary<int, byte[]> Prototypes = new();

    /// <summary>
    /// A clean record to start a new control of a type: the stock control of that type with the fewest own values (its arrays
    /// are emptied by AddControl). Falls back to a window (type 2).
    /// </summary>
    static byte[] Prototype(int type) => Prototypes.GetOrAdd(type, t =>
    {
        byte[]? best = null; int bestValues = int.MaxValue;
        foreach (var f in Directory.Exists(_stockCache) ? Directory.GetFiles(_stockCache, "*.ui") : Array.Empty<string>())
        {
            UiDocument d;
            try { d = UiDocument.Parse(File.ReadAllBytes(f)); } catch { continue; }
            foreach (var c in d.Controls)
            {
                if (UiDocument.TypeOf(c) != t) continue;
                int values = c.Arrays.Values.Sum(a => a?.Entries.Count ?? 0);
                if (values < bestValues) { bestValues = values; best = (byte[])c.Raw.Clone(); }
            }
            if (bestValues <= 1) break;
        }
        return best ?? (t != 2 ? Prototype(2) : throw new InvalidOperationException("no stock layouts cached to copy a control from"));
    });

    static int IntProp(UiDocument doc, int c, int id)
    {
        doc.Controls[c].Arrays.TryGetValue(UiDocument.IntsField, out var a);
        return a?.Entries.FirstOrDefault(e => e.I32(0) == id)?.I32(8) ?? 0;
    }

    static object Summary(OpenDoc d) => new { id = d.Id, title = d.Title, path = d.Path, dirty = d.Dirty };

    /// <summary>The layout as the page sees it: every control with its type, class, properties, styles and other arrays.</summary>
    static object Model(OpenDoc d)
    {
        var doc = d.Doc;
        var controls = doc.Controls.Select((c, i) =>
        {
            List<object[]> Entries(int field, Func<UiDocument.Block, object> value) =>
                c.Arrays.TryGetValue(field, out var a) && a != null ? a.Entries.Select(e => new object[] { e.I32(0), value(e) }).ToList() : new();
            return new
            {
                i,
                name = UiDocument.NameOf(c),
                parent = UiDocument.ParentOf(c),
                cls = UiDocument.ClassOf(c),
                type = UiDocument.TypeOf(c),
                ints = Entries(UiDocument.IntsField, e => e.I32(8)),
                floats = Entries(UiDocument.FloatsField, e => e.F32(8)),
                styles = Entries(UiDocument.StylesField, e => e.Str(UiDocument.StyleTextAt, UiDocument.StyleSize - UiDocument.StyleTextAt)),
                other = c.Arrays.Where(kv => kv.Key is not (UiDocument.IntsField or UiDocument.FloatsField or UiDocument.StylesField) && kv.Value is { Entries.Count: > 0 })
                    .ToDictionary(kv => $"+{kv.Key}", kv => kv.Value!.Entries.Count),
            };
        }).ToList();
        return new { doc = Summary(d), sno = doc.SnoId, version = doc.Version, canUndo = d.Undo.Count > 0, canRedo = d.Redo.Count > 0, controls,
            layout = Layout(d, _screen) };
    }

    static IResult Asset(string name, string type)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Designer/" + name);
        if (s == null) return Results.NotFound();
        using var r = new StreamReader(s);
        return Results.Content(r.ReadToEnd(), type);
    }
}
