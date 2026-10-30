using System.Text.Json;
using System.IO;
using WowReader.Windows.Models;

namespace WowReader.Windows.Services;

public sealed class ReadingProgressStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private Dictionary<string, ReadingPosition> _positions = new(StringComparer.Ordinal);
    private volatile bool _dirty;
    private long _saveGeneration;
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);

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

    public Task SaveAsync(string bookId, int chapterIndex, double scrollFraction)
    {
        if (bookId.Length != 64 || !bookId.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("The book identifier is invalid.", nameof(bookId));
        }

        var safeChapterIndex = Math.Max(0, chapterIndex);
        var safeFraction = Math.Clamp(double.IsFinite(scrollFraction) ? scrollFraction : 0d, 0d, 1d);
        if (!_dirty && _positions.TryGetValue(bookId, out var previous)
            && previous.ChapterIndex == safeChapterIndex
            && Math.Abs(previous.ScrollFraction - safeFraction) < 0.0005d)
        {
            return Task.CompletedTask;
        }

        var updated = new Dictionary<string, ReadingPosition>(_positions, StringComparer.Ordinal)
        {
            [bookId] = new ReadingPosition(safeChapterIndex, safeFraction, DateTimeOffset.UtcNow)
        };
        _positions = updated;
        _dirty = true;
        var generation = Interlocked.Increment(ref _saveGeneration);
        return PersistAsync(updated, generation);
    }

    private async Task PersistAsync(Dictionary<string, ReadingPosition> snapshot, long generation)
    {
        await _saveSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != Volatile.Read(ref _saveGeneration)) return;
            await Task.Run(() =>
            {
                AppPaths.EnsureCreated();
                var temporary = AppPaths.ProgressFile + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
                File.Move(temporary, AppPaths.ProgressFile, overwrite: true);
            }).ConfigureAwait(false);
            if (generation == Volatile.Read(ref _saveGeneration)) _dirty = false;
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }
}
