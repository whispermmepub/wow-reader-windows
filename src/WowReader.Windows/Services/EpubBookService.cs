using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;
using WowReader.Windows.Models;

namespace WowReader.Windows.Services;

public sealed class EpubBookService
{
    private const long MaxExtractedBytes = 1_500_000_000L;
    private const string CacheMarker = ".wow-reader-cache-ready-v2";
    private const string ContentSecurityPolicy = "default-src 'self'; script-src 'none'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; media-src 'self' data:; connect-src 'none'; frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'self'";

    public Task<EpubBook> ImportAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Import(sourcePath, cancellationToken), cancellationToken);

    public Task<EpubBook> OpenAsync(string bookId, string bookPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(bookId, bookPath, cancellationToken), cancellationToken);

    private static EpubBook Import(string sourcePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath) || !string.Equals(Path.GetExtension(sourcePath), ".epub", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose a readable .epub file.");
        }

        AppPaths.EnsureCreated();
        var id = ComputeSha256(sourcePath, cancellationToken);
        var localPath = Path.Combine(AppPaths.Books, id + ".epub");
        if (!File.Exists(localPath))
        {
            var temporaryCopy = localPath + ".tmp";
            File.Copy(sourcePath, temporaryCopy, overwrite: true);
            File.Move(temporaryCopy, localPath, overwrite: true);
        }
        return Load(id, localPath, cancellationToken);
    }

    private static EpubBook Load(string bookId, string bookPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(bookPath))
        {
            throw new FileNotFoundException("The imported EPUB is no longer available in the local library.", bookPath);
        }

        AppPaths.EnsureCreated();
        var cacheDirectory = Path.Combine(AppPaths.Cache, bookId);
        var marker = Path.Combine(cacheDirectory, CacheMarker);
        if (!File.Exists(marker))
        {
            var stagingDirectory = Path.Combine(AppPaths.Cache, ".staging-" + bookId + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                ExtractArchive(bookPath, stagingDirectory, cancellationToken);
                var package = ReadPackage(stagingDirectory);
                foreach (var chapter in package.Chapters)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chapterPath = GetContainedPath(stagingDirectory, chapter.ArchivePath);
                    if (!File.Exists(chapterPath))
                    {
                        throw new InvalidDataException($"An EPUB spine file is missing: {chapter.ArchivePath}");
                    }
                }
                foreach (var htmlPath in Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
                    .Where(IsHtmlDocument))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SanitizeHtml(htmlPath);
                }
                File.WriteAllText(Path.Combine(stagingDirectory, CacheMarker), "sanitized-v2");
                if (Directory.Exists(cacheDirectory))
                {
                    Directory.Delete(cacheDirectory, recursive: true);
                }
                Directory.Move(stagingDirectory, cacheDirectory);
            }
            catch
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
                throw;
            }
        }

        var parsed = ReadPackage(cacheDirectory);
        var chapters = parsed.Chapters.Select(chapter => new EpubChapter(
            chapter.ArchivePath,
            chapter.Title,
            GetContainedPath(cacheDirectory, chapter.ArchivePath),
            chapter.Weight)).ToList();
        if (chapters.Count == 0)
        {
            throw new InvalidDataException("The EPUB package has no readable spine chapters.");
        }

        return new EpubBook(bookId, parsed.Title, bookPath, cacheDirectory, chapters, parsed.Author,
            parsed.Navigation.Select(item => new EpubNavigationItem(item.Title, item.ChapterIndex, item.Fragment, item.Depth)).ToArray());
    }

    private static string ComputeSha256(string filePath, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(filePath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void ExtractArchive(string epubPath, string destination, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(epubPath);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var extractedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = NormalizeArchiveEntry(entry.FullName);
            if (name.Length == 0)
            {
                continue;
            }
            if (!extractedNames.Add(name))
            {
                throw new InvalidDataException($"The EPUB contains conflicting paths on Windows: {name}");
            }
            if (entry.Length > MaxExtractedBytes || totalBytes > MaxExtractedBytes - entry.Length)
            {
                throw new InvalidDataException("This first-milestone importer stops at 1.5 GB of uncompressed EPUB content as a zip-bomb safety guard.");
            }

            var target = Path.GetFullPath(Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The EPUB contains an unsafe archive path.");
            }
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            var directory = Path.GetDirectoryName(target);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }
            using var input = entry.Open();
            using var output = File.Create(target);
            var buffer = new byte[64 * 1024];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (read > entry.Length - written || read > MaxExtractedBytes - totalBytes - written)
                {
                    throw new InvalidDataException("The EPUB expanded beyond its declared or allowed uncompressed size.");
                }
                output.Write(buffer, 0, read);
                written += read;
            }
            if (written != entry.Length)
            {
                throw new InvalidDataException("An EPUB archive entry did not match its declared uncompressed size.");
            }
            totalBytes += written;
        }
    }

    private static string NormalizeArchiveEntry(string entryName)
    {
        var value = entryName.Replace('\\', '/');
        if (value.StartsWith('/') || value.Contains(':'))
        {
            throw new InvalidDataException("The EPUB contains an absolute archive path.");
        }
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".."))
        {
            throw new InvalidDataException("The EPUB contains a traversal archive path.");
        }
        return string.Join('/', parts);
    }

    private static ParsedPackage ReadPackage(string extractedRoot)
    {
        var containerPath = GetContainedPath(extractedRoot, "META-INF/container.xml");
        if (!File.Exists(containerPath))
        {
            throw new InvalidDataException("This file is not a valid EPUB: META-INF/container.xml is missing.");
        }

        var container = LoadXml(containerPath);
        var rootfile = container.Descendants().FirstOrDefault(element => element.Name.LocalName == "rootfile")
            ?.Attribute("full-path")?.Value;
        if (string.IsNullOrWhiteSpace(rootfile))
        {
            throw new InvalidDataException("The EPUB container does not name its package document.");
        }
        var packagePath = ResolveArchivePath(string.Empty, rootfile);
        var packageFile = GetContainedPath(extractedRoot, packagePath);
        var package = LoadXml(packageFile);
        var title = package.Descendants().FirstOrDefault(element => element.Name.LocalName == "title")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = Path.GetFileNameWithoutExtension(packagePath);
        }
        var author = string.Join(", ", package.Descendants()
            .Where(element => element.Name.LocalName == "creator")
            .Select(element => element.Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(5));

        var manifest = package.Descendants().Where(element => element.Name.LocalName == "item")
            .Select(element => new
            {
                Id = (string?)element.Attribute("id"),
                Href = (string?)element.Attribute("href"),
                MediaType = (string?)element.Attribute("media-type"),
                Properties = (string?)element.Attribute("properties") ?? ""
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
            .ToDictionary(item => item.Id!, item => new ManifestItem(item.Href!, item.MediaType, item.Properties), StringComparer.Ordinal);

        var packageDirectory = Path.GetDirectoryName(packagePath)?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty;
        var navEntry = manifest.FirstOrDefault(pair => HasToken(pair.Value.Properties, "nav"));
        var navItemId = navEntry.Value is null ? null : navEntry.Key;
        var navArchivePath = navEntry.Value is null ? null : ResolveArchivePath(packageDirectory, navEntry.Value.Href);
        var spine = package.Descendants().FirstOrDefault(element => element.Name.LocalName == "spine");
        var ncxId = (string?)spine?.Attribute("toc");
        ManifestItem? ncxItem = null;
        if (ncxId is not null && manifest.TryGetValue(ncxId, out var referencedNcx)
            && string.Equals(referencedNcx.MediaType, "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase))
        {
            ncxItem = referencedNcx;
        }
        else
        {
            ncxItem = manifest.Values.FirstOrDefault(item =>
                string.Equals(item.MediaType, "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase));
        }
        var ncxArchivePath = ncxItem is null ? null : ResolveArchivePath(packageDirectory, ncxItem.Href);
        var chapterList = new List<ParsedChapter>();
        foreach (var itemref in spine?.Elements().Where(element => element.Name.LocalName == "itemref") ?? [])
        {
            var idref = (string?)itemref.Attribute("idref");
            if (idref is null || !manifest.TryGetValue(idref, out var item)
                || item.MediaType is not ("application/xhtml+xml" or "text/html")
                || string.Equals(idref, navItemId, StringComparison.Ordinal))
            {
                continue;
            }
            var archivePath = ResolveArchivePath(packageDirectory, item.Href);
            if (string.Equals(archivePath, navArchivePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var path = GetContainedPath(extractedRoot, archivePath);
            if (!File.Exists(path))
            {
                continue;
            }
            var document = LoadHtml(File.ReadAllText(path));
            RemoveUnsafeMarkup(document);
            var textLength = document.DocumentNode.InnerText.Trim().Length;
            var basename = Path.GetFileNameWithoutExtension(archivePath);
            var frontMatter = basename.Contains("toc", StringComparison.OrdinalIgnoreCase)
                || basename.Contains("nav", StringComparison.OrdinalIgnoreCase)
                || basename.Contains("cover", StringComparison.OrdinalIgnoreCase)
                || basename.Contains("titlepage", StringComparison.OrdinalIgnoreCase)
                || basename.Contains("copyright", StringComparison.OrdinalIgnoreCase);
            var weight = frontMatter && textLength < 30_000 ? 1L : Math.Max(250L, textLength);
            chapterList.Add(new ParsedChapter(archivePath, GetDocumentTitle(document, archivePath, chapterList.Count + 1), weight));
        }

        var chapterIndices = chapterList.Select((chapter, index) => (chapter.ArchivePath, index))
            .ToDictionary(item => item.ArchivePath, item => item.index, StringComparer.OrdinalIgnoreCase);
        var navigation = new List<ParsedNavigation>();
        if (navArchivePath is not null)
        {
            var navPath = GetContainedPath(extractedRoot, navArchivePath);
            if (File.Exists(navPath)) navigation.AddRange(ParseEpub3Navigation(navPath, navArchivePath, chapterIndices));
        }
        if (navigation.Count == 0 && ncxArchivePath is not null)
        {
            var ncxPath = GetContainedPath(extractedRoot, ncxArchivePath);
            if (File.Exists(ncxPath)) navigation.AddRange(ParseEpub2Navigation(ncxPath, ncxArchivePath, chapterIndices));
        }

        for (var index = 0; index < chapterList.Count; index++)
        {
            var bestLabel = navigation.Where(item => item.ChapterIndex == index && item.Title.Length > 0)
                .OrderBy(item => item.Depth)
                .ThenBy(item => item.Fragment.Length > 0 ? 1 : 0)
                .Select(item => item.Title)
                .FirstOrDefault();
            if (bestLabel is not null) chapterList[index] = chapterList[index] with { Title = bestLabel };
        }
        if (navigation.Count == 0)
        {
            navigation.AddRange(chapterList.Select((chapter, index) => new ParsedNavigation(chapter.Title, index, "", 0)));
        }
        return new ParsedPackage(title, author, chapterList, navigation);
    }

    private static IReadOnlyList<ParsedNavigation> ParseEpub3Navigation(string navPath, string navArchivePath, IReadOnlyDictionary<string, int> chapterIndices)
    {
        var document = LoadHtml(File.ReadAllText(navPath));
        var navNodes = document.DocumentNode.SelectNodes("//nav")?.ToList() ?? [];
        var tocNav = navNodes.FirstOrDefault(node => node.Attributes.Any(attribute =>
            (attribute.Name.Equals("epub:type", StringComparison.OrdinalIgnoreCase)
                || attribute.Name.Equals("type", StringComparison.OrdinalIgnoreCase)
                || attribute.Name.Equals("role", StringComparison.OrdinalIgnoreCase))
            && (attribute.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Any(value => value.Equals("toc", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("doc-toc", StringComparison.OrdinalIgnoreCase))));
        var nav = tocNav ?? navNodes.FirstOrDefault();
        var list = nav?.SelectSingleNode("./ol") ?? nav?.SelectSingleNode(".//ol");
        if (list is null) return [];

        var results = new List<ParsedNavigation>();
        var navDirectory = Path.GetDirectoryName(navArchivePath)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
        CollectEpub3Navigation(list, navDirectory, chapterIndices, 0, results);
        return results;
    }

    private static void CollectEpub3Navigation(HtmlNode list, string navDirectory,
        IReadOnlyDictionary<string, int> chapterIndices, int depth, List<ParsedNavigation> results)
    {
        foreach (var item in list.ChildNodes.Where(node => node.Name.Equals("li", StringComparison.OrdinalIgnoreCase)))
        {
            var link = item.SelectSingleNode("./a[@href]")
                ?? item.SelectSingleNode("./p/a[@href]")
                ?? item.SelectSingleNode("./span/a[@href]");
            if (link is not null)
            {
                var title = CleanNavigationLabel(link.InnerText);
                if (title.Length == 0)
                {
                    title = CleanNavigationLabel(link.SelectSingleNode(".//img[@alt]")?.GetAttributeValue("alt", "") ?? "");
                }
                if (title.Length > 0 && TryResolveNavigationTarget(link.GetAttributeValue("href", ""), navDirectory,
                    chapterIndices, out var chapterIndex, out var fragment))
                {
                    results.Add(new ParsedNavigation(title, chapterIndex, fragment, depth));
                }
            }

            foreach (var childList in item.ChildNodes.Where(node => node.Name.Equals("ol", StringComparison.OrdinalIgnoreCase)))
            {
                CollectEpub3Navigation(childList, navDirectory, chapterIndices, depth + 1, results);
            }
        }
    }

    private static IReadOnlyList<ParsedNavigation> ParseEpub2Navigation(string ncxPath, string ncxArchivePath, IReadOnlyDictionary<string, int> chapterIndices)
    {
        XDocument document;
        try
        {
            document = LoadXml(ncxPath);
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var navMap = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "navMap");
        if (navMap is null) return [];

        var results = new List<ParsedNavigation>();
        var ncxDirectory = Path.GetDirectoryName(ncxArchivePath)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
        CollectNcxNavigation(navMap, ncxDirectory, chapterIndices, 0, results);
        return results;
    }

    private static void CollectNcxNavigation(XElement parent, string ncxDirectory,
        IReadOnlyDictionary<string, int> chapterIndices, int depth, List<ParsedNavigation> results)
    {
        foreach (var point in parent.Elements().Where(element => element.Name.LocalName == "navPoint"))
        {
            var label = point.Elements().FirstOrDefault(element => element.Name.LocalName == "navLabel")?
                .Descendants().FirstOrDefault(element => element.Name.LocalName == "text")?.Value ?? "";
            var source = (string?)point.Elements().FirstOrDefault(element => element.Name.LocalName == "content")?.Attribute("src");
            var title = CleanNavigationLabel(label);
            if (source is not null && title.Length > 0
                && TryResolveNavigationTarget(source, ncxDirectory, chapterIndices, out var chapterIndex, out var fragment))
            {
                results.Add(new ParsedNavigation(title, chapterIndex, fragment, depth));
            }
            CollectNcxNavigation(point, ncxDirectory, chapterIndices, depth + 1, results);
        }
    }

    private static bool TryResolveNavigationTarget(string href, string baseDirectory,
        IReadOnlyDictionary<string, int> chapterIndices, out int chapterIndex, out string fragment)
    {
        chapterIndex = -1;
        fragment = "";
        try
        {
            var hash = href.IndexOf('#');
            if (hash >= 0 && hash + 1 < href.Length)
            {
                var fragmentValue = href[(hash + 1)..];
                var query = fragmentValue.IndexOf('?');
                fragment = Uri.UnescapeDataString(query >= 0 ? fragmentValue[..query] : fragmentValue);
            }
            var archivePath = ResolveArchivePath(baseDirectory, href);
            return chapterIndices.TryGetValue(archivePath, out chapterIndex);
        }
        catch (Exception exception) when (exception is InvalidDataException or UriFormatException or ArgumentException)
        {
            return false;
        }
    }

    private static string CleanNavigationLabel(string? value)
    {
        var decoded = HtmlEntity.DeEntitize(value ?? "");
        return string.Join(' ', decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static bool HasToken(string values, string token) =>
        values.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(value => value.Equals(token, StringComparison.OrdinalIgnoreCase));

    private static string GetDocumentTitle(HtmlDocument document, string archivePath, int chapterNumber)
    {
        var basename = Path.GetFileNameWithoutExtension(archivePath).Replace('_', ' ').Replace('-', ' ').Trim();
        foreach (var selector in new[] { "//h1", "//h2", "//title" })
        {
            var candidate = CleanNavigationLabel(document.DocumentNode.SelectSingleNode(selector)?.InnerText);
            if (candidate.Length > 0 && !IsGenericDocumentTitle(candidate, basename)) return candidate;
        }
        return IsGenericDocumentTitle(basename, basename) ? $"Chapter {chapterNumber}" : basename;
    }

    private static bool IsGenericDocumentTitle(string title, string basename)
    {
        var normalized = title.Trim().Replace('_', ' ').Replace('-', ' ');
        return normalized.Length == 0
            || normalized.Equals(basename, StringComparison.OrdinalIgnoreCase)
            || new[] { "index", "toc", "nav", "contents", "table of contents", "untitled", "cover", "titlepage" }
                .Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static XDocument LoadXml(string path)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true
        };
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string ResolveArchivePath(string baseDirectory, string reference)
    {
        var separator = reference.IndexOfAny(['#', '?']);
        var pathOnly = separator >= 0 ? reference[..separator] : reference;
        var decoded = Uri.UnescapeDataString(pathOnly).Replace('\\', '/');
        if (decoded.StartsWith('/') || decoded.Contains(':'))
        {
            throw new InvalidDataException("An EPUB package path is absolute or invalid.");
        }

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(baseDirectory))
        {
            parts.AddRange(baseDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries));
        }
        foreach (var part in decoded.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    throw new InvalidDataException("An EPUB package path escapes the archive root.");
                }
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        if (parts.Count == 0)
        {
            throw new InvalidDataException("The EPUB contains an empty package path.");
        }
        return string.Join('/', parts);
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        var rootPath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("An EPUB path escaped its extracted directory.");
        }
        return fullPath;
    }

    private static bool IsHtmlDocument(string path) =>
        Path.GetExtension(path) is var extension
        && (extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xhtml", StringComparison.OrdinalIgnoreCase));

    private static void SanitizeHtml(string path)
    {
        var document = LoadHtml(File.ReadAllText(path));
        RemoveUnsafeMarkup(document);
        var html = document.DocumentNode.SelectSingleNode("//html");
        if (html is null)
        {
            html = HtmlNode.CreateNode("<html><head></head><body></body></html>");
            var original = document.DocumentNode.ChildNodes.ToList();
            foreach (var child in original)
            {
                child.Remove();
                html.SelectSingleNode("//body")?.AppendChild(child);
            }
            document.DocumentNode.RemoveAllChildren();
            document.DocumentNode.AppendChild(html);
        }
        var head = html.SelectSingleNode("./head");
        if (head is null)
        {
            head = document.CreateElement("head");
            html.PrependChild(head);
        }
        foreach (var oldPolicy in head.SelectNodes(".//meta[@http-equiv]")?.ToList() ?? [])
        {
            if (string.Equals(oldPolicy.GetAttributeValue("http-equiv", ""), "Content-Security-Policy", StringComparison.OrdinalIgnoreCase))
            {
                oldPolicy.Remove();
            }
        }
        var policy = document.CreateElement("meta");
        policy.SetAttributeValue("http-equiv", "Content-Security-Policy");
        policy.SetAttributeValue("content", ContentSecurityPolicy);
        head.PrependChild(policy);
        File.WriteAllText(path, document.DocumentNode.OuterHtml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static HtmlDocument LoadHtml(string html)
    {
        var document = new HtmlDocument
        {
            OptionAutoCloseOnEnd = true,
            OptionFixNestedTags = true,
            OptionWriteEmptyNodes = true
        };
        document.LoadHtml(html);
        return document;
    }

    private static void RemoveUnsafeMarkup(HtmlDocument document)
    {
        foreach (var node in document.DocumentNode.SelectNodes("//script|//iframe|//frame|//object|//embed|//applet|//form|//base")?.ToList() ?? [])
        {
            node.Remove();
        }
        foreach (var meta in document.DocumentNode.SelectNodes("//meta[@http-equiv]")?.ToList() ?? [])
        {
            if (string.Equals(meta.GetAttributeValue("http-equiv", ""), "refresh", StringComparison.OrdinalIgnoreCase))
            {
                meta.Remove();
            }
        }
        foreach (var node in document.DocumentNode.Descendants().ToList())
        {
            foreach (var attribute in node.Attributes.ToList())
            {
                var name = attribute.Name.ToLowerInvariant();
                var value = attribute.Value?.Trim() ?? string.Empty;
                if (name.StartsWith("on", StringComparison.Ordinal) || name == "srcset")
                {
                    node.Attributes.Remove(attribute);
                    continue;
                }
                if (name is "src" or "href" or "poster" or "xlink:href")
                {
                    if (IsUnsafeUrl(value, name == "src" && node.Name.Equals("img", StringComparison.OrdinalIgnoreCase)))
                    {
                        node.Attributes.Remove(attribute);
                    }
                }
            }
        }
    }

    private static bool IsUnsafeUrl(string value, bool allowDataImage)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('#'))
        {
            return false;
        }
        if (value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !allowDataImage;
    }

    private sealed record ManifestItem(string Href, string? MediaType, string Properties);
    private sealed record ParsedPackage(string Title, string Author, IReadOnlyList<ParsedChapter> Chapters, IReadOnlyList<ParsedNavigation> Navigation);
    private sealed record ParsedChapter(string ArchivePath, string Title, long Weight);
    private sealed record ParsedNavigation(string Title, int ChapterIndex, string Fragment, int Depth);
}
