using System.Reflection;
using Majorsilence.Forms;
using Majorsilence.Forms.Drawing.Text;
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

        // Owns the registered faces; text layout resolves them by family name for the life of the process.
        private static PrivateFontCollection? collection;

        public static void Use ()
        {
            // FRAMEWORK-GAP: GAP-003. A Headless render follows the host OS: macOS gets native (undrawn)
            // window chrome, and text uses whatever "sans-serif" the machine has installed. Force the
            // custom chrome every other OS draws, and the Noto Sans the framework already embeds.
            Environment.SetEnvironmentVariable ("MF_FORCE_CUSTOM_CHROME", "1");
            HeadlessRenderer.Use ();
            Theme.UIFont = fonts.Value.Regular;
            Theme.UIFontBold = fonts.Value.Bold;

            // FRAMEWORK-GAP: GAP-003. Code that draws with Control.Font (the owner-drawn list tiles)
            // gets SystemFonts.DefaultFont, which is whatever sans-serif the machine has, not the
            // Theme.UIFont above. Pin the default to the same embedded Noto Sans (see also PinFont).
            if (!default_font_set) {
                Application.SetDefaultFont (new Majorsilence.Forms.Drawing.Font ("Noto Sans", SystemFonts.DefaultFont.Size));
                default_font_set = true;
            }

            // Formatting follows the culture ("2%" here, "2 %" on a runner set to the invariant culture).
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo ("en-US");
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo ("en-US");
        }

        private static bool default_font_set;

        /// <summary>
        /// Makes every control in <paramref name="window"/> draw in the embedded Noto Sans.
        /// </summary>
        public static void PinFont (WindowBase window)
            // FRAMEWORK-GAP: GAP-011. Application.SetDefaultFont (above) changes Control.Font but not the
            // typeface controls draw with: GetEffectiveFont falls back to the system face, so the tree view
            // drew in each machine's own sans-serif. The window's style is the last stop before that
            // fallback, and setting only the typeface there leaves every font size alone.
            => window.Style.Font = fonts.Value.Regular;

        private static (SKTypeface, SKTypeface) LoadFonts ()
        {
            var assembly = Assembly.Load ("Majorsilence.Forms.Drawing.Common");

            // FRAMEWORK-GAP: GAP-004. Assigning Theme.UIFont alone only changes the family *name* text
            // is laid out with; the face itself comes from the machine's fonts, so wherever Noto Sans is
            // not installed (Windows, macOS) the title fell back to the system UI font. Registering the
            // faces in a PrivateFontCollection is what makes layout use these exact files everywhere.
            var regular = Read (assembly, "NotoSans-Regular.ttf");
            var bold = Read (assembly, "NotoSans-Bold.ttf");

            collection = new PrivateFontCollection ();
            collection.AddMemoryFont (regular);
            collection.AddMemoryFont (bold);

            return (FromBytes (regular), FromBytes (bold));
        }

        private static SKTypeface FromBytes (byte[] font)
            => SKTypeface.FromData (SKData.CreateCopy (font)) ?? throw new InvalidOperationException ("Could not load an embedded font.");

        private static byte[] Read (Assembly assembly, string fileName)
        {
            var name = assembly.GetManifestResourceNames ().SingleOrDefault (n => n.EndsWith ("." + fileName, StringComparison.Ordinal))
                ?? throw new InvalidOperationException ($"{assembly.GetName ().Name} no longer embeds {fileName}; see GAP-003.");

            using var stream = assembly.GetManifestResourceStream (name)!;
            using var buffer = new MemoryStream ();
            stream.CopyTo (buffer);
            return buffer.ToArray ();
        }
    }
}
