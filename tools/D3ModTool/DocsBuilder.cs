using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;

namespace D3ModTool;

/// <summary>
/// docs-build: the ModKit documentation (Markdown files, read in file-name order: 01-intro.md, 02-...) -> one self-contained
/// HTML page (DocsTemplate.html: sidebar with every page and its sections, full-text search, light/dark). No network needed
/// to read it. Every page's first "# " heading is its title; "## " headings become the sidebar's sections.
/// </summary>
public static class DocsBuilder
{
    sealed record Page(string Id, string Title, string Html, List<(string Id, string Title)> Sections, string Text);

    public static int Build(string srcDir, string outFile, Action<string> log)
    {
        var files = Directory.GetFiles(srcDir, "*.md").OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) { log($"no .md files in {srcDir}"); return 1; }
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var pages = new List<Page>();
        foreach (var f in files)
        {
            var md = File.ReadAllText(f);
            var id = Regex.Replace(Path.GetFileNameWithoutExtension(f), @"^\d+[-_]?", "");
            var title = Regex.Match(md, @"^#\s+(.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : id;
            var html = Markdown.ToHtml(md, pipeline);
            // section anchors are unique per page: prefix them with the page id
            var sections = new List<(string, string)>();
            html = Regex.Replace(html, @"<h([23]) id=""([^""]+)"">(.*?)</h\1>", h =>
            {
                var anchor = $"{id}--{h.Groups[2].Value}";
                if (h.Groups[1].Value == "2") sections.Add((anchor, Regex.Replace(h.Groups[3].Value, "<.*?>", "")));
                return $"<h{h.Groups[1].Value} id=\"{anchor}\">{h.Groups[3].Value}</h{h.Groups[1].Value}>";
            });
            // links between pages: [text](other-page.md#section) -> #other-page--section
            html = Regex.Replace(html, @"href=""(?:\d+[-_]?)?([\w-]+)\.md(?:#([\w-]+))?""",
                l => l.Groups[2].Success ? $"href=\"#{l.Groups[1].Value}--{l.Groups[2].Value}\"" : $"href=\"#{l.Groups[1].Value}\"");
            var text = Markdown.ToPlainText(md, pipeline);
            pages.Add(new Page(id, title, html, sections, text));
            log($"{Path.GetFileName(f)}: {title} ({sections.Count} sections)");
        }
        var data = JsonSerializer.Serialize(pages.Select(p => new
        {
            id = p.Id, title = p.Title, html = p.Html, text = p.Text,
            sections = p.Sections.Select(s => new { id = s.Id, title = s.Title }),
        }));
        using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("DocsTemplate.html")!;
        var template = new StreamReader(res).ReadToEnd();
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var outHtml = template.Replace("/*__DOCS__*/null", data.Replace("</", "<\\/")).Replace("__BUILT__", stamp);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, outHtml, new UTF8Encoding(false));
        log($"{pages.Count} page(s) -> {outFile}");
        return 0;
    }
}
