using System.Buffers.Binary;

namespace D3Casc;

/// <summary>
/// <c>Base\Data_D3\PC\Misc\Packages.dat</c>: <c>int32 magic (0xAABB0002); int32 count; count x cstring fileName</c>.
/// It lists full file names with extension and is used to give the sub-files ("{Name}\0001.xxx") their extension.
/// </summary>
public sealed class PackagesDat
{
    public uint Magic { get; }
    public IReadOnlyList<string> Names { get; }
    public int TrailingBytes { get; }
    readonly Dictionary<string, string> _extByStem = new(StringComparer.OrdinalIgnoreCase);

    PackagesDat(uint magic, List<string> names, int trailing)
    {
        Magic = magic;
        Names = names;
        TrailingBytes = trailing;
        foreach (var n in names)
        {
            string ext = Path.GetExtension(n);
            string stem = ext.Length > 0 ? n[..^ext.Length] : n;
            _extByStem.TryAdd(stem, ext);
        }
    }

    public static PackagesDat Parse(byte[] data)
    {
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        var names = new List<string>(Math.Max(0, count));
        int pos = 8;
        for (int i = 0; i < count && pos < data.Length; i++)
        {
            string s = CoreToc.ReadCString(data, pos);
            pos += s.Length + 1;
            names.Add(s);
        }
        return new PackagesDat(magic, names, data.Length - pos);
    }

    /// <summary>Extension (with dot) for a path without extension, or null.</summary>
    public string? ExtensionFor(string stem) => _extByStem.TryGetValue(stem, out var e) ? e : null;
}
