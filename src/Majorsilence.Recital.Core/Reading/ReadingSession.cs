using Majorsilence.Recital.Core.Annotations;
using Majorsilence.Recital.Epub;
using Majorsilence.Recital.Layout;

namespace Majorsilence.Recital.Core.Reading
{
    /// <summary>
    /// An open book being read: the current chapter's pages and the page showing. Page turns cross chapter
    /// boundaries, and the position survives any change of viewport or typography because it is held as a
    /// DOM position (the <em>anchor</em>) rather than a page number.
    /// </summary>
    /// <remarks>
    /// The anchor moves only when the reader navigates (a page turn, the contents, a locator). Re-paginating
    /// lands on the page that contains it and leaves it alone, so resizing back and forth never drifts
    /// forward a page at a time. Navigation calls are serialised; each returns once the new page is ready.
    /// </remarks>
    public sealed class ReadingSession : IAsyncDisposable
    {
        private readonly SemaphoreSlim gate = new (1, 1);
        private readonly Dictionary<int, Task<ChapterLayout>> layouts = [];
        private readonly long[] spine_offsets;
        private readonly long total_size;
        private ChapterLayout chapter = null!;
        private DomPosition anchor;
        private ReadingLocator locator = null!;

        private ReadingSession (EpubBook book, LayoutSettings settings)
        {
            Book = book;
            Engine = new LayoutEngine (book);
            Settings = settings;

            // Percentages weight each linear spine item by its size, the usual proxy for its share of the reading.
            spine_offsets = new long[book.Spine.Count + 1];
            for (var i = 0; i < book.Spine.Count; i++)
                spine_offsets[i + 1] = spine_offsets[i] + SpineSize (i);
            total_size = Math.Max (1, spine_offsets[^1]);
        }

        public EpubBook Book { get; }
        public LayoutEngine Engine { get; }
        public LayoutSettings Settings { get; private set; }
        public ChapterLayout Chapter => chapter;
        public int PageIndex { get; private set; }
        public LayoutPage Page => chapter.Pages[PageIndex];
        public int SpineIndex => chapter.SpineIndex;

        /// <summary>The position being read: where the reader last navigated to.</summary>
        public ReadingLocator Locator => locator;

        public bool CanGoForward => PageIndex < chapter.Pages.Count - 1 || NextReadable (SpineIndex, +1) is not null;
        public bool CanGoBack => PageIndex > 0 || NextReadable (SpineIndex, -1) is not null;

        /// <summary>Opens a session at <paramref name="start"/>, or at the start of the book.</summary>
        public static async Task<ReadingSession> OpenAsync (EpubBook book, LayoutSettings settings, ReadingLocator? start = null, CancellationToken ct = default)
        {
            if (book.Spine.Count == 0)
                throw new EpubException ("The book has nothing to read: its spine is empty.");

            var session = new ReadingSession (book, settings);
            try {
                if ((start is null || !await session.TryGoToCoreAsync (start, ct).ConfigureAwait (false))
                    && !(session.NextReadable (-1, +1) is { } first && await session.GoToChapterCoreAsync (first, atEnd: false, ct).ConfigureAwait (false)))
                    throw new EpubException ("The book has nothing to read: no chapter has any content.");
            } catch {
                session.Engine.Dispose ();
                throw;
            }
            return session;
        }

        public Task<bool> NextPageAsync (CancellationToken ct = default) => Serialised (async () => {
            if (PageIndex < chapter.Pages.Count - 1) {
                SetPage (PageIndex + 1);
                return true;
            }
            return NextReadable (SpineIndex, +1) is { } next && await GoToChapterCoreAsync (next, atEnd: false, ct).ConfigureAwait (false);
        }, ct);

        public Task<bool> PreviousPageAsync (CancellationToken ct = default) => Serialised (async () => {
            if (PageIndex > 0) {
                SetPage (PageIndex - 1);
                return true;
            }
            return NextReadable (SpineIndex, -1) is { } previous && await GoToChapterCoreAsync (previous, atEnd: true, ct).ConfigureAwait (false);
        }, ct);

        /// <summary>Goes to the first (or last) page of the current chapter.</summary>
        public Task ChapterEdgeAsync (bool end, CancellationToken ct = default) => Serialised (() => {
            SetPage (end ? chapter.Pages.Count - 1 : 0);
            return Task.FromResult (true);
        }, ct);

        /// <summary>Goes to a saved position. Returns false (and stays put) when it cannot be found in this book.</summary>
        public Task<bool> GoToAsync (ReadingLocator target, CancellationToken ct = default)
            => Serialised (() => TryGoToCoreAsync (target, ct), ct);

        /// <summary>
        /// Shows the page holding <paramref name="position"/> in chapter <paramref name="spine"/> (following speech
        /// as it is read aloud) and makes that exact position the one saved. Returns whether the page changed.
        /// </summary>
        public Task<bool> ShowPositionAsync (int spine, DomPosition position, CancellationToken ct = default) => Serialised (async () => {
            var layout = spine == chapter.SpineIndex ? chapter : await LayoutAsync (spine, ct).ConfigureAwait (false);
            if (layout.Pages.Count == 0)
                return false;
            var page = layout.PageOf (position).Index;
            var changed = layout != chapter || page != PageIndex;
            if (changed)
                Show (layout, page);
            SetAnchor (position);
            return changed;
        }, ct);

        /// <summary>The next chapter after <paramref name="spine"/> with something to read, or null at the end of the book.</summary>
        public int? NextReadableChapter (int spine) => NextReadable (spine, +1);

        /// <summary>Goes to a table-of-contents entry. Returns false when it points outside the spine.</summary>
        public Task<bool> GoToAsync (TocEntry entry, CancellationToken ct = default) => Serialised (async () => {
            if (entry.SpineIndex is not { } spine)
                return false;

            var target = await LayoutAsync (spine, ct).ConfigureAwait (false);
            if (target.Pages.Count == 0)
                return false;
            var element = entry.Fragment is { Length: > 0 } fragment ? target.Document.FindFragment (fragment) : null;
            Show (target, element is null ? 0 : target.PageOf (new DomPosition (element, 0)).Index);
            if (element is not null)
                SetAnchor (new DomPosition (element, 0));
            return true;
        }, ct);

        /// <summary>
        /// Lays the book out again for a new viewport or typography and shows the page holding the anchor,
        /// without moving the anchor.
        /// </summary>
        public Task ChangeSettingsAsync (LayoutSettings settings, CancellationToken ct = default) => Serialised (async () => {
            if (settings == Settings)
                return true;

            Settings = settings;
            layouts.Clear ();
            var relaid = await LayoutAsync (SpineIndex, ct).ConfigureAwait (false);
            chapter = relaid;
            PageIndex = relaid.PageOf (anchor).Index;
            PrefetchNeighbours ();
            return true;
        }, ct);

        /// <summary>
        /// The deepest table-of-contents entry at or before the current page, for "where am I" displays.
        /// </summary>
        public TocEntry? CurrentTocEntry ()
        {
            TocEntry? found = null;
            foreach (var entry in Flatten (Book.TableOfContents)) {
                if (entry.SpineIndex is not { } spine || spine > SpineIndex)
                    continue;
                if (spine == SpineIndex && entry.Fragment is { Length: > 0 } fragment
                    && chapter.Document.FindFragment (fragment) is { } element
                    && chapter.PageOf (new DomPosition (element, 0)).Index > PageIndex)
                    continue;
                if (found?.SpineIndex is not { } best || spine >= best)
                    found = entry;
            }
            return found;
        }

        /// <summary>
        /// The passage between two positions on the current chapter (in either order), anchored for saving
        /// as a highlight; null when it holds no text.
        /// </summary>
        public SelectedPassage? Select (DomPosition a, DomPosition b)
        {
            var (start, end) = DomOrder.Compare (a, b) <= 0 ? (a, b) : (b, a);
            if (DomOrder.Compare (start, end) == 0)
                return null;
            var document = chapter.Document;
            var text = document.GetText (start, end);
            if (text.Length == 0)
                return null;
            string? cfi;
            try {
                cfi = document.GetRangeCfi (start, end).ToString ();
            } catch (ArgumentException) {
                cfi = null;
            }
            var offset = document.GetCharOffset (start);
            return new SelectedPassage (start, end, chapter.SpineIndex, cfi, offset, PercentOf (chapter.SpineIndex, offset), text);
        }

        /// <summary>A new highlight for <paramref name="selection"/>; save it with the library.</summary>
        public static Annotation NewHighlight (SelectedPassage selection, string color = HighlightColors.Default, string? note = null) => new () {
            Id = Guid.NewGuid (),
            Kind = AnnotationKind.Highlight,
            Cfi = selection.Cfi,
            SpineIndex = selection.SpineIndex,
            CharOffset = selection.CharOffset,
            Percent = selection.Percent,
            Text = selection.Text,
            Color = color,
            Note = note,
        };

        /// <summary>A new bookmark for the current page; save it with the library.</summary>
        public Annotation NewBookmark ()
        {
            var excerpt = string.Join (" ", Page.Lines.Select (l => l.Text.Trim ())).Trim ();
            if (excerpt.Length > 160)
                excerpt = excerpt[..160].TrimEnd () + "…";
            return new Annotation {
                Id = Guid.NewGuid (),
                Kind = AnnotationKind.Bookmark,
                Cfi = locator.Cfi,
                SpineIndex = locator.SpineIndex,
                CharOffset = locator.CharOffset,
                Percent = locator.Percent,
                Text = excerpt,
            };
        }

        /// <summary>
        /// Where an annotation is in the current chapter: its start and end (equal for a bookmark), or null
        /// when it is in another chapter or cannot be found. A CFI that no longer resolves falls back to the
        /// saved character offset.
        /// </summary>
        public (DomPosition Start, DomPosition End)? Locate (Annotation annotation)
        {
            var document = chapter.Document;
            EpubCfi? cfi = annotation.Cfi is { } text && EpubCfi.TryParse (text, out var parsed) ? parsed : null;
            var spine = cfi is null ? annotation.SpineIndex : Book.ResolveSpineIndex (cfi) ?? annotation.SpineIndex;
            if (spine != chapter.SpineIndex)
                return null;

            if (cfi is not null && document.Resolve (cfi) is { } start) {
                var end = annotation.Kind == AnnotationKind.Bookmark ? start : document.Resolve (cfi, end: true) ?? start;
                return (start, end);
            }

            if (annotation.SpineIndex != chapter.SpineIndex || document.PositionAtCharOffset (annotation.CharOffset) is not { } fallback)
                return null;
            var length = annotation.Kind == AnnotationKind.Bookmark ? 0 : annotation.Text?.Length ?? 0;
            return (fallback, document.PositionAtCharOffset (annotation.CharOffset + length) ?? fallback);
        }

        /// <summary>Highlights on the current page with the rectangles (content-box coordinates) they cover.</summary>
        public IReadOnlyList<(Annotation Annotation, IReadOnlyList<PageRect> Rects)> HighlightsOnPage (IEnumerable<Annotation> annotations)
        {
            var found = new List<(Annotation, IReadOnlyList<PageRect>)> ();
            if (Page.StartPosition is not { } pageStart || Page.EndPosition is not { } pageEnd)
                return found;
            foreach (var annotation in annotations) {
                if (annotation.Deleted || annotation.Kind != AnnotationKind.Highlight || Locate (annotation) is not var (start, end))
                    continue;
                if (DomOrder.Compare (end, pageStart) <= 0 || DomOrder.Compare (start, pageEnd) >= 0)
                    continue;
                var rects = Page.TextRects (start, end);
                if (rects.Count > 0)
                    found.Add ((annotation, rects));
            }
            return found;
        }

        /// <summary>The bookmark on the current page, if there is one.</summary>
        public Annotation? BookmarkOnPage (IEnumerable<Annotation> annotations)
        {
            foreach (var annotation in annotations)
                if (!annotation.Deleted && annotation.Kind == AnnotationKind.Bookmark && Locate (annotation) is var (start, _) && chapter.PageOf (start).Index == PageIndex)
                    return annotation;
            return null;
        }

        /// <summary>Goes to an annotation. Returns false when it cannot be found in this book.</summary>
        public Task<bool> GoToAsync (Annotation annotation, CancellationToken ct = default)
        {
            string? start = annotation.Cfi;
            // A range CFI: go to its start.
            if (start is not null && EpubCfi.TryParse (start, out var cfi) && cfi.End is not null)
                start = EpubCfi.Point (cfi.Start).ToString ();
            return GoToAsync (new ReadingLocator (start, annotation.SpineIndex, annotation.CharOffset, annotation.Percent), ct);
        }

        /// <summary>
        /// The chapter heading each annotation falls under: the deepest table-of-contents entry at or before
        /// it (Gutenberg books put many chapters in one file, so entries in the same file are placed by their
        /// fragment's position). Null where no entry comes before it.
        /// </summary>
        public async Task<IReadOnlyDictionary<Guid, string?>> ChapterTitlesAsync (IEnumerable<Annotation> annotations, CancellationToken ct = default)
        {
            var entries = Flatten (Book.TableOfContents).Where (e => e.SpineIndex is not null).ToList ();
            var offsets = new Dictionary<TocEntry, int> ();
            var result = new Dictionary<Guid, string?> ();

            foreach (var group in annotations.GroupBy (a => a.SpineIndex)) {
                var spine = group.Key;
                var inSpine = entries.Where (e => e.SpineIndex == spine).ToList ();
                if (inSpine.Any (e => e.Fragment is { Length: > 0 }) && spine >= 0 && spine < Book.Spine.Count) {
                    var document = await Engine.GetDocumentAsync (spine, ct).ConfigureAwait (false);
                    foreach (var entry in inSpine)
                        offsets[entry] = entry.Fragment is { Length: > 0 } fragment && document.FindFragment (fragment) is { } element
                            ? document.GetCharOffset (new DomPosition (element, 0))
                            : 0;
                }
                var before = entries.LastOrDefault (e => e.SpineIndex < spine);
                foreach (var annotation in group) {
                    var here = inSpine.Where (e => offsets.GetValueOrDefault (e) <= annotation.CharOffset)
                        .OrderBy (e => offsets.GetValueOrDefault (e)).LastOrDefault ();
                    result[annotation.Id] = (here ?? before)?.Title;
                }
            }
            return result;
        }

        public static IEnumerable<TocEntry> Flatten (IEnumerable<TocEntry> entries)
        {
            foreach (var entry in entries) {
                yield return entry;
                foreach (var child in Flatten (entry.Children))
                    yield return child;
            }
        }

        private async Task<bool> TryGoToCoreAsync (ReadingLocator target, CancellationToken ct)
        {
            int? spine = null;
            EpubCfi? cfi = null;
            if (target.Cfi is { } text && EpubCfi.TryParse (text, out var parsed)) {
                cfi = parsed;
                spine = Book.ResolveSpineIndex (parsed);
            }
            spine ??= target.SpineIndex;
            if (spine < 0 || spine >= Book.Spine.Count)
                return false;

            var layout = await LayoutAsync (spine.Value, ct).ConfigureAwait (false);
            var position = cfi is null ? null : layout.Document.Resolve (cfi);

            // The CFI did not resolve: fall back to the saved chapter and character offset.
            if (position is null && target.SpineIndex >= 0 && target.SpineIndex < Book.Spine.Count) {
                if (target.SpineIndex != layout.SpineIndex)
                    layout = await LayoutAsync (target.SpineIndex, ct).ConfigureAwait (false);
                position = layout.Document.PositionAtCharOffset (target.CharOffset);
            }

            if (layout.Pages.Count == 0)
                return false;

            if (position is { } found) {
                Show (layout, layout.PageOf (found).Index);
                // Keep the exact saved position (not the page start) so the next re-pagination finds it again.
                anchor = found;
                locator = target with { SpineIndex = layout.SpineIndex };
            } else {
                Show (layout, 0);
            }
            return true;
        }

        /// <summary>
        /// Shows the first (or, going backwards, last) page of a chapter, carrying on past chapters that turn
        /// out to have no pages at all. Returns false when there is none left in that direction.
        /// </summary>
        private async Task<bool> GoToChapterCoreAsync (int spineIndex, bool atEnd, CancellationToken ct)
        {
            for (int? index = spineIndex; index is { } i; index = NextReadable (i, atEnd ? -1 : +1)) {
                var layout = await LayoutAsync (i, ct).ConfigureAwait (false);
                if (layout.Pages.Count > 0) {
                    Show (layout, atEnd ? layout.Pages.Count - 1 : 0);
                    return true;
                }
            }
            return false;
        }

        private void Show (ChapterLayout layout, int pageIndex)
        {
            chapter = layout;
            SetPage (pageIndex);
            PrefetchNeighbours ();
        }

        private void SetPage (int index)
        {
            PageIndex = Math.Clamp (index, 0, chapter.Pages.Count - 1);
            SetAnchor (PageStart (Page));
        }

        private void SetAnchor (DomPosition position)
        {
            anchor = position;
            var document = chapter.Document;
            var charOffset = document.GetCharOffset (position);
            string? cfi;
            try {
                cfi = document.GetCfi (position).ToString ();
            } catch (ArgumentException) {
                cfi = null;
            }

            locator = new ReadingLocator (cfi, chapter.SpineIndex, charOffset, PercentOf (chapter.SpineIndex, charOffset));
        }

        /// <summary>How far through the book a character offset in the current chapter is, 0 to 1.</summary>
        private double PercentOf (int spine, int charOffset)
        {
            var length = Math.Max (1, chapter.Document.TextLength);
            var percent = (spine_offsets[spine] + (spine_offsets[spine + 1] - spine_offsets[spine]) * Math.Min (1.0, charOffset / (double)length)) / total_size;
            return Math.Clamp (percent, 0, 1);
        }

        private DomPosition PageStart (LayoutPage page)
            => page.StartPosition
            ?? page.Items.Select (i => i.StartPosition).FirstOrDefault (p => p is not null)
            ?? new DomPosition ((AngleSharp.Dom.INode?)chapter.Document.Document.Body ?? chapter.Document.Root, 0);

        /// <summary>The next spine item in <paramref name="direction"/> that is linear and has pages, or null.</summary>
        private int? NextReadable (int from, int direction)
        {
            for (var i = from + direction; i >= 0 && i < Book.Spine.Count; i += direction) {
                if (!Book.Spine[i].Linear || !Book.Spine[i].Content.IsXhtml)
                    continue;
                // A chapter already laid out with no pages is skipped; one not laid out yet is assumed readable.
                if (layouts.TryGetValue (i, out var task) && task.IsCompletedSuccessfully && task.Result.Pages.Count == 0)
                    continue;
                return i;
            }
            return null;
        }

        private Task<ChapterLayout> LayoutAsync (int spineIndex, CancellationToken ct)
        {
            if (!layouts.TryGetValue (spineIndex, out var task) || task.IsFaulted || task.IsCanceled) {
                task = Engine.LayoutChapterAsync (spineIndex, Settings, ct);
                layouts[spineIndex] = task;
            }
            return task;
        }

        /// <summary>Lays out the chapters either side in the background so crossing into them is instant.</summary>
        private void PrefetchNeighbours ()
        {
            // Only the current chapter and its neighbours stay cached.
            foreach (var key in layouts.Keys.Where (k => Math.Abs (k - SpineIndex) > 1).ToList ())
                layouts.Remove (key);

            foreach (var neighbour in new[] { NextReadable (SpineIndex, +1), NextReadable (SpineIndex, -1) })
                if (neighbour is { } index && !layouts.ContainsKey (index))
                    _ = LayoutAsync (index, CancellationToken.None).ContinueWith (t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        private long SpineSize (int index)
        {
            var item = Book.Spine[index];
            return item.Linear && item.Content.Path is { } path ? Book.GetResourceSize (path) ?? 0 : 0;
        }

        private async Task<T> Serialised<T> (Func<Task<T>> action, CancellationToken ct)
        {
            await gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                return await action ().ConfigureAwait (false);
            } finally {
                gate.Release ();
            }
        }

        public async ValueTask DisposeAsync ()
        {
            await gate.WaitAsync ().ConfigureAwait (false);
            Engine.Dispose ();
            await Book.DisposeAsync ().ConfigureAwait (false);
            gate.Dispose ();
        }
    }

    /// <summary>A passage the reader selected: its positions, and how to save it.</summary>
    /// <param name="Cfi">A range CFI, or null when none could be made.</param>
    /// <param name="CharOffset">Characters in the chapter before its start (the fallback anchor).</param>
    /// <param name="Text">The selected words, whitespace collapsed.</param>
    public sealed record SelectedPassage (DomPosition Start, DomPosition End, int SpineIndex, string? Cfi, int CharOffset, double Percent, string Text);
}
