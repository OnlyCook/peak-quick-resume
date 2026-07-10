## Requirements

- [BepInExPack PEAK](https://thunderstore.io/c/peak/p/BepInEx/BepInExPack_PEAK/) `5.4.2403`

No other mods are required. [PEAK Checkpoint Save](https://thunderstore.io/c/peak/p/dominik0207/PEAK_Checkpoint_Save/) by dominik0207 can still be installed alongside this mod without conflicting, but it isn't needed (see "Credit where it's due" above).

## For players

- You can install the mod through r2modman as `PEAK_Quick_Resume`
- Or on Thunderstore as `PEAK Quick Resume` ([Website](https://thunderstore.io/c/peak/p/OnlyCook/PEAK_Quick_Resume/))

⚠️ Loading a checkpoint save can grant Steam achievements. Don't use it if you want to earn everything unassisted.

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
