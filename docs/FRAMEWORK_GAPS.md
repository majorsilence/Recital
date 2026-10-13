# Majorsilence.Forms gaps

Every place Recital hit a missing, stubbed or WinForms-divergent Majorsilence.Forms behaviour, per plan
section 0.2. Local workarounds are marked `// FRAMEWORK-GAP: <id>` (or the XML-comment equivalent) in
code, so `git grep FRAMEWORK-GAP` finds each one.

Severity: **blocker** (cannot ship the feature), **major** (workaround costs real effort or
fidelity), **minor** (cosmetic or cheap to work around), **scope** (a question of what the framework
should cover, not a bug).

| ID | Area | Backends | Severity | Status |
|---|---|---|---|---|
| GAP-001 | Packaging: no `net10.0-ios` asset in `Majorsilence.Forms.Avalonia` | Avalonia iOS | blocker (iOS) | Fix on Forms branch `fix/avalonia-ios-package` |
| GAP-002 | No pixel-diff/baseline helper for Headless renders | Headless | scope | Local helper |

---

## GAP-001: `Majorsilence.Forms.Avalonia` on nuget.org has no `net10.0-ios` asset

- **Found:** Phase 1, setting up `hosts/Majorsilence.Recital.iOS`, framework 26.11.4.
- **Expected:** The package ships the same `net10.0-ios` row the source project builds with
  `EnableIOSHead`, as it does for `net10.0-android` and `net10.0-browser`. `Application.RunIOS` and
  `IosAudioBackend` are documented and used by `samples/Gallery.iOS`.
- **Actual:** The 26.11.4 nuspec lists `net8.0`, `net10.0`, `net10.0-android36.0` and
  `net10.0-browser1.0` only. `publish-nuget.yml` and `release.yml` pack `Majorsilence.Forms.Avalonia` on
  Linux in the `PACKABLE_PROJECTS` loop with `-p:EnableAndroidTarget=true`, and the iOS row can only build on
  macOS. On `net10.0-ios`, NuGet falls back to the plain `net10.0` desktop asset (and its
  `Avalonia.Desktop` dependency), so none of the iOS-specific backend code is present.
- **Repro:**
  `curl -s https://api.nuget.org/v3-flatcontainer/majorsilence.forms.avalonia/26.11.4/majorsilence.forms.avalonia.nuspec | grep targetFramework`
- **Severity:** blocker for the iOS head (plan phases 4, 8 and 10). Phase 1 only needs it to build.
- **Workaround:** none. The iOS head builds against the fallback asset and is marked in its csproj.
- **Fix:** Majorsilence.Forms branch `fix/avalonia-ios-package`. It packs `Majorsilence.Forms.Avalonia` in the
  macOS job with both mobile workloads, the way `Majorsilence.Forms.Essentials` already is.

## GAP-002: No pixel-diff/baseline helper for Headless renders

- **Found:** Phase 1, `Majorsilence.Recital.UI.Tests`.
- **Expected:** Plan 0.3 calls the Headless backend "pixel-diff capable". `HeadlessRenderer.CapturePng`
  provides the render, but comparing it against a committed baseline, with a tolerance for cross-OS
  anti-aliasing, is left to each app.
- **Actual:** Majorsilence.Forms' own tests assert on pixels ad hoc. There is no shared helper.
- **Severity:** scope. Should every consumer write its own, or should `Majorsilence.Forms.Headless`
  offer `AssertMatchesBaseline`?
- **Workaround:** `tests/Majorsilence.Recital.UI.Tests/PixelBaseline.cs` (about 70 lines, SkiaSharp).
