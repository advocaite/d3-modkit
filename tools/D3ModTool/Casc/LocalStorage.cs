using System.Text;

namespace D3ModTool.Casc;

/// <summary>
/// Read side of a local CASC install (independent of WiseOwl.Casc, so the writer can be checked against two readers).
/// <paramref name="stateDir"/> can point at a snapshot holding <c>.build.info</c> and <c>idx\*.idx</c> to read the storage as it was then.
/// </summary>
public sealed class LocalStorage
{
    public string Client { get; }
    public string DataDir => Path.Combine(Client, "Data", "data");
    public string BuildInfoText { get; }
    public string BuildKey { get; }
    public byte[] BuildConfigRaw { get; }
    public Dictionary<string, string> BuildConfig { get; }
    public SortedDictionary<int, string> IdxFiles { get; }
    public string IdxDir { get; }
    public Dictionary<int, LocalIndex> Indexes { get; } = new();
    readonly Dictionary<string, LocalIndex.Entry> _byKey = new();
    public byte[] EncodingRaw { get; }
    public EncodingFile Encoding { get; }
    public int MaxArchive { get; }

    public LocalStorage(string client, string? stateDir = null)
    {
        Client = client;
        BuildInfoText = File.ReadAllText(Path.Combine(stateDir ?? client, ".build.info"));
        BuildKey = ActiveBuildKey(BuildInfoText);
        BuildConfigRaw = File.ReadAllBytes(ConfigPath(client, BuildKey));
        BuildConfig = ParseConfig(BuildConfigRaw);
        IdxDir = stateDir != null ? Path.Combine(stateDir, "idx") : DataDir;
        IdxFiles = LocalIndex.Latest(IdxDir);
        foreach (var (bucket, name) in IdxFiles)
        {
            var idx = new LocalIndex(ReadShared(Path.Combine(IdxDir, name)));
            Indexes[bucket] = idx;
            foreach (var e in idx.Entries())
            {
                _byKey.TryAdd(Keys.Hex(e.Key9), e);
                MaxArchive = Math.Max(MaxArchive, e.Archive);
            }
        }
        var enc = BuildConfig["encoding"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        EncodingRaw = ReadEKey(Keys.Parse(enc[1]));
        Encoding = new EncodingFile(EncodingRaw);
    }

    public static string ConfigPath(string client, string key) => Path.Combine(client, "Data", "config", key[..2], key.Substring(2, 2), key);

    public static string ActiveBuildKey(string buildInfo)
    {
        var lines = buildInfo.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var columns = lines[0].Split('|').Select(c => c.Split('!')[0]).ToList();
        int active = columns.IndexOf("Active"), key = columns.IndexOf("Build Key");
        foreach (var row in lines.Skip(1))
        {
            var f = row.Split('|');
            if (f.Length > key && (active < 0 || f[active] == "1")) return f[key];
        }
        throw new InvalidDataException("no active build in .build.info");
    }

    public static Dictionary<string, string> ParseConfig(byte[] raw)
    {
        var d = new Dictionary<string, string>();
        foreach (var line in System.Text.Encoding.UTF8.GetString(raw).Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith('#')) continue;
            int eq = line.IndexOf('=');
            if (eq > 0) d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    /// <summary>The config with some values replaced, keeping every other byte (line order, comments, line endings).</summary>
    public static byte[] SetConfigValues(byte[] raw, IReadOnlyDictionary<string, string> values)
    {
        var lines = System.Text.Encoding.UTF8.GetString(raw).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int eq = lines[i].IndexOf('=');
            if (eq <= 0 || lines[i].StartsWith('#')) continue;
            var name = lines[i][..eq].Trim();
            if (values.TryGetValue(name, out var v)) lines[i] = $"{name} = {v}" + (lines[i].EndsWith('\r') ? "\r" : "");
        }
        return System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    public static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var b = new byte[fs.Length];
        fs.ReadExactly(b);
        return b;
    }

    public bool TryLocate(ReadOnlySpan<byte> ekey, out LocalIndex.Entry entry) => _byKey.TryGetValue(Keys.Hex(ekey[..9]), out entry);

    /// <summary>The raw archive bytes of an entry (30-byte header + BLTE).</summary>
    public byte[] ReadRaw(LocalIndex.Entry e)
    {
        using var fs = new FileStream(Path.Combine(DataDir, $"data.{e.Archive:D3}"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(e.Offset, SeekOrigin.Begin);
        var b = new byte[e.Size];
        fs.ReadExactly(b);
        return b;
    }

    public byte[] ReadEKey(ReadOnlySpan<byte> ekey)
    {
        if (!TryLocate(ekey, out var e)) throw new KeyNotFoundException($"ekey {Keys.Hex(ekey)} is not in the local index");
        return Blte.Decode(ReadRaw(e).AsSpan(ArchiveEntryHeader.Size));
    }

    public byte[] ReadCKey(byte[] ckey)
    {
        var hit = Encoding.Lookup(ckey) ?? throw new KeyNotFoundException($"ckey {Keys.Hex(ckey)} is not in the encoding file");
        return ReadEKey(hit.EKey);
    }

    public byte[] RootRaw => ReadCKey(Keys.Parse(BuildConfig["root"]));
}
