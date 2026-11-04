using System;
using System.Collections.Generic;
using System.Diagnostics;
using Acrux.Core.Input;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using VRage.Game.GUI.TextPanel;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

namespace Acrux.Lcd
{
    /// <summary>
    /// Composes one LCD surface's frame from its layers, bottom to top: the tiled background, the content
    /// (the retained UI and its windows), the overlay and the cursor. The owner supplies state only: what the
    /// content and overlay are, where the pointer is and whether it shows. Drawing, the cursor styles and reuse
    /// of unchanged layers happen here.
    /// </summary>
    public sealed class LcdSurfaceLayers : global::Acrux.Toolkit.IExternalPeerHost
    {
        object _externalPeer;
        object global::Acrux.Toolkit.IExternalPeerHost.ExternalPeer { get { return _externalPeer; } set { _externalPeer = value; } }

        readonly IMyTextSurface _surface;
        readonly List<MySprite> _background = new List<MySprite>();
        readonly List<MySprite> _content = new List<MySprite>();
        readonly List<MySprite> _overlay = new List<MySprite>();
        readonly List<MySprite> _frame = new List<MySprite>();
        bool _hasContent;
        float _cursorScale = 1f;
        Vector2 _cursorSize = new Vector2(LcdCursor.DefaultSize);
        Vector2 _cursorPosition = new Vector2(float.NaN, float.NaN);

        public LcdSurfaceLayers(IMyTextSurface surface)
        {
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));
            _surface = surface;
        }

        /// <summary>
        /// The retained UI drawn into the content layer. It styles the cursor (the cursor its visual under the
        /// pointer asks for) and tells whether the content layer is still current.
        /// </summary>
        public LcdGuiHost Host { get; set; }

        // ---- Background layer ----

        public bool HasBackground => _background.Count > 0;

        /// <summary>Drops the tiles so the next <see cref="PrepareBackground"/> rebuilds them (size or colour changed).</summary>
        public void InvalidateBackground()
        {
            _background.Clear();
        }

        /// <summary>
        /// Builds the game's tiled LCD background in <paramref name="color"/> over the whole texture and
        /// <paramref name="viewBox"/> (a view box may reach past the texture). Does nothing while tiles exist.
        /// </summary>
        public void PrepareBackground(Color color, RectangleF viewBox)
        {
            if (_background.Count > 0)
                return;

            MySprite tile = MyTextSurfaceHelper.DEFAULT_BACKGROUND;
            tile.Color = color;

            Vector2 tileSize = tile.Size ?? new Vector2(MyTextSurfaceHelper.BACKGROUND_SIZE);
            if (tileSize.X <= 0f || tileSize.Y <= 0f)
                tileSize = new Vector2(MyTextSurfaceHelper.BACKGROUND_SIZE);

            Vector2 shift = MyTextSurfaceHelper.BACKGROUND_SHIFT;
            float stepX = Math.Abs(shift.X) > 0.001f ? Math.Abs(shift.X) : tileSize.X;
            float stepY = Math.Abs(shift.Y) > 0.001f ? Math.Abs(shift.Y) : tileSize.Y;
            if (stepX <= 0f || stepY <= 0f)
                return;

            Vector2 textureSize = _surface.TextureSize;
            Vector2 basePosition = (tile.Position ?? Vector2.Zero) + textureSize / 2f;
            RectangleF bounds = BackgroundBounds(textureSize, viewBox);
            Vector2 halfSize = tileSize * 0.5f;

            float startX = FirstTileCenter(bounds.X, basePosition.X, halfSize.X, stepX);
            float startY = FirstTileCenter(bounds.Y, basePosition.Y, halfSize.Y, stepY);
            for (float y = startY; y - halfSize.Y < bounds.Bottom; y += stepY)
            {
                for (float x = startX; x - halfSize.X < bounds.Right; x += stepX)
                {
                    tile.Position = new Vector2(x, y);
                    _background.Add(tile);
                }
            }
        }

        /// <summary>Appends the prepared background tiles; content that wants the background starts with them.</summary>
        public void DrawBackground(List<MySprite> sprites)
        {
            if (sprites == null)
                throw new ArgumentNullException(nameof(sprites));
            sprites.AddRange(_background);
        }

        static RectangleF BackgroundBounds(Vector2 textureSize, RectangleF viewBox)
        {
            var bounds = new RectangleF(0f, 0f, Math.Max(1f, textureSize.X), Math.Max(1f, textureSize.Y));
            if (viewBox.Width <= 0f || viewBox.Height <= 0f)
                return bounds;

            float left = Math.Min(bounds.X, viewBox.X);
            float top = Math.Min(bounds.Y, viewBox.Y);
            float right = Math.Max(bounds.Right, viewBox.Right);
            float bottom = Math.Max(bounds.Bottom, viewBox.Bottom);
            return new RectangleF(left, top, Math.Max(1f, right - left), Math.Max(1f, bottom - top));
        }

        static float FirstTileCenter(float boundsStart, float baseCenter, float halfSize, float step)
        {
            float baseStart = baseCenter - halfSize;
            return baseCenter + (float)Math.Floor((boundsStart - baseStart) / step) * step;
        }

        // ---- Content and overlay layers ----

        /// <summary>
        /// True when the content and overlay given last still show what the surface should: a frame was given
        /// and the retained UI has nothing new to draw. The owner can then skip building them and only
        /// <see cref="Compose"/>, which moves the cursor over the kept layers.
        /// </summary>
        public bool CanReuseContent => _hasContent && Host != null && !Host.NeedsRender;

        /// <summary>Forgets the kept content and overlay (the surface layout changed).</summary>
        public void InvalidateContent()
        {
            _hasContent = false;
            _content.Clear();
            _overlay.Clear();
        }

        /// <summary>Sets the content layer; the sprites are copied, so the list can be reused by the caller.</summary>
        public void SetContent(List<MySprite> sprites)
        {
            _content.Clear();
            if (sprites != null)
                _content.AddRange(sprites);
            _hasContent = true;
        }

        /// <summary>Sets the layer drawn over the content and under the cursor; null or empty removes it.</summary>
        public void SetOverlay(List<MySprite> sprites)
        {
            _overlay.Clear();
            if (sprites != null)
                _overlay.AddRange(sprites);
        }

        // ---- Cursor layer ----

        /// <summary>Cursor size multiplier; 0 hides the cursor.</summary>
        public float CursorScale
        {
            get { return _cursorScale; }
            set { _cursorScale = float.IsNaN(value) || value < 0f ? 0f : value; }
        }

        /// <summary>Cursor size in surface pixels at scale 1.</summary>
        public Vector2 CursorSize
        {
            get { return _cursorSize; }
            set { _cursorSize = value; }
        }

        /// <summary>Where the pointer is on the surface; NaN while it is not on it.</summary>
        public Vector2 CursorPosition
        {
            get { return _cursorPosition; }
            set { _cursorPosition = value; }
        }

        /// <summary>False while the pointer must not show even though it has a position (nobody is looking).</summary>
        public bool CursorVisible { get; set; } = true;

        /// <summary>
        /// The cursor style. Inherit (the default) follows the retained UI: the cursor the visual under the
        /// pointer asks for, or the arrow without a <see cref="Host"/>. Any other value overrides it; None hides it.
        /// </summary>
        public PointerCursor Cursor { get; set; } = PointerCursor.Inherit;

        /// <summary>The style <see cref="Compose"/> draws now.</summary>
        public PointerCursor EffectiveCursor
        {
            get
            {
                PointerCursor cursor = Cursor;
                if (cursor == PointerCursor.Inherit && Host != null && !Host.IsDisposed)
                    cursor = Host.PointerCursor;
                return cursor == PointerCursor.Inherit ? PointerCursor.Arrow : cursor;
            }
        }

        /// <summary>Whether the last <see cref="Compose"/> drew a cursor (the frame must be redrawn to remove it).</summary>
        public bool CursorDrawn { get; private set; }

        // ---- Frame ----

        /// <summary>
        /// The surface's frame: content, overlay, the cursor, then (when switched on) the frame-rate counter
        /// above everything: the surface's own frames, and below it how often each dynamic texture it shows (a
        /// 3D scene) actually got a new frame. The returned list is reused by the next call.
        /// </summary>
        public List<MySprite> Compose()
        {
            _frame.Clear();
            _frame.AddRange(_content);
            _frame.AddRange(_overlay);
            CursorDrawn = CursorVisible && _cursorScale > 0f &&
                          LcdCursor.Draw(_frame, EffectiveCursor, _cursorPosition, _cursorSize, _cursorScale);
            CountFrame();
            if (ShowFrameRate)
                DrawFrameRate(_frame);
            return _frame;
        }

        // ---- Frame-rate counter (diagnostics) ----

        // The "/acrux fps" chat command, shared by every surface of every client.
        internal static bool ShowFrameRate;

        static readonly Color FrameRateColor = Color.Lime;
        const string FRAME_RATE_FONT = "Debug";
        // Text scale per 512 pixels of surface height.
        const float FRAME_RATE_SCALE = 0.6f;

        long _windowStart;
        int _windowFrames;
        int _framesPerSecond = -1;
        string _frameRateText = string.Empty;
        // Dynamic textures this surface shows (a 3D map, a video): their content version at the last window,
        // and one line per texture of how often its pixels changed. A 3D scene's frame rate is that, not the
        // surface's: the surface recomposes over the same texture while a new frame is still painting.
        readonly Dictionary<string, int> _textureVersions = new Dictionary<string, int>();
        readonly HashSet<string> _texturesSeen = new HashSet<string>();
        readonly List<string> _textureLines = new List<string>();
        readonly List<string> _staleTextures = new List<string>();

        // Frames composed in each second: what the surface actually shows, not how often it was asked to.
        void CountFrame()
        {
            long now = Stopwatch.GetTimestamp();
            if (_windowStart == 0)
                _windowStart = now;
            _windowFrames++;
            long elapsed = now - _windowStart;
            if (elapsed < Stopwatch.Frequency)
                return;
            int fps = (int)Math.Round(_windowFrames * (double)Stopwatch.Frequency / elapsed);
            _windowStart = now;
            _windowFrames = 0;
            if (fps != _framesPerSecond)
            {
                _framesPerSecond = fps;
                _frameRateText = fps + " FPS";
            }
            if (ShowFrameRate)
                CountTextureUpdates(elapsed);
        }

        void CountTextureUpdates(long elapsed)
        {
            _textureLines.Clear();
            _texturesSeen.Clear();
            for (int i = 0; i < _content.Count; i++)
            {
                MySprite sprite = _content[i];
                string name = sprite.Data;
                if (sprite.Type != SpriteType.TEXTURE || string.IsNullOrEmpty(name) || !_texturesSeen.Add(name))
                    continue;
                int version = LcdDynamicTexture.GetContentVersion(name);
                if (version <= 0)
                    continue;
                int previous;
                if (_textureVersions.TryGetValue(name, out previous))
                {
                    int rate = (int)Math.Round((version - previous) * (double)Stopwatch.Frequency / elapsed);
                    _textureLines.Add(TextureLabel(name) + " " + rate + " FPS");
                }
                _textureVersions[name] = version;
            }
            _staleTextures.Clear();
            foreach (string name in _textureVersions.Keys)
            {
                if (!_texturesSeen.Contains(name))
                    _staleTextures.Add(name);
            }
            for (int i = 0; i < _staleTextures.Count; i++)
                _textureVersions.Remove(_staleTextures[i]);
        }

        // The name hint a generated texture was created with, without the session's numeric suffix.
        static string TextureLabel(string name)
        {
            int end = name.Length;
            while (end > 0 && (char.IsDigit(name[end - 1]) || name[end - 1] == '_' || name[end - 1] == '-' ||
                               name[end - 1] == ' ' || name[end - 1] == '#'))
                end--;
            return end > 0 ? name.Substring(0, end) : name;
        }

        void DrawFrameRate(List<MySprite> frame)
        {
            if (_framesPerSecond < 0)
                return;
            // The visible part of the texture is the surface's size, centred in it.
            Vector2 texture = _surface.TextureSize;
            Vector2 visible = _surface.SurfaceSize;
            Vector2 corner = (texture - visible) * 0.5f;
            float scale = FRAME_RATE_SCALE * Math.Max(0.25f, visible.Y / 512f);
            Vector2 position = corner + new Vector2(4f, 2f) * scale / FRAME_RATE_SCALE;
            frame.Add(new MySprite(SpriteType.TEXT, _frameRateText, position,
                null, FrameRateColor, FRAME_RATE_FONT, TextAlignment.LEFT, scale));
            // The Debug font's line height is about 30 pixels at scale 1.
            float line = 30f * scale;
            for (int i = 0; i < _textureLines.Count; i++)
            {
                position.Y += line;
                frame.Add(new MySprite(SpriteType.TEXT, _textureLines[i], position,
                    null, FrameRateColor, FRAME_RATE_FONT, TextAlignment.LEFT, scale));
            }
        }
    }
}
