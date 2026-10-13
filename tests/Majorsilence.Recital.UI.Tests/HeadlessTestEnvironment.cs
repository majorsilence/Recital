using System.Reflection;
using Majorsilence.Forms;
using Majorsilence.Forms.Headless;
using SkiaSharp;

namespace Majorsilence.Recital.UI.Tests
{
    /// <summary>
    /// Puts the Headless backend in a state where a render depends only on the code under test, not on the
    /// machine running it. Call at the start of every UI test, before creating any form.
    /// </summary>
    internal static class HeadlessTestEnvironment
    {
        private static readonly Lazy<(SKTypeface Regular, SKTypeface Bold)> fonts = new (LoadFonts);

        public static void Use ()
        {
            // FRAMEWORK-GAP: GAP-003. A Headless render follows the host OS: macOS gets native (undrawn)
            // window chrome, and text uses whatever "sans-serif" the machine has installed. Force the
            // custom chrome every other OS draws, and the Noto Sans the framework already embeds.
            Environment.SetEnvironmentVariable ("MF_FORCE_CUSTOM_CHROME", "1");
            HeadlessRenderer.Use ();
            Theme.UIFont = fonts.Value.Regular;
            Theme.UIFontBold = fonts.Value.Bold;
        }

        private static (SKTypeface, SKTypeface) LoadFonts ()
        {
            var assembly = Assembly.Load ("Majorsilence.Forms.Drawing.Common");
            return (Load (assembly, "NotoSans-Regular.ttf"), Load (assembly, "NotoSans-Bold.ttf"));
        }

        private static SKTypeface Load (Assembly assembly, string fileName)
        {
            var name = assembly.GetManifestResourceNames ().SingleOrDefault (n => n.EndsWith ("." + fileName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException ($"{assembly.GetName ().Name} no longer embeds {fileName}; see GAP-003.");

            using var stream = assembly.GetManifestResourceStream (name)!;
            return SKTypeface.FromStream (stream)
                ?? throw new InvalidOperationException ($"Could not load {name}.");
        }
    }
}
