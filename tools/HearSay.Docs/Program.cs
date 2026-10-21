using System.Diagnostics;
using System.Globalization;
using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HearSay.Docs;

/// <summary>
/// Turns the manuals into PDF files: each Markdown file becomes one self-contained HTML page (its images embedded, links
/// to the other manual and to other files of the repository at GitHub, as of --ref), which a headless Chrome (or Edge,
/// or Chromium) prints to PDF with page numbers. A link from one PDF to the other cannot work: a package may hold only
/// one of them, and a relative link is printed as the absolute path of the folder it was built in.
///
/// <code>dotnet run -c Release --project tools/HearSay.Docs -- [--out folder] [--version 0.4.0] [--ref commit] [--html]</code>
///
/// It writes to artifacts/manuals by default; --ref is the commit, tag or branch the links point at (main by default);
/// --html keeps the HTML pages next to the PDF files. The browser is found on the usual places, or given in
/// HEARSAY_CHROME.
/// </summary>
internal static class Program
{
    /// <summary>Where links to files of the repository point: GitHub, at the commit, tag or branch of --ref.</summary>
    private static string _repository = "https://github.com/shimpe/HearSay/blob/main/";

    /// <summary>The manuals: Markdown source under the repository, PDF name, title in the page header.</summary>
    private static readonly (string Source, string Pdf, string Title)[] Manuals =
    [
        ("docs/studio-manual.md", "HearSay-Studio-Manual.pdf", "HearSay Studio manual"),
        ("docs/expert-manual.md", "HearSay-Expert-Manual.pdf", "HearSay expert manual"),
        ("docs/patch-matching.md", "HearSay-Patch-Matching.pdf", "HearSay patch matching"),
        ("docs/library-reference.md", "HearSay-Library-Reference.pdf", "HearSay library reference"),
    ];

    public static int Main(string[] args)
    {
        try
        {
            string repository = FindRepository();
            string output = Path.Combine(repository, "artifacts", "manuals");
            string? version = null;
            bool keepHtml = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--out" when i + 1 < args.Length:
                        output = Path.GetFullPath(args[++i]);
                        break;
                    case "--version" when i + 1 < args.Length:
                        version = args[++i];
                        break;
                    case "--ref" when i + 1 < args.Length:
                        _repository = $"https://github.com/shimpe/HearSay/blob/{Uri.EscapeDataString(args[++i])}/";
                        break;
                    case "--html":
                        keepHtml = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{args[i]}'. Use --out folder, --version x.y.z, --ref commit, --html.");
                }
            }

            Directory.CreateDirectory(output);
            string browser = FindBrowser();
            foreach ((string source, string pdf, string title) in Manuals)
            {
                string markdown = Path.Combine(repository, source.Replace('/', Path.DirectorySeparatorChar));
                string html = Path.Combine(output, Path.ChangeExtension(pdf, ".html"));
                File.WriteAllText(html, ToHtml(repository, markdown, title, version), Encoding.UTF8);
                Print(browser, html, Path.Combine(output, pdf));
                if (!keepHtml)
                {
                    File.Delete(html);
                }

                Console.WriteLine($"{source} -> {Path.Combine(output, pdf)}");
            }

            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    private static string FindRepository()
    {
        for (string? dir = Directory.GetCurrentDirectory(); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "HearSay.sln")))
            {
                return dir;
            }
        }

        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "HearSay.sln")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException("HearSay.sln not found: run this from the repository.");
    }

    /// <summary>One manual as an HTML page with everything in it.</summary>
    private static string ToHtml(string repository, string markdownPath, string title, string? version)
    {
        MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseAutoIdentifiers(Markdig.Extensions.AutoIdentifiers.AutoIdentifierOptions.GitHub)
            .Build();
        MarkdownDocument document = Markdown.Parse(File.ReadAllText(markdownPath), pipeline);
        string folder = Path.GetDirectoryName(markdownPath)!;

        // The ids GitHub gives headings, which the links of the manuals use: Markdig's own drop a leading section number.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (HeadingBlock heading in document.Descendants<HeadingBlock>())
        {
            string slug = GitHubSlug(PlainText(heading.Inline));
            int seen = ids.GetValueOrDefault(slug);
            ids[slug] = seen + 1;
            heading.GetAttributes().Id = seen == 0 ? slug : FormattableString.Invariant($"{slug}-{seen}");
        }

        foreach (LinkInline link in document.Descendants<LinkInline>())
        {
            if (link.IsImage && PrintedWidth(folder, link.Url) is { } width)
            {
                link.GetAttributes().AddProperty("style", FormattableString.Invariant($"width: {width:0.#}mm"));
            }

            link.Url = Rewrite(repository, folder, link.Url, link.IsImage);
        }

        var body = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(body);
        pipeline.Setup(renderer);
        renderer.Render(document);

        string subtitle = version is null ? string.Empty : $"<p class=\"version\">Version {Escape(version)}</p>";
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>{{Escape(title)}}</title>
            <style>
            {{Css.Replace("$TITLE", title.Replace("\"", "\\\"", StringComparison.Ordinal), StringComparison.Ordinal)}}
            </style>
            </head>
            <body>
            {{subtitle}}
            {{body}}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// A link as the PDF needs it: an image embedded, any other file of the repository (the other manual too) at GitHub;
    /// web links and anchors as they are.
    /// </summary>
    private static string? Rewrite(string repository, string folder, string? url, bool image)
    {
        if (string.IsNullOrEmpty(url) || url.StartsWith('#') || url.Contains("://", StringComparison.Ordinal) || url.StartsWith("mailto:", StringComparison.Ordinal))
        {
            return url;
        }

        int hash = url.IndexOf('#', StringComparison.Ordinal);
        string path = hash < 0 ? url : url[..hash];
        string fragment = hash < 0 ? string.Empty : url[hash..];
        string full = Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(path)));
        if (image)
        {
            if (!File.Exists(full))
            {
                throw new FileNotFoundException($"Image '{url}' not found.", full);
            }

            string type = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".svg" => "image/svg+xml",
                ".gif" => "image/gif",
                _ => throw new NotSupportedException($"Image type of '{url}'."),
            };
            return $"data:{type};base64,{Convert.ToBase64String(File.ReadAllBytes(full))}";
        }

        string relative = Path.GetRelativePath(repository, full).Replace(Path.DirectorySeparatorChar, '/');
        return _repository + relative + fragment;
    }

    /// <summary>The text of a heading's inlines, without their markup.</summary>
    private static string PlainText(ContainerInline? inlines)
    {
        var text = new StringBuilder();
        foreach (Inline inline in inlines?.Descendants<Inline>() ?? [])
        {
            switch (inline)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>A heading's anchor as GitHub makes it: lower case, punctuation dropped, spaces as hyphens.</summary>
    private static string GitHubSlug(string heading)
    {
        var slug = new StringBuilder();
        foreach (char c in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                slug.Append(c);
            }
            else if (c == ' ')
            {
                slug.Append('-');
            }
        }

        return slug.ToString();
    }

    /// <summary>Width of the text column of a page, and the tallest an image may print, in millimetres.</summary>
    private const double ColumnMm = 174, ImageHeightMm = 105;

    /// <summary>
    /// The width a PNG prints at: its pixels at twice the screen resolution (the screenshots are captured so), within the
    /// text column and no taller than <see cref="ImageHeightMm"/>; null for another kind of image.
    /// </summary>
    private static double? PrintedWidth(string folder, string? url)
    {
        if (url is null || !url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || url.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        // A missing image is reported where it is embedded. The size is in the IHDR chunk, the first after the
        // signature: width and height, big-endian, at bytes 16 and 20.
        string path = Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(url)));
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] head = new byte[24];
        using (FileStream stream = File.OpenRead(path))
        {
            if (stream.Read(head) < head.Length || head[1] != (byte)'P' || head[2] != (byte)'N' || head[3] != (byte)'G')
            {
                throw new InvalidDataException($"'{url}' is not a PNG image.");
            }
        }

        double pixelsWide = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16));
        double pixelsHigh = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20));
        double millimetres = pixelsWide / 2 / 96 * 25.4;
        return Math.Min(Math.Min(millimetres, ColumnMm), ImageHeightMm * pixelsWide / pixelsHigh);
    }

    private static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text);

    /// <summary>Chrome, Edge or Chromium: HEARSAY_CHROME, or where they are usually installed.</summary>
    private static string FindBrowser()
    {
        if (Environment.GetEnvironmentVariable("HEARSAY_CHROME") is { Length: > 0 } given)
        {
            return given;
        }

        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            candidates.Add("/Applications/Chromium.app/Contents/MacOS/Chromium");
            candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
        }
        else
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string name in new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge" })
                {
                    candidates.Add(Path.Combine(dir, name));
                }
            }
        }

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("No Chrome, Edge or Chromium found; set HEARSAY_CHROME to one.");
    }

    /// <summary>Prints an HTML page to PDF with a headless browser of its own (a profile of its own, so no open window is used).</summary>
    private static void Print(string browser, string html, string pdf)
    {
        string profile = Path.Combine(Path.GetTempPath(), "hearsay-docs-" + Guid.NewGuid().ToString("N"));
        File.Delete(pdf);
        try
        {
            var start = new ProcessStartInfo(browser)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string argument in new[]
            {
                "--headless=new",
                "--disable-gpu",
                "--disable-dev-shm-usage",
                "--no-sandbox",
                "--no-first-run",
                "--no-default-browser-check",
                "--no-pdf-header-footer",
                "--user-data-dir=" + profile,
                "--print-to-pdf=" + pdf,
                new Uri(html).AbsoluteUri,
            })
            {
                start.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(start) ?? throw new InvalidOperationException("The browser did not start.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The browser did not finish printing " + html + ".");
            }

            if (!File.Exists(pdf) || new FileInfo(pdf).Length == 0)
            {
                throw new InvalidOperationException($"The browser wrote no PDF for {html} (exit {process.ExitCode}): {output.Result} {errors.Result}");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (IOException)
            {
                // The browser may still hold its profile for a moment; it is in the temporary folder.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The look of the printed manuals: A4, a header with the title, page numbers, a new page per chapter.</summary>
    private const string Css = """
        @page {
          size: A4;
          margin: 20mm 18mm 20mm 18mm;
          @top-right { content: "$TITLE"; font: 8pt "Segoe UI", "DejaVu Sans", sans-serif; color: #64748b; }
          @bottom-center { content: counter(page) " / " counter(pages); font: 8pt "Segoe UI", "DejaVu Sans", sans-serif; color: #64748b; }
        }
        @page :first { @top-right { content: none; } }
        html { font: 10pt/1.45 "Segoe UI", "DejaVu Sans", "Helvetica Neue", Arial, sans-serif; color: #0f172a; }
        body { margin: 0; }
        h1 { font-size: 24pt; margin: 0 0 4mm 0; color: #0e7490; }
        h2 { font-size: 15pt; margin: 0 0 3mm 0; color: #0e7490; break-before: page; }
        h3 { font-size: 12pt; margin: 6mm 0 2mm 0; color: #155e75; break-after: avoid; }
        h4 { font-size: 10.5pt; margin: 4mm 0 1mm 0; break-after: avoid; }
        p, li { orphans: 3; widows: 3; }
        a { color: #0e7490; text-decoration: none; }
        code { font: 8.6pt "Cascadia Mono", Consolas, "DejaVu Sans Mono", monospace; background: #f1f5f9; padding: 0 2px; border-radius: 2px; }
        pre { background: #f1f5f9; border: 1px solid #e2e8f0; border-radius: 4px; padding: 3mm; white-space: pre-wrap; break-inside: avoid; }
        pre code { background: none; padding: 0; }
        table { border-collapse: collapse; margin: 3mm 0; font-size: 9pt; }
        th, td { border: 1px solid #cbd5e1; padding: 1.2mm 2mm; vertical-align: top; text-align: left; }
        th { background: #e2e8f0; }
        tr { break-inside: avoid; }
        img { max-width: 100%; height: auto; border: 1px solid #cbd5e1; border-radius: 3px; }
        p:has(> img) { text-align: center; break-inside: avoid; break-after: avoid; margin: 4mm 0 1mm 0; }
        p:has(> img) + p > em:only-child { display: block; text-align: center; font-size: 9pt; color: #475569; }
        blockquote { margin: 3mm 0; padding: 1mm 4mm; border-left: 3px solid #22d3ee; background: #f0fdff; }
        hr { border: none; border-top: 1px solid #cbd5e1; }
        .version { float: right; margin: 2mm 0 0 0; color: #64748b; }
        """;
}
