# Android-to-Windows reader settings coverage

The Windows companion implements a local-first EPUB reader with Windows-appropriate equivalents for the core Android reading modes and typography settings. The Android reference used for this comparison is public `main` at commit `46b8d578f3a67771863ce996ce95bb5141c884db`. The existing Android checkout was left untouched. Android source is not a Windows build dependency.

## Implemented in Windows

- **Reading flow:** `Page by page` uses horizontal viewport columns and previous/next page controls; `Vertical scroll` uses normal document scrolling. Progress is stored per book and restored as a normalized chapter position in either mode. Left/right arrow keys turn pages in page mode.
- **Page movement:** `None · default` or `Smooth slide`. Page Curl is not implemented on Windows.
- **EPUB formatting:** `Original EPUB` preserves publisher typography/layout where possible; `Reader settings` applies reader-controlled line spacing, margins, weight, and alignment. `Auto Space` applies language-aware Myanmar/Latin justification CSS for justified Reader settings. It is smart text justification, not extra paragraph spacing.
- **Typography:** font size 80–300% (default 100%), weight 200–800 (default 400%), line height 1.20–2.20 (default 1.60), and margins 1–14% (default 5%). Alignment choices are Original, Justify, Left, and Right. The existing picker uses publisher fonts, installed Windows fonts, and user-imported local TTF/OTF/WOFF/WOFF2 fonts.
- **Auto scroll:** Off by default, Vertical scroll only, speed 1–10 (default 4, using Android's approximate speed mapping), Whole book or This chapter (default Whole book). User input pauses it briefly; Whole book advances at chapter boundaries.
- **Appearance:** Light, Sepia, Dark, E-Ink, and Custom themes, plus optional `#RRGGBB` text/background overrides. Night Light supports Off, On, and Auto (local 7pm–6am). Brightness supports System or 40/60/80/100% using an app-content dimming overlay, not monitor backlight control.
- **Reading helpers:** Keep display awake while the app is open, a configurable reading ruler (1–20 lines), a 30-minute eye-break reminder, and book/chapter/mode position labels.
- **Storage:** reader settings are versioned JSON under `%LOCALAPPDATA%\\WoWReader\\reader-settings.json`; reading positions and imported font catalog remain in their existing local files. Nothing is uploaded.

## Differences and remaining gaps

- The Android experimental Page Curl animation is unavailable; Windows offers None and Smooth slide only.
- Android's bundled Myanmar fonts are not copied. Windows offers fonts already installed on Windows and fonts the owner imports, subject to the owner's license rights.
- Android text-size uses WebView text zoom. Windows currently uses WebView2 zoom, which can scale images and other content as well as text; exact reflow is therefore not pixel-identical.
- Android hardware orientation lock, hardware volume-key navigation, and system-bar immersive behavior do not map directly to a desktop window. The settings panel explains these differences rather than pretending to control them.
- Windows brightness is an in-reader overlay rather than hardware display brightness. Night Light Auto is a local time schedule.
- Settings currently use one global reader profile. Android's optional per-book typography override store is not implemented.
- The Android project also includes TOC, search, bookmarks, notes/highlights, dictionary actions, and PDF paths. These are not provided by this Windows EPUB settings update; PDF remains unsupported.
- Windows EPUB reflow, page columns, script shaping, publisher CSS, and accessibility still need verification with the owner's own authorized books and fonts on Windows/WebView2.

## Suggested Windows checks

1. Switch between Page by page and Vertical scroll; resize the window; confirm position is retained and page arrows/left-right keys behave sensibly.
2. Compare Original EPUB with Reader settings on books with fixed publisher fonts, margins, headings, and images. Confirm the original setting remains legible and Reader controls apply only in Reader mode.
3. Try Myanmar-only and mixed Myanmar/Latin justified text with Auto Space on and off, plus a user-owned Myanmar font. Confirm no Android font or book data is bundled.
4. Try the font-size, weight, line-height, and margin limits; then close/reopen the book and app to verify persistence.
5. Test Auto scroll in chapter-only and whole-book modes, pause after wheel/keyboard input, and confirm it stops at the end of the book.
6. Test Light/Sepia/Dark/E-Ink/Custom colors, Night Light Off/On/Auto, content brightness overlay, ruler, display-awake behavior, and the eye reminder.
7. Verify that all library, settings, progress, and imported-font data remain below `%LOCALAPPDATA%\\WoWReader\\` and that no sign-in or sync is introduced.
