using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.iOS;
using Foundation;
using Majorsilence.Recital.UI;

using MSForms = Majorsilence.Forms;

namespace Majorsilence.Recital.iOS
{
    // "AppDelegate" is UIKit's own name for this role.
    [Register ("AppDelegate")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage ("Naming", "CA1711", Justification = "UIKit app delegate")]
    public class RecitalAppDelegate : AvaloniaAppDelegate<RecitalAvaloniaApp>
    {
    }

    public sealed class RecitalAvaloniaApp : Avalonia.Application
    {
        public override void OnFrameworkInitializationCompleted ()
        {
            // Secrets in the Keychain, before anything asks for them.
            Majorsilence.Recital.Platform.SecretStores.Host = new KeychainSecretStore ();

            if (ApplicationLifetime is ISingleViewApplicationLifetime)
                // The device's own voices first: offline, instant, and light on the battery.
                MSForms.Application.RunIOS (() => new MainForm {
                    HostTtsEngines = [new AppleSpeechEngine ()],
                    HostAudioOutput = () => new AppleAudioOutput (),
                });

            base.OnFrameworkInitializationCompleted ();
        }
    }
}
