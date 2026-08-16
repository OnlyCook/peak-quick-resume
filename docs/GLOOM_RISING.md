# Hardcoded lava-reset height on load — diagnosed, fix deferred

Reported symptom (2026-08-16): maintainer asked whether this mod could stop The
Citadel's rising fog from ever rising. Diagnosed via decompile, then ruled out
empirically by in-game testing (fresh run, no F7 use at all; and F7 used once but not
to load into Citadel — fog rose normally both times, ~3 minutes in, matching vanilla's
own arming delay). No live bug in Citadel today. This doc records a related latent
issue found along the way, in the same function, not yet fixable for the same reason
`docs/FLARES.md` gives: The Kiln isn't in the map pool to test against.

## Root cause

`OwnEnvironmentReset.ResetLavaAfterLoad()` (`src/PeakQuickResume/OwnEnvironmentReset.cs`)
runs whenever a loaded save resolves into segment 4, and hardcodes the rising object's
rigidbody to `y = 847.8f`. That's a field-for-field port of the old checkpoint-save
mod's own lava-reset routine, written before the Gloom/Citadel biome variant existed.

The Citadel and The Kiln are two different scenes that both map to the same
`Segment.TheKiln` (see `docs/RESEARCH.md`'s scene-placement note, and `docs/FLARES.md`
for the identical problem shape with flare spawning):

```
Map / Biome_4 / Gloom   / Temple_Segment  / Peak               <- THE CITADEL
Map / Biome_4 / Caldera / Volcano / Volcano_Segment / Peak_Kiln Variant   <- THE KILN
```

Citadel's rising fog and the Kiln's actual lava are both a `LavaRising` component
(decompile: `risingFieldType == Gloom` vs `Lava`), and `847.8f` is presumably tuned to
the Kiln lava's own rest height. There's no reason to expect it also happens to be
correct for Citadel's fog plane in a structurally different scene.

**Why this hasn't actually broken anything observed so far:** `LavaRising.Update()`
only repositions the rigidbody while `started` is true, recomputing from
`Mathf.Lerp(startHeight, topTransform.position.y, timeTraveled/travelTime)` — where
`startHeight` is a *different*, private field cached by vanilla's own `Start()` from
the object's real position at scene load, never touched by our reset. So the moment
the fog actually starts rising, our hardcoded value gets silently overwritten by the
correct one anyway — consistent with testing showing no visible difference. The
`847.8f` write only matters in the window between a load and the field arming, and
only if that window is ever visible or if the field never arms at all in a given run
(e.g. Tenderfoot, or a hazard-disable path) — neither of which the testing above
happened to exercise for Citadel specifically, and Kiln wasn't tested either.

## The fix (written, not applied)

Read the object's own `startHeight` private field reflectively instead of hardcoding
a constant, and skip repositioning (rather than guessing) if it can't be read:

```csharp
if (lava.lava != null)
{
    object startHeightValue = typeof(LavaRising)
        .GetField("startHeight", BindingFlags.Instance | BindingFlags.NonPublic)
        ?.GetValue(lava);
    if (startHeightValue is float startHeight)
    {
        Vector3 position = lava.lava.position;
        position.y = startHeight;
        lava.lava.position = position;
    }
    else
    {
        log?.LogWarning("OwnEnvironmentReset: could not read LavaRising's own startHeight; "
            + "leaving its position untouched rather than guessing.");
    }
}
```

This is correct for whichever segment-4 variant is actually active by construction
(reads that instance's own cached rest height), instead of assuming the Kiln's number
applies to both. Given the "gets overwritten once started" behavior above, this should
be a no-op for the Kiln in the common case — but "should be" is exactly the kind of
claim `docs/FLARES.md` already flagged as not good enough to ship on inference alone,
for the same underlying reason: a maintainer stranded with the wrong hazard state at
the final segment is a bad failure mode to risk on an untested guess, and the current
`847.8f` is known to have worked in The Kiln (no reported issues) for as long as it's
existed, which the replacement hasn't earned yet.

## Why this isn't shipped yet

The map-pool rotation currently only shows The Citadel (see
`scratch/map-pool-backup/README.md` — Kiln returns roughly late Aug/early Sep 2026).
`src/PeakQuickResume/OwnEnvironmentReset.cs` is left exactly as it was — the `847.8f`
constant, unguarded, same as always — rather than risk the untested reflection path on
the one biome variant this mod has actual track record with.

## Plan

Once The Kiln is back in the map pool: load a save taken in The Kiln, confirm the lava
still resets and rises normally with the change above applied (build with it in,
in-game test before merging/shipping). If confirmed:

- Apply the diff above to `ResetLavaAfterLoad`.
- Also worth a Citadel re-test at that point on Tenderfoot specifically (`Ascents
  .currentAscent == -1`), since that's the one case reasoned above but never actually
  observed where the pre-fix hardcoded value could sit uncorrected indefinitely rather
  than being overwritten by `Update()`.
- Delete this doc, or fold a one-line note into `docs/RESEARCH.md`'s Nadir/Citadel
  section if anything about the investigation is worth keeping.
