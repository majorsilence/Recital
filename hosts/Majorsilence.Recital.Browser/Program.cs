using Majorsilence.Forms;
using Majorsilence.Recital.UI;

namespace Majorsilence.Recital.Browser
{
    public static class Program
    {
        // No blocking Application.Run: the browser backend attaches to the "out" div in
        // wwwroot/index.html and is then driven by the browser's own event loop.
        private static Task Main () => Application.RunBrowserAsync (() => new MainForm ());
    }
}
