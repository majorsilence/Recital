using Android.App;
using Android.Runtime;
using Avalonia.Android;
using Avalonia.Controls.ApplicationLifetimes;
using Majorsilence.Recital.UI;

using MSForms = Majorsilence.Forms;

namespace Majorsilence.Recital.Android
{
    [Application]
    public class RecitalApplication : AvaloniaAndroidApplication<RecitalAvaloniaApp>
    {
        public RecitalApplication (IntPtr handle, JniHandleOwnership transfer) : base (handle, transfer)
        {
        }
    }

    public sealed class RecitalAvaloniaApp : Avalonia.Application
    {
        public override void OnFrameworkInitializationCompleted ()
        {
            // Secrets in the Android Keystore, before anything asks for them.
            var context = global::Android.App.Application.Context;
            Majorsilence.Recital.Platform.SecretStores.Host = new AndroidKeystoreSecretStore (
                System.IO.Path.Combine (context.NoBackupFilesDir!.AbsolutePath, "secrets"));

            // AvaloniaMainActivity asks for MainViewFactory once the Activity exists, so the form is only
            // created then. RunAndroid shows it in the single-view host, which registers itself as
            // ISingleViewApplicationLifetime.MainView; hand that back to the Activity.
            if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime) {
                activityLifetime.MainViewFactory = () => {
                    // The device's own voices first: offline, instant, and light on the battery.
                    MSForms.Application.RunAndroid (() => {
                        var form = new MainForm {
                            HostTtsEngines = [new AndroidSpeechEngine (context)],
                            HostAudioOutput = () => new AndroidAudioOutput (),
                        };
                        // A media notification while reading aloud, so it goes on with the screen off.
                        form.ReadingAloudChanged += (_, state) => {
                            if (state.Listening) {
                                MainActivity.AskToShowNotifications ();
                                ReadingAloudService.Start (context, state.Title);
                            }
                            else
                                ReadingAloudService.Stop (context);
                        };
                        return form;
                    });
                    return ((ISingleViewApplicationLifetime) ApplicationLifetime!).MainView!;
                };
            }

            base.OnFrameworkInitializationCompleted ();
        }
    }
}
