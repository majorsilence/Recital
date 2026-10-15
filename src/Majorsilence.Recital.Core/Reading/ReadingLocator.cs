namespace Majorsilence.Recital.Core.Reading
{
    /// <summary>
    /// A portable reading position (plan 1.4): the EPUB CFI is canonical; the spine index and character
    /// offset are the fallback when the CFI no longer resolves (a re-downloaded edition, say), and the
    /// percentage is for display.
    /// </summary>
    /// <param name="Cfi">An <c>epubcfi(...)</c> string, or null when none could be made.</param>
    /// <param name="SpineIndex">The spine item the position is in.</param>
    /// <param name="CharOffset">Text characters in that spine item's body before the position.</param>
    /// <param name="Percent">How far through the book, 0 to 1, weighted by the size of each spine item.</param>
    public sealed record ReadingLocator (string? Cfi, int SpineIndex, int CharOffset, double Percent);
}
