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

## The Strange Gem on the scout statue (PEAK 2.1.a)

Reported symptom: the gem the statue behind the peak holds is missing after loading a
checkpoint taken in **THE CITADEL**, while loading a **GLOOM** checkpoint is fine.

It is **not** a level `ISpawner`, which is why no spawner re-trigger ever brings it back.
`Peak.ScoutStatue` (Assembly-CSharp, new in 2.x) spawns it itself, once, host only:

```csharp
private void Update() {                                   // only ticks while active
    if (PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient && !spawnedInitialGem) {
        SpawnGem_Master();
        spawnedInitialGem = true;                         // latches forever
    }
}
private void SpawnGem_Master() {
    if (PhotonNetwork.IsMasterClient && spawnedGem_master == null) {
        spawnedGem_master = PhotonNetwork.Instantiate("0_Items/" + gemPrefab.name,
                                                      gemSpot.position, gemSpot.rotation, 0);
        spawnedGem_master.GetComponent<Item>().SetKinematicNetworked(true);
        if (AllAmuletsFilled()) SpawnScoutsHonor();
    }
}
```

`RPC_InsertAmulet(99)` is the only other route in, and it just calls `SpawnGem_Master()`
on the master client, so calling that method directly on the host is equivalent.

Scene placement (both final-segment variants, `Segment.TheKiln` = 4):

```
Map / Biome_4 / Gloom   / Temple_Segment  / Peak               / AmuletScoutStatue   <- THE CITADEL
Map / Biome_4 / Caldera / Volcano / Volcano_Segment / Peak_Kiln Variant / AmuletScoutStatue   <- THE KILN
```

Both sit **inside the segment parent**, so the statue only starts ticking when segment 4
is activated. Hence: loading a Citadel save activates the segment during the jump, the
statue spawns the gem a frame later, our cleanup then deletes it, and `spawnedInitialGem`
is already true so vanilla never retries. Loading a Gloom save leaves the statue inactive
throughout the load, so the later campfire transition spawns the gem normally.

Why our cleanup catches it while ordinary loot survives: `ScoutStatue` uses
`PhotonNetwork.Instantiate`, not `PhotonNetwork.InstantiateItemRoom`. PUN2 defines
`IsRoomView => CreatorActorNr == 0`, so a normal spawner item is a room view and is
skipped by `OwnWorldLootReset.ResetWorldLoot`'s dropped-item pass, while the gem is not
and gets destroyed map-wide (no radius). `DestroyLeftoverHeldItems` would take it too on
a repeat load (`IsMine` is true for the host's own instantiate).

Fix: `StrangeGemRestore.Restore()`, called from `OwnTeleportSequence` for segment 4 after
every destructive pass. See that class for the guards.

Statue state needs no save/restore: it can only be interacted with past the last campfire,
so no checkpoint can contain amulets already inserted.

### Inspecting scenes (how the placement above was established)

`level*` in `PEAK_Data` are plain Unity serialized scenes, readable with UnityPy:

```bash
python3 -m venv venv && ./venv/bin/pip install UnityPy
# then walk env.objects: GameObject -> m_Component -> Transform.m_Father for hierarchy,
# MonoBehaviour.m_Script -> MonoScript.m_ClassName for component names
```

In 2.1.a the gem statue lives in `level4`. Note `scratch/PEAK-game-backup` is 1.65.a and
predates Gloom/The Citadel entirely, so always inspect the live install for this.

## Nadir biome (v2.1.a) — save-point feasibility research

Live install is actually **2.1.a** (`0657e527f`), newer than the "v2.0.a" the biome shipped
in and newer than the stale `scratch/decomp/*` dumps (1.65.a). Re-decompiled fresh into
`scratch/decomp/v2.1.a/Assembly-CSharp.decompiled.cs` (100k lines) for this research —
that's the one to grep going forward, not the older `allcs`/`scratch_full` dumps.

**Goal recap:** add a save point for Nadir, triggered by communing with the "scoutmaster's
soul" statue (hold E), that behaves like a campfire autosave. Research only in this pass —
nothing below is implemented yet.

### Nadir is not a separate scene — correction to the initial assumption

`Biome.BiomeType.Void` / `Segment.Void` (the last value of `enum Segment : byte { Beach,
Tropics, Alpine, Caldera, TheKiln, Peak, Void }`, i.e. 6) is baked into the **same level
scene** as the rest of the mountain, just kept deactivated until reached. `MapHandler`
holds it as a `VoidBiome.instance.segment` and only appends it to its own `segments[]`
array the first time it's needed:

```csharp
private void SetUpVoidSegment() {           // MapHandler
    if (voidInitialized) return;
    if ((bool)VoidBiome.instance) {
        segments = segments.Append(VoidBiome.instance.segment).ToArray();  // paraphrased
        voidInitialized = true;
    } else {
        Debug.LogError("Tried to set up void biome without instantiating it!");
    }
}
```

Entering Nadir is `MapHandler.JumpToSegmentLogic(Segment.Void, ...)` — the exact same
function that handles every other segment transition (Beach→Tropics etc.), just with a
few `if (newSegment == Segment.Void)` special cases (calls `SetUpVoidSegment()`,
`VoidBiome.instance.Activate()`, hides the old Peak/TheKiln parent). **No scene load, no
`LoadingScreenHandler.LoadSceneProcess` call anywhere in this path** — the "loading
screen" used (`LoadWithoutDisablingQueue(LoadingScreenType.White/WhiteInstant, ...,
MapHandler.GoToVoidRoutine(...))`) is purely a visual fade coroutine.

This matters a lot for us: our whole save/teleport machinery (`OwnSaveCapture`,
`OwnTeleportSequence`, `MapHandler.JumpToSegment`/`SetSegmentOnSpawn`) already operates in
terms of `Segment`/`MapHandler.currentSegment` for every other biome, and Nadir slots into
that exact same mechanism as segment index 5 (see indexing gotcha below) rather than
needing a whole separate code path. Good news for implementation effort.

### How Scout's Honor loads Nadir

- `ScoutStatue` (a *different* object from the "commune" statue below) is the mid-run
  pedestal where the four amulets (`DoubleJumpAmulet`, `HealingAmulet`,
  `InfiniteStamAmulet`, a 4th) are slotted; once all 4 are in, it spawns `ScoutsHonor`
  (`class ScoutsHonor : AmuletBase {}` — an otherwise-empty subclass, all its logic is
  inherited).
- The actual "consume Scout's Honor → enter Nadir" logic lives in `Action_WarpToBiome`, an
  `ItemAction` with a `Segment segmentToWarpTo` field:
  ```csharp
  public class Action_WarpToBiome : ItemAction {
      public Segment segmentToWarpTo;
      public override void RunAction() { ... base.photonView.RPC("RPC_Warp", RpcTarget.All); }
      [PunRPC] public void RPC_Warp() {
          if (segmentToWarpTo == Segment.Void) {
              if (!MapHandler.Instance.inNadir) {
                  // LoadWithoutDisablingQueue(..., MapHandler.GoToVoidRoutine(clearStatus: true))
              }
          } else if (PhotonNetwork.IsMasterClient) { MapHandler.JumpToSegment(segmentToWarpTo); }
      }
  }
  ```
  This is very likely an `ItemAction` wired onto the `ScoutsHonor` prefab in the Unity
  editor with `segmentToWarpTo = Segment.Void` — matches the log line
  `"WE'RE GOING TO THE SHADOW REALM BABY"` and the fact that `GoToVoidRoutine` opens by
  calling `MapHandler.DeleteScoutsHonorFromLocalCharacter()` (consumes the item). **Not
  scene-verified** (Editor-only wiring, see "open items" below) but there's no other
  plausible caller.
- `MapHandler.GoToVoidRoutine(bool clearStatus)`: deletes Scout's Honor from the local
  character, clears afflictions, and — master-client-only — calls
  `JumpToSegment(Segment.Void)`, then revives any dead/passed-out characters at
  `VoidBiome.instance.GetSpawnPosition(n)`.

### The "commune" interactible — `ScoutmasterSoulPillar`

This is the object the request describes: `GetInteractionText()` returns
`LocalizedText.GetText("COMMUNE")`, `GetName()` returns `LocalizedText.GetText(
"NAME_SCOUTMASTERSOUL")`, hold-to-interact (`GetInteractTime` → 2s,
`IsConstantlyInteractable` → true). On a successful hold:

```csharp
public void Interact_CastFinished(Character interactor) =>
    base.photonView.RPC("RPC_Break", RpcTarget.All, 0, interactor.photonView);

[PunRPC] private void RPC_Break(int type, PhotonView view) {
    switch (type) {
        case 0: SetBroken(true, view); break;      // the real "commune" event
        case 1: /* start charge-telegraph while holding */ break;
        case 2: /* cancel telegraph */ break;
    }
}
```

`SetBroken` is a **one-time, permanent** event guarded by a private `_broken` bool (synced
via `OnPhotonSerializeView` for late joiners, but that path calls `SetBroken` directly, not
through the `RPC_Break` PunRPC — important, see hook plan below). It permanently disables
the pillar's collider, explodes glass shards, spawns a `ScoutmasterGhostOrbiter` that then
chases the interacting character, and fires `GlobalEvents.TriggerSoulFreed(0)` then `(1)`.
There is exactly one of these per Nadir run (no scene-verified count, but the narrative —
"free the trapped scoutmaster's ghost" — and the permanent-break design both point to a
single instance near the start of the climb).

Caution: `RPC_Break(1)`/`(2)` fire on every player who is *currently holding* the
interaction (multiple players could be charging it at once — `charactersTryingToBreak`
counts holders), so a hook must filter to `type == 0` only, exactly once.

Note this also kicks off a hazard (the ghost chase) rather than a purely-safe moment —
`MapHandler.LastSeenCampfireIsSafe` reads `VoidBiome.SoulFreedStatus != 1` while
`VoidBiomeActive`, and `SoulFreedStatus` goes `0` then `1` right after breaking. That
distinction affects *revive* safety-checks elsewhere in vanilla, not our save/teleport
target (see restore section — we never spawn players at the pillar, always at the saved
world-anchor position), so it's a "worth knowing", not a blocker.

### Mapping to our existing "campfire lit → autosave" pattern

`CampfireAutoSavePatch` already hooks `Campfire.Light_Rpc` (postfix, `RpcTarget`-to-all RPC
so the host always sees it regardless of who lit it) rather than `Interact_CastFinished`,
specifically because a client's `Interact_CastFinished` never reaches the host directly.
The exact same reasoning applies to `ScoutmasterSoulPillar`, and the fix is the same shape:

- Patch target: `AccessTools.Method(typeof(ScoutmasterSoulPillar), "RPC_Break")`, postfix.
- Guard: `type == 0` only (skip 1/2 telegraph pings).
- No "recently lit" cooldown needed the way `CampfireAutoSavePatch` needs one — `_broken`
  already guards `SetBroken` against re-entry, and (unlike `Light_Rpc`, which also replays
  for late-joiner sync) the late-joiner sync path for this pillar calls `SetBroken`
  directly rather than going through the RPC, so a patch on `RPC_Break` itself won't
  double-fire for a joining client.
- Master-client / offline gating, `SavePlayerOffline`/`SavePlayerCoop` calls: identical to
  `CampfireAutoSavePatch.Postfix` today.

### Save-data gaps found (need fixing when this is implemented, not yet done)

1. **`AreaNameCompat` will very likely mis-resolve "NADIR".** It does
   `int index = (int)segment; progressPoints[index]`. `MountainProgressHandler
   .progressPoints` is filtered per-run to only the biomes actually present
   (`InitProgressPoints`), then has `progressPoints.Last()` appended — its indexing has
   **no fixed relationship** to the raw `Segment` byte value. Worse, `MapHandler`'s own
   internal `currentSegment` int for Void is **5**, not 6 (`JumpToSegmentLogic` does
   `if ((int)newSegment >= 5) num2--;` because Void is appended as the *6th* array
   element, index 5) — so even a "just use the right index" fix needs to use `mapHandler`'s
   internal index, not `(int)Segment.Void`. Cleanest fix: special-case `Segment.Void` to
   return the literal string `"NADIR"` directly, skipping `progressPoints` entirely.
   Confirmed `"NADIR"` is the real progress-point title (used in
   `MountainProgressHandler.GetRichPresenceState`'s `switch (p.title)`), and
   `SaveArchive.TryGetOfficialCampfireTitle` already has a documented fallback for exactly
   this case — an internal name not in `CampfireLocKeys` gets tried as a raw
   `LocalizedText` key — so once `campfireName` is written as `"NADIR"`, the F7 picker
   should display it correctly with **no SaveArchive changes needed**.

   **[CORRECTED 2026-08-16]** That last inference was wrong, and the picker did show a bare
   `NADIR` in every language. Unlike every other area title, `"NADIR"` is **not** a key in
   the localization table — Nadir is filed under `"AREA_VOID"`, whose English value happens
   to be `"NADIR"` (Polish `OTCHŁAŃ`, Portuguese `COVÃO`). Verified by grepping the runtime
   `Localization/SerializedTermsData` JSON out of the live 2.1.a `resources.assets`;
   `AREA_VOID` is also the only `AREA_*` key in the whole table. So the raw-key fallback
   missed its `ContainsKey` guard and `CampfireLabel` fell back to the stored name. Fixed by
   mapping `"NADIR" → "AREA_VOID"` in `SaveArchive.CampfireLocKeys`, which leaves the saved
   `campfireName` alone and so needs no migration of existing saves.
2. **`OwnTeleportSequence.RunSequence` resolves `targetSegment` too early for Void.** Line
   ~199-201 does `mh.segments[(int)finalSegment]` *before* calling
   `MapHandler.JumpToSegment(finalSegment)` a few lines later. On a cold load where Nadir
   hasn't been reached yet this session, `mh.segments.Length` is still the original 5 (not
   yet extended by `SetUpVoidSegment()`), so `index = 6` is out of range and
   `targetSegment` safely comes back `null` (the bounds check already guards against a
   crash) — but that silently skips re-running Nadir's own item spawners
   (`targetSegment.segmentParent...GetComponentsInChildren<ISpawner>()`) on load. Minor
   (loot-respawn only), needs `targetSegment` re-resolved *after* the jump for `Segment.Void`.
3. **`index--` special-case at line ~279 only covers `Peak` (5), not `Void` (6).** Currently
   harmless — nothing downstream keys off `index` for segment ≥ 5 — but flag for whoever
   touches that block next.
4. **Biggest open unknown: does `VoidBiome.instance` exist on a cold load?**
   `SetUpVoidSegment()` no-ops (logs an error, does nothing) if `VoidBiome.instance` is
   null, which depends on that `MonoBehaviour`'s `Awake()` having already run — which in
   Unity requires the GameObject to have been active at least once. If a player loads a
   Nadir checkpoint on a **freshly started run** (never organically walked into Nadir this
   session), it's unverified whether `VoidBiome.instance` is already populated or not. This
   is the load-bearing risk for the whole feature and needs an in-game check (e.g. a debug
   log of `VoidBiome.instance != null` right after a fresh run start, before ever reaching
   Nadir) — can't be resolved from decompiled code alone.
5. **Capture radius framing.** The request's "30m around the statue, wider for Nadir" maps
   onto existing per-mechanic radius constants (`AncientStatueRestore.StatueSearchRadius`
   = 100f, `LuggageRestore.LuggageSearchRadius` = 30f, `WorldItemRestore.SearchRadius` =
   30f, `DeployableRestore.SearchRadius` = 30f, `CampfireAreaHelpers.CampfireSearchRadius`
   = 30f) — all of which are already anchored on the **player's position at save time**
   (`OwnSaveCapture.ResolveWorldAnchor`, player head), not on a campfire/statue position.
   So "increase the radius for Nadir" is really "raise these constants conditionally when
   `segment == Segment.Void`" — no architecture change, just a per-biome override. Deferred
   per your note ("we'll talk about this later").
6. **Position/anchor capture itself needs no change.** `ResolveWorldAnchor` is already
   biome-agnostic (falls back through: local player's head → any living player → previous
   campfire → local player regardless). Works for Nadir as-is.
7. **The pillar's own broken/unbroken state needs no save/restore work.** Breaking only
   flips a bool on the pillar's own existing `PhotonView` — it isn't destroyed or
   respawned by a segment jump, so a restored Nadir checkpoint naturally reloads with the
   pillar already broken (our save can only be taken after `RPC_Break(0)` fires anyway).

### Open items before implementing (in priority order)

1. **Scene/prefab verification** (blocked this session — see below): confirm
   `Action_WarpToBiome` really sits on the `ScoutsHonor` item prefab with
   `segmentToWarpTo = Void`, confirm there's exactly one `ScoutmasterSoulPillar` per Nadir
   run, and inspect whether `VoidBiome`'s GameObject starts active-but-hidden (renderers
   off) vs. fully inactive (which would leave `Awake()` unrun).
2. **In-game check of the cold-load `VoidBiome.instance` risk** (item 4 above) — needs
   Nikoj, can't be resolved by static analysis.
3. Only then: implement the `RPC_Break` Harmony patch (mirrors `CampfireAutoSavePatch`
   almost verbatim), fix `AreaNameCompat`'s Void case, fix the `targetSegment` resolution
   order in `OwnTeleportSequence`.

Scene inspection with UnityPy stalled this session: level files only carry `MonoBehaviour`
instances, and their `m_Script` PPtrs didn't resolve even loading `globalgamemanagers
.assets` + `level4` together (45/63313 resolved) — needs a full `AssetsManager`-style load
of the whole `PEAK_Data` folder as one environment (not attempted, likely slow/heavy) or
an in-game reflection dump instead. A throwaway venv is at `scratch/venv-unitypy/` if
picking this back up.
