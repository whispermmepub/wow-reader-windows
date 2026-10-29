using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
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
    private readonly ReaderSettingsStore _settingsStore = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _eyeReminderTimer = new() { Interval = TimeSpan.FromMinutes(30) };
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
    private string _pagePositionLabel = "";
    private ReaderSettings _settings = new();
    private bool _suppressSettingsPicker;

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint executionState);

    public MainWindow()
    {
        InitializeComponent();
        _saveTimer.Tick += SaveTimer_Tick;
        _eyeReminderTimer.Tick += EyeReminderTimer_Tick;
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
            _settingsStore.Load();
            _settings = _settingsStore.Current;
            _suppressPicker = true;
            _suppressFontPicker = true;
            _suppressSettingsPicker = true;
            RefreshLibraryPicker(_library.LastOpenedBookId);
            RefreshFontPicker(_fonts.SelectedFontId);
            RefreshSettingsPicker();
            _suppressPicker = false;
            _suppressFontPicker = false;
            _suppressSettingsPicker = false;
            ApplyKeepScreenAwake(_settings.KeepScreenOn);
            UpdateEyeReminderTimer();
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
        core.WebMessageReceived += CoreWebMessageReceived;
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
        await ApplyReaderSettingsAsync();
        var fraction = _pendingScrollFraction;
        _pendingScrollFraction = 0d;
        if (fraction > 0d)
        {
            await RestoreReaderPositionAsync(fraction);
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

    private async void ReadingModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsPicker || ReadingModePicker.SelectedValue is not string mode || !IsLoaded || _isClosing
            || mode == _settings.ReadingMode)
        {
            return;
        }

        await SaveCurrentPositionAsync();
        var fraction = _book is null ? 0d : _progress.Get(_book.Id).ScrollFraction;
        _settings.ReadingMode = mode;
        _settings.Normalize();
        _settingsStore.Save(_settings);
        await ApplyReaderSettingsAsync();
        await RestoreReaderPositionAsync(fraction);
        UpdateReaderLabels(fraction);
    }

    private async void ReaderSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrentPositionAsync();
        var dialog = new ReaderSettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var fraction = _book is null ? 0d : _progress.Get(_book.Id).ScrollFraction;
        _settings = dialog.Settings;
        _settings.Normalize();
        _settingsStore.Save(_settings);
        if (dialog.ResetRequested)
        {
            _fonts.Select(null);
            _suppressFontPicker = true;
            RefreshFontPicker(null);
            _suppressFontPicker = false;
        }
        _suppressSettingsPicker = true;
        RefreshSettingsPicker();
        _suppressSettingsPicker = false;
        ApplyKeepScreenAwake(_settings.KeepScreenOn);
        UpdateEyeReminderTimer();
        if (_book is not null && _documentLoaded)
        {
            await ApplySelectedFontAsync();
            await ApplyReaderSettingsAsync();
            await RestoreReaderPositionAsync(fraction);
        }
        UpdateReaderLabels(fraction);
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
        _pagePositionLabel = "";
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
        await TurnOrNavigateAsync(-1);
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        await TurnOrNavigateAsync(1);
    }

    private async Task TurnOrNavigateAsync(int direction)
    {
        if (_book is null)
        {
            return;
        }

        if (_settings.ReadingMode == "page" && _documentLoaded && ReaderBrowser.CoreWebView2 is not null)
        {
            try
            {
                var result = await ReaderBrowser.CoreWebView2.ExecuteScriptAsync(
                    $"window.wowReaderTurnPage ? window.wowReaderTurnPage({direction}) : false;");
                if (JsonSerializer.Deserialize<bool>(result))
                {
                    await SaveCurrentPositionAsync();
                    return;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or COMException)
            {
                // If the document cannot page-turn, keep chapter navigation available as a fallback.
            }
        }

        var nextIndex = _chapterIndex + direction;
        if (nextIndex < 0 || nextIndex >= _book.Chapters.Count)
        {
            return;
        }
        await SaveCurrentPositionAsync();
        _chapterIndex = nextIndex;
        _pendingScrollFraction = direction < 0 && _settings.ReadingMode == "page" ? 1d : 0d;
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
        _pagePositionLabel = "";
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
                "(function(){if(window.wowReaderGetProgress)return window.wowReaderGetProgress();const d=document.documentElement;const m=Math.max(0,d.scrollHeight-window.innerHeight);return m===0?0:Math.max(0,Math.min(1,window.scrollY/m));})()")
                .ConfigureAwait(true);
            var fraction = JsonSerializer.Deserialize<double>(result);
            if (!double.IsFinite(fraction))
            {
                fraction = 0d;
            }
            if (_settings.ReadingMode == "page")
            {
                var pageLabelJson = await ReaderBrowser.CoreWebView2.ExecuteScriptAsync(
                    "window.wowReaderGetPageLabel?window.wowReaderGetPageLabel():'Page mode'");
                _pagePositionLabel = JsonSerializer.Deserialize<string>(pageLabelJson) ?? "Page mode";
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
        _eyeReminderTimer.Stop();
        await SaveCurrentPositionAsync();
        ApplyKeepScreenAwake(false);
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
            PreviousButton.Content = "‹ Previous";
            NextButton.Content = "Next ›";
            ProgressText.Text = "Local EPUB reader";
            return;
        }
        var pageMode = _settings.ReadingMode == "page";
        PreviousButton.Content = pageMode ? "‹ Previous page" : "‹ Previous chapter";
        NextButton.Content = pageMode ? "Next page ›" : "Next chapter ›";
        PreviousButton.IsEnabled = pageMode ? _documentLoaded : _chapterIndex > 0;
        NextButton.IsEnabled = pageMode ? _documentLoaded : _chapterIndex < _book.Chapters.Count - 1;
        var position = fraction ?? _progress.Get(_book.Id).ScrollFraction;
        var percent = _book.OverallPercent(_chapterIndex, position);
        var chapter = _book.Chapters[_chapterIndex];
        var positionText = _settings.PositionDisplayMode switch
        {
            1 => $"{Math.Round(position * 100d)}% chapter",
            2 => pageMode ? (_pagePositionLabel.Length > 0 ? _pagePositionLabel : "Page mode")
                : $"Vertical scroll · {Math.Round(position * 100d)}%",
            _ => $"{percent}% book"
        };
        ProgressText.Text = $"{_book.Title}  ·  {positionText}";
        StatusText.Text = $"Chapter {_chapterIndex + 1} of {_book.Chapters.Count}: {chapter.Title}  ·  saved on this PC";
    }

    private void SetBusy(bool isBusy, string status)
    {
        OpenButton.IsEnabled = !isBusy;
        LibraryPicker.IsEnabled = !isBusy;
        ReadingModePicker.IsEnabled = !isBusy;
        ReaderSettingsButton.IsEnabled = !isBusy;
        AddFontButton.IsEnabled = !isBusy;
        FontPicker.IsEnabled = !isBusy;
        PreviousButton.IsEnabled = !isBusy && _book is not null
            && (_settings.ReadingMode == "page" ? _documentLoaded : _chapterIndex > 0);
        NextButton.IsEnabled = !isBusy && _book is not null
            && (_settings.ReadingMode == "page" ? _documentLoaded : _chapterIndex < _book.Chapters.Count - 1);
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

    private async Task ApplyReaderSettingsAsync()
    {
        if (_book is null || !_documentLoaded || ReaderBrowser.CoreWebView2 is null)
        {
            return;
        }

        _settings.Normalize();
        ReaderBrowser.ZoomFactor = _settings.FontSizePercent / 100d;
        var configuration = new
        {
            mode = _settings.ReadingMode,
            animation = _settings.PageAnimation,
            autoScroll = _settings.AutoScrollEnabled,
            autoScrollSpeed = AutoScrollSpeedPixels(_settings.AutoScrollSpeed),
            autoScrollWholeBook = _settings.AutoScrollWholeBook,
            nightLight = _settings.NightLight,
            darkTheme = _settings.Theme == "dark",
            brightness = _settings.BrightnessPercent,
            ruler = _settings.ReadingRulerEnabled,
            rulerLines = _settings.ReadingRulerLines
        };
        var settingsJson = JsonSerializer.Serialize(configuration);
        var cssJson = JsonSerializer.Serialize(BuildReaderSettingsCss());
        var script = "(()=>{const c=" + settingsJson + ";const css=" + cssJson + ";"
            + "let s=document.getElementById('wow-reader-settings');if(!s){s=document.createElement('style');s.id='wow-reader-settings';document.head.appendChild(s);}s.textContent=css;"
            + "const b=document.body,d=document.documentElement;if(!b)return;"
            + "const prep=()=>{if(c.mode==='page'){b.scrollLeft=Math.max(0,b.scrollLeft);b.scrollTop=0;}else{b.scrollLeft=0;}};prep();"
            + "window.wowReaderGetProgress=()=>{if(c.mode==='page'){const m=Math.max(0,b.scrollWidth-b.clientWidth);return m===0?0:Math.max(0,Math.min(1,b.scrollLeft/m));}const m=Math.max(0,d.scrollHeight-window.innerHeight);return m===0?0:Math.max(0,Math.min(1,window.scrollY/m));};"
            + "window.wowReaderGetPageLabel=()=>{const w=Math.max(1,b.clientWidth);const total=Math.max(1,Math.ceil(b.scrollWidth/w));const page=Math.max(1,Math.min(total,Math.floor((b.scrollLeft+2)/w)+1));return 'Page '+page+' / '+total;};"
            + "window.wowReaderRestoreProgress=f=>{f=Math.max(0,Math.min(1,Number(f)||0));if(c.mode==='page'){const m=Math.max(0,b.scrollWidth-b.clientWidth);b.scrollTo({left:Math.round(m*f),top:0,behavior:'auto'});}else{const m=Math.max(0,d.scrollHeight-window.innerHeight);window.scrollTo(0,Math.round(m*f));}};"
            + "window.wowReaderTurnPage=direction=>{if(c.mode!=='page')return false;const m=Math.max(0,b.scrollWidth-b.clientWidth);const x=Math.max(0,Math.min(m,b.scrollLeft+(direction<0?-1:1)*Math.max(1,window.innerWidth)));if(Math.abs(x-b.scrollLeft)<2)return false;b.scrollTo({left:x,top:0,behavior:c.animation==='slide'?'smooth':'auto'});return true;};"
            + "window.__wowReaderMode=c.mode;if(!window.__wowReaderKeysBound){window.addEventListener('keydown',e=>{if(window.__wowReaderMode!=='page'||e.altKey||e.ctrlKey||e.metaKey)return;if(e.key==='ArrowRight'&&window.wowReaderTurnPage(1)){e.preventDefault();e.stopPropagation();}else if(e.key==='ArrowLeft'&&window.wowReaderTurnPage(-1)){e.preventDefault();e.stopPropagation();}},true);window.__wowReaderKeysBound=true;}"
            + "const markLanguageBlocks=()=>{const re=/[\\u1000-\\u109f\\ua9e0-\\ua9ff\\uaa60-\\uaa7f]/;document.querySelectorAll('p,li,blockquote,dd,dt,figcaption,td,th').forEach(n=>{const t=(n.innerText||n.textContent||'').trim();if(!t)return;n.classList.toggle('wow-reader-auto-myanmar',re.test(t));n.classList.toggle('wow-reader-auto-latin',!re.test(t));});};markLanguageBlocks();"
            + "const updateOverlay=(id,show,css)=>{let o=document.getElementById(id);if(show){if(!o){o=document.createElement('div');o.id=id;document.body.appendChild(o);}o.style.cssText=css;}else if(o)o.remove();};"
            + "const updateDisplay=()=>{const h=new Date().getHours();const night=c.nightLight==='on'||(c.nightLight==='auto'&&(h>=19||h<6));updateOverlay('wow-reader-night-overlay',night&&!c.darkTheme,'position:fixed;inset:0;z-index:2147483646;pointer-events:none;background:rgba(255,145,40,.095);');const dim=c.brightness<0?0:Math.max(0,1-c.brightness/100);updateOverlay('wow-reader-dim-overlay',dim>0,'position:fixed;inset:0;z-index:2147483647;pointer-events:none;background:rgba(0,0,0,'+dim+');');if(c.ruler){const lh=parseFloat(getComputedStyle(b).lineHeight)||24;const height=Math.max(1,c.rulerLines)*lh;updateOverlay('wow-reader-ruler',true,'position:fixed;left:0;right:0;top:42%;height:'+height+'px;z-index:2147483645;pointer-events:none;box-sizing:border-box;background:rgba(250,220,120,.13);border-top:1px solid rgba(120,100,50,.26);border-bottom:1px solid rgba(120,100,50,.26);');}else updateOverlay('wow-reader-ruler',false,'');};updateDisplay();if(window.__wowDisplayTimer)clearInterval(window.__wowDisplayTimer);window.__wowDisplayTimer=setInterval(updateDisplay,60000);"
            + "if(window.__wowAutoTimer)clearInterval(window.__wowAutoTimer);window.__wowAutoTimer=null;window.__wowPausedUntil=0;"
            + "if(!window.__wowInteractionPauseBound){const pause=()=>{window.__wowPausedUntil=Date.now()+3500;};['wheel','pointerdown','touchstart','keydown'].forEach(k=>window.addEventListener(k,pause,{passive:true}));window.__wowInteractionPauseBound=true;}"
            + "if(c.mode==='scroll'&&c.autoScroll){let last=performance.now();window.__wowAutoTimer=setInterval(()=>{const now=performance.now();const dt=Math.min(100,now-last);last=now;if(Date.now()<window.__wowPausedUntil)return;const max=Math.max(0,d.scrollHeight-window.innerHeight);if(window.scrollY>=max-2){clearInterval(window.__wowAutoTimer);window.__wowAutoTimer=null;if(c.autoScrollWholeBook&&window.chrome?.webview)window.chrome.webview.postMessage('autoscroll-next');return;}window.scrollBy(0,c.autoScrollSpeed*dt/1000);},25);}"
            + "})();";
        try
        {
            await ReaderBrowser.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            StatusText.Text = "Some reader settings could not be applied to this chapter";
        }
    }

    private string BuildReaderSettingsCss()
    {
        var (background, foreground) = _settings.Theme switch
        {
            "sepia" => ("#F4ECD8", "#4A4033"),
            "dark" => ("#121212", "#E8EAED"),
            "eink" => ("#E6E5DE", "#2C3130"),
            "custom" => ("#FAFAFA", "#202124"),
            _ => ("#FFFFFF", "#202124")
        };
        if (_settings.CustomBackgroundColor.Length == 7) background = _settings.CustomBackgroundColor;
        if (_settings.CustomTextColor.Length == 7) foreground = _settings.CustomTextColor;
        var margin = _settings.MarginPercent.ToString(CultureInfo.InvariantCulture);
        var lineHeight = (_settings.LineHeightPercent / 100d).ToString("0.00", CultureInfo.InvariantCulture);
        var css = new StringBuilder()
            .Append("html,body{background-color:").Append(background).Append("!important;color:").Append(foreground).Append("!important;}")
            .Append("body,body *{color:").Append(foreground).Append("!important;}")
            .Append("body a{color:").Append(_settings.Theme == "dark" ? "#9CC7FF" : "#245B9E").Append("!important;}");

        if (_settings.FormattingMode == "reader")
        {
            css.Append("body{padding-left:").Append(margin).Append("vw!important;padding-right:").Append(margin).Append("vw!important;}")
                .Append("body p,body li,body blockquote,body dd,body dt,body figcaption,body td,body th{line-height:")
                .Append(lineHeight).Append("!important;font-weight:").Append(_settings.FontWeight).Append("!important;}")
                .Append("body strong,body b{font-weight:700!important;}");
            var alignment = _settings.TextAlignment switch
            {
                "justify" => "justify",
                "left" => "left",
                "right" => "right",
                _ => ""
            };
            if (alignment.Length > 0)
            {
                css.Append("body p,body li,body blockquote,body dd,body dt,body figcaption,body td,body th{text-align:")
                    .Append(alignment).Append("!important;}");
            }
            if (alignment == "justify" && _settings.AutoSpacing)
            {
                css.Append(".wow-reader-auto-myanmar{text-align:justify!important;text-justify:inter-character!important;line-break:loose!important;hyphens:none!important;word-break:normal!important;}")
                    .Append(".wow-reader-auto-latin{text-align:justify!important;text-justify:inter-word!important;hyphens:auto!important;}");
            }
        }

        if (_settings.ReadingMode == "page")
        {
            css.Append("html{height:100%!important;overflow:hidden!important;}")
                .Append("body{height:100vh!important;max-height:100vh!important;overflow-x:auto!important;overflow-y:hidden!important;")
                .Append("column-count:auto!important;column-width:100vw!important;column-gap:0!important;column-fill:auto!important;")
                .Append("box-sizing:border-box!important;margin-top:0!important;margin-bottom:0!important;padding-top:5vh!important;padding-bottom:5vh!important;}")
                .Append("body img,body video,body table{max-width:90vw;}");
        }
        else
        {
            css.Append("html{height:auto!important;overflow-x:hidden!important;overflow-y:auto!important;}")
                .Append("body{height:auto!important;max-height:none!important;overflow-x:hidden!important;overflow-y:visible!important;")
                .Append("column-count:auto!important;column-width:auto!important;column-gap:normal!important;}");
        }
        return css.ToString();
    }

    private async Task RestoreReaderPositionAsync(double fraction)
    {
        if (ReaderBrowser.CoreWebView2 is null || !_documentLoaded)
        {
            return;
        }
        var safeFraction = Math.Clamp(double.IsFinite(fraction) ? fraction : 0d, 0d, 1d)
            .ToString("0.########", CultureInfo.InvariantCulture);
        try
        {
            await ReaderBrowser.CoreWebView2.ExecuteScriptAsync(
                $"window.wowReaderRestoreProgress?window.wowReaderRestoreProgress({safeFraction}):window.scrollTo(0,Math.round((document.documentElement.scrollHeight-window.innerHeight)*{safeFraction}));");
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            // Best-effort restore; a publisher script may prevent host-side movement.
        }
    }

    private static int AutoScrollSpeedPixels(int level) => level switch
    {
        <= 1 => 2,
        2 => 4,
        3 => 8,
        4 => 16,
        5 => 24,
        6 => 32,
        7 => 48,
        8 => 64,
        9 => 80,
        _ => 96
    };

    private void RefreshSettingsPicker() => ReadingModePicker.SelectedValue = _settings.ReadingMode;

    private void ApplyKeepScreenAwake(bool enabled)
    {
        try
        {
            SetThreadExecutionState(enabled
                ? EsContinuous | EsSystemRequired | EsDisplayRequired
                : EsContinuous);
        }
        catch (DllNotFoundException)
        {
            // This target runs on Windows; retain a graceful fallback for design-time environments.
        }
    }

    private void UpdateEyeReminderTimer()
    {
        _eyeReminderTimer.Stop();
        if (_settings.EyeBreakReminder)
        {
            _eyeReminderTimer.Start();
        }
    }

    private void EyeReminderTimer_Tick(object? sender, EventArgs e)
    {
        if (_book is not null)
        {
            MessageBox.Show(this, "You have been reading for about 30 minutes. Consider taking a short eye break.",
                "WoW Reader eye-break reminder", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void CoreWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.TryGetWebMessageAsString() == "autoscroll-next" && _settings.AutoScrollWholeBook
            && _settings.ReadingMode == "scroll" && _book is not null && _chapterIndex < _book.Chapters.Count - 1)
        {
            await TurnOrNavigateAsync(1);
        }
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
