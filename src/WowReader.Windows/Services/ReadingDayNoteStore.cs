using System.Globalization;
using System.IO;
using System.Text.Json;

namespace WowReader.Windows.Services;

public sealed class ReadingDayNoteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string DateFormat = "yyyy-MM-dd";
    private readonly Dictionary<string, string> _notes = new(StringComparer.Ordinal);

    public void Load()
    {
        AppPaths.EnsureCreated();
        _notes.Clear();
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(AppPaths.DayNotesFile));
            if (loaded is null) return;
            foreach (var (date, note) in loaded)
            {
                if (DateOnly.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    && !string.IsNullOrWhiteSpace(note))
                {
                    _notes[date] = Limit(note);
                }
            }
        }
        catch (FileNotFoundException)
        {
            // A new installation has no daily reading notes.
        }
        catch (JsonException)
        {
            // Ignore malformed local note data and allow the app to continue.
        }
    }

    public string Get(DateOnly date) => _notes.TryGetValue(Key(date), out var note) ? note : "";

    public bool HasNote(DateOnly date) => _notes.ContainsKey(Key(date));

    public void Set(DateOnly date, string? note)
    {
        var key = Key(date);
        var safeNote = Limit(note ?? "");
        if (safeNote.Length == 0)
        {
            _notes.Remove(key);
        }
        else
        {
            _notes[key] = safeNote;
        }
        Save();
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        var temporary = AppPaths.DayNotesFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_notes, JsonOptions));
        File.Move(temporary, AppPaths.DayNotesFile, overwrite: true);
    }

    private static string Key(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    private static string Limit(string value) => value.Trim().Length > 4000 ? value.Trim()[..4000] : value.Trim();
}
