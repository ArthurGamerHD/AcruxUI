using System;

namespace Acrux.Render3D
{
    /// <summary>
    /// Scratch owned by paint bands, without locks: slot b belongs to the band starting at row
    /// b * <see cref="SceneTriangleList.BAND_ROWS"/>, and only the worker painting that band touches it.
    /// <see cref="Prepare"/> runs on the paint coordinator before the bands start (a layer's Build), and the
    /// parallel join between frames orders one frame's writes before the next frame's reads.
    /// </summary>
    internal sealed class BandSlots<T> where T : class
    {
        T[] _slots = new T[0];

        // Paint coordinator only.
        public void Prepare(int height)
        {
            int bands = Math.Max(1, (height + SceneTriangleList.BAND_ROWS - 1) / SceneTriangleList.BAND_ROWS);
            if (_slots.Length < bands)
                Array.Resize(ref _slots, bands);
        }

        // -1 unless the rows are exactly one prepared band (the caller then uses scratch of its own).
        public int IndexOf(int firstRow, int endRow)
        {
            if (firstRow < 0 || firstRow % SceneTriangleList.BAND_ROWS != 0 ||
                endRow - firstRow > SceneTriangleList.BAND_ROWS)
                return -1;
            int band = firstRow / SceneTriangleList.BAND_ROWS;
            return band < _slots.Length ? band : -1;
        }

        public T this[int index]
        {
            get { return _slots[index]; }
            set { _slots[index] = value; }
        }
    }
}
