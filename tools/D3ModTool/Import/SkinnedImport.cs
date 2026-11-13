using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using D3Casc;
using static D3ModTool.Import.ModelImport;

namespace D3ModTool.Import;

/// <summary>
/// <c>import-gltf</c> for a <b>skinned</b> glTF (a mesh with a skin): a new animated monster.
/// Writes into the mod's <c>data\Base\</c>:
/// <list type="bullet">
/// <item><c>Textures\&lt;Name&gt;_diff.tex</c> per material, plus <c>&lt;Name&gt;_glow.tex</c> (from the glTF emissive texture, black
///   when there is none) when the template's materials use a glow map (slot 6).</item>
/// <item><c>Appearance\&lt;Name&gt;.app</c>: the template appearance rebuilt around the new skeleton and meshes (blob order and
///   8-byte alignment as stock skinned files: materials with one SubObjectAppearance per look, looks, bones, sub-objects with
///   vertices / VertInfluences / indices, hardpoints).</item>
/// <item><c>Anim\&lt;Name&gt;_&lt;clip&gt;.ani</c> per glTF animation (resampled to <c>--fps</c>, keyframe-reduced), plus a 2-frame
///   <c>&lt;Name&gt;_dead</c> corpse loop made from the last death frame when the glTF has a death clip but no dead clip.</item>
/// <item><c>AnimSet\&lt;Name&gt;.ans</c>: every tag of the template's AnimSet (plus the core monster tags) mapped to a clip by
///   name convention (<see cref="AnimImport"/>).</item>
/// <item><c>Actor\&lt;Name&gt;.acr</c>: the template actor (a Monster) with the new appearance, AnimSet and bounds; it keeps the
///   template's Monster SNO (stats, AI, powers), tags, sounds and collision.</item>
/// </list>
/// Template: <c>--like</c>, default 5187 <c>Sandling_A</c> (Monster; one skinned mesh, one material, 13 looks, 22 bones, an AnimSet
/// without a parent). Fields this tool does not understand are copied from it. Format facts: appearance_2.8.0.md, anim_2.8.0.md,
/// MODDING.md §7.5.
/// </summary>
public static class SkinnedImport
{
    public const int DefaultTemplateActor = 5187;
    public const int AnimGroup = 6, AnimSetGroup = 8, MonsterGroup = 25;
    const int AppBase = 16, St = 32;
    const int VertexSize = 44, SubSize = 400, MatSize = 144, SoaSize = 248, TexEntrySize = 160, LookSize = 64, HardpointSize = 96, BoneSize = 312;
    const int ScaleTagKey = 65543;
    public const int SlotDiffuse = 1, SlotGlow = 6;

    /// <summary>Texture slots painted for the template's own UV layout, so they are dropped: 2 lightmap, 3 normal map,
    /// 5 specular, 18/19 masks, 54 tint mask, 58 PMA mask. Slots 1 (diffuse) and 6 (glow) get our textures; all others are
    /// generic and kept as they are: 4 environment sphere, 8 irradiance (holy / spirit deaths), 11 champion / rare colour,
    /// 25-41 death and elemental overlays (Sandling_A uses exactly 1, 6, 8, 11 and 25-41).</summary>
    static bool GenericSlot(int slot) => slot is not (2 or 3 or 5 or 18 or 19 or 54 or 58);

    // ============================================================================================ skeleton

    /// <summary>A D3 PRSTransform: rotation, translation, uniform scale; p' = R(s p) + t.</summary>
    public readonly record struct Prs(Quaternion Q, Vector3 T, float S)
    {
        public static readonly Prs Identity = new(Quaternion.Identity, Vector3.Zero, 1);
        /// <summary>parent ∘ local (the local transform applied first).</summary>
        public static Prs Compose(Prs parent, Prs local) =>
            new(Quaternion.Normalize(parent.Q * local.Q), parent.T + Vector3.Transform(local.T * parent.S, parent.Q), parent.S * local.S);
        /// <summary>Stock rule (all files): q = conjugate (w kept), t = -R⁻¹ t / s, s = 1 / s.</summary>
        public Prs Inverse() { var qi = Quaternion.Conjugate(Q); return new(qi, -Vector3.Transform(T, qi) / S, 1 / S); }
        public Vector3 Apply(Vector3 p) => Vector3.Transform(p * S, Q) + T;
    }

    public sealed class Bone
    {
        public string Name = "";
        public int Index, Parent = -1, Node;
        public Prs Local, Bind, Inv;
    }

    public sealed class Skeleton
    {
        public List<Bone> Bones { get; } = new();
        public int Skin;
        public int[] JointToBone = Array.Empty<int>();         // skin joint index -> bone index
        public Dictionary<int, int> NodeToBone { get; } = new();
        public int ParentNodeOf(Bone b) => b.Parent >= 0 ? Bones[b.Parent].Node : -1;

        /// <summary>Nearest ancestor of a node that is a joint (the node itself included), or -1.</summary>
        public int BoneOfNodeOrAncestor(GltfModel m, int node)
        {
            for (int n = node, guard = 0; n >= 0 && guard < 256; n = m.Nodes[n].Parent, guard++)
                if (NodeToBone.TryGetValue(n, out int b)) return b;
            return -1;
        }

        /// <summary>Bones from the model's (single) skin: parent = nearest ancestor joint, ordered so a parent comes before its
        /// children (stock rule); local = the rest transform relative to the parent bone (non-joint nodes in between and above
        /// the roots are folded in); bind = the composed chain; inverse = its inverse.</summary>
        public static Skeleton Build(GltfModel m, AxisMap map, Action<string> say)
        {
            var skinIds = m.Primitives.Where(p => p.Skin >= 0).Select(p => p.Skin).Distinct().ToList();
            if (skinIds.Count != 1) throw new InvalidDataException($"the skinned meshes use {skinIds.Count} different skins; export one armature (one glTF skin) per model");
            var skin = m.Skins[skinIds[0]];
            var sk = new Skeleton { Skin = skinIds[0] };
            var jointSet = skin.Joints.ToHashSet();
            if (jointSet.Count != skin.Joints.Length) throw new InvalidDataException("the skin lists a joint twice");
            int ParentJoint(int node)
            {
                for (int n = m.Nodes[node].Parent, guard = 0; n >= 0 && guard < 256; n = m.Nodes[n].Parent, guard++)
                    if (jointSet.Contains(n)) return n;
                return -1;
            }
            var parentOf = skin.Joints.ToDictionary(j => j, ParentJoint);
            var order = new List<int>();
            void Visit(int j) { order.Add(j); foreach (int c in skin.Joints.Where(c => parentOf[c] == j)) Visit(c); }
            foreach (int r in skin.Joints.Where(j => parentOf[j] < 0)) Visit(r);
            if (order.Count != skin.Joints.Length) throw new InvalidDataException("the skin's joints do not form a tree");
            if (order.Count > 255) say($"warning: {order.Count} bones; stock models have at most ~120");

            bool scaleWarned = false;
            foreach (int node in order)
            {
                var b = new Bone { Index = sk.Bones.Count, Name = SafeName(m.Nodes[node].Name, 63), Node = node };
                sk.NodeToBone[node] = b.Index;
                int pj = parentOf[node];
                b.Parent = pj >= 0 ? sk.NodeToBone[pj] : -1;
                var world = m.RestWorld(node);
                var rel = world;
                if (pj >= 0) { Matrix4x4.Invert(m.RestWorld(pj), out var inv); rel = world * inv; }
                b.Local = ToD3(rel, map, ref scaleWarned, say, $"bone {b.Name}");
                sk.Bones.Add(b);
            }
            var names = sk.Bones.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (names.Count > 0) throw new InvalidDataException($"bone names must be unique (anims match bones by name): {string.Join(", ", names)}");
            foreach (var b in sk.Bones)
            {
                b.Bind = b.Parent >= 0 ? Prs.Compose(sk.Bones[b.Parent].Bind, b.Local) : b.Local;
                b.Inv = b.Bind.Inverse();
            }
            sk.JointToBone = skin.Joints.Select(j => sk.NodeToBone[j]).ToArray();
            return sk;
        }

        /// <summary>A glTF-space matrix (row-vector) -> D3 PRS (axes mapped, translation in feet, uniform scale).</summary>
        public static Prs ToD3(Matrix4x4 m, AxisMap map, ref bool scaleWarned, Action<string> say, string what)
        {
            if (!Matrix4x4.Decompose(m, out var s, out var q, out var t)) throw new InvalidDataException($"{what}: transform is not a TRS (shear?)");
            float su = (s.X + s.Y + s.Z) / 3;
            if (!scaleWarned && (Math.Abs(s.X - su) > 1e-3f * su || Math.Abs(s.Y - su) > 1e-3f * su || Math.Abs(s.Z - su) > 1e-3f * su))
            {
                say($"warning: {what} has a non-uniform scale ({s.X:G4}, {s.Y:G4}, {s.Z:G4}); D3 bones scale uniformly, the average is used");
                scaleWarned = true;
            }
            return new Prs(map.Rot(Quaternion.Normalize(q)), map.Pos(t), su);
        }
    }

    // ============================================================================================ template appearance

    /// <summary>A stock skinned appearance used as a template: its looks, the SubObjectAppearance of its main material for every
    /// look (shader, overlay textures, visibility), the main skinned sub-object record, one vertex, its bones (for hardpoint
    /// mapping) and hardpoints.</summary>
    public sealed class SkinTemplate
    {
        public readonly byte[] B;
        public readonly int LookCount;
        public readonly byte[] Looks, MaterialRec, Sub, Vertex0, Bone0;
        public readonly List<byte[]> Soas = new(), SoaTagMaps = new();
        public readonly List<List<byte[]>> SoaTex = new();
        public readonly List<(string Name, int Parent, Vector3 Pos)> Bones = new();
        public readonly List<(string Name, int Bone, Quaternion Q, Vector3 T)> Hardpoints = new();
        public readonly Vector3 Center, Extent;
        public readonly string MainMaterial;
        public readonly int DiffuseTexture = -1, GlowTexture = -1;
        public readonly List<string> Notes = new();
        public bool UsesGlow => SoaTex.Any(l => l.Any(e => I32(e, 0) == SlotGlow));

        public SkinTemplate(byte[] b)
        {
            B = b;
            if (I32(b, 0) != unchecked((int)0xDEADBEEF) || I32(b, 4) != 260) throw new InvalidDataException("template is not a 2.8.0 appearance (version 260)");
            int sno = I32(b, 16);
            void Need(bool ok, string what) { if (!ok) throw new InvalidDataException($"template appearance {sno} can't be used for a skinned import: {what}"); }
            int nb = I32(b, St + 4);
            Need(nb > 0 && I32(b, St + 12) == nb * BoneSize, "it has no bones");
            int bo = I32(b, St + 8) + 16;
            for (int i = 0; i < nb; i++)
            {
                int o = bo + i * BoneSize;
                Bones.Add((CStr(b, o, 64), I32(b, o + 64), new Vector3(F32(b, o + 124), F32(b, o + 128), F32(b, o + 132))));
            }
            Bone0 = b.AsSpan(bo, BoneSize).ToArray();
            Need(I32(b, St + 160) == 0, "GeoSet 1 is not empty");
            Need(I32(b, AppBase + 464) == 0, "static lights");
            LookCount = I32(b, AppBase + 444) / LookSize;
            Need(LookCount > 0, "no looks");
            Looks = Blob(b, AppBase + 440);
            // main sub-object: the largest skinned one visible in look 0
            int nsub = I32(b, St + 136), so = I32(b, St + 140) + 16;
            int mats = I32(b, AppBase + 428) / MatSize, mo = I32(b, AppBase + 424) + 16;
            int best = -1, bestV = -1, bestMat = -1;
            for (int k = 0; k < nsub; k++)
            {
                int s = so + k * SubSize;
                if (I32(b, s + 28) == 0) continue;
                string mat = CStr(b, s + 92, 128);
                int mi = Enumerable.Range(0, mats).FirstOrDefault(m => CStr(b, mo + m * MatSize, 128) == mat, -1);
                if (mi < 0) continue;
                int soa0 = I32(b, mo + mi * MatSize + 128) + 16;
                bool visible = (I32(b, soa0) & 1) != 0;
                int v = I32(b, s + 4) + (visible ? 1 << 24 : 0);
                if (v > bestV) (best, bestV, bestMat) = (k, v, mi);
            }
            Need(best >= 0, "no skinned sub-object");
            Sub = b.AsSpan(so + best * SubSize, SubSize).ToArray();
            Vertex0 = b.AsSpan(I32(Sub, 8) + 16, VertexSize).ToArray();
            MaterialRec = b.AsSpan(mo + bestMat * MatSize, MatSize).ToArray();
            MainMaterial = CStr(MaterialRec, 0, 128);
            Need(I32(MaterialRec, 132) == LookCount * SoaSize, $"material {MainMaterial} has {I32(MaterialRec, 132) / SoaSize} look entries for {LookCount} looks");
            int soaAt = I32(MaterialRec, 128) + 16;
            for (int l = 0; l < LookCount; l++)
            {
                var soa = b.AsSpan(soaAt + l * SoaSize, SoaSize).ToArray();
                if (I32(soa, 4) > 0) Notes.Add($"look {l}: cloth SNO {I32(soa, 4)} dropped");
                if (I32(soa, 136 + 80) != 0) Notes.Add($"look {l}: second UberMaterial textures dropped");
                Soas.Add(soa);
                SoaTagMaps.Add(Blob(soa, 8, b));
                var tex = Blob(soa, 24 + 76, b);
                SoaTex.Add(Enumerable.Range(0, tex.Length / TexEntrySize).Select(i => tex.AsSpan(i * TexEntrySize, TexEntrySize).ToArray()).ToList());
            }
            DiffuseTexture = SoaTex[0].Where(e => I32(e, 0) == SlotDiffuse).Select(e => I32(e, 8)).FirstOrDefault(-1);
            GlowTexture = SoaTex.SelectMany(l => l).Where(e => I32(e, 0) == SlotGlow).Select(e => I32(e, 8)).FirstOrDefault(-1);
            Need(DiffuseTexture > 0, $"material {MainMaterial} has no diffuse (slot 1) texture in look 0");
            var dropped = SoaTex.SelectMany(l => l).Select(e => I32(e, 0)).Where(s => s != SlotDiffuse && s != SlotGlow && !GenericSlot(s)).Distinct().OrderBy(s => s).ToList();
            if (dropped.Count > 0) Notes.Add($"texture slots {string.Join(", ", dropped)} belong to the template's UV layout and are dropped");
            var hp = Blob(b, St + 228);
            for (int i = 0; i < hp.Length / HardpointSize; i++)
            {
                int o = i * HardpointSize;
                Hardpoints.Add((CStr(hp, o, 64), I32(hp, o + 64), new Quaternion(F32(hp, o + 68), F32(hp, o + 72), F32(hp, o + 76), F32(hp, o + 80)),
                    new Vector3(F32(hp, o + 84), F32(hp, o + 88), F32(hp, o + 92))));
            }
            Center = new Vector3(F32(b, St + 336), F32(b, St + 340), F32(b, St + 344));
            Extent = new Vector3(F32(b, St + 348), F32(b, St + 352), F32(b, St + 356));
            if (I32(b, St + 116) != 0 || I32(b, St + 132) != 0) Notes.Add("bone pulses dropped");
            if (I32(b, St + 200) != 0 || I32(b, St + 208) != 0) Notes.Add("collision capsules dropped");
            if (I32(b, St + 360) != 0 || I32(b, St + 368) != 0) Notes.Add("ragdoll constraints dropped");
            if (Enumerable.Range(0, nb).Any(i => I32(b, bo + i * BoneSize + 276) != 0)) Notes.Add("per-bone collision shapes / constraints (ragdoll) are not written: the new bones have none");
            if (nsub > 1) Notes.Add($"{nsub - 1} other sub-object(s) of the template (death / FX meshes) are not used");
        }

        static byte[] Blob(byte[] b, int serAt) => Blob(b, serAt, b);
        /// <summary>A blob whose SerializeData sits in <paramref name="rec"/> at <paramref name="serAt"/> (offsets into the file <paramref name="file"/>).</summary>
        static byte[] Blob(byte[] rec, int serAt, byte[] file)
        {
            int off = I32(rec, serAt) + 16, size = I32(rec, serAt + 4);
            return size == 0 ? Array.Empty<byte>() : file.AsSpan(off, size).ToArray();
        }
    }

    static string CStr(byte[] b, int at, int max)
    {
        var span = b.AsSpan(at, max);
        int n = span.IndexOf((byte)0);
        return Encoding.ASCII.GetString(span[..(n < 0 ? max : n)]);
    }

    // ============================================================================================ meshes

    /// <summary>The skinned meshes in D3 space, in the rest pose (bind pose = glTF rest pose: positions are skinned with
    /// restWorld × inverseBind, which is the identity for the usual export), with up to 3 influences per vertex (the 3 largest
    /// of glTF's 4, renormalised). Unskinned meshes are attached rigidly to their nearest ancestor joint (else the root).</summary>
    static List<SubMesh> BuildMeshes(GltfModel m, Skeleton sk, AxisMap map, List<int> usedMats, Action<string> say)
    {
        var skin = m.Skins[sk.Skin];
        var jointMats = skin.Joints.Select((node, j) => skin.InverseBind[j] * m.RestWorld(node)).ToArray();
        var subs = new List<SubMesh>();
        int dropped = 0, zero = 0;
        float droppedMax = 0;
        foreach (var p in m.Primitives)
        {
            int n = p.LocalPositions.Length / 3;
            var pos = new Vector3[n];
            Vector3[]? nrm = p.LocalNormals != null || p.Normals != null ? new Vector3[n] : null;
            var bi = new int[n * 3];
            var bw = new float[n * 3];
            for (int i = 0; i < n; i++)
            {
                var v = new Vector3(p.LocalPositions[3 * i], p.LocalPositions[3 * i + 1], p.LocalPositions[3 * i + 2]);
                if (p.Skin >= 0 && p.Joints != null && p.Weights != null)
                {
                    var acc = new Dictionary<int, float>();
                    var pv = Vector3.Zero; var pn = Vector3.Zero; float sum = 0;
                    var ln = p.LocalNormals != null ? new Vector3(p.LocalNormals[3 * i], p.LocalNormals[3 * i + 1], p.LocalNormals[3 * i + 2]) : Vector3.Zero;
                    for (int k = 0; k < 4; k++)
                    {
                        float w = p.Weights[4 * i + k];
                        if (!(w > 0)) continue;
                        int j = p.Joints[4 * i + k];
                        if (j < 0 || j >= skin.Joints.Length) throw new InvalidDataException($"mesh {p.Name}: vertex {i} uses joint {j} of {skin.Joints.Length}");
                        pv += Vector3.Transform(v, jointMats[j]) * w;
                        pn += Vector3.TransformNormal(ln, jointMats[j]) * w;
                        sum += w;
                        int b = sk.JointToBone[j];
                        acc[b] = acc.GetValueOrDefault(b) + w;
                    }
                    if (sum <= 1e-6f) { zero++; pv = Vector3.Transform(v, p.World); pn = Vector3.TransformNormal(ln, p.World); acc = new() { [0] = 1 }; sum = 1; }
                    pos[i] = map.Pos(pv / sum);
                    if (nrm != null) nrm[i] = pn.LengthSquared() > 1e-12f ? Vector3.Normalize(map.Dir(pn)) : Vector3.UnitZ;
                    var top = acc.OrderByDescending(x => x.Value).ToList();
                    if (top.Count > 3) { dropped++; droppedMax = Math.Max(droppedMax, top.Skip(3).Sum(x => x.Value) / acc.Values.Sum()); }
                    top = top.Take(3).ToList();
                    float ts = top.Sum(x => x.Value);
                    for (int k = 0; k < 3; k++)
                    {
                        bi[3 * i + k] = k < top.Count ? top[k].Key : 0;
                        bw[3 * i + k] = k < top.Count ? top[k].Value / ts : 0;
                    }
                }
                else
                {
                    int b = Math.Max(0, sk.BoneOfNodeOrAncestor(m, p.Node));
                    pos[i] = map.Pos(Vector3.Transform(v, p.World));
                    if (nrm != null && p.Normals != null) nrm[i] = Vector3.Normalize(map.Dir(new Vector3(p.Normals[3 * i], p.Normals[3 * i + 1], p.Normals[3 * i + 2])));
                    bi[3 * i] = b; bw[3 * i] = 1;
                }
            }
            int mi = usedMats.IndexOf(p.Material);
            var matName = SafeName(p.Material >= 0 && p.Material < m.Materials.Count ? m.Materials[p.Material].Name : "default", 120);
            var sub = SubMesh.FromD3(p.Name, pos, nrm, p.Uv0, p.Indices, mi, matName);
            sub.BoneIdx = bi; sub.BoneW = bw;
            subs.Add(sub);
            if (p.Skin < 0) say($"mesh {p.Name}: not skinned, attached rigidly to bone {sk.Bones[Math.Max(0, sk.BoneOfNodeOrAncestor(m, p.Node))].Name}");
        }
        if (dropped > 0) say($"warning: {dropped} vertices had 4 influences; D3 keeps 3 (largest dropped share {droppedMax:P1}, the rest renormalised)");
        if (zero > 0) say($"warning: {zero} skinned vertices had no weight; bound to the root bone");
        return subs;
    }

    // ============================================================================================ hardpoints

    public sealed record Hardpoint(string Name, int Bone, Quaternion Q, Vector3 T, string Source);

    /// <summary>
    /// Hardpoints (named attachment points: effects, weapons, the health bar ...). Stock hardpoints store a MODEL-space bind
    /// transform plus the bone they follow (Sandling: HP_Head = the Head bone's bind position). Every template hardpoint is kept
    /// by name:
    /// 1. a glTF node named like it (or any node named HP_*), with no mesh, under a joint: exact (its rest transform);
    /// 2. bone -1 (model-fixed, e.g. HP_aura above the head, Default at the origin): the template position scaled by our
    ///    height / the template's height;
    /// 3. a template bone whose name our skeleton also has (exact, else with trailing digits ignored: Spine3 -> Spine): that
    ///    bone, at its bind position + the template offset × the ratio;
    /// 4. else the bone nearest to the scaled position.
    /// </summary>
    static List<Hardpoint> BuildHardpoints(GltfModel m, Skeleton sk, AxisMap map, SkinTemplate t, float ratio, Action<string> say)
    {
        var fromGltf = new Dictionary<string, Hardpoint>(StringComparer.OrdinalIgnoreCase);
        var tplNames = t.Hardpoints.Select(h => h.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool warned = false;
        foreach (var n in m.Nodes)
        {
            if (n.Mesh >= 0 || sk.NodeToBone.ContainsKey(n.Index)) continue;
            if (!(n.Name.StartsWith("HP_", StringComparison.OrdinalIgnoreCase) || tplNames.Contains(n.Name))) continue;
            int b = sk.BoneOfNodeOrAncestor(m, n.Index);
            var prs = Skeleton.ToD3(m.RestWorld(n.Index), map, ref warned, say, $"hardpoint {n.Name}");
            fromGltf[n.Name] = new Hardpoint(SafeName(n.Name, 63), b, prs.Q, prs.T, "glTF node");
        }
        var list = new List<Hardpoint>();
        foreach (var h in t.Hardpoints)
        {
            if (fromGltf.Remove(h.Name, out var g)) { list.Add(g with { Name = h.Name }); continue; }
            if (h.Bone < 0 || h.Bone >= t.Bones.Count) { list.Add(new Hardpoint(h.Name, -1, h.Q, h.T * ratio, "model, scaled")); continue; }
            var tb = t.Bones[h.Bone];
            static string Stem(string n) => n.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_');
            var ours = sk.Bones.FirstOrDefault(b => b.Name.Equals(tb.Name, StringComparison.OrdinalIgnoreCase))
                ?? sk.Bones.FirstOrDefault(b => Stem(b.Name).Length > 0 && Stem(b.Name).Equals(Stem(tb.Name), StringComparison.OrdinalIgnoreCase));
            if (ours != null) { list.Add(new Hardpoint(h.Name, ours.Index, h.Q, ours.Bind.T + (h.T - tb.Pos) * ratio, $"bone {ours.Name} (template bone {tb.Name})")); continue; }
            var p = h.T * ratio;
            var near = sk.Bones.OrderBy(b => Vector3.DistanceSquared(b.Bind.T, p)).First();
            list.Add(new Hardpoint(h.Name, near.Index, h.Q, p, $"bone {near.Name} (nearest to template bone {tb.Name})"));
        }
        list.AddRange(fromGltf.Values);
        return list;
    }

    // ============================================================================================ appearance

    /// <summary>
    /// The template rebuilt around our skeleton and meshes. Blob order and alignment as stock skinned files (Sandling_A):
    /// materials, then per material its SubObjectAppearance array (one per look) and per look its tag map and texture entries;
    /// looks; bones; sub-objects, then per sub-object vertices, VertInfluences and indices; hardpoints. Every blob is 8-byte
    /// aligned and the file ends at the end of the last one.
    /// </summary>
    public static BuiltAppearance BuildAppearance(SkinTemplate t, int sno, Skeleton sk, List<SubMesh> subs, List<string> matNames,
        List<int> diffuse, List<int> glow, List<Hardpoint> hps, string name, IReadOnlyDictionary<int, int> texSubst, out List<string> notes)
    {
        notes = new List<string>();
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
        void Patch(int at, byte[] data) { long end = o.Length; o.Position = at; o.Write(data); o.Position = end; }

        // ---- materials: one record per glTF material, each with the template main material's look entries ----
        int matsAt = Put(new byte[matNames.Count * MatSize]);
        var matRecs = new List<byte[]>();
        int droppedEntries = 0;
        for (int m = 0; m < matNames.Count; m++)
        {
            var rec = (byte[])t.MaterialRec.Clone();
            Array.Clear(rec, 0, 128);
            Encoding.ASCII.GetBytes(matNames[m]).CopyTo(rec, 0);
            var soas = t.Soas.Select(s => (byte[])s.Clone()).ToList();
            int soasAt = Put(new byte[soas.Count * SoaSize]);
            SetSer(rec, 128, soasAt, soas.Count * SoaSize);
            for (int l = 0; l < soas.Count; l++)
            {
                var soa = soas[l];
                if (I32(soa, 4) > 0) W32(soa, 4, -1);                 // no cloth
                SetSer(soa, 136 + 76, 0, 0);                            // no textures in the second UberMaterial
                int tagAt = Put(t.SoaTagMaps[l]);
                SetSer(soa, 8, tagAt, t.SoaTagMaps[l].Length);
                var tex = new MemoryStream();
                foreach (var e in t.SoaTex[l])
                {
                    var copy = (byte[])e.Clone();
                    int slot = I32(copy, 0);
                    if (slot == SlotDiffuse) W32(copy, 8, diffuse[m]);
                    else if (slot == SlotGlow) W32(copy, 8, glow[m]);
                    else if (!GenericSlot(slot)) { droppedEntries++; continue; }
                    else if (texSubst.TryGetValue(I32(copy, 8), out int repl))
                    {
                        if (repl < 0) { droppedEntries++; continue; }
                        W32(copy, 8, repl);
                    }
                    tex.Write(copy);
                }
                int texAt = Put(tex.ToArray());
                SetSer(soa, 24 + 76, texAt, (int)tex.Length);
            }
            Patch(soasAt, soas.SelectMany(x => x).ToArray());
            matRecs.Add(rec);
        }
        Patch(matsAt, matRecs.SelectMany(x => x).ToArray());
        if (droppedEntries > 0) notes.Add($"{droppedEntries} template texture entries dropped (slots tied to its UV layout)");
        int looksAt = Put(t.Looks);

        // ---- bones ----
        var boneRecs = new byte[sk.Bones.Count * BoneSize];
        var allVerts = subs.SelectMany(s => Enumerable.Range(0, s.VertexCount).Select(i => (s, i))).ToList();
        foreach (var b in sk.Bones)
        {
            int at = b.Index * BoneSize;
            var r = boneRecs.AsSpan(at, BoneSize);
            Encoding.ASCII.GetBytes(b.Name).CopyTo(r);
            BinaryPrimitives.WriteInt32LittleEndian(r[64..], b.Parent);
            // bounds of the vertices this bone moves most, in bone space: AABB (center, half extent) + sphere (AABB center, farthest)
            var pts = allVerts.Where(x => x.s.BoneIdx![3 * x.i] == b.Index && x.s.BoneW![3 * x.i] > 0).Select(x => b.Inv.Apply(x.s.Pos[x.i])).ToList();
            if (pts.Count == 0)
                pts = allVerts.Where(x => Enumerable.Range(0, 3).Any(k => x.s.BoneIdx![3 * x.i + k] == b.Index && x.s.BoneW![3 * x.i + k] > 0)).Select(x => b.Inv.Apply(x.s.Pos[x.i])).ToList();
            if (pts.Count > 0)
            {
                var bmn = pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
                var bmx = pts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
                var bc = (bmn + bmx) / 2;
                WVec(boneRecs, at + 68, bc); WVec(boneRecs, at + 80, (bmx - bmn) / 2);
                WVec(boneRecs, at + 92, bc); WF(boneRecs, at + 104, pts.Max(p => Vector3.Distance(p, bc)));
            }
            void PutPrs(int off, Prs p) { WQuat(boneRecs, at + off, p.Q); WVec(boneRecs, at + off + 16, p.T); WF(boneRecs, at + off + 28, p.S); }
            PutPrs(108, b.Bind); PutPrs(140, b.Inv); PutPrs(172, b.Local); PutPrs(204, b.Bind); PutPrs(236, b.Inv);
            W32(boneRecs, at + 304, I32(t.Bone0, 304));             // SNO (group 27), -1 in the template
            W32(boneRecs, at + 308, I32(t.Bone0, 308));
        }
        int bonesAt = Put(boneRecs);

        // ---- sub-objects ----
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
            var inf = new byte[s.VertexCount * 24];
            for (int i = 0; i < s.VertexCount * 3; i++) { W32(inf, 8 * i, s.BoneIdx![i]); WF(inf, 8 * i + 4, s.BoneW![i]); }
            int fAt = Put(inf);
            var idx = new byte[s.Indices.Length * 2];
            for (int i = 0; i < s.Indices.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(idx.AsSpan(2 * i), s.Indices[i]);
            int iAt = Put(idx);
            W32(rec, 4, s.VertexCount);
            SetSer(rec, 8, vAt, v.Length);
            SetSer(rec, 24, fAt, inf.Length);
            W32(rec, 40, s.Indices.Length);
            SetSer(rec, 44, iAt, idx.Length);
            SetSer(rec, 64, 0, 0);                                     // no cloth
            Array.Clear(rec, 92, 256);
            Encoding.ASCII.GetBytes(s.MaterialName).CopyTo(rec, 92);
            Encoding.ASCII.GetBytes(SafeName($"{name} | {s.MaterialName}", 127)).CopyTo(rec, 220);
            var smn = s.Pos.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var smx = s.Pos.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            WVec(rec, 348, (smn + smx) / 2); WVec(rec, 360, (smx - smn) / 2);
            W32(rec, 372, 0); SetSer(rec, 376, 0, 0);                  // no collision shapes
            subRecs.Add(rec);
        }
        var hp = new byte[hps.Count * HardpointSize];
        for (int i = 0; i < hps.Count; i++)
        {
            int at = i * HardpointSize;
            Encoding.ASCII.GetBytes(hps[i].Name.Length > 63 ? hps[i].Name[..63] : hps[i].Name).CopyTo(hp, at);
            W32(hp, at + 64, hps[i].Bone);
            WQuat(hp, at + 68, hps[i].Q);
            WVec(hp, at + 84, hps[i].T);
        }
        int hpAt = Put(hp);
        var bytes = o.ToArray();
        for (int i = 0; i < subRecs.Count; i++) subRecs[i].CopyTo(bytes, subsAt + i * SubSize);

        // ---- header / Structure / Appearance fields ----
        W32(bytes, 16, sno);
        W32(bytes, St + 4, sk.Bones.Count);
        SetSer(bytes, St + 8, bonesAt, boneRecs.Length);
        W32(bytes, St + 116, 0); SetSer(bytes, St + 128, 0, 0);       // bone pulses
        W32(bytes, St + 136, subs.Count);
        SetSer(bytes, St + 140, subsAt, subs.Count * SubSize);
        WVec(bytes, St + 184, center); WF(bytes, St + 196, radius);
        W32(bytes, St + 200, 0); SetSer(bytes, St + 204, 0, 0);       // collision capsules
        W32(bytes, St + 224, hps.Count);
        SetSer(bytes, St + 228, hpAt, hp.Length);
        WVec(bytes, St + 336, center); WVec(bytes, St + 348, ext);
        W32(bytes, St + 360, 0); SetSer(bytes, St + 364, 0, 0);       // ragdoll constraints
        W32(bytes, AppBase + 416, t.LookCount);
        W32(bytes, AppBase + 420, matNames.Count);
        SetSer(bytes, AppBase + 424, matsAt, matNames.Count * MatSize);
        SetSer(bytes, AppBase + 440, looksAt, t.Looks.Length);
        // +504: shared only by appearances made from the same source model; our own value (CRC32 of the sub-object data)
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(AppBase + 504), Png.Crc32(bytes.AsSpan(subsAt)));
        return new BuiltAppearance { Bytes = bytes, Center = center, Extent = ext, Radius = radius };
    }

    // ============================================================================================ actor

    /// <summary>
    /// The template monster actor with the new appearance and AnimSet. Stock monster rules (all 3,974 Monster actors): actor
    /// AABB = appearance AABB, sphere = appearance sphere with radius + 2 (3,898). The cylinder is hand-authored for monsters
    /// (only 499 equal the AABB height), so the template's is kept and scaled by our size / the template's (its appearance
    /// height × its Scale tag). The Scale tag is set to <paramref name="actorScale"/>; Monster SNO, tags, sounds, collision
    /// and the other fields stay the template's.
    /// </summary>
    static byte[] BuildActor(byte[] template, int sno, int appSno, int animSetSno, int monsterSno, BuiltAppearance app, float sizeRatio, float actorScale, Action<string> say)
    {
        var a = (byte[])template.Clone();
        W32(a, 16, sno);
        W32(a, 36, appSno);
        if (I32(a, 40) != -1) { say($"template PhysMesh {I32(a, 40)} cleared"); W32(a, 40, -1); }
        for (int k = 0; k < 5; k++) WF(a, 44 + 4 * k, F32(template, 44 + 4 * k) * sizeRatio);
        WVec(a, 64, app.Center); WF(a, 76, app.Radius + 2);
        WVec(a, 80, app.Center); WVec(a, 92, app.Extent);
        W32(a, 120, animSetSno);
        W32(a, 124, monsterSno);
        SetScaleTag(a, actorScale, say);
        return a;
    }

    internal static float ScaleTag(byte[] actor)
    {
        float s = 1;
        ForTags(actor, (at, key) => { if (key == ScaleTagKey) s = F32(actor, at + 8); });
        return s;
    }

    static void SetScaleTag(byte[] a, float scale, Action<string> say) =>
        ForTags(a, (at, key) => { if (key == ScaleTagKey) { say($"actor Scale tag {F32(a, at + 8)} -> {scale}"); WF(a, at + 8, scale); } });

    /// <summary>Actor tag map (Ser at file 104): {i32 count, {i32 type, i32 key, 4-byte value}*}; type 4 entries are longer, so stop there.</summary>
    static void ForTags(byte[] a, Action<int, int> f)
    {
        int tm = I32(a, 104) + 16, tmSize = I32(a, 108);
        if (tmSize < 4) return;
        int n = I32(a, tm), at = tm + 4;
        for (int i = 0; i < n && at + 12 <= tm + tmSize; i++, at += 12)
        {
            if (I32(a, at) == 4) break;
            f(at, I32(a, at + 4));
        }
    }

    // ============================================================================================ run

    public static int Run(Options o, GltfModel model, Action<string> say)
    {
        var map = new AxisMap(o.Forward ?? "+z", o.Scale);
        using var d3 = D3Storage.Open(D3Storage.ResolveInstall(o.Install));
        int templateActor = ResolveActor(d3, o.Like, DefaultTemplateActor);
        var actorT = Read(d3, ActorGroup, templateActor);
        int typeT = I32(actorT, 32), appT = I32(actorT, 36), animSetT = I32(actorT, 120), monsterT = I32(actorT, 124);
        d3.Toc.TryGetName(ActorGroup, templateActor, out var tName);
        if (animSetT <= 0) throw new InvalidDataException($"template actor {templateActor} {tName} has no AnimSet; use an animated monster (default {DefaultTemplateActor})");
        if (typeT != 1) say($"warning: template actor {templateActor} is type {typeT}, not a Monster (1)");
        int monster = o.MonsterSno ?? monsterT;
        if (o.MonsterSno is int ms && d3.SnoEntries(MonsterGroup, ms).Count == 0) throw new ArgumentException($"--monster {ms} is not a Monster SNO in the game");
        var t = new SkinTemplate(Read(d3, AppearanceGroup, appT));
        var setT = Read(d3, AnimSetGroup, animSetT);
        float tplScale = ScaleTag(actorT);
        d3.Toc.TryGetName(MonsterGroup, monster, out var monName);
        d3.Toc.TryGetName(AnimSetGroup, animSetT, out var setName);
        say($"template: actor {templateActor} {tName} (type {typeT}, Scale tag {tplScale}), appearance {appT} ({t.Bones.Count} bones, {t.LookCount} looks, material {t.MainMaterial}), AnimSet {animSetT} {setName}, Monster {monster} {monName}");
        foreach (var n in t.Notes) say("template: " + n);

        // ---- skeleton, meshes, clips ----
        var sk = Skeleton.Build(model, map, say);
        say($"skeleton: {sk.Bones.Count} bones: {string.Join(", ", sk.Bones.Select(b => b.Parent < 0 ? b.Name : $"{b.Name}<{sk.Bones[b.Parent].Name}"))}");
        var usedMats = model.Primitives.Select(p => p.Material).Distinct().ToList();
        var subs = BuildMeshes(model, sk, map, usedMats, say);
        var clips = AnimImport.Sample(model, sk, map, o.Fps, say);

        // ---- names / ids ----
        string Tex(int i, string kind) => usedMats.Count == 1 ? $"{o.Name}_{kind}" : $"{o.Name}_{i}_{kind}";
        var names = new List<(int Group, string Name)> { (ActorGroup, o.Name), (AppearanceGroup, o.Name) };
        for (int i = 0; i < usedMats.Count; i++)
        {
            names.Add((TextureGroup, Tex(i, "diff")));
            if (t.UsesGlow) names.Add((TextureGroup, Tex(i, "glow")));
        }
        names.Add((AnimSetGroup, o.Name));
        foreach (var c in clips) names.Add((AnimGroup, c.SnoName = UniqueName(names, $"{o.Name}_{SafeName(c.Name, 40).Replace(' ', '_').Replace('|', '_').Replace('.', '_').Replace('-', '_')}")));
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

        // ---- textures (headers from the template's diffuse / glow textures) ----
        var texDiffT = new TexFile(Read(d3, TextureGroup, t.DiffuseTexture));
        var texGlowT = t.UsesGlow ? new TexFile(Read(d3, TextureGroup, t.GlowTexture)) : null;
        var diffIds = new List<int>(); var glowIds = new List<int>();
        for (int i = 0; i < usedMats.Count; i++)
        {
            var mat = usedMats[i] >= 0 && usedMats[i] < model.Materials.Count ? model.Materials[usedMats[i]] : null;
            Write(TextureGroup, Tex(i, "diff"), BuildTexture(ids[(TextureGroup, Tex(i, "diff"))], mat, texDiffT, say));
            diffIds.Add(ids[(TextureGroup, Tex(i, "diff"))]);
            if (texGlowT == null) continue;
            int gid = ids[(TextureGroup, Tex(i, "glow"))];
            if (mat?.EmissiveRgba != null) Write(TextureGroup, Tex(i, "glow"), BuildTexture(gid, mat.EmissiveWidth, mat.EmissiveHeight, mat.EmissiveRgba, texGlowT, say));
            else
            {
                var f = mat?.EmissiveFactor ?? new float[] { 0, 0, 0 };
                Write(TextureGroup, Tex(i, "glow"), BuildTexture(gid, 4, 4, Flat(new[] { f[0], f[1], f[2], 1f }), texGlowT, say));
                say($"material {mat?.Name}: no emissive texture; glow map = flat emissiveFactor ({string.Join(", ", f.Take(3))})");
            }
            glowIds.Add(gid);
        }

        // ---- appearance ----
        var matNames = usedMats.Select((m, i) => subs.First(s => s.MaterialIndex == i).MaterialName).ToList();
        var allPos = subs.SelectMany(s => s.Pos).ToList();
        float height = allPos.Max(p => p.Z) - allPos.Min(p => p.Z);
        float ratio = height / Math.Max(1e-3f, 2 * t.Extent.Z);
        var hps = BuildHardpoints(model, sk, map, t, ratio, say);
        int appSno = ids[(AppearanceGroup, o.Name)];
        // overlay textures painted for the template model itself (named after it, e.g. Sandling_BodyPulve_alpha in its
        // A_pulverize look): the generic stock equivalent when there is one (689 of 692 stock A_pulverize looks use
        // DeadBodyPulve_alpha), else the entry is dropped
        var texSubst = new Dictionary<int, int>();
        d3.Toc.TryGetName(AppearanceGroup, appT, out var appTName);
        string stem = (appTName ?? "").Split('_')[0] + "_";
        int genericPulve = d3.Toc.InGroup(TextureGroup).Where(x => x.Name.Equals("DeadBodyPulve_alpha", StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).FirstOrDefault(-1);
        foreach (var te in t.SoaTex.SelectMany(l => l))
        {
            int slot = I32(te, 0), tex = I32(te, 8);
            if (slot is SlotDiffuse or SlotGlow || stem.Length < 3 || texSubst.ContainsKey(tex)) continue;
            if (!d3.Toc.TryGetName(TextureGroup, tex, out var tn) || !tn.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
            texSubst[tex] = tn.Contains("Pulve", StringComparison.OrdinalIgnoreCase) ? genericPulve : -1;
            say($"template texture {tex} {tn} (slot {slot}) is the template model's own -> {(texSubst[tex] > 0 ? $"{texSubst[tex]} DeadBodyPulve_alpha" : "dropped")}");
        }
        var app = BuildAppearance(t, appSno, sk, subs, matNames, diffIds, glowIds, hps, o.Name, texSubst, out var notes);
        foreach (var n in notes) say(n);
        Write(AppearanceGroup, o.Name, app.Bytes);

        // ---- anims + AnimSet ----
        var setFile = new AnimSetFileLite(setT);
        var animIds = new Dictionary<AnimImport.Clip, int>();
        foreach (var c in clips)
        {
            int tplAnim = AnimImport.TemplateAnimFor(c.Role, setFile, s => d3.SnoEntries(AnimGroup, s).Count > 0 ? Read(d3, AnimGroup, s) : null, out var tplBytes);
            c.TemplateAnim = tplAnim;
            int sno = ids[(AnimGroup, c.SnoName)];
            Write(AnimGroup, c.SnoName, AnimImport.BuildAnim(tplBytes, sno, appSno, c, sk));
            animIds[c] = sno;
        }
        int setSno = ids[(AnimSetGroup, o.Name)];
        var tagMap = AnimImport.MapTags(setFile, clips, animIds, say);
        Write(AnimSetGroup, o.Name, AnimImport.BuildAnimSet(setT, setSno, tagMap));

        // ---- actor ----
        float tplHeight = 2 * t.Extent.Z * tplScale;
        float sizeRatio = height * o.ActorScale / Math.Max(1e-3f, tplHeight);
        int actorSno = ids[(ActorGroup, o.Name)];
        Write(ActorGroup, o.Name, BuildActor(actorT, actorSno, appSno, setSno, monster, app, sizeRatio, o.ActorScale, say));

        UpdateModJson(o.ModDir, written, say);
        foreach (var w in written) say($"{SnoGroups.Name(w.Group),-10} {w.Sno,7} {w.Name,-32} {w.Size,9:N0} B  {Path.GetRelativePath(o.ModDir, w.Path)}");
        foreach (var h in hps) say($"hardpoint {h.Name,-20} -> {h.Source}");
        foreach (var c in clips) say($"clip {c.Name,-12} -> Anim {animIds[c]} {c.SnoName}: {c.Frames} frames @ {c.FramesPerTick * 60:0.#} fps, {(c.Loop ? "loops" : "plays once")}, role {c.Role}, {c.KeyCount} keys, fields from anim {c.TemplateAnim}");
        var (cc, e) = (app.Center, app.Extent);
        say($"model: {subs.Sum(s => s.VertexCount)} vertices, {subs.Sum(s => s.Indices.Length / 3)} triangles, {subs.Count} mesh(es), {sk.Bones.Count} bones, {clips.Count} anims; bounds (feet) x {cc.X - e.X:F2}..{cc.X + e.X:F2}, y {cc.Y - e.Y:F2}..{cc.Y + e.Y:F2}, z {cc.Z - e.Z:F2}..{cc.Z + e.Z:F2}");
        say($"next: D3ModTool mod-install {o.ModDir} --client <game copy> (or mod-build), restart the server, then spawn actor {actorSno} (e.g. !spawn {actorSno})");
        return 0;
    }

    static string UniqueName(List<(int Group, string Name)> names, string n)
    {
        string r = n; int k = 2;
        while (names.Any(x => x.Group == AnimGroup && x.Name.Equals(r, StringComparison.OrdinalIgnoreCase))) r = $"{n}{k++}";
        return r;
    }
}

/// <summary>The AnimSet fields the importer needs (default tag map + parent), read without DevHub.</summary>
public sealed class AnimSetFileLite
{
    public int Parent { get; }
    public List<(int Type, int Tag, int Value)> Default { get; } = new();

    public AnimSetFileLite(byte[] b)
    {
        if (b.Length < 16 + 480 || BinaryPrimitives.ReadUInt32LittleEndian(b) != 0xDEADBEEF) throw new InvalidDataException("bad AnimSet");
        Parent = I32(b, 16 + 12);
        int off = I32(b, 16 + 16) + 16, size = I32(b, 16 + 20);
        if (size < 4) return;
        int n = I32(b, off), at = off + 4;
        for (int i = 0; i < n && at + 12 <= off + size; i++, at += 12)
        {
            int type = I32(b, at);
            if (type == 4) break;
            Default.Add((type, I32(b, at + 4), I32(b, at + 8)));
        }
    }
}
