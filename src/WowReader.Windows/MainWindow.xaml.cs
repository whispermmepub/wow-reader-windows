using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HtmlAgilityPack;
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
    private readonly ReadingActivityStore _activity = new();
    private readonly ReaderBookmarkStore _bookmarks = new();
    private readonly ReaderNoteStore _notes = new();
    private readonly ReadingDayNoteStore _dayNotes = new();
    private readonly ReadingDayMemoryStore _dayMemories = new();
    private readonly VocabularyStore _vocabulary = new();
    private readonly BookCompletionStore _completion = new();
    private readonly LocalFontStore _fonts = new();
    private readonly ReaderSettingsStore _settingsStore = new();
    private readonly ReaderBookSettingsStore _bookSettingsStore = new();
    private readonly PdfBookService _pdfService = new();
    private readonly ExploreFeedService _exploreFeed = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _activityTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _eyeReminderTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly DispatcherTimer _dailyReminderTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private CoreWebView2Environment? _webEnvironment;
    private EpubBook? _book;
    private LibraryBookEntry? _pdfBook;
    private int _chapterIndex;
    private double _pendingScrollFraction;
    private bool _documentLoaded;
    private bool _suppressPicker;
    private bool _suppressFontPicker;
    private bool _isClosing;
    private bool _allowClose;
    private bool _exitDialogOpen;
    private DateTimeOffset _startupStartedAtUtc = DateTimeOffset.UtcNow;
    private string? _mappedBookId;
    private string _pendingSearchTerm = "";
    private string _pagePositionLabel = "";
    private ReaderSettings _settings = new();
    private ReaderSettings _globalSettings = new();
    private bool _suppressSettingsPicker;
    private DateTimeOffset _lastActivitySampleUtc = DateTimeOffset.UtcNow;
    private DateOnly _displayedCalendarMonth = DateOnly.FromDateTime(DateTime.Today);
    private DateOnly _selectedCalendarDate = DateOnly.FromDateTime(DateTime.Today);
    private string _calendarMode = "month";
    private DateOnly? _loadedCalendarNoteDate;
    private bool _suppressCalendarNoteChange;
    private bool _suppressCalendarPeriodChange;
    private bool _suppressCalendarMemoryChange;
    private DateOnly? _loadedCalendarMemoryDate;
    private string? _loadedCalendarMemoryBookId;
    private CancellationTokenSource? _exploreLoadCancellation;

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint executionState);

    public MainWindow()
    {
        InitializeComponent();
        _saveTimer.Tick += SaveTimer_Tick;
        _activityTimer.Tick += ActivityTimer_Tick;
        _eyeReminderTimer.Tick += EyeReminderTimer_Tick;
        _dailyReminderTimer.Tick += DailyReminderTimer_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _startupStartedAtUtc = DateTimeOffset.UtcNow;
        StartupSplash.Opacity = 1d;
        try
        {
            AppPaths.EnsureCreated();
            _webEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.WebViewProfile);
            await ReaderBrowser.EnsureCoreWebView2Async(_webEnvironment);
            if (_isClosing) return;
            ConfigureWebView();

            _library.Load();
            _progress.Load();
            _activity.Load();
            _bookmarks.Load();
            _notes.Load();
            _dayNotes.Load();
            _dayMemories.Load();
            _vocabulary.Load();
            _completion.Load();
            _fonts.Load();
            _settingsStore.Load();
            _globalSettings = _settingsStore.Current;
            _settings = _globalSettings.Clone();
            _bookSettingsStore.Load();
            var today = DateOnly.FromDateTime(DateTime.Today);
            _displayedCalendarMonth = new DateOnly(today.Year, today.Month, 1);
            _selectedCalendarDate = today;
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
            UpdateDailyReminderTimer();
            ShowView("Home");
            RefreshScreenData();
            _lastActivitySampleUtc = DateTimeOffset.UtcNow;
            _saveTimer.Start();
            _activityTimer.Start();

            if (_library.LastOpenedBookId is { } lastId)
            {
                await OpenLibraryBookAsync(lastId, navigateToReader: false);
            }
            if (!_isClosing) await DismissStartupSplashAsync();
        }
        catch (Exception exception)
        {
            if (_isClosing) return;
            if (!_isClosing) await DismissStartupSplashAsync();
            StatusText.Text = "WebView2 could not start";
            MessageBox.Show(this,
                "The reader could not start its embedded browser. Install or update the Microsoft Edge WebView2 Evergreen Runtime, then try again.\n\n" + exception.Message,
                "WoW Reader for Windows", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DismissStartupSplashAsync()
    {
        var remainingMilliseconds = 720d - (DateTimeOffset.UtcNow - _startupStartedAtUtc).TotalMilliseconds;
        if (remainingMilliseconds > 0d)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(remainingMilliseconds));
        }
        if (!IsLoaded || _isClosing || StartupSplash.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(0d, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        StartupSplash.BeginAnimation(OpacityProperty, fade);
        await Task.Delay(280);
        StartupSplash.Visibility = Visibility.Collapsed;
        StartupSplash.BeginAnimation(OpacityProperty, null);
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
            && ((uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    && uri.Host.Equals(ReaderHost, StringComparison.OrdinalIgnoreCase))
                || IsCurrentPdfFile(uri)))
        {
            return;
        }

        e.Response = _webEnvironment?.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Content-Type: text/plain");
    }

    private bool IsCurrentPdfFile(Uri uri)
    {
        if (_pdfBook is null || !uri.IsFile) return false;
        try
        {
            return string.Equals(Path.GetFullPath(uri.LocalPath), Path.GetFullPath(_library.BookPath(_pdfBook.Id)), StringComparison.OrdinalIgnoreCase)
                && File.Exists(uri.LocalPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void CoreNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_pdfBook is not null)
        {
            e.Cancel = !Uri.TryCreate(e.Uri, UriKind.Absolute, out var pdfUri) || !IsCurrentPdfFile(pdfUri);
            return;
        }
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
        if (_pdfBook is not null)
        {
            if (e.IsSuccess)
            {
                ReaderBrowser.Visibility = Visibility.Visible;
                EmptyState.Visibility = Visibility.Collapsed;
                UpdateReaderLabels();
            }
            else
            {
                StatusText.Text = "The local PDF could not be displayed by the installed WebView2 runtime";
            }
            return;
        }
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
        if (_pendingSearchTerm.Length > 0 && ReaderBrowser.CoreWebView2 is not null)
        {
            var term = JsonSerializer.Serialize(_pendingSearchTerm);
            _pendingSearchTerm = "";
            await ReaderBrowser.CoreWebView2.ExecuteScriptAsync($"window.find({term});");
        }
        await ApplyReaderAnnotationsAsync();

        ReaderBrowser.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        UpdateReaderLabels();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a book",
            Filter = "Supported books (*.epub;*.pdf)|*.epub;*.pdf|EPUB books (*.epub)|*.epub|PDF documents (*.pdf)|*.pdf",
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
            if (string.Equals(Path.GetExtension(dialog.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var book = await _pdfService.ImportAsync(dialog.FileName);
                _library.AddPdfOrUpdate(book);
                await OpenPdfAsync(_library.Get(book.Id)!);
            }
            else
            {
                var book = await _epubService.ImportAsync(dialog.FileName);
                _library.AddOrUpdate(book);
                _suppressPicker = true;
                RefreshLibraryPicker(book.Id);
                _suppressPicker = false;
                await SetBookAsync(book, _progress.Get(book.Id));
            }
            RefreshScreenData();
            ShowView("Reader");
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

    private void LibrarySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshScreenData();
        }
    }

    private void LibrarySortPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshScreenData();
        }
    }

    private void LibraryStatusPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshScreenData();
        }
    }

    private async void FinishBookButton_Click(object sender, RoutedEventArgs e)
    {
        var bookId = _book?.Id ?? _pdfBook?.Id;
        if (bookId is null)
        {
            MessageBox.Show(this, "Open a book before changing its reading status.", "Book status", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await SaveCurrentPositionAsync();
        var finished = !_completion.IsFinished(bookId);
        _completion.SetFinished(bookId, finished);
        FinishBookButton.Content = finished ? "Mark as reading" : "Mark finished";
        StatusText.Text = finished ? "Book marked finished on this PC" : "Book marked as reading on this PC";
        RefreshScreenData();
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
        SaveActiveSettings();
        await ApplyReaderSettingsAsync();
        await RestoreReaderPositionAsync(fraction);
        UpdateReaderLabels(fraction);
    }

    private async void ReaderSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfBook is not null)
        {
            MessageBox.Show(this, "Reader typography settings apply to EPUB content. Use the PDF toolbar for PDF zoom and layout.", "PDF settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await SaveCurrentPositionAsync();
        var dialog = new ReaderSettingsWindow(_settings, _book is not null, _book is not null && _bookSettingsStore.HasOverride(_book.Id)) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var fraction = _book is null ? 0d : _progress.Get(_book.Id).ScrollFraction;
        var edited = dialog.Settings;
        edited.Normalize();
        if (dialog.UseBookTypography && _book is not null)
        {
            _bookSettingsStore.Set(_book.Id, ReaderBookSettings.From(edited));
            var global = edited.Clone();
            CopyTypography(_globalSettings, global);
            _globalSettings = global;
            _settingsStore.Save(_globalSettings);
            _settings = _globalSettings.Clone();
            _bookSettingsStore.Get(_book.Id)?.ApplyTo(_settings);
        }
        else
        {
            if (_book is not null && _bookSettingsStore.HasOverride(_book.Id))
            {
                if (TypographyEquals(edited, _settings)) CopyTypography(_globalSettings, edited);
                _bookSettingsStore.Remove(_book.Id);
            }
            _globalSettings = edited.Clone();
            _settings = _globalSettings.Clone();
            _settingsStore.Save(_globalSettings);
        }
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
        UpdateDailyReminderTimer();
        if (_book is not null && _documentLoaded)
        {
            await ApplySelectedFontAsync();
            await ApplyReaderSettingsAsync();
            await RestoreReaderPositionAsync(fraction);
        }
        UpdateReaderLabels(fraction);
    }

    private void SaveActiveSettings()
    {
        _settings.Normalize();
        if (_book is not null && _bookSettingsStore.HasOverride(_book.Id))
        {
            _bookSettingsStore.Set(_book.Id, ReaderBookSettings.From(_settings));
            var global = _globalSettings.Clone();
            global.ReadingMode = _settings.ReadingMode;
            global.Normalize();
            _globalSettings = global;
            _settingsStore.Save(_globalSettings);
            return;
        }
        _globalSettings = _settings.Clone();
        _settingsStore.Save(_globalSettings);
    }

    private static void CopyTypography(ReaderSettings source, ReaderSettings destination)
    {
        destination.FontSizePercent = source.FontSizePercent;
        destination.FontWeight = source.FontWeight;
        destination.LineHeightPercent = source.LineHeightPercent;
        destination.MarginPercent = source.MarginPercent;
        destination.FormattingMode = source.FormattingMode;
        destination.TextAlignment = source.TextAlignment;
        destination.AutoSpacing = source.AutoSpacing;
        destination.Normalize();
    }

    private static bool TypographyEquals(ReaderSettings left, ReaderSettings right) =>
        left.FontSizePercent == right.FontSizePercent
        && left.FontWeight == right.FontWeight
        && left.LineHeightPercent == right.LineHeightPercent
        && left.MarginPercent == right.MarginPercent
        && left.FormattingMode == right.FormattingMode
        && left.TextAlignment == right.TextAlignment
        && left.AutoSpacing == right.AutoSpacing;

    private async Task OpenLibraryBookAsync(string id, bool navigateToReader = true)
    {
        try
        {
            await SaveCurrentPositionAsync();
            SetBusy(true, "Opening from this PC…");
            var entry = _library.Get(id) ?? throw new FileNotFoundException("This book is no longer in the local library.");
            if (entry.Format == "pdf")
            {
                await OpenPdfAsync(entry);
                RefreshScreenData();
                if (navigateToReader) ShowView("Reader");
                return;
            }
            var bookPath = _library.BookPath(id);
            var book = await _epubService.OpenAsync(id, bookPath);
            _library.SetLastOpened(id);
            await SetBookAsync(book, _progress.Get(id));
            RefreshScreenData();
            if (navigateToReader)
            {
                ShowView("Reader");
            }
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

    private async Task OpenPdfAsync(LibraryBookEntry entry)
    {
        if (entry.Format != "pdf") throw new ArgumentException("The selected library item is not a PDF.", nameof(entry));
        var path = _library.BookPath(entry.Id);
        if (!File.Exists(path)) throw new FileNotFoundException("The imported PDF is no longer available in the local library.", path);
        _book = null;
        _pdfBook = entry;
        _chapterIndex = 0;
        _pendingScrollFraction = 0;
        _documentLoaded = false;
        _library.SetLastOpened(entry.Id);
        _suppressPicker = true;
        RefreshLibraryPicker(entry.Id);
        _suppressPicker = false;
        if (_mappedBookId is not null)
        {
            ReaderBrowser.CoreWebView2?.ClearVirtualHostNameToFolderMapping(ReaderHost);
            _mappedBookId = null;
        }
        ReaderBrowser.ZoomFactor = 1d;
        ReaderBrowser.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        UpdateReaderLabels();
        ReaderBrowser.CoreWebView2?.Navigate(new Uri(path).AbsoluteUri);
        StatusText.Text = "PDF opened locally; page position is managed by the WebView2 PDF toolbar";
        await Task.CompletedTask;
    }

    private Task SetBookAsync(EpubBook book, ReadingPosition position)
    {
        _pdfBook = null;
        _book = book;
        _settings = _globalSettings.Clone();
        _bookSettingsStore.Get(book.Id)?.ApplyTo(_settings);
        _suppressSettingsPicker = true;
        RefreshSettingsPicker();
        _suppressSettingsPicker = false;
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
        if (_isClosing || _exitDialogOpen)
        {
            return;
        }
        _exitDialogOpen = true;
        ActivityTimer_Tick(null, EventArgs.Empty);
        var shouldExit = ShowExitConfirmationDialog();
        _exitDialogOpen = false;
        if (!shouldExit) return;
        _isClosing = true;
        _saveTimer.Stop();
        _activityTimer.Stop();
        _eyeReminderTimer.Stop();
        _dailyReminderTimer.Stop();
        SaveCalendarDayNoteIfNeeded();
        SaveCalendarDayMemoryIfNeeded();
        await SaveCurrentPositionAsync();
        ApplyKeepScreenAwake(false);
        _allowClose = true;
        Close();
    }

    private bool ShowExitConfirmationDialog()
    {
        var secondsToday = _activity.GetSeconds(DateOnly.FromDateTime(DateTime.Today));
        var hasSavedTime = secondsToday > 0;
        var dialog = new Window
        {
            Title = "WoW Reader",
            Owner = this,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            MinWidth = 430,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(244, 245, 243)),
            ShowInTaskbar = false
        };
        var card = new StackPanel { Margin = new Thickness(22), Width = 420 };
        var sparkle = new Border
        {
            Width = 54, Height = 54, CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromRgb(239, 238, 253)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = "✦", FontSize = 25, Foreground = new SolidColorBrush(Color.FromRgb(83, 82, 211)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        card.Children.Add(sparkle);
        card.Children.Add(new TextBlock
        {
            Text = hasSavedTime ? "A LITTLE READING WIN" : "A QUICK CHECK-IN",
            FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(83, 82, 211)),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0)
        });
        card.Children.Add(new TextBlock
        {
            Text = hasSavedTime ? "Look at you, reader." : "Before you go",
            FontSize = 23, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(28, 30, 38)),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0)
        });
        card.Children.Add(new TextBlock
        {
            Text = hasSavedTime
                ? "You showed up for your story today. That deserves a little celebration."
                : "No reading time has been saved today yet.",
            FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(102, 106, 120)),
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 8, 8, 17)
        });
        var metric = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(248, 248, 252)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(224, 226, 235)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(14)
        };
        var metricRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        metricRow.Children.Add(new TextBlock { Text = "◷", FontSize = 30, Foreground = new SolidColorBrush(Color.FromRgb(83, 82, 211)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 18, 0) });
        var metricText = new StackPanel();
        metricText.Children.Add(new TextBlock { Text = FormatExitDuration(secondsToday), FontSize = 27, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(28, 30, 38)) });
        metricText.Children.Add(new TextBlock { Text = hasSavedTime ? "Reader foreground time today" : "No saved reading time today", FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(102, 106, 120)) });
        metricRow.Children.Add(metricText);
        metric.Child = metricRow;
        card.Children.Add(metric);
        card.Children.Add(new TextBlock
        {
            Text = "Are you sure you want to exit WoW Reader?",
            FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(28, 30, 38)),
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 18, 4, 13)
        });
        var stay = new Button
        {
            Content = "Stay in WoW Reader", MinHeight = 46, IsDefault = true, IsCancel = true,
            Background = new SolidColorBrush(Color.FromRgb(39, 93, 80)), Foreground = Brushes.White,
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 0)
        };
        stay.Click += (_, _) => dialog.DialogResult = false;
        card.Children.Add(stay);
        var exit = new Button
        {
            Content = "Exit app", MinHeight = 46,
            Background = new SolidColorBrush(Color.FromRgb(248, 248, 252)),
            Foreground = new SolidColorBrush(Color.FromRgb(82, 86, 100)),
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 9, 0, 0)
        };
        exit.Click += (_, _) => dialog.DialogResult = true;
        card.Children.Add(exit);
        dialog.Content = new Border
        {
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(224, 226, 235)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(22), Child = card
        };
        dialog.ShowDialog();
        return dialog.DialogResult == true;
    }

    private static string FormatExitDuration(int seconds)
    {
        if (seconds <= 0) return "—";
        var minutes = seconds / 60;
        if (minutes < 1) return "<1m";
        if (minutes < 60) return $"{minutes}m";
        var hours = minutes / 60;
        var remainder = minutes % 60;
        return remainder == 0 ? $"{hours}h" : $"{hours}h {remainder}m";
    }

    private void RefreshLibraryPicker(string? selectedId)
    {
        LibraryPicker.ItemsSource = _library.Books.ToList();
        LibraryPicker.SelectedValue = selectedId;
        var selectedShelf = LibraryShelfPicker.SelectedItem as string;
        var shelves = new List<string> { "All shelves" };
        shelves.AddRange(_library.GetShelves());
        LibraryShelfPicker.ItemsSource = shelves;
        LibraryShelfPicker.SelectedItem = selectedShelf is not null && shelves.Contains(selectedShelf, StringComparer.CurrentCultureIgnoreCase)
            ? shelves.First(value => string.Equals(value, selectedShelf, StringComparison.CurrentCultureIgnoreCase))
            : "All shelves";
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string view })
        {
            ShowView(view);
        }
    }

    private void ShowView(string view)
    {
        HomeView.Visibility = view == "Home" ? Visibility.Visible : Visibility.Collapsed;
        LibraryView.Visibility = view == "Library" ? Visibility.Visible : Visibility.Collapsed;
        ExploreView.Visibility = view == "Explore" ? Visibility.Visible : Visibility.Collapsed;
        ReaderView.Visibility = view == "Reader" ? Visibility.Visible : Visibility.Collapsed;
        StatisticsView.Visibility = view == "Statistics" ? Visibility.Visible : Visibility.Collapsed;
        CalendarView.Visibility = view == "Calendar" ? Visibility.Visible : Visibility.Collapsed;

        var selectedBackground = new SolidColorBrush(Color.FromRgb(39, 93, 80));
        var selectedForeground = Brushes.White;
        var normalForeground = new SolidColorBrush(Color.FromRgb(52, 69, 60));
        foreach (var (button, name) in new[]
        {
            (HomeNavButton, "Home"), (LibraryNavButton, "Library"), (ExploreNavButton, "Explore"), (ReaderNavButton, "Reader"),
            (StatisticsNavButton, "Statistics"), (CalendarNavButton, "Calendar")
        })
        {
            var isSelected = name == view;
            button.Background = isSelected ? selectedBackground : Brushes.Transparent;
            button.Foreground = isSelected ? selectedForeground : normalForeground;
            button.BorderBrush = isSelected ? selectedBackground : Brushes.Transparent;
        }

        if (view == "Reader" && _book is null && _pdfBook is null)
        {
            ReaderBrowser.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
        }
        if (view == "Explore") _ = LoadExploreFeedAsync();
        RefreshScreenData();
    }

    private async void HomeContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (HomeContinueButton.Tag is string id && id.Length == 64)
        {
            await OpenLibraryBookAsync(id);
        }
        else
        {
            ShowView("Library");
        }
    }

    private async void LibraryBookButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            await OpenLibraryBookAsync(id);
        }
    }

    private static void ValidateCoverImage(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"))
            throw new InvalidDataException("Choose a PNG, JPEG, BMP, or GIF image.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 1 or > 8 * 1024 * 1024)
            throw new InvalidDataException("Cover images must be no larger than 8 MB.");
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0 || decoder.Frames[0].PixelWidth is < 1 or > 12000 || decoder.Frames[0].PixelHeight is < 1 or > 12000
            || (long)decoder.Frames[0].PixelWidth * decoder.Frames[0].PixelHeight > 50_000_000)
            throw new InvalidDataException("The cover image dimensions are not supported.");
    }

    private static string CopyCoverImageForBook(string bookId, string sourcePath)
    {
        ValidateCoverImage(sourcePath);
        AppPaths.EnsureCreated();
        var fileName = bookId + Path.GetExtension(sourcePath).ToLowerInvariant();
        var destination = Path.Combine(AppPaths.Covers, fileName);
        var temporary = destination + ".tmp";
        try
        {
            File.Copy(sourcePath, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
            return fileName;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void LibraryBookDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || _library.Get(id) is not { } entry) return;
        var dialog = new Window
        {
            Title = "Book details · " + entry.Title,
            Owner = this,
            Width = 540,
            Height = 690,
            MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            Background = new SolidColorBrush(Color.FromRgb(244, 245, 243))
        };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Book details and shelves", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(32, 52, 45)) });
        panel.Children.Add(new TextBlock { Text = "Saved only in the local library catalog. Shelf names are separated by commas.", Margin = new Thickness(0, 5, 0, 14), Foreground = new SolidColorBrush(Color.FromRgb(101, 114, 107)), TextWrapping = TextWrapping.Wrap });
        var title = AddEditor("Title", entry.Title);
        var author = AddEditor("Author", entry.Author);
        var series = AddEditor("Series", entry.Series);
        var shelves = AddEditor("Shelves", string.Join(", ", entry.Shelves));
        var description = AddEditor("Description (optional)", entry.Description, multiline: true);
        string? selectedCoverPath = null;
        var clearCover = false;
        var coverStatus = new TextBlock { Text = _library.CoverPath(id) is null ? "No local cover selected" : "A local cover is saved for this book", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 132, 126)), VerticalAlignment = VerticalAlignment.Center };
        var coverActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var chooseCover = new Button { Content = "Choose cover…", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
        var removeCover = new Button { Content = "Remove cover", Padding = new Thickness(10, 6, 10, 6) };
        chooseCover.Click += (_, _) =>
        {
            var coverDialog = new OpenFileDialog { Title = "Choose a book cover you are allowed to use", Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*", CheckFileExists = true };
            if (coverDialog.ShowDialog(dialog) != true) return;
            try
            {
                ValidateCoverImage(coverDialog.FileName);
                selectedCoverPath = coverDialog.FileName;
                clearCover = false;
                coverStatus.Text = Path.GetFileName(selectedCoverPath) + " will be copied locally";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                MessageBox.Show(dialog, exception.Message, "Could not use this cover image", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        removeCover.Click += (_, _) => { selectedCoverPath = null; clearCover = true; coverStatus.Text = "The cover will be removed when you save"; };
        coverActions.Children.Add(chooseCover);
        coverActions.Children.Add(removeCover);
        coverActions.Children.Add(coverStatus);
        panel.Children.Add(new TextBlock { Text = "Cover image", Foreground = new SolidColorBrush(Color.FromRgb(82, 100, 91)) });
        panel.Children.Add(coverActions);
        var rating = new ComboBox { Margin = new Thickness(0, 4, 0, 12), MinHeight = 34, DisplayMemberPath = nameof(RatingOption.Label) };
        rating.ItemsSource = new RatingOption?[] { new("Not rated", null) }
            .Concat(Enumerable.Range(1, 5).Select(value => new RatingOption($"{value} / 5", value))).ToArray();
        rating.SelectedItem = rating.Items.Cast<RatingOption>().FirstOrDefault(option => option.Value == entry.Rating) ?? (RatingOption)rating.Items[0];
        panel.Children.Add(new TextBlock { Text = "Rating", Foreground = new SolidColorBrush(Color.FromRgb(82, 100, 91)) });
        panel.Children.Add(rating);
        panel.Children.Add(new TextBlock { Text = "Format: " + entry.Format.ToUpperInvariant() + " · Local file · Added " + entry.AddedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(120, 132, 126)), Margin = new Thickness(0, 0, 0, 14) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(16, 8, 16, 8) };
        var save = new Button { Content = "Save details", IsDefault = true, Padding = new Thickness(16, 8, 16, 8), Background = new SolidColorBrush(Color.FromRgb(39, 93, 80)), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        save.Click += (_, _) =>
        {
            try
            {
                var ratingValue = ((RatingOption)rating.SelectedItem).Value;
                if (selectedCoverPath is not null)
                    _library.SetCoverFile(id, CopyCoverImageForBook(id, selectedCoverPath));
                else if (clearCover)
                    _library.SetCoverFile(id, null);
                _library.UpdateDetails(id, title.Text, author.Text, series.Text, description.Text, ratingValue,
                    shelves.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                dialog.DialogResult = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                MessageBox.Show(dialog, exception.Message, "Book details", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        panel.Children.Add(actions);
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (dialog.ShowDialog() == true)
        {
            RefreshLibraryPicker(LibraryPicker.SelectedValue as string);
            RefreshScreenData();
            StatusText.Text = "Book details saved locally";
        }

        TextBox AddEditor(string label, string value, bool multiline = false)
        {
            panel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(82, 100, 91)) });
            var editor = new TextBox { Text = value, Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(8, 6, 8, 6), MinHeight = multiline ? 84 : 34, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
            panel.Children.Add(editor);
            return editor;
        }
    }

    private void LibraryShelfPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) RefreshScreenData();
    }

    private async void RefreshExploreButton_Click(object sender, RoutedEventArgs e) => await LoadExploreFeedAsync();

    private async Task LoadExploreFeedAsync()
    {
        _exploreLoadCancellation?.Cancel();
        _exploreLoadCancellation?.Dispose();
        _exploreLoadCancellation = new CancellationTokenSource();
        var cancellationToken = _exploreLoadCancellation.Token;
        ExploreStatusText.Text = "Loading recent public posts…";
        try
        {
            var (posts, isCached) = await _exploreFeed.LoadLatestAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested || _isClosing) return;
            ExplorePostItems.ItemsSource = posts;
            ExploreStatusText.Text = posts.Count == 0
                ? "No public posts are available right now. Try refreshing when you are online."
                : isCached
                    ? $"Showing {posts.Count} locally cached public posts; the sources could not be reached."
                    : $"Showing {posts.Count} recent public posts. Only public feeds were requested; no personal reading data was sent.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidDataException)
        {
            ExploreStatusText.Text = "Could not load Explore right now. Any previously cached posts remain on this PC.";
        }
    }

    private void OpenExplorePostButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string address } || !ExploreFeedService.IsApprovedPostUrl(address)
            || !Uri.TryCreate(address, UriKind.Absolute, out var uri)) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "The public post could not be opened in the default browser.", "Explore", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void ContentsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_book is null)
        {
            MessageBox.Show(this, "Open an EPUB to browse its contents.", "Table of contents", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Window
        {
            Title = $"Contents · {_book.Title}", Owner = this, Width = 520, Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White
        };
        var list = new ListBox { Margin = new Thickness(12), DisplayMemberPath = nameof(TocItem.Title) };
        list.ItemsSource = _book.Chapters.Select((chapter, index) => new TocItem(index, chapter.Title)).ToArray();
        list.SelectedIndex = _chapterIndex;
        list.MouseDoubleClick += async (_, _) =>
        {
            if (list.SelectedItem is TocItem item)
            {
                await SaveCurrentPositionAsync();
                _chapterIndex = item.Index;
                _pendingScrollFraction = 0d;
                _pendingSearchTerm = "";
                NavigateToCurrentChapter();
                dialog.Close();
            }
        };
        dialog.Content = new DockPanel { Children = { new TextBlock { Text = "Double-click a chapter to open it.", Foreground = Brushes.DimGray, Margin = new Thickness(14, 12, 14, 0) }, list } };
        DockPanel.SetDock(dialog.Content is DockPanel content ? content.Children[0] : null, Dock.Top);
        dialog.ShowDialog();
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_book is null)
        {
            MessageBox.Show(this, "Open an EPUB to search its text.", "Search in book", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Window
        {
            Title = $"Search in book · {_book.Title}", Owner = this, Width = 720, Height = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White
        };
        var root = new DockPanel { Margin = new Thickness(14) };
        var searchBox = new TextBox { MinWidth = 440, Padding = new Thickness(9, 7, 9, 7), VerticalContentAlignment = VerticalAlignment.Center };
        var results = new ListBox { Margin = new Thickness(0, 12, 0, 0), DisplayMemberPath = nameof(SearchResultItem.Display) };
        var message = new TextBlock { Text = "Search the text of this EPUB on this PC.", Foreground = Brushes.DimGray, Margin = new Thickness(0, 10, 0, 0) };
        var findButton = new Button { Content = "Find", Margin = new Thickness(8, 0, 0, 0), MinWidth = 76 };
        var searchRow = new DockPanel();
        DockPanel.SetDock(findButton, Dock.Right);
        searchRow.Children.Add(findButton);
        searchRow.Children.Add(searchBox);
        DockPanel.SetDock(searchRow, Dock.Top);
        DockPanel.SetDock(message, Dock.Bottom);
        root.Children.Add(message);
        root.Children.Add(searchRow);
        root.Children.Add(results);

        void RunSearch()
        {
            var term = searchBox.Text.Trim();
            if (term.Length == 0)
            {
                results.ItemsSource = Array.Empty<SearchResultItem>();
                message.Text = "Enter a word or phrase to search.";
                return;
            }
            try
            {
                var matches = FindInBook(term);
                results.ItemsSource = matches;
                message.Text = matches.Count == 0 ? "No matches found in this EPUB." : $"{matches.Count} match(es). Double-click a result to jump to it.";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                message.Text = "The local EPUB text could not be searched.";
            }
        }

        findButton.Click += (_, _) => RunSearch();
        results.MouseDoubleClick += async (_, _) =>
        {
            if (results.SelectedItem is SearchResultItem item && _book is not null)
            {
                await SaveCurrentPositionAsync();
                _chapterIndex = item.ChapterIndex;
                _pendingScrollFraction = item.Fraction;
                _pendingSearchTerm = item.Term;
                NavigateToCurrentChapter();
                dialog.Close();
            }
        };
        dialog.Content = root;
        searchBox.Focus();
        dialog.ShowDialog();
    }

    private List<SearchResultItem> FindInBook(string term)
    {
        var results = new List<SearchResultItem>();
        if (_book is null)
        {
            return results;
        }

        var boundedTerm = term.Length > 160 ? term[..160] : term;
        for (var chapterIndex = 0; chapterIndex < _book.Chapters.Count && results.Count < 350; chapterIndex++)
        {
            var chapter = _book.Chapters[chapterIndex];
            var document = new HtmlDocument();
            document.Load(chapter.LocalPath);
            var text = HtmlEntity.DeEntitize(document.DocumentNode.InnerText)
                .Replace('\u00a0', ' ');
            text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var offset = 0;
            while (offset < text.Length && results.Count < 350)
            {
                var match = text.IndexOf(boundedTerm, offset, StringComparison.CurrentCultureIgnoreCase);
                if (match < 0)
                {
                    break;
                }
                var start = Math.Max(0, match - 55);
                var end = Math.Min(text.Length, match + boundedTerm.Length + 75);
                var snippet = text[start..end].Trim();
                if (start > 0) snippet = "…" + snippet;
                if (end < text.Length) snippet += "…";
                var fraction = text.Length <= 0 ? 0d : match / (double)text.Length;
                results.Add(new SearchResultItem(chapterIndex, fraction, boundedTerm, $"{chapter.Title} — {snippet}"));
                offset = match + Math.Max(1, boundedTerm.Length);
            }
        }
        return results;
    }

    private async void BookmarkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_book is null || _book.Chapters.Count == 0)
        {
            MessageBox.Show(this, "Open an EPUB to bookmark a reading position.", "Bookmark", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await SaveCurrentPositionAsync();
        var position = _progress.Get(_book.Id);
        var chapter = _book.Chapters[Math.Clamp(_chapterIndex, 0, _book.Chapters.Count - 1)];
        var added = _bookmarks.Toggle(new ReaderBookmark(_book.Id, _chapterIndex, position.ScrollFraction, chapter.Title, DateTimeOffset.UtcNow));
        BookmarkButton.Content = added ? "Remove bookmark" : "Bookmark";
        StatusText.Text = added ? "Bookmark saved on this PC" : "Bookmark removed";
    }

    private async void BookmarksButton_Click(object sender, RoutedEventArgs e)
    {
        if (_book is null)
        {
            MessageBox.Show(this, "Open an EPUB to view its bookmarks.", "Bookmarks", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Window
        {
            Title = $"Bookmarks · {_book.Title}", Owner = this, Width = 560, Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White
        };
        var root = new DockPanel { Margin = new Thickness(14) };
        var list = new ListBox { DisplayMemberPath = nameof(BookmarkListItem.Details) };
        var remove = new Button { Content = "Remove selected", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(remove, Dock.Bottom);
        root.Children.Add(remove);
        root.Children.Add(list);

        void RefreshItems()
        {
            list.ItemsSource = _bookmarks.ForBook(_book.Id).Select(bookmark => new BookmarkListItem(bookmark,
                $"{bookmark.ChapterTitle} · {bookmark.ScrollFraction:P0} · {bookmark.CreatedAt.ToLocalTime():g}")).ToArray();
            remove.IsEnabled = false;
        }

        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItem is BookmarkListItem;
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is BookmarkListItem item)
            {
                _bookmarks.Remove(item.Bookmark);
                RefreshItems();
            }
        };
        list.MouseDoubleClick += async (_, _) =>
        {
            if (list.SelectedItem is BookmarkListItem item && _book is not null)
            {
                await SaveCurrentPositionAsync();
                _chapterIndex = Math.Clamp(item.Bookmark.ChapterIndex, 0, _book.Chapters.Count - 1);
                _pendingScrollFraction = item.Bookmark.ScrollFraction;
                _pendingSearchTerm = "";
                NavigateToCurrentChapter();
                dialog.Close();
            }
        };
        RefreshItems();
        dialog.Content = root;
        dialog.ShowDialog();
    }

    private sealed record TocItem(int Index, string Title);
    private sealed record SearchResultItem(int ChapterIndex, double Fraction, string Term, string Display);
    private sealed record BookmarkListItem(ReaderBookmark Bookmark, string Details);

    private async void AddNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_book is null || !_documentLoaded || ReaderBrowser.CoreWebView2 is null)
        {
            MessageBox.Show(this, "Open an EPUB chapter before adding a note or highlight.", "Notes & highlights", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string quote;
        try
        {
            var selectedText = await ReaderBrowser.CoreWebView2.ExecuteScriptAsync("window.getSelection ? window.getSelection().toString() : '';");
            quote = JsonSerializer.Deserialize<string>(selectedText) ?? "";
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or COMException)
        {
            quote = "";
        }
        quote = quote.Trim();
        if (quote.Length > 1200) quote = quote[..1200];

        var dialog = new Window
        {
            Title = "Add a reading note or highlight", Owner = this, Width = 560, Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White
        };
        var form = new StackPanel { Margin = new Thickness(18) };
        form.Children.Add(new TextBlock { Text = "Selected text (optional)", FontWeight = FontWeights.SemiBold });
        form.Children.Add(new TextBox { Text = quote, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            MinHeight = 72, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 14) });
        form.Children.Add(new TextBlock { Text = "Your note", FontWeight = FontWeights.SemiBold });
        var noteInput = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 14) };
        form.Children.Add(noteInput);
        var highlight = new CheckBox { Content = "Highlight selected text", IsChecked = quote.Length > 0, Margin = new Thickness(0, 0, 0, 8) };
        form.Children.Add(highlight);
        form.Children.Add(new TextBlock { Text = "Highlight color", FontWeight = FontWeights.SemiBold });
        var colors = new ComboBox { SelectedValuePath = "Tag", SelectedIndex = 0, Margin = new Thickness(0, 6, 0, 14), Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        colors.Items.Add(new ComboBoxItem { Content = "Yellow", Tag = "yellow" });
        colors.Items.Add(new ComboBoxItem { Content = "Teal", Tag = "teal" });
        colors.Items.Add(new ComboBoxItem { Content = "Pink", Tag = "pink" });
        colors.Items.Add(new ComboBoxItem { Content = "Purple", Tag = "purple" });
        form.Children.Add(colors);
        var save = new Button { Content = "Save locally", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 120 };
        save.Click += async (_, _) =>
        {
            if (quote.Length == 0 && string.IsNullOrWhiteSpace(noteInput.Text))
            {
                MessageBox.Show(dialog, "Select text or enter a note before saving.", "Notes & highlights", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                await SaveCurrentPositionAsync();
                var position = _progress.Get(_book.Id);
                var chapter = _book.Chapters[Math.Clamp(_chapterIndex, 0, _book.Chapters.Count - 1)];
                var color = highlight.IsChecked == true ? colors.SelectedValue as string ?? "yellow" : "none";
                var saved = _notes.Add(_book.Id, _chapterIndex, position.ScrollFraction, chapter.Title, quote, noteInput.Text, color);
                if (saved.HighlightColor != "none") await ApplyReaderAnnotationsAsync();
                StatusText.Text = "Note saved on this PC";
                dialog.Close();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBox.Show(dialog, exception.Message, "Could not save note", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        form.Children.Add(save);
        dialog.Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();
    }

    private void NotesHubButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Notes & highlights", Owner = this, Width = 700, Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White
        };
        var root = new DockPanel { Margin = new Thickness(14) };
        var list = new ListBox { DisplayMemberPath = nameof(NoteListItem.Display) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var remove = new Button { Content = "Delete selected", IsEnabled = false };
        buttons.Children.Add(remove);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(list);

        void RefreshItems()
        {
            list.ItemsSource = _notes.All().Select(note =>
            {
                var bookTitle = _library.Books.FirstOrDefault(book => book.Id == note.BookId)?.Title ?? "Book not in local library";
                var excerpt = note.Quote.Length > 0 ? note.Quote : note.Note;
                if (excerpt.Length > 110) excerpt = excerpt[..110] + "…";
                var kind = note.HighlightColor == "none" ? "Note" : "Highlight / note";
                return new NoteListItem(note, $"{bookTitle} · {note.ChapterTitle} · {kind} · {excerpt}");
            }).ToArray();
            remove.IsEnabled = false;
        }

        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItem is NoteListItem;
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is NoteListItem item)
            {
                _notes.Remove(item.Note.Id);
                RefreshItems();
            }
        };
        list.MouseDoubleClick += async (_, _) =>
        {
            if (list.SelectedItem is NoteListItem item)
            {
                dialog.Close();
                await NavigateToNoteAsync(item.Note);
            }
        };
        RefreshItems();
        dialog.Content = root;
        dialog.ShowDialog();
    }

    private async void VocabularyButton_Click(object sender, RoutedEventArgs e)
    {
        string selectedText = "";
        if (_book is not null && ReaderBrowser.CoreWebView2 is not null)
        {
            try
            {
                var encodedSelection = await ReaderBrowser.CoreWebView2.ExecuteScriptAsync("window.getSelection ? window.getSelection().toString() : '';");
                selectedText = JsonSerializer.Deserialize<string>(encodedSelection) ?? "";
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or COMException)
            {
                selectedText = "";
            }
        }
        var captured = _vocabulary.Capture(selectedText, _book?.Title);
        var preferred = captured.FirstOrDefault()?.Word;
        var window = new VocabularyWindow(_vocabulary, preferred) { Owner = this };
        window.ShowDialog();
    }

    private async Task NavigateToNoteAsync(ReaderNote note)
    {
        if (!_library.Books.Any(book => book.Id == note.BookId))
        {
            MessageBox.Show(this, "The book for this note is no longer in the local library.", "Notes & highlights", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_book?.Id != note.BookId)
        {
            await OpenLibraryBookAsync(note.BookId);
        }
        else
        {
            await SaveCurrentPositionAsync();
        }
        if (_book is null || _book.Chapters.Count == 0) return;
        _chapterIndex = Math.Clamp(note.ChapterIndex, 0, _book.Chapters.Count - 1);
        _pendingScrollFraction = note.ScrollFraction;
        _pendingSearchTerm = note.Quote;
        ShowView("Reader");
        NavigateToCurrentChapter();
    }

    private async Task ApplyReaderAnnotationsAsync()
    {
        if (_book is null || !_documentLoaded || ReaderBrowser.CoreWebView2 is null) return;
        var annotations = _notes.ForBook(_book.Id)
            .Where(note => note.ChapterIndex == _chapterIndex && note.Quote.Length > 0 && note.HighlightColor != "none")
            .Take(100)
            .ToArray();
        var clearMarks = "document.querySelectorAll('mark[data-wow-reader-note]').forEach(m=>{const f=document.createDocumentFragment();while(m.firstChild)f.appendChild(m.firstChild);m.replaceWith(f);});";
        await ReaderBrowser.CoreWebView2.ExecuteScriptAsync(clearMarks);
        foreach (var annotation in annotations)
        {
            var quote = JsonSerializer.Serialize(annotation.Quote);
            var color = JsonSerializer.Serialize(annotation.HighlightColor switch
            {
                "teal" => "#A9DCC8",
                "pink" => "#F4B8C4",
                "purple" => "#D5C2EF",
                _ => "#FFE082"
            });
            var script = $"(()=>{{const t={quote},c={color};if(!t||!window.find)return;const s=window.getSelection();if(window.find(t,false,false,true,false,true,false)&&s&&s.rangeCount){{const r=s.getRangeAt(0);const m=document.createElement('mark');m.dataset.wowReaderNote='1';m.style.cssText='background:'+c+';color:inherit;border-radius:2px;padding:0 1px;';m.appendChild(r.extractContents());r.insertNode(m);s.removeAllRanges();}}}})();";
            await ReaderBrowser.CoreWebView2.ExecuteScriptAsync(script);
        }
    }

    private sealed record NoteListItem(ReaderNote Note, string Display);

    private void ReadingRecapButton_Click(object sender, RoutedEventArgs e)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthDays = _activity.GetDays(monthStart, DateTime.DaysInMonth(today.Year, today.Month));
        var totalSeconds = monthDays.Sum(day => day.Seconds);
        var activeDays = monthDays.Count(day => day.Seconds > 0);
        var finishedBooks = _library.Books.Count(book => _completion.IsFinished(book.Id));

        var card = new Border
        {
            Width = 900,
            Height = 510,
            Padding = new Thickness(34),
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromRgb(39, 93, 80)),
            Child = new StackPanel()
        };
        var content = (StackPanel)card.Child;
        content.Children.Add(new TextBlock { Text = "WHISPER OF WORDS · READING RECAP", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(200, 224, 214)) });
        content.Children.Add(new TextBlock { Text = DateTime.Today.ToString("MMMM yyyy", CultureInfo.CurrentCulture), FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 24) });
        var metrics = new UniformGrid { Columns = 3, Rows = 2 };
        AddMetric("READING TIME", FormatDuration(totalSeconds));
        AddMetric("ACTIVE DAYS", activeDays.ToString(CultureInfo.CurrentCulture));
        AddMetric("READING SESSIONS", monthDays.Sum(day => day.Sessions).ToString(CultureInfo.CurrentCulture));
        AddMetric("FINISHED BOOKS", finishedBooks.ToString(CultureInfo.CurrentCulture));
        AddMetric("CURRENT STREAK", _activity.CurrentStreak(today) + " days");
        AddMetric("LONGEST STREAK", _activity.LongestStreak + " days");
        content.Children.Add(metrics);
        content.Children.Add(new TextBlock { Text = "A small reading win, saved from your local Windows activity. Reading time is an estimate; book titles and private notes are not included.", FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(227, 238, 233)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0) });

        var dialog = new Window { Title = "Reading recap", Owner = this, Width = 960, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(244, 245, 243)), ResizeMode = ResizeMode.NoResize };
        var layout = new DockPanel { Margin = new Thickness(18) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var close = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Save PNG…", IsDefault = true, Padding = new Thickness(14, 8, 14, 8), Background = new SolidColorBrush(Color.FromRgb(39, 93, 80)), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
        close.Click += (_, _) => dialog.DialogResult = false;
        save.Click += (_, _) =>
        {
            var saveDialog = new SaveFileDialog { Title = "Save reading recap", Filter = "PNG image (*.png)|*.png", FileName = $"wow-reading-recap-{today:yyyy-MM}.png", DefaultExt = ".png", AddExtension = true };
            if (saveDialog.ShowDialog(dialog) != true) return;
            try
            {
                card.Measure(new Size(card.Width, card.Height));
                card.Arrange(new Rect(0, 0, card.Width, card.Height));
                card.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)card.Width, (int)card.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(card);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(saveDialog.FileName);
                encoder.Save(stream);
                StatusText.Text = "Reading recap saved as a PNG on this PC";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                MessageBox.Show(dialog, exception.Message, "Could not save recap", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        actions.Children.Add(close);
        actions.Children.Add(save);
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        layout.Children.Add(card);
        dialog.Content = layout;
        dialog.ShowDialog();

        void AddMetric(string label, string value)
        {
            var box = new Border { Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(16), CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromRgb(54, 111, 96)), BorderBrush = new SolidColorBrush(Color.FromRgb(101, 149, 132)), BorderThickness = new Thickness(1) };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(200, 224, 214)) });
            stack.Children.Add(new TextBlock { Text = value, FontSize = 25, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) });
            box.Child = stack;
            metrics.Children.Add(box);
        }
    }

    private void PreviousMonthButton_Click(object sender, RoutedEventArgs e) => MoveCalendarPeriod(-1);

    private void NextMonthButton_Click(object sender, RoutedEventArgs e) => MoveCalendarPeriod(1);

    private void MoveCalendarPeriod(int direction)
    {
        SaveCalendarDayNoteIfNeeded();
        SaveCalendarDayMemoryIfNeeded();
        if (_calendarMode == "week")
        {
            _selectedCalendarDate = _selectedCalendarDate.AddDays(direction * 7);
            _displayedCalendarMonth = new DateOnly(_selectedCalendarDate.Year, _selectedCalendarDate.Month, 1);
        }
        else if (_calendarMode == "year")
        {
            _displayedCalendarMonth = _displayedCalendarMonth.AddYears(direction);
            _selectedCalendarDate = new DateOnly(_displayedCalendarMonth.Year, _selectedCalendarDate.Month, 1);
        }
        else
        {
            _displayedCalendarMonth = _displayedCalendarMonth.AddMonths(direction);
            var day = Math.Min(_selectedCalendarDate.Day, DateTime.DaysInMonth(_displayedCalendarMonth.Year, _displayedCalendarMonth.Month));
            _selectedCalendarDate = new DateOnly(_displayedCalendarMonth.Year, _displayedCalendarMonth.Month, day);
        }
        RefreshScreenData();
    }

    private void CalendarPeriodPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressCalendarPeriodChange) return;
        SaveCalendarDayNoteIfNeeded();
        SaveCalendarDayMemoryIfNeeded();
        _calendarMode = (CalendarPeriodPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "month";
        _displayedCalendarMonth = new DateOnly(_selectedCalendarDate.Year, _selectedCalendarDate.Month, 1);
        RefreshScreenData();
    }

    private void CalendarYearMonth_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DateTime date }) return;
        SaveCalendarDayNoteIfNeeded();
        SaveCalendarDayMemoryIfNeeded();
        _selectedCalendarDate = new DateOnly(date.Year, date.Month, 1);
        _displayedCalendarMonth = _selectedCalendarDate;
        _calendarMode = "month";
        _suppressCalendarPeriodChange = true;
        CalendarPeriodPicker.SelectedIndex = 0;
        _suppressCalendarPeriodChange = false;
        RefreshScreenData();
    }

    private void CalendarDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DateTime date })
        {
            SaveCalendarDayNoteIfNeeded();
            SaveCalendarDayMemoryIfNeeded();
            _selectedCalendarDate = DateOnly.FromDateTime(date);
            _displayedCalendarMonth = new DateOnly(_selectedCalendarDate.Year, _selectedCalendarDate.Month, 1);
            RefreshScreenData();
        }
    }

    private void SaveCalendarDayNoteButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCalendarDayNoteIfNeeded();
        StatusText.Text = "Reading day note saved on this PC";
    }

    private void SaveCalendarDayNoteIfNeeded()
    {
        if (_suppressCalendarNoteChange || _loadedCalendarNoteDate is not { } date)
        {
            return;
        }
        try
        {
            _dayNotes.Set(date, CalendarDayNoteBox.Text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Reading day note could not be saved locally";
        }
    }

    private void CalendarMemoryBookPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCalendarMemoryChange) return;
        SaveCalendarDayMemoryIfNeeded();
        LoadCalendarDayMemory(CalendarMemoryBookPicker.SelectedValue as string);
    }

    private void SaveCalendarDayMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCalendarDayMemoryIfNeeded();
        StatusText.Text = "Per-book reading memory saved on this PC";
    }

    private void SaveCalendarDayMemoryIfNeeded()
    {
        if (_suppressCalendarMemoryChange || _loadedCalendarMemoryDate is not { } date || string.IsNullOrWhiteSpace(_loadedCalendarMemoryBookId)) return;
        try
        {
            _dayMemories.Set(date, _loadedCalendarMemoryBookId, CalendarMemoryTextBox.Text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = "Per-book reading memory could not be saved locally";
        }
    }

    private void LoadCalendarDayMemory(string? bookId)
    {
        _suppressCalendarMemoryChange = true;
        CalendarMemoryTextBox.Text = bookId is null ? "" : _dayMemories.Get(_selectedCalendarDate, bookId);
        CalendarMemoryTextBox.IsEnabled = bookId is not null;
        CalendarMemoryBookPicker.IsEnabled = CalendarMemoryBookPicker.Items.Count > 0;
        CalendarMemorySaveButton.IsEnabled = bookId is not null;
        _loadedCalendarMemoryDate = _selectedCalendarDate;
        _loadedCalendarMemoryBookId = bookId;
        _suppressCalendarMemoryChange = false;
    }

    private void ActivityTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _lastActivitySampleUtc;
        _lastActivitySampleUtc = now;
        var activeBookId = _book?.Id ?? _pdfBook?.Id;
        if (activeBookId is null || !_documentLoaded || !IsActive || elapsed <= TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(90))
        {
            return;
        }

        try
        {
            _activity.Record(now, elapsed, activeBookId);
            RefreshScreenData();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = "Reading activity could not be saved locally";
        }
    }

    private void RefreshScreenData()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        HomeLibraryCountText.Text = $"{_library.Books.Count} {( _library.Books.Count == 1 ? "book" : "books")}";
        HomeReadTodayText.Text = FormatDuration(_activity.GetSeconds(today));
        HomeActiveDaysText.Text = $"{_activity.ActiveDays} {(_activity.ActiveDays == 1 ? "day" : "days")}";

        var lastBook = _library.Books.FirstOrDefault(entry => entry.Id == _library.LastOpenedBookId);
        if (lastBook is null)
        {
            HomeLastTitleText.Text = "Your next chapter starts here";
            HomeLastMetaText.Text = _library.Books.Count == 0
                ? "Import an EPUB to begin your local library."
                : "Choose a book from your library to continue.";
            HomeContinueButton.Content = "Browse library";
            HomeContinueButton.Tag = null;
            HomeContinueButton.IsEnabled = true;
        }
        else
        {
            HomeLastTitleText.Text = lastBook.Title;
            HomeContinueButton.Content = "Continue reading";
            HomeContinueButton.Tag = lastBook.Id;
            HomeContinueButton.IsEnabled = true;
            if (_progress.Has(lastBook.Id))
            {
                var position = _progress.Get(lastBook.Id);
                if (_book?.Id == lastBook.Id)
                {
                    HomeLastMetaText.Text = $"{_book.OverallPercent(_chapterIndex, position.ScrollFraction)}% through the book · Last read {position.UpdatedAt.ToLocalTime():MMM d}";
                }
                else
                {
                    HomeLastMetaText.Text = $"Chapter {position.ChapterIndex + 1} · {position.ScrollFraction:P0} through chapter · Last read {position.UpdatedAt.ToLocalTime():MMM d}";
                }
            }
            else
            {
                HomeLastMetaText.Text = "Ready to start · Stored on this PC";
            }
        }

        IEnumerable<LibraryBookEntry> libraryEntries = _library.Books;
        var searchText = LibrarySearchBox.Text.Trim();
        if (searchText.Length > 0)
        {
            libraryEntries = libraryEntries.Where(entry => entry.Title.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                || entry.Author.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                || entry.Series.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                || entry.Shelves.Any(shelf => shelf.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)));
        }
        var shelfFilter = LibraryShelfPicker.SelectedItem as string;
        if (!string.IsNullOrWhiteSpace(shelfFilter) && shelfFilter != "All shelves")
            libraryEntries = libraryEntries.Where(entry => entry.Shelves.Contains(shelfFilter, StringComparer.CurrentCultureIgnoreCase));
        var statusFilter = LibraryStatusPicker.SelectedValue as string ?? "all";
        libraryEntries = libraryEntries.Where(entry => statusFilter switch
        {
            "reading" => (_progress.Has(entry.Id) || _activity.GetBookSeconds(entry.Id) > 0) && !_completion.IsFinished(entry.Id),
            "unread" => !_progress.Has(entry.Id) && _activity.GetBookSeconds(entry.Id) == 0 && !_completion.IsFinished(entry.Id),
            "finished" => _completion.IsFinished(entry.Id),
            _ => true
        });
        libraryEntries = (LibrarySortPicker.SelectedValue as string) switch
        {
            "title" => libraryEntries.OrderBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase),
            "title-desc" => libraryEntries.OrderByDescending(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase),
            "author" => libraryEntries.OrderBy(entry => entry.Author, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase),
            "rating" => libraryEntries.OrderByDescending(entry => entry.Rating ?? 0).ThenBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => libraryEntries
        };
        var visibleEntries = libraryEntries.ToArray();
        LibraryBookItems.ItemsSource = visibleEntries.Select(entry =>
        {
            var secondsRead = _activity.GetBookSeconds(entry.Id);
            var state = _completion.IsFinished(entry.Id) ? "Finished" : _progress.Has(entry.Id) || secondsRead > 0 ? "Reading" : "Unread";
            var metadata = string.Join(" · ", new[]
            {
                entry.Format.ToUpperInvariant(),
                entry.Author,
                entry.Series,
                entry.Rating is { } stars ? new string('★', stars) + new string('☆', 5 - stars) : "",
                entry.Shelves.Count > 0 ? "Shelves: " + string.Join(", ", entry.Shelves) : ""
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!_progress.Has(entry.Id))
            {
                var status = secondsRead > 0 ? $"{state} · {FormatDuration(secondsRead)} tracked" : $"{state} · Not started yet";
                return new LibraryBookItem(entry.Id, entry.Title, JoinDetails(metadata, status, "Stored on this PC"), _library.CoverPath(entry.Id));
            }

            var position = _progress.Get(entry.Id);
            var detail = _book?.Id == entry.Id
                ? $"{_book.OverallPercent(_chapterIndex, position.ScrollFraction)}% through the book · Last read {position.UpdatedAt.ToLocalTime():MMM d, yyyy}"
                : $"Chapter {position.ChapterIndex + 1} · {position.ScrollFraction:P0} through chapter · Last read {position.UpdatedAt.ToLocalTime():MMM d, yyyy}";
            return new LibraryBookItem(entry.Id, entry.Title, JoinDetails(metadata, state, detail), _library.CoverPath(entry.Id));
        }).ToArray();
        LibraryResultCountText.Text = $"{visibleEntries.Length} of {_library.Books.Count} books";
        LibraryEmptyText.Text = _library.Books.Count == 0
            ? "Your library is ready for a book. Import an EPUB or PDF from your PC to add it here."
            : "No books match this search. Try another title or clear the search field.";
        LibraryEmptyState.Visibility = visibleEntries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var week = _activity.GetDays(weekStart, 7);
        var maxMinutes = Math.Max(1, week.Max(day => day.Minutes));
        StatsWeekBars.ItemsSource = week.Select(day => new WeeklyBarItem(
            day.Date.ToString("ddd", CultureInfo.CurrentCulture),
            day.Minutes == 0 ? "0 min" : $"{day.Minutes} min",
            day.Minutes * 100d / maxMinutes)).ToArray();
        StatsTotalText.Text = FormatDuration(_activity.TotalSeconds);
        StatsTodayText.Text = FormatDuration(_activity.GetSeconds(today));
        StatsActiveDaysText.Text = _activity.ActiveDays.ToString(CultureInfo.CurrentCulture);
        var streak = _activity.CurrentStreak(today);
        StatsStreakText.Text = $"{streak} {(streak == 1 ? "day" : "days")}";
        var longestStreak = _activity.LongestStreak;
        StatsLongestStreakText.Text = $"{longestStreak} {(longestStreak == 1 ? "day" : "days")}";
        StatsFinishedBooksText.Text = _library.Books.Count(book => _completion.IsFinished(book.Id)).ToString(CultureInfo.CurrentCulture);
        StatsWeekTotalText.Text = $"{FormatDuration(week.Sum(day => day.Seconds))} read this week";
        StatsSessionCountText.Text = $"{_activity.TotalSessions} local reading {(_activity.TotalSessions == 1 ? "session" : "sessions")} recorded. A session resumes after a 15-minute pause.";
        StatsTopBooks.ItemsSource = _activity.BooksByTime().Select(item => new TopBookItem(
            _library.Books.FirstOrDefault(book => book.Id == item.BookId)?.Title ?? "Book no longer in library",
            FormatDuration(item.Seconds))).ToArray();

        if (_calendarMode == "year")
        {
            CalendarMonthText.Text = _displayedCalendarMonth.Year.ToString(CultureInfo.CurrentCulture);
            CalendarMonthWeekBorder.Visibility = Visibility.Collapsed;
            CalendarYearMonths.Visibility = Visibility.Visible;
            CalendarYearMonths.ItemsSource = Enumerable.Range(1, 12).Select(month =>
            {
                var monthDate = new DateOnly(_displayedCalendarMonth.Year, month, 1);
                var days = _activity.GetDays(monthDate, DateTime.DaysInMonth(monthDate.Year, monthDate.Month));
                var totalSeconds = days.Sum(day => day.Seconds);
                var activeDays = days.Count(day => day.Seconds > 0);
                return new CalendarYearMonthItem(monthDate.ToDateTime(TimeOnly.MinValue), monthDate.ToString("MMMM", CultureInfo.CurrentCulture),
                    $"{activeDays} active days · {FormatDuration(totalSeconds)}");
            }).ToArray();
        }
        else
        {
            CalendarMonthWeekBorder.Visibility = Visibility.Visible;
            CalendarYearMonths.Visibility = Visibility.Collapsed;
            DateOnly periodStart;
            int count;
            int rows;
            if (_calendarMode == "week")
            {
                periodStart = _selectedCalendarDate.AddDays(-(((int)_selectedCalendarDate.DayOfWeek + 6) % 7));
                count = 7;
                rows = 1;
                var periodEnd = periodStart.AddDays(6);
                CalendarMonthText.Text = $"{periodStart:MMM d} – {periodEnd:MMM d, yyyy}";
            }
            else
            {
                var monthStart = new DateOnly(_displayedCalendarMonth.Year, _displayedCalendarMonth.Month, 1);
                periodStart = monthStart.AddDays(-(((int)monthStart.DayOfWeek + 6) % 7));
                count = 42;
                rows = 6;
                CalendarMonthText.Text = _displayedCalendarMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
            }
            CalendarDays.ItemsPanel = CreateCalendarItemsPanel(rows);
            CalendarDays.ItemsSource = _activity.GetDays(periodStart, count).Select(day =>
            {
                var inPeriod = _calendarMode == "week" || day.Date.Month == _displayedCalendarMonth.Month;
                var selected = day.Date == _selectedCalendarDate;
                var hasActivity = day.Seconds > 0;
                var hasNote = _dayNotes.HasNote(day.Date);
                var background = selected ? new SolidColorBrush(Color.FromRgb(39, 93, 80))
                    : hasActivity ? new SolidColorBrush(Color.FromRgb(222, 238, 229)) : Brushes.White;
                var foreground = selected ? Brushes.White
                    : inPeriod ? new SolidColorBrush(Color.FromRgb(52, 69, 60)) : new SolidColorBrush(Color.FromRgb(175, 184, 179));
                var border = selected ? new SolidColorBrush(Color.FromRgb(39, 93, 80)) : new SolidColorBrush(Color.FromRgb(230, 234, 231));
                var tooltip = hasActivity ? $"{day.Date:MMMM d}: {FormatDuration(day.Seconds)} reading" : $"{day.Date:MMMM d}: no reading recorded";
                if (hasNote) tooltip += " · daily note saved";
                return new CalendarDayItem(day.Date.ToDateTime(TimeOnly.MinValue), day.Date.Day.ToString(CultureInfo.CurrentCulture),
                    hasActivity ? $"{day.Minutes}m" : hasNote ? "Note" : "", inPeriod, background, foreground, border, tooltip);
            }).ToArray();
        }

        var selectedDay = _activity.GetDays(_selectedCalendarDate, 1).First();
        CalendarSelectedDateText.Text = _selectedCalendarDate.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);
        CalendarSelectedActivityText.Text = selectedDay.Seconds > 0
            ? $"{FormatDuration(selectedDay.Seconds)} · {selectedDay.Sessions} {(selectedDay.Sessions == 1 ? "session" : "sessions")}"
            : "No reading recorded";
        var titles = selectedDay.BookIds.Select(id =>
        {
            var title = _library.Books.FirstOrDefault(book => book.Id == id)?.Title ?? "Book no longer in library";
            return selectedDay.BookSeconds.TryGetValue(id, out var seconds) ? $"{title} · {FormatDuration(seconds)}" : title;
        });
        CalendarBookNamesText.Text = string.Join(" · ", titles);
        if (_loadedCalendarNoteDate != _selectedCalendarDate)
        {
            _suppressCalendarNoteChange = true;
            CalendarDayNoteBox.Text = _dayNotes.Get(_selectedCalendarDate);
            _loadedCalendarNoteDate = _selectedCalendarDate;
            _suppressCalendarNoteChange = false;
        }
        var preferredMemoryBook = selectedDay.BookIds.Contains(_loadedCalendarMemoryBookId ?? "", StringComparer.Ordinal)
            ? _loadedCalendarMemoryBookId
            : selectedDay.BookIds.FirstOrDefault();
        _suppressCalendarMemoryChange = true;
        CalendarMemoryBookPicker.ItemsSource = selectedDay.BookIds.Select(id => new CalendarMemoryBookItem(id,
            _library.Books.FirstOrDefault(book => book.Id == id)?.Title ?? "Book no longer in library")).ToArray();
        CalendarMemoryBookPicker.SelectedValue = preferredMemoryBook;
        _suppressCalendarMemoryChange = false;
        if (_loadedCalendarMemoryDate != _selectedCalendarDate || _loadedCalendarMemoryBookId != preferredMemoryBook)
            LoadCalendarDayMemory(preferredMemoryBook);
    }

    private static ItemsPanelTemplate CreateCalendarItemsPanel(int rows)
    {
        var factory = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.UniformGrid));
        factory.SetValue(System.Windows.Controls.Primitives.UniformGrid.ColumnsProperty, 7);
        factory.SetValue(System.Windows.Controls.Primitives.UniformGrid.RowsProperty, rows);
        return new ItemsPanelTemplate(factory);
    }

    private static string JoinDetails(params string[] values) => string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string FormatDuration(int seconds)
    {
        var minutes = (int)Math.Round(Math.Max(0, seconds) / 60d, MidpointRounding.AwayFromZero);
        if (minutes < 60)
        {
            return $"{minutes} min";
        }

        var hours = minutes / 60;
        var remainder = minutes % 60;
        return remainder == 0 ? $"{hours} hr" : $"{hours} hr {remainder} min";
    }

    private sealed record LibraryBookItem(string Id, string Title, string Details, string? CoverPath = null);
    private sealed record RatingOption(string Label, int? Value);
    private sealed record WeeklyBarItem(string DayLabel, string MinutesText, double Percent);
    private sealed record TopBookItem(string Title, string TimeText);
    private sealed record CalendarYearMonthItem(DateTime Date, string Title, string Summary);
    private sealed record CalendarMemoryBookItem(string Id, string Title);
    private sealed record CalendarDayItem(DateTime Date, string DayNumber, string MinutesText, bool IsCurrentMonth,
        Brush Background, Brush Foreground, Brush BorderBrush, string ToolTipText);

    private void UpdateReaderLabels(double? fraction = null)
    {
        if (_pdfBook is not null)
        {
            PreviousButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            BookmarkButton.Content = "Bookmark";
            BookmarkButton.IsEnabled = false;
            VocabularyButton.IsEnabled = false;
            ReaderSettingsButton.IsEnabled = true;
            AddFontButton.IsEnabled = false;
            FontPicker.IsEnabled = false;
            FinishBookButton.IsEnabled = true;
            FinishBookButton.Content = _completion.IsFinished(_pdfBook.Id) ? "Mark as reading" : "Mark finished";
            ProgressText.Text = _pdfBook.Title + " · PDF · use the built-in PDF toolbar";
            StatusText.Text = "PDF reading time is tracked locally; page position and annotations are not stored by WoW Reader";
            return;
        }
        if (_book is null || _book.Chapters.Count == 0)
        {
            PreviousButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            PreviousButton.Content = "‹ Previous";
            NextButton.Content = "Next ›";
            BookmarkButton.Content = "Bookmark";
            BookmarkButton.IsEnabled = false;
            VocabularyButton.IsEnabled = false;
            FinishBookButton.Content = "Mark finished";
            FinishBookButton.IsEnabled = false;
            ProgressText.Text = "Local EPUB reader";
            return;
        }
        var pageMode = _settings.ReadingMode == "page";
        VocabularyButton.IsEnabled = true;
        PreviousButton.Content = pageMode ? "‹ Previous page" : "‹ Previous chapter";
        NextButton.Content = pageMode ? "Next page ›" : "Next chapter ›";
        PreviousButton.IsEnabled = pageMode ? _documentLoaded : _chapterIndex > 0;
        NextButton.IsEnabled = pageMode ? _documentLoaded : _chapterIndex < _book.Chapters.Count - 1;
        var position = fraction ?? _progress.Get(_book.Id).ScrollFraction;
        BookmarkButton.IsEnabled = true;
        BookmarkButton.Content = _bookmarks.IsBookmarked(_book.Id, _chapterIndex, position) ? "Remove bookmark" : "Bookmark";
        FinishBookButton.IsEnabled = true;
        FinishBookButton.Content = _completion.IsFinished(_book.Id) ? "Mark as reading" : "Mark finished";
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
        ReadingModePicker.IsEnabled = !isBusy && _pdfBook is null;
        ReaderSettingsButton.IsEnabled = !isBusy;
        AddFontButton.IsEnabled = !isBusy && _pdfBook is null;
        FontPicker.IsEnabled = !isBusy && _pdfBook is null;
        VocabularyButton.IsEnabled = !isBusy && _book is not null;
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

    private void UpdateDailyReminderTimer()
    {
        _dailyReminderTimer.Stop();
        if (_globalSettings.DailyReadingReminderEnabled)
        {
            _dailyReminderTimer.Start();
            DailyReminderTimer_Tick(null, EventArgs.Empty);
        }
    }

    private void DailyReminderTimer_Tick(object? sender, EventArgs e)
    {
        if (_isClosing) return;
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        if (!ReadingReminderPolicy.IsDue(_globalSettings.DailyReadingReminderEnabled, today, TimeOnly.FromDateTime(now),
                _globalSettings.DailyReadingReminderHour, _globalSettings.DailyReadingReminderMinute,
                _globalSettings.DailyReminderLastDeliveredDate)) return;

        var secondsToday = _activity.GetSeconds(today);
        var message = ReadingReminderPolicy.BuildMessage(_globalSettings.DailyReminderOnlyIfUnread, secondsToday,
            _globalSettings.DailyReminderStreakNudge, _activity.CurrentStreak(today));
        _globalSettings.DailyReminderLastDeliveredDate = ReadingReminderPolicy.DateKey(today);
        _settings.DailyReminderLastDeliveredDate = _globalSettings.DailyReminderLastDeliveredDate;
        _settingsStore.Save(_globalSettings);
        if (message.Length == 0) return;
        DailyReminderToastText.Text = message;
        DailyReminderToast.Visibility = Visibility.Visible;
        StatusText.Text = "Your daily reading reminder is ready";
    }

    private void DismissReminderToastButton_Click(object sender, RoutedEventArgs e) =>
        DailyReminderToast.Visibility = Visibility.Collapsed;

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
