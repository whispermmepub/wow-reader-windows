using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WowReader.Windows.Models;

namespace WowReader.Windows.Services;

public sealed class LocalFontStore
{
    private const long MaxFontBytes = 8L * 1024L * 1024L;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private FontCatalog _catalog = new();

    public IReadOnlyList<ReaderFontEntry> Fonts => _catalog.Fonts;
    public string? SelectedFontId => _catalog.SelectedFontId;

    public void Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            _catalog = JsonSerializer.Deserialize<FontCatalog>(File.ReadAllText(AppPaths.FontCatalogFile)) ?? new FontCatalog();
        }
        catch (FileNotFoundException)
        {
            _catalog = new FontCatalog();
        }
        catch (JsonException)
        {
            _catalog = new FontCatalog();
        }

        _catalog.Fonts = _catalog.Fonts
            .Where(font => IsSafeId(font.Id) && IsSupportedExtension(Path.GetExtension(font.FileName))
                && string.Equals(font.FileName, font.Id + Path.GetExtension(font.FileName), StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(AppPaths.Fonts, font.FileName)))
            .GroupBy(font => font.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(font => font.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (_catalog.SelectedFontId is not null
            && !(_catalog.SelectedFontId.StartsWith("system:", StringComparison.Ordinal) && _catalog.SelectedFontId.Length > "system:".Length)
            && !_catalog.Fonts.Any(font => font.Id == _catalog.SelectedFontId))
        {
            _catalog.SelectedFontId = null;
        }
    }

    public ReaderFontEntry Import(string sourcePath)
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The selected font file could not be found.", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!IsSupportedExtension(extension))
        {
            throw new InvalidDataException("Choose a TTF, OTF, WOFF, or WOFF2 font file.");
        }
        var info = new FileInfo(sourcePath);
        if (info.Length < 12 || info.Length > MaxFontBytes)
        {
            throw new InvalidDataException("The font must be between 12 bytes and 8 MB.");
        }
        if (!HasSupportedSignature(sourcePath))
        {
            throw new InvalidDataException("The selected file does not have a supported font signature.");
        }

        var id = ComputeId(sourcePath, extension);
        var fileName = id + extension;
        var destination = Path.Combine(AppPaths.Fonts, fileName);
        if (!File.Exists(destination))
        {
            var temporary = destination + ".tmp";
            File.Copy(sourcePath, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }

        var name = Path.GetFileNameWithoutExtension(sourcePath).Trim();
        if (name.Length > 100)
        {
            name = name[..100].Trim();
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Custom font";
        }
        var entry = new ReaderFontEntry(id, name, fileName);
        var existing = _catalog.Fonts.FindIndex(font => font.Id == id);
        if (existing >= 0)
        {
            _catalog.Fonts[existing] = entry;
        }
        else
        {
            _catalog.Fonts.Add(entry);
        }
        _catalog.Fonts = _catalog.Fonts.OrderBy(font => font.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _catalog.SelectedFontId = id;
        Save();
        return entry;
    }

    public string? GetSelectedFontPath()
    {
        var entry = _catalog.Fonts.FirstOrDefault(font => font.Id == _catalog.SelectedFontId);
        if (entry is null || !IsSafeId(entry.Id) || !IsSupportedExtension(Path.GetExtension(entry.FileName)))
        {
            return null;
        }
        var path = Path.GetFullPath(Path.Combine(AppPaths.Fonts, entry.FileName));
        var root = Path.GetFullPath(AppPaths.Fonts) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    public string? GetSelectedFormat()
    {
        var extension = Path.GetExtension(GetSelectedFontPath() ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".ttf" => "truetype",
            ".otf" => "opentype",
            ".woff" => "woff",
            ".woff2" => "woff2",
            _ => null
        };
    }

    public void Select(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            _catalog.SelectedFontId = null;
        }
        else if (id.StartsWith("system:", StringComparison.Ordinal) && id.Length > "system:".Length)
        {
            _catalog.SelectedFontId = id;
        }
        else if (_catalog.Fonts.Any(font => font.Id == id))
        {
            _catalog.SelectedFontId = id;
        }
        else
        {
            throw new ArgumentException("The selected font is not in the local font library.", nameof(id));
        }
        Save();
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        var temporary = AppPaths.FontCatalogFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_catalog, JsonOptions));
        File.Move(temporary, AppPaths.FontCatalogFile, overwrite: true);
    }

    private static string ComputeId(string path, string extension)
    {
        using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, count);
        }
        hash.AppendData(Encoding.UTF8.GetBytes(extension));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool HasSupportedSignature(string path)
    {
        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == 4 &&
            (header.SequenceEqual(new byte[] { 0x00, 0x01, 0x00, 0x00 })
             || header.SequenceEqual("OTTO"u8)
             || header.SequenceEqual("true"u8)
             || header.SequenceEqual("typ1"u8)
             || header.SequenceEqual("wOFF"u8)
             || header.SequenceEqual("wOF2"u8));
    }

    private static bool IsSupportedExtension(string? extension) =>
        extension is not null && (extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".otf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".woff", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".woff2", StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeId(string? id) =>
        id is { Length: 64 } && id.All(character => char.IsAsciiHexDigit(character));

    private sealed class FontCatalog
    {
        public List<ReaderFontEntry> Fonts { get; set; } = [];
        public string? SelectedFontId { get; set; }
    }
}
