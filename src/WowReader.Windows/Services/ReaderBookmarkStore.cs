using System.IO;
using System.Text.Json;

namespace WowReader.Windows.Services;

public sealed record ReaderBookmark(string BookId, int ChapterIndex, double ScrollFraction, string ChapterTitle, DateTimeOffset CreatedAt);

public sealed class ReaderBookmarkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Dictionary<string, List<ReaderBookmark>> _bookmarks = new(StringComparer.Ordinal);

    public void Load()
    {
        AppPaths.EnsureCreated();
        _bookmarks.Clear();
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<ReaderBookmark>>>(File.ReadAllText(AppPaths.BookmarksFile));
            if (loaded is null)
            {
                return;
            }

            foreach (var (bookId, items) in loaded)
            {
                if (!IsSafeId(bookId) || items is null)
                {
                    continue;
                }

                _bookmarks[bookId] = items
                    .Where(item => item is not null && item.BookId == bookId && item.ChapterIndex >= 0)
                    .Select(item => item with
                    {
                        ScrollFraction = Math.Clamp(double.IsFinite(item.ScrollFraction) ? item.ScrollFraction : 0d, 0d, 1d),
                        ChapterTitle = string.IsNullOrWhiteSpace(item.ChapterTitle) ? "Chapter" : item.ChapterTitle.Trim()
                    })
                    .OrderByDescending(item => item.CreatedAt)
                    .Take(500)
                    .ToList();
            }
        }
        catch (FileNotFoundException)
        {
            // No bookmarks exist on a new installation.
        }
        catch (JsonException)
        {
            // Ignore malformed local bookmarks instead of preventing reader startup.
        }
    }

    public IReadOnlyList<ReaderBookmark> ForBook(string bookId) =>
        _bookmarks.TryGetValue(bookId, out var items) ? items.ToArray() : Array.Empty<ReaderBookmark>();

    public bool IsBookmarked(string bookId, int chapterIndex, double scrollFraction) =>
        ForBook(bookId).Any(item => item.ChapterIndex == chapterIndex && Math.Abs(item.ScrollFraction - scrollFraction) <= 0.04d);

    public bool Toggle(ReaderBookmark bookmark)
    {
        if (!IsSafeId(bookmark.BookId))
        {
            throw new ArgumentException("The book identifier is invalid.", nameof(bookmark));
        }

        if (!_bookmarks.TryGetValue(bookmark.BookId, out var items))
        {
            items = [];
            _bookmarks[bookmark.BookId] = items;
        }

        var existing = items.FindIndex(item => item.ChapterIndex == bookmark.ChapterIndex
            && Math.Abs(item.ScrollFraction - bookmark.ScrollFraction) <= 0.04d);
        if (existing >= 0)
        {
            items.RemoveAt(existing);
            if (items.Count == 0)
            {
                _bookmarks.Remove(bookmark.BookId);
            }
            Save();
            return false;
        }

        items.Insert(0, bookmark with
        {
            ScrollFraction = Math.Clamp(double.IsFinite(bookmark.ScrollFraction) ? bookmark.ScrollFraction : 0d, 0d, 1d),
            ChapterTitle = string.IsNullOrWhiteSpace(bookmark.ChapterTitle) ? "Chapter" : bookmark.ChapterTitle.Trim()
        });
        if (items.Count > 500)
        {
            items.RemoveRange(500, items.Count - 500);
        }
        Save();
        return true;
    }

    public void Remove(ReaderBookmark bookmark)
    {
        if (_bookmarks.TryGetValue(bookmark.BookId, out var items))
        {
            items.RemoveAll(item => item.ChapterIndex == bookmark.ChapterIndex
                && Math.Abs(item.ScrollFraction - bookmark.ScrollFraction) <= 0.001d
                && item.CreatedAt == bookmark.CreatedAt);
            if (items.Count == 0)
            {
                _bookmarks.Remove(bookmark.BookId);
            }
            Save();
        }
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        var temporary = AppPaths.BookmarksFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_bookmarks, JsonOptions));
        File.Move(temporary, AppPaths.BookmarksFile, overwrite: true);
    }

    private static bool IsSafeId(string? id) => id is { Length: 64 } && id.All(char.IsAsciiHexDigit);
}
