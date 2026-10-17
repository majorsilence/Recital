using Majorsilence.Forms;
using Majorsilence.Forms.Headless;
using Majorsilence.Recital.Core;
using Xunit;

namespace Majorsilence.Recital.UI.Tests
{
    public class SyncTests : IDisposable
    {
        private readonly string temp = Path.Combine (Path.GetTempPath (), "recital-ui-sync", Guid.NewGuid ().ToString ("N"));

        [Fact (Timeout = 120_000)]
        public void ABookAddedOnAnotherDeviceAppearsAfterChoosingTheSameFolder ()
        {
            var folder = Path.Combine (temp, "Dropbox");
            Directory.CreateDirectory (folder);
            var laptopData = Path.Combine (temp, "laptop");
            var desktopData = Path.Combine (temp, "desktop");

            using var ui = new UiTest ();
            var form = new MainForm (ct => AppServices.OpenAsync (laptopData, ct));
            UiTest.Resize (form, 1000, 700);
            form.Show ();
            UiTest.WaitIdle (form);
            Assert.Empty (form.Library!.Books);
            Assert.Equal ("", form.Library.SyncStatusText);

            // Another device, already syncing with the folder, adds a book.
            form.Operations.Run (async () => {
                await AppServices.SetSyncFolderAsync (desktopData, folder);
                await using var desktop = await AppServices.OpenAsync (desktopData);
                await using var file = File.OpenRead (UiTest.Corpus (UiTest.Sherlock));
                await desktop.Library.ImportAsync (file, "sherlock.epub");
                await desktop.Sync!.SyncOnceAsync ();
            });
            UiTest.WaitIdle (form);

            // Sync > Choose sync folder…, answered by the folder picker.
            HeadlessRenderer.OpenFolderResponse = _ => folder;
            try {
                form.Operations.Run (form.Library.ChooseSyncFolderAsync);
                UiTest.WaitIdle (form);
            } finally {
                HeadlessRenderer.OpenFolderResponse = null;
            }
            UiTest.Pump (() => form.Library!.Books.Count == 1);

            Assert.Equal ("The Adventures of Sherlock Holmes", form.Library!.Books[0].Title);
            Assert.Equal (folder, form.Services!.SyncFolder);
            Assert.StartsWith ("Synced ", form.Library.SyncStatusText, StringComparison.Ordinal);
            Assert.True (File.Exists (Path.Combine (folder, "Recital", "devices", form.Services.Device.DeviceId + ".json")));

            form.Operations.Run (() => form.ChangeSyncFolderAsync (null));
            UiTest.WaitIdle (form);
            Assert.Null (form.Services!.Sync);
            Assert.Equal ("", form.Library!.SyncStatusText);
            // The local copy keeps the library.
            Assert.Single (form.Library.Books);
            form.Close ();
            Application.DoEvents ();
        }

        public void Dispose ()
        {
            try {
                if (Directory.Exists (temp))
                    Directory.Delete (temp, recursive: true);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // A sync round may still be finishing; the OS cleans temp.
            }
            GC.SuppressFinalize (this);
        }
    }
}
