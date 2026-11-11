using CrystalMpq;

namespace D3ModTool;

/// <summary>
/// The 2.0.1.22044 client's MPQ set, read-only. Base archives sit in <c>Data_D3\PC\MPQs</c>; the 21876 and 22044 updates
/// sit in <c>base\</c>, <c>enUS\</c>, <c>Win\</c> (the <c>Cache\</c> copies are the downloaded originals and are skipped).
/// Update archives store paths with a locale prefix (<c>base\UI\X.ui</c>), base archives without (<c>UI\X.ui</c>).
/// The last archive that has a path wins. Incremental patch entries (magic "PTCH") cannot be applied by CrystalMpq and are reported.
/// </summary>
public sealed class Mpq201 : IDisposable
{
    public sealed record Hit(string Archive, string StoredPath, string LogicalPath, long Size, bool IsPtch);

    readonly List<(string Name, MpqArchive Archive, string Prefix, Stream Stream)> _archives = new();
    readonly Dictionary<string, List<string>> _lists = new();

    public Mpq201(string mpqDir)
    {
        var order = new List<string>();
        // base archives, alphabetical; the audio/cutscene/sound ones hold nothing a mod needs and are big
        order.AddRange(Directory.GetFiles(mpqDir, "*.mpq")
            .Where(p => !Path.GetFileName(p).Contains("Audio") && !Path.GetFileName(p).Contains("Cutscene") && !Path.GetFileName(p).StartsWith("Sound"))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        // updates in build order
        foreach (var sub in new[] { "base", "enUS", "Win" })
        {
            var d = Path.Combine(mpqDir, sub);
            if (Directory.Exists(d)) order.AddRange(Directory.GetFiles(d, "*.mpq"));
        }
        order = order.OrderBy(p => Path.GetDirectoryName(p) == mpqDir.TrimEnd('\\') ? 0 : 1)
                     .ThenBy(p => BuildOf(p)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var p in order)
        {
            var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var a = new MpqArchive(fs, true);
            var sub = Path.GetFileName(Path.GetDirectoryName(p)!);
            var prefix = Path.GetDirectoryName(p)!.TrimEnd('\\') == mpqDir.TrimEnd('\\') ? "" : sub + "\\";
            _archives.Add((Path.GetRelativePath(mpqDir, p), a, prefix, fs));
        }
    }

    static int BuildOf(string p)
    {
        var digits = new string(Path.GetFileNameWithoutExtension(p).Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return digits.Length > 0 ? int.Parse(digits) : 0;
    }

    public IEnumerable<string> ArchiveNames => _archives.Select(a => a.Name);

    public IReadOnlyList<string> ListFile(int index)
    {
        var (name, archive, _, _) = _archives[index];
        if (_lists.TryGetValue(name, out var l)) return l;
        l = new List<string>();
        var lf = archive.FindFile("(listfile)");
        if (lf != null)
        {
            using var s = lf.Open();
            using var r = new StreamReader(s);
            l.AddRange(r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        _lists[name] = l;
        return l;
    }

    /// <summary>Every archive entry whose logical path contains <paramref name="filter"/>, in load order.</summary>
    public List<Hit> Find(string filter)
    {
        var hits = new List<Hit>();
        for (int i = 0; i < _archives.Count; i++)
        {
            var (name, archive, prefix, _) = _archives[i];
            foreach (var stored in ListFile(i))
            {
                var logical = Logical(stored, prefix);
                if (logical == null || logical.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var f = archive.FindFile(stored);
                if (f == null || f.IsDeleted) continue;
                hits.Add(new Hit(name, stored, logical, f.Size, f.IsPatch));
            }
        }
        return hits;
    }

    /// <summary>Path as the base archives store it, or null for locale folders we do not want (other than base/enUS/Win).</summary>
    static string? Logical(string stored, string prefix)
    {
        if (prefix.Length == 0) return stored;
        foreach (var p in new[] { "base\\", "enUS\\", "Win\\" })
            if (stored.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return stored.Substring(p.Length);
        return stored;
    }

    public byte[] Read(Hit hit)
    {
        var a = _archives.First(x => x.Name == hit.Archive).Archive;
        var f = a.FindFile(hit.StoredPath) ?? throw new FileNotFoundException(hit.StoredPath);
        using var s = f.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// The final 2.0.1.22044 bytes of one logical path: the last full copy, with every later patch entry applied on top
    /// (CrystalMpq's <c>MpqFile.Open(baseStream)</c> applies one PTCH). <paramref name="chain"/> = all hits of that path in load order.
    /// </summary>
    public byte[] ReadPatched(IReadOnlyList<Hit> chain, out string how)
    {
        int start = -1;
        for (int i = chain.Count - 1; i >= 0; i--) if (!chain[i].IsPtch) { start = i; break; }
        if (start < 0)
        {
            // the base copy can sit in an archive without a (listfile), e.g. enUS_Text.mpq: look it up by name
            var logical = chain[0].LogicalPath;
            foreach (var (name, archive, prefix, _) in _archives)
            {
                if (prefix.Length != 0) continue;
                foreach (var candidate in new[] { logical, "enUS\\" + logical, "base\\" + logical })
                {
                    var f = archive.FindFile(candidate);
                    if (f == null || f.IsPatch || f.IsDeleted) continue;
                    chain = chain.Prepend(new Hit(name, candidate, logical, f.Size, false)).ToList();
                    start = 0;
                    break;
                }
                if (start == 0) break;
            }
        }
        if (start < 0) throw new InvalidDataException($"{chain[0].LogicalPath}: only patch entries, no base file");
        var bytes = Read(chain[start]);
        var steps = new List<string> { chain[start].Archive };
        for (int i = start + 1; i < chain.Count; i++)
        {
            var a = _archives.First(x => x.Name == chain[i].Archive).Archive;
            var f = a.FindFile(chain[i].StoredPath)!;
            // CrystalMpq asks for the patch's base through MpqArchive.ResolveBaseFile; hand it the previous step's bytes
            var previous = bytes;
            EventHandler<ResolveStreamEventArgs> resolve = (_, e) => e.Stream = new MemoryStream(previous, false);
            a.ResolveBaseFile += resolve;
            try
            {
                using var s = f.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                bytes = ms.ToArray();
            }
            finally { a.ResolveBaseFile -= resolve; }
            steps.Add("+" + chain[i].Archive);
        }
        how = string.Join(" ", steps);
        return bytes;
    }

    public void Dispose()
    {
        foreach (var a in _archives) a.Stream.Dispose();
    }
}
