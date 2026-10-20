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

            // A book opened from Files or shared from another app ("Open in Recital", Info.plist's document type).
            if (TryGetFeature (typeof (IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) => OpenedBooks.Deliver (Books (e));

            if (ApplicationLifetime is ISingleViewApplicationLifetime)
                // The device's own voices first: offline, instant, and light on the battery.
                MSForms.Application.RunIOS (() => {
                    var form = new MainForm {
                        HostTtsEngines = [new AppleSpeechEngine ()],
                        HostAudioOutput = () => new AppleAudioOutput (),
                    };
                    OpenedBooks.Attach (form);
                    return form;
                });

            base.OnFrameworkInitializationCompleted ();
        }

        // Avalonia hands a document over as a storage file, which keeps its security-scoped access while it is
        // read; a plain file URL is read directly.
        private static List<MSForms.Backends.PickedFile> Books (ActivatedEventArgs e) => e switch {
            FileActivatedEventArgs files => [.. files.Files.OfType<Avalonia.Platform.Storage.IStorageFile> ()
                .Select (f => new MSForms.Backends.PickedFile (f.Name, f.Path.ToString (), async _ => await f.OpenReadAsync ()))],
            ProtocolActivatedEventArgs { Uri.IsFile: true } link => [MSForms.Backends.PickedFile.FromPath (link.Uri.LocalPath)],
            _ => [],
        };
    }

    /// <summary>Books opened from another app, held until the form exists and then handed to it.</summary>
    internal static class OpenedBooks
    {
        private static MainForm? form;
        private static List<MSForms.Backends.PickedFile>? pending;

        public static void Attach (MainForm created)
        {
            form = created;
            if (pending is { } waiting) {
                pending = null;
                created.Operations.Run (() => created.OpenFilesAsync (waiting));
            }
        }

        public static void Deliver (List<MSForms.Backends.PickedFile> files)
        {
            if (files.Count == 0)
                return;
            if (form is { } ready)
                ready.Operations.Run (() => ready.OpenFilesAsync (files));
            else
                (pending ??= []).AddRange (files);
        }
    }
}
