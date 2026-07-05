# Changelog

## 1.0.0

Full release.

> If you ever had issues as the client (not the host) in co-op while loading a save (such as falling through the world or seeing an empty map), then this update should fix a lot of such issues.  
> **Note:** if you are updating from a **beta release** (v0.3.0 or older) then be sure to delete this `PEAK Quick Resume` mod and the underlying `PEAK Checkpoint Save` mod and then simply reinstall the `PEAK Quick Resume` mod (which will install the other automatically). This makes sure an outdated or changed config does not interfere with any functionality.

- **Detects and mitigates PEAK Checkpoint Save's occasional bad-teleport bug**:
  - **In co-op, a plain load now defaults to `teleportJumpLogic 1`** instead of PEAK Checkpoint Save's own base setting, since extensive testing found this avoids nearly every case of the bug above. New `enable-optimized-coop-loading` setting (on by default) controls this; hold **Shift** while loading to use your own base setting instead for just that one load, or **Alt** for `teleportJumpLogic 2`. Solo play is never affected.
- **F7 save picker redesign:** rebuilt as a real UGUI Canvas matching the game's own visual style, with a smoothly fading-in dimming overlay, and a one-time loading indicator on the very first open each session so the initial build doesn't cause a seemingly freeze.
- New `panel-opacity` config setting: lower it to see through the picker's background while it's open.
- New `resume-key-also-confirms-load` setting, and the resume key is now a real rebindable `KeyCode`, so PEAKLib.ModConfig's in-game menu can rebind it directly (click, press a key) instead of only via the config file.
- Localized everything in the picker and its messages across every language the game ships
- Fixed `Escape` bleeding through to also open the vanilla pause menu.
- Now the arrow keys can be held down to quickly traverse save entries on an interval.
- Panel is now restricted to only showing 10 save entries at a time. It introduces a scrolling mechanic with an arrow indicator, if there are more entries instead.
- Localized "PEAK Checkpoint Save"'s own "Savegame loading..." and "Savegame loaded!" messages.
- All config setting names now use hyphens as spaces, so they are actually readable (`ALLOW-MID-GAME` instead of `ALLOWMIDGAME`).
- "Board Flight" pause menu button now matches "Return to Airport"'s teal.
- F1 help screen rewritten into a real small menu matching the F7 picker's look.
- Added a bigger more noticeable version of the PEAK Checkpoint Save's island toggle button to the Boarding Pass.
- New `move-rebind-controls-to-settings` setting (off by default): moves the vanilla "Rebind Controls" button out of the pause menu into the Settings page (below the `Mod Settings` button), freeing up a row for this mod's and other mods' own pause-menu buttons.
- Removed the `require-double-press_DEPRECATED` / `double-press-window_DEPRECATED` settings (deprecated since 0.2.0, superseded by the picker's confirm step).

## 0.3.0
- **Miscellaneous QoL: pause menu buttons.** Three extra buttons are injected into the vanilla pause menu:
  - **Restart** (mid-run, host-only): returns everyone to the Airport and immediately starts a fresh run of the same difficulty, no checkpoint is loaded.
  - **Return to Airport** (mid-run, host-only): sends everyone back to the Airport without starting a new run.
  - **Board Flight** (at the Airport, any player): opens the gate-kiosk UI directly, skipping the walk over to it.
  - Each is independently toggleable in config (`PauseMenu` section) and confirms before acting on Restart/Return to Airport.
- **Localized** into every language the game ships (French, Italian, German, Spanish Spain/Latam, Portuguese Brazil, Russian, Ukrainian, Simplified Chinese, Japanese, Korean, Polish, Turkish), both the button labels and their confirm dialogs. Translations were done with AI, if one reads wrong in your language, please open a GitHub issue.
- Appended `_DEPRECATED` to `requireDoublePress` / `doublePressWindow` config keys in settings. 

## 0.2.0
- **In-game save picker (F7):** browse all your checkpoints (newest first) and load any of them from anywhere. Arrow keys to select, F7/Enter to load, Del to delete (twice to confirm), Esc to close.
- **Multiple saves per difficulty:** checkpoints are now archived instead of overwritten, so older campfires are kept and can be revisited (even mid-run, without losing the run's later saves). PEAK Checkpoint Save's own files are untouched.
- **Custom-run support:** custom difficulty runs can now be resumed, not just the standard difficulties.
- Pressing **F7 twice still loads your latest checkpoint**, exactly as before.
- `requireDoublePress` / `doublePressWindow` config are deprecated (superseded by the picker's confirm step).

## 0.1.1
- Description and README updated.

## 0.1.0
Initial release.

- Press **F7** (twice to confirm) to start a fresh run of your saved difficulty and automatically load your **PEAK Checkpoint Save** checkpoint.
- Works after death, from the Airport, and mid-run.
- Full co-op support (host-driven): all players are returned to the Airport, brought into the fresh run, and restored together.
- On-screen prompts, configurable resume key, double-press confirmation, and advanced timing options.

