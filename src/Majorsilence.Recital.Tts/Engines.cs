namespace Majorsilence.Recital.Tts
{
    /// <param name="Id">The engine's own voice identifier.</param>
    /// <param name="Name">For display.</param>
    /// <param name="Language">BCP 47, when the engine says.</param>
    public sealed record TtsVoice (string Id, string Name, string? Language);

    /// <param name="VoiceId">The narrator's voice, or null for the engine's default.</param>
    /// <param name="DialogueVoiceId">A voice for quoted speech, or null to use the narrator's (plan 3.2).</param>
    /// <param name="Rate">Speed, 1 = normal (0.5 to 3).</param>
    public sealed record TtsSettings (string? VoiceId = null, string? DialogueVoiceId = null, double Rate = 1.0)
    {
        public double ClampedRate => Math.Clamp (Rate, 0.5, 3.0);
    }

    /// <param name="Ssml">Takes SSML (emphasis, breaks, voices).</param>
    /// <param name="WordTimings">Reports when each word is spoken; otherwise the position is interpolated.</param>
    /// <param name="MaxChunkChars">The longest chunk it takes.</param>
    public sealed record TtsCapabilities (bool Ssml, bool WordTimings, int MaxChunkChars);

    /// <summary>
    /// An engine (or synthesizer) with something slow to load the first time, such as an on-device model: the reader
    /// asks it to load when a book opens, so Listen does not wait for it.
    /// </summary>
    public interface IPreparable
    {
        Task PrepareAsync (CancellationToken ct = default);
    }

    /// <summary>A text-to-speech engine (plan 3.3): it lists voices and opens channels that speak chunks.</summary>
    public interface ITtsEngine : IAsyncDisposable
    {
        /// <summary>"speechd", "sapi", "say", "piper", ...</summary>
        string Id { get; }

        string Name { get; }

        TtsCapabilities Capabilities { get; }

        Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default);

        /// <summary>A queue that speaks chunks one after another without gaps.</summary>
        ISpeechChannel OpenChannel (TtsSettings settings, string? language);
    }

    /// <summary>Character <paramref name="CharIndex"/> of <paramref name="Chunk"/> (the start of a word) is being spoken.</summary>
    public sealed record SpeechProgress (SpeechChunk Chunk, int CharIndex);

    /// <summary>
    /// Chunks being spoken, in the order enqueued, without gaps between them. Engines that make audio prepare
    /// what is enqueued ahead of time; engines that speak themselves queue it with the OS speech service.
    /// Events come on a worker thread.
    /// </summary>
    public interface ISpeechChannel : IAsyncDisposable
    {
        /// <summary>Adds a chunk to speak after those already queued.</summary>
        void Enqueue (SpeechChunk chunk);

        /// <summary>Chunks enqueued and not yet finished.</summary>
        int Pending { get; }

        bool IsPaused { get; }

        event EventHandler<SpeechChunk>? ChunkStarted;
        event EventHandler<SpeechProgress>? Progress;
        event EventHandler<SpeechChunk>? ChunkFinished;

        /// <summary>The engine failed; nothing more will be spoken on this channel.</summary>
        event EventHandler<Exception>? Failed;

        void Pause ();
        void Unpause ();

        /// <summary>Stops speaking and drops everything queued.</summary>
        Task StopAsync ();
    }
}
