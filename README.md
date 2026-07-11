<!-- GENERATED FILE - do not edit by hand.
     Source: packaging/README.md + packaging/README.github-extra.md
     Regenerate with: bash packaging/gen-readme.sh -->

# PEAK Quick Resume

**Automatically saves when you light a campfire. Press F7 to browse your campfires and jump straight back to any of them.**

Press **F7** anywhere and a **save picker** opens listing all your checkpoints for the current mode. Choose one with the arrow keys and press **F7** (or Enter) and it will:
1. Start a **fresh run** of that save's difficulty, then
2. Automatically **load that checkpoint**. So you're back at that campfire with your items and progress.

The newest save is preselected, so **pressing F7 twice will load your latest checkpoint**.

<img width="1920" height="1080" alt="new-pause-menu-btns" src="https://raw.githubusercontent.com/OnlyCook/peak-quick-resume/refs/heads/main/packaging/in-game-menu-screenshot.png" />

Works **after you die**, **from the Airport**, or **mid-run** (so no need to die first). Full **co-op** support: the host presses F7 and everyone is brought along and restored.

> The mod also adds a few **optional** Quality of Life pause menu buttons (more details at the bottom of this README).

---

## Credit where it's due

This mod started as an automation layer on top of [PEAK Checkpoint Save](https://thunderstore.io/c/peak/p/dominik0207/PEAK_Checkpoint_Save/) by **dominik0207**, and its save file format is still based directly on that mod's own. As of version 2.0.0, Quick Resume has its own independent save/load/teleport implementation and **no longer requires PEAK Checkpoint Save to be installed**. Huge thanks to dominik0207, this mod wouldn't exist without their original work.

## How to use

1. Play as normal. Lighting a campfire saves automatically. Each save is added to your history.
2. Press **F7** to open the save picker.
3. Use **↑/↓** to highlight a checkpoint, then press **F7** again (or Enter) to load it. Just want the latest? Press **F7** twice.  
  
- Press **Del** to delete the highlighted save (press it twice to confirm).  
- Press **B** to star the highlighted save (press **B** again to unstar it). Starred saves are moved to the top and can't be deleted until unstarred again.
- Press **Esc** to close the menu.  

In co-op, only the **host** can use F7 and everyone is restored together.

## Configuration

<details>

<summary><b>View config information</b></summary>

Config file: `BepInEx/config/OnlyCook.PEAKQuickResume.cfg`. If you have [PEAKLib.ModConfig](https://thunderstore.io/c/peak/p/PEAKModding/ModConfig/) installed, every setting below is also editable in the game's settings under **Mod Settings → PEAK Quick Resume**, no need to touch the config file by hand.

- **resume-key**: the key to open the picker / load the highlighted save (default **F7**). Rebindable directly in ModConfig's menu.
- **resume-key-also-confirms-load**: if disabled, pressing the resume key while the picker is open does nothing, only Enter confirms a load (useful if you keep accidentally reloading while trying to close the picker).
- **help-key**: the key to open the help menu (default **F2**).
- **allow-mid-game**: allow resuming while still alive (default on).
- **panel-opacity**: how see-through the picker's background panel is (0 = fully see-through; 1 = fully opaque, default).
- **Pause-Menu**: disable/re-enable any of the 3 added QoL pause menu buttons, and optionally enable `move-rebind-controls-to-settings` to move the 'Rebind Controls' button away from the pause menu.
- **Timing**: advanced settle/timeout values; raise `coop-airport-settle` if a client occasionally gets left behind on a slow connection.
- **Teleport**: advanced settings for the restore itself (inventory/afflictions/campfire reset/time of day toggles, retry timing). Rarely needs touching.
- **Network**: advanced co-op networking settings, currently just the readiness-check toggle used before a co-op load starts.
- **Teleport-Mitigation**: detects and helps recover from occasional teleport glitching after a load (brief up/down snapping, or rarely falling through the world). Everything here is on by default and rarely needs touching; thresholds and delays are all individually configurable if you want to tune them.
- **Debug**: verbose logging of every step of the resume sequence, on by default. Please keep this on when reporting issues.

</details>

Your saved checkpoints live in `BepInEx/plugins/QuickResume/Archive/` (split into `Offline/` and `Coop/`).

## Notes

- **Host-only** in co-op. It works fine if only the host has PEAK Quick Resume installed, but it's recommended that everyone has it installed for compatibility reasons.
- Achievement progress (Plunderer, Foraging, Mycology, Knot Tying, First Aid, Clutch, Gourmand, the "without ever X" badges, and more) is saved and restored correctly when you load a checkpoint - and teleporting no longer inflates the High Altitude Badge's climbed-height total. This only applies per-player to whoever has PEAK Quick Resume installed themselves; a co-op player without it keeps the old, unrestored behavior for their own achievements.
- Custom runs are resumed with your *current* custom settings (the checkpoint file doesn't store the run's original settings).
- Translations were done by AI, so if something is off in your language you are free to open a GitHub Issue (see below).

## Feedback & bug reports

Found a bug or have a suggestion? Please **[open an issue on GitHub](https://github.com/OnlyCook/peak-quick-resume/issues/new)** or send me an email at `theactualcooker@gmail.com`. It helps a lot to include:

- what you did and what happened (solo or co-op; if co-op, whether you were the host),
- your **`LogOutput.log`** (Quick Resume logs every step of a resume, this file is what makes bugs fixable).

### Where is `LogOutput.log`?

BepInEx rewrites this file every time you launch the game, so **reproduce the bug, then quit and grab the file** before playing again.

#### r2modman / Thunderstore Manager:
- Open the manager, pick PEAK, choose your profile, then go to **Settings** and search for the following setting: `Copy log file contents to clipboard`, click on that setting and paste it in your GitHub Issue.  

#### Manual install?
- Find out where your `LogOutput.log` file is on your OS. You can for example use [**Everything**](https://www.voidtools.com/downloads/) on Windows or use this command `find ~ -name "LogOutput.log"` on Linux/Mac.

## Miscellaneous Pause Menu Buttons

<img width="1920" height="1080" alt="new-pause-menu-btns" src="https://raw.githubusercontent.com/OnlyCook/peak-quick-resume/refs/heads/main/packaging/new-pause-menu-btns.png" />

- **Board Flight** (at the Airport): Opens the gate kiosk directly without having to walk over.
- **Restart** and **Return to Airport** (mid-run, host-only): Both buttons do the advertised action and apply it to all clients.

You can disable/hide any of these buttons through 'Mod Settings' (on the `PEAK Quick Resume` tab, under `Pause-Menu`)

> If you have too many pause menu buttons because of this, you can enable the `move-rebind-controls-to-settings` setting (also under `Pause-Menu`). This will move the 'Rebind Controls' button from the pause menu to the 'Settings' page, below the 'Back' (or if applicable 'Mod Settings') button.

## Requirements

- [BepInExPack PEAK](https://thunderstore.io/c/peak/p/BepInEx/BepInExPack_PEAK/) `5.4.2403`

No other mods are required. [PEAK Checkpoint Save](https://thunderstore.io/c/peak/p/dominik0207/PEAK_Checkpoint_Save/) by dominik0207 can still be installed alongside this mod without conflicting, but it isn't needed (see "Credit where it's due" above).

## For players

- You can install the mod through r2modman as `PEAK_Quick_Resume`
- Or on Thunderstore as `PEAK Quick Resume` ([Website](https://thunderstore.io/c/peak/p/OnlyCook/PEAK_Quick_Resume/))

Achievement progress is saved and restored correctly when you load a checkpoint (this also fixes teleporting inflating the High Altitude Badge's climbed-height total) - but only for whoever has PEAK Quick Resume installed themselves; a co-op player without it keeps the old, unrestored behavior for their own achievements.

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
