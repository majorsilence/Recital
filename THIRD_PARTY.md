# Third-party dependencies

Every dependency with its license, added **before** it is used (plan 0.1). Only MIT, Apache-2.0, BSD,
Unlicense or similar permissive licenses; anything copyleft needs sign-off first. Versions are pinned in
`Directory.Packages.props`.

| Package | Used by | License |
|---|---|---|
| Majorsilence.Forms, .Avalonia, .Headless (+ Majorsilence.Forms.Drawing.Common) | UI, hosts, UI tests | MIT |
| Avalonia, Avalonia.Desktop, .Browser, .Android, .iOS (+ Avalonia.Controls.WebView via Majorsilence.Forms.Avalonia) | hosts | MIT |
| SkiaSharp, HarfBuzzSharp (via Majorsilence.Forms) | UI, UI tests | MIT |
| Topten.RichTextKit (via Majorsilence.Forms) | UI; Layout from Phase 3 | Apache-2.0 |
| Noto Sans (embedded in Majorsilence.Forms.Drawing.Common; loaded by UI tests, not redistributed by Recital) | UI tests | OFL-1.1 |
| xunit.v3, xunit.runner.visualstudio | tests | Apache-2.0 |
| Microsoft.NET.Test.Sdk | tests | MIT |

Content (test corpus books, TTS voices) is listed with its license where it is committed, starting
in Phase 2.
