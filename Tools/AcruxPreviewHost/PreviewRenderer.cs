using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Acrux.Controls;
using Acrux.Core;
using Acrux.Lcd;
using Acrux.Markup;
using Acrux.Styling;
using SkiaSharp;
using VRage.Game.GUI.TextPanel;
using VRageMath;

namespace AcruxPreview
{
    /// <summary>
    /// Turns the XAML the IDE sends into a bitmap: builds the view's content with Acrux's runtime loader,
    /// lays it out on a simulated LCD surface and paints the resulting sprite frame.
    /// </summary>
    public sealed class PreviewRenderer
    {

        readonly string _toolkitRoot;
        string _modContent;
        ModClient _mod;

        public PreviewRenderer()
        {
            _toolkitRoot = typeof(PreviewRenderer).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(a => a.Key == "AcruxPreview.ToolkitRoot").Select(a => a.Value).FirstOrDefault() ?? string.Empty;
            // The host shipped in the Acrux.Sdk package has no checkout: it carries the toolkit's styles and content.
            if (!Directory.Exists(Path.Combine(_toolkitRoot, "Framework")))
                _toolkitRoot = Path.Combine(AppContext.BaseDirectory, "toolkit");
            // Through the bridge's loader, as in the toolkit mod: the previewed mod's own loaders (none at design time) first.
            Acrux.Toolkit.Server.ServerUi.InstallSourceLoader(LoadSource);
            StyleSheet.SourceLoadFailed = (source, error) => Program.Log("style '" + source + "' failed: " + error.Message);
            // As the game's definitions answer them in the mod (image sizes drive Stretch and natural sizes).
            Acrux.Toolkit.Server.ExternalInterop.Warning = message => Program.Log("warning: " + message);
            PreviewGame.Install();
            LcdTextureResolver.IsTextureDefined = GameTextures.IsDefined;
            LcdTextureResolver.TextureSize = GameTextures.Size;
        }

        LcdGuiHost _host;
        int _width = 512, _height = 512;
        DesignResources _theme = new DesignResources(null);

        /// <summary>
        /// Builds the view from the IDE's XAML into a live tree (kept between frames, so it reacts to the pointer);
        /// returns an error message instead when the view cannot be built.
        /// </summary>
        public string Rebuild(string xaml, string assemblyPath, PreviewSettingsMessage settings, Acrux.Animation.AnimationController animations,
            string xamlPath = null)
        {
            _xamlPath = xamlPath;
            settings = settings ?? new PreviewSettingsMessage();
            int width = Math.Max(16, (int)Math.Round(settings.Width));
            int height = Math.Max(16, (int)Math.Round(settings.Height));
            var theme = new DesignResources(settings);
            _modContent = FindModContent(assemblyPath);
            _mod = ModClient.For(assemblyPath);
            GameTextures.UseMod(_modContent);
            PreviewLocalization.Use(settings.Language, _modContent);
            LcdGuiHost host;
            try
            {
                VisualElement root = BuildView(xaml, theme, settings, assemblyPath);
                host = new LcdGuiHost(PreviewSurface.Create(new Vector2(width, height)), root, animations);
                host.Input.KeyboardEnabled = false;
                // Templates (item templates) are built during layout: their errors surface here.
                try
                {
                    host.Arrange(new RectangleF(0, 0, width, height));
                }
                catch (Exception)
                {
                    host.Dispose();
                    throw;
                }
            }
            catch (Exception error)
            {
                Program.Log("view not built: " + error);
                return error.GetType().Name + ": " + error.Message;
            }

            if (_host != null)
                _host.Dispose();
            _host = host;
            DesignProperties = AcruxPreview.DesignProperties.Describe(_designTypes);
            _width = width;
            _height = height;
            _theme = theme;
            return null;
        }

        string _drawError;

        public bool HasView => _host != null;

        /// <summary>The design properties of the last view built (for the plugin's editors).</summary>
        public PreviewDesignPropertiesMessage DesignProperties { get; private set; }

        List<Type> _designTypes = new List<Type>();
        /// <summary>The last frame is stale (invalidated, or volatile: e.g. a generated texture created while drawing).</summary>
        public bool NeedsRender => _host != null && _host.NeedsRender;
        public int Width => _width;
        public int Height => _height;
        public Acrux.Core.Input.PointerCursor Cursor => _host == null ? Acrux.Core.Input.PointerCursor.Arrow : _host.PointerCursor;

        /// <summary>Paints the live tree into BGRA pixels at <paramref name="scale"/> (device pixels per surface pixel).</summary>
        public byte[] Draw(float scale, out int pixelWidth, out int pixelHeight)
        {
            pixelWidth = Math.Max(1, (int)Math.Round(_width * scale));
            pixelHeight = Math.Max(1, (int)Math.Round(_height * scale));
            var sprites = new List<MySprite>();
            if (_host != null)
            {
                _host.InvalidateFrame();
                try
                {
                    _host.Draw(sprites);
                }
                catch (Exception error)
                {
                    // A control that fails to draw (e.g. reading game state the preview lacks) leaves the rest.
                    string message = error.GetType().Name + ": " + error.Message;
                    if (message != _drawError)
                        Program.Log("draw failed: " + error);
                    _drawError = message;
                }
            }
            var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var bitmap = new SKBitmap(info))
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(new SKColor(_theme.Background.R, _theme.Background.G, _theme.Background.B));
                SpriteRasterizer.Paint(canvas, sprites, scale, new Vector2(_width, _height));
                canvas.Flush();
                return bitmap.Bytes;
            }
        }

        // ------------------------------------------------------------------ pointer (in surface pixels)

        // A host a failed rebuild (or the view) disposed takes no more input: the next rebuild replaces it.
        bool Usable => _host != null && !_host.IsDisposed;

        public void PointerMoved(double x, double y)
        {
            if (!Usable)
                return;
            if (x < 0 || y < 0 || x >= _width || y >= _height)
                _host.ClearPointer();
            else
                _host.MovePointer(new Vector2((float)x, (float)y));
        }

        public void PointerButton(int button, bool pressed)
        {
            if (!Usable)
                return;
            switch (button)
            {
                case 2: _host.SetSecondaryButton(pressed); break;
                case 3: _host.SetMiddleButton(pressed); break;
                default: _host.SetPrimaryButton(pressed); break;
            }
        }

        /// <summary><paramref name="notches"/> wheel notches; positive scrolls up, as the game's wheel delta.</summary>
        public void Scroll(double notches)
        {
            if (Usable)
                _host.Scroll((int)Math.Round(notches * 120));
        }

        public void PointerLeft()
        {
            if (Usable)
                _host.ClearPointer();
        }

        string _xamlPath;
        // A style document being previewed (its Design.PreviewWith control): its source and its edited, unsaved text,
        // which LoadSource answers for that source instead of the file.
        string _editedStyleSource;
        string _editedStyleText;

        VisualElement BuildView(string xaml, DesignResources theme, PreviewSettingsMessage settings, string assemblyPath)
        {
            _editedStyleSource = null;
            _editedStyleText = null;
            XElement styleRoot = StyleDocumentRoot(xaml);
            if (styleRoot != null)
            {
                _editedStyleSource = StyleSourceFor(_xamlPath) ?? "/Data/Gui/Styles/__AcruxPreview__.xaml";
                _editedStyleText = xaml;
                xaml = PreviewWithView(styleRoot);
            }

            // The mod's design-time resources first: their setup is in place before the view's design data is read.
            object designContext = DesignContext(settings, assemblyPath);
            GameTextures.UseRuntimeTextures(RuntimeTextures(designContext), _modContent);
            var design = new DesignViewBuilder(_mod);
            _designTypes = design.DesignTypes;
            VisualElement built = design.Build(xaml);
            Notice = PendingBuildNotice(design);

            // The view's design data context (Design.DataContext), sized as Design.Width / Height ask.
            // Always set (null without one): the view never sees the surrounding theme as its data context, as in
            // game, where a view gets its own view model.
            // As Avalonia's previewer, the view's own class hosts the edited content when it can (a UserControl), so
            // its code-behind runs: layout overrides (MeasureOverride sizing the view model), resources, handlers.
            VisualElement hosted = built;
            var userControl = design.CodeBehind as UserControl;
            if (userControl != null)
            {
                try
                {
                    userControl.Content = built;
                    hosted = userControl;
                }
                catch (Exception error)
                {
                    Program.Log("view class does not host the preview: " + error.Message);
                }
            }
            // The view's own Styles as edited, not as last compiled (InitializeComponent set those): its inline styles
            // follow every edit, and with none left only its includes apply (in the sheet below).
            var hostedControl = hosted as Control;
            if (hostedControl != null && (design.InlineStyles != null || ReferenceEquals(hosted, userControl)))
            {
                try
                {
                    hostedControl.Styles = design.InlineStyles != null
                        ? StyleSheet.LoadInline(design.InlineStyles)
                        : StyleSheet.Empty;
                }
                catch (Exception error)
                {
                    hostedControl.Styles = StyleSheet.Empty;
                    Program.Log("inline styles skipped: " + error.Message);
                }
            }
            // A Border (a type the mod's bridge knows, so a design host can take it) around the view's holder.
            var content = new Border { Child = new PreviewViewHolder { Child = hosted }, DataContext = design.RootContext };
            if (!float.IsNaN(design.DesignWidth))
                content.Width = design.DesignWidth;
            if (!float.IsNaN(design.DesignHeight))
                content.Height = design.DesignHeight;

            // Around it, the theme an app gives its views: the mod's design-time theme when it names one, else a
            // stand-in; and the resources the view's code-behind adds (converters).
            VisualElement shown = HostView(content, hosted, design, assemblyPath) ?? content;
            var view = new Border { Child = shown, DataContext = designContext ?? theme };
            if (design.CodeBehind != null && !ReferenceEquals(hosted, design.CodeBehind))
                view.Resources.Parent = design.CodeBehind.Resources;
            DisposeCodeBehind();
            _codeBehind = design.CodeBehind;

            var sources = new List<string> { "/Data/Gui/Styles/Acrux/Fluent/FluentStyle.xaml" };
            // The mod's own theme: the stylesheets its project names (AcruxPreviewDesignStyles), else the usual layout
            // (as Arthur's LCD Mod has it).
            string designStyles = PreviewProjectSettings.Get(assemblyPath, "designStyles");
            if (!string.IsNullOrEmpty(designStyles))
            {
                foreach (string source in designStyles.Split(','))
                    if (source.Trim().Length > 0)
                        sources.Add(source.Trim());
            }
            else if (_modContent != null && File.Exists(Path.Combine(_modContent, "Data/Gui/Styles/Fluent/FluentStyle.xaml")))
                sources.Add("/Data/Gui/Styles/Fluent/FluentStyle.xaml");
            sources.AddRange(design.StyleSources);
            if (_editedStyleSource != null)
            {
                // Last, so its setters refine the themes; read again on every edit (and its includes with it).
                sources.Add(_editedStyleSource);
                StyleSheet.ClearSourceCache();
            }
            try
            {
                view.Styles = StyleSheet.LoadFromSources(sources);
            }
            catch (Exception error)
            {
                Program.Log("styles skipped: " + error.Message);
            }
            return view;
        }

        Control _codeBehind;

        /// <summary>A warning for the plugin about the last view built; null when none.</summary>
        public string Notice { get; private set; }

        // The view class's root (its attributes, Resources...) is the compiled one: tell when the edited XAML's differs.
        string PendingBuildNotice(DesignViewBuilder design)
        {
            if (_mod == null || string.IsNullOrEmpty(design.ViewClass) || design.RootSignature == null)
                return null;
            bool buildHasSignatures;
            string compiled = _mod.CompiledRootSignature(design.ViewClass, out buildHasSignatures);
            if (!buildHasSignatures || compiled == design.RootSignature)
                return null;
            return compiled == null
                ? "This view is not in the last build of the mod: build it to preview its own class (code-behind, root bindings)."
                : "The file has logical changes pending and requires a recompilation to display properly.";
        }

        void DisposeCodeBehind()
        {
            var disposable = _codeBehind as IDisposable;
            _codeBehind = null;
            if (disposable == null)
                return;
            try
            {
                disposable.Dispose();
            }
            catch (Exception error)
            {
                Program.Log("view class not disposed: " + error.Message);
            }
        }

        // The mod's design-time resource provider (named by AcruxPreviewDesignResources in its project), with the
        // preview's settings applied; null when the mod names none or it cannot be created.
        object DesignContext(PreviewSettingsMessage settings, string assemblyPath)
        {
            string typeName = PreviewProjectSettings.Get(assemblyPath, "designResources");
            if (_mod == null || string.IsNullOrEmpty(typeName))
                return null;
            var values = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Background", settings.Background),
                new KeyValuePair<string, string>("Foreground", settings.Foreground),
                new KeyValuePair<string, string>("Seed", settings.Seed),
                new KeyValuePair<string, string>("FontId", string.IsNullOrEmpty(settings.FontId) ? "White" : settings.FontId),
                new KeyValuePair<string, string>("FontScale", settings.FontScale.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
            string error;
            object context = _mod.CreateObject(typeName, values, out error);
            if (context == null)
                Program.Log("design resources '" + typeName + "' not created: " + error);
            return context;
        }

        // The LCD textures the mod registers at run time, as its design resources list them (DesignTextures()); none
        // when they have no such method.
        IEnumerable<KeyValuePair<string, string>> RuntimeTextures(object designContext)
        {
            MethodInfo method = designContext == null ? null
                : designContext.GetType().GetMethod("DesignTextures", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method == null)
                return null;
            try
            {
                return method.Invoke(designContext, null) as IEnumerable<KeyValuePair<string, string>>;
            }
            catch (TargetInvocationException error)
            {
                Program.Log("design textures not read: " + (error.InnerException ?? error).Message);
                return null;
            }
        }

        // The mod's design-time host (AcruxPreviewDesignHost) around the view, as the mod hosts it at run time; null when the
        // mod names none or the host leaves this view as it is.
        VisualElement HostView(VisualElement content, VisualElement root, DesignViewBuilder design, string assemblyPath)
        {
            string typeName = PreviewProjectSettings.Get(assemblyPath, "designHost");
            if (_mod == null || string.IsNullOrEmpty(typeName))
                return null;
            string error;
            object host = _mod.CreateObject(typeName, new KeyValuePair<string, string>[0], out error);
            if (host == null)
            {
                Program.Log("design host '" + typeName + "' not created: " + error);
                return null;
            }
            try
            {
                _mod.WriteProperty(host, "ViewClass", design.ViewClass);
                _mod.WriteProperty(host, "DesignContext", design.RootContext);
                _mod.WriteProperty(host, "ViewRoot", _mod.WrapperOf(root));
                _mod.WriteProperty(host, "Content", _mod.WrapperOf(content));
                object view = _mod.ReadModProperty(host, "View");
                return ReferenceEquals(view, Acrux.Data.BindingOperations.DoNothing) ? null : _mod.NativeOf(view) as VisualElement;
            }
            catch (Exception error2)
            {
                Program.Log("design host '" + typeName + "' failed: " + error2.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------ style documents (Design.PreviewWith)

        // Avalonia's previewer shows a style document through the control its Design.PreviewWith names; Acrux style
        // documents are <Styles> (a lone <Style> root is not one).
        static XElement StyleDocumentRoot(string xaml)
        {
            try
            {
                XElement root = XDocument.Parse(xaml).Root;
                return root != null && root.Name.LocalName == "Styles" ? root : null;
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
        }

        // The Design.PreviewWith control as a view of its own, with the document's namespace prefixes; a hint when
        // the document has none.
        static string PreviewWithView(XElement styles)
        {
            XNamespace acrux = "https://github.com/ArthurGamerHD/AcruxUI";
            XElement previewWith = styles.Elements().FirstOrDefault(e => e.Name.LocalName == "Design.PreviewWith");
            XElement control = previewWith?.Elements().FirstOrDefault();
            if (control == null)
            {
                Program.Log("style document without Design.PreviewWith: nothing to preview");
                control = new XElement(acrux + "TextBlock",
                    new XAttribute("Text", "Add <Design.PreviewWith> with a control to preview these styles on."),
                    new XAttribute("TextWrapping", "Wrap"), new XAttribute("Margin", "12"));
            }
            var view = new XElement(acrux + "UserControl",
                styles.Attributes().Where(a => a.IsNamespaceDeclaration),
                new XElement(control));
            return view.ToString(SaveOptions.DisableFormatting);
        }

        // The style source (as a StyleInclude names it) of the previewed file, from its path in the project or on disk,
        // so its relative includes resolve; null when it is not under a Data/Gui folder.
        static string StyleSourceFor(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            string normalized = path.Replace('\\', '/');
            const string toolkitStyles = "/Framework/Acrux/Styles/";
            int index = normalized.IndexOf(toolkitStyles, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
                return "/Data/Gui/Styles/Acrux/" + normalized.Substring(index + toolkitStyles.Length);
            index = normalized.IndexOf("Data/Gui/", StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (index == 0 || normalized[index - 1] == '/'))
                return "/" + normalized.Substring(index);
            return null;
        }

        static bool SameSource(string a, string b)
        {
            return string.Equals((a ?? string.Empty).Replace('\\', '/').TrimStart('/'),
                (b ?? string.Empty).Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase);
        }

        string LoadSource(string source)
        {
            if (_editedStyleText != null && SameSource(source, _editedStyleSource))
                return _editedStyleText;
            string path = ResolveSource(source);
            if (path == null || !File.Exists(path))
                throw new FileNotFoundException("Style source not found: " + source, path ?? source);
            return File.ReadAllText(path);
        }

        string ResolveSource(string source)
        {
            string relative = (source ?? string.Empty).Replace('\\', '/').TrimStart('/');
            const string acruxPrefix = "Data/Gui/Styles/Acrux/";
            if (relative.StartsWith(acruxPrefix, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(_toolkitRoot, "Framework/Acrux/Styles", relative.Substring(acruxPrefix.Length));
            string toolkitFile = Path.Combine(_toolkitRoot, "AcruxUiToolkit/Content", relative);
            if (relative.StartsWith("Data/Gui/Styles/Toolkit/", StringComparison.OrdinalIgnoreCase))
                return toolkitFile;
            return _modContent == null ? toolkitFile : Path.Combine(_modContent, relative);
        }

        // The view's assembly is built into bin/...; the mod's Content folder sits next to its project file.
        static string FindModContent(string assemblyPath)
        {
            if (string.IsNullOrEmpty(assemblyPath))
                return null;
            string project = ModClient.FindProjectDirectory(assemblyPath);
            string content = project == null ? null : Path.Combine(project, "Content");
            return content != null && Directory.Exists(content) ? content : null;
        }
    }

    /// <summary>
    /// Holds the previewed view. A window view (a dialog) is arranged over the whole surface, as the window tree
    /// arranges a window over its owner (its own Width and Height size its frame, not the element); any other view
    /// lays out as in a Border.
    /// </summary>
    sealed class PreviewViewHolder : Acrux.Layout.Decorator
    {
        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            if (!(Child is Window))
                return base.MeasureOverride(availableSize);
            Child.Measure(availableSize);
            return availableSize;
        }

        protected override void ArrangeChildren(RectangleF bounds)
        {
            var window = Child as Window;
            if (window == null || !window.Visible)
            {
                base.ArrangeChildren(bounds);
                return;
            }
            window.Measure(bounds.Size);
            window.Arrange(bounds);
        }
    }
}
