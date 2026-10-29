# Android-to-Windows feature parity

The Windows companion should preserve the useful offline reading experience where Windows has an appropriate equivalent, but it is a separate implementation. Android-only UI, storage and renderer details are not a compatibility contract.

## In this first Windows milestone

- Local EPUB library import and reading; spine chapter navigation and saved chapter/scroll position.
- Selection of fonts already installed on Windows, plus local TTF/OTF/WOFF/WOFF2 import for EPUB text. Installed fonts are applied by family name and not copied; imported bytes stay in `%LOCALAPPDATA%\WoWReader\Fonts\`. The repository bundles no font. Current selection is app-wide; Android's per-book typography behavior is a later parity item.
- PDF remains an early Windows-native renderer investigation, not an implemented claim.

## Practical next parity steps

1. Validate Windows EPUB reading, reflow, position restore, reading order, footnotes/endnotes, internal links, accessibility and custom-font glyph coverage with books the tester is authorized to use.
2. Add typography controls (font size, line spacing, margins, alignment and per-book overrides) and ensure custom fonts can be selected per book without changing original publisher styling unexpectedly.
3. Design a local Smart Library with metadata, shelves, covers, completion state, notes/highlights, history and reading statistics; preserve the owner's data expectations before choosing storage migrations.
4. Decide the native Windows PDF renderer early, based on text/search/selection, continuous reading, annotations, performance, accessibility and license/deployment terms.
5. Evaluate dictionary, vocabulary and calendar features only after verifying Windows UX and every third-party dictionary/font license.

Google/Firebase sign-in, Google Drive backup and cross-device sync are intentionally absent from the first MVP. The owner must approve a later sync design before any account or cross-device capability is added. Android position values must not be assumed to map directly to Windows chapter/scroll anchors.
