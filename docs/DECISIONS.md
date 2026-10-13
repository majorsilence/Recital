# Decisions

One short entry per non-obvious choice. Newest last.

## D-001: Project names are `Majorsilence.Recital.*`; the app is called Recital (2026-10-13)

The plan's `Reader.*` working names become `Majorsilence.Recital.<Area>` (`Majorsilence.Recital.Core`,
`.Epub`, `.Layout`, ...), with tests as `Majorsilence.Recital.<Area>.Tests`. This matches how
Majorsilence.Forms (`Majorsilence.Forms.*`) and Reporting (`Majorsilence.Reporting.*`, `Majorsilence.Pdf.*`)
are named, and keeps every package under the reserved `Majorsilence.*` NuGet ID prefix. Assemblies,
root namespaces and package IDs all use the same name. This settles open question 5: the app is
**Recital**, and the sync folder is `<SyncRoot>/Recital/`.

## D-002: Avalonia is the only interactive host (2026-10-13)

Hosts are the Majorsilence.Forms Avalonia backend on desktop (Windows/macOS/Linux), Android, iOS and
the browser. The plan's `Desktop.Uno` and `Desktop.Gtk4` second-/third-backend heads are dropped.
Framework coverage beyond Avalonia comes from the Headless backend (all UI tests) and the stretch
Terminal host. Per-backend columns in `FRAMEWORK_COVERAGE.md` cover only those backends.

## D-003: Framework fixes go to a Majorsilence.Forms branch, not only a patch note (2026-10-13)

Plan 0.2 step 3 says to write proposed fixes up in `docs/framework-patches/<id>.md`. When the fix is
clear, make it instead in a `git worktree` of Majorsilence.Forms on a new branch, and link that branch
from the gap entry. Recital still consumes the framework only as a pinned NuGet package, never as a
project reference or a vendored copy, so a fix reaches Recital when a framework release ships it and
the pin is bumped.

## D-004: Projects are created in the phase that first needs them (2026-10-13)

Phase 1 creates the core libraries (`Core`, `Epub`, `Layout`, `Storage`, `Tts`, `UI`), the four
Avalonia hosts and `UI.Tests`. Storage providers, TTS engines and the other test projects are added
in their own phases rather than as empty shells now.

## D-005: Warnings as errors (2026-10-13)

`src/` treats warnings as errors in every configuration (plan 0.3). `hosts/` and `tests/` follow the
Majorsilence.Forms rule, errors in Release only, which is what CI builds.

## D-006: Pixel baselines are per OS, with a tight tolerance (2026-10-13)

Headless renders are compared to PNGs under `tests/Majorsilence.Recital.UI.Tests/Baselines/`, one per
OS (`<name>.linux.png`, `.windows.png`, `.macos.png`), because the title bar and other text render with
each OS's own fonts. The comparison allows a per-channel difference of 8 and up to 0.01% of pixels
differing. A looser 0.1% let a one-character title change (about 380 pixels at 1000x700) pass.

Every UI test starts with `HeadlessTestEnvironment.Use ()`. It forces custom window chrome and the
framework's embedded Noto Sans (GAP-003), so a baseline does not depend on the machine's installed
fonts or, on macOS, the native title bar.

Even then, Linux text rasterizes through the system FreeType, so two Linux machines differ by as much as
a real one-character change. The baselines are therefore the renders from the **pinned CI runner
images** (`ubuntu-24.04`, `windows-2025`, `macos-26`). In CI (or with `RECITAL_STRICT_PIXELS=1`) a missing or
mismatched baseline fails the test; elsewhere it is reported as skipped. Either way the render is
written as `<name>.<os>.actual.png` beside the test assembly, and CI uploads it. To accept a render,
download that artifact, review it, and commit it as the baseline. Moving a runner image means
re-accepting its baselines.
