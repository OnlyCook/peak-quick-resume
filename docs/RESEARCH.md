# Research notes — how PEAK, the checkpoint mod, and our resume flow fit together

Everything here was reverse-engineered from decompiled assemblies for **PEAK 1.64.a**
and **PEAK Checkpoint Save 0.4.7**. When the game or the checkpoint mod updates,
re-run the decompile steps below and diff against these notes — the goal of this
document is that a future session can re-orient in minutes.

## How to re-decompile (reproducible)

Tooling: `dotnet` (10.x here) + `ilspycmd` global tool.

```bash
dotnet tool install --global ilspycmd     # once
export PATH="$PATH:$HOME/.dotnet/tools"

GAME="$HOME/.local/share/Steam/steamapps/common/PEAK/PEAK_Data/Managed"

# A whole plugin DLL:
ilspycmd path/to/PEAK_Checkpoint_Save.dll -o out/

# A single game type (fast, targeted):
ilspycmd "$GAME/Assembly-CSharp.dll" -t AirportCheckInKiosk
```

The checkpoint mod ships only a compiled DLL (no source in the zip), but its GitHub
is https://github.com/dominik0207/peak_checkpoint_save — check there first, the
decompile is the fallback.

## Vanilla: what "start a run" actually does

Chain, top to bottom (all in `Assembly-CSharp.dll`, global namespace unless noted):

```
BoardingPass.StartGame()                       // the boarding-pass "Start" button
  -> kiosk.StartGame(ascentIndex)              // kiosk : AirportCheckInKiosk (public)
       -> photonView.RPC("LoadIslandMaster", MasterClient, ascent, runSettings)
            LoadIslandMaster:
              sceneName = MapBaker.GetLevel(nextLevelIndex + offset)   // <-- patched by checkpoint mod
              -> photonView.RPC("BeginIslandLoadRPC", All, sceneName, ascent, runSettings)
                   BeginIslandLoadRPC:
                     Ascents.currentAscent = ascent
                     GameUtils.ApplySerializedRunSettings(runSettings)
                     LoadingScreenHandler.Load( ... LoadSceneProcess(sceneName, networked:true, ...) )
```

Key takeaways:
- **`AirportCheckInKiosk.StartGame(int ascent)` is the clean, UI-free entry point.**
  We call it directly (found via `FindObjectOfType<AirportCheckInKiosk>()`) — no need
  to poke the boarding-pass UI.
- The **saved scene** is forced by the checkpoint mod's Harmony patch on
  `MapBaker.GetLevel` (returns `selectedLevel` when set). So all we must do before
  calling `StartGame` is make the checkpoint mod set its `selectedLevel` for the
  right ascent — which is exactly what its `PreStartSetSegment()` does.
- `Ascents.currentAscent` (public static) is the current run's difficulty and
  survives into the endscreen/airport, so we read it to know which save to load.

## Vanilla: death → Airport

```
EndScreen (: MenuWindow)  // shown when the run ends
  returnToAirportButton -> EndScreen.ReturnToAirport()
     -> LoadingScreenHandler.Load(Basic, ... LoadSceneProcess("Airport", networked:true, ...))
```

So after death the player is on the `EndScreen`; pressing "Return to Airport" loads
the **`Airport`** scene. We reproduce this by calling `EndScreen.ReturnToAirport()`
when an `EndScreen` exists (post-death), else fall back to a direct scene load
(mid-game case — needs multiplayer validation).

Scene names that matter: `Title`, `Airport`, and level scenes start with `Level`.

## Checkpoint mod (0.4.7) internals we drive

Type `PEAK_Checkpoint_Save.Plugin` (BepInEx GUID `PEAK_Checkpoint_Save`):

| Member | Vis | What it does / why we use it |
|---|---|---|
| `static Plugin Instance` | public | Singleton handle. |
| `int selectedAscent` | private | Which difficulty's save file to look at. We set it. |
| `string selectedLevel` | private | Saved scene name; consumed by the `MapBaker.GetLevel` patch. Set for us by `PreStartSetSegment`. |
| `bool currentlyLoading` | private | Guard so we don't trigger a load mid-load. |
| `bool PreStartSetSegment()` | private | Reads the save file for `selectedAscent`, populates `selectedLevel` + metadata. Returns **true if a save exists**. |
| `void LoadPlayerOffline()` | public | The offline restore (teleport + inventory + afflictions + time). |
| `void LoadPlayerCoop()` | public | The coop restore. |

The mod's own load key (default **F6**) in `Plugin.Update()`:
- Only acts when in a **level** scene, as **master client**, not `currentlyLoading`.
- Requires a **double press** (confirmation) unless `enableLegacyLoadingKey`.
- Then calls `LoadPlayerOffline()` (if `PhotonNetwork.OfflineMode`) or `LoadPlayerCoop()`.

We replicate the *effect* of that F6 confirm by calling those two public methods
directly once we're safely in the loaded level — see `CheckpointInterop.TryLoadPlayer()`.

### Save file layout (for reference / auto-detection)

Under `PEAK/BepInEx/plugins/Checkpoint_Save/` (offline) and `.../Coop/` (coop),
named by `GetPlayerSaveFile(userId, ascent)`:

- Offline, normal run: `peak_save_{ascent}_offline.json`
- Offline, custom run:  `peak_save_CustomRun_offline.json`
- Coop, normal run:     `Coop/peak_save_{ascent}_{userId}.json`
- Coop, custom run:     `Coop/peak_save_CustomRun_{userId}.json`
- Legacy single-file modes exist too (`peak_save_offline.json`, `peak_save_765...`).

We currently don't parse these ourselves — we let `PreStartSetSegment()` do the
file selection. Auto-detecting the ascent from disk is a possible future nicety.

## Coop / multiplayer mechanics

- **Networked scene loads propagate to clients.** `LoadingScreenHandler.LoadSceneProcess(
  sceneName, networked:true)` calls `PhotonNetwork.LoadLevel(sceneName)`. When the host
  does this, Photon replays the load to every client. So:
  - `kiosk.StartGame(ascent)` (host) → `BeginIslandLoadRPC` to **All** → every client loads
    the fresh run. No extra work from us.
  - Returning to the Airport must ALSO be an explicit RPC-to-all, **not** a plain
    networked scene load. Use **`GameOverHandler.LoadAirport()`**
    (`Singleton<GameOverHandler>.Instance`), which does
    `RPC LoadAirportMaster → RPC BeginAirportLoadRPC (to All) → networked Airport load`
    — the same reliable pattern as the run-start. This is what vanilla uses once
    everyone has closed the endscreen ("Everyone has closed end screen.. Loading airport").
    `GameOverHandler`'s photonView is a persistent singleton, so its RPC reaches clients
    even while they're sitting on the endscreen/old level.
  - ❌ **Do NOT rely on `EndScreen.ReturnToAirport()` or a bare
    `LoadingScreenHandler.Load(...networked:true)` for coop.** Those only load the Airport
    locally for the caller; despite `AutomaticallySyncScene=true`, a client stuck on the
    endscreen is NOT dragged along. Symptom: the client stays in the *old* level (the
    endscreen is a UI overlay, so their scene name is still `Level_X`), even reports
    "ready" to the checkpoint mod, then `LoadPlayerCoop` teleports them using the host's
    new-instance coordinates → client ends up in the sky / desynced. These remain only as
    solo-safe fallbacks.
- **`LoadPlayerCoop()` is host-only and self-driving.** It gathers every player's actor
  number and teleports/restores them via RPCs (`CustomJumpToSegment`, `ReviveDeadPlayers`,
  `RPC_ApplyAfflictions`, `TeleportClientsToHost`, …). The host calling it restores
  everyone; clients don't call anything.
- **Readiness gate.** `LoadPlayerCoop` bails with "Please wait until everybody is ready!"
  when `CheckReadyStatusForPlayers()` is false and `enableClientReadyStatus` is on.
  Clients auto-report ready in `Plugin.Update()` once the scene name starts with "Level"
  (`SendReadyStatusToMaster`). We therefore poll `CheckReadyStatusForPlayers()` (via
  interop) before loading — see `ReadyCheckEnabled()` / `AllClientsReady()`.
- **We bypass `BoardingPass.StartGame`** (we call `kiosk.StartGame` directly), so the
  checkpoint mod's optional client **mod-version** check (`CheckForClientsModVersions`,
  in its `startGame_Override`) does not run for us. Harmless; just not enforced.

## Our resume sequence (implemented in `ResumeOrchestrator`)

1. Read `Ascents.currentAscent` → `targetAscent`.
2. If not in `Airport`: `RunLauncher.ReturnToAirport()`, wait for the Airport scene.
3. Wait for `AirportCheckInKiosk`. `interop.TrySetSelectedAscent(targetAscent)`,
   then `interop.TryPreStartSetSegment()` (abort if no save).
4. `RunLauncher.StartRun(targetAscent)` → `kiosk.StartGame`. Wait for a `Level`
   scene and `Character.localCharacter`, settle, then `interop.TryLoadPlayer()`.

## Open questions to validate in-game (see ROADMAP)

- Exact scene name(s) after death — confirmed `Airport` from code, verify live.
- Timing: how long after the level loads is it safe to call the restore? (settle knobs)
- Coop: does driving `kiosk.StartGame`/`ReturnToAirport` from a non-UI path replicate
  the RPC handshake correctly for clients? Start single-player, then test coop.
- Custom runs (`RunSettings.IsCustomRun`) — is that flag still set after death?
```
