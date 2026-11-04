using System;
using System.Collections.Generic;
using Acrux.Controls.Documents;
using Acrux.Core;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using VRageMath;

namespace Acrux.Controls
{
    public enum TextWrapping
    {
        NoWrap,
        Wrap
    }

    public enum TextTrimming
    {
        None,
        CharacterEllipsis,
        WordEllipsis
    }

    /// <summary>
    /// Attached text-rendering options.  These are intentionally independent of
    /// individual text controls so a theme can opt controls into text effects with
    /// owner-qualified setters such as TextOptions.DropShadow.
    /// </summary>
    public static class TextOptions
    {
        public static readonly StyledProperty<bool> DropShadowProperty =
            StyledProperty.RegisterAttachedInternal<Control, bool>(
                typeof(TextOptions),
                "DropShadow",
                false,
                UiInvalidation.Render);

        public static bool GetDropShadow(Control control)
        {
            if (control == null)
                throw new ArgumentNullException(nameof(control));
            return control.GetValue(DropShadowProperty);
        }

        public static void SetDropShadow(Control control, bool value)
        {
            if (control == null)
                throw new ArgumentNullException(nameof(control));
            control.SetValue(DropShadowProperty, value);
        }

        /// <summary>Forces attached-property registration before a stylesheet is parsed.</summary>
        public static void EnsureRegistered()
        {
            // Accessing the static field is sufficient; the CLR initializes this type first.
            StyledProperty<bool> ignored = DropShadowProperty;
        }
    }

    public class TextBlock : VisualElement
    {
        static TextBlock() { }

        public static readonly StyledProperty<string> TextProperty =
            StyledProperty.Register<TextBlock, string>(
                "Text", null, UiInvalidation.Measure);
        public static readonly StyledProperty<float> FontSizeProperty =
            StyledProperty.Register<TextBlock, float>(
                nameof(FontSize), 0.8f, UiInvalidation.Measure);
        public static readonly StyledProperty<string> FontFamilyProperty =
            StyledProperty.Register<TextBlock, string>(
                nameof(FontFamily), "White", UiInvalidation.Measure);
        public static readonly StyledProperty<Color> ForegroundProperty =
            StyledProperty.Register<TextBlock, Color>(
                nameof(Foreground), Color.White, UiInvalidation.Render);
        public static readonly StyledProperty<HorizontalAlignment> TextAlignmentProperty =
            StyledProperty.Register<TextBlock, HorizontalAlignment>(
                nameof(TextAlignment), HorizontalAlignment.Left, UiInvalidation.Render);
        public static readonly StyledProperty<VerticalAlignment> VerticalTextAlignmentProperty =
            StyledProperty.RegisterInternal<TextBlock, VerticalAlignment>(
                nameof(VerticalTextAlignment), VerticalAlignment.Center, UiInvalidation.Render);
        public static readonly StyledProperty<TextWrapping> TextWrappingProperty =
            StyledProperty.Register<TextBlock, TextWrapping>(
                nameof(TextWrapping), TextWrapping.NoWrap, UiInvalidation.Measure);
        public static readonly StyledProperty<TextTrimming> TextTrimmingProperty =
            StyledProperty.Register<TextBlock, TextTrimming>(
                nameof(TextTrimming), TextTrimming.None, UiInvalidation.Measure);
        const string Ellipsis = "\u2026";

        InlineCollection _inlines;
        readonly List<InlinePiece> _pieces = new List<InlinePiece>();
        readonly List<VisualElement> _inlineChildren = new List<VisualElement>();
        float _inlineLayoutWidth = -1f;
        Vector2 _inlineSize;

        struct InlineStyle
        {
            public string Family;
            public float Size;
            public Color Color;
            public bool ColorSet;
            public FontWeight Weight;
            public FontStyle Style;
            public bool Underline;
            public bool Strikethrough;
        }

        sealed class InlinePiece
        {
            public string Text;
            public VisualElement Element;
            public bool Break;
            public InlineStyle Style;
            public RectangleF Rect;
        }

        string _wrappedText;
        string _wrappedSource;
        string _wrappedFont;
        float _wrappedScale = -1f;
        float _wrappedWidth = -1f;
        string _trimmedText;
        string _trimmedSource;
        string _trimmedFont;
        float _trimmedScale = -1f;
        float _trimmedWidth = -1f;
        TextTrimming _trimmedMode;

        public string Text
        {
            get { return GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        public Func<string> TextProvider { get; set; }
        public Func<Color> ColorProvider { get; set; }

        public float FontSize
        {
            get { return GetValue(FontSizeProperty); }
            set { SetValue(FontSizeProperty, value); }
        }

        public string FontFamily
        {
            get { return GetValue(FontFamilyProperty) ?? "White"; }
            set { SetValue(FontFamilyProperty, string.IsNullOrWhiteSpace(value) ? "White" : value); }
        }

        public Color Foreground
        {
            get { return GetValue(ForegroundProperty); }
            set { SetValue(ForegroundProperty, value); }
        }

        /// <summary>Horizontal text alignment using the same primitive as layout.</summary>
        public HorizontalAlignment TextAlignment
        {
            get { return GetValue(TextAlignmentProperty); }
            set { SetValue(TextAlignmentProperty, value); }
        }

        /// <summary>Vertical text alignment using the same primitive as layout.</summary>
        public VerticalAlignment VerticalTextAlignment
        {
            get { return GetValue(VerticalTextAlignmentProperty); }
            set { SetValue(VerticalTextAlignmentProperty, value); }
        }

        public TextWrapping TextWrapping
        {
            get { return GetValue(TextWrappingProperty); }
            set { SetValue(TextWrappingProperty, value); }
        }

        public TextTrimming TextTrimming
        {
            get { return GetValue(TextTrimmingProperty); }
            set { SetValue(TextTrimmingProperty, value); }
        }

        /// <summary>TextBlock.Inlines: styled runs laid out as one wrapping text flow, replacing Text.</summary>
        public InlineCollection Inlines
        {
            get { return _inlines ?? (_inlines = new InlineCollection()); }
            set
            {
                _inlines = value;
                InvalidateInlines();
            }
        }

        /// <summary>Re-reads <see cref="Inlines"/> after it was changed in place.</summary>
        public void InvalidateInlines()
        {
            _inlineLayoutWidth = -1f;
            Invalidate(UiInvalidation.Measure);
        }

        bool HasInlines => _inlines != null && _inlines.Count > 0;

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            if (HasInlines)
                return MeasureInlines(availableSize);
            string text = TextProvider != null ? TextProvider() : Text;
            if (string.IsNullOrEmpty(text))
                return Vector2.Zero;
            text = GetDisplayText(text, availableSize.X);
            Vector2 measured = TextMetrics?.Measure(FontFamily, text, FontSize) ?? Vector2.Zero;
            return new Vector2(
                Math.Min(availableSize.X, Math.Max(0f, measured.X)),
                Math.Min(availableSize.Y, Math.Max(0f, measured.Y)));
        }

        protected override void DrawSelf(RenderContext context)
        {
            if (HasInlines)
            {
                DrawInlines(context);
                return;
            }
            string text = TextProvider != null ? TextProvider() : Text;
            text = GetDisplayText(text, Bounds.Width);
            HorizontalAlignment horizontal = NormalizeHorizontal(TextAlignment);
            VerticalAlignment vertical = NormalizeVertical(VerticalTextAlignment);
            Vector2 position = ResolveTextPosition(horizontal, vertical);
            Color color = ColorProvider?.Invoke() ?? Foreground;
            context.Text(
                FontFamily,
                text,
                position,
                FontSize,
                color,
                horizontal,
                vertical,
                TextOptions.GetDropShadow(this));
        }

        string GetDisplayText(string text, float width)
        {
            text = text ?? string.Empty;
            if (TextWrapping != TextWrapping.Wrap)
                return GetTrimmedText(text, width);
            if (TextMetrics == null ||
                float.IsPositiveInfinity(width) || width >= float.MaxValue * 0.5f)
                return text;
            width = Math.Max(1f, width);
            if (string.Equals(_wrappedSource, text, StringComparison.Ordinal) &&
                string.Equals(_wrappedFont, FontFamily, StringComparison.Ordinal) &&
                Math.Abs(_wrappedScale - FontSize) < 0.0001f &&
                Math.Abs(_wrappedWidth - width) < 0.5f)
                return _wrappedText ?? string.Empty;

            _wrappedSource = text;
            _wrappedFont = FontFamily;
            _wrappedScale = FontSize;
            _wrappedWidth = width;
            _wrappedText = Wrap(text, width);
            return _wrappedText;
        }

        string GetTrimmedText(string text, float width)
        {
            TextTrimming trimming = TextTrimming;
            if (trimming == TextTrimming.None || TextMetrics == null || text.Length == 0 ||
                float.IsPositiveInfinity(width) || width >= float.MaxValue * 0.5f)
                return text;
            width = Math.Max(0f, width);
            if (string.Equals(_trimmedSource, text, StringComparison.Ordinal) &&
                string.Equals(_trimmedFont, FontFamily, StringComparison.Ordinal) &&
                _trimmedMode == trimming &&
                Math.Abs(_trimmedScale - FontSize) < 0.0001f &&
                Math.Abs(_trimmedWidth - width) < 0.5f)
                return _trimmedText ?? string.Empty;

            _trimmedSource = text;
            _trimmedFont = FontFamily;
            _trimmedMode = trimming;
            _trimmedScale = FontSize;
            _trimmedWidth = width;
            _trimmedText = Trim(text, width, trimming);
            return _trimmedText;
        }

        const float FitTolerance = 0.05f;

        string Trim(string value, float maximumWidth, TextTrimming trimming)
        {
            if (MeasureWidth(value) <= maximumWidth + FitTolerance)
                return value;
            if (MeasureWidth(Ellipsis) > maximumWidth)
                return string.Empty;

            int low = 0;
            int high = value.Length - 1;
            while (low < high)
            {
                int middle = low + (high - low + 1) / 2;
                if (MeasureWidth(value.Substring(0, middle) + Ellipsis) <= maximumWidth) low = middle;
                else high = middle - 1;
            }

            if (trimming == TextTrimming.WordEllipsis)
            {
                int boundary = FindWordBoundary(value, low);
                if (boundary < low && boundary > 0)
                    low = boundary;
            }
            return value.Substring(0, low).TrimEnd() + Ellipsis;
        }

        string Wrap(string value, float maximumWidth)
        {
            string normalized = value.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] paragraphs = normalized.Split('\n');
            var lines = new List<string>();
            for (int paragraphIndex = 0; paragraphIndex < paragraphs.Length; paragraphIndex++)
            {
                string remaining = paragraphs[paragraphIndex];
                if (remaining.Length == 0)
                {
                    lines.Add(string.Empty);
                    continue;
                }
                while (remaining.Length > 0)
                {
                    if (MeasureWidth(remaining) <= maximumWidth + WrapTolerance)
                    {
                        lines.Add(remaining);
                        break;
                    }
                    int fit = FindFittingLength(remaining, maximumWidth);
                    int split = FindWordBoundary(remaining, fit);
                    string line = remaining.Substring(0, split).TrimEnd();
                    if (line.Length == 0)
                    {
                        split = Math.Max(1, fit);
                        line = remaining.Substring(0, split);
                    }
                    lines.Add(line);
                    remaining = remaining.Substring(split).TrimStart();
                }
            }
            return string.Join("\n", lines.ToArray());
        }

        int FindFittingLength(string value, float maximumWidth)
        {
            int low = 1;
            int high = value.Length;
            while (low < high)
            {
                int middle = low + (high - low + 1) / 2;
                if (MeasureWidth(value.Substring(0, middle)) <= maximumWidth + WrapTolerance) low = middle;
                else high = middle - 1;
            }
            return Math.Max(1, low);
        }

        static int FindWordBoundary(string value, int fit)
        {
            int maximum = Math.Min(value.Length, Math.Max(1, fit));
            for (int i = maximum; i > 0; i--)
                if (char.IsWhiteSpace(value[i - 1])) return i;
            return maximum;
        }

        float MeasureWidth(string value)
        {
            return TextMetrics.Measure(FontFamily, value, FontSize).X;
        }

        Vector2 ResolveTextPosition(
            HorizontalAlignment horizontalAlignment,
            VerticalAlignment verticalAlignment)
        {
            float x = Bounds.X;
            float y = Bounds.Y;

            switch (horizontalAlignment)
            {
                case HorizontalAlignment.Center:
                    x = Bounds.Center.X;
                    break;
                case HorizontalAlignment.Right:
                    x = Bounds.Right;
                    break;
            }

            switch (verticalAlignment)
            {
                case VerticalAlignment.Center:
                    y = Bounds.Center.Y;
                    break;
                case VerticalAlignment.Bottom:
                    y = Bounds.Bottom;
                    break;
            }

            return new Vector2(x, y);
        }

        // Arranging at the measured width round-trips it through padding and margins, which can lose a float ULP; a line
        // that fit when measured must still fit then, or it wraps into a line the measured height has no room for.
        const float WrapTolerance = .01f;

        Vector2 MeasureInlines(Vector2 availableSize)
        {
            float width = TextWrapping == TextWrapping.Wrap ? availableSize.X : float.PositiveInfinity;
            BuildPieces();
            for (int i = 0; i < _inlineChildren.Count; i++)
                _inlineChildren[i].Measure(new Vector2(width, float.PositiveInfinity));
            LayoutPieces(width);
            return new Vector2(Math.Min(availableSize.X, _inlineSize.X), Math.Min(availableSize.Y, _inlineSize.Y));
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            if (!HasInlines)
            {
                base.ArrangeChildren(bounds);
                return;
            }
            if (Math.Abs(_inlineLayoutWidth - bounds.Width) > 0.5f)
                LayoutPieces(TextWrapping == TextWrapping.Wrap ? bounds.Width : float.PositiveInfinity);
            for (int i = 0; i < _pieces.Count; i++)
            {
                InlinePiece piece = _pieces[i];
                if (piece.Element != null)
                    piece.Element.Arrange(Offset(piece.Rect, bounds.Position));
            }
        }

        void DrawInlines(RenderContext context)
        {
            bool shadow = TextOptions.GetDropShadow(this);
            Color foreground = ColorProvider?.Invoke() ?? Foreground;
            for (int i = 0; i < _pieces.Count; i++)
            {
                InlinePiece piece = _pieces[i];
                if (piece.Text == null)
                    continue;
                RectangleF rect = Offset(piece.Rect, Bounds.Position);
                Color color = piece.Style.ColorSet ? piece.Style.Color : foreground;
                context.Text(piece.Style.Family, piece.Text, rect.Position, piece.Style.Size, color,
                    HorizontalAlignment.Left, VerticalAlignment.Top, shadow);
                float thickness = Math.Max(1f, piece.Style.Size * 2f);
                float width = TextMetrics == null ? rect.Width : TextMetrics.Measure(piece.Style.Family, piece.Text.TrimEnd(), piece.Style.Size).X;
                if (piece.Style.Underline)
                    context.FillRectangle(new RectangleF(rect.X, rect.Bottom - thickness, width, thickness), color);
                if (piece.Style.Strikethrough)
                    context.FillRectangle(new RectangleF(rect.X, rect.Center.Y - thickness * .5f, width, thickness), color);
            }
        }

        static RectangleF Offset(RectangleF rect, Vector2 origin)
        {
            return new RectangleF(rect.Position + origin, rect.Size);
        }

        void BuildPieces()
        {
            _pieces.Clear();
            var children = new List<VisualElement>();
            var root = new InlineStyle
            {
                Family = FontFamily,
                Size = FontSize,
                Color = Foreground,
                Weight = FontWeight.Normal,
                Style = FontStyle.Normal
            };
            Collect(_inlines, root, children);
            for (int i = _inlineChildren.Count - 1; i >= 0; i--)
                if (!children.Contains(_inlineChildren[i]))
                    RemoveChild(_inlineChildren[i]);
            for (int i = 0; i < children.Count; i++)
                if (!ReferenceEquals(children[i].Parent, this))
                    AddChild(children[i]);
            _inlineChildren.Clear();
            _inlineChildren.AddRange(children);
            _inlineLayoutWidth = -1f;
        }

        void Collect(IList<Inline> inlines, InlineStyle parent, List<VisualElement> children)
        {
            if (inlines == null)
                return;
            for (int i = 0; i < inlines.Count; i++)
            {
                Inline inline = inlines[i];
                if (inline == null)
                    continue;
                InlineStyle style = Apply(parent, inline);
                if (inline is LineBreak)
                {
                    _pieces.Add(new InlinePiece { Break = true, Style = style });
                    continue;
                }
                var container = inline as InlineUIContainer;
                if (container != null)
                {
                    if (container.Child != null)
                    {
                        children.Add(container.Child);
                        _pieces.Add(new InlinePiece { Element = container.Child, Style = style });
                    }
                    continue;
                }
                var span = inline as Span;
                if (span != null)
                {
                    Collect(span.Inlines, style, children);
                    continue;
                }
                var run = inline as Run;
                if (run != null && !string.IsNullOrEmpty(run.Text))
                    AddWords(run.Text, style);
            }
        }

        void AddWords(string text, InlineStyle style)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            int start = 0;
            for (int i = 0; i <= normalized.Length; i++)
            {
                bool end = i == normalized.Length;
                char c = end ? '\0' : normalized[i];
                if (!end && c != ' ' && c != '\n')
                    continue;
                int length = end ? i - start : i - start + (c == ' ' ? 1 : 0);
                if (length > 0)
                    _pieces.Add(new InlinePiece { Text = normalized.Substring(start, length), Style = style });
                if (c == '\n')
                    _pieces.Add(new InlinePiece { Break = true, Style = style });
                start = i + 1;
            }
        }

        static InlineStyle Apply(InlineStyle parent, Inline inline)
        {
            InlineStyle style = parent;
            if (!string.IsNullOrWhiteSpace(inline.FontFamily))
                style.Family = inline.FontFamily;
            if (inline.FontSize.HasValue)
                style.Size = inline.FontSize.Value;
            if (inline.Foreground.HasValue)
            {
                style.Color = inline.Foreground.Value;
                style.ColorSet = true;
            }
            if (inline.FontWeight.HasValue)
                style.Weight = inline.FontWeight.Value;
            if (inline.FontStyle.HasValue)
                style.Style = inline.FontStyle.Value;
            if (inline.TextDecorations != null)
            {
                style.Underline |= inline.TextDecorations.Has(TextDecorationLocation.Underline);
                style.Strikethrough |= inline.TextDecorations.Has(TextDecorationLocation.Strikethrough);
            }
            return style;
        }

        Vector2 MeasurePiece(InlinePiece piece)
        {
            if (piece.Element != null)
                return piece.Element.DesiredSize;
            if (piece.Break)
                return new Vector2(0f, MeasureText(piece.Style, "X").Y);
            return MeasureText(piece.Style, piece.Text);
        }

        Vector2 MeasureText(InlineStyle style, string text)
        {
            if (TextMetrics == null)
                return Vector2.Zero;
            return TextMetrics.Measure(FontResolver.Resolve(style.Family, style.Weight, style.Style), text, style.Size);
        }

        void LayoutPieces(float maximumWidth)
        {
            _inlineLayoutWidth = maximumWidth;
            float x = 0f;
            float y = 0f;
            float lineHeight = 0f;
            float widest = 0f;
            int lineStart = 0;
            for (int i = 0; i < _pieces.Count; i++)
            {
                InlinePiece piece = _pieces[i];
                piece.Style.Family = FontResolver.Resolve(piece.Style.Family, piece.Style.Weight, piece.Style.Style);
                piece.Style.Weight = FontWeight.Normal;
                piece.Style.Style = FontStyle.Normal;
                Vector2 size = MeasurePiece(piece);
                if (piece.Break)
                {
                    lineHeight = Math.Max(lineHeight, size.Y);
                    FinishLine(lineStart, i, x, lineHeight, maximumWidth);
                    widest = Math.Max(widest, x);
                    y += lineHeight;
                    x = 0f;
                    lineHeight = 0f;
                    lineStart = i + 1;
                    piece.Rect = new RectangleF(0f, y, 0f, 0f);
                    continue;
                }
                float visibleWidth = piece.Text == null ? size.X : MeasureText(piece.Style, piece.Text.TrimEnd()).X;
                if (x > 0f && x + visibleWidth > maximumWidth + WrapTolerance)
                {
                    FinishLine(lineStart, i, x, lineHeight, maximumWidth);
                    widest = Math.Max(widest, x);
                    y += lineHeight;
                    x = 0f;
                    lineHeight = 0f;
                    lineStart = i;
                }
                piece.Rect = new RectangleF(x, y, size.X, size.Y);
                x += size.X;
                lineHeight = Math.Max(lineHeight, size.Y);
            }
            FinishLine(lineStart, _pieces.Count, x, lineHeight, maximumWidth);
            widest = Math.Max(widest, x);
            _inlineSize = new Vector2(widest, y + lineHeight);
        }

        void FinishLine(int start, int end, float lineWidth, float lineHeight, float maximumWidth)
        {
            float shift = 0f;
            HorizontalAlignment alignment = NormalizeHorizontal(TextAlignment);
            if (!float.IsPositiveInfinity(maximumWidth) && alignment != HorizontalAlignment.Left)
                shift = alignment == HorizontalAlignment.Center
                    ? (maximumWidth - lineWidth) * .5f
                    : maximumWidth - lineWidth;
            for (int i = start; i < end; i++)
            {
                InlinePiece piece = _pieces[i];
                RectangleF rect = piece.Rect;
                rect.X += Math.Max(0f, shift);
                rect.Y += (lineHeight - rect.Height) * .5f;
                piece.Rect = rect;
            }
        }

        static HorizontalAlignment NormalizeHorizontal(HorizontalAlignment alignment)
        {
            return alignment == HorizontalAlignment.Stretch
                ? HorizontalAlignment.Left
                : alignment;
        }

        static VerticalAlignment NormalizeVertical(VerticalAlignment alignment)
        {
            return alignment == VerticalAlignment.Stretch
                ? VerticalAlignment.Top
                : alignment;
        }
    }
}
