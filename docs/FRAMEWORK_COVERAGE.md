# Majorsilence.Forms coverage

Every Majorsilence.Forms control and API Recital uses, which backends it was exercised on, and whether it
behaved. Regenerated at the end of each phase (plan section 6). Kept accurate, not flattering.

Legend: ✅ works · ⚠️ works with a gap (see link) · ❌ broken · — not exercised yet

Backends: **Desktop** = Avalonia on Windows/macOS/Linux, **Android** / **iOS** / **Browser** = Avalonia
heads, **Headless** = offscreen Skia (UI tests).

| API | Desktop | Android | iOS | Browser | Headless | Gaps |
|---|---|---|---|---|---|---|
| `Form` (Text, ClientSize, Show, Load, FormClosing with Cancel) | ✅ Linux (starts; not checked by eye) | — | — | — | ✅ | |
| `Application.Run` | ✅ Linux (starts and initialises the library) | | | | | |
| `Application.RunAndroid` | | — | | | | |
| `Application.RunIOS` | | | — | | | [GAP-001](FRAMEWORK_GAPS.md#gap-001-majorsilenceformsavalonia-on-nugetorg-has-no-net100-ios-asset) |
| `Application.RunBrowserAsync` | | | | — | | |
| `AvaloniaPlatformBackend.RaiseBackRequested` / `RegisterAndroidActivity` / `ReportAndroidIntent` | | — | | | | |
| `HeadlessRenderer.Use` / `CapturePng` / `Click` / `MouseDown/Move/Up` / `MouseWheel` / `KeyDown` / `OpenFileResponse` | | | | | ⚠️ | [GAP-002](FRAMEWORK_GAPS.md#gap-002-no-pixel-diffbaseline-helper-for-headless-renders), [GAP-003](FRAMEWORK_GAPS.md#gap-003-headless-renders-depend-on-the-host-os), [GAP-006](FRAMEWORK_GAPS.md#gap-006-headless-cannot-inject-touch-gestures) |
| `Theme.UIFont` / `UIFontBold` | | | | | ⚠️ | [GAP-004](FRAMEWORK_GAPS.md#gap-004-themeuifont-honours-only-the-typefaces-family-name) |
| `PrivateFontCollection.AddMemoryFont (byte[])` | | | | | ✅ | |
| `MF_FORCE_CUSTOM_CHROME` | | | | | ✅ | [GAP-003](FRAMEWORK_GAPS.md#gap-003-headless-renders-depend-on-the-host-os) |
| Custom `Control` (`BookPageView`): `OnPaint` canvas in logical units, `IsInputKey`, `OnKeyDown`, `OnMouseDown/Up`, `OnMouseWheel`, `Focus`, `AccessibleRole/Description` | — | — | — | — | ✅ | |
| `Control.OnSwipe` | — | — | — | — | — (cannot inject) | [GAP-006](FRAMEWORK_GAPS.md#gap-006-headless-cannot-inject-touch-gestures) |
| `UserControl`, `Panel`, `Label`, `Dock`, `SetBounds`, `BringToFront`, re-parenting via `Controls.Add` | — | — | — | — | ✅ | |
| Ambient `BackColor` / `ForeColor` from the form | — | — | — | — | ✅ (inputs and toolbars are not ambient, as in WinForms) | |
| `ToolStrip`, `ToolStripButton` (`Checked`), `ToolStripLabel`, `ToolStripSeparator`, `ToolStripTextBox` | — | — | — | — | ⚠️ | [GAP-009](FRAMEWORK_GAPS.md#gap-009-toolbar-item-text-ignores-forecolor) |
| `ToolStripDropDownButton` + `ToolStripMenuItem` | — | — | — | — | ⚠️ | [GAP-008](FRAMEWORK_GAPS.md#gap-008-toolstripdropdownbutton-text-wraps-onto-two-lines), [GAP-009](FRAMEWORK_GAPS.md#gap-009-toolbar-item-text-ignores-forecolor) |
| `ToolStripProfessionalRenderer.OnRenderItemText` (custom renderer) | — | — | — | — | ✅ | |
| `ToolBar.DefaultItemHoverStyle` (themed hover/checked background) | — | — | — | — | ✅ | |
| `StatusStrip`, `ToolStripStatusLabel` (`Spring`) | — | — | — | — | ✅ | |
| `ListView`: `LargeIcon` with `OwnerDraw`/`DrawItem` (device pixels), `TileSize`, `Details` columns, `ItemActivate` on double-click, `ContextMenuStrip`, `GetItemRect` | — | — | — | — | ✅ | |
| `ContextMenuStrip` | — | — | — | — | — (built, not opened in a test) | |
| `TreeView`: `Nodes`, `TreeNode.Tag`, `ExpandAll`, `NodeMouseClick`, `SelectedNode`, `EnsureVisible`, `TreeNode.Bounds` | — | — | — | — | ✅ | |
| `SplitContainer` (`FixedPanel`, `SplitterDistance`, `Panel1Collapsed`, `SplitterColor`) | — | — | — | — | ⚠️ | [GAP-010](FRAMEWORK_GAPS.md#gap-010-splitcontainerpanel1collapsed-leaves-the-splitter-showing) |
| `FlowLayoutPanel` (TopDown, no wrap), `ComboBox` (DropDownList), `NumericUpDown`, `CheckBox` | — | — | — | — | ✅ | |
| `OpenFileDialog.ShowDialogAsync` (Multiselect, Filter) | — | — | — | — | ✅ | [GAP-005](FRAMEWORK_GAPS.md#gap-005-file-dialogs-return-paths-only) |
| Drag-and-drop file import (`AllowDrop`, `DragEnter`, `DragDrop`, `DataFormats.FileDrop`) | — | — | — | — | — | |
| `Majorsilence.Forms.Timer` (progress-save debounce) | — | — | — | — | ✅ (implicitly: saves happen) | |
| `Graphics` in owner draw: `FillRectangle`, `DrawImage`, `DrawString` with `StringFormat`, `Bitmap (Stream)` | — | — | — | — | ✅ | |
| `WindowsFormsSynchronizationContext` | ✅ (Avalonia has its own context) | | | | ⚠️ | [GAP-007](FRAMEWORK_GAPS.md#gap-007-no-ui-synchronizationcontext-on-the-headless-backend) |

Phases 2 and 3 used no Majorsilence.Forms APIs (EPUB parsing and the layout engine are UI-free by design), so this table is unchanged.

Phase 1 status: every host entry point compiles in CI (Desktop on all three OSes, Android, iOS
simulator, Browser publish: 47 MB wwwroot). Only the Linux desktop host has been run. On-device
checks happen in Phase 4, when there is a UI worth running.

Phase 4: every Headless row above is covered by `Majorsilence.Recital.UI.Tests` (12 tests, four pixel
baselines). On the desktop host the app has been started on Linux (Wayland) and runs, initialising the
library; nobody has yet clicked through it by eye on any OS, so the Desktop column says "—" for the
controls. The Android, iOS and browser heads build but have not been run with the reader UI.

