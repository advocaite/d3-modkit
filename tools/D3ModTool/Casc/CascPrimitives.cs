using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace D3ModTool.Casc;

/// <summary>Bob Jenkins' lookup3 <c>hashlittle2</c>, as CASC uses it for .idx checksums and archive entry headers.</summary>
public static class Lookup3
{
    static uint Rot(uint x, int k) => (x << k) | (x >> (32 - k));

    /// <summary>Returns (c, b) = the "pc"/"pb" outputs; the inputs are the in/out values of the C function.</summary>
    public static (uint C, uint B) HashLittle2(ReadOnlySpan<byte> key, uint pc = 0, uint pb = 0)
    {
        uint a, b, c;
        a = b = c = 0xDEADBEEF + (uint)key.Length + pc;
        c += pb;
        int length = key.Length, i = 0;
        while (length > 12)
        {
            a += BinaryPrimitives.ReadUInt32LittleEndian(key[i..]);
            b += BinaryPrimitives.ReadUInt32LittleEndian(key[(i + 4)..]);
            c += BinaryPrimitives.ReadUInt32LittleEndian(key[(i + 8)..]);
            a -= c; a ^= Rot(c, 4); c += b;
            b -= a; b ^= Rot(a, 6); a += c;
            c -= b; c ^= Rot(b, 8); b += a;
            a -= c; a ^= Rot(c, 16); c += b;
            b -= a; b ^= Rot(a, 19); a += c;
            c -= b; c ^= Rot(b, 4); b += a;
            i += 12;
            length -= 12;
        }
        if (length == 0) return (c, b);
        Span<byte> tail = stackalloc byte[12];
        tail.Clear();
        key.Slice(i, length).CopyTo(tail);
        a += BinaryPrimitives.ReadUInt32LittleEndian(tail);
        b += BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);
        c += BinaryPrimitives.ReadUInt32LittleEndian(tail[8..]);
        c ^= b; c -= Rot(b, 14);
        a ^= c; a -= Rot(c, 11);
        b ^= a; b -= Rot(a, 25);
        c ^= b; c -= Rot(b, 16);
        a ^= c; a -= Rot(c, 4);
        b ^= a; b -= Rot(a, 14);
        c ^= b; c -= Rot(b, 24);
        return (c, b);
    }
}

public static class Keys
{
    public static byte[] Md5(ReadOnlySpan<byte> data) => MD5.HashData(data);
    public static string Hex(ReadOnlySpan<byte> key) => Convert.ToHexString(key).ToLowerInvariant();
    public static byte[] Parse(string hex) => Convert.FromHexString(hex.Trim());

    /// <summary>Local index bucket of an encoding key: XOR of its first 9 bytes, folded to 4 bits.</summary>
    public static int Bucket(ReadOnlySpan<byte> ekey)
    {
        int x = 0;
        for (int i = 0; i < 9; i++) x ^= ekey[i];
        return (x & 0xF) ^ (x >> 4);
    }

    public static int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    public static readonly Comparer<byte[]> Comparer = Comparer<byte[]>.Create(Compare);
}

/// <summary>BLTE: Blizzard's chunked container. We decode N (plain) and Z (zlib) chunks and encode zlib 256 KiB chunks.</summary>
public static class Blte
{
    public const int ChunkSize = 256 * 1024;
    public const string ESpec = "b:256K*=z";

    public static byte[] Decode(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(blob) != 0x424C5445) throw new InvalidDataException("not BLTE");
        int headerSize = (int)BinaryPrimitives.ReadUInt32BigEndian(blob[4..]);
        var chunks = new List<int>();
        int at;
        if (headerSize == 0) { chunks.Add(blob.Length - 8); at = 8; }
        else
        {
            int n = (blob[9] << 16) | (blob[10] << 8) | blob[11];
            for (int i = 0; i < n; i++) chunks.Add((int)BinaryPrimitives.ReadUInt32BigEndian(blob[(12 + i * 24)..]));
            at = headerSize;
        }
        using var output = new MemoryStream();
        foreach (var size in chunks)
        {
            var chunk = blob.Slice(at, size);
            at += size;
            switch ((char)chunk[0])
            {
                case 'N': output.Write(chunk[1..]); break;
                case 'Z':
                    using (var z = new ZLibStream(new MemoryStream(chunk[1..].ToArray()), CompressionMode.Decompress)) z.CopyTo(output);
                    break;
                default: throw new InvalidDataException($"BLTE chunk mode '{(char)chunk[0]}' is not supported");
            }
        }
        return output.ToArray();
    }

    /// <summary>Encodes as zlib chunks of 256 KiB (espec <c>b:256K*=z</c>). The encoding key is the MD5 of the BLTE header.</summary>
    public static (byte[] Blob, byte[] EKey) Encode(byte[] data)
    {
        int count = Math.Max(1, (data.Length + ChunkSize - 1) / ChunkSize);
        var plain = new byte[count][];
        var packed = new byte[count][];
        Parallel.For(0, count, i =>
        {
            int start = i * ChunkSize;
            plain[i] = data.AsSpan(start, Math.Min(ChunkSize, data.Length - start)).ToArray();
            using var ms = new MemoryStream();
            ms.WriteByte((byte)'Z');
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(plain[i]);
            packed[i] = ms.ToArray();
        });
        int headerSize = 12 + 24 * count;
        var blob = new byte[headerSize + packed.Sum(p => p.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(blob, 0x424C5445);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4), (uint)headerSize);
        blob[8] = 0x0F;
        blob[9] = (byte)(count >> 16); blob[10] = (byte)(count >> 8); blob[11] = (byte)count;
        int at = headerSize;
        for (int i = 0; i < count; i++)
        {
            int e = 12 + i * 24;
            BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(e), (uint)packed[i].Length);
            BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(e + 4), (uint)plain[i].Length);
            Keys.Md5(packed[i]).CopyTo(blob, e + 8);
            packed[i].CopyTo(blob, at);
            at += packed[i].Length;
        }
        return (blob, Keys.Md5(blob.AsSpan(0, headerSize)));
    }
}

/// <summary>
/// The 30-byte header in front of every file in a local <c>data.###</c> archive:
/// ekey reversed (16), u32 LE total size incl. header, u16 flags, u32 LE lookup3 checksum of bytes 0..21 (seed 0x3D6BE971),
/// u32 checksum of (header bytes XOR-folded) XOR (encoded archive offset).
/// </summary>
public static class ArchiveEntryHeader
{
    public const int Size = 30;

    static readonly uint[] OffsetMix =
    {
        0x049396B8, 0x72A82A9B, 0xEE626CCA, 0x9917754F, 0x15DE40B1, 0xF5A8A9B6, 0x421EAC7E, 0xA9D55C9A,
        0x317FD40C, 0x04FAF80D, 0x3D6BE971, 0x52933CFD, 0x27F64B7D, 0xC6F5C11B, 0xD5757E3A, 0x6C388745,
    };

    /// <summary>
    /// Verified 2026-11-10 against 653 real headers of 2.8.0.99920 (all match): the XOR-fold lanes are aligned to the byte's
    /// ABSOLUTE position in the archive (offset + i), and the mixed value is computed from the encoded offset + 30.
    /// </summary>
    public static byte[] Make(ReadOnlySpan<byte> ekey, int blobLength, int archive, long offset)
    {
        var h = new byte[Size];
        for (int i = 0; i < 16; i++) h[i] = ekey[15 - i];
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), (uint)(Size + blobLength));
        return Rechecksum(h, archive, offset);
    }

    /// <summary>Recomputes both checksums (bytes 22..29) from bytes 0..21 for a header stored at (archive, offset).</summary>
    public static byte[] Rechecksum(byte[] h, int archive, long offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(22), Lookup3.HashLittle2(h.AsSpan(0, 22), 0x3D6BE971).C);
        ulong encodedOffset = (((ulong)archive << 30) | (ulong)offset) + Size;
        uint mixed = OffsetMix[encodedOffset & 0xF] ^ (uint)encodedOffset;
        Span<byte> fold = stackalloc byte[4];
        fold.Clear();
        for (int i = 0; i < 26; i++) fold[(int)((offset + i) & 3)] ^= h[i];
        Span<byte> mix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(mix, mixed);
        for (int i = 0; i < 4; i++)
        {
            int lane = (int)((offset + 26 + i) & 3);
            h[26 + i] = (byte)(fold[lane] ^ mix[lane]);
        }
        return h;
    }
}
