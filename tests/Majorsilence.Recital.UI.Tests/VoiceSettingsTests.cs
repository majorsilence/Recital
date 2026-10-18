using Majorsilence.Recital.Core;
using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Storage;
using Xunit;

namespace Majorsilence.Recital.UI.Tests
{
    public sealed class VoiceSettingsTests : IDisposable
    {
        private readonly string temp = Path.Combine (Path.GetTempPath (), "recital-voices", Guid.NewGuid ().ToString ("N"));

        [Fact (Timeout = 120_000)]
        public async Task ACloudKeyGoesToTheSecureStoreAndAddsItsEngine ()
        {
            var secrets = new InMemorySecretStore ();
            var platform = new PlatformServices (secrets, null, CloudClients.None);
            using var ui = new UiTest ();
            var form = new MainForm (ct => AppServices.OpenAsync (temp, platform, ct));
            UiTest.Resize (form, 1000, 700);
            form.Show ();
            UiTest.WaitIdle (form);

            form.Operations.Run (form.ShowVoiceSettingsAsync);
            UiTest.WaitIdle (form);
            Assert.True (form.VoiceSettings.Visible);
            form.VoiceSettings.KeyBox ("openai").Text = "sk-ui-test-secret-key";
            form.VoiceSettings.PiperVoices.Text = Path.Combine (temp, "voices");
            var save = All (form.VoiceSettings).OfType<Majorsilence.Forms.Button> ().First (b => b.Text == "Save");
            UiTest.Click (form, save, 4, 4);
            UiTest.WaitIdle (form);

            Assert.False (form.VoiceSettings.Visible);
            Assert.Equal ("sk-ui-test-secret-key", await secrets.GetAsync (Tts.Platform.TtsEngines.KeyFor ("openai"), TestContext.Current.CancellationToken));
            if (Tts.Platform.TtsEngines.AudioOutputFactory () is not null)
                Assert.Contains (form.TtsEngines (), e => e.Id == "openai");
            // Settings on disk name the Piper folder, never a key.
            foreach (var file in Directory.EnumerateFiles (temp, "*", SearchOption.AllDirectories).Where (f => f.EndsWith (".json", StringComparison.Ordinal)))
                Assert.DoesNotContain ("sk-ui-test-secret-key", File.ReadAllText (file), StringComparison.Ordinal);
            Assert.Contains ("voices", File.ReadAllText (Path.Combine (temp, "tts.json")), StringComparison.Ordinal);
            form.Close ();
        }

        private static IEnumerable<Majorsilence.Forms.Control> All (Majorsilence.Forms.Control root)
        {
            foreach (Majorsilence.Forms.Control child in root.Controls) {
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
            }
        }
    }
}
