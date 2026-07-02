# Testing guide

This doc is the loop:
build → deploy → run → collect logs → report back.

## Build & deploy in one command

```bash
cd src/PeakQuickResume
dotnet build -c Release -p:DeployToProfile=true
```

This compiles and copies `PEAKQuickResume.dll` into:
`~/.config/r2modmanPlus-local/PEAK/profiles/Default/BepInEx/plugins/OnlyCook-PEAKQuickResume/`

(If you use a different r2modman profile, override `ProfilePluginsDir` — see INSTALL.md.)

Then in **r2modman**: make sure *BepInExPack PEAK* and *PEAK Checkpoint Save* are
installed & enabled in the same profile, and click **Start modded**.

> Manual alternative: copy `bin/Release/PEAKQuickResume.dll` into a new folder
> `BepInEx/plugins/OnlyCook-PEAKQuickResume/` in the profile yourself.

## Where the logs are

- **In-game BepInEx console** (if enabled in the BepInEx config), or
- `…/profiles/Default/BepInEx/LogOutput.log`

Filter for our lines (all prefixed by the plugin name/logger):
```bash
grep -iE "PEAKQuickResume|Quick Resume|Checkpoint interop" \
  ~/.config/r2modmanPlus-local/PEAK/profiles/Default/BepInEx/LogOutput.log
```
Please send that snippet after each test.

## Where the save files are

The checkpoint mod (PEAK Checkpoint Save) writes its saves here — handy for confirming a
save exists / which difficulty (ascent) it is / deleting a save to test the no-save path:

```
~/.config/r2modmanPlus-local/PEAK/profiles/Default/BepInEx/plugins/Checkpoint_Save/
```

- Offline (solo): `peak_save_{ascent}_offline.json` (e.g. Tenderfoot = `peak_save_-1_offline.json`)
- Co-op: `Coop/peak_save_{ascent}_{steamUserId}.json`
- Custom runs: `peak_save_CustomRun_offline.json` / `Coop/peak_save_CustomRun_{userId}.json`

The checkpoint mod's own debug log (enable `enableLogging` in its config) lives in
`Checkpoint_Save/Logs/Debug_latest.log`.

## Test checklist (mirrors ROADMAP)

### §1 Smoke test (T1)
1. Start modded game, reach the main menu.
2. In the log you should see:
   `PEAK Quick Resume 0.2.0 loaded. Resume key: F7. Checkpoint interop: READY`
   and a `Checkpoint interop probe:` block with all lines `OK`.
3. ❓Report: does it say `READY`? Any `MISSING`?

### §2 Offline post-death happy path (T2 — the big one)
Pre-req: play an **offline** (single-player) run at a known difficulty, **light a
campfire** (creates the checkpoint save), then **die**.
1. On the death/end screen (or after it returns you to the Airport), press **F7**.
   Log should say `Resume armed. Press F7 again…`.
2. Press **F7** again within 5s. Log: `Resume confirmed — starting sequence.` then
   `=== Quick Resume: sequence START ===`.
3. Watch it walk the stages (return to Airport → start run → wait for level →
   trigger restore) ending in `sequence COMPLETE`.
4. ❓Report: Did it load a fresh run at the saved location with your inventory/state?
   Paste the full `Quick Resume` log block. If it aborted, the reason is logged.

### §3 Timing tuning (T3)
If the restore fires too early (e.g. teleport fails, missing character) or too late,
adjust in the config file
`…/BepInEx/config/OnlyCook.PEAKQuickResume.cfg` (created after first run):
- `Timing.settleAfterLevel` (default 1.5) — raise if load fires before the level is ready.
- `Timing.stepTimeout` (default 30) — raise if a stage times out on a slow load.
No rebuild needed; just edit and relaunch.

### §4 Already-at-Airport (T4)
Stand in the Airport with a valid save, press F7 twice → should start the run and load.

### §5 No-save guard (T5)
Delete/never-create a save for a difficulty, press F7 twice → clean abort:
`Quick Resume aborted: No checkpoint save found for ascent N …`. No crash, no restart.

### §6 Mid-game (Phase 2 — only if `allowMidGame=true`)
While **alive** in a level, press F7 twice. ⚠️ Uses an unvalidated return-to-Airport
path; expect this to be the rough edge. Report what happens.

### §7 Coop (host + at least one other player)
The mod is **host-only** — only the host installs-and-drives it (clients don't need it,
though the underlying checkpoint mod does need to be installed by everyone for its own
restore to work; keep both on all machines to be safe).

1. **Client guard:** on a non-host machine (if it has the mod), press F7 → should show
   "Only the host can resume the save!" and do nothing.
2. **Coop post-death:** host + client play a coop run, light a campfire (writes
   `Coop/peak_save_{ascent}_{userId}.json` for each), then everyone dies to the
   end/death screen. **Host** presses **F7 twice**.
   - Expect: everyone returns to the Airport → a fresh run starts for everyone → host's
     log shows `[stage] Coop: waiting for all clients to report ready...` →
     `all clients ready` → `COMPLETE`, and all players are restored at the campfire.
3. **Coop mid-game:** host presses F7 twice while alive → same, via the networked
   Airport return.
4. ❓Report per case: did **all** clients reload the fresh instance and get restored?
   Paste the **host's** `Quick Resume / [stage] / [savescan]` log block. If a client
   glitched through the map after load, note it (likely an upstream checkpoint-mod
   teleport quirk — try its `teleportJumpLogic` config = 1 or 2).

## Reporting template (paste back to the coder)

```
Test: §2 offline post-death
Difficulty/ascent: 0
Result: <worked / failed / partial>
What happened in-game: <one or two lines>
Log block:
<paste the PEAKQuickResume / Quick Resume lines>
```
