# Acrux.Sdk and Acrux.Templates

What client mods use, released on GitHub (no NuGet server), every file signed with the Acrux release key:

| File | What it is |
|------|------------|
| `Acrux.Sdk.<version>.nupkg` | The SDK package a mod references. |
| `Acrux.Templates.<version>.nupkg` | `dotnet new acrux-mod`: a new mod, with that SDK package inside. |
| `AcruxPreview.<version>.jar` | The Rider preview plugin (also inside both packages). |
| `*.sig` | RSA-SHA256 signature of each file. |

`Acrux.Sdk` contains:

- `analyzers/dotnet/cs/`: `Acrux.Generator.dll`, `Adk.Analyzer.dll` and `Acrux.ClientApi.Generator.dll` (compile
  time only). The last one carries ClientApi (wrappers, bridge runtime, shared types) and adds it to the mod as
  generated source, because Space Engineers only loads mod source. MDK packs generated source under `MdkGenerated/`,
  so the packed mod never depends on where NuGet extracted the package.
- `build/`: the properties, additional files and view items the generators read, and the preview settings.
- `tools/preview/`: the preview host. It is published without any game file (the build refuses one), finds the user's
  Space Engineers through Steam or `ACRUX_SE_BIN`, and carries the toolkit's styles and content. It needs the .NET 9
  (or later) runtime.
- `tools/rider/acrux-preview.jar`: the Rider plugin, built against the local Rider install.

## Building

Like MDK's template project, the packages build with the solution: building `AcruxUiToolkit.sln` in Rider (or
`dotnet build Tools/Acrux.Templates`) runs the whole chain into `Tools/Acrux.Sdk/out/`, no script involved:

1. the generators (always Release: Debug turns on EXPERIMENTAL features in Acrux.Generator), the preview host
   (published) and the Rider plugin (`AcruxRiderPlugin/build.sh`, needs Rider at `$RIDER_HOME` or `/opt/rider`);
2. `Acrux.Sdk.<version>.nupkg`, signed;
3. `Acrux.Templates.<version>.nupkg`: `../Acrux.Templates/content/` with the update scripts, the signed SDK package,
   its version in the mod's `.csproj` and the plugin jar, signed. The build checks that the public key in the update
   scripts matches the signing key.

A Debug or Release build makes a development version, `<Version>-dev.<UTC timestamp>`, new on every build (older
ones in `out/` are removed), so NuGet never reuses a stale extracted copy and GitHub's latest release never points at
one. The solution's **Publish** configuration makes the release (see Releasing).
`<Version>` itself stays numeric: the toolkit's generated constants parse it as `System.Version`.

## Signing

The key is `$ACRUX_SDK_SIGNING_KEY` or `~/.config/acrux/acrux-sdk-signing.pem`. Without it a development SDK package
is unsigned (update-acrux refuses it, and the template build stops); a release build fails. The private key never goes
into a repository; keep a backup, because a lost key means every client must replace its update scripts. Its public
key is built into `template/update-acrux.sh` (PEM) and `template/update-acrux.ps1` (.NET XML).

## Releasing

1. Set `<Version>` in `AcruxUiToolkit/Directory.Build.props` (numeric; bump it right after a release, so the next
   development builds are prereleases of the next version).
2. In Rider, pick the **Publish** solution configuration and build the solution (command line:
   `dotnet build AcruxUiToolkit.sln -c Publish`). It builds the toolkit mod as Release (deploying it like any
   Release build), runs the bridge harness (an API break stops the build, see the main README's API stability),
   records the shipped API in `ClientApi/api.shipped.txt`, and packs exactly `<Version>`; a version that was built
   before is refused (restore keeps the first copy of a version it extracted, so a version is never built twice).
3. Commit `ClientApi/api.shipped.txt`, create the GitHub release `v<version>` and upload the six files from `out/`.

## Client mods

New mod:

    dotnet new install Acrux.Templates.<version>.nupkg
    dotnet new acrux-mod -n MyMod [--title "My Mod"]

It renames everything after the mod, gives it its own random mod-message channels, and builds right away (the SDK
package is in `packages/`, the Rider plugin in `tools/`).

Existing mod: copy `template/` (`nuget.config`, `Directory.Build.targets`, `update-acrux.sh`, `update-acrux.ps1`,
`update-acrux.bat`) next to the solution, run `./update-acrux.sh` (or `update-acrux.bat`), and reference the package:

```xml
<PackageReference Include="Acrux.Sdk" Version="0.0.1" PrivateAssets="all" />
```

The update scripts take an optional version (default: the latest release) and bump that reference; a development
build installs with `ACRUX_SDK_URL=file://<this folder>/out ./update-acrux.sh <dev version>`. Commit
`packages/Acrux.Sdk.<version>.nupkg` with the mod, so a fresh clone builds without running the script.
