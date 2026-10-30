# WoW Reader for Windows

A local-first WPF companion for WoW Reader, built for Windows with WebView2. It includes the Android app's major user-facing reading and library workflows where they make sense on desktop; platform-specific differences and remaining limitations are listed in [Android parity notes](docs/ANDROID-PARITY.md).

## Download for Windows x64

Both packages are published on the [GitHub Releases page](https://github.com/whispermmepub/wow-reader-windows/releases/latest):

- **Portable ZIP — `wow-reader-windows-win-x64.zip`:** extract it to a writable folder on the local PC and launch `WowReader.Windows.exe`. It includes a self-contained .NET app and Microsoft WebView2 Fixed Version Runtime, so a separate WebView2 installation is not required. Do not run it from a network/UNC path. The bundled browser runtime does not auto-update; use a later app release to receive runtime updates.
- **Offline installer — `WoWReader-Windows-Offline-Setup-x64.exe`:** installs the self-contained app and includes Microsoft's WebView2 Evergreen Standalone Runtime, which it installs only if the runtime is missing. The installer is not code-signed, so Windows SmartScreen or other security software may show a warning.

Both packages are x64. Validate the application on Windows with authorized books, fonts, and cover images; the packages have not had interactive Windows GUI testing in the build sandbox.

## What is included

- **Home and Library:** continue reading; import EPUB and PDF files from this PC; search title, author, series, or shelf; filter by reading state and shelf; sort by title, author, rating, or recent addition; edit local metadata, ratings and shelves; and optionally choose a local cover image.
- **EPUB reader:** Page by page and Vertical scroll modes, saved reading position, contents, full-book text search, bookmarks, notes/highlights, and reader settings for formatting, typography, themes, colors, dimming, auto-scroll, reading ruler, eye-break reminder, and keep-awake behavior. Typography may be scoped per EPUB.
- **PDF reader:** use the Microsoft Edge WebView2 built-in PDF viewer. Local reading time and completion state are tracked; PDF page position and annotations are not stored by WoW Reader.
- **Vocabulary:** capture selected EPUB text; save your own meanings locally; mark words learned or needing review; and practice with flashcards. Wiktionary lookup opens only when requested. No dictionary database is included.
- **Explore:** optionally load a small set of public blog feeds when you open or refresh the page. Posts are sanitized, cached locally, and linked to their public sources; no personal reading data is sent.
- **Statistics and Calendar:** local reading time, active days, streaks, weekly bars, sessions, and per-book totals; Month, Week and Year Calendar views; private daily notes and per-book memories; and a current-month recap PNG that omits book titles and notes.

Reading activity is an estimate sampled while a book is open and the app window is foreground; it cannot tell whether someone is actively looking at the screen. Daily reading reminders are in-app only and require WoW Reader to be running. See the [parity notes](docs/ANDROID-PARITY.md) for other Windows-specific differences.

The font picker lists Windows-installed families plus custom fonts imported with **Add font…** (TTF, OTF, WOFF, or WOFF2, up to 8 MB). Installed fonts are used by family name; imported fonts stay below `%LOCALAPPDATA%\WoWReader\Fonts\`. Import only fonts you have permission to use. Android source was reviewed read-only; no Android build outputs, book samples, dictionary data, fonts, credentials, signing files, or unlicensed assets are bundled.

## Local data and privacy boundary

Books, PDFs, cover images, reader settings, progress, activity history, bookmarks, notes/highlights, Calendar notes and per-book memories, vocabulary, Explore cache, completion state, fonts, and WebView2 profile are stored under `%LOCALAPPDATA%\WoWReader\`. Explore and explicitly requested Wiktionary lookup are the only new user-facing network actions; the app does not upload personal reading data. There is no sign-in or cross-device sync. Google/Firebase sign-in and Drive/sync remain excluded unless the owner separately approves them.

Project-owned code is under the PolyForm Noncommercial License 1.0.0. See [third-party notices](THIRD-PARTY-NOTICES.md) for dependency terms.

## Build and run from source

For development, use Windows 10 or later, the .NET 10 SDK, and the Microsoft Edge WebView2 Evergreen Runtime. Most supported Windows installations already have the runtime; see [Microsoft's WebView2 distribution guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) if it is missing.

```powershell
dotnet restore .\WowReader.Windows.sln
dotnet run --project .\src\WowReader.Windows\WowReader.Windows.csproj
```

The [Windows build workflow](.github/workflows/windows-build.yml) compiles the source and, on pushes, publishes a self-contained x64 build with a pinned and signature-checked WebView2 Fixed Version Runtime. The runtime pin should be refreshed for a future release; Fixed Version Runtime does not auto-update.

To build the offline installer, run `bash ./installer/build-installer.sh` in an environment with the .NET 10 SDK, NSIS (`makensis`), `curl`, OpenSSL, and `osslsigncode`. The script downloads and verifies Microsoft's Evergreen Standalone Runtime before packaging it. Keep generated output under `artifacts/`; those build products and runtimes are intentionally excluded from Git.
