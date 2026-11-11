using System.Buffers.Binary;
using WiseOwl.Casc;

namespace D3Casc;

/// <summary>One file of the D3 root: an SNO file (type 0), an SNO sub-file (type 1) or a named file (type 2).</summary>
public sealed record D3Entry(string Directory, int Type, ContentKey Key, int Group, int SnoId, int FileIndex, string Path)
{
    public bool IsSno => Type == 0;
}

/// <summary>A root directory ("Base", "Windows", "enUS_Text", ...) and the entries of its asset file.</summary>
public sealed class D3Directory
{
    public required string Name { get; init; }
    public required ContentKey Key { get; init; }
    public bool Present { get; set; }
    public string? Error { get; set; }
    public uint Magic { get; set; }
    public List<D3Entry> Entries { get; } = new();
    public int SnoCount => Entries.Count(e => e.Type == 0);
    public int SubFileCount => Entries.Count(e => e.Type == 1);
    public int NamedCount => Entries.Count(e => e.Type == 2);
}

/// <summary>
/// Diablo III on top of the game-agnostic WiseOwl.Casc transport (the same package the D4 tool uses):
/// the <c>root</c> content key of the build config points at a small table of {MD5, directory name};
/// each directory's MD5 is a content key of an asset file holding {MD5, sno} / {MD5, sno, index} / {MD5, name} entries.
/// Names come from <c>Base\CoreTOC.dat</c> (sno id -> group + name) and <c>Base\Data_D3\PC\Misc\Packages.dat</c> (sub-file extensions).
/// </summary>
public sealed class D3Storage : IDisposable
{
    public const uint RootMagic = 0x8007D0C4;
    public const string InstallEnvVar = "D3CASC_INSTALL";

    /// <summary>The official install: %D3_INSTALL_DIR%, else the Windows uninstall entry "Diablo III" (InstallLocation),
    /// else the Battle.net default folder.</summary>
    public static string DefaultInstall { get; } = FindOfficialInstall();

    static string FindOfficialInstall()
    {
        if (Environment.GetEnvironmentVariable("D3_INSTALL_DIR") is { Length: > 0 } env) return env;
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Diablo III",
                                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Diablo III" })
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                if (k?.GetValue("InstallLocation") is string path && path.Length > 0) return path;
            }
        }
        return @"C:\Program Files (x86)\Diablo III";
    }

    public CascStorage Casc { get; }
    public string InstallPath { get; }
    public uint RootFileMagic { get; private set; }
    public IReadOnlyList<D3Directory> Directories => _dirs;
    public CoreToc Toc { get; private set; } = null!;
    public PackagesDat Packages { get; private set; } = null!;
    public int DuplicatePaths { get; private set; }
    public int UnknownSnoIds { get; private set; }

    readonly List<D3Directory> _dirs = new();
    readonly Dictionary<string, D3Entry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(int group, int id), List<D3Entry>> _bySno = new();

    D3Storage(CascStorage casc, string install)
    {
        Casc = casc;
        InstallPath = install;
    }

    public static string ResolveInstall(string? arg) =>
        arg ?? Environment.GetEnvironmentVariable(InstallEnvVar) ?? DefaultInstall;

    public static D3Storage Open(string install, Action<string>? log = null)
    {
        if (!File.Exists(Path.Combine(install, ".build.info")))
            throw new DirectoryNotFoundException($"No .build.info in '{install}' (pass --install <game folder> or set {InstallEnvVar})");
        var casc = CascStorage.OpenLocal(install, new CascOpenOptions { Product = "d3" });
        var d3 = new D3Storage(casc, install);
        try
        {
            d3.Load(log ?? (_ => { }));
            return d3;
        }
        catch
        {
            casc.Dispose();
            throw;
        }
    }

    void Load(Action<string> log)
    {
        if (Casc.Config.VfsRoot is not null)
            log("note: this storage has a vfs-root (TVFS); D3 is expected to use the legacy root");

        byte[] root = Casc.Read(Casc.Config.RootContentKey);
        RootFileMagic = BinaryPrimitives.ReadUInt32LittleEndian(root);
        if (RootFileMagic != RootMagic)
            throw new InvalidDataException($"root: magic 0x{RootFileMagic:X8}, expected 0x{RootMagic:X8}");
        int count = BinaryPrimitives.ReadInt32LittleEndian(root.AsSpan(4));
        int pos = 8;
        for (int i = 0; i < count; i++)
        {
            var key = ContentKey.FromBytes(root.AsSpan(pos, 16));
            pos += 16;
            string name = CoreToc.ReadCString(root, pos);
            pos += name.Length + 1;
            _dirs.Add(new D3Directory { Name = name, Key = key });
        }
        if (pos != root.Length) throw new InvalidDataException($"root: {root.Length - pos} trailing bytes");

        // Raw entries first; names need the TOC, which is itself a named entry of "Base".
        var raw = new Dictionary<string, List<(int type, ContentKey key, int sno, int index, string? name)>>();
        foreach (var dir in _dirs)
        {
            byte[] data;
            try { data = Casc.Read(dir.Key); }
            catch (CascException ex) { dir.Error = ex.GetType().Name; continue; }
            dir.Present = true;
            raw[dir.Name] = ParseDirectory(data, dir);
            log($"  {dir.Name,-14} {raw[dir.Name].Count,7} entries");
        }

        var baseEntries = raw.TryGetValue("Base", out var b) ? b : throw new InvalidDataException("root has no readable 'Base' directory");
        Toc = CoreToc.Parse(Casc.Read(FindNamed(baseEntries, "CoreTOC.dat")));
        Packages = PackagesDat.Parse(Casc.Read(FindNamed(baseEntries, @"Data_D3\PC\Misc\Packages.dat")));
        log($"  CoreTOC.dat: {Toc.Entries.Count} SNOs in {Toc.Groups.Count()} groups ({Toc.GroupSlots} slots); Packages.dat: {Packages.Names.Count} names");

        foreach (var dir in _dirs)
        {
            if (!raw.TryGetValue(dir.Name, out var list)) continue;
            foreach (var (type, key, sno, index, name) in list)
            {
                D3Entry entry;
                switch (type)
                {
                    case 0:
                    {
                        var (group, snoName) = Resolve(sno);
                        entry = new D3Entry(dir.Name, 0, key, group, sno, -1, $@"{dir.Name}\{SnoGroups.Name(group)}\{snoName}{SnoGroups.Ext(group)}");
                        break;
                    }
                    case 1:
                    {
                        var (group, snoName) = Resolve(sno);
                        string stem = $@"{SnoGroups.Name(group)}\{snoName}\{index:D4}";
                        string ext = Packages.ExtensionFor(stem) ?? Packages.ExtensionFor($@"{snoName}\{index:D4}") ?? ".xxx";
                        entry = new D3Entry(dir.Name, 1, key, group, sno, index, $@"{dir.Name}\{stem}{ext}");
                        break;
                    }
                    default:
                        entry = new D3Entry(dir.Name, 2, key, -1, -1, -1, $@"{dir.Name}\{name}");
                        break;
                }
                dir.Entries.Add(entry);
                if (!_byPath.TryAdd(entry.Path, entry)) DuplicatePaths++;
                if (type == 0)
                {
                    if (!_bySno.TryGetValue((entry.Group, sno), out var l)) _bySno[(entry.Group, sno)] = l = new List<D3Entry>();
                    l.Add(entry);
                }
            }
        }
    }

    (int group, string name) Resolve(int sno)
    {
        foreach (var g in Toc.Groups)
            if (Toc.TryGetName(g, sno, out var n)) return (g, n);
        UnknownSnoIds++;
        return (-1, sno.ToString());
    }

    static ContentKey FindNamed(List<(int type, ContentKey key, int sno, int index, string? name)> entries, string name)
    {
        foreach (var e in entries)
            if (e.type == 2 && string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase)) return e.key;
        throw new FileNotFoundException($"'Base' directory has no named entry '{name}'");
    }

    /// <summary>{magic; n0 x {md5, sno}; n1 x {md5, sno, index}; n2 x {md5, cstring}} - must consume the file exactly.</summary>
    static List<(int type, ContentKey key, int sno, int index, string? name)> ParseDirectory(byte[] data, D3Directory dir)
    {
        var list = new List<(int, ContentKey, int, int, string?)>();
        int pos = 0;
        dir.Magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos)); pos += 4;
        int n0 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos)); pos += 4;
        for (int i = 0; i < n0; i++)
        {
            var key = ContentKey.FromBytes(data.AsSpan(pos, 16));
            int sno = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 16));
            pos += 20;
            list.Add((0, key, sno, -1, null));
        }
        int n1 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos)); pos += 4;
        for (int i = 0; i < n1; i++)
        {
            var key = ContentKey.FromBytes(data.AsSpan(pos, 16));
            int sno = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 16));
            int index = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 20));
            pos += 24;
            list.Add((1, key, sno, index, null));
        }
        int n2 = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos)); pos += 4;
        for (int i = 0; i < n2; i++)
        {
            var key = ContentKey.FromBytes(data.AsSpan(pos, 16));
            pos += 16;
            string name = CoreToc.ReadCString(data, pos);
            pos += name.Length + 1;
            list.Add((2, key, -1, -1, name));
        }
        if (pos != data.Length)
            throw new InvalidDataException($"directory '{dir.Name}': parsed {pos} of {data.Length} bytes (n0={n0} n1={n1} n2={n2})");
        return list;
    }

    public bool TryGet(string path, out D3Entry entry) => _byPath.TryGetValue(path.Replace('/', '\\'), out entry!);

    /// <summary>All SNO-file entries for (group, id), "Base" first.</summary>
    public IReadOnlyList<D3Entry> SnoEntries(int group, int id) =>
        _bySno.TryGetValue((group, id), out var l) ? l.OrderBy(e => e.Directory == "Base" ? 0 : 1).ToList() : Array.Empty<D3Entry>();

    public IEnumerable<D3Entry> AllEntries => _dirs.SelectMany(d => d.Entries);

    public byte[] Read(D3Entry entry) => Casc.Read(entry.Key);

    public byte[] ReadPath(string path) =>
        TryGet(path, out var e) ? Read(e) : throw new FileNotFoundException($"not in the D3 root: {path}");

    public void Dispose() => Casc.Dispose();
}
