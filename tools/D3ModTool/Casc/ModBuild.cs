using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using D3Casc;

namespace D3ModTool.Casc;

/// <summary>
/// Writes mod files into a COPY of the client (never the official install): one new archive <c>data.NNN</c> we own,
/// the changed sub-roots, CoreTOC, root, encoding file, the 16 local index buckets, a new build config and the copy's .build.info.
/// The first build snapshots the copy's original .build.info and live .idx files to <c>.d3mod\base</c>; every build starts from
/// that snapshot, so builds are repeatable and <see cref="Restore"/> returns the copy to stock.
/// Mod package layout: <c>mods\&lt;id&gt;\data\&lt;RootDir&gt;\&lt;Group&gt;\&lt;Name&gt;&lt;ext&gt;</c>, e.g. data\Base\UI\BattleNetAuctionHouse.ui.
/// A file whose SNO id (@16) is already in CoreTOC replaces that SNO; any other is added as a new SNO (group from the folder, name from the file).
/// </summary>
public static class ModBuild
{
    /// <summary>The official install (never written to): %D3_INSTALL_DIR% or the one Windows has registered.</summary>
    public static string OfficialInstall => D3Casc.D3Storage.DefaultInstall;
    static string StateDir(string client) => Path.Combine(client, ".d3mod");
    static string BaseDir(string client) => Path.Combine(StateDir(client), "base");
    static string ReportPath(string client) => Path.Combine(StateDir(client), "build.json");

    static void RefuseOfficial(string client)
    {
        var full = Path.GetFullPath(client).TrimEnd('\\');
        if (string.Equals(full, Path.GetFullPath(OfficialInstall).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("refusing to write into the official install; mods go into a copy of the game folder (--client / D3_CLIENT_DIR)");
        if (!File.Exists(Path.Combine(client, ".build.info"))) throw new InvalidOperationException($"{client} is not a client folder");
    }

    /// <summary>
    /// <c>Data\data\shmem</c> records the storage's free-space state, including how far each archive is used. After the client has run
    /// on one build it records our archive's size; a later build with a smaller archive then fails with
    /// "tact::ClientHandler::Create() failed with error '1'" (seen 2026-11-11). So the snapshot keeps a clean shmem (the stock file with
    /// the copy's path in its name field) and every build/restore puts it back.
    /// </summary>
    static void EnsureShmemSnapshot(string client, Action<string> say)
    {
        var snap = Path.Combine(BaseDir(client), "shmem");
        if (File.Exists(snap)) return;
        var data = Path.Combine(client, "Data", "data");
        var source = File.Exists(Path.Combine(data, "shmem.orig")) ? Path.Combine(data, "shmem.orig") : Path.Combine(data, "shmem");
        var b = File.ReadAllBytes(source);
        var full = Path.GetFullPath(client).TrimEnd('\\');
        var name = "Global\\" + char.ToLowerInvariant(full[0]) + full[1..].Replace('\\', '/') + "/Data/data";
        if (Encoding.ASCII.GetString(b, 8, 7) != "Global\\" || name.Length >= 256) throw new InvalidDataException($"unexpected shmem layout in {source}");
        Array.Clear(b, 8, 256);
        Encoding.ASCII.GetBytes(name).CopyTo(b, 8);
        Directory.CreateDirectory(BaseDir(client));
        File.WriteAllBytes(snap, b);
        say($"clean shmem ({name}) from {Path.GetFileName(source)} -> {snap}");
    }

    static void ResetShmem(string client) =>
        File.Copy(Path.Combine(BaseDir(client), "shmem"), Path.Combine(client, "Data", "data", "shmem"), true);

    static void EnsureSnapshot(string client, Action<string> say)
    {
        var dir = BaseDir(client);
        if (File.Exists(Path.Combine(dir, ".build.info"))) { EnsureShmemSnapshot(client, say); return; }
        if (File.Exists(ReportPath(client))) throw new InvalidOperationException("build.json exists but the base snapshot is missing; restore the copy by hand");
        Directory.CreateDirectory(Path.Combine(dir, "idx"));
        foreach (var name in LocalIndex.Latest(Path.Combine(client, "Data", "data")).Values)
            File.Copy(Path.Combine(client, "Data", "data", name), Path.Combine(dir, "idx", name));
        File.Copy(Path.Combine(client, ".build.info"), Path.Combine(dir, ".build.info"));
        EnsureShmemSnapshot(client, say);
        say($"snapshot of the stock storage state -> {dir}");
    }

    sealed record ModFile(string Mod, string RootDir, int Group, int Sno, string Name, string Source, byte[] Bytes, bool IsNew);

    public static int Build(string client, IReadOnlyList<string> modDirs, Action<string> say)
    {
        RefuseOfficial(client);
        EnsureSnapshot(client, say);
        var basis = new LocalStorage(client, BaseDir(client));
        say($"base build {basis.BuildKey}, {basis.Encoding.ContentKeys:N0} content keys, archives 0..{basis.MaxArchive}");

        var root = new D3RootFile(basis.RootRaw);
        var subRoots = new Dictionary<string, D3SubRoot>();
        D3SubRoot Sub(string dir)
        {
            if (subRoots.TryGetValue(dir, out var s)) return s;
            var d = root.Dirs.FirstOrDefault(x => x.Name.Equals(dir, StringComparison.OrdinalIgnoreCase));
            if (d.Name == null) throw new InvalidDataException($"root has no directory '{dir}' ({string.Join(", ", root.Dirs.Select(x => x.Name))})");
            return subRoots[d.Name] = new D3SubRoot(basis.ReadCKey(d.CKey));
        }
        var baseSub = Sub("Base");
        var tocRaw = basis.ReadCKey(basis.Raw(baseSub, "CoreTOC.dat"));
        var toc = new CoreTocFile(tocRaw);
        var tocNames = CoreToc.Parse(tocRaw);

        // collect the mod files (later mods override earlier ones)
        var files = new Dictionary<(string, int), ModFile>();
        foreach (var modDir in modDirs)
        {
            var mod = Path.GetFileName(modDir.TrimEnd('\\'));
            var data = Path.Combine(modDir, "data");
            if (!Directory.Exists(data)) { say($"{mod}: no data folder, nothing for the client"); continue; }
            foreach (var path in Directory.GetFiles(data, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(data, path).Split('\\');
                if (rel.Length != 3) throw new InvalidDataException($"{mod}: {string.Join('\\', rel)} is not <RootDir>\\<Group>\\<file>");
                if (!SnoGroups.TryParse(rel[1], out int group)) throw new InvalidDataException($"{mod}: unknown SNO group folder '{rel[1]}'");
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0xDEADBEEF) throw new InvalidDataException($"{mod}: {path} is not an SNO file");
                int sno = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
                var name = Path.GetFileNameWithoutExtension(path);
                bool known = tocNames.TryGetName(group, sno, out var tocName);
                if (known && !tocName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{mod}: {path} has SNO {sno}, which CoreTOC names '{tocName}'");
                if (!known && toc.Contains(sno)) throw new InvalidDataException($"{mod}: SNO {sno} ({name}) collides with an SNO of another group");
                if (group == 46)
                {
                    var errors = new UiFile(bytes).Validate();
                    if (errors.Count > 0) throw new InvalidDataException($"{mod}: {name}.ui is invalid: {string.Join("; ", errors.Take(3))}");
                }
                files[(rel[0], sno)] = new ModFile(mod, rel[0], group, sno, name, path, bytes, !known);
            }
        }
        if (files.Count == 0) { say("no mod files: nothing to build (use mod-restore for a stock copy)"); return 1; }

        // the archive we own: one past the snapshot's highest; remove the one a previous build wrote
        ResetShmem(client);
        int archive = basis.MaxArchive + 1;
        foreach (var stale in Directory.GetFiles(basis.DataDir, "data.*"))
            if (int.TryParse(Path.GetExtension(stale).TrimStart('.'), out int n) && n > basis.MaxArchive) { File.Delete(stale); say($"removed previous mod archive {Path.GetFileName(stale)}"); }
        var archivePath = Path.Combine(basis.DataDir, $"data.{archive:D3}");
        var added = new List<LocalIndex.Entry>();
        var report = new JsonArray();
        long offset = 0;
        var enc = basis.Encoding;

        using (var fs = new FileStream(archivePath + ".tmp", FileMode.Create, FileAccess.Write))
        {
            byte[] Store(byte[] content)
            {
                var ckey = Keys.Md5(content);
                var (blob, ekey) = Blte.Encode(content);
                var header = ArchiveEntryHeader.Make(ekey, blob.Length, archive, offset);
                fs.Write(header);
                fs.Write(blob);
                added.Add(new LocalIndex.Entry(ekey[..9], archive, offset, header.Length + blob.Length));
                offset += header.Length + blob.Length;
                enc.Add(ckey, ekey, content.Length, blob.Length, Blte.ESpec);
                return ckey;
            }

            foreach (var f in files.Values.OrderBy(f => f.RootDir).ThenBy(f => f.Sno))
            {
                var ckey = Store(f.Bytes);
                Sub(f.RootDir).SetSnos(new[] { (f.Sno, ckey) });
                if (f.IsNew) toc.Add(f.Group, f.Sno, f.Name);
                report.Add(new JsonObject
                {
                    ["mod"] = f.Mod, ["path"] = $"{f.RootDir}\\{SnoGroups.Name(f.Group)}\\{f.Name}{SnoGroups.Ext(f.Group)}",
                    ["sno"] = f.Sno, ["new"] = f.IsNew, ["size"] = f.Bytes.Length, ["ckey"] = Keys.Hex(ckey),
                });
                say($"{f.Mod}: {(f.IsNew ? "add" : "replace")} {f.RootDir}\\{SnoGroups.Name(f.Group)}\\{f.Name} (SNO {f.Sno}, {f.Bytes.Length:N0} B)");
            }
            if (files.Values.Any(f => f.IsNew))
            {
                var newToc = toc.Build();
                baseSub.SetNamed("CoreTOC.dat", Store(newToc));
                say($"CoreTOC.dat: +{files.Values.Count(f => f.IsNew)} names ({tocRaw.Length:N0} -> {newToc.Length:N0} B)");
            }
            var newSubKeys = subRoots.ToDictionary(kv => kv.Key, kv => Store(kv.Value.Raw));
            var rootCKey = Store(root.WithSubRoots(newSubKeys));
            var encodingBytes = enc.Build();
            var (encBlob, encEKey) = Blte.Encode(encodingBytes);
            var encHeader = ArchiveEntryHeader.Make(encEKey, encBlob.Length, archive, offset);
            fs.Write(encHeader);
            fs.Write(encBlob);
            added.Add(new LocalIndex.Entry(encEKey[..9], archive, offset, encHeader.Length + encBlob.Length));
            offset += encHeader.Length + encBlob.Length;
            fs.Flush(true);

            // index buckets: the snapshot's bucket files + our entries, written under the same names (same versions) into the copy
            foreach (var (bucket, name) in basis.IdxFiles)
            {
                var mine = added.Where(a => Keys.Bucket(a.Key9) == bucket);
                File.WriteAllBytes(Path.Combine(basis.DataDir, name), basis.Indexes[bucket].With(mine));
            }

            var config = LocalStorage.SetConfigValues(basis.BuildConfigRaw, new Dictionary<string, string>
            {
                ["root"] = Keys.Hex(rootCKey),
                ["encoding"] = $"{Keys.Hex(Keys.Md5(encodingBytes))} {Keys.Hex(encEKey)}",
                ["encoding-size"] = $"{encodingBytes.Length} {encBlob.Length}",
            });
            var buildKey = Keys.Hex(Keys.Md5(config));
            var configPath = LocalStorage.ConfigPath(client, buildKey);
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllBytes(configPath, config);
            File.WriteAllText(Path.Combine(client, ".build.info"), basis.BuildInfoText.Replace(basis.BuildKey, buildKey), new UTF8Encoding(false));

            var summary = new JsonObject
            {
                ["built"] = DateTime.Now.ToString("s"), ["base_build_key"] = basis.BuildKey, ["build_key"] = buildKey,
                ["archive"] = $"data.{archive:D3}", ["archive_bytes"] = offset, ["root_ckey"] = Keys.Hex(rootCKey),
                ["mods"] = new JsonArray(modDirs.Select(m => (JsonNode)Path.GetFileName(m.TrimEnd('\\'))).ToArray()), ["files"] = report,
            };
            File.WriteAllText(ReportPath(client), summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            say($"build key {basis.BuildKey} -> {buildKey}; data.{archive:D3} = {offset:N0} B; {added.Count} new storage entries");
        }
        File.Move(archivePath + ".tmp", archivePath, true);
        return Verify(client, say);
    }

    /// <summary>Reads every file of the last build back through this tool's reader AND through D3Casc.Lib (WiseOwl.Casc) and compares bytes.</summary>
    public static int Verify(string client, Action<string> say)
    {
        var report = JsonNode.Parse(File.ReadAllText(ReportPath(client)))!;
        var mine = new LocalStorage(client);
        if (mine.BuildKey != (string)report["build_key"]!) { say($"FAIL: .build.info has {mine.BuildKey}, the last build wrote {report["build_key"]}"); return 2; }
        using var wise = D3Storage.Open(client);
        int bad = 0;
        foreach (var f in report["files"]!.AsArray())
        {
            var path = (string)f!["path"]!;
            var ckey = Keys.Parse((string)f["ckey"]!);
            byte[] a = mine.ReadCKey(ckey);
            bool viaPath = wise.TryGet(path, out var entry);
            byte[]? b = viaPath ? wise.Read(entry) : null;
            bool ok = Keys.Md5(a).AsSpan().SequenceEqual(ckey) && b != null && b.AsSpan().SequenceEqual(a);
            if (!ok) { bad++; say($"FAIL {path}: own reader {(Keys.Md5(a).AsSpan().SequenceEqual(ckey) ? "ok" : "BAD")}, D3Casc by path {(b == null ? "not found" : b.AsSpan().SequenceEqual(a) ? "ok" : "differs")}"); }
        }
        // untouched files must still read the same as in the official install
        using var official = D3Storage.Open(OfficialInstall);
        var rnd = new Random(1);
        var sample = official.AllEntries.Where(e => e.IsSno).OrderBy(_ => rnd.Next()).Take(64).ToList();
        int same = 0;
        foreach (var e in sample)
            if (wise.TryGet(e.Path, out var c) && wise.Read(c).AsSpan().SequenceEqual(official.Read(e))) same++;
        say($"verify: {report["files"]!.AsArray().Count - bad}/{report["files"]!.AsArray().Count} mod files read back identical through both readers; {same}/{sample.Count} random stock SNOs identical to the official install");
        return bad == 0 && same == sample.Count ? 0 : 2;
    }

    public static int Restore(string client, Action<string> say)
    {
        RefuseOfficial(client);
        var dir = BaseDir(client);
        if (!File.Exists(Path.Combine(dir, ".build.info"))) { say("no snapshot: the copy was never built"); return 0; }
        var basis = new LocalStorage(client, dir);
        foreach (var name in Directory.GetFiles(Path.Combine(dir, "idx")))
            File.Copy(name, Path.Combine(basis.DataDir, Path.GetFileName(name)), true);
        File.Copy(Path.Combine(dir, ".build.info"), Path.Combine(client, ".build.info"), true);
        EnsureShmemSnapshot(client, say);
        ResetShmem(client);
        foreach (var stale in Directory.GetFiles(basis.DataDir, "data.*"))
            if (int.TryParse(Path.GetExtension(stale).TrimStart('.'), out int n) && n > basis.MaxArchive) File.Delete(stale);
        File.Delete(ReportPath(client));
        say($"copy restored to stock build {basis.BuildKey} (snapshot kept in {dir})");
        return 0;
    }

    /// <summary>Round-trip checks against an unmodified install; every writer must reproduce the stock bytes before it is trusted.</summary>
    public static int SelfTest(string client, Action<string> say)
    {
        var s = new LocalStorage(client);
        int fails = 0;
        void Check(string what, bool ok) { say($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) fails++; }

        int idxSame = s.IdxFiles.Count(kv => s.Indexes[kv.Key].With(Array.Empty<LocalIndex.Entry>()).AsSpan().SequenceEqual(s.Indexes[kv.Key].Raw));
        Check($"idx: {idxSame}/{s.IdxFiles.Count} buckets re-sorted and re-hashed byte-identical", idxSame == s.IdxFiles.Count);

        var encBuilt = s.Encoding.Build();
        int firstDiff = encBuilt.AsSpan().CommonPrefixLength(s.EncodingRaw);
        Check($"encoding: rebuilt {encBuilt.Length:N0} B vs {s.EncodingRaw.Length:N0} B, {s.Encoding.ContentKeys:N0} ckeys{(firstDiff < encBuilt.Length ? $", first difference @{firstDiff}" : "")}",
            encBuilt.AsSpan().SequenceEqual(s.EncodingRaw));
        Check("encoding: config ckey = MD5(decoded file)", Keys.Hex(Keys.Md5(s.EncodingRaw)) == s.BuildConfig["encoding"].Split(' ')[0]);

        var root = new D3RootFile(s.RootRaw);
        say($"     root: {string.Join(", ", root.Dirs.Select(d => d.Name))}");
        var baseSub = new D3SubRoot(s.ReadCKey(root.Dirs.First(d => d.Name == "Base").CKey));
        say($"     Base sub-root: {baseSub.SnoAt.Count:N0} SNOs (sorted by id: {baseSub.SnosSorted}), {baseSub.NamedAt.Count} named");
        var tocRaw = s.ReadCKey(s.Raw(baseSub, "CoreTOC.dat"));
        var toc = new CoreTocFile(tocRaw);
        Check($"CoreTOC: copied blocks byte-identical ({tocRaw.Length:N0} B, next sno {toc.NextSno})", toc.Build().AsSpan().SequenceEqual(tocRaw));
        toc.Touch(46);
        Check($"CoreTOC: UI group (46) rebuilt from its entries byte-identical (entries sorted by id: {toc.Sorted})", toc.Build().AsSpan().SequenceEqual(tocRaw));

        // archive entry headers: recompute a sample of real ones
        {
            int ok = 0, total = 0;
            var mismatches = new List<string>();
            foreach (var idx in s.Indexes.Values)
            {
                var all = idx.Entries().ToList();
                foreach (var e in all.Where((_, i) => i % Math.Max(1, all.Count / 64) == 0))
                {
                    var raw = s.ReadRaw(e);
                    var ekey = new byte[16];
                    for (int i = 0; i < 16; i++) ekey[i] = raw[15 - i];
                    var made = ArchiveEntryHeader.Make(ekey, e.Size - ArchiveEntryHeader.Size, e.Archive, e.Offset);
                    // bytes 20..21 are flags (0 or 1 in stock); ours are always 0, so compare with the stock flags put in
                    made[20] = raw[20]; made[21] = raw[21];
                    if (raw[20] != 0 || raw[21] != 0) made = ArchiveEntryHeader.Rechecksum(made, e.Archive, e.Offset);
                    total++;
                    if (made.AsSpan().SequenceEqual(raw.AsSpan(0, 30))) ok++;
                    else if (mismatches.Count < 3) mismatches.Add($"data.{e.Archive:D3}@{e.Offset}");
                }
            }
            Check($"archive entry headers: {ok}/{total} sampled headers reproduced{(mismatches.Count > 0 ? " (e.g. " + string.Join(", ", mismatches) + ")" : "")}", ok == total);
        }
        // BLTE: decode -> encode -> decode round trip of a real file
        var sample = s.RootRaw;
        var (blob, _) = Blte.Encode(sample);
        Check("BLTE: encode/decode round trip", Blte.Decode(blob).AsSpan().SequenceEqual(sample));
        return fails == 0 ? 0 : 2;
    }

    static byte[] Raw(this LocalStorage s, D3SubRoot sub, string name) => sub.Raw.AsSpan(sub.NamedAt[name], 16).ToArray();
}
