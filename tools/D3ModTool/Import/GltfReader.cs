using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;

namespace D3ModTool.Import;

/// <summary>
/// Reads a glTF 2.0 model (.glb, or .gltf with embedded/external buffers and images) into flat triangle lists in WORLD space
/// (node transforms applied, rest pose). Supported: triangle primitives (mode 4) with POSITION, optional NORMAL and TEXCOORD_0,
/// any index type; float or normalized integer UVs; interleaved buffer views; one base color texture per material (PNG) times
/// baseColorFactor, and the emissive texture (PNG) times emissiveFactor.
/// For skinned models it also keeps the node tree (<see cref="Nodes"/>, rest TRS), the skins (joints + inverse bind matrices),
/// each primitive's mesh-space data with JOINTS_0 / WEIGHTS_0, and the animations (channels with their samplers); the skinned
/// importer (<see cref="SkinnedImport"/>) builds the D3 skeleton and clips from them.
/// Not supported (refused or ignored with a warning): sparse accessors, morph targets, texture transforms, other texture slots.
/// </summary>
public sealed class GltfModel
{
    public sealed class Primitive
    {
        public string Name = "";
        public int Material = -1;
        public float[] Positions = Array.Empty<float>();   // xyz, glTF space (Y up), world (rest pose)
        public float[]? Normals;                           // xyz, world, unit
        public float[]? Uv0;                               // uv, top-left origin
        public int[] Indices = Array.Empty<int>();         // triangle list, counter-clockwise front faces
        /// <summary>The node that instances the mesh, and its skin (-1 = none).</summary>
        public int Node = -1, Skin = -1;
        /// <summary>Mesh-space positions / normals (before any node transform), for skinning.</summary>
        public float[] LocalPositions = Array.Empty<float>();
        public float[]? LocalNormals;
        /// <summary>JOINTS_0 (indices into the skin's joint list) and WEIGHTS_0, 4 per vertex; null when not skinned.</summary>
        public int[]? Joints;
        public float[]? Weights;
        /// <summary>World matrix of <see cref="Node"/> in the rest pose (row-vector convention).</summary>
        public Matrix4x4 World = Matrix4x4.Identity;
    }

    public sealed class Material
    {
        public string Name = "";
        public int Width, Height;
        public byte[]? Rgba;                               // base color texture (already multiplied by baseColorFactor)
        public float[] BaseColor = { 1, 1, 1, 1 };
        /// <summary>Emissive texture times emissiveFactor (null when the material has no emissive texture).</summary>
        public byte[]? EmissiveRgba;
        public int EmissiveWidth, EmissiveHeight;
        public float[] EmissiveFactor = { 0, 0, 0 };
    }

    /// <summary>A scene node: rest-pose local transform (TRS, or a matrix decomposed into TRS), parent and children.</summary>
    public sealed class Node
    {
        public int Index, Parent = -1, Mesh = -1, Skin = -1;
        public string Name = "";
        public int[] Children = Array.Empty<int>();
        public Vector3 T = Vector3.Zero, S = Vector3.One;
        public Quaternion R = Quaternion.Identity;
        public Matrix4x4 Local => Matrix4x4.CreateScale(S) * Matrix4x4.CreateFromQuaternion(R) * Matrix4x4.CreateTranslation(T);
    }

    public sealed class Skin
    {
        public string Name = "";
        /// <summary>Joint node indices; JOINTS_0 values index this list.</summary>
        public int[] Joints = Array.Empty<int>();
        /// <summary>Inverse bind matrix per joint (identity when the skin has none), row-vector convention.</summary>
        public Matrix4x4[] InverseBind = Array.Empty<Matrix4x4>();
    }

    /// <summary>One animated property of one node: key times (seconds) and values (3 or 4 floats per key; CUBICSPLINE
    /// stores in-tangent, value, out-tangent per key).</summary>
    public sealed class Channel
    {
        public int Node;
        public string Path = "";            // translation | rotation | scale (weights are skipped)
        public string Interpolation = "LINEAR";
        public float[] Times = Array.Empty<float>();
        public float[] Values = Array.Empty<float>();
    }

    public sealed class Animation
    {
        public string Name = "";
        public List<Channel> Channels { get; } = new();
        public float Duration => Channels.Count == 0 ? 0 : Channels.Max(c => c.Times.Length == 0 ? 0 : c.Times[^1]);
    }

    public List<Primitive> Primitives { get; } = new();
    public List<Material> Materials { get; } = new();
    public List<Node> Nodes { get; } = new();
    public List<Skin> Skins { get; } = new();
    public List<Animation> Animations { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool HasSkin => Skins.Count > 0 && Primitives.Any(p => p.Skin >= 0);

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
        for (int i = 0; i < m.Arr("materials").Count; i++) m.Materials.Add(m.ReadMaterial(i));
        m.ReadNodes();
        m.ReadSkins();
        m.ReadAnimations();

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
        var world = Nodes[index].Local * parent;
        if (n["mesh"] is JsonNode mi) AddMesh((int)mi, world, (string?)n["name"], index, (int?)n["skin"] ?? -1);
        if (n["children"] is JsonArray ch) foreach (var c in ch) Walk((int)c!, world, depth + 1);
    }

    /// <summary>Rest-pose world matrix of a node (row-vector convention: local * parent's world).</summary>
    public Matrix4x4 RestWorld(int node)
    {
        var m = Matrix4x4.Identity;
        for (int n = node, guard = 0; n >= 0 && guard < 256; n = Nodes[n].Parent, guard++) m *= Nodes[n].Local;
        return m;
    }

    void ReadNodes()
    {
        var nodes = Arr("nodes");
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i]!;
            var node = new Node { Index = i, Name = (string?)n["name"] ?? $"node{i}", Mesh = (int?)n["mesh"] ?? -1, Skin = (int?)n["skin"] ?? -1 };
            if (n["children"] is JsonArray ch) node.Children = ch.Select(c => (int)c!).ToArray();
            if (n["matrix"] is JsonArray ma)
            {
                var f = ma.Select(x => (float)x!).ToArray();
                // glTF matrices are column-major; System.Numerics uses row vectors, so the column-major array maps straight onto M11..M44
                var local = new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
                if (!Matrix4x4.Decompose(local, out var sc, out var rot, out var tr)) throw new InvalidDataException($"node {i}: matrix is not a TRS transform");
                (node.T, node.R, node.S) = (tr, rot, sc);
            }
            else
            {
                if (n["translation"] is JsonArray ta) node.T = new Vector3((float)ta[0]!, (float)ta[1]!, (float)ta[2]!);
                if (n["rotation"] is JsonArray ra) node.R = Quaternion.Normalize(new Quaternion((float)ra[0]!, (float)ra[1]!, (float)ra[2]!, (float)ra[3]!));
                if (n["scale"] is JsonArray sa) node.S = new Vector3((float)sa[0]!, (float)sa[1]!, (float)sa[2]!);
            }
            Nodes.Add(node);
        }
        foreach (var n in Nodes) foreach (int c in n.Children)
        {
            if (c < 0 || c >= Nodes.Count) throw new InvalidDataException($"node {n.Index}: child {c} out of range");
            if (Nodes[c].Parent >= 0) throw new InvalidDataException($"node {c} has two parents");
            Nodes[c].Parent = n.Index;
        }
    }

    void ReadSkins()
    {
        var skins = Arr("skins");
        for (int i = 0; i < skins.Count; i++)
        {
            var sj = skins[i]!;
            var sk = new Skin { Name = (string?)sj["name"] ?? $"skin{i}" };
            sk.Joints = (sj["joints"] as JsonArray ?? new JsonArray()).Select(x => (int)x!).ToArray();
            if (sk.Joints.Length == 0) throw new InvalidDataException($"skin {i} has no joints");
            if (sk.Joints.Any(j => j < 0 || j >= Nodes.Count)) throw new InvalidDataException($"skin {i}: joint out of range");
            sk.InverseBind = Enumerable.Repeat(Matrix4x4.Identity, sk.Joints.Length).ToArray();
            if (sj["inverseBindMatrices"] is JsonNode ib)
            {
                var f = ReadFloats((int)ib, 16);
                if (f.Length != 16 * sk.Joints.Length) throw new InvalidDataException($"skin {i}: {f.Length / 16} inverse bind matrices for {sk.Joints.Length} joints");
                for (int j = 0; j < sk.Joints.Length; j++)
                    sk.InverseBind[j] = new Matrix4x4(f[16 * j], f[16 * j + 1], f[16 * j + 2], f[16 * j + 3], f[16 * j + 4], f[16 * j + 5], f[16 * j + 6], f[16 * j + 7],
                        f[16 * j + 8], f[16 * j + 9], f[16 * j + 10], f[16 * j + 11], f[16 * j + 12], f[16 * j + 13], f[16 * j + 14], f[16 * j + 15]);
            }
            Skins.Add(sk);
        }
    }

    void ReadAnimations()
    {
        var anims = Arr("animations");
        for (int i = 0; i < anims.Count; i++)
        {
            var aj = anims[i]!;
            var a = new Animation { Name = (string?)aj["name"] ?? $"animation{i}" };
            var samplers = aj["samplers"] as JsonArray ?? new JsonArray();
            foreach (var cj in aj["channels"] as JsonArray ?? new JsonArray())
            {
                var target = cj!["target"]!;
                string path = (string?)target["path"] ?? "";
                if (target["node"] == null || path is not ("translation" or "rotation" or "scale"))
                {
                    if (path == "weights") Warnings.Add($"animation {a.Name}: morph target weights ignored");
                    continue;
                }
                var sj = samplers[(int)cj["sampler"]!]!;
                int comps = path == "rotation" ? 4 : 3;
                var ch = new Channel
                {
                    Node = (int)target["node"]!, Path = path, Interpolation = (string?)sj["interpolation"] ?? "LINEAR",
                    Times = ReadFloats((int)sj["input"]!, 1),
                };
                ch.Values = ReadFloats((int)sj["output"]!, comps);
                int per = ch.Interpolation == "CUBICSPLINE" ? 3 : 1;
                if (ch.Values.Length != ch.Times.Length * comps * per)
                    throw new InvalidDataException($"animation {a.Name}: {path} of node {ch.Node} has {ch.Values.Length / comps} values for {ch.Times.Length} keys");
                if (ch.Interpolation is not ("LINEAR" or "STEP" or "CUBICSPLINE")) throw new NotSupportedException($"animation {a.Name}: interpolation {ch.Interpolation}");
                a.Channels.Add(ch);
            }
            if (a.Channels.Count > 0) Animations.Add(a);
        }
    }

    void AddMesh(int meshIndex, Matrix4x4 world, string? nodeName, int nodeIndex, int skin)
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
                Node = nodeIndex, Skin = skin, LocalPositions = pos, World = world,
            };
            if (prims.Count > 1) prim.Name += $"_{k}";
            int nv = pos.Length / 3;
            prim.Positions = new float[nv * 3];
            for (int i = 0; i < nv; i++)
            {
                var v = Vector3.Transform(new Vector3(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]), world);
                prim.Positions[3 * i] = v.X; prim.Positions[3 * i + 1] = v.Y; prim.Positions[3 * i + 2] = v.Z;
            }
            if (skin >= 0 && attrs["JOINTS_0"] != null && attrs["WEIGHTS_0"] != null)
            {
                prim.Joints = ReadFloats((int)attrs["JOINTS_0"]!, 4).Select(x => (int)x).ToArray();
                prim.Weights = ReadFloats((int)attrs["WEIGHTS_0"]!, 4);
                if (prim.Joints.Length != nv * 4 || prim.Weights.Length != nv * 4) throw new InvalidDataException($"mesh {meshIndex} primitive {k}: JOINTS_0/WEIGHTS_0 count differs from POSITION");
                if (attrs["JOINTS_1"] != null) Warnings.Add($"mesh {meshIndex}: JOINTS_1/WEIGHTS_1 ignored (D3 keeps at most 3 influences per vertex)");
            }
            else if (skin >= 0) { Warnings.Add($"mesh {meshIndex}: node has a skin but the primitive has no JOINTS_0/WEIGHTS_0; treated as unskinned"); prim.Skin = -1; }
            if (attrs["NORMAL"] != null)
            {
                var nor = ReadFloats((int)attrs["NORMAL"]!, 3);
                prim.LocalNormals = nor;
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
        if (mj["emissiveFactor"] is JsonArray ef) mat.EmissiveFactor = ef.Select(x => (float)x!).ToArray();
        if (mj["emissiveTexture"] is JsonNode et)
        {
            var (w, h, rgba) = ReadImage(mat.Name, (int)et["index"]!);
            for (int i = 0; i < w * h; i++)
                for (int c = 0; c < 3; c++) rgba[i * 4 + c] = (byte)Math.Clamp((int)MathF.Round(rgba[i * 4 + c] * mat.EmissiveFactor[c]), 0, 255);
            (mat.EmissiveWidth, mat.EmissiveHeight, mat.EmissiveRgba) = (w, h, rgba);
        }
        foreach (var slot in new[] { "normalTexture", "occlusionTexture" })
            if (mj[slot] != null) Warnings.Add($"material {mat.Name}: {slot} ignored (only the base color and emissive textures are imported)");
        return mat;
    }

    (int W, int H, byte[] Rgba) ReadImage(string material, int textureIndex)
    {
        var tex = Arr("textures")[textureIndex]!;
        var img = Arr("images")[(int)tex["source"]!]!;
        byte[] data = img["bufferView"] is JsonNode bv ? View((int)bv).ToArray() : ReadUri((string)img["uri"]!);
        string mime = (string?)img["mimeType"] ?? "";
        if (mime.Length > 0 && mime != "image/png") throw new NotSupportedException($"material {material}: image type {mime} (only PNG is supported; re-export the texture as PNG)");
        return PngDecoder.Decode(data);
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
        components = (string)a["type"]! switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, var t => throw new NotSupportedException($"accessor type {t}") };
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
