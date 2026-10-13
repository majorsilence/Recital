using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.iOS;
using Foundation;
using Majorsilence.Recital.UI;

using MSForms = Majorsilence.Forms;

namespace Majorsilence.Recital.iOS
{
    [Register ("AppDelegate")]
    public class RecitalAppDelegate : AvaloniaAppDelegate<RecitalAvaloniaApp>
    {
    }

    public sealed class RecitalAvaloniaApp : Avalonia.Application
    {
        public override void OnFrameworkInitializationCompleted ()
        {
            if (ApplicationLifetime is ISingleViewApplicationLifetime)
                MSForms.Application.RunIOS (() => new MainForm ());

            base.OnFrameworkInitializationCompleted ();
        }
    }
}
