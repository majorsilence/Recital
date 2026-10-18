using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Majorsilence.Recital.Tts.Cloud;
using Xunit;

namespace Majorsilence.Recital.Tts.Tests
{
    public class CloudEngineTests
    {
        private const string Key = "sk-TEST-KEY-must-never-leak-1234567890";
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static async Task<SpeechChunk> ChunkAsync ()
        {
            await using var book = await PlannerTests.OpenAsync ("pg-1661-sherlock-holmes-epub3.epub");
            return (await PlannerTests.AllChunksAsync (book)).First (c => c.Text.StartsWith ("“Wedlock suits you,”", StringComparison.Ordinal));
        }

        /// <summary>A service that records requests and answers with what the test gives it.</summary>
        private sealed class FakeService (Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
        {
            public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

            protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync (cancellationToken);
                Requests.Add ((request, body));
                return answer (request, body);
            }
        }

        private static HttpResponseMessage Json (string json) => new (HttpStatusCode.OK) { Content = new StringContent (json, Encoding.UTF8, "application/json") };

        private static byte[] Silence (int rate, TimeSpan duration) => new byte[new PcmFormat (rate, 1, 16).Bytes (duration)];

        [Fact]
        public async Task ElevenLabsGivesExactWordTimingsFromItsCharacterAlignment ()
        {
            var chunk = await ChunkAsync ();
            var starts = new JsonArray (Enumerable.Range (0, chunk.Text.Length).Select (i => (JsonNode)JsonValue.Create (i * 0.05)).ToArray ());
            var service = new FakeService ((_, _) => Json (new JsonObject {
                ["audio_base64"] = Convert.ToBase64String (Silence (22050, TimeSpan.FromSeconds (chunk.Text.Length * 0.05))),
                ["alignment"] = new JsonObject { ["characters"] = new JsonArray (), ["character_start_times_seconds"] = starts },
            }.ToJsonString ()));
            var synthesizer = new ElevenLabsSynthesizer (new HttpClient (service), Key);
            var audio = await synthesizer.SynthesizeAsync (chunk, new TtsSettings ("voice-1", Rate: 1.1), "en", Ct);

            var (request, body) = service.Requests.Single ();
            Assert.Equal (Key, request.Headers.GetValues ("xi-api-key").Single ());
            Assert.Contains ("/text-to-speech/voice-1/with-timestamps", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal (chunk.Text, JsonNode.Parse (body)!["text"]!.GetValue<string> ());
            Assert.Equal (chunk.WordStarts ().Count (), audio.Words.Count);
            Assert.All (audio.Words, w => Assert.Equal (TimeSpan.FromSeconds (w.CharIndex * 0.05), w.Offset));
        }

        [Fact]
        public async Task GoogleTimesTheMarksAndTakesTheKeyInAHeader ()
        {
            var chunk = await ChunkAsync ();
            var service = new FakeService ((_, body) => {
                var ssml = XDocument.Parse (JsonNode.Parse (body)!["input"]!["ssml"]!.GetValue<string> ());
                XNamespace ns = "http://www.w3.org/2001/10/synthesis";
                var marks = ssml.Descendants (ns + "mark").Select ((m, i) => (JsonNode)new JsonObject { ["markName"] = m.Attribute ("name")!.Value, ["timeSeconds"] = i * 0.3 }).ToArray ();
                return Json (new JsonObject {
                    ["audioContent"] = Convert.ToBase64String (Wave.Write (new PcmFormat (24000, 1, 16), Silence (24000, TimeSpan.FromSeconds (2)))),
                    ["timepoints"] = new JsonArray (marks),
                }.ToJsonString ());
            });
            var audio = await new GoogleSynthesizer (new HttpClient (service), Key).SynthesizeAsync (chunk, new TtsSettings ("en-GB-Neural2-B"), "en-GB", Ct);

            var (request, body) = service.Requests.Single ();
            Assert.Equal (Key, request.Headers.GetValues ("X-Goog-Api-Key").Single ());
            Assert.DoesNotContain (Key, request.RequestUri!.ToString (), StringComparison.Ordinal);
            Assert.Equal ("en-GB-Neural2-B", JsonNode.Parse (body)!["voice"]!["name"]!.GetValue<string> ());
            Assert.Equal (24000, audio.Format.SampleRate);
            Assert.Equal (chunk.WordStarts ().ToList (), audio.Words.Select (w => w.CharIndex));
        }

        [Fact]
        public async Task OpenAiAndAzureReturnAudioToInterpolate ()
        {
            var chunk = await ChunkAsync ();
            var openAi = new FakeService ((_, _) => new HttpResponseMessage (HttpStatusCode.OK) { Content = new ByteArrayContent (Silence (24000, TimeSpan.FromSeconds (1))) });
            var audio = await new OpenAiSynthesizer (new HttpClient (openAi), Key).SynthesizeAsync (chunk, new TtsSettings ("nova", Rate: 1.25), "en", Ct);
            Assert.Equal ("Bearer " + Key, openAi.Requests[0].Request.Headers.Authorization!.ToString ());
            var openAiBody = JsonNode.Parse (openAi.Requests[0].Body)!;
            Assert.Equal ("nova", openAiBody["voice"]!.GetValue<string> ());
            Assert.Equal ("pcm", openAiBody["response_format"]!.GetValue<string> ());
            Assert.Equal (TimeSpan.FromSeconds (1), audio.Duration);
            Assert.Empty (audio.Words);

            var azure = new FakeService ((_, _) => new HttpResponseMessage (HttpStatusCode.OK) { Content = new ByteArrayContent (Wave.Write (new PcmFormat (24000, 1, 16), Silence (24000, TimeSpan.FromSeconds (1)))) });
            audio = await new AzureSynthesizer (new HttpClient (azure), Key, "westeurope").SynthesizeAsync (chunk, new TtsSettings ("en-GB-SoniaNeural", "en-GB-RyanNeural"), "en-GB", Ct);
            var (request, body) = azure.Requests.Single ();
            Assert.Equal ("westeurope.tts.speech.microsoft.com", request.RequestUri!.Host);
            Assert.Equal (Key, request.Headers.GetValues ("Ocp-Apim-Subscription-Key").Single ());
            var ssml = XDocument.Parse (body);
            XNamespace ns = "http://www.w3.org/2001/10/synthesis";
            Assert.Equal ("en-GB-SoniaNeural", ssml.Root!.Element (ns + "voice")!.Attribute ("name")!.Value);
            // Dialogue in the second voice (plan 3.2).
            Assert.Contains (ssml.Descendants (ns + "voice"), v => v.Attribute ("name")!.Value == "en-GB-RyanNeural");
            Assert.Equal (TimeSpan.FromSeconds (1), audio.Duration);
        }

        /// <summary>Plan Phase 9: an API key never appears in an error, an exception's text, the cache or the cache's key.</summary>
        [Theory]
        [InlineData (HttpStatusCode.Unauthorized)]
        [InlineData (HttpStatusCode.TooManyRequests)]
        [InlineData (HttpStatusCode.InternalServerError)]
        public async Task KeysNeverLeakIntoErrorsOrTheCache (HttpStatusCode status)
        {
            var chunk = await ChunkAsync ();
            // A careless service that echoes the request's headers back in its error.
            var service = new FakeService ((request, _) => new HttpResponseMessage (status) {
                Content = new StringContent ("""{"error":{"message":"bad request"}}""", Encoding.UTF8, "application/json"),
            });
            ISpeechSynthesizer[] synthesizers = [
                new ElevenLabsSynthesizer (new HttpClient (service), Key), new OpenAiSynthesizer (new HttpClient (service), Key),
                new AzureSynthesizer (new HttpClient (service), Key, "westeurope"), new GoogleSynthesizer (new HttpClient (service), Key),
            ];
            foreach (var synthesizer in synthesizers) {
                var ex = await Assert.ThrowsAsync<TtsServiceException> (() => synthesizer.SynthesizeAsync (chunk, new TtsSettings (), "en", Ct));
                Assert.DoesNotContain (Key, ex.ToString (), StringComparison.Ordinal);
                Assert.Equal (status == HttpStatusCode.Unauthorized, ex.KeyRefused);
                Assert.DoesNotContain (Key, synthesizer.ToString () ?? "", StringComparison.Ordinal);
            }

            // The cache keys and stores audio by engine, voice, speed and text: no key.
            var directory = Path.Combine (Path.GetTempPath (), "recital-keys-" + Guid.NewGuid ().ToString ("N"));
            try {
                var cache = new AudioCache (directory);
                var cacheKey = AudioCache.Key ("openai", new TtsSettings ("nova"), "en", chunk);
                await cache.PutAsync (cacheKey, new SynthesizedAudio (PcmFormat.Speech, [1, 2, 3, 4], []), Ct);
                Assert.DoesNotContain (Key, cacheKey, StringComparison.Ordinal);
                foreach (var file in Directory.EnumerateFiles (directory))
                    Assert.DoesNotContain (Key, Encoding.UTF8.GetString (await File.ReadAllBytesAsync (file, Ct)), StringComparison.Ordinal);
            } finally {
                Directory.Delete (directory, recursive: true);
            }
        }
    }
}
