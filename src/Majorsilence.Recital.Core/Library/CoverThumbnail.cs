using SkiaSharp;

namespace Majorsilence.Recital.Core.Library
{
    /// <summary>Makes the small JPEG stored in <c>covers/</c> on import.</summary>
    public static class CoverThumbnail
    {
        public const int MaxHeight = 360;

        /// <summary>A JPEG no taller than <see cref="MaxHeight"/>, or null when the image cannot be decoded.</summary>
        public static byte[]? Create (byte[] image)
        {
            using var source = SKBitmap.Decode (image);
            if (source is null || source.Width <= 0 || source.Height <= 0)
                return null;

            var scale = Math.Min (1f, MaxHeight / (float)source.Height);
            var info = new SKImageInfo (Math.Max (1, (int)Math.Round (source.Width * scale)), Math.Max (1, (int)Math.Round (source.Height * scale)), SKColorType.Rgba8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create (info);
            // JPEG has no alpha: transparent covers go on white.
            surface.Canvas.Clear (SKColors.White);
            using (var sourceImage = SKImage.FromBitmap (source))
                surface.Canvas.DrawImage (sourceImage, SKRect.Create (info.Width, info.Height), new SKSamplingOptions (SKCubicResampler.Mitchell));

            using var snapshot = surface.Snapshot ();
            using var data = snapshot.Encode (SKEncodedImageFormat.Jpeg, 85);
            return data?.ToArray ();
        }
    }
}
