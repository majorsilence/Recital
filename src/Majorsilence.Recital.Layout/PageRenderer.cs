using Majorsilence.Recital.Layout.Css;
using SkiaSharp;

namespace Majorsilence.Recital.Layout
{
    /// <summary>
    /// Draws a laid-out page with Skia. Used by the reader view and by tests that need to see a page.
    /// Layout never depends on drawing; this only reads the positions layout computed.
    /// </summary>
    public static class PageRenderer
    {
        // RGB to 1 - RGB, alpha kept: CSS filter: invert(100%).
        private static readonly SKColorFilter Invert = SKColorFilter.CreateColorMatrix ([
            -1, 0, 0, 0, 1,
            0, -1, 0, 0, 1,
            0, 0, -1, 0, 1,
            0, 0, 0, 1, 0,
        ]);

        /// <param name="canvas">Target, in CSS px (scale it for device pixels).</param>
        /// <param name="page">The page to draw.</param>
        /// <param name="settings">Supplies the page size and margins.</param>
        /// <param name="images">Decoded images by archive path; a missing one draws as a grey box.</param>
        /// <param name="background">Page colour.</param>
        /// <param name="foreground">Replaces the book's text colours when set (reader themes).</param>
        public static void Draw (SKCanvas canvas, LayoutPage page, LayoutSettings settings, Func<string, SKImage?>? images = null, SKColor? background = null, SKColor? foreground = null)
        {
            using (var fill = new SKPaint { Color = background ?? SKColors.White })
                canvas.DrawRect (0, 0, settings.PageWidth, settings.PageHeight, fill);
            canvas.Save ();
            canvas.Translate (settings.MarginLeft, settings.MarginTop);

            using var paint = new SKPaint { IsAntialias = true };

            foreach (var item in page.Items) {
                switch (item) {
                    case TextLine line:
                        DrawLine (canvas, line, paint, foreground);
                        break;

                    case RuleItem rule:
                        paint.Color = foreground ?? new SKColor (rule.Color);
                        canvas.DrawRect (rule.X, rule.Y, rule.Width, Math.Max (1, rule.Height), paint);
                        break;

                    case ImageItem image:
                        var rect = SKRect.Create (image.X, image.Y, image.Width, image.Height);
                        if (images?.Invoke (image.Path) is { } bitmap) {
                            using var imagePaint = image.Invert ? new SKPaint { ColorFilter = Invert } : null;
                            canvas.DrawImage (bitmap, rect, new SKSamplingOptions (SKFilterMode.Linear, SKMipmapMode.Linear), imagePaint);
                        } else {
                            paint.Color = new SKColor (0xFFDDDDDD);
                            canvas.DrawRect (rect, paint);
                        }
                        break;
                }
            }

            canvas.Restore ();
        }

        private static void DrawLine (SKCanvas canvas, TextLine line, SKPaint paint, SKColor? foreground)
        {
            foreach (var run in line.Runs) {
                using var font = new SKFont (run.Face.Typeface, run.FontSize) {
                    Subpixel = true,
                    Edging = SKFontEdging.Antialias,
                    Hinting = SKFontHinting.None,
                };

                var baseline = line.Y + line.Baseline - run.BaselineShift;

                using var builder = new SKTextBlobBuilder ();
                var buffer = builder.AllocatePositionedRun (font, run.Glyphs.Length);
                var glyphs = buffer.Glyphs;
                var positions = buffer.Positions;
                for (var i = 0; i < run.Glyphs.Length; i++) {
                    glyphs[i] = run.Glyphs[i];
                    positions[i] = new SKPoint (line.X + run.X[i], baseline);
                }

                using var blob = builder.Build ();
                paint.Color = foreground ?? new SKColor (run.Color);
                if (blob is not null)
                    canvas.DrawText (blob, 0, 0, paint);

                if (run.Decoration != TextDecoration.None && run.Glyphs.Length > 0) {
                    var left = line.X + run.X[0];
                    var right = line.X + run.EndX;
                    var thickness = Math.Max (1, run.FontSize / 16);
                    if (run.Decoration.HasFlag (TextDecoration.Underline))
                        canvas.DrawRect (left, baseline + run.FontSize * 0.12f, right - left, thickness, paint);
                    if (run.Decoration.HasFlag (TextDecoration.LineThrough))
                        canvas.DrawRect (left, baseline - run.FontSize * 0.3f, right - left, thickness, paint);
                }
            }
        }

        /// <summary>Renders a page to PNG bytes at <paramref name="scale"/> device pixels per CSS px.</summary>
        public static byte[] RenderPng (LayoutPage page, LayoutSettings settings, float scale = 1, Func<string, SKImage?>? images = null)
        {
            var info = new SKImageInfo ((int)Math.Ceiling (settings.PageWidth * scale), (int)Math.Ceiling (settings.PageHeight * scale));
            using var surface = SKSurface.Create (info);
            surface.Canvas.Scale (scale);
            Draw (surface.Canvas, page, settings, images);
            using var image = surface.Snapshot ();
            using var data = image.Encode (SKEncodedImageFormat.Png, 100);
            return data.ToArray ();
        }
    }
}
