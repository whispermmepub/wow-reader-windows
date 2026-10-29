using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using WowReader.Windows.Models;
using WowReader.Windows.Services;

namespace WowReader.Windows;

public partial class MainWindow : Window
{
    private const string ReaderHost = "wowreader.local";
    private readonly EpubBookService _epubService = new();
    private readonly LocalLibraryStore _library = new();
    private readonly ReadingProgressStore _progress = new();
    private readonly LocalFontStore _fonts = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private CoreWebView2Environment? _webEnvironment;
    private EpubBook? _book;
    private int _chapterIndex;
    private double _pendingScrollFraction;
    private bool _documentLoaded;
    private bool _suppressPicker;
    private bool _suppressFontPicker;
    private bool _isClosing;
    private bool _allowClose;
    private string? _mappedBookId;

    public MainWindow()
    {
        InitializeComponent();
        _saveTimer.Tick += SaveTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            _webEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.WebViewProfile);
            await ReaderBrowser.EnsureCoreWebView2Async(_webEnvironment);
            ConfigureWebView();

            _library.Load();
            _progress.Load();
            _fonts.Load();
            _suppressPicker = true;
            _suppressFontPicker = true;
            RefreshLibraryPicker(_library.LastOpenedBookId);
            RefreshFontPicker(_fonts.SelectedFontId);
            _suppressPicker = false;
            _suppressFontPicker = false;
            _saveTimer.Start();

            if (_library.LastOpenedBookId is { } lastId)
            {
                await OpenLibraryBookAsync(lastId);
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = "WebView2 could not start";
            MessageBox.Show(this,
                "The reader could not start its embedded browser. Install or update the Microsoft Edge WebView2 Evergreen Runtime, then try again.\n\n" + exception.Message,
                "WoW Reader for Windows", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ConfigureWebView()
    {
        var core = ReaderBrowser.CoreWebView2 ?? throw new InvalidOperationException("WebView2 did not initialize.");
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += CoreWebResourceRequested;
        core.NavigationStarting += CoreNavigationStarting;
        core.NavigationCompleted += CoreNavigationCompleted;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.DownloadStarting += (_, args) =>
        {
            args.Cancel = true;
            args.Handled = true;
        };
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
    }

    private void CoreWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Host.Equals(ReaderHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Response = _webEnvironment?.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Content-Type: text/plain");
    }

    private void CoreNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(ReaderHost, StringComparison.OrdinalIgnoreCase)
            || _book is null)
        {
            e.Cancel = true;
            return;
        }

        try
        {
            var localPath = GetPathFromReaderUri(uri);
            var bookRoot = Path.GetFullPath(_book.ExtractedRoot) + Path.DirectorySeparatorChar;
            var extension = Path.GetExtension(localPath);
            var isHtml = extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".xhtml", StringComparison.OrdinalIgnoreCase);
            if (!isHtml || !localPath.StartsWith(bookRoot, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            e.Cancel = true;
        }
    }

    private async void CoreNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _documentLoaded = e.IsSuccess;
        if (!e.IsSuccess || _book is null)
        {
            return;
        }

        var activePath = GetPathFromReaderUri(ReaderBrowser.Source);
        var spineIndex = _book.Chapters.ToList().FindIndex(chapter =>
            string.Equals(Path.GetFullPath(chapter.LocalPath), Path.GetFullPath(activePath), StringComparison.OrdinalIgnoreCase));
        if (spineIndex >= 0)
        {
            _chapterIndex = spineIndex;
        }

        await ApplySelectedFontAsync();
        var fraction = _pendingScrollFraction;
        _pendingScrollFraction = 0d;
        if (fraction > 0d)
        {
            var safeFraction = fraction.ToString("0.########", CultureInfo.InvariantCulture);
            try
            {
                await ReaderBrowser.CoreWebView2.ExecuteScriptAsync(
                    $"window.scrollTo(0, Math.round((document.documentElement.scrollHeight - window.innerHeight) * {safeFraction}));");
            }
            catch (InvalidOperationException)
            {
                // A page that blocks host script execution will still open; restoring intra-chapter position is best effort.
            }
        }

        ReaderBrowser.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        UpdateReaderLabels();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open an EPUB book",
            Filter = "EPUB books (*.epub)|*.epub",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await SaveCurrentPositionAsync();
        SetBusy(true, "Importing EPUB locally…");
        try
        {
            var book = await _epubService.ImportAsync(dialog.FileName);
            _library.AddOrUpdate(book);
            _suppressPicker = true;
            RefreshLibraryPicker(book.Id);
            _suppressPicker = false;
            await SetBookAsync(book, _progress.Get(book.Id));
        }
        catch (Exception exception)
        {
            _suppressPicker = false;
            StatusText.Text = "Import failed";
            MessageBox.Show(this, exception.Message, "Could not open EPUB", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, "Ready");
        }
    }

    private async void AddFontButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add a custom reader font",
            Filter = "Font files (*.ttf;*.otf;*.woff;*.woff2)|*.ttf;*.otf;*.woff;*.woff2",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ReaderFontEntry? addedFont = null;
        SetBusy(true, "Importing font on this PC…");
        try
        {
            addedFont = await Task.Run(() => _fonts.Import(dialog.FileName));
            _suppressFontPicker = true;
            RefreshFontPicker(addedFont.Id);
            _suppressFontPicker = false;
            await ApplySelectedFontAsync();
        }
        catch (Exception exception)
        {
            _suppressFontPicker = false;
            MessageBox.Show(this, exception.Message, "Could not add font", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, "Ready");
        }
        if (addedFont is not null)
        {
            StatusText.Text = _book is null
                ? $"Added {addedFont.Name} locally. Open an EPUB to use it."
                : $"Using {addedFont.Name} for this reader. Font remains on this PC.";
        }
    }

    private async void FontPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFontPicker || FontPicker.SelectedValue is not string id || !IsLoaded || _isClosing)
        {
            return;
        }
        try
        {
            if (string.IsNullOrEmpty(id))
            {
                _fonts.Select(null);
            }
            else if (id.StartsWith("custom:", StringComparison.Ordinal))
            {
                _fonts.Select(id["custom:".Length..]);
            }
            else
            {
                _fonts.Select(id);
            }
            await ApplySelectedFontAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = "Could not apply the selected font";
            MessageBox.Show(this, exception.Message, "Could not apply font", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void LibraryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPicker || LibraryPicker.SelectedValue is not string id || !IsLoaded || _isClosing)
        {
            return;
        }
        await OpenLibraryBookAsync(id);
    }

    private async Task OpenLibraryBookAsync(string id)
    {
        try
        {
            await SaveCurrentPositionAsync();
            SetBusy(true, "Opening from this PC…");
            var bookPath = _library.BookPath(id);
            var book = await _epubService.OpenAsync(id, bookPath);
            _library.SetLastOpened(id);
            await SetBookAsync(book, _progress.Get(id));
        }
        catch (Exception exception)
        {
            StatusText.Text = "Could not reopen this book";
            MessageBox.Show(this, exception.Message, "Could not open EPUB", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false, "Ready");
        }
    }

    private Task SetBookAsync(EpubBook book, ReadingPosition position)
    {
        _book = book;
        _chapterIndex = Math.Clamp(position.ChapterIndex, 0, book.Chapters.Count - 1);
        _pendingScrollFraction = Math.Clamp(position.ScrollFraction, 0d, 1d);
        _documentLoaded = false;

        var core = ReaderBrowser.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not ready.");
        if (!string.Equals(_mappedBookId, book.Id, StringComparison.Ordinal))
        {
            if (_mappedBookId is not null)
            {
                core.ClearVirtualHostNameToFolderMapping(ReaderHost);
            }
            core.SetVirtualHostNameToFolderMapping(ReaderHost, book.ExtractedRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            _mappedBookId = book.Id;
        }

        NavigateToCurrentChapter();
        UpdateReaderLabels();
        StatusText.Text = "Reading locally — no account or sync";
        return Task.CompletedTask;
    }

    private async void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_book is null || _chapterIndex <= 0)
        {
            return;
        }
        await SaveCurrentPositionAsync();
        _chapterIndex--;
        _pendingScrollFraction = 0d;
        NavigateToCurrentChapter();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_book is null || _chapterIndex >= _book.Chapters.Count - 1)
        {
            return;
        }
        await SaveCurrentPositionAsync();
        _chapterIndex++;
        _pendingScrollFraction = 0d;
        NavigateToCurrentChapter();
    }

    private void NavigateToCurrentChapter()
    {
        if (_book is null || ReaderBrowser.CoreWebView2 is null || _book.Chapters.Count == 0)
        {
            return;
        }
        _chapterIndex = Math.Clamp(_chapterIndex, 0, _book.Chapters.Count - 1);
        _documentLoaded = false;
        var chapter = _book.Chapters[_chapterIndex];
        var relative = Path.GetRelativePath(_book.ExtractedRoot, chapter.LocalPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var escapedPath = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        ReaderBrowser.CoreWebView2.Navigate($"https://{ReaderHost}/{escapedPath}");
        ReaderBrowser.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        UpdateReaderLabels();
    }

    private async Task SaveCurrentPositionAsync()
    {
        if (_book is null || !_documentLoaded || ReaderBrowser.CoreWebView2 is null)
        {
            return;
        }
        try
        {
            var result = await ReaderBrowser.CoreWebView2.ExecuteScriptAsync(
                "(function(){const d=document.documentElement;const m=Math.max(0,d.scrollHeight-window.innerHeight);return m===0?0:Math.max(0,Math.min(1,window.scrollY/m));})()")
                .ConfigureAwait(true);
            var fraction = JsonSerializer.Deserialize<double>(result);
            if (!double.IsFinite(fraction))
            {
                fraction = 0d;
            }
            _progress.Save(_book.Id, _chapterIndex, Math.Clamp(fraction, 0d, 1d));
            UpdateReaderLabels(fraction);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Reading — position save will retry";
        }
    }

    private async void SaveTimer_Tick(object? sender, EventArgs e)
    {
        await SaveCurrentPositionAsync();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }
        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }
        _isClosing = true;
        _saveTimer.Stop();
        await SaveCurrentPositionAsync();
        _allowClose = true;
        Close();
    }

    private void RefreshLibraryPicker(string? selectedId)
    {
        LibraryPicker.ItemsSource = _library.Books.ToList();
        LibraryPicker.SelectedValue = selectedId;
    }

    private void UpdateReaderLabels(double? fraction = null)
    {
        if (_book is null || _book.Chapters.Count == 0)
        {
            PreviousButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            ProgressText.Text = "Local EPUB reader";
            return;
        }
        PreviousButton.IsEnabled = _chapterIndex > 0;
        NextButton.IsEnabled = _chapterIndex < _book.Chapters.Count - 1;
        var position = fraction ?? _progress.Get(_book.Id).ScrollFraction;
        var percent = _book.OverallPercent(_chapterIndex, position);
        var chapter = _book.Chapters[_chapterIndex];
        ProgressText.Text = $"{_book.Title}  ·  {percent}%";
        StatusText.Text = $"Chapter {_chapterIndex + 1} of {_book.Chapters.Count}: {chapter.Title}  ·  position saved on this PC";
    }

    private void SetBusy(bool isBusy, string status)
    {
        OpenButton.IsEnabled = !isBusy;
        LibraryPicker.IsEnabled = !isBusy;
        AddFontButton.IsEnabled = !isBusy;
        FontPicker.IsEnabled = !isBusy;
        PreviousButton.IsEnabled = !isBusy && _book is not null && _chapterIndex > 0;
        NextButton.IsEnabled = !isBusy && _book is not null && _chapterIndex < (_book?.Chapters.Count ?? 0) - 1;
        StatusText.Text = status;
    }

    private void RefreshFontPicker(string? selectedId)
    {
        var systemFonts = Fonts.SystemFontFamilies
            .Select(family => family.Source.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var choices = new List<FontPickerItem> { new(string.Empty, "Publisher font (book default)") };
        choices.AddRange(systemFonts.Select(name => new FontPickerItem("system:" + name, "Windows: " + name)));
        choices.AddRange(_fonts.Fonts.Select(font => new FontPickerItem("custom:" + font.Id, font.Name + " (imported)")));

        var pickerId = selectedId is null
            ? string.Empty
            : selectedId.StartsWith("system:", StringComparison.Ordinal)
                ? selectedId
                : "custom:" + selectedId;
        if (pickerId.StartsWith("system:", StringComparison.Ordinal)
            && !systemFonts.Contains(pickerId["system:".Length..], StringComparer.CurrentCultureIgnoreCase))
        {
            _fonts.Select(null);
            pickerId = string.Empty;
        }
        FontPicker.ItemsSource = choices;
        FontPicker.SelectedValue = pickerId;
    }

    private async Task ApplySelectedFontAsync()
    {
        if (_book is null || !_documentLoaded || ReaderBrowser.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var selectedId = _fonts.SelectedFontId;
            if (selectedId?.StartsWith("system:", StringComparison.Ordinal) == true)
            {
                var familyName = selectedId["system:".Length..];
                var systemCss = $"html,body,body *{{font-family:{CssString(familyName)},serif!important;}}";
                await SetReaderFontCssAsync(systemCss);
                return;
            }

            var fontPath = _fonts.GetSelectedFontPath();
            var fontId = _fonts.SelectedFontId;
            var format = _fonts.GetSelectedFormat();
            if (fontPath is null || fontId is null || format is null)
            {
                await ReaderBrowser.CoreWebView2.ExecuteScriptAsync(
                    "document.getElementById('wow-reader-custom-font')?.remove();");
                return;
            }

            var fontDirectory = Path.Combine(_book.ExtractedRoot, ".wow-reader-fonts");
            Directory.CreateDirectory(fontDirectory);
            var extension = Path.GetExtension(fontPath);
            var localFont = Path.Combine(fontDirectory, fontId + extension);
            File.Copy(fontPath, localFont, overwrite: true);
            var relative = Path.GetRelativePath(_book.ExtractedRoot, localFont)
                .Replace(Path.DirectorySeparatorChar, '/');
            var escapedPath = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
            var fontUri = $"https://{ReaderHost}/{escapedPath}";
            var css = $"@font-face{{font-family:'WoWReaderCustom';src:url('{fontUri}') format('{format}');font-style:normal;font-weight:normal;font-display:swap;}} html,body,body *{{font-family:'WoWReaderCustom',serif!important;}}";
            await SetReaderFontCssAsync(css);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = "Could not apply the selected font";
        }
    }

    private async Task SetReaderFontCssAsync(string css)
    {
        var cssLiteral = JsonSerializer.Serialize(css);
        var script = $"(()=>{{let s=document.getElementById('wow-reader-custom-font');if(!s){{s=document.createElement('style');s.id='wow-reader-custom-font';document.head.appendChild(s);}}s.textContent={cssLiteral};document.fonts?.load('16px WoWReaderCustom');}})();";
        await ReaderBrowser.CoreWebView2!.ExecuteScriptAsync(script);
    }

    private static string CssString(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", string.Empty).Replace("\n", string.Empty);
        return "\"" + escaped + "\"";
    }

    private string GetPathFromReaderUri(Uri? uri)
    {
        if (uri is null || _book is null)
        {
            return string.Empty;
        }
        var relative = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(_book.ExtractedRoot, relative));
    }

    private sealed record FontPickerItem(string Id, string Name);
}
