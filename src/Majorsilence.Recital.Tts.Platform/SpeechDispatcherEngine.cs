using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Majorsilence.Recital.Tts.Platform
{
    /// <summary>
    /// Linux's speech-dispatcher, which most desktops run (it drives espeak-ng, Festival, RHVoice, Piper...).
    /// It speaks by itself; Recital talks to it in its SSIP protocol over the session socket, sends each chunk
    /// as SSML with a mark before every word, and turns the service's index-mark, begin and end events into
    /// progress. A protocol, not a library: nothing of speech-dispatcher is linked or shipped.
    /// </summary>
    public sealed class SpeechDispatcherEngine : ITtsEngine
    {
        public string Id => "speechd";
        public string Name => "System voices (speech-dispatcher)";
        public TtsCapabilities Capabilities => new (Ssml: true, WordTimings: true, MaxChunkChars: 1500);

        /// <summary>The service's socket: <c>$SPEECHD_ADDRESS</c>, else the session default.</summary>
        public static string? SocketPath ()
        {
            var address = Environment.GetEnvironmentVariable ("SPEECHD_ADDRESS");
            if (address?.StartsWith ("unix_socket:", StringComparison.Ordinal) == true)
                return address["unix_socket:".Length..];
            var runtime = Environment.GetEnvironmentVariable ("XDG_RUNTIME_DIR");
            return string.IsNullOrEmpty (runtime) ? null : Path.Combine (runtime, "speech-dispatcher", "speechd.sock");
        }

        /// <summary>Whether speech-dispatcher can be used here: Linux, and its socket exists or the service can be started.</summary>
        public static bool IsAvailable ()
            => OperatingSystem.IsLinux () && !OperatingSystem.IsAndroid () && SocketPath () is { } path
                && (File.Exists (path) || File.Exists ("/usr/bin/speech-dispatcher"));

        public async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default)
        {
            await using var connection = await SsipConnection.OpenAsync (ct).ConfigureAwait (false);
            var lines = await connection.CommandAsync ("LIST SYNTHESIS_VOICES", ct).ConfigureAwait (false);
            // "name<TAB>language<TAB>variant" per line.
            return lines.Select (l => l.Split ('\t')).Where (p => p.Length >= 2 && p[0].Length > 0)
                .Select (p => new TtsVoice (p[0], p[0] + (p.Length > 2 && p[2] != "none" ? $" ({p[2]})" : ""), p[1]))
                .ToList ();
        }

        public ISpeechChannel OpenChannel (TtsSettings settings, string? language) => new Channel (settings, language);

        public ValueTask DisposeAsync () => ValueTask.CompletedTask;

        /// <summary>speech-dispatcher's rate is -100..100 around the voice's normal speed.</summary>
        public static int Rate (double rate) => (int)Math.Round (rate >= 1 ? Math.Min (100, (rate - 1) * 100) : Math.Max (-100, (rate - 1) * 200));

        private sealed class Channel (TtsSettings settings, string? language) : ISpeechChannel
        {
            private readonly SemaphoreSlim open_gate = new (1, 1);
            private readonly object gate = new ();
            private readonly List<(string? MessageId, SpeechChunk Chunk)> queued = [];
            private SsipConnection? connection;
            // Sends run one after another in enqueue order: the service speaks messages in the order it receives them.
            private Task sending = Task.CompletedTask;
            private bool paused;
            private bool stopped;

            public int Pending {
                get {
                    lock (gate)
                        return queued.Count;
                }
            }

            public bool IsPaused => paused;

            public event EventHandler<SpeechChunk>? ChunkStarted;
            public event EventHandler<SpeechProgress>? Progress;
            public event EventHandler<SpeechChunk>? ChunkFinished;
            public event EventHandler<Exception>? Failed;

            public void Enqueue (SpeechChunk chunk)
            {
                lock (gate) {
                    queued.Add ((null, chunk));
                    var previous = sending;
                    sending = Task.Run (async () => {
                        await previous.ConfigureAwait (false);
                        await SendAsync (chunk).ConfigureAwait (false);
                    });
                }
            }

            private async Task SendAsync (SpeechChunk chunk)
            {
                try {
                    var ssip = await ConnectAsync ().ConfigureAwait (false);
                    var ssml = Ssml.Build (chunk, language, settings.DialogueVoiceId, marks: true);
                    var id = await ssip.SpeakAsync (ssml, CancellationToken.None).ConfigureAwait (false);
                    lock (gate) {
                        var index = queued.FindIndex (q => q.Chunk == chunk && q.MessageId is null);
                        if (index >= 0)
                            queued[index] = (id, chunk);
                    }
                } catch (Exception ex) when (!stopped) {
                    Failed?.Invoke (this, ex);
                } catch (Exception) {
                    // Stopped meanwhile.
                }
            }

            /// <summary>One connection per channel, set up once: client name, SSML, notifications, voice, rate.</summary>
            private async Task<SsipConnection> ConnectAsync ()
            {
                await open_gate.WaitAsync ().ConfigureAwait (false);
                try {
                    if (connection is not null)
                        return connection;
                    var ssip = await SsipConnection.OpenAsync (CancellationToken.None).ConfigureAwait (false);
                    ssip.Event += OnEvent;
                    await ssip.CommandAsync ("SET self SSML_MODE on", CancellationToken.None).ConfigureAwait (false);
                    await ssip.CommandAsync ("SET self NOTIFICATION all on", CancellationToken.None).ConfigureAwait (false);
                    await ssip.CommandAsync ("SET self RATE " + Rate (settings.ClampedRate).ToString (CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait (false);
                    if (!string.IsNullOrEmpty (language))
                        await ssip.CommandAsync ("SET self LANGUAGE " + language.Split ('-')[0], CancellationToken.None).ConfigureAwait (false);
                    if (!string.IsNullOrEmpty (settings.VoiceId))
                        await ssip.CommandAsync ("SET self SYNTHESIS_VOICE " + settings.VoiceId, CancellationToken.None).ConfigureAwait (false);
                    var volume = Environment.GetEnvironmentVariable ("RECITAL_SPEECHD_VOLUME");
                    if (int.TryParse (volume, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
                        await ssip.CommandAsync ("SET self VOLUME " + Math.Clamp (v, -100, 100).ToString (CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait (false);
                    connection = ssip;
                    return ssip;
                } finally {
                    open_gate.Release ();
                }
            }

            private void OnEvent (object? sender, SsipEvent e)
            {
                SpeechChunk? chunk;
                lock (gate)
                    chunk = queued.FirstOrDefault (q => q.MessageId == e.MessageId).Chunk;
                if (chunk is null)
                    return;
                switch (e.Code) {
                case 701:
                    ChunkStarted?.Invoke (this, chunk);
                    break;
                case 700 when e.Mark is { } mark && mark.StartsWith ('c') && int.TryParse (mark.AsSpan (1), NumberStyles.None, CultureInfo.InvariantCulture, out var index):
                    Progress?.Invoke (this, new SpeechProgress (chunk, index));
                    break;
                case 702 or 703:
                    lock (gate)
                        queued.RemoveAll (q => q.MessageId == e.MessageId);
                    if (e.Code == 702)
                        ChunkFinished?.Invoke (this, chunk);
                    break;
                }
            }

            public void Pause ()
            {
                paused = true;
                _ = connection?.CommandAsync ("PAUSE self", CancellationToken.None);
            }

            public void Unpause ()
            {
                paused = false;
                _ = connection?.CommandAsync ("RESUME self", CancellationToken.None);
            }

            public async Task StopAsync ()
            {
                stopped = true;
                lock (gate)
                    queued.Clear ();
                if (connection is { } ssip) {
                    try {
                        await ssip.CommandAsync ("CANCEL self", CancellationToken.None).ConfigureAwait (false);
                    } catch (IOException) {
                        // The service went away: nothing to cancel.
                    }
                }
            }

            public async ValueTask DisposeAsync ()
            {
                await StopAsync ().ConfigureAwait (false);
                if (connection is not null)
                    await connection.DisposeAsync ().ConfigureAwait (false);
                open_gate.Dispose ();
            }
        }
    }

    internal sealed record SsipEvent (int Code, string MessageId, string? Mark);

    /// <summary>
    /// One SSIP session: commands and their replies in order, and asynchronous 7xx events (begin, end,
    /// index mark) arriving on the same socket in between.
    /// </summary>
    internal sealed class SsipConnection : IAsyncDisposable
    {
        private readonly Socket socket;
        private readonly NetworkStream stream;
        private readonly StreamReader reader;
        private readonly SemaphoreSlim command_gate = new (1, 1);
        private readonly Queue<TaskCompletionSource<(int Code, List<string> Lines)>> replies = new ();
        private readonly Task reading;

        private SsipConnection (Socket socket)
        {
            this.socket = socket;
            stream = new NetworkStream (socket, ownsSocket: false);
            reader = new StreamReader (stream, new UTF8Encoding (false));
            reading = Task.Run (ReadLoopAsync);
        }

        public event EventHandler<SsipEvent>? Event;

        public static async Task<SsipConnection> OpenAsync (CancellationToken ct)
        {
            var path = SpeechDispatcherEngine.SocketPath () ?? throw new IOException ("speech-dispatcher: no session runtime directory.");
            if (!File.Exists (path))
                await SpawnAsync (path, ct).ConfigureAwait (false);
            var socket = new Socket (AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try {
                await socket.ConnectAsync (new UnixDomainSocketEndPoint (path), ct).ConfigureAwait (false);
            } catch (SocketException ex) {
                socket.Dispose ();
                throw new IOException ("Could not reach speech-dispatcher: " + ex.Message, ex);
            }
            var connection = new SsipConnection (socket);
            await connection.CommandAsync ("SET self CLIENT_NAME " + Environment.UserName.Replace (' ', '_') + ":recital:main", ct).ConfigureAwait (false);
            return connection;
        }

        /// <summary>Starts the service the way its own clients do (<c>--spawn</c> exits at once if it is running).</summary>
        private static async Task SpawnAsync (string path, CancellationToken ct)
        {
            try {
                using var process = System.Diagnostics.Process.Start (new System.Diagnostics.ProcessStartInfo ("speech-dispatcher", "--spawn") { UseShellExecute = false, CreateNoWindow = true });
                if (process is not null)
                    await process.WaitForExitAsync (ct).ConfigureAwait (false);
            } catch (System.ComponentModel.Win32Exception) {
                throw new IOException ("speech-dispatcher is not installed.");
            }
            for (var i = 0; i < 50 && !File.Exists (path); i++)
                await Task.Delay (50, ct).ConfigureAwait (false);
        }

        /// <summary>Sends a command; returns the reply's data lines. A 3xx/4xx/5xx reply throws.</summary>
        public async Task<List<string>> CommandAsync (string command, CancellationToken ct)
        {
            var (_, lines) = await RoundTripAsync (command + "\r\n", ct).ConfigureAwait (false);
            return lines;
        }

        /// <summary>SPEAK, the text (dot-stuffed), and the end marker; returns the message id.</summary>
        public async Task<string> SpeakAsync (string text, CancellationToken ct)
        {
            await command_gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                await SendAndWaitAsync ("SPEAK\r\n", ct).ConfigureAwait (false);
                var body = new StringBuilder ();
                foreach (var line in text.Replace ("\r", "", StringComparison.Ordinal).Split ('\n'))
                    body.Append (line.StartsWith ('.') ? "." + line : line).Append ("\r\n");
                body.Append (".\r\n");
                var (_, lines) = await SendAndWaitAsync (body.ToString (), ct).ConfigureAwait (false);
                return lines.FirstOrDefault () ?? throw new IOException ("speech-dispatcher gave no message id.");
            } finally {
                command_gate.Release ();
            }
        }

        private async Task<(int, List<string>)> RoundTripAsync (string text, CancellationToken ct)
        {
            await command_gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                return await SendAndWaitAsync (text, ct).ConfigureAwait (false);
            } finally {
                command_gate.Release ();
            }
        }

        private async Task<(int, List<string>)> SendAndWaitAsync (string text, CancellationToken ct)
        {
            var reply = new TaskCompletionSource<(int, List<string>)> (TaskCreationOptions.RunContinuationsAsynchronously);
            lock (replies)
                replies.Enqueue (reply);
            await stream.WriteAsync (Encoding.UTF8.GetBytes (text), ct).ConfigureAwait (false);
            var (code, lines) = await reply.Task.WaitAsync (TimeSpan.FromSeconds (10), ct).ConfigureAwait (false);
            if (code >= 300)
                throw new IOException ($"speech-dispatcher refused ({code}): {string.Join (" ", lines)}");
            return (code, lines);
        }

        private async Task ReadLoopAsync ()
        {
            var lines = new List<string> ();
            try {
                while (await reader.ReadLineAsync ().ConfigureAwait (false) is { } line) {
                    if (line.Length < 4 || !int.TryParse (line.AsSpan (0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                        continue;
                    if (line[3] == '-') {
                        lines.Add (line[4..]);
                        continue;
                    }
                    var done = lines;
                    lines = [];
                    if (code is >= 700 and < 800) {
                        // msg_id, client_id[, mark name]
                        var mark = code == 700 && done.Count > 2 ? done[2] : null;
                        if (done.Count > 0)
                            Event?.Invoke (this, new SsipEvent (code, done[0], mark));
                        continue;
                    }
                    if (code == 230) {
                        done.Add (line[4..]);
                    }
                    TaskCompletionSource<(int, List<string>)>? reply;
                    lock (replies)
                        replies.TryDequeue (out reply);
                    reply?.TrySetResult ((code, done));
                }
            } catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
                // Closed.
            }
            lock (replies)
                while (replies.TryDequeue (out var pending))
                    pending.TrySetException (new IOException ("speech-dispatcher closed the connection."));
        }

        public async ValueTask DisposeAsync ()
        {
            try {
                await stream.WriteAsync ("QUIT\r\n"u8.ToArray ()).ConfigureAwait (false);
            } catch (IOException) {
            }
            socket.Shutdown (SocketShutdown.Both);
            socket.Dispose ();
            try {
                await reading.ConfigureAwait (false);
            } catch (Exception) {
            }
            stream.Dispose ();
            reader.Dispose ();
            command_gate.Dispose ();
        }
    }
}
