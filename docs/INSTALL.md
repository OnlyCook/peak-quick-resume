# Dev environment setup (reproducible)

What was installed on the dev machine (Fedora/Arch Linux, Steam + Proton) to build
this mod. Times/paths are for the current developer; adjust for another machine.

## Prerequisites

| Tool | Version used | Install |
|---|---|---|
| .NET SDK | 10.0.109 | `dotnet` (distro package or https://dotnet.microsoft.com) |
| ilspycmd (decompiler, optional — only for research) | 10.1.0 | `dotnet tool install --global ilspycmd` |
| git / gh | 2.54 | distro package |
| PEAK (Steam) | 1.64.a | Steam, run once via Proton Experimental |
| BepInExPack PEAK | 5.4.2403 | installed via r2modman (see below) |
| PEAK Checkpoint Save (dependency) | 0.4.7 | installed via r2modman |

`ilspycmd` puts binaries in `~/.dotnet/tools` — make sure that's on `PATH`:
```bash
export PATH="$PATH:$HOME/.dotnet/tools"
```

## Reference assemblies (no vendoring needed)

The build references DLLs straight from the game install and the r2modman profile —
nothing is copied into the repo. Defaults (see
`src/PeakQuickResume/Directory.Build.props`):

- **Game managed dir:** `~/.local/share/Steam/steamapps/common/PEAK/PEAK_Data/Managed`
- **BepInEx core dir:** `~/.config/r2modmanPlus-local/PEAK/profiles/Default/BepInEx/core`

On a different machine, override without editing tracked files — create
`src/PeakQuickResume/Directory.Build.props.local` (git-ignored):

```xml
<Project>
  <PropertyGroup>
    <GameManagedDir>/path/to/PEAK/PEAK_Data/Managed</GameManagedDir>
    <BepInExCoreDir>/path/to/r2modman/PEAK/profiles/Default/BepInEx/core</BepInExCoreDir>
    <ProfilePluginsDir>/path/to/r2modman/PEAK/profiles/Default/BepInEx/plugins</ProfilePluginsDir>
  </PropertyGroup>
</Project>
```
…or pass on the CLI: `dotnet build -p:GameManagedDir=/path ...`.

## r2modman / mod manager layout (for reference)

- AppImage: `~/Downloads/r2modman-3.2.18.AppImage`
- Profile root: `~/.config/r2modmanPlus-local/PEAK/profiles/Default`
- Mods live in `…/Default/BepInEx/plugins/<Author>-<Mod>/`
- The game is launched **through r2modman** ("Start modded"), which injects BepInEx
  via `winhttp.dll` + doorstop.

## Build

```bash
cd src/PeakQuickResume
dotnet build -c Release
# output: bin/Release/PEAKQuickResume.dll
```

See `docs/TESTING.md` for deploying into the game and testing.
```
