using System.Collections.Generic;
using Acrux.Lcd;
using AcruxAppTemplate.Client.Resources;
using LcdEyeTracking.Api;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.IMyTextSurface;

namespace AcruxAppTemplate.Client.Surface
{
    /// <summary>
    /// An LCD script (pick "Acrux App Template" in the block's screen settings) that shows <see cref="Client.View.CounterView"/>.
    /// The view and its view model live as long as the script; each frame only lays out and draws what changed.
    /// </summary>
    [MyTextSurfaceScript(ID, TITLE)]
    public sealed class CounterSurfaceScript : MyTSSCommon
    {
        public const string ID = "AcruxAppTemplate_Counter";
        public const string TITLE = "Acrux App Template";

        readonly IMyTextSurface _surface;
        readonly IMyCubeBlock _block;
        readonly List<MySprite> _sprites = new List<MySprite>();

        SurfaceTheme _theme;
        ViewModel.CounterViewModel _model;
        LcdGuiHost _host;
        LcdSurfaceLayers _layers;
        bool _cursorMoved;
        RectangleF _viewport;
        long _pointerHandle;
        bool _disposed;

        public CounterSurfaceScript(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size)
        {
            _surface = surface;
            _block = block;
        }

        public override ScriptUpdate NeedsUpdate => ScriptUpdate.Update10;

        public override void Run()
        {
            base.Run();
            if (_disposed || !AcruxAppSession.IsUiReady)
                return;
            if (_host == null)
                CreateUi();
            _theme.SetColors(_surface.ScriptBackgroundColor, _surface.ScriptForegroundColor);
            Render();
        }

        void CreateUi()
        {
            _theme = new SurfaceTheme(_surface.ScriptBackgroundColor, _surface.ScriptForegroundColor);
            _model = new ViewModel.CounterViewModel(_theme);
            var view = new Client.View.CounterView { DataContext = _model };
            _host = new LcdGuiHost(_surface, view);

            // Acrux composes the frame: the view as the content layer and, on top, the cursor in the style the
            // control under the pointer asks for. This script only says where the pointer is.
            _layers = new LcdSurfaceLayers(_surface) { Host = _host };

            // The visible part of the texture; pointer positions are in the same texture pixels.
            Vector2 offset = (_surface.TextureSize - _surface.SurfaceSize) / 2f;
            _viewport = new RectangleF(offset, _surface.SurfaceSize);

            var tracker = AcruxAppSession.EyeTracking;
            if (tracker != null)
                _pointerHandle = tracker.Register(_block, _surface, OnPointerMoved, OnPointerLeft, OnButton, OnScroll,
                    OnCaptureLost, 0);
        }

        void Render()
        {
            if (_host == null)
                return;
            _host.Arrange(_viewport);
            bool contentChanged = !_layers.CanReuseContent;
            if (!contentChanged && !_cursorMoved)
                return;
            if (contentChanged)
            {
                _sprites.Clear();
                _host.Draw(_sprites);
                _layers.SetContent(_sprites);
            }
            _cursorMoved = false;
            // An unchanged view is not drawn again: the kept content layer is reused under the moved cursor.
            using (MySpriteDrawFrame frame = _surface.DrawFrame())
                frame.AddRange(_layers.Compose());
        }

        void SetCursor(Vector2 position)
        {
            _layers.CursorPosition = position;
            _cursorMoved = true;
        }

        void HideCursor()
        {
            SetCursor(new Vector2(float.NaN, float.NaN));
        }

        // Input arrives every frame the surface is looked at, faster than Run: redraw right away for responsive hover.
        void OnPointerMoved(Vector2 position)
        {
            if (_host == null)
                return;
            _host.MovePointer(position);
            SetCursor(position);
            Render();
        }

        void OnPointerLeft()
        {
            if (_host == null)
                return;
            _host.ClearPointer();
            HideCursor();
            Render();
        }

        void OnButton(int button, bool pressed)
        {
            // The callback reports a PointerButton as its int value.
            if (_host == null || (PointerButton)button != PointerButton.Primary)
                return;
            _host.SetPrimaryButton(pressed);
            Render();
        }

        void OnScroll(int delta)
        {
            if (_host != null && _host.Scroll(delta))
                Render();
        }

        void OnCaptureLost()
        {
            if (_host == null)
                return;
            _host.ClearPointer();
            HideCursor();
            Render();
        }

        public override void Dispose()
        {
            _disposed = true;
            var tracker = AcruxAppSession.EyeTracking;
            if (_pointerHandle != 0 && tracker != null)
                tracker.Unregister(_pointerHandle);
            _pointerHandle = 0;
            if (_host != null && AcruxAppSession.IsUiReady)
                _host.Dispose();
            _host = null;
            _layers = null;
            _model = null;
            base.Dispose();
        }
    }
}
