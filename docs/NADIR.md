# Nadir save-point — implementation handoff

**Status: feature complete. All four passes confirmed working in co-op in-game on
2026-08-16, host and client logs both clean. Passes 3 and 4 are still uncommitted.**

This doc exists so a fresh session (or a different model) can pick this up without
re-deriving the research. Read "Handoff: state of play" immediately below first, then
`docs/RESEARCH.md`'s "Nadir biome" section for the code-reference-heavy background. The
middle of this file is the original research the implementation was built from, kept as-is
apart from two corrections marked **[CORRECTED]**; "What was actually implemented" at the
bottom is the record of what actually shipped, in three passes.

## Handoff: state of play (as of 2026-08-16, end of session)

### Where the code is

| Pass | What | Commit |
| --- | --- | --- |
| 1 | Save hook, `"NADIR"` area name, `Segment.Void` index/`targetSegment` fixes, missing-`VoidBiome` guard | `6d798d9` *nadir save/load scaffold* |
| 2 | 80m capture radius anchored on the pillar, pre-commune on load | `fe27aa9` *increase save area in nadir to 80m; pre-commune with statue* |
| 3 | Rising-field hold until everyone has control | **uncommitted** |
| 4 | Commune ownership: anchor the save on the communing player, restore the ghost to them | **uncommitted** |

Uncommitted working tree at handoff:

- `src/PeakQuickResume/NadirRisingField.cs` (new, untracked — pass 3)
- `src/PeakQuickResume/NadirCommuner.cs` (new, untracked — pass 4)
- `src/PeakQuickResume/OwnTeleportSequence.cs` (modified: `HoldRisingFieldUntilEveryoneHasControl`,
  `_risingFieldHold`, `RisingFieldHoldCeilingSeconds` and the `StartCoroutine` call in
  `PreCommuneWithScoutmasterSoul` from pass 3; `PreCommuneWithScoutmasterSoul` taking `data`
  and sending the saved interactor's view from pass 4)
- `src/PeakQuickResume/OwnSaveCapture.cs`, `OwnSaveData.cs`,
  `ScoutmasterSoulPillarAutoSavePatch.cs` (modified — pass 4)
- `packaging/CHANGELOG.md` (added bullets under 2.3.0)
- `docs/NADIR.md` (this file, untracked)

Left uncommitted deliberately: the maintainer wants passes 3 and 4 verified in game first.
Everything builds clean (`dotnet build src/PeakQuickResume/PeakQuickResume.csproj`), and
passes 1 and 2 were confirmed working in both solo and co-op without either of them.

### What has actually been verified in game

- Communing writes a save. ✔
- Loading a Nadir save from a fresh run start works, no hard lock, no issues reported. ✔
  (This was the one part of the plan that rested on inference rather than evidence, so it
  being confirmed closes the biggest open risk in the whole feature.)
- `RuleZeroBadge` is granted for winning the run *after* communing, confirmed by the
  maintainer. That's `RunManager.Win` gated on `MapHandler.inNadir`, so a loaded save cannot
  hand it out and the pre-commune gives nothing away. ✔
- **Co-op, 2 players, 2026-08-16, three consecutive Nadir loads.** ✔ Both logs
  (host: r2modman `Default` profile; client: `~/Downloads/message.txt`) are clean of any
  mod-side warning or error. Specifically confirmed from the host log:
  - the commune wrote one save event with a host file and a client file,
  - the pillar-anchored 80m search worked (capture searched from `(3.66, 742.69, 199.54)`,
    the pillar, while the save's own position was the host at `(-1.50, 732.12, 157.47)`),
  - each load jumped to Void, warped both players, and pre-communed with `SuppressNextBreak`
    correctly stopping the restore's own break from writing a save,
  - the client's own presentation (loading screen → collapse → wake-up → ready RPC) completed
    on all three loads.
  - `Attempted to spawn campfire items for a non-existent campfire! Current segment is Peak`
    appears on every Nadir jump — **vanilla noise, not ours**: the identical line appears on
    the organic Scout's Honor jump earlier in the same log.

- **Co-op, second round, same day, passes 3 and 4 included.** ✔ Four loads, host and client
  logs both entirely free of mod-side warnings and errors. From the host log:
  - `NadirCommuner: Name (76561199181099132) communed at (2.23, 742.62, 196.76) - this save
    is anchored on them, not on the host.` The written save's `posX/Y/Z` match that exactly,
    and the pillar is now **3.1m / 3.6m** from the checkpoint on load instead of 43.7m.
  - `NadirCommuner: replaying the commune as Name (...), the player who originally communed`
    → `pre-communed with the soul pillar 3.1m from the checkpoint as Name`. The ghost goes
    back to the client who communed.
  - The legacy path was exercised too, by loading the morning's pre-pass-4 save:
    `this Nadir checkpoint predates recording who communed, so the host stands in` →
    `pre-communed ... 43.7m from the checkpoint as N1K0`. Old saves still load, just with
    the old anchor and the host holding the ghost.
  - `Nadir's rising field parked until every player has control` →
    `everyone has control, Nadir's rising field released` on all four loads. The 90s ceiling
    never fired.

### What has NOT been verified

- **The pass-3 failure paths.** The release condition was met normally on all four loads, so
  the 90s ceiling, a hung/dropped client, and the `EnsureSoulFreed` failsafe have all still
  never actually run.
- The pass-4 "communer left the session" fallback in co-op (the equivalent legacy-save path
  through the same fallback did run, so only the user-id lookup miss is untested).
- Whether the F7 picker actually renders "NADIR" localized (inferred from
  `SaveArchive.TryGetOfficialCampfireTitle`'s raw-key fallback plus `"NADIR"` appearing as a
  `LocalizedText` key in `MountainProgressHandler.GetRichPresenceState`, never seen on screen).

### Next session: what to do

**Deploy before testing.** The deploy is opt-in
(`dotnet build src/PeakQuickResume/PeakQuickResume.csproj -p:DeployToProfile=true`), and the
first co-op round was run against a DLL that predated pass 3 without anyone noticing until
the logs were read afterwards. Check the DLL's mtime, or `strings` it for the newest type
name, before drawing conclusions from a test.

1. **Commit passes 3 and 4.** They're verified; there's nothing left blocking them.
2. Optional hardening runs, none of which block anything:
   - Two players finishing the 2s hold together: exactly one save should be written. The
     prefix/`__state` guard is what should make that true.
   - Have the communing player leave before the load: expect the "isn't in this session"
     warning and the ghost falling back to the host, everything else unchanged.
   - Throttle or hang a client mid-load and confirm the field still starts, at the latest 90s
     after the pre-commune, with the "gave up holding" warning in the log.
3. Log Nadir's real `initialWaitTime` and `travelTime` off the live `LavaRising` (see "Open
   items"). They're scene-serialized, so they cannot be read from the decompile, and they
   decide how much any of the pass-3 timing actually matters.

### Things that would be easy to get wrong picking this up

- `Segment.Void` is ordinal **6**, but `MapHandler` stores it at array index **5**. Vanilla's
  own `JumpToSegmentLogic` does `if ((int)newSegment >= 5) num2--;`. Peak (5) collapses onto
  the Kiln's slot (4) by the same rule.
- `MountainProgressHandler.progressPoints` cannot be indexed by the raw `Segment` ordinal at
  all. Vanilla dodges this too (`CharacterSpawner` skips `DisplaySegmentTitleAfterDelay`
  outright while the segment is Void). Hence the hardcoded `"NADIR"`.
- Anything that looks up Nadir objects (`ScoutmasterSoulPillar`, the `LavaRising`,
  `MapHandler.segments[5]`) only resolves **after** the segment jump has activated the Void
  segment. `LavaRising.ALL_LAVA` is populated from `OnEnable`, and `FindObjectsByType`
  excludes inactive objects.
- The pre-commune must never be allowed back through `ScoutmasterSoulPillarAutoSavePatch`, or
  loading a save writes a new save mid-load. `SuppressNextBreak` is what prevents it.

## The ask

Add a save point for the Nadir biome (PEAK's final, added-in-2.0.a biome). Mechanically:
the player holds **E** on the scoutmaster's soul statue ("commune"), and that should
save exactly like lighting a campfire does everywhere else — capture inventory, ground
items, position, etc. — using the game's own "NADIR" translation for the F7 save-picker
label. Loading that save should: start a run of the correct saved level, wait for
everyone to be ready, jump into Nadir, teleport every player to the statue, and restore
everyone's items (inventory + world loot) exactly like any other checkpoint restore.

## Confirmed mental model (code research + in-game testing, 2026-08-15)

### Nadir lives in the same scene, always-instantiated, not streamed

`Biome.BiomeType.Void` / `Segment.Void` (`enum Segment : byte { Beach, Tropics, Alpine,
Caldera, TheKiln, Peak, Void }`, i.e. ordinal 6) is baked into the **same level scene**
as every other biome. Every segment's geometry already exists in the scene from load;
`MapHandler.JumpToSegmentLogic` just flips `segmentParent.SetActive(true/false)` per
segment — there is no `SceneManager.LoadSceneAsync`, no Addressables, no real terrain
streaming anywhere in this path (checked; the only `LoadSceneAsync` calls in the whole
decompile are the actual Title/Airport/Level scene loads, unrelated to segment
transitions). The "background loading" look-and-feel when lighting a campfire is
`OrbFogHandler`'s fog-of-war reveal effect, not asset streaming.

**This was tested in-game and confirmed:** once inside an active run (any level scene
loaded), you can jump straight to Nadir from literally anywhere — beach included — no
prior progression required, "you are just teleported there." From the Airport or main
menu, trying to trigger Nadir hard-locks on a Nadir-only loading screen forever.

**Why, mechanically:** `VoidBiome` is a `MonoBehaviour` baked into the level scene whose
`Awake()` sets `VoidBiome.instance = this`, making `VoidBiome.instance` non-null once the
**level** scene has loaded, regardless of whether the player has organically reached that
segment yet. That object
does not exist at all in the `Airport`/`Title` scenes (they don't contain any mountain
geometry). `MapHandler.SetUpVoidSegment()` — called from `JumpToSegmentLogic` the first
time `Segment.Void` is requested — no-ops (just logs an error) if `VoidBiome.instance` is
null instead of throwing.

**[CORRECTED]** The original wording here claimed Unity runs `Awake()` even for scripts on
an *inactive* `GameObject`. It does not — `Awake()` is deferred until the object is first
activated. The in-game test result (jump to Nadir from anywhere in a loaded level) still
stands, which means the `VoidBiome` component sits on an object that is *itself* active at
scene load, with the segment geometry under it being what's toggled. `Deactivate()` doing
`gameObject.SetActive(false)` on its own object fits that reading. Nothing downstream
depended on the wrong half of the explanation, but `OwnTeleportSequence` now guards the
jump on `VoidBiome.instance` being non-null rather than assuming it, since the guarantee is
weaker than this section originally claimed. The `SetUpVoidSegment` shape:

```csharp
private void SetUpVoidSegment() {           // MapHandler, paraphrased
    if (voidInitialized) return;
    if ((bool)VoidBiome.instance) { /* append VoidBiome.instance.segment to segments[] */ }
    else Debug.LogError("Tried to set up void biome without instantiating it!");
}
```

`MapHandler.GoToVoidRoutine` (the coroutine behind "use Scout's Honor") then keeps
running past that silent failure and dereferences `VoidBiome.instance.GetSpawnPosition(0)`
a few lines later — from the Airport that's a null MapHandler/VoidBiome outright (the
Airport scene has neither), so the coroutine throws mid-flight. Because the surrounding
`LoadWithoutDisablingQueue(...)` only tears down its loading screen after the passed
coroutine *completes*, an exception partway through leaves the screen up forever. That's
the hard-lock the testing found — consistent, no longer a mystery.

**Conclusion:** any implementation must only trigger the Nadir jump **after** a run has
been started and its level scene is fully loaded — never from the Airport directly. This
naturally falls out of following the exact same flow every other checkpoint load already
uses (see next section) — it isn't an extra thing to build.

### The existing save/load pipeline already does almost everything this needs

This mod (`PeakQuickResume`) already owns its full save/load pipeline (`OwnSaveCapture`,
`OwnSaveData`, `OwnLoadEntryPoints`, `OwnTeleportSequence`), independent of the original
`PEAK Checkpoint Save` mod. The existing flow for loading ANY save is already exactly
what the ask describes for Nadir:

1. `OwnLoadEntryPoints.TryPreStartSetSegment` reads the save's `sceneName` into
   `SelectedLevel`, consumed by `MapBakerLevelOverridePatch` to force the correct level
   scene.
2. `RunLauncher.StartRun` → vanilla `kiosk.StartGame` → the level scene loads for
   everyone (this is where `VoidBiome.instance` becomes non-null, scene-wide).
3. `ResumeOrchestrator`/`OwnLoadEntryPoints.TryLoadPlayer` waits on
   `OwnNetwork.CheckReadyStatusForPlayers()` before proceeding (coop only) — "wait until
   everybody is ready" already exists, already gates this exact call.
4. `OwnTeleportSequence.RunSequence(data, selection)` runs, host-only:
   - Calls `MapHandler.JumpToSegment(finalSegment)` where `finalSegment = data.segment`
     (a `Segment` enum value) — **already generic over every segment**, no Nadir-specific
     branch needed here in principle (see gaps below for what actually needs fixing).
   - `TeleportClientsToHost(hostPos)` warps every connected character to the saved
     position — already fully coop-generic, this is the "teleport all players to the
     statue" step (the saved worldAnchor position IS right next to the statue, see
     below), nothing new to build.
   - Runs `WorldItemRestore`, `AncientStatueRestore`, `LuggageRestore`,
     `DeployableRestore`, inventory/backpack/afflictions restore — all already
     position/anchor-based, not biome-specific.

**So the honest scope of this feature is much smaller than "build a new save/load path
for a weird biome"** — it's: (a) hook the commune event to trigger a capture using the
existing capture functions, and (b) fix a handful of concrete `Segment.Void` edge cases
in code that currently assumes segments are always in `0..5`. Both are enumerated exactly
below.

### The "commune" interactible — `ScoutmasterSoulPillar`

`GetInteractionText()` → `LocalizedText.GetText("COMMUNE")`, `GetName()` →
`LocalizedText.GetText("NAME_SCOUTMASTERSOUL")`. Hold-to-interact (2s). On a successful
hold:

```csharp
public void Interact_CastFinished(Character interactor) =>
    base.photonView.RPC("RPC_Break", RpcTarget.All, 0, interactor.photonView);

[PunRPC] private void RPC_Break(int type, PhotonView view) {
    switch (type) {
        case 0: SetBroken(true, view); break;   // <- the real "commune" event, one-time
        case 1: /* charge-telegraph while a player holds E */ break;
        case 2: /* telegraph cancelled */ break;
    }
}
```

`SetBroken` is permanent, guarded by a private `_broken` bool, synced for late joiners
via `OnPhotonSerializeView` — which calls `SetBroken` **directly**, not through the
`RPC_Break` PunRPC, so a Harmony patch on `RPC_Break` itself is naturally immune to
late-joiner re-triggering (no extra guard needed there, unlike `Campfire.Light_Rpc`'s
`updateSegment` parameter).

`RPC_Break(0)` also: disables the pillar's collider, explodes glass shards, spawns a
`ScoutmasterGhostOrbiter` that chases the interacting character, and fires
`GlobalEvents.TriggerSoulFreed(0)` then `(1)`. This starts a hazard (ghost chase), not a
purely-safe moment — irrelevant to our save/restore target since we never spawn players
at the pillar itself, only at the saved world-anchor position (see below) — but worth
knowing if anyone later wants "safe to interact" framing around this.

**Not scene-verified: how many `ScoutmasterSoulPillar` instances exist per Nadir run.**
Assumed to be exactly one (permanent one-way break, narratively "free the trapped
ghost"), but this was never confirmed against the actual level asset. If there turn out
to be several, the hook below still works per-instance (each has its own PhotonView/
`_broken` flag) — it would just mean multiple save points instead of one, which is
harmless, just worth knowing going in.

### Hooking the save (mirrors the existing campfire pattern almost exactly)

`CampfireAutoSavePatch.cs` already hooks `Campfire.Light_Rpc` (postfix on the RPC-to-all
method, not `Interact_CastFinished`, specifically because a *client's*
`Interact_CastFinished` never reaches the host directly — only the RPC does). Same
reasoning, same shape, applies directly:

- Patch target: `AccessTools.Method(typeof(ScoutmasterSoulPillar), "RPC_Break")`,
  postfix.
- Guard: only act when `type == 0` (skip the 1/2 telegraph pings, and skip if
  `charactersTryingToBreak` semantics mean this could fire more than once — verify
  `_broken`'s guard is airtight before assuming a single call).
- Master-client/offline branching, calling `OwnSaveCapture.SavePlayerOffline` /
  `OwnSaveCapture.SavePlayerCoop`: copy `CampfireAutoSavePatch.Postfix` verbatim, no
  cooldown/`RecentlyLitCampfireUntil` logic should be *needed* here (the break itself is
  already one-shot) but keep it anyway for consistency/cheap insurance.

### Save-data gaps that need fixing (found by code review, not yet fixed)

These are the concrete, scoped code changes. All in `src/PeakQuickResume/`.

1. **`AreaNameCompat.cs` will very likely mis-resolve "NADIR".** It does
   `int index = (int)segment; progressPoints[index]`. `MountainProgressHandler
   .progressPoints` is filtered per-run to only the biomes actually present
   (`InitProgressPoints`), then has `progressPoints.Last()` appended — its indexing has
   **no fixed relationship** to the raw `Segment` byte value, and `MapHandler`'s own
   internal `currentSegment` int for Void is **5**, not 6 (`JumpToSegmentLogic` does
   `if ((int)newSegment >= 5) num2--;` because Void is appended as the *6th* array
   element at index 5). **Fix:** special-case `Segment.Void` in `AreaNameCompat
   .ResolveAreaName` (or at the two call sites in `OwnSaveCapture.cs`, lines ~90 and
   ~318) to return the literal string `"NADIR"` directly, bypassing `progressPoints`
   entirely. Confirmed `"NADIR"` is a real top-level localization key (used in
   `MountainProgressHandler.GetRichPresenceState`'s `switch (p.title)` in the decompile).
   `SaveArchive.TryGetOfficialCampfireTitle` (`SaveArchive.cs` ~line 545) already has a
   documented fallback for exactly this shape — an internal name not in
   `CampfireLocKeys` gets tried as a raw `LocalizedText` key — so once `campfireName` is
   written as `"NADIR"`, the F7 picker should display it correctly **with no
   `SaveArchive.cs` changes needed**. Worth a quick in-game sanity check regardless.

2. **`OwnTeleportSequence.RunSequence` resolves `targetSegment` too early for Void**
   (~line 199-201):
   ```csharp
   MapHandler mh = MapHandler.Instance;
   int index = (int)finalSegment;
   MapHandler.MapSegment targetSegment = mh != null && index >= 0 && index < mh.segments.Length
       ? mh.segments[index] : null;
   ```
   This runs **before** `MapHandler.JumpToSegment(finalSegment)` (~line 213-214). On a
   cold load — Nadir not yet reached this session — `mh.segments.Length` is still the
   original 5 (`SetUpVoidSegment()` hasn't run yet), so `index = 6` is out of range and
   `targetSegment` safely comes back `null` (the bounds check already prevents a crash)
   — but this means Nadir's own item spawners
   (`targetSegment.segmentParent.GetComponentsInChildren<ISpawner>()`, used at ~line
   298-304) get silently skipped on load. Likely loot-respawn-only impact. **Fix:**
   re-resolve `targetSegment` *after* the `JumpToSegment` call for `Segment.Void`
   specifically (by then `mh.segments.Length` is 6 and index 5 — not 6 — is correct; see
   next item, these two fixes are linked).

3. **The `index` variable itself is wrong for Void even after the reorder above.**
   `index = (int)finalSegment` uses the raw enum ordinal (6 for Void), but the actual
   array slot Void lives at is **5**. The existing code already special-cases this for
   Peak: `if ((int)finalSegment == 5) index--;` (~line 279) — that same off-by-one logic
   needs to also cover `Segment.Void` (`if ((int)finalSegment >= 5) index--;` would cover
   both Peak and Void the same way `JumpToSegmentLogic` itself does it). Currently
   harmless for Void only because nothing downstream keys off `index` for segment ≥ 5
   *yet* — but item 2's fix will start relying on a correct index, so fix this first/
   alongside it.

4. **Scout's Honor consumption needs no special-case.** By the time a player reaches the
   pillar, vanilla's own `MapHandler.GoToVoidRoutine` (triggered by using Scout's Honor to
   get there in the first place) has already deleted it from their inventory
   (`DeleteScoutsHonorFromLocalCharacter`). So `OwnSaveCapture`'s normal inventory
   capture naturally reflects "no Scout's Honor" already — nothing to add.

5. **The pillar's own broken/unbroken state needs no save/restore work.** Breaking only
   flips a private bool on the pillar, which is a scene object with a pre-existing
   `PhotonView` — it is never destroyed or respawned by a segment jump.

   **[CORRECTED]** The original text concluded from this that "a restored Nadir checkpoint
   naturally reloads with the pillar already broken". It doesn't: loading a checkpoint
   starts a **brand-new run**, so the level scene is fresh and the pillar comes back
   *unbroken*, exactly as if the player had just arrived. That's the better outcome anyway
   (the save point is immediately re-usable), and it's still true that no save/restore work
   is needed either way. It does mean the ghost chase, the invisible walls and
   `VoidBiome.SoulFreedStatus` all reset to their just-arrived state on load, which is
   consistent with how every other biome reloads.

6. **Capture radius.** The maintainer's original ask mentioned wanting a bigger capture
   radius for Nadir specifically (spawns can be far from the statue). Existing radius
   constants, all anchored on the **player's position at save time**
   (`OwnSaveCapture.ResolveWorldAnchor`, not the statue's position):
   `AncientStatueRestore.StatueSearchRadius` = 100f, `LuggageRestore
   .LuggageSearchRadius` = 30f, `WorldItemRestore.SearchRadius` = 30f,
   `DeployableRestore.SearchRadius` = 30f, `CampfireAreaHelpers.CampfireSearchRadius` =
   30f. "Increase the radius for Nadir" = conditionally raise these when
   `segment == Segment.Void`. ~~**Explicitly deferred by the maintainer**~~ — **resolved
   2026-08-15: 80m, and anchored on the pillar rather than on the saving player.** See
   `NadirSearchRadius` in the implementation section below.

7. **Position/anchor capture needs no change.** `OwnSaveCapture.ResolveWorldAnchor` is
   already biome-agnostic (local player's head → any living player → previous campfire →
   local player regardless). Works for Nadir as-is, and since the interact happens right
   at the pillar, the captured position naturally lands next to the statue.

### Vanilla wipes every status effect on entry to Nadir (answered 2026-08-16)

The question, from testing: a Nadir save restores with essentially no status effects — is
that the game, or is the mod losing them? **It's the game, and the mod never touches them.**

`MapHandler.GoToVoidRoutine(bool clearStatus)` — the coroutine behind entering Nadir — clears
everything twice on the way in:

```csharp
public static IEnumerator GoToVoidRoutine(bool clearStatus) {
    DeleteScoutsHonorFromLocalCharacter();
    yield return null;
    Character.localCharacter.refs.afflictions.ClearAllAfflictions();   // unconditional
    Character.localCharacter.refs.afflictions.ClearAllStatus();        // unconditional
    ...
    if (clearStatus) {
        Character.localCharacter.refs.afflictions.ClearAllAfflictions();
        Character.localCharacter.refs.afflictions.ClearAllStatus();
        Character.localCharacter.refs.afflictions.AdjustStatus(STATUSTYPE.Petrify, -0.2f);
    }
```

Its only two call sites are both in `Action_WarpToBiome.RPC_Warp`, both passing
`clearStatus: true`, and that RPC is `RpcTarget.All` — so every player runs it against their
own `localCharacter`. Arriving in Nadir organically therefore always leaves the whole party
on a clean status bar.

Nothing on the mod's load path replays that. `OwnTeleportSequence` calls
`MapHandler.JumpToSegment` (co-op) / `SetSegmentOnSpawn` (solo), never `GoToVoidRoutine`, and
`JumpToSegmentLogic` doesn't reference `afflictions` at all — checked line by line. The only
`ClearAllStatus`/`ClearAllAfflictions` in this codebase is inside `ReviveDeadPlayers`'
local-only revive fallback, mirroring `Character.ReviveCharacter`, and it only runs for a
character that is actually dead or passed out.

Corroborated by the save files themselves: both Nadir saves from the 2026-08-16 co-op session
have every entry of `afflictions_current` at `0.0` except index 7, which is `Weight` — and
`Weight` isn't stored state, it's recomputed from carried items every frame, exactly like
`Thorns`. So the statuses were already gone *before* the commune wrote the file, and the
restore is faithfully putting back the nothing that was saved.

One real asymmetry falls out of this, in the opposite direction from the worry: a modded load
into Nadir restores whatever statuses the save holds, while an organic arrival always wipes.
So a party that spends a long time down there, gets hungry, and then communes will get that
hunger back on load. That's correct checkpoint behaviour (the save is of the moment of the
commune, not of the arrival), just worth knowing it differs from what Scout's Honor does.

### How Scout's Honor loads Nadir (context only — not part of the save/load feature)

Documented for completeness/debugging reference, not because the save feature needs to
touch this: `ScoutsHonor : AmuletBase` (empty subclass) is crafted at a **different**
object, `ScoutStatue` (the 4-amulet pedestal), then consumed via what's very likely an
`Action_WarpToBiome` `ItemAction` (`segmentToWarpTo = Segment.Void`) wired onto the
`ScoutsHonor` prefab in the editor — not scene-verified, but there's no other plausible
caller and the log line `"WE'RE GOING TO THE SHADOW REALM BABY"` plus
`GoToVoidRoutine`'s immediate `DeleteScoutsHonorFromLocalCharacter()` call both point the
same direction.

## Suggested implementation order

The original plan, kept for context. Steps 1-3 are done and steps 4-5 passed in solo; step
7 turned into pass 2. Step 6 (co-op) is the whole remaining job - see "Next session" above
for the expanded version of it.

1. `ScoutmasterSoulPillarAutoSavePatch.cs` (new file, mirrors `CampfireAutoSavePatch.cs`)
   — patch `RPC_Break`, guard `type == 0`, call the existing `OwnSaveCapture` functions.
2. Fix `AreaNameCompat`/`OwnSaveCapture` call sites for the `"NADIR"` name (gap #1).
3. Fix the `targetSegment`/`index` ordering in `OwnTeleportSequence` for `Segment.Void`
   (gaps #2 + #3).
4. In-game test, solo first: reach the pillar, commune, confirm a save file with
   `segment: "Void"`/`campfireName: "NADIR"` gets written, confirm the F7 picker shows
   "NADIR" (or its localized text) correctly.
5. In-game test: load that save from a **fresh run start** (never having organically
   reached Nadir this session) — this is the scenario the "hard lock from Airport"
   finding was specifically about, and per the mental model above it should now work
   fine since the load always happens after the level scene is loaded, but it's the one
   part of this plan resting on inference rather than a direct empirical test.
6. In-game test: coop — commune as a client (not host), confirm the host receives the
   RPC and writes the save; load as host, confirm every client gets teleported/restored
   correctly.
7. ~~Only after all of the above: revisit the radius question (gap #6)~~ — done ahead of
   co-op testing, at the maintainer's request, as pass 2.

## Open items / genuinely unverified

- **Nadir's real `initialWaitTime` and `travelTime` on the `VoidGhosts` `LavaRising`.**
  Both are scene-serialized, so the decompile only shows the class defaults (`1f` and `60f`),
  which are almost certainly not the shipped values. These decide how much grace the rising
  field actually gives and therefore how much the pass-3 hold is worth. Cheapest way to find
  out: log them once from the live instance after a Nadir jump, e.g. off
  `NadirRisingField.Find(...)`. Also worth confirming `waitForEvent` is set on that instance
  (the implementation deliberately doesn't depend on it, but it would confirm the mental
  model) and which `Lava` subclass is on the moving object, which is the other thing the
  decompile can't answer.
- Exact count and placement of `ScoutmasterSoulPillar` instances in the live level asset
  (attempted via UnityPy this session, stalled on cross-file `MonoScript` PPtr
  resolution — see `docs/RESEARCH.md`'s "Nadir biome" section for what was tried; a
  throwaway venv is at `scratch/venv-unitypy/` if picking this back up is worth it, but
  in-game testing/logging is probably faster than fighting UnityPy further).
- Whether `Action_WarpToBiome` is really the `ScoutsHonor` prefab's wiring — still not
  scene-verified, but narrowed: `Action_WarpToBiome.RPC_Warp` is now confirmed as the *only*
  caller of `MapHandler.GoToVoidRoutine` in the decompile, and it's the one that deletes
  Scout's Honor, so the remaining doubt is only about which prefab references it. Irrelevant
  to the save/load feature either way; only matters if someone later wants to touch the
  *entry into* Nadir rather than the save point.
- ~~Whether `charactersTryingToBreak`/the type 1/2 telegraph RPCs can ever race with type
  0 in a way that fires our save-hook postfix more than once in coop~~ — closed by
  construction: the patch reads `_broken` in a prefix and only saves on the call that
  actually flipped it false→true, so a simultaneous double break saves once (see below).

## What was actually implemented

Four files, all in `src/PeakQuickResume/` unless noted. Builds clean; **nothing here has
been tested in-game yet** — the checklist in "Suggested implementation order" steps 4-7 is
still entirely open.

### `ScoutmasterSoulPillarAutoSavePatch.cs` (new)

Structurally a copy of `CampfireAutoSavePatch`, wired in from `Plugin.cs` right after it,
with three differences:

- Patches `Peak.ScoutmasterSoulPillar.RPC_Break` instead of `Campfire.Light_Rpc`.
- Guards `type == 0` (the real break; 1/2 are the hold/cancel telegraph pings).
- Adds a prefix that stashes the pillar's private `_broken` value in `__state`, and only
  saves when the postfix sees `false → true`. Vanilla's own idempotence guard lives inside
  `SetBroken`, not in `RPC_Break`, so two players finishing the 2s hold on the same frame
  would otherwise run the postfix twice. This also means the patch degrades to "never
  saves" rather than "saves wrongly" if another mod's prefix skips the original.

`Apply` fails soft at every step: a missing `RPC_Break` disables just this save point with
a warning; a missing `_broken` field falls back to the shared cooldown alone. The
`ArmRecentlyLitCampfireCooldown(32f)` / `ArmRecentlyLoadedCooldown(30f)` /
`RecentlyLitCampfireOthers()` calls and the master-client gate are kept verbatim from the
campfire patch, so the Nadir save point behaves identically to every other one.

### `AreaNameCompat.ResolveAreaName`

`if (segment == Segment.Void) return "NADIR";` at the top, before the `progressPoints`
lookup — gap #1. Both `OwnSaveCapture` call sites go through here, so neither needed
touching, and `SaveArchive.TryGetOfficialCampfireTitle`'s raw-key fallback turns `"NADIR"`
into the localized picker label with no change there either (as predicted above). Vanilla
avoids indexing `progressPoints` by the Void ordinal for the same reason — see
`CharacterSpawner`'s reconnect path, which skips `DisplaySegmentTitleAfterDelay` outright
when the current segment is `Void`.

### `OwnTeleportSequence.RunSequence`

Three changes, all `Segment.Void`-gated so no other segment's behaviour moves:

- `if ((int)finalSegment == 5) index--;` widened to `>= 5`, matching vanilla
  `JumpToSegmentLogic`'s own fixup — gap #3.
- `targetSegment` re-resolved from `mh.segments[index]` **after** the jump, Void only, so
  Nadir's `ISpawner`s actually run on a repeat load — gap #2. Deliberately *not* extended
  to Peak: Peak resolves to `null` today too, and pointing it at the Kiln's segment would
  start respawning Kiln loot on a Peak load, which is a behaviour change unrelated to this
  feature.
- Solo only: `OrbFogHandler.SetFogOrigin(index)` for Void, in a try/catch. Coop reaches
  Nadir via `MapHandler.JumpToSegment`, which moves the fog origin itself (index 5 is past
  the last origin, so vanilla just switches the fog sphere off); the solo branch uses
  `SetSegmentOnSpawn`, which never touches fog and would leave the sphere growing from the
  starting biome. Purely cosmetic parity with vanilla's own Nadir entry.

Plus one defensive guard that isn't in the plan above: if a Nadir checkpoint is loaded into
a level scene with no `VoidBiome` at all, the jump is skipped and `savedPos`/`spawnPos` are
retargeted to the local character's current position, with a loud error. Vanilla's
`JumpToSegmentLogic` would null-ref halfway through in that case, leaving the segment
un-activated while our warps still fired — dropping everyone into un-instantiated geometry
with nothing to stand on. Should be unreachable on stock PEAK 2.0.a+, but the failure mode
is bad enough (and cheap enough to rule out) to be worth the eight lines.

### Second pass, 2026-08-15 (after the maintainer's first solo test)

Loading was reported working with no issues; these two are restore-side follow-ups from
that test. Solo-focused by request, but both are written to hold up in co-op.

#### 80m capture radius, anchored on the pillar — `NadirSearchRadius.cs` (new)

Closes gap #6. Maintainer's findings from testing: Nadir ships with **no** physics
items/objects, **no** luggage and **no** randomly spawned items — everything loose down
there was carried in and dropped by a player, and a party can only carry so much. That's
what makes a wide radius affordable: the item count stays tiny no matter how large the
radius, so the usual "don't scan half the map" concern doesn't apply.

- `NadirSearchRadius.Radius` = 80f, applied via `ForCurrentSegment` (capture, reads the
  live `MapHandler`) and `ForSavedSegment` (restore, reads `data.segment` so the restore
  searches the same radius the capture used even if the segment jump didn't take).
  Combined with `Mathf.Max`, so it can never *narrow* an existing radius —
  `AncientStatueRestore`'s 100f stays 100f.
- Wired into `WorldItemRestore` (dropped items + backpacks), `LuggageRestore` and
  `DeployableRestore` (portable stoves + scout cannons). Luggage and the deployable
  *capture* have nothing to find in Nadir today; they're covered anyway so a future update
  that adds them needs no second pass through here.
- `CampfireAreaHelpers.ResolveNearestCampfirePos` now falls back to the nearest
  `ScoutmasterSoulPillar` when no campfire is in range **and** the run is in Nadir. This is
  the part that makes the radius mean what the maintainer asked for: "80m of the pillar",
  not 80m of wherever the saving player happened to be standing. It also makes capture and
  restore centre on the identical fixed point instead of two positions that merely happen
  to be close. Outside Nadir it's unreachable — the pillar only exists in the Void segment,
  which is inactive everywhere else, so `FindObjectsByType` can't see it — and it's gated
  on the segment anyway.
- `MaxItems`/`MaxPerType` caps left alone. They're anti-abuse limits, not tuning, and a
  party physically cannot drop 50 items.

#### Pre-commune on load — `OwnTeleportSequence.PreCommuneWithScoutmasterSoul`

A Nadir checkpoint can only exist in a world where somebody already communed (that's the
save hook), so the restore now replays it: the host sends the same `RPC_Break(0)` a
player's 2s hold sends, against the pillar nearest the checkpoint position. Result on load
— invisible walls already down, ghost scoutmaster already orbiting, rising-souls hazard
already under way.

The maintainer's reason for wanting this is exploit closure: leaving the pillar intact
would let a loaded save hand Nadir's story beat to a friend, repeatedly, with none of the
climb. Breaking it up front leaves nothing to re-commune with.

**On "skip the achievement unlock trigger if needed" — not needed, and there is nothing to
skip.** Traced the whole break path: `RPC_Break(0)` → `SetBroken` → `BreakTriggeredRoutine`
→ `GlobalEvents.TriggerSoulFreed(0)` then `(1)`. `OnSoulFreed`'s only subscribers in the
entire decompile are `VoidInvisWall.TestSoul` (drops the walls), `VoidBiome.SetSoulState`
(sets `SoulFreedStatus`) and `LavaRising.TestSoul` (starts the rising souls). No
`ThrowAchievement` anywhere on that path, and none in `ScoutmasterGhostOrbiter` either —
it's purely a cosmetic soul that lerps toward the highest living character. Nadir's two
badges are elsewhere: the NADIR progress point's own area badge (granted for *being* in
the biome, which a load does regardless of the pillar) and `RuleZeroBadge`, thrown from
`RunManager.Win` when `MapHandler.inNadir` — i.e. for actually winning via Nadir. The
commune itself grants nothing, so pre-communing hands out nothing.

Supporting pieces:

- `ScoutmasterSoulPillarAutoSavePatch.SuppressNextBreak(10f)` is armed immediately before
  the RPC. Without it the restore's own break comes straight back through the autosave
  postfix and writes a save mid-load. It's a time window rather than a flag so it can't get
  stuck on; the check sits before the cooldown arming, so a suppressed break leaves no
  trace at all.
- Fired right after the segment jump rather than at the end of the sequence, so the break
  routine's ~6s of staged waits elapse behind the loading screen instead of in the player's
  face after the reveal.
- Exactly one pillar is broken (nearest to the checkpoint position). The walls listen to a
  *global* event, so one is enough to drop all of them, and breaking several would spawn
  several ghosts. This is the answer to the "how many pillars are there?" open item: the
  implementation no longer cares.
- `EnsureSoulFreed` failsafe. Only the master client runs the ghost-spawn block, and it
  dereferences the interactor's `Character` ~4s after the fact; if that reference ever went
  stale the coroutine would throw *there* and never reach `TriggerSoulFreed`, leaving the
  host's walls up while every client's dropped. Since Nadir's walls gate the way out, that
  would strand the host — exactly the class of lock-in to avoid. So: 12s later, if
  `VoidBiome.SoulFreedStatus` is still `-1`, fire the event locally. Both listeners are
  idempotent (`StartWaiting` guards on its own timer, the wall setter no-ops when
  unchanged), so a false positive costs nothing.
- Idempotent on a repeat load: if the pillar is already broken, vanilla's `SetBroken`
  no-ops, so no second ghost and no duplicate anything.
- Co-op: the RPC is `RpcTarget.All`, and a client that misses it still gets `_broken` via
  the pillar's `OnPhotonSerializeView`, which is vanilla's own late-joiner path. Clients
  run the postfix but return at the master-client gate before writing anything.

### Third pass, 2026-08-15 — holding the rising field until everyone has control

**Uncommitted at handoff, and untested.** `NadirRisingField.cs` (new) plus
`OwnTeleportSequence.HoldRisingFieldUntilEveryoneHasControl`.

The pre-commune arms Nadir's rising "anti-lava" field as a side effect, and that turned out
to matter. Confirmed mechanics: it's a `LavaRising` with
`risingFieldType == VoidGhosts`, armed *only* by the commune (`LavaRising.TestSoul` is
subscribed to `GlobalEvents.OnSoulFreed` and calls `StartWaiting()` on phase 1). The master
client then counts `secondsWaitedToStart` up to the field's `initialWaitTime`, flips
`started`, and every machine simulates the climb locally off `timeTraveled`, with the host
re-syncing via `RPC_SyncLava` on the start transition and every 15s. Damage is a
`Lava`-family component on the moving object: Injury 0.25 + Hot 0.25 per hit on a 1s
cooldown (`IHoldPlayer`), so roughly half a status point per second, i.e. dead in about
four seconds from full - and it stops hitting once `statusSum > 1.9`. `initialWaitTime` and
`travelTime` are scene-serialized, so their real Nadir values are **still unknown** and
worth logging once in-game.

Firing the pre-commune early (which is right, for the walls and the ghost) therefore started
the hazard clock while everyone was still behind a loading screen and collapsed into the
wake-up pose, unable to run. So `HoldRisingFieldUntilEveryoneHasControl` parks the field from
the pre-commune and releases it when every player can actually move.

- Park = `started = false; ended = false; timeTraveled = 0; secondsWaitedToStart = 0`,
  re-applied every frame. Per-frame matters twice over: the break routine's own
  `TriggerSoulFreed(1)` lands several seconds after the RPC was sent, and zeroing
  `secondsWaitedToStart` each frame means the host's Update can never accumulate past
  `initialWaitTime` no matter what tries to arm it. That last part is why nothing here
  depends on assuming the Nadir instance has `waitForEvent` set.
- No transform work needed: `LavaRising` recomputes its height from scratch every frame as
  `Mathf.Lerp(startHeight, top, timeTraveled / travelTime)` rather than accumulating, so
  zeroing `timeTraveled` puts it back at the bottom on the release frame by itself.
- `BroadcastParked` pushes the parked state to clients immediately rather than waiting out
  the host's 15s sync tick. Only matters on a repeat load into a run where the field was
  already climbing, where a client left on `started == true` would keep raising its own copy.
- **Release condition is "everyone has control", not "everyone is connected."** `IsRunning`
  covers the host (false at the end of the sequence, and also on a throw, since
  `RunSequenceWrapper` resets it either way); `OwnNetwork.AllClientsPresentationDone()`
  covers the clients, each of which RPCs that itself at the end of
  `RunClientPresentationExit`, i.e. after its own loading screen faded out and its own
  wake-up animation finished. That call already exempts players without the mod, who never
  had an overlay and have had control since before the segment jump. The client wait is
  skipped entirely when the wake-up presentation is disabled, since clients never send the
  ack in that case.
- **Nothing can stall it forever.** `RisingFieldHoldCeilingSeconds` = 90s absolute, measured
  from the pre-commune, past the host's own worst case (32s client-warp hold plus fades) and
  past `ResumeOrchestrator`'s 60s tail timeout for the same client-presentation wait. A
  client stuck on an infinite Photon loading screen, a mid-load disconnect, or an aborted
  restore all end with the hazard armed rather than with Nadir losing its one mechanic.
- A second load starting inside the previous hold's ceiling supersedes it
  (`_risingFieldHold` is stopped first), so an older hold can't release the field in the
  middle of a newer restore.

Known and accepted: this makes a loaded run marginally easier than the true saved state,
since in vanilla the ~6s commune cutscene runs with the clock already ticking. Against a
travel time that is probably a minute or more, not worth compensating for.

### Fourth pass, 2026-08-16 — whose commune it was

**Uncommitted at handoff, and untested.** `NadirCommuner.cs` (new), plus changes to
`ScoutmasterSoulPillarAutoSavePatch`, `OwnSaveCapture.ResolveWorldAnchor`, `OwnSaveData` and
`OwnTeleportSequence.PreCommuneWithScoutmasterSoul`. Both halves come out of the first co-op
test: the commune has an owner, and passes 1-3 quietly assumed that owner was the host.

**The premise.** Every other checkpoint in the game is a campfire — a fixed world object that
can only be lit with the whole party gathered within a few metres of it. That's what makes
"anchor the save on the host" free everywhere else: the host *is* at the checkpoint, because
the game refused to let anyone light it otherwise. The soul pillar has no gather requirement
at all. Anybody can commune from wherever they're standing, with the rest of the party
arbitrarily far away, dead, or falling through the void — which the co-op log shows plainly:
the save's own position was the host at `(-1.50, 732.12, 157.47)`, the biome's entry spawn,
while the pillar (and the player who communed) was 43.7m away at `(3.66, 742.69, 199.54)`.
Everyone got warped to the entry spawn on load. A host who had dived into the void would have
taken the whole party with them.

- **Capture.** `ScoutmasterSoulPillarAutoSavePatch.Postfix` now also takes vanilla's
  `PhotonView view` parameter — the interacting character's view, exactly as
  `Interact_CastFinished` sent it, so the host resolves the *client* who communed even though
  the host is the machine writing the file. It arms `NadirCommuner` with that character
  immediately before saving and clears it in a `finally`.
- `OwnSaveCapture.ResolveWorldAnchor` checks `NadirCommuner.TryGetPendingAnchor` first and
  returns the communing player's head. That one change fixes the teleport target, and with it
  everything else anchored off `posX/Y/Z`. The pillar-anchored search radius from pass 2 was
  already immune (it re-centres on the pillar via `CampfireAreaHelpers`), but it re-centres
  by searching *within 80m of the anchor* — so a host far enough away would have failed to
  find the pillar and silently fallen back to their own position. Now the anchor is at the
  pillar by construction.
- **Save shape.** Two new host-file-only fields, `nadirCommunerUserId` and
  `nadirCommunerName`. Additive, so old saves deserialize unchanged and read as null, which
  the restore treats as "the host stands in".
- **Restore.** `PreCommuneWithScoutmasterSoul` now takes `data` and sends
  `RPC_Break(0, interactor.photonView)` with the saved player's view instead of the host's.
  This is not cosmetic bookkeeping: `BreakTriggeredRoutine` hands that same view straight to
  `ScoutmasterGhostOrbiter.SetOrbitCharacter`, so the view decides which player the
  scoutmaster's ghost circles for the rest of the run.
- Matching is by user id (`NetworkingUtilities.GetUserId`), the same key the save store itself
  is filed under, so it survives actor numbers being reshuffled between sessions. Offline
  short-circuits to the local character. A saved player who isn't in the session falls back to
  the host with a warning, and nothing else about the restore changes.

**Correction to the third pass's notes:** they described `ScoutmasterGhostOrbiter` as "purely
a cosmetic soul that lerps toward the highest living character". That's only its *fallback*.
`Update` follows `orbitCharacter` — the character it was handed — and only calls
`PickOrbitCharacter` (highest living) once that character is null or dead. The conclusion
drawn there still holds (there's no achievement anywhere on the break path, so pre-communing
gives nothing away); it just isn't true that the orbit target sorts itself out. Hence this
pass. The same mechanism is why a communer who dies later needs no handling: the master client
re-picks on its own.

### What was deliberately *not* changed

- The offline `MapHandler.SetSegmentOnSpawn(finalSegment, (int)finalSegment)` call still
  passes the raw ordinal 6 as `lastRevivedSegment`. Vanilla does the same thing —
  `JumpToSegmentLogic` sets `LastRevivedSegment = (int)statue.SegmentNumber`, also the raw
  ordinal — and every consumer of it (`BaseCampHasRevived`, `CurrentBaseCampSpawnPoint`,
  `PreviousCampfire`, `PreviousScoutStatue`) short-circuits on `VoidBiome.VoidBiomeActive`
  before the value is ever read. Changing it would diverge from vanilla for no gain.
- ~~Capture radii (gap #6) — still deferred~~ — done in the second pass above.
- `biome_names`/`BiomesSummary` still report the run's deepest *biome* (the Kiln), not
  Nadir, on a Nadir save. Cosmetic, and already true of Peak saves.
