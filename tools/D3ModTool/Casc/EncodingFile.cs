using System.Buffers.Binary;
using System.Text;

namespace D3ModTool.Casc;

/// <summary>
/// The CASC encoding file: content key -> encoding key(s), and encoding key -> espec + encoded size.
/// <code>
/// "EN" u8 version u8 ckeySize u8 ekeySize u16BE cePageKB u16BE ekPageKB u32BE cePages u32BE ekPages u8 unk u32BE especBlockSize
/// espec strings (NUL-terminated) | CE page index {firstKey, md5(page)} | CE pages | EK page index | EK pages | trailer (the file's own espec)
/// CE record: u8 keyCount, u40BE contentSize, ckey, ekey * keyCount       (a page ends at keyCount 0 / zero padding)
/// EK record: ekey, u32BE especIndex, u40BE encodedSize                    (a page ends at an all-zero ekey)
/// </code>
/// Pages are rebuilt greedily from the sorted records; <see cref="Build"/> on an unmodified file must reproduce it byte for byte (selftest).
/// </summary>
public sealed class EncodingFile
{
    readonly byte[] _header;            // the 22 fixed bytes
    readonly List<string> _especs;
    readonly int _ck, _ek, _cePage, _ekPage;
    readonly SortedDictionary<byte[], byte[]> _ce = new(Keys.Comparer);   // ckey -> whole record
    readonly SortedDictionary<byte[], byte[]> _ekr = new(Keys.Comparer);  // ekey -> whole record
    readonly List<byte[]> _ekTerminators = new();                         // records with especIndex 0xFFFFFFFF, kept in place at the end
    readonly byte[] _trailer;

    public int ContentKeys => _ce.Count;

    public EncodingFile(byte[] b)
    {
        if (b[0] != 'E' || b[1] != 'N') throw new InvalidDataException("not an encoding file");
        _header = b[..22];
        _ck = b[3]; _ek = b[4];
        _cePage = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(5)) * 1024;
        _ekPage = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(7)) * 1024;
        int cePages = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(9));
        int ekPages = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(13));
        int especSize = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(18));
        _especs = Encoding.ASCII.GetString(b, 22, especSize).Split('\0').SkipLast(1).ToList();
        int at = 22 + especSize + cePages * (_ck + 16);
        for (int p = 0; p < cePages; p++, at += _cePage)
            for (int i = 0; i + 6 + _ck <= _cePage && b[at + i] != 0;)
            {
                int len = 6 + _ck + _ek * b[at + i];
                var rec = b.AsSpan(at + i, len).ToArray();
                _ce[rec[6..(6 + _ck)]] = rec;
                i += len;
            }
        at += ekPages * (_ek + 16);
        int ekLen = _ek + 9;
        for (int p = 0; p < ekPages; p++, at += _ekPage)
            for (int i = 0; i + ekLen <= _ekPage; i += ekLen)
            {
                var rec = b.AsSpan(at + i, ekLen).ToArray();
                if (rec.AsSpan(0, _ek).IndexOfAnyExcept((byte)0) < 0 && rec.AsSpan(_ek).IndexOfAnyExcept((byte)0) < 0) break;
                if (BinaryPrimitives.ReadUInt32BigEndian(rec.AsSpan(_ek)) == 0xFFFFFFFF) { _ekTerminators.Add(rec); continue; }
                _ekr[rec[.._ek]] = rec;
            }
        _trailer = b[at..];
    }

    public (byte[] EKey, long Size)? Lookup(byte[] ckey)
    {
        if (!_ce.TryGetValue(ckey, out var r)) return null;
        long size = 0;
        for (int i = 1; i < 6; i++) size = (size << 8) | r[i];
        return (r[(6 + _ck)..(6 + _ck + _ek)], size);
    }

    /// <summary>Adds or replaces ckey -> ekey (one encoding key) and the ekey's espec/encoded size.</summary>
    public void Add(byte[] ckey, byte[] ekey, long size, long encodedSize, string espec)
    {
        var ce = new byte[6 + _ck + _ek];
        ce[0] = 1;
        for (int i = 0; i < 5; i++) ce[1 + i] = (byte)(size >> (8 * (4 - i)));
        ckey.CopyTo(ce, 6);
        ekey.CopyTo(ce, 6 + _ck);
        _ce[ckey] = ce;
        int especIndex = _especs.IndexOf(espec);
        if (especIndex < 0) { _especs.Add(espec); especIndex = _especs.Count - 1; }
        var ek = new byte[_ek + 9];
        ekey.CopyTo(ek, 0);
        BinaryPrimitives.WriteUInt32BigEndian(ek.AsSpan(_ek), (uint)especIndex);
        for (int i = 0; i < 5; i++) ek[_ek + 4 + i] = (byte)(encodedSize >> (8 * (4 - i)));
        _ekr[ekey] = ek;
    }

    static (List<byte[]> Pages, byte[] Index) Pack(IEnumerable<byte[]> records, int pageSize, int keyOffset, int keyLength)
    {
        var pages = new List<byte[]>();
        var firsts = new List<byte[]>();
        byte[]? page = null;
        int used = 0;
        foreach (var r in records)
        {
            if (page == null || used + r.Length > pageSize)
            {
                page = new byte[pageSize];
                pages.Add(page);
                firsts.Add(r.AsSpan(keyOffset, keyLength).ToArray());
                used = 0;
            }
            r.CopyTo(page, used);
            used += r.Length;
        }
        using var index = new MemoryStream();
        for (int i = 0; i < pages.Count; i++) { index.Write(firsts[i]); index.Write(Keys.Md5(pages[i])); }
        return (pages, index.ToArray());
    }

    public byte[] Build()
    {
        var (cePages, ceIndex) = Pack(_ce.Values, _cePage, 6, _ck);
        var (ekPages, ekIndex) = Pack(_ekr.Values.Concat(_ekTerminators), _ekPage, 0, _ek);
        var especs = Encoding.ASCII.GetBytes(string.Concat(_especs.Select(s => s + "\0")));
        var header = (byte[])_header.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(9), (uint)cePages.Count);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(13), (uint)ekPages.Count);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(18), (uint)especs.Length);
        using var ms = new MemoryStream();
        ms.Write(header);
        ms.Write(especs);
        ms.Write(ceIndex);
        foreach (var p in cePages) ms.Write(p);
        ms.Write(ekIndex);
        foreach (var p in ekPages) ms.Write(p);
        ms.Write(_trailer);
        return ms.ToArray();
    }
}
