# Dalamud Plus

<p align="center">
  <img src="https://raw.githubusercontent.com/goatcorp/DalamudAssets/master/UIRes/logo.png" alt="Dalamud" width="200"/>
</p>

Dalamud Plus is an independently maintained soft fork of [goatcorp/Dalamud](https://github.com/goatcorp/Dalamud), the FFXIV plugin framework. It follows upstream development while maintaining Smooth Motion compatibility, fixes, and other improvements in this repository.

The project repository is [ConfoundedHermit/DalamudPlus](https://github.com/ConfoundedHermit/DalamudPlus).

## Branches and maintenance

- `main` contains fork development, upstream integration, and releases.
- `master` follows upstream `goatcorp/master` without fork commits.
- Existing feature and backup branches preserve their development history.

Upstream changes are merged into `main` through the clean `master` branch. Fork changes are maintained here, with no routine PR submission back to upstream. See [fork maintenance](docs/FORK-MAINTENANCE.md) for upstream updates and the release procedure.

## Running the fork

Dalamud is normally loaded by [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) or a compatible launcher, which manages required runtime dependencies. Using Dalamud Plus requires explicitly selecting a fork build through the launcher's Dalamud directory override.

Fork release bundles retain the `Dalamud-win-x64.zip` filename and existing DLL names for launcher and plugin compatibility. The framework runs inside the Windows x64 game process, including through Wine on Linux and macOS. The local validation recorded for the integration covers Windows builds and tests; in-game and Wine checks remain separate.

## Building and testing locally

The full build uses Windows, Visual Studio 2026 with the v145 C++ toolset and Windows SDK, CMake 4.2 or newer, and .NET 10. Run each command from the repository root and stop if it fails:

```powershell
git submodule update --init --recursive
.\generate.cmd vs2026
.\build.cmd Release
dotnet test Dalamud.Test/Dalamud.Test.csproj -c Release -tl:off -p:WarningLevel=0
```

The [upstream build documentation](https://dalamud.dev/building) provides additional background. Runtime assembly names, namespaces, plugin APIs, configuration paths, and upstream dependency services retain their existing identities.

## Plugin development
Dalamud features a growing API for in-game plugin development with game data and chat access and overlays.
The [upstream Developer FAQ](https://goatcorp.github.io/faq/development) and [API documentation](https://dalamud.dev) describe the shared plugin API.

Report fork issues and discuss changes in [Dalamud Plus issues](https://github.com/ConfoundedHermit/DalamudPlus/issues). Include the fork commit, launcher, host OS, graphics setup, and reproduction steps. Fork support is maintained in this repository.

<br>

Thanks to Mino, whose work has made this possible!

## Components & Pipeline

These components are used in order to load Dalamud into a target process.
Dalamud can be loaded via DLL injection, or by rewriting a process' entrypoint.

| Name                          | Purpose                                                                                                                      |
|-------------------------------|------------------------------------------------------------------------------------------------------------------------------|
| *Dalamud.Injector* (C#)       | Performs DLL injection on the target process                                                                                 |
| *Dalamud.Boot* (C++)          | Loads the .NET Core runtime into the active process and kicks off Dalamud, or rewrites a target process' entrypoint to do so |
| *Dalamud* (C#)                | Core API, game bindings, plugin framework                                                                                    |
| *Dalamud.CorePlugin* (C#)     | Testbed plugin that can access Dalamud internals, to prototype new Dalamud features                                          |

## Contributions

AI-assisted contributions are welcome in Dalamud Plus. Contributors are responsible for understanding their changes, keeping them focused, and providing appropriate validation. Changes should describe the problem, resulting behavior, and tests or runtime checks performed.

## Credits and license

Dalamud Plus builds on the work of the Dalamud contributors and its dependencies. The existing [AGPL license](LICENSE) and third-party notices are retained. The upstream logo above credits the original project.

<br>

##### Final Fantasy XIV © 2010-2021 SQUARE ENIX CO., LTD. All Rights Reserved. We are not affiliated with SQUARE ENIX CO., LTD. in any way.
