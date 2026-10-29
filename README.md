# WoW Reader for Windows

A separate Windows desktop companion for WoW Reader. This repository is an independent WPF project; the Android source repository remains unchanged and is not a dependency.

## First milestone

The initial proof of concept opens local EPUB files, extracts their reading order, displays chapters through WebView2, supports previous/next chapter navigation, and saves the chapter plus within-chapter scroll position locally. Imported books and reading state remain on this Windows device.

This is a deliberately small first milestone, not a feature-complete port of Android. PDF is planned for an early Windows-native renderer evaluation; it is not implemented yet. Reader notes, highlights, dictionary, sign-in, Google Drive, and cross-device sync are also out of scope. Ask the product owner before adding any sync or account feature.

## Build and run

Requirements: Windows 10 or later, the .NET 10 SDK, and the Microsoft Edge WebView2 Evergreen Runtime. Most supported Windows installations already have the runtime; see [Microsoft's WebView2 distribution guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution) if it is missing.

```powershell
dotnet restore .\WowReader.Windows.sln
dotnet run --project .\src\WowReader.Windows\WowReader.Windows.csproj
```

Use **Open EPUB…** to import a book. The app stores its private copy, extracted reader cache, catalog, WebView2 profile, reading positions, and imported fonts below `%LOCALAPPDATA%\WoWReader\`. No book content is uploaded. The first import may take time because this proof of concept extracts EPUB resources locally.

The font picker lists families already installed on Windows, plus custom fonts imported with **Add font…** (TTF, OTF, WOFF, or WOFF2, up to 8 MB). Installed Windows fonts are used by family name and are not copied; imported fonts stay in `%LOCALAPPDATA%\WoWReader\Fonts\`. The choice applies app-wide in this first version rather than using Android's per-book typography overrides. Import only fonts you have permission to use. The repository bundles no fonts.

On each push, the Windows workflow also uploads a portable `win-x64` ZIP as an Actions artifact named `wow-reader-windows-win-x64`. Download it from the repository's latest successful **Windows build** run, unzip it, and launch `WowReader.Windows.exe`. This is not a signed installer and still requires the WebView2 Evergreen Runtime; the project has not yet been smoke-tested on the owner's Windows device.

A Windows CI workflow builds the WPF solution on pushes and pull requests. This repository was scaffolded on a non-Windows host, so an actual Windows launch and real-book compatibility still need owner/device validation.

## Product boundary

- Keep this repository separate from `whispermmepub/wow-reader-lab`; do not edit or copy Android build outputs, signing material, private data, credentials, book files, fonts, or dictionary databases.
- No Google/Firebase sign-in or Drive access is present in this milestone.
- The project is local-first. Ask the owner before implementing cross-device sync.
- Use only books and fonts that the tester has the right to use. No book samples, fonts, or dictionaries are bundled.

## License

Project-owned code is provided under [PolyForm Noncommercial License 1.0.0](LICENSE). It is source-available, noncommercial software and is not an OSI-approved open-source license. Third-party dependencies keep their own terms; see [third-party notices](THIRD-PARTY-NOTICES.md).
