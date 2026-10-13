using Majorsilence.Forms.Headless;
using Xunit;

namespace Majorsilence.Recital.UI.Tests
{
    public class MainFormTests
    {
        [Fact]
        public void EmptyMainFormMatchesBaseline ()
        {
            HeadlessTestEnvironment.Use ();
            using var form = new MainForm ();

            var png = HeadlessRenderer.CapturePng (form, 1000, 700);

            PixelBaseline.AssertMatches ("MainForm.Empty", png);
        }
    }
}
