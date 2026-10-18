# Third-party dependencies

Every dependency with its license, added **before** it is used (plan 0.1). Only MIT, Apache-2.0, BSD,
Unlicense or similar permissive licenses; anything copyleft needs sign-off first. Versions are pinned in
`Directory.Packages.props`.

| Package | Used by | License |
|---|---|---|
| Majorsilence.Forms, .Avalonia, .Headless (+ Majorsilence.Forms.Drawing.Common) | UI, hosts, UI tests | MIT |
| Avalonia, Avalonia.Desktop, .Browser, .Android, .iOS (+ Avalonia.Controls.WebView via Majorsilence.Forms.Avalonia) | hosts | MIT |
| SkiaSharp, HarfBuzzSharp (via Majorsilence.Forms; referenced directly by Layout) | UI, UI tests, Layout | MIT |
| Topten.RichTextKit (via Majorsilence.Forms) | UI; Layout from Phase 3 | Apache-2.0 |
| Noto Sans (embedded in Majorsilence.Forms.Drawing.Common; loaded by UI tests, not redistributed by Recital) | UI tests | OFL-1.1 |
| AngleSharp, AngleSharp.Xml | Epub | MIT |
| AngleSharp.Css | Layout | MIT |
| HarfBuzzSharp (+ native assets) | Layout (text shaping) | MIT |
| DejaVu fonts 2.37 (Serif, Sans, Sans Mono), embedded in Majorsilence.Recital.Layout | Layout (default and fallback faces) | Bitstream Vera license (permissive) + public domain; see `src/Majorsilence.Recital.Layout/Fonts/DejaVu/LICENSE` |
| System.Speech | Tts.Platform (Windows SAPI voices; does nothing on other OSes) | MIT |
| Microsoft.ML.OnnxRuntime (+ .Managed, native runtimes) | Tts.Kokoro (runs the bundled voices) | MIT |
| Kokoro-82M v1.0, onnx-community ONNX export (`model_uint8f16.onnx` and six voices; fetched at build time by `build/Kokoro.targets`, shipped beside the app) | Tts.Kokoro, desktop head | Apache-2.0. Trained in part on Koniwa `tnc` (CC BY 3.0) and SIWIS (CC BY 4.0), credited here as their licences ask |
| misaki English dictionaries `us_gold.json`, `us_silver.json` (embedded, gzipped) and the logic of `misaki/en.py` (ported to C#) | Tts.Kokoro | Apache-2.0, © hexgrad |
| PeterReid/graphemes_to_phonemes_en_us weights (embedded `g2p_en_us.safetensors`) | Tts.Kokoro (names and rare words) | Apache-2.0 |
| xunit.v3, xunit.runner.visualstudio | tests | Apache-2.0 |
| Microsoft.NET.Test.Sdk | tests | MIT |

System libraries and tools used at run time and not redistributed: libsecret and GLib on Linux
(LGPL, loaded by `Majorsilence.Recital.Platform` for the Secret Service), Security.framework on macOS,
and advapi32 on Windows. For speech: the speech-dispatcher service on Linux, reached over its SSIP
socket (a protocol; nothing linked), `pw-cat`/`pacat`/`aplay` for audio output on Linux, `say` and
`afplay` on macOS, and winmm on Windows. Piper (GPL-3.0, with espeak-ng) is run only if the user
installed it; Recital does not ship, download or link it. Piper voice models carry their own licences. Test-only: the `httpd:2.4.62` Docker image (Apache-2.0) runs the WebDAV conformance tests
in CI (`tests/webdav/`).

Content (test corpus books, TTS voices) is listed with its license where it is committed: the EPUB
test corpus in [tests/corpus/README.md](tests/corpus/README.md).
