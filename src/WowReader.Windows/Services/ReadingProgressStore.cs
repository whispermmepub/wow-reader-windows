using System.Text.Json;
using System.IO;
using WowReader.Windows.Models;

namespace WowReader.Windows.Services;

public sealed class ReadingProgressStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private Dictionary<string, ReadingPosition> _positions = new(StringComparer.Ordinal);

    public void Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            _positions = JsonSerializer.Deserialize<Dictionary<string, ReadingPosition>>(File.ReadAllText(AppPaths.ProgressFile))
                ?? new Dictionary<string, ReadingPosition>(StringComparer.Ordinal);
        }
        catch (FileNotFoundException)
        {
            _positions = new Dictionary<string, ReadingPosition>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            _positions = new Dictionary<string, ReadingPosition>(StringComparer.Ordinal);
        }
    }

    public ReadingPosition Get(string bookId) =>
        _positions.TryGetValue(bookId, out var position) ? position : ReadingPosition.Start;

    public bool Has(string bookId) => _positions.ContainsKey(bookId);

    public void Save(string bookId, int chapterIndex, double scrollFraction)
    {
        if (bookId.Length != 64 || !bookId.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("The book identifier is invalid.", nameof(bookId));
        }

        _positions[bookId] = new ReadingPosition(
            Math.Max(0, chapterIndex),
            Math.Clamp(double.IsFinite(scrollFraction) ? scrollFraction : 0d, 0d, 1d),
            DateTimeOffset.UtcNow);

        AppPaths.EnsureCreated();
        var temporary = AppPaths.ProgressFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_positions, JsonOptions));
        File.Move(temporary, AppPaths.ProgressFile, overwrite: true);
    }
}
