# EPUB test corpus

Real books that `Majorsilence.Recital.Epub.Tests` parses end to end. Each file is committed unmodified,
exactly as downloaded on 2026-10-14. Hand-made edge cases (missing nav, NCX only, broken manifest,
obfuscated fonts, DRM, huge chapters, and so on) are built in code by `TestEpub` instead.

| File | Source | Shape | License |
|---|---|---|---|
| `se-austen-pride-and-prejudice.epub` | [Standard Ebooks](https://standardebooks.org/ebooks/jane-austen/pride-and-prejudice) | EPUB 3, nav + NCX, 65 documents, cover via `cover-image` | Text public domain (US); Standard Ebooks' own work dedicated under [CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/) (see the book's Uncopyright page) |
| `se-shelley-frankenstein.epub` | [Standard Ebooks](https://standardebooks.org/ebooks/mary-shelley/frankenstein) | EPUB 3, nav + NCX, 38 documents | Same as above |
| `pg-11-alice-epub3.epub` | [Project Gutenberg #11](https://www.gutenberg.org/ebooks/11) (EPUB3, images) | EPUB 3 (Ebookmaker), nav + NCX, SVG cover wrapper | Public domain in the USA; distributed under the [Project Gutenberg License](https://www.gutenberg.org/policy/license.html), included in the book |
| `pg-1342-pride-and-prejudice-epub2.epub` | [Project Gutenberg #1342](https://www.gutenberg.org/ebooks/1342) (EPUB, no images) | EPUB 2, NCX only, XHTML 1.1 DOCTYPE, `.html` file names | Same as above |

These are test data only; Recital does not ship them.
