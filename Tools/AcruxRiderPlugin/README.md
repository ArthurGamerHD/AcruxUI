# Acrux Preview (Rider plugin)

Live preview for Acrux UI views: `.axaml` / `.xaml` files whose root element is in the Acrux XML namespace
(`https://github.com/ArthurGamerHD/AcruxUI`). The file opens as a split editor (text + preview). The preview toolbar sets
the simulated LCD surface size, font, font scale and the background, foreground and seed colours of the design-time
theme; the choices are remembered per project.

The preview is rendered by the toolkit's preview host (`Tools/AcruxPreviewHost`), which uses Acrux's real LCD
backend. The plugin talks to it over Avalonia's remote previewer protocol, used only as the wire format; the mod needs
no Avalonia references. The preview hosting follows [AvaloniaRider](https://github.com/ForNeVeR/AvaloniaRider); both
AvaloniaRider's and Avalonia's MIT licences are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and inside the
plugin jar.

## Build and install (local plugin)

    ./build.sh [rider-install-dir]      # default: $RIDER_HOME or /opt/rider

Then in Rider: Settings > Plugins > gear icon > Install Plugin from Disk... > `build/acrux-preview.jar`, and restart.
Rebuild it after a Rider major update (it compiles against the installed Rider's jars).

## Enable it for a mod

Import `Tools/AcruxPreviewHost/AcruxPreview.props` in the mod's project (Debug only is enough) and build the mod once.
The build writes `obj/acrux-preview.properties` (host, dotnet and mod assembly paths) and builds the preview host the
first time.

## Design-time data

As in Avalonia's previewer, a view can give the preview a data context; the view compiler drops this markup
(Design.* and the Blend `d:` directives), so it may name design-only types:

    <Window ... xmlns:d="http://schemas.microsoft.com/expression/blend/2008" mc:Ignorable="d"
            Design.DataContext="{x:Static dialogs:MessageBoxDesignData.Confirm}"
            d:DesignWidth="440">

or as a property element, `<Design.DataContext><vm:MyDesignViewModel /></Design.DataContext>` (parameterless
constructor; literal attributes set its properties). Put such types in `*.AcruxDesign.cs` files: MDK does not pack them.
Bindings, converters (from the view's code-behind, when the view class can be created with no arguments or with the
design data context), item templates and dynamic resources then render as in game.

The surrounding theme comes from the type the mod names in `AcruxPreviewDesignResources` (see `AcruxPreview.props`); the
preview toolbar's colours and font are set on it.

## Design properties

The "Design data" toolbar button shows a panel of editors for the view's design properties: the public static
settable properties of the class its `{x:Static}` design data comes from. Strings get a text field, `bool` a
checkbox, `int`/`long` and `float`/`double` a number spinner, enums a combo box and `VRageMath.Color` a colour
swatch. A change rebuilds the preview; the values are remembered per view file, and "Defaults" goes back to the
class's own values. For example, a design data class can expose the app's theme ("display mode"):

    internal static class CargoFilledDesignData
    {
        public static DisplayMode DisplayMode { get; set; } = DisplayMode.Grid;
        public static bool DrawLines { get; set; }

        public static CargoFilledViewModel Default => Create(DisplayMode == DisplayMode.Grid, DrawLines);
    }

with `Design.DataContext="{x:Static cargo:CargoFilledDesignData.Default}"` on the view. Without the IDE, the host's
`--render view.xaml out.png <mod dll> <width> <height> [Type.Property=value ...]` sets them too and lists them.
