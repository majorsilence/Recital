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
            // AvaloniaMainActivity asks for MainViewFactory once the Activity exists, so the form is only
            // created then. RunAndroid shows it in the single-view host, which registers itself as
            // ISingleViewApplicationLifetime.MainView; hand that back to the Activity.
            if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime) {
                activityLifetime.MainViewFactory = () => {
                    MSForms.Application.RunAndroid (() => new MainForm ());
                    return ((ISingleViewApplicationLifetime) ApplicationLifetime!).MainView!;
                };
            }

            base.OnFrameworkInitializationCompleted ();
        }
    }
}
