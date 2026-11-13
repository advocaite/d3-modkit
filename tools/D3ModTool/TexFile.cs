using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using D3Casc;

namespace D3ModTool;

/// <summary>
/// Texture SNO (group 44, <c>.tex</c>), format version 47 in 2.8.0.99920. Verified 2026-11-11 over all 33,431 stock textures
/// (FILE offsets; the exe's in-memory struct is the same minus 16, e.g. frame count = <c>[tex+0x208]</c>, frame pointer <c>[tex+0x218]</c>):
/// <code>
/// @0x000 u32 0xDEADBEEF  @0x004 i32 format version (47 in all)  @0x010 i32 SNO id   (rest of 0x00-0x1F zero)
/// @0x020 i32 pixel format (<see cref="PixelFormat"/>)  @0x024 i32 width  @0x028 i32 height
/// @0x02C i32 face count (1; 6 = cube map, never in UI)  @0x030 i32 first mip (0 in all)  @0x034 i32 last mip (mip count = last + 1)
/// @0x038 60 x {i32 offset (+16), i32 size}: mip table, mip 0 first, unused slots zero (cube maps: one entry per face)
/// @0x218 i32 frame count (>= 1)  @0x21C {i32 offset (+16), i32 size = count * 80}: frame array  @0x228 u64 0 (runtime pointer)
/// @0x230 i32 = @0x234 i32 (nonzero in 20 textures)  @0x238 i32 (0/1/2/4/8)  @0x23C i32 (8 in 92 %)  @0x240 i32 (7 in 92 %)  rest zero
/// @0x268 {i32 offset = 0x270 (+16), i32 size}: frame index list, i32 0..n-1 (all but 2DInventoryRunes, which lists only 0)
/// @0x270 i32 (0; 1 in 34)  @0x274 i32 flags (0x20/0x21/0x220/0x230 on UI textures)
/// @0x278 u32 content hash: textures with identical art share it (e.g. 14 blank minimap tiles); not CRC32/MD5/FNV of the stored bytes
/// @0x280 frame index list, padded to 8; frame array; mip data (mip 0 first); the last mip ends at the end of the file.
/// Frame (80 B): char name[64] (NUL-terminated, case-sensitive), float u0, v0, u1, v1 (0..1 of mip 0; u * width is an integer in all 54,392 frames).
/// </code>
/// A texture without named frames still has one frame with an empty name, (0,0)-(1,1) or (0,0)-(0.75,0.75) etc.
/// <para><b>Block-compressed mips are stored PLANAR</b> (verified visually on DXT1/DXT3/DXT5 UI art): instead of interleaved
/// 8/16-byte blocks, each mip holds every block's first field, then every block's second field, ... (<see cref="Planes"/>):
/// DXT1 = color endpoints (4 B) | color indices (4 B); DXT3 = alpha (8 B) | endpoints | indices;
/// DXT5 = alpha endpoints (2 B) | alpha indices (6 B) | color endpoints | color indices. Uncompressed formats are interleaved as usual.</para>
/// </summary>
public sealed class TexFile
{
    public const uint Magic = 0xDEADBEEF;
    public const int Version280 = 47;
    public const int HeaderSize = 0x280, FrameSize = 80, FrameNameSize = 64, MipSlots = 60;
    public const int FormatAt = 0x20, WidthAt = 0x24, HeightAt = 0x28, FaceCountAt = 0x2C, MipFirstAt = 0x30, MipLastAt = 0x34, MipTableAt = 0x38;
    public const int FrameCountAt = 0x218, FrameArrayAt = 0x21C, FrameIndexArrayAt = 0x268, FlagsAt = 0x274, HashAt = 0x278;

    /// <summary>
    /// Pixel formats by the value @0x20. Every stock mip table entry matches <see cref="MipByteSize"/> (cube maps per face).
    /// Counts: all 33,431 textures / the 389 <c>2DUI*</c> ones.
    /// </summary>
    public enum PixelFormat
    {
        /// <summary>32 bpp, bytes B,G,R,A, interleaved (277 / 4). Verified visually.</summary>
        Bgra8 = 0,
        /// <summary>16 bpp single channel (2 / 0: <c>2DInventoryRunes</c>, <c>2DInventorySockets</c>): the high byte is a smooth gray
        /// image, the low byte looks like its low bits, so 16-bit luminance (guess); decoded as gray = high byte.</summary>
        L16 = 4,
        /// <summary>8 bpp single channel (4 / 1: <c>2DUIHelp</c>, a flat 0x40 panel with a 0x80 border). L8 or A8 is not known;
        /// decoded as opaque gray.</summary>
        L8 = 7,
        /// <summary>BC1 / DXT1, 8 B per 4x4 block, planar (12,364 / 37). Verified visually.</summary>
        Dxt1 = 9,
        /// <summary>BC1 / DXT1 as well, planar (1,536 / 4); presumably the variant whose 1-bit alpha is used. Verified visually.</summary>
        Dxt1Alpha = 10,
        /// <summary>BC2 / DXT3, 16 B per block, explicit 4-bit alpha, planar (9,162 / 118). Verified visually.</summary>
        Dxt3 = 11,
        /// <summary>BC3 / DXT5, 16 B per block, interpolated alpha, planar (9,250 / 225). Verified visually.</summary>
        Dxt5 = 12,
        /// <summary>8 bpp single channel, 1 texture (<c>x1_Crusader_Trebuchet_GrenadeCross</c>, a white cross on black); decoded like <see cref="L8"/>.</summary>
        L8b = 23,
        /// <summary>DXT5 storage used by the 835 <c>*_NM</c> normal maps (0 in UI): color is a near-constant orange, the detail is in
        /// alpha (swizzled normal). Decoded as plain DXT5 channels.</summary>
        Dxt5Normal = 43,
    }

    /// <summary>A named sub-rectangle ("frame") of the texture, in UV and in pixels of mip 0.</summary>
    public readonly record struct Frame(int Index, string Name, float U0, float V0, float U1, float V1)
    {
        public (int X, int Y, int W, int H) PixelRect(int texWidth, int texHeight)
        {
            int x0 = (int)MathF.Round(U0 * texWidth), y0 = (int)MathF.Round(V0 * texHeight);
            int x1 = (int)MathF.Round(U1 * texWidth), y1 = (int)MathF.Round(V1 * texHeight);
            return (x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }
    }

    /// <summary>One mip table entry: file offset (already +16), byte size (of one face), size in pixels.</summary>
    public readonly record struct Mip(int Offset, int Size, int Width, int Height);

    public byte[] Bytes { get; }
    public int SnoId => I32(0x10);
    public int Version => I32(4);
    public PixelFormat Format => (PixelFormat)I32(FormatAt);
    public int Width => I32(WidthAt);
    public int Height => I32(HeightAt);
    public int FaceCount => I32(FaceCountAt);
    public int MipCount => I32(MipLastAt) - I32(MipFirstAt) + 1;
    public uint Flags => (uint)I32(FlagsAt);
    public uint ContentHash => (uint)I32(HashAt);
    public IReadOnlyList<Frame> Frames { get; }
    /// <summary>Mips of face 0 (cube maps: the table order of the other faces was not checked).</summary>
    public IReadOnlyList<Mip> Mips { get; }

    public TexFile(byte[] bytes)
    {
        if (bytes.Length < HeaderSize) throw new InvalidDataException($"texture is {bytes.Length} bytes, < {HeaderSize}");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic) throw new InvalidDataException("bad magic");
        Bytes = bytes;

        var mips = new List<Mip>();
        for (int i = 0; i < Math.Clamp(MipCount, 0, MipSlots); i++)
        {
            int off = I32(MipTableAt + 8 * i), size = I32(MipTableAt + 8 * i + 4);
            if (size <= 0 || off + 16 + (long)size > bytes.Length) break;
            mips.Add(new Mip(off + 16, size, Math.Max(1, Width >> i), Math.Max(1, Height >> i)));
        }
        Mips = mips;

        var frames = new List<Frame>();
        int count = I32(FrameCountAt), at = I32(FrameArrayAt) + 16;
        if (count > 0 && at >= HeaderSize && at + (long)count * FrameSize <= bytes.Length)
            for (int i = 0; i < count; i++)
            {
                int r = at + i * FrameSize;
                int n = bytes.AsSpan(r, FrameNameSize).IndexOf((byte)0);
                frames.Add(new Frame(i, Encoding.ASCII.GetString(bytes, r, n < 0 ? FrameNameSize : n),
                    F32(r + 64), F32(r + 68), F32(r + 72), F32(r + 76)));
            }
        Frames = frames;
    }

    int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(at));
    float F32(int at) => BinaryPrimitives.ReadSingleLittleEndian(Bytes.AsSpan(at));

    public Frame? FindFrame(string name)
    {
        foreach (var f in Frames) if (f.Name == name) return f;
        return null;
    }

    /// <summary>Bytes one face of a mip takes in <paramref name="format"/> (-1 = unknown format); matches every stock mip table entry.</summary>
    public static int MipByteSize(PixelFormat format, int w, int h)
    {
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        return format switch
        {
            PixelFormat.Bgra8 => w * h * 4,
            PixelFormat.L16 => w * h * 2,
            PixelFormat.L8 or PixelFormat.L8b => w * h,
            PixelFormat.Dxt1 or PixelFormat.Dxt1Alpha => bw * bh * 8,
            PixelFormat.Dxt3 or PixelFormat.Dxt5 or PixelFormat.Dxt5Normal => bw * bh * 16,
            _ => -1,
        };
    }

    public static bool CanDecode(PixelFormat f) => MipByteSize(f, 4, 4) > 0;

    /// <summary>Per-block field sizes of the planar storage, in storage order (see the class summary).</summary>
    public static int[] Planes(PixelFormat f) => f switch
    {
        PixelFormat.Dxt1 or PixelFormat.Dxt1Alpha => new[] { 4, 4 },
        PixelFormat.Dxt3 => new[] { 8, 4, 4 },
        PixelFormat.Dxt5 or PixelFormat.Dxt5Normal => new[] { 2, 6, 4, 4 },
        _ => Array.Empty<int>(),
    };

    /// <summary>Planar storage -> standard interleaved BCn blocks (what a GPU / DDS file expects).</summary>
    public static byte[] Interleave(PixelFormat f, ReadOnlySpan<byte> planar) => Shuffle(Planes(f), planar, toPlanar: false);

    /// <summary>Standard interleaved BCn blocks -> the planar storage a .tex mip needs (for writing new textures).</summary>
    public static byte[] Deinterleave(PixelFormat f, ReadOnlySpan<byte> blocks) => Shuffle(Planes(f), blocks, toPlanar: true);

    static byte[] Shuffle(int[] planes, ReadOnlySpan<byte> src, bool toPlanar)
    {
        if (planes.Length == 0) return src.ToArray();
        int bs = planes.Sum(), n = src.Length / bs;
        var dst = new byte[src.Length];
        int planeStart = 0, inBlock = 0;
        foreach (int p in planes)
        {
            for (int i = 0; i < n; i++)
            {
                int planar = planeStart + i * p, block = i * bs + inBlock;
                if (toPlanar) src.Slice(block, p).CopyTo(dst.AsSpan(planar, p));
                else src.Slice(planar, p).CopyTo(dst.AsSpan(block, p));
            }
            planeStart += n * p; inBlock += p;
        }
        return dst;
    }

    /// <summary>Decodes a mip (face 0) to RGBA8 (row-major, 4 bytes per pixel, straight alpha).</summary>
    public byte[] DecodeMip(int level = 0)
    {
        var m = Mips[level];
        return Decode(Format, Bytes.AsSpan(m.Offset, m.Size), m.Width, m.Height);
    }

    /// <summary>RGBA8 pixels of a frame, cut from mip 0 (pass <paramref name="decodedMip0"/> to reuse a decoded mip).</summary>
    public (byte[] Rgba, int Width, int Height) DecodeFrame(Frame frame, byte[]? decodedMip0 = null)
    {
        var rgba = decodedMip0 ?? DecodeMip(0);
        var (x, y, w, h) = frame.PixelRect(Width, Height);
        x = Math.Clamp(x, 0, Width); y = Math.Clamp(y, 0, Height);
        w = Math.Min(w, Width - x); h = Math.Min(h, Height - y);
        var o = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(rgba, ((y + row) * Width + x) * 4, o, row * w * 4, w * 4);
        return (o, w, h);
    }

    /// <summary>Decodes one stored mip (planar for BCn, as in the file) to RGBA8.</summary>
    public static byte[] Decode(PixelFormat format, ReadOnlySpan<byte> src, int w, int h)
    {
        int need = MipByteSize(format, w, h);
        if (need < 0) throw new NotSupportedException($"texture pixel format {(int)format}");
        if (src.Length < need) throw new InvalidDataException($"mip is {src.Length} bytes, format {format} {w}x{h} needs {need}");
        var dst = new byte[w * h * 4];
        switch (format)
        {
            case PixelFormat.Bgra8:
                for (int i = 0; i < w * h; i++)
                {
                    dst[i * 4] = src[i * 4 + 2]; dst[i * 4 + 1] = src[i * 4 + 1]; dst[i * 4 + 2] = src[i * 4]; dst[i * 4 + 3] = src[i * 4 + 3];
                }
                break;
            case PixelFormat.L16:
                for (int i = 0; i < w * h; i++) { dst[i * 4] = dst[i * 4 + 1] = dst[i * 4 + 2] = src[i * 2 + 1]; dst[i * 4 + 3] = 255; }
                break;
            case PixelFormat.L8:
            case PixelFormat.L8b:
                for (int i = 0; i < w * h; i++) { dst[i * 4] = dst[i * 4 + 1] = dst[i * 4 + 2] = src[i]; dst[i * 4 + 3] = 255; }
                break;
            default:
                DecodeBlocks(format, Interleave(format, src[..need]), w, h, dst);
                break;
        }
        return dst;
    }

    static void DecodeBlocks(PixelFormat format, ReadOnlySpan<byte> src, int w, int h, byte[] dst)
    {
        bool bc1 = format is PixelFormat.Dxt1 or PixelFormat.Dxt1Alpha;
        int blockBytes = bc1 ? 8 : 16;
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        Span<byte> px = stackalloc byte[64];  // 16 texels, RGBA
        Span<byte> a = stackalloc byte[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var blk = src.Slice((by * bw + bx) * blockBytes, blockBytes);
                if (bc1) ColorBlock(blk, px, punchThrough: true);
                else
                {
                    ColorBlock(blk[8..], px, punchThrough: false);
                    if (format == PixelFormat.Dxt3)
                        for (int i = 0; i < 16; i++) px[i * 4 + 3] = (byte)(((blk[i / 2] >> (4 * (i & 1))) & 15) * 17);
                    else
                    {
                        AlphaBlock(blk, a);
                        for (int i = 0; i < 16; i++) px[i * 4 + 3] = a[i];
                    }
                }
                for (int ty = 0; ty < 4; ty++)
                {
                    int y = by * 4 + ty;
                    if (y >= h) break;
                    for (int tx = 0; tx < 4; tx++)
                    {
                        int x = bx * 4 + tx;
                        if (x >= w) break;
                        px.Slice((ty * 4 + tx) * 4, 4).CopyTo(dst.AsSpan((y * w + x) * 4, 4));
                    }
                }
            }
    }

    /// <summary>BC1 color part: two RGB565 endpoints + 2-bit indices; for BC1 with c0 &lt;= c1, index 3 is transparent black.</summary>
    static void ColorBlock(ReadOnlySpan<byte> b, Span<byte> px, bool punchThrough)
    {
        int c0 = b[0] | b[1] << 8, c1 = b[2] | b[3] << 8;
        Span<int> r = stackalloc int[4], g = stackalloc int[4], bl = stackalloc int[4], al = stackalloc int[4];
        Rgb565(c0, out r[0], out g[0], out bl[0]);
        Rgb565(c1, out r[1], out g[1], out bl[1]);
        al[0] = al[1] = al[2] = al[3] = 255;
        if (c0 > c1 || !punchThrough)
        {
            r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; bl[2] = (2 * bl[0] + bl[1]) / 3;
            r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; bl[3] = (bl[0] + 2 * bl[1]) / 3;
        }
        else
        {
            r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; bl[2] = (bl[0] + bl[1]) / 2;
            r[3] = g[3] = bl[3] = al[3] = 0;
        }
        uint idx = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        for (int i = 0; i < 16; i++)
        {
            int k = (int)(idx >> (2 * i)) & 3;
            px[i * 4] = (byte)r[k]; px[i * 4 + 1] = (byte)g[k]; px[i * 4 + 2] = (byte)bl[k]; px[i * 4 + 3] = (byte)al[k];
        }
    }

    static void Rgb565(int c, out int r, out int g, out int b)
    {
        r = (c >> 11) & 31; g = (c >> 5) & 63; b = c & 31;
        r = (r << 3) | (r >> 2); g = (g << 2) | (g >> 4); b = (b << 3) | (b >> 2);
    }

    /// <summary>BC3 alpha block: two 8-bit endpoints + 16 x 3-bit indices.</summary>
    static void AlphaBlock(ReadOnlySpan<byte> b, Span<byte> a)
    {
        int a0 = b[0], a1 = b[1];
        Span<int> v = stackalloc int[8];
        v[0] = a0; v[1] = a1;
        if (a0 > a1) for (int i = 1; i < 7; i++) v[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else
        {
            for (int i = 1; i < 5; i++) v[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            v[6] = 0; v[7] = 255;
        }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) a[i] = (byte)v[(int)(bits >> (3 * i)) & 7];
    }

    /// <summary>
    /// The hash the client keys its frame tables with (exe 2.8.0.99920 @0x17DA10): h = h * 33 + (signed char), from 0, case-sensitive.
    /// The lookup compares ONLY this hash (no string compare), so frame names are case-sensitive; when two frames of one namespace
    /// hash alike the second is dropped with "Frame '%s' in (%s) already exists in (%s)". New frames must not clash with stock ones.
    /// </summary>
    public static uint FrameHash(string name)
    {
        uint h = 0;
        foreach (byte c in Encoding.ASCII.GetBytes(name)) h = unchecked(h * 33 + (uint)(sbyte)c);
        return h;
    }

    /// <summary>
    /// Builds a new single-mip BGRA8 (format 0, uncompressed, so no planar shuffle) texture with the stock layout.
    /// NOT yet loaded by the client (2026-11-11): the fields whose meaning is unknown (@0x230-0x27F: 0x238-0x240, flags, content hash)
    /// are copied from <paramref name="template"/> (use a stock 2DUI texture) and the content hash is then replaced by a CRC32 of the
    /// pixels so two different textures never share one (guess: identical art shares the stock hash).
    /// </summary>
    public static byte[] CreateBgra8(int snoId, int width, int height, ReadOnlySpan<byte> rgba,
        IReadOnlyList<(string Name, int X, int Y, int W, int H)> frames, TexFile? template = null)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("rgba size != width*height*4");
        var bgra = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            bgra[i * 4] = rgba[i * 4 + 2]; bgra[i * 4 + 1] = rgba[i * 4 + 1];
            bgra[i * 4 + 2] = rgba[i * 4]; bgra[i * 4 + 3] = rgba[i * 4 + 3];
        }
        return Create(snoId, PixelFormat.Bgra8, width, height, new[] { bgra }, frames, template, Png.Crc32(rgba));
    }

    /// <summary>
    /// Builds a texture SNO with the stock layout (see the class summary) from mips that are already in the stored form: BGRA8
    /// bytes for <see cref="PixelFormat.Bgra8"/>, PLANAR blocks for DXT (use <see cref="Deinterleave"/> on standard BCn blocks).
    /// Mip i must be <see cref="MipByteSize"/>(w >> i, h >> i) bytes. 0x230-0x27F (meaning unknown) come from <paramref name="template"/>
    /// (a stock texture of the same use); 0x278 is then set to <paramref name="contentHash"/> (stock: identical art shares the value,
    /// so pass something unique, e.g. a CRC32 of the source pixels).
    /// </summary>
    public static byte[] Create(int snoId, PixelFormat format, int width, int height, IReadOnlyList<byte[]> mips,
        IReadOnlyList<(string Name, int X, int Y, int W, int H)> frames, TexFile? template, uint contentHash)
    {
        if (mips.Count == 0 || mips.Count > MipSlots) throw new ArgumentException($"{mips.Count} mips (1..{MipSlots})");
        for (int i = 0; i < mips.Count; i++)
        {
            int need = MipByteSize(format, Math.Max(1, width >> i), Math.Max(1, height >> i));
            if (need < 0) throw new NotSupportedException($"pixel format {format}");
            if (mips[i].Length != need) throw new ArgumentException($"mip {i} is {mips[i].Length} bytes, format {format} {Math.Max(1, width >> i)}x{Math.Max(1, height >> i)} needs {need}");
        }
        if (frames.Count == 0) frames = new[] { ("", 0, 0, width, height) };
        int n = frames.Count;
        int indexAt = HeaderSize, framesAt = (indexAt + 4 * n + 7) & ~7, pixelsAt = framesAt + n * FrameSize;
        var b = new byte[pixelsAt + mips.Sum(m => m.Length)];
        var s = b.AsSpan();
        if (template != null) template.Bytes.AsSpan(0x230, HeaderSize - 0x230).CopyTo(s[0x230..]);
        void W(int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), v);
        BinaryPrimitives.WriteUInt32LittleEndian(s, Magic);
        W(4, Version280);
        W(0x10, snoId);
        W(FormatAt, (int)format); W(WidthAt, width); W(HeightAt, height);
        W(FaceCountAt, 1); W(MipFirstAt, 0); W(MipLastAt, mips.Count - 1);
        int pos = pixelsAt;
        for (int i = 0; i < mips.Count; i++)
        {
            W(MipTableAt + 8 * i, pos - 16); W(MipTableAt + 8 * i + 4, mips[i].Length);
            mips[i].CopyTo(b, pos);
            pos += mips[i].Length;
        }
        W(FrameCountAt, n); W(FrameArrayAt, framesAt - 16); W(FrameArrayAt + 4, n * FrameSize);
        W(FrameIndexArrayAt, indexAt - 16); W(FrameIndexArrayAt + 4, 4 * n);
        for (int i = 0; i < n; i++)
        {
            W(indexAt + 4 * i, i);
            var (name, x, y, w, h) = frames[i];
            var nb = Encoding.ASCII.GetBytes(name);
            if (nb.Length >= FrameNameSize) throw new ArgumentException($"frame name '{name}' is longer than 63 characters");
            int r = framesAt + i * FrameSize;
            nb.CopyTo(s[r..]);
            BinaryPrimitives.WriteSingleLittleEndian(s[(r + 64)..], (float)x / width);
            BinaryPrimitives.WriteSingleLittleEndian(s[(r + 68)..], (float)y / height);
            BinaryPrimitives.WriteSingleLittleEndian(s[(r + 72)..], (float)(x + w) / width);
            BinaryPrimitives.WriteSingleLittleEndian(s[(r + 76)..], (float)(y + h) / height);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(s[HashAt..], contentHash);
        return b;
    }
}

/// <summary>
/// Resolves layout image references "&lt;Namespace&gt;:&lt;Frame&gt;" to a texture SNO + frame exactly as the 2.8.0.99920 exe does
/// (verified by disassembly 2026-11-11; lookup @0x3A67E0, per-namespace builder @0x3A6DE0, frame registration @0x3A6B30/@0x3A7150):
/// <list type="bullet">
/// <item>The text before the first ':' picks a namespace by _stricmp from {0 "Items", 1 "Minimap", 2 "UI"} (table @0x108EA60).</item>
/// <item>A namespace owns every texture SNO (group 44, from the SNO catalog) whose NAME starts with its prefix, by _strnicmp
/// (table @0x13EFC00: Items = "2DInventory", Minimap = "2DMap", UI = "2DUI").</item>
/// <item>Each frame of those textures is registered under <see cref="TexFile.FrameHash"/> of its name -> {texture SNO, frame index};
/// on a hash clash the first one stays. The lookup compares only the hash.</item>
/// </list>
/// Stock 2.8.0: UI = 389 textures / 9,461 named frames, Items = 153 / 3,264, Minimap = 60 / 1,759; no duplicate names or hashes.
/// The index is built lazily per namespace (reads every texture of the prefix once, a few seconds for UI) and cached.
/// </summary>
public sealed class TexFrameResolver
{
    public const int TextureGroup = 44;

    /// <summary>Namespace name -> texture name prefix, in the exe's index order.</summary>
    public static readonly (string Namespace, string TexturePrefix)[] Namespaces = { ("Items", "2DInventory"), ("Minimap", "2DMap"), ("UI", "2DUI") };

    public readonly record struct Hit(string Namespace, int TextureSno, string TextureName, int FrameIndex, string FrameName);

    readonly D3Storage _storage;
    readonly Dictionary<string, Dictionary<uint, Hit>> _index = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, TexFile> _textures = new();
    readonly Dictionary<int, byte[]> _decoded = new();
    readonly object _lock = new();

    /// <summary>Clashes met while building an index (the client's "already exists" errors).</summary>
    public List<string> Clashes { get; } = new();

    public TexFrameResolver(D3Storage storage) => _storage = storage;

    /// <summary>Splits "UI:Name" at the first ':' and canonicalises the namespace; false when there is no ':' or the namespace is unknown.</summary>
    public static bool TrySplit(string reference, out string ns, out string frame)
    {
        ns = frame = "";
        int c = reference.IndexOf(':');
        if (c <= 0) return false;
        string head = reference[..c];
        frame = reference[(c + 1)..];
        foreach (var (name, _) in Namespaces)
            if (string.Equals(name, head, StringComparison.OrdinalIgnoreCase)) { ns = name; return true; }
        return false;
    }

    public bool TryResolve(string reference, out Hit hit)
    {
        hit = default;
        if (!TrySplit(reference, out var ns, out var frame) || frame.Length == 0) return false;
        return Index(ns).TryGetValue(TexFile.FrameHash(frame), out hit);
    }

    /// <summary>All registered frames of a namespace ("UI", "Items", "Minimap").</summary>
    public IReadOnlyCollection<Hit> All(string ns) => TrySplit(ns + ":x", out var n, out _) ? Index(n).Values : Array.Empty<Hit>();

    Dictionary<uint, Hit> Index(string ns)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(ns, out var map)) return map;
            string prefix = Namespaces.First(x => x.Namespace == ns).TexturePrefix;
            map = new Dictionary<uint, Hit>();
            // The exe walks its SNO catalog order; stock data has no clashes, so the order only matters for new frames.
            foreach (var e in _storage.Toc.InGroup(TextureGroup).Where(e => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Id))
            {
                var tex = LoadUnlocked(e.Id);
                if (tex == null) continue;
                foreach (var f in tex.Frames)
                {
                    if (f.Name.Length == 0) continue;
                    uint h = TexFile.FrameHash(f.Name);
                    if (map.TryGetValue(h, out var old)) { Clashes.Add($"{ns}:{f.Name} in {e.Name} already exists in {old.TextureName}"); continue; }
                    map[h] = new Hit(ns, e.Id, e.Name, f.Index, f.Name);
                }
                _textures.Remove(e.Id);   // keep memory low while indexing; Texture() reloads on demand
            }
            _index[ns] = map;
            return map;
        }
    }

    TexFile? LoadUnlocked(int sno)
    {
        if (_textures.TryGetValue(sno, out var t)) return t;
        var entries = _storage.SnoEntries(TextureGroup, sno);
        if (entries.Count == 0) return null;
        t = new TexFile(_storage.Read(entries[0]));
        if (_textures.Count > 64) _textures.Clear();
        _textures[sno] = t;
        return t;
    }

    public TexFile? Texture(int sno) { lock (_lock) return LoadUnlocked(sno); }

    /// <summary>Decoded RGBA8 mip 0 of a texture (small cache).</summary>
    public byte[] Mip0(int sno)
    {
        lock (_lock)
        {
            if (_decoded.TryGetValue(sno, out var px)) return px;
            var t = LoadUnlocked(sno) ?? throw new FileNotFoundException($"texture SNO {sno} not in the storage");
            if (_decoded.Count >= 16) _decoded.Clear();
            return _decoded[sno] = t.DecodeMip(0);
        }
    }

    /// <summary>The RGBA8 pixels of a "UI:Name" frame, or null when it does not resolve.</summary>
    public (byte[] Rgba, int Width, int Height)? FramePixels(string reference)
    {
        if (!TryResolve(reference, out var hit)) return null;
        var tex = Texture(hit.TextureSno)!;
        return tex.DecodeFrame(tex.Frames[hit.FrameIndex], Mip0(hit.TextureSno));
    }

    /// <summary>"UI:Name" -> PNG bytes, or null.</summary>
    public byte[]? FramePng(string reference) => FramePixels(reference) is { } p ? Png.Encode(p.Width, p.Height, p.Rgba) : null;
}

/// <summary>Minimal PNG writer: 8-bit RGBA, filter 0 on every row, zlib via <see cref="ZLibStream"/>.</summary>
public static class Png
{
    static readonly uint[] CrcTable = MakeCrcTable();

    static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static uint CrcUpdate(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>Standard CRC-32 (zlib / PNG).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data) => CrcUpdate(0xFFFFFFFF, data) ^ 0xFFFFFFFF;

    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length < width * height * 4) throw new ArgumentException("rgba too short");
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0);   // filter type 0 (none)
                z.Write(rgba.Slice(y * width * 4, width * 4));
            }
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8; ihdr[9] = 6;   // 8-bit, RGBA; compression, filter method, interlace = 0
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", raw.GetBuffer().AsSpan(0, (int)raw.Length));
        Chunk(png, "IEND", ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        Encoding.ASCII.GetBytes(type, head[4..]);
        s.Write(head);
        s.Write(data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, CrcUpdate(CrcUpdate(0xFFFFFFFF, head[4..]), data) ^ 0xFFFFFFFF);
        s.Write(c);
    }
}
