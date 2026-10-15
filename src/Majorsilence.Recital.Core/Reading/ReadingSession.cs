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

            var length = Math.Max (1, document.TextLength);
            var spine = chapter.SpineIndex;
            var percent = (spine_offsets[spine] + (spine_offsets[spine + 1] - spine_offsets[spine]) * Math.Min (1.0, charOffset / (double)length)) / total_size;
            locator = new ReadingLocator (cfi, spine, charOffset, Math.Clamp (percent, 0, 1));
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
}
