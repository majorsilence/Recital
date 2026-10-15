using System.Drawing;
using Majorsilence.Forms;
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

    /// <summary>
    /// Shows one laid-out page and turns input into page turns. It owns no book state: the reader view
    /// sets <see cref="Page"/> and answers <see cref="TurnRequested"/>.
    /// </summary>
    /// <remarks>
    /// Input: the left and right <see cref="TapZoneFraction"/> of the width turn back and forward, the middle
    /// raises <see cref="CenterTapped"/>; a horizontal drag (mouse or touch) or a touch swipe turns the page;
    /// the wheel, arrow keys, Page Up/Down, Space and Home/End do the obvious. Pages are drawn in logical
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
        private DateTime last_drag_turn;

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

        public event EventHandler<PageTurnEventArgs>? TurnRequested;

        /// <summary>The middle of the page was tapped or clicked: show or hide the reader's chrome.</summary>
        public event EventHandler? CenterTapped;

        public void ShowPage (LayoutPage? newPage, LayoutSettings? settings)
        {
            page = newPage;
            page_settings = settings;
            AccessibleDescription = newPage is null ? null : string.Join (" ", newPage.Lines.Select (l => l.Text.Trim ()));
            Invalidate ();
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

            canvas.Save ();
            canvas.ClipRect (SKRect.Create (Width, Height));
            PageRenderer.Draw (canvas, page, page_settings, ImageSource, ToSk (BackColor), OverrideTextColor ? ToSk (ForeColor) : null);
            canvas.Restore ();
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
            if (e.Button == MouseButtons.Left)
                press = e.Location;
        }

        protected override void OnMouseUp (MouseEventArgs e)
        {
            base.OnMouseUp (e);
            if (e.Button != MouseButtons.Left || press is not { } start)
                return;
            press = null;

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

            var zone = Width * TapZoneFraction;
            if (e.X < zone)
                Turn (PageTurn.Back);
            else if (e.X > Width - zone)
                Turn (PageTurn.Forward);
            else
                CenterTapped?.Invoke (this, EventArgs.Empty);
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
