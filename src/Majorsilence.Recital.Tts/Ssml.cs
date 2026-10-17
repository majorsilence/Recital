using System.Globalization;
using System.Security;
using System.Text;

namespace Majorsilence.Recital.Tts
{
    /// <summary>
    /// A chunk as SSML (plan 3.2), for engines that take it: breaks for its pauses, <c>emphasis</c>, an
    /// optional separate voice for dialogue, and, when asked, a <c>mark</c> before each word naming its
    /// character index (<c>c123</c>), which marks-reporting engines echo back as progress.
    /// </summary>
    public static class Ssml
    {
        /// <param name="language">The book's language (<c>xml:lang</c>).</param>
        /// <param name="dialogueVoice">A voice for quoted speech, or null to read it in the narrator's voice.</param>
        /// <param name="marks">Put a mark before every word.</param>
        public static string Build (SpeechChunk chunk, string? language = null, string? dialogueVoice = null, bool marks = false)
        {
            var text = chunk.Text;
            var ssml = new StringBuilder ();
            ssml.Append ("<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"")
                .Append (SecurityElement.Escape (string.IsNullOrEmpty (language) ? "en" : language)).Append ("\">");
            if (chunk.PauseBefore > TimeSpan.Zero)
                ssml.Append ("<break time=\"").Append (((int)chunk.PauseBefore.TotalMilliseconds).ToString (CultureInfo.InvariantCulture)).Append ("ms\"/>");

            // Open and close elements at span edges, keeping nesting valid by closing inner ones first.
            var events = new SortedDictionary<int, List<(bool Open, ProsodySpan Span)>> ();
            foreach (var span in chunk.Spans.Where (s => s.Kind != ProsodyKind.Dialogue || dialogueVoice is not null)) {
                Add (events, span.Start, (true, span));
                Add (events, span.End, (false, span));
            }
            var wordStarts = marks ? chunk.WordStarts ().ToHashSet () : [];
            var openNow = new List<ProsodySpan> ();

            for (var i = 0; i <= text.Length; i++) {
                if (events.TryGetValue (i, out var at)) {
                    foreach (var (_, span) in at.Where (e => !e.Open))
                        CloseThrough (ssml, openNow, span, dialogueVoice);
                    foreach (var (_, span) in at.Where (e => e.Open).OrderByDescending (e => e.Span.End)) {
                        ssml.Append (OpenTag (span, dialogueVoice));
                        openNow.Add (span);
                    }
                }
                if (i == text.Length)
                    break;
                if (wordStarts.Contains (i))
                    ssml.Append ("<mark name=\"c").Append (i.ToString (CultureInfo.InvariantCulture)).Append ("\"/>");
                ssml.Append (text[i] switch { '<' => "&lt;", '>' => "&gt;", '&' => "&amp;", '"' => "&quot;", _ => text[i].ToString () });
            }
            for (var k = openNow.Count - 1; k >= 0; k--)
                ssml.Append (CloseTag (openNow[k]));

            if (chunk.PauseAfter > TimeSpan.Zero)
                ssml.Append ("<break time=\"").Append (((int)chunk.PauseAfter.TotalMilliseconds).ToString (CultureInfo.InvariantCulture)).Append ("ms\"/>");
            ssml.Append ("</speak>");
            return ssml.ToString ();
        }

        /// <summary>Closes <paramref name="span"/>, closing and reopening any opened after it (overlapping spans).</summary>
        private static void CloseThrough (StringBuilder ssml, List<ProsodySpan> openNow, ProsodySpan span, string? dialogueVoice)
        {
            var index = openNow.IndexOf (span);
            if (index < 0)
                return;
            var reopen = openNow.Skip (index + 1).ToList ();
            for (var k = openNow.Count - 1; k >= index; k--)
                ssml.Append (CloseTag (openNow[k]));
            openNow.RemoveRange (index, openNow.Count - index);
            foreach (var s in reopen) {
                ssml.Append (OpenTag (s, dialogueVoice));
                openNow.Add (s);
            }
        }

        private static void Add (SortedDictionary<int, List<(bool, ProsodySpan)>> events, int at, (bool, ProsodySpan) e)
        {
            if (!events.TryGetValue (at, out var list))
                events[at] = list = [];
            list.Add (e);
        }

        private static string OpenTag (ProsodySpan span, string? dialogueVoice) => span.Kind switch {
            ProsodyKind.Emphasis => "<emphasis level=\"moderate\">",
            ProsodyKind.Strong => "<emphasis level=\"strong\">",
            _ => "<voice name=\"" + SecurityElement.Escape (dialogueVoice ?? "") + "\">",
        };

        private static string CloseTag (ProsodySpan span) => span.Kind == ProsodyKind.Dialogue ? "</voice>" : "</emphasis>";
    }
}
