using System.Drawing;
using Majorsilence.Forms.Drawing;
using Majorsilence.Forms.Drawing.Drawing2D;

namespace Majorsilence.Recital.UI
{
    /// <summary>
    /// Small monochrome line icons for toolbar buttons, drawn in code (a 24x24 view box, a 2-unit stroke,
    /// round caps and joins — the Feather/Lucide convention) rather than shipped as image assets, so every
    /// icon is tinted to the reader theme's chrome colour at the size the strip asks for.
    /// </summary>
    internal static class ToolbarIcons
    {
        public enum Kind
        {
            NavBack, Contents, Notes, Bookmark, Listen, Theme, Import, Sort, Grid, List,
            Play, Pause, SkipBack, SkipForward, Stop, Close, Language,
        }

        private const float ViewBox = 24f;

        /// <summary>Draws <paramref name="kind"/> tinted <paramref name="color"/>, as a bitmap <paramref name="pixelSize"/>
        /// square. Rendered well above the strip's <c>ImageScalingSize</c> (typically 16-18) so it downscales crisply
        /// on dense phone screens.</summary>
        public static Image Draw (Kind kind, Color color, int pixelSize = 48)
        {
            var bitmap = new Bitmap (pixelSize, pixelSize);
            using var g = Graphics.FromImage (bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform (pixelSize / ViewBox, pixelSize / ViewBox);

            using var stroke = new Pen (color, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var fill = new SolidBrush (color);

            switch (kind) {
                case Kind.NavBack:
                    g.DrawLines (stroke, new PointF[] { new (15, 4), new (9, 12), new (15, 20) });
                    break;

                case Kind.Contents:
                    Dot (g, fill, 4, 6); g.DrawLine (stroke, 8, 6, 20, 6);
                    Dot (g, fill, 4, 12); g.DrawLine (stroke, 8, 12, 20, 12);
                    Dot (g, fill, 4, 18); g.DrawLine (stroke, 8, 18, 17, 18);
                    break;

                case Kind.Notes:
                case Kind.Bookmark:
                    var ribbon = new PointF[] { new (6, 3), new (18, 3), new (18, 21), new (12, 16), new (6, 21) };
                    if (kind == Kind.Bookmark)
                        g.FillPolygon (fill, ribbon);
                    else
                        g.DrawPolygon (stroke, ribbon);
                    break;

                case Kind.Listen:
                    g.FillPolygon (fill, new PointF[] { new (3, 9), new (8, 9), new (13, 4), new (13, 20), new (8, 15), new (3, 15) });
                    g.DrawArc (stroke, 13, 7, 8, 10, -55, 110);
                    g.DrawArc (stroke, 13, 3, 14, 18, -45, 90);
                    break;

                case Kind.Theme:
                    g.DrawEllipse (stroke, 4, 4, 16, 16);
                    using (var half = new GraphicsPath ()) {
                        half.AddArc (4, 4, 16, 16, 90, 180);
                        half.CloseFigure ();
                        g.FillPath (fill, half);
                    }
                    break;

                case Kind.Language:
                    g.DrawEllipse (stroke, 4, 4, 16, 16);
                    g.DrawEllipse (stroke, 8, 4, 8, 16);
                    g.DrawLine (stroke, 5, 9, 19, 9);
                    g.DrawLine (stroke, 5, 15, 19, 15);
                    break;

                case Kind.Import:
                    g.DrawLine (stroke, 12, 3, 12, 14);
                    g.DrawLines (stroke, new PointF[] { new (7, 9), new (12, 14), new (17, 9) });
                    g.DrawLines (stroke, new PointF[] { new (4, 15), new (4, 20), new (20, 20), new (20, 15) });
                    break;

                case Kind.Sort:
                    g.DrawLine (stroke, 8, 19, 8, 5);
                    g.DrawLines (stroke, new PointF[] { new (5, 8), new (8, 5), new (11, 8) });
                    g.DrawLine (stroke, 16, 5, 16, 19);
                    g.DrawLines (stroke, new PointF[] { new (13, 16), new (16, 19), new (19, 16) });
                    break;

                case Kind.Grid:
                    g.FillRectangle (fill, 4, 4, 7, 7);
                    g.FillRectangle (fill, 13, 4, 7, 7);
                    g.FillRectangle (fill, 4, 13, 7, 7);
                    g.FillRectangle (fill, 13, 13, 7, 7);
                    break;

                case Kind.List:
                    Dot (g, fill, 5, 6); g.DrawLine (stroke, 9, 6, 20, 6);
                    Dot (g, fill, 5, 12); g.DrawLine (stroke, 9, 12, 20, 12);
                    Dot (g, fill, 5, 18); g.DrawLine (stroke, 9, 18, 20, 18);
                    break;

                case Kind.Play:
                    g.FillPolygon (fill, new PointF[] { new (8, 4), new (8, 20), new (20, 12) });
                    break;

                case Kind.Pause:
                    g.FillRectangle (fill, 7, 4, 4, 16);
                    g.FillRectangle (fill, 14, 4, 4, 16);
                    break;

                case Kind.SkipBack:
                    g.FillRectangle (fill, 5, 4, 3, 16);
                    g.FillPolygon (fill, new PointF[] { new (18, 4), new (18, 20), new (9, 12) });
                    break;

                case Kind.SkipForward:
                    g.FillPolygon (fill, new PointF[] { new (6, 4), new (6, 20), new (15, 12) });
                    g.FillRectangle (fill, 16, 4, 3, 16);
                    break;

                case Kind.Stop:
                    using (var square = RoundedRect (6, 6, 12, 12, 2))
                        g.FillPath (fill, square);
                    break;

                case Kind.Close:
                    g.DrawLine (stroke, 6, 6, 18, 18);
                    g.DrawLine (stroke, 18, 6, 6, 18);
                    break;
            }

            return bitmap;
        }

        private static void Dot (Graphics g, Brush fill, float cx, float cy) => g.FillEllipse (fill, cx - 1.5f, cy - 1.5f, 3, 3);

        private static GraphicsPath RoundedRect (float x, float y, float width, float height, float radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath ();
            path.AddArc (x, y, d, d, 180, 90);
            path.AddArc (x + width - d, y, d, d, 270, 90);
            path.AddArc (x + width - d, y + height - d, d, d, 0, 90);
            path.AddArc (x, y + height - d, d, d, 90, 90);
            path.CloseFigure ();
            return path;
        }
    }
}
