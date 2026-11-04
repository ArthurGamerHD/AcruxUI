using System;
using System.Collections.Generic;
using System.Globalization;
using VRageMath;

namespace Acrux.Media
{
    /// <summary>A small geometry subset for open M/L paths.</summary>
    public abstract class Geometry
    {
        internal abstract Vector2[] Points { get; }

        public static Geometry Parse(string data)
        {
            return StreamGeometry.Parse(data);
        }
    }

    public sealed class StreamGeometry : Geometry
    {
        readonly Vector2[] _points;

        StreamGeometry(Vector2[] points)
        {
            _points = points;
        }

        internal override Vector2[] Points => _points;

        public static new StreamGeometry Parse(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                throw new FormatException("A path requires geometry data.");
            var points = new List<Vector2>();
            int index = 0;
            char command = '\0';
            while (index < data.Length)
            {
                SkipSeparators(data, ref index);
                if (index == data.Length) break;
                if (char.IsLetter(data[index]))
                {
                    command = data[index++];
                    if (command != 'M' && command != 'm' && command != 'L' && command != 'l')
                        throw new FormatException("Only open M/L path commands are supported.");
                    SkipSeparators(data, ref index);
                }
                if (command == '\0' || index == data.Length)
                    throw new FormatException("A path command requires a point.");
                float x = ReadNumber(data, ref index);
                SkipSeparators(data, ref index);
                float y = ReadNumber(data, ref index);
                Vector2 point = new Vector2(x, y);
                if ((command == 'm' || command == 'l') && points.Count > 0)
                    point += points[points.Count - 1];
                points.Add(point);
                if (command == 'M') command = 'L';
                if (command == 'm') command = 'l';
            }
            if (points.Count < 2)
                throw new FormatException("A stroked path requires at least two points.");
            return new StreamGeometry(points.ToArray());
        }

        static void SkipSeparators(string data, ref int index)
        {
            while (index < data.Length && (char.IsWhiteSpace(data[index]) || data[index] == ',')) index++;
        }

        static float ReadNumber(string data, ref int index)
        {
            int start = index;
            if (index < data.Length && (data[index] == '+' || data[index] == '-')) index++;
            while (index < data.Length && (char.IsDigit(data[index]) || data[index] == '.')) index++;
            float result;
            if (start == index || !float.TryParse(data.Substring(start, index - start),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out result) ||
                float.IsNaN(result) || float.IsInfinity(result))
                throw new FormatException("Invalid path coordinate.");
            return result;
        }
    }
}
