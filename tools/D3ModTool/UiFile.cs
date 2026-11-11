using System.Buffers.Binary;
using System.Text;

namespace D3ModTool;

/// <summary>
/// UI layout SNO (group 46, <c>.ui</c>). Verified 2026-11-10: the binary layout is the same in 2.0.1.22044 (format 463)
/// and 2.8.0.99920 (format 471); 65 of the 190 layouts both clients ship differ ONLY in the version int @4.
/// <code>
/// @0  u32 0xDEADBEEF   @4 u32 format version   @16 i32 SNO id   @28 u32 layout hash (unchanged by small edits; meaning unknown)
/// @32 i32 control count   @36 i32 control array offset (+16)   @40 i32 control array size = count * 1856
/// control i at 200 + i*1856: +16 name[512]  +536 path[512]  +1576 {i32 count, i32 offset} styles (264 B)  +1600 {count, offset} properties (16 B)
/// </code>
/// </summary>
public sealed class UiFile
{
    public const uint Magic = 0xDEADBEEF;
    public const int Version201 = 463, Version280 = 471;
    public const int ControlSize = 1856, ControlsAt = 200, StyleSize = 264, PropertySize = 16;

    /// <summary>
    /// The three per-control arrays, each described in the record by {i32 count, i32 offset (+16), i32 byteSize}:
    /// +1576 styles (264 B entries: id + inline string), +1600 int properties (16 B: id, 0, value, 0),
    /// +1624 float properties (16 B: id, 0, float value, 0; ids 0x30-0x32 on text controls, e.g. font size 24.0).
    /// Every tool that moves data must relocate all three (missing +1624 broke whole subtrees, 2026-11-11).
    /// </summary>
    public static readonly (int Field, int EntrySize)[] Arrays = { (1576, StyleSize), (1600, PropertySize), (1624, PropertySize) };

    public readonly record struct Control(int Index, string Name, string Path, int StyleCount, int StyleOffset, int PropCount, int PropOffset);

    public byte[] Bytes { get; }
    public uint Version => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(4));
    public int SnoId => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(16));
    public uint LayoutHash => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(28));
    public int ControlCount => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(32));

    public UiFile(byte[] bytes) => Bytes = bytes;

    int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(at));

    string CStr(int at, int max)
    {
        int n = Bytes.AsSpan(at, max).IndexOf((byte)0);
        return Encoding.ASCII.GetString(Bytes, at, n < 0 ? max : n);
    }

    public IEnumerable<Control> Controls()
    {
        for (int i = 0; i < ControlCount; i++)
        {
            int r = ControlsAt + i * ControlSize;
            yield return new Control(i, CStr(r + 16, 512), CStr(r + 536, 512), I32(r + 1576), I32(r + 1580), I32(r + 1600), I32(r + 1604));
        }
    }

    /// <summary>Structural checks the client would otherwise trip over; empty = valid.</summary>
    public List<string> Validate(int? expectVersion = Version280)
    {
        var errors = new List<string>();
        var b = Bytes;
        if (b.Length < ControlsAt) { errors.Add($"file is {b.Length} bytes, < {ControlsAt}"); return errors; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic) errors.Add("bad magic");
        if (expectVersion != null && Version != expectVersion) errors.Add($"format version {Version}, expected {expectVersion}");
        int count = ControlCount;
        if (count < 0 || count > 16384) { errors.Add($"control count {count}"); return errors; }
        if (I32(36) + 16 != ControlsAt) errors.Add($"control array offset {I32(36)} (+16 != {ControlsAt})");
        if (I32(40) != count * ControlSize) errors.Add($"control array size {I32(40)} != {count}*{ControlSize}");
        if ((long)ControlsAt + (long)count * ControlSize > b.Length) { errors.Add($"{count} controls do not fit in {b.Length} bytes"); return errors; }
        for (int i = 0; i < count; i++)
        {
            int r = ControlsAt + i * ControlSize;
            if (b.AsSpan(r + 16, 512).IndexOf((byte)0) < 0) errors.Add($"control {i}: name not terminated");
            if (b.AsSpan(r + 536, 512).IndexOf((byte)0) < 0) errors.Add($"control {i}: path not terminated");
            // identity hashes (all hold on 100% of stock records): own path, parent path, class name
            string name = CStr(r + 16, 512), path = CStr(r + 536, 512), cls = CStr(r + ClassNameAt, 512);
            if (path.Length > 0)
            {
                if (BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(r + 8)) != PathHash(path + "." + name)) errors.Add($"control {i} ({name}): wrong path hash @8");
                if (BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(r + ParentHashAt)) != PathHash(path)) errors.Add($"control {i} ({name}): wrong parent hash @528");
            }
            if (BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(r + ClassHashAt)) != PathHash(cls)) errors.Add($"control {i} ({name}): wrong class hash @1056");
            foreach (var (field, size, what) in new[] { (1576, StyleSize, "styles"), (1600, PropertySize, "properties"), (1624, PropertySize, "float properties") })
            {
                int n = I32(r + field), off = I32(r + field + 4);
                if (n < 0) errors.Add($"control {i}: {what} count {n}");
                else if (n > 0 && (off < 0 || 16L + off + (long)n * size > b.Length)) errors.Add($"control {i}: {what} [{n} @ {off}] outside the file");
            }
        }
        return errors;
    }

    /// <summary>FNV-1a 64 of the lowercased full control path: the per-control hash stored at record +8 (verified on 39 of 39 non-root records).</summary>
    public static ulong PathHash(string fullPath)
    {
        ulong h = 0xCBF29CE484222325;
        foreach (var c in fullPath.ToLowerInvariant()) { h ^= (byte)c; h *= 0x100000001B3; }
        return h;
    }

    /// <summary>
    /// Copies the subtree rooted at <paramref name="rootFullPath"/> <paramref name="copies"/> times as siblings named
    /// &lt;Name&gt;1..&lt;Name&gt;N (descendant paths follow). Each copy gets its own copy of the style/property data. Their order values
    /// come after every existing control, so a stack container draws them after its stock children.
    /// Only the control array grows; every record's style/property offset is shifted by the inserted size (all data lies after the array).
    /// </summary>
    public static byte[] CloneSubtree(byte[] source, string rootFullPath, int copies)
    {
        var ui = new UiFile(source);
        var errors = ui.Validate(null);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("; ", errors));
        int count = ui.ControlCount;
        string Full(Control c) => (c.Path.Length == 0 ? c.Name : c.Path + "." + c.Name);
        var controls = ui.Controls().ToList();
        var root = controls.FirstOrDefault(c => Full(c) == rootFullPath);
        if (root.Name == null) throw new InvalidDataException($"no control {rootFullPath}");
        // copies are numbered in the ORIGINAL order sequence: the game builds controls by order, so a parent must come before its children
        int OrderOf(Control c) => BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(ControlsAt + c.Index * ControlSize));
        var subtree = controls.Where(c => Full(c) == rootFullPath || Full(c).StartsWith(rootFullPath + ".")).OrderBy(OrderOf).ToList();
        int maxOrder = Enumerable.Range(0, count).Max(i => BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(ControlsAt + i * ControlSize)));
        var existing = controls.Select(Full).ToHashSet();

        int added = copies * subtree.Count, delta = added * ControlSize;
        var output = new byte[source.Length + delta];
        source.AsSpan(0, ControlsAt + count * ControlSize).CopyTo(output);
        source.AsSpan(ControlsAt + count * ControlSize).CopyTo(output.AsSpan(ControlsAt + (count + added) * ControlSize));
        int next = count, order = maxOrder + 1;
        string rootPrefix = rootFullPath;
        for (int k = 1; k <= copies; k++)
        {
            string newRoot = root.Path + "." + root.Name + k;
            if (existing.Contains(newRoot)) throw new InvalidDataException($"{newRoot} already exists");
            foreach (var c in subtree)
            {
                int at = ControlsAt + next++ * ControlSize;
                source.AsSpan(ControlsAt + c.Index * ControlSize, ControlSize).CopyTo(output.AsSpan(at));
                string full = newRoot + Full(c)[rootPrefix.Length..];
                SetIdentity(output, at, full);
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at), order++);
                var rec = output.AsSpan(at, ControlSize).ToArray();
                SetUniqueClass(rec, full[(full.LastIndexOf('.') + 1)..], $"c{k}");
                rec.CopyTo(output, at);
            }
        }
        for (int i = 0; i < count + added; i++)
        {
            int r = ControlsAt + i * ControlSize;
            foreach (var (field, _) in Arrays)
                if (BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(r + field)) > 0)
                    BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(r + field + 4), BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(r + field + 4)) + delta);
        }
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(32), count + added);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(40), (count + added) * ControlSize);
        // every copy gets its OWN style/property blocks (appended), so later edits of one copy (ui-prop, ui-style,
        // ui-style-remove write in place) do not change the original or the other copies (2026-11-11: cloned stash tabs
        // all moved to the last y set)
        var tail = new MemoryStream();
        for (int i = count; i < count + added; i++)
        {
            int r = ControlsAt + i * ControlSize;
            foreach (var (field, size) in Arrays)
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(r + field));
                if (n <= 0) continue;
                int off = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(r + field + 4));
                while ((output.Length + tail.Length) % 8 != 0) tail.WriteByte(0);
                int at = (int)(output.Length + tail.Length);
                tail.Write(output, 16 + off, n * size);
                BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(r + field + 4), at - 16);
            }
        }
        if (tail.Length > 0)
        {
            var grown = new byte[output.Length + tail.Length];
            output.CopyTo(grown, 0);
            tail.ToArray().CopyTo(grown, output.Length);
            output = grown;
        }
        var check = new UiFile(output).Validate(null);
        if (check.Count > 0) throw new InvalidDataException("clone produced an invalid layout: " + string.Join("; ", check.Take(3)));
        return output;
    }

    /// <summary>
    /// Replaces a template reference (style id 158 "Layout:path") on control <paramref name="refFull"/> with a copy of that template's
    /// descendants, so they become ordinary controls of this layout. Needed because the game does not build the static children of a
    /// stack container (type 38) that comes in through a template (seen 2026-11-11 with the AH result list).
    /// Copied records get new names/paths/hashes/orders; their style/property arrays are appended to the end of the file.
    /// </summary>
    public static byte[] InlineTemplate(byte[] main, string refFull, byte[] template, string templateRootFull)
    {
        var mu = new UiFile(main); var tu = new UiFile(template);
        string Full(Control c) => c.Path.Length == 0 ? c.Name : c.Path + "." + c.Name;
        var mControls = mu.Controls().ToList();
        var r = mControls.FirstOrDefault(c => Full(c) == refFull);
        if (r.Name == null) throw new InvalidDataException($"no control {refFull}");
        int OrderOf(byte[] b, Control c) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(ControlsAt + c.Index * ControlSize));
        var copy = tu.Controls().Where(c => Full(c).StartsWith(templateRootFull + ".")).OrderBy(c => OrderOf(template, c)).ToList();
        if (copy.Count == 0) throw new InvalidDataException($"template has nothing under {templateRootFull}");
        int count = mu.ControlCount, added = copy.Count, delta = added * ControlSize;
        int oldArrayEnd = ControlsAt + count * ControlSize;
        int maxOrder = mControls.Max(c => OrderOf(main, c));

        var blobs = new MemoryStream();                 // appended after the existing data
        long dataEnd = main.Length + delta;             // file offset where blobs start
        int Append(byte[] src, int fileOffset, int length)
        {
            int at = (int)(dataEnd + blobs.Length);
            blobs.Write(src, fileOffset, length);
            while (blobs.Length % 8 != 0) blobs.WriteByte(0);
            return at - 16;                              // offsets are stored relative to +16
        }

        var records = new List<byte[]>();
        for (int i = 0; i < count; i++) records.Add(main.AsSpan(ControlsAt + i * ControlSize, ControlSize).ToArray());
        // existing offsets move by delta
        foreach (var rec in records)
            foreach (var (field, _) in Arrays)
                if (BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(field)) > 0)
                    BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(field + 4), BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(field + 4)) + delta);
        // the referencing control loses its template style (158)
        {
            var rec = records[r.Index];
            int sc = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(1576)), so = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(1580)) - delta;
            var kept = new MemoryStream();
            int keptCount = 0;
            for (int k = 0; k < sc; k++)
            {
                int e = 16 + so + k * StyleSize;
                if (BinaryPrimitives.ReadInt32LittleEndian(main.AsSpan(e)) == 158) continue;
                kept.Write(main, e, StyleSize); keptCount++;
            }
            if (keptCount == sc) throw new InvalidDataException($"{refFull} has no template style (158)");
            var keptBytes = kept.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(1576), keptCount);
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(1580), keptCount > 0 ? Append(keptBytes, 0, keptBytes.Length) : 0);
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(1584), keptBytes.Length);
        }
        int order = maxOrder + 1;
        foreach (var c in copy)
        {
            var rec = template.AsSpan(ControlsAt + c.Index * ControlSize, ControlSize).ToArray();
            string full = refFull + Full(c)[templateRootFull.Length..];
            int dot = full.LastIndexOf('.');
            SetIdentity(rec, 0, full);
            BinaryPrimitives.WriteInt32LittleEndian(rec, order++);
            SetUniqueClass(rec, full[(dot + 1)..], "inl" + (order - maxOrder));
            foreach (var (field, size) in Arrays)
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(field));
                if (n <= 0) continue;
                int src = 16 + BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(field + 4));
                BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(field + 4), Append(template, src, n * size));
            }
            records.Add(rec);
        }
        var output = new byte[dataEnd + blobs.Length];
        main.AsSpan(0, ControlsAt).CopyTo(output);
        for (int i = 0; i < records.Count; i++) records[i].CopyTo(output, ControlsAt + i * ControlSize);
        main.AsSpan(oldArrayEnd).CopyTo(output.AsSpan(ControlsAt + records.Count * ControlSize));
        blobs.ToArray().CopyTo(output, dataEnd);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(32), records.Count);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(40), records.Count * ControlSize);
        var check = new UiFile(output).Validate(null);
        if (check.Count > 0) throw new InvalidDataException("inline produced an invalid layout: " + string.Join("; ", check.Take(3)));
        return output;
    }

    /// <summary>
    /// Record +1064 holds the control's class name (every stock control: "$anon$&lt;name&gt;") and +1056 its hash,
    /// FNV-1a 64 of the lowercased class name (129/129 records verified). Copies need their own class, or they collide with the original.
    /// </summary>
    public const int ClassNameAt = 1064, ClassHashAt = 1056;

    /// <summary>D3MODTOOL_CLASS=name: copies get "$anon$&lt;name&gt;" exactly like stock controls instead of a unique suffix (experiment switch).</summary>
    static readonly bool ClassFromName = Environment.GetEnvironmentVariable("D3MODTOOL_CLASS") == "name";

    static void SetUniqueClass(byte[] rec, string name, string suffix)
    {
        string cls = Encoding.ASCII.GetString(rec, ClassNameAt, 512).Split('\0')[0];
        if (!cls.StartsWith("$anon$")) return;   // named classes are shared on purpose; only anonymous ones are per-control
        string unique = ClassFromName ? "$anon$" + name : "$anon$" + name + "_" + suffix;
        if (unique.Length > 511) throw new InvalidDataException($"class name too long: {unique}");
        Array.Clear(rec, ClassNameAt, 512);
        Encoding.ASCII.GetBytes(unique).CopyTo(rec, ClassNameAt);
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(ClassHashAt), PathHash(unique));
    }

    /// <summary>
    /// Sets an int property of one control. An existing entry is changed in place; a missing one is added by appending a new,
    /// larger property array to the end of the file (appended data is read fine, verified 2026-11-11). Returns the new file.
    /// </summary>
    public static byte[] SetProperty(byte[] b, string controlFull, int id, int value)
    {
        var ui = new UiFile(b);
        var c = ui.Controls().FirstOrDefault(x => (x.Path.Length == 0 ? x.Name : x.Path + "." + x.Name) == controlFull);
        if (c.Name == null) throw new InvalidDataException($"no control {controlFull}");
        int existing = -1;
        for (int k = 0; k < c.PropCount; k++)
            if (BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(16 + c.PropOffset + k * PropertySize)) == id) existing = k;
        // ui-clone copies share their original's property block: writing in place would change all of them (2026-11-11:
        // three cloned stash tabs ended up at the same y). Copy-on-write when another control uses the same block.
        bool shared = c.PropCount > 0 && ui.Controls().Any(o => o.Index != c.Index && o.PropOffset == c.PropOffset && o.PropCount > 0);
        if (existing >= 0 && !shared)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16 + c.PropOffset + existing * PropertySize + 8), value);
            return b;
        }
        int pad = (8 - b.Length % 8) % 8, at = b.Length + pad, n = existing >= 0 ? c.PropCount : c.PropCount + 1;
        var output = new byte[at + n * PropertySize];
        b.CopyTo(output, 0);
        if (c.PropCount > 0) b.AsSpan(16 + c.PropOffset, c.PropCount * PropertySize).CopyTo(output.AsSpan(at));
        int slot = existing >= 0 ? existing : c.PropCount;
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at + slot * PropertySize), id);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at + slot * PropertySize + 8), value);
        int r = ControlsAt + c.Index * ControlSize;
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(r + 1600), n);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(r + 1604), at - 16);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(r + 1608), n * PropertySize);
        return output;
    }

    /// <summary>Re-parents/renames a subtree in place: every control at or under <paramref name="fromFull"/> gets the path prefix
    /// <paramref name="toFull"/> (identity rewritten: name, path, own hash, parent hash). The new parent must exist.</summary>
    public static void MoveSubtree(byte[] b, string fromFull, string toFull)
    {
        var ui = new UiFile(b);
        string Full(Control c) => c.Path.Length == 0 ? c.Name : c.Path + "." + c.Name;
        var all = ui.Controls().ToList();
        string newParent = toFull.Contains('.') ? toFull[..toFull.LastIndexOf('.')] : "";
        if (newParent.Length > 0 && !all.Any(c => Full(c) == newParent)) throw new InvalidDataException($"new parent {newParent} does not exist");
        if (all.Any(c => Full(c) == toFull)) throw new InvalidDataException($"{toFull} already exists");
        int moved = 0;
        foreach (var c in all.Where(c => Full(c) == fromFull || Full(c).StartsWith(fromFull + ".")))
        {
            SetIdentity(b, ControlsAt + c.Index * ControlSize, toFull + Full(c)[fromFull.Length..]);
            moved++;
        }
        if (moved == 0) throw new InvalidDataException($"no control {fromFull}");
    }

    /// <summary>Removes every style entry with the given id from one control (in place: entries compacted, count/size updated).
    /// Style (1, "1") marks a control as a TEMPLATE definition; the game does not build such a control as an ordinary child.</summary>
    public static int RemoveStyle(byte[] b, string controlFull, int id)
    {
        var c = new UiFile(b).Controls().FirstOrDefault(x => (x.Path.Length == 0 ? x.Name : x.Path + "." + x.Name) == controlFull);
        if (c.Name == null) throw new InvalidDataException($"no control {controlFull}");
        int kept = 0, start = 16 + c.StyleOffset;
        var entries = new List<byte[]>();
        for (int k = 0; k < c.StyleCount; k++)
        {
            var e = b.AsSpan(start + k * StyleSize, StyleSize).ToArray();
            if (BinaryPrimitives.ReadInt32LittleEndian(e) != id) entries.Add(e);
        }
        foreach (var e in entries) e.CopyTo(b, start + kept++ * StyleSize);
        int r = ControlsAt + c.Index * ControlSize;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(r + 1576), kept);
        if (kept == 0) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(r + 1580), 0);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(r + 1584), kept * StyleSize);
        return c.StyleCount - kept;
    }

    /// <summary>Changes a control's type (record +1048), e.g. 38 stack -> 2 panel.</summary>
    public static void SetType(byte[] b, string controlFull, int type)
    {
        var c = new UiFile(b).Controls().FirstOrDefault(x => (x.Path.Length == 0 ? x.Name : x.Path + "." + x.Name) == controlFull);
        if (c.Name == null) throw new InvalidDataException($"no control {controlFull}");
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(ControlsAt + c.Index * ControlSize + 1048), type);
    }

    /// <summary>
    /// Turns a static row inside a container into the game's dynamic list form (as 2.8 does for e.g. RecipeFilterListBox):
    /// the container becomes a list box (type 13) with a style-187 reference to "&lt;Layout&gt;_main.&lt;templateName&gt;", and the
    /// row subtree MOVES out of LayoutRoot to that top-level template (root type 14 with style (1,"1"), like stock templates).
    /// Rows are then created at runtime with listbox->vtbl[0x138] (AddItem) / vtbl[0x148] (GetItem).
    /// </summary>
    public static byte[] MakeListBox(byte[] source, string containerFull, string rowFull, string templateName)
    {
        var ui = new UiFile(source);
        string Full(Control c) => c.Path.Length == 0 ? c.Name : c.Path + "." + c.Name;
        var controls = ui.Controls().ToList();
        var container = controls.FirstOrDefault(c => Full(c) == containerFull);
        if (container.Name == null) throw new InvalidDataException($"no control {containerFull}");
        if (!rowFull.StartsWith(containerFull + ".")) throw new InvalidDataException("the row must be inside the container");
        string layoutRoot = containerFull[..containerFull.IndexOf('.')];          // e.g. BattleNetAuctionHouseList_main
        string templateFull = layoutRoot + "." + templateName;
        var bytes = (byte[])source.Clone();
        var appended = new MemoryStream();
        int AppendBlob(byte[] blob)
        {
            int at = bytes.Length + (int)appended.Length;
            appended.Write(blob);
            while (appended.Length % 8 != 0) appended.WriteByte(0);
            return at - 16;
        }
        // 1. container: list box type + template style
        {
            int r = ControlsAt + container.Index * ControlSize;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(r + 1048), 13);
            int sc = container.StyleCount;
            var styles = new byte[(sc + 1) * StyleSize];
            if (sc > 0) source.AsSpan(16 + container.StyleOffset, sc * StyleSize).CopyTo(styles);
            BinaryPrimitives.WriteInt32LittleEndian(styles.AsSpan(sc * StyleSize), 187);
            Encoding.ASCII.GetBytes(templateFull).CopyTo(styles, sc * StyleSize + 8);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(r + 1576), sc + 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(r + 1580), AppendBlob(styles));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(r + 1584), styles.Length);
        }
        // 2. row subtree -> top-level template
        foreach (var c in controls.Where(c => Full(c) == rowFull || Full(c).StartsWith(rowFull + ".")))
        {
            int r = ControlsAt + c.Index * ControlSize;
            string full = templateFull + Full(c)[rowFull.Length..];
            SetIdentity(bytes, r, full);
            if (Full(c) != rowFull) continue;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(r + 1048), 14);
            var cls = "$anon$" + templateName;
            Array.Clear(bytes, r + ClassNameAt, 512);
            Encoding.ASCII.GetBytes(cls).CopyTo(bytes, r + ClassNameAt);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(r + ClassHashAt), PathHash(cls));
            if (c.StyleCount == 0)
            {
                var style = new byte[StyleSize];
                BinaryPrimitives.WriteInt32LittleEndian(style, 1);
                style[8] = (byte)'1';
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(r + 1576), 1);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(r + 1580), AppendBlob(style));
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(r + 1584), StyleSize);
            }
        }
        // 3. creation order: templates must exist before the list box that uses them (stock: RecipeFilterListItemTemplate is
        //    order 1, its list box 103). Renumber: layout root, then the template subtree, then everything else (relative order kept).
        {
            int n = ui.ControlCount;
            int Ord(int i) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(ControlsAt + i * ControlSize));
            string PathOf(int i) => Encoding.ASCII.GetString(bytes, ControlsAt + i * ControlSize + 536, 512).Split('\0')[0];
            string NameOf(int i) => Encoding.ASCII.GetString(bytes, ControlsAt + i * ControlSize + 16, 512).Split('\0')[0];
            bool InTemplate(int i) { var p = PathOf(i); var f = p.Length == 0 ? NameOf(i) : p + "." + NameOf(i); return f == templateFull || f.StartsWith(templateFull + "."); }
            var byOrder = Enumerable.Range(0, n).OrderBy(Ord).ToList();
            var root = byOrder.First();
            var sequence = new List<int> { root };
            sequence.AddRange(byOrder.Where(i => i != root && InTemplate(i)));
            sequence.AddRange(byOrder.Where(i => i != root && !InTemplate(i)));
            for (int k = 0; k < sequence.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(ControlsAt + sequence[k] * ControlSize), k);
        }
        var output = new byte[bytes.Length + appended.Length];
        bytes.CopyTo(output, 0);
        appended.ToArray().CopyTo(output, bytes.Length);
        var check = new UiFile(output).Validate(null);
        if (check.Count > 0) throw new InvalidDataException("make-listbox produced an invalid layout: " + string.Join("; ", check.Take(3)));
        return output;
    }

    /// <summary>
    /// Copies a whole subtree (root included) from another layout into this one under a new full path, e.g. a stock row template
    /// to a new top-level template. Arrays are copied and relocated; copies get unique class names; then the whole file is
    /// renumbered so the copied subtree is created first (right after the layout root), as templates must be.
    /// </summary>
    public static byte[] CopySubtree(byte[] main, byte[] source, string sourceRootFull, string newRootFull)
    {
        var mu = new UiFile(main); var su = new UiFile(source);
        string Full(Control c) => c.Path.Length == 0 ? c.Name : c.Path + "." + c.Name;
        int OrderOf(byte[] b, Control c) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(ControlsAt + c.Index * ControlSize));
        var copy = su.Controls().Where(c => Full(c) == sourceRootFull || Full(c).StartsWith(sourceRootFull + ".")).OrderBy(c => OrderOf(source, c)).ToList();
        if (copy.Count == 0) throw new InvalidDataException($"no control {sourceRootFull}");
        if (mu.Controls().Any(c => Full(c) == newRootFull)) throw new InvalidDataException($"{newRootFull} already exists");
        int count = mu.ControlCount, added = copy.Count, delta = added * ControlSize;
        long dataEnd = main.Length + delta;
        var blobs = new MemoryStream();
        int Append(byte[] src, int fileOffset, int length)
        {
            int at = (int)(dataEnd + blobs.Length);
            blobs.Write(src, fileOffset, length);
            while (blobs.Length % 8 != 0) blobs.WriteByte(0);
            return at - 16;
        }
        var records = new List<byte[]>();
        for (int i = 0; i < count; i++)
        {
            var rec = main.AsSpan(ControlsAt + i * ControlSize, ControlSize).ToArray();
            foreach (var (field, _) in Arrays)
                if (BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(field)) > 0)
                    BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(field + 4), BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(field + 4)) + delta);
            records.Add(rec);
        }
        var copiedIdx = new List<int>();
        foreach (var c in copy)
        {
            var rec = source.AsSpan(ControlsAt + c.Index * ControlSize, ControlSize).ToArray();
            string full = newRootFull + Full(c)[sourceRootFull.Length..];
            int dot = full.LastIndexOf('.');
            SetIdentity(rec, 0, full);
            SetUniqueClass(rec, full[(dot + 1)..], "cp");
            foreach (var (field, size) in Arrays)
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(field));
                if (n <= 0) continue;
                int src = 16 + BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(field + 4));
                BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(field + 4), Append(source, src, n * size));
            }
            copiedIdx.Add(records.Count);
            records.Add(rec);
        }
        // renumber: layout root, copied subtree (in its own order), everything else
        var others = Enumerable.Range(0, count).OrderBy(i => BinaryPrimitives.ReadInt32LittleEndian(records[i])).ToList();
        var sequence = new List<int> { others[0] };
        sequence.AddRange(copiedIdx);
        sequence.AddRange(others.Skip(1));
        for (int k = 0; k < sequence.Count; k++) BinaryPrimitives.WriteInt32LittleEndian(records[sequence[k]], k);

        var output = new byte[dataEnd + blobs.Length];
        main.AsSpan(0, ControlsAt).CopyTo(output);
        for (int i = 0; i < records.Count; i++) records[i].CopyTo(output, ControlsAt + i * ControlSize);
        main.AsSpan(ControlsAt + count * ControlSize).CopyTo(output.AsSpan(ControlsAt + records.Count * ControlSize));
        blobs.ToArray().CopyTo(output, dataEnd);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(32), records.Count);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(40), records.Count * ControlSize);
        var check = new UiFile(output).Validate(null);
        if (check.Count > 0) throw new InvalidDataException("copy-subtree produced an invalid layout: " + string.Join("; ", check.Take(3)));
        return output;
    }

    /// <summary>Sets a control's style entry string: replaced in place if the id exists, otherwise added by appending a new,
    /// larger style array (e.g. a click handler, style 41). Returns the new file.</summary>
    public static byte[] SetStyle(byte[] b, string controlFull, int id, string value)
    {
        if (value.Length > StyleSize - 9) throw new InvalidDataException("style string too long");
        var c = new UiFile(b).Controls().FirstOrDefault(x => (x.Path.Length == 0 ? x.Name : x.Path + "." + x.Name) == controlFull);
        if (c.Name == null) throw new InvalidDataException($"no control {controlFull}");
        for (int k = 0; k < c.StyleCount; k++)
        {
            int e = 16 + c.StyleOffset + k * StyleSize;
            if (BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(e)) != id) continue;
            Array.Clear(b, e + 8, StyleSize - 8);
            Encoding.ASCII.GetBytes(value).CopyTo(b, e + 8);
            return b;
        }
        int pad = (8 - b.Length % 8) % 8, at = b.Length + pad, n = c.StyleCount + 1;
        var output = new byte[at + n * StyleSize];
        b.CopyTo(output, 0);
        if (c.StyleCount > 0) b.AsSpan(16 + c.StyleOffset, c.StyleCount * StyleSize).CopyTo(output.AsSpan(at));
        int entry = at + c.StyleCount * StyleSize;
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(entry), id);
        Encoding.ASCII.GetBytes(value).CopyTo(output, entry + 8);
        int r = ControlsAt + c.Index * ControlSize;
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(r + 1576), n);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(r + 1580), at - 16);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(r + 1584), n * StyleSize);
        return output;
    }

    /// <summary>Record +528 (between name and path): FNV-1a 64 of the lowercased PARENT path (0 for the root). The loader
    /// (0x1D6D30) attaches each record to the record whose +8 hash equals this, so it must follow every rename/move/copy
    /// (verified 103/103 and 128/128 on stock layouts; forgetting it left moved/copied controls attached to their old parent).</summary>
    public const int ParentHashAt = 528;

    /// <summary>Writes a record's identity for a full path: name (+16), parent hash (+528), path (+536), own hash (+8).</summary>
    public static void SetIdentity(byte[] b, int recordAt, string full)
    {
        int dot = full.LastIndexOf('.');
        string name = dot < 0 ? full : full[(dot + 1)..], path = dot < 0 ? "" : full[..dot];
        if (name.Length > 511 || path.Length > 511) throw new InvalidDataException($"name/path too long: {full}");
        WriteFixed(b, recordAt + 16, name);
        WriteFixed(b, recordAt + 536, path);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(recordAt + 8), PathHash(full));
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(recordAt + ParentHashAt), path.Length == 0 ? 0 : PathHash(path));
    }

    static void WriteFixed(byte[] b, int at, string text)
    {
        Array.Clear(b, at, 512);
        Encoding.ASCII.GetBytes(text).CopyTo(b, at);
    }

    static void WriteFixed(Span<byte> b, int at, string text)
    {
        b.Slice(at, 512).Clear();
        Encoding.ASCII.GetBytes(text).CopyTo(b[at..]);
    }

    /// <summary>A 2.0.1 (463) layout as a 2.8.0 (471) layout: only the format version changes (see the class comment).</summary>
    public static byte[] Convert201To280(byte[] source)
    {
        var ui = new UiFile(source);
        var errors = ui.Validate(Version201);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("; ", errors));
        var copy = (byte[])source.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(4), Version280);
        return copy;
    }
}
