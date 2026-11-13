using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using static D3ModTool.Import.ModelImport;
using Prs = D3ModTool.Import.SkinnedImport.Prs;

namespace D3ModTool.Import;

/// <summary>
/// glTF animations -> Anim SNOs (group 6, format 118) and the AnimSet (group 8) that maps animation tags to them.
/// Layouts: anim_2.8.0.md. Clip naming convention (case-insensitive; the last word of the name counts, so "Armature|idle" and
/// "golem_attack" work):
/// <list type="bullet">
/// <item><c>idle</c> / <c>neutral</c> / <c>stand</c> -> Neutral, Idle and every tag without a clip of its own (loops)</item>
/// <item><c>walk</c> -> Walk, TownWalk, WalkBack (loops; falls back to run, then idle)</item>
/// <item><c>run</c> / <c>sprint</c> -> Run, TownRun, Flee (loops; falls back to walk, then idle)</item>
/// <item><c>attack</c> / <c>swing</c> / <c>slam</c> / <c>cast</c> -> Attack, Attack2, RangedAttack, SpecialAttack, Cast, GenericCast, Parry</item>
/// <item><c>hit</c> / <c>gethit</c> / <c>hurt</c> -> GetHit and the KnockBack tags</item>
/// <item><c>stunned</c> / <c>stun</c> -> Stunned (loops)</item>
/// <item><c>death</c> / <c>die</c> -> Death and every Death* tag (fire, cold, ...; plays once)</item>
/// <item><c>dead</c> / <c>corpse</c> -> Dead, Dead1-8 (the corpse loop). Without it, a 2-frame loop of the death clip's last
///   frame is generated (<c>&lt;Name&gt;_dead</c>), like stock corpse anims.</item>
/// <item><c>spawn</c> / <c>intro</c> -> Spawn, Spawn2</item>
/// <item>a curated tag name (<c>Attack2</c>, <c>DeathFire</c>, <c>TownWalk</c> ...) or a number (<c>69792</c>) -> exactly that tag.</item>
/// </list>
/// A word <c>loop</c> / <c>once</c> in the name overrides the loop default. Clips that match nothing are still written (reachable
/// by SNO). Every tag of the template's AnimSet is filled, plus the core monster tags, so the client never misses an anim.
/// </summary>
public static class AnimImport
{
    public enum Role { Idle, Walk, Run, Attack, Hit, Stunned, Death, Dead, Spawn, Other }

    /// <summary>Curated tag ids (checked against the anims stock AnimSets map to them; anim_2.8.0.md §3) and their role.</summary>
    public static readonly IReadOnlyDictionary<int, (string Name, Role Role)> Tags = new Dictionary<int, (string, Role)>
    {
        [69632] = ("Neutral", Role.Idle), [69968] = ("Idle", Role.Idle), [70064] = ("Talk", Role.Idle), [270336] = ("Eating", Role.Idle),
        [69744] = ("Walk", Role.Walk), [69760] = ("TownWalk", Role.Walk), [69768] = ("WalkBack", Role.Walk),
        [69728] = ("Run", Role.Run), [69736] = ("TownRun", Role.Run), [70768] = ("Flee", Role.Run),
        [69776] = ("Attack", Role.Attack), [69792] = ("Attack2", Role.Attack), [69840] = ("RangedAttack", Role.Attack),
        [69904] = ("SpecialAttack", Role.Attack), [69920] = ("Cast", Role.Attack), [262144] = ("GenericCast", Role.Attack),
        [70032] = ("Parry", Role.Attack), [70037] = ("HTHParry", Role.Attack), [70144] = ("Ambush", Role.Spawn),
        [69664] = ("GetHit", Role.Hit), [71168] = ("KnockBack", Role.Hit), [71176] = ("KnockBackLand", Role.Hit),
        [71216] = ("KnockBackMegaIntro", Role.Hit), [71218] = ("KnockBackMegaOutro", Role.Hit), [196608] = ("RootBreak", Role.Hit),
        [69680] = ("Stunned", Role.Stunned),
        [69648] = ("Death", Role.Death), [71424] = ("DeathFlyingOrDefault", Role.Death), [71440] = ("SpecialDead", Role.Death),
        [73744] = ("DeathFire", Role.Death), [73760] = ("DeathLightning", Role.Death), [73776] = ("DeathArcane", Role.Death),
        [73792] = ("DeathPoison", Role.Death), [73808] = ("DeathDisintegrate", Role.Death), [73824] = ("DeathPulverise", Role.Death),
        [73840] = ("DeathDecap", Role.Death), [73856] = ("DeathPlague", Role.Death), [73872] = ("DeathDismember", Role.Death),
        [73984] = ("DeathAcid", Role.Death), [74016] = ("DeathCold", Role.Death), [74032] = ("DeathLava", Role.Death),
        [74048] = ("DeathHoly", Role.Death), [74064] = ("DeathSpirit", Role.Death),
        [69712] = ("Dead", Role.Dead), [79168] = ("Dead1", Role.Dead), [79152] = ("Dead2", Role.Dead), [77920] = ("Dead3", Role.Dead),
        [77888] = ("Dead4", Role.Dead), [77904] = ("Dead5", Role.Dead), [77872] = ("Dead6", Role.Dead), [77856] = ("Dead7", Role.Dead),
        [77840] = ("Dead8", Role.Dead),
        [70097] = ("Spawn", Role.Spawn), [291072] = ("Spawn2", Role.Spawn),
    };

    /// <summary>The tag each role is known by: always present in the new AnimSet, and the tag whose template anim lends its
    /// permutation fields (blend times, speed factors ...) to clips of that role.</summary>
    public static readonly IReadOnlyDictionary<Role, int> CoreTags = new Dictionary<Role, int>
    {
        [Role.Idle] = 69632, [Role.Walk] = 69744, [Role.Run] = 69728, [Role.Attack] = 69776, [Role.Hit] = 69664,
        [Role.Stunned] = 69680, [Role.Death] = 69648, [Role.Dead] = 69712, [Role.Spawn] = 70097,
    };
    static readonly int[] ExtraCoreTags = { 69968 };    // Idle (the out-of-combat idle) next to Neutral

    static readonly Dictionary<string, Role> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["idle"] = Role.Idle, ["neutral"] = Role.Idle, ["stand"] = Role.Idle,
        ["walk"] = Role.Walk, ["run"] = Role.Run, ["sprint"] = Role.Run,
        ["attack"] = Role.Attack, ["swing"] = Role.Attack, ["slam"] = Role.Attack, ["cast"] = Role.Attack,
        ["hit"] = Role.Hit, ["gethit"] = Role.Hit, ["hurt"] = Role.Hit,
        ["stunned"] = Role.Stunned, ["stun"] = Role.Stunned,
        ["death"] = Role.Death, ["die"] = Role.Death, ["dying"] = Role.Death,
        ["dead"] = Role.Dead, ["corpse"] = Role.Dead,
        ["spawn"] = Role.Spawn, ["intro"] = Role.Spawn,
    };

    public sealed class Clip
    {
        public string Name = "", SnoName = "";
        public Role Role = Role.Other;
        public int? Tag;                       // explicit tag (curated name or number)
        public bool Loop;
        public int Frames;
        public float FramesPerTick = 0.5f;
        /// <summary>[bone][frame] local transform (to the parent bone), D3 space.</summary>
        public Prs[][] Local = Array.Empty<Prs[]>();
        public bool Generated;
        public int TemplateAnim = -1, KeyCount;
        /// <summary>Root motion: cumulative model-space displacement per frame (null = none), and the average velocity in feet
        /// per game tick (1/60 s), as stock walk / run anims store them (anim_2.8.0.md: permutation +288 / +320).</summary>
        public Vector3[]? Root;
        public Vector3 Velocity;
    }

    /// <summary>
    /// Root motion for moving clips. Stock walk / run anims are in place: the skeleton stays put, the permutation stores the
    /// cumulative root displacement per frame (+288, a straight line along +X: Sandling walk 0 -> 3.9 ft over 30 frames) and the
    /// average velocity (+320) = displacement per frame x frames per tick (3.9 / 30 x 0.5 = 0.065 ft/tick = 3.9 ft/s). The game
    /// moves the actor; the velocity tells it how far one cycle carries the model, so the steps can match the movement.
    /// <list type="bullet">
    /// <item><paramref name="extract"/>: for walk / run clips whose root bone travels (horizontally, ≥ 0.1 ft over the clip), the
    ///   straight-line travel first frame -> last frame is removed from the root bones (sway and bob stay) and stored as root
    ///   displacement + velocity. Author such a clip with the feet planted on the ground while the body moves forward.</item>
    /// <item><paramref name="speeds"/> (ft/s, by clip name or role word): sets the stored travel for a clip, e.g. for an in-place
    ///   clip; an extracted value is replaced (with a note).</item>
    /// <item>A walk / run clip without either keeps velocity 0 and gets a warning (the game cannot pace it: foot sliding).</item>
    /// </list>
    /// </summary>
    public static void ApplyRootMotion(List<Clip> clips, SkinnedImport.Skeleton sk, bool extract, IReadOnlyDictionary<string, float> speeds, Action<string> say)
    {
        var roots = sk.Bones.Where(b => b.Parent < 0).Select(b => b.Index).ToList();
        foreach (var c in clips.Where(c => !c.Generated))
        {
            string word = c.Role.ToString().ToLowerInvariant();
            float? speed = speeds.TryGetValue(c.Name, out var s1) ? s1 : speeds.TryGetValue(word, out var s2) && c.Role is Role.Walk or Role.Run ? s2 : null;
            bool moving = c.Role is Role.Walk or Role.Run;
            int n = c.Frames;
            if (n < 2 || !(moving || speed != null)) continue;
            var t0 = c.Local[roots[0]][0].T; var t1 = c.Local[roots[0]][n - 1].T;
            var travel = new Vector3(t1.X - t0.X, t1.Y - t0.Y, 0);
            if (extract && moving && travel.Length() >= 0.1f)
            {
                for (int f = 0; f < n; f++)
                {
                    var step = travel * (f / (float)(n - 1));
                    foreach (int r in roots) c.Local[r][f] = c.Local[r][f] with { T = c.Local[r][f].T - step };
                }
                c.Root = Enumerable.Range(0, n).Select(f => travel * (f / (float)(n - 1))).ToArray();
                c.Velocity = travel / (n - 1) * c.FramesPerTick;
                double ang = Math.Atan2(travel.Y, travel.X) * 180 / Math.PI;
                say($"clip {c.Name}: root motion {travel.Length():0.##} ft per cycle ({c.Velocity.Length() * 60:0.##} ft/s) taken out of the root bone{(roots.Count > 1 ? "s" : "")} and stored as the anim's travel" +
                    (Math.Abs(ang) > 20 ? $"; WARNING: it goes {ang:0}° off the model's forward axis (+X), check --forward" : ""));
            }
            if (speed is float sp)
            {
                if (c.Velocity != Vector3.Zero) say($"clip {c.Name}: --speed {sp:0.##} ft/s replaces the authored {c.Velocity.Length() * 60:0.##} ft/s (the feet will not match the movement)");
                float perFrame = sp / 60f / c.FramesPerTick;
                c.Root = Enumerable.Range(0, n).Select(f => new Vector3(perFrame * f, 0, 0)).ToArray();
                c.Velocity = new Vector3(sp / 60f, 0, 0);
                say($"clip {c.Name}: stored speed {sp:0.##} ft/s ({c.Velocity.X:0.####} ft/tick)");
            }
            else if (moving && c.Velocity == Vector3.Zero)
                say($"warning: clip {c.Name} ({c.Role}) is in place with no travel: velocity 0, so the game cannot pace its steps to the movement (feet slide). Move the root forward over the cycle with the feet planted, or give --speed {word}=<ft/s>");
        }
    }

    /// <summary>Role, explicit tag and loop flag from a clip name (see the class summary).</summary>
    public static (Role Role, int? Tag, bool? Loop) Classify(string clipName)
    {
        string name = clipName.Contains('|') ? clipName[(clipName.LastIndexOf('|') + 1)..] : clipName;
        var words = Regex.Split(name, @"[^A-Za-z0-9]+").Where(w => w.Length > 0).ToList();
        bool? loop = words.Any(w => w.Equals("loop", StringComparison.OrdinalIgnoreCase)) ? true
            : words.Any(w => w.Equals("once", StringComparison.OrdinalIgnoreCase)) ? false : null;
        foreach (var cand in new[] { name }.Concat(words.AsEnumerable().Reverse()))
        {
            if (Regex.IsMatch(cand, @"^(tag)?\d{5,6}$", RegexOptions.IgnoreCase)) { int id = int.Parse(Regex.Match(cand, @"\d+").Value); return (Tags.TryGetValue(id, out var ti) ? ti.Role : Role.Other, id, loop); }
            if (Words.TryGetValue(cand, out var r)) return (r, null, loop);
            var tag = Tags.FirstOrDefault(kv => kv.Value.Name.Equals(cand, StringComparison.OrdinalIgnoreCase));
            if (tag.Value.Name != null) return (tag.Value.Role, CoreTags.Values.Contains(tag.Key) ? null : tag.Key, loop);
        }
        return (Role.Other, null, loop);
    }

    static bool LoopsByDefault(Role r) => r is Role.Idle or Role.Walk or Role.Run or Role.Stunned or Role.Dead;

    // ============================================================================================ sampling

    /// <summary>
    /// Samples every glTF animation at <paramref name="fps"/> (frames 0 .. round(duration × fps), at least 2) into per-bone
    /// local transforms in D3 space: each frame evaluates the animated node tree (LINEAR / STEP / CUBICSPLINE samplers; rest
    /// values for unanimated properties), then local = world(joint) × world(parent joint)⁻¹, so non-joint nodes in between are
    /// folded in like in the bind pose. Root motion of walk / run clips is handled afterwards (<see cref="ApplyRootMotion"/>).
    /// </summary>
    public static List<Clip> Sample(GltfModel m, SkinnedImport.Skeleton sk, AxisMap map, float fps, Action<string> say)
    {
        var clips = new List<Clip>();
        foreach (var a in m.Animations)
        {
            var (role, tag, loop) = Classify(a.Name);
            float dur = a.Duration;
            int frames = Math.Max(2, (int)Math.Round(dur * fps) + 1);
            var c = new Clip { Name = a.Name, Role = role, Tag = tag, Loop = loop ?? LoopsByDefault(role), Frames = frames, FramesPerTick = fps / 60f };
            c.Local = sk.Bones.Select(_ => new Prs[frames]).ToArray();
            var byNode = a.Channels.GroupBy(ch => ch.Node).ToDictionary(g => g.Key, g => g.ToList());
            bool warned = false;
            for (int f = 0; f < frames; f++)
            {
                float t = Math.Min(dur, f / fps);
                var world = new Dictionary<int, Matrix4x4>();
                Matrix4x4 World(int node)
                {
                    if (node < 0) return Matrix4x4.Identity;
                    if (world.TryGetValue(node, out var w)) return w;
                    var n = m.Nodes[node];
                    Vector3 tr = n.T, sc = n.S; Quaternion q = n.R;
                    if (byNode.TryGetValue(node, out var chs))
                        foreach (var ch in chs)
                        {
                            var v = Eval(ch, t);
                            if (ch.Path == "translation") tr = new Vector3(v[0], v[1], v[2]);
                            else if (ch.Path == "scale") sc = new Vector3(v[0], v[1], v[2]);
                            else q = Quaternion.Normalize(new Quaternion(v[0], v[1], v[2], v[3]));
                        }
                    w = Matrix4x4.CreateScale(sc) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(tr) * World(n.Parent);
                    return world[node] = w;
                }
                foreach (var b in sk.Bones)
                {
                    var rel = World(b.Node);
                    if (b.Parent >= 0) { Matrix4x4.Invert(World(sk.Bones[b.Parent].Node), out var inv); rel *= inv; }
                    var p = SkinnedImport.Skeleton.ToD3(rel, map, ref warned, say, $"clip {a.Name}, bone {b.Name}");
                    // hemisphere-continuous rotations (keys are interpolated)
                    if (f > 0 && Quaternion.Dot(c.Local[b.Index][f - 1].Q, p.Q) < 0) p = p with { Q = Quaternion.Negate(p.Q) };
                    c.Local[b.Index][f] = p;
                }
            }
            var unknown = a.Channels.Select(ch => ch.Node).Distinct().Where(n => !sk.NodeToBone.ContainsKey(n) && !IsAncestorOfJoint(m, sk, n)).ToList();
            if (unknown.Count > 0) say($"warning: clip {a.Name}: channels on {unknown.Count} node(s) outside the skeleton ignored ({string.Join(", ", unknown.Take(4).Select(n => m.Nodes[n].Name))})");
            if (role == Role.Other && tag == null) say($"warning: clip {a.Name}: name matches no animation tag; written as an Anim, but no tag plays it (see the clip naming convention)");
            clips.Add(c);
        }
        if (!clips.Any(c => c.Role == Role.Idle && c.Tag == null))
        {
            if (clips.Count > 0)
            {
                var first = clips[0];
                say($"warning: no idle clip; clip {first.Name} is used for the idle tags");
                clips.Insert(0, new Clip { Name = "idle", Role = Role.Idle, Loop = true, Frames = first.Frames, FramesPerTick = first.FramesPerTick, Local = first.Local, Generated = true });
            }
            else
            {
                say("warning: the model has no animations; a 2-frame bind-pose idle is generated");
                clips.Add(new Clip { Name = "idle", Role = Role.Idle, Loop = true, Frames = 2, FramesPerTick = fps / 60f, Local = sk.Bones.Select(b => new[] { b.Local, b.Local }).ToArray(), Generated = true });
            }
        }
        var death = clips.FirstOrDefault(c => c.Role == Role.Death && c.Tag == null);
        if (death != null && !clips.Any(c => c.Role == Role.Dead && c.Tag == null))
        {
            clips.Add(new Clip
            {
                Name = "dead", Role = Role.Dead, Loop = true, Frames = 2, FramesPerTick = death.FramesPerTick, Generated = true,
                Local = death.Local.Select(tr => new[] { tr[^1], tr[^1] }).ToArray(),
            });
            say($"clip dead: generated from the last frame of {death.Name} (corpse loop)");
        }
        return clips;
    }

    static bool IsAncestorOfJoint(GltfModel m, SkinnedImport.Skeleton sk, int node) =>
        sk.Bones.Any(b => { for (int n = m.Nodes[b.Node].Parent; n >= 0; n = m.Nodes[n].Parent) if (n == node) return true; return false; });

    /// <summary>One sampler value at time <paramref name="t"/> (clamped to the key range).</summary>
    static float[] Eval(GltfModel.Channel ch, float t)
    {
        int comps = ch.Path == "rotation" ? 4 : 3;
        bool cubic = ch.Interpolation == "CUBICSPLINE";
        float[] Val(int k) => ch.Values.AsSpan((cubic ? 3 * k + 1 : k) * comps, comps).ToArray();
        var times = ch.Times;
        int n = times.Length;
        if (n == 0) return new float[comps];
        if (t <= times[0] || n == 1) return Val(0);
        if (t >= times[n - 1]) return Val(n - 1);
        int i = Array.BinarySearch(times, t);
        if (i >= 0) return Val(i);
        i = ~i - 1;
        float t0 = times[i], t1 = times[i + 1], dt = t1 - t0, u = dt > 0 ? (t - t0) / dt : 0;
        if (ch.Interpolation == "STEP") return Val(i);
        var a = Val(i); var b = Val(i + 1);
        var r = new float[comps];
        if (cubic)
        {
            var m0 = ch.Values.AsSpan((3 * i + 2) * comps, comps); var m1 = ch.Values.AsSpan((3 * (i + 1)) * comps, comps);
            float u2 = u * u, u3 = u2 * u;
            for (int k = 0; k < comps; k++)
                r[k] = (2 * u3 - 3 * u2 + 1) * a[k] + (u3 - 2 * u2 + u) * dt * m0[k] + (-2 * u3 + 3 * u2) * b[k] + (u3 - u2) * dt * m1[k];
        }
        else if (comps == 4)
        {
            var q = Quaternion.Slerp(new Quaternion(a[0], a[1], a[2], a[3]), new Quaternion(b[0], b[1], b[2], b[3]), u);
            return new[] { q.X, q.Y, q.Z, q.W };
        }
        else for (int k = 0; k < comps; k++) r[k] = a[k] + (b[k] - a[k]) * u;
        if (comps == 4)
        {
            var q = Quaternion.Normalize(new Quaternion(r[0], r[1], r[2], r[3]));
            return new[] { q.X, q.Y, q.Z, q.W };
        }
        return r;
    }

    // ============================================================================================ tags

    /// <summary>The template AnimSet's anim for a role's core tag (falls back to its idle anim when that one is missing or
    /// its permutation has flag bit 0, which stock sets on 2-frame ragdoll deaths). Its fields (blend ticks, speed / ragdoll
    /// factors, contact frames, hardpoint link) are copied into our clip's permutation.</summary>
    public static int TemplateAnimFor(Role role, AnimSetFileLite set, Func<int, byte[]?> read, out byte[] bytes)
    {
        int Find(int tag) => set.Default.Where(e => e.Tag == tag && e.Value > 0).Select(e => e.Value).FirstOrDefault(-1);
        foreach (int tag in new[] { CoreTags.GetValueOrDefault(role, CoreTags[Role.Idle]), CoreTags[Role.Idle], 69968 })
        {
            int sno = Find(tag);
            if (sno <= 0 || read(sno) is not { } b) continue;
            int po = I32(b, 16 + 24) + 16;
            if (tag != CoreTags[Role.Idle] && tag != 69968 && (I32(b, po) & 1) != 0) continue;
            bytes = b;
            return sno;
        }
        int any = set.Default.Where(e => e.Value > 0).Select(e => e.Value).FirstOrDefault(-1);
        bytes = any > 0 ? read(any) ?? throw new InvalidDataException("template AnimSet has no readable anim") : throw new InvalidDataException("template AnimSet has no anims");
        return any;
    }

    /// <summary>
    /// The new default tag map: every tag of the template's (in its order; tags the template sets to -1 stay -1), then the core
    /// monster tags, every curated tag of a role the glTF has a clip for (a death clip fills Death and all 15 Death* variants,
    /// so the emulator's random death pick, which looks at the Death* tags and 69712, mostly finds it), and the explicit tags
    /// of our clips. Each tag gets: the clip named for exactly that tag, else the clip of its role (walk/run fall back to each
    /// other), else idle.
    /// </summary>
    public static List<(int Tag, int Anim)> MapTags(AnimSetFileLite set, List<Clip> clips, Dictionary<Clip, int> ids, Action<string> say)
    {
        Clip? ByRole(Role r) => clips.FirstOrDefault(c => c.Role == r && c.Tag == null && !(c.Generated && r != Role.Dead && r != Role.Idle));
        var idle = clips.First(c => c.Role == Role.Idle && c.Tag == null);
        Clip For(int tag)
        {
            var exact = clips.FirstOrDefault(c => c.Tag == tag);
            if (exact != null) return exact;
            var role = Tags.TryGetValue(tag, out var ti) ? ti.Role : Role.Other;
            return role switch
            {
                Role.Walk => ByRole(Role.Walk) ?? ByRole(Role.Run) ?? idle,
                Role.Run => ByRole(Role.Run) ?? ByRole(Role.Walk) ?? idle,
                Role.Other or Role.Idle => ByRole(Role.Idle) ?? idle,
                Role.Dead => ByRole(Role.Dead) ?? idle,
                _ => ByRole(role) ?? idle,
            };
        }
        var tags = new List<(int Tag, int Anim)>();
        foreach (var e in set.Default)
        {
            if (e.Type != 2) continue;
            tags.Add((e.Tag, e.Value <= 0 ? -1 : ids[For(e.Tag)]));
        }
        var provided = clips.Where(c => !c.Generated && c.Tag == null && c.Role is not (Role.Idle or Role.Other)).Select(c => c.Role).ToHashSet();
        var roleTags = Tags.Where(kv => provided.Contains(kv.Value.Role)).Select(kv => kv.Key).OrderBy(t => t);
        foreach (int tag in CoreTags.Values.Concat(ExtraCoreTags).Concat(roleTags).Concat(clips.Where(c => c.Tag != null).Select(c => c.Tag!.Value)))
            if (!tags.Any(x => x.Tag == tag)) tags.Add((tag, ids[For(tag)]));
        var summary = tags.GroupBy(x => x.Anim).Select(g => $"{(g.Key < 0 ? "-1" : clips.First(c => ids[c] == g.Key).Name)}: {string.Join(" ", g.Select(x => Tags.TryGetValue(x.Tag, out var ti) ? ti.Name : x.Tag.ToString()))}");
        say($"AnimSet: {tags.Count} tags; " + string.Join("; ", summary));
        return tags;
    }

    // ============================================================================================ writers

    const int PermSize = 408, CurveSize = 24, NameSize = 64;

    /// <summary>
    /// An Anim file: the template anim's 16-byte header and 56-byte struct (flags 0, our playback mode, appearance, one
    /// permutation) and its first permutation's 408 bytes with our name, frames per tick, bones, frame count, curves, root
    /// displacement and velocity (<see cref="ApplyRootMotion"/>; zero = in place) and the template's other fields (blend ticks, speed / ragdoll factors, contact frames and
    /// the earliest interrupt scaled to our length, contact offsets, hardpoint link); no events.
    /// Blob order and 8-byte alignment as stock: permutation, bone names, translation curves, their keys, rotation curves,
    /// their keys, scale curves, their keys, root positions, nonlinear offsets.
    /// </summary>
    public static byte[] BuildAnim(byte[] template, int sno, int appSno, Clip c, SkinnedImport.Skeleton sk)
    {
        int nb = sk.Bones.Count, frames = c.Frames;
        var o = new MemoryStream();
        o.Write(template, 0, 16 + 56);
        int Align() { while (o.Length % 8 != 0) o.WriteByte(0); return (int)o.Length; }
        int Put(byte[] d) { int at = Align(); o.Write(d); return at; }
        void SetSer(byte[] rec, int at, int fileOffset, int size) { W32(rec, at, size == 0 ? 0 : fileOffset - 16); W32(rec, at + 4, size); }

        int tpo = I32(template, 16 + 24) + 16;
        var perm = template.AsSpan(tpo, PermSize).ToArray();
        int tplFrames = Math.Max(1, I32(perm, 144));
        int permAt = Put(new byte[PermSize]);

        var names = new byte[nb * NameSize];
        for (int b = 0; b < nb; b++) Encoding.ASCII.GetBytes(sk.Bones[b].Name).CopyTo(names, b * NameSize);
        int namesAt = Put(names);

        // keyframe reduction: keep a frame only where linear interpolation between the kept neighbours would miss it
        var tKeys = new List<int>[nb]; var rKeys = new List<int>[nb]; var sKeys = new List<int>[nb];
        for (int b = 0; b < nb; b++)
        {
            var tr = c.Local[b];
            tKeys[b] = Reduce(frames, (i, j, k) => Vector3.Distance(Vector3.Lerp(tr[i].T, tr[j].T, (k - i) / (float)(j - i)), tr[k].T) < 5e-4f);
            rKeys[b] = Reduce(frames, (i, j, k) =>
            {
                var q = Quaternion.Normalize(Quaternion.Lerp(tr[i].Q, tr[j].Q, (k - i) / (float)(j - i)));
                return Math.Abs(Quaternion.Dot(q, tr[k].Q)) > 1 - 2e-8f;
            });
            sKeys[b] = Reduce(frames, (i, j, k) => Math.Abs(tr[i].S + (tr[j].S - tr[i].S) * (k - i) / (j - i) - tr[k].S) < 1e-4f);
        }
        int Curves(List<int>[] keys, int keySize, Action<byte[], int, int, int> writeKey)
        {
            var curves = new byte[nb * CurveSize];
            int curvesAt = Put(curves);
            for (int b = 0; b < nb; b++)
            {
                var data = new byte[keys[b].Count * keySize];
                for (int k = 0; k < keys[b].Count; k++) { W32(data, k * keySize, keys[b][k]); writeKey(data, k * keySize + 4, b, keys[b][k]); }
                int at = Put(data);
                W32(curves, b * CurveSize, keys[b].Count);
                SetSer(curves, b * CurveSize + 4, at, data.Length);
            }
            long end = o.Length; o.Position = curvesAt; o.Write(curves); o.Position = end;
            return curvesAt;
        }
        int tAt = Curves(tKeys, 16, (d, at, b, f) => WVec(d, at, c.Local[b][f].T));
        int rAt = Curves(rKeys, 12, (d, at, b, f) =>
        {
            var q = c.Local[b][f].Q;
            BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(at), Q16(q.X)); BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(at + 2), Q16(q.Y));
            BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(at + 4), Q16(q.Z)); BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(at + 6), Q16(q.W));
        });
        int sAt = Curves(sKeys, 8, (d, at, b, f) => WF(d, at, c.Local[b][f].S));
        var rootData = new byte[frames * 12];
        if (c.Root != null) for (int f = 0; f < frames; f++) WVec(rootData, 12 * f, c.Root[f]);
        int rootAt = Put(rootData);
        int nlAt = Put(new byte[frames * 12]);
        var bytes = o.ToArray();

        int tk = tKeys.Sum(k => k.Count), rk = rKeys.Sum(k => k.Count), skc = sKeys.Sum(k => k.Count);
        c.KeyCount = tk + rk + skc;
        // permutation
        W32(perm, 0, 0);
        Array.Clear(perm, 4, 68);
        Encoding.ASCII.GetBytes(c.SnoName.Length > 67 ? c.SnoName[..67] : c.SnoName).CopyTo(perm, 4);
        WF(perm, 72, c.FramesPerTick);
        // +76..+88: stock stats (5, rotation keys / (2 x bones), translation keys / (2 x bones), frames / 2) [G]
        WF(perm, 80, rk / (2f * nb)); WF(perm, 84, tk / (2f * nb)); WF(perm, 88, frames / 2f);
        W32(perm, 100, 100);
        W32(perm, 120, nb);
        SetSer(perm, 124, namesAt, names.Length);
        W32(perm, 144, frames);
        SetSer(perm, 148, tAt, nb * CurveSize);
        SetSer(perm, 168, rAt, nb * CurveSize);
        SetSer(perm, 184, sAt, nb * CurveSize);
        float scale = (frames - 1) / (float)Math.Max(1, tplFrames - 1);
        for (int i = 0; i < 4; i++) { float cf = F32(perm, 200 + 4 * i); if (cf > 0) WF(perm, 200 + 4 * i, cf * scale); }
        if (F32(perm, 264) > 0) WF(perm, 264, F32(perm, 264) * scale);
        SetSer(perm, 268, 0, 0); W32(perm, 276, 0);                 // no events
        SetSer(perm, 288, rootAt, frames * 12);
        SetSer(perm, 304, nlAt, frames * 12);
        WVec(perm, 320, c.Velocity);                                 // average travel per tick (0 = in place)
        perm.CopyTo(bytes, permAt);

        // struct
        W32(bytes, 16, sno);
        W32(bytes, 16 + 12, 0);
        W32(bytes, 16 + 16, c.Loop ? 2 : 0);
        W32(bytes, 16 + 20, appSno);
        SetSer(bytes, 16 + 24, permAt, PermSize);
        W32(bytes, 16 + 32, 1);
        return bytes;
    }

    static short Q16(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767), -32767, 32767);

    /// <summary>Greedy reduction: from the last kept frame, extend the segment while every frame inside it is reproduced
    /// (<paramref name="ok"/>(start, end, frame)); first and last frames are always kept.</summary>
    static List<int> Reduce(int frames, Func<int, int, int, bool> ok)
    {
        var keep = new List<int> { 0 };
        int a = 0;
        for (int b = 2; b < frames; b++)
        {
            bool fits = true;
            for (int k = a + 1; k < b && fits; k++) fits = ok(a, b, k);
            if (!fits) { keep.Add(b - 1); a = b - 1; }
        }
        if (frames > 1) keep.Add(frames - 1);
        return keep;
    }

    /// <summary>
    /// An AnimSet file: the template's 16-byte header and 480-byte struct with our id, no parent, our default tag map and 28
    /// empty stance maps (count 0, 4 bytes each, like stock sets without stance variants). Tag maps are packed 4-byte aligned
    /// in that order, as in stock files.
    /// </summary>
    public static byte[] BuildAnimSet(byte[] template, int sno, List<(int Tag, int Anim)> tags)
    {
        var o = new MemoryStream();
        o.Write(template, 0, 16 + 480);
        var def = new byte[4 + 12 * tags.Count];
        W32(def, 0, tags.Count);
        for (int i = 0; i < tags.Count; i++) { W32(def, 4 + 12 * i, 2); W32(def, 8 + 12 * i, tags[i].Tag); W32(def, 12 + 12 * i, tags[i].Anim); }
        int defAt = (int)o.Length; o.Write(def);
        var maps = new int[28];
        for (int i = 0; i < 28; i++) { maps[i] = (int)o.Length; o.Write(new byte[4]); }
        var b = o.ToArray();
        W32(b, 16, sno);
        W32(b, 16 + 12, -1);
        W32(b, 16 + 16, defAt - 16); W32(b, 16 + 20, def.Length);
        for (int i = 0; i < 28; i++) { W32(b, 16 + 32 + 16 * i, maps[i] - 16); W32(b, 16 + 36 + 16 * i, 4); }
        return b;
    }
}
