using System.Text.Json;
using System.IO;
using WowReader.Windows.Models;

namespace WowReader.Windows.Services;

public static class AppPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WoWReader");
    public static string Books => Path.Combine(Root, "Books");
    public static string Cache => Path.Combine(Root, "Cache");
    public static string Fonts => Path.Combine(Root, "Fonts");
    public static string WebViewProfile => Path.Combine(Root, "WebView2Profile");
    public static string CatalogFile => Path.Combine(Root, "library-catalog.json");
    public static string ProgressFile => Path.Combine(Root, "reading-progress.json");
    public static string FontCatalogFile => Path.Combine(Root, "font-catalog.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Books);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Fonts);
        Directory.CreateDirectory(WebViewProfile);
    }
}

public sealed record LibraryBookEntry(string Id, string Title);

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
                .Where(book => IsSafeId(book.Id) && File.Exists(BookPath(book.Id)))
                .GroupBy(book => book.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (_data.LastOpenedBookId is not null && !_data.Books.Any(book => book.Id == _data.LastOpenedBookId))
            {
                _data.LastOpenedBookId = null;
            }
        }
        catch (FileNotFoundException)
        {
            _data = new CatalogData();
        }
        catch (JsonException)
        {
            _data = new CatalogData();
        }
    }

    public void AddOrUpdate(EpubBook book)
    {
        var entry = new LibraryBookEntry(book.Id, book.Title);
        var existing = _data.Books.FindIndex(item => item.Id == book.Id);
        if (existing >= 0)
        {
            _data.Books[existing] = entry;
        }
        else
        {
            _data.Books.Insert(0, entry);
        }
        _data.LastOpenedBookId = book.Id;
        Save();
    }

    public void SetLastOpened(string? bookId)
    {
        _data.LastOpenedBookId = bookId;
        Save();
    }

    public string BookPath(string id)
    {
        if (!IsSafeId(id))
        {
            throw new ArgumentException("The library book identifier is invalid.", nameof(id));
        }
        return Path.Combine(AppPaths.Books, id + ".epub");
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        var temporary = AppPaths.CatalogFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_data, JsonOptions));
        File.Move(temporary, AppPaths.CatalogFile, overwrite: true);
    }

    private static bool IsSafeId(string? id) =>
        id is { Length: 64 } && id.All(character => char.IsAsciiHexDigit(character));

    private sealed class CatalogData
    {
        public List<LibraryBookEntry> Books { get; set; } = [];
        public string? LastOpenedBookId { get; set; }
    }
}
