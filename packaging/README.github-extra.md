## Requirements

- [BepInExPack PEAK](https://thunderstore.io/c/peak/p/BepInEx/BepInExPack_PEAK/) `5.4.2403`

No other mods are required. [PEAK Checkpoint Save](https://thunderstore.io/c/peak/p/dominik0207/PEAK_Checkpoint_Save/) by dominik0207 can still be installed alongside this mod without conflicting, but it isn't needed (see "Credit where it's due" above).

## For players

- You can install the mod through r2modman as `PEAK_Quick_Resume`,
- On [Thunderstore](https://thunderstore.io/c/peak/p/OnlyCook/PEAK_Quick_Resume/),
- Or on [Nexus Mods](https://www.nexusmods.com/peak/mods/189)

## For developers

Build:
```bash
cd src/PeakQuickResume
dotnet build -c Release                         # -> bin/Release/PEAKQuickResume.dll
dotnet build -c Release -p:DeployToProfile=true # also copy into the r2modman profile
```
