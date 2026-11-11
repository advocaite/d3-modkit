using System.Buffers.Binary;
using System.Text;

namespace D3ModTool;

/// <summary>
/// A whole <c>.ui</c> layout as an editable tree that writes back BYTE-EXACT (verified on every stock 2.8 layout with
/// <c>D3ModTool ui-roundtrip</c>). Unlike <see cref="UiFile"/>'s in-place patches, any edit is safe here: adding, removing
/// and reordering controls, and growing or shrinking any array, because the writer lays the data area out again and
/// rewrites every array reference.
///
/// A file is the 200-byte header, the control records (1856 B each, header @32 count, @36 offset+16, @40 size) and a
/// data area of arrays. Each array is referenced by a triplet {i32 count, i32 offset (file offset - 16), i32 byteSize} in
/// its holder (the header, a control record, or an entry of another array). Holders and the arrays they reference (found
/// by scanning all 258 stock layouts, 2026-11-11; every byte of 257 files is covered, EventText.ui has 4 padding bytes):
/// <code>
/// header (200 B)    +56 656-byte blocks   +80 styles   +104 int props   +128 int props
/// control (1856 B)  +1576 styles (264 B)  +1600 int props (16 B)  +1624 float props (16 B)  +1648 608-byte blocks
///                   +1712 8-byte entries  +1736 8-byte entries    +1760 12-byte entries     +1784 536-byte  +1808 520-byte
/// 656-byte block    +520 styles  +544 int props  +568 float props  +592 608-byte blocks
/// 608-byte block    +512 styles  +536 int props  +560 float props (unused in stock files)
/// </code>
/// Style entry (264 B): i32 id, i32 0, then the value string at +8 (256 B, NUL-terminated). Int/float property entry (16 B): i32 id, 0, value, 0.
/// Unknown bytes of every block are kept as they are. Arrays referenced from several holders stay shared when written.
/// </summary>
public sealed class UiDocument
{
    public const int HeaderSize = 200, ControlSize = 1856, StyleSize = 264, PropertySize = 16;

    /// <summary>Where arrays hang, per holder kind: field offset in the holder -> entry size.</summary>
    static readonly Dictionary<int, (int Field, int EntrySize)[]> Schema = new()
    {
        [HeaderSize] = new[] { (56, 656), (80, StyleSize), (104, PropertySize), (128, PropertySize) },
        [ControlSize] = new[] { (1576, StyleSize), (1600, PropertySize), (1624, PropertySize), (1648, 608), (1712, 8), (1736, 8), (1760, 12), (1784, 536), (1808, 520) },
        [656] = new[] { (520, StyleSize), (544, PropertySize), (568, PropertySize), (592, 608) },
        [608] = new[] { (512, StyleSize), (536, PropertySize), (560, PropertySize) },
    };
    public static IReadOnlyDictionary<int, (int Field, int EntrySize)[]> ArrayFields => Schema;

    /// <summary>A fixed-size record (header, control, or an array entry) and the arrays it references.</summary>
    public sealed class Block
    {
        public byte[] Raw;
        /// <summary>field offset -> array (null = none referenced: count 0).</summary>
        public readonly Dictionary<int, UiArray?> Arrays = new();
        public Block(byte[] raw) => Raw = raw;
        public int Size => Raw.Length;

        public int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(Raw.AsSpan(at));
        public void SetI32(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(Raw.AsSpan(at), v);
        public float F32(int at) => BinaryPrimitives.ReadSingleLittleEndian(Raw.AsSpan(at));
        public string Str(int at, int max)
        {
            int n = Raw.AsSpan(at, max).IndexOf((byte)0);
            return Encoding.UTF8.GetString(Raw, at, n < 0 ? max : n);
        }
        public void SetStr(int at, int max, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length >= max) throw new ArgumentException($"'{value}' is longer than {max - 1} bytes");
            Raw.AsSpan(at, max).Clear();
            bytes.CopyTo(Raw, at);
        }
        public Block DeepCopy()
        {
            var b = new Block((byte[])Raw.Clone());
            foreach (var (field, arr) in Arrays) b.Arrays[field] = arr?.DeepCopy();
            return b;
        }
    }

    /// <summary>An array in the data area.</summary>
    public sealed class UiArray
    {
        public int EntrySize;
        public List<Block> Entries = new();
        /// <summary>Where it was in the source file (layout order on write; new arrays go last).</summary>
        internal long Order = long.MaxValue;
        /// <summary>Bytes that followed it in the source file before the next array (padding), kept for exact output.</summary>
        internal byte[] Gap = Array.Empty<byte>();
        public UiArray(int entrySize) => EntrySize = entrySize;
        public UiArray DeepCopy()
        {
            var a = new UiArray(EntrySize);
            foreach (var e in Entries) a.Entries.Add(e.DeepCopy());
            return a;
        }
    }

    public Block Header { get; private set; } = null!;
    public List<Block> Controls { get; } = new();
    /// <summary>Bytes before the first array that no array covers (none in stock files), kept for exact output.</summary>
    byte[] _lead = Array.Empty<byte>();

    public int Version => Header.I32(4);
    public int SnoId { get => Header.I32(16); set => Header.SetI32(16, value); }

    public static UiDocument Parse(byte[] bytes)
    {
        if (bytes.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != UiFile.Magic) throw new InvalidDataException("not a .ui layout");
        var doc = new UiDocument();
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32));
        int dataStart = HeaderSize + count * ControlSize;
        if (count < 0 || dataStart > bytes.Length) throw new InvalidDataException($"{count} controls do not fit");
        var byStart = new Dictionary<int, UiArray>();
        Block Read(int at, int size)
        {
            var block = new Block(bytes.AsSpan(at, size).ToArray());
            if (!Schema.TryGetValue(size, out var fields)) return block;
            foreach (var (field, entry) in fields)
            {
                int n = block.I32(field), start = block.I32(field + 4) + 16, byteSize = block.I32(field + 8);
                if (n <= 0) { block.Arrays[field] = null; continue; }
                if (byteSize != n * entry || start < dataStart || start + (long)byteSize > bytes.Length)
                    throw new InvalidDataException($"array at holder+{field}: {n} x {entry} B @ {start} does not fit");
                if (!byStart.TryGetValue(start, out var arr))
                {
                    arr = new UiArray(entry) { Order = start };
                    byStart[start] = arr;
                    for (int e = 0; e < n; e++) arr.Entries.Add(Read(start + e * entry, entry));
                }
                else if (arr.EntrySize != entry || arr.Entries.Count != n)
                    throw new InvalidDataException($"array @ {start} is referenced with different shapes");
                block.Arrays[field] = arr;
            }
            return block;
        }
        doc.Header = Read(0, HeaderSize);
        for (int i = 0; i < count; i++) doc.Controls.Add(Read(HeaderSize + i * ControlSize, ControlSize));
        // gaps: bytes between arrays (and after the last), kept with the array before them
        var ordered = byStart.OrderBy(kv => kv.Key).ToList();
        int lead = ordered.Count > 0 ? ordered[0].Key : bytes.Length;
        doc._lead = bytes.AsSpan(dataStart, Math.Max(0, lead - dataStart)).ToArray();
        for (int i = 0; i < ordered.Count; i++)
        {
            var (start, arr) = (ordered[i].Key, ordered[i].Value);
            int end = start + arr.Entries.Count * arr.EntrySize;
            int next = i + 1 < ordered.Count ? ordered[i + 1].Key : bytes.Length;
            if (next < end) throw new InvalidDataException($"arrays overlap at {next}");
            arr.Gap = bytes.AsSpan(end, next - end).ToArray();
        }
        return doc;
    }

    public byte[] Write()
    {
        // every array reachable from the header and the controls, once, in source order (new ones last, in discovery order)
        var arrays = new List<UiArray>();
        var seen = new HashSet<UiArray>(ReferenceEqualityComparer.Instance);
        void Collect(Block b)
        {
            foreach (var arr in b.Arrays.Values)
            {
                if (arr == null || arr.Entries.Count == 0 || !seen.Add(arr)) continue;
                arrays.Add(arr);
                foreach (var e in arr.Entries) Collect(e);
            }
        }
        Collect(Header);
        foreach (var c in Controls) Collect(c);
        var layout = arrays.Select((a, i) => (a, i)).OrderBy(x => x.a.Order).ThenBy(x => x.i).Select(x => x.a).ToList();

        int dataStart = HeaderSize + Controls.Count * ControlSize;
        var at = new Dictionary<UiArray, int>(ReferenceEqualityComparer.Instance);
        long pos = dataStart + _lead.Length;
        foreach (var arr in layout)
        {
            at[arr] = (int)pos;
            pos += (long)arr.Entries.Count * arr.EntrySize + arr.Gap.Length;
        }
        var output = new byte[pos];
        void Patch(Block b)
        {
            foreach (var (field, arr) in b.Arrays)
            {
                if (arr == null) continue; // never referenced one: the source bytes stay as they were
                bool any = arr.Entries.Count > 0;    // an array emptied by an edit is written as {0, 0, 0}
                b.SetI32(field, any ? arr.Entries.Count : 0);
                b.SetI32(field + 4, any ? at[arr] - 16 : 0);
                b.SetI32(field + 8, any ? arr.Entries.Count * arr.EntrySize : 0);
            }
        }
        Header.SetI32(32, Controls.Count);
        Header.SetI32(36, HeaderSize - 16);
        Header.SetI32(40, Controls.Count * ControlSize);
        Patch(Header);
        Header.Raw.CopyTo(output, 0);
        for (int i = 0; i < Controls.Count; i++)
        {
            if (Controls[i].Size != ControlSize) throw new InvalidOperationException($"control {i} is {Controls[i].Size} bytes");
            Patch(Controls[i]);
            Controls[i].Raw.CopyTo(output, HeaderSize + i * ControlSize);
        }
        _lead.CopyTo(output, dataStart);
        foreach (var arr in layout)
        {
            int p = at[arr];
            foreach (var e in arr.Entries)
            {
                if (e.Size != arr.EntrySize) throw new InvalidOperationException($"entry of {e.Size} bytes in an array of {arr.EntrySize}");
                Patch(e);
                e.Raw.CopyTo(output, p);
                p += arr.EntrySize;
            }
            arr.Gap.CopyTo(output, p);
        }
        return output;
    }

    // ------------------------------------------------------------------------------------------------- editing

    public const int TypeAt = 1048, StylesField = 1576, IntsField = 1600, FloatsField = 1624;
    /// <summary>Style entry (264 B): i32 id, i32 0, then the string (256 B, NUL-terminated).</summary>
    public const int StyleTextAt = 8;

    public static string NameOf(Block c) => c.Str(16, 512);
    public static string ParentOf(Block c) => c.Str(536, 512);
    public static string FullOf(Block c) => ParentOf(c).Length == 0 ? NameOf(c) : ParentOf(c) + "." + NameOf(c);
    public static string ClassOf(Block c) => c.Str(UiFile.ClassNameAt, 512);
    public static int TypeOf(Block c) => c.I32(TypeAt);

    public int IndexOf(string full) => Controls.FindIndex(c => FullOf(c) == full);

    /// <summary>The control and every control below it (by path), in file order.</summary>
    public List<int> Subtree(int index)
    {
        var full = FullOf(Controls[index]);
        var list = new List<int> { index };
        for (int i = 0; i < Controls.Count; i++)
            if (i != index && (ParentOf(Controls[i]) == full || ParentOf(Controls[i]).StartsWith(full + ".", StringComparison.Ordinal))) list.Add(i);
        return list;
    }

    /// <summary>How many holders reference the array (an edit must not change the other holders' data).</summary>
    int References(UiArray arr)
    {
        int n = 0;
        void Count(Block b)
        {
            foreach (var a in b.Arrays.Values)
            {
                if (a == null) continue;
                if (ReferenceEquals(a, arr)) n++;
                foreach (var e in a.Entries) Count(e);
            }
        }
        Count(Header);
        foreach (var c in Controls) Count(c);
        return n;
    }

    /// <summary>The block's array at <paramref name="field"/>, created if missing, copied first if another holder shares it.</summary>
    public UiArray Own(Block b, int field, int entrySize)
    {
        b.Arrays.TryGetValue(field, out var arr);
        if (arr == null) { arr = new UiArray(entrySize); b.Arrays[field] = arr; return arr; }
        if (References(arr) > 1) { arr = arr.DeepCopy(); b.Arrays[field] = arr; }
        return arr;
    }

    /// <summary>Sets (or adds) an int (<see cref="IntsField"/>) or float (<see cref="FloatsField"/>) property: entries {id, 0, value, 0}.</summary>
    public void SetProperty(int control, int field, int id, double value)
    {
        var arr = Own(Controls[control], field, PropertySize);
        var entry = arr.Entries.FirstOrDefault(e => e.I32(0) == id);
        if (entry == null) { entry = new Block(new byte[PropertySize]); entry.SetI32(0, id); arr.Entries.Add(entry); }
        if (field == FloatsField) BinaryPrimitives.WriteSingleLittleEndian(entry.Raw.AsSpan(8), (float)value);
        else entry.SetI32(8, (int)value);
    }

    /// <summary>Sets (or adds) a style: entry {i32 id, string value}.</summary>
    public void SetStyle(int control, int id, string value)
    {
        var arr = Own(Controls[control], StylesField, StyleSize);
        var entry = arr.Entries.FirstOrDefault(e => e.I32(0) == id);
        if (entry == null) { entry = new Block(new byte[StyleSize]); entry.SetI32(0, id); arr.Entries.Add(entry); }
        entry.SetStr(StyleTextAt, StyleSize - StyleTextAt, value);
    }

    /// <summary>Removes a style or property entry (field = StylesField, IntsField or FloatsField). False if it was not there.</summary>
    public bool Remove(int control, int field, int id)
    {
        if (!Controls[control].Arrays.TryGetValue(field, out var a) || a == null || a.Entries.All(e => e.I32(0) != id)) return false;
        Own(Controls[control], field, a.EntrySize).Entries.RemoveAll(e => e.I32(0) == id);
        return true;
    }

    public void SetType(int control, int type) => Controls[control].SetI32(TypeAt, type);

    /// <summary>Gives the control (and, by path, its whole subtree) a new full path: name, parent, hashes.</summary>
    void Relocate(List<int> subtree, string oldFull, string newFull)
    {
        foreach (var i in subtree)
        {
            var c = Controls[i];
            var full = FullOf(c);
            var moved = newFull + full[oldFull.Length..];
            UiFile.SetIdentity(c.Raw, 0, moved);
        }
    }

    /// <summary>Renames a control; its descendants' paths follow.</summary>
    public void Rename(int control, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('.')) throw new ArgumentException("a name has no dots and is not empty");
        var c = Controls[control];
        var oldFull = FullOf(c);
        var parent = ParentOf(c);
        var newFull = parent.Length == 0 ? newName : parent + "." + newName;
        if (IndexOf(newFull) >= 0) throw new ArgumentException($"{newFull} exists already");
        Relocate(Subtree(control), oldFull, newFull);
    }

    /// <summary>Moves a control (with its subtree) under another parent control; its records move after the new parent's.</summary>
    public void Reparent(int control, int newParent)
    {
        var sub = Subtree(control);
        if (sub.Contains(newParent)) throw new ArgumentException("a control cannot move into itself");
        var oldFull = FullOf(Controls[control]);
        var parentFull = FullOf(Controls[newParent]);
        var newFull = parentFull + "." + NameOf(Controls[control]);
        if (IndexOf(newFull) >= 0) throw new ArgumentException($"{newFull} exists already");
        Relocate(sub, oldFull, newFull);
        // keep stock order: the moved records go right after the new parent's subtree (indexes change)
        var moved = sub.OrderBy(i => i).Select(i => Controls[i]).ToList();
        foreach (var i in sub.OrderByDescending(i => i)) Controls.RemoveAt(i);
        int p = IndexOf(parentFull);
        Controls.InsertRange(Subtree(p).Max() + 1, moved);
    }

    /// <summary>
    /// Copies a control with its subtree as a sibling named <paramref name="newName"/> (default: name + a number), inserted
    /// right after the original subtree. The copies own their data (no sharing) and anonymous classes get their own names.
    /// Returns the copy's index.
    /// </summary>
    public int Duplicate(int control, string? newName = null)
    {
        var sub = Subtree(control);
        var src = Controls[control];
        var parent = ParentOf(src);
        string Full(string n) => parent.Length == 0 ? n : parent + "." + n;
        if (newName == null)
        {
            int k = 1;
            while (IndexOf(Full(NameOf(src) + k)) >= 0) k++;
            newName = NameOf(src) + k;
        }
        if (IndexOf(Full(newName)) >= 0) throw new ArgumentException($"{Full(newName)} exists already");
        var oldFull = FullOf(src);
        var newFull = Full(newName);
        var copies = sub.Select(i => Controls[i].DeepCopy()).ToList();
        int insertAt = sub.Max() + 1;
        Controls.InsertRange(insertAt, copies);
        var copyIdx = Enumerable.Range(insertAt, copies.Count).ToList();
        Relocate(copyIdx, oldFull, newFull);
        foreach (var i in copyIdx) UiFile.SetUniqueClass(Controls[i].Raw, NameOf(Controls[i]), newName);
        return insertAt;
    }

    /// <summary>Record +0: the control's order among its siblings (children are built and drawn in this order).</summary>
    public const int OrderAt = 0;

    /// <summary>
    /// Adds a new control under <paramref name="parentFull"/> ("" = a new root): a copy of <paramref name="prototype"/> (a
    /// 1856-byte record of a control of the wanted type) with every array emptied, its own identity and anonymous class, and
    /// the next order value. Inserted after the parent's subtree (or at <paramref name="insertAt"/>: stock layouts list a parent before its children). Returns its index.
    /// </summary>
    public int AddControl(byte[] prototype, string parentFull, string name, int type, int? insertAt = null)
    {
        if (prototype.Length != ControlSize) throw new ArgumentException("a control record is 1856 bytes");
        if (string.IsNullOrWhiteSpace(name) || name.Contains('.')) throw new ArgumentException("a name has no dots and is not empty");
        var full = parentFull.Length == 0 ? name : parentFull + "." + name;
        if (IndexOf(full) >= 0) throw new ArgumentException($"{full} exists already");
        int parent = parentFull.Length == 0 ? -1 : IndexOf(parentFull);
        if (parentFull.Length > 0 && parent < 0) throw new ArgumentException($"no control {parentFull}");
        var block = new Block((byte[])prototype.Clone());
        foreach (var (field, _) in Schema[ControlSize])
        {
            block.SetI32(field, 0); block.SetI32(field + 4, 0); block.SetI32(field + 8, 0);
            block.Arrays[field] = null;
        }
        UiFile.SetIdentity(block.Raw, 0, full);
        var cls = "$anon$" + name;
        block.SetStr(UiFile.ClassNameAt, 512, cls);
        BinaryPrimitives.WriteUInt64LittleEndian(block.Raw.AsSpan(UiFile.ClassHashAt), UiFile.PathHash(cls));
        block.SetI32(TypeAt, type);
        block.SetI32(OrderAt, Controls.Count == 0 ? 0 : Controls.Max(c => c.I32(OrderAt)) + 1);
        int at = insertAt ?? (parent < 0 ? Controls.Count : Subtree(parent).Max() + 1);
        Controls.Insert(at, block);
        return at;
    }

    /// <summary>A name that is free under the parent: base, base1, base2...</summary>
    public string FreeName(string parentFull, string baseName)
    {
        string Full(string n) => parentFull.Length == 0 ? n : parentFull + "." + n;
        if (IndexOf(Full(baseName)) < 0) return baseName;
        int k = 1;
        while (IndexOf(Full(baseName + k)) >= 0) k++;
        return baseName + k;
    }

    /// <summary>
    /// An empty layout "&lt;name&gt;_main.LayoutRoot" (two windows) on the header of an existing layout (its header arrays are
    /// dropped), for a new screen. <paramref name="windowPrototype"/> = a type-2 window record.
    /// </summary>
    public static UiDocument CreateEmpty(byte[] headerSource, byte[] windowPrototype, string name, int snoId)
    {
        var src = Parse(headerSource);
        var doc = new UiDocument { Header = new Block((byte[])src.Header.Raw.Clone()) };
        foreach (var (field, _) in Schema[HeaderSize])
        {
            doc.Header.SetI32(field, 0); doc.Header.SetI32(field + 4, 0); doc.Header.SetI32(field + 8, 0);
            doc.Header.Arrays[field] = null;
        }
        doc.SnoId = snoId;
        int root = doc.AddControl(windowPrototype, "", name + "_main", 2);
        doc.AddControl(windowPrototype, name + "_main", "LayoutRoot", 2);
        doc.SetProperty(root, IntsField, 0x09, 1600);
        doc.SetProperty(root, IntsField, 0x0A, 1200);
        return doc;
    }

    /// <summary>Deletes a control and its subtree.</summary>
    public void Delete(int control)
    {
        foreach (var i in Subtree(control).OrderByDescending(i => i)) Controls.RemoveAt(i);
    }

    /// <summary>
    /// Round-trip check for every .ui under a folder: Parse + Write must give the identical bytes. Returns the number of
    /// files that differ or fail.
    /// </summary>
    public static int RoundTrip(string dir, Action<string> log)
    {
        int ok = 0, bad = 0;
        foreach (var f in Directory.GetFiles(dir, "*.ui", SearchOption.AllDirectories).OrderBy(f => f))
        {
            try
            {
                var src = File.ReadAllBytes(f);
                var dst = Parse(src).Write();
                if (src.AsSpan().SequenceEqual(dst)) { ok++; continue; }
                int first = 0;
                while (first < Math.Min(src.Length, dst.Length) && src[first] == dst[first]) first++;
                log($"DIFFERS {Path.GetFileName(f)}: {src.Length} -> {dst.Length} B, first difference at {first}");
                bad++;
            }
            catch (Exception e) { log($"FAILED {Path.GetFileName(f)}: {e.Message}"); bad++; }
        }
        log($"round trip: {ok} identical, {bad} differ or fail");
        return bad;
    }
}
