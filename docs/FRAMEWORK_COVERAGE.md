# Majorsilence.Forms coverage

Every Majorsilence.Forms control and API Recital uses, which backends it was exercised on, and whether it
behaved. Regenerated at the end of each phase (plan section 6). Kept accurate, not flattering.

Legend: ✅ works · ⚠️ works with a gap (see link) · ❌ broken · — not exercised yet

Backends: **Desktop** = Avalonia on Windows/macOS/Linux, **Android** / **iOS** / **Browser** = Avalonia
heads, **Headless** = offscreen Skia (UI tests).

| API | Desktop | Android | iOS | Browser | Headless | Gaps |
|---|---|---|---|---|---|---|
| `Form` (Text, ClientSize, Show, Load, FormClosing with Cancel, FormClosed) | ✅ Linux (starts; not checked by eye) | — | — | — | ✅ | |
| `Application.Run` | ✅ Linux (starts and initialises the library) | | | | | |
| `Application.RunAndroid` | | — | | | | |
| `Application.RunIOS` | | | — | | | [GAP-001](FRAMEWORK_GAPS.md#gap-001-majorsilenceformsavalonia-on-nugetorg-has-no-net100-ios-asset) (fixed) |
| `Application.RunBrowserAsync` | | | | — | | |
| `AvaloniaPlatformBackend.RaiseBackRequested` / `RegisterAndroidActivity` / `ReportAndroidIntent` | | — | | | | |
| `HeadlessRenderer.Use` / `UseDeterministic` / `UseSynchronizationContext` / `CapturePng` / `Click` / `MouseDown/Move/Up` / `MouseWheel` / `KeyDown` / `Swipe` | | | | | ✅ | [GAP-003](FRAMEWORK_GAPS.md#gap-003-headless-renders-depend-on-the-host-os), [GAP-006](FRAMEWORK_GAPS.md#gap-006-headless-cannot-inject-touch-gestures), [GAP-007](FRAMEWORK_GAPS.md#gap-007-no-ui-synchronizationcontext-on-the-headless-backend) (all fixed) |
| `HeadlessRenderer.OpenFileResponse` / `OpenFilePickResponse` / `OpenFolderResponse` | | | | | ✅ | |
| `PixelComparison.Compare` / `IsWithin` / `CreateDiffPng` | | | | | ✅ | [GAP-002](FRAMEWORK_GAPS.md#gap-002-no-pixel-diffbaseline-helper-for-headless-renders) (fixed) |
| Custom `Control` (`BookPageView`): `OnPaint` canvas in logical units, `IsInputKey`, `OnKeyDown`, `OnMouseDown/Up`, `OnMouseWheel`, `OnSwipe`, `Focus`, `AccessibleRole/Description` | — | — | — | — | ✅ | |
| `Control.BeginInvoke` from a worker thread (sync rounds) | — | — | — | — | ✅ | |
| `UserControl`, `Panel`, `Label`, `Dock`, `SetBounds`, `BringToFront`, re-parenting via `Controls.Add` | — | — | — | — | ✅ | |
| Ambient `BackColor` / `ForeColor` from the form | — | — | — | — | ✅ (inputs and toolbars are not ambient, as in WinForms) | |
| `ToolStrip`, `ToolStripButton` (`Checked`), `ToolStripLabel`, `ToolStripSeparator`, `ToolStripTextBox`, item `ForeColor`, `Alignment = Right` | — | — | — | — | ✅ | [GAP-009](FRAMEWORK_GAPS.md#gap-009-toolbar-item-text-ignores-forecolor) (fixed) |
| `ToolStripDropDownButton` + `ToolStripMenuItem` (`Checked`, `Enabled`) | — | — | — | — | ✅ | [GAP-008](FRAMEWORK_GAPS.md#gap-008-toolstripdropdownbutton-text-wraps-onto-two-lines) (fixed) |
| `ToolBar.DefaultItemHoverStyle` (themed hover/checked background) | — | — | — | — | ✅ | |
| `StatusStrip`, `ToolStripStatusLabel` (`Spring`, `TextAlign`) | — | — | — | — | ✅ | |
| `ListView`: `LargeIcon` with `OwnerDraw`/`DrawItem` (device pixels), `TileSize`, `Details` columns, `ItemActivate` on double-click, `ContextMenuStrip`, `GetItemRect` | — | — | — | — | ✅ | |
| `ContextMenuStrip` | — | — | — | — | — (built, not opened in a test) | |
| `TreeView`: `Nodes`, `TreeNode.Tag`, `ExpandAll`, `NodeMouseClick`, `SelectedNode`, `EnsureVisible`, `TreeNode.Bounds` | — | — | — | — | ✅ | |
| `SplitContainer` (`FixedPanel`, `SplitterDistance`, `Panel1Collapsed`, `SplitterColor`) | — | — | — | — | ✅ | [GAP-010](FRAMEWORK_GAPS.md#gap-010-splitcontainerpanel1collapsed-leaves-the-splitter-showing) (fixed) |
| `FlowLayoutPanel` (TopDown, no wrap), `ComboBox` (DropDownList), `NumericUpDown`, `CheckBox` | — | — | — | — | ✅ | |
| `OpenFileDialog.ShowDialogAsync` (Multiselect, Filter), `SelectedFiles`, `PickedFile.OpenReadAsync` | — | — | — | — | ✅ | [GAP-005](FRAMEWORK_GAPS.md#gap-005-file-dialogs-return-paths-only) (fixed) |
| `FolderBrowserDialog.ShowDialogAsync` (`Description`, `ShowNewFolderButton`, `SelectedPath`) | — | | | | ✅ | |
| `Control.OnMouseMove` (drag selection), `OnLongPress` | — | — | — | — | ✅ | |
| `SaveFileDialog.ShowDialogAsync` (`Filter`, `DefaultExt`, `FileName`) | — | | | | ✅ (Headless `SaveFileResponse`) | [GAP-012](FRAMEWORK_GAPS.md#gap-012-savefiledialog-returns-a-path-only) |
| `Clipboard.SetTextAsync` | — | — | — | — | — (called, not checked) | |
| `TextBox` (`Multiline`, `AcceptsReturn`, `WordWrap`, `PlaceholderText`), `Button` | — | — | — | — | ✅ | |
| `TreeNode.ToolTipText`, `TreeView.ContextMenuStrip` | — | — | — | — | — (built, not opened in a test) | |
| `TextBox.UseSystemPasswordChar`, `Label.MaximumSize` (wrapping) | — | | | | ✅ | |
| `ListView` `Details` with `MultiSelect`, `SelectedIndexChanged` (import browser) | — | — | — | — | ✅ | |
| `ToolStripMenuItem` `Enabled`/`Checked`/`ToolTipText` per state (Sync menu) | — | — | — | — | ✅ | |
| Drag-and-drop file import (`AllowDrop`, `DragEnter`, `DragDrop`, `DataFormats.FileDrop`) | — | — | — | — | — | |
| `Majorsilence.Forms.Timer` (progress-save debounce) | — | — | — | — | ✅ (implicitly: saves happen) | |
| `Graphics` in owner draw: `FillRectangle`, `DrawImage`, `DrawString` with `StringFormat`, `Bitmap (Stream)` | — | — | — | — | ✅ | |

Since 26.11.6 Recital no longer uses `Theme.UIFont`, `PrivateFontCollection`, `Application.SetDefaultFont`,
`MF_FORCE_CUSTOM_CHROME` or a custom `ToolStripProfessionalRenderer`: `HeadlessRenderer.UseDeterministic`
and the framework's own fixes replaced them (GAP-003, GAP-004, GAP-008, GAP-009, GAP-011).

Phase 5: the sync menu (a right-aligned `ToolStripDropDownButton`), `FolderBrowserDialog` (Headless
`OpenFolderResponse` drives it in `SyncTests`) and `Control.BeginInvoke` from the sync loop's thread
are new. The menu is shown only on the desktop, so the folder dialog has no mobile or browser column.
No new gaps.

Phase 6: selection gestures (`OnMouseMove` after a hold, `OnLongPress`), the floating highlight bar
(a `ToolStrip` placed over the page), the note editor (multiline `TextBox`), the Notes panel
(`TreeView`), `SaveFileDialog` for Markdown export and `Clipboard.SetTextAsync`. One new gap: GAP-012,
saves on phones and in the browser.

Phase 7: the Sync menu grew one item per kind of sync home. The WebDAV form (password `TextBox`) and
the import browser (`ListView` with multi-select) are in-window panels; Headless tests drive both. No
new gaps.

Phases 2 and 3 used no Majorsilence.Forms APIs (EPUB parsing and the layout engine are UI-free by design), so this table is unchanged.

Phase 1 status: every host entry point compiles in CI (Desktop on all three OSes, Android, iOS
simulator, Browser publish: 47 MB wwwroot). Only the Linux desktop host has been run. On-device
checks happen in Phase 4, when there is a UI worth running.

Phase 4: every Headless row above is covered by `Majorsilence.Recital.UI.Tests` (12 tests, four pixel
baselines). On the desktop host the app has been started on Linux (Wayland) and runs, initialising the
library; nobody has yet clicked through it by eye on any OS, so the Desktop column says "—" for the
controls. The Android, iOS and browser heads build but have not been run with the reader UI.

