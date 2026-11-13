using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using D3Casc;

namespace D3ModTool.Import;

/// <summary>
/// <c>import-gltf</c>: a glTF model -> a new static prop for the game, written into a mod's <c>data\</c> folder (a skinned
/// glTF goes to <see cref="SkinnedImport"/> instead: skeleton, animations, AnimSet and a Monster actor):
/// <list type="bullet">
/// <item><c>Base\Textures\&lt;Name&gt;_diff.tex</c> (one per material): DXT1 (format 9, or DXT5 = 12 when the image has alpha), full
///   mip chain down to a 4-pixel side, header fields 0x230-0x27F from the template's diffuse texture.</item>
/// <item><c>Base\Appearance\&lt;Name&gt;.app</c>: the template appearance rebuilt around the new geometry (same blob order and 8-byte
///   alignment as stock files; materials, sub-objects, vertices, indices and bounds replaced; everything else copied).</item>
/// <item><c>Base\Actor\&lt;Name&gt;.acr</c>: the template actor with the new SNO id, appearance and bounds (no PhysMesh).</item>
/// </list>
/// The template ("--like", default 61743 <c>a2dun_Aqd_Act_Post_B_02_Base</c>: a ServerProp actor with a single unskinned, unboned
/// mesh and one diffuse texture) is read from the official install, read only. It must be a ServerProp (type 4) or another
/// server-spawned type: the client ignores Environment (type 5) actors sent by the server (it spawns those itself from scene
/// marker sets), verified in game 2026-11-13. File facts: appearance_2.8.0.md and MODDING.md §7.5.
/// </summary>
public static class ModelImport
{
    public const int ActorGroup = 1, AppearanceGroup = 9, TextureGroup = 44, ShaderMapGroup = 36;
    public const int DefaultTemplateActor = 61743;
    public const int FirstModSno = 600000, LastModSno = 719999;
    const int AppBase = 16, St = 32;            // struct offset 0 = file 16; Structure at file 32
    const int VertexSize = 44, SubSize = 400, MatSize = 144, SoaSize = 248, TexEntrySize = 160, LookSize = 64, HardpointSize = 96;
    const int ScaleTagKey = 65543;              // ActorKeys.Scale (float)

    public sealed class Options
    {
        public string Input = "", ModDir = "", Name = "";
        public string? Like, Install;
        public int? FirstSno;
        public float Scale = 3.28084f;          // glTF metres -> D3 feet
        public float ActorScale = 1f;
        /// <summary>The glTF axis the model faces (<see cref="AxisMap"/>); null = +z for skinned models, +x for static ones.</summary>
        public string? Forward;
        /// <summary>Import a skinned glTF as a static prop in its rest pose.</summary>
        public bool Static;
        /// <summary>Skinned import: the Monster SNO the new actor uses (default: the template actor's).</summary>
        public int? MonsterSno;
        /// <summary>Skinned import: animation sample rate (frames per second; D3 stores frames per game tick = fps / 60).</summary>
        public float Fps = 30f;
    }

    public static int Run(Options o, Action<string> say)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(o.Name, "^[A-Za-z][A-Za-z0-9_]{0,47}$"))
            throw new ArgumentException("--name must be 1-48 characters: a letter, then letters, digits or _");
        var model = GltfModel.Load(o.Input);
        foreach (var w in model.Warnings) say("warning: " + w);
        if (model.HasSkin && !o.Static) return SkinnedImport.Run(o, model, say);
        if (model.Skins.Count > 0) say("warning: skins are ignored (static import of the rest pose; drop --static to import a monster)");
        if (model.Animations.Count > 0) say("warning: animations are ignored (static prop)");
        var map = new AxisMap(o.Forward ?? "+x", o.Scale);

        using var d3 = D3Storage.Open(D3Storage.ResolveInstall(o.Install));
        int templateActor = ResolveActor(d3, o.Like);
        var actorT = Read(d3, ActorGroup, templateActor);
        int appT = I32(actorT, 36);
        var appTBytes = Read(d3, AppearanceGroup, appT);
        var t = new AppTemplate(appTBytes);
        var texEntry0 = t.TexEntries.FirstOrDefault(e => I32(e, 0) == 1);
        if (texEntry0 == null) throw new InvalidDataException($"template appearance {appT} has no diffuse (slot 1) texture");
        var texT = new TexFile(Read(d3, TextureGroup, I32(texEntry0, 8)));
        d3.Toc.TryGetName(ActorGroup, templateActor, out var tName);
        d3.Toc.TryGetName(ShaderMapGroup, t.Shader, out var shaderName);
        say($"template: actor {templateActor} {tName} (type {I32(actorT, 32)}), appearance {appT}, shader {t.Shader} {shaderName}, texture header from {I32(texEntry0, 8)}");

        // materials used by the primitives (in first-use order) -> textures
        var usedMats = model.Primitives.Select(p => p.Material).Distinct().ToList();
        string TexName(int i) => usedMats.Count == 1 ? $"{o.Name}_diff" : $"{o.Name}_{i}_diff";
        var names = new List<(int Group, string Name)> { (ActorGroup, o.Name), (AppearanceGroup, o.Name) };
        for (int i = 0; i < usedMats.Count; i++) names.Add((TextureGroup, TexName(i)));
        var ids = AllocateIds(d3, o, names, say);

        var data = Path.Combine(o.ModDir, "data", "Base");
        var written = new List<(int Group, int Sno, string Name, string Path, int Size)>();
        void Write(int group, string name, byte[] bytes)
        {
            var path = Path.Combine(data, SnoGroups.Name(group), name + SnoGroups.Ext(group));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            written.Add((group, ids[(group, name)], name, path, bytes.Length));
        }

        // ---- textures ----
        var texIds = new List<int>();
        for (int i = 0; i < usedMats.Count; i++)
        {
            var mat = usedMats[i] >= 0 && usedMats[i] < model.Materials.Count ? model.Materials[usedMats[i]] : null;
            int sno = ids[(TextureGroup, TexName(i))];
            Write(TextureGroup, TexName(i), BuildTexture(sno, mat, texT, say));
            texIds.Add(sno);
        }

        // ---- geometry: glTF (Y up, metres) -> D3 (Z up, feet): (x, y, z) -> (x, -z, y) * scale, a proper rotation (winding kept) ----
        var subs = new List<SubMesh>();
        foreach (var p in model.Primitives)
        {
            int mi = usedMats.IndexOf(p.Material);
            var matName = SafeName(p.Material >= 0 && p.Material < model.Materials.Count ? model.Materials[p.Material].Name : "default", 120);
            subs.Add(SubMesh.From(p, map, mi, matName));
        }
        var matNames = usedMats.Select((m, i) => subs.First(s => s.MaterialIndex == i).MaterialName).ToList();
        int appSno = ids[(AppearanceGroup, o.Name)];
        var app = BuildAppearance(t, appSno, subs, matNames, texIds, o.Name);
        Write(AppearanceGroup, o.Name, app.Bytes);

        // ---- actor ----
        int actorSno = ids[(ActorGroup, o.Name)];
        Write(ActorGroup, o.Name, BuildActor(actorT, actorSno, appSno, app, o.ActorScale, say));

        UpdateModJson(o.ModDir, written, say);
        foreach (var w in written) say($"{SnoGroups.Name(w.Group),-10} {w.Sno,7} {w.Name,-32} {w.Size,9:N0} B  {Path.GetRelativePath(o.ModDir, w.Path)}");
        var (c, e) = (app.Center, app.Extent);
        say($"model: {subs.Sum(s => s.VertexCount)} vertices, {subs.Sum(s => s.Indices.Length / 3)} triangles, {subs.Count} mesh(es); bounds (feet) x {c.X - e.X:F2}..{c.X + e.X:F2}, y {c.Y - e.Y:F2}..{c.Y + e.Y:F2}, z {c.Z - e.Z:F2}..{c.Z + e.Z:F2}");
        say($"next: D3ModTool mod-install {o.ModDir} --client <game copy> (or mod-build), then spawn actor {actorSno} on the server");
        return 0;
    }

    // ------------------------------------------------------------------------------------------------ ids

    internal static int ResolveActor(D3Storage d3, string? like, int defaultActor = DefaultTemplateActor)
    {
        if (string.IsNullOrEmpty(like)) return defaultActor;
        if (int.TryParse(like, out int id)) return id;
        var e = d3.Toc.InGroup(ActorGroup).FirstOrDefault(x => x.Name.Equals(like, StringComparison.OrdinalIgnoreCase));
        if (e.Name == null) throw new ArgumentException($"no actor named '{like}'");
        return e.Id;
    }

    internal static byte[] Read(D3Storage d3, int group, int sno)
    {
        var f = d3.SnoEntries(group, sno);
        if (f.Count == 0) throw new FileNotFoundException($"{SnoGroups.Name(group)} SNO {sno} is not in {d3.InstallPath}");
        return d3.Read(f[0]);
    }

    /// <summary>
    /// Ids for the new SNOs: a re-import reuses the ids the mod's files already have; otherwise the first free ids from --sno (default
    /// 600000, the "core" block of MODDING.md §4.4) that are in no CoreTOC group and in no SNO file of any mod next to this one.
    /// The names must not exist in the game (CoreTOC) for their group.
    /// </summary>
    internal static Dictionary<(int, string), int> AllocateIds(D3Storage d3, Options o, List<(int Group, string Name)> names, Action<string> say)
    {
        var used = d3.Toc.Entries.Select(e => e.Id).ToHashSet();
        var mine = new Dictionary<(int, string), int>();
        var modsRoot = Path.GetDirectoryName(Path.GetFullPath(o.ModDir).TrimEnd('\\', '/'))!;
        foreach (var dir in Directory.Exists(modsRoot) ? Directory.GetDirectories(modsRoot) : Array.Empty<string>())
        {
            var data = Path.Combine(dir, "data");
            if (!Directory.Exists(data)) continue;
            bool self = Path.GetFullPath(dir).TrimEnd('\\').Equals(Path.GetFullPath(o.ModDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(data, "*", SearchOption.AllDirectories))
            {
                var b = File.ReadAllBytes(f);
                if (b.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(b) != 0xDEADBEEF) continue;
                int sno = I32(b, 16);
                if (self && SnoGroups.TryParse(Path.GetFileName(Path.GetDirectoryName(f)!), out int g)) mine[(g, Path.GetFileNameWithoutExtension(f))] = sno;
                else used.Add(sno);
            }
        }
        var result = new Dictionary<(int, string), int>();
        foreach (var (g, n) in names)
        {
            if (d3.Toc.InGroup(g).Any(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"the game already has a {SnoGroups.Name(g)} named '{n}'; pick another --name");
            if (mine.TryGetValue((g, n), out int keep) && !used.Contains(keep) && o.FirstSno == null) { result[(g, n)] = keep; used.Add(keep); }
        }
        int next = o.FirstSno ?? FirstModSno;
        foreach (var (g, n) in names)
        {
            if (result.ContainsKey((g, n))) continue;
            while (used.Contains(next)) next++;
            if (next > LastModSno && o.FirstSno == null) throw new InvalidDataException("no free SNO id left in 600000-719999");
            result[(g, n)] = next;
            used.Add(next);
        }
        say("ids: " + string.Join(", ", names.Select(x => $"{SnoGroups.Name(x.Group)} {x.Name} = {result[x]}")));
        return result;
    }

    // ------------------------------------------------------------------------------------------------ texture

    internal static byte[] BuildTexture(int sno, GltfModel.Material? mat, TexFile template, Action<string> say)
    {
        if (mat?.Rgba != null) return BuildTexture(sno, mat.Width, mat.Height, mat.Rgba, template, say);
        // no texture: a flat 4x4 of the base color
        var c = mat?.BaseColor ?? new float[] { 0.7f, 0.7f, 0.7f, 1 };
        return BuildTexture(sno, 4, 4, Flat(c), template, say);
    }

    /// <summary>A 4x4 RGBA image of one colour (components 0..1).</summary>
    internal static byte[] Flat(float[] c)
    {
        var rgba = new byte[64];
        for (int i = 0; i < 16; i++) for (int k = 0; k < 4; k++) rgba[i * 4 + k] = (byte)Math.Clamp((int)MathF.Round((k < c.Length ? c[k] : 1) * 255), 0, 255);
        return rgba;
    }

    internal static byte[] BuildTexture(int sno, int w, int h, byte[] rgba, TexFile template, Action<string> say)
    {
        int pw = Pow2(w), ph = Pow2(h);
        if (pw != w || ph != h)
        {
            say($"texture {w}x{h} resized to {pw}x{ph} (power-of-two sizes)");
            rgba = BcEncoder.Resize(rgba, w, h, pw, ph);
            (w, h) = (pw, ph);
        }
        bool alpha = false;
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] < 255) { alpha = true; break; }
        var format = alpha ? TexFile.PixelFormat.Dxt5 : TexFile.PixelFormat.Dxt1;
        var mips = BcEncoder.MipChain(rgba, w, h)
            .Select(m => TexFile.Deinterleave(format, alpha ? BcEncoder.Bc3(m.Rgba, m.W, m.H) : BcEncoder.Bc1(m.Rgba, m.W, m.H)))
            .ToList();
        return TexFile.Create(sno, format, w, h, mips, Array.Empty<(string, int, int, int, int)>(), template, Png.Crc32(rgba));
    }

    static int Pow2(int v)
    {
        int p = 4;
        while (p < v && p < 2048) p *= 2;
        return p;
    }

    // ------------------------------------------------------------------------------------------------ appearance

    /// <summary>A stock appearance usable as a template: one look, one material (one SubObjectAppearance), one unskinned sub-object,
    /// no bones, no collision/physics/cloth/lights, empty octree.</summary>
    public sealed class AppTemplate
    {
        public readonly byte[] B;
        public readonly byte[] Material, Soa, TagMap, Look, Sub, Hardpoints, Vertex0;
        public readonly List<byte[]> TexEntries = new();
        public int Shader => I32(Soa, 24);

        public AppTemplate(byte[] b)
        {
            B = b;
            if (I32(b, 0) != unchecked((int)0xDEADBEEF) || I32(b, 4) != 260) throw new InvalidDataException("template is not a 2.8.0 appearance (version 260)");
            void Need(bool ok, string what) { if (!ok) throw new InvalidDataException($"template appearance {I32(b, 16)} is not a simple static prop: {what}"); }
            Need(I32(b, St + 4) == 0 && I32(b, St + 12) == 0, "it has bones");
            Need(I32(b, St + 136) == 1, $"{I32(b, St + 136)} sub-objects");
            Need(I32(b, St + 160) == 0, "GeoSet 1 is not empty");
            Need(I32(b, St + 132) == 0 && I32(b, St + 208) == 0 && I32(b, St + 368) == 0, "bone pulses, capsules or constraints");
            Need(I32(b, St + 292) == 0 && I32(b, St + 308) == 0 && I32(b, St + 324) == 0, "a picking octree");
            Need(I32(b, AppBase + 420) == 1 && I32(b, AppBase + 428) == MatSize, "not exactly one material");
            Need(I32(b, AppBase + 444) == LookSize, "not exactly one look");
            Need(I32(b, AppBase + 464) == 0, "static lights");
            int sub = I32(b, St + 140) + 16;
            Need(I32(b, sub + 28) == 0 && I32(b, sub + 68) == 0 && I32(b, sub + 380) == 0, "the mesh has skinning, cloth or collision shapes");
            Material = Blob(b, AppBase + 424);
            int soaAt = I32(Material, 128) + 16;
            Need(I32(Material, 132) == SoaSize, "the material has more than one look entry");
            Soa = b.AsSpan(soaAt, SoaSize).ToArray();
            Need(I32(Soa, 4 + 0) == -1 || I32(Soa, 4) == 0, "cloth");
            Need(I32(Soa, 136 + 80) == 0, "a second UberMaterial with textures");
            TagMap = Blob(b, soaAt + 8);
            var tex = Blob(b, soaAt + 24 + 76);
            for (int i = 0; i < tex.Length / TexEntrySize; i++) TexEntries.Add(tex.AsSpan(i * TexEntrySize, TexEntrySize).ToArray());
            Look = Blob(b, AppBase + 440);
            Sub = b.AsSpan(sub, SubSize).ToArray();
            Hardpoints = Blob(b, St + 228);
            Vertex0 = b.AsSpan(I32(b, sub + 8) + 16, VertexSize).ToArray();
        }

        static byte[] Blob(byte[] b, int serAt)
        {
            int off = I32(b, serAt) + 16, size = I32(b, serAt + 4);
            return size == 0 ? Array.Empty<byte>() : b.AsSpan(off, size).ToArray();
        }
    }

    /// <summary>One D3 sub-object: FatVertex bytes are produced by <see cref="BuildAppearance"/> (or the skinned writer).</summary>
    public sealed class SubMesh
    {
        public string Name = "", MaterialName = "";
        public int MaterialIndex;
        public Vector3[] Pos = Array.Empty<Vector3>(), Nrm = Array.Empty<Vector3>(), Tan = Array.Empty<Vector3>();
        public Vector2[] Uv = Array.Empty<Vector2>();
        public ushort[] Indices = Array.Empty<ushort>();
        /// <summary>Skinned meshes: 3 influences per vertex (VertInfluences), bone index + weight, sorted by weight.</summary>
        public int[]? BoneIdx;
        public float[]? BoneW;
        public int VertexCount => Pos.Length;

        public static SubMesh From(GltfModel.Primitive p, AxisMap map, int materialIndex, string materialName)
        {
            int n = p.Positions.Length / 3;
            var pos = Enumerable.Range(0, n).Select(i => map.Pos(new Vector3(p.Positions[3 * i], p.Positions[3 * i + 1], p.Positions[3 * i + 2]))).ToArray();
            var nrm = p.Normals == null ? null : Enumerable.Range(0, n).Select(i => Vector3.Normalize(map.Dir(new Vector3(p.Normals[3 * i], p.Normals[3 * i + 1], p.Normals[3 * i + 2])))).ToArray();
            return FromD3(p.Name, pos, nrm, p.Uv0, p.Indices, materialIndex, materialName);
        }

        /// <summary>A sub-object from D3-space positions and (optional) normals: computes missing normals and the tangents.</summary>
        public static SubMesh FromD3(string name, Vector3[] pos, Vector3[]? normals, float[]? uv0, int[] indices, int materialIndex, string materialName)
        {
            int n = pos.Length;
            if (n > 65535) throw new InvalidDataException($"mesh {name} has {n} vertices; a D3 sub-object holds at most 65535 (split the mesh)");
            var m = new SubMesh { Name = name, MaterialIndex = materialIndex, MaterialName = materialName, Pos = pos };
            m.Uv = Enumerable.Range(0, n).Select(i => uv0 != null ? new Vector2(uv0[2 * i], uv0[2 * i + 1]) : Vector2.Zero).ToArray();
            m.Indices = indices.Select(i => (ushort)i).ToArray();
            if (normals != null) m.Nrm = normals;
            else
            {
                // no normals: area-weighted smooth normals
                m.Nrm = new Vector3[n];
                for (int t = 0; t < m.Indices.Length; t += 3)
                {
                    var (a, b, c) = (m.Pos[m.Indices[t]], m.Pos[m.Indices[t + 1]], m.Pos[m.Indices[t + 2]]);
                    var fn = Vector3.Cross(b - a, c - a);
                    m.Nrm[m.Indices[t]] += fn; m.Nrm[m.Indices[t + 1]] += fn; m.Nrm[m.Indices[t + 2]] += fn;
                }
                for (int i = 0; i < n; i++) m.Nrm[i] = m.Nrm[i].LengthSquared() > 1e-12f ? Vector3.Normalize(m.Nrm[i]) : Vector3.UnitZ;
            }
            // tangents (direction of +u), Gram-Schmidt against the normal; the bitangent is N x T (as in stock files)
            var tan = new Vector3[n];
            for (int t = 0; t < m.Indices.Length; t += 3)
            {
                int i0 = m.Indices[t], i1 = m.Indices[t + 1], i2 = m.Indices[t + 2];
                Vector3 e1 = m.Pos[i1] - m.Pos[i0], e2 = m.Pos[i2] - m.Pos[i0];
                Vector2 d1 = m.Uv[i1] - m.Uv[i0], d2 = m.Uv[i2] - m.Uv[i0];
                float det = d1.X * d2.Y - d2.X * d1.Y;
                if (Math.Abs(det) < 1e-12f) continue;
                var tt = (e1 * d2.Y - e2 * d1.Y) / det;
                tan[i0] += tt; tan[i1] += tt; tan[i2] += tt;
            }
            for (int i = 0; i < n; i++)
            {
                var tt = tan[i] - m.Nrm[i] * Vector3.Dot(m.Nrm[i], tan[i]);
                if (tt.LengthSquared() < 1e-12f)
                {
                    var any = Math.Abs(m.Nrm[i].Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
                    tt = Vector3.Cross(any, m.Nrm[i]);
                }
                tan[i] = Vector3.Normalize(tt);
            }
            m.Tan = tan;
            return m;
        }
    }

    public sealed class BuiltAppearance
    {
        public byte[] Bytes = Array.Empty<byte>();
        public Vector3 Center, Extent;
        public float Radius;
    }

    /// <summary>
    /// Rebuilds the template around the new meshes. Blob order and 8-byte alignment as in every stock file: materials, then per
    /// material its SubObjectAppearance + tag map + texture entries, looks, sub-objects, then per sub-object vertices + indices,
    /// hardpoints. Bounds: AABB = center + half extent (stock rule, all 3,176 simple stock files), sphere = AABB center with the
    /// farthest vertex as radius. Fields this tool does not understand are the template's.
    /// </summary>
    public static BuiltAppearance BuildAppearance(AppTemplate t, int sno, List<SubMesh> subs, List<string> matNames, List<int> texIds, string name)
    {
        var all = subs.SelectMany(s => s.Pos).ToList();
        var mn = all.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var mx = all.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        Vector3 center = (mn + mx) / 2, ext = (mx - mn) / 2;
        float radius = all.Max(p => Vector3.Distance(p, center)) * 1.0001f;

        var o = new MemoryStream();
        o.Write(t.B, 0, AppBase + 536);
        int Align() { while (o.Length % 8 != 0) o.WriteByte(0); return (int)o.Length; }
        int Put(byte[] data) { int at = Align(); o.Write(data); return at; }
        void SetSer(byte[] rec, int at, int fileOffset, int size) { W32(rec, at, size == 0 ? 0 : fileOffset - 16); W32(rec, at + 4, size); }

        // materials: placeholder array first (offsets known only after the per-material blobs)
        int matsAt = Put(new byte[matNames.Count * MatSize]);
        var matRecs = new List<byte[]>();
        for (int m = 0; m < matNames.Count; m++)
        {
            var rec = (byte[])t.Material.Clone();
            Array.Clear(rec, 0, 128);
            Encoding.ASCII.GetBytes(matNames[m]).CopyTo(rec, 0);
            var soa = (byte[])t.Soa.Clone();
            int soaAt = Put(soa);
            SetSer(rec, 128, soaAt, SoaSize);
            int tagAt = Put(t.TagMap);
            SetSer(soa, 8, tagAt, t.TagMap.Length);
            var tex = new MemoryStream();
            foreach (var e in t.TexEntries)
            {
                var copy = (byte[])e.Clone();
                if (I32(copy, 0) == 1) W32(copy, 8, texIds[m]);
                tex.Write(copy);
            }
            int texAt = Put(tex.ToArray());
            SetSer(soa, 24 + 76, texAt, (int)tex.Length);
            o.Position = soaAt; o.Write(soa); o.Position = o.Length;
            matRecs.Add(rec);
        }
        o.Position = matsAt;
        foreach (var r in matRecs) o.Write(r);
        o.Position = o.Length;
        int looksAt = Put(t.Look);

        int subsAt = Put(new byte[subs.Count * SubSize]);
        var subRecs = new List<byte[]>();
        foreach (var s in subs)
        {
            var rec = (byte[])t.Sub.Clone();
            var v = new byte[s.VertexCount * VertexSize];
            for (int i = 0; i < s.VertexCount; i++)
            {
                int at = i * VertexSize;
                t.Vertex0.CopyTo(v, at);   // colors (16..23), UV1 (28..31) and the last word (40..43) as in the template
                WF(v, at, s.Pos[i].X); WF(v, at + 4, s.Pos[i].Y); WF(v, at + 8, s.Pos[i].Z);
                PutDir(v, at + 12, s.Nrm[i]);
                WUv(v, at + 24, s.Uv[i].X); WUv(v, at + 26, s.Uv[i].Y);
                PutDir(v, at + 32, s.Tan[i]);
                PutDir(v, at + 36, Vector3.Normalize(Vector3.Cross(s.Nrm[i], s.Tan[i])));
            }
            int vAt = Put(v);
            var idx = new byte[s.Indices.Length * 2];
            for (int i = 0; i < s.Indices.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(idx.AsSpan(2 * i), s.Indices[i]);
            int iAt = Put(idx);
            W32(rec, 4, s.VertexCount);
            SetSer(rec, 8, vAt, v.Length);
            W32(rec, 40, s.Indices.Length);
            SetSer(rec, 44, iAt, idx.Length);
            Array.Clear(rec, 92, 256);
            Encoding.ASCII.GetBytes(s.MaterialName).CopyTo(rec, 92);
            Encoding.ASCII.GetBytes(SafeName($"{name} | {s.MaterialName}", 127)).CopyTo(rec, 220);
            var smn = s.Pos.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var smx = s.Pos.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            WVec(rec, 348, (smn + smx) / 2); WVec(rec, 360, (smx - smn) / 2);
            subRecs.Add(rec);
        }
        int hpAt = Put(t.Hardpoints);
        var bytes = o.ToArray();
        for (int i = 0; i < subRecs.Count; i++) subRecs[i].CopyTo(bytes, subsAt + i * SubSize);

        // header / Structure / Appearance fields
        W32(bytes, 16, sno);
        W32(bytes, St + 136, subs.Count);
        SetSer(bytes, St + 140, subsAt, subs.Count * SubSize);
        WVec(bytes, St + 184, center); WF(bytes, St + 196, radius);
        SetSer(bytes, St + 228, hpAt, t.Hardpoints.Length);
        W32(bytes, St + 224, t.Hardpoints.Length / HardpointSize);
        WVec(bytes, St + 336, center); WVec(bytes, St + 348, ext);
        W32(bytes, AppBase + 420, matNames.Count);
        SetSer(bytes, AppBase + 424, matsAt, matNames.Count * MatSize);
        SetSer(bytes, AppBase + 440, looksAt, t.Look.Length);
        // +504: shared only by appearances made from the same source model (822 of 11,347 share one); our own value
        uint hash = Png.Crc32(bytes.AsSpan(subsAt));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(AppBase + 504), hash);
        return new BuiltAppearance { Bytes = bytes, Center = center, Extent = ext, Radius = radius };
    }

    // ------------------------------------------------------------------------------------------------ actor

    /// <summary>
    /// The template actor with a new id and appearance. Bounds follow the stock rules (checked on all Environment/Gizmo actors):
    /// actor AABB = appearance AABB, sphere = appearance sphere with radius + 2, cylinder = (center x, center y, bottom z) and
    /// height = AABB height (its second float is kept). PhysMesh is cleared (a template's collision mesh would not fit).
    /// The Scale tag (65543), if present, is set to <paramref name="actorScale"/>.
    /// </summary>
    public static byte[] BuildActor(byte[] template, int sno, int appSno, BuiltAppearance app, float actorScale, Action<string> say)
    {
        var a = (byte[])template.Clone();
        W32(a, 16, sno);
        W32(a, 36, appSno);
        if (I32(a, 40) != -1) { say($"template PhysMesh {I32(a, 40)} cleared"); W32(a, 40, -1); }
        var (c, e) = (app.Center, app.Extent);
        WF(a, 44, c.X); WF(a, 48, c.Y); WF(a, 52, c.Z - e.Z); WF(a, 56, 2 * e.Z);
        WVec(a, 64, c); WF(a, 76, app.Radius + 2);
        WVec(a, 80, c); WVec(a, 92, e);
        // tag map: {i32 count, {i32 type, i32 key, 4-byte value} * count} (type 4 entries are longer; stop there)
        int tm = I32(a, 104) + 16, tmSize = I32(a, 108);
        if (tmSize >= 4)
        {
            int n = I32(a, tm), at = tm + 4;
            for (int i = 0; i < n && at + 12 <= tm + tmSize; i++, at += 12)
            {
                if (I32(a, at) == 4) break;
                if (I32(a, at + 4) == ScaleTagKey) { WF(a, at + 8, actorScale); say($"actor Scale tag {BinaryPrimitives.ReadSingleLittleEndian(template.AsSpan(at + 8))} -> {actorScale}"); }
            }
        }
        return a;
    }

    // ------------------------------------------------------------------------------------------------ mod.json

    internal static void UpdateModJson(string modDir, List<(int Group, int Sno, string Name, string Path, int Size)> written, Action<string> say)
    {
        var path = Path.Combine(modDir, "mod.json");
        JsonObject j = File.Exists(path)
            ? (JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new JsonObject())
            : new JsonObject { ["id"] = Path.GetFileName(Path.GetFullPath(modDir).TrimEnd('\\', '/')), ["name"] = Path.GetFileName(Path.GetFullPath(modDir).TrimEnd('\\', '/')), ["version"] = "0.1.0", ["build"] = "2.8.0.99920", ["type"] = "both" };
        var adds = j["adds"] as JsonArray ?? new JsonArray();
        foreach (var w in written)
        {
            foreach (var old in adds.Where(x => x is JsonObject ox && (string?)ox["group"] == SnoGroups.Name(w.Group) && string.Equals((string?)ox["name"], w.Name, StringComparison.OrdinalIgnoreCase)).ToList())
                adds.Remove(old);
            adds.Add(new JsonObject { ["group"] = SnoGroups.Name(w.Group), ["sno"] = w.Sno, ["name"] = w.Name });
        }
        j["adds"] = adds;
        File.WriteAllText(path, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        say($"mod.json: {adds.Count} entries in \"adds\"");
    }

    // ------------------------------------------------------------------------------------------------ helpers

    internal static string SafeName(string s, int max)
    {
        var sb = new StringBuilder();
        foreach (char ch in s) sb.Append(ch < 128 && (char.IsLetterOrDigit(ch) || ch is '_' or ' ' or '|' or '-' or '.') ? ch : '_');
        var r = sb.ToString();
        return r.Length > max ? r[..max] : r;
    }

    internal static int I32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
    internal static float F32(byte[] b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at));
    internal static void W32(byte[] b, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), v);
    internal static void WF(byte[] b, int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), v);
    internal static void WVec(byte[] b, int at, Vector3 v) { WF(b, at, v.X); WF(b, at + 4, v.Y); WF(b, at + 8, v.Z); }
    internal static void WQuat(byte[] b, int at, Quaternion q) { WF(b, at, q.X); WF(b, at + 4, q.Y); WF(b, at + 8, q.Z); WF(b, at + 12, q.W); }

    /// <summary>Unit vector -> 3 bytes (b - 128) / 127, fourth byte 0 (FatVertex normal/tangent/bitangent encoding).</summary>
    internal static void PutDir(byte[] b, int at, Vector3 v)
    {
        b[at] = Enc(v.X); b[at + 1] = Enc(v.Y); b[at + 2] = Enc(v.Z); b[at + 3] = 0;
        static byte Enc(float f) => (byte)Math.Clamp((int)MathF.Round(f * 127 + 128), 1, 255);
    }

    /// <summary>UV0: s16 = round(u * 512), stored as raw ^ 0x8000 (decode: (s16)(raw ^ 0x8000) / 512).</summary>
    internal static void WUv(byte[] b, int at, float u)
    {
        int s = Math.Clamp((int)MathF.Round(u * 512), short.MinValue, short.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)((ushort)(short)s ^ 0x8000));
    }
}
