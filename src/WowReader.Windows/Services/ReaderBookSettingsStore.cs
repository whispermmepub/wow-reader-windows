using System.IO;
using System.Text.Json;
using WowReader.Windows.Models;

namespace WowReader.Windows.Services;

public sealed class ReaderBookSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Dictionary<string, ReaderBookSettings> _settings = new(StringComparer.Ordinal);

    public void Load()
    {
        AppPaths.EnsureCreated();
        _settings.Clear();
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ReaderBookSettings>>(File.ReadAllText(AppPaths.BookSettingsFile));
            if (loaded is null) return;
            foreach (var (id, profile) in loaded)
            {
                if (!IsSafeId(id) || profile is null) continue;
                profile.Normalize();
                _settings[id] = profile.Clone();
            }
        }
        catch (FileNotFoundException) { }
        catch (JsonException) { }
    }

    public bool HasOverride(string bookId) => _settings.ContainsKey(bookId);
    public ReaderBookSettings? Get(string bookId) => _settings.TryGetValue(bookId, out var settings) ? settings.Clone() : null;

    public void Set(string bookId, ReaderBookSettings settings)
    {
        if (!IsSafeId(bookId)) throw new ArgumentException("The book identifier is invalid.", nameof(bookId));
        var normalized = settings.Clone();
        normalized.Normalize();
        _settings[bookId] = normalized;
        Save();
    }

    public void Remove(string bookId)
    {
        if (_settings.Remove(bookId)) Save();
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        var temporary = AppPaths.BookSettingsFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_settings, JsonOptions));
        File.Move(temporary, AppPaths.BookSettingsFile, overwrite: true);
    }

    private static bool IsSafeId(string? id) => id is { Length: 64 } && id.All(char.IsAsciiHexDigit);
}
