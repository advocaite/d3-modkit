using System.Buffers.Binary;
using System.IO.Compression;

namespace D3ModTool.Import;

/// <summary>
/// Minimal PNG decoder for model textures: non-interlaced, bit depth 8 or 16 (16 keeps the high byte), color types
/// 0 (gray), 2 (RGB), 3 (palette, with tRNS), 4 (gray + alpha) and 6 (RGBA). Output: RGBA8, row-major, top row first.
/// </summary>
public static class PngDecoder
{
    public static (int Width, int Height, byte[] Rgba) Decode(byte[] png)
    {
        if (png.Length < 33 || BinaryPrimitives.ReadUInt64BigEndian(png) != 0x89504E470D0A1A0AUL) throw new InvalidDataException("not a PNG file");
        int w = 0, h = 0, depth = 0, type = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        for (int at = 8; at + 8 <= png.Length;)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string t = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, len);
            switch (t)
            {
                case "IHDR":
                    w = BinaryPrimitives.ReadInt32BigEndian(data); h = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    depth = data[8]; type = data[9];
                    if (data[12] != 0) throw new NotSupportedException("interlaced PNG (save it without interlacing)");
                    if (depth != 8 && depth != 16) throw new NotSupportedException($"PNG bit depth {depth} (use 8 or 16)");
                    if (type is not (0 or 2 or 3 or 4 or 6)) throw new NotSupportedException($"PNG color type {type}");
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": trns = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
            }
            if (t == "IEND") break;
            at += 12 + len;
        }
        if (w <= 0 || h <= 0) throw new InvalidDataException("PNG has no IHDR");
        int channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bpp = channels * depth / 8, stride = w * bpp;
        var raw = new byte[(stride + 1) * h];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length) { int n = z.Read(raw, read, raw.Length - read); if (n == 0) break; read += n; }
            if (read != raw.Length) throw new InvalidDataException($"PNG data is {read} bytes, expected {raw.Length}");
        }
        var cur = new byte[stride];
        var prev = new byte[stride];
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int f = raw[y * (stride + 1)];
            var src = raw.AsSpan(y * (stride + 1) + 1, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int p = f switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG filter {f}"),
                };
                cur[i] = (byte)(src[i] + p);
            }
            int step = depth / 8;
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4, s = x * bpp;
                byte S(int k) => cur[s + k * step];   // high byte for 16-bit samples (big endian)
                switch (type)
                {
                    case 0: rgba[o] = rgba[o + 1] = rgba[o + 2] = S(0); rgba[o + 3] = 255; break;
                    case 2: rgba[o] = S(0); rgba[o + 1] = S(1); rgba[o + 2] = S(2); rgba[o + 3] = 255; break;
                    case 3:
                        int idx = cur[s];
                        if (palette == null || idx * 3 + 2 >= palette.Length) throw new InvalidDataException("PNG palette index out of range");
                        rgba[o] = palette[idx * 3]; rgba[o + 1] = palette[idx * 3 + 1]; rgba[o + 2] = palette[idx * 3 + 2];
                        rgba[o + 3] = trns != null && idx < trns.Length ? trns[idx] : (byte)255;
                        break;
                    case 4: rgba[o] = rgba[o + 1] = rgba[o + 2] = S(0); rgba[o + 3] = S(1); break;
                    default: rgba[o] = S(0); rgba[o + 1] = S(1); rgba[o + 2] = S(2); rgba[o + 3] = S(3); break;
                }
            }
            (cur, prev) = (prev, cur);
        }
        return (w, h, rgba);
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
