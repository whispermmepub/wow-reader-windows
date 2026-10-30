# Maintainer handoff

## Run and test on Windows

1. Install the .NET 10 SDK and Microsoft Edge WebView2 Evergreen Runtime for development, then clone this repository and run `dotnet restore .\WowReader.Windows.sln` followed by `dotnet run --project .\src\WowReader.Windows\WowReader.Windows.csproj`.
2. Import an EPUB that the tester is authorized to use. Check the title, spine order, images/styles, full cover display, no adjacent-page text bleed in page mode, responsive wheel/keyboard navigation, previous/next controls, progress, restart/reopen behavior, and position retention after resize or zoom. Verify F5 toggles fullscreen and Esc/F5 exits it while restoring the prior window size and chrome.
3. Pick an existing Windows-installed family and confirm the EPUB text changes without copying its file. Then use **Add font…** with a font the tester is authorized to use. Try TTF/OTF/WOFF/WOFF2, check the imported font appears in the picker, applies to the open chapter, remains selected after restart, and displays the expected Myanmar/English glyphs. Verify imported font bytes stay under `%LOCALAPPDATA%\WoWReader\Fonts\` and are not uploaded.
4. Confirm local data is under `%LOCALAPPDATA%\WoWReader\` and that the app operates with the network disconnected after dependencies/runtime are present.

Use a legally obtained test book; do not commit it or any local app data. The importer has a 1.5 GB uncompressed extraction guard and currently assumes UTF-8-compatible chapter text. Validate real books before calling the EPUB support broad.

## Packaging

- `.github/workflows/windows-build.yml` builds on pushes and pull requests. On pushes, it also creates a self-contained x64 portable ZIP containing a pinned Microsoft WebView2 Fixed Version Runtime. The workflow validates Microsoft's CAB hash and Authenticode signature; the included Fixed Version Runtime does not auto-update and must be refreshed for a future release.
- `installer/build-installer.sh` creates the x64 offline setup executable using NSIS and includes Microsoft's Evergreen Standalone Runtime when needed. The script checks the downloaded runtime's size and Microsoft Authenticode signature. It requires the .NET 10 SDK, `makensis`, `curl`, OpenSSL, and `osslsigncode`.
- Keep generated binaries, packages, downloaded runtimes, and test data under `artifacts/` or other ignored build directories. Publish ZIP/EXE packages as GitHub Release assets, never as Git files.
- The current setup executable is unsigned. Windows may show SmartScreen or security software warnings. The Linux build sandbox can compile the Windows-targeted source but cannot confirm interactive installation, launch, rendering, or WebView2 behavior on a Windows desktop.

## Before expanding scope

- Review EPUB 2/3 navigation, footnotes/endnotes, internal anchors, multi-file spine layout, RTL/vertical writing, large/image-heavy chapters, malformed content, and accessibility with real Windows builds.
- Compare reading position semantics with Android: Windows stores spine chapter plus vertical-scroll fraction and content-weighted approximate percentage; Android has its own content-weighted progress model and persistence. Do not claim compatible sync positions.
- Evaluate a Windows-native PDF renderer early. Record supported Windows versions, text/search/selection behavior, memory performance, packaging, and third-party license terms before choosing it.
- Preserve project-owned code under PolyForm Noncommercial 1.0.0. Verify every added package, font, dictionary, icon, and sample asset license; add required notices. Do not bundle unlicensed material.
- No Google/Firebase sign-in or Drive sync belongs in the first MVP. Ask the owner before adding any account or cross-device sync design or code.
- Keep `whispermmepub/wow-reader-lab` read-only from this project. Do not port signing/build/release artifacts, repository secrets, production keys, private app data, or copyrighted book files.

## Validation status

A Release build of the Windows-targeted solution in the Linux sandbox is a compile check, not a launch test. The packages require validation on a Windows PC with authorized EPUB/PDF files, fonts, and cover images. The current release should be treated as a preview until that interactive testing is complete.
