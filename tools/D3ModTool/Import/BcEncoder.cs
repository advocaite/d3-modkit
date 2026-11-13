namespace D3ModTool.Import;

/// <summary>
/// Small BC1 (DXT1) / BC3 (DXT5) encoder for model textures, plus mip-chain building. Output is STANDARD interleaved blocks;
/// a .tex stores them planar, so pass the result through <see cref="TexFile.Deinterleave"/>.
/// Color endpoints: principal axis of the block's colors (power iteration), extremes projected on it, then one least-squares refit.
/// BC1 always uses the 4-color mode (c0 &gt; c1), so the 1-bit alpha of format 10 is never triggered.
/// </summary>
public static class BcEncoder
{
    /// <summary>Encodes RGBA8 (w x h) to BC1 blocks (8 bytes per 4x4 block, row-major blocks).</summary>
    public static byte[] Bc1(byte[] rgba, int w, int h) => Encode(rgba, w, h, bc3: false);

    /// <summary>Encodes RGBA8 (w x h) to BC3 blocks (16 bytes: interpolated alpha, then a BC1 color block).</summary>
    public static byte[] Bc3(byte[] rgba, int w, int h) => Encode(rgba, w, h, bc3: true);

    static byte[] Encode(byte[] rgba, int w, int h, bool bc3)
    {
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4), bs = bc3 ? 16 : 8;
        var o = new byte[bw * bh * bs];
        var px = new byte[64];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                for (int ty = 0; ty < 4; ty++)
                    for (int tx = 0; tx < 4; tx++)
                    {
                        int x = Math.Min(w - 1, bx * 4 + tx), y = Math.Min(h - 1, by * 4 + ty);
                        Buffer.BlockCopy(rgba, (y * w + x) * 4, px, (ty * 4 + tx) * 4, 4);
                    }
                var dst = o.AsSpan((by * bw + bx) * bs, bs);
                if (bc3) { AlphaBlock(px, dst[..8]); ColorBlock(px, dst[8..]); }
                else ColorBlock(px, dst);
            }
        return o;
    }

    static void AlphaBlock(byte[] px, Span<byte> dst)
    {
        int a0 = 0, a1 = 255;
        for (int i = 0; i < 16; i++) { a0 = Math.Max(a0, px[i * 4 + 3]); a1 = Math.Min(a1, px[i * 4 + 3]); }
        dst[0] = (byte)a0; dst[1] = (byte)a1;
        ulong bits = 0;
        if (a0 > a1)
        {
            Span<int> v = stackalloc int[8];
            v[0] = a0; v[1] = a1;
            for (int i = 1; i < 7; i++) v[i + 1] = ((7 - i) * a0 + i * a1) / 7;
            for (int i = 0; i < 16; i++)
            {
                int a = px[i * 4 + 3], best = 0, bestErr = int.MaxValue;
                for (int k = 0; k < 8; k++) { int e = Math.Abs(v[k] - a); if (e < bestErr) { bestErr = e; best = k; } }
                bits |= (ulong)best << (3 * i);
            }
        }
        for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(bits >> (8 * i));
    }

    static void ColorBlock(byte[] px, Span<byte> dst)
    {
        Span<float> c = stackalloc float[48];
        float mr = 0, mg = 0, mb = 0;
        for (int i = 0; i < 16; i++) { c[i * 3] = px[i * 4]; c[i * 3 + 1] = px[i * 4 + 1]; c[i * 3 + 2] = px[i * 4 + 2]; mr += c[i * 3]; mg += c[i * 3 + 1]; mb += c[i * 3 + 2]; }
        mr /= 16; mg /= 16; mb /= 16;
        // covariance and principal axis
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        for (int i = 0; i < 16; i++)
        {
            float r = c[i * 3] - mr, g = c[i * 3 + 1] - mg, b = c[i * 3 + 2] - mb;
            xx += r * r; xy += r * g; xz += r * b; yy += g * g; yz += g * b; zz += b * b;
        }
        float ax = 1, ay = 1, az = 1;
        for (int it = 0; it < 8; it++)
        {
            float nx = xx * ax + xy * ay + xz * az, ny = xy * ax + yy * ay + yz * az, nz = xz * ax + yz * ay + zz * az;
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-6f) { ax = ay = az = 0; break; }
            ax = nx / len; ay = ny / len; az = nz / len;
        }
        float tmin = 0, tmax = 0;
        for (int i = 0; i < 16; i++)
        {
            float t = (c[i * 3] - mr) * ax + (c[i * 3 + 1] - mg) * ay + (c[i * 3 + 2] - mb) * az;
            tmin = Math.Min(tmin, t); tmax = Math.Max(tmax, t);
        }
        var e0 = (mr + ax * tmax, mg + ay * tmax, mb + az * tmax);
        var e1 = (mr + ax * tmin, mg + ay * tmin, mb + az * tmin);
        var (best, bestErr) = Fit(c, Pack(e0), Pack(e1));
        // one least-squares refit of the endpoints for the chosen indices
        {
            float a2 = 0, b2 = 0, ab = 0;
            float axr = 0, axg = 0, axb = 0, bxr = 0, bxg = 0, bxb = 0;
            for (int i = 0; i < 16; i++)
            {
                int k = (int)(best.Idx >> (2 * i)) & 3;
                float alpha = k switch { 0 => 1f, 1 => 0f, 2 => 2f / 3, _ => 1f / 3 }, beta = 1 - alpha;
                a2 += alpha * alpha; b2 += beta * beta; ab += alpha * beta;
                axr += alpha * c[i * 3]; axg += alpha * c[i * 3 + 1]; axb += alpha * c[i * 3 + 2];
                bxr += beta * c[i * 3]; bxg += beta * c[i * 3 + 1]; bxb += beta * c[i * 3 + 2];
            }
            float det = a2 * b2 - ab * ab;
            if (Math.Abs(det) > 1e-6f)
            {
                float f = 1 / det;
                var n0 = ((axr * b2 - bxr * ab) * f, (axg * b2 - bxg * ab) * f, (axb * b2 - bxb * ab) * f);
                var n1 = ((bxr * a2 - axr * ab) * f, (bxg * a2 - axg * ab) * f, (bxb * a2 - axb * ab) * f);
                var (alt, altErr) = Fit(c, Pack(n0), Pack(n1));
                if (altErr < bestErr) best = alt;
            }
        }
        dst[0] = (byte)best.C0; dst[1] = (byte)(best.C0 >> 8);
        dst[2] = (byte)best.C1; dst[3] = (byte)(best.C1 >> 8);
        for (int i = 0; i < 4; i++) dst[4 + i] = (byte)(best.Idx >> (8 * i));
    }

    readonly record struct Block(int C0, int C1, uint Idx);

    /// <summary>Orders two 565 endpoints for the 4-color mode, picks the nearest palette entry per texel, returns the squared error.</summary>
    static (Block, float) Fit(ReadOnlySpan<float> c, int p0, int p1)
    {
        if (p0 < p1) (p0, p1) = (p1, p0);
        if (p0 == p1)
        {
            // all texels one color (after quantization): c0 == c1 means BC1's 3-color mode, where index 0 is still c0
            float err0 = 0;
            Expand(p0, out int r, out int g, out int b);
            for (int i = 0; i < 16; i++) err0 += Sq(c[i * 3] - r) + Sq(c[i * 3 + 1] - g) + Sq(c[i * 3 + 2] - b);
            return (new Block(p0, p1, 0), err0);
        }
        Span<int> pr = stackalloc int[4], pg = stackalloc int[4], pb = stackalloc int[4];
        Expand(p0, out pr[0], out pg[0], out pb[0]);
        Expand(p1, out pr[1], out pg[1], out pb[1]);
        pr[2] = (2 * pr[0] + pr[1]) / 3; pg[2] = (2 * pg[0] + pg[1]) / 3; pb[2] = (2 * pb[0] + pb[1]) / 3;
        pr[3] = (pr[0] + 2 * pr[1]) / 3; pg[3] = (pg[0] + 2 * pg[1]) / 3; pb[3] = (pb[0] + 2 * pb[1]) / 3;
        uint idx = 0;
        float err = 0;
        for (int i = 0; i < 16; i++)
        {
            int bi = 0;
            float be = float.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                float e = Sq(c[i * 3] - pr[k]) + Sq(c[i * 3 + 1] - pg[k]) + Sq(c[i * 3 + 2] - pb[k]);
                if (e < be) { be = e; bi = k; }
            }
            idx |= (uint)bi << (2 * i);
            err += be;
        }
        return (new Block(p0, p1, idx), err);
    }

    static float Sq(float x) => x * x;

    static int Pack((float R, float G, float B) c)
    {
        int r = Math.Clamp((int)MathF.Round(c.R * 31 / 255f), 0, 31);
        int g = Math.Clamp((int)MathF.Round(c.G * 63 / 255f), 0, 63);
        int b = Math.Clamp((int)MathF.Round(c.B * 31 / 255f), 0, 31);
        return (r << 11) | (g << 5) | b;
    }

    static void Expand(int c, out int r, out int g, out int b)
    {
        r = (c >> 11) & 31; g = (c >> 5) & 63; b = c & 31;
        r = (r << 3) | (r >> 2); g = (g << 2) | (g >> 4); b = (b << 3) | (b >> 2);
    }

    /// <summary>
    /// The mip chain the stock model textures use: halve (2x2 box filter) until the smaller side is 4
    /// (128x128 -> 6 mips ending at 4x4; 512x1024 -> 8 mips ending at 4x8). Sizes must be powers of two.
    /// </summary>
    public static List<(int W, int H, byte[] Rgba)> MipChain(byte[] rgba, int w, int h)
    {
        var list = new List<(int, int, byte[])> { (w, h, rgba) };
        while (Math.Min(w, h) > 4)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var n = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                    for (int k = 0; k < 4; k++)
                    {
                        int s = rgba[((2 * y) * w + 2 * x) * 4 + k] + rgba[((2 * y) * w + 2 * x + 1) * 4 + k]
                              + rgba[((2 * y + 1) * w + 2 * x) * 4 + k] + rgba[((2 * y + 1) * w + 2 * x + 1) * 4 + k];
                        n[(y * nw + x) * 4 + k] = (byte)((s + 2) / 4);
                    }
            list.Add((nw, nh, n));
            (rgba, w, h) = (n, nw, nh);
        }
        return list;
    }

    /// <summary>Bilinear resize (used to bring a texture to power-of-two sizes).</summary>
    public static byte[] Resize(byte[] rgba, int w, int h, int nw, int nh)
    {
        var o = new byte[nw * nh * 4];
        for (int y = 0; y < nh; y++)
        {
            float sy = Math.Clamp((y + 0.5f) * h / nh - 0.5f, 0, h - 1);
            int y0 = (int)sy, y1 = Math.Min(h - 1, y0 + 1);
            float fy = sy - y0;
            for (int x = 0; x < nw; x++)
            {
                float sx = Math.Clamp((x + 0.5f) * w / nw - 0.5f, 0, w - 1);
                int x0 = (int)sx, x1 = Math.Min(w - 1, x0 + 1);
                float fx = sx - x0;
                for (int k = 0; k < 4; k++)
                {
                    float a = rgba[(y0 * w + x0) * 4 + k] * (1 - fx) + rgba[(y0 * w + x1) * 4 + k] * fx;
                    float b = rgba[(y1 * w + x0) * 4 + k] * (1 - fx) + rgba[(y1 * w + x1) * 4 + k] * fx;
                    o[(y * nw + x) * 4 + k] = (byte)Math.Clamp((int)MathF.Round(a * (1 - fy) + b * fy), 0, 255);
                }
            }
        }
        return o;
    }
}
