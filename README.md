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

Dalamud is normally loaded by [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) or a compatible launcher, which manages required runtime dependencies. To test Dalamud Plus, use the fork's injector as described below, or select the extracted build through your launcher's Dalamud directory override. Fork builds are updated manually; this repository does not provide an automatic launcher update channel.

The injection instructions below use XIVLauncher on Windows. The framework also runs inside the Windows x64 game process through Wine on Linux and macOS. On those hosts, select the fork build using your launcher's supported configuration within its Wine environment. The local validation recorded for the integration covers Windows builds and tests; in-game and Wine checks remain separate.

### Download a test build

Download `Dalamud-win-x64.zip` from the [Dalamud Plus releases](https://github.com/ConfoundedHermit/DalamudPlus/releases) page and extract it into a dedicated directory, separate from launcher-managed files.

The ZIP contains the Windows/x64 Release build, native libraries, managed dependencies, debug symbols, and `hashes.json`. `Dalamud.dll` and `Dalamud.Injector.exe` are at the ZIP root. The separate `Dalamud-win-x64.zip.sha256` asset provides the archive's SHA-256 checksum. These are unsigned test builds. The archive and DLL names retain their existing identities for launcher and plugin compatibility.

Keep the release tag or commit when reporting a problem. Fork release tags use `dalamud-plus/vYYYYMMDD`, with an optional same-day sequence suffix; the numeric Dalamud assembly version alone does not identify the fork release.

### Automatic injection (Windows/XIVLauncher)

1. Open XIVLauncher, go to **Settings > Dalamud**, and uncheck **Enable Dalamud**.
2. Go to **Auto-launch > Add new** and select `Dalamud.Injector.exe` in your extracted directory.
3. Start the game through XIVLauncher as normal and verify that Dalamud and your plugins load.

To return to launcher-managed Dalamud, remove or disable the injector's Auto-launch entry and re-enable **Enable Dalamud**.

### Manual injection (Windows/XIVLauncher)

Start the game through XIVLauncher using **Start w/o Dalamud** (right-click **Log in**). If you configured automatic injection above, disable that Auto-launch entry for a manual test. Once the game is running, open PowerShell in the extracted directory and run:

```powershell
.\Dalamud.Injector.exe inject -a
```

Verify that Dalamud and your plugins load. See the upstream [manual injection guide](https://dalamud.dev/building/#manual-injection) for additional details.

## For best experience (Windows 11)

When playing FFXIV in windowed or borderless windowed mode, enable **Optimizations for windowed games** in **Windows Settings > System > Display > Graphics**. Check the `ffxiv_dx11.exe` entry under **Custom settings for applications** (called **Custom options for apps** on some Windows versions) and ensure the setting is enabled for the game. Restart FFXIV after changing it.

This setting moves compatible DirectX 10/11 games to the modern flip presentation model, reducing presentation latency and enabling modern display features. See [Microsoft's windowed games optimization guide](https://support.microsoft.com/en-us/windows/hardware/display-graphics/optimizations-for-windowed-games-in-windows-11). Check this setting when testing Smooth Motion; if you encounter stuttering, latency spikes, or inconsistent frame delivery, include its state in your issue report.

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

## Reporting issues

Report problems using the [Dalamud Plus bug report form](https://github.com/ConfoundedHermit/DalamudPlus/issues/new?template=bug_report.yml), suggest improvements with the [feature request form](https://github.com/ConfoundedHermit/DalamudPlus/issues/new?template=feature_request.yml), or discuss changes in [Dalamud Plus issues](https://github.com/ConfoundedHermit/DalamudPlus/issues). For bug reports, include the fork release tag or commit, launcher, host OS, graphics setup, and reproduction steps. Fork support is maintained in this repository.

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
