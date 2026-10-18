using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Majorsilence.Recital.Tts.Cloud
{
    /// <summary>The engine could not be used: a refused key, a quota, an unreachable service. The message never contains the key.</summary>
    public sealed class TtsServiceException (string message, bool keyRefused = false, Exception? inner = null) : IOException (message, inner)
    {
        /// <summary>The service refused the key (401/403): the user has to fix it in settings.</summary>
        public bool KeyRefused { get; } = keyRefused;
    }

    /// <summary>Shared plumbing for the cloud voices: sending, retrying once, and errors that never echo the key.</summary>
    public abstract class CloudSynthesizer (HttpClient http, string service) : ISpeechSynthesizer
    {
        protected HttpClient Http { get; } = http;

        public abstract string Id { get; }
        public abstract string Name { get; }
        public abstract TtsCapabilities Capabilities { get; }
        public abstract Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default);
        public abstract Task<SynthesizedAudio> SynthesizeAsync (SpeechChunk chunk, TtsSettings settings, string? language, CancellationToken ct = default);

        public ValueTask DisposeAsync ()
        {
            GC.SuppressFinalize (this);
            return ValueTask.CompletedTask;
        }

        /// <summary>Sends, once more after a dropped connection, a 429 or a 5xx; maps failures to <see cref="TtsServiceException"/>.</summary>
        protected async Task<HttpResponseMessage> SendAsync (Func<HttpRequestMessage> create, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++) {
                using var request = create ();
                HttpResponseMessage response;
                try {
                    response = await Http.SendAsync (request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
                } catch (HttpRequestException ex) {
                    if (attempt == 0)
                        continue;
                    throw new TtsServiceException ($"Could not reach {service}: {ex.Message}", inner: ex);
                } catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) {
                    throw new TtsServiceException ($"{service} did not answer in time.", inner: ex);
                }
                if (attempt == 0 && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)) {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds (1);
                    response.Dispose ();
                    await Task.Delay (wait < TimeSpan.FromSeconds (20) ? wait : TimeSpan.FromSeconds (20), ct).ConfigureAwait (false);
                    continue;
                }
                if (response.IsSuccessStatusCode)
                    return response;
                using (response)
                    throw await FailureAsync (response, ct).ConfigureAwait (false);
            }
        }

        private async Task<TtsServiceException> FailureAsync (HttpResponseMessage response, CancellationToken ct)
        {
            var code = (int)response.StatusCode;
            string detail;
            try {
                var text = await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false);
                detail = Detail (text);
            } catch (Exception ex) when (ex is HttpRequestException or IOException) {
                detail = "";
            }
            // The body is the service's text, never our request, so it cannot contain the key; trimmed anyway.
            if (detail.Length > 200)
                detail = detail[..200];
            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? new TtsServiceException ($"{service} refused the key ({code}). Check it in the voice settings.", keyRefused: true)
                : new TtsServiceException ($"{service} failed ({code}){(detail.Length > 0 ? ": " + detail : "")}");
        }

        /// <summary>The useful part of an error body: a JSON error message where there is one.</summary>
        protected static string Detail (string text)
        {
            try {
                var json = JsonNode.Parse (text);
                return json?["error"]?["message"]?.GetValue<string> ()
                    ?? json?["detail"]?["message"]?.GetValue<string> ()
                    ?? json?["error"]?.GetValue<string> ()
                    ?? text.Trim ();
            } catch (Exception ex) when (ex is JsonException or InvalidOperationException) {
                return text.Trim ();
            }
        }

        /// <summary>Word timings from character timings (ElevenLabs): each word starts when its first character does.</summary>
        protected static List<WordTiming> WordsFromCharacters (SpeechChunk chunk, IReadOnlyList<double> startSeconds, int offset = 0)
        {
            var words = new List<WordTiming> ();
            foreach (var start in chunk.WordStarts ()) {
                var index = start + offset;
                if (index >= 0 && index < startSeconds.Count)
                    words.Add (new WordTiming (start, TimeSpan.FromSeconds (startSeconds[index])));
            }
            return words;
        }

        /// <summary>Word timings from mark timepoints (<c>c123</c> at 1.5 s), as SSML engines report them.</summary>
        protected static List<WordTiming> WordsFromMarks (IEnumerable<(string Mark, double Seconds)> marks)
        {
            var words = new List<WordTiming> ();
            foreach (var (mark, seconds) in marks)
                if (mark.StartsWith ('c') && int.TryParse (mark.AsSpan (1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
                    words.Add (new WordTiming (index, TimeSpan.FromSeconds (seconds)));
            return words.OrderBy (w => w.Offset).ToList ();
        }
    }
}
