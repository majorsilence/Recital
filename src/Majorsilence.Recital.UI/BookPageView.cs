using System.Drawing;
using Majorsilence.Forms;
using Majorsilence.Recital.Epub;
using Majorsilence.Recital.Layout;
using SkiaSharp;

namespace Majorsilence.Recital.UI
{
    public enum PageTurn
    {
        Back,
        Forward,
        ChapterStart,
        ChapterEnd,
    }

    public sealed class PageTurnEventArgs (PageTurn turn) : EventArgs
    {
        public PageTurn Turn { get; } = turn;
    }

    /// <summary>A highlight drawn on the page; <see cref="Tag"/> identifies it to whoever set it.</summary>
    public sealed record PageHighlight (object Tag, IReadOnlyList<PageRect> Rects, uint Argb, bool HasNote);

    /// <param name="Bounds">Where it is, in the view's coordinates (for placing a toolbar beside it).</param>
    public sealed class PageSelectionEventArgs (DomPosition start, DomPosition end, Rectangle bounds) : EventArgs
    {
        public DomPosition Start { get; } = start;
        public DomPosition End { get; } = end;
        public Rectangle Bounds { get; } = bounds;
    }

    public sealed class LinkTappedEventArgs (PageLink link) : EventArgs
    {
        public PageLink Link { get; } = link;
    }

    public sealed class HighlightTappedEventArgs (PageHighlight highlight, Rectangle bounds) : EventArgs
    {
        public PageHighlight Highlight { get; } = highlight;
        public Rectangle Bounds { get; } = bounds;
    }

    /// <summary>
    /// Shows one laid-out page and turns input into page turns. It owns no book state: the reader view
    /// sets <see cref="Page"/> and answers <see cref="TurnRequested"/>.
    /// </summary>
    /// <remarks>
    /// Input: the left and right <see cref="TapZoneFraction"/> of the width turn back and forward, the middle
    /// raises <see cref="CenterTapped"/>; a horizontal drag (mouse or touch) or a touch swipe turns the page;
    /// the wheel, arrow keys, Page Up/Down, Space and Home/End do the obvious. Text is selected by pressing,
    /// holding still for <see cref="SelectHoldTime"/> and then dragging, or by a long press (which selects
    /// the word, then dragging extends it); tapping a highlight raises <see cref="HighlightTapped"/> and tapping a link <see cref="LinkTapped"/>. Pages are drawn in logical
    /// units, which are the layout's CSS pixels, so the layout's geometry is the page's geometry at any
    /// display scale.
    /// </remarks>
    public class BookPageView : Control
    {
        /// <summary>A drag at least this far (logical px) sideways, and more sideways than vertical, turns the page.</summary>
        public const int DragThreshold = 40;

        // A touch drag reaches us twice: as pointer events (our own drag test) and as the backend's swipe.
        private static readonly TimeSpan SwipeDedupeWindow = TimeSpan.FromMilliseconds (400);

        private LayoutPage? page;
        private LayoutSettings? page_settings;
        private Point? press;
        private DateTime press_time;
        private bool press_dragging;
        private bool selecting;
        private DomPosition? select_anchor;
        private DomPosition? select_focus;
        private DateTime last_drag_turn;
        private IReadOnlyList<PageHighlight> highlights = [];
        private bool bookmarked;

        public BookPageView ()
        {
            TabStop = true;
            AccessibleRole = AccessibleRole.Document;
            AccessibleName = "Book page";
        }

        /// <summary>The page shown; null draws an empty page.</summary>
        public LayoutPage? Page => page;

        /// <summary>The settings <see cref="Page"/> was laid out with (its size and margins).</summary>
        public LayoutSettings? PageSettings => page_settings;

        /// <summary>Draw all text in <see cref="Control.ForeColor"/> instead of the book's colours (dark themes).</summary>
        public bool OverrideTextColor {
            get;
            set {
                field = value;
                Invalidate ();
            }
        }

        /// <summary>Decoded images by archive path. Returning null draws a placeholder (for example while loading).</summary>
        public Func<string, SKImage?>? ImageSource { get; set; }

        /// <summary>The share of the width, at each side, that turns the page when tapped.</summary>
        public float TapZoneFraction { get; set; } = 0.3f;

        /// <summary>How long a press must stay still before dragging selects text instead of turning the page.</summary>
        public TimeSpan SelectHoldTime { get; set; } = TimeSpan.FromMilliseconds (350);

        /// <summary>The highlights drawn under the text.</summary>
        public IReadOnlyList<PageHighlight> Highlights {
            get => highlights;
            set {
                highlights = value;
                Invalidate ();
            }
        }

        /// <summary>The sentence and word being read aloud, drawn under the text.</summary>
        public IReadOnlyList<PageMark> Spoken {
            get;
            set {
                field = value;
                Invalidate ();
            }
        } = [];

        /// <summary>Draws a bookmark ribbon in the top corner.</summary>
        public bool Bookmarked {
            get => bookmarked;
            set {
                bookmarked = value;
                Invalidate ();
            }
        }

        /// <summary>The selected passage, in document order, or null.</summary>
        public (DomPosition Start, DomPosition End)? Selection
            => select_anchor is { } a && select_focus is { } f
                ? DomOrder.Compare (a, f) <= 0 ? (a, f) : (f, a)
                : null;

        /// <summary>Colour of the selection (0xAARRGGBB, translucent).</summary>
        public uint SelectionArgb { get; set; } = 0x553399FF;

        public event EventHandler<PageTurnEventArgs>? TurnRequested;

        /// <summary>A selection was finished (the press lifted).</summary>
        public event EventHandler<PageSelectionEventArgs>? SelectionMade;

        /// <summary>The selection was dismissed by a tap or a page change.</summary>
        public event EventHandler? SelectionCleared;

        public event EventHandler<HighlightTappedEventArgs>? HighlightTapped;

        /// <summary>A link's text was tapped or clicked (instead of turning the page).</summary>
        public event EventHandler<LinkTappedEventArgs>? LinkTapped;

        /// <summary>The middle of the page was tapped or clicked: show or hide the reader's chrome.</summary>
        public event EventHandler? CenterTapped;

        public void ShowPage (LayoutPage? newPage, LayoutSettings? settings)
        {
            if (newPage != page && Selection is not null)
                ClearSelection ();
            page = newPage;
            page_settings = settings;
            AccessibleDescription = newPage is null ? null : string.Join (" ", newPage.Lines.Select (l => l.Text.Trim ()));
            Invalidate ();
        }

        /// <summary>Selects the passage between two positions on the page (in either order) and reports it.</summary>
        public void Select (DomPosition anchor, DomPosition focus)
        {
            select_anchor = anchor;
            select_focus = focus;
            selecting = false;
            Invalidate ();
            RaiseSelectionMade ();
        }

        public void ClearSelection ()
        {
            var had = select_anchor is not null;
            select_anchor = select_focus = null;
            selecting = false;
            Invalidate ();
            if (had)
                SelectionCleared?.Invoke (this, EventArgs.Empty);
        }

        /// <summary>The rectangles (view coordinates) covering a passage on the page.</summary>
        public IReadOnlyList<Rectangle> RectsOf (DomPosition start, DomPosition end)
            => page is null || page_settings is null ? [] : page.TextRects (start, end).Select (ToView).ToList ();

        private Rectangle ToView (PageRect r)
            => Rectangle.FromLTRB ((int)Math.Floor (r.X + page_settings!.MarginLeft), (int)Math.Floor (r.Y + page_settings.MarginTop),
                (int)Math.Ceiling (r.X + r.Width + page_settings.MarginLeft), (int)Math.Ceiling (r.Y + r.Height + page_settings.MarginTop));

        private static Rectangle Union (IEnumerable<Rectangle> rects)
            => rects.Aggregate (Rectangle.Empty, (a, b) => a.IsEmpty ? b : Rectangle.Union (a, b));

        private DomPosition? HitTest (Point p)
            => page is null || page_settings is null ? null : page.HitTest (p.X - page_settings.MarginLeft, p.Y - page_settings.MarginTop);

        private void RaiseSelectionMade ()
        {
            if (Selection is not var (start, end))
                return;
            if (DomOrder.Compare (start, end) == 0) {
                ClearSelection ();
                return;
            }
            SelectionMade?.Invoke (this, new PageSelectionEventArgs (start, end, Union (RectsOf (start, end))));
        }

        protected virtual void OnTurnRequested (PageTurnEventArgs e) => TurnRequested?.Invoke (this, e);

        private void Turn (PageTurn turn) => OnTurnRequested (new PageTurnEventArgs (turn));

        protected override void OnPaint (PaintEventArgs e)
        {
            base.OnPaint (e);

            var canvas = e.Canvas;
            using (var background = new SKPaint { Color = ToSk (BackColor) })
                canvas.DrawRect (0, 0, Width, Height, background);

            if (page is null || page_settings is null)
                return;

            var marks = new List<PageMark> ();
            foreach (var highlight in highlights)
                foreach (var rect in highlight.Rects)
                    marks.Add (new PageMark (rect, highlight.Argb, highlight.HasNote));
            marks.AddRange (Spoken);
            if (Selection is var (start, end))
                foreach (var rect in page.TextRects (start, end))
                    marks.Add (new PageMark (rect, SelectionArgb));

            canvas.Save ();
            canvas.ClipRect (SKRect.Create (Width, Height));
            PageRenderer.Draw (canvas, page, page_settings, ImageSource, ToSk (BackColor), OverrideTextColor ? ToSk (ForeColor) : null, marks);
            if (bookmarked)
                DrawRibbon (canvas);
            canvas.Restore ();
        }

        private void DrawRibbon (SKCanvas canvas)
        {
            var width = 18f;
            var height = 30f;
            var right = Width - Math.Max (12, page_settings!.MarginRight / 2);
            using var path = new SKPath ();
            path.MoveTo (right - width, 0);
            path.LineTo (right, 0);
            path.LineTo (right, height);
            path.LineTo (right - width / 2, height - 7);
            path.LineTo (right - width, height);
            path.Close ();
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor (0xFFC0392B) };
            canvas.DrawPath (path, paint);
        }

        private static SKColor ToSk (Color color) => new (color.R, color.G, color.B, color.A);

        protected override bool IsInputKey (Keys keyData)
            => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey (keyData);

        protected override void OnKeyDown (KeyEventArgs e)
        {
            base.OnKeyDown (e);
            if (e.Handled)
                return;

            PageTurn? turn = e.KeyCode switch {
                Keys.Right or Keys.Down or Keys.PageDown or Keys.Next => PageTurn.Forward,
                Keys.Space => e.Shift ? PageTurn.Back : PageTurn.Forward,
                Keys.Left or Keys.Up or Keys.PageUp or Keys.Prior or Keys.Back => PageTurn.Back,
                Keys.Home => PageTurn.ChapterStart,
                Keys.End => PageTurn.ChapterEnd,
                _ => null,
            };

            if (e.KeyCode == Keys.Escape && select_anchor is not null) {
                e.Handled = true;
                ClearSelection ();
                return;
            }

            if (turn is { } t) {
                e.Handled = true;
                Turn (t);
            }
        }

        protected override void OnMouseDown (MouseEventArgs e)
        {
            base.OnMouseDown (e);
            if (CanFocus)
                Focus ();
            if (e.Button == MouseButtons.Left) {
                press = e.Location;
                press_time = DateTime.UtcNow;
                press_dragging = false;
            }
        }

        protected override void OnMouseMove (MouseEventArgs e)
        {
            base.OnMouseMove (e);
            if (press is not { } start || !e.Button.HasFlag (MouseButtons.Left))
                return;

            if (selecting) {
                if (HitTest (e.Location) is { } focus) {
                    select_focus = focus;
                    Invalidate ();
                }
                return;
            }

            if (press_dragging || Math.Abs (e.X - start.X) + Math.Abs (e.Y - start.Y) < 4)
                return;
            // Moving: held still long enough first, it selects; otherwise it is a page drag.
            if (DateTime.UtcNow - press_time >= SelectHoldTime && HitTest (start) is { } anchor) {
                selecting = true;
                select_anchor = anchor;
                select_focus = HitTest (e.Location) ?? anchor;
                Invalidate ();
            } else {
                press_dragging = true;
            }
        }

        protected override void OnLongPress (LongPressEventArgs e)
        {
            base.OnLongPress (e);
            if (page is null || page_settings is null || page.WordAt (e.X - page_settings.MarginLeft, e.Y - page_settings.MarginTop) is not var (start, end))
                return;
            select_anchor = start;
            select_focus = end;
            Invalidate ();
            if (press is null) {
                // A long press with no pointer still down (some backends): the word is the selection.
                RaiseSelectionMade ();
            } else {
                // The finger is still down: dragging extends from the word, lifting finishes it.
                selecting = true;
            }
        }

        protected override void OnMouseUp (MouseEventArgs e)
        {
            base.OnMouseUp (e);
            if (e.Button != MouseButtons.Left || press is not { } start)
                return;
            press = null;

            if (selecting) {
                selecting = false;
                RaiseSelectionMade ();
                return;
            }

            var dx = e.X - start.X;
            var dy = e.Y - start.Y;

            if (Math.Abs (dx) >= DragThreshold && Math.Abs (dx) > Math.Abs (dy)) {
                last_drag_turn = DateTime.UtcNow;
                // Dragging the page leftwards brings the next page in, as on paper.
                Turn (dx < 0 ? PageTurn.Forward : PageTurn.Back);
                return;
            }

            if (Math.Abs (dx) >= DragThreshold || Math.Abs (dy) >= DragThreshold)
                return;

            // A tap with text selected dismisses the selection; a tap on a highlight opens it.
            if (select_anchor is not null) {
                ClearSelection ();
                return;
            }
            if (TappedHighlight (e.Location) is { } tapped) {
                HighlightTapped?.Invoke (this, new HighlightTappedEventArgs (tapped, Union (tapped.Rects.Select (ToView))));
                return;
            }
            if (LinkTapped is not null && page is not null && page_settings is not null
                && page.LinkAt (e.X - page_settings.MarginLeft, e.Y - page_settings.MarginTop) is { } link) {
                LinkTapped (this, new LinkTappedEventArgs (link));
                return;
            }

            var zone = Width * TapZoneFraction;
            if (e.X < zone)
                Turn (PageTurn.Back);
            else if (e.X > Width - zone)
                Turn (PageTurn.Forward);
            else
                CenterTapped?.Invoke (this, EventArgs.Empty);
        }

        private PageHighlight? TappedHighlight (Point p)
        {
            if (page_settings is null)
                return null;
            foreach (var highlight in highlights)
                foreach (var rect in highlight.Rects)
                    if (Rectangle.Inflate (ToView (rect), 2, 2).Contains (p))
                        return highlight;
            return null;
        }

        protected override void OnMouseWheel (MouseEventArgs e)
        {
            base.OnMouseWheel (e);
            if (e.Delta != 0)
                Turn (e.Delta < 0 ? PageTurn.Forward : PageTurn.Back);
        }

        protected override void OnSwipe (SwipeGestureEventArgs e)
        {
            base.OnSwipe (e);
            if (DateTime.UtcNow - last_drag_turn < SwipeDedupeWindow)
                return;
            if (e.Direction is SwipeDirection.Left)
                Turn (PageTurn.Forward);
            else if (e.Direction is SwipeDirection.Right)
                Turn (PageTurn.Back);
        }
    }
}
