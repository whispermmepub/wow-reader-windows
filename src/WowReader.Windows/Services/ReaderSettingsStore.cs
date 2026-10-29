using System.IO;
using System.Text.Json;
using WowReader.Windows.Models;

namespace WowReader.Windows.Services;

public sealed class ReaderSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private ReaderSettings _settings = new();

    public ReaderSettings Current => _settings.Clone();

    public void Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            _settings = JsonSerializer.Deserialize<ReaderSettings>(File.ReadAllText(SettingsPath)) ?? new ReaderSettings();
        }
        catch (FileNotFoundException)
        {
            _settings = new ReaderSettings();
        }
        catch (JsonException)
        {
            _settings = new ReaderSettings();
        }
        _settings.Normalize();
    }

    public void Save(ReaderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Clone();
        _settings.Normalize();
        AppPaths.EnsureCreated();
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_settings, JsonOptions));
        File.Move(temporary, SettingsPath, overwrite: true);
    }

    private static string SettingsPath => Path.Combine(AppPaths.Root, "reader-settings.json");
}
