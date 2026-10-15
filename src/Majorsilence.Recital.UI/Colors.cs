using System.Drawing;

namespace Majorsilence.Recital.UI
{
    internal static class Colors
    {
        public static Color FromArgb (uint argb) => Color.FromArgb (unchecked((int)argb));

        /// <summary><paramref name="a"/> moved <paramref name="amount"/> (0 to 1) of the way to <paramref name="b"/>.</summary>
        public static Color Mix (Color a, Color b, float amount)
            => Color.FromArgb (
                (int)(a.A + (b.A - a.A) * amount),
                (int)(a.R + (b.R - a.R) * amount),
                (int)(a.G + (b.G - a.G) * amount),
                (int)(a.B + (b.B - a.B) * amount));
    }
}
