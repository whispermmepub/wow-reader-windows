using System.IO;
using System.Text.Json;

namespace WowReader.Windows.Services;

public sealed record VocabularyEntry(
    string Word,
    string Definition,
    string SourceBook,
    int LookupCount,
    bool Learned,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public sealed class VocabularyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Dictionary<string, VocabularyEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<VocabularyEntry> All => _entries.Values
        .OrderByDescending(entry => entry.LastSeenAt)
        .ThenBy(entry => entry.Word, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public void Load()
    {
        AppPaths.EnsureCreated();
        _entries.Clear();
        try
        {
            var loaded = JsonSerializer.Deserialize<List<VocabularyEntry>>(File.ReadAllText(AppPaths.VocabularyFile));
            if (loaded is null) return;
            foreach (var entry in loaded)
            {
                var word = NormalizeWord(entry.Word);
                if (word.Length == 0) continue;
                _entries[Key(word)] = entry with
                {
                    Word = word,
                    Definition = Limit(entry.Definition, 1200),
                    SourceBook = Limit(entry.SourceBook, 240),
                    LookupCount = Math.Clamp(entry.LookupCount, 1, 1_000_000)
                };
            }
        }
        catch (FileNotFoundException) { }
        catch (JsonException) { }
    }

    public IReadOnlyList<VocabularyEntry> Capture(string? selectedText, string? sourceBook)
    {
        var words = NormalizeWords(selectedText);
        foreach (var word in words)
        {
            var key = Key(word);
            var now = DateTimeOffset.UtcNow;
            if (_entries.TryGetValue(key, out var existing))
            {
                _entries[key] = existing with
                {
                    LookupCount = Math.Min(1_000_000, existing.LookupCount + 1),
                    SourceBook = Limit(string.IsNullOrWhiteSpace(sourceBook) ? existing.SourceBook : sourceBook, 240),
                    LastSeenAt = now
                };
            }
            else
            {
                _entries[key] = new VocabularyEntry(word, "", Limit(sourceBook ?? "", 240), 1, false, now, now);
            }
        }
        if (words.Count > 0) Save();
        return words.Select(word => _entries[Key(word)]).ToArray();
    }

    public void SetDefinition(string word, string? definition)
    {
        var key = Key(word);
        if (!_entries.TryGetValue(key, out var entry)) return;
        _entries[key] = entry with { Definition = Limit(definition ?? "", 1200) };
        Save();
    }

    public void SetLearned(string word, bool learned)
    {
        var key = Key(word);
        if (!_entries.TryGetValue(key, out var entry)) return;
        _entries[key] = entry with { Learned = learned };
        Save();
    }

    public void Remove(string word)
    {
        if (_entries.Remove(Key(word))) Save();
    }

    public static IReadOnlyList<string> NormalizeWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var cleaned = new string(text.Trim().Where(character => !char.IsControl(character)).Take(120).ToArray());
        char[] edgePunctuation = [' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '…', '“', '”'];
        return cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => NormalizeWord(word.Trim(edgePunctuation)))
            .Where(word => word.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        var temporary = AppPaths.VocabularyFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(All, JsonOptions));
        File.Move(temporary, AppPaths.VocabularyFile, overwrite: true);
    }

    private static string Key(string word) => NormalizeWord(word).ToUpperInvariant();
    private static string NormalizeWord(string? word) => string.Join(' ', (word ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Limit(string? value, int maximum)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length > maximum ? trimmed[..maximum] : trimmed;
    }
}
