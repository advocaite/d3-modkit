using D3ModTool;

// D3ModTool: research and build tools for D3 2.8.0 modding (see ..\MODDING.md). Reads the 2.0.1 client and the retail
// CASC storage; never writes to either install.
//   D3ModTool mpq-find <filter>                     every 2.0.1 archive entry whose path contains <filter> (load order, PTCH flagged)
//   D3ModTool mpq-extract <filter> <outDir>         the winning (last-loaded) version of each matching path -> outDir\<path>
// Options: --mpq DIR (default %D3_201_MPQ_DIR%), --client DIR (default %D3_CLIENT_DIR%); the official install: %D3_INSTALL_DIR% or the registered one

var positional = new List<string>();
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--") && i + 1 < args.Length) options[args[i]] = args[++i];
    else positional.Add(args[i]);
}
string Opt(string name, string def) => options.TryGetValue(name, out var v) ? v : def;
string cmd = positional.Count > 0 ? positional[0].ToLowerInvariant() : "help";
string mpqDir = Opt("--mpq", Environment.GetEnvironmentVariable("D3_201_MPQ_DIR") ?? "");
// the game copy mods are built into: --client, else %D3_CLIENT_DIR%
string ClientDir() => options.TryGetValue("--client", out var c) ? c
    : Environment.GetEnvironmentVariable("D3_CLIENT_DIR") is { Length: > 0 } e ? e
    : throw new ArgumentException("give --client <game copy folder> or set D3_CLIENT_DIR");

// An unhandled exception leaves a dotnet.exe waiting on Windows Error Reporting that holds the dll; report and exit instead.
try { return Run(); }
catch (Exception e) { Console.Error.WriteLine(e); return 99; }

int Run()
{
switch (cmd)
{
    case "mpq-find":
    case "mpq-extract":
    {
        if (positional.Count < (cmd == "mpq-find" ? 2 : 3)) { Console.WriteLine($"usage: {cmd} <filter>{(cmd == "mpq-extract" ? " <outDir>" : "")}"); return 1; }
        using var mpq = new Mpq201(mpqDir);
        var hits = mpq.Find(positional[1]);
        if (cmd == "mpq-find")
        {
            foreach (var h in hits) Console.WriteLine($"{h.Archive,-36} {h.Size,10} {(h.IsPtch ? "PTCH " : "     ")}{h.StoredPath}");
            Console.WriteLine($"{hits.Count} entries, {hits.Select(h => h.LogicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Count()} paths");
            return 0;
        }
        var outDir = positional[2];
        int written = 0, failed = 0;
        foreach (var g in hits.GroupBy(h => h.LogicalPath, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = mpq.ReadPatched(g.ToList(), out var how);
                var dst = Path.Combine(outDir, g.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.WriteAllBytes(dst, bytes);
                written++;
                Console.WriteLine($"{g.Key} ({bytes.Length} B) <- {how}");
            }
            catch (Exception e) { failed++; Console.WriteLine($"FAILED {g.Key}: {e.Message}"); }
        }
        Console.WriteLine($"{written} files written to {outDir}, {failed} failed");
        return failed > 0 ? 2 : 0;
    }
    case "casc-extract":
    {
        // casc-extract <group> <nameRegex> <outDir>: retail SNO files of one group whose CoreTOC name matches -> outDir\<Group>\<Name><ext>
        if (positional.Count < 4) { Console.WriteLine("usage: casc-extract <group> <nameRegex> <outDir>"); return 1; }
        using var d3 = D3Casc.D3Storage.Open(D3Casc.D3Storage.ResolveInstall(options.GetValueOrDefault("--install")));
        if (!D3Casc.SnoGroups.TryParse(positional[1], out int group)) { Console.WriteLine($"unknown group {positional[1]}"); return 1; }
        var rx = new System.Text.RegularExpressions.Regex(positional[2], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int n = 0, missing = 0;
        foreach (var e in d3.Toc.InGroup(group).Where(e => rx.IsMatch(e.Name)))
        {
            var path = $"Base\\{D3Casc.SnoGroups.Name(group)}\\{e.Name}{D3Casc.SnoGroups.Ext(group)}";
            if (!d3.TryGet(path, out var entry)) { missing++; continue; }
            var dst = Path.Combine(positional[3], D3Casc.SnoGroups.Name(group), e.Name + D3Casc.SnoGroups.Ext(group));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllBytes(dst, d3.Read(entry));
            n++;
        }
        Console.WriteLine($"{n} files written, {missing} in CoreTOC but not readable from Base");
        return 0;
    }
    case "ui-check":
    {
        // ui-check <file|dir> [version]: structural validation of .ui files (default: expect 2.8.0's 471; "any" skips the version check)
        if (positional.Count < 2) { Console.WriteLine("usage: ui-check <file|dir> [version|any]"); return 1; }
        int? want = positional.Count > 2 ? (positional[2] == "any" ? null : int.Parse(positional[2])) : UiFile.Version280;
        var files = Directory.Exists(positional[1]) ? Directory.GetFiles(positional[1], "*.ui", SearchOption.AllDirectories) : new[] { positional[1] };
        int bad = 0;
        foreach (var f in files)
        {
            var errors = new UiFile(File.ReadAllBytes(f)).Validate(want);
            if (errors.Count == 0) continue;
            bad++;
            Console.WriteLine($"{Path.GetFileName(f)}: {string.Join("; ", errors.Take(5))}");
        }
        Console.WriteLine($"{files.Length - bad}/{files.Length} valid");
        return bad > 0 ? 2 : 0;
    }
    case "ui-info":
    {
        // ui-info <file> [--full]: header and control list; --full: full path, type, every style (id "text") and int property
        var ui = new UiFile(File.ReadAllBytes(positional[1]));
        var full = args.Contains("--full");
        Console.WriteLine($"version {ui.Version}, sno {ui.SnoId}, hash 0x{ui.LayoutHash:X8}, {ui.ControlCount} controls");
        foreach (var c in ui.Controls().OrderBy(c => full ? (c.Path.Length == 0 ? c.Name : c.Path + "." + c.Name) : "").ThenBy(c => c.Index))
        {
            if (!full) { Console.WriteLine($"  {c.Index,4} {c.Path}  styles={c.StyleCount} props={c.PropCount}"); continue; }
            var b = ui.Bytes;
            int r = UiFile.ControlsAt + c.Index * UiFile.ControlSize;
            var type = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(r + 1048));
            Console.WriteLine($"  {c.Index,4} {(c.Path.Length == 0 ? c.Name : c.Path + "." + c.Name)}  type={type}");
            for (int k = 0; k < c.StyleCount; k++)
            {
                int e = 16 + c.StyleOffset + k * UiFile.StyleSize;
                int id = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(e));
                int n = b.AsSpan(e + 8, UiFile.StyleSize - 8).IndexOf((byte)0);
                Console.WriteLine($"         style {id} \"{System.Text.Encoding.ASCII.GetString(b, e + 8, n < 0 ? UiFile.StyleSize - 8 : n)}\"");
            }
            var props = new List<string>();
            for (int k = 0; k < c.PropCount; k++)
            {
                int e = 16 + c.PropOffset + k * UiFile.PropertySize;
                props.Add($"0x{System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(e)):X2}={System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(e + 8))}");
            }
            if (props.Count > 0) Console.WriteLine($"         props {string.Join(" ", props)}");
        }
        return 0;
    }
    case "ui-convert":
    {
        // ui-convert <in201Dir> <outDir>: 2.0.1 (463) layouts -> 2.8.0 (471)
        if (positional.Count < 3) { Console.WriteLine("usage: ui-convert <in201Dir|file> <outDir>"); return 1; }
        var files = Directory.Exists(positional[1]) ? Directory.GetFiles(positional[1], "*.ui") : new[] { positional[1] };
        Directory.CreateDirectory(positional[2]);
        foreach (var f in files)
        {
            var converted = UiFile.Convert201To280(File.ReadAllBytes(f));
            var check = new UiFile(converted).Validate();
            if (check.Count > 0) throw new InvalidDataException($"{f}: {string.Join("; ", check)}");
            File.WriteAllBytes(Path.Combine(positional[2], Path.GetFileName(f)), converted);
            Console.WriteLine($"{Path.GetFileName(f)}: sno {new UiFile(converted).SnoId}, {new UiFile(converted).ControlCount} controls");
        }
        Console.WriteLine($"{files.Length} layouts converted to {positional[2]}");
        return 0;
    }
    case "ui-clone":
    {
        // ui-clone <file.ui> <control full path> <copies> [out.ui]: N sibling copies of a control subtree (list rows), in place by default
        if (positional.Count < 4) { Console.WriteLine("usage: ui-clone <file.ui> <control full path> <copies> [out.ui]"); return 1; }
        var bytes = UiFile.CloneSubtree(File.ReadAllBytes(positional[1]), positional[2], int.Parse(positional[3]));
        var outPath = positional.Count > 4 ? positional[4] : positional[1];
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"{positional[2]}: {positional[3]} copies -> {outPath} ({new UiFile(bytes).ControlCount} controls, {bytes.Length} B)");
        return 0;
    }
    case "ui-inline":
    {
        // ui-inline <main.ui> <referencing control> <template.ui> <template root control> [out.ui]
        if (positional.Count < 5) { Console.WriteLine("usage: ui-inline <main.ui> <referencing control> <template.ui> <template root control> [out.ui]"); return 1; }
        var bytes = UiFile.InlineTemplate(File.ReadAllBytes(positional[1]), positional[2], File.ReadAllBytes(positional[3]), positional[4]);
        var outPath = positional.Count > 5 ? positional[5] : positional[1];
        File.WriteAllBytes(outPath, bytes);
        Console.WriteLine($"{positional[2]} <- {positional[4]}: {new UiFile(bytes).ControlCount} controls, {bytes.Length} B -> {outPath}");
        return 0;
    }
    case "ui-make-listbox":
    {
        // ui-make-listbox <file.ui> <container full path> <row full path> <templateName>: static row -> dynamic list box + row template
        if (positional.Count < 5) { Console.WriteLine("usage: ui-make-listbox <file.ui> <container> <row> <templateName>"); return 1; }
        var bytes = UiFile.MakeListBox(File.ReadAllBytes(positional[1]), positional[2], positional[3], positional[4]);
        File.WriteAllBytes(positional[1], bytes);
        Console.WriteLine($"{positional[2]} is now a list box with template {positional[4]} ({bytes.Length} B)");
        return 0;
    }
    case "ui-copy-subtree":
    {
        // ui-copy-subtree <dst.ui> <src.ui> <src root full path> <new root full path>
        if (positional.Count < 5) { Console.WriteLine("usage: ui-copy-subtree <dst.ui> <src.ui> <src root> <new root>"); return 1; }
        var bytes = UiFile.CopySubtree(File.ReadAllBytes(positional[1]), File.ReadAllBytes(positional[2]), positional[3], positional[4]);
        File.WriteAllBytes(positional[1], bytes);
        Console.WriteLine($"{positional[3]} -> {positional[4]} ({new UiFile(bytes).ControlCount} controls)");
        return 0;
    }
    case "ui-style":
    {
        // ui-style <file.ui> <control full path> <styleId> <string>: set (or add) a style entry, e.g. 41 = click handler
        if (positional.Count < 5) { Console.WriteLine("usage: ui-style <file.ui> <control> <styleId> <string>"); return 1; }
        var bytes = UiFile.SetStyle(File.ReadAllBytes(positional[1]), positional[2], int.Parse(positional[3]), positional[4]);
        File.WriteAllBytes(positional[1], bytes);
        Console.WriteLine($"{positional[2]}: style {positional[3]} = {positional[4]}");
        return 0;
    }
    case "ui-move":
    {
        // ui-move <file.ui> <from full path> <to full path>: re-parent/rename a subtree in place
        if (positional.Count < 4) { Console.WriteLine("usage: ui-move <file.ui> <from> <to>"); return 1; }
        var bytes = File.ReadAllBytes(positional[1]);
        UiFile.MoveSubtree(bytes, positional[2], positional[3]);
        File.WriteAllBytes(positional[1], bytes);
        Console.WriteLine($"{positional[2]} -> {positional[3]}");
        return 0;
    }
    case "ui-style-remove":
    {
        // ui-style-remove <file.ui> <control full path> <styleId>: drop a style entry (e.g. 1 = "this is a template")
        if (positional.Count < 4) { Console.WriteLine("usage: ui-style-remove <file.ui> <control full path> <styleId>"); return 1; }
        var bytes = File.ReadAllBytes(positional[1]);
        int removed = UiFile.RemoveStyle(bytes, positional[2], int.Parse(positional[3]));
        File.WriteAllBytes(positional[1], bytes);
        Console.WriteLine($"{positional[2]}: removed {removed} style {positional[3]} entr{(removed == 1 ? "y" : "ies")}");
        return 0;
    }
    case "ui-type":
    {
        // ui-type <file.ui> <control full path> <type>: change a control's type (e.g. 38 stack -> 2 panel)
        if (positional.Count < 4) { Console.WriteLine("usage: ui-type <file.ui> <control full path> <type>"); return 1; }
        var bytes = File.ReadAllBytes(positional[1]);
        UiFile.SetType(bytes, positional[2], int.Parse(positional[3]));
        File.WriteAllBytes(positional[1], bytes);
        Console.WriteLine($"{positional[2]}: type {positional[3]}");
        return 0;
    }
    case "ui-prop":
    {
        // ui-prop <file.ui> <control full path> <propertyId hex> <value>: set (or add) an int property
        if (positional.Count < 5) { Console.WriteLine("usage: ui-prop <file.ui> <control full path> <propertyId, e.g. 0x9b> <value>"); return 1; }
        var bytes = UiFile.SetProperty(File.ReadAllBytes(positional[1]), positional[2], Convert.ToInt32(positional[3], 16), int.Parse(positional[4]));
        File.WriteAllBytes(positional[1], bytes);
        Console.WriteLine($"{positional[2]}: property {positional[3]} = {positional[4]}");
        return 0;
    }
    case "import-gltf":
    {
        // import-gltf <model.glb|.gltf> <modDir> --name <Name> [--like <actor SNO|name>] [--sno <first id>] [--scale <s>] [--actor-scale <s>]
        //             [--forward +z|-z|+x|-x] [--static yes] [--fps <n>] [--install DIR]
        //             [--monster new|new:<sno|name>|<sno|name>] [--display-name <text>] [--monster-type <type>] [--root-motion auto|off] [--speed walk=<ft/s>,run=<ft/s>]
        // a skinned glTF becomes an animated monster (skeleton, Anims, AnimSet, own Monster SNO + name, Monster actor), anything else a static prop
        const string usage = "usage: import-gltf <model.glb|.gltf> <modDir> --name <Name> [--like <actor SNO|name>] [--sno <first id>] [--scale <s>] [--actor-scale <s>] [--forward +z|-z|+x|-x] [--static yes] [--fps <n>] [--install DIR]\n" +
                             "       skinned: [--monster new|new:<sno|name>|<sno|name>] [--display-name <text>] [--monster-type Undead|Demon|Beast|Human|...] [--root-motion auto|off] [--speed walk=<ft/s>,run=<ft/s>]";
        if (positional.Count < 3 || !options.ContainsKey("--name")) { Console.WriteLine(usage); return 1; }
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return D3ModTool.Import.ModelImport.Run(new D3ModTool.Import.ModelImport.Options
        {
            Input = positional[1], ModDir = Path.GetFullPath(positional[2]), Name = options["--name"],
            Like = options.GetValueOrDefault("--like"), Install = options.GetValueOrDefault("--install"),
            FirstSno = options.TryGetValue("--sno", out var sn) ? int.Parse(sn) : null,
            Scale = options.TryGetValue("--scale", out var sc) ? float.Parse(sc, ci) : 3.28084f,
            ActorScale = options.TryGetValue("--actor-scale", out var asc) ? float.Parse(asc, ci) : 1f,
            Forward = options.GetValueOrDefault("--forward"),
            Static = Opt("--static", "no").Equals("yes", StringComparison.OrdinalIgnoreCase),
            Monster = options.GetValueOrDefault("--monster"),
            DisplayName = options.GetValueOrDefault("--display-name"),
            MonsterType = options.GetValueOrDefault("--monster-type"),
            Fps = options.TryGetValue("--fps", out var fp) ? float.Parse(fp, ci) : 30f,
            RootMotion = Opt("--root-motion", "auto"),
            Speeds = options.TryGetValue("--speed", out var spd)
                ? spd.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Split('='))
                    .ToDictionary(x => x[0], x => x.Length == 2 ? float.Parse(x[1], ci) : throw new ArgumentException("--speed: <clip>=<ft/s>[,...]"), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase),
        }, Console.WriteLine);
    }
    case "casc-selftest":
        // casc-selftest [client]: every CASC writer must rebuild the stock files byte for byte
        return D3ModTool.Casc.ModBuild.SelfTest(positional.Count > 1 ? positional[1] : D3ModTool.Casc.ModBuild.OfficialInstall, Console.WriteLine);
    case "mod-build":
    {
        // mod-build <modDir> [modDir ...] [--client DIR]: write the mods into the client COPY (load order = argument order)
        if (positional.Count < 2) { Console.WriteLine("usage: mod-build <modDir> [modDir ...] [--client DIR]"); return 1; }
        return D3ModTool.Casc.ModBuild.Build(ClientDir(), positional.Skip(1).Select(Path.GetFullPath).ToList(), Console.WriteLine);
    }
    case "mod-install":
    {
        // mod-install <modDir>... [--server DIR] [--client DIR] [--data no]: installed layout <dir>\Mods\<id> (+ client data build)
        if (positional.Count < 2) { Console.WriteLine("usage: mod-install <modDir> [modDir ...] [--server DIR] [--client DIR] [--data no]"); return 1; }
        return ModInstall.Install(positional.Skip(1).Select(Path.GetFullPath).ToList(), options.GetValueOrDefault("--server"), options.GetValueOrDefault("--client"),
            !Opt("--data", "yes").Equals("no", StringComparison.OrdinalIgnoreCase), Console.WriteLine);
    }
    case "mod-uninstall":
    {
        if (positional.Count < 2) { Console.WriteLine("usage: mod-uninstall <id> [id ...] [--server DIR] [--client DIR]"); return 1; }
        return ModInstall.Uninstall(positional.Skip(1).ToList(), options.GetValueOrDefault("--server"), options.GetValueOrDefault("--client"), Console.WriteLine);
    }
    case "mod-data":
        // mod-data [--client DIR]: rebuild the client's storage from every mod installed in <client>\Mods
        return ModInstall.BuildClientData(ClientDir(), Console.WriteLine);
    case "modhost-install":
    {
        if (positional.Count < 2) { Console.WriteLine("usage: modhost-install <D3ModHost build dir> [--client DIR]"); return 1; }
        return ModInstall.InstallHost(positional[1], ClientDir(), Console.WriteLine);
    }
    case "ui-designer":
        // ui-designer [projectDir] [--port 5190] [--install DIR] [--browser no]: the visual UI designer (local web app)
        return D3ModTool.Designer.DesignerServer.Run(positional.Count > 1 ? positional[1] : ".", options.GetValueOrDefault("--install"),
            int.Parse(Opt("--port", "5190")), !Opt("--browser", "yes").Equals("no", StringComparison.OrdinalIgnoreCase), Console.WriteLine);
    case "string-set":
    {
        // string-set <modDir> <List> <Key> <Text> [--locale enUS] [--install DIR]: add or change one game text in a mod
        // (data\<locale>\StringList\<List>.stl, started from the mod's copy or the stock list; every stock entry kept)
        if (positional.Count < 5) { Console.WriteLine("usage: string-set <modDir> <List> <Key> <Text> [--locale enUS] [--install DIR]"); return 1; }
        using var d3 = D3Casc.D3Storage.Open(D3Casc.D3Storage.ResolveInstall(options.GetValueOrDefault("--install")));
        StringListFile.SetInMod(d3, Path.GetFullPath(positional[1]), positional[2], positional[3], positional[4], options.GetValueOrDefault("--locale"), Console.WriteLine);
        return 0;
    }
    case "string-get":
    {
        // string-get <List> <Key|regex:...> [--locale enUS] [--install DIR]: a game text (from the install, or a mod's .stl given as <List>)
        if (positional.Count < 3) { Console.WriteLine("usage: string-get <List|file.stl> <Key|re:regex> [--locale enUS] [--install DIR]"); return 1; }
        StringListFile list;
        if (File.Exists(positional[1])) list = new StringListFile(File.ReadAllBytes(positional[1]));
        else
        {
            using var d3 = D3Casc.D3Storage.Open(D3Casc.D3Storage.ResolveInstall(options.GetValueOrDefault("--install")));
            var toc = d3.Toc.InGroup(StringListFile.Group).FirstOrDefault(e => e.Name.Equals(positional[1], StringComparison.OrdinalIgnoreCase));
            if (toc.Name == null) { Console.WriteLine($"no StringList named {positional[1]}"); return 1; }
            var loc = Opt("--locale", "enUS");
            var e = d3.SnoEntries(StringListFile.Group, toc.Id).FirstOrDefault(x => x.Directory.Equals(loc, StringComparison.OrdinalIgnoreCase));
            if (e == null) { Console.WriteLine($"{toc.Name} is not in locale {loc}"); return 1; }
            list = new StringListFile(d3.Read(e));
        }
        var q = positional[2];
        var rx = q.StartsWith("re:") ? new System.Text.RegularExpressions.Regex(q[3..], System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;
        int hits = 0;
        foreach (var e in list.Entries.Where(e => rx != null ? rx.IsMatch(e.Key) || rx.IsMatch(e.Text) : e.Key == q)) { Console.WriteLine($"{e.Key}\t{e.Text}"); hits++; }
        return hits > 0 ? 0 : 2;
    }
    case "string-roundtrip":
    {
        // string-roundtrip [--install DIR]: every StringList of every locale must rebuild byte for byte through StringListFile
        using var d3 = D3Casc.D3Storage.Open(D3Casc.D3Storage.ResolveInstall(options.GetValueOrDefault("--install")));
        int ok = 0, bad = 0, entries = 0, badHash = 0;
        foreach (var e in d3.AllEntries.Where(e => e.IsSno && e.Group == StringListFile.Group))
        {
            var b = d3.Read(e);
            try
            {
                var f = new StringListFile(b);
                entries += f.Entries.Count;
                for (int i = 0; i < f.Entries.Count; i++)
                    if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(StringListFile.EntriesAt + i * StringListFile.EntrySize + 32)) != StringListFile.Hash(f.Entries[i].Key)) badHash++;
                if (f.Build().AsSpan().SequenceEqual(b)) ok++; else { bad++; if (bad <= 10) Console.WriteLine($"DIFFERS: {e.Path}"); }
            }
            catch (Exception x) { bad++; if (bad <= 10) Console.WriteLine($"FAILED: {e.Path}: {x.Message}"); }
        }
        Console.WriteLine($"{ok + bad} StringLists, {entries:N0} entries: {ok} rebuild byte for byte, {bad} do not; stored key hashes that differ from h*33+c: {badHash}");
        return bad == 0 && badHash == 0 ? 0 : 2;
    }
    case "ui-roundtrip":
        // ui-roundtrip <dir>: every .ui must write back byte-exact through UiDocument
        if (positional.Count < 2) { Console.WriteLine("usage: ui-roundtrip <dir with .ui files>"); return 1; }
        return UiDocument.RoundTrip(positional[1], Console.WriteLine) == 0 ? 0 : 2;
    case "docs-build":
    {
        // docs-build [srcDir] [out.html]: the ModKit docs (Markdown) -> one HTML page
        var src = positional.Count > 1 ? positional[1] : Path.Combine("ModKit", "docs", "src");
        var dst = positional.Count > 2 ? positional[2] : Path.Combine("ModKit", "docs", "index.html");
        return DocsBuilder.Build(src, dst, Console.WriteLine);
    }
    case "mod-verify":
        return D3ModTool.Casc.ModBuild.Verify(ClientDir(), Console.WriteLine);
    case "mod-restore":
        return D3ModTool.Casc.ModBuild.Restore(ClientDir(), Console.WriteLine);
    default:
        Console.WriteLine("commands: mpq-find <filter> | mpq-extract <filter> <outDir> | casc-extract <group> <nameRegex> <outDir>");
        Console.WriteLine("          casc-selftest [client] | mod-build <modDir>... [--client DIR] | mod-verify | mod-restore   (client: --client or %D3_CLIENT_DIR%)");
        Console.WriteLine("          mod-install <modDir>... [--server DIR] [--client DIR] [--data no] | mod-uninstall <id>... | mod-data | modhost-install <hostBuildDir>");
        Console.WriteLine("          ui-check <file|dir> [version|any] | ui-info <file> | ui-convert <in201Dir|file> <outDir>   [--mpq DIR] [--install DIR]");
        Console.WriteLine("          import-gltf <model.glb|.gltf> <modDir> --name <Name> [--like <actor>] [--sno <id>] [--scale <s>] [--actor-scale <s>] [--forward +z] [--static yes] [--fps <n>]");
        Console.WriteLine("                      [--monster new|new:<sno>|<sno>] [--display-name <text>] [--monster-type <type>] [--root-motion auto|off] [--speed walk=<ft/s>]");
        Console.WriteLine("          string-set <modDir> <List> <Key> <Text> [--locale enUS] | string-get <List|file.stl> <Key|re:regex> | string-roundtrip");
        return cmd == "help" ? 0 : 1;
}
}
