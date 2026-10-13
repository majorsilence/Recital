# Majorsilence.Forms coverage

Every Majorsilence.Forms control and API Recital uses, which backends it was exercised on, and whether it
behaved. Regenerated at the end of each phase (plan section 6). Kept accurate, not flattering.

Legend: ✅ works · ⚠️ works with a gap (see link) · ❌ broken · — not exercised yet

Backends: **Desktop** = Avalonia on Windows/macOS/Linux, **Android** / **iOS** / **Browser** = Avalonia
heads, **Headless** = offscreen Skia (UI tests).

| API | Desktop | Android | iOS | Browser | Headless | Gaps |
|---|---|---|---|---|---|---|
| `Form` (Text, ClientSize, title-bar chrome render) | — | — | — | — | ✅ | |
| `Application.Run` | — | | | | | |
| `Application.RunAndroid` | | — | | | | |
| `Application.RunIOS` | | | — | | | [GAP-001](FRAMEWORK_GAPS.md#gap-001-majorsilenceformsavalonia-on-nugetorg-has-no-net100-ios-asset) |
| `Application.RunBrowserAsync` | | | | — | | |
| `AvaloniaPlatformBackend.RaiseBackRequested` / `RegisterAndroidActivity` / `ReportAndroidIntent` | | — | | | | |
| `HeadlessRenderer.Use` / `CapturePng` | | | | | ✅ | [GAP-002](FRAMEWORK_GAPS.md#gap-002-no-pixel-diffbaseline-helper-for-headless-renders) |

Phase 1 status: the host entry points compile; run-time checks on each device happen in Phase 4,
when there is a UI worth running.
