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

Quick Resume writes every save here — handy for confirming a save exists / which
difficulty (ascent) it is / deleting a save to test the no-save path:

```
~/.config/r2modmanPlus-local/PEAK/profiles/Default/BepInEx/plugins/QuickResume/Archive/
```

This is the one and only save store: saves are written straight into it and loaded
straight back out of it, so there is no separate "current save" file anywhere. The old
`plugins/Checkpoint_Save/` folder (PEAK Checkpoint Save's own) is never read or written —
if that mod is still installed, its files sit there untouched and invisible to us.

- Offline (solo): `Offline/peak_save_{ascent}_offline__{stamp}.json`
  (e.g. Tenderfoot = `Offline/peak_save_-1_offline__20260725_140311_204.json`)
- Co-op: `Coop/peak_save_{ascent}_{steamUserId}__{stamp}.json`
- Custom runs: same, with `CustomRun` in place of the ascent number

`{stamp}` (`yyyyMMdd_HHmmss_fff`, UTC) identifies the **save event**, not the file: one
autosave generates a single stamp and writes it into every participating player's
filename. That's what makes a co-op event's files findable as one group, and it's why
editing a save's contents is safe — a file's identity is in its name, never in its
modification time.

In co-op, only the **host's** file carries the level/world half of the save (which island
and segment, the teleport position, time of day, day counter, ground items, luggage, the
ancient statue, deployables). A client's file carries only that client's own state
(inventory, backpack, held item, afflictions, extra stamina, skeleton flag, thorns, ticks,
achievement progress). A client file therefore has no `sceneName` — that's the quickest
way to tell the two apart by eye, and it's how the F7 picker identifies the host's file.

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
4. **Coop alive/dead restore (joined-late client):** host starts a run **alone** and lights a
   campfire (the save event then contains only the host's file), then invites a friend who
   joins mid-run — PEAK kills a joining player on arrival, so they spawn as a spectating
   ghost. Host presses **F7** and loads that save.
   - Expect: the client is **alive** at the campfire, not spectating. Host log:
     `OwnTeleportSequence.ReviveDeadPlayers: revived <name> (networked).` and
     `DeathStateRestore: '<userId>' has no file in this checkpoint's save event … restoring them alive.`
5. **Coop alive/dead restore (someone really died):** host + client in a run, the **client**
   dies, then the host lights a campfire and saves with the client dead. Load that save.
   - Expect: the client is warped to the campfire with everyone else but is **already
     spectating when their loading screen clears** - they should never be visible standing up
     and then dropping dead. Host log: `OwnInventoryRestore: skipping the per-player restore
     for '<userId>' …` followed by `DeathStateRestore: restored <name> as dead …`, both
     *before* the fade-out. No "teleport bug detected" hint on the client's screen. Setting
     `restore-death-state = false` in the config should instead bring them back alive with
     their save restored as normal.
6. **Coop save taken while the HOST is dead:** host dies, then the **client** lights a campfire
   (that RPCs the save request to the host). Load that save.
   - Expect: a normal load at that campfire, with the host restored dead. The host's log must
     show `OwnSaveCapture: the host is dead … anchoring this save's world state on a living
     player instead` at **save** time, and a `Pos:` that is a real map position, never
     `(0, 5000, -5000)` (PEAK's off-map death zone). A save written by an older build with
     that death-zone position logs `death-zone checkpoint retargeted to the <segment>
     campfire` on load and should land everyone at the campfire instead of in empty space.
7. ❓Report per case: did **all** clients reload the fresh instance and get restored?
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
