namespace Majorsilence.Recital.UI
{
    /// <summary>
    /// Runs the UI's asynchronous work (opening a book, turning a page, importing) from event handlers:
    /// counts what is in flight, so tests and the busy indicator know when the UI has settled, and reports
    /// failures through <see cref="Failed"/> instead of letting an <c>async void</c> handler crash the app.
    /// </summary>
    public sealed class UiOperations
    {
        private int running;
        private TaskCompletionSource idle = NewIdle ();

        public bool IsBusy => running > 0;

        /// <summary>Completes when nothing is running.</summary>
        public Task WhenIdle => running == 0 ? Task.CompletedTask : idle.Task;

        public event EventHandler<Exception>? Failed;
        public event EventHandler? BusyChanged;

        /// <summary>Starts <paramref name="work"/> on the calling (UI) thread; continuations resume there.</summary>
        public async void Run (Func<Task> work)
        {
            if (running++ == 0) {
                idle = NewIdle ();
                BusyChanged?.Invoke (this, EventArgs.Empty);
            }

            try {
                await work ();
            } catch (OperationCanceledException) {
                // Superseded or shut down: not a failure.
            } catch (Exception ex) {
                Failed?.Invoke (this, ex);
            } finally {
                if (--running == 0) {
                    idle.TrySetResult ();
                    BusyChanged?.Invoke (this, EventArgs.Empty);
                }
            }
        }

        private static TaskCompletionSource NewIdle () => new (TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
