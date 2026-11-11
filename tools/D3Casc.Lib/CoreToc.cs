using System.Buffers.Binary;
using System.Text;

namespace D3Casc;

/// <summary>
/// <c>Base\CoreTOC.dat</c>: the master directory (group, sno id) -> name.
/// Layout (2.x client; the 1.0.x layout the emulator's unused <c>Data.LoadCatalog</c> parses is different):
/// <code>
/// int32 entryCounts[N]; int32 entryOffsets[N]; int32 entryUnkCounts[N]; int32 unk;   // header, N = number of SNO groups (70 in CASCLib)
/// per group i, at header + entryOffsets[i]:
///     entryCounts[i] x { int32 group; int32 snoId; int32 nameOffset; }
///     string table; name = cstring at (entries end + nameOffset)
/// </code>
/// N is detected instead of hard-coded: it is the header size for which every non-empty group's first entry carries its own group id.
/// </summary>
public sealed class CoreToc
{
    public readonly record struct Entry(int Group, int Id, string Name);

    public int GroupSlots { get; }
    public IReadOnlyList<Entry> Entries { get; }
    readonly Dictionary<(int, int), string> _names;
    readonly Dictionary<int, List<Entry>> _byGroup;

    CoreToc(int groupSlots, List<Entry> entries)
    {
        GroupSlots = groupSlots;
        Entries = entries;
        _names = new Dictionary<(int, int), string>(entries.Count);
        foreach (var e in entries) _names.TryAdd((e.Group, e.Id), e.Name);
        _byGroup = entries.GroupBy(e => e.Group).ToDictionary(g => g.Key, g => g.ToList());
    }

    public bool TryGetName(int group, int id, out string name) => _names.TryGetValue((group, id), out name!);

    public IReadOnlyList<Entry> InGroup(int group) => _byGroup.TryGetValue(group, out var l) ? l : Array.Empty<Entry>();

    public IEnumerable<int> Groups => _byGroup.Keys.OrderBy(k => k);

    public static CoreToc Parse(byte[] data)
    {
        int n = DetectGroupSlots(data);
        int header = 4 * (3 * n + 1);
        var entries = new List<Entry>();
        for (int i = 0; i < n; i++)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4 * i));
            if (count == 0) continue;
            int start = header + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4 * (n + i)));
            int strings = start + 12 * count;
            for (int j = 0; j < count; j++)
            {
                var e = data.AsSpan(start + 12 * j);
                int group = BinaryPrimitives.ReadInt32LittleEndian(e);
                int id = BinaryPrimitives.ReadInt32LittleEndian(e[4..]);
                int nameOff = BinaryPrimitives.ReadInt32LittleEndian(e[8..]);
                entries.Add(new Entry(group, id, ReadCString(data, strings + nameOff)));
            }
        }
        return new CoreToc(n, entries);
    }

    static int DetectGroupSlots(byte[] data)
    {
        for (int n = 48; n <= 128; n++)
        {
            int header = 4 * (3 * n + 1);
            if (header > data.Length) break;
            bool ok = true;
            int nonEmpty = 0;
            for (int i = 0; i < n && ok; i++)
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4 * i));
                int offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4 * (n + i)));
                if (count < 0 || offset < 0) { ok = false; break; }
                if (count == 0) continue;
                long first = header + (long)offset;
                if (first + 12L * count > data.Length) { ok = false; break; }
                if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)first)) != i) ok = false;
                nonEmpty++;
            }
            if (ok && nonEmpty >= 10) return n;
        }
        throw new InvalidDataException("CoreTOC.dat: could not detect the group-table size (unexpected layout)");
    }

    internal static string ReadCString(byte[] data, int offset)
    {
        int end = Array.IndexOf(data, (byte)0, offset);
        if (end < 0) end = data.Length;
        return Encoding.ASCII.GetString(data, offset, end - offset);
    }
}
