using System.Drawing;
using Majorsilence.Forms;

namespace Majorsilence.Recital.UI
{
    /// <summary>
    /// The app's top-level window. Every host (desktop, Android, iOS, browser) and the Headless UI
    /// tests show this same form; only the backend underneath differs.
    /// </summary>
    public class MainForm : Form
    {
        public MainForm ()
        {
            Text = "Recital";
            ClientSize = new Size (1000, 700);
        }
    }
}
