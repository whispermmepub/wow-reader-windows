# WoW Reader for Windows

A separate Windows desktop companion for WoW Reader. This WPF project uses WebView2 for local EPUB reading and implements Windows equivalents for many Android reading settings. It is not a complete Android port.

## Reading and settings

Open an EPUB using **Open EPUB…**. The reader supports chapter navigation, local reading-position restore, page-by-page or vertical-scroll mode, and a **Reader settings…** panel. Settings include Original EPUB/Reader settings formatting, font size and weight, line spacing, margins, alignment, Auto Space, themes, custom text/background colors, Night Light, app-level brightness dimming, auto-scroll speed and scope, a reading ruler, a 30-minute reminder, and keep-display-awake behavior.

The Windows font picker lists families already installed on Windows plus custom fonts imported with **Add font…** (TTF, OTF, WOFF, or WOFF2, up to 8 MB). Installed Windows fonts are used by family name and are not copied; imported fonts stay in `%LOCALAPPDATA%\WoWReader\Fonts\`. Import only fonts you have permission to use. No fonts, book samples, dictionaries, or Android assets are bundled.

The app stores books, reading positions, reader settings, WebView2 profile, and imported fonts below `%LOCALAPPDATA%\WoWReader\`. Nothing is uploaded. Settings use a global local profile; Android's optional per-book typography overrides are not yet mirrored.

## Windows-specific limitations

Page Curl, Android bundled fonts, hardware orientation lock, volume-key navigation, Android system-bar behavior, TOC/search/bookmarks/notes/dictionary, and PDF reading are not included in this Windows settings update. Windows font-size scaling currently uses WebView2 zoom and may scale images as well as text. The brightness setting dims page content, not the monitor backlight. See [Android parity notes](docs/ANDROID-PARITY.md) for the exact coverage and test checklist.

## Build and run

Requirements: Windows 10 or later, the .NET 10 SDK, and the Microsoft Edge WebView2 Evergreen Runtime. Most supported Windows installations already have the runtime; see [Microsoft's WebView2 distribution guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) if it is missing.

```powershell
dotnet restore .\WowReader.Windows.sln
dotnet run --project .\src\WowReader.Windows\WowReader.Windows.csproj
```

On each push, the Windows CI workflow creates a portable `win-x64` ZIP. Unzip it and launch `WowReader.Windows.exe`; the WebView2 Evergreen Runtime is still required. The artifact is not a signed installer, and the owner should validate it on their Windows device with their own authorized EPUBs and fonts.

## Product boundary

- Keep this repository separate from `whispermmepub/wow-reader-lab`; do not edit or copy Android build outputs, signing material, private data, credentials, book files, fonts, or dictionary databases.
- No Google/Firebase sign-in or Drive access is present.
- The project is local-first. Cross-device sync requires the owner's separate approval.
- Project-owned code is provided under the PolyForm Noncommercial License 1.0.0. See [third-party notices](THIRD-PARTY-NOTICES.md) for dependency terms.
