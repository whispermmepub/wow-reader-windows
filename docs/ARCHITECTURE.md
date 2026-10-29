# Architecture and product boundary

## Small first milestone

This is a new, independent Windows desktop application. `whispermmepub/wow-reader-lab` remains the Android source of truth and is not a build input. The Windows app uses WPF for its desktop shell, WebView2 for EPUB XHTML/CSS, and `HtmlAgilityPack` to sanitize chapter markup. The project does not share Android code or data formats yet.

- **WPF shell:** imports `.epub` files, chooses a local library item, moves among spine chapters, and shows approximate whole-book progress.
- **EPUB service:** checks archive paths, extracts a local working copy, parses `META-INF/container.xml` and the OPF manifest/spine, sanitizes local HTML/XHTML documents, and estimates a text weight per spine chapter.
- **WebView2:** presents local XHTML through a virtual HTTPS host name. Scripts, frames, forms and unsafe URL schemes are removed; a restrictive content-security policy is added; top-level navigation is limited to sanitized local HTML/XHTML, and non-local requests, downloads, new windows and permissions are denied.
- **Local stores:** the library catalog, font catalog and reading positions are JSON under `%LOCALAPPDATA%\WoWReader\`; imported EPUB bytes, imported fonts and extracted files remain on the same PC. The per-book key is SHA-256 of the imported EPUB bytes. Imported font bytes are also copied into the active book cache so WebView2 can load them from the same local origin. Installed Windows font families are listed by WPF and applied by CSS family name, without copying or redistributing their files.

The importer currently stops after 1.5 GB of uncompressed archive content to reduce zip-bomb risk. This first-milestone guard is not Android parity and should be measured against licensed large-book samples before the limit is revisited.

## Reading position: Android vs Windows

The Android source uses a content-weighted EPUB progress model, stores a chapter-spine anchor and permille position, and keeps reading progress in its own local persistence layer (SQLite with compatibility preferences). This Windows POC stores a content hash, chapter index, and fraction of the chapter's vertical scroll range in a local JSON file. The percentage shown is weighted by chapter text length, but the extraction and reflow engines differ. Windows lists installed system fonts and can import/apply local TTF/OTF/WOFF/WOFF2 fonts, but the first selection is app-wide; Android supports broader typography and per-book overrides that remain later Windows work.

Do not assume a stored percentage or page/scroll anchor is portable between Android and Windows. Windows resize, zoom, fonts, image-only pages, RTL/vertical writing, footnotes, internal links, unusual encodings and malformed EPUBs need device testing. Cross-device mapping and sync are not implemented. Get the owner's approval before adding account login, Google Drive or any other cross-device sync.

## PDF is an early Windows-native renderer decision

PDF support is planned, but deliberately not faked through WebView2. Evaluate Windows-native options early (including Windows' own PDF APIs) against the actual product needs: continuous scrolling, text selection/search, rotation, annotations/highlights, large-file memory use, accessibility, Windows-version support, packaging, and redistribution/license terms. If a native API fails the requirements, compare a maintained renderer such as PDFium and its binary distribution and license obligations before committing. Android's PDF renderer and UI are not directly portable.

## Privacy and boundaries

No sign-in, analytics, network sync, or upload exists in this milestone. Reader-origin traffic is local-only. NuGet restore and WebView2 runtime installation/update may use the network; book content should not. No book examples, secrets, signing files, fonts, or dictionary data are included. Keep future build and release output out of Git.
