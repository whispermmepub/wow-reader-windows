using System.IO;
using System.Text.Json;

namespace WowReader.Windows.Services;

public sealed class BookCompletionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Dictionary<string, DateTimeOffset> _finished = new(StringComparer.Ordinal);

    public void Load()
    {
        AppPaths.EnsureCreated();
        _finished.Clear();
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(AppPaths.CompletionFile));
            if (loaded is null) return;
            foreach (var (bookId, finishedAt) in loaded)
            {
                if (IsSafeId(bookId)) _finished[bookId] = finishedAt;
            }
        }
        catch (FileNotFoundException)
        {
            // No completed books exist on a new installation.
        }
        catch (JsonException)
        {
            // Malformed local completion state should not prevent reader startup.
        }
    }

    public bool IsFinished(string bookId) => _finished.ContainsKey(bookId);

    public int Count => _finished.Count;

    public void SetFinished(string bookId, bool finished)
    {
        if (!IsSafeId(bookId)) throw new ArgumentException("The book identifier is invalid.", nameof(bookId));
        if (finished) _finished[bookId] = DateTimeOffset.UtcNow;
        else _finished.Remove(bookId);
        Save();
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        var temporary = AppPaths.CompletionFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_finished, JsonOptions));
        File.Move(temporary, AppPaths.CompletionFile, overwrite: true);
    }

    private static bool IsSafeId(string? id) => id is { Length: 64 } && id.All(char.IsAsciiHexDigit);
}
