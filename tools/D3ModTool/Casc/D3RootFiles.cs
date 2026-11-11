using System.Buffers.Binary;
using System.Text;

namespace D3ModTool.Casc;

/// <summary>
/// D3's root file: <c>u32 magic 0x8007D0C4, u32 count, {ckey[16], name\0} * count</c> - one entry per directory (Base, enUS, Windows...),
/// whose ckey is that directory's sub-root.
/// </summary>
public sealed class D3RootFile
{
    public const uint Magic = 0x8007D0C4;
    public readonly record struct Dir(string Name, byte[] CKey, int At);

    public byte[] Raw { get; }
    public List<Dir> Dirs { get; } = new();

    public D3RootFile(byte[] raw)
    {
        Raw = raw;
        if (BinaryPrimitives.ReadUInt32LittleEndian(raw) != Magic) throw new InvalidDataException("not a D3 root");
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)), at = 8;
        for (int i = 0; i < count; i++)
        {
            int end = Array.IndexOf(raw, (byte)0, at + 16);
            Dirs.Add(new Dir(Encoding.UTF8.GetString(raw, at + 16, end - at - 16), raw[at..(at + 16)], at));
            at = end + 1;
        }
    }

    /// <summary>A copy with some directories' sub-root ckeys replaced.</summary>
    public byte[] WithSubRoots(IReadOnlyDictionary<string, byte[]> ckeys)
    {
        var copy = (byte[])Raw.Clone();
        foreach (var d in Dirs)
            if (ckeys.TryGetValue(d.Name, out var k)) k.CopyTo(copy, d.At);
        return copy;
    }
}

/// <summary>
/// A D3 sub-root (one directory of the root):
/// <code>
/// u32 magic | u32 n, {ckey[16], i32 sno} * n | u32 n, {ckey[16], i32 sno, i32 subId} * n | u32 n, {ckey[16], name\0} * n
/// </code>
/// SNO files are found by id (their group comes from CoreTOC); the named list holds CoreTOC.dat, Packages.dat etc.
/// </summary>
public sealed class D3SubRoot
{
    public byte[] Raw { get; private set; }
    int _snoCount;
    public readonly Dictionary<int, int> SnoAt = new();         // sno -> offset of its ckey
    public readonly Dictionary<string, int> NamedAt = new();    // name -> offset of its ckey
    public bool SnosSorted { get; private set; } = true;

    public D3SubRoot(byte[] raw)
    {
        Raw = raw;
        int at = 4;
        _snoCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at)); at += 4;
        int previous = int.MinValue;
        for (int i = 0; i < _snoCount; i++, at += 20)
        {
            int sno = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at + 16));
            if (sno < previous) SnosSorted = false;
            previous = sno;
            SnoAt[sno] = at;
        }
        int subCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at)); at += 4 + subCount * 24;
        int namedCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at)); at += 4;
        for (int i = 0; i < namedCount; i++)
        {
            int end = Array.IndexOf(raw, (byte)0, at + 16);
            NamedAt[Encoding.UTF8.GetString(raw, at + 16, end - at - 16)] = at;
            at = end + 1;
        }
        if (at != raw.Length) throw new InvalidDataException($"sub-root parse ended at {at} of {raw.Length}");
    }

    public void SetNamed(string name, byte[] ckey) => ckey.CopyTo(Raw, NamedAt[name]);

    /// <summary>Adds or replaces SNO entries; new ones are inserted in id order when the list is sorted (it is in 2.8.0.99920).</summary>
    public void SetSnos(IEnumerable<(int Sno, byte[] CKey)> entries)
    {
        var added = new List<(int Sno, byte[] CKey)>();
        foreach (var (sno, ckey) in entries)
            if (SnoAt.TryGetValue(sno, out int at)) ckey.CopyTo(Raw, at);
            else added.Add((sno, ckey));
        if (added.Count == 0) return;
        var list = new List<(int Sno, byte[] Entry)>(_snoCount + added.Count);
        for (int i = 0; i < _snoCount; i++)
        {
            int at = 8 + i * 20;
            list.Add((BinaryPrimitives.ReadInt32LittleEndian(Raw.AsSpan(at + 16)), Raw[at..(at + 20)]));
        }
        foreach (var (sno, ckey) in added)
        {
            var e = new byte[20];
            ckey.CopyTo(e, 0);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(16), sno);
            list.Add((sno, e));
        }
        if (SnosSorted) list = list.OrderBy(x => x.Sno).ToList();   // stable: originals keep their relative order
        int tail = 8 + _snoCount * 20;
        var raw = new byte[Raw.Length + added.Count * 20];
        Raw.AsSpan(0, 4).CopyTo(raw);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), (uint)list.Count);
        for (int i = 0; i < list.Count; i++) list[i].Entry.CopyTo(raw, 8 + i * 20);
        Raw.AsSpan(tail).CopyTo(raw.AsSpan(8 + list.Count * 20));
        var reparsed = new D3SubRoot(raw);
        Raw = reparsed.Raw; _snoCount = reparsed._snoCount;
        SnoAt.Clear(); foreach (var kv in reparsed.SnoAt) SnoAt[kv.Key] = kv.Value;
        NamedAt.Clear(); foreach (var kv in reparsed.NamedAt) NamedAt[kv.Key] = kv.Value;
    }
}

/// <summary>
/// <c>Base\CoreTOC.dat</c>. Header: i32 counts[70], i32 offsets[70], i32 counts2[70], i32 nextSno. Group g's block starts at
/// 844 + offsets[g]: {i32 group, i32 sno, i32 nameOffset} * counts[g], then the names (NUL-terminated), padded to 4 bytes.
/// Verified on 2.8.0.99920: several groups' blocks hold more than that (counts2 &gt; counts, e.g. group 3 has 0 / 786), so this
/// class keeps every block verbatim and rebuilds only the groups something is added to (which must have counts == counts2).
/// </summary>
public sealed class CoreTocFile
{
    public const int Groups = 70, HeaderSize = Groups * 12 + 4;
    readonly byte[] _raw;
    readonly int[] _counts = new int[Groups], _offsets = new int[Groups], _counts2 = new int[Groups];
    readonly Dictionary<int, List<(int Sno, string Name)>> _rebuilt = new();
    readonly HashSet<int> _allSnos = new();
    public int NextSno { get; private set; }
    public bool Sorted { get; private set; } = true;

    public CoreTocFile(byte[] b)
    {
        _raw = b;
        for (int g = 0; g < Groups; g++)
        {
            _counts[g] = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(g * 4));
            _offsets[g] = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(Groups * 4 + g * 4));
            _counts2[g] = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(Groups * 8 + g * 4));
            foreach (var (sno, _) in Read(g)) _allSnos.Add(sno);
        }
        NextSno = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(Groups * 12));
        var order = Enumerable.Range(0, Groups).OrderBy(g => _offsets[g]).ToList();
        if (!order.SequenceEqual(Enumerable.Range(0, Groups))) throw new InvalidDataException("CoreTOC group blocks are not in group order");
    }

    List<(int Sno, string Name)> Read(int g)
    {
        var list = new List<(int, string)>(_counts[g]);
        int start = HeaderSize + _offsets[g], names = start + _counts[g] * 12, previous = int.MinValue;
        for (int i = 0; i < _counts[g]; i++)
        {
            int e = start + i * 12;
            int sno = BinaryPrimitives.ReadInt32LittleEndian(_raw.AsSpan(e + 4));
            int at = names + BinaryPrimitives.ReadInt32LittleEndian(_raw.AsSpan(e + 8));
            list.Add((sno, Encoding.UTF8.GetString(_raw, at, Array.IndexOf(_raw, (byte)0, at) - at)));
            if (sno < previous) Sorted = false;
            previous = sno;
        }
        return list;
    }

    int BlockEnd(int g) => g + 1 < Groups ? HeaderSize + _offsets[g + 1] : _raw.Length;

    public bool Contains(int sno) => _allSnos.Contains(sno);

    public void Add(int group, int sno, string name)
    {
        if (!_allSnos.Add(sno)) throw new InvalidDataException($"SNO {sno} is already in CoreTOC");
        if (!_rebuilt.TryGetValue(group, out var list))
        {
            if (_counts[group] != _counts2[group]) throw new InvalidDataException($"CoreTOC group {group} has counts {_counts[group]} / {_counts2[group]}; adding to it is not supported");
            _rebuilt[group] = list = Read(group);
        }
        int i = list.FindIndex(e => e.Sno > sno);
        list.Insert(i < 0 ? list.Count : i, (sno, name));
        NextSno = Math.Max(NextSno, sno + 1);
    }

    public byte[] Build()
    {
        using var body = new MemoryStream();
        var offsets = new int[Groups];
        var counts = (int[])_counts.Clone();
        var counts2 = (int[])_counts2.Clone();
        for (int g = 0; g < Groups; g++)
        {
            offsets[g] = (int)body.Length;
            if (!_rebuilt.TryGetValue(g, out var list))
            {
                body.Write(_raw.AsSpan(HeaderSize + _offsets[g], BlockEnd(g) - HeaderSize - _offsets[g]));
                continue;
            }
            var table = new byte[list.Count * 12];
            using var names = new MemoryStream();
            for (int i = 0; i < list.Count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(i * 12), g);
                BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(i * 12 + 4), list[i].Sno);
                BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(i * 12 + 8), (int)names.Length);
                names.Write(Encoding.UTF8.GetBytes(list[i].Name));
                names.WriteByte(0);
            }
            body.Write(table);
            body.Write(names.ToArray());
            while (body.Length % 4 != 0) body.WriteByte(0);
            counts[g] = counts2[g] = list.Count;
        }
        var output = new byte[HeaderSize + body.Length];
        for (int g = 0; g < Groups; g++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(g * 4), counts[g]);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(Groups * 4 + g * 4), offsets[g]);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(Groups * 8 + g * 4), counts2[g]);
        }
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(Groups * 12), NextSno);
        body.ToArray().CopyTo(output, HeaderSize);
        return output;
    }

    /// <summary>Selftest helper: rebuild one group from its parsed entries (as an Add would) without adding anything.</summary>
    public void Touch(int group)
    {
        if (!_rebuilt.ContainsKey(group)) _rebuilt[group] = Read(group);
    }
}
