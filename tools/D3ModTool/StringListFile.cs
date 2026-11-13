using System.Buffers.Binary;
using System.Text;
using D3Casc;

namespace D3ModTool;

/// <summary>
/// StringList SNO (group 42, <c>.stl</c>, format 45 in 2.8.0.99920): the game's text, one file per list and locale, stored in the
/// locale's root directory (<c>enUS\StringList\Monsters.stl</c>). Layout (all 5,508 enUS lists rebuild byte for byte):
/// <code>
/// @0x00 0xDEADBEEF, 45, 0, 0      @0x10 SNO id, 20 zero bytes
/// @0x28 {i32 offset (file - 16), i32 size}: entry array, 40 bytes per entry; @0x30 8 zero bytes; entries start at 0x38
///   +0  8 zero bytes   +8  {offset, size incl. NUL} key       +16 8 zero bytes   +24 {offset, size incl. NUL} text (UTF-8)
///   +32 u32 hash of the key, u32 0
/// then per entry its key and its text, each 8-byte aligned; the file ends at the end of the last string.
/// </code>
/// The client looks texts up by the hash only (a hash table built at load, <c>h = h * 33 + (signed char)c</c>, case-sensitive;
/// exe 0x17da10 / 0x7015e0), so two keys of one list must not share a hash.
/// </summary>
public sealed class StringListFile
{
    public const int Group = 42, FormatVersion = 45, EntriesAt = 0x38, EntrySize = 40;

    public sealed record Entry(string Key, string Text);

    readonly byte[] _head;
    public int Sno => BinaryPrimitives.ReadInt32LittleEndian(_head.AsSpan(16));
    public List<Entry> Entries { get; } = new();

    public StringListFile(byte[] b)
    {
        if (b.Length < EntriesAt || BinaryPrimitives.ReadUInt32LittleEndian(b) != 0xDEADBEEF || I32(b, 4) != FormatVersion)
            throw new InvalidDataException("not a 2.8.0 StringList (format 45)");
        int off = I32(b, 0x28) + 16, size = I32(b, 0x2C);
        if (size == 0 && off == 16) off = EntriesAt;            // an empty list: {0, 0} (3 stock lists)
        if (off != EntriesAt || size % EntrySize != 0 || off + (long)size > b.Length) throw new InvalidDataException($"unexpected entry array {off}/{size}");
        _head = b.AsSpan(0, EntriesAt).ToArray();
        for (int at = off; at < off + size; at += EntrySize)
        {
            string key = Str(b, at + 8), text = Str(b, at + 24);
            Entries.Add(new Entry(key, text));
        }
    }

    static string Str(byte[] b, int serAt)
    {
        int off = I32(b, serAt) + 16, size = I32(b, serAt + 4);
        if (size <= 0) return "";
        if (off < EntriesAt || off + (long)size > b.Length) throw new InvalidDataException("string out of range");
        int n = size;
        while (n > 0 && b[off + n - 1] == 0) n--;
        return Encoding.UTF8.GetString(b, off, n);
    }

    /// <summary>The key hash the client uses: h = h * 33 + c over the UTF-8 bytes, c sign-extended, case-sensitive.</summary>
    public static uint Hash(string key)
    {
        uint h = 0;
        foreach (byte c in Encoding.UTF8.GetBytes(key)) h = unchecked(h * 33 + (uint)(sbyte)c);
        return h;
    }

    /// <summary>Adds the entry, or changes the text of the entry with that key (its position is kept). Refuses a key whose
    /// hash equals another key's (the client would find the other one). Returns the previous text, or null for a new key.</summary>
    public string? Set(string key, string text)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("empty key");
        int i = Entries.FindIndex(e => e.Key == key);
        uint h = Hash(key);
        var clash = Entries.FirstOrDefault(e => e.Key != key && Hash(e.Key) == h);
        if (clash != null) throw new InvalidDataException($"key '{key}' has the same hash as '{clash.Key}' in this list; pick another name");
        if (i >= 0) { var old = Entries[i].Text; Entries[i] = new Entry(key, text); return old; }
        Entries.Add(new Entry(key, text));
        return null;
    }

    public string? Get(string key) => Entries.FirstOrDefault(e => e.Key == key)?.Text;

    public byte[] Build()
    {
        var o = new MemoryStream();
        o.Write(_head);
        var table = new byte[Entries.Count * EntrySize];
        o.Write(table);
        void Ser(int at, int fileOffset, int size) { W32(table, at, fileOffset - 16); W32(table, at + 4, size); }
        for (int i = 0; i < Entries.Count; i++)
        {
            int at = i * EntrySize;
            foreach (var (s, ser) in new[] { (Entries[i].Key, 8), (Entries[i].Text, 24) })
            {
                while ((o.Length - 16) % 8 != 0) o.WriteByte(0);
                var bytes = Encoding.UTF8.GetBytes(s);
                Ser(at + ser, (int)o.Length, bytes.Length + 1);
                o.Write(bytes); o.WriteByte(0);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at + 32), Hash(Entries[i].Key));
        }
        var b = o.ToArray();
        table.CopyTo(b, EntriesAt);
        W32(b, 0x28, table.Length == 0 ? 0 : EntriesAt - 16); W32(b, 0x2C, table.Length);
        return b;
    }

    static int I32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
    static void W32(byte[] b, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), v);

    // ------------------------------------------------------------------------------------------------ mods

    /// <summary>Locale root directories of the install that hold this list (a US install has only enUS).</summary>
    public static List<string> Locales(D3Storage d3, int listSno) =>
        d3.Directories.Where(d => d.Present && d.Name is not ("Base" or "Windows" or "Mac"))
            .Where(d => d.Entries.Any(e => e.IsSno && e.Group == Group && e.SnoId == listSno)).Select(d => d.Name).ToList();

    /// <summary>
    /// Sets one text in a mod: <c>&lt;modDir&gt;\data\&lt;locale&gt;\StringList\&lt;List&gt;.stl</c> for every locale of the install that has the
    /// list (or only <paramref name="onlyLocale"/>). The mod's copy is the starting point when it exists (so several texts can be
    /// added), else the stock list. Every stock entry is checked to be unchanged in the result. Returns the files written.
    /// </summary>
    public static List<(string Locale, int Sno, string Path)> SetInMod(D3Storage d3, string modDir, string list, string key, string text,
        string? onlyLocale, Action<string> say)
    {
        var toc = d3.Toc.InGroup(Group).FirstOrDefault(e => e.Name.Equals(list, StringComparison.OrdinalIgnoreCase));
        if (toc.Name == null) throw new ArgumentException($"the game has no StringList named '{list}'");
        var locales = Locales(d3, toc.Id);
        if (onlyLocale != null)
        {
            if (!locales.Contains(onlyLocale, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"locale {onlyLocale} has no {toc.Name}.stl in {d3.InstallPath} (it has: {string.Join(", ", locales)})");
            locales = locales.Where(l => l.Equals(onlyLocale, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (locales.Count == 0) throw new InvalidDataException($"{toc.Name}.stl is in no locale of {d3.InstallPath}");
        var written = new List<(string, int, string)>();
        foreach (var loc in locales)
        {
            var entry = d3.SnoEntries(Group, toc.Id).First(e => e.Directory == loc);
            var stockBytes = d3.Read(entry);
            var stock = new StringListFile(stockBytes);
            if (!stock.Build().AsSpan().SequenceEqual(stockBytes)) throw new InvalidDataException($"{loc}\\{toc.Name}.stl does not rebuild byte for byte; not touching it");
            var path = Path.Combine(modDir, "data", loc, "StringList", toc.Name + ".stl");
            var file = new StringListFile(File.Exists(path) ? File.ReadAllBytes(path) : stockBytes);
            if (file.Sno != toc.Id) throw new InvalidDataException($"{path} has SNO {file.Sno}, not {toc.Id}");
            var old = file.Set(key, text);
            // every stock text that the mod did not change on purpose must still be there, in order
            var mine = file.Entries.ToDictionary(e => e.Key, e => e.Text);
            foreach (var e in stock.Entries)
                if (!mine.ContainsKey(e.Key)) throw new InvalidDataException($"{path}: stock key '{e.Key}' is missing");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Build());
            int added = file.Entries.Count - stock.Entries.Count;
            int changed = stock.Entries.Count(e => mine[e.Key] != e.Text);
            say($"{loc}\\StringList\\{toc.Name}.stl: '{key}' = '{text}'{(old == null ? " (new)" : old == text ? " (unchanged)" : $" (was '{old}')")}; {stock.Entries.Count} stock entries, {added} added, {changed} stock text(s) changed by this mod");
            written.Add((loc, toc.Id, path));
        }
        return written;
    }
}
