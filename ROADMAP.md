# ROADMAP — PEAK Quick Resume

> One-key resume for PEAK: press a key to start a fresh run of your saved
> difficulty and immediately load your "PEAK Checkpoint Save" checkpoint.
> This mod is an **orchestrator** — dominik0207's *PEAK Checkpoint Save* still
> does all real saving/loading; we just automate the tedious manual steps.

**Status:** v0.2.0 — Phases 1–4 + Phase 5 mechanics 1 (custom runs) & 2 (F7 save
picker) working in-game (solo + 2-player coop). Mechanic 3 (reset run) still TODO.
**Last updated:** 2026-07-02 (session 7).

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

## Handoff notes for the next session

- Build + deploy: `dotnet build -c Release -p:DeployToProfile=true` (see TESTING.md).
- The decompiled reference sources live under `scratch/` (git-ignored). Regenerate
  with the commands in `docs/RESEARCH.md` if missing.
- First real milestone is **T2**. Everything is logged; grab the BepInEx
  console / `LogOutput.log` after a test and tune from there.
```
