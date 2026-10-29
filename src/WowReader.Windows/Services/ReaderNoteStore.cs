using System.IO;
using System.Text.Json;

namespace WowReader.Windows.Services;

public sealed record ReaderNote(string Id, string BookId, int ChapterIndex, double ScrollFraction,
    string ChapterTitle, string Quote, string Note, string HighlightColor, DateTimeOffset CreatedAt);

public sealed class ReaderNoteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly List<ReaderNote> _notes = [];

    public void Load()
    {
        AppPaths.EnsureCreated();
        _notes.Clear();
        try
        {
            var loaded = JsonSerializer.Deserialize<List<ReaderNote>>(File.ReadAllText(AppPaths.NotesFile));
            if (loaded is null)
            {
                return;
            }

            _notes.AddRange(loaded
                .Where(note => note is not null && IsSafeBookId(note.BookId) && Guid.TryParse(note.Id, out _)
                    && note.ChapterIndex >= 0)
                .Select(note => note with
                {
                    ScrollFraction = Math.Clamp(double.IsFinite(note.ScrollFraction) ? note.ScrollFraction : 0d, 0d, 1d),
                    ChapterTitle = Limit(note.ChapterTitle, 240),
                    Quote = Limit(note.Quote, 1200),
                    Note = Limit(note.Note, 4000),
                    HighlightColor = NormalizeColor(note.HighlightColor)
                })
                .OrderByDescending(note => note.CreatedAt)
                .Take(10000));
        }
        catch (FileNotFoundException)
        {
            // A new local installation has no annotations yet.
        }
        catch (JsonException)
        {
            // Keep startup resilient if the local annotation file is malformed.
        }
    }

    public IReadOnlyList<ReaderNote> All() => _notes.OrderByDescending(note => note.CreatedAt).ToArray();

    public IReadOnlyList<ReaderNote> ForBook(string bookId) =>
        _notes.Where(note => note.BookId == bookId).OrderByDescending(note => note.CreatedAt).ToArray();

    public ReaderNote Add(string bookId, int chapterIndex, double scrollFraction, string chapterTitle,
        string quote, string note, string highlightColor)
    {
        if (!IsSafeBookId(bookId))
        {
            throw new ArgumentException("The book identifier is invalid.", nameof(bookId));
        }
        if (chapterIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chapterIndex));
        }

        var item = new ReaderNote(Guid.NewGuid().ToString("N"), bookId, chapterIndex,
            Math.Clamp(double.IsFinite(scrollFraction) ? scrollFraction : 0d, 0d, 1d),
            Limit(chapterTitle, 240), Limit(quote, 1200), Limit(note, 4000), NormalizeColor(highlightColor), DateTimeOffset.UtcNow);
        if (item.Quote.Length == 0 && item.Note.Length == 0)
        {
            throw new ArgumentException("Add a note or select text to highlight.");
        }
        _notes.Insert(0, item);
        Save();
        return item;
    }

    public void Remove(string id)
    {
        if (_notes.RemoveAll(note => note.Id == id) > 0)
        {
            Save();
        }
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        var temporary = AppPaths.NotesFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_notes, JsonOptions));
        File.Move(temporary, AppPaths.NotesFile, overwrite: true);
    }

    private static string NormalizeColor(string? color) => color?.ToLowerInvariant() switch
    {
        "teal" => "teal",
        "pink" => "pink",
        "purple" => "purple",
        "none" => "none",
        _ => "yellow"
    };

    private static string Limit(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim()[..Math.Min(value.Trim().Length, maximum)];

    private static bool IsSafeBookId(string? id) => id is { Length: 64 } && id.All(char.IsAsciiHexDigit);
}
