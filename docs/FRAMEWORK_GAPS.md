# Majorsilence.Forms gaps

Every place Recital hit a missing, stubbed or WinForms-divergent Majorsilence.Forms behaviour, per plan
section 0.2. Local workarounds are marked `// FRAMEWORK-GAP: <id>` (or the XML-comment equivalent) in
code, so `git grep FRAMEWORK-GAP` finds each one.

Severity: **blocker** (cannot ship the feature), **major** (workaround costs real effort or
fidelity), **minor** (cosmetic or cheap to work around), **scope** (a question of what the framework
should cover, not a bug).

| ID | Area | Backends | Severity | Status |
|---|---|---|---|---|
| GAP-001 | Packaging: no `net10.0-ios` asset in `Majorsilence.Forms.Avalonia` | Avalonia iOS | blocker (iOS) | Fix in [Forms PR #478](https://github.com/majorsilence/Majorsilence.Forms/pull/478) |
| GAP-002 | No pixel-diff/baseline helper for Headless renders | Headless | scope | Local helper |
| GAP-003 | Headless renders depend on the host OS (chrome policy, system fonts) | Headless | major (for UI tests) | Local workaround |
| GAP-004 | `Theme.UIFont` honours only the typeface's family name, not the typeface | all | major | Local workaround |

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
- **Fix:** [Majorsilence.Forms PR #478](https://github.com/majorsilence/Majorsilence.Forms/pull/478) (branch `fix/avalonia-ios-package`). It packs `Majorsilence.Forms.Avalonia` in the
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

## GAP-003: Headless renders depend on the host OS

- **Found:** Phase 1, the first CI run of `Majorsilence.Recital.UI.Tests`, on framework 26.11.4.
- **Expected:** An offscreen test backend whose output depends only on the code under test, so one
  machine can produce a baseline that another machine reproduces.
- **Actual:** Two kinds of host dependence:
  1. *Chrome.* `Form`'s constructor sets `UseSystemDecorations = true` on macOS, so a Headless render
     there has no title bar while Windows and Linux draw the custom one. The CI macOS render of an
     empty `MainForm` is a blank grey rectangle.
  2. *Fonts.* `Theme`'s default UI font is `"Segoe UI Emoji"` on Windows and `"sans-serif"` elsewhere, resolved
     through the machine's font manager. `FontSubstitution.ResolveWhereNoSystemFonts` only uses the embedded
     Noto/Liberation faces when the platform has *no* fonts. The CI Ubuntu 24.04 image and a local
     Ubuntu 26.04 machine rendered the same title 319 pixels apart.
- **Repro:** `HeadlessRenderer.Use (); HeadlessRenderer.CapturePng (new Form { Text = "Recital" }, 1000, 700);`
  on macOS vs Linux, or on two Linux machines with different fonts installed.
- **Severity:** major for pixel tests. Without a workaround, every baseline is tied to one machine image.
- **Workaround:** `tests/Majorsilence.Recital.UI.Tests/HeadlessTestEnvironment.cs` sets
  `MF_FORCE_CUSTOM_CHROME=1`, the switch Majorsilence.Forms' own CI uses. It also registers the Noto Sans faces
  that `Majorsilence.Forms.Drawing.Common` embeds as the UI font (see GAP-004), finding them by manifest
  resource name, which is not a public contract. With both in place, a local Ubuntu 26.04 render matches
  the CI Ubuntu 24.04 baseline. Baselines are still per OS, because each OS's Skia anti-aliases the same
  font differently (about 200 pixels on the title); that is expected, not a gap.
- **Proposed fix:** a `HeadlessRenderer.UseDeterministic ()` (or an option on `Use`) that forces custom
  chrome and the embedded fonts, plus a public accessor for the bundled typefaces.

## GAP-004: `Theme.UIFont` honours only the typeface's family name

- **Found:** Phase 1, reviewing CI renders after the GAP-003 workaround.
- **Expected:** `Theme.UIFont = someTypeface` makes controls draw with that typeface, as the property's
  type (`SKTypeface`) suggests.
- **Actual:** Text layout goes through `CachingFontMapper`, which resolves a face from the style's
  family *name*: first `PrivateFontRegistry`, then the platform font manager. A typeface created with
  `SKTypeface.FromData`/`FromStream` is never used directly. Where its family is not installed, text
  silently renders in the system UI font: Helvetica on the macOS runner, Segoe UI on Windows.
- **Repro:** On Windows or macOS without Noto Sans installed,
  `Theme.UIFont = SKTypeface.FromStream (notoSansStream);` then render a `Form` with a title. The
  title is not Noto Sans.
- **Severity:** major. An app bundling its own UI font (Recital will, for consistent typography across
  devices) has to know to go through `PrivateFontCollection` as well.
- **Workaround:** register the same font bytes with `PrivateFontCollection.AddMemoryFont` before
  assigning `Theme.UIFont`, which is what WinForms code that bundles a font does anyway.
- **Proposed fix:** have the `Theme.UIFont`/`UIFontBold` setters add a typeface that the font manager
  cannot resolve by family name to `PrivateFontRegistry`, or at least document the requirement on the
  property.
