# Acrux styles

`Fluent/FluentStyle.xaml` is the renderer-neutral default theme for Acrux controls.
Consumer projects package these files automatically through `Acrux.projitems` and
load the root style from:

`Data/Gui/Styles/Acrux/Fluent/FluentStyle.xaml`

The public style surface is an explicit subset of Avalonia-style XAML. Acrux may omit
properties that the Space Engineers renderers cannot support, but it does not add
renderer-specific styled properties. Those details remain internal to the controls.

The host supplies the following dynamic color resources:

- `surfaceColor`, `surfaceContainerHighColor`, `surfaceContainerHighestColor`, `onSurfaceColor`
- `borderVariantColor`, `disabledColor`, `shadowColor`
- `accentColor`, `accentContainerColor`
- `secondaryContainerColor`, `onAccentColor`
- `scrollBarTrackColor`, `scrollBarThumbColor`
- `scrollBarThumbHoverColor`, `scrollBarThumbPressedColor`

`SpaceEngineers/SpaceEngineersStyle.xaml` (`Data/Gui/Styles/Acrux/SpaceEngineers/SpaceEngineersStyle.xaml`) is the
Keen-style theme for screen-space windows. Its controls draw the game's own GUI textures through the
`Acrux_SE_*` transparent materials in `SpaceEngineers/AcruxSpaceEngineersTheme.sbc` (packaged as
`Data/AcruxSpaceEngineersTheme.sbc`). It reads the same color resources, plus `uiOpacity` (controls and text)
and `uiBackgroundOpacity` (window background images), both 1 when absent.

Screen-space windows supply `uiOpacity` and `uiBackgroundOpacity` themselves: the game's *Options > Interface* UI
opacity and UI background opacity, as the game's own screens draw with them. They are platform resources, the
lowest-priority tier (a window's, its data context's or its styles' own resources override them), and are read
once each time the window opens, never per frame (the game parses its config text on every read), so a change in
Options applies from the next opening. Any style can use them, e.g. `Opacity="{DynamicResource uiOpacity}"` or
`<ImageBrush Source="..." Opacity="{DynamicResource uiBackgroundOpacity}" />`. Add-on styles for it live with the add-on, e.g.
`Data/Gui/Styles/Acrux/Addons/SpaceEngineers/KeySelector.xaml`.

Global themes belong here, shared by every client mod. Application-specific selectors (a mod's own controls,
dialogs or settings screens) belong in the consumer, which includes a theme and adds its own styles on top.
