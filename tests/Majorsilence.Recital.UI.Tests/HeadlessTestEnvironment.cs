using Majorsilence.Forms.Headless;

namespace Majorsilence.Recital.UI.Tests
{
    /// <summary>
    /// Puts the Headless backend in a state where a render depends only on the code under test, not on the
    /// machine running it. Call at the start of every UI test, before creating any form.
    /// </summary>
    internal static class HeadlessTestEnvironment
    {
        /// <summary>Disposing the result restores the framework's chrome policy and fonts.</summary>
        public static IDisposable Use ()
        {
            // Custom chrome on every OS and the framework's bundled Noto Sans for every UI font.
            var deterministic = HeadlessRenderer.UseDeterministic ();

            // Formatting follows the culture ("2%" here, "2 %" on a runner set to the invariant culture).
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo ("en-US");
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo ("en-US");

            return deterministic;
        }
    }
}
