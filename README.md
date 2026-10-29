# WoW Reader for Windows

A local-first WPF companion for WoW Reader, built for Windows with WebView2. It includes the Android app's major user-facing reading and library workflows where they make sense on desktop; platform-specific differences and remaining limitations are listed in [Android parity notes](docs/ANDROID-PARITY.md).

## What is included

- **Home and Library:** continue reading; import EPUB and PDF files from this PC; search title, author, series, or shelf; filter by reading state and shelf; sort by title, author, rating, or recent addition; edit local metadata, ratings and shelves; and optionally choose a local cover image.
- **EPUB reader:** Page by page and Vertical scroll modes, saved reading position, contents, full-book text search, bookmarks, notes/highlights, and the existing reader settings for formatting, typography, themes, colors, dimming, auto-scroll, reading ruler, eye-break reminder, and keep-awake behavior. Typography may be scoped per EPUB.
- **PDF reader:** use the Microsoft Edge WebView2 built-in PDF viewer. Local reading time and completion state are tracked; PDF page position and annotations are not stored by WoW Reader.
- **Vocabulary:** capture selected EPUB text; save your own meanings locally; mark words learned or needing review; and practice with flashcards. Wiktionary lookup opens only when requested. No dictionary database is included.
- **Explore:** optionally load a small set of public blog feeds when you open or refresh the page. Posts are sanitized, cached locally, and linked to their public sources; no personal reading data is sent.
- **Statistics and Calendar:** local reading time, active days, streaks, weekly bars, sessions, and per-book totals; Month, Week and Year Calendar views; private daily notes and per-book memories; and a current-month recap PNG that omits book titles and notes.

Reading activity is an estimate sampled while a book is open and the app window is foreground; it cannot tell whether someone is actively looking at the screen. Daily reading reminders are in-app only and require WoW Reader to be running. See the [parity notes](docs/ANDROID-PARITY.md) for other Windows-specific differences.

The font picker lists Windows-installed families plus custom fonts imported with **Add font…** (TTF, OTF, WOFF, or WOFF2, up to 8 MB). Installed fonts are used by family name; imported fonts stay below `%LOCALAPPDATA%\WoWReader\Fonts\`. Import only fonts you have permission to use. Android source was reviewed read-only; no Android build outputs, book samples, dictionary data, fonts, credentials, signing files, or unlicensed assets are bundled.

## Local data and privacy boundary

Books, PDFs, cover images, reader settings, progress, activity history, bookmarks, notes/highlights, Calendar notes and per-book memories, vocabulary, Explore cache, completion state, fonts, and WebView2 profile are stored under `%LOCALAPPDATA%\WoWReader\`. Explore and explicitly requested Wiktionary lookup are the only new user-facing network actions; the app does not upload personal reading data. There is no sign-in or cross-device sync. Google/Firebase sign-in and Drive/sync remain excluded unless the owner separately approves them.

Project-owned code is under the PolyForm Noncommercial License 1.0.0. See [third-party notices](THIRD-PARTY-NOTICES.md) for dependency terms.

## Build and run

Requirements: Windows 10 or later, the .NET 10 SDK, and the Microsoft Edge WebView2 Evergreen Runtime. Most supported Windows installations already have the runtime; see [Microsoft's WebView2 distribution guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) if it is missing.

```powershell
dotnet restore .\\WowReader.Windows.sln
dotnet run --project .\\src\\WowReader.Windows\\WowReader.Windows.csproj
```

To publish a portable self-contained .NET package (the WebView2 Evergreen Runtime is still required):

```powershell
dotnet publish .\\src\\WowReader.Windows\\WowReader.Windows.csproj --configuration Release --runtime win-x64 --self-contained true --output .\\artifacts\\win-x64
Compress-Archive -Path .\\artifacts\\win-x64\\* -DestinationPath .\\artifacts\\wow-reader-windows-win-x64.zip -Force
```

The GitHub Actions workflow builds on Windows and creates the same portable ZIP on pushes. It is not a signed installer. Validate the packaged app on Windows with the WebView2 runtime and authorized books, fonts, and cover images.
