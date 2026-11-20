using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Designer;
using Avalonia.Remote.Protocol.Input;
using Avalonia.Remote.Protocol.Viewport;

// Preview host for Acrux views: the IDE's preview plugin launches it and sends the edited XAML, the simulated LCD
// settings and the mouse; it keeps the view live (hover, clicks, animations) and answers with rendered frames.
// Everything runs on one worker thread; protocol messages are queued to it.
static class Program
{
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "acrux-preview-host.log");
    static readonly BlockingCollection<Action> Work = new BlockingCollection<Action>();
    static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60.0);

    static IAvaloniaRemoteTransportConnection _transport;
    static AcruxPreview.PreviewRenderer _renderer;
    static AcruxPreview.PreviewSettingsMessage _settings = new AcruxPreview.PreviewSettingsMessage();
    static Acrux.Animation.AnimationController _animations;
    static readonly List<Action> _nextFrame = new List<Action>();
    static long _frame;
    static double _dpi = 96;
    static long _sequence;
    static string _xaml = string.Empty;
    static string _assemblyPath;
    static string _xamlPath;
    static bool _rebuild;
    static bool _dirty;
    static int _cursor = -1;

    static int Main(string[] args)
    {
        Log("started: " + string.Join(" ", args));
        // The view models' preview clones report their stubbed commands and handlers through Trace.
        System.Diagnostics.Trace.Listeners.Add(new PreviewTraceListener());
        if (args.Length >= 3 && args[0] == "--render")
            return RenderToFile(args);
#if DEBUG
        if (args.Length >= 3 && args[0] == "--render-capture")
            return RenderCapture(args);
#endif
        Uri transport = null;
        string sessionId = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--transport")
                transport = new Uri(args[i + 1]);
            else if (args[i] == "--session-id")
                sessionId = args[i + 1];
        }
        if (transport == null || transport.Scheme != "tcp-bson")
        {
            Console.Error.WriteLine("Usage: --transport tcp-bson://127.0.0.1:port/ [--session-id id] [--method avalonia-remote] app");
            return 1;
        }

        // Avalonia's messages plus ours (the preview plugin's settings and cursor).
        _transport = new BsonTcpTransport(new DefaultMessageTypeResolver(typeof(Program).Assembly))
            .Connect(IPAddress.Parse(transport.Host), transport.Port).Result;
        _transport.OnMessage += (connection, message) => Work.Add(() => OnMessage(message));
        _transport.OnException += (connection, error) => { Log("transport closed: " + error.Message); Work.CompleteAdding(); };
        _transport.Start();
        _transport.Send(new StartDesignerSessionMessage { SessionId = sessionId });
        _transport.Send(new AcruxPreview.PreviewLanguagesMessage { Languages = AcruxPreview.PreviewLocalization.Languages() });
        Log("session started (" + sessionId + ")");

        _renderer = new AcruxPreview.PreviewRenderer();
        _animations = new Acrux.Animation.AnimationController(
            () => _frame,
            next => _nextFrame.Add(next),
            () => _dirty = true);
        Run();
        return 0;
    }

    // The worker: queued messages, then one animation frame and at most one rendered frame per tick.
    static void Run()
    {
        DateTime nextTick = DateTime.UtcNow;
        while (!Work.IsCompleted)
        {
            TimeSpan wait = nextTick - DateTime.UtcNow;
            Action job;
            if (Work.TryTake(out job, wait > TimeSpan.Zero ? wait : TimeSpan.Zero))
            {
                Safely(job);
                continue;
            }
            nextTick = DateTime.UtcNow + FrameInterval;
            _frame++;
            AcruxPreview.PreviewGame.Frame = _frame;
            if (_nextFrame.Count > 0)
            {
                Action[] callbacks = _nextFrame.ToArray();
                _nextFrame.Clear();
                foreach (Action callback in callbacks)
                    Safely(callback);
            }
            if (_rebuild)
                Safely(RebuildView);
            // As the game redraws a screen whose last frame is stale (animations, textures generated while drawing).
            if (_renderer.NeedsRender)
                _dirty = true;
            if (_dirty)
                Safely(SendFrame);
        }
    }

    static void OnMessage(object message)
    {
        switch (message)
        {
            case ClientRenderInfoMessage info:
                if (info.DpiX > 0)
                    _dpi = info.DpiX;
                _dirty = true;
                break;
            case ClientViewportAllocatedMessage viewport:
                if (viewport.DpiX > 0)
                    _dpi = viewport.DpiX;
                _dirty = true;
                break;
            case AcruxPreview.PreviewDesignValuesMessage design:
                AcruxPreview.DesignProperties.SetValues(design.Names, design.Values);
                _rebuild = true;
                break;
            case AcruxPreview.PreviewSettingsMessage settings:
                _settings = settings;
                _rebuild = true;
                break;
            case UpdateXamlMessage xaml:
                _xaml = xaml.Xaml ?? string.Empty;
                _assemblyPath = xaml.AssemblyPath;
                _xamlPath = xaml.XamlFileProjectPath;
                Log("xaml " + _xaml.Length + " chars: " + xaml.XamlFileProjectPath);
                _rebuild = true;
                break;
            // Pointer coordinates are in surface pixels (the frames are sent at 96 DPI).
            case PointerMovedEventMessage moved:
                _renderer.PointerMoved(moved.X, moved.Y);
                AfterInput();
                break;
            case PointerPressedEventMessage pressed:
                _renderer.PointerMoved(pressed.X, pressed.Y);
                _renderer.PointerButton((int)pressed.Button, true);
                AfterInput();
                break;
            case PointerReleasedEventMessage released:
                _renderer.PointerMoved(released.X, released.Y);
                _renderer.PointerButton((int)released.Button, false);
                AfterInput();
                break;
            case ScrollEventMessage scroll:
                _renderer.PointerMoved(scroll.X, scroll.Y);
                _renderer.Scroll(scroll.DeltaY);
                AfterInput();
                break;
        }
    }

    static void AfterInput()
    {
        _dirty = true;
        int cursor = (int)_renderer.Cursor;
        if (cursor != _cursor)
        {
            _cursor = cursor;
            _transport.Send(new AcruxPreview.PreviewCursorMessage { Cursor = cursor });
        }
    }

    static void RebuildView()
    {
        _rebuild = false;
        if (_xaml.Length == 0)
            return;
        string error = _renderer.Rebuild(_xaml, _assemblyPath, _settings, _animations, _xamlPath);
        if (error != null)
        {
            Log("preview failed: " + error);
            _transport.Send(new UpdateXamlResultMessage { Error = error, Exception = new ExceptionDetails { ExceptionType = "AcruxPreview", Message = error } });
            return;
        }
        _transport.Send(new UpdateXamlResultMessage { Handle = "acrux-preview" });
        _transport.Send(new AcruxPreview.PreviewNoticeMessage { Text = _renderer.Notice ?? string.Empty });
        if (_renderer.DesignProperties != null)
            _transport.Send(_renderer.DesignProperties);
        _dirty = true;
    }

    static void SendFrame()
    {
        _dirty = false;
        if (!_renderer.HasView)
            return;
        float scale = (float)(_dpi / 96.0);
        int width, height;
        byte[] pixels = _renderer.Draw(scale, out width, out height);
        _transport.Send(new FrameMessage
        {
            SequenceId = Interlocked.Increment(ref _sequence),
            Format = PixelFormat.Bgra8888,
            Data = pixels,
            Width = width,
            Height = height,
            Stride = width * 4,
            DpiX = _dpi,
            DpiY = _dpi,
        });
    }

    // Without an IDE: --render view.axaml out.png [mod assembly] [width height] [Type.Property=value ...]; renders one
    // frame to a PNG with those design property values, and prints the view's design properties.
    static int RenderToFile(string[] args)
    {
        var settings = new AcruxPreview.PreviewSettingsMessage();
        if (args.Length >= 6)
        {
            settings.Width = double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
            settings.Height = double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
        }
        var names = new List<string>();
        var values = new List<string>();
        for (int i = 6; i < args.Length; i++)
        {
            int equals = args[i].IndexOf('=');
            if (equals <= 0)
                continue;
            names.Add(args[i].Substring(0, equals));
            values.Add(args[i].Substring(equals + 1));
        }
        AcruxPreview.DesignProperties.SetValues(names.ToArray(), values.ToArray());
        var renderer = new AcruxPreview.PreviewRenderer();
        long frame = 0;
        var animations = new Acrux.Animation.AnimationController(() => frame, next => { }, () => { });
        string error = renderer.Rebuild(File.ReadAllText(args[1]), args.Length >= 4 ? args[3] : null, settings, animations,
            Path.GetFullPath(args[1]));
        if (error != null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        if (renderer.Notice != null)
            Console.WriteLine("notice: " + renderer.Notice);
        AcruxPreview.PreviewDesignPropertiesMessage properties = renderer.DesignProperties;
        if (properties != null)
            for (int i = 0; i < properties.Names.Length; i++)
                Console.WriteLine("design " + properties.Names[i] + " (" + properties.Kinds[i] + ") = " + properties.Values[i] +
                                  (properties.Options[i].Length == 0 ? "" : "  [" + properties.Options[i].Replace("\n", ", ") + "]"));
        int width, height;
        // As on the LCD, a generated texture created while drawing shows from the next frame on.
        byte[] pixels = renderer.Draw(1f, out width, out height);
        for (int i = 0; i < 4 && renderer.NeedsRender; i++)
        {
            frame++;
            AcruxPreview.PreviewGame.Frame = frame;
            pixels = renderer.Draw(1f, out width, out height);
        }
#if DEBUG
        string capture = Environment.GetEnvironmentVariable("ACRUX_RENDER_CAPTURE");
        if (!string.IsNullOrEmpty(capture))
            renderer.SaveCapture(capture);
#endif
        var info = new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        using (var bitmap = new SkiaSharp.SKBitmap(info))
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            using (SkiaSharp.SKData png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            using (FileStream file = File.Create(args[2]))
                png.SaveTo(file);
        }
        return 0;
    }

#if DEBUG
    // --render-capture capture.acruxcap out.png [--mod <mod Content folder>] [--scale <factor>]: renders a frame
    // "/acrux capture" saved in a dev build of the toolkit, with the textures it captured and the game's own sprites
    // and fonts; --mod adds the sprites a mod defines (its LCD texture definitions).
    static int RenderCapture(string[] args)
    {
        string mod = null;
        float scale = 1f;
        for (int i = 3; i < args.Length - 1; i++)
        {
            if (args[i] == "--mod")
                mod = args[++i];
            else if (args[i] == "--scale")
                scale = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
        }
        if (mod != null)
            AcruxPreview.GameTextures.UseMod(Path.GetFullPath(mod));
        Acrux.Lcd.LcdSurfaceCapture.Frame frame;
        try
        {
            using (var reader = new BinaryReader(File.OpenRead(args[1])))
                frame = Acrux.Lcd.LcdSurfaceCapture.Read(reader, (packed, size) => Adk.Compression.Zip.Zlib.Inflate(packed, size));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("cannot read " + args[1] + ": " + error.Message);
            return 1;
        }
        foreach (var texture in frame.Textures)
            AcruxPreview.PreviewGeneratedTextures.Inject(texture.Key, new VRageMath.Vector2I(texture.Value.Width, texture.Value.Height),
                texture.Value.Rgba);

        // Sprites are placed on the whole texture; the screen shows its centre, the surface's size.
        VRageMath.Vector2 visible = frame.SurfaceSize;
        VRageMath.Vector2 corner = (frame.TextureSize - visible) * 0.5f;
        int width = Math.Max(1, (int)Math.Round(visible.X * scale));
        int height = Math.Max(1, (int)Math.Round(visible.Y * scale));
        var info = new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        using (var bitmap = new SkiaSharp.SKBitmap(info))
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            VRageMath.Color background = frame.ScriptBackground;
            canvas.Clear(new SkiaSharp.SKColor(background.R, background.G, background.B));
            canvas.Scale(scale);
            canvas.Translate(-corner.X, -corner.Y);
            AcruxPreview.SpriteRasterizer.Paint(canvas, frame.Sprites, 1f, frame.TextureSize);
            canvas.Flush();
            using (SkiaSharp.SKData png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            using (FileStream file = File.Create(args[2]))
                png.SaveTo(file);
        }
        int missing = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sprite in frame.Sprites)
            if (sprite.Type == VRage.Game.GUI.TextPanel.SpriteType.TEXTURE && sprite.Data != null && seen.Add(sprite.Data) &&
                !AcruxPreview.PreviewGeneratedTextures.IsDefined(sprite.Data) && AcruxPreview.GameTextures.Image(sprite.Data) == null)
            {
                missing++;
                Console.WriteLine("missing texture: " + sprite.Data);
            }
        Console.WriteLine("rendered '" + frame.DisplayName + "' of " + frame.BlockName + " (" + frame.Sprites.Count + " sprites, " +
                          frame.Textures.Count + " captured textures, " + missing + " missing) to " + args[2]);
        return 0;
    }
#endif

    static void Safely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            Log("error: " + error);
        }
    }

    // Forwards what the preview clones write under their category (a bound command or handler called) to the log.
    sealed class PreviewTraceListener : System.Diagnostics.TraceListener
    {
        const string CATEGORY = "Acrux preview";

        public override void Write(string message)
        {
        }

        public override void WriteLine(string message)
        {
        }

        public override void WriteLine(string message, string category)
        {
            if (category == CATEGORY)
                Log("invoked " + message);
        }
    }

    internal static void Log(string line)
    {
        try
        {
            File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }
}
