using System.Buffers.Binary;

namespace D3ModTool.Casc;

/// <summary>
/// One local index bucket file <c>Data\data\BBVVVVVVVV.idx</c> (BB = bucket, V = version; the highest version per bucket is live).
/// <code>
/// @0  u32 header block size, u32 header hash     @8 u16 version(7) u8 bucket u8 extra u8 sizeBytes(4) u8 offsetBytes(5) u8 keyBytes(9) u8 segmentBits(30) u64 maxFileOffset
/// @32 u32 entries size, u32 entries hash (lookup3 hashlittle2 chained over every 18-byte entry, pc of the last)
/// @40 entries sorted by key: key[9], offset[5] big-endian = archive &lt;&lt; 30 | offset, u32 LE size (incl. the 30-byte header); rest zero
/// </code>
/// </summary>
public sealed class LocalIndex
{
    public const int EntriesAt = 40, KeyBytes = 9, EntrySize = 18;

    public readonly record struct Entry(byte[] Key9, int Archive, long Offset, int Size);

    public byte[] Raw { get; }
    public int Bucket => Raw[10];
    public int Count => (int)BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(32)) / EntrySize;

    public LocalIndex(byte[] raw)
    {
        Raw = raw;
        if (raw[12] != 4 || raw[13] != 5 || raw[14] != 9 || raw[15] != 30) throw new InvalidDataException("unexpected .idx field sizes");
    }

    public IEnumerable<Entry> Entries()
    {
        for (int i = 0; i < Count; i++)
        {
            int e = EntriesAt + i * EntrySize;
            ulong packed = 0;
            for (int j = 0; j < 5; j++) packed = (packed << 8) | Raw[e + 9 + j];
            yield return new Entry(Raw.AsSpan(e, 9).ToArray(), (int)(packed >> 30), (long)(packed & 0x3FFFFFFF),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(Raw.AsSpan(e + 14)));
        }
    }

    /// <summary>A copy of this bucket with <paramref name="added"/> merged in (same key = replaced), re-sorted and re-hashed. Same file size.</summary>
    public byte[] With(IEnumerable<Entry> added)
    {
        var all = new SortedDictionary<byte[], byte[]>(Keys.Comparer);
        for (int i = 0; i < Count; i++)
        {
            var e = Raw.AsSpan(EntriesAt + i * EntrySize, EntrySize).ToArray();
            all[e[..9]] = e;
        }
        foreach (var a in added)
        {
            if (Keys.Bucket(a.Key9) != Bucket) throw new InvalidDataException($"key {Keys.Hex(a.Key9)} belongs to bucket {Keys.Bucket(a.Key9)}, not {Bucket}");
            var e = new byte[EntrySize];
            a.Key9.AsSpan(0, 9).CopyTo(e);
            ulong packed = ((ulong)a.Archive << 30) | (ulong)a.Offset;
            for (int j = 0; j < 5; j++) e[9 + j] = (byte)(packed >> (8 * (4 - j)));
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(14), (uint)a.Size);
            all[e[..9]] = e;
        }
        int size = all.Count * EntrySize;
        if (EntriesAt + size > Raw.Length) throw new InvalidDataException($"bucket {Bucket} is full");
        var output = new byte[Raw.Length];
        Raw.AsSpan(0, EntriesAt).CopyTo(output);
        int at = EntriesAt;
        uint pc = 0, pb = 0;
        foreach (var e in all.Values)
        {
            e.CopyTo(output, at);
            (pc, pb) = Lookup3.HashLittle2(e, pc, pb);
            at += EntrySize;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(32), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(36), pc);
        return output;
    }

    /// <summary>The live (highest-version) file of each bucket in a folder: bucket -> file name.</summary>
    public static SortedDictionary<int, string> Latest(string dir)
    {
        var best = new Dictionary<int, (long Version, string Name)>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.idx"))
        {
            var name = Path.GetFileName(path);
            if (name.Length != 14) continue;
            int bucket = Convert.ToInt32(name[..2], 16);
            long version = Convert.ToInt64(name.Substring(2, 8), 16);
            if (!best.TryGetValue(bucket, out var b) || version > b.Version) best[bucket] = (version, name);
        }
        return new SortedDictionary<int, string>(best.ToDictionary(kv => kv.Key, kv => kv.Value.Name));
    }
}
