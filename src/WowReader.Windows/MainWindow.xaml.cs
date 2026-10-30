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
using System.Windows.Input;
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
    private const string FixedWebViewRuntimeFolderName = "WebView2FixedRuntime";
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
    private readonly DispatcherTimer _librarySearchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private CoreWebView2Environment? _webEnvironment;
    private EpubBook? _book;
    private LibraryBookEntry? _pdfBook;
    private int _chapterIndex;
    private double _pendingScrollFraction;
    private string _pendingNavigationFragment = "";
    private bool _documentLoaded;
    private bool _suppressPicker;
    private bool _suppressChapterPicker;
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
    private readonly SemaphoreSlim _positionSaveSemaphore = new(1, 1);
    private readonly SemaphoreSlim _readerModeApplySemaphore = new(1, 1);
    private long _positionSaveGeneration;
    private long _lastProcessedPositionSaveGeneration;
    private long _readerModeChangeGeneration;
    private ReaderFullscreenState? _readerFullscreenState;
    private bool _fullscreenTransitionInProgress;
    private string _currentView = "";

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
        _librarySearchTimer.Tick += (_, _) =>
        {
            _librarySearchTimer.Stop();
            if (IsLoaded) RefreshLibraryItems();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _startupStartedAtUtc = DateTimeOffset.UtcNow;
        StartupSplash.Opacity = 1d;
        try
        {
            AppPaths.EnsureCreated();
            _webEnvironment = await CreateWebViewEnvironmentAsync(FindBundledWebViewRuntime());
            await ReaderBrowser.EnsureCoreWebView2Async(_webEnvironment);
            if (_isClosing) return;
            ConfigureWebView();

            await Task.Run(() =>
            {
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
                _bookSettingsStore.Load();
            });
            if (_isClosing) return;
            _globalSettings = _settingsStore.Current;
            _settings = _globalSettings.Clone();
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
                "The reader could not start its embedded browser. Run the portable app from a writable local folder with its bundled WebView2 Fixed Version runtime, or install the Microsoft Edge WebView2 Evergreen Runtime. The Fixed Version runtime cannot run from a network/UNC path.\n\n" + exception.Message,
                "WoW Reader for Windows", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? FindBundledWebViewRuntime()
    {
        if (IsNetworkLocation(AppContext.BaseDirectory)) return null;
        var runtimeFolder = Path.Combine(AppContext.BaseDirectory, FixedWebViewRuntimeFolderName);
        return File.Exists(Path.Combine(runtimeFolder, "msedgewebview2.exe")) ? runtimeFolder : null;
    }

    private static bool IsNetworkLocation(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return true;
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<CoreWebView2Environment> CreateWebViewEnvironmentAsync(string? browserExecutableFolder)
    {
        if (browserExecutableFolder is null)
        {
            return await CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.WebViewProfile);
        }

        try
        {
            await Task.Run(() => PrepareFixedRuntimePermissions(browserExecutableFolder));
            return await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: browserExecutableFolder,
                userDataFolder: AppPaths.WebViewProfile);
        }
        catch (Exception fixedRuntimeException)
        {
            try
            {
                return await CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.WebViewProfile);
            }
            catch (Exception evergreenException)
            {
                throw new InvalidOperationException(
                    $"The bundled Fixed Version runtime could not start ({fixedRuntimeException.Message}); the installed Evergreen fallback could not start either ({evergreenException.Message}).",
                    evergreenException);
            }
        }
    }

    private static void PrepareFixedRuntimePermissions(string runtimeFolder)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0)
            || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var executable = Path.Combine(runtimeFolder, "msedgewebview2.exe");
        var runtimeVersion = FileVersionInfo.GetVersionInfo(executable).ProductVersion
            ?? FileVersionInfo.GetVersionInfo(executable).FileVersion
            ?? string.Empty;
        var markerPath = Path.Combine(AppContext.BaseDirectory, ".wow-reader-webview2-acl-v1");
        var markerContents = Path.GetFullPath(runtimeFolder) + "\n" + runtimeVersion;
        try
        {
            if (File.Exists(markerPath)
                && string.Equals(File.ReadAllText(markerPath), markerContents, StringComparison.Ordinal)) return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        GrantRuntimeReadAccess(runtimeFolder, "*S-1-15-2-2:(OI)(CI)(RX)");
        GrantRuntimeReadAccess(runtimeFolder, "*S-1-15-2-1:(OI)(CI)(RX)");
        try
        {
            File.WriteAllText(markerPath, markerContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The permissions are applied; without a writable marker they will be checked again at next launch.
        }
    }

    private static void GrantRuntimeReadAccess(string runtimeFolder, string accessRule)
    {
        var startInfo = new ProcessStartInfo("icacls.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(runtimeFolder);
        startInfo.ArgumentList.Add("/grant");
        startInfo.ArgumentList.Add(accessRule);
        startInfo.ArgumentList.Add("/T");
        startInfo.ArgumentList.Add("/C");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not start icacls to prepare the bundled WebView2 runtime.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Windows did not finish preparing the bundled WebView2 runtime permissions.");
        }

        if (process.ExitCode != 0)
        {
            var details = string.Join(Environment.NewLine,
                new[] { standardError.GetAwaiter().GetResult(), standardOutput.GetAwaiter().GetResult() }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                ? "Windows could not grant read access to the bundled WebView2 runtime."
                : details.Trim());
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
        if (_pendingNavigationFragment.Length > 0)
        {
            var fragment = _pendingNavigationFragment;
            _pendingNavigationFragment = "";
            await RestoreReaderFragmentAsync(fragment);
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
        if (!IsLoaded) return;
        _librarySearchTimer.Stop();
        _librarySearchTimer.Start();
    }

    private void LibrarySortPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshLibraryItems();
        }
    }

    private void LibraryStatusPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RefreshLibraryItems();
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
        if (_suppressSettingsPicker || ReadingModePicker.SelectedValue is not string mode || !IsLoaded || _isClosing)
        {
            return;
        }
        var requestGeneration = Interlocked.Increment(ref _readerModeChangeGeneration);
        if (mode == _settings.ReadingMode)
        {
            QueueReaderFocus();
            return;
        }

        if (!ReadingModePicker.IsDropDownOpen)
        {
            QueueReaderFocus();
        }
        await SaveCurrentPositionAsync(waitForPersistence: false);
        if (!IsCurrentReaderModeRequest(requestGeneration, mode)) return;

        await _readerModeApplySemaphore.WaitAsync();
        try
        {
            if (!IsCurrentReaderModeRequest(requestGeneration, mode)) return;
            var fraction = _book is null ? 0d : _progress.Get(_book.Id).ScrollFraction;
            _settings.ReadingMode = mode;
            _settings.Normalize();
            SaveActiveSettings();
            await ApplyReaderSettingsAsync();
            if (!IsCurrentReaderModeRequest(requestGeneration, mode)) return;
            await RestoreReaderPositionAsync(fraction);
            if (!IsCurrentReaderModeRequest(requestGeneration, mode)) return;
            UpdateReaderLabels(fraction);
        }
        finally
        {
            _readerModeApplySemaphore.Release();
        }
        QueueReaderFocus();
    }

    private bool IsCurrentReaderModeRequest(long generation, string mode) =>
        generation == Volatile.Read(ref _readerModeChangeGeneration)
        && ReadingModePicker.SelectedValue as string == mode
        && !_isClosing;

    private void ReadingModePicker_DropDownClosed(object sender, EventArgs e)
    {
        QueueReaderFocus();
    }

    private void QueueReaderFocus()
    {
        if (_book is null || !_documentLoaded || ReaderView.Visibility != Visibility.Visible || _isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_isClosing && _book is not null && _documentLoaded && ReaderView.Visibility == Visibility.Visible)
            {
                ReaderBrowser.Focus();
                Keyboard.Focus(ReaderBrowser);
            }
        }));
    }

    private async void ReaderSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
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
        catch (Exception exception)
        {
            StatusText.Text = "Reader settings could not be opened or saved";
            MessageBox.Show(this, $"Reader settings were not fully applied. Your book and reading position are unchanged.\n\n{exception.Message}",
                "Reader settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        await ToggleReaderFullscreenAsync();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && CanToggleReaderFullscreen())
        {
            e.Handled = true;
            await ToggleReaderFullscreenAsync();
            return;
        }
        if (e.Key == Key.Escape && _readerFullscreenState is not null)
        {
            e.Handled = true;
            await ToggleReaderFullscreenAsync();
            return;
        }

        var direction = e.Key switch
        {
            Key.Left or Key.Up or Key.PageUp => -1,
            Key.Right or Key.Down or Key.PageDown => 1,
            _ => 0
        };
        if (direction != 0 && Keyboard.Modifiers == ModifierKeys.None
            && ReadingModePicker.IsKeyboardFocusWithin && !ReadingModePicker.IsDropDownOpen
            && _book is not null && _documentLoaded && ReaderBrowser.CoreWebView2 is { } core)
        {
            e.Handled = true;
            try
            {
                await core.ExecuteScriptAsync($"if(window.wowReaderNavigate)window.wowReaderNavigate({direction});");
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException)
            {
                StatusText.Text = "Reader navigation could not be completed";
            }
            QueueReaderFocus();
        }
    }

    private bool CanToggleReaderFullscreen() => !_isClosing
        && ReaderView.Visibility == Visibility.Visible
        && (_book is not null || _pdfBook is not null);

    private async Task ToggleReaderFullscreenAsync()
    {
        if (!CanToggleReaderFullscreen() || _fullscreenTransitionInProgress) return;
        _fullscreenTransitionInProgress = true;
        try
        {
            double? savedFraction = null;
            if (_book is not null && _documentLoaded)
            {
                await SaveCurrentPositionAsync();
                savedFraction = _progress.Get(_book.Id).ScrollFraction;
            }

            if (_readerFullscreenState is null) EnterReaderFullscreen();
            else ExitReaderFullscreen();

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (_book is not null && _documentLoaded && savedFraction.HasValue)
            {
                await RestoreReaderPositionAsync(savedFraction.Value);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Fullscreen mode could not be changed";
        }
        finally
        {
            _fullscreenTransitionInProgress = false;
            UpdateReaderLabels();
        }
    }

    private void EnterReaderFullscreen()
    {
        _readerFullscreenState = new ReaderFullscreenState(
            WindowStyle, ResizeMode, WindowState, RestoreBounds,
            AppHeader.Visibility, AppFooter.Visibility, ReaderToolbar.Visibility,
            ReaderPageSurface.Margin, ReaderPageSurface.BorderThickness, ReaderPageSurface.CornerRadius);
        AppHeader.Visibility = Visibility.Collapsed;
        AppFooter.Visibility = Visibility.Collapsed;
        ReaderToolbar.Visibility = Visibility.Collapsed;
        ReaderPageSurface.Margin = new Thickness(0);
        ReaderPageSurface.BorderThickness = new Thickness(0);
        ReaderPageSurface.CornerRadius = new CornerRadius(0);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        FullscreenButton.Content = "Exit full screen";
    }

    private void ExitReaderFullscreen()
    {
        if (_readerFullscreenState is not { } previous) return;
        WindowState = WindowState.Normal;
        WindowStyle = previous.WindowStyle;
        ResizeMode = previous.ResizeMode;
        if (previous.WindowState == WindowState.Normal && !previous.RestoreBounds.IsEmpty)
        {
            Left = previous.RestoreBounds.Left;
            Top = previous.RestoreBounds.Top;
            Width = previous.RestoreBounds.Width;
            Height = previous.RestoreBounds.Height;
        }
        AppHeader.Visibility = previous.AppHeaderVisibility;
        AppFooter.Visibility = previous.AppFooterVisibility;
        ReaderToolbar.Visibility = previous.ReaderToolbarVisibility;
        ReaderPageSurface.Margin = previous.ReaderPageSurfaceMargin;
        ReaderPageSurface.BorderThickness = previous.ReaderPageSurfaceBorderThickness;
        ReaderPageSurface.CornerRadius = previous.ReaderPageSurfaceCornerRadius;
        WindowState = previous.WindowState;
        _readerFullscreenState = null;
        FullscreenButton.Content = "Full screen";
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
        _pendingNavigationFragment = "";
        _documentLoaded = false;
        RefreshChapterPicker();
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
        _pendingNavigationFragment = "";
        _pagePositionLabel = "";
        _documentLoaded = false;
        RefreshChapterPicker();

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

    private void RefreshChapterPicker()
    {
        _suppressChapterPicker = true;
        try
        {
            ChapterPicker.ItemsSource = _book?.Chapters
                .Select((chapter, index) => new ChapterPickerItem(index, $"{index + 1}. {chapter.Title}"))
                .ToArray();
            ChapterPicker.SelectedValue = _book is null ? null : _chapterIndex;
            ChapterPicker.IsEnabled = _documentLoaded && _book is not null && _book.Chapters.Count > 0;
        }
        finally
        {
            _suppressChapterPicker = false;
        }
    }

    private void UpdateChapterPickerSelection()
    {
        _suppressChapterPicker = true;
        ChapterPicker.SelectedValue = _book is null ? null : _chapterIndex;
        ChapterPicker.IsEnabled = _documentLoaded && _book is not null && _book.Chapters.Count > 0;
        _suppressChapterPicker = false;
    }

    private async void ChapterPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChapterPicker || _book is null || !IsLoaded || _isClosing
            || ChapterPicker.SelectedValue is not int selectedIndex
            || selectedIndex < 0 || selectedIndex >= _book.Chapters.Count || selectedIndex == _chapterIndex)
        {
            return;
        }

        await SaveCurrentPositionAsync();
        _chapterIndex = selectedIndex;
        _pendingScrollFraction = 0d;
        _pendingNavigationFragment = "";
        _pendingSearchTerm = "";
        NavigateToCurrentChapter();
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
        _pendingScrollFraction = direction < 0 ? 1d : 0d;
        _pendingNavigationFragment = "";
        NavigateToCurrentChapter();
    }

    private void NavigateToCurrentChapter()
    {
        if (_book is null || ReaderBrowser.CoreWebView2 is null || _book.Chapters.Count == 0)
        {
            return;
        }
        _chapterIndex = Math.Clamp(_chapterIndex, 0, _book.Chapters.Count - 1);
        UpdateChapterPickerSelection();
        _documentLoaded = false;
        ChapterPicker.IsEnabled = false;
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

    private async Task SaveCurrentPositionAsync(bool waitForPersistence = true)
    {
        var generation = Interlocked.Increment(ref _positionSaveGeneration);
        while (true)
        {
            await SavePositionRequestAsync(generation, waitForPersistence);
            var latestGeneration = Volatile.Read(ref _positionSaveGeneration);
            if (generation == latestGeneration) return;
            generation = latestGeneration;
        }
    }

    private async Task SavePositionRequestAsync(long generation, bool waitForPersistence)
    {
        await _positionSaveSemaphore.WaitAsync();
        try
        {
            if (generation != Volatile.Read(ref _positionSaveGeneration)
                || generation <= _lastProcessedPositionSaveGeneration)
            {
                return;
            }
            if (_book is null || !_documentLoaded || ReaderBrowser.CoreWebView2 is null)
            {
                _lastProcessedPositionSaveGeneration = generation;
                return;
            }

            var book = _book;
            var chapterIndex = _chapterIndex;
            var readingMode = _settings.ReadingMode;
            var core = ReaderBrowser.CoreWebView2;
            await SaveCurrentPositionCoreAsync(book, chapterIndex, readingMode, core, waitForPersistence);
            _lastProcessedPositionSaveGeneration = generation;
        }
        finally
        {
            _positionSaveSemaphore.Release();
        }
    }

    private async Task SaveCurrentPositionCoreAsync(EpubBook book, int chapterIndex, string readingMode, CoreWebView2 core, bool waitForPersistence)
    {
        try
        {
            const string script = "(()=>{const d=document.documentElement;const fraction=window.wowReaderGetProgress?window.wowReaderGetProgress():(()=>{const m=Math.max(0,d.scrollHeight-window.innerHeight);return m===0?0:Math.max(0,Math.min(1,window.scrollY/m));})();const pageLabel=window.wowReaderGetPageLabel?window.wowReaderGetPageLabel():'Page mode';return {Fraction:fraction,PageLabel:pageLabel};})()";
            var result = await core.ExecuteScriptAsync(script);
            var snapshot = JsonSerializer.Deserialize<ReaderPositionSnapshot>(result);
            var fraction = snapshot?.Fraction ?? 0d;
            if (!double.IsFinite(fraction)) fraction = 0d;
            if (readingMode == "page") _pagePositionLabel = snapshot?.PageLabel ?? "Page mode";
            fraction = Math.Clamp(fraction, 0d, 1d);
            var persistence = _progress.SaveAsync(book.Id, chapterIndex, fraction);
            if (waitForPersistence) await persistence;
            else _ = ObservePositionPersistenceAsync(persistence);
            if (_book?.Id == book.Id && _chapterIndex == chapterIndex) UpdateReaderLabels(fraction);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or IOException or UnauthorizedAccessException or COMException)
        {
            if (_book?.Id == book.Id && _chapterIndex == chapterIndex) StatusText.Text = "Reading — position save will retry";
        }
    }

    private async Task ObservePositionPersistenceAsync(Task persistence)
    {
        try
        {
            await persistence.ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_isClosing) StatusText.Text = "Reading — position save will retry";
                }));
            }
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
        await SampleActivityAsync();
        var shouldExit = ShowExitConfirmationDialog();
        _exitDialogOpen = false;
        if (!shouldExit) return;
        _isClosing = true;
        _saveTimer.Stop();
        _activityTimer.Stop();
        _librarySearchTimer.Stop();
        _eyeReminderTimer.Stop();
        _dailyReminderTimer.Stop();
        SaveCalendarDayNoteIfNeeded();
        SaveCalendarDayMemoryIfNeeded();
        await SaveCurrentPositionAsync();
        await _activity.SaveAsync();
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
        var viewChanged = !string.Equals(_currentView, view, StringComparison.Ordinal);
        _currentView = view;
        if (view != "Reader" && _readerFullscreenState is not null) ExitReaderFullscreen();
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
        if (view == "Explore" && viewChanged) _ = LoadExploreFeedAsync();
        if (viewChanged) RefreshScreenData();
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
        if (IsLoaded) RefreshLibraryItems();
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
            Title = $"Contents · {_book.Title}", Owner = this, Width = 560, Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White
        };
        var items = _book.Contents.Select(item => new TocItem(item.Title, item.ChapterIndex, item.Fragment, item.Depth)).ToArray();
        var list = new ListBox { Margin = new Thickness(12), DisplayMemberPath = nameof(TocItem.DisplayTitle) };
        list.ItemsSource = items;
        list.SelectedIndex = Array.FindIndex(items, item => item.ChapterIndex == _chapterIndex);
        if (list.SelectedIndex < 0 && items.Length > 0) list.SelectedIndex = 0;
        var openButton = new Button { Content = "Open selected section", Padding = new Thickness(14, 8, 14, 8), MinWidth = 150, IsDefault = true };
        openButton.IsEnabled = list.SelectedItem is TocItem;
        openButton.Click += async (_, _) => await OpenSelectedAsync();
        list.MouseDoubleClick += async (_, _) => await OpenSelectedAsync();
        var root = new DockPanel { Margin = new Thickness(8) };
        var instruction = new TextBlock { Text = "Choose a section from this book’s contents.", Foreground = Brushes.DimGray, Margin = new Thickness(12, 10, 12, 0) };
        DockPanel.SetDock(instruction, Dock.Top);
        DockPanel.SetDock(openButton, Dock.Bottom);
        root.Children.Add(instruction);
        root.Children.Add(openButton);
        root.Children.Add(list);
        dialog.Content = root;
        dialog.ShowDialog();

        async Task OpenSelectedAsync()
        {
            if (list.SelectedItem is not TocItem item || _book is null) return;
            await SaveCurrentPositionAsync();
            _chapterIndex = item.ChapterIndex;
            _pendingScrollFraction = 0d;
            _pendingNavigationFragment = item.Fragment;
            _pendingSearchTerm = "";
            NavigateToCurrentChapter();
            dialog.Close();
        }
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

        CancellationTokenSource? searchCancellation = null;
        var searchGeneration = 0;

        async Task RunSearchAsync()
        {
            var term = searchBox.Text.Trim();
            if (term.Length == 0)
            {
                results.ItemsSource = Array.Empty<SearchResultItem>();
                message.Text = "Enter a word or phrase to search.";
                return;
            }
            if (_book is not { } book)
            {
                message.Text = "Open an EPUB before searching its text.";
                return;
            }

            var generation = ++searchGeneration;
            var cancellation = new CancellationTokenSource();
            searchCancellation?.Cancel();
            searchCancellation = cancellation;
            findButton.IsEnabled = false;
            message.Text = "Searching this EPUB on this PC…";
            try
            {
                var matches = await Task.Run(() => FindInBook(book, term, cancellation.Token), cancellation.Token);
                if (generation != searchGeneration || !dialog.IsVisible || !ReferenceEquals(_book, book)) return;
                results.ItemsSource = matches;
                message.Text = matches.Count == 0 ? "No matches found in this EPUB." : $"{matches.Count} match(es). Double-click a result to jump to it.";
            }
            catch (OperationCanceledException)
            {
                // A newer query or dialog close superseded this search.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                message.Text = "The local EPUB text could not be searched.";
            }
            finally
            {
                if (generation == searchGeneration) findButton.IsEnabled = true;
                cancellation.Dispose();
            }
        }

        findButton.Click += async (_, _) => await RunSearchAsync();
        searchBox.PreviewKeyDown += async (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            await RunSearchAsync();
        };
        searchBox.TextChanged += (_, _) =>
        {
            if (searchCancellation is null) return;
            searchGeneration++;
            searchCancellation.Cancel();
            searchCancellation = null;
            findButton.IsEnabled = searchBox.Text.Trim().Length > 0;
            message.Text = "Search changed. Press Find to run the updated query.";
        };
        dialog.Closed += (_, _) =>
        {
            searchGeneration++;
            searchCancellation?.Cancel();
            searchCancellation = null;
        };
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

    private List<SearchResultItem> FindInBook(EpubBook book, string term, CancellationToken cancellationToken)
    {
        var results = new List<SearchResultItem>();
        var boundedTerm = term.Length > 160 ? term[..160] : term;
        for (var chapterIndex = 0; chapterIndex < book.Chapters.Count && results.Count < 350; chapterIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chapter = book.Chapters[chapterIndex];
            var document = new HtmlDocument();
            document.Load(chapter.LocalPath);
            var text = HtmlEntity.DeEntitize(document.DocumentNode.InnerText)
                .Replace('\u00a0', ' ');
            text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var offset = 0;
            while (offset < text.Length && results.Count < 350)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

    private sealed record TocItem(string Title, int ChapterIndex, string Fragment, int Depth)
    {
        public string DisplayTitle => string.Concat(Enumerable.Repeat("\u2003", Math.Clamp(Depth, 0, 8)))
            + (Depth > 0 ? "› " : "") + Title;
    }
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
                if (saved.HighlightColor != "none") await ApplyReaderAnnotationAsync(saved);
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
            .Select(note => new
            {
                quote = note.Quote,
                color = HighlightColorCss(note.HighlightColor)
            })
            .ToArray();
        var annotationsJson = JsonSerializer.Serialize(annotations);
        var script = "(()=>{const notes=" + annotationsJson + ";document.querySelectorAll('mark[data-wow-reader-note]').forEach(m=>{const f=document.createDocumentFragment();while(m.firstChild)f.appendChild(m.firstChild);m.replaceWith(f);});let i=0;const applyBatch=()=>{const end=Math.min(i+6,notes.length);for(;i<end;i++){const {quote,color}=notes[i];if(!quote||!window.find)continue;if(!window.find(quote,false,false,true,false,true,false))continue;const selection=window.getSelection();if(!selection||!selection.rangeCount)continue;const range=selection.getRangeAt(0);const mark=document.createElement('mark');mark.dataset.wowReaderNote='1';mark.style.cssText='background:'+color+';color:inherit;border-radius:2px;padding:0 1px;';mark.appendChild(range.extractContents());range.insertNode(mark);selection.removeAllRanges();}if(i<notes.length){if(window.requestIdleCallback)window.requestIdleCallback(applyBatch,{timeout:60});else window.setTimeout(applyBatch,0);}};window.setTimeout(applyBatch,0);})();";
        try
        {
            await ReaderBrowser.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            StatusText.Text = "Some saved highlights could not be displayed";
        }
    }

    private async Task ApplyReaderAnnotationAsync(ReaderNote annotation)
    {
        if (_book is null || !_documentLoaded || ReaderBrowser.CoreWebView2 is null
            || annotation.Quote.Length == 0 || annotation.HighlightColor == "none") return;
        var quote = JsonSerializer.Serialize(annotation.Quote);
        var color = JsonSerializer.Serialize(HighlightColorCss(annotation.HighlightColor));
        var script = "(()=>{const quote=" + quote + ",color=" + color + ";if(!window.find||!window.find(quote,false,false,true,false,true,false))return;const selection=window.getSelection();if(!selection||!selection.rangeCount)return;const range=selection.getRangeAt(0);const mark=document.createElement('mark');mark.dataset.wowReaderNote='1';mark.style.cssText='background:'+color+';color:inherit;border-radius:2px;padding:0 1px;';mark.appendChild(range.extractContents());range.insertNode(mark);selection.removeAllRanges();})();";
        try
        {
            await ReaderBrowser.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            StatusText.Text = "Note saved, but its highlight could not be displayed";
        }
    }

    private static string HighlightColorCss(string color) => color switch
    {
        "teal" => "#A9DCC8",
        "pink" => "#F4B8C4",
        "purple" => "#D5C2EF",
        _ => "#FFE082"
    };

    private sealed record ReaderPositionSnapshot(double Fraction, string? PageLabel);

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

    private async void ActivityTimer_Tick(object? sender, EventArgs e)
    {
        await SampleActivityAsync();
    }

    private async Task SampleActivityAsync()
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
            if (_currentView == "Library") RefreshLibraryItems();
            else if (_currentView != "Reader" && _currentView != "Explore") RefreshScreenData();
            await _activity.SaveAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = "Reading activity could not be saved locally";
        }
    }

    private void RefreshLibraryItems()
    {
        var searchText = LibrarySearchBox.Text.Trim();
        var shelfFilter = LibraryShelfPicker.SelectedItem as string;
        var statusFilter = LibraryStatusPicker.SelectedValue as string ?? "all";
        IEnumerable<(LibraryBookEntry Entry, int SecondsRead, bool HasProgress, ReadingPosition Position, bool IsFinished)> libraryEntries =
            _library.Books.Select(entry => (entry, _activity.GetBookSeconds(entry.Id), _progress.Has(entry.Id),
                _progress.Get(entry.Id), _completion.IsFinished(entry.Id)));

        if (searchText.Length > 0)
        {
            libraryEntries = libraryEntries.Where(book => book.Entry.Title.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                || book.Entry.Author.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                || book.Entry.Series.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                || book.Entry.Shelves.Any(shelf => shelf.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)));
        }
        if (!string.IsNullOrWhiteSpace(shelfFilter) && shelfFilter != "All shelves")
            libraryEntries = libraryEntries.Where(book => book.Entry.Shelves.Contains(shelfFilter, StringComparer.CurrentCultureIgnoreCase));
        libraryEntries = libraryEntries.Where(book => statusFilter switch
        {
            "reading" => (book.HasProgress || book.SecondsRead > 0) && !book.IsFinished,
            "unread" => !book.HasProgress && book.SecondsRead == 0 && !book.IsFinished,
            "finished" => book.IsFinished,
            _ => true
        });
        libraryEntries = (LibrarySortPicker.SelectedValue as string) switch
        {
            "title" => libraryEntries.OrderBy(book => book.Entry.Title, StringComparer.CurrentCultureIgnoreCase),
            "title-desc" => libraryEntries.OrderByDescending(book => book.Entry.Title, StringComparer.CurrentCultureIgnoreCase),
            "author" => libraryEntries.OrderBy(book => book.Entry.Author, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(book => book.Entry.Title, StringComparer.CurrentCultureIgnoreCase),
            "rating" => libraryEntries.OrderByDescending(book => book.Entry.Rating ?? 0)
                .ThenBy(book => book.Entry.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => libraryEntries
        };

        var visibleEntries = libraryEntries.ToArray();
        LibraryBookItems.ItemsSource = visibleEntries.Select(book =>
        {
            var entry = book.Entry;
            var coverPath = _library.CoverPath(entry);
            var state = book.IsFinished ? "Finished" : book.HasProgress || book.SecondsRead > 0 ? "Reading" : "Unread";
            var metadata = string.Join(" · ", new[]
            {
                entry.Format.ToUpperInvariant(),
                entry.Author,
                entry.Series,
                entry.Rating is { } stars ? new string('★', stars) + new string('☆', 5 - stars) : "",
                entry.Shelves.Count > 0 ? "Shelves: " + string.Join(", ", entry.Shelves) : ""
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!book.HasProgress)
            {
                var status = book.SecondsRead > 0 ? $"{state} · {FormatDuration(book.SecondsRead)} tracked" : $"{state} · Not started yet";
                return new LibraryBookItem(entry.Id, entry.Title, JoinDetails(metadata, status, "Stored on this PC"), coverPath);
            }

            var detail = _book?.Id == entry.Id
                ? $"{_book.OverallPercent(_chapterIndex, book.Position.ScrollFraction)}% through the book · Last read {book.Position.UpdatedAt.ToLocalTime():MMM d, yyyy}"
                : $"Chapter {book.Position.ChapterIndex + 1} · {book.Position.ScrollFraction:P0} through chapter · Last read {book.Position.UpdatedAt.ToLocalTime():MMM d, yyyy}";
            return new LibraryBookItem(entry.Id, entry.Title, JoinDetails(metadata, state, detail), coverPath);
        }).ToArray();
        LibraryResultCountText.Text = $"{visibleEntries.Length} of {_library.Books.Count} books";
        LibraryEmptyText.Text = _library.Books.Count == 0
            ? "Your library is ready for a book. Import an EPUB or PDF from your PC to add it here."
            : "No books match this search. Try another title or clear the search field.";
        LibraryEmptyState.Visibility = visibleEntries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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

        RefreshLibraryItems();

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
            FullscreenButton.IsEnabled = true;
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
            FullscreenButton.IsEnabled = false;
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
        FullscreenButton.IsEnabled = true;
        UpdateChapterPickerSelection();
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
        ChapterPicker.IsEnabled = !isBusy && _documentLoaded && _book is not null;
        FullscreenButton.IsEnabled = !isBusy && (_book is not null || _pdfBook is not null);
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
        var book = _book;
        if (book is null || !_documentLoaded || ReaderBrowser.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var selectedId = _fonts.SelectedFontId;
            if (selectedId?.StartsWith("system:", StringComparison.Ordinal) == true)
            {
                var familyName = selectedId["system:".Length..];
                var systemCss = $"html,body,body *{{font-family:{CssString(familyName)},'Segoe UI','Myanmar Text',sans-serif!important;}}";
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

            var fontDirectory = Path.Combine(book.ExtractedRoot, ".wow-reader-fonts");
            var extension = Path.GetExtension(fontPath);
            var localFont = Path.Combine(fontDirectory, fontId + extension);
            await Task.Run(() =>
            {
                Directory.CreateDirectory(fontDirectory);
                if (!File.Exists(localFont)) File.Copy(fontPath, localFont, overwrite: false);
            });
            if (_book?.Id != book.Id || _fonts.SelectedFontId != fontId) return;
            var relative = Path.GetRelativePath(book.ExtractedRoot, localFont)
                .Replace(Path.DirectorySeparatorChar, '/');
            var escapedPath = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
            var fontUri = $"https://{ReaderHost}/{escapedPath}";
            var css = $"@font-face{{font-family:'WoWReaderCustom';src:url('{fontUri}') format('{format}');font-style:normal;font-weight:normal;font-display:swap;}} html,body,body *{{font-family:'WoWReaderCustom','Segoe UI','Myanmar Text',sans-serif!important;}}";
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
        var script = $"(async()=>{{let s=document.getElementById('wow-reader-custom-font');if(!s){{s=document.createElement('style');s.id='wow-reader-custom-font';document.head.appendChild(s);}}s.textContent={cssLiteral};if(document.fonts)await document.fonts.load('16px WoWReaderCustom');}})();";
        await ReaderBrowser.CoreWebView2!.ExecuteScriptAsync(script);
    }

    private async Task ApplyReaderSettingsAsync()
    {
        if (_book is null || !_documentLoaded || ReaderBrowser.CoreWebView2 is null)
        {
            return;
        }

        _settings.Normalize();
        ReaderBrowser.ZoomFactor = 1d;
        var configuration = new
        {
            mode = _settings.ReadingMode,
            animation = _settings.PageAnimation,
            autoScroll = _settings.AutoScrollEnabled,
            autoScrollSpeed = AutoScrollSpeedPixels(_settings.AutoScrollSpeed),
            autoScrollWholeBook = _settings.AutoScrollWholeBook,
            nightLight = _settings.NightLight,
            theme = _settings.Theme,
            darkTheme = _settings.Theme == "dark",
            fontSizePercent = _settings.FontSizePercent,
            hasPreviousChapter = _chapterIndex > 0,
            hasNextChapter = _chapterIndex < _book.Chapters.Count - 1,
            brightness = _settings.BrightnessPercent,
            ruler = _settings.ReadingRulerEnabled,
            rulerLines = _settings.ReadingRulerLines
        };
        var settingsJson = JsonSerializer.Serialize(configuration);
        var cssJson = JsonSerializer.Serialize(BuildReaderSettingsCss());
        var script = "(()=>{const c=" + settingsJson + ";const css=" + cssJson + ";"
            + "let s=document.getElementById('wow-reader-settings');if(!s){s=document.createElement('style');s.id='wow-reader-settings';document.head.appendChild(s);}s.textContent=css;"
            + "const b=document.body,d=document.documentElement;if(!b)return;"
            + "if(!b.dataset.wowReaderImageOnlyChecked){const readerText=(b.textContent||'').trim();b.classList.toggle('wow-reader-image-only',!!b.querySelector('img,svg')&&readerText.length<80);b.dataset.wowReaderImageOnlyChecked='1';}"
            + "const applyTextScale=()=>{const scale=Math.max(.5,Math.min(3,(Number(c.fontSizePercent)||100)/100));if(b.dataset.wowReaderScale===String(c.fontSizePercent))return;const nodes=[b,...b.querySelectorAll('*')].filter(n=>!n.matches('img,svg,math,video,audio,canvas,iframe,object,embed,script,style,source,track,br,hr'));const sizes=nodes.map(n=>({n,base:Number(n.dataset.wowReaderBaseFontSize)||parseFloat(getComputedStyle(n).fontSize)||16}));sizes.forEach(({n,base})=>{n.dataset.wowReaderBaseFontSize=String(base);n.style.setProperty('font-size',(base*scale)+'px','important');});b.dataset.wowReaderScale=String(c.fontSizePercent);};applyTextScale();"
            + "const prep=()=>{if(c.mode==='page'){b.scrollLeft=Math.max(0,b.scrollLeft);b.scrollTop=0;}else{b.scrollLeft=0;}};prep();"
            + "window.wowReaderGetProgress=()=>{if(c.mode==='page'){const m=Math.max(0,b.scrollWidth-b.clientWidth);return m===0?0:Math.max(0,Math.min(1,b.scrollLeft/m));}const m=Math.max(0,d.scrollHeight-window.innerHeight);return m===0?0:Math.max(0,Math.min(1,window.scrollY/m));};"
            + "window.wowReaderGetPageLabel=()=>{const w=Math.max(1,b.clientWidth);const total=Math.max(1,Math.ceil(b.scrollWidth/w));const page=Math.max(1,Math.min(total,Math.floor((b.scrollLeft+2)/w)+1));return 'Page '+page+' / '+total;};"
            + "window.wowReaderRestoreProgress=f=>{f=Math.max(0,Math.min(1,Number(f)||0));if(c.mode==='page'){const m=Math.max(0,b.scrollWidth-b.clientWidth);b.scrollTo({left:Math.round(m*f),top:0,behavior:'auto'});}else{const m=Math.max(0,d.scrollHeight-window.innerHeight);window.scrollTo(0,Math.round(m*f));}};"
            + "window.wowReaderTurnPage=direction=>{if(c.mode!=='page')return false;const m=Math.max(0,b.scrollWidth-b.clientWidth),current=b.scrollLeft,step=Math.max(1,b.clientWidth);if(direction<0&&current<2||direction>0&&current>=m-2)return false;const x=Math.max(0,Math.min(m,current+(direction<0?-step:step)));if(Math.abs(x-current)<2)return false;b.scrollTo({left:x,top:0,behavior:c.animation==='slide'?'smooth':'auto'});return true;};"
            + "const postChapter=direction=>{const allowed=direction<0?c.hasPreviousChapter:c.hasNextChapter;if(!allowed||!window.chrome?.webview)return false;if(window.__wowReaderBoundaryPending)return true;window.__wowReaderBoundaryPending=true;window.chrome.webview.postMessage(direction<0?'reader-previous':'reader-next');setTimeout(()=>{window.__wowReaderBoundaryPending=false;},1600);return true;};window.wowReaderPostChapter=postChapter;"
            + "const step=direction=>{if(c.mode==='page'){if(window.wowReaderTurnPage(direction))return true;return postChapter(direction);}const max=Math.max(0,d.scrollHeight-window.innerHeight),current=window.scrollY;if(direction<0&&current>2||direction>0&&current<max-2){window.__wowReaderUserScrollUntil=Date.now()+320;window.scrollBy({top:(direction<0?-1:1)*Math.max(1,window.innerHeight*.82),behavior:c.animation==='slide'?'smooth':'auto'});return true;}return postChapter(direction);};window.wowReaderNavigate=step;window.__wowReaderMode=c.mode;"
            + "if(!window.__wowReaderKeysBound){window.addEventListener('keydown',e=>{const t=e.target;if(e.altKey||e.ctrlKey||e.metaKey||t?.isContentEditable||t?.closest?.('input,textarea,select,button,a[href]'))return;let dir=0;if(e.key==='Enter')dir=e.shiftKey?-1:1;else if(['ArrowRight','ArrowDown','PageDown',' '].includes(e.key))dir=1;else if(['ArrowLeft','ArrowUp','PageUp'].includes(e.key))dir=-1;if(!dir)return;const handled=e.key==='Enter'&&window.__wowReaderMode==='scroll'?window.wowReaderPostChapter(dir):window.wowReaderNavigate(dir);if(!handled)return;e.preventDefault();e.stopPropagation();},true);window.__wowReaderKeysBound=true;}"
            + "if(!window.__wowReaderWheelBound){let lastPageWheel=0;window.addEventListener('wheel',e=>{if(window.__wowReaderMode==='page'){const delta=Math.abs(e.deltaY)>=Math.abs(e.deltaX)?e.deltaY:e.deltaX;if(Math.abs(delta)>1){e.preventDefault();const now=performance.now();if(now-lastPageWheel>=220){lastPageWheel=now;window.wowReaderNavigate(delta>0?1:-1);}}return;}if(Math.abs(e.deltaY)>1){window.__wowReaderUserScrollUntil=Date.now()+320;const max=Math.max(0,document.documentElement.scrollHeight-window.innerHeight);if(e.deltaY>0&&window.scrollY>=max-2)window.wowReaderNavigate(1);else if(e.deltaY<0&&window.scrollY<=2)window.wowReaderNavigate(-1);}}, {passive:false});window.addEventListener('scroll',()=>{if(window.__wowReaderMode!=='scroll'||Date.now()>(window.__wowReaderUserScrollUntil||0)||window.__wowReaderBoundaryPending)return;const max=Math.max(0,document.documentElement.scrollHeight-window.innerHeight);if(window.scrollY>=max-2)window.wowReaderNavigate(1);else if(window.scrollY<=2)window.wowReaderNavigate(-1);},{passive:true});window.__wowReaderWheelBound=true;}"
            + "if(!b.dataset.wowReaderLanguageScanned){b.dataset.wowReaderLanguageScanned='1';const re=/[\\u1000-\\u109f\\ua9e0-\\ua9ff\\uaa60-\\uaa7f]/;const nodes=document.querySelectorAll('p,li,blockquote,dd,dt,figcaption,td,th');let i=0;const scan=deadline=>{let count=0;while(i<nodes.length&&count<80&&(deadline.didTimeout||deadline.timeRemaining()>1)){const n=nodes[i++],t=(n.textContent||'').trim();if(t){const myanmar=re.test(t);n.classList.toggle('wow-reader-auto-myanmar',myanmar);n.classList.toggle('wow-reader-auto-latin',!myanmar);}count++;}if(i<nodes.length){if(window.requestIdleCallback)window.requestIdleCallback(scan,{timeout:1500});else setTimeout(()=>scan({didTimeout:true,timeRemaining:()=>0}),0);}};if(window.requestIdleCallback)window.requestIdleCallback(scan,{timeout:1500});else setTimeout(()=>scan({didTimeout:true,timeRemaining:()=>0}),0);}"
            + "const updateOverlay=(id,show,css)=>{let o=document.getElementById(id);if(show){if(!o){o=document.createElement('div');o.id=id;o.dataset.wowReaderOverlay='true';document.body.appendChild(o);}o.style.cssText=css;}else if(o)o.remove();};"
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
            "premium" => ("#EDF3EC", "#20382B"),
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
            .Append("body a{color:").Append(_settings.Theme == "dark" ? "#9CC7FF" : _settings.Theme == "premium" ? "#226949" : "#245B9E").Append("!important;}")
            .Append("*,*::before,*::after{break-before:auto!important;break-after:auto!important;page-break-before:auto!important;page-break-after:auto!important;-webkit-column-break-before:auto!important;-webkit-column-break-after:auto!important;}")
            .Append("body img,body video,body svg{max-width:90vw!important;max-height:85vh!important;object-fit:contain!important;height:auto!important;}");

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
            var pageMargin = margin + "vw";
            css.Append("html{height:100%!important;overflow:hidden!important;}")
                .Append("body{width:100vw!important;min-width:100vw!important;box-sizing:border-box!important;height:100vh!important;max-height:100vh!important;overflow-x:auto!important;overflow-y:hidden!important;")
                .Append("column-count:auto!important;column-width:calc(100vw - ").Append(pageMargin).Append(" - ").Append(pageMargin).Append(")!important;")
                .Append("column-gap:calc(").Append(pageMargin).Append(" + ").Append(pageMargin).Append(")!important;column-fill:auto!important;")
                .Append("margin:0!important;padding:5vh ").Append(pageMargin).Append("!important;}")
                .Append("body img,body video,body svg{max-width:calc(100vw - ").Append(pageMargin).Append(" - ").Append(pageMargin).Append(")!important;max-height:90vh!important;")
                .Append("width:auto!important;height:auto!important;object-fit:contain!important;display:block!important;margin-left:auto!important;margin-right:auto!important;")
                .Append("break-inside:avoid!important;page-break-inside:avoid!important;-webkit-column-break-inside:avoid!important;}")
                .Append("body.wow-reader-image-only>:not([data-wow-reader-overlay]){height:auto!important;min-height:0!important;max-height:90vh!important;overflow:visible!important;break-inside:avoid!important;page-break-inside:avoid!important;}");
        }
        else
        {
            css.Append("html{height:auto!important;overflow-x:hidden!important;overflow-y:auto!important;}")
                .Append("body{width:auto!important;min-width:0!important;height:auto!important;max-height:none!important;overflow-x:hidden!important;overflow-y:visible!important;")
                .Append("column-count:auto!important;column-width:auto!important;column-gap:normal!important;}");
        }
        return css.ToString();
    }

    private async Task RestoreReaderFragmentAsync(string fragment)
    {
        if (ReaderBrowser.CoreWebView2 is null || !_documentLoaded || string.IsNullOrWhiteSpace(fragment)) return;
        var fragmentJson = JsonSerializer.Serialize(fragment);
        try
        {
            await ReaderBrowser.CoreWebView2.ExecuteScriptAsync(
                $"(()=>{{const f={fragmentJson};let e=document.getElementById(f);if(!e)e=document.getElementsByName(f)[0];if(e)e.scrollIntoView({{block:'start',behavior:'auto'}});}})();");
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            // The target may have been removed by EPUB sanitization; keep chapter navigation available.
        }
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
        var message = e.TryGetWebMessageAsString();
        if (message == "reader-next")
        {
            await TurnOrNavigateAsync(1);
        }
        else if (message == "reader-previous")
        {
            await TurnOrNavigateAsync(-1);
        }
        else if (message == "autoscroll-next" && _settings.AutoScrollWholeBook
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

    private sealed record ChapterPickerItem(int Index, string Title);
    private sealed record FontPickerItem(string Id, string Name);
    private sealed record ReaderFullscreenState(WindowStyle WindowStyle, ResizeMode ResizeMode, WindowState WindowState,
        Rect RestoreBounds, Visibility AppHeaderVisibility, Visibility AppFooterVisibility, Visibility ReaderToolbarVisibility,
        Thickness ReaderPageSurfaceMargin, Thickness ReaderPageSurfaceBorderThickness, CornerRadius ReaderPageSurfaceCornerRadius);
}
