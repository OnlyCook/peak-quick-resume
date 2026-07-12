## Requirements

- [BepInExPack PEAK](https://thunderstore.io/c/peak/p/BepInEx/BepInExPack_PEAK/) `5.4.2403`

No other mods are required. [PEAK Checkpoint Save](https://thunderstore.io/c/peak/p/dominik0207/PEAK_Checkpoint_Save/) by dominik0207 can still be installed alongside this mod without conflicting, but it isn't needed (see "Credit where it's due" above).

## For players

- You can install the mod through r2modman as `PEAK_Quick_Resume`,
- On [Thunderstore](https://thunderstore.io/c/peak/p/OnlyCook/PEAK_Quick_Resume/),
- Or on [Nexus Mods](https://www.nexusmods.com/peak/mods/189)

Achievement progress is saved and restored correctly when you load a checkpoint, but only for whoever has PEAK Quick Resume installed themselves; a co-op player without it keeps the old, unrestored behavior for their own achievements.

## For developers

- [`docs/INSTALL.md`](docs/INSTALL.md): reproducible dev setup + build.
- [`docs/TESTING.md`](docs/TESTING.md): build → deploy → test loop.
- [`docs/RESEARCH.md`](docs/RESEARCH.md): how PEAK's run-start / death flows and the
  checkpoint mod internals work (decompilation notes).
- [`ROADMAP.md`](ROADMAP.md): plan, status, deferred issues, handoff notes.

Build:
```bash
cd src/PeakQuickResume
dotnet build -c Release                         # -> bin/Release/PEAKQuickResume.dll
dotnet build -c Release -p:DeployToProfile=true # also copy into the r2modman profile
```
