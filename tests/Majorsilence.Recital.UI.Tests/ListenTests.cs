using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Tts;
using Majorsilence.Recital.Tts.Tests;
using Xunit;

namespace Majorsilence.Recital.UI.Tests
{
    public class ListenTests
    {
        private static MainForm OpenMobyDick (UiTest ui, (Storage.InMemoryStorageProvider Storage, Core.Library.DeviceIdentity Device) library, FakeDirectEngine engine)
        {
            var form = ui.OpenMainForm (library.Storage, library.Device);
            form.TtsEngines = () => [engine];
            form.Operations.Run (() => form.OpenBookAsync (form.Library!.Books[0]));
            UiTest.WaitIdle (form);
            var entry = ReadingSession.Flatten (form.Reader!.Session!.Book.TableOfContents).First (e => e.Title == "CHAPTER 1. Loomings.");
            form.Operations.Run (() => form.Reader.GoToAsync (entry));
            UiTest.WaitIdle (form);
            return form;
        }

        [Fact (Timeout = 120_000)]
        public async Task ListeningReadsFromThePageHighlightsAndTurnsPages ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.MobyDick);
            var engine = new FakeDirectEngine { PerWord = TimeSpan.FromMilliseconds (2) };
            using var ui = new UiTest ();
            var form = OpenMobyDick (ui, library, engine);
            var reader = form.Reader!;
            var firstPage = reader.Session!.PageIndex;

            form.Operations.Run (reader.ToggleListeningAsync);
            UiTest.WaitIdle (form);
            Assert.True (reader.TtsBar.Visible);
            Assert.True (reader.Player!.IsPlaying);
            UiTest.Pump (() => engine.Spoken.Count > 0);
            Assert.Equal ("CHAPTER 1. Loomings.", engine.Spoken[0].Text);

            // The sentence and word being read are drawn; the page turns by itself as the voice moves on.
            UiTest.Pump (() => reader.PageView.Spoken.Count > 0);
            UiTest.Pump (() => reader.Session!.PageIndex > firstPage, timeoutMs: 60_000);
            var spokenOn = reader.Session!.PageIndex;

            reader.TtsBar.Perform (TtsCommand.PlayPause);
            UiTest.WaitIdle (form);
            Assert.True (reader.Player.IsPaused);
            reader.TtsBar.Perform (TtsCommand.Stop);
            UiTest.WaitIdle (form);
            Assert.False (reader.Player.IsPlaying);
            Assert.Empty (reader.PageView.Spoken);
            Assert.True (reader.Session.PageIndex >= spokenOn);

            reader.TtsBar.Perform (TtsCommand.Close);
            UiTest.WaitIdle (form);
            Assert.False (reader.TtsBar.Visible);
        }

        [Fact (Timeout = 120_000)]
        public async Task SpeedIsAPreferenceAndReachesTheEngine ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.MobyDick);
            var engine = new FakeDirectEngine { PerWord = TimeSpan.FromMilliseconds (20) };
            using var ui = new UiTest ();
            var form = OpenMobyDick (ui, library, engine);
            var reader = form.Reader!;
            form.Operations.Run (reader.ToggleListeningAsync);
            UiTest.WaitIdle (form);
            UiTest.Pump (() => reader.PageView.Spoken.Count > 0);

            reader.ChangePreferences (reader.Preferences with { TtsRate = 1.5f });
            UiTest.WaitIdle (form);
            UiTest.Pump (() => engine.Opened.Count >= 2 && engine.Opened[^1].Rate == 1.5);
            Assert.Equal (1.5f, form.Preferences.TtsRate);
            Assert.Equal (1.5, reader.TtsBar.Rate);
            form.Operations.Run (reader.ToggleListeningAsync);
            UiTest.WaitIdle (form);
        }

        // Play and Pause take the same room, so the buttons after it do not move under a finger.
        [Fact]
        public void PlayAndPauseAreTheSameWidth ()
        {
            using var ui = new UiTest ();
            var bar = new TtsBar ();
            using var form = new Majorsilence.Forms.Form { Width = 900, Height = 200 };
            form.Controls.Add (bar);
            bar.Visible = true;
            form.Show ();

            var play = bar.Items[1];
            bar.ShowState (playing: false, paused: false);
            Assert.Equal ("Play", play.Text);
            var asPlay = play.GetPreferredSize (System.Drawing.Size.Empty).Width;
            bar.ShowState (playing: true, paused: false);
            Assert.Equal ("Pause", play.Text);
            Assert.Equal (asPlay, play.GetPreferredSize (System.Drawing.Size.Empty).Width);
        }

        [Fact]
        public void TtsPreferencesRoundTripAndDoNotRelayOut ()
        {
            var prefs = new ReaderPreferences { TtsRate = 1.75f, TtsVoice = "speechd:en-gb" };
            var back = ReaderPreferences.FromValues (prefs.ToValues ());
            Assert.Equal (prefs, back);
            Assert.False (prefs.AffectsLayout (new ReaderPreferences ()));
        }
    }
}
