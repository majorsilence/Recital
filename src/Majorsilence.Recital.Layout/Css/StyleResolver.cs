using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using Majorsilence.Recital.Epub;

namespace Majorsilence.Recital.Layout.Css
{
    /// <summary>A stylesheet and the archive path its urls resolve against (empty for inline styles).</summary>
    public sealed record StyleSheetSource (string Path, string Text, bool UserAgent = false);

    /// <summary>
    /// Parsed stylesheets by their text. A book's chapters usually share the same few sheets, and parsing
    /// is far more expensive than walking the parsed rules, so one parse serves every chapter.
    /// </summary>
    public sealed class StyleSheetCache
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ICssStyleSheet> sheets = new (StringComparer.Ordinal);

        internal ICssStyleSheet Parse (string text) => sheets.GetOrAdd (text, static t => new CssParser ().ParseStyleSheet (t));
    }

    /// <summary>An <c>@font-face</c> rule with its font file resolved to an archive path.</summary>
    public sealed record FontFaceSource (string Family, int Weight, bool Italic, string Path);

    /// <summary>
    /// The cascade: user-agent sheet, then the book's sheets in document order, then inline styles, with
    /// !important and specificity as in CSS 2.1. AngleSharp.Css parses sheets and matches selectors;
    /// computing values (and the subset of properties that matter) is ours.
    /// </summary>
    public sealed class StyleResolver
    {
        private static readonly Lazy<string> ua_sheet = new (() => {
            using var stream = typeof (StyleResolver).Assembly.GetManifestResourceStream ("Majorsilence.Recital.Layout.Css.ua.css")!;
            return new StreamReader (stream).ReadToEnd ();
        });

        private static readonly StyleSheetCache ua_cache = new ();
        private readonly StyleSheetCache cache;

        private readonly LayoutSettings settings;
        private readonly List<Rule> rules = [];
        private readonly Dictionary<string, List<Rule>> by_id = new (StringComparer.Ordinal);
        private readonly Dictionary<string, List<Rule>> by_class = new (StringComparer.Ordinal);
        private readonly Dictionary<string, List<Rule>> by_tag = new (StringComparer.OrdinalIgnoreCase);
        private readonly List<Rule> universal = [];
        private readonly CssParser parser = new ();

        public StyleResolver (IEnumerable<StyleSheetSource> authorSheets, LayoutSettings settings, StyleSheetCache? cache = null)
        {
            this.settings = settings;
            this.cache = cache ?? new StyleSheetCache ();

            AddSheet (new StyleSheetSource (string.Empty, ua_sheet.Value, UserAgent: true));
            foreach (var sheet in authorSheets)
                AddSheet (sheet);
        }

        public IReadOnlyList<FontFaceSource> FontFaces => font_faces;
        private readonly List<FontFaceSource> font_faces = [];

        /// <summary>
        /// Collects a content document's stylesheets in document order, following <c>@import</c>, reading
        /// files through <paramref name="read"/>.
        /// </summary>
        public static async Task<List<StyleSheetSource>> CollectAsync (ContentDocument document, Func<string, Task<byte[]?>> read, StyleSheetCache? cache = null)
        {
            cache ??= new StyleSheetCache ();
            var sheets = new List<StyleSheetSource> ();
            var directory = EpubPathsDirectory (document.SpineItem.Content.Path ?? string.Empty);
            var seen = new HashSet<string> (StringComparer.Ordinal);

            foreach (var element in document.Document.QuerySelectorAll ("link, style")) {
                if (element.LocalName == "style") {
                    await AddWithImportsAsync (sheets, document.SpineItem.Content.Path ?? string.Empty, element.TextContent, read, seen, cache).ConfigureAwait (false);
                    continue;
                }

                var rel = (element.GetAttribute ("rel") ?? string.Empty).Split (' ', StringSplitOptions.RemoveEmptyEntries);
                if (!rel.Contains ("stylesheet", StringComparer.OrdinalIgnoreCase) || rel.Contains ("alternate", StringComparer.OrdinalIgnoreCase))
                    continue;

                if (element.GetAttribute ("href") is { } href && ResolvePath (directory, href) is { } path)
                    await AddFileAsync (sheets, path, read, seen, cache).ConfigureAwait (false);
            }

            return sheets;
        }

        private static async Task AddFileAsync (List<StyleSheetSource> sheets, string path, Func<string, Task<byte[]?>> read, HashSet<string> seen, StyleSheetCache cache)
        {
            if (!seen.Add (path) || await read (path).ConfigureAwait (false) is not { } bytes)
                return;

            var text = System.Text.Encoding.UTF8.GetString (bytes).TrimStart ('﻿');
            await AddWithImportsAsync (sheets, path, text, read, seen, cache).ConfigureAwait (false);
        }

        private static async Task AddWithImportsAsync (List<StyleSheetSource> sheets, string path, string text, Func<string, Task<byte[]?>> read, HashSet<string> seen, StyleSheetCache cache)
        {
            // Imported sheets come before the importing sheet's own rules.
            foreach (var import in cache.Parse (text).Rules.OfType<ICssImportRule> ())
                if (import.Href is { } importHref && ResolvePath (EpubPathsDirectory (path), importHref) is { } importPath)
                    await AddFileAsync (sheets, importPath, read, seen, cache).ConfigureAwait (false);

            sheets.Add (new StyleSheetSource (path, text));
        }

        /// <summary>Computes an element's style from its parent's.</summary>
        public ComputedStyle Compute (IElement element, ComputedStyle parent, float rootFontSize)
        {
            var declarations = new List<(int Level, bool Inline, Priority Specificity, int Order, string Name, string Value)> ();

            foreach (var rule in Candidates (element)) {
                if (!rule.Selector.Match (element, null))
                    continue;

                foreach (var (name, value, important) in rule.Declarations)
                    declarations.Add ((Level (rule.UserAgent, important), false, rule.Selector.Specificity, rule.Order, name, value));
            }

            if (element.GetAttribute ("style") is { Length: > 0 } inline && parser.ParseDeclaration (inline) is { } inlineStyle) {
                var order = 0;
                foreach (var property in inlineStyle)
                    declarations.Add ((Level (false, property.IsImportant), true, Priority.Inline, order++, property.Name, property.Value));
            }

            declarations.Sort ((a, b) => {
                var c = a.Level.CompareTo (b.Level);
                if (c == 0) c = a.Inline.CompareTo (b.Inline);
                if (c == 0) c = a.Specificity.CompareTo (b.Specificity);
                if (c == 0) c = a.Order.CompareTo (b.Order);
                return c;
            });

            var specified = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase);
            foreach (var d in declarations)
                specified[d.Name] = d.Value;

            return ComputeValues (element, specified, parent, rootFontSize);
        }

        /// <summary>The root (html) element's parent style: initial values at the reader's font size.</summary>
        public ComputedStyle InitialStyle () => new () {
            FontSize = settings.FontSize,
            FontFamilies = ["serif"],
            Widows = settings.Widows,
            Orphans = settings.Orphans,
        };

        // CSS 2.1 cascade order, lowest first: UA normal, author normal, author important, UA important.
        private static int Level (bool userAgent, bool important) => (userAgent, important) switch {
            (true, false) => 0,
            (false, false) => 1,
            (false, true) => 2,
            (true, true) => 3,
        };

        private IEnumerable<Rule> Candidates (IElement element)
        {
            IEnumerable<Rule> candidates = universal;

            if (by_tag.TryGetValue (element.LocalName, out var tagRules))
                candidates = candidates.Concat (tagRules);
            if (element.Id is { Length: > 0 } id && by_id.TryGetValue (id, out var idRules))
                candidates = candidates.Concat (idRules);
            foreach (var cls in element.ClassList)
                if (by_class.TryGetValue (cls, out var classRules))
                    candidates = candidates.Concat (classRules);

            return candidates.Distinct ();
        }

        private void AddSheet (StyleSheetSource source)
        {
            var sheet = (source.UserAgent ? ua_cache : cache).Parse (source.Text);
            AddRules (sheet.Rules, source);
        }

        private void AddRules (ICssRuleList list, StyleSheetSource source)
        {
            foreach (var rule in list) {
                switch (rule) {
                    case ICssStyleRule style:
                        AddStyleRule (style, source);
                        break;
                    case ICssMediaRule media when MediaMatches (media.ConditionText):
                        AddRules (media.Rules, source);
                        break;
                    case ICssSupportsRule supports when SupportsMatches (supports.ConditionText):
                        AddRules (supports.Rules, source);
                        break;
                    case ICssFontFaceRule face:
                        AddFontFace (face, source);
                        break;
                }
            }
        }

        private void AddStyleRule (ICssStyleRule rule, StyleSheetSource source)
        {
            var declarations = rule.Style.Select (p => (p.Name, p.Value, p.IsImportant)).ToList ();
            if (declarations.Count == 0)
                return;

            // A selector list matches through each member separately, with that member's own specificity.
            // Compound and complex selectors are enumerable too (over their parts), so only a top-level
            // comma marks a list.
            IEnumerable<ISelector> selectors = rule.Selector is IEnumerable<ISelector> list && HasTopLevelComma (rule.Selector.Text)
                ? list
                : [rule.Selector];

            foreach (var selector in selectors) {
                // Pseudo-elements style only part of an element (::first-letter) or generated content
                // (::before); matching them as the whole element would be wrong.
                if (HasPseudoElement (selector.Text))
                    continue;

                var entry = new Rule (selector, declarations, rules.Count, source.UserAgent);
                rules.Add (entry);

                var (kind, key) = IndexKey (selector.Text);
                var bucket = kind switch {
                    '#' => GetBucket (by_id, key),
                    '.' => GetBucket (by_class, key),
                    't' => GetBucket (by_tag, key),
                    _ => universal,
                };
                bucket.Add (entry);
            }
        }

        private static List<Rule> GetBucket (Dictionary<string, List<Rule>> index, string key)
        {
            if (!index.TryGetValue (key, out var bucket))
                index[key] = bucket = [];
            return bucket;
        }

        private void AddFontFace (ICssFontFaceRule face, StyleSheetSource source)
        {
            var family = face.Family.Trim ().Trim ('"', '\'');
            var src = face.Source;
            var start = src.IndexOf ("url(", StringComparison.OrdinalIgnoreCase);
            if (family.Length == 0 || start < 0)
                return;

            var end = src.IndexOf (')', start);
            var url = src[(start + 4)..(end < 0 ? src.Length : end)].Trim ().Trim ('"', '\'');

            if (ResolvePath (EpubPathsDirectory (source.Path), url) is not { } path)
                return;

            var weight = CssValues.FontWeight (string.IsNullOrEmpty (face.Weight) ? "normal" : face.Weight, 400) ?? 400;
            var italic = face.Style is { } style && (style.Contains ("italic", StringComparison.OrdinalIgnoreCase) || style.Contains ("oblique", StringComparison.OrdinalIgnoreCase));
            font_faces.Add (new FontFaceSource (family, weight, italic, path));
        }

        /// <summary>
        /// Media queries: a query matches if its media type is all/screen (or absent) and every feature it
        /// tests is one this engine can answer and does: width against the page, a light colour scheme,
        /// colour, a portrait or landscape page. Anything unknown (amzn-kf8, print, ...) does not match.
        /// </summary>
        private bool MediaMatches (string condition)
        {
            if (string.IsNullOrWhiteSpace (condition))
                return true;

            foreach (var query in condition.ToLowerInvariant ().Split (',')) {
                var text = query.Trim ();
                var negate = text.StartsWith ("not ", StringComparison.Ordinal);
                if (negate)
                    text = text[4..];
                text = text.Replace ("only ", string.Empty, StringComparison.Ordinal);

                var parts = text.Split (" and ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var matches = parts.All (MediaPartMatches);

                if (matches != negate)
                    return true;
            }

            return false;
        }

        private bool MediaPartMatches (string part)
        {
            if (part is "all" or "screen")
                return true;
            if (!part.StartsWith ('(') || !part.EndsWith (')'))
                return false;

            var feature = part[1..^1].Split (':', 2, StringSplitOptions.TrimEntries);
            var name = feature[0];
            var value = feature.Length > 1 ? feature[1] : null;
            float? Px () => value is null ? null : CssValues.Length (value, 16, 16, 1, allowAuto: false)?.Resolve (0);

            return name switch {
                "min-width" => Px () is { } min && settings.PageWidth >= min,
                "max-width" => Px () is { } max && settings.PageWidth <= max,
                "min-height" => Px () is { } minH && settings.PageHeight >= minH,
                "max-height" => Px () is { } maxH && settings.PageHeight <= maxH,
                "orientation" => value == (settings.PageHeight >= settings.PageWidth ? "portrait" : "landscape"),
                "prefers-color-scheme" => value == "light",
                "color" => true,
                _ => false,
            };
        }

        /// <summary>
        /// <c>@supports</c>: this engine supports almost none of what books test for (grid, flex,
        /// font-variant-numeric, ...), so only a negated condition matches.
        /// </summary>
        private static bool SupportsMatches (string condition)
            => condition.TrimStart ().StartsWith ("not", StringComparison.OrdinalIgnoreCase);

        private static bool HasTopLevelComma (string selector)
        {
            var depth = 0;
            foreach (var c in selector) {
                if (c is '[' or '(')
                    depth++;
                else if (c is ']' or ')')
                    depth--;
                else if (c == ',' && depth == 0)
                    return true;
            }
            return false;
        }

        private static bool HasPseudoElement (string selector)
        {
            var lower = selector.ToLowerInvariant ();
            return lower.Contains ("::", StringComparison.Ordinal)
                || lower.Contains (":before", StringComparison.Ordinal)
                || lower.Contains (":after", StringComparison.Ordinal)
                || lower.Contains (":first-letter", StringComparison.Ordinal)
                || lower.Contains (":first-line", StringComparison.Ordinal)
                || lower.Contains (":marker", StringComparison.Ordinal);
        }

        /// <summary>
        /// The bucket a selector goes in, from its rightmost compound: '#' + id, '.' + a class, 't' + a tag,
        /// or '*' for anything else. Only used to skip rules that cannot match; matching itself is AngleSharp's.
        /// </summary>
        internal static (char Kind, string Key) IndexKey (string selector)
        {
            var depth = 0;
            var start = 0;

            for (var i = 0; i < selector.Length; i++) {
                var c = selector[i];
                if (c is '[' or '(')
                    depth++;
                else if (c is ']' or ')')
                    depth--;
                else if (depth == 0 && c is ' ' or '>' or '+' or '~' && !(c == '~' && i + 1 < selector.Length && selector[i + 1] == '='))
                    start = i + 1;
            }

            var compound = selector[start..].Trim ();
            string? tag = null, cls = null;

            depth = 0;
            for (var i = 0; i < compound.Length; i++) {
                var c = compound[i];
                if (c is '[' or '(') {
                    depth++;
                    continue;
                }
                if (c is ']' or ')') {
                    depth--;
                    continue;
                }
                if (depth > 0)
                    continue;

                if (c == '#')
                    return ('#', Identifier (compound, i + 1));
                if (c == '.' && cls is null)
                    cls = Identifier (compound, i + 1);
                if (i == 0 && (char.IsLetter (c) || c == '*' || c == '|')) {
                    var name = Identifier (compound, i, allowNamespace: true);
                    var bar = name.IndexOf ('|');
                    tag = bar >= 0 ? name[(bar + 1)..] : name;
                }
            }

            if (cls is { Length: > 0 })
                return ('.', cls);
            if (tag is { Length: > 0 } && tag != "*")
                return ('t', tag.ToLowerInvariant ());
            return ('*', string.Empty);
        }

        private static string Identifier (string text, int start, bool allowNamespace = false)
        {
            var end = start;
            while (end < text.Length && (char.IsLetterOrDigit (text[end]) || text[end] is '-' or '_' || text[end] == '\\' || allowNamespace && text[end] is '|' or '*'))
                end++;
            return text[start..end].Replace ("\\", string.Empty, StringComparison.Ordinal);
        }

        private ComputedStyle ComputeValues (IElement element, Dictionary<string, string> specified, ComputedStyle parent, float rootFontSize)
        {
            var style = parent.Inherit ();
            var scale = settings.Scale;

            string? Get (string name)
            {
                if (!specified.TryGetValue (name, out var value) || string.IsNullOrWhiteSpace (value))
                    return null;
                value = value.Trim ();
                // inherit/unset on an inherited property is the default here; on others we keep initial.
                return value.ToLowerInvariant () is "inherit" or "unset" or "initial" or "revert" ? null : value;
            }

            // Font size first: em lengths below depend on it.
            if (Get ("font-size") is { } fontSize && CssValues.FontSize (fontSize, parent.FontSize, rootFontSize, settings.FontSize, scale) is { } size)
                style.FontSize = size;

            var em = style.FontSize;
            CssLength? Length (string name, bool allowAuto = true)
                => Get (name) is { } v ? CssValues.Length (v, em, rootFontSize, scale, allowAuto) : null;

            if (Get ("font-family") is { } family)
                style.FontFamilies = CssValues.FontFamilies (family);
            if (Get ("font-weight") is { } weight && CssValues.FontWeight (weight, parent.FontWeight) is { } w)
                style.FontWeight = w;
            if (Get ("font-style") is { } fontStyle)
                style.Italic = fontStyle.Contains ("italic", StringComparison.OrdinalIgnoreCase) || fontStyle.Contains ("oblique", StringComparison.OrdinalIgnoreCase);
            if (Get ("font-variant") is { } variant)
                style.SmallCaps = variant.Contains ("small-caps", StringComparison.OrdinalIgnoreCase);
            if (Get ("font-variant-caps") is { } caps)
                style.SmallCaps = caps.Contains ("small-caps", StringComparison.OrdinalIgnoreCase);

            if (Get ("line-height") is { } lineHeight) {
                if (lineHeight.Equals ("normal", StringComparison.OrdinalIgnoreCase)) {
                    style.LineHeightPx = null;
                    style.LineHeightFactor = null;
                } else if (CssValues.Number (lineHeight) is { } factor) {
                    style.LineHeightPx = null;
                    style.LineHeightFactor = factor;
                } else if (CssValues.Length (lineHeight, em, rootFontSize, scale, allowAuto: false) is { } lh) {
                    style.LineHeightPx = lh.Resolve (em);
                    style.LineHeightFactor = null;
                }
            }

            if (Get ("text-align") is { } align) {
                style.TextAlign = align.ToLowerInvariant () switch {
                    "right" or "end" => TextAlign.Right,
                    "center" or "-webkit-center" => TextAlign.Center,
                    "justify" => TextAlign.Justify,
                    _ => TextAlign.Left,
                };
            }

            if (Length ("text-indent", allowAuto: false) is { } indent)
                style.TextIndent = indent;

            if (Get ("white-space") is { } whiteSpace) {
                style.WhiteSpace = whiteSpace.ToLowerInvariant () switch {
                    "nowrap" => WhiteSpace.NoWrap,
                    "pre" => WhiteSpace.Pre,
                    "pre-wrap" or "break-spaces" => WhiteSpace.PreWrap,
                    "pre-line" => WhiteSpace.PreLine,
                    _ => WhiteSpace.Normal,
                };
            }

            if (Get ("text-transform") is { } transform) {
                style.TextTransform = transform.ToLowerInvariant () switch {
                    "uppercase" => TextTransform.Uppercase,
                    "lowercase" => TextTransform.Lowercase,
                    "capitalize" => TextTransform.Capitalize,
                    _ => TextTransform.None,
                };
            }

            if (Get ("letter-spacing") is { } letter)
                style.LetterSpacing = letter.Equals ("normal", StringComparison.OrdinalIgnoreCase) ? 0 : CssValues.Length (letter, em, rootFontSize, scale, false)?.Resolve (0) ?? style.LetterSpacing;
            if (Get ("word-spacing") is { } word)
                style.WordSpacing = word.Equals ("normal", StringComparison.OrdinalIgnoreCase) ? 0 : CssValues.Length (word, em, rootFontSize, scale, false)?.Resolve (0) ?? style.WordSpacing;

            if (Get ("color") is { } color && CssValues.Color (color) is { } argb)
                style.Color = argb;
            style.BorderColor = style.Color;

            if (Get ("list-style-type") is { } listStyle)
                style.ListStyleType = listStyle.ToLowerInvariant ();
            if (Get ("widows") is { } widows && CssValues.Integer (widows) is { } wn && wn > 0)
                style.Widows = wn;
            if (Get ("orphans") is { } orphans && CssValues.Integer (orphans) is { } on && on > 0)
                style.Orphans = on;
            if (Get ("visibility") is { } visibility)
                style.Hidden = visibility.Equals ("hidden", StringComparison.OrdinalIgnoreCase) || visibility.Equals ("collapse", StringComparison.OrdinalIgnoreCase);

            // Not inherited from here on.
            style.Display = Get ("display")?.ToLowerInvariant () switch {
                "block" or "flex" or "grid" or "flow-root" or "table-caption" or "run-in" => Display.Block,
                "list-item" => Display.ListItem,
                "none" => Display.None,
                "table" or "inline-table" => Display.Table,
                "table-row-group" or "table-header-group" or "table-footer-group" => Display.TableRowGroup,
                "table-row" => Display.TableRow,
                "table-cell" => Display.TableCell,
                _ => Display.Inline,
            };

            style.MarginTop = Length ("margin-top") ?? CssLength.Zero;
            style.MarginRight = Length ("margin-right") ?? CssLength.Zero;
            style.MarginBottom = Length ("margin-bottom") ?? CssLength.Zero;
            style.MarginLeft = Length ("margin-left") ?? CssLength.Zero;
            style.PaddingTop = Length ("padding-top", false) ?? CssLength.Zero;
            style.PaddingRight = Length ("padding-right", false) ?? CssLength.Zero;
            style.PaddingBottom = Length ("padding-bottom", false) ?? CssLength.Zero;
            style.PaddingLeft = Length ("padding-left", false) ?? CssLength.Zero;
            style.Width = Length ("width") ?? CssLength.Auto;
            style.Height = Length ("height") ?? CssLength.Auto;
            style.MaxWidth = Length ("max-width") ?? CssLength.Auto;
            style.MaxHeight = Length ("max-height") ?? CssLength.Auto;

            style.BorderTopWidth = BorderWidth ("top");
            style.BorderBottomWidth = BorderWidth ("bottom");
            if ((Get ("border-top-color") ?? Get ("border-bottom-color")) is { } borderColor && CssValues.Color (borderColor) is { } bc)
                style.BorderColor = bc;

            style.VerticalAlign = Get ("vertical-align")?.ToLowerInvariant () switch {
                "super" or "text-top" or "top" => VerticalAlign.Super,
                "sub" or "text-bottom" or "bottom" => VerticalAlign.Sub,
                _ => VerticalAlign.Baseline,
            };

            var decoration = Get ("text-decoration-line") ?? Get ("text-decoration");
            if (decoration is not null) {
                style.TextDecoration = (decoration.Contains ("underline", StringComparison.OrdinalIgnoreCase) ? TextDecoration.Underline : 0)
                    | (decoration.Contains ("line-through", StringComparison.OrdinalIgnoreCase) ? TextDecoration.LineThrough : 0);
            } else {
                // Decorations propagate to inline descendants in CSS; inheriting is the same in practice.
                style.TextDecoration = parent.Display == Display.Inline ? parent.TextDecoration : TextDecoration.None;
            }

            style.BreakBefore = (Get ("break-before") ?? Get ("page-break-before")) is { } bb ? CssValues.Break (bb) ?? BreakRule.Auto : BreakRule.Auto;
            style.BreakAfter = (Get ("break-after") ?? Get ("page-break-after")) is { } ba ? CssValues.Break (ba) ?? BreakRule.Auto : BreakRule.Auto;
            style.BreakInside = (Get ("break-inside") ?? Get ("page-break-inside")) is { } bi && CssValues.Break (bi) == BreakRule.Avoid ? BreakRule.Avoid : BreakRule.Auto;

            ApplyReaderOverrides (style, element);
            return style;

            float BorderWidth (string side)
            {
                var styleValue = Get ($"border-{side}-style")?.ToLowerInvariant ();
                if (styleValue is null or "none" or "hidden")
                    return 0;

                var width = Get ($"border-{side}-width")?.ToLowerInvariant () ?? "medium";
                return width switch {
                    "thin" => 1,
                    "medium" => 3,
                    "thick" => 5,
                    _ => CssValues.Length (width, em, rootFontSize, scale, false)?.Resolve (0) ?? 0,
                };
            }
        }

        private void ApplyReaderOverrides (ComputedStyle style, IElement element)
        {
            if (settings.LineHeight is { } lineHeight) {
                style.LineHeightFactor = lineHeight;
                style.LineHeightPx = null;
            }

            var monospace = style.FontFamilies.Count > 0 && style.FontFamilies[0].Equals ("monospace", StringComparison.OrdinalIgnoreCase);
            if (settings.FontFamily is { } family && !monospace)
                style.FontFamilies = [family];

            if (settings.Justify is { } justify && style.TextAlign is TextAlign.Left or TextAlign.Justify && element.LocalName is not ("h1" or "h2" or "h3" or "h4" or "h5" or "h6"))
                style.TextAlign = justify ? TextAlign.Justify : TextAlign.Left;
        }

        private static string EpubPathsDirectory (string path)
        {
            var slash = path.LastIndexOf ('/');
            return slash < 0 ? string.Empty : path[..slash];
        }

        private static string? ResolvePath (string directory, string href)
        {
            href = href.Trim ();
            var hash = href.IndexOf ('#');
            if (hash >= 0)
                href = href[..hash];
            if (href.Length == 0 || href.Contains (':', StringComparison.Ordinal))
                return null;

            var combined = href.StartsWith ('/') ? href.TrimStart ('/') : directory.Length == 0 ? href : directory + "/" + href;
            var parts = new List<string> ();
            foreach (var segment in Uri.UnescapeDataString (combined).Split ('/')) {
                if (segment is "" or ".")
                    continue;
                if (segment == "..") {
                    if (parts.Count > 0)
                        parts.RemoveAt (parts.Count - 1);
                    continue;
                }
                parts.Add (segment);
            }
            return string.Join ('/', parts);
        }

        private sealed record Rule (ISelector Selector, List<(string Name, string Value, bool Important)> Declarations, int Order, bool UserAgent);
    }
}
