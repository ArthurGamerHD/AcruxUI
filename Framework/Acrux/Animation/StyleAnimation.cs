using System;
using System.Collections.Generic;
using System.Globalization;
using Acrux.Core;
using Acrux.Core.PropertySystem;
using Acrux.Markup;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Animation
{
    // Avalonia's PlaybackDirection and FillMode.
    internal enum StylePlaybackDirection
    {
        Normal,
        Reverse,
        Alternate,
        AlternateReverse
    }

    internal enum StyleFillMode
    {
        None,
        Forward,
        Backward,
        Both
    }

    internal sealed class StyleKeyFrame
    {
        public StyleKeyFrame(double cue)
        {
            Cue = cue;
        }

        public double Cue { get; }
        public List<Setter> Setters { get; } = new List<Setter>();
    }

    // An Avalonia <Animation> inside <Style.Animations>: it runs on every control the style matches, for as long as it
    // matches. The subset supported is Duration, Delay, IterationCount, PlaybackDirection, FillMode, Easing, SpeedRatio
    // and key frames by Cue or KeyTime.
    internal sealed class StyleAnimation
    {
        public TimeSpan Duration;
        public TimeSpan Delay;
        // Negative: Infinite.
        public long IterationCount = 1;
        public StylePlaybackDirection PlaybackDirection;
        public StyleFillMode FillMode;
        public EasingMode Easing;
        public double SpeedRatio = 1d;
        public readonly List<StyleKeyFrame> KeyFrames = new List<StyleKeyFrame>();

        public static StyleAnimation Create(IDictionary<string, string> attributes)
        {
            var animation = new StyleAnimation();
            foreach (KeyValuePair<string, string> attribute in attributes)
            {
                string value = attribute.Value.Trim();
                switch (attribute.Key)
                {
                    case "Duration":
                        animation.Duration = ParseTime(value, "Duration");
                        break;
                    case "Delay":
                        animation.Delay = ParseTime(value, "Delay");
                        break;
                    case "IterationCount":
                        animation.IterationCount = ParseIterationCount(value);
                        break;
                    case "PlaybackDirection":
                        animation.PlaybackDirection = ParseEnum<StylePlaybackDirection>(value, "PlaybackDirection");
                        break;
                    case "FillMode":
                        animation.FillMode = ParseEnum<StyleFillMode>(value, "FillMode");
                        break;
                    case "Easing":
                        animation.Easing = ParseEasing(value);
                        break;
                    case "SpeedRatio":
                        double ratio;
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio) ||
                            ratio < 0d)
                            throw new FormatException("Invalid Animation SpeedRatio '" + value + "'.");
                        animation.SpeedRatio = ratio;
                        break;
                    default:
                        throw new FormatException("Animation does not support the '" + attribute.Key + "' attribute.");
                }
            }
            return animation;
        }

        public StyleKeyFrame AddKeyFrame(IDictionary<string, string> attributes)
        {
            string cue, keyTime;
            bool hasCue = attributes.TryGetValue("Cue", out cue);
            bool hasKeyTime = attributes.TryGetValue("KeyTime", out keyTime);
            if (hasCue == hasKeyTime)
                throw new FormatException("A KeyFrame requires either a Cue or a KeyTime.");
            foreach (string name in attributes.Keys)
                if (name != "Cue" && name != "KeyTime")
                    throw new FormatException("KeyFrame does not support the '" + name + "' attribute.");
            double position = hasCue ? ParseCue(cue.Trim()) : KeyTimeCue(ParseTime(keyTime.Trim(), "KeyTime"));
            var frame = new StyleKeyFrame(position);
            KeyFrames.Add(frame);
            return frame;
        }

        double KeyTimeCue(TimeSpan keyTime)
        {
            if (Duration <= TimeSpan.Zero)
                return 1d;
            double cue = keyTime.TotalSeconds / Duration.TotalSeconds;
            if (cue < 0d || cue > 1d)
                throw new FormatException("A KeyFrame KeyTime must be within the animation's Duration.");
            return cue;
        }

        static double ParseCue(string value)
        {
            string number = value.EndsWith("%", StringComparison.Ordinal) ? value.Substring(0, value.Length - 1) : value;
            double cue;
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out cue))
                throw new FormatException("Invalid KeyFrame Cue '" + value + "'.");
            cue /= 100d;
            if (cue < 0d || cue > 1d)
                throw new FormatException("A KeyFrame Cue must be within 0% and 100%.");
            return cue;
        }

        static TimeSpan ParseTime(string value, string name)
        {
            TimeSpan time;
            if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time) || time < TimeSpan.Zero)
                throw new FormatException("Invalid Animation " + name + " '" + value + "'.");
            return time;
        }

        static long ParseIterationCount(string value)
        {
            if (value.EndsWith("Infinite", StringComparison.OrdinalIgnoreCase))
                return -1;
            long count;
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count))
                throw new FormatException("Invalid Animation IterationCount '" + value + "'.");
            return count;
        }

        static T ParseEnum<T>(string value, string name) where T : struct
        {
            T result;
            if (!Enum.TryParse(value, false, out result))
                throw new FormatException("Invalid Animation " + name + " '" + value + "'.");
            return result;
        }

        // Avalonia's easing class names, for the curves Acrux has.
        static EasingMode ParseEasing(string value)
        {
            switch (value)
            {
                case "LinearEasing": return EasingMode.Linear;
                case "QuadraticEaseIn": return EasingMode.EaseInQuadratic;
                case "QuadraticEaseOut": return EasingMode.EaseOutQuadratic;
                case "QuadraticEaseInOut": return EasingMode.EaseInOutQuadratic;
                case "CubicEaseIn": return EasingMode.EaseInCubic;
                case "CubicEaseOut": return EasingMode.EaseOutCubic;
                case "CubicEaseInOut": return EasingMode.EaseInOutCubic;
                default: throw new FormatException("Unsupported Animation Easing '" + value + "'.");
            }
        }
    }

    // Driven by the controller with the frames elapsed since it started, instead of a progress within a fixed duration.
    internal interface IFrameClockedStep
    {
        bool Advance(long elapsedFrames, out bool changed);
    }

    // One style animation running on one control. Time only passes while the control is shown, as Avalonia pauses an
    // animation on an invisible control.
    internal sealed class StyleAnimationInstance : IAnimationStep, IFrameClockedStep
    {
        const double FramesPerSecond = 60d;
        static readonly PropertyConverter Converter = new PropertyConverter();

        readonly Control _target;
        Track[] _tracks;
        AnimationHandle _handle;
        long _lastFrames;
        double _seconds;

        public StyleAnimationInstance(StyleAnimation definition, Control target)
        {
            Definition = definition;
            _target = target;
        }

        public StyleAnimation Definition { get; }

        public int DurationFrames => 1;
        public bool RequiresRedraw => true;

        public void Start(AnimationController clock)
        {
            if (clock == null || clock.IsDisposed)
                return;
            _lastFrames = 0;
            _seconds = 0d;
            if (IsShown())
                ApplyAt(0d);
            _handle = clock.Run(_target, () => { }, this);
        }

        public void Stop()
        {
            AnimationHandle handle = _handle;
            _handle = null;
            if (handle != null && handle.IsRunning)
                handle.Cancel();
            if (_tracks != null)
                for (int i = 0; i < _tracks.Length; i++)
                    _tracks[i].Clear(_target);
        }

        public void Begin()
        {
        }

        // Completing the animation at once (AnimationHandle.Complete) lands on its end.
        public void Apply(float progress)
        {
            if (progress >= 1f)
                Finish();
        }

        public bool Advance(long elapsedFrames, out bool changed)
        {
            long delta = elapsedFrames - _lastFrames;
            _lastFrames = elapsedFrames;
            if (!IsShown())
            {
                changed = false;
                return false;
            }
            _seconds += delta / FramesPerSecond * Definition.SpeedRatio;
            return ApplyAt(_seconds, out changed);
        }

        void ApplyAt(double seconds)
        {
            bool changed;
            ApplyAt(seconds, out changed);
        }

        bool ApplyAt(double seconds, out bool changed)
        {
            StyleAnimation definition = Definition;
            changed = false;
            double delay = definition.Delay.TotalSeconds;
            if (seconds < delay)
            {
                if (definition.FillMode == StyleFillMode.Backward || definition.FillMode == StyleFillMode.Both)
                    changed = SetProgress(Directed(0d, 0));
                else
                    changed = ClearTracks();
                return false;
            }
            double duration = definition.Duration.TotalSeconds;
            double iterations = duration <= 0d ? double.PositiveInfinity : (seconds - delay) / duration;
            long count = definition.IterationCount;
            if (count >= 0 && iterations >= count || double.IsInfinity(iterations))
            {
                changed = Finish();
                return true;
            }
            long index = (long)Math.Floor(iterations);
            changed = SetProgress(Directed(iterations - index, index));
            return false;
        }

        bool Finish()
        {
            StyleAnimation definition = Definition;
            bool fill = definition.FillMode == StyleFillMode.Forward || definition.FillMode == StyleFillMode.Both;
            if (!fill || definition.IterationCount == 0)
                return ClearTracks();
            long last = definition.IterationCount < 0 ? 0 : definition.IterationCount - 1;
            return SetProgress(Directed(1d, last));
        }

        double Directed(double local, long iteration)
        {
            bool odd = (iteration & 1L) == 1L;
            switch (Definition.PlaybackDirection)
            {
                case StylePlaybackDirection.Reverse:
                    return 1d - local;
                case StylePlaybackDirection.Alternate:
                    return odd ? 1d - local : local;
                case StylePlaybackDirection.AlternateReverse:
                    return odd ? local : 1d - local;
                default:
                    return local;
            }
        }

        bool SetProgress(double progress)
        {
            double eased = Easings.Apply(Definition.Easing, (float)progress);
            Track[] tracks = EnsureTracks();
            bool changed = false;
            for (int i = 0; i < tracks.Length; i++)
                changed |= tracks[i].Apply(_target, eased);
            return changed;
        }

        bool ClearTracks()
        {
            if (_tracks == null)
                return false;
            bool changed = false;
            for (int i = 0; i < _tracks.Length; i++)
                changed |= _tracks[i].Clear(_target);
            return changed;
        }

        bool IsShown()
        {
            for (var visual = _target as VisualElement; visual != null; visual = visual.Parent)
                if (!visual.Visible)
                    return false;
            return _target.Visible;
        }

        Track[] EnsureTracks()
        {
            if (_tracks != null)
                return _tracks;
            var tracks = new List<Track>();
            var byName = new Dictionary<string, Track>(StringComparer.Ordinal);
            List<StyleKeyFrame> frames = Definition.KeyFrames;
            for (int f = 0; f < frames.Count; f++)
                for (int s = 0; s < frames[f].Setters.Count; s++)
                {
                    Setter setter = frames[f].Setters[s];
                    Track track;
                    if (!byName.TryGetValue(setter.Property, out track))
                    {
                        track = CreateTrack(setter.Property);
                        byName[setter.Property] = track;
                        if (track != null)
                            tracks.Add(track);
                    }
                    if (track != null)
                        track.Add(frames[f].Cue, setter.Value);
                }
            return _tracks = tracks.ToArray();
        }

        Track CreateTrack(string name)
        {
            IStyledProperty property = FindProperty(name);
            if (property == null)
            {
                Acrux.Toolkit.Server.ExternalInterop.WarnOnce("Animation setter '" + name + "' is not a property of " +
                                                              _target.GetType().Name + ".");
                return null;
            }
            Type type = property.ValueType;
            if (type == typeof(float))
                return new Track<float>((StyledProperty<float>)property, (a, b, t) => a + (b - a) * (float)t);
            if (type == typeof(double))
                return new Track<double>((StyledProperty<double>)property, (a, b, t) => a + (b - a) * t);
            if (type == typeof(int))
                return new Track<int>((StyledProperty<int>)property, (a, b, t) => (int)Math.Round(a + (b - a) * t));
            if (type == typeof(Color))
                return new Track<Color>((StyledProperty<Color>)property, LerpColor);
            if (type == typeof(Vector2))
                return new Track<Vector2>((StyledProperty<Vector2>)property, (a, b, t) => Vector2.Lerp(a, b, (float)t));
            if (type == typeof(Vector4))
                return new Track<Vector4>((StyledProperty<Vector4>)property, (a, b, t) => Vector4.Lerp(a, b, (float)t));
            Acrux.Toolkit.Server.ExternalInterop.WarnOnce("Animation cannot interpolate " + name + " (" + type.Name + ").");
            return null;
        }

        IStyledProperty FindProperty(string name)
        {
            IList<IStyledProperty> properties = StyledPropertyCatalog.Snapshot();
            for (int i = 0; i < properties.Count; i++)
                if (string.Equals(properties[i].Name, name, StringComparison.Ordinal) && properties[i].Matches(_target))
                    return properties[i];
            return null;
        }

        static Color LerpColor(Color a, Color b, double t)
        {
            float f = (float)t;
            return new Color(
                (int)Math.Round(a.R + (b.R - a.R) * f),
                (int)Math.Round(a.G + (b.G - a.G) * f),
                (int)Math.Round(a.B + (b.B - a.B) * f),
                (int)Math.Round(a.A + (b.A - a.A) * f));
        }

        abstract class Track
        {
            public abstract void Add(double cue, object value);
            public abstract bool Apply(Control target, double progress);
            public abstract bool Clear(Control target);
        }

        // A property's key frames; a missing first (0%) or last (100%) frame is the property's value without the
        // animation, as in Avalonia.
        sealed class Track<T> : Track
        {
            readonly StyledProperty<T> _property;
            readonly Func<T, T, double, T> _interpolate;
            readonly List<double> _cues = new List<double>();
            readonly List<T> _values = new List<T>();

            public Track(StyledProperty<T> property, Func<T, T, double, T> interpolate)
            {
                _property = property;
                _interpolate = interpolate;
            }

            public override void Add(double cue, object value)
            {
                T converted = value is T ? (T)value : (T)Converter.Convert(value as string ?? value.ToString(), typeof(T));
                int index = _cues.Count;
                while (index > 0 && _cues[index - 1] > cue)
                    index--;
                _cues.Insert(index, cue);
                _values.Insert(index, converted);
            }

            public override bool Apply(Control target, double progress)
            {
                double fromCue = 0d, toCue = 1d;
                T from = default(T), to = default(T);
                bool hasFrom = false, hasTo = false;
                for (int i = 0; i < _cues.Count; i++)
                {
                    if (_cues[i] <= progress)
                    {
                        fromCue = _cues[i];
                        from = _values[i];
                        hasFrom = true;
                        continue;
                    }
                    toCue = _cues[i];
                    to = _values[i];
                    hasTo = true;
                    break;
                }
                if (!hasFrom)
                    from = target.GetBaseValue(_property);
                if (!hasTo)
                {
                    if (hasFrom && fromCue >= 1d)
                        return target.SetAnimatedValue(_property, from);
                    to = target.GetBaseValue(_property);
                }
                double span = toCue - fromCue;
                double t = span <= 0d ? 1d : (progress - fromCue) / span;
                return target.SetAnimatedValue(_property, _interpolate(from, to, t));
            }

            public override bool Clear(Control target)
            {
                return target.ClearAnimatedValue(_property);
            }
        }
    }
}
