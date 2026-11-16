using System;
using VRageMath;

namespace Acrux.Rendering
{
    /// <summary>
    /// Picks the size a generated texture is actually allocated at.
    /// </summary>
    internal static class DynamicTextureSize
    {
        /// <summary>
        /// Scales <paramref name="requested"/> uniformly so its longer side is a power of two no larger than
        /// <paramref name="maxSide"/>; zero when either is not positive.
        /// </summary>
        /// <remarks>
        /// The game always gives a generated texture a mip chain, and counts one level too many whenever the longer
        /// side is not a power of two. Direct3D 11 on Windows rejects that description (E_INVALIDARG on the first
        /// upload, which takes the render thread down); DXVK clamps it, so the fault never shows under Proton.
        /// </remarks>
        public static Vector2I Fit(Vector2I requested, int maxSide)
        {
            if (maxSide <= 0 || requested.X <= 0 || requested.Y <= 0)
                return Vector2I.Zero;

            int longest = Math.Max(requested.X, requested.Y);
            int side = 1;
            while (side < longest && side <= maxSide / 2)
                side *= 2;
            if (side == longest)
                return requested;

            double scale = side / (double)longest;
            return new Vector2I(
                requested.X == longest ? side : Math.Max(1, (int)Math.Round(requested.X * scale)),
                requested.Y == longest ? side : Math.Max(1, (int)Math.Round(requested.Y * scale)));
        }
    }
}
