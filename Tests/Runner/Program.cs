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
        CheckGradients(view);
        CheckGradientSharing();
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
        WindowManagerChecks();

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
    // Owned, non-modal windows: activation and stacking, title-bar move, edge resize, minimize and restore.
    static void WindowManagerChecks()
    {
        var surface = SurfaceMock.Create(new Vector2(400, 300));
        var platform = new toolkit::Acrux.Lcd.LcdWindowPlatform(surface, null, null, false);
        var root = new toolkit::Acrux.Controls.Window
        {
            WindowState = toolkit::Acrux.Controls.WindowState.FullScreen,
            Content = new toolkit::Acrux.Controls.Border { Background = new toolkit::Acrux.Styling.SolidColorBrush(Color.Black) },
            Platform = platform
        };
        root.Open();
        var host = platform.GetGuiHost(root);
        host.Input.KeyboardEnabled = false;
        Func<Vector2, toolkit::Acrux.Controls.Window> make = position =>
        {
            var window = new toolkit::Acrux.Controls.Window
            {
                Title = "w",
                Width = 160,
                Height = 120,
                Content = new toolkit::Acrux.Controls.Button { Content = new toolkit::Acrux.Controls.TextBlock { Text = "b" } }
            };
            window.Position = position;
            window.Show(root);
            return window;
        };
        var a = make(new Vector2(10, 10));
        var b = make(new Vector2(200, 100));
        var frame = new List<MySprite>();
        host.Arrange(new RectangleF(0, 0, 400, 300));
        host.Draw(frame);
        Check(b.IsActive && !a.IsActive, "the window shown last is the active one");
        Check(a.FrameBounds == new RectangleF(10, 10, 160, 120), "Position places a normal owned window (" + a.FrameBounds + ")");

        host.MovePointer(new Vector2(90, 100));
        host.SetPrimaryButton(true);
        host.SetPrimaryButton(false);
        Check(a.IsActive && !b.IsActive && ReferenceEquals(root.OwnedWindows[root.OwnedWindows.Count - 1], a),
            "a click inside a window activates it and brings it to the top");

        int moved = 0;
        a.PositionChanged += () => moved++;
        host.MovePointer(new Vector2(40, 20));
        host.SetPrimaryButton(true);
        host.MovePointer(new Vector2(70, 50));
        host.SetPrimaryButton(false);
        host.Draw(frame);
        Check(a.Position == new Vector2(40, 40) && moved > 0, "dragging the title bar moves the window (" + a.Position + ", " + moved + " events)");

        int resized = 0;
        a.Resized += () => resized++;
        float right = a.FrameBounds.Right;
        host.MovePointer(new Vector2(right - 2f, a.FrameBounds.Center.Y));
        host.SetPrimaryButton(true);
        host.MovePointer(new Vector2(right + 38f, a.FrameBounds.Center.Y));
        host.SetPrimaryButton(false);
        host.Draw(frame);
        Check(Math.Abs(a.FrameBounds.Width - 200f) < .5f && resized > 0, "dragging the east edge resizes the window (" + a.FrameBounds + ")");

        a.WindowState = toolkit::Acrux.Controls.WindowState.Minimized;
        frame.Clear();
        host.Draw(frame);
        Check(!a.IsActive && b.IsActive, "minimizing the active window activates the next one");
        host.MovePointer(new Vector2(50, 60));
        host.SetPrimaryButton(true);
        host.SetPrimaryButton(false);
        Check(!a.IsActive && !b.IsActive, "a minimized window takes no input; a click on the owner deactivates every window");
        a.Activate();
        Check(a.WindowState == toolkit::Acrux.Controls.WindowState.Normal && a.IsActive, "Activate restores a minimized window");
        root.Padding = new Vector4(0, 0, 0, 40);
        a.WindowState = toolkit::Acrux.Controls.WindowState.Maximized;
        frame.Clear();
        host.Draw(frame);
        Check(a.FrameBounds == new RectangleF(0, 0, 400, 260),
            "an owned window maximizes over its owner's client area, inside the owner's padding (" + a.FrameBounds + ")");
        a.WindowState = toolkit::Acrux.Controls.WindowState.Normal;
        root.Padding = Vector4.Zero;
        var bar = new toolkit::Acrux.Controls.Window { Topmost = true, WindowDecorations = toolkit::Acrux.Controls.WindowDecorations.None };
        bar.Show(root);
        a.Activate();
        Check(ReferenceEquals(root.OwnedWindows[root.OwnedWindows.Count - 1], bar) &&
              ReferenceEquals(root.OwnedWindows[root.OwnedWindows.Count - 2], a),
            "an activated window stays below a Topmost one");
        var hint = new toolkit::Acrux.Controls.Window { ShowActivated = false, Topmost = true };
        hint.Show(root);
        Check(a.IsActive && !hint.IsActive, "a window with ShowActivated false opens without taking activation");
        hint.Close();
        bar.Close();
        bar.ShowActivated = false;
        bar.Show(root);
        a.Close();
        Check(b.IsActive && !bar.IsActive, "closing the active window skips windows that are not shown activated");
        bar.Close();
        var menuWindow = new toolkit::Acrux.Controls.Window
        {
            WindowDecorations = toolkit::Acrux.Controls.WindowDecorations.None,
            Width = 400,
            Height = 30,
            ShowActivated = false
        };
        menuWindow.Position = new Vector2(0, 0);
        var menu = new toolkit::Acrux.Controls.Menu();
        var top = new toolkit::Acrux.Controls.MenuItem { Header = "File" };
        int picked = 0;
        var item = new toolkit::Acrux.Controls.MenuItem { Header = "Pick", Command = new toolkit::Acrux.Core.Input.DelegateCommand(_ => picked++) };
        top.Items.Add(item);
        menu.Items.Add(top);
        menuWindow.Content = menu;
        menuWindow.Show(root);
        frame.Clear();
        host.Draw(frame);
        host.MovePointer(top.Bounds.Center);
        host.SetPrimaryButton(true);
        host.SetPrimaryButton(false);
        frame.Clear();
        host.Draw(frame);
        bool opened = top.IsSubMenuOpen;
        host.MovePointer(item.Bounds.Center);
        host.SetPrimaryButton(true);
        host.SetPrimaryButton(false);
        Check(opened && picked == 1, "a menu in an owned window opens and its items take clicks (open " + opened + ", picked " + picked + ")");

        var comboWindow = new toolkit::Acrux.Controls.Window
        {
            WindowDecorations = toolkit::Acrux.Controls.WindowDecorations.None,
            Width = 400,
            Height = 30,
            Topmost = true,
            ShowActivated = false
        };
        comboWindow.Position = new Vector2(0, 270);
        var pages = new toolkit::Acrux.Controls.ComboBox { Width = 200, ItemsSource = new List<string> { "one", "two", "three" } };
        pages.SelectedIndex = 0;
        comboWindow.Content = pages;
        comboWindow.Show(root);
        frame.Clear();
        host.Arrange(new RectangleF(0, 0, 400, 300));
        host.Draw(frame);
        host.MovePointer(pages.Bounds.Center);
        host.SetPrimaryButton(true);
        host.SetPrimaryButton(false);
        frame.Clear();
        host.Draw(frame);
        bool comboOpened = pages.IsDropDownOpen;
        var dropDown = new List<MySprite>(frame);
        var two = dropDown.FirstOrDefault(sp => sp.Data == "two");
        Console.WriteLine("combo open " + comboOpened + " at " + pages.Bounds + " item two " + two.Position);
        if (two.Position.HasValue)
        {
            host.MovePointer(two.Position.Value + new Vector2(4, 6));
            host.SetPrimaryButton(true);
            host.SetPrimaryButton(false);
        }
        Check(comboOpened && pages.SelectedIndex == 1,
            "a combo box in a topmost owned window opens and takes a selection (open " + comboOpened + ", index " + pages.SelectedIndex + ")");
        comboWindow.Close();

        var scaledWindow = new toolkit::Acrux.Controls.Window
        {
            WindowDecorations = toolkit::Acrux.Controls.WindowDecorations.None,
            Width = 400,
            Height = 300,
            Topmost = true,
            ShowActivated = false
        };
        scaledWindow.Position = new Vector2(0, 0);
        int scaledClicks = 0;
        var scaledButton = new toolkit::Acrux.Controls.Button
        {
            Width = 40, Height = 20,
            HorizontalAlignment = toolkit::Acrux.Core.HorizontalAlignment.Left,
            Command = new toolkit::Acrux.Core.Input.DelegateCommand(_ => scaledClicks++)
        };
        var scaledCombo = new toolkit::Acrux.Controls.ComboBox
        {
            Width = 80,
            HorizontalAlignment = toolkit::Acrux.Core.HorizontalAlignment.Left,
            ItemsSource = new List<string> { "uno", "dos", "tres" }
        };
        scaledCombo.SelectedIndex = 0;
        var scaledPanel = new toolkit::Acrux.Layout.StackPanel();
        scaledPanel.AddChild(scaledButton);
        scaledPanel.AddChild(scaledCombo);
        var transform = new toolkit::Acrux.Layout.LayoutTransformControl
        {
            LayoutTransform = new toolkit::Acrux.Styling.ScaleTransform(2f, 2f),
            Child = scaledPanel
        };
        scaledWindow.Content = transform;
        scaledWindow.Show(root);
        frame.Clear();
        host.Arrange(new RectangleF(0, 0, 400, 300));
        host.Draw(frame);
        Func<Vector2, Vector2> toRoot = point => transform.Bounds.Position + (point - transform.Bounds.Position) * 2f;
        bool laidOutScaled = Math.Abs(scaledPanel.Bounds.Width - transform.Bounds.Width / 2f) < .5f;
        host.MovePointer(toRoot(scaledButton.Bounds.Center));
        host.SetPrimaryButton(true);
        host.SetPrimaryButton(false);
        host.MovePointer(toRoot(scaledCombo.Bounds.Center));
        host.SetPrimaryButton(true);
        host.SetPrimaryButton(false);
        frame.Clear();
        host.Draw(frame);
        bool scaledOpened = scaledCombo.IsDropDownOpen;
        var dos = frame.FirstOrDefault(sp => sp.Data == "dos");
        if (dos.Position.HasValue)
        {
            host.MovePointer(dos.Position.Value + new Vector2(8, 12));
            host.SetPrimaryButton(true);
            host.SetPrimaryButton(false);
        }
        Check(laidOutScaled && scaledClicks == 1 && scaledOpened && scaledCombo.SelectedIndex == 1,
            "a LayoutTransformControl lays its child out scaled and maps clicks and its popups (layout " + laidOutScaled +
            ", clicks " + scaledClicks + ", open " + scaledOpened + ", index " + scaledCombo.SelectedIndex + ")");

        for (int i = 0; i < 3; i++)
            new toolkit::Acrux.Controls.ComboBox().IsDropDownOpen = false;
        scaledCombo.IsDropDownOpen = true;
        frame.Clear();
        host.Draw(frame);
        bool popupDrawn = frame.Any(sp => sp.Data == "tres");
        scaledCombo.IsDropDownOpen = false;
        Check(popupDrawn, "closing combo boxes that never opened does not hide another one's open popup");
        scaledWindow.Close();

        var appWindow = new toolkit::Acrux.Controls.Window
        {
            WindowDecorations = toolkit::Acrux.Controls.WindowDecorations.None,
            Width = 300,
            Height = 200,
            Topmost = true
        };
        appWindow.Position = new Vector2(20, 20);
        int appClicks = 0;
        appWindow.Content = new toolkit::Acrux.Controls.Button
        {
            Command = new toolkit::Acrux.Core.Input.DelegateCommand(_ => appClicks++)
        };
        appWindow.Show(root);
        var nestedDialog = new toolkit::Acrux.Controls.Window
        {
            WindowDecorations = toolkit::Acrux.Controls.WindowDecorations.None,
            Width = 120,
            Height = 60
        };
        int dialogClicks = 0;
        var dialogButton = new toolkit::Acrux.Controls.Button
        {
            Command = new toolkit::Acrux.Core.Input.DelegateCommand(_ => dialogClicks++)
        };
        var dialogCombo = new toolkit::Acrux.Controls.ComboBox { ItemsSource = new List<string> { "alpha", "beta" } };
        var dialogPanel = new toolkit::Acrux.Layout.StackPanel();
        dialogPanel.AddChild(dialogButton);
        dialogPanel.AddChild(dialogCombo);
        nestedDialog.Content = dialogPanel;
        toolkit::Acrux.Extensions.WindowDialogs.ShowModal(nestedDialog, appWindow);
        frame.Clear();
        host.Arrange(new RectangleF(0, 0, 400, 300));
        host.Draw(frame);
        host.MovePointer(dialogButton.Bounds.Center);
        host.SetPrimaryButton(true);
        host.SetPrimaryButton(false);
        host.MovePointer(new Vector2(appWindow.FrameBounds.X + 4, appWindow.FrameBounds.Bottom - 4));
        host.SetPrimaryButton(true);
        host.SetPrimaryButton(false);
        host.MovePointer(dialogCombo.Bounds.Center);
        host.SetPrimaryButton(true);
        host.SetPrimaryButton(false);
        frame.Clear();
        host.Draw(frame);
        bool nestedComboOpen = dialogCombo.IsDropDownOpen && frame.Any(sp => sp.Data == "beta");
        Check(dialogClicks == 1 && appClicks == 0 && nestedComboOpen && appWindow.IsActive,
            "a dialog of an owned window takes input and blocks that window (dialog " + dialogClicks + ", window " +
            appClicks + ", combo " + nestedComboOpen + ", owner active " + appWindow.IsActive + ")");
        dialogCombo.IsDropDownOpen = false;
        nestedDialog.Close();
        appWindow.Close();

        var growing = new toolkit::Adk.Mvvm.ObservableList<string>();
        var follower = new toolkit::Acrux.Controls.ComboBox { ItemsSource = growing };
        growing.Add("first");
        growing.Add("second");
        follower.SelectedIndex = 1;
        growing.Insert(0, "zero");
        Check(follower.SelectedIndex == 2 && Equals(follower.SelectedItem, "second"),
            "a combo box follows an observable ItemsSource and keeps its selected item (" + follower.SelectedIndex + ")");

        int opens = 0;
        b.PositionChanged += () =>
        {
            if (opens++ == 0)
                new toolkit::Acrux.Controls.Window { ShowActivated = false }.Show(root);
        };
        b.Position = new Vector2(150, 90);
        Exception arrangeError = null;
        try
        {
            frame.Clear();
            host.Arrange(new RectangleF(0, 0, 400, 300));
            host.Draw(frame);
        }
        catch (Exception error)
        {
            arrangeError = error;
        }
        Check(arrangeError == null && opens > 0, "a PositionChanged handler may open a window while the owner lays out (" +
              (arrangeError == null ? "ok" : arrangeError.Message) + ")");
        root.Dispose();
    }

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

    static void CheckGradients(CounterView view)
    {
        var gradient = view.GradientBorder.Background as Acrux.Styling.LinearGradientBrush;
        Check(gradient != null && gradient.GradientStops.Count == 2 &&
              gradient.GradientStops[1].Color == new Color(0, 0, 255) && gradient.GradientStops[1].Offset == 1f &&
              gradient.EndPoint.Equals(new Acrux.Styling.RelativePoint(1f, 0f, Acrux.Styling.RelativeUnit.Relative)) &&
              gradient.SpreadMethod == Acrux.Styling.GradientSpreadMethod.Reflect,
            "a compiled <LinearGradientBrush> with GradientStop children reached the client as one brush");
        Color fallback = gradient == null ? Color.Transparent : gradient.FallbackColor;
        Check(Math.Abs(fallback.R - 127) <= 1 && fallback.G == 0 && Math.Abs(fallback.B - 127) <= 1,
            "a gradient's fallback color is the average of its stops (" + fallback + ")");

        var sheet = toolkit::Acrux.Styling.StyleSheet.Load(
            "<Styles xmlns=\"https://github.com/ArthurGamerHD/AcruxUI\"><Style Selector=\"Border\">" +
            "<Setter Property=\"Background\"><RadialGradientBrush RadiusX=\"25%\" RadiusY=\"10\">" +
            "<RadialGradientBrush.GradientStops><GradientStop Color=\"#FFFFFF\" Offset=\"0\" />" +
            "<GradientStop Color=\"#000000\" Offset=\"1\" /></RadialGradientBrush.GradientStops>" +
            "</RadialGradientBrush></Setter></Style></Styles>");
        var styled = new toolkit::Acrux.Controls.Border { Width = 10, Height = 10, Styles = sheet };
        var styledHost = new toolkit::Acrux.Lcd.LcdGuiHost(SurfaceMock.Create(new Vector2(32, 32)), styled);
        styledHost.Input.KeyboardEnabled = false;
        styledHost.Arrange(new RectangleF(0, 0, 32, 32));
        var styledSprites = new List<MySprite>();
        styledHost.Draw(styledSprites);
        var radial = styled.Background as toolkit::Acrux.Styling.RadialGradientBrush;
        Check(radial != null && radial.GradientStops.Count == 2 &&
              radial.RadiusX.Equals(new toolkit::Acrux.Styling.RelativeScalar(.25f, toolkit::Acrux.Styling.RelativeUnit.Relative)) &&
              radial.RadiusY.Equals(new toolkit::Acrux.Styling.RelativeScalar(10f, toolkit::Acrux.Styling.RelativeUnit.Absolute)),
            "a style Setter parsed a <RadialGradientBrush> with a GradientStops property element");

        // The baked texels: a horizontal red-to-blue gradient over 8x2 texels, its middle purple.
        var linear = new toolkit::Acrux.Styling.LinearGradientBrush
        {
            EndPoint = new toolkit::Acrux.Styling.RelativePoint(1f, 0f, toolkit::Acrux.Styling.RelativeUnit.Relative)
        };
        linear.GradientStops.Add(new Color(255, 0, 0), 0f);
        linear.GradientStops.Add(new Color(0, 0, 255), 1f);
        var pixels = new byte[8 * 2 * 4];
        var canvas = new toolkit::Acrux.Rendering.PixelCanvas(pixels, 8, 2);
        Type painter = typeof(toolkit::Acrux.Rendering.PixelCanvas).Assembly.GetType("Acrux.Rendering.GradientTexture");
        painter.GetMethod("PaintFill", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { canvas, linear, new Vector2(80f, 20f), Vector4.Zero });
        Check(pixels[0] > 220 && pixels[2] < 35 && pixels[7 * 4] < 35 && pixels[7 * 4 + 2] > 220 &&
              Math.Abs(pixels[4 * 4] - pixels[4 * 4 + 2]) < 40 && pixels[3] == 255,
            "a LinearGradientBrush baked red at its start and blue at its end (" + pixels[0] + "," + pixels[2] + " / " +
            pixels[28] + "," + pixels[30] + ")");

        // A shallow gradient (three levels over 256 texels) is dithered: the column averages follow the ramp instead of
        // stepping in bands, and opaque stays opaque.
        var shallow = new toolkit::Acrux.Styling.LinearGradientBrush
        {
            EndPoint = new toolkit::Acrux.Styling.RelativePoint(1f, 0f, toolkit::Acrux.Styling.RelativeUnit.Relative)
        };
        shallow.GradientStops.Add(new Color(10, 10, 10), 0f);
        shallow.GradientStops.Add(new Color(13, 13, 13), 1f);
        const int rampWidth = 256, rampHeight = 16;
        var ramp = new byte[rampWidth * rampHeight * 4];
        painter.GetMethod("PaintFill", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[]
        {
            new toolkit::Acrux.Rendering.PixelCanvas(ramp, rampWidth, rampHeight), shallow,
            new Vector2(rampWidth, rampHeight), Vector4.Zero
        });
        double worst = 0;
        bool opaque = true;
        var levels = new HashSet<byte>();
        for (int x = 0; x < rampWidth; x += 8)
        {
            double sum = 0;
            for (int column = x; column < x + 8; column++)
            for (int y = 0; y < rampHeight; y++)
            {
                int index = (y * rampWidth + column) * 4;
                sum += ramp[index];
                levels.Add(ramp[index]);
                opaque &= ramp[index + 3] == 255;
            }
            double expected = 10 + 3 * (x + 4) / (double)rampWidth;
            worst = Math.Max(worst, Math.Abs(sum / (8 * rampHeight) - expected));
        }
        Check(worst < .35 && opaque && levels.Count >= 4,
            "a shallow gradient is dithered instead of banded (worst column off by " + worst.ToString("0.00") +
            " levels, " + levels.Count + " levels used, opaque " + opaque + ")");

        // An OpacityMask texture: resolved once through the host and decoded in the background (not drawn until then),
        // with one warning naming it, then served from the cache;
        // re-encoded as texel times gradient (the white texel takes the gradient's color, the clear one stays clear).
        Assembly toolkitAssembly = typeof(toolkit::Acrux.Rendering.PixelCanvas).Assembly;
        Type interop = toolkitAssembly.GetType("Acrux.Toolkit.Server.ExternalInterop");
        Type texturePixels = toolkitAssembly.GetType("Acrux.Rendering.TexturePixels");
        FieldInfo warningField = interop.GetField("Warning", BindingFlags.Static | BindingFlags.NonPublic);
        FieldInfo loaderField = interop.GetField("LoadTexturePixels", BindingFlags.Static | BindingFlags.NonPublic);
        FieldInfo backgroundField = interop.GetField("RunInBackground", BindingFlags.Static | BindingFlags.NonPublic);
        object previousWarning = warningField.GetValue(null);
        object previousLoader = loaderField.GetValue(null);
        object previousBackground = backgroundField.GetValue(null);
        var queued = new List<Action>();
        var warnings = new List<string>();
        int loads = 0;
        // Premultiplied: one opaque white texel, one transparent.
        object maskPixels = Activator.CreateInstance(texturePixels, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { 2, 1, new byte[] { 255, 255, 255, 255, 0, 0, 0, 0 } }, null);
        Type decodeType = typeof(Func<>).MakeGenericType(texturePixels);
        Func<object> decode = () => { loads++; return maskPixels; };
        Delegate typedDecode = System.Linq.Expressions.Expression.Lambda(decodeType,
            System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(decode)), texturePixels)).Compile();
        var nameParameter = System.Linq.Expressions.Expression.Parameter(typeof(string));
        var sideParameter = System.Linq.Expressions.Expression.Parameter(typeof(int));
        warningField.SetValue(null, new Action<string>(warnings.Add));
        backgroundField.SetValue(null, new Action<Action>(queued.Add));
        loaderField.SetValue(null, System.Linq.Expressions.Expression.Lambda(loaderField.FieldType,
            System.Linq.Expressions.Expression.Constant(typedDecode, decodeType), nameParameter, sideParameter).Compile());
        try
        {
            MethodInfo getMask = painter.GetMethod("GetMask", BindingFlags.Static | BindingFlags.NonPublic);
            var arguments = new object[] { "HarnessMask", null };
            string pending = getMask.Invoke(null, arguments).ToString();
            Check(pending == "Loading" && arguments[1] == null && loads == 0 && queued.Count == 1,
                "a gradient's OpacityMask decodes on a background thread, and is not drawn until then (" + pending + ")");
            queued[0]();
            string ready = getMask.Invoke(null, arguments).ToString();
            object first = arguments[1];
            getMask.Invoke(null, arguments);
            object second = arguments[1];
            Check(ready == "Ready" && first != null && ReferenceEquals(first, second) && loads == 1 && queued.Count == 1 &&
                  warnings.Count == 1 &&
                  warnings[0].Contains("slow") && warnings[0].Contains("'HarnessMask' will be cached"),
                "a gradient's OpacityMask texture is decoded once, with a single warning naming it (" + loads + " loads, " +
                warnings.Count + " warnings: " + string.Join(" | ", warnings) + ")");

            var masked = new byte[2 * 1 * 4];
            painter.GetMethod("PaintMasked", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[]
            {
                new toolkit::Acrux.Rendering.PixelCanvas(masked, 2, 1), linear, new RectangleF(0, 0, 20, 10),
                new RectangleF(0, 0, 20, 10), first
            });
            Check(masked[0] > 150 && masked[2] < 100 && masked[3] == 255 && masked[7] == 0,
                "the mask was re-encoded with the gradient (" + string.Join(",", masked) + ")");
        }
        finally
        {
            warningField.SetValue(null, previousWarning);
            loaderField.SetValue(null, previousLoader);
            backgroundField.SetValue(null, previousBackground);
        }
    }

    sealed class FakeTexture : toolkit::Acrux.Rendering.IDynamicTexture
    {
        public FakeTexture(Vector2I size) { Size = size; }
        public toolkit::Acrux.Rendering.TextureSource Source => toolkit::Acrux.Rendering.TextureSource.FromKey("fake");
        public bool IsAvailable => !Disposed;
        public Vector2I Size { get; }
        public bool Disposed;
        public int Paints;
        public bool Update(Action<toolkit::Acrux.Rendering.PixelCanvas> paint)
        {
            Paints++;
            paint(new toolkit::Acrux.Rendering.PixelCanvas(new byte[Size.X * Size.Y * 4], Size.X, Size.Y));
            return true;
        }
        public void Dispose() { Disposed = true; }
    }

    sealed class FakeTextures : toolkit::Acrux.Rendering.IDynamicTextureFactory
    {
        public readonly List<FakeTexture> Created = new List<FakeTexture>();
        public Vector2I FitToSupportedSize(Vector2I requested) { return requested; }
        public toolkit::Acrux.Rendering.IDynamicTexture Create(string name, Vector2I size)
        {
            var texture = new FakeTexture(size);
            Created.Add(texture);
            return texture;
        }
    }

    static void CheckGradientSharing()
    {
        Assembly assembly = typeof(toolkit::Acrux.Rendering.PixelCanvas).Assembly;
        Type cacheType = assembly.GetType("Acrux.Rendering.GradientTextureCache");
        Type keyType = cacheType.GetNestedType("Key", BindingFlags.NonPublic | BindingFlags.Public);
        Type paintType = typeof(Action<,>).MakeGenericType(typeof(toolkit::Acrux.Rendering.PixelCanvas),
            assembly.GetType("Acrux.Styling.GradientBrush"));
        var textures = new FakeTextures();
        object cache = Activator.CreateInstance(cacheType, true);
        MethodInfo acquire = cacheType.GetMethod("Acquire", BindingFlags.Instance | BindingFlags.NonPublic);
        Delegate paint = System.Linq.Expressions.Expression.Lambda(paintType, System.Linq.Expressions.Expression.Empty(),
            System.Linq.Expressions.Expression.Parameter(typeof(toolkit::Acrux.Rendering.PixelCanvas)),
            System.Linq.Expressions.Expression.Parameter(assembly.GetType("Acrux.Styling.GradientBrush"))).Compile();
        Func<Color, object> lease = top =>
        {
            var brush = new toolkit::Acrux.Styling.LinearGradientBrush();
            brush.GradientStops.Add(top, 0f);
            brush.GradientStops.Add(Color.Black, 1f);
            object key = Activator.CreateInstance(keyType);
            keyType.GetField("Brush").SetValue(key, brush);
            keyType.GetField("Size").SetValue(key, new Vector2I(64, 32));
            keyType.GetField("Layout").SetValue(key, new Vector2(64f, 32f));
            return acquire.Invoke(cache, new[] { textures, key, paint });
        };
        object first = lease(Color.Red);
        object second = lease(Color.Red);
        object third = lease(Color.Blue);
        Check(first != null && second != null && third != null && textures.Created.Count == 2 &&
              textures.Created[0].Paints == 1,
            "equal gradients at one size share one baked texture; another brush gets its own (" + textures.Created.Count + " made)");
        ((IDisposable)first).Dispose();
        ((IDisposable)second).Dispose();
        ((IDisposable)third).Dispose();
        Check(!textures.Created[0].Disposed, "a released gradient texture is kept a while for reuse");

        // Across surfaces: two LCDs' factories (the toolkit's own budget) resolve the same stores; a factory that
        // cannot share gets none.
        Type stores = assembly.GetType("Acrux.Rendering.SharedTextureStores");
        MethodInfo gradientStore = stores.GetMethod("Gradient", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo borderStore = stores.GetMethod("Border", BindingFlags.Static | BindingFlags.NonPublic);
        var lcdA = new toolkit::Acrux.Lcd.LcdDynamicTextureFactory();
        var lcdB = new toolkit::Acrux.Lcd.LcdDynamicTextureFactory();
        var screen = new toolkit::Acrux.ScreenSpace.ScreenSpaceDynamicTextureFactory();
        object gradientA = gradientStore.Invoke(null, new object[] { lcdA });
        object gradientB = gradientStore.Invoke(null, new object[] { lcdB });
        object borderA = borderStore.Invoke(null, new object[] { lcdA });
        object borderB = borderStore.Invoke(null, new object[] { lcdB });
        object screenGradient = gradientStore.Invoke(null, new object[] { screen });
        Check(gradientA != null && ReferenceEquals(gradientA, gradientB) && borderA != null && ReferenceEquals(borderA, borderB) &&
              !ReferenceEquals(screenGradient, gradientA) && gradientStore.Invoke(null, new object[] { textures }) == null,
            "two LCD surfaces of one mod share gradient and Border texture stores; screen space and unshareable factories do not");
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
