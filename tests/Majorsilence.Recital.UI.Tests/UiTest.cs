using System.Drawing;
using Majorsilence.Forms;
using Majorsilence.Forms.Headless;
using Majorsilence.Recital.Core;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Storage;
using Xunit;

namespace Majorsilence.Recital.UI.Tests
{
    /// <summary>
    /// One Headless UI test: the backend, a UI synchronization context so <c>await</c> in event handlers
    /// resumes on this thread, and a pump that runs posted work until the UI settles.
    /// </summary>
    internal sealed class UiTest : IDisposable
    {
        private readonly SynchronizationContext? previous = SynchronizationContext.Current;

        public UiTest ()
        {
            HeadlessTestEnvironment.Use ();
            // FRAMEWORK-GAP: GAP-007. Nothing installs a UI SynchronizationContext on the Headless backend,
            // so continuations after an await in an event handler ran on the thread pool. Install the
            // framework's own context; Pump drains what it posts.
            SynchronizationContext.SetSynchronizationContext (new WindowsFormsSynchronizationContext ());
        }

        public static string Corpus (string file) => Path.Combine (AppContext.BaseDirectory, "corpus", file);

        public const string Alice = "pg-11-alice-epub3.epub";
        public const string SePride = "se-austen-pride-and-prejudice.epub";
        public const string Frankenstein = "se-shelley-frankenstein.epub";

        /// <summary>An in-memory library holding <paramref name="books"/>, imported in order.</summary>
        public static async Task<(InMemoryStorageProvider Storage, DeviceIdentity Device)> LibraryWithAsync (params string[] books)
        {
            var storage = new InMemoryStorageProvider (new FixedTime ());
            var device = new DeviceIdentity ("0123456789abcdef0123456789abcdef", "Test", "linux");
            using var library = new LibraryService (storage, device, new FixedTime ());
            foreach (var book in books) {
                await using var file = File.OpenRead (Corpus (book));
                await library.ImportAsync (file, book);
            }
            return (storage, device);
        }

        /// <summary>A main form on <paramref name="storage"/>, laid out at the given size and initialised.</summary>
        public MainForm OpenMainForm (InMemoryStorageProvider storage, DeviceIdentity device, int width = 1000, int height = 700)
        {
            var form = new MainForm (ct => AppServices.InMemoryAsync (storage, device, new FixedTime (), ct));
            HeadlessTestEnvironment.PinFont (form);
            Resize (form, width, height);
            // Shown, as the hosts do: a form never shown has no focusable controls. Showing raises Load,
            // which opens the library.
            form.Show ();
            WaitIdle (form);
            Assert.NotNull (form.Library);
            return form;
        }

        /// <summary>Runs posted work until <paramref name="done"/> holds (or fails after a timeout).</summary>
        public static void Pump (Func<bool> done, int timeoutMs = 30000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds (timeoutMs);
            while (true) {
                Application.DoEvents ();
                if (done ())
                    break;
                if (DateTime.UtcNow > deadline)
                    Assert.Fail ("The UI did not settle in time.");
                Thread.Sleep (1);
            }
            Application.DoEvents ();
        }

        public static void WaitIdle (MainForm form)
        {
            // Twice: finishing one operation can start another (a relayout after an open, say).
            Pump (() => !form.Operations.IsBusy);
            Pump (() => !form.Operations.IsBusy);
            if (form.ErrorText is { } error)
                Assert.Fail ("The UI reported an error: " + error);
        }

        /// <summary>Resizes the window (a capture at that size) and lets the resulting work finish.</summary>
        public static byte[] Resize (MainForm form, int width, int height)
        {
            HeadlessRenderer.CapturePng (form, width, height);
            Application.DoEvents ();
            return HeadlessRenderer.CapturePng (form, width, height);
        }

        public static byte[] Capture (MainForm form)
        {
            WaitIdle (form);
            return HeadlessRenderer.CapturePng (form, form.Width, form.Height);
        }

        /// <summary>
        /// A point inside <paramref name="control"/>, in the coordinates <see cref="HeadlessRenderer.Click"/>
        /// takes (the window's, which include the drawn title bar). Paints first: toolbar items and list
        /// tiles get their bounds when they are drawn.
        /// </summary>
        public static Point PointIn (MainForm form, Control control, int x, int y)
        {
            HeadlessRenderer.CapturePng (form, form.Width, form.Height);
            return form.PointToClient (control.PointToScreen (new Point (x, y)));
        }

        public static void Click (MainForm form, Control control, int x, int y)
        {
            var p = PointIn (form, control, x, y);
            HeadlessRenderer.Click (form, p.X, p.Y);
        }

        public static void ClickItem (MainForm form, ToolStrip strip, ToolStripItem item)
        {
            HeadlessRenderer.CapturePng (form, form.Width, form.Height);
            var bounds = item.Bounds;
            Click (form, strip, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        }

        public static void Key (MainForm form, Keys key)
        {
            HeadlessRenderer.KeyDown (form, key);
            HeadlessRenderer.KeyUp (form, key);
        }

        public void Dispose ()
        {
            foreach (var form in Application.OpenForms.Cast<Form> ().ToList ())
                form.Dispose ();
            SynchronizationContext.SetSynchronizationContext (previous);
        }

        /// <summary>A fixed clock, so "added" dates and orderings in renders never change.</summary>
        private sealed class FixedTime : TimeProvider
        {
            private static long ticks = new DateTimeOffset (2026, 10, 14, 12, 0, 0, TimeSpan.Zero).UtcTicks;
            public override DateTimeOffset GetUtcNow () => new (Interlocked.Increment (ref ticks), TimeSpan.Zero);
        }
    }
}
