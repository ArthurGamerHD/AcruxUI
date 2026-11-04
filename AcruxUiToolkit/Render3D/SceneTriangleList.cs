using System;
using VRageMath;

namespace Acrux.Render3D
{
    /// <summary>A projected triangle. Attributes are pre-divided by depth for perspective-correct interpolation.</summary>
    internal struct ScreenTriangle
    {
        public double X0, Y0, X1, Y1, X2, Y2;
        public float InverseZ0, InverseZ1, InverseZ2;
        public Vector3 A0, A1, A2;
        public float S0, S1, S2;
        public int MinX, MaxX, MinY, MaxY;
        public int Mip;
    }

    /// <summary>
    /// The projected triangles of one scene layer. Building (near-plane clipping, projection, screen culling)
    /// happens once on one thread; the row-band workers then only read it.
    /// </summary>
    internal sealed class SceneTriangleList
    {
        ScreenTriangle[] _items = new ScreenTriangle[4096];
        int _count;

        public int Count
        {
            get { return _count; }
        }

        public ScreenTriangle[] Items
        {
            get { return _items; }
        }

        // Hosts paint in bands of this height starting at row 0.
        public const int BAND_ROWS = 16;

        // Triangle indices grouped by the bands they touch: band b's are _bandItems[_bandStart[b].._bandStart[b+1]).
        int[] _bandStart = new int[0];
        int[] _bandCursor = new int[0];
        int[] _bandItems = new int[0];
        int _bands;
        bool _binned;

        public void Clear()
        {
            _binned = false;
            _count = 0;
        }

        // After the last Add: each band then visits only its own triangles.
        public void Bin(int height)
        {
            _bands = Math.Max(1, (height + BAND_ROWS - 1) / BAND_ROWS);
            if (_bandStart.Length < _bands + 1)
            {
                _bandStart = new int[_bands + 1];
                _bandCursor = new int[_bands + 1];
            }
            Array.Clear(_bandStart, 0, _bands + 1);
            int total = 0;
            for (int i = 0; i < _count; i++)
            {
                int first = Math.Min(_bands - 1, _items[i].MinY / BAND_ROWS);
                int last = Math.Min(_bands - 1, _items[i].MaxY / BAND_ROWS);
                for (int b = first; b <= last; b++)
                    _bandStart[b + 1]++;
                total += last - first + 1;
            }
            for (int b = 0; b < _bands; b++)
                _bandStart[b + 1] += _bandStart[b];
            if (_bandItems.Length < total)
                _bandItems = new int[Math.Max(total, _bandItems.Length * 2)];
            Array.Copy(_bandStart, _bandCursor, _bands + 1);
            for (int i = 0; i < _count; i++)
            {
                int first = Math.Min(_bands - 1, _items[i].MinY / BAND_ROWS);
                int last = Math.Min(_bands - 1, _items[i].MaxY / BAND_ROWS);
                for (int b = first; b <= last; b++)
                    _bandItems[_bandCursor[b]++] = i;
            }
            _binned = true;
        }

        public int[] BandItems
        {
            get { return _bandItems; }
        }

        public bool TryGetBand(int firstRow, int endRow, out int start, out int end)
        {
            start = end = 0;
            if (!_binned || firstRow < 0 || firstRow % BAND_ROWS != 0 || endRow - firstRow > BAND_ROWS)
                return false;
            int band = firstRow / BAND_ROWS;
            if (band >= _bands)
                return true;
            start = _bandStart[band];
            end = _bandStart[band + 1];
            return true;
        }

        public void Add(ref SceneCamera camera, ref SceneVertex a, ref SceneVertex b, ref SceneVertex c)
        {
            double near = camera.NearDepth;
            bool insideA = a.Z > near;
            bool insideB = b.Z > near;
            bool insideC = c.Z > near;
            int inside = (insideA ? 1 : 0) + (insideB ? 1 : 0) + (insideC ? 1 : 0);
            if (inside == 0)
                return;
            if (inside == 3)
            {
                Emit(ref camera, ref a, ref b, ref c);
                return;
            }

            SceneVertex ab;
            SceneVertex bc;
            SceneVertex ca;
            if (inside == 1)
            {
                if (insideA)
                {
                    Intersect(ref c, ref a, near, out ca);
                    Intersect(ref a, ref b, near, out ab);
                    Emit(ref camera, ref ca, ref a, ref ab);
                }
                else if (insideB)
                {
                    Intersect(ref a, ref b, near, out ab);
                    Intersect(ref b, ref c, near, out bc);
                    Emit(ref camera, ref ab, ref b, ref bc);
                }
                else
                {
                    Intersect(ref c, ref a, near, out ca);
                    Intersect(ref b, ref c, near, out bc);
                    Emit(ref camera, ref ca, ref bc, ref c);
                }
                return;
            }

            if (!insideA)
            {
                Intersect(ref c, ref a, near, out ca);
                Intersect(ref a, ref b, near, out ab);
                Emit(ref camera, ref ca, ref ab, ref b);
                Emit(ref camera, ref ca, ref b, ref c);
            }
            else if (!insideB)
            {
                Intersect(ref a, ref b, near, out ab);
                Intersect(ref b, ref c, near, out bc);
                Emit(ref camera, ref a, ref ab, ref bc);
                Emit(ref camera, ref a, ref bc, ref c);
            }
            else
            {
                Intersect(ref c, ref a, near, out ca);
                Intersect(ref b, ref c, near, out bc);
                Emit(ref camera, ref ca, ref a, ref b);
                Emit(ref camera, ref ca, ref b, ref bc);
            }
        }

        static void Intersect(ref SceneVertex from, ref SceneVertex to, double near, out SceneVertex result)
        {
            double span = to.Z - from.Z;
            double t = Math.Abs(span) < 1e-12 ? 0d : (near - from.Z) / span;
            if (t < 0d) t = 0d; else if (t > 1d) t = 1d;
            float tf = (float)t;
            result = default(SceneVertex);
            result.X = from.X + (to.X - from.X) * t;
            result.Y = from.Y + (to.Y - from.Y) * t;
            // Nudged inside so the projection below never divides by the plane itself.
            result.Z = near * 1.000001d;
            result.Attribute = from.Attribute + (to.Attribute - from.Attribute) * tf;
            result.Shade = from.Shade + (to.Shade - from.Shade) * tf;
        }

        void Emit(ref SceneCamera camera, ref SceneVertex a, ref SceneVertex b, ref SceneVertex c)
        {
            double focal = camera.FocalLengthPixels;
            double x0 = camera.CenterX + focal * a.X / a.Z;
            double y0 = camera.CenterY - focal * a.Y / a.Z;
            double x1 = camera.CenterX + focal * b.X / b.Z;
            double y1 = camera.CenterY - focal * b.Y / b.Z;
            double x2 = camera.CenterX + focal * c.X / c.Z;
            double y2 = camera.CenterY - focal * c.Y / c.Z;

            double minX = Math.Min(x0, Math.Min(x1, x2));
            double maxX = Math.Max(x0, Math.Max(x1, x2));
            double minY = Math.Min(y0, Math.Min(y1, y2));
            double maxY = Math.Max(y0, Math.Max(y1, y2));
            if (maxX < 0d || maxY < 0d || minX > camera.Width || minY > camera.Height)
                return;
            // A vertex just past the near plane can project absurdly far; the float edge
            // setup below would lose the on-screen part, so such slivers are dropped.
            const double limit = 1e7;
            if (minX < -limit || minY < -limit || maxX > limit || maxY > limit)
                return;
            if (Math.Abs((x1 - x0) * (y2 - y0) - (y1 - y0) * (x2 - x0)) < 1e-5)
                return;

            if (_count == _items.Length)
                Array.Resize(ref _items, _items.Length * 2);

            float iz0 = (float)(1d / a.Z);
            float iz1 = (float)(1d / b.Z);
            float iz2 = (float)(1d / c.Z);
            ScreenTriangle triangle;
            triangle.X0 = x0;
            triangle.Y0 = y0;
            triangle.X1 = x1;
            triangle.Y1 = y1;
            triangle.X2 = x2;
            triangle.Y2 = y2;
            triangle.InverseZ0 = iz0;
            triangle.InverseZ1 = iz1;
            triangle.InverseZ2 = iz2;
            triangle.A0 = a.Attribute * iz0;
            triangle.A1 = b.Attribute * iz1;
            triangle.A2 = c.Attribute * iz2;
            triangle.S0 = a.Shade * iz0;
            triangle.S1 = b.Shade * iz1;
            triangle.S2 = c.Shade * iz2;
            triangle.MinX = Math.Max(0, (int)Math.Floor(minX));
            triangle.MaxX = Math.Min(camera.Width - 1, (int)Math.Ceiling(maxX));
            triangle.MinY = Math.Max(0, (int)Math.Floor(minY));
            triangle.MaxY = Math.Min(camera.Height - 1, (int)Math.Ceiling(maxY));
            triangle.Mip = 0;
            if (triangle.MinX > triangle.MaxX || triangle.MinY > triangle.MaxY)
                return;
            _items[_count++] = triangle;
        }
    }
}
