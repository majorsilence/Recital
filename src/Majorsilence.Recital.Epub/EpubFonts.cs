using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Majorsilence.Recital.Epub
{
    /// <summary>Embedded font detection, and undoing the two font obfuscation schemes EPUBs use.</summary>
    internal static class EpubFonts
    {
        public const string IdpfAlgorithm = "http://www.idpf.org/2008/embedding";
        public const string AdobeAlgorithm = "http://ns.adobe.com/pdf/enc#RC";

        private const int IdpfLength = 1040;
        private const int AdobeLength = 1024;

        private static readonly HashSet<string> font_media_types = new (StringComparer.OrdinalIgnoreCase) {
            "font/ttf", "font/otf", "font/woff", "font/woff2", "font/sfnt", "font/collection",
            "application/font-sfnt", "application/font-woff", "application/x-font-ttf",
            "application/x-font-truetype", "application/x-font-opentype", "application/vnd.ms-opentype",
            "application/x-font-otf",
        };

        public static bool IsFontMediaType (string mediaType) => font_media_types.Contains (mediaType);

        public static bool HasFontExtension (string href)
            => href.EndsWith (".ttf", StringComparison.OrdinalIgnoreCase)
            || href.EndsWith (".otf", StringComparison.OrdinalIgnoreCase)
            || href.EndsWith (".woff", StringComparison.OrdinalIgnoreCase)
            || href.EndsWith (".woff2", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Reads META-INF/encryption.xml: archive path → algorithm URI for every encrypted resource.
        /// </summary>
        public static Dictionary<string, string> ReadEncryption (XDocument document)
        {
            XNamespace enc = "http://www.w3.org/2001/04/xmlenc#";
            var result = new Dictionary<string, string> (StringComparer.Ordinal);

            foreach (var data in document.Descendants (enc + "EncryptedData")) {
                var algorithm = data.Element (enc + "EncryptionMethod")?.Attribute ("Algorithm")?.Value;
                var uri = data.Element (enc + "CipherData")?.Element (enc + "CipherReference")?.Attribute ("URI")?.Value;

                if (algorithm is null || uri is null)
                    continue;

                // CipherReference URIs are relative to the container root.
                var (path, _) = EpubPaths.Resolve (string.Empty, uri);
                if (path is not null)
                    result[path] = algorithm.Trim ();
            }

            return result;
        }

        public static FontObfuscation FromAlgorithm (string? algorithm) => algorithm switch {
            IdpfAlgorithm => FontObfuscation.Idpf,
            AdobeAlgorithm => FontObfuscation.Adobe,
            _ => FontObfuscation.None,
        };

        /// <summary>
        /// XORs the obfuscated prefix in place. The operation is its own inverse, so this also obfuscates.
        /// Returns false when the identifier cannot produce a key (Adobe needs a UUID).
        /// </summary>
        public static bool Deobfuscate (byte[] data, FontObfuscation scheme, string uniqueIdentifier)
        {
            byte[] key;
            int length;

            switch (scheme) {
                case FontObfuscation.Idpf:
                    // Key: SHA-1 of the package's unique identifier with all XML whitespace removed.
                    var identifier = new string (uniqueIdentifier.Where (c => c is not (' ' or '\t' or '\r' or '\n')).ToArray ());
#pragma warning disable CA5350 // SHA-1 is what the IDPF obfuscation spec mandates; this is not security.
                    key = SHA1.HashData (Encoding.UTF8.GetBytes (identifier));
#pragma warning restore CA5350
                    length = IdpfLength;
                    break;

                case FontObfuscation.Adobe:
                    // Key: the 16 bytes of the identifier's UUID ("urn:uuid:" and dashes removed).
                    var hex = uniqueIdentifier.Trim ();
                    if (hex.StartsWith ("urn:uuid:", StringComparison.OrdinalIgnoreCase))
                        hex = hex["urn:uuid:".Length..];
                    hex = hex.Replace ("-", string.Empty, StringComparison.Ordinal);
                    if (hex.Length != 32 || !hex.All (char.IsAsciiHexDigit))
                        return false;
                    key = Convert.FromHexString (hex);
                    length = AdobeLength;
                    break;

                default:
                    return true;
            }

            var count = Math.Min (length, data.Length);
            for (var i = 0; i < count; i++)
                data[i] ^= key[i % key.Length];

            return true;
        }
    }
}
