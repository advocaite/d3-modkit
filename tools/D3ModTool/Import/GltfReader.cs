using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;

namespace D3ModTool.Import;

/// <summary>
/// Reads a glTF 2.0 model (.glb, or .gltf with embedded/external buffers and images) into flat triangle lists in WORLD space
/// (node transforms applied). Supported: triangle primitives (mode 4) with POSITION, optional NORMAL and TEXCOORD_0, any index
/// type; float or normalized integer UVs; interleaved buffer views; one base color texture per material (PNG) times
/// baseColorFactor. Not supported (refused or ignored with a warning): sparse accessors, skins, morph targets, animations,
/// texture transforms, other texture slots.
/// </summary>
public sealed class GltfModel
{
    public sealed class Primitive
    {
        public string Name = "";
        public int Material = -1;
        public float[] Positions = Array.Empty<float>();   // xyz, glTF space (Y up), world
        public float[]? Normals;                           // xyz, world, unit
        public float[]? Uv0;                               // uv, top-left origin
        public int[] Indices = Array.Empty<int>();         // triangle list, counter-clockwise front faces
    }

    public sealed class Material
    {
        public string Name = "";
        public int Width, Height;
        public byte[]? Rgba;                               // base color texture (already multiplied by baseColorFactor)
        public float[] BaseColor = { 1, 1, 1, 1 };
    }

    public List<Primitive> Primitives { get; } = new();
    public List<Material> Materials { get; } = new();
    public List<string> Warnings { get; } = new();

    JsonNode _j = null!;
    readonly List<byte[]> _buffers = new();
    string _dir = ".";

    public static GltfModel Load(string path)
    {
        var m = new GltfModel { _dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "." };
        var bytes = File.ReadAllBytes(path);
        byte[]? bin = null;
        if (bytes.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x46546C67)   // "glTF"
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 2) throw new InvalidDataException("only glTF 2.0 .glb files are supported");
            int at = 12;
            string? json = null;
            while (at + 8 <= bytes.Length)
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
                if (type == 0x4E4F534A) json = System.Text.Encoding.UTF8.GetString(bytes, at + 8, len);
                else if (type == 0x004E4942) bin = bytes.AsSpan(at + 8, len).ToArray();
                at += 8 + len;
            }
            m._j = JsonNode.Parse(json ?? throw new InvalidDataException("glb has no JSON chunk"))!;
        }
        else m._j = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(bytes))!;

        foreach (var b in m.Arr("buffers"))
        {
            var uri = (string?)b!["uri"];
            if (uri == null) m._buffers.Add(bin ?? throw new InvalidDataException("buffer without uri and no glb BIN chunk"));
            else m._buffers.Add(m.ReadUri(uri));
        }
        if ((m._j["extensionsRequired"] as JsonArray)?.Count > 0)
            throw new NotSupportedException($"required glTF extensions are not supported: {string.Join(", ", m.Arr("extensionsRequired"))}");
        if (m.Arr("skins").Count > 0) m.Warnings.Add("skins are ignored (static mesh import; the bind pose is used)");
        if (m.Arr("animations").Count > 0) m.Warnings.Add("animations are ignored");

        for (int i = 0; i < m.Arr("materials").Count; i++) m.Materials.Add(m.ReadMaterial(i));

        // scene graph: the default scene's roots, else every node that is nobody's child
        var nodes = m.Arr("nodes");
        IEnumerable<int> roots;
        int scene = (int?)m._j["scene"] ?? 0;
        if (m.Arr("scenes").Count > scene && m.Arr("scenes")[scene]!["nodes"] is JsonArray sn) roots = sn.Select(x => (int)x!);
        else
        {
            var children = nodes.SelectMany(n => (n!["children"] as JsonArray)?.Select(c => (int)c!) ?? Enumerable.Empty<int>()).ToHashSet();
            roots = Enumerable.Range(0, nodes.Count).Where(i => !children.Contains(i));
        }
        foreach (int r in roots) m.Walk(r, Matrix4x4.Identity, 0);
        if (m.Primitives.Count == 0) throw new InvalidDataException("the model has no triangle meshes");
        return m;
    }

    JsonArray Arr(string name) => _j[name] as JsonArray ?? new JsonArray();

    byte[] ReadUri(string uri)
    {
        if (uri.StartsWith("data:"))
        {
            int comma = uri.IndexOf(',');
            if (comma < 0 || !uri[..comma].EndsWith(";base64")) throw new NotSupportedException("data URI that is not base64");
            return Convert.FromBase64String(uri[(comma + 1)..]);
        }
        return File.ReadAllBytes(Path.Combine(_dir, Uri.UnescapeDataString(uri)));
    }

    void Walk(int index, Matrix4x4 parent, int depth)
    {
        if (depth > 64) throw new InvalidDataException("node hierarchy too deep (cycle?)");
        var n = Arr("nodes")[index]!;
        var local = Matrix4x4.Identity;
        if (n["matrix"] is JsonArray ma)
        {
            var f = ma.Select(x => (float)x!).ToArray();
            // glTF matrices are column-major; System.Numerics uses row vectors, so the column-major array maps straight onto M11..M44
            local = new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
        }
        else
        {
            var t = n["translation"] is JsonArray ta ? new Vector3((float)ta[0]!, (float)ta[1]!, (float)ta[2]!) : Vector3.Zero;
            var r = n["rotation"] is JsonArray ra ? new Quaternion((float)ra[0]!, (float)ra[1]!, (float)ra[2]!, (float)ra[3]!) : Quaternion.Identity;
            var s = n["scale"] is JsonArray sa ? new Vector3((float)sa[0]!, (float)sa[1]!, (float)sa[2]!) : Vector3.One;
            local = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
        }
        var world = local * parent;
        if (n["mesh"] is JsonNode mi) AddMesh((int)mi, world, (string?)n["name"]);
        if (n["children"] is JsonArray ch) foreach (var c in ch) Walk((int)c!, world, depth + 1);
    }

    void AddMesh(int meshIndex, Matrix4x4 world, string? nodeName)
    {
        var mesh = Arr("meshes")[meshIndex]!;
        var prims = mesh["primitives"] as JsonArray ?? new JsonArray();
        Matrix4x4.Invert(world, out var inv);
        var normalMatrix = Matrix4x4.Transpose(inv);
        bool flip = world.GetDeterminant() < 0;
        int k = 0;
        foreach (var p in prims)
        {
            int mode = (int?)p!["mode"] ?? 4;
            if (mode != 4) { Warnings.Add($"mesh {meshIndex} primitive {k}: mode {mode} is not a triangle list, skipped"); k++; continue; }
            var attrs = p["attributes"]!;
            if (attrs["POSITION"] == null) throw new InvalidDataException($"mesh {meshIndex} primitive {k} has no POSITION");
            if (p["targets"] is JsonArray { Count: > 0 }) Warnings.Add($"mesh {meshIndex}: morph targets ignored");
            var pos = ReadFloats((int)attrs["POSITION"]!, 3);
            var prim = new Primitive
            {
                Name = (string?)mesh["name"] ?? nodeName ?? $"mesh{meshIndex}",
                Material = (int?)p["material"] ?? -1,
            };
            if (prims.Count > 1) prim.Name += $"_{k}";
            int nv = pos.Length / 3;
            prim.Positions = new float[nv * 3];
            for (int i = 0; i < nv; i++)
            {
                var v = Vector3.Transform(new Vector3(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]), world);
                prim.Positions[3 * i] = v.X; prim.Positions[3 * i + 1] = v.Y; prim.Positions[3 * i + 2] = v.Z;
            }
            if (attrs["NORMAL"] != null)
            {
                var nor = ReadFloats((int)attrs["NORMAL"]!, 3);
                prim.Normals = new float[nv * 3];
                for (int i = 0; i < nv; i++)
                {
                    var v = Vector3.Normalize(Vector3.TransformNormal(new Vector3(nor[3 * i], nor[3 * i + 1], nor[3 * i + 2]), normalMatrix));
                    if (!float.IsFinite(v.X)) v = Vector3.UnitY;
                    prim.Normals[3 * i] = v.X; prim.Normals[3 * i + 1] = v.Y; prim.Normals[3 * i + 2] = v.Z;
                }
            }
            if (attrs["TEXCOORD_0"] != null) prim.Uv0 = ReadFloats((int)attrs["TEXCOORD_0"]!, 2);
            prim.Indices = p["indices"] != null ? ReadInts((int)p["indices"]!) : Enumerable.Range(0, nv).ToArray();
            if (prim.Indices.Length % 3 != 0) throw new InvalidDataException($"mesh {meshIndex} primitive {k}: {prim.Indices.Length} indices is not a triangle list");
            if (prim.Indices.Any(i => i < 0 || i >= nv)) throw new InvalidDataException($"mesh {meshIndex} primitive {k}: index out of range");
            if (flip) for (int i = 0; i < prim.Indices.Length; i += 3) (prim.Indices[i + 1], prim.Indices[i + 2]) = (prim.Indices[i + 2], prim.Indices[i + 1]);
            Primitives.Add(prim);
            k++;
        }
    }

    Material ReadMaterial(int index)
    {
        var mj = Arr("materials")[index]!;
        var mat = new Material { Name = (string?)mj["name"] ?? $"material{index}" };
        var pbr = mj["pbrMetallicRoughness"];
        if (pbr?["baseColorFactor"] is JsonArray bc) mat.BaseColor = bc.Select(x => (float)x!).ToArray();
        if (pbr?["baseColorTexture"] is JsonNode bt)
        {
            if (bt["extensions"]?["KHR_texture_transform"] != null) Warnings.Add($"material {mat.Name}: KHR_texture_transform ignored");
            if (((int?)bt["texCoord"] ?? 0) != 0) Warnings.Add($"material {mat.Name}: base color uses TEXCOORD_{(int?)bt["texCoord"]}, imported with TEXCOORD_0");
            var tex = Arr("textures")[(int)bt["index"]!]!;
            var img = Arr("images")[(int)tex["source"]!]!;
            byte[] data;
            if (img["bufferView"] is JsonNode bv) data = View((int)bv).ToArray();
            else data = ReadUri((string)img["uri"]!);
            string mime = (string?)img["mimeType"] ?? "";
            if (mime.Length > 0 && mime != "image/png") throw new NotSupportedException($"material {mat.Name}: image type {mime} (only PNG is supported; re-export the texture as PNG)");
            var (w, h, rgba) = PngDecoder.Decode(data);
            if (mat.BaseColor.Any(f => Math.Abs(f - 1) > 1e-4))
                for (int i = 0; i < w * h; i++)
                    for (int c = 0; c < 4; c++) rgba[i * 4 + c] = (byte)Math.Clamp((int)MathF.Round(rgba[i * 4 + c] * mat.BaseColor[c]), 0, 255);
            (mat.Width, mat.Height, mat.Rgba) = (w, h, rgba);
        }
        foreach (var slot in new[] { "normalTexture", "occlusionTexture", "emissiveTexture" })
            if (mj[slot] != null) Warnings.Add($"material {mat.Name}: {slot} ignored (only the base color texture is imported)");
        return mat;
    }

    ReadOnlySpan<byte> View(int index)
    {
        var v = Arr("bufferViews")[index]!;
        int off = (int?)v["byteOffset"] ?? 0, len = (int)v["byteLength"]!;
        return _buffers[(int)v["buffer"]!].AsSpan(off, len);
    }

    (byte[] Buffer, int Offset, int Stride, int Count, int Comp, bool Norm) Accessor(int index, out int components)
    {
        var a = Arr("accessors")[index]!;
        if (a["sparse"] != null) throw new NotSupportedException($"accessor {index} is sparse");
        components = (string)a["type"]! switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, var t => throw new NotSupportedException($"accessor type {t}") };
        int comp = (int)a["componentType"]!;
        int compSize = comp switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new NotSupportedException($"component type {comp}") };
        var v = Arr("bufferViews")[(int)a["bufferView"]!]!;
        int stride = (int?)v["byteStride"] ?? compSize * components;
        int off = ((int?)v["byteOffset"] ?? 0) + ((int?)a["byteOffset"] ?? 0);
        return (_buffers[(int)v["buffer"]!], off, stride, (int)a["count"]!, comp, (bool?)a["normalized"] ?? false);
    }

    float[] ReadFloats(int accessor, int want)
    {
        var (buf, off, stride, count, comp, norm) = Accessor(accessor, out int n);
        if (n != want) throw new InvalidDataException($"accessor {accessor} has {n} components, expected {want}");
        var o = new float[count * n];
        for (int i = 0; i < count; i++)
            for (int c = 0; c < n; c++)
            {
                var s = buf.AsSpan(off + i * stride);
                o[i * n + c] = comp switch
                {
                    5126 => BinaryPrimitives.ReadSingleLittleEndian(s[(4 * c)..]),
                    5121 => norm ? s[c] / 255f : s[c],
                    5123 => norm ? BinaryPrimitives.ReadUInt16LittleEndian(s[(2 * c)..]) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(s[(2 * c)..]),
                    5120 => norm ? Math.Max(-1f, (sbyte)s[c] / 127f) : (sbyte)s[c],
                    5122 => norm ? Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(s[(2 * c)..]) / 32767f) : BinaryPrimitives.ReadInt16LittleEndian(s[(2 * c)..]),
                    _ => throw new NotSupportedException($"component type {comp} for floats"),
                };
            }
        return o;
    }

    int[] ReadInts(int accessor)
    {
        var (buf, off, stride, count, comp, _) = Accessor(accessor, out int n);
        if (n != 1) throw new InvalidDataException("indices must be SCALAR");
        var o = new int[count];
        for (int i = 0; i < count; i++)
        {
            var s = buf.AsSpan(off + i * stride);
            o[i] = comp switch
            {
                5121 => s[0],
                5123 => BinaryPrimitives.ReadUInt16LittleEndian(s),
                5125 => (int)BinaryPrimitives.ReadUInt32LittleEndian(s),
                _ => throw new NotSupportedException($"index component type {comp}"),
            };
        }
        return o;
    }
}
