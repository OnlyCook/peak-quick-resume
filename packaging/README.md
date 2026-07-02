# PEAK Quick Resume

> **Beta (v0.2.0).** Fully working in solo and co-op (tested with up to 2 players);
> larger lobbies should work but aren't verified yet. Feedback welcome!

**Press one key to browse your campfires and jump straight back to any of them.**

Tired of the long ritual to load a [PEAK Checkpoint Save](https://thunderstore.io/c/peak/p/dominik0207/PEAK_Checkpoint_Save/): die, restart, walk to and open the gate kiosk, pick the difficulty, load in, then press the load key twice? This mod collapses all of that into a single key and adds an in-game menu so you can pick **which** checkpoint to load.

Press **F7** anywhere and a **save picker** opens listing all your checkpoints for the current mode. Choose one with the arrow keys and press **F7** (or Enter) and it will:
1. Start a **fresh run** of that save's difficulty, then
2. Automatically **load that checkpoint**. So you're back at that campfire with your items and progress.

The newest save is preselected, so **pressing F7 twice still loads your latest checkpoint** just like before.

<img width="1920" height="1080" alt="Screenshot_20260702_134149" src="https://github.com/user-attachments/assets/e5d81f68-731b-44d3-b6f9-27fc588ee10e" />

Works **after you die**, **from the Airport**, or **mid-run** (so no need to die first). Full **co-op** support: the host presses F7 and everyone is brought along and restored.

Because it always spins up a fresh run instance before loading, you can resume as many times as you like without having the problem of loading twice (through F6) and breaking the game.

---

## ⚠️ Requires PEAK Checkpoint Save

This mod does **not** save or load anything by itself, it automates **[PEAK Checkpoint Save](https://thunderstore.io/c/peak/p/dominik0207/PEAK_Checkpoint_Save/)** by **dominik0207**, which does all the real work. Huge thanks to them; that mod is a hard dependency and will be installed automatically.

The campfire **saving** is still handled entirely by PEAK Checkpoint Save (light a campfire to save). Quick Resume archives each of those saves (now you can have multiple saves per difficulty), makes **loading** effortless from anywhere in-game, and lets you pick which one to load.

## How to use

1. Play as normal. Light a campfire to save (that's PEAK Checkpoint Save doing its thing). Each save is added to your history (and is never overwritten/deleted automatically).
2. Press **F7** to open the save picker.
3. Use **↑/↓** to highlight a checkpoint, then press **F7** again (or Enter) to load it. Just want the latest? Press **F7** twice.
4. Press **Del** to delete the highlighted save (press it twice to confirm), or **Esc** to close the menu.

In co-op, only the **host** can use F7 and everyone is restored together.

## Configuration

Config file: `BepInEx/config/OnlyCook.PEAKQuickResume.cfg`

- **resumeKey**: the key to open the picker / load the highlighted save (default **F7**).
- **allowMidGame**: allow resuming while still alive (default on).
- **Timing**: advanced settle/timeout values; raise `coopAirportSettle` if a client occasionally gets left behind on a slow connection.
- *(requireDoublePress / doublePressWindow are deprecated. The picker now provides the confirmation step. Kept for compatibility reasons.)*

Your archived checkpoints live in `BepInEx/plugins/QuickResume/Archive/` (split into `Offline/` and `Coop/`). PEAK Checkpoint Save's own files are never modified.

## Notes

- **Host-only** in co-op (just like PEAK Checkpoint Save). Everyone should have PEAK Checkpoint Save installed.
- Loading a checkpoint can grant Steam achievements (a property of the underlying mod). Don't use it if you want to earn everything unassisted.
- Custom runs are resumed with your *current* custom settings (the checkpoint file doesn't store the run's original settings).
- If a client ever glitches after loading, that's the underlying teleport in PEAK Checkpoint Save. Try its `teleportJumpLogic` config (1 or 2) or its manual F9 teleport.

## Feedback & bug reports

Found a bug or have a suggestion? Please **[open an issue on GitHub](https://github.com/OnlyCook/peak-quick-resume/issues/new)**. It helps a lot to include:

- what you did and what happened (solo or co-op; if co-op, whether you were the host),
- your **`LogOutput.log`** (Quick Resume logs every step of a resume, this file is what makes bugs fixable).

### Where is `LogOutput.log`?

BepInEx rewrites this file every time you launch the game, so **reproduce the bug, then quit and grab the file** before playing again.

#### r2modman / Thunderstore Manager:
- Open the manager, pick PEAK, choose your profile, then go to **Settings** and search for the following setting: `Copy log file contents to clipboard`, click on that setting and paste it in your GitHub Issue.  

#### Manual install?
- Find out where your `LogOutput.log` file is on your OS. You can for example use [**Everything**](https://www.voidtools.com/downloads/) on Windows or use this command `find ~ -name "LogOutput.log"` on Linux/Mac.
