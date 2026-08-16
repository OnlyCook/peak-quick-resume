# Duplicate flares in The Citadel — diagnosed, fix deferred

Reported symptom: loading a save taken in **The Citadel** spawns the correct set of
flares in the peak basket *plus* a second, floating batch nearby in the sky. Not
observed in **The Kiln** (can't currently be re-tested — see "Why this isn't shipped
yet" below).

## Root cause

Two independent things put flares near the peak on load:

1. `OwnTeleportSequence.cs` (~line 373) re-triggers every `ISpawner` under the target
   segment's `segmentParent`:
   ```csharp
   foreach (ISpawner spawner in targetSegment.segmentParent.GetComponentsInChildren<ISpawner>())
       spawner.TrySpawnItems();
   ```
   This includes the vanilla flare basket's own `Spawner` components (8 of them,
   `Flare_Spawner`/`Flare (1)_Spawner`.../`Flare (7)_Spawner`, each a `Spawner` with
   `belowAscentRequirement` set — matches the "flares only up to ascent 3" behavior).
   This produces the *correct* flares.

2. `OwnEnvironmentReset.SpawnFlaresAtPeak()`, called right after from
   `OwnTeleportSequence.cs` (`if ((int)finalSegment == 4 && Ascents.currentAscent < 4)`),
   spawns a *second* batch of 10 flares at a hardcoded world position
   `(19f, 1228.1f, 2240f)`. This is a field-for-field port of the old checkpoint-save
   mod's own flare-respawn routine, written before the Gloom/Citadel biome variant
   existed.

The Citadel and The Kiln are two different scenes that both map to the same
`Segment.TheKiln` (see `docs/RESEARCH.md`'s scene-placement note):

```
Map / Biome_4 / Gloom   / Temple_Segment  / Peak               <- THE CITADEL
Map / Biome_4 / Caldera / Volcano / Volcano_Segment / Peak_Kiln Variant   <- THE KILN
```

The hardcoded coordinate happens to land inside the Kiln basket but floats in empty
space above the Citadel basket — hence the bug appearing only in Citadel.

## Verified with UnityPy against the live game files

Inspected `PEAK_Data/level4` directly (not guesswork). Both biome variants have an
identical, symmetric spawner layout:

```
Map/Biome_4/Gloom/Temple_Segment/Peak/Box/Flare_Spawner .. Flare (7)_Spawner        (Citadel, 8 spawners)
Map/Biome_4/Caldera/Volcano/Volcano_Segment/Peak_Kiln Variant/Box/Flare_Spawner .. Flare (7)_Spawner   (Kiln, 8 spawners)
```

Both `Peak` and `Peak_Kiln Variant` sit directly under the node
`targetSegment.segmentParent` resolves to for their respective biome. Since the
`ISpawner`/`TrySpawnItems()` pass (item 1 above) walks that exact node, it should
re-trigger the Kiln's flare spawners exactly the same way it does Citadel's — nothing
in that code path is biome-specific. Structurally, removing `SpawnFlaresAtPeak()`
should not affect Kiln flare spawning at all, since that function is pure duplication
on top of a mechanism common to both biomes.

## Why this isn't shipped yet

The map-pool rotation currently only shows The Citadel (see
`scratch/map-pool-backup/README.md` — Kiln returns roughly late Aug/early Sep 2026).
The `TrySpawnItems()` theory above is well-supported by the scene structure but has
not been confirmed by an actual in-game load test on The Kiln. Given the stakes (a
player stranded flareless at the final segment can't finish the run), the fix is
**not applied** — `OwnEnvironmentReset.SpawnFlaresAtPeak()` and its call site in
`OwnTeleportSequence.cs` are left exactly as they were, duplicate-flare bug and all.

## Plan

Once The Kiln is back in the map pool: load a save taken in The Kiln, confirm flares
still appear correctly in the basket. If confirmed, apply the fix:

- Remove the `SpawnFlaresAtPeak()` call in `OwnTeleportSequence.cs`
  (`if ((int)finalSegment == 4 && Ascents.currentAscent < 4) StartCoroutine(...)`).
- Remove the now-dead `OwnEnvironmentReset.SpawnFlaresAtPeak()` method.
- Update the `OwnEnvironmentReset` class doc comment (currently says "Peak flare
  spawn" as one of its jobs).

No other files reference `SpawnFlaresAtPeak`.
