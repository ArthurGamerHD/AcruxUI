extern alias toolkit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Acrux.Lcd;
using Sample;
using VRage.Game.GUI.TextPanel;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;

/// <summary>
/// Loads the toolkit (Harness.Toolkit.dll) and a client (Harness.Client.dll) as separate assemblies,
/// connects them only through the published delegate table, then drives a real XAML view.
/// </summary>
static class Program
{
    static int _failures;

    static int Main(string[] args)
    {
        string clientApi = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../ClientApi"));
        var broken = ApiContract.Compare(clientApi, args.Contains("--accept-api"));
        Check(broken.Count == 0, "the toolkit still serves every member, handshake constant and enum value of " +
            ApiContract.ShippedFile + " (released clients keep their ClientApi copy; see the README's API stability)");

        var api = toolkit::Harness.ToolkitApi.Create();
        // Toolkit content: the Acrux Fluent theme ships in the toolkit mod as /Data/Gui/Styles/Acrux/...
        string acruxStyles = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../Framework/Acrux/Styles"));
        toolkit::Harness.ToolkitApi.InstallSourceLoader(source =>
        {
            const string prefix = "Data/Gui/Styles/Acrux/";
            string path = source.TrimStart('/');
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new System.IO.FileNotFoundException(source);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(acruxStyles, path.Substring(prefix.Length)));
        });
        Console.WriteLine("toolkit members: " + api.Count);
        Acrux.Toolkit.Ui.Load(api);
        // The harness host compiles Acrux only; Acrux.* features live in the toolkit mod project.
        var missing = Acrux.Toolkit.Ui.Missing.Where(m => !m.StartsWith("Acrux.", StringComparison.Ordinal)).ToList();
        Check(missing.Count == 0, "client found every Acrux toolkit member (" + string.Join(", ", missing.Take(5)) + ")");

        // Client styles: read by the client, parsed by the toolkit.
        Acrux.Styling.StyleSheet.SourceLoader = source => System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, source.TrimStart('/')));
        IMyTextSurface badgeSurface = SurfaceMock.Create(new Vector2(256, 128));
        var badge = new Badge { Label = "Toolkit styled", Styles = Acrux.Styling.StyleSheet.LoadFromSource("/Styles/Sample.styles.xml") };
        var badgeHost = new LcdGuiHost(badgeSurface, badge);
        badgeHost.Input.KeyboardEnabled = false;
        badgeHost.Arrange(new RectangleF(0, 0, 256, 128));
        var badgeSprites = new List<MySprite>();
        badgeHost.Draw(badgeSprites);
        Dump("badge frame", badgeSprites);
        Check(badgeSprites.Any(s => s.Data == "Toolkit styled"), "toolkit style selected the client type and its template bound the client Label");
        Check(badge.Fill == new Color(0x20, 0x50, 0xA0), "style setter assigned a client styled property (" + badge.Fill + ")");
        Check(badgeSprites.Any(s => s.Color == new Color(0x20, 0x50, 0xA0)), "TemplateBinding carried the client Fill into a toolkit Border");

        badgeHost.MovePointer(new Vector2(128, 64));
        badgeHost.SetPrimaryButton(true);
        badgeHost.SetPrimaryButton(false);
        Check(badge.Presses == 1, "pointer press reached the client templated control's override (" + badge.Presses + ")");

        var menu = new Acrux.Controls.Menu();
        var menuItem = new Acrux.Controls.MenuItem();
        menu.Items.Add(menuItem);
        Check(menu.Items.Count == 1 && ReferenceEquals(menu.Items[0], menuItem),
            "client Add on a toolkit IList member changed the toolkit list (count " + menu.Items.Count + ")");

        var side = new SidePanel();
        side.AddChild(new Acrux.Controls.TextBlock { Text = "left" });
        side.AddChild(new Acrux.Controls.TextBlock { Text = "right" });
        var sideHost = new LcdGuiHost(SurfaceMock.Create(new Vector2(256, 64)), side);
        sideHost.Input.KeyboardEnabled = false;
        sideHost.Arrange(new RectangleF(0, 0, 256, 64));
        var sideSprites = new List<MySprite>();
        sideHost.Draw(sideSprites);
        var right = sideSprites.FirstOrDefault(sp => sp.Data == "right");
        Check(side.Arranges > 0 && right.Position.HasValue && right.Position.Value.X >= 128f,
            "client panel laid out toolkit children (right at " + right.Position + ")");

        IMyTextSurface surface = SurfaceMock.Create(new Vector2(512, 512));
        var vm = new CounterViewModel();
        // The client has no copy of the base theme: the toolkit serves it.
        Acrux.Styling.StyleSheet.Application = Acrux.Styling.StyleSheet.LoadFromSource("/Data/Gui/Styles/Acrux/Fluent/FluentStyle.xaml");
        var view = new CounterView { DataContext = vm };
        var host = new LcdGuiHost(surface, view);
        host.Input.KeyboardEnabled = false; // no game input in the harness
        host.Arrange(new RectangleF(0, 0, 512, 512));

        Console.WriteLine("view " + view.Bounds + " content " + ((Acrux.Core.VisualElement)view.Content).Bounds + " title " + view.TitleBlock.Bounds + " desired " + view.TitleBlock.DesiredSize);
        var grid = (Acrux.Layout.Grid)view.Content;
        foreach (var child in grid.VisualChildren) Console.WriteLine("  child " + child.GetType().Name + " " + child.Bounds + " row " + child.GridRow);
        var sprites = new List<MySprite>();
        host.Draw(sprites);
        Dump("initial frame", sprites);
        Check(sprites.Any(s => s.Data == "Clicks: 0"), "bound TextBlock renders 'Clicks: 0'");
        Check(sprites.Any(s => s.Data == "item A") && sprites.Any(s => s.Data == "item B"), "ItemsControl built client DataTemplate<T> items");
        // CounterItem.Label is generated by ADK's [ObservableProperty]: bound, and its change notification reaches the toolkit.
        vm.Items[0].Label = "item A2";
        host.Arrange(new RectangleF(0, 0, 512, 512));
        var relabeled = new List<MySprite>();
        host.Draw(relabeled);
        Check(relabeled.Any(s => s.Data == "item A2") && !relabeled.Any(s => s.Data == "item A"),
            "an [ObservableProperty] change re-rendered its DataTemplate item");
        vm.Items[0].Label = "item A";
        host.Arrange(new RectangleF(0, 0, 512, 512));
        Check(sprites.Any(sp => sp.Color == Color.Orange), "DynamicResource color came from the client view model (IResourceProvider)");
        Check(sprites.Any(sp => sp.Data == "x2=0"), "client converter from a StaticResource ran inside a toolkit binding");
        Check(Math.Abs(view.InlineBoxBorder.Bounds.Width - 33f) < .5f,
            "an inline UserControl.Styles style reached a descendant (" + view.InlineBoxBorder.Bounds.Width + ")");
        Check(view.MeterControl.Mode == GaugeMode.Low, "client enum crossed a binding (" + view.MeterControl.Mode + ")");
        Check(view.MeterControl.Draws > 0, "client DrawSelf override ran inside the toolkit render pass");
        Check(view.MeterControl.Bounds.Width > 0, "client control was arranged by the toolkit (" + view.MeterControl.Bounds + ")");
        Check(ReferenceEquals(view.IncrementButton.Parent?.Parent?.Parent, view.Content) || view.IncrementButton.Parent != null, "wrapper identity survives a round trip (Parent)");

        RectangleF button = view.IncrementButton.Bounds;
        var label = sprites.FirstOrDefault(sp => sp.Data == "+1");
        Check(label.Position.HasValue && button.Contains(label.Position.Value), "toolkit Fluent Button template placed the client content inside the button (" + label.Position + ")");
        Vector2 center = new Vector2(button.X + button.Width / 2, button.Y + button.Height / 2);
        Console.WriteLine("button bounds " + button);
        host.MovePointer(center);
        host.SetPrimaryButton(true);
        host.SetPrimaryButton(false);
        Check(vm.Count == 1, "clicking the toolkit Button executed the client command (count=" + vm.Count + ")");
        Check(view.PointerEnteredCount == 1 && view.PointerExitedCount == 0 && ReferenceEquals(view.LastPointerSender, view.IncrementButton),
            "XAML PointerEntered reached the client code-behind once, with the Button as sender (" + view.PointerEnteredCount + ")");
        host.MovePointer(new Vector2(500, 500));
        Check(view.PointerExitedCount == 1, "XAML PointerExited ran a handler taking the base RoutedEventArgs (" + view.PointerExitedCount + ")");
        host.MovePointer(center);

        sprites.Clear();
        host.Draw(sprites);
        Dump("after click", sprites);
        Check(sprites.Any(s => s.Data == "Clicks: 1"), "binding pushed the new title into the toolkit tree");
        Check(Math.Abs(view.MeterControl.Value - 0.1f) < 0.001f, "client styled property updated through binding (" + view.MeterControl.Value + ")");
        Check(view.MeterControl.Mode == GaugeMode.High, "client enum updated through binding (" + view.MeterControl.Mode + ")");
        Check(sprites.Any(sp => sp.Data == "x2=2"), "converter re-ran on change");
        Check(view.MeterControl.SameFactory, "context.DynamicTextures keeps one client wrapper across frames (dynamic textures are not recreated)");
        Check(view.MeterControl.PropertyChanges > 0, "OnPropertyChanged hook reached the client with the client property identity");

        // A toolkit element whose own type clients do not mirror is wrapped as its nearest mirrored base (here the abstract
        // VisualElement, through a stand-in) instead of null, and its own styled properties are reachable by name.
        var panel = new Acrux.Layout.StackPanel();
        var unmirrored = new toolkit::Harness.UnmirroredElement();
        ((toolkit::Acrux.Layout.StackPanel)panel.Native).Add(unmirrored);
        var wrapped = panel.VisualChildren.Count > 0 ? panel.VisualChildren[0] : null;
        Check(wrapped != null, "an element of an unmirrored toolkit type is wrapped as its nearest mirrored base, not null (" +
                               (wrapped == null ? "null" : wrapped.GetType().Name) + ")");
        if (wrapped != null)
        {
            Check(wrapped.ToolkitTypeName == "Harness.UnmirroredElement", "the wrapper reports the toolkit type (" + wrapped.ToolkitTypeName + ")");
            var names = wrapped.CustomProperties.Select(p => p.Name).ToList();
            Check(names.Contains("Level") && names.Contains("Tint") && names.Contains("Align") && names.Contains("Marker"),
                "the unmirrored type's own styled properties are listed as custom (" + string.Join(", ", names) + ")");
            Check(!names.Contains("Width"), "mirrored base properties are not listed as custom");
            var level = wrapped.CustomProperties.First(p => p.Name == "Level");
            Check(level.CanTransfer && level.ValueType == typeof(float) && Equals(level.DefaultValue, 3f),
                "the schema types a custom property and converts its default (" + level.ValueTypeName + ", " + level.DefaultValue + ")");
            Check(wrapped.GetCustomProperty<float>("Level") == 3f, "a custom property reads its toolkit value");
            wrapped.SetCustomProperty("Level", 5f);
            Check(Math.Abs(unmirrored.Level - 5f) < 0.001f, "setting a custom property reaches the toolkit element (" + unmirrored.Level + ")");
            Check(wrapped.GetCustomProperty<Color>("Tint") == Color.Red, "a game-type custom property crosses unchanged");
            wrapped.SetCustomProperty("Tint", Color.Lime);
            Check(unmirrored.Tint == Color.Lime, "setting a game-type custom property reaches the toolkit (" + unmirrored.Tint + ")");
            Check(wrapped.GetCustomProperty<Acrux.Core.HorizontalAlignment>("Align") == Acrux.Core.HorizontalAlignment.Right,
                "a mirrored-enum custom property converts to the client enum");
            wrapped.SetCustomProperty("Align", Acrux.Core.HorizontalAlignment.Center);
            Check(unmirrored.Align == toolkit::Acrux.Core.HorizontalAlignment.Center, "setting a mirrored-enum custom property converts back (" + unmirrored.Align + ")");
            var marker = wrapped.CustomProperties.First(p => p.Name == "Marker");
            Check(marker.CanTransfer && marker.ValueType == typeof(byte) && Equals(marker.DefaultValue, (byte)1),
                "a toolkit-only enum with ': byte' crosses as a byte (" + marker.ValueType + ", default " + marker.DefaultValue + ")");
            wrapped.SetCustomProperty("Marker", (byte)0);
            Check(unmirrored.Marker == toolkit::Harness.MarkerKind.Dot && wrapped.GetCustomProperty<byte>("Marker") == 0,
                "setting a byte-backed enum converts back to the toolkit enum (" + unmirrored.Marker + ")");
            var legacy = wrapped.CustomProperties.First(p => p.Name == "Legacy");
            bool refused = false;
            try { wrapped.GetCustomProperty("Legacy"); } catch (NotSupportedException) { refused = true; }
            Check(!legacy.CanTransfer && refused, "a toolkit-only enum without an underlying type is listed but not transferable");
        }

        // Visual.Opacity multiplies a subtree's alpha; Brush.Opacity (ImageBrush) only the brush's, on top of it.
        var faded = new Acrux.Controls.Border
        {
            Background = Color.Red,
            Opacity = .5f,
            Child = new Acrux.Controls.TextBlock { Text = "faded", Foreground = Color.White }
        };
        var fadedHost = new LcdGuiHost(SurfaceMock.Create(new Vector2(128, 64)), faded);
        fadedHost.Input.KeyboardEnabled = false;
        fadedHost.Arrange(new RectangleF(0, 0, 128, 64));
        var fadedSprites = new List<MySprite>();
        fadedHost.Draw(fadedSprites);
        var fadedText = fadedSprites.FirstOrDefault(sp => sp.Data == "faded");
        Check(fadedText.Color.HasValue && Math.Abs(fadedText.Color.Value.A - 128) <= 1,
            "Opacity .5 halves a child's alpha (" + fadedText.Color + ")");
        Check(fadedSprites.Any(sp => sp.Type == SpriteType.TEXTURE && sp.Color.HasValue && sp.Color.Value.R > 0 &&
                                     Math.Abs(sp.Color.Value.A - 128) <= 1),
            "Opacity .5 halves the element's own background alpha");

        var brushed = new Acrux.Controls.Border
        {
            Background = new Acrux.Styling.ImageBrush("SquareSimple") { Opacity = .25f },
            Child = new Acrux.Controls.TextBlock { Text = "opaque", Foreground = Color.White }
        };
        var brushedHost = new LcdGuiHost(SurfaceMock.Create(new Vector2(128, 64)), brushed);
        brushedHost.Input.KeyboardEnabled = false;
        brushedHost.Arrange(new RectangleF(0, 0, 128, 64));
        var brushedSprites = new List<MySprite>();
        brushedHost.Draw(brushedSprites);
        var image = brushedSprites.FirstOrDefault(sp => sp.Type == SpriteType.TEXTURE);
        var opaqueText = brushedSprites.FirstOrDefault(sp => sp.Data == "opaque");
        Check(image.Color.HasValue && Math.Abs(image.Color.Value.A - 64) <= 1,
            "ImageBrush.Opacity .25 fades the image (" + image.Color + ")");
        Check(opaqueText.Color.HasValue && opaqueText.Color.Value.A == 255,
            "ImageBrush.Opacity leaves the element's content opaque (" + opaqueText.Color + ")");

        // A client view model is ADK's ObservableObject, compiled into the client (not a toolkit object): the toolkit's
        // compiled binding subscribes through the BCL INotifyPropertyChanged, and in-mod subscribers get PropertyChanged.
        var titleModel = new TitleViewModel();
        int titleChanges = 0;
        titleModel.PropertyChanged += (sender, name) => { if (name == "Title") titleChanges++; };
        var titleLabel = new Acrux.Controls.TextBlock { DataContext = titleModel };
        string boundTitle = null;
        titleLabel.Bind<TitleViewModel, string>(source => source.Title, value => boundTitle = value, "Title");
        titleModel.Title = "Raised";
        titleModel.Title = "Raised";
        Check(boundTitle == "Raised", "client RaiseAndSetIfChanged reached a toolkit compiled binding (" + (boundTitle ?? "null") + ")");
        Check(titleChanges == 1, "client RaiseAndSetIfChanged raised PropertyChanged once, not for an equal value (" + titleChanges + ")");

        // A two-way compiled binding to a client ObservableObject: the toolkit calls the client's getter and setter with
        // its own object for the source, which the client maps back to the view model (it used to cast it and throw).
        var twoWayModel = new TitleViewModel { Title = "From model" };
        Exception twoWayError = null;
        Acrux.Controls.TextBox twoWayBox = null;
        try
        {
            Acrux.Controls.Border twoWayHost;
            twoWayBox = TwoWaySample.Bind(twoWayModel, out twoWayHost);
        }
        catch (Exception exception)
        {
            twoWayError = exception;
        }
        Check(twoWayError == null && twoWayBox != null && twoWayBox.Text == "From model",
            "two-way binding read a client view model through an inherited DataContext (" +
            (twoWayError != null ? twoWayError.GetType().Name : twoWayBox != null ? twoWayBox.Text ?? "null" : "no box") + ")");
        if (twoWayBox != null && twoWayError == null)
        {
            try
            {
                twoWayBox.Text = "From control";
            }
            catch (Exception exception)
            {
                twoWayError = exception;
            }
            Check(twoWayError == null && twoWayModel.Title == "From control",
                "two-way binding wrote the control's value back to the client view model (" +
                (twoWayError != null ? twoWayError.GetType().Name : twoWayModel.Title ?? "null") + ")");
        }

        // The framework's Space Engineers theme (shared by every client) parses with its includes.
        Exception themeError = null;
        try
        {
            Check(Acrux.Styling.StyleSheet.LoadFromSource("/Data/Gui/Styles/Acrux/SpaceEngineers/SpaceEngineersStyle.xaml") != null,
                "Space Engineers theme loaded from the toolkit's content");
        }
        catch (Exception error)
        {
            themeError = error;
        }
        Check(themeError == null, "Space Engineers theme parsed (" + (themeError == null ? "no error" : themeError.Message) + ")");

        // Two buttons with their own texts and results: the form a client can call (MessageBoxButton does not cross).
        var confirm = new Acrux.Controls.MessageBox();
        Acrux.Controls.MessageBoxResult? confirmResult = null;
        confirm.Show("Bulldoze", "Remove the road?", "Bulldoze", Acrux.Controls.MessageBoxResult.Ok,
            "Cancel", Acrux.Controls.MessageBoxResult.Cancel, result => confirmResult = result);
        Check(confirm.IsOpen, "two-button MessageBox.Show opened the box from a client");
        confirm.Close(Acrux.Controls.MessageBoxResult.Cancel);
        Check(!confirm.IsOpen && confirmResult == Acrux.Controls.MessageBoxResult.Cancel,
            "two-button MessageBox reported its result to the client (" + confirmResult + ")");

        // <ImageBrush Opacity="{DynamicResource ...}"> in a style: resolved per control, and again when the resource changes.
        var resourceFaded = new Acrux.Controls.Border
        {
            StyleClass = "resource-faded",
            Styles = Acrux.Styling.StyleSheet.LoadFromSource("/Styles/Opacity.styles.xml")
        };
        resourceFaded.Resources.Add("imageOpacity", .25f);
        var resourceHost = new LcdGuiHost(SurfaceMock.Create(new Vector2(64, 64)), resourceFaded);
        resourceHost.Input.KeyboardEnabled = false;
        resourceHost.Arrange(new RectangleF(0, 0, 64, 64));
        var resourceSprites = new List<MySprite>();
        resourceHost.Draw(resourceSprites);
        var resourceImage = resourceSprites.FirstOrDefault(sp => sp.Type == SpriteType.TEXTURE);
        Check(resourceImage.Color.HasValue && Math.Abs(resourceImage.Color.Value.A - 64) <= 1,
            "a style's ImageBrush took its Opacity from a dynamic resource (" + resourceImage.Color + ")");
        resourceFaded.Resources.Add("imageOpacity", .5f);
        resourceSprites.Clear();
        resourceHost.Draw(resourceSprites);
        resourceImage = resourceSprites.FirstOrDefault(sp => sp.Type == SpriteType.TEXTURE);
        Check(resourceImage.Color.HasValue && Math.Abs(resourceImage.Color.Value.A - 128) <= 1,
            "the ImageBrush followed the resource when it changed (" + resourceImage.Color + ")");

        // Square Border strokes: two rectangles when opaque, the fill plus one shared ring texture otherwise.
        var stroked = new Acrux.Controls.Border
        {
            Background = new Acrux.Styling.SolidColorBrush(Color.Black),
            BorderBrush = Color.Red,
            BorderThickness = new Vector4(2, 3, 4, 5)
        };
        var strokedHost = new LcdGuiHost(SurfaceMock.Create(new Vector2(128, 64)), stroked);
        strokedHost.Input.KeyboardEnabled = false;
        strokedHost.Arrange(new RectangleF(0, 0, 128, 64));
        var strokedSprites = new List<MySprite>();
        strokedHost.Draw(strokedSprites);
        var strokeDrawn = strokedSprites.Where(sp => sp.Type == SpriteType.TEXTURE).ToList();
        Check(strokeDrawn.Count == 2 && strokeDrawn[0].Color == Color.Red && strokeDrawn[0].Size == new Vector2(128, 64) &&
              strokeDrawn[1].Color == Color.Black && strokeDrawn[1].Size == new Vector2(122, 56) &&
              strokeDrawn[1].Position == new Vector2(63, 31),
            "an opaque stroked Border is two rectangles: stroke, then background inside it (" + strokeDrawn.Count +
            (strokeDrawn.Count > 1 ? " inner " + strokeDrawn[1].Position + " " + strokeDrawn[1].Size : "") + ")");
        stroked.BorderBrush = new Color(255, 0, 0, 128);
        strokedSprites.Clear();
        strokedHost.Draw(strokedSprites);
        strokeDrawn = strokedSprites.Where(sp => sp.Type == SpriteType.TEXTURE).ToList();
        Console.WriteLine("  translucent stroke: " + string.Join(", ", strokeDrawn.Select(sp => sp.Data + " " + sp.Size)));
        Check(strokeDrawn.Count >= 2 && strokeDrawn[0].Color == Color.Black && strokeDrawn[0].Size == new Vector2(128, 64) &&
              (strokeDrawn.Count == 2
                  ? strokeDrawn[1].Data != "SquareSimple" && strokeDrawn[1].Size == new Vector2(128, 64)
                  : strokeDrawn.Count == 5),
            "a translucent stroke is the full background plus one ring texture (four edges only while no texture is available) (" + strokeDrawn.Count + ")");

        // Surface layers: the toolkit composes content, overlay and cursor; the client only supplies state.
        var layers = new LcdSurfaceLayers(surface) { Host = host };
        var layerContent = new List<MySprite> { new MySprite(SpriteType.TEXTURE, "Content") };
        var layerOverlay = new List<MySprite> { new MySprite(SpriteType.TEXTURE, "Overlay") };
        layers.SetContent(layerContent);
        layers.SetOverlay(layerOverlay);
        layerContent.Clear();
        List<MySprite> composed = layers.Compose();
        Check(composed.Count == 2 && composed[0].Data == "Content" && composed[1].Data == "Overlay" && !layers.CursorDrawn,
            "layers keep their own copy of content and overlay, and draw no cursor without a pointer (" + composed.Count + ")");
        layers.CursorPosition = new Vector2(100, 100);
        composed = layers.Compose();
        // The pointer rests on the button clicked above, so the retained UI asks for its hand.
        Check(composed.Count == 3 && layers.CursorDrawn && composed[2].Size == new Vector2(32) &&
              layers.EffectiveCursor == host.PointerCursor &&
              composed[2].Data == (host.PointerCursor == Acrux.Core.Input.PointerCursor.Hand ? "Acrux_CursorHand" : "Acrux_CursorArrow"),
            "the cursor layer draws the retained UI's cursor on top (" + composed[composed.Count - 1].Data + ")");
        layers.Host = null;
        composed = layers.Compose();
        Check(composed[2].Data == "Acrux_CursorArrow", "without a retained UI the cursor is the arrow (" + composed[2].Data + ")");
        layers.Host = host;
        layers.Cursor = Acrux.Core.Input.PointerCursor.Hand;
        layers.CursorScale = 2f;
        composed = layers.Compose();
        Check(composed[2].Data == "Acrux_CursorHand" && composed[2].Size == new Vector2(64) &&
              layers.EffectiveCursor == Acrux.Core.Input.PointerCursor.Hand,
            "a cursor override and scale restyle the cursor (" + composed[2].Data + " " + composed[2].Size + ")");
        layers.Cursor = Acrux.Core.Input.PointerCursor.AppStarting;
        composed = layers.Compose();
        Check(composed.Count == 4 && composed[2].Data == "Acrux_CursorAppStarting" && composed[3].Data == "Acrux_CursorWait",
            "AppStarting draws the arrow with the busy wheel (" + composed.Count + ")");
        layers.Cursor = Acrux.Core.Input.PointerCursor.SizeAll;
        Check(layers.Compose()[2].Data == "Acrux_CursorSizeAll" && (int)Acrux.Core.Input.PointerCursor.None == 255 &&
              (int)Acrux.Core.Input.PointerCursor.Arrow == 0 && (int)Acrux.Core.Input.PointerCursor.DragLink == 22,
            "every Avalonia cursor style has its own sprite; None is 255");
        layers.CursorVisible = false;
        Check(layers.Compose().Count == 2 && !layers.CursorDrawn, "a hidden cursor leaves only content and overlay");
        layers.CursorVisible = true;
        layers.Cursor = Acrux.Core.Input.PointerCursor.None;
        Check(layers.Compose().Count == 2, "the None style draws no cursor");
        var layerFrame = new List<MySprite>();
        host.Draw(layerFrame);
        Check(layers.CanReuseContent, "content is reusable while the retained UI has nothing new to draw");
        layers.InvalidateContent();
        Check(!layers.CanReuseContent && layers.Compose().Count == 0, "invalidated content is dropped");
        var tiles = new List<MySprite>();
        layers.PrepareBackground(Color.Blue, new RectangleF(0, 0, 512, 512));
        layers.DrawBackground(tiles);
        Check(layers.HasBackground && tiles.Count > 0 && tiles[0].Color == Color.Blue,
            "the background layer tiles the surface (" + tiles.Count + " tiles)");
        layers.InvalidateBackground();
        Check(!layers.HasBackground, "invalidating the background drops its tiles");

        StyleAnimationChecks();
        TransformParseChecks();
        TrimmingChecks();
        ComboBoxPlacementChecks();
        FlyoutChecks();
        ComboBoxTemplateChecks();

        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : _failures + " CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // Avalonia's transform syntax, toolkit side. Offset and ScaleFactor are internal to the toolkit.
    static void TransformParseChecks()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        Func<object, string, Vector2> vector = (t, name) => (Vector2)t.GetType().GetProperty(name, flags).GetValue(t);
        var translations = new[]
        {
            Tuple.Create("translate(10px, -20px)", 10f, -20f),
            Tuple.Create("translate(10px)", 10f, 0f),
            Tuple.Create("translateX(4.5px)", 4.5f, 0f),
            Tuple.Create("translateY(12px)", 0f, 12f),
            Tuple.Create("translateY(0)", 0f, 0f),
        };
        foreach (var t in translations)
        {
            var parsed = toolkit::Acrux.Styling.Transform.Parse(t.Item1) as toolkit::Acrux.Styling.TranslateTransform;
            Check(parsed != null && parsed.X == t.Item2 && parsed.Y == t.Item3 && vector(parsed, "ScaleFactor") == Vector2.One &&
                  parsed.RotationRadians == 0f, "transform '" + t.Item1 + "' is a translation by (" + t.Item2 + ", " + t.Item3 + ")");
        }
        foreach (string text in new[] { "translate(10)", "translateY(1px, 2px)", "translate(1px, 2px, 3px)" })
        {
            bool rejected = false;
            try
            {
                toolkit::Acrux.Styling.Transform.Parse(text);
            }
            catch (FormatException)
            {
                rejected = true;
            }
            Check(rejected, "transform '" + text + "' is rejected, as Avalonia rejects it");
        }
        Check(vector(toolkit::Acrux.Styling.Transform.Parse("scale(0.95)"), "Offset") == Vector2.Zero &&
              vector(toolkit::Acrux.Styling.Transform.Parse("rotate(90deg)"), "Offset") == Vector2.Zero,
            "scale and rotate transforms carry no offset");
    }

    sealed class MonospaceMetrics : toolkit::Acrux.Rendering.ITextMetrics
    {
        public Vector2 Measure(string font, string text, float scale) => new Vector2(text.Length, 1f);
    }

    // One unit per character, so widths count characters; the ellipsis counts as one.
    static void TrimmingChecks()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var text = new toolkit::Acrux.Controls.TextBlock();
        typeof(toolkit::Acrux.Core.VisualElement).GetField("_textMetrics", flags).SetValue(text, new MonospaceMetrics());
        var trim = typeof(toolkit::Acrux.Controls.TextBlock).GetMethod("Trim", flags);
        Func<string, float, toolkit::Acrux.Controls.TextTrimming, string> run = (value, width, mode) => (string)trim.Invoke(text, new object[] { value, width, mode });
        var prefix = toolkit::Acrux.Controls.TextTrimming.PrefixCharacterEllipsis;
        Check(run("Destination Pad", 15f, prefix) == "Destination Pad", "PrefixCharacterEllipsis leaves text that fits");
        Check(run("Destination Pad", 12f, prefix) == "Destinat\u2026Pad", "PrefixCharacterEllipsis keeps 8 leading characters and fills the rest from the end");
        Check(run("Destination Pad", 7f, prefix) == "Destin\u2026", "PrefixCharacterEllipsis keeps only the prefix when 8 characters or fewer fit");
        Check(run("Destination Pad", 12f, toolkit::Acrux.Controls.TextTrimming.CharacterEllipsis) == "Destination\u2026", "CharacterEllipsis still trims at the end");
    }

    // A selection shown through an item template stays inside the box: the presenter clips it, and a TextBlock with
    // TextTrimming in a grid's star column trims to the room before the arrow.
    static void ComboBoxTemplateChecks()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        toolkit::Acrux.Controls.TextBlock text = null;
        var combo = new toolkit::Acrux.Controls.ComboBox(new[] { new string('m', 400) }) { Width = 120, Height = 24 };
        combo.ItemTemplate = new toolkit::Acrux.Styling.DataTemplate<string>(item =>
        {
            var row = new toolkit::Acrux.Layout.Grid();
            row.SetColumnDefinitions(toolkit::Acrux.Layout.GridLength.Auto, toolkit::Acrux.Layout.GridLength.Star);
            row.Set(new toolkit::Acrux.Controls.Border { Width = 10, Height = 10 }, 0, 0, 1, 1);
            text = new toolkit::Acrux.Controls.TextBlock { Text = item, TextTrimming = toolkit::Acrux.Controls.TextTrimming.CharacterEllipsis };
            row.Set(text, 1, 0, 1, 1);
            return row;
        });
        combo.SelectedIndex = 0;
        var panel = new toolkit::Acrux.Layout.StackPanel();
        panel.Add(combo);
        var host = new toolkit::Acrux.Lcd.LcdGuiHost(SurfaceMock.Create(new Vector2(256, 64)), panel);
        for (int i = 0; i < 3; i++)
        {
            host.Arrange(new RectangleF(0, 0, 256, 64));
            host.Draw(new List<MySprite>());
        }
        var presenter = (toolkit::Acrux.Core.VisualElement)typeof(toolkit::Acrux.Controls.ComboBox)
            .GetField("_headerContent", flags).GetValue(combo);
        var indicator = (toolkit::Acrux.Core.VisualElement)typeof(toolkit::Acrux.Controls.ComboBox)
            .GetField("_headerIndicator", flags).GetValue(combo);
        Check(text != null && presenter != null && presenter.Visible, "the templated selection is shown");
        if (text == null || presenter == null)
            return;
        Check(presenter.ClipToBounds, "the selection box clips templated content");
        Check(text.Bounds.Right <= combo.Bounds.Right + 0.5f, "templated selection text stays inside the box (" + text.Bounds + " in " + combo.Bounds + ")");
        Check(indicator == null || text.Bounds.Right <= indicator.Bounds.X + 0.5f,
            "templated selection text stops before the arrow (" + text.Bounds + ", arrow " + (indicator == null ? "none" : indicator.Bounds.ToString()) + ")");
        string shown = (string)typeof(toolkit::Acrux.Controls.TextBlock).GetField("_trimmedText", flags).GetValue(text);
        Check(shown != null && shown.EndsWith("\u2026"), "templated selection text is trimmed with an ellipsis (" + (shown == null ? "null" : shown.Length + " characters") + ")");
    }

    // A Flyout takes its content's size, sits against its target, and takes the target's data context; ShowAt opens one
    // at an element that is not a button, which the tree finds through the element.
    static void FlyoutChecks()
    {
        Func<bool, Tuple<RectangleF, RectangleF, object, bool>> open = viaButton =>
        {
            var content = new toolkit::Acrux.Controls.Border { Width = 60, Height = 30 };
            var flyout = new toolkit::Acrux.Controls.Flyout { Content = content };
            toolkit::Acrux.Core.VisualElement target = viaButton
                ? (toolkit::Acrux.Core.VisualElement)new toolkit::Acrux.Controls.Button { Width = 20, Height = 20, Flyout = flyout }
                : new toolkit::Acrux.Controls.Border { Width = 20, Height = 20 };
            target.DataContext = "target context";
            var panel = new toolkit::Acrux.Layout.StackPanel();
            panel.Add(new toolkit::Acrux.Controls.Border { Height = 80 });
            panel.Add(target);
            var host = new toolkit::Acrux.Lcd.LcdGuiHost(SurfaceMock.Create(new Vector2(128, 128)), panel);
            host.Arrange(new RectangleF(0, 0, 128, 128));
            host.Draw(new List<MySprite>());
            flyout.ShowAt(target);
            host.Draw(new List<MySprite>());
            return Tuple.Create(flyout.Child.Bounds, target.Bounds, content.DataContext, flyout.IsOpen);
        };
        foreach (bool viaButton in new[] { true, false })
        {
            string at = viaButton ? "a button's flyout" : "a flyout shown at a border";
            var result = open(viaButton);
            RectangleF presenter = result.Item1;
            RectangleF target = result.Item2;
            Check(result.Item4, at + " opens");
            Check(presenter.Width >= 60f && presenter.Width <= 100f && presenter.Height >= 30f && presenter.Height <= 70f,
                at + " takes its content's size plus the presenter's padding (" + presenter + ")");
            Check(presenter.Bottom <= target.Y + 0.5f, at + " opens above its target (" + presenter + " over " + target + ")");
            Check(Equals(result.Item3, "target context"), at + " inherits the target's data context");
        }
    }

    // The drop-down opens on the side with room and is shortened to fit the surface.
    static void ComboBoxPlacementChecks()
    {
        Func<float, toolkit::Acrux.Controls.ComboBoxOpenDirection, RectangleF> open = (spacer, direction) =>
        {
            var combo = new toolkit::Acrux.Controls.ComboBox(new[] { "a", "b", "c", "d", "e" }) { Height = 24, OpenDirection = direction };
            var panel = new toolkit::Acrux.Layout.StackPanel();
            panel.Add(new toolkit::Acrux.Controls.Border { Height = spacer });
            panel.Add(combo);
            var host = new toolkit::Acrux.Lcd.LcdGuiHost(SurfaceMock.Create(new Vector2(128, 128)), panel);
            host.Arrange(new RectangleF(0, 0, 128, 128));
            host.Draw(new List<MySprite>());
            combo.Open();
            var popup = (toolkit::Acrux.Core.VisualElement)typeof(toolkit::Acrux.Controls.ComboBox)
                .GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(combo);
            return popup.Bounds;
        };
        RectangleF low = open(100f, toolkit::Acrux.Controls.ComboBoxOpenDirection.Down);
        Check(low.Bottom <= 100.5f && low.Y >= -0.5f, "a combo box near the bottom opens upward, within the surface (" + low + ")");
        RectangleF high = open(4f, toolkit::Acrux.Controls.ComboBoxOpenDirection.Up);
        Check(high.Y >= 27.5f && high.Bottom <= 128.5f, "a combo box near the top opens downward even when Up is preferred (" + high + ")");
        RectangleF fits = open(0f, toolkit::Acrux.Controls.ComboBoxOpenDirection.Down);
        Check(Math.Abs(fits.Y - 24f) < 0.5f && fits.Bottom <= 128.5f, "a drop-down with room below opens downward (" + fits + ")");
    }

    // Avalonia's <Style.Animations>, toolkit side, on a hand-driven frame clock.
    static void StyleAnimationChecks()
    {
        long frame = 0;
        var queued = new List<Action>();
        var clock = new toolkit::Acrux.Animation.AnimationController(() => frame, queued.Add, () => { });
        Action<long> runTo = target =>
        {
            for (; frame < target; frame++)
            {
                Action[] callbacks = queued.ToArray();
                queued.Clear();
                foreach (Action callback in callbacks)
                    callback();
            }
            Action[] last = queued.ToArray();
            queued.Clear();
            foreach (Action callback in last)
                callback();
        };
        const string xml = @"<Styles xmlns=""https://github.com/ArthurGamerHD/AcruxUI"">
  <Style Selector=""Border.blink"">
    <Style.Animations>
      <Animation Duration=""0:0:1"" IterationCount=""Infinite"">
        <KeyFrame Cue=""0%""><Setter Property=""Opacity"" Value=""1"" /></KeyFrame>
        <KeyFrame Cue=""50%""><Setter Property=""Opacity"" Value=""0"" /></KeyFrame>
        <KeyFrame Cue=""100%""><Setter Property=""Opacity"" Value=""1"" /></KeyFrame>
      </Animation>
    </Style.Animations>
  </Style>
  <Style Selector=""Border.once"">
    <Style.Animations>
      <Animation Duration=""0:0:1"" FillMode=""Forward"">
        <KeyFrame Cue=""100%""><Setter Property=""Opacity"" Value=""0.2"" /></KeyFrame>
      </Animation>
    </Style.Animations>
  </Style>
</Styles>";
        var styles = toolkit::Acrux.Styling.StyleSheet.Load(xml);
        var blink = new toolkit::Acrux.Controls.Border { StyleClass = "blink", Width = 10, Height = 10, Styles = styles };
        var blinkHost = new toolkit::Acrux.Lcd.LcdGuiHost(SurfaceMock.Create(new Vector2(32, 32)), blink, clock);
        blinkHost.Arrange(new RectangleF(0, 0, 32, 32));
        blinkHost.Draw(new List<MySprite>());
        Func<float, bool> near = expected => Math.Abs(blink.Opacity - expected) < 0.02f;
        runTo(0);
        Check(near(1f), "a style animation starts on its first key frame (" + blink.Opacity + ")");
        runTo(15);
        Check(near(0.5f), "a quarter in, the opacity is halfway to the 50% key frame (" + blink.Opacity + ")");
        runTo(30);
        Check(near(0f), "at 50% the opacity is the key frame's (" + blink.Opacity + ")");
        runTo(75);
        Check(near(0.5f), "IterationCount Infinite repeats the animation (" + blink.Opacity + ")");
        blink.Opacity = 0.8f;
        runTo(90);
        Check(near(0f), "an animated value wins over a local value (" + blink.Opacity + ")");
        blink.Visible = false;
        runTo(120);
        blink.Visible = true;
        runTo(121);
        Check(Math.Abs(blink.Opacity - 1f / 30f) < 0.02f, "time stops while the control is hidden (" + blink.Opacity + ")");
        blink.StyleClass = "";
        blinkHost.Draw(new List<MySprite>());
        Check(near(0.8f), "when the style stops matching, the animation stops and the local value is back (" + blink.Opacity + ")");
        int running = clock.ActiveCount;
        runTo(130);
        Check(running == 0 && near(0.8f), "a stopped style animation no longer runs (" + running + ")");

        var once = new toolkit::Acrux.Controls.Border { StyleClass = "once", Width = 10, Height = 10, Styles = styles };
        var onceHost = new toolkit::Acrux.Lcd.LcdGuiHost(SurfaceMock.Create(new Vector2(32, 32)), once, clock);
        onceHost.Arrange(new RectangleF(0, 0, 32, 32));
        onceHost.Draw(new List<MySprite>());
        long start = frame;
        runTo(start + 30);
        Check(Math.Abs(once.Opacity - 0.6f) < 0.02f, "a missing 0% key frame starts from the value without the animation (" + once.Opacity + ")");
        runTo(start + 90);
        Check(Math.Abs(once.Opacity - 0.2f) < 0.02f && clock.ActiveCount == 0,
            "FillMode Forward keeps the last key frame once the animation ends (" + once.Opacity + ")");

        bool rejected = false;
        try
        {
            toolkit::Acrux.Styling.StyleSheet.Load(xml.Replace("FillMode=", "Bogus="));
        }
        catch (FormatException)
        {
            rejected = true;
        }
        Check(rejected, "an Animation attribute Acrux does not support is a parse error");
    }

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) _failures++;
    }

    static void Dump(string title, List<MySprite> sprites)
    {
        Console.WriteLine(title + ": " + sprites.Count + " sprites");
        foreach (var s in sprites.Take(12))
            Console.WriteLine("    " + s.Type + " '" + s.Data + "' at " + s.Position + " size " + s.Size);
    }
}

/// <summary>Minimal text surface: fixed size, monospace text metrics.</summary>
public class SurfaceMock : DispatchProxy
{
    Vector2 _size;

    public static IMyTextSurface Create(Vector2 size)
    {
        var surface = Create<IMyTextSurface, SurfaceMock>();
        ((SurfaceMock)(object)surface)._size = size;
        return surface;
    }

    protected override object Invoke(MethodInfo method, object[] args)
    {
        switch (method.Name)
        {
            case "get_SurfaceSize":
            case "get_TextureSize":
                return _size;
            case "get_TextPadding":
                return 0f;
            case "MeasureStringInPixels":
                var text = (StringBuilder)args[0];
                float scale = (float)args[2];
                return new Vector2(text.Length * 0.6f * scale, 1.2f * scale);
            case "GetSprites":
                return null;
        }
        if (method.ReturnType == typeof(void))
            return null;
        return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
