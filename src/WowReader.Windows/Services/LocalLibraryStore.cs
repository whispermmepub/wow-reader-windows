using System.IO;
using System.Text.Json;
using WowReader.Windows.Models;

namespace WowReader.Windows.Services;

public static class AppPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WoWReader");
    public static string Books => Path.Combine(Root, "Books");
    public static string Pdfs => Path.Combine(Root, "PDFs");
    public static string Covers => Path.Combine(Root, "Covers");
    public static string Cache => Path.Combine(Root, "Cache");
    public static string Fonts => Path.Combine(Root, "Fonts");
    public static string WebViewProfile => Path.Combine(Root, "WebView2Profile");
    public static string CatalogFile => Path.Combine(Root, "library-catalog.json");
    public static string ProgressFile => Path.Combine(Root, "reading-progress.json");
    public static string ActivityFile => Path.Combine(Root, "reading-activity.json");
    public static string BookmarksFile => Path.Combine(Root, "reader-bookmarks.json");
    public static string NotesFile => Path.Combine(Root, "reader-notes.json");
    public static string DayNotesFile => Path.Combine(Root, "reading-day-notes.json");
    public static string DayMemoryFile => Path.Combine(Root, "reading-day-memory.json");
    public static string CompletionFile => Path.Combine(Root, "book-completion.json");
    public static string FontCatalogFile => Path.Combine(Root, "font-catalog.json");
    public static string BookSettingsFile => Path.Combine(Root, "reader-book-settings.json");
    public static string VocabularyFile => Path.Combine(Root, "vocabulary.json");
    public static string ExploreCache => Path.Combine(Cache, "Explore");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Books);
        Directory.CreateDirectory(Pdfs);
        Directory.CreateDirectory(Covers);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Fonts);
        Directory.CreateDirectory(WebViewProfile);
    }
}

public sealed record LibraryBookEntry(string Id, string Title)
{
    public string Format { get; init; } = "epub";
    public string Author { get; init; } = "";
    public string Series { get; init; } = "";
    public string Description { get; init; } = "";
    public string CoverFileName { get; init; } = "";
    public int? Rating { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<string> Shelves { get; init; } = [];
}

public sealed class LocalLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private CatalogData _data = new();

    public IReadOnlyList<LibraryBookEntry> Books => _data.Books;
    public string? LastOpenedBookId => _data.LastOpenedBookId;

    public void Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            var json = File.ReadAllText(AppPaths.CatalogFile);
            _data = JsonSerializer.Deserialize<CatalogData>(json) ?? new CatalogData();
            _data.Books = _data.Books
                .Where(book => book is not null && IsSafeId(book.Id)
                    && File.Exists(book.Format == "pdf" ? PdfBookPath(book.Id) : Path.Combine(AppPaths.Books, book.Id + ".epub")))
                .Select(NormalizeEntry)
                .GroupBy(book => book.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (_data.LastOpenedBookId is not null && !_data.Books.Any(book => book.Id == _data.LastOpenedBookId))
                _data.LastOpenedBookId = null;
        }
        catch (FileNotFoundException) { _data = new CatalogData(); }
        catch (JsonException) { _data = new CatalogData(); }
    }

    public LibraryBookEntry? Get(string id) => _data.Books.FirstOrDefault(book => book.Id == id);

    public void AddOrUpdate(EpubBook book)
    {
        var existing = Get(book.Id);
        var entry = NormalizeEntry(new LibraryBookEntry(book.Id, book.Title)
        {
            Format = "epub",
            Author = string.IsNullOrWhiteSpace(book.Author) ? existing?.Author ?? "" : book.Author,
            Series = existing?.Series ?? "",
            Description = existing?.Description ?? "",
            CoverFileName = existing?.CoverFileName ?? "",
            Rating = existing?.Rating,
            AddedAt = existing?.AddedAt ?? DateTimeOffset.UtcNow,
            Shelves = existing?.Shelves ?? []
        });
        Upsert(entry, markLastOpened: true);
    }

    public void AddPdfOrUpdate(PdfBook book)
    {
        var existing = Get(book.Id);
        var entry = NormalizeEntry(new LibraryBookEntry(book.Id, book.Title)
        {
            Format = "pdf",
            Author = existing?.Author ?? "",
            Series = existing?.Series ?? "",
            Description = existing?.Description ?? "",
            CoverFileName = existing?.CoverFileName ?? "",
            Rating = existing?.Rating,
            AddedAt = existing?.AddedAt ?? DateTimeOffset.UtcNow,
            Shelves = existing?.Shelves ?? []
        });
        Upsert(entry, markLastOpened: true);
    }

    public void UpdateDetails(string id, string? title, string? author, string? series, string? description, int? rating, IEnumerable<string>? shelves)
    {
        var index = _data.Books.FindIndex(book => book.Id == id);
        if (index < 0) return;
        var current = _data.Books[index];
        _data.Books[index] = NormalizeEntry(current with
        {
            Title = Limit(title, 240, current.Title),
            Author = Limit(author, 240, ""),
            Series = Limit(series, 240, ""),
            Description = Limit(description, 2000, ""),
            Rating = rating is >= 0 and <= 5 ? rating : null,
            Shelves = NormalizeShelves(shelves ?? current.Shelves)
        });
        Save();
    }

    public IReadOnlyList<string> GetShelves() => _data.Books
        .SelectMany(book => book.Shelves)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public string? CoverPath(string id)
    {
        return CoverPath(Get(id));
    }

    public string? CoverPath(LibraryBookEntry? entry)
    {
        if (entry is null || !IsSafeId(entry.Id) || !IsSafeCoverName(entry.Id, entry.CoverFileName)) return null;
        var path = Path.Combine(AppPaths.Covers, entry.CoverFileName);
        return File.Exists(path) ? path : null;
    }

    public void SetCoverFile(string id, string? fileName)
    {
        var index = _data.Books.FindIndex(book => book.Id == id);
        if (index < 0) return;
        if (!string.IsNullOrWhiteSpace(fileName) && !IsSafeCoverName(id, fileName))
            throw new ArgumentException("The cover file name is invalid.", nameof(fileName));
        var entry = _data.Books[index];
        var newName = fileName ?? "";
        _data.Books[index] = entry with { CoverFileName = newName };
        Save();
        if (!string.IsNullOrWhiteSpace(entry.CoverFileName) && !string.Equals(entry.CoverFileName, newName, StringComparison.Ordinal))
        {
            var oldPath = Path.Combine(AppPaths.Covers, entry.CoverFileName);
            if (File.Exists(oldPath)) File.Delete(oldPath);
        }
    }

    public void RenameShelf(string oldName, string newName)
    {
        var normalizedOld = NormalizeShelf(oldName);
        var normalizedNew = NormalizeShelf(newName);
        if (normalizedNew.Length == 0) throw new ArgumentException("Shelf names must contain at least one character.", nameof(newName));
        if (!string.Equals(normalizedOld, normalizedNew, StringComparison.CurrentCultureIgnoreCase)
            && GetShelves().Contains(normalizedNew, StringComparer.CurrentCultureIgnoreCase))
            throw new InvalidOperationException("A shelf with that name already exists.");
        foreach (var book in _data.Books)
        {
            if (!book.Shelves.Contains(normalizedOld, StringComparer.CurrentCultureIgnoreCase)) continue;
            var changed = book.Shelves.Select(name => string.Equals(name, normalizedOld, StringComparison.CurrentCultureIgnoreCase) ? normalizedNew : name);
            var index = _data.Books.FindIndex(item => item.Id == book.Id);
            _data.Books[index] = book with { Shelves = NormalizeShelves(changed) };
        }
        Save();
    }

    public void DeleteShelf(string shelfName)
    {
        var normalized = NormalizeShelf(shelfName);
        for (var index = 0; index < _data.Books.Count; index++)
        {
            var book = _data.Books[index];
            _data.Books[index] = book with { Shelves = book.Shelves.Where(name => !string.Equals(name, normalized, StringComparison.CurrentCultureIgnoreCase)).ToList() };
        }
        Save();
    }

    public void SetLastOpened(string? bookId)
    {
        _data.LastOpenedBookId = bookId;
        Save();
    }

    public string BookPath(string id)
    {
        if (!IsSafeId(id)) throw new ArgumentException("The library book identifier is invalid.", nameof(id));
        var format = Get(id)?.Format;
        return format == "pdf" ? PdfBookPath(id) : Path.Combine(AppPaths.Books, id + ".epub");
    }

    public static string PdfBookPath(string id)
    {
        if (!IsSafeId(id)) throw new ArgumentException("The library book identifier is invalid.", nameof(id));
        return Path.Combine(AppPaths.Pdfs, id + ".pdf");
    }

    private void Upsert(LibraryBookEntry entry, bool markLastOpened)
    {
        var existing = _data.Books.FindIndex(item => item.Id == entry.Id);
        if (existing >= 0) _data.Books[existing] = entry;
        else _data.Books.Insert(0, entry);
        if (markLastOpened) _data.LastOpenedBookId = entry.Id;
        Save();
    }

    private static LibraryBookEntry NormalizeEntry(LibraryBookEntry book) => book with
    {
        Title = Limit(book.Title, 240, "Untitled"),
        Format = book.Format == "pdf" ? "pdf" : "epub",
        Author = Limit(book.Author, 240, ""),
        Series = Limit(book.Series, 240, ""),
        Description = Limit(book.Description, 2000, ""),
        CoverFileName = IsSafeCoverName(book.Id, book.CoverFileName) ? book.CoverFileName : "",
        Rating = book.Rating is >= 0 and <= 5 ? book.Rating : null,
        Shelves = NormalizeShelves(book.Shelves ?? [])
    };

    private static List<string> NormalizeShelves(IEnumerable<string> names) => names
        .Select(NormalizeShelf)
        .Where(name => name.Length > 0)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .Take(40)
        .ToList();

    private static string NormalizeShelf(string? name) => Limit(string.Join(' ', (name ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), 40, "");
    private static string Limit(string? value, int maximum, string fallback)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) return fallback;
        return trimmed.Length > maximum ? trimmed[..maximum] : trimmed;
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        var temporary = AppPaths.CatalogFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_data, JsonOptions));
        File.Move(temporary, AppPaths.CatalogFile, overwrite: true);
    }

    private static bool IsSafeId(string? id) => id is { Length: 64 } && id.All(char.IsAsciiHexDigit);

    private static bool IsSafeCoverName(string id, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)) return false;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return fileName.Equals(id + extension, StringComparison.OrdinalIgnoreCase)
            && extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif";
    }

    private sealed class CatalogData
    {
        public List<LibraryBookEntry> Books { get; set; } = [];
        public string? LastOpenedBookId { get; set; }
    }
}
