# Acrux UI

One Space Engineers mod that owns the Acrux UI runtime — Avalonia-style primitive controls, layout,
styles (the Acrux Fluent theme), rendering to LCD sprites / screen space, and LCD eye tracking — shared by
every mod that draws UI. Client mods keep only their XAML user controls (generated code), view models and
apps, compiled against thin generated wrappers.

```
client mod                                  Acrux UI mod
----------                                  -----------------------
XAML views  -> generated code               real Acrux controls, styles, layout, renderer
  new Border { Background = ... }  ---->    Border (toolkit object)
  (wrapper: holds the toolkit object,        delegate table published by mod message
   calls typed delegates)
class Gauge : VisualElement     <----->     Remote_VisualElement : VisualElement
  override DrawSelf / Measure / input        overrides forward to the client (hooks)
StyledProperty.Register<Gauge,float>  -->   real StyledProperty in the toolkit store
                                             (toolkit styles + TemplateBinding see it)
IMyTextSurface + LcdGuiHost.Draw(list) -->  renders sprites into the client's list
```

## Layout

- `AcruxUiToolkit/` — the mod. `Bridge/AcruxUiToolkitSession.cs` publishes the API; `EyeTracking/` is the
  shared gaze/click tracker (same API and port as the former LcdEyeTrackingApi mod).
- `ClientApi/` — **generated on every toolkit build; do not edit.** Import
  `ClientApi/ClientApi.projitems` into a client mod instead of `Acrux*.projitems`.
  - `Generated/` thin wrappers (same namespaces/names as Acrux), `Runtime/` the bridge runtime,
    `Shared/` Acrux value types compiled verbatim (`acrux-ui-toolkit-shared.txt`), `EyeTracking/` the tracker's client mirror.
- `Framework/` — the Acrux UI framework as shared projects: `Acrux` (core, controls, styling, markup, Fluent theme),
  `Acrux.Lcd`, `Acrux.ScreenSpace`, `Acrux.Toolkit/{Client,Server}` (the hand-written bridge runtime),
  `Acrux.Addons.KeySelector`, and `Acrux.Generator` (XAML views, the base-member whitelist check and the client mirror
  generator `AcruxUiToolkitMirrorGenerator.cs`). A project with Acrux XAML imports `Acrux.Generator/Acrux.Generator.props`.
- `External/` — git submodules: `Adk` (branch `ui-toolkit`), the shared library for mods without UI (the non-UI
  generators, Audio, Video, Image, Compression, Utils); `SE-Procedural-Voxel-Cubemap-Api` and `SE-Water`, the
  optional inter-mod APIs the cartography module talks to.
- `Tools/AcruxPreviewHost` and `Tools/AcruxRiderPlugin` — the live XAML preview for Rider (see its README).
- `Tests/` — offline harness: the toolkit and a sample client compile into **separate assemblies** (as two
  mods do) and talk only through the published delegate table. `Tests/run.sh` regenerates ClientApi,
  builds, renders a XAML view, clicks a button, checks bindings, styles and template bindings.

## Building

```
git clone --recursive https://github.com/ArthurGamerHD/AcruxUI.git
```

(or `git submodule update --init` in an existing clone). The mod builds with MDK2; `AcruxUiToolkit/Directory.Build.props`
points `MdkSourceRoot` at a local MDK2 checkout, so set that property for your machine.

## Whitelist

Mod scripts may only use what Space Engineers whitelists (no `#pragma`, no `GC`, no reflection beyond
`Type.Name/FullName`, ...). MDK's analyzer misses some of these, so after packing run
`Tests/check-whitelist.sh`: it compiles the packed mod folders with the game's own `MyScriptCompiler` and
whitelist (`MySpaceGameDefaultIlChecker`, with the se-dotnet-compat scripting patches the .NET 10 launcher uses).
Generated code must stay inside that subset.

## Using it from a client mod

```xml
<PackageReference Include="Acrux.Sdk" Version="0.0.1" PrivateAssets="all" />
```

The package (see `Tools/Acrux.Sdk/README.md`) emits ClientApi into the mod's build and brings the generators.

```csharp
// LoadData: mods load in any order, so the toolkit may announce itself later.
AcruxUiToolkitClient.Connect(myPrivateReplyChannel, () => StartUi());
```

The client provides the screen (an `IMyTextSurface`) and drives a host exactly as with in-mod Acrux:
`new LcdGuiHost(surface, view)`, `host.Arrange(...)`, `host.Draw(sprites)`, pointer calls for input.
`LcdSurfaceLayers` composes the surface's whole frame (tiled background, content, overlay, cursor): the client
hands it the content and overlay sprites, the pointer position and whether it shows, and uploads what
`Compose()` returns. The cursor styles (`PointerCursor`, drawn by `LcdCursor` from the `Acrux_Cursor*` textures)
follow the control under the pointer.
Style files are read by the client (`StyleSheet.SourceLoader`) and parsed by the toolkit; a path the
client does not have (for example `/Data/Gui/Styles/Acrux/Fluent/FluentStyle.xaml`) comes from the toolkit.

## Maps and 3D scenes

`Acrux.Cartography.Maps.CartographyMapRenderer` paints the Star Map and the Planet Map into a `PixelCanvas`. Past
the Planet Map's maximum zoom it becomes the 3D surface view, a camera over displaced terrain
(`ScrollPlanetSurfaceView`, `EnterPlanetSurfaceView`, `PanPlanetSurfaceView`, `OrbitPlanetSurfaceView`).

- A client draws its own meshes in the 3D scenes with `Acrux.Render3D.SceneModel`: `SetMesh(vertices, indices
  [, triangleColors])` in model metres, `WorldMatrix`, `Color`, `Opacity`, `Unlit`, `Visible`, then
  `renderer.AddModel(model)`. Models are depth-tested against terrain, grids and voxels, and show in the surface view
  and the Star Map's first-person camera (not on the flat globe).
- A client that draws its own overlay over the map projects its points with `TryGetPlanetSurfaceProjection` (flat
  globe) or `TryGetPlanetSurfaceViewCamera` plus `TryGetPlanetSurfaceViewGroundRadius` / `...GroundRadii` (3D surface
  view: the camera, and the ground as the view draws it).

## Protocol

Request channel `0x4143525558554901` (send your private reply channel as `long`), broadcast channel
`0x4143525558554902`, protocol version `1` (`Toolkit.ProtocolVersion`), toolkit version string `Toolkit.Version`. A client may be
built from an older ClientApi than the toolkit it runs on (see API stability).

## API stability

A published client mod keeps the ClientApi it was built with for as long as it stays on the Workshop, while
players always get the newest Acrux UI. So every Acrux UI update must keep serving what earlier releases shipped:

- `ClientApi/api.shipped.txt` lists the contract: each delegate-table key with its delegate type, the handshake
  constants, and every enum value that crosses the bridge as an integer. `Tests/run.sh` fails when a shipped line
  disappears (a member removed or renamed, a signature or enum value changed).
- Adding is always safe: new controls, members and enum values. A client built against a newer Acrux UI still
  loads on an older one; the members it misses are logged (`Ui.Missing`) and return null.
- Never change a shipped line. To replace a member, add the new one and keep the old one working.
- `ProtocolVersion` changes only for a wire break. A toolkit with a new protocol must keep answering
  `RequestChannel` (protocol 1) with the protocol 1 table. A client that gets a protocol it does not know disables
  its UI and logs why (`AcruxUiToolkitClient.Error`); it does not throw, so the world still loads.
- When releasing, run `Tests/run.sh --accept-api` and commit the updated `api.shipped.txt` with the release.

## Known limits

- View hot reload is not bridged to clients yet.
- Generic virtual/protected members and `ref` parameters are not mirrored (see the skip report at the end
  of `obj/.../Server.AcruxUiToolkitBridge.g.cs`).
