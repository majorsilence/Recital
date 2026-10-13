using SkiaSharp;
using Xunit;

namespace Majorsilence.Recital.UI.Tests
{
    /// <summary>
    /// Compares a Headless render against a committed PNG under Baselines/, one per OS
    /// (&lt;name&gt;.&lt;linux|windows|macos&gt;.png): even with the same font file, each OS's Skia anti-aliases
    /// text differently (about 200 pixels on the title bar alone).
    ///
    /// A missing or mismatched baseline fails the test and writes the actual render next to the test
    /// output as &lt;name&gt;.&lt;os&gt;.actual.png, which CI uploads. Set RECITAL_UPDATE_BASELINES=1 to (re)write
    /// this OS's baseline in the source tree.
    /// </summary>
    internal static class PixelBaseline
    {
        // Per-channel slack absorbs anti-aliasing noise. The pixel allowance has to stay small: adding one
        // character to the form's title changes about 380 pixels of a 1000x700 render.
        private const int ChannelTolerance = 8;
        private const double MaxDifferingFraction = 0.0001;

        public static void AssertMatches (string name, byte[] actualPng)
        {
            var fileName = $"{name}.{OsName ()}.png";
            var sourcePath = Path.Combine (SourceDirectory (), "Baselines", fileName);

            if (Environment.GetEnvironmentVariable ("RECITAL_UPDATE_BASELINES") == "1") {
                Directory.CreateDirectory (Path.GetDirectoryName (sourcePath)!);
                File.WriteAllBytes (sourcePath, actualPng);
                return;
            }

            var baselinePath = Path.Combine (AppContext.BaseDirectory, "Baselines", fileName);

            if (!File.Exists (baselinePath))
                Assert.Fail ($"No baseline {fileName}. Review {WriteActual (fileName, actualPng)} and commit it as {sourcePath}.");

            using var expected = SKBitmap.Decode (baselinePath);
            using var actual = SKBitmap.Decode (actualPng);

            var differing = CountDifferingPixels (expected, actual);
            var allowed = (int)(expected.Width * expected.Height * MaxDifferingFraction);

            if (differing > allowed)
                Assert.Fail ($"{fileName}: {differing} pixels differ (allowed {allowed}). Actual render: {WriteActual (fileName, actualPng)}");
        }

        private static string WriteActual (string fileName, byte[] png)
        {
            var path = Path.Combine (AppContext.BaseDirectory, Path.ChangeExtension (fileName, ".actual.png"));
            File.WriteAllBytes (path, png);
            return path;
        }

        private static string OsName ()
            => OperatingSystem.IsWindows () ? "windows" : OperatingSystem.IsMacOS () ? "macos" : "linux";

        private static int CountDifferingPixels (SKBitmap expected, SKBitmap actual)
        {
            Assert.Equal ((expected.Width, expected.Height), (actual.Width, actual.Height));

            var differing = 0;

            for (var y = 0; y < expected.Height; y++)
                for (var x = 0; x < expected.Width; x++)
                    if (!Close (expected.GetPixel (x, y), actual.GetPixel (x, y)))
                        differing++;

            return differing;
        }

        private static bool Close (SKColor a, SKColor b)
            => Math.Abs (a.Red - b.Red) <= ChannelTolerance
            && Math.Abs (a.Green - b.Green) <= ChannelTolerance
            && Math.Abs (a.Blue - b.Blue) <= ChannelTolerance
            && Math.Abs (a.Alpha - b.Alpha) <= ChannelTolerance;

        private static string SourceDirectory ([System.Runtime.CompilerServices.CallerFilePath] string path = "")
            => Path.GetDirectoryName (path)!;
    }
}
