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

## `teleportJumpLogic` (checkpoint mod 0.4.7) — why 0/1/2 behave so differently

All three values are switched on inside `Plugin.CustomJumpToSegment()`, which only ever
runs as a coroutine on **whichever machine calls `LoadPlayerOffline()`/`LoadPlayerCoop()`**
— i.e. only the host in coop. Nothing re-runs this coroutine on clients. So whether a
client is affected at all depends entirely on whether the chosen branch itself talks to
the network.

- **`0` = `MapHandler.SetSegmentOnSpawn(segment, lastRevivedSegment)`.** Internally calls
  `JumpToSegmentLogic(segment, playersToTeleport: { NetCode.Session.SeatNumber }, sendToEveryone: false)`.
  `playersToTeleport` is hardcoded to **the caller's own seat only**, and
  `sendToEveryone: false` means the segment/campfire/wall `GameObject.SetActive` calls
  (not networked in themselves) never get propagated anywhere. Nothing is sent over the
  network at all. Solo: caller = only player, works perfectly. Coop: only the host
  teleports and only the host's local scene activates the new segment; every client is
  left in the old segment with no idea anything happened. This is not a Linux/Proton
  thing — nothing in this path touches platform-specific code, it's a pure C#/Unity/Photon
  call graph. Confirmed identical behavior on a native Windows 11 VM. It reads like the
  method was written for the vanilla single-player "restore my last segment on scene load"
  case and reused here as the mod's default without accounting for coop.
- **`1` = `MapHandler.JumpToSegment(segment)`** (static). On the master client (the host,
  since only the host calls this) it takes the `if (PhotonNetwork.IsMasterClient)` branch:
  `playersToTeleport` = every player's actor number, and
  `sendToEveryone: !NetCode.Session.IsOffline` (true whenever online). Two real network
  effects follow: (1) the per-player teleport inside `JumpToSegmentLogic` is a genuine
  Photon RPC — `photonView.RPC("WarpPlayerRPC", RpcTarget.All, vector, false)` — so
  everyone's position actually moves on every machine; (2) because `sendToEveryone` is
  true, it also does `CustomCommands.SendPackage(new SyncMapHandlerDebugCommandPackage(...), ReceiverGroup.Others)`,
  which every client receives in `OnPackageHandle` and uses to re-run
  `JumpToSegmentLogic(..., sendToEveryone: false)` **locally on their own machine**,
  replicating the segment/campfire/wall activation there too. This is the only one of
  the three that both moves everyone's position over the network *and* syncs the local
  scene state to every client — matches our validated finding that `1` is the value that
  actually works host+client (see `PluginConfig.OptimizedCoopJumpLogic`, default `1`).
- **`2` = `mh.GoToSegment(segment)`** (instance method). This is the *vanilla* "walk from
  one campfire to the next" transition, not a teleport primitive: it has a hard guard
  `if ((int)s <= currentSegment) { LogError(...); return; }` (a no-op on a freshly loaded
  level where `currentSegment` starts at `0` unless the target segment is strictly
  greater), it is entirely local/non-networked (no RPC, no `CustomCommands` package,
  ever), and critically **it never calls `WarpPlayerRPC` or moves the player's position at
  all** — it only flips which segment's GameObjects are active and lets the player walk
  into the newly-revealed area. It cannot function as a teleport in either solo or coop;
  best case it silently re-activates a segment around a player who doesn't move, worst
  case the guard makes it a no-op. Nothing here supports it as a usable multiplayer
  teleport workaround (our own `AltTeleportJumpLogic` currently defaults to `2` for the
  Alt-hold override — worth revisiting given this).

## Scene "saving"/loading and whether old daily islands get cleared

The checkpoint mod does not save a level scene — there's nothing bespoke to save. All
possible islands are a **fixed, permanent array baked into the game build**:
`MapBaker.ScenePaths`, indexed via `GetLevel(levelIndex) => ScenePaths[levelIndex % ScenePaths.Length]`.
The "daily island" is just an integer, `NextLevelService.NextLevelData.CurrentLevelIndex`
(sourced from the server's `LoginResponse.LevelIndex` on login), selecting into this
always-present, always-shipped array — it is not procedurally generated or downloaded
per day.

The checkpoint mod records whichever scene name was active at save time
(`sceneName = SceneManager.GetActiveScene().name`, e.g. `"Level_3"`), then on load
Harmony-prefixes `MapBaker.GetLevel`:
```csharp
[HarmonyPatch(typeof(MapBaker), "GetLevel")]
Prefix: if (Instance.selectedLevel is set) { __result = Instance.selectedLevel; return false; }
```
forcing that exact scene name regardless of what today's server-assigned index says.

**Conclusion: old islands are never cleared.** `Level_3` is exactly as permanent as every
other entry in the pool — shipped game content, not ephemeral per-day data. A save
referencing an old `Level_N` stays loadable indefinitely, unless a future PEAK update
restructures/renames/removes that entry from `MapBaker.ScenePaths` (a compatibility break
on the game's side, not a cleanup mechanism). Matches our own live testing: cross-loading
between two different daily islands across full runs worked flawlessly on v1.1.0.
```
