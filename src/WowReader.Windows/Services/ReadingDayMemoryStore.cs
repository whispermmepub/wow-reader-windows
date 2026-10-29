using System.Globalization;
using System.IO;
using System.Text.Json;

namespace WowReader.Windows.Services;

public sealed class ReadingDayMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string DateFormat = "yyyy-MM-dd";
    private readonly Dictionary<string, string> _memories = new(StringComparer.Ordinal);

    public void Load()
    {
        AppPaths.EnsureCreated();
        _memories.Clear();
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(AppPaths.DayMemoryFile));
            if (data is null) return;
            foreach (var (key, value) in data)
            {
                var separator = key.IndexOf('|');
                if (separator <= 0 || !DateOnly.TryParseExact(key[..separator], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) continue;
                var id = key[(separator + 1)..];
                if (!IsSafeId(id) || string.IsNullOrWhiteSpace(value)) continue;
                _memories[key] = Limit(value);
            }
        }
        catch (FileNotFoundException) { }
        catch (JsonException) { }
    }

    public string Get(DateOnly date, string bookId) => _memories.TryGetValue(Key(date, bookId), out var value) ? value : "";
    public bool HasMemory(DateOnly date, string bookId) => _memories.ContainsKey(Key(date, bookId));

    public void Set(DateOnly date, string bookId, string? text)
    {
        var key = Key(date, bookId);
        var value = Limit(text ?? "");
        if (value.Length == 0) _memories.Remove(key);
        else _memories[key] = value;
        Save();
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        var temporary = AppPaths.DayMemoryFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_memories, JsonOptions));
        File.Move(temporary, AppPaths.DayMemoryFile, overwrite: true);
    }

    private static string Key(DateOnly date, string id)
    {
        if (!IsSafeId(id)) throw new ArgumentException("The book identifier is invalid.", nameof(id));
        return date.ToString(DateFormat, CultureInfo.InvariantCulture) + "|" + id;
    }

    private static bool IsSafeId(string? id) => id is { Length: 64 } && id.All(char.IsAsciiHexDigit);
    private static string Limit(string value) => value.Trim().Length > 4000 ? value.Trim()[..4000] : value.Trim();
}
