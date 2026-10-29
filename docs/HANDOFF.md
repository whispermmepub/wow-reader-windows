# Maintainer handoff

## Run it on Windows

1. Install the .NET 10 SDK and Microsoft Edge WebView2 Evergreen Runtime.
2. Clone this repository and run `dotnet restore .\WowReader.Windows.sln` followed by `dotnet run --project .\src\WowReader.Windows\WowReader.Windows.csproj`.
3. Import an EPUB that the tester is authorized to use. Check the title, spine order, images/styles, previous/next controls, percentage, restart/reopen behavior, and whether the reader returns close to the same vertical position after resize or zoom.
4. Pick an existing Windows-installed family and confirm the EPUB text changes without copying its file. Then use **Add font…** with a font the tester is authorized to use. Try TTF/OTF/WOFF/WOFF2, check the imported font appears in the picker, applies to the open chapter, remains selected after restart, and displays the expected Myanmar/English glyphs. Verify imported font bytes stay under `%LOCALAPPDATA%\WoWReader\Fonts\` and are not uploaded.
5. Confirm local data is under `%LOCALAPPDATA%\WoWReader\` and that the app operates with the network disconnected after dependencies/runtime are present.

Use a legally obtained test book; do not commit it or any local app data. The importer has a 1.5 GB uncompressed extraction guard and currently assumes UTF-8-compatible chapter text. Validate real books before calling the EPUB support broad.

## Before expanding scope

- Review EPUB 2/3 navigation, footnotes/endnotes, internal anchors, multi-file spine layout, RTL/vertical writing, large/image-heavy chapters, malformed content, and accessibility with real Windows builds.
- Compare reading position semantics with Android: Windows stores spine chapter plus vertical-scroll fraction and content-weighted approximate percentage; Android has its own content-weighted progress model and persistence. Do not claim compatible sync positions.
- Evaluate a Windows-native PDF renderer early. Record supported Windows versions, text/search/selection behavior, memory performance, packaging and third-party license terms before choosing it.
- Preserve project-owned code under PolyForm Noncommercial 1.0.0. Verify every added package, font, dictionary, icon, and sample asset license; add required notices. Do not bundle unlicensed material.
- No Google/Firebase sign-in or Drive sync belongs in the first MVP. Ask the owner before adding any account or cross-device sync design or code.
- Keep `whispermmepub/wow-reader-lab` read-only from this project. Do not port signing/build/release artifacts, repository secrets, production keys, private app data, or copyrighted book files.

## Validation status

The .NET 10 SDK in the Linux sandbox successfully restored and compiled the solution targeting `net10.0-windows`; this is a compile check, not a launch test. A separate isolated smoke test checked font-file signature validation, local font import/catalog persistence, and storing a Windows family selection using a synthetic font header; it did not test real font rendering. The GitHub Actions workflow is configured to build on pushes and pull requests and upload a portable, self-contained `win-x64` ZIP artifact on pushes (WebView2 Evergreen Runtime is still required; this is not a signed installer).

The owner should install and launch that artifact on Windows, then validate real authorized EPUBs and fonts before treating the POC as an end-user reader.
