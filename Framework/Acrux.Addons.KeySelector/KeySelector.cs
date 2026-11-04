using System;
using System.Collections.Generic;
using Acrux.Controls;
using Acrux.Core;
using Acrux.Core.Input;
using Acrux.Core.Invalidation;
using Acrux.Core.PropertySystem;
using Acrux.Rendering;
using Acrux.Styling;
using VRageMath;

namespace Acrux.Addons
{
    /// <summary>
    /// Focusable key-binding editor. Clicking begins capture; the next newly
    /// pressed key becomes SelectedKey and ends capture.
    /// </summary>
    public sealed class KeySelector : TemplatedControl
    {
        static KeySelector() { }

        Border _border;
        TextBlock _text;
        readonly List<Key> _pressedKeys = new List<Key>(16);
        readonly Action<Key> _selectionChanged;
        readonly Action _stateChanged;

        public static readonly StyledProperty<Key> SelectedKeyProperty =
            StyledProperty.Register<KeySelector, Key>(
                nameof(SelectedKey), Key.None, UiInvalidation.Measure);
        public static readonly StyledProperty<string> CaptureTextProperty =
            StyledProperty.Register<KeySelector, string>(
                nameof(CaptureText), "Press a key…", UiInvalidation.Measure);
        public static readonly StyledProperty<Vector4> PaddingProperty =
            StyledProperty.Register<KeySelector, Vector4>(
                nameof(Padding), new Vector4(12f, 0f, 12f, 0f), UiInvalidation.Measure);
        public static readonly StyledProperty<Vector4> CornerRadiusProperty =
            StyledProperty.Register<KeySelector, Vector4>(nameof(CornerRadius), new Vector4(6f));
        public static readonly StyledProperty<Vector4> BorderThicknessProperty =
            StyledProperty.Register<KeySelector, Vector4>(nameof(BorderThickness), new Vector4(1f));
        public static readonly StyledProperty<float> TextScaleProperty =
            StyledProperty.RegisterInternal<KeySelector, float>(nameof(TextScale), 0.68f, UiInvalidation.Measure);
        public static readonly StyledProperty<BackgroundBrush> BackgroundProperty =
            StyledProperty.Register<KeySelector, BackgroundBrush>(
                "Background", new SolidColorBrush(new Color(32, 38, 42, 245)));
        public static readonly StyledProperty<Color> ForegroundProperty =
            StyledProperty.Register<KeySelector, Color>(nameof(Foreground), Color.White);
        public static readonly StyledProperty<Color> BorderBrushProperty =
            StyledProperty.Register<KeySelector, Color>(
                nameof(BorderBrush), new Color(82, 98, 107));

        /// <summary>For XAML: bind <see cref="SelectedKey"/> (two-way) instead of passing callbacks.</summary>
        public KeySelector()
            : this(Key.None, null, null)
        {
        }

        public KeySelector(Key selectedKey,
            Action<Key> selectionChanged = null, Action stateChanged = null)
        {
            SelectedKey = selectedKey;
            _selectionChanged = selectionChanged;
            _stateChanged = stateChanged;
        }

        public Key SelectedKey
        {
            get { return GetValue(SelectedKeyProperty); }
            set { SetValue(SelectedKeyProperty, value); }
        }

        public Key SelectedValue
        {
            get { return KeyProvider?.Invoke() ?? SelectedKey; }
        }

        public string CaptureText
        {
            get { return GetValue(CaptureTextProperty); }
            set { SetValue(CaptureTextProperty, value ?? string.Empty); }
        }

        public Vector4 Padding
        {
            get { return GetValue(PaddingProperty); }
            set { SetValue(PaddingProperty, value); }
        }

        public Vector4 CornerRadius
        {
            get { return GetValue(CornerRadiusProperty); }
            set { SetValue(CornerRadiusProperty, value); }
        }

        public Vector4 BorderThickness
        {
            get { return GetValue(BorderThicknessProperty); }
            set { SetValue(BorderThicknessProperty, value); }
        }

        public float TextScale
        {
            get { return GetValue(TextScaleProperty); }
            set { SetValue(TextScaleProperty, value); }
        }

        public BackgroundBrush Background
        {
            get { return GetValue(BackgroundProperty); }
            set { SetValue(BackgroundProperty, value); }
        }

        public Color Foreground
        {
            get { return GetValue(ForegroundProperty); }
            set { SetValue(ForegroundProperty, value); }
        }

        public Color BorderBrush
        {
            get { return GetValue(BorderBrushProperty); }
            set { SetValue(BorderBrushProperty, value); }
        }

        public bool IsOpen { get; private set; }
        public Func<Key> KeyProvider { get; set; }
        public Action<Key> SelectionChanged { get; set; }
        public Action<Key> Changed
        {
            get { return SelectionChanged; }
            set { SelectionChanged = value; }
        }

        public Action StateChanged { get; set; }
        public override bool AcceptsKeyboardFocus
        {
            get { return true; }
        }

        protected override void OnApplyTemplate()
        {
            _border = GetTemplateChild<Border>("PART_Border");
            _text = GetTemplateChild<TextBlock>("PART_Text");
        }

        protected override void UpdatePseudoClasses()
        {
            base.UpdatePseudoClasses();
            PseudoClasses.Set(PseudoClassNames.Pressed, IsPressed);
        }

        public void Configure(RectangleF bounds, float scale)
        {
            Arrange(bounds);
            Visible = true;
        }

        public void SetSelectedValue(Key value, bool notify = false)
        {
            if (SelectedKey == value)
                return;
            SelectedKey = value;
            if (notify)
                RaiseSelectionChanged(value);
        }

        public void Open()
        {
            if (IsOpen || !Enabled)
                return;
            IsOpen = true;
            PseudoClasses.Set(PseudoClassNames.Open, true);
            Invalidate(UiInvalidation.Measure);
            RaiseStateChanged();
        }

        public void Close()
        {
            if (!IsOpen)
                return;
            IsOpen = false;
            PseudoClasses.Set(PseudoClassNames.Open, false);
            Invalidate(UiInvalidation.Measure);
            RaiseStateChanged();
        }

        public void CloseSelector() { Close(); }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            EnsureStyleApplied();
            string text = IsOpen ? CaptureText : FormatKey(SelectedValue);
            Vector2 measured = TextMetrics == null
                ? Vector2.Zero
                : TextMetrics.Measure("White", text, TextScale);
            float horizontalPadding = Math.Max(0f, Padding.X) + Math.Max(0f, Padding.Z);
            float verticalPadding = Math.Max(0f, Padding.Y) + Math.Max(0f, Padding.W);
            return new Vector2(
                Math.Min(availableSize.X, Math.Max(160f, measured.X + horizontalPadding)),
                Math.Min(availableSize.Y, Math.Max(34f, measured.Y + verticalPadding)));
        }

        protected override bool HitTestSelf(Vector2 point) { return true; }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            ArrangeTemplate(bounds);
        }

        protected override void DrawSelf(RenderContext context)
        {
            if (_text != null)
                _text.Text = IsOpen ? CaptureText : FormatKey(SelectedValue);
        }

        internal override void PointerReleased(Vector2 point, bool clicked)
        {
            if (!clicked)
                return;
            if (IsOpen) Close(); else Open();
        }

        internal override void KeyboardInput(IInputSource input)
        {
            if (!IsOpen || input == null)
                return;
            if (input.IsNewKeyPressed(Key.Escape))
            {
                Close();
                return;
            }

            _pressedKeys.Clear();
            input.GetPressedKeys(_pressedKeys);
            for (int i = 0; i < _pressedKeys.Count; i++)
            {
                Key key = _pressedKeys[i];
                if (!input.IsNewKeyPressed(key))
                    continue;
                SelectedKey = key;
                RaiseSelectionChanged(key);
                Close();
                return;
            }
        }

        public static string FormatKey(Key key)
        {
            switch (key)
            {
                case Key.OemPlus: return "+";
                case Key.OemMinus: return "-";
                case Key.OemQuotes: return "'";
                case Key.Space: return "Space";
                case Key.None: return "None";
                default: return key.ToString();
            }
        }

        void RaiseSelectionChanged(Key key)
        {
            if (_selectionChanged != null) _selectionChanged(key);
            if (SelectionChanged != null) SelectionChanged(key);
        }

        void RaiseStateChanged()
        {
            if (_stateChanged != null) _stateChanged();
            if (StateChanged != null) StateChanged();
        }
    }
}
