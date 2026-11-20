using System;
using System.Collections;
using System.Collections.Generic;
using VRageMath;

namespace Acrux.Styling
{
    /// <summary>Media.GradientSpreadMethod: how a gradient fills the area outside its 0-1 offsets.</summary>
    public enum GradientSpreadMethod
    {
        Pad,
        Reflect,
        Repeat
    }

    /// <summary>Media.GradientStop: a color at an offset (0-1) along a gradient.</summary>
    public sealed class GradientStop : IEquatable<GradientStop>
    {
        public GradientStop()
        {
        }

        public GradientStop(Color color, float offset)
        {
            Color = color;
            Offset = offset;
        }

        public Color Color { get; set; }
        public float Offset { get; set; }

        public bool Equals(GradientStop other)
        {
            return !ReferenceEquals(other, null) && Color == other.Color && Offset.Equals(other.Offset);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as GradientStop);
        }

        public override int GetHashCode()
        {
            return (Color.GetHashCode() * 397) ^ Offset.GetHashCode();
        }
    }

    /// <summary>Media.GradientStops: the ordered stops of a <see cref="GradientBrush"/>.</summary>
    public sealed class GradientStops : IEnumerable<GradientStop>
    {
        readonly List<GradientStop> _stops = new List<GradientStop>();

        public int Count => _stops.Count;

        public GradientStop this[int index]
        {
            get { return _stops[index]; }
            set { _stops[index] = Require(value); }
        }

        public void Add(GradientStop stop)
        {
            _stops.Add(Require(stop));
        }

        public void Add(Color color, float offset)
        {
            _stops.Add(new GradientStop(color, offset));
        }

        public void Insert(int index, GradientStop stop)
        {
            _stops.Insert(index, Require(stop));
        }

        public bool Remove(GradientStop stop)
        {
            return _stops.Remove(stop);
        }

        public void RemoveAt(int index)
        {
            _stops.RemoveAt(index);
        }

        public void Clear()
        {
            _stops.Clear();
        }

        public IEnumerator<GradientStop> GetEnumerator()
        {
            return _stops.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        static GradientStop Require(GradientStop stop)
        {
            if (stop == null)
                throw new ArgumentNullException(nameof(stop));
            return stop;
        }
    }

    /// <summary>
    /// Media.GradientBrush. Gradients are baked into a generated texture on the CPU, so only surfaces whose texture
    /// Acrux generates draw them: a Border's Background (square or rounded, with its stroke on top), and a Border with
    /// an ImageBrush OpacityMask (the mask texture is decoded once, cached, and re-encoded with the gradient). Every
    /// other surface fills with <see cref="FallbackColor"/>, the average of the stops.
    /// </summary>
    public abstract class GradientBrush : BackgroundBrush
    {
        GradientStops _gradientStops = new GradientStops();
        float _opacity = 1f;

        public GradientStops GradientStops
        {
            get { return _gradientStops; }
            set { _gradientStops = value ?? new GradientStops(); }
        }

        public GradientSpreadMethod SpreadMethod { get; set; }

        /// <summary>Brush.Opacity: the gradient's alpha (0-1), on top of the element's own Opacity.</summary>
        public float Opacity
        {
            get { return _opacity; }
            set { _opacity = MathHelper.Clamp(value, 0f, 1f); }
        }

        /// <summary>Applied around <see cref="TransformOrigin"/> before sampling: rotation, scale and translation.</summary>
        public Transform Transform { get; set; }
        public RelativePoint TransformOrigin { get; set; } = RelativePoint.TopLeft;

        public override Color FallbackColor
        {
            get
            {
                int count = _gradientStops.Count;
                if (count == 0)
                    return Color.Transparent;
                Vector4 sum = Vector4.Zero;
                foreach (GradientStop stop in _gradientStops)
                    sum += stop.Color.ToVector4();
                Vector4 average = sum / count;
                average.W *= _opacity;
                return new Color(average);
            }
        }
    }

    /// <summary>Media.LinearGradientBrush: colors along the line from <see cref="StartPoint"/> to <see cref="EndPoint"/>.</summary>
    public sealed class LinearGradientBrush : GradientBrush
    {
        public RelativePoint StartPoint { get; set; } = RelativePoint.TopLeft;
        public RelativePoint EndPoint { get; set; } = RelativePoint.BottomRight;

        protected override bool EqualsCore(BackgroundBrush other)
        {
            return GradientBrushes.AreEqual(this, (GradientBrush)other);
        }

        public override int GetHashCode()
        {
            return GradientBrushes.Hash(this);
        }
    }

    /// <summary>
    /// Media.RadialGradientBrush: offset 0 at <see cref="GradientOrigin"/> to offset 1 on the ellipse around
    /// <see cref="Center"/> whose radii are <see cref="RadiusX"/> and <see cref="RadiusY"/>.
    /// </summary>
    public sealed class RadialGradientBrush : GradientBrush
    {
        public RelativePoint Center { get; set; } = RelativePoint.Center;
        public RelativePoint GradientOrigin { get; set; } = RelativePoint.Center;

        /// <summary>Horizontal radius, as a fraction of the width (Relative) or in layout units (Absolute).</summary>
        public RelativeScalar RadiusX { get; set; } = RelativeScalar.Middle;

        /// <summary>Vertical radius, as a fraction of the height (Relative) or in layout units (Absolute).</summary>
        public RelativeScalar RadiusY { get; set; } = RelativeScalar.Middle;

        protected override bool EqualsCore(BackgroundBrush other)
        {
            return GradientBrushes.AreEqual(this, (GradientBrush)other);
        }

        public override int GetHashCode()
        {
            return GradientBrushes.Hash(this);
        }
    }

    /// <summary>
    /// Media.ConicGradientBrush: colors swept clockwise around <see cref="Center"/>, offset 0 at <see cref="Angle"/>
    /// degrees from the top.
    /// </summary>
    public sealed class ConicGradientBrush : GradientBrush
    {
        public RelativePoint Center { get; set; } = RelativePoint.Center;
        public float Angle { get; set; }

        protected override bool EqualsCore(BackgroundBrush other)
        {
            return GradientBrushes.AreEqual(this, (GradientBrush)other);
        }

        public override int GetHashCode()
        {
            return GradientBrushes.Hash(this);
        }
    }

    // Kept off the public types, which the client API mirrors member by member.
    internal static class GradientBrushes
    {
        internal static bool AreEqual(GradientBrush a, GradientBrush b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null || a.GetType() != b.GetType() ||
                a.SpreadMethod != b.SpreadMethod || !a.Opacity.Equals(b.Opacity) ||
                !Equals(a.Transform, b.Transform) || !a.TransformOrigin.Equals(b.TransformOrigin) ||
                a.GradientStops.Count != b.GradientStops.Count)
                return false;
            for (int i = 0; i < a.GradientStops.Count; i++)
                if (!a.GradientStops[i].Equals(b.GradientStops[i]))
                    return false;

            var linear = a as LinearGradientBrush;
            if (linear != null)
            {
                var other = (LinearGradientBrush)b;
                return linear.StartPoint.Equals(other.StartPoint) && linear.EndPoint.Equals(other.EndPoint);
            }
            var radial = a as RadialGradientBrush;
            if (radial != null)
            {
                var other = (RadialGradientBrush)b;
                return radial.Center.Equals(other.Center) && radial.GradientOrigin.Equals(other.GradientOrigin) &&
                       radial.RadiusX.Equals(other.RadiusX) && radial.RadiusY.Equals(other.RadiusY);
            }
            var conic = a as ConicGradientBrush;
            if (conic != null)
            {
                var other = (ConicGradientBrush)b;
                return conic.Center.Equals(other.Center) && conic.Angle.Equals(other.Angle);
            }
            return false;
        }

        internal static int Hash(GradientBrush brush)
        {
            unchecked
            {
                int hash = brush.GetType().Name.GetHashCode();
                hash = hash * 397 ^ (int)brush.SpreadMethod;
                hash = hash * 397 ^ brush.Opacity.GetHashCode();
                hash = hash * 397 ^ (brush.Transform == null ? 0 : brush.Transform.GetHashCode());
                hash = hash * 397 ^ brush.TransformOrigin.GetHashCode();
                foreach (GradientStop stop in brush.GradientStops)
                    hash = hash * 397 ^ stop.GetHashCode();

                var linear = brush as LinearGradientBrush;
                if (linear != null)
                    return (hash * 397 ^ linear.StartPoint.GetHashCode()) * 397 ^ linear.EndPoint.GetHashCode();
                var radial = brush as RadialGradientBrush;
                if (radial != null)
                {
                    hash = hash * 397 ^ radial.Center.GetHashCode();
                    hash = hash * 397 ^ radial.GradientOrigin.GetHashCode();
                    hash = hash * 397 ^ radial.RadiusX.GetHashCode();
                    return hash * 397 ^ radial.RadiusY.GetHashCode();
                }
                var conic = brush as ConicGradientBrush;
                if (conic != null)
                    return (hash * 397 ^ conic.Center.GetHashCode()) * 397 ^ conic.Angle.GetHashCode();
                return hash;
            }
        }

        internal static GradientBrush Clone(GradientBrush brush)
        {
            GradientBrush clone;
            var linear = brush as LinearGradientBrush;
            var radial = brush as RadialGradientBrush;
            var conic = brush as ConicGradientBrush;
            if (linear != null)
                clone = new LinearGradientBrush { StartPoint = linear.StartPoint, EndPoint = linear.EndPoint };
            else if (radial != null)
                clone = new RadialGradientBrush
                {
                    Center = radial.Center,
                    GradientOrigin = radial.GradientOrigin,
                    RadiusX = radial.RadiusX,
                    RadiusY = radial.RadiusY
                };
            else if (conic != null)
                clone = new ConicGradientBrush { Center = conic.Center, Angle = conic.Angle };
            else
                throw new NotSupportedException("Unknown gradient brush " + brush.GetType().Name + ".");

            foreach (GradientStop stop in brush.GradientStops)
                clone.GradientStops.Add(stop.Color, stop.Offset);
            clone.SpreadMethod = brush.SpreadMethod;
            clone.Opacity = brush.Opacity;
            clone.Transform = brush.Transform;
            clone.TransformOrigin = brush.TransformOrigin;
            return clone;
        }

        internal static float GetOffset(GradientBrush brush, Vector2 point, RectangleF bounds)
        {
            var linear = brush as LinearGradientBrush;
            if (linear != null)
            {
                Vector2 start = linear.StartPoint.ToPixels(bounds);
                Vector2 axis = linear.EndPoint.ToPixels(bounds) - start;
                float lengthSquared = axis.LengthSquared();
                return lengthSquared <= 0f ? 0f : Vector2.Dot(point - start, axis) / lengthSquared;
            }

            var radial = brush as RadialGradientBrush;
            if (radial != null)
            {
                float radiusX = radial.RadiusX.ToValue(bounds.Width);
                float radiusY = radial.RadiusY.ToValue(bounds.Height);
                if (radiusX <= 0f || radiusY <= 0f)
                    return 1f;

                // In the ellipse's unit-circle space, offset 1 is where the ray from the focus through the point meets
                // the circle, and the offset is the point's share of that distance.
                Vector2 center = radial.Center.ToPixels(bounds);
                var radius = new Vector2(radiusX, radiusY);
                Vector2 focus = (radial.GradientOrigin.ToPixels(bounds) - center) / radius;
                float focusLength = focus.Length();
                if (focusLength > .999f)
                    focus *= .999f / focusLength;
                Vector2 delta = (point - center) / radius - focus;
                float distance = delta.Length();
                if (distance <= 0f)
                    return 0f;
                Vector2 direction = delta / distance;
                float along = Vector2.Dot(focus, direction);
                float edge = -along + (float)Math.Sqrt(Math.Max(0f, along * along - focus.LengthSquared() + 1f));
                return edge <= 0f ? 1f : distance / edge;
            }

            var conic = brush as ConicGradientBrush;
            if (conic != null)
            {
                Vector2 delta = point - conic.Center.ToPixels(bounds);
                if (delta.X == 0f && delta.Y == 0f)
                    return 0f;
                // Clockwise from the top in screen space (y down).
                double degrees = Math.Atan2(delta.X, -delta.Y) * 180d / Math.PI - conic.Angle;
                degrees -= 360d * Math.Floor(degrees / 360d);
                return (float)(degrees / 360d);
            }
            return 0f;
        }
    }

    internal sealed class GradientSampler
    {
        readonly GradientBrush _brush;
        readonly float[] _offsets;
        readonly Vector4[] _colors;
        readonly bool _transformed;
        readonly float _cos;
        readonly float _sin;
        readonly Vector2 _inverseScale;
        readonly Vector2 _translate;

        internal GradientSampler(GradientBrush brush)
        {
            _brush = brush;
            var stops = new List<KeyValuePair<int, GradientStop>>();
            int index = 0;
            foreach (GradientStop stop in brush.GradientStops)
                stops.Add(new KeyValuePair<int, GradientStop>(index++, stop));
            stops.Sort((a, b) =>
            {
                int order = a.Value.Offset.CompareTo(b.Value.Offset);
                return order != 0 ? order : a.Key.CompareTo(b.Key);
            });
            _offsets = new float[stops.Count];
            _colors = new Vector4[stops.Count];
            for (int i = 0; i < stops.Count; i++)
            {
                _offsets[i] = stops[i].Value.Offset;
                Vector4 color = stops[i].Value.Color.ToVector4();
                color.W *= brush.Opacity;
                _colors[i] = color;
            }

            Transform transform = brush.Transform;
            if (transform != null)
            {
                float rotation = transform.RotationRadians;
                Vector2 scale = transform.ScaleFactor;
                _cos = (float)Math.Cos(-rotation);
                _sin = (float)Math.Sin(-rotation);
                _inverseScale = new Vector2(
                    Math.Abs(scale.X) < 1e-6f ? 0f : 1f / scale.X,
                    Math.Abs(scale.Y) < 1e-6f ? 0f : 1f / scale.Y);
                _translate = transform.Offset;
                _transformed = rotation != 0f || scale != Vector2.One || _translate != Vector2.Zero;
            }
        }

        internal Vector4 Sample(Vector2 point, RectangleF bounds)
        {
            if (_colors.Length == 0)
                return Vector4.Zero;
            if (_transformed)
            {
                // The brush is transformed, so sample where the inverse transform takes the point.
                Vector2 origin = _brush.TransformOrigin.ToPixels(bounds);
                Vector2 local = point - origin - _translate;
                local = new Vector2(local.X * _cos - local.Y * _sin, local.X * _sin + local.Y * _cos);
                point = origin + local * _inverseScale;
            }
            return ColorAt(Spread(GradientBrushes.GetOffset(_brush, point, bounds)));
        }

        float Spread(float offset)
        {
            if (float.IsNaN(offset) || float.IsInfinity(offset))
                return 0f;
            switch (_brush.SpreadMethod)
            {
                case GradientSpreadMethod.Repeat:
                    return offset - (float)Math.Floor(offset);
                case GradientSpreadMethod.Reflect:
                    float period = offset - 2f * (float)Math.Floor(offset * .5f);
                    return period > 1f ? 2f - period : period;
                default:
                    return offset;
            }
        }

        Vector4 ColorAt(float offset)
        {
            int last = _offsets.Length - 1;
            if (offset <= _offsets[0])
                return _colors[0];
            if (offset >= _offsets[last])
                return _colors[last];
            for (int i = 1; i <= last; i++)
            {
                if (offset > _offsets[i])
                    continue;
                float span = _offsets[i] - _offsets[i - 1];
                if (span <= 0f)
                    return _colors[i];
                return Vector4.Lerp(_colors[i - 1], _colors[i], (offset - _offsets[i - 1]) / span);
            }
            return _colors[last];
        }
    }
}
