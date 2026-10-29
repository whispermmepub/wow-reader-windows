# Android-to-Windows feature coverage

WoW Reader for Windows is a separate local-first WPF app, based on the public Windows repository. The Android app in `whispermmepub/wow-reader-lab` was used as a read-only behavior reference; this work did not modify that repository or copy its book data, fonts, dictionary assets, credentials, signing material, or other unlicensed content. Android source is not a Windows build dependency. Project-owned code remains under the PolyForm Noncommercial License 1.0.0.

## Available in Windows

### Home and Library

Home is the landing page and provides continue-reading, a local activity summary, and routes to the available screens. The app keeps the branded splash, Windows icon, and an exit confirmation with today's locally tracked reading time.

The Library imports local EPUB and PDF files. It searches by title, author, series, and shelf; filters by reading state and shelf; and sorts by recent addition, title, author, or rating. Book details can store a title, author, series, description, 1–5 rating, and multiple shelf names. A user-selected cover image can be copied into the local library after basic file-size, format, and dimensions checks. Use only cover images you have permission to use. Books and metadata stay on this PC.

### Reading and reader settings

For EPUBs, the reader supports chapter navigation, saved reading position, Page by page and Vertical scroll modes, contents, full-book text search, bookmarks, notes/highlights, and the existing typography, theme, ruler, auto-scroll, keep-awake, brightness-dimming, and eye-break options. Typography settings can be scoped to an individual EPUB; other reader settings remain global.

PDFs open in the Microsoft Edge WebView2 built-in PDF viewer. WoW Reader tracks foreground reading time and finished/in-progress state for a PDF, but does not save its page position or provide its own PDF annotation layer or EPUB typography controls. PDF rendering depends on the installed WebView2 Runtime; see [Microsoft's WebView2 PDF documentation](https://learn.microsoft.com/en-us/deployedge/microsoft-edge-pdf).

The Vocabulary window can capture selected EPUB text, store words and user-written meanings locally, mark terms learned or needing review, and run simple flashcard review. Online lookup is an explicit user action that opens the term on Wiktionary in the default browser. No dictionary database is bundled.

### Explore, statistics, and Calendar

Explore is an optional page that requests a small set of public feed URLs only when opened or refreshed. It shows sanitized text snippets and source links, uses an allowlist and bounded local cache, and does not send personal reading data. Opening a post hands the link to the user's default browser; feed availability depends on network/source availability.

Statistics show local reading time, active days, current and longest streaks, weekly bars, sessions, per-book time, and manually finished-book count. Activity is sampled while a book is open and the app window is foreground, so the duration is an estimate, not proof of active attention.

Calendar supports Month, Week, and Year views. Selecting a day shows reading time, sessions, and books read; the user can save a private daily note and a separate memory for each book tracked on that date. Statistics can export a current-month recap as a PNG. The recap omits book titles and private notes.

### Local storage and privacy

Books, PDFs, cover images, reader settings and per-book typography overrides, progress, activity journal, bookmarks, notes/highlights, daily Calendar notes and per-book memories, vocabulary, Explore cache, completion state, and imported fonts remain below `%LOCALAPPDATA%\WoWReader\`. Explore and explicit Wiktionary lookup are the only new user-facing network actions; reading data is not uploaded. There is no Google/Firebase sign-in, account, Drive integration, or cross-device sync. Sync requires separate owner approval.

## Genuinely platform-specific or unavailable behavior

- Android hardware-orientation lock, volume-key navigation, immersive system-bar behavior, Android lifecycle hooks, Android sharing intents, and Play Store integrations do not map directly to this desktop app. Windows brightness is a reader dimming layer, not monitor backlight control.
- Daily reading reminders appear as an in-app toast while WoW Reader is running. Windows does not currently schedule a native background notification after the app is closed; reminders are disabled by default.
- PDF page position, annotations, and the Android PDF reading-mode controls are not stored or implemented by WoW Reader. The embedded viewer uses WebView2/Edge's own PDF experience.
- Explore is a small public feed, not a personalized catalogue or recommendation service. Dictionary definitions are not bundled; users add meanings themselves or deliberately open Wiktionary.
- The splash and shell do not mirror every Android app-theme palette. WebView2 rendering, publisher CSS, fonts, and pagination can differ from Android; desktop zoom may scale images as well as text.
- Android reading history is not imported. Activity on Windows begins locally and is not merged across devices.

## Validation and Windows test checklist

A Release build of `WowReader.Windows.sln` targeting `net10.0-windows` has compiled successfully in the sandbox, and `git diff --check` passes. This validates compilation, not interactive Windows runtime behavior; the sandbox does not provide the Windows desktop runtime. The packaged app still needs validation on a Windows PC with the Microsoft Edge WebView2 Evergreen Runtime.

1. Import authorized EPUB and PDF files; verify they remain under `%LOCALAPPDATA%\WoWReader\` and survive restart.
2. Try Library search, author/title/rating sorting, Reading/Unread/Finished filters, shelves, cover selection/removal, and manual completion.
3. Compare EPUB Page by page and Vertical scroll, contents, search, bookmarks, notes/highlights, and saved progress after reopening.
4. Try PDF reading and its built-in toolbar; confirm the documented lack of WoW Reader page-position and annotation storage.
5. Capture selected words, add a meaning, mark it learned, use flashcard review, and verify that Wiktionary opens only after the explicit lookup action.
6. Open/refresh Explore online and offline; verify cache behavior and that no personal reading data is required.
7. Inspect Month, Week, and Year views, save a day note and per-book memory, export a PNG recap, and verify that private titles/notes are absent.
8. Try typography scope, reminder settings/toast, the 30-minute eye-break reminder, imported owner-authorized fonts, and a complex EPUB.
9. Confirm the app still has no sign-in, reading-data upload, Google/Firebase integration, or Drive/cross-device sync.
