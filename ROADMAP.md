# ROADMAP — PEAK Quick Resume

> One-key resume for PEAK: press a key to start a fresh run of your saved
> difficulty and immediately load your "PEAK Checkpoint Save" checkpoint.
> This mod is an **orchestrator** — dominik0207's *PEAK Checkpoint Save* still
> does all real saving/loading; we just automate the tedious manual steps.

**Status:** v1.0.0 published-candidate — Phases 1–4 + Phase 5 mechanics 1 (custom
runs) & 2 (F7 save picker) working in-game (solo + 2-player coop). Mechanic 3
(reset run) still TODO. **Phase 6 (teleport-bug mitigation) in progress on
branch `feature/teleport-mitigation`: Steps 1–3 done and confirmed working
in-game, Step 4 is next** — see "Handoff notes for the next session" at the
bottom of this file for exactly where to pick up. **Phase 7 (boarding-pass
island-toggle button) added on the same branch, session 10, NOT YET IN-GAME
TESTED** — see below. **Phase 8 (own save/load/teleport, drop the runtime
dependency on PEAK Checkpoint Save) got a full, detailed execution plan on
branch `feature/phase8-independent-saveload` (session 11, 2026-07-09) — see
that section below for the milestone breakdown (M0-M9). M0 (save-file
compatibility layer) is done and verified (session 12): 110/110 real save
files round-trip losslessly through `OwnSaveData.cs`/`OwnSavePaths.cs`. M1
(own PhotonView/RPC channel, `ViewID=69420`) is implemented and confirmed
in-game (solo + solo-hosted coop, session 13). M2 (own load entry points,
not yet wired into the live resume path) is next.**
**Last updated:** 2026-07-09 (session 13).

## Phase 7 — boarding-pass island-toggle button (session 10, untested)

The checkpoint mod's own boarding-pass overlay has a tiny, unlabeled checkbox
(small blue square, smaller green inner square, top-left next to its
`LEVEL: USING...` text) that toggles between loading your saved island (with
its biomes) and a new/daily one. Easy to never notice it's clickable at all.

Added `IslandToggleButton` (MonoBehaviour, polled every frame, no Harmony hook
needed): a big labeled toggle switch, bottom-right of the boarding pass screen,
that mirrors and drives the checkpoint mod's own hidden `Toggle` component
(`CheckpointInterop.TryGetBoardingToggle()`, new reflection member). Visibility
and on/off state are read from that real Toggle's own `gameObject.activeInHierarchy`
/ `isOn` every frame rather than hooked via Harmony on the checkpoint mod's various
`OnOpen`/`OnClose`/`HideIt`/`StartGame`/`UpdateAscent` call sites, several of which
toggle the checkbox's visibility as a follow-up statement AFTER calling
`ShowBoardingpassMessage` (e.g. hiding it when no savefile exists) — polling the
real checkbox's own state sidesteps needing to replicate every one of those sites.
Clicking sets the real `Toggle.isOn`, which fires the checkpoint mod's own
listener exactly like a real click would (persists its config, refreshes its
own overlay text) — we add no toggling logic of our own, just a bigger, obvious,
self-explaining door to the same setting.

New files: `IslandToggleButton.cs`, `UiShapes.cs` (runtime-generated rounded-rect/
circle sprites, no bundled art), `IslandToggleLocalization.cs` (15-language table,
same pattern as `PauseMenuLocalization`/`SavePickerLocalization`). Gated by new
config `ShowIslandToggleButton` (`Boardingpass` section, default true).

**Compiles cleanly but is UNTESTED in-game** (maintainer cannot run the game
themselves, see "Handoff notes" below) — needs a real boarding-pass screen with
a savefile present to confirm: the button appears bottom-right, position/size
look right at actual game resolution, clicking it actually flips the island and
the checkpoint mod's own text updates, and it correctly disappears in the
"no savefile found" case.

---

## Phase 8 — own save/load/teleport, drop the PEAK Checkpoint Save runtime dependency (planned in full 2026-07-09, not started)

**Maintainer decision, 2026-07-08 (context), scope locked down 2026-07-09:**
after fully reverse-engineering why `teleportJumpLogic` 0/1/2 behave the way
they do (see `docs/RESEARCH.md`), the conclusion was that the checkpoint mod's
teleport/scene-sync path is fragile by construction (ad-hoc per-case
RPC/`CustomCommands` patchwork, not a coherent design) and we've been carrying
the cost of that fragility (Phase 6's whole watchdog/mitigation system exists
only to paper over it) without being able to actually fix the root cause,
since it's not our code. On top of that: PEAK is expecting a big content
update, the checkpoint mod has had no updates in 3 months and is left in a
state that already needs our mitigations to work reliably, and we have no
source access to it — every future game update risks silently breaking our
`CheckpointInterop` reflection with no way to fix the checkpoint mod's half.
**Decision: port the checkpoint mod's own save/load/teleport logic into this
mod as fully our own code, so the only remaining external dependency is
BepInEx itself.**

**2026-07-09 refinement of the approach (session 11 planning), locking in
three decisions after discussion with the maintainer:**
1. **Fidelity first, optimization later.** The very first working version of
   our own teleport/restore must be a **near-literal port** of the checkpoint
   mod's own `CustomJumpToSegment` coroutine — same retry-loop shape, same
   `teleportJumpLogic` 0/1/2 branches, same wait-frame cadence, same
   `TeleportClientsToHost` correction loop — not the "cleaner"
   `MapHandler.JumpToSegment`-only design an earlier draft of this section
   sketched. Reasoning: the mod already works perfectly in real testing today
   (multiple islands/biomes/lobby sizes/items, confirmed by the maintainer),
   and every mitigation we've built in Phase 6 (`TeleportWatchdog`,
   `TeleportConfigOverride`, warp suppression) was tuned against *this exact*
   retry-loop shape. Jumping straight to a cleaner design would both change
   behavior we haven't verified is equivalent AND require re-tuning or
   rewriting all of Phase 6 at the same time as the port, which is exactly the
   kind of two-things-at-once risk we want to avoid. Optimization is an
   explicit **second pass**, after the fidelity port is proven solid in-game
   across several sessions (see "Second-pass optimization ideas" at the end of
   this section — noted now so they aren't lost, not acted on yet).
2. **Keep PEAK Checkpoint Save installed during development.** Our own code
   stops *calling into* it, but it stays installed on the test profile so its
   save files/behavior can be diffed against ours, and so there's an immediate
   fallback if a bug is found in our new path mid-development. Only drop the
   hard BepInEx dependency once the new system is proven solid across several
   sessions (unchanged from the original plan's "Watch out for" note below).
   **Consequence:** our own `PhotonView` must use a `ViewID` that cannot
   collide with the checkpoint mod's own hardcoded `19420` (decompile line
   992, `CreatePhotonView`) — pick something far away, e.g. `194200`, and
   double check it doesn't collide with anything else PUN auto-assigns.
3. **First coding milestone is the save-file compatibility layer, not a
   minimal load path.** Write our own `SaveData`/`SavedItemState`/
   `SavedBackpackItemState`/`SavedEntry` model and JSON reader/writer, matching
   the existing shape field-for-field, and verify it round-trips real,
   existing save files byte-for-byte (or at least semantically identical,
   Newtonsoft doesn't guarantee byte-identical key ordering) **before writing
   a single line of restore/capture logic.** This is verifiable without any
   in-game test (just feed it real files from the maintainer's own save
   folders), which matters a lot given every other milestone needs a full
   build→deploy→maintainer-tests→report loop.

### What the decompile actually showed (corrections to the original draft above)

Re-reading the full decompile (`scratch/decomp/checkpoint/PEAK_Checkpoint_Save.decompiled.cs`,
4775 lines) end to end for this plan turned up a few things the original sketch
above got wrong or left too vague. These change the *risk profile* of the port,
so recording them explicitly:

- **The "world object state" list is destroy-only, not restore.** The original
  draft said the `ChainShootable`/`RopeAnchor`/`RopeDynamic`/
  `PortableStovetop_Placed`/`ClimbingSpikeHammered`/`CloudFungus`/
  `Flag_Planted_Checkpoint`/`ShelfShroom`/`ScoutCannon_Placed`/
  `BounceShroomSpawn`/`MagicBean` list (decompile ~2441-2504) "needs its own
  inventory of what state each of these actually needs restored." **That's not
  what the code does.** It's a cleanup pass: after a load, on the master
  client only, it walks every `GameObject` in the scene and
  `PhotonNetwork.Destroy()`s any whose name contains one of those strings, has
  a non-room `PhotonView` with `CreatorActorNr > 0` (i.e. was player-placed
  this session, not part of the baked scene) — same pattern immediately
  followed by a separate destroy pass for stray `MagicBeanVine` objects
  (~2485-2503). This is the same idea as `ResetLuggage`/"ResetDroppedRuntimeItems"
  (decompile ~3527-3627, logged as "ResetWorldLoot"): the freshly-loaded level
  starts from a clean slate (chests/luggage reset via reflective
  duck-typing on field/method names like `opened`/`isOpened`/`Reset`/`Refresh`,
  dropped non-player-holding `Item`/`ItemPickup` objects with a real
  `PhotonView` destroyed outright) and then `ISpawner.TrySpawnItems()`
  (called on every `ISpawner` in the target segment, ~2415-2426) deterministically
  re-populates segment loot. **Nothing about ropes/chains/hammered spikes/
  planted flags/etc. is saved or restored from the save file at all** — they're
  just deleted so a re-load doesn't end up with duplicates or orphaned
  player-placed props from a different timeline. This turns what looked like
  the single highest-unknown, highest-risk part of the port into one of the
  simplest: a straight destroy-by-name-substring loop, no state schema to
  reverse-engineer.
- **Inventory/backpack item-state restore has zero checkpoint-mod-specific
  dependency once you look past the wrapper methods.** `TryGetEntryObject`/
  `TryReadEntryNumeric`/`TryWriteEntryNumeric`/`TryGetKey`/`TryConvertToFloat`
  (decompile ~3258-3480) all reflect into **game types**, not checkpoint-mod
  types: `ItemInstanceData`'s own private dictionary field (reflected via a
  cached `FieldInfo`, referred to in our existing `CheckpointInterop` as
  `_iidDataField` — actually resolved against `ItemInstanceData` itself, not
  the checkpoint mod), and `DataEntryKey`, a vanilla game enum. The only
  checkpoint-mod-*owned* piece of this whole subsystem is the fixed list of 13
  key names it happens to read/write (`ItemStateKeyNames` in our own
  `CheckpointInterop.cs`: `ItemUses`, `PetterItemUses`, `UseRemainingPercentage`,
  `CookedAmount`, `Fuel`, `Color`, `Scale`, `value__`, `Used`, `SpawnedBees`,
  `ScreamTime`, `FlareActive`, `InstanceID`) and the hardcoded
  `ExcludedItemIds` list (`{100, 58, 66, 2, 24, 104, 115, 17, 63, 64}`, decompile
  line 891 — skips `ItemUses`/`UseRemainingPercentage` specifically for these
  IDs; item names behind these IDs aren't labeled in the decompile, treat as
  an opaque literal to copy verbatim rather than a list to second-guess).
  **We already half-built this** — `BackpackSaveMitigation.cs`/
  `CheckpointInterop.ReadItemStateValues` reach this exact functionality today
  via reflection into the *checkpoint mod's* wrapper methods purely so we
  didn't have to duplicate the reflection-into-`ItemInstanceData` logic
  ourselves. Porting this properly means reimplementing those 5 small
  reflection helpers directly against `ItemInstanceData`/`DataEntryKey`
  (genuinely simple, game-type-only reflection, no dependency on the
  checkpoint mod surviving at all) and retiring the wrapper-method reflection
  in `CheckpointInterop`.
- **`AddItemToInventory`/`AddItemToInventory_GetSlot`** (decompile ~3213-3525)
  reflect into `Player.AddItem(ushort, ItemInstanceData, out ItemSlot)` — a
  private/internal vanilla `Player` method, again a pure game-type reflection
  target, no checkpoint-mod dependency.
- **`ReviveDeadPlayers`** (decompile ~2760-2779) is *fully public, vanilla-only*
  code already — `Character.data.dead/passedOut/fullyPassedOut/deathTimer/
  sinceGrounded` (public fields) and `CharacterAfflictions.ClearAllStatus/
  RemoveAllThorns/ClearAllAfflictions` (public methods). Trivial, no-risk port,
  no reflection needed at all.
- **The `PhotonView` for the checkpoint mod's own RPC channel is a manually
  created, non-scene `GameObject` with a hardcoded `ViewID = 19420`**
  (`CreatePhotonView`, decompile ~961-1003), `DontDestroyOnLoad`'d, holding a
  `CheckpointNetwork : MonoBehaviourPun` component with these RPCs (decompile
  ~461-695), which is the **complete surface** we need to replicate for our own
  channel:
  | RPC | Purpose | Do we need it? |
  |---|---|---|
  | `RPC_SendModVersionToMaster` | optional client mod-version mismatch warning | **Skip** — we don't enforce a mod-version check today (`docs/RESEARCH.md` C4 already notes we bypass this), no reason to add it now |
  | `RPC_SendReadyStatusToMaster` | client → host "I'm ready" for the readiness gate | **Port** — needed for our own `CheckReadyStatusForPlayers` equivalent |
  | `RPC_RequestSave` | client → host "please autosave now" (from a lit campfire) | **Port** — needed for our own campfire-autosave patch |
  | `RPC_RecentlyLitCampfire` | host → clients, syncs the 32s re-light cooldown | **Port** |
  | `RPC_RequestFalldamageProtection` | any → all, starts the fall-damage-immunity window | **Port** |
  | `RPC_SendMessage` | host → clients, mirrors an on-screen message | **Port** (or reuse our own message overlay path if we build one — see M7) |
  | `RPC_Loadingscreen` | host → clients, toggles the loading-screen overlay | **Port** |
  | `RPC_SetHeroTitle` | host → clients, plays the "reached new area" banner | **Port** |
  | `RPC_CloseEndscreen` | host → clients, force-closes a lingering end screen | **Port** |
  | `RPC_ApplyAfflictions` | host → one client, applies afflictions/stamina | **Port** — this is how coop afflictions restore actually reaches non-host clients |
  | `RPC_SyncMapVisuals` | PEAKapalooza-only segment resync | **Do not port** (PEAKapalooza) |
- **Every place `Peakapalooza` appears in the decompile (63 hits, full list
  catalogued this session)** is now a closed set, so "ignore entirely, do not
  port" (below) is a checklist, not a vague instruction: lines 133, 137, 139,
  149, 430, 575, 847, 849, 851, 853, 855, 857, 859, 861, 863, 889, 947, 1363,
  1365, 1367, 1368, 1370, 1372, 1373, 1375, 1376, 1388, 1389, 1391, 1393, 1394,
  1396, 1397, 1398, 1399, 1585, 1587, 1590, 1592, 1614, 1616, 1617, 1625, 1626,
  1627, 1632, 1634, 1635, 1717, 1720, 1728, 2073, 2125, 2311, 2320, 2324, 2384,
  2435, 2505, 2528, 2555, 2978, 4117, 4581 (line numbers as of the current
  decompile snapshot — re-grep `Peakapalooza` case-insensitively if the source
  file changes). Every one of these is either a field declaration, a guard
  condition gating some other behavior off when PEAKapalooza is present, or a
  wholly separate coroutine (`PeakapaloozaCheckGameobjects`,
  `PeakapaloozaTeleportPlayersPeakToBeach`) — none of it needs a replacement in
  our port, the *non*-PEAKapalooza branch of every one of those guards is what
  we port instead.

### Scope boundary (unchanged in spirit, restated precisely)

- **Keep, near-verbatim (borrowed with credit, see Attribution below):** the
  save-file *shape* — `SaveData`/`SavedItemState`/`SavedBackpackItemState`/
  `SavedEntry` (decompile 389-459, full field list below) — and the
  `MapBaker.GetLevel` Harmony-prefix trick (force a specific scene name over
  the daily-rotation index; already proven safe, `docs/RESEARCH.md` "Scene
  saving/loading" section).
- **Port near-literally (this phase's actual work):** everything else the
  checkpoint mod does to capture and restore a save — `CustomJumpToSegment`,
  `TeleportToPosition`/`TeleportClientsToHost`, `ReviveDeadPlayers`,
  `LoadInventoryDelayed`/`LoadPlayerInventory`/`LoadBackpackFromSave` and their
  reflection helpers, `ResetLuggage` (world-loot cleanup), `ResetFogAfterLoad`/
  `ResetLavaAfterLoad`/`ResetCampfire`, `SavePlayerOffline`/`SavePlayerCoop`,
  `LoadPlayerOffline`/`LoadPlayerCoop`, `PreStartSetSegment`,
  `CheckReadyStatusForPlayers`/`SendReadyStatusToMaster`, the `CheckpointNetwork`
  RPC surface (minus PEAKapalooza's `RPC_SyncMapVisuals` and the unused
  `RPC_SendModVersionToMaster`), and the campfire-autosave Harmony patch
  (`Campfire_AutoSave_Patch`, decompile 123-172).
- **Ignore entirely, do not port:** every PEAKapalooza branch (see the 63-line
  list above). Per maintainer, 2026-07-08: PEAKapalooza hasn't been updated in
  ~6 months and no longer works on the current PEAK version — the dev has
  effectively abandoned it. If it's ever revived, that's a new, separate
  compatibility request to evaluate then.

### Full `SaveData` field reference (must match exactly, for JSON compatibility)

From `PEAK_Checkpoint_Save.Plugin.SaveData` (decompile 389-431) — every field,
verbatim, must exist with the same name/type/JSON-serialized shape in our own
model (Newtonsoft `JsonConvert.SerializeObject(obj, Formatting.Indented)` /
`DeserializeObject<SaveData>`, same as the checkpoint mod uses, so an existing
file loads unchanged and a file we write loads unchanged in the checkpoint mod
too during the transition window):

```
int settingsVersion; string saveDate; List<string> playerNames;
string campfireName; float timePlayed; float timeOfDay;
float posX; float posY; float posZ; string sceneName;
List<BiomeType> biomes; List<string> biome_names; Segment segment;
bool hasBackpack; bool isSkeleton;
List<SavedItemState> inventoryItemStates;       // { int slotIndex; ushort itemId; Dictionary<string, SavedEntry> values; }
List<SavedBackpackItemState> backpackItemStates; // { byte slotIndex; ushort itemId; Dictionary<string, SavedEntry> values; }
float[] afflictions_current; float extraStamina;
bool extModsPeakapaloozaPEAKTOBEACH;             // always write `false`, PEAKapalooza not supported — see below
```
`SavedEntry` = `{ string type; float value; }` (the `type` is a
`Type.AssemblyQualifiedName`, used by `TrySetOrCreateEntry`'s
`Activator.CreateInstance` to reconstruct the right wrapper type on load —
keep this exactly as-is, do not simplify to a plain float, or a checkpoint-mod
save's own item states — and vice versa, our own saves loaded by the
checkpoint mod during the transition window — would fail to round-trip).
`extModsPeakapaloozaPEAKTOBEACH` **stays in our model and is always written
`false`** — dropping the field would break deserializing older/checkpoint-mod
files that have it (Newtonsoft defaults missing fields, but the reverse —
the checkpoint mod reading one of *our* files during the transition window —
needs the field present, defaulting harmlessly).

File paths and naming (`GetPlayerSaveFile`, decompile 2177-2216) — port
exactly, this is the on-disk contract users and `SaveArchive`/`SaveDiscovery`
already depend on:
- Offline, normal: `{PluginPath}\Checkpoint_Save\peak_save_{ascent}_offline.json`
- Offline, custom run: `{PluginPath}\Checkpoint_Save\peak_save_CustomRun_offline.json`
- Coop, normal: `{PluginPath}\Checkpoint_Save\Coop\peak_save_{ascent}_{userId}.json`
- Coop, custom run: `{PluginPath}\Checkpoint_Save\Coop\peak_save_CustomRun_{userId}.json`
- Legacy single-file variants exist too (`configLegacySaveFile`) — **decide
  later whether to carry the legacy toggle forward; not needed for parity
  with a fresh install**, flag as an open question rather than silently
  dropping it.

We keep writing into the **same `Checkpoint_Save` folder structure** (not a
new `QuickResume`-owned folder) specifically so `SaveArchive`/`SaveDiscovery`/
the F7 picker need **zero changes** to keep working, and so the checkpoint mod
(still installed per decision 2 above) keeps seeing/writing the exact same
files for diffing during development.

### Architecture — new/changed files, mapped against the existing module table

Following this mod's own convention (small, single-responsibility files, see
the "Architecture" table above) rather than one giant port of the checkpoint
mod's single 4775-line `Plugin.cs`:

| New file | Ports from (decompile) | Replaces / retires |
|---|---|---|
| `OwnSaveData.cs` | `SaveData`/`SavedItemState`/`SavedBackpackItemState`/`SavedEntry` (389-459) | nothing yet — new model, additive |
| `OwnSavePaths.cs` | `GetPlayerSaveFile` (2177-2216) | nothing yet — additive, `SaveArchive`/`SaveDiscovery` keep using the same on-disk paths either way |
| `OwnItemStateIO.cs` | `TryGetEntryObject`/`TryReadEntryNumeric`/`TryWriteEntryNumeric`/`TryGetKey`/`TryConvertToFloat` (3258-3480) + `ItemStateKeyNames`/`ExcludedItemIds` | `CheckpointInterop.ReadItemStateValues` and the wrapper-method reflection fields (`_tryGetKeyMethod` etc.) |
| `OwnInventoryRestore.cs` | `LoadPlayerInventory`/`LoadBackpackFromSave`/`AddItemToInventory`/`AddItemToInventory_GetSlot`/`GetBackpackData` (3070-3256, 3482-3526) | — |
| `OwnWorldLootReset.cs` | `ResetLuggage` a.k.a. "ResetWorldLoot" + destroy-by-name-substring passes from `CustomJumpToSegment` (3527-3627, 2441-2504) | — |
| `OwnEnvironmentReset.cs` | `ResetFogAfterLoad`/`ResetLavaAfterLoad`/`ResetCampfire`/`SpawnFlaresAtPeak` (2563-2261 range) | — |
| `OwnTeleportSequence.cs` | `CustomJumpToSegment`/`TeleportToPosition`/`TeleportClientsToHost`/`ReviveDeadPlayers` (2263-2779) | the parts of `TeleportWatchdog`/`TeleportConfigOverride` that currently only *observe*/tweak the checkpoint mod's version of this now become the actual implementation these mitigations sit on top of — same mitigation logic, new substrate |
| `OwnSaveCapture.cs` | `SavePlayerOffline`/`SavePlayerCoop` (3715-4605) | — |
| `OwnLoadEntryPoints.cs` | `LoadPlayerOffline`/`LoadPlayerCoop`/`PreStartSetSegment` (4605-4763, 914-954) | `CheckpointInterop.TryLoadPlayer`/`TryPreStartSetSegment` call sites in `ResumeOrchestrator` |
| `OwnNetwork.cs` | `CheckpointNetwork` RPCs (461-695) + `CreatePhotonView` (961-1003) + `CheckReadyStatusForPlayers`/`SendReadyStatusToMaster` (1005-1054) | `CheckpointInterop.ReadyCheckEnabled`/`AllClientsReady`, our own new `PhotonView`/`ViewID`, see decision 2 above for the ID-collision note |
| `MapBakerLevelOverridePatch.cs` | `GetLevel_Override`'s Harmony prefix on `MapBaker.GetLevel` (347-375) | the checkpoint mod's own copy of this patch — we now ship it ourselves |
| `CampfireAutoSavePatch.cs` | `Campfire_AutoSave_Patch` (123-172) | the checkpoint mod's own patch on `Campfire.Interact_CastFinished` |
| `PluginConfig.cs` (extended, not new) | every `ConfigEntry` we currently reflect into via `CheckpointInterop` (`configTeleportJumpLogic`, `configAdvancedTeleportFramesToWait`, `configAdvancedJumpLogicWaitTime`, `configInventory`, `configAfflictions`, `configItemStats`, `configOnetimeLoad`, `configLoadLevelScene`, `configDaytime`, `configCampfireReset`, `configTeleportTheKilnWorkaround`, `configAdvancedEnableClientReadyStatusCheck`, message colors) | the reflected `ConfigEntry` fields in `CheckpointInterop` — these become **our own** config entries with the same defaults, no more reading someone else's config object |

`CheckpointInterop.cs` itself doesn't disappear in one shot — it retires
member-by-member as each new `Own*` file takes over that member's job (see
Milestones below), and only gets deleted once nothing calls into it anymore
(Milestone M8).

### Milestones (each independently build-deploy-test-able; do not start N+1 before N is confirmed)

- **M0 — Save-file compatibility layer.** `OwnSaveData.cs` + `OwnSavePaths.cs`,
  read/write only, no capture/restore logic. Verify against **real existing
  save files** from the maintainer's own folders: deserialize with our model,
  reserialize, confirm every field round-trips (a text diff is fine even if
  key order differs — Newtonsoft doesn't guarantee stable ordering — the
  important thing is no field is lost, renamed, or type-changed). No in-game
  test needed for this milestone specifically.

  **Done (session 12, 2026-07-09).** `OwnSaveData.cs`/`OwnSavePaths.cs` written,
  fields verified byte-for-byte against the decompile (`SaveData` 389-431,
  `GetPlayerSaveFile` 2177-2216 — legacy-single-file mode deliberately not
  ported yet, see the open question above). Verified with a throwaway scratch
  project (`scratch/roundtrip-test/`, git-ignored, references the real
  `OwnSaveData.cs` via a `<Compile Include>` so it can never drift from what
  ships) that deserializes every real `peak_save_*.json` on the maintainer's
  machine (canonical `Checkpoint_Save/` + `Checkpoint_Save/Coop/` + every
  archived copy under `QuickResume/Archive/` — 110 files total) with our model
  and structurally diffs the reserialized JSON against the original.
  **110/110 round-tripped with zero information loss.** First pass flagged
  ~100 as mismatched on `posX`/`posY`/`posZ` only — turned out to be a false
  alarm in the *test's own comparison*, not a real bug: the model's position
  fields are `float` (32-bit), but the test's JSON library parses numbers as
  `double`, so a 9-digit string like `-14.5291834` and a re-serialized
  7-digit string like `-14.529183` can be the exact same float32 bit pattern
  (float32 only has ~7-9 significant digits of precision either way) even
  though they look different as text. Fixed by comparing both sides parsed
  back down to `float` (matching the model's real field type) instead of
  comparing the raw double/string values; all 110 then matched exactly.
  **No in-game test needed or done for this milestone**, exactly as planned.
- **M1 — Own `PhotonView`/RPC channel skeleton.** `OwnNetwork.cs`: our own
  `ViewID`, `CheckpointNetwork`-equivalent component, the readiness-gate RPCs
  only (`RPC_SendReadyStatusToMaster`, `CheckReadyStatusForPlayers`). Testable
  solo trivially (offline mode never touches this path); coop needs a real
  2-player test to confirm the RPC actually arrives and doesn't collide with
  the checkpoint mod's own channel (still installed per decision 2).

  **Implemented (session 12).** `OwnNetwork.cs`: a new `PEAKQuickResume.OwnNetwork`
  `GameObject` (`DontDestroyOnLoad`), its own `PhotonView` at `ViewID=69420`
  (maintainer's choice, session 13 — changed from an initial `194200`; both are
  safely clear of the checkpoint mod's hardcoded `19420` for the same reason:
  PEAK caps rooms at 4 players (`NetworkingUtilities.MAX_PLAYERS`, decompile
  line 89482), so with PUN's default 1000 auto-assigned IDs per actor, nothing
  auto-assigned ever gets remotely close to either number) plus an
  `OwnNetworkRpc : MonoBehaviourPun` holding just `RPC_SendReadyStatusToMaster`.
  `OwnNetwork.Update()` mirrors the checkpoint mod's own scene-keyed state
  machine (decompile 1345-1413) field-for-field for just this RPC's bookkeeping:
  resets `_playerReceivedReadyStatus`/`_clientSentReadyStatus` on `Airport`/
  `Title`, starts the same 5s-after-character-exists `SendReadyStatusToMaster`
  coroutine on a `Level` scene for non-host clients only.
  `CheckReadyStatusForPlayers()` mirrors the original method's exact traversal
  (every live `Player`'s `character.player` userId via `NetworkingUtilities.
  GetUserId`, master-client owner exempted) rather than approximating it.
  Gated by a new `OwnEnableClientReadyStatusCheck` config entry (`Own-Network`
  section, default `true`, same meaning/default as the checkpoint mod's own
  `configAdvancedEnableClientReadyStatusCheck`). Wired into `Plugin.Awake` (own
  `GameObject`, not touching any existing component) purely to stand the
  channel up — **nothing reads `CheckReadyStatusForPlayers()` yet**, this
  milestone is only about the channel existing and not colliding.
  Builds clean against the real game assemblies, deployed to the test profile.
  **Confirmed (session 13):** maintainer launched once solo and once in a
  (solo-hosted) coop session, each loading a separate save, no errors reported.
  `ViewId` changed to `69420` right after per the maintainer's preference (see
  above) and redeployed.
- **M2 — Load entry points, disabled from the live path.** `OwnLoadEntryPoints.cs`
  + `MapBakerLevelOverridePatch.cs`: our own `PreStartSetSegment`/
  `LoadPlayerOffline`/`LoadPlayerCoop` guards, wired to call a stub
  `OwnTeleportSequence` that just logs and returns. **Not yet wired into
  `ResumeOrchestrator`** — build and deploy to confirm it compiles, resolves,
  and logs correctly (probe-style, matching how `CheckpointInterop.Probe()`
  already reports resolution status at startup) without touching the live F7
  flow at all yet.
- **M3 — Full teleport sequence port, solo only.** `OwnTeleportSequence.cs` +
  `OwnWorldLootReset.cs` + `OwnEnvironmentReset.cs`, literal port of
  `CustomJumpToSegment`'s full body including all three `teleportJumpLogic`
  branches. Switch `ResumeOrchestrator`'s solo path (only) to call our own
  `LoadPlayerOffline` instead of `CheckpointInterop.TryLoadPlayer()`. Test
  solo, multiple islands/biomes, matching the maintainer's existing solo test
  coverage before calling this milestone done.
- **M4 — Inventory + backpack restore.** `OwnItemStateIO.cs` +
  `OwnInventoryRestore.cs`, wired into M3's solo path. Test solo across
  several item types (at minimum: a fuel-based tool, a rope/climbing item, a
  food/cooked item, a throwable, something occupying a backpack slot) — this
  is the part with the least ready-made test coverage elsewhere in this repo,
  budget real time for it.
- **M5 — Afflictions, revive, campfire/fog/lava/daytime restore.** Remaining
  pieces of `LoadInventoryDelayed` not covered by M4, `ReviveDeadPlayers`,
  `ResetFogAfterLoad`/`ResetLavaAfterLoad`/`ResetCampfire`/`SpawnFlaresAtPeak`.
  Solo test across segments 0-5 (Caldera/segment 4's kiln-workaround branch
  and the Peak/segment-5 flare-spawn branch both need at least one dedicated
  test each, they're the most special-cased branches in the whole coroutine).
- **M6 — Save capture.** `OwnSaveCapture.cs` + `CampfireAutoSavePatch.cs`. This
  is the point where we can create a save file **without the checkpoint mod
  running the show at all** — critically, diff a save we write against one
  the still-installed checkpoint mod writes for the same in-game state
  (decision 2's whole reason for keeping it installed). Solo only so far.
- **M7 — Coop pass.** Finish `OwnNetwork.cs`'s remaining RPCs
  (`RPC_RequestSave`, `RPC_RecentlyLitCampfire`, `RPC_RequestFalldamageProtection`,
  `RPC_SendMessage` or our own message-overlay equivalent, `RPC_Loadingscreen`,
  `RPC_SetHeroTitle`, `RPC_CloseEndscreen`, `RPC_ApplyAfflictions`),
  `TeleportClientsToHost`, wire the coop path of `ResumeOrchestrator` to our
  own `LoadPlayerCoop`/`SavePlayerCoop`. Full 2-player retest of everything
  M3-M6 already covered solo, plus the existing Phase 6 mitigations
  (`TeleportWatchdog`, `TeleportConfigOverride`, warp suppression, Shift/Alt
  override) re-verified against our own teleport sequence instead of the
  checkpoint mod's.
- **M8 — Cut over Phase 6 mitigations and the F7/F1 UI to our own types.**
  Once M0-M7 are solid: repoint `TeleportWatchdogPatch` (currently Harmony-
  patches the checkpoint mod's `CustomJumpToSegment`/`Character.WarpPlayerRPC`)
  onto our own `OwnTeleportSequence` methods directly (no Harmony needed for
  our own code — just call our own hooks inline), repoint
  `TeleportConfigOverride`/`SavePicker`'s footer indicator/`HelpScreenContent`
  from `CheckpointInterop` reflection to our own `PluginConfig` entries
  directly, repoint `IslandToggleButton` to our own boarding-pass-toggle
  config (may need its own small UI addition since we no longer have the
  checkpoint mod's own boarding-pass checkbox to mirror — flag as an open
  question when this milestone starts). Delete `CheckpointInterop.cs` once
  nothing references it.
- **M9 — Drop the hard dependency, migration, packaging.** Change the
  BepInEx dependency on `PEAK_Checkpoint_Save` from hard to soft/optional (or
  remove it outright — decide based on how M0-M8 went). Write a one-time
  migration note/importer if any file-format drift was introduced (shouldn't
  be any, given M0's whole point). Update `packaging/manifest.json`,
  `packaging/README.md`, `CHANGELOG.md` with the Attribution note (below).
  Uninstall the checkpoint mod from the test profile for a final from-scratch
  verification pass.

### Attribution

dominik0207's save-data shape and the `MapBaker.GetLevel` technique are both
being reused deliberately (not reverse-engineered from scratch by us) — credit
stays explicit in README/CHANGELOG when this ships, same as it is today. We
are porting their restore/capture *implementation* into our own codebase (with
credit), not claiming to have invented save/load for this game from nothing.

### Second-pass optimization ideas (explicitly NOT this phase — noted so they aren't lost)

Once the fidelity port (M0-M9) is proven solid across several real sessions:
- Call `MapHandler.JumpToSegment` directly instead of replaying the full
  `teleportJumpLogic` branch/retry-loop shape, now that we fully own and can
  simplify it instead of blindly copying it.
- Revisit whether the 150-attempt/30-second `TeleportClientsToHost` retry loop
  is still needed once we're not fighting someone else's cadence — Phase 6's
  entire warp-suppression mechanism exists only because that retry loop's
  cadence structurally can't converge (see the Step 4/5 write-up above); owning
  both sides means we could just fix the cadence instead of suppressing its
  symptoms.
- Reconsider `AltTeleportJumpLogic`'s default of `2` (`MapHandler.GoToSegment`)
  given `docs/RESEARCH.md`'s finding that it never actually moves a player at
  all — this was left alone deliberately while depending on the checkpoint
  mod's own config surface, but becomes trivial to fix for real once we own
  the whole teleport path.
- Investigate whether the ~13 `WaitForSeconds(configAdvancedJumpLogicWaitTime.Value)`
  calls scattered through the ported `CustomJumpToSegment` sequence are all
  load-bearing or partly cargo-culted from the original code's own caution —
  not to be touched during the fidelity port itself, but worth an efficiency
  pass afterward.

---

## Goal (why this exists)

Vanilla checkpoint-save load flow after you die is ~2 minutes of manual work:
die → restart → open in-game loader → pick difficulty → load fresh run → press F6
twice. We collapse that to: **press the resume key (default F7) twice.**

- **Phase 1 (post-mortem resume):** after death, one key → fresh run of the same
  difficulty → auto-load the save.
- **Phase 2 (mid-game resume):** same key while alive → return to Airport → fresh
  run → auto-load. Reuses the exact same orchestration.

Hard requirement: always spin up a **fresh run instance first, then load the save**,
so nothing is duplicated or left in a half-state.

---

## Architecture (modular by design)

| File | Responsibility | Update when… |
|---|---|---|
| `Plugin.cs` | BepInEx entry, key + double-press confirm, wiring | rarely |
| `PluginConfig.cs` | config entries (key, timing, toggles) | adding options |
| `RunLauncher.cs` | vanilla flows: start run, return to Airport, scene checks | the **game** updates |
| `CheckpointInterop.cs` | **the only** bridge to the checkpoint mod (reflection) | the **checkpoint mod** updates |
| `ResumeOrchestrator.cs` | the step-by-step resume state machine + timing | behaviour tuning |

If a dependency updates and something breaks, the failing layer is almost always
`RunLauncher` (game changed) or `CheckpointInterop` (checkpoint mod changed). Both
log a startup probe / loud errors to pinpoint the break. See `docs/RESEARCH.md`.

---

## Phase 5 — save-management overhaul (session 7, in progress)

Three mechanics requested together; implement **one at a time**. Written down here
so none are lost even if only mechanic 1 lands this session.

### Mechanic 1 — Custom difficulty / custom-run support ✅ (this session)

Today F7 only resumes the standard ascent difficulties (tenderfoot / peak /
ascent 1–7). Custom runs (the boarding-pass "Custom" toggle) save to
`peak_save_CustomRun_{offline|userId}.json` — **not** ascent-tagged — and were
skipped by `SaveDiscovery`, so resuming a custom run from the Airport failed.

Design (all in our mod, no checkpoint-mod changes needed):
- `SaveDiscovery` now understands `CustomRun` files and returns a `SaveTarget`
  `{ bool IsCustom; int Ascent }` for the newest save on disk (custom or ascent).
- `ResumeOrchestrator.ResolveTarget()`:
  - **mid-run:** `IsCustom = RunSettings.IsCustomRun`, `Ascent = Ascents.currentAscent`
    (custom mid-run already carried the flag; now honoured explicitly).
  - **at Airport:** newest file on disk via `SaveDiscovery`.
- Before `PreStartSetSegment` + `StartRun` we **explicitly** set
  `RunSettings.IsCustomRun` to the target value (via `RunLauncher.TrySetCustomRun`).
  This is required both ways: the checkpoint mod's `GetPlayerSaveFile` picks the
  filename off `IsCustomRun`, and the game is not guaranteed to reset it at the
  Airport, so a stale `true` would otherwise start a custom run for a normal save
  (and vice-versa). `kiosk.StartGame` serializes `RunSettings` (incl. the flag),
  so setting it before the call is sufficient; custom runs force `ascent = 0`.
- **Limitation (DEFERRED — see below):** the checkpoint save file does **not** store
  the custom run *settings* (fog/hazards/etc.). We reproduce the saved scene +
  checkpoint, but the custom settings are whatever is currently in `RunSettings`
  memory. Confirmed in-game (session 7): resuming a CustomRun save works, but it uses
  the *current* settings, not the ones the run was created with.

**Deferred follow-up — persist custom-run settings.** Custom runs are rarely played,
so this is low priority, but ideally a CustomRun resume should restore the exact
difficulty settings the run used. The checkpoint mod's `SaveData` has no field for
them and we can't edit its schema, so we'd persist them ourselves: on save (Harmony
postfix its `SavePlayerOffline`/`SavePlayerCoop`, or when we detect a CustomRun save
appears) capture `RunSettings.GetSerializedRunSettings()` into a sidecar file keyed to
the save, and on resume `GameUtils.ApplySerializedRunSettings(...)` before `StartRun`.
Ties in naturally with mechanic 2's write-path patching.

### Mechanic 2 — In-game F7 save picker + multiple saves per difficulty ✅ (this session)

Goal: replace ever needing the gate kiosk loader. F7 opens an on-screen menu of all
saves for the current category (solo **or** coop), navigable with arrow keys.

**Chosen approach: sidecar archive** (decided with the user). We do NOT patch the
checkpoint mod's filenames — it actively *deletes* files matching `peak_save_{ascent}_*`
in its own folders on save (`SavePlayerCoop`) and wipes `Coop/` on hardmode load, so
extra files placed there would be destroyed. Instead the mod stays 100% untouched and
we keep our own growing archive.

- `SaveArchive.cs` — archive dir `BepInEx/plugins/QuickResume/Archive`. On every save we
  copy the mod's just-written canonical file in as `{stem}__{yyyyMMdd_HHmmss_fff}.json`
  (source mtime → sortable, idempotent name). `List(offline)` returns entries newest-first
  for the category; `Restore` copies a chosen archive back over the canonical file; `Delete`
  removes an archive. Metadata (campfire, date, playtime, biome, players) read via Newtonsoft.
- `SavePatch.cs` — Harmony-postfix `SavePlayerOffline`/`SavePlayerCoop` → `SaveArchive.Sync`.
  Non-fatal if renamed. Writes are synchronous so the file exists by the postfix.
- `SavePicker.cs` — IMGUI overlay MonoBehaviour: list sorted **date-desc**, **newest
  preselected**, arrow-key nav, `Del` deletes (two-step confirm), `Esc` closes. Category
  **separated**: solo shows only offline saves, coop only coop saves.
- `Plugin.cs` — F7 opens the picker (anywhere; host-only; blocked on Title); F7/Enter again
  loads the highlighted save. Mid-run the default selection prefers the current run's
  difficulty, so **F7+F7 still loads the current/latest checkpoint** exactly as before.
- `ResumeOrchestrator.RequestResume(ArchivedSave)` — restores the chosen archive over the
  canonical file, then runs the normal resume with that target. Loading an OLDER checkpoint
  never deletes archives, so mid-run you can revisit an old campfire without losing the run's
  later checkpoints.

✅ Tested in-game (session 7, solo): picker appears/navigates, category split, delete,
load-older mid-run, F7+F7 latest all work. Coop verified with a friend. Follow-up polish
done: coop party listed (host excluded), "Xm played" label, archive split into
`Offline/`+`Coop/` subfolders (with migration).

**Coop fixes (session 8, after 2-player test):**
- The picker now lists **only the host's own** coop saves. In coop the checkpoint mod
  writes one file per player (`peak_save_{ascent}_{userId}`); we filter by the host's
  `PhotonNetwork.LocalPlayer.UserId` (== SteamID64, and == the checkpoint mod's own
  `NetworkingUtilities.GetUserId` = `photonView.Owner.UserId`), embedded as the last
  `_`-segment of the stem. Falls back to showing all if the id can't be read.
- Player list now shows the **whole party** — `playerNames` is alphabetical, not
  host-first, so the old "skip index 0" hid a random player instead of the host.

### Mechanic 3 — Reset current run from the very beginning (TODO)

Nearly free: we already cancel the run, teleport to the Airport, and start the same
difficulty. Just **skip the checkpoint load** at the end → a clean restart of the
same difficulty from scratch. Wire it to a key / picker action.

---

## Phase 6 — teleport-bug mitigation & QoL (branch `feature/teleport-mitigation`)

**Not our bug, but our problem to soften.** The checkpoint mod's own teleport
(`CustomJumpToSegment` → `TeleportToPosition`, triggered by ITS F6, or by anyone's
save load including ours) intermittently breaks for non-host clients: up/down
warp-loop glitching for several seconds, sometimes falling through the world
entirely. Reported twice by the maintainer across sessions, both times only the
non-host player was affected, both times fixed by the host switching the
checkpoint mod's `teleportJumpLogic` setting from `0` to `1` (Mod Settings /
PEAKLib.ModConfig) and reloading — a setting the checkpoint mod's own F1 tutorial
never mentions. We will not touch the checkpoint mod's teleport logic itself
(explicitly out of scope); we detect when it went wrong and help the user
recover, the same way `TutorialPatch`/`SavePatch`/etc. already hook it non-invasively.

**Confirmed from decompile (0.4.7), grounding every step below:**
- `TeleportToPosition(Vector3 pos)` (private coroutine): RPC-warps the local
  player, then polls for up to 30s — if `Character.localCharacter.Head.y` is
  still >3 units off target it **re-issues the warp RPC** (up to 150 times),
  waiting `configAdvancedTeleportFramesToWait` frames (default 30) between
  tries. This loop *is* the up/down glitch symptom when the client's position
  keeps fighting the warp.
- Fall/lava damage is suppressed for a fixed window via
  `RPC_RequestFalldamageProtection(seconds)` → `NoFallDamageUntil = Time.time +
  seconds` → Harmony prefixes on `CharacterMovement.CheckFallDamage`,
  `Lava.HitPlayer`, `Lava.Heat` short-circuit while `Time.time < NoFallDamageUntil`.
  This does **not** cover falling out of the world entirely (a different,
  unprotected code path), which matches the fall-damage the maintainer took even
  though a protection window was active.
- Config levers already exposed by the checkpoint mod (`BaseUnityPlugin.Config`,
  read/settable via reflection exactly like `CheckpointInterop` already does):
  `configTeleportJumpLogic` (int, 0/1/2 — `SetSegmentOnSpawn` / `JumpToSegment` /
  `GoToSegment`), `configAdvancedTeleportFramesToWait` (int, default 30),
  `configAdvancedJumpLogicWaitTime` (float, default 1), `configTeleportTheKilnWorkaround`
  (bool, default false, Caldera-specific — **deferred**, not in scope this phase).
- Damage is tracked as a float per `STATUSTYPE` on `CharacterAfflictions`
  (`GetCurrentStatus(STATUSTYPE.Injury)`, `SubtractStatus(STATUSTYPE.Injury, amount)`
  — both public), not a health value. "Undoing fall damage" means subtracting the
  Injury delta observed across the watch window, not adding health.
- Player world position: `Character.localCharacter.Head` (Vector3, public
  getter); vertical velocity/oscillation: `Character.localCharacter.data.avarageVelocity`
  (public field, updated every physics step from the ragdoll rig).

**Design decisions made without asking (documented so they're easy to challenge):**
- The watchdog arms on **every** checkpoint-mod teleport, not just ones triggered
  through our own F7 flow — the bug is upstream-general (native F6, our F7, either
  player's load) and the fix should help regardless of trigger.
- The Shift/Alt temporary-override trick (step 2) is wired for **both** the
  checkpoint mod's native load key (F6) and our own F7/Enter — cheap to support
  both since we already poll `Input` every frame ourselves; no Harmony patch on
  the checkpoint mod's `Update()` needed, we just read the modifier + write its
  `ConfigEntry.Value` for one frame before it processes the keypress, then restore.
- Auto-fixes (steps 4–5) only ever act on a teleport the watchdog already **flagged**
  as anomalous (never unconditionally on every teleport), so we never touch
  legitimate damage/position taken outside of a detected bad teleport.
- Everything here is config-toggleable (default **on**), consistent with the rest
  of the mod, so anyone who'd rather not have automatic corrections can disable them.
- `teleportTheKilnWorkaround` dynamic auto-enable: **deferred**, per explicit
  request, revisit later.

Implement **one step at a time** — build, deploy, the maintainer tests in-game,
then move to the next. Do not start step *n+1* before step *n* is confirmed working.

### Step 1 — Teleport watchdog: detect a bad teleport (log + on-screen hint only)

- New `TeleportWatchdogPatch.cs`: Harmony-prefix the checkpoint mod's private
  `CustomJumpToSegment(Segment, HashSet<int>, Vector3)`. Only arm if the local
  player's Photon actor number is in the `playersToTeleport` set (skip otherwise —
  not everyone teleports every time, e.g. a player who already left the level).
- New `TeleportWatchdog.cs` (MonoBehaviour on our persistent GO): `BeginWatch(Vector3
  targetPos)` starts a coroutine sampling `Character.localCharacter.Head` and
  `.data.avarageVelocity` for up to `WatchdogWindowSeconds` (config, default 30s).
  Flag as "bad teleport" when either:
  - **Falling through the world:** cumulative downward displacement from the
    post-teleport starting point exceeds `FallDistanceThreshold` (config, default
    1000m) while still trending downward (small buffer for normal terrain drops).
  - **Warp-loop glitch:** vertical velocity sign flips more than
    `GlitchOscillationCount` times (config, default 4) within a short rolling
    window (e.g. 3s) with each swing exceeding a minimum amplitude (avoid
    triggering on normal fall/land bobbing).
- On flag (once per watch window): log a warning with the sampled values (for
  tuning thresholds from real reports) and `interop.TryShowMessage(...)`:
  *"Did something go wrong? If yes, press F1 to open the help screen."* Record
  `LastFlaggedTeleport = (time, targetPos)` for steps 4–5 to consume later.
- New config section "Teleport-Mitigation" in `PluginConfig.cs`:
  `EnableTeleportWatchdog` (bool, default true), `WatchdogWindowSeconds` (float,
  30), `FallDistanceThreshold` (float, 1000), `GlitchOscillationCount` (int, 4),
  `NeverTeleportedDistanceThreshold` (float, 200 — see "never-teleported" case below).
- **No auto-fix yet.** Goal of this step alone: confirm in-game that (a) it does
  NOT fire during a normal, healthy teleport, and (b) it DOES fire the next time
  the real bug recurs. Thresholds are config-tunable without a rebuild... almost:
  they're `ConfigEntry`s, so editable live via the config file or PEAKLib.ModConfig,
  no rebuild needed to retune.

  **First in-game test (session 9): correctly flagged both a real fall-through and**
  **a real up/down glitch — but also fired during ordinary loading**, before the
  checkpoint mod's own loading screen had even lifted (only visible to the
  maintainer because they run with the loading screen disabled). Root cause: the
  watchdog was armed straight off `CustomJumpToSegment`'s start (+1s), which is
  well before the load finishes; the checkpoint mod itself doesn't consider a
  load "done" until `LoadInventoryDelayed` calls `ShowMessage("Save game
  loaded!", ...)`, tens of frames plus several `jumpLogicWaitTime` waits later.
  **Fix:** the `CustomJumpToSegment` prefix now only records the intended
  `savedPos` as *pending*; the watch window itself is armed from a new postfix
  on `ShowMessage` matching the (already-localized, see `SavegameLoadedMessagePatch`)
  "Save game loaded!" text — the same anchor point that message already uses, so
  it fires exactly once per real completed load, coop-safe (each client's own
  `ShowMessage` call arms its own local watchdog).

  **Second in-game test (session 9), 2-account self-hosted coop (laptop + desktop,**
  **each own Steam account, so the maintainer can freely reproduce coop bugs solo):**
  desktop-hosts → laptop-client got the up/down-glitch/fall-through bug (config 0
  broken, config 1 fixed it, consistent with before). **New case found**:
  laptop-hosts (the slower machine) → desktop-client saw the map fine but **was
  never teleported at all** — stayed exactly where it was, no glitching, simply
  never moved to the campfire, even though the host log showed it correctly
  waited for the client to be ready first. Also fixed by switching to
  `teleportJumpLogic = 1`. Host itself is unaffected every time, in both
  directions — this really does look purely like a client-side warp-application
  race, not a host performance thing.

  **Third detection case added (never-teleported):** a client that never actually
  moves can't be caught by the fall-through or oscillation checks (nothing moves,
  nothing glitches). Cheap and reliable check instead: **distance from the
  post-load position to the intended teleport target.** The nearest campfire to
  spawn is ~500m out, and you cannot light a campfire (which is what creates a
  save in the first place) unless every player is within 30m of it — so any
  player who ends up 200m+ (config: `never-teleported-distance-threshold`,
  buffer under the real ~500m/30m gap) from the save's target position right
  after a load is provably not where the save says they should be. Checked
  **once, immediately** when the watch window arms (right after "Save game
  loaded!"), not sampled over time like the other two checks. Flags the same
  `LastFlaggedTeleport` and on-screen hint as the other two cases.

  **Third in-game test (session 9): the never-teleported case did NOT fire** (client**
  was ~3000m from the campfire per its own in-game hover tooltip, watchdog stayed
  silent). **Root cause found and fixed:** the target position was being captured
  from a prefix on `CustomJumpToSegment` — but that coroutine only ever *runs* on
  the host, since only the host calls `LoadPlayerCoop`/`LoadPlayerOffline`. A
  client's own game process never executes that method at all, so a client's
  local watchdog never recorded a pending target and `ArmPendingWatch()` silently
  no-op'd. This was invisible in the first two tests only because in both of
  those the client *did* move (glitching/falling), which the other two checks
  caught by other means; it's exactly the failure mode of this third case,
  though, where the client never moves at all.

  **Fix:** stopped hooking `CustomJumpToSegment` for the target position.
  Instead, `TeleportWatchdogPatch` now Harmony-postfixes the **vanilla**
  `Character.WarpPlayerRPC(Vector3 position, bool poof)` — the one call in the
  whole teleport chain Photon actually delivers to each affected machine
  individually (the host warping itself via `TeleportToPosition`, and each
  client being warped via `TeleportClientsToHost`, both funnel through this same
  RPC method on the receiving machine). No reflection needed for this one,
  `Character` is a vanilla type we already reference directly. Gated by a new
  "load in progress" flag on `TeleportWatchdog`, set from `LoadingScreenPatch`'s
  existing prefix on `LoadingScreen(bool, string)` — which, conveniently, is
  already known to fire identically on host and every client (the host calls it
  directly, and relays it to clients via its own `RPC_Loadingscreen`), so it was
  already the correct "a load just started" signal, we just weren't using it for
  this yet. This also means a load that reports itself done (`"Save game
  loaded!"`) while the watchdog never saw a single `WarpPlayerRPC` for the local
  player is now flagged immediately, on its own, as an even more direct
  "never-teleported" signal than the distance check (which still runs too, as a
  second line of defense for a warp that technically fired but landed far away).

  **Fourth in-game test (session 9), same laptop-hosts/desktop-client setup, 4**
  **tries:** tries 1–3 all had the real bug (immediate fall-through-and-death on
  try 1; ~10s of visible up/down glitching then fall-through-and-death on tries 2
  and 3) but the watchdog **stayed silent on all three** — a clear regression from
  the WarpPlayerRPC fix above (which fixed detecting a client that's never warped
  at all, but broke down for a client that IS warped, repeatedly). Try 4 (after
  restarting both PCs) hit the never-teleported case again and was detected
  correctly. Root-caused both silent misses:
  - **Glitch never flagged:** the old check inferred oscillation from
    `avarageVelocity.y` sign flips, assuming a smooth up/down bounce. A
    `WarpPlayerRPC` is an instantaneous position snap, not a velocity change, so
    between corrections the character is just genuinely falling (velocity stays
    negative throughout) — the sign never actually flips positive, so the
    threshold was never reachable no matter how many times it visibly "glitched."
  - **Fall-through never flagged:** the old check tracked a rolling peak height
    and looked for a big drop below it. But the checkpoint mod's own correction
    loop (`TeleportClientsToHost` re-sending `WarpPlayerRPC` on a mismatch) keeps
    snapping the player back up near the target between falls, which kept
    resetting the rolling peak upward before a real cumulative drop could ever
    exceed the threshold. Also likely too high a threshold (1000m) versus however
    deep the game's actual out-of-bounds/void kill trigger sits — try 1 died
    "immediately," almost certainly from that kill trigger rather than an actual
    1000m fall, so the threshold was never reached before death ended the round.

  **Fix, replacing both checks:**
  - **Glitch** is no longer inferred from position/velocity sampling at all.
    `TeleportWatchdogPatch`'s `WarpPlayerRPC` postfix now always reports to a new
    `TeleportWatchdog.OnLocalWarp(Vector3)`, regardless of load-in-progress state.
    While a watch window is active (i.e. AFTER "Save game loaded!" already
    fired — the checkpoint mod's correction loop is fire-and-forget and can keep
    running up to 30s in the background past that point, independently of when
    it reports itself "done"), every such warp is counted as a repeat correction;
    `glitch-oscillation-count` (still 4) repeats within a rolling 5s window flags
    the glitch immediately. This directly counts the actual mechanism of the bug
    instead of guessing at its side effects.
  - **Fall-through** now measures against the fixed, original teleport target
    (`targetPos.y`), not a rolling peak, so mid-flight corrections can no longer
    reset the reference point. Default threshold lowered from 1000m to 150m
    (generous over any legitimate post-load settle jitter, well under a
    plausible void-kill depth).
  - **New, added alongside:** flag immediately if the local character's
    `data.dead` goes true within the watch window, regardless of measured fall
    distance — catches an instant out-of-bounds/void kill that can happen faster
    than any fall-distance threshold would ever fire (this is what try 1 almost
    certainly hit).
  - Still untested in-game as of this writing; needs another round to confirm all
    four symptoms (never-teleported, fall-through, died-shortly-after, and
    warp-loop-glitch) are now caught.

  **Open theory raised alongside this (not implemented, not confirmed):** the
  maintainer suspects `teleportJumpLogic = 0` (the checkpoint mod's own default)
  may not fully unfog/load the next island's segment for a client (normally only
  triggered by lighting a campfire there), which could be *why* it's the flakier
  of the three options — `1`/`2` seem to load the segment directly, which the
  maintainer interprets as why they "worked" (with visible transient glitching)
  where `0` didn't. If this holds up after more testing, it could justify making
  Step 2's override the default behavior instead of a Shift/Alt opt-in (i.e. F7/
  Enter would use `teleportJumpLogic = 1` unless a modifier is held to force `0`
  or `2`) — flagged here so it isn't lost, but **explicitly not decided**: the
  maintainer hasn't thoroughly tested this theory yet and doesn't know why
  dominik0207 chose `0` as the default in the first place (may have its own
  tradeoffs we're not seeing). Noted as an open, unconfirmed theory in the
  player-facing README too (`packaging/README.md`, Notes section) so it isn't
  presented as settled.

  **Fifth in-game test (session 9), same laptop-hosts/desktop-client setup:**
  two findings.
  1. Confirmed the "died shortly after load" flag does fire and show a message —
     but only at actual **death**, which takes a few seconds (bleed-out) unless
     the player forces it, so it lags well behind the moment things actually went
     wrong. **Fixed:** now triggers at **knocked out** (`data.fullyPassedOut`,
     the same earlier signal `Plugin.PlayerIsDead()` already uses elsewhere in
     this codebase), not full death.
  2. A repeat of the fall-through case (client teleported fine, then immediately
     fell ~2000m to its death within the 30s window) went **undetected** on
     screen. Maintainer's hypothesis, not confirmed: the checkpoint mod's own
     single shared message overlay (one text field + one hide-timer,
     `ShowMessage` just overwrites both unconditionally, no queue) may have had a
     late-arriving "Save game loaded!" RPC — plausible on a laggy host/client
     pairing like this one — land right on top of ours and visually erase it a
     moment after it appeared. The **log line is authoritative regardless**
     (`LogOutput.log`, `TeleportWatchdog:` prefix, written before the on-screen
     call), so this may only ever have been a display problem, not a missed
     detection — needs a test where the log is captured alongside to confirm
     either way. **Mitigation added in the meantime:** the warning message is
     now re-shown three times (immediately, +2s, +5s) instead of once, so a
     single stomped attempt doesn't erase it for the whole window. **When
     testing next, always grab `LogOutput.log` even if no message was seen
     on-screen** — that's the ground truth either way.

  **Sixth in-game test (session 9), same laptop-hosts/desktop-client setup, logs**
  **captured this time.** Client log: `TeleportWatchdog: flagged bad teleport
  (knocked out / died shortly after load). ... within 20.9s of the load finishing
  (target=(-0.54, 925.97, 1216.23))` — **detected correctly, and the on-screen
  message was seen this time too.** No separate "fall-through" line appears in
  the log; most likely both conditions became true around the same final
  moment (a while spent oscillating within the 150m band, then the actual
  fall-through and knock-out landing close together) and the knock-out check
  (evaluated first in the loop, per-iteration) simply wins the race and breaks
  before the fall-through check below it runs that same iteration — not a
  detection gap, just which of two simultaneously-true reasons gets logged.
  Low priority: could reorder or log both, not done since a message got through
  either way and the underlying flag/behavior is identical regardless of which
  reason is named.

  **Notable supporting evidence for the `teleportJumpLogic` theory above:** the
  **host's** log shows both the client's and the host's own warp converging in a
  single attempt each (`warped ... after 1 attempts`) — no visible retry loop
  from the host's own bookkeeping (which only checks the networked position
  estimate). Yet the client still fell through and died. This fits the theory
  that `teleportJumpLogic = 0` can leave the next island's segment
  un-loaded/un-unfogged on the client even when the position warp itself is
  reported as successful — the client has nothing solid to land on regardless of
  whether their networked position "matches," since the segment simply isn't
  there for them. Still not confirmed, but this test is a second independent
  data point for it.

**Step 1 status: all four detection cases (never-teleported, fall-through,**
**knocked-out/died, warp-loop glitch) have now each been confirmed working**
**in-game at least once. Ready to move to Step 2** unless further Step-1
hardening is wanted first.

**Follow-up question raised right after (session 9):** in the sixth test above,
the maintainer reports falling continuously straight down at max velocity for
the *entire* ~20.9s (no lateral drift, no pauses), which should have dropped far
more than the 150m `fall-distance-threshold` within the first couple of seconds
— yet only the knocked-out check (at the very end) fired; the fall-through check
apparently never crossed its threshold at any point along the way, which doesn't
add up against a genuine ~2000m/20s fall. Possible causes flagged for
investigation, not yet ruled in or out: wrong axis assumption (this codebase
assumes Unity's standard Y-up throughout, matching how the checkpoint mod's own
`TeleportToPosition`/`TeleportClientsToHost` check `Head.y`, so it should be
correct, but worth confirming with real numbers rather than assuming); `Head`
(a ragdoll bodypart transform) behaving unexpectedly once knocked into a
ragdoll/falling state; or something suspending/skipping our polling coroutine
without breaking it outright. **Temporary diagnostic added** (gated on the
existing `enable-debug-logging`, on by default): `WatchRoutine` now logs, once
per second during the watch window, the full `Head` position, `avarageVelocity`,
`dead`/`fullyPassedOut` flags, the target, and the computed `targetPos.y -
Head.y` delta against the threshold. Meant to be removed once this is
root-caused — next occurrence of this symptom should make the actual numbers
obvious from `LogOutput.log` alone (`TeleportWatchdog [diag]:` prefix, once/sec).

  **Root-caused (session 9), diagnostic removed again.** Same laptop-hosts/
  desktop-client setup, two tries (glitch-then-fall-through-then-knocked-out,
  then a clean fall-through-then-knocked-out); the diagnostic log nailed it
  immediately: `fallThreshold=1000`, not the 150 shipped a couple rounds
  earlier. **Not a code bug** — BepInEx config entries persist to the user's
  on-disk `.cfg` once written; lowering a `ConfigEntry`'s default in code does
  NOT retroactively rewrite a value the user already has saved from an earlier
  build. The maintainer's config file still had `fall-distance-threshold = 1000`
  from testing an older build, so the fall-through check was silently correct
  the entire time, just gated by a threshold change that never reached disk.
  Everything else in the log fully vindicates the detection logic: continuous
  ~-55 m/s fall for the entire window (matches "max downward velocity" exactly),
  `Head.y` dropping smoothly and linearly from ~925 to a resting floor at
  ~-0.75 (deltaY tops out at 926.7 - comfortably past the intended 150m, just
  under the stale 1000m), then knocked out ~6s after landing on that floor
  (presumably an unrelated void/out-of-bounds penalty timer). Y-axis convention,
  fixed-baseline math, and the repeat-warp glitch counter (also separately
  confirmed correct in the first of these two tries, flagging at exactly 4
  repeats within its 5s window) are all now validated against real numbers.
  Diagnostic logging block removed from `TeleportWatchdog.WatchRoutine`, its
  job is done. **Lesson for future config-default changes:** a lowered/raised
  `ConfigEntry` default only affects fresh installs / deleted config files;
  existing testers need to manually update (or delete) the relevant line to
  actually pick up a new default. Worth calling out explicitly in any future
  handoff/test-request rather than assuming a rebuilt DLL alone is enough.

**Step 1 is now considered fully validated** across all four detection cases
with real in-game numbers behind each. Moving on to Step 2 next.

### Step 2 — Shift/Alt temporary teleport-config override

- In `Plugin.Update()` (native F6 is handled inside the checkpoint mod itself, so
  this needs a tiny Harmony prefix on its `Update()` — or simpler: a prefix on
  `PreStartSetSegment`/the load entry point it calls — pick whichever single
  choke point covers both F6 and our own `TryLoadPlayer`/`TryPreStartSetSegment`
  calls; investigate exact call site when implementing) detect `LeftShift`/
  `RightShift` or `LeftAlt`/`RightAlt` held during the load-triggering keypress.
- If Shift held: temporarily set `configTeleportJumpLogic.Value = 1` (JumpToSegment).
  If Alt held: `= 2` (GoToSegment). Either way also bump
  `configAdvancedTeleportFramesToWait.Value = 40` (maintainer's call after seeing
  the default of 30 in practice: 100 was excessive, 40 is "extra safe" without
  being wasteful) and `configAdvancedJumpLogicWaitTime.Value = 2` — **but only
  ever raise these two, never lower them:** snapshot the user's current values
  first, and only override with 40 / 2 respectively if their existing value is
  lower. A user who already deliberately set either one higher (e.g. tuning for
  their own connection) should not have it silently pulled back down by this trick.
- Snapshot the previous values first; restore all three after the teleport
  completes (reuse the watchdog's window-end, or a flat delay if simpler/safer).
- Verify in-game: holding Shift while pressing F6 (or our F7/Enter) actually
  changes behavior, and values are restored afterward (config file / ModConfig
  reflects the original settings again once done).
- **Save-picker footer indicator (requested alongside this step):** while the F7
  save picker (`SavePicker.cs`) is open, its footer's "Load" label becomes
  "Load (1)" while Shift is held, "Load (2)" while Alt is held, plain "Load" the
  rest of the time — a live, held-only readout of which override (if any) will
  apply the next time the load key is pressed, so the player gets visible
  confirmation they're holding the right key before committing. Always resets to
  plain "Load" when the picker (re)opens, never persists across opens. Purely a
  label swap driven by `Input.GetKey`, no functional dependency beyond this
  step's override existing.

**Implemented (session 9).** Design settled before writing code (see the plan
discussion above this line was written): two trigger paths funnel into one
shared override, since they need the modifier state read at different times.

- **`CheckpointInterop`** extended with `TryGetTeleportConfig`/`TrySetTeleportConfig`,
  reflection get/set for `configTeleportJumpLogic` / `configAdvancedTeleportFramesToWait`
  / `configAdvancedJumpLogicWaitTime`, non-fatal like everything else in that file.
- **New `TeleportConfigOverride.cs`**: `Apply(int? jumpLogicOverride)` snapshots the
  true original values once (never re-snapshots its own override as if it were
  original), sets jumpLogic to the requested value and raises frames/waitTime to
  `max(original, configured-minimum)` (never lowers below what the user already
  had), then (re)starts a flat-delay restore coroutine (`override-restore-delay-seconds`,
  default 35s — comfortably past the ~30s window the correction loop can keep
  running in; a new overridden load before that fires just extends/resets the
  timer rather than restoring mid-sequence).
- **Our own F7 path**: modifier captured at `Plugin.ConfirmLoad()` (confirm time,
  since our own load doesn't happen synchronously with the keypress — it waits
  for a fresh run to start first, by which point Shift/Alt is likely long
  released), threaded through `ResumeOrchestrator.RequestResume(chosen,
  teleportOverrideValue)` and applied right before its `TryLoadPlayer()` call,
  wrapped in a `TeleportConfigOverride.IsDrivingOurOwnLoad` flag for the
  duration of that synchronous call.
- **Native F6 path**: `LoadingScreenPatch`'s existing prefix on `LoadingScreen(bool,
  string)` — already known to fire synchronously with F6's own key handling, on
  host and every client via RPC relay — now also calls
  `TeleportConfigOverride.ApplyFromAmbientModifiers()` (reading `Input` for
  Shift/Alt AT THAT MOMENT, valid specifically because there's no delay for this
  path), gated to `PhotonNetwork.OfflineMode || IsMasterClient` (the setting only
  ever matters on whichever machine drives the actual teleport) and skipped
  entirely while `IsDrivingOurOwnLoad` is true (our own flow already applied its
  own confirm-time capture; an ambient read here would just see nothing held and
  incorrectly clear it).
- **Made "easy to change" per the maintainer's request** (still unconvinced by
  their own `teleportJumpLogic=1` theory pending more testing, so not baking
  any number in as fixed): `Shift`/`Alt` → which `teleportJumpLogic` value are
  each their own `PluginConfig` entries (`shift-teleport-jump-logic` default 1,
  `alt-teleport-jump-logic` default 2), likewise `override-frames-to-wait`
  (default 40) and `override-jump-logic-wait-time` (default 2). The checkpoint
  mod's own base default (0, used when neither key is held) is deliberately
  **left untouched** — not overridden, not exposed as a "base override" setting,
  per explicit instruction not to commit to the theory yet.
- **Footer indicator, made dynamic per the maintainer's request** (not the
  simpler "Load (1)"/"Load (2)"-only-while-held version originally sketched
  above): `SavePicker.ComputeLoadLabel()` now always shows a number — while
  Shift/Alt is held, the corresponding config value; otherwise the checkpoint
  mod's **live** current `teleportJumpLogic` (read fresh via
  `TryGetTeleportConfig`, not cached), so it stays correct if the base default,
  the Shift/Alt mappings, or anything else changes, no hardcoded label text.
  Never shows more than one number at once. Checked every frame (cheap
  early-out: only touches the label / forces a layout pass when the computed
  text actually changed, e.g. right when a key is pressed/released), not just
  on menu open/move, so it updates live as the player holds/releases the keys.
  Resets to whatever's live-current whenever the picker (re)opens.
- Still needs in-game verification: holding Shift/Alt while pressing F6 (or our
  F7/Enter) actually changes teleport behavior, values restore correctly after
  the delay, and the footer indicator tracks held keys/live config accurately.

  **Frontend confirmed working (session 9):** the footer indicator (dynamic
  "Load (N)" reflecting held Shift/Alt or the live checkpoint-mod default)
  behaves correctly. **Deferred:** the actual coop backend effect (does holding
  Shift/Alt for a real load actually change teleport behavior and restore
  afterward) still needs testing with the laptop+desktop coop setup — put off
  for now to avoid repeatedly pinging ~50 Steam friends with "is playing PEAK"
  notifications every couple minutes across many more test cycles. Revisit
  before calling Step 2 done.

  **Revised (session 11) after extensive maintainer testing.** Both directions
  of host/client (laptop-hosts and desktop-hosts), every campfire on 3 islands,
  only-host-lit and only-client-lit campfires, varied inventories — `teleportJumpLogic=1`
  avoided the checkpoint mod's intermittent teleport bug in every single case in
  coop (it unfogs/loads every intervening island's segment on the way to the
  target rather than jumping straight there, which is the maintainer's theory
  for why it's more reliable — still not confirmed from the checkpoint mod's own
  source, but now backed by exhaustive empirical testing rather than a hunch).
  `teleportTheKilnWorkaround` was also tried as a possible additional mitigation
  and **made things worse** in the maintainer's test cases — explicitly rejected,
  not just deferred; not part of this system.

  Given 1 is no longer just "a thing you can opt into if you get unlucky" but
  the empirically best default, the maintainer asked to make it the default for
  a plain coop load, while keeping an opt-out. This reshapes what Shift/Alt mean:
  - **Alt** is unchanged: always forces `alt-teleport-jump-logic` (default 2),
    in both solo and coop.
  - **Shift** changes meaning: no longer a distinct configured value, now means
    "use my own base config anyway" (i.e. applies no override at all) — the
    escape hatch back to pre-optimization behavior for one load without editing
    settings. `shift-teleport-jump-logic` is removed as a config entry (no
    longer a numeric setting).
  - **No modifier** changes meaning: in COOP, with new setting
    `enable-optimized-coop-loading` (default **true**) on, forces
    `optimized-coop-jump-logic` (default 1) instead of the base config, for
    that one load only. Disabling the setting (or being in SOLO, which this
    never touches) falls back to the base config, same as Shift.
  - `TeleportConfigOverride.ResolveHeldModifierOverride()` renamed to
    `ResolveOverride()` and rewritten around this table; single source of truth
    for both applying an override (native F6 and our own F7/Enter, as before)
    and read-only display (footer indicator, F1 help). Coop/solo detected via
    `!PhotonNetwork.OfflineMode` (try/catch, defaults to solo on error, matching
    the pattern already used elsewhere in this codebase).
  - New `TeleportConfigOverride.TryGetBaseJumpLogic()`: the user's own
    teleportJumpLogic ignoring any override currently in effect — returns the
    pre-override snapshot correctly even mid-override (unlike reading
    `TryGetTeleportConfig` directly, which would return the temporarily
    overridden live value in that window). Used anywhere the UI needs to show
    "your own base value" specifically (footer indicator when Shift is held or
    in solo, F1 help screen's live numbers) rather than whatever's live-active.
  - Footer indicator (`SavePicker.ComputeLoadLabel`) and F1 help screen text
    (`HelpScreenContent.Build`) both rewritten around the same table, and the F1
    text now branches on whether the coop optimization is enabled, showing
    different guidance either way (including a pointer to set the checkpoint
    mod's own `teleportJumpLogic` config directly to 1, for anyone who disables
    the optimization but still wants it as their own personal default).
  - Not yet re-tested in-game against this specific revision (the testing above
    predates the code change, done directly against the checkpoint mod's own
    config with no modifier scheme involved) — do that before considering this
    revision done.

### Step 3 — Custom F1 help screen (full rewrite, not just append)

- Extend `TutorialPatch.cs` (or split into a new file if it grows) to fully
  reformat the tutorial text rather than inserting lines into the existing wall
  of text: compact sections, e.g. "Quick Resume", "If your load bugged out"
  (explains the Shift/Alt trick from step 2 in plain language, mentions the
  `teleportJumpLogic` setting exists and what it does, credits it's a
  known upstream teleport quirk, not a Quick Resume bug), version line.
- Reuse the real config descriptions (already collected in this doc, from the
  decompile) so the in-game text matches the actual `configTeleportJumpLogic` /
  `configAdvancedTeleportFramesToWait` / `configAdvancedJumpLogicWaitTime`
  descriptions instead of paraphrasing.
- Still defensive/string-anchored like today: if the checkpoint mod's wording
  changes upstream, no-op with a log warning rather than producing garbled text.

**Implemented (session 9).** `TutorialPatch.cs` no longer inserts lines into the
original wall of text, it replaces it outright with a fully custom, sectioned,
TMP-rich-text-formatted (bold/colored headers, colored key mentions) block:
"PEAK Checkpoint Save + Quick Resume" header, saving/native-load/Quick-Resume
lines, then a "**Did your load bug out?**" section explaining the Shift/Alt
trick in plain language, credited as an occasional PEAK Checkpoint Save
upstream teleport hiccup (not a Quick Resume bug), plus the achievements note
and the close/reopen key + both mod versions.

- **Less anchor-fragile than before, not more:** the previous append-only
  version depended on finding specific substrings in the checkpoint mod's own
  wording (the F6 line, the version marker) to know WHERE to insert text. This
  full replacement instead pulls everything it needs live via reflection
  (`CheckpointInterop.TryGetLoadKeyText`/`TryGetTutorialKeyText`, both new) or
  already has from our own config/state (`Plugin.ResumeKeyText`,
  `PluginConfig.Shift/AltTeleportJumpLogic`), and degrades gracefully (a
  sensible fallback key name) rather than breaking if any single lookup fails.
  The ONLY substring dependency left is extracting the PCS version number from
  the tail of the original text (`"Mod version: X"`), which falls back to `"?"`
  rather than blocking the whole rewrite if that one marker ever changes.
- **Real, live config description, not a paraphrase:** added
  `CheckpointInterop.TryGetTeleportJumpLogicDescription()`, which reflects
  straight into the checkpoint mod's own `ConfigEntry.Description.Description`
  for `configTeleportJumpLogic` - so the screen quotes whatever text
  dominik0207 actually ships, word for word, live, rather than a copy that
  could drift out of sync after an update.
- **Rich-text safety net:** checks the TMP component's own `richText` property
  via reflection before setting the text; if it's ever disabled, our `<b>`/
  `<color>`/`<size>` tags are stripped first so the screen shows plain (still
  fully readable) text instead of literal tag characters.
- Not yet verified in-game: open F1 and confirm it renders as intended
  (formatting, colors, live key names, live description text, no leftover
  literal tags), and that it still closes/reopens with the tutorial key.

  **First in-game look (session 9): rejected.** Screenshot showed the checkpoint
  mod's own plain-black tutorial backdrop with our TMP text on top, three
  problems: (1) `<b>` on the game's own font (which has no real bold face,
  already noted in `SavePicker`'s own font-lookup comment - TMP fakes bold by
  distorting the regular glyphs, illegible here) (2) long lines ran off the
  right edge of the screen, no word-wrap (3) the light-blue header color read
  poorly. **Redirected to a bigger, better fix**, per the maintainer: not a
  reformatted text block at all, but an actual small menu reusing the F7 save
  picker's own visual system (rounded/bordered blue panel, its font, its gold
  key badges), sized to its content vertically rather than a fixed screen-wide
  box.

  **Rebuilt (session 9):**
  - `SavePicker.cs`: widened visibility (`private` → `internal`) on the pieces
    needed for reuse - the palette (`DimColor`/`PanelFillColor`/`TitleColor`/
    `FooterColor`/`KeyChipFillColor`/`KeyTextColor`/`BadgeBorderColor`), the
    panel geometry constants (`PanelPadding(Horizontal)`, `TitleHeight`,
    `FooterHeight`, `PanelWidth`, `PanelCornerRadius`, `PanelBorderThickness`,
    `PanelOuterMargin`), and three static helpers (`FindGameFont`,
    `StretchFull`, `BadgeSprite`, `PanelSprite`). No logic duplicated or
    changed, purely a visibility change so a second screen can share the exact
    same look instead of approximating it.
  - New **`HelpScreen.cs`**: a real UGUI panel (same rounded/bordered sprite,
    same dim backdrop) with a title, a word-wrapped body (`ContentSizeFitter`
    `PreferredSize` vertically, fixed width horizontally, so the panel grows
    downward to fit whatever content it has instead of a fixed box people
    scroll past), and a single "(F1) Close" footer badge, identical style to
    the picker's own footer chips. **No bold anywhere** - `FontStyles.Normal`
    everywhere in `MakeText`, emphasis is color-only (one gold accent,
    `#FFF2B8`, matching the picker's own key badges) against the panel's
    proven pale-blue-on-blue body text, not the picker's title color (that
    earlier light blue was specifically called out as bad against a plain
    black background; against the actual blue panel it's back to the same
    combination the picker already uses successfully, so the panel swap
    resolves that complaint by construction rather than needing a new color).
  - New **`HelpScreenContent.cs`**: pure content/formatting, split out from the
    screen itself - same informational content as before (saving, native
    load, Quick Resume, the Shift/Alt bug-workaround section with the live
    `teleportJumpLogic` description, achievements note), rewritten without any
    `<b>` tags.
  - **`TutorialPatch.cs` rewritten**: no longer edits the checkpoint mod's own
    TMP text at all. Its postfix on `ShowTutorialMessage` now reflects into
    the checkpoint mod's private `_tutorialOverlay` GameObject and force-hides
    it, then opens/closes `HelpScreen` in lockstep (`active=true` → hide
    theirs + open ours, `active=false` → close ours). Still rides entirely on
    the checkpoint mod's own F1/tutorial-key `Update()` detection, no key
    handling of our own needed. A custom popup through the same method (e.g.
    its mod-version-mismatch warning, called with a non-empty `message`) is
    left completely untouched, exactly as before.
- Not yet re-verified in-game after the rebuild: open F1, confirm the new panel
  renders correctly (sizes to content, no overflow, readable, closes/reopens
  correctly), and that the original checkpoint-mod tutorial overlay is fully
  hidden (no double backdrop/flash of the old text before ours appears).

  **Second look (session 9): much better, four polish requests:**
  1. Font size across the whole screen bumped by 1px (title 26→27, body 19→20,
     footer key 15→16, footer label 16→17). The panel already grows to fit its
     body's `ContentSizeFitter`-measured preferred height, so this needed no
     extra sizing logic, just bigger numbers.
  2. The Shift/Alt line showed the literal word **"load key"** instead of the
     actual (rebindable) key, and didn't mention Enter even though our own
     F7/Enter path is also covered by the override. Fixed in
     `HelpScreenContent.cs`: now reads `{Key(loadKey)} or {Key(resumeKey)}/Enter`
     (both already pulled live, `loadKey` via `CheckpointInterop.TryGetLoadKeyText`,
     `resumeKey` via `Plugin.ResumeKeyText`) — a real rebind of either key shows
     correctly with no further changes needed. (The `{Key(resumeKey)} ANYWHERE...`
     and native-load lines above were already live-resolved from session 9's
     first pass, just this one line had a leftover literal placeholder.)
  3. "Did your load bug out?" → "Did loading a save bug out?".
  4. **Jagged-edge animation + a cross-screen caching bug.** Added the same
     3-seed animated jag cycling `SavePicker`'s own panel has (`Update()` ticks
     `_jagFrame` on the same `JagFrameInterval`, now `internal` on `SavePicker`
     alongside `JagFrameCount`, and swaps `_panelFillImage.sprite` via
     `SavePicker.PanelSprite`). Doing this exposed (and let us fix) a real bug:
     `PanelSprite`'s cache was a single "most recently baked size" slot shared
     by BOTH screens (now that HelpScreen reuses it too) — opening F1 baked at
     its size, then opening F7 invalidated and rebaked at ITS size, then
     reopening F1 invalidated and rebaked AGAIN, a stutter on every single
     open instead of only the first-ever-open per size. Also
     `SavePicker.ApplyJagFrame` itself read the cache's currently-stored
     width/height rather than its OWN panel's actual current size, meaning
     the picker's animation could silently start reading the WRONG cached
     size's frames if the shared slot had been overwritten by the other
     screen in between. **Fixed both:** `PanelSprite` now keys a
     `Dictionary<(int width, int height), Sprite[]>` (never evicted, trivial
     memory for the handful of distinct sizes either screen ever needs), so
     each screen's size stays independently cached; `ApplyJagFrame` now reads
     `_panelRect.sizeDelta` directly instead of trusting the (now-removed)
     shared cache-position fields. Also added a first-open "Loading..."
     indicator for HelpScreen (same pattern as `SavePicker`'s own warm-up),
     since building its panel sprite from scratch is the ~300ms hitch this
     was all about in the first place.
- Not yet re-verified in-game after this round of fixes.

  **Third look (session 9): two more small requests.**
  1. Footer close badge showed "(F1)" - redundant parens on top of an actual
     badge shape, inconsistent with `SavePicker`'s own footer badges (which
     just show the bare key, e.g. "F7 / Enter", no parens). Fixed:
     `_footerKeyText.text = tutorialKey` (was `$"({tutorialKey})"`).
  2. The Shift/Alt line mentioning both the native load key AND
     `{resumeKey}/Enter` together read as confusing. Simplified to drop the
     native-F6 mention entirely for this specific instruction (F6 is still
     covered functionally by `TeleportConfigOverride`/`LoadingScreenPatch`,
     just not called out in THIS sentence) and clarified it has to happen
     **from inside the save picker**: "Ask your HOST to reload the SAME save
     from the save picker ({resumeKey}), while it's open, holding: Shift +
     {resumeKey}/Enter = teleportJumpLogic N" (and same for Alt) - previously
     it wasn't clear the Shift/Alt hold has to happen while the picker menu
     is actually open, not just anywhere.
- Not yet re-verified in-game after this round of fixes.

  **Fourth look (session 9): one bug, one wording pass, one visual request.**
  1. **Double-parens bug:** the save-picker mention rendered as "((F7))" -
     `Key()` already wraps its argument in parens (`<color>({k})</color>`),
     but the surrounding sentence ALSO wrapped the `Key(resumeKey)` call in a
     literal `(...)`. Fixed by dropping the redundant outer parens (the
     colored key badge-text already reads fine on its own, no need for the
     sentence's own parens around it too).
  2. "Did loading a save bug out?" → "Did loading a save bug you out?", and
     its symptom list gained a fourth entry for the never-teleported case:
     "or you never actually moved to your campfire" (rephrased from the
     maintainer's own suggested "still at the start", made more precise:
     the actual failure is staying wherever you were BEFORE the load, not
     necessarily the map's spawn point).
  3. **Animated dim fade added**, matching the F7 picker's own treatment
     exactly: first-ever open (still warming up) shows the dim at full
     opacity immediately (no fade, since the loading indicator's own dim was
     already opaque a frame earlier, fading here would flash-then-refade);
     every subsequent open fades the dim in from transparent over 0.25s
     instead of snapping straight to full. `HelpScreen.ShowReal(bool
     skipDimFade)` mirrors `SavePicker.ShowRealMenu`'s exact same parameter/
     logic for this reason.
- Not yet re-verified in-game after this round of fixes.

  **Fifth (likely final) look, requested alongside the fourth round's report:**
  three more asks, all resolved by reusing more of `SavePicker`'s own pieces
  rather than reimplementing anything new.
  1. **Grain overlay:** the same fractal-noise texture the F7 picker's panel
     has, masked to the fill area only (inset by the border thickness so it
     never draws over the border ring). Widened visibility on
     `SavePicker.PanelInnerMaskSprite()`, `PanelGrainTexture()`, and
     `GrainTextureSize` (same `private` → `internal`, no logic touched,
     consistent with how the panel sprite/font/badge helpers were shared
     earlier); `HelpScreen.EnsureUi` now builds the identical mask+grain
     GameObject structure `SavePicker` does, verbatim.
  2. **`PluginConfig.PanelOpacity` now applies here too:** `HelpScreen.
     RebuildContent` reads it fresh every rebuild and fades both the panel
     fill AND the grain overlay together, the exact same two lines
     `SavePicker.RebuildUi` uses.
  3. **Escape now also closes the F1 screen** (deliberately, not just the
     tutorial key it opened with — a player used to "same key closes what it
     opened" would otherwise try F7 to close the F7 picker, which does
     something else there, loads the highlighted save). Reuses
     `PauseSuppressPatch.SuppressNextOpen()` exactly like the F7 picker
     already does for the same reason (Escape closing a menu must not also
     bleed through to open the vanilla pause menu that same frame).
     **One extra wrinkle HelpScreen has that SavePicker doesn't:** the
     checkpoint mod tracks its OWN open/closed state internally
     (`tutorialMessageEnabled`, toggled only by its own `ShowTutorialMessage`
     calls). Closing our screen via Escape without telling it would leave
     that flag thinking the tutorial is still "open," so the next tutorial-key
     press would just close it again (a no-op, ours is already closed)
     instead of reopening — needing two presses to reopen after an
     Escape-close. Fixed with a new `CheckpointInterop.TryCloseTutorial()`
     (reflects `ShowTutorialMessage(false, "")`), called alongside `Close()`
     whenever Escape triggers it, so the checkpoint mod's own toggle stays in
     sync and a single tutorial-key press reopens correctly every time.
- Not yet re-verified in-game after this round of fixes.

  **Sixth, tiny, ask: the footer badge only advertised the tutorial key, not**
  Escape, even though Escape closes it too as of the fix above. Fixed:
  `_footerKeyText.text = $"{tutorialKey} / Esc"` (same one-badge-two-keys
  style the F7 picker's own footer already uses for "F7 / Enter").

**Step 3 confirmed working end-to-end (session 9), including this last visual**
**tweak. Considered done** unless something else comes up in further testing.

### Step 4 — Auto-revert fall damage from a flagged bad teleport

- Only runs off a `LastFlaggedTeleport` from step 1 (never unconditionally).
- On flag, snapshot `Character.localCharacter.refs.afflictions.GetCurrentStatus(
  STATUSTYPE.Injury)`. After 20s (config: `DamageRevertDelaySeconds`), read it
  again; if higher, `SubtractStatus(STATUSTYPE.Injury, delta)` once. Only fires
  once per flagged teleport.
- Note: legitimate damage taken in that same 20s window (e.g. a mob hit) would
  also get reverted, since we only see the net Injury delta, not its cause. This
  is an accepted, documented trade-off (config-toggleable:
  `EnableFallDamageRevert`, default true) — acceptable because it only engages
  after a teleport was already flagged as broken.

**Implemented (session 10), together with Step 5** — prompted by an in-game
report (below) of exactly the damage this step exists to undo. `RevertFallDamageRoutine`
in `TeleportWatchdog.cs`, started from `FlagBadTeleport` alongside the existing
on-screen hint. Matches the plan above as written; `SubtractStatus`/`GetCurrentStatus`
confirmed present and public on the decompiled `CharacterAfflictions` (via `ilspycmd`
against the installed game's `Assembly-CSharp.dll`).

### Step 5 — Auto position recovery from persistent glitching

- Also gated on `LastFlaggedTeleport`. If the watchdog's oscillation condition is
  still being met `PositionRecoveryDelaySeconds` (config, default matches or is
  inside the 30s window) after the flag, forcibly reposition the local player
  back to the recorded `targetPos` (the same warp the checkpoint mod itself was
  attempting), breaking the glitch loop early instead of waiting it out.
- Config-toggleable: `EnablePositionRecovery`, default true.

**Implemented (session 10).** Redesigned against the current `TeleportWatchdog.cs`
per the "before starting Step 4" warning further down this doc — the old
"oscillation condition" from the original plan (position/velocity sampling) no
longer exists in that form. Condition used instead: **distance from the real
target**, checked once, `PositionRecoveryDelaySeconds` (default 5s) after the
flag — if still more than `PositionRecoveryDistanceThreshold` (default 5m) away,
force the player there via `Character.WarpPlayerRPC(targetPos, false)` called
*directly* (not through Photon — this only ever needs to move the local player's
own view of themselves, exactly like the checkpoint mod's own correction would).
This also transparently covers fall-through and the "warped somewhere, but not
close enough" never-teleported case, not just the glitch loop specifically — any
flagged teleport where the player is still off-target after the delay gets
pulled back, which is simpler and more robust than trying to specifically detect
"is the glitch loop still running." Skips repositioning if the character is dead
or knocked out (`c.data.dead || c.data.fullyPassedOut`) at check time — not worth
yanking a corpse around, and the game's own revive flow already handles that case.
`FlagBadTeleport` was also fixed alongside this to consistently resolve the real
recorded `targetPos` (via a new `_currentTargetPos` field set in `BeginWatch`)
for kinds that weren't already passing an explicit override — previously the
distance-based "never teleported" case in `WatchRoutine` fell back to the
character's CURRENT position instead of the real target, which would have made
position recovery a no-op for that case.

**Prompted by (session 10) in-game report:** host (laptop) loaded via Shift+F7
(config 1); both host and client (desktop) loaded in fine and got the initial
teleport to the right campfire, but a few seconds later the host got warped a
short distance further, then the client got warped to the same spot a beat
later — the client then kept getting warped in a loop for roughly 10 repeats
over ~15s (still visibly starting from partway off the ground each time),
resolving on its own eventually, but racking up real fall damage in the
process (repeatedly re-warped mid-air before actually landing, so the game's
own fall-distance tracking kept accumulating). This is the checkpoint mod's own
`teleportFramesToWait`/correction-loop behavior — separately reproduced
host-side by setting that value to 100, which showed the same repeat-warp
pattern while still mid-load (before "Save game loaded!"), confirming it's the
checkpoint mod re-correcting a teleport it doesn't yet consider settled, not a
Quick Resume bug. Steps 4-5 above are the mitigation: shorten how long the
player is stuck in the loop (Step 5) and refund whatever fall damage still gets
through before that happens (Step 4). Not yet re-tested in-game with this
exact repro after the fix — do that before considering these two done.

**Re-tested (session 10) with both sides' `LogOutput.log` in hand — root cause**
**confirmed precisely, steps 4-5 alone weren't enough, added warp suppression.**
Same repro as above; damage was still taken (partially healed by Step 4's revert
around the 8th client re-warp, then re-accumulated over ~2 more). The HOST's log
made the actual mechanism unambiguous — `PEAK_Checkpoint_Save`'s own
`TeleportClientsToHost` coroutine re-sent `WarpPlayerRPC` to the client **30+
times**, every single one logged as correcting the same client back to the same
target Y because the client's `Head.y` (as the HOST observes it) was still off
by more than its 2m tolerance at every check — but the check interval is
`teleportFramesToWait` frames (40, raised by the Shift override), long enough
that ordinary gravity pulls the client back out of tolerance again before the
next check almost every time. It isn't a rare glitch, it's the retry loop
fighting its own cadence and structurally unable to converge within it, hence
"about 10, always eventually frees itself" — it only stops via the loop's own
30s/150-attempt ceiling or, rarely, a lucky check landing in-window.

**Fix: warp suppression**, new in `TeleportWatchdog.cs`/`TeleportWatchdogPatch.cs`,
config `enable-warp-suppression` (default true). The instant the checkpoint mod
reports the load done ("Save game loaded!", i.e. `ArmPendingWatch`), a new
`_suppressPostLoadWarps` flag goes true and a Harmony **prefix** (added
alongside the existing postfix) on the vanilla `Character.WarpPlayerRPC`
returns `false` for the local player for as long as it's set, cancelling the
call outright before its body ever runs — the checkpoint mod's redundant
corrections simply never take effect, so gravity between its (too-slow) checks
stops mattering. Cleared after `watchdog-window-seconds` (reusing that existing
knob rather than adding a near-duplicate one) or immediately by the next load
starting (`BeginLoadWindow`). One exception: Step 5's own `PositionRecoveryRoutine`
calls that exact same method to force a correction of its own — a new
`_isOwnRecoveryWarp` flag (set only for the duration of that one call) keeps
this from suppressing itself. The existing postfix (glitch detection/counting)
still runs even on a suppressed call, so `LastFlaggedTeleport`/steps 4-5 still
see it as a repeat attempt for their own bookkeeping.

Also fixed alongside this while reading the host log closely: `FlagBadTeleport`
now always resolves a real target position (via `_currentTargetPos`, set in
`BeginWatch`) for call sites that don't pass an explicit override, rather than
falling back to the character's current position — see the Step 5 write-up
above, this was a latent bug that would have made position recovery a no-op for
the `WatchRoutine` "never teleported" (distance) case specifically.

With suppression active, steps 4-5 become the fallback for whatever suppression
doesn't cover (anything before "Save game loaded!" fires, or if the feature is
disabled) rather than the primary fix — expected to fire far less often now.

**Re-tested (session 10) — big improvement, two small follow-ups.** Repeat count
dropped from ~10 down to 2 (both `LogOutput.log`s confirm suppression firing
correctly, four attempts logged as suppressed before the glitch flag even
tripped). Host-side, `TeleportClientsToHost` keeps counting/logging its own
attempts the entire time regardless of what the client does with them — it has
no idea the client is silently ignoring the RPC, it just keeps observing the
client's (still genuinely, if more slowly, drifting under normal gravity) synced
position and re-issuing corrections on its own schedule. Two things still
observed:
- A stray warp landed roughly 5s after everything had already settled and felt
  fine (interactable, no visible issue) — a straggler correction arriving right
  at the tail end of the checkpoint mod's own ~30s retry ceiling, which starts
  ticking at a slightly different moment than our suppression window does.
  Described as "more annoying than a real issue" rather than a real problem.
  **Fix:** new `warp-suppression-extra-seconds` config (default 2f), added on
  top of `watchdog-window-seconds` purely for the suppression-clear delay
  (`TeleportWatchdog.ClearWarpSuppressionAfterDelay`) — a safety buffer per
  explicit maintainer request ("extra safe"), not a guarantee this can never
  recur (the two retry loops still aren't synchronized to a shared clock), but
  cheap and low-risk.
- The "Did something go wrong?" hint still fired (glitch detection counts a
  suppressed attempt the same as a real one, by design, so steps 4-5's
  bookkeeping still sees it as a repeat) even though suppression meant nothing
  was actually visibly wrong on screen this time. Noted, not changed — the
  maintainer's report treated this as an accurate-enough description of events
  rather than something to fix, and the message itself is still correct
  (a correction attempt *was* happening) even if suppression hid its effect.
**Not yet re-tested in-game against the extra-seconds buffer specifically** — do
that next before considering steps 4-5-and-suppression fully done.

### Deferred (this phase)

- Dynamic `teleportTheKilnWorkaround` auto-enable for the Caldera-specific case —
  explicitly deferred by the maintainer, revisit later, not part of steps 1–5.

---

## Done

- [x] Decompiled + documented the vanilla run-start chain, death→Airport flow, and
      the checkpoint mod's load internals (`docs/RESEARCH.md`).
- [x] Repo scaffold, `.gitignore`, build that references game + BepInEx assemblies.
- [x] Reflection interop layer with a startup **probe** that logs which checkpoint
      members resolved (fast post-update diagnosis).
- [x] Orchestrator state machine (Airport → start run → wait for level → load).
- [x] Key handling with configurable double-press confirmation.
- [x] `dotnet build` succeeds; one-command deploy into the r2modman profile.
- [x] Install + testing guides (`docs/INSTALL.md`, `docs/TESTING.md`).

## Next up (Phase 1 — needs in-game testing; the maintainer is the tester)

- [x] **T1. Smoke test:** mod loads, probe logs `READY`, F7 recognized. (TESTING.md §1)
      ✅ session 1 — all 6 interop members `OK`, `READY`. Benign PEAKLib.ModConfig
      warning "Missing SettingType: resumeKey (KeyboardShortcut)" — PEAKLib can't render
      a keybind in its menu; same warning fires for the checkpoint mod's own keys. Ignore.
- [x] **T2. Resume from inside a run (was "post-death").** ✅ session 2 — mid-run F7
      lands in a fresh run at the saved campfire with restored state, repeatable.
- [x] **T3. Timing knobs** — default settle/timeout values worked first try; no tuning
      needed yet. Revisit only if a slower machine / bigger map reports timeouts.
- [~] **T4. Already-at-Airport path:** ⏳ retest after the SaveDiscovery fix (ascent
      detection). Was failing only because of the ascent=0 default; fix deployed.
- [ ] **T5. No-save guard:** F7 with no save for that difficulty aborts cleanly (now
      shows an on-screen "No save found" message too).
- [~] **T6. Higher ascents + custom runs:** custom-run support implemented (Phase 5
      mechanic 1) — `SaveDiscovery` now recognizes `peak_save_CustomRun_*` and we set
      `RunSettings.IsCustomRun` before start. ⏳ verify in-game: resume a custom run from
      the Airport and mid-run; confirm higher ascents still capture correctly.

## Phase 2 (mid-game resume)

- [x] **M1.** Mid-game return-to-Airport via `SceneManager.LoadScene("Airport")`
      (`LoadAirportDirect`) — ✅ validated in solo (this is the path the working mid-run
      test used). Still UNVALIDATED for coop (see C1).
- [x] **M2.** Mid-game F7 while alive → full cycle. ✅ works in solo. Guarded by
      `allowMidGame`.

## Phase 3 (multiplayer / coop) — implemented, NEEDS 2-PLAYER TESTING

Design confirmed from decompile: networked scene loads propagate to clients via
`PhotonNetwork.LoadLevel`, so the host starting a run (`kiosk.StartGame` →
`BeginIslandLoadRPC` to All) and returning to the Airport already bring everyone.
`LoadPlayerCoop` is host-only and teleports/restores all players itself.

- [x] **C0. Host-only + client feedback.** Non-host F7 shows "Only the host can
      resume the save!" and does nothing. Host drives the whole sequence.
- [x] **C1. Synchronized return-to-Airport.** ✅ code — Airport-start already worked in
      coop (test 1). **Fixed** the two broken cases (mid-game + post-death): they used
      `EndScreen.ReturnToAirport()` / local networked load, which left the client on the
      endscreen/old level → `LoadPlayerCoop` then teleported them into the sky/desynced.
      Now uses `GameOverHandler.LoadAirport()` (RPC-to-all), the same pattern as the
      run-start. ⏳ re-verify with 2 players.
      **First coop test results (session 4):** Airport-start = perfect for both players.
      Mid-game + post-death = worked for host, client bugged (sky, can't grab items) —
      exactly the desync this fix targets.
- [x] **C2. Client readiness gate.** Before `LoadPlayerCoop`, wait on the checkpoint
      mod's own `CheckReadyStatusForPlayers()`. ✅ (works; host log showed the wait).
      Added a coop-only `coopAirportSettle` delay so clients finish loading the Airport
      (and thus receive the kiosk run-start RPC) before the host starts the run.
- [x] **C3. Verified end-to-end in coop** (host + client). ✅ session 5 — Airport-start,
      mid-game, and post-death all work for BOTH players. The GameOverHandler fix resolved
      the client desync. **COOP DONE.**
- [ ] **C4. Mod-version check note:** we call `kiosk.StartGame` directly, bypassing the
      checkpoint mod's `BoardingPass.StartGame` patch (its optional client mod-version
      check). Functionally fine; revisit only if we want to enforce that check.
- [ ] **C5. `>4` players teleport bug** was historically finicky in the checkpoint mod
      (their changelog). If clients glitch after load, that's likely upstream; document
      the `teleportJumpLogic` workaround rather than fixing it here.

## Phase 4 (polish / release)

- [x] On-screen feedback — reuse the checkpoint mod's message overlay via interop
      (`ShowMessage`). ✅ session 2 (arm/confirm, starting, no-save, done).
- [x] Auto-detect saved ascent from disk — ✅ `SaveDiscovery` ("choose the latest").
- [x] Thunderstore packaging: `manifest.json`, `icon.png` (256×256), player `README.md`,
      `CHANGELOG.md`, dependency strings, dominik0207 credit. One-command release build
      (`packaging/build-release.sh` → `dist/PEAKQuickResume-<ver>.zip`). ✅ session 6.
      Optionally swap the generated placeholder icon later. Then upload to Thunderstore.
- [x] **License:** MIT (`LICENSE` at repo root, included in the release zip). ✅ session 6.
- [x] **F1 tutorial integration:** Harmony-postfix the checkpoint mod's `ShowTutorialMessage`
      to add a Quick Resume (F7) line and change the footer to
      "PCS Mod Version: X / Quick Resume Mod Version: Y" (`TutorialPatch.cs`). Defensive
      string anchoring; no-ops if the checkpoint wording changes. ✅ code, ⏳ verify F1 in-game.

---

## Known intermittent issue (upstream — deferred)

- **Rare "fresh slate" load** (session 5): once, a coop mid-game F7 landed players at the
  map START with no items instead of at the campfire. Our log was clean (fresh run started
  + `LoadPlayerCoop` invoked normally). Cause is inside the checkpoint mod's `LoadPlayerCoop`
  → either a silent early-return guard (notably the 30s `RecentlyLoaded` campfire cooldown,
  normally reset at the Airport) or, more likely, its `CustomJumpToSegment` teleport
  coroutine failing mid-way (historically flaky in multiplayer per their changelog).
  - **Diagnosis if it recurs:** `grep -iE "Checkpoint_Save|Custom Jump|Please wait" LogOutput.log`.
    Presence of `[Checkpoint_Save] Executing Custom Jump to: …` = restore ran, teleport
    failed; absence = an early guard returned. Also enable the checkpoint mod's
    `enableLogging` for its `Checkpoint_Save/Logs/Debug_latest.log`.
  - **Our possible mitigation (only if it recurs):** increase `Timing.settleAfterLevel` /
    `Timing.coopAirportSettle` so the level is more fully initialized before we trigger the
    restore. Not done yet — one-off, not reproduced.
  - Upstream levers: checkpoint mod's `jumpLogicWaitTime`, teleport-frames, `teleportJumpLogic`
    (try 1 or 2), and its manual F9 re-teleport.

## Watch out for / deferred issues

- **Hard dependency:** we declare a BepInEx hard-dep on `PEAK_Checkpoint_Save`, so
  our plugin won't load without it (intended). If the user reports we don't load,
  check the checkpoint mod is installed/enabled.
- **`MapBaker.GetLevel` patch ordering:** our `StartRun` relies on the checkpoint
  mod's patch being active. It is a separate plugin loaded before us (hard-dep), so
  the patch is registered by the time we call `StartGame`. If saved-scene loading
  ever fails, suspect this.
- **Endscreen timing:** if F7 is pressed before the `EndScreen` fully exists, the
  return-to-Airport fallback (direct scene load) kicks in. Fine offline; revisit for
  coop (M1/C1).
- **Achievements:** loading a save can grant Steam achievements (documented by the
  checkpoint mod). Not our concern to fix, but mention in README.
- **Config `settingsVersion`/save wipes on checkpoint-mod updates:** unrelated to us,
  but a checkpoint-mod update can delete saves — warn users before they rely on F7.
- **`AltTeleportJumpLogic` defaults to `2`, but decompile analysis (see
  `docs/RESEARCH.md`, "teleportJumpLogic ... why 0/1/2 behave so differently") shows
  value `2` (`MapHandler.GoToSegment`) is the vanilla walk-between-campfires transition:
  it never calls `WarpPlayerRPC` (doesn't move the player at all) and no-ops entirely
  if the target segment isn't strictly greater than wherever `currentSegment` already
  is. It is not a working teleport in solo or coop. Our help screen currently tells
  users "if it still happens, try Shift or Alt instead" — the Alt path is likely dead
  advice as configured. **Deliberately left as-is for now** (maintainer decision,
  2026-07-08) — revisit default/help-text wording in a future session.

## Handoff notes for the next session

**Pick up here: Phase 6, Step 4 (auto-revert fall damage), on branch**
**`feature/teleport-mitigation`.** Session 9 (2026-07-04/05) implemented and
in-game-confirmed Steps 1–3 end to end; the maintainer deliberately cut the
session here rather than starting Step 4 cold. Read the full "Phase 6" section
above (search for `## Phase 6`) before touching anything, it has the complete
narrative (every test result, every bug found and how it was root-caused, every
piece of maintainer feedback) — this summary is just the map, not a
substitute for it.

**What's done and confirmed working in-game (don't re-litigate these):**
- **Step 1 (`TeleportWatchdog`/`TeleportWatchdogPatch`):** detects all four bad-
  teleport symptoms (never-teleported, fall-through, knocked-out/died shortly
  after load, warp-loop glitch) via a vanilla `Character.WarpPlayerRPC` postfix
  + the checkpoint mod's own `LoadingScreen`/`ShowMessage` hooks, no game-logic
  changes, detection only. Validated against real numbers from multiple 2-PC
  self-hosted coop tests (see the test log entries in the Step 1 section).
- **Step 2 (`TeleportConfigOverride`):** Shift/Alt temporarily overrides the
  checkpoint mod's `teleportJumpLogic` (to configurable values, default 1/2)
  plus raises (never lowers) `teleportFramesToWait`/`jumpLogicWaitTime`, for
  one load, on both the native F6 path and our own F7/Enter path, auto-restores
  after ~35s. **Frontend (footer indicator) confirmed working. The actual
  backend effect (does holding Shift/Alt for a REAL load change teleport
  behavior and restore correctly after) is UNTESTED** — the maintainer
  deferred this specifically to avoid repeatedly pinging ~50 Steam friends
  with "is playing PEAK" notifications across more coop test cycles using
  their 2-account laptop+desktop setup. **Do this test before considering
  Step 2 done**, ideally alongside whatever Step 4/5 testing happens next.
- **Step 3 (`HelpScreen`/`HelpScreenContent`/rewritten `TutorialPatch`):** the
  F1 screen is now a real small menu (not text pasted over the checkpoint
  mod's own overlay), reusing `SavePicker`'s panel/font/badge/grain/dim-fade/
  jag-animation primitives (several were widened `private`→`internal` on
  `SavePicker` for this, no logic changed). Closes with either the tutorial
  key or Escape (synced with the checkpoint mod's own internal open/closed
  toggle via `CheckpointInterop.TryCloseTutorial()`, and suppresses the
  vanilla pause menu the same way `SavePicker` already does). **Confirmed
  fully working after 6 rounds of visual/content polish.** If you need a
  precedent for "how do I build a second screen that matches the F7 picker's
  look," `HelpScreen.cs` is now that precedent — reuse ITS pieces too rather
  than going back to `SavePicker` from scratch a third time.

**Before starting Step 4, re-check its plan against what Step 1 became:** the
Step 4/5 write-ups below were drafted BEFORE Step 1's oscillation detection was
reworked (see Step 1's "fourth/fifth in-game test" notes) from position/
velocity sampling to counting repeat `WarpPlayerRPC` calls via
`TeleportWatchdog.OnLocalWarp`. Step 5 in particular says "if the watchdog's
oscillation condition is still being met" — that condition doesn't exist in
quite that form anymore, re-derive it against the CURRENT `TeleportWatchdog.cs`
rather than assuming the old plan text still matches the code.

**Also still open / worth knowing:**
- The `teleportJumpLogic=1` (or 2) theory — that the checkpoint mod's own
  default of 0 may not fully unfog/load the next island's segment for a
  client, which is *why* it's the flakier setting — is explicitly UNCONFIRMED
  (flagged as such in both this file and `packaging/README.md`'s Notes
  section). Don't upgrade it to "known fact" without more dedicated testing;
  the maintainer doesn't know why dominik0207 chose 0 as the default either.
- Phase 5 Mechanic 3 (reset current run from scratch, skip the checkpoint
  load) is separately still TODO, unrelated to Phase 6, nearly free to add
  whenever it comes up again (see that section above).
- Build + deploy: `dotnet build -c Release -p:DeployToProfile=true` from
  `src/PeakQuickResume/` (see `docs/TESTING.md`). The maintainer is the only
  in-game tester (cannot run the game themselves) — build, deploy, then ask
  them to test and paste back `LogOutput.log`; don't assume a change works
  from compiling alone.
- The decompiled reference sources live under `scratch/` (git-ignored).
  Regenerate with the commands in `docs/RESEARCH.md` if missing.
