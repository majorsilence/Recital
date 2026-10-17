using Majorsilence.Forms;
using Majorsilence.Recital.Core;
using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Storage;
using Majorsilence.Recital.Storage.Tests;
using Xunit;

namespace Majorsilence.Recital.UI.Tests
{
    public sealed class CloudSyncUiTests : IDisposable
    {
        private readonly string temp = Path.Combine (Path.GetTempPath (), "recital-ui-cloud", Guid.NewGuid ().ToString ("N"));

        [Fact (Timeout = 120_000)]
        public void ConnectingToWebDavThenImportingABookFromIt ()
        {
            var server = new FakeWebDavServer { RequiredCredentials = "reader:pass" };
            var secrets = new InMemorySecretStore ();
            var platform = new PlatformServices (secrets, null, CloudClients.None, () => new Shared (server));

            using var ui = new UiTest ();
            var form = new MainForm (ct => AppServices.OpenAsync (Path.Combine (temp, "laptop"), platform, ct));
            UiTest.Resize (form, 1000, 700);
            form.Show ();
            UiTest.WaitIdle (form);
            var library = form.Library!;

            // Put a book on the server, outside the sync home, as a user's own files would be.
            form.Operations.Run (async () => {
                using var http = Storage.WebDav.WebDavStorageProvider.CreateClient ("reader", "pass", server);
                var direct = new Storage.WebDav.WebDavStorageProvider (server.BaseUri, http);
                await using var book = File.OpenRead (UiTest.Corpus (UiTest.Sherlock));
                await direct.WriteAsync ("Books/Sherlock Holmes.epub", book);
            });
            UiTest.WaitIdle (form);

            // Sync > WebDAV…, a wrong password first.
            library.ShowWebDavForm ();
            library.WebDav.Address.Text = server.BaseUri.AbsoluteUri;
            library.WebDav.UserName.Text = "reader";
            library.WebDav.Password.Text = "nope";
            ClickButton (form, library.WebDav, "Connect");
            Assert.True (library.WebDav.Visible);
            Assert.Contains ("refused", library.WebDav.Message, StringComparison.Ordinal);
            Assert.Null (form.Services!.SyncHome);

            library.WebDav.Password.Text = "pass";
            ClickButton (form, library.WebDav, "Connect");
            Assert.Equal (SyncHomeKind.WebDav, form.Services!.SyncHome?.EffectiveKind);
            Assert.StartsWith ("Synced ", form.Library!.SyncStatusText, StringComparison.Ordinal);

            // Import from WebDAV…: open Books, pick the book.
            library = form.Library!;
            form.Operations.Run (library.OpenRemoteBrowserAsync);
            UiTest.WaitIdle (form);
            var browser = library.RemoteBrowser;
            Assert.Contains (browser.List.Items.Cast<ListViewItem> (), i => i.Text == "Books/");
            form.Operations.Run (() => browser.ShowFolderAsync ("Books"));
            UiTest.WaitIdle (form);
            browser.List.Items[0].Selected = true;
            ClickButton (form, browser, "Import");
            UiTest.Pump (() => form.Library!.Books.Count == 1);
            Assert.Equal ("The Adventures of Sherlock Holmes", form.Library!.Books[0].Title);

            form.Close ();
            Application.DoEvents ();
        }

        private static void ClickButton (MainForm form, Control parent, string text)
        {
            var button = All (parent).OfType<Button> ().First (b => b.Text == text);
            UiTest.Click (form, button, 4, 4);
            UiTest.WaitIdle (form);
        }

        private static IEnumerable<Control> All (Control root)
        {
            foreach (Control child in root.Controls) {
                yield return child;
                foreach (var nested in All (child))
                    yield return nested;
            }
        }

        public void Dispose ()
        {
            try {
                if (Directory.Exists (temp))
                    Directory.Delete (temp, recursive: true);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // A sync round may still be finishing.
            }
        }

        /// <summary>One fake server behind the many HttpClients the app makes.</summary>
        private sealed class Shared (HttpMessageHandler inner) : DelegatingHandler (new Invoker (inner))
        {
            private sealed class Invoker (HttpMessageHandler inner) : HttpMessageHandler
            {
                private readonly HttpMessageInvoker invoker = new (inner, disposeHandler: false);

                protected override Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
                    => invoker.SendAsync (request, cancellationToken);
            }
        }
    }
}
