using Avalonia.Remote.Protocol;

namespace AcruxPreview
{
    /// <summary>
    /// The Acrux preview plugin's settings (not part of Avalonia's protocol; both ends are ours): the simulated LCD
    /// surface and the design-time theme.
    /// </summary>
    [AvaloniaRemoteMessageGuid("3D6A0C1E-7B44-4E0F-9A54-ADC0F5E2B101")]
    public class PreviewSettingsMessage
    {
        public double Width { get; set; } = 512;
        public double Height { get; set; } = 512;
        public string FontId { get; set; }
        public double FontScale { get; set; } = 0.7;
        // The game's default LCD colours, and Arthur's LCD Mod's default faction colour as the seed.
        public string Background { get; set; } = "#005897";
        public string Foreground { get; set; } = "#C5EDFF";
        public string Seed { get; set; } = "#3A203F";
        /// <summary>Culture of the previewed texts ("en", "de", "pt-BR", ...).</summary>
        public string Language { get; set; } = "en";
    }
}

namespace AcruxPreview
{
    /// <summary>Host to plugin: the mouse cursor the hovered control asks for (Acrux.Core.Input.PointerCursor).</summary>
    [Avalonia.Remote.Protocol.AvaloniaRemoteMessageGuid("3D6A0C1E-7B44-4E0F-9A54-ADC0F5E2B102")]
    public class PreviewCursorMessage
    {
        public int Cursor { get; set; }
    }
}

namespace AcruxPreview
{
    /// <summary>Host to plugin: the languages the game ships, for the preview's language selector.</summary>
    [Avalonia.Remote.Protocol.AvaloniaRemoteMessageGuid("3D6A0C1E-7B44-4E0F-9A54-ADC0F5E2B103")]
    public class PreviewLanguagesMessage
    {
        public string[] Languages { get; set; }
    }
}

namespace AcruxPreview
{
    /// <summary>
    /// Host to plugin: the previewed view's design properties (the public static settable properties of its design
    /// data classes), for the plugin's editors. Parallel arrays: name ("Type.Property"), kind ("string", "bool", "int",
    /// "float", "enum" or "color"), current value (as text; colours "#RRGGBB" or "#AARRGGBB") and, for enums, the
    /// names to choose from (one per line).
    /// </summary>
    [Avalonia.Remote.Protocol.AvaloniaRemoteMessageGuid("3D6A0C1E-7B44-4E0F-9A54-ADC0F5E2B104")]
    public class PreviewDesignPropertiesMessage
    {
        public string[] Names { get; set; }
        public string[] Kinds { get; set; }
        public string[] Values { get; set; }
        public string[] Options { get; set; }
    }
}

namespace AcruxPreview
{
    /// <summary>
    /// Plugin to host: the design property values the plugin's editors hold for the view (as text, by "Type.Property");
    /// every other design property keeps its default. The host rebuilds the view with them.
    /// </summary>
    [Avalonia.Remote.Protocol.AvaloniaRemoteMessageGuid("3D6A0C1E-7B44-4E0F-9A54-ADC0F5E2B106")]
    public class PreviewNoticeMessage
    {
        /// <summary>A warning about the preview (e.g. the view needs a build to show properly); empty when none.</summary>
        public string Text { get; set; }
    }

    [Avalonia.Remote.Protocol.AvaloniaRemoteMessageGuid("3D6A0C1E-7B44-4E0F-9A54-ADC0F5E2B105")]
    public class PreviewDesignValuesMessage
    {
        public string[] Names { get; set; }
        public string[] Values { get; set; }
    }
}
