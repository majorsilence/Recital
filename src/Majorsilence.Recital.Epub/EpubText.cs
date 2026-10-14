using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html;

namespace Majorsilence.Recital.Epub
{
    internal static partial class EpubText
    {
        static EpubText ()
        {
            // windows-1252 and friends, which older books declare.
            Encoding.RegisterProvider (CodePagesEncodingProvider.Instance);
        }

        /// <summary>
        /// Decodes an XML/XHTML file: byte-order mark first, then the XML declaration's encoding, else UTF-8.
        /// </summary>
        public static string Decode (byte[] bytes)
        {
            using var reader = new StreamReader (new MemoryStream (bytes), DeclaredEncoding (bytes) ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd ();
        }

        private static Encoding? DeclaredEncoding (byte[] bytes)
        {
            // The declaration is ASCII in every ASCII-compatible encoding; UTF-16 files carry a BOM instead.
            var head = Encoding.ASCII.GetString (bytes, 0, Math.Min (bytes.Length, 256));
            var match = DeclarationEncoding ().Match (head);

            if (!match.Success)
                return null;

            try {
                return Encoding.GetEncoding (match.Groups[1].Value);
            } catch (ArgumentException) {
                return null;
            }
        }

        /// <summary>
        /// Rewrites HTML named entities (<c>&amp;nbsp;</c>, <c>&amp;mdash;</c>, ...) as numeric references.
        /// XHTML served as XML may only use the five XML entities, but real books use the HTML ones anyway,
        /// and a strict XML parser rejects the whole document for it. Unknown names are left alone so the
        /// document still fails (and falls back) when it really is broken.
        /// </summary>
        public static string ReplaceHtmlEntities (string text)
        {
            if (!text.Contains ('&'))
                return text;

            return NamedEntity ().Replace (text, match => {
                var name = match.Groups[1].Value;

                if (name is "amp" or "lt" or "gt" or "quot" or "apos")
                    return match.Value;

                var symbol = HtmlEntityProvider.Resolver.GetSymbol (name + ";");
                if (symbol is null)
                    return match.Value;

                var builder = new StringBuilder ();
                foreach (var rune in symbol.EnumerateRunes ())
                    builder.Append ("&#x").Append (rune.Value.ToString ("X", System.Globalization.CultureInfo.InvariantCulture)).Append (';');
                return builder.ToString ();
            });
        }

        /// <summary>Parses package-level XML (container, OPF, NCX, encryption) without touching the network.</summary>
        public static XDocument ParseXml (byte[] bytes)
        {
            var settings = new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreComments = true,
            };

            using var reader = XmlReader.Create (new StringReader (ReplaceHtmlEntities (Decode (bytes))), settings);
            return XDocument.Load (reader, LoadOptions.None);
        }

        [GeneratedRegex (@"^\s*<\?xml[^>]*encoding\s*=\s*[""']([A-Za-z0-9._-]+)[""']")]
        private static partial Regex DeclarationEncoding ();

        [GeneratedRegex (@"&([A-Za-z][A-Za-z0-9]{1,31});")]
        private static partial Regex NamedEntity ();
    }
}
