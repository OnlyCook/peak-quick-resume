using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Zorro.UI;

namespace PEAKQuickResume
{
    /// <summary>
    /// Optional QoL (<see cref="PluginConfig.MoveRebindControlsToSettings"/>, disabled by
    /// default): relocates the vanilla "Rebind Controls" button from the pause menu's main
    /// page into the Settings page, stacked below whatever's already lowest there (its own
    /// Back button, plus anything another mod already placed under it, e.g. PEAKLib.
    /// ModConfig's "Mod Settings")
    ///
    /// The pause menu can hold at most 9 visible buttons; in coop, mid-run, with every QoL
    /// button this mod adds plus other mods' own, that ceiling is easy to hit. Rebind
    /// Controls is used once (if ever) and then never again, so it's a good candidate to
    /// free up a row without losing functionality, it's just one extra click away instead
    ///
    /// Implemented by literally REPARENTING the existing button (not cloning it): its
    /// Button/onClick/LocalizedText all stay exactly as the game set them up, only its
    /// Transform moves. Its click handler was wired in <c>PauseMenuMainPage.Start()</c> as
    /// a closure over THAT instance, which still calls the correct (shared, one per pause
    /// menu) <c>UIPageHandler</c>, so it keeps transitioning to <c>PauseMenuControlsPage</c>
    /// correctly regardless of which page's hierarchy it now physically sits under
    ///
    /// Two hooks, not one, each solving a different half of the problem:
    ///
    ///  - <c>PauseMenuMainPage.OnEnable()</c> (already proven to re-fire every single time
    ///    the pause menu opens, see <see cref="PauseMenuPatch"/>) does the actual move,
    ///    re-evaluating the config live on every pause rather than only reacting once ever
    ///    like a plain <c>PauseMenuSettingsMenuPage.Start()</c> postfix would (Unity defers
    ///    a MonoBehaviour's Start() until its GameObject is first active, i.e. only once the
    ///    player has actually opened Settings, which made an earlier version of this feature
    ///    require exactly that one extra step before it took effect). Its
    ///    <c>backButton</c>-equivalent field lookups aren't even needed to do the move
    ///    itself: <see cref="PauseMenuSettingsMenuPage.backButton"/> is a serialized
    ///    reference, valid immediately even before that page's own Start() has ever run
    ///  - <c>PauseMenuSettingsMenuPage.Start()</c> gets a second, corrective postfix: other
    ///    mods that add their own Settings-page buttons (PEAKLib.ModConfig's "Mod Settings",
    ///    seen in practice) turned out to inject THEIRS via a hook on this exact method,
    ///    only the first time Settings is actually opened, and not even as a sibling of
    ///    Back (a different container entirely, at a hardcoded fixed position). The OnEnable
    ///    pass above can't see a button that doesn't exist yet, so this second pass re-runs
    ///    the same placement once Settings has actually been opened at least once, by which
    ///    point anything else's own injection into this method is guaranteed to have already
    ///    run too (a Harmony postfix here fires after the original method body, and after
    ///    any earlier-registered prefix, regardless of patching framework)
    ///
    /// Either pass alone leaves a gap: OnEnable alone can collide with a not-yet-existing
    /// competitor; the Settings-page postfix alone would need Settings opened once before
    /// doing anything at all. Running both means the button always moves immediately on
    /// pause, and self-corrects the moment there's enough information to place it right
    ///
    /// Positioning compares WORLD-space bottom edges (see <see cref="PositionBelowLowest"/>),
    /// not raw anchoredPosition: other mods' buttons aren't guaranteed to share Back's own
    /// parent transform, and anchoredPosition values from two different parents aren't
    /// comparable at all, only a common (world) space is
    /// </summary>
    public static class RebindControlsRelocationPatch
    {
        // Gap between whatever's already lowest in that column and the relocated button
        private const float ButtonSpacing = 10f;

        private static ManualLogSource _log;
        private static PluginConfig _cfg;
        private static FieldInfo _controlsButtonField; // PauseMenuMainPage.m_controllsButton

        private class RelocationState
        {
            public bool Moved;
            public Transform OriginalParent;
            public int OriginalSiblingIndex;
            public Vector2 OriginalAnchorMin, OriginalAnchorMax, OriginalPivot, OriginalSizeDelta, OriginalAnchoredPosition;
        }

        // Keyed by PauseMenuMainPage instance, same reasoning as PauseMenuPatch's own
        // table: a fresh scene load means a fresh instance, state shouldn't carry over
        private static readonly ConditionalWeakTable<PauseMenuMainPage, RelocationState> _state =
            new ConditionalWeakTable<PauseMenuMainPage, RelocationState>();

        public static void Apply(Harmony harmony, PluginConfig cfg, ManualLogSource log)
        {
            _cfg = cfg;
            _log = log;
            try
            {
                _controlsButtonField = AccessTools.Field(typeof(PauseMenuMainPage), "m_controllsButton");
                if (_controlsButtonField == null)
                {
                    log.LogWarning("RebindControlsRelocationPatch: m_controllsButton not found; "
                        + "moveRebindControlsToSettings will have no effect.");
                    return;
                }

                var onEnable = AccessTools.Method(typeof(PauseMenuMainPage), "OnEnable");
                harmony.Patch(onEnable, postfix: new HarmonyMethod(typeof(RebindControlsRelocationPatch), nameof(OnEnablePostfix)));

                // Corrective second pass: PEAKLib.ModConfig's own "Mod Settings" button
                // (and potentially other mods' own additions) is injected via a hook on
                // this EXACT method (a prefix, so it runs before Start()'s own body), and
                // only ever the first time the player actually opens Settings. A Harmony
                // postfix here is guaranteed to run after both that injection AND Start()'s
                // own body have finished, so re-running our placement at this point sees
                // everything that's actually there instead of racing it
                var settingsStart = AccessTools.Method(typeof(PauseMenuSettingsMenuPage), "Start");
                harmony.Patch(settingsStart, postfix: new HarmonyMethod(typeof(RebindControlsRelocationPatch), nameof(SettingsStartPostfix)));

                log.LogInfo("RebindControlsRelocationPatch: patched PauseMenuMainPage.OnEnable + "
                    + "PauseMenuSettingsMenuPage.Start (optional Rebind Controls relocation).");
            }
            catch (Exception e)
            {
                log.LogError($"RebindControlsRelocationPatch.Apply failed (non-fatal): {e}");
            }
        }

        private static void OnEnablePostfix(PauseMenuMainPage __instance)
        {
            try
            {
                __instance.StartCoroutine(ApplyNextFrame(__instance));
            }
            catch (Exception e)
            {
                _log.LogError($"RebindControlsRelocationPatch.OnEnablePostfix failed (non-fatal): {e}");
            }
        }

        private static IEnumerator ApplyNextFrame(PauseMenuMainPage mainPage)
        {
            yield return null;

            try
            {
                var controlsButton = (Button)_controlsButtonField.GetValue(mainPage);
                if (controlsButton == null) yield break;

                var state = _state.GetOrCreateValue(mainPage);
                bool wantMoved = _cfg.MoveRebindControlsToSettings.Value;

                if (wantMoved && !state.Moved) MoveToSettings(mainPage, controlsButton, state);
                else if (!wantMoved && state.Moved) MoveBackToMainPage(controlsButton, state);
            }
            catch (Exception e)
            {
                _log.LogError($"RebindControlsRelocationPatch.ApplyNextFrame failed (non-fatal): {e}");
            }
        }

        // Runs once, the first time the player actually opens Settings this scene load.
        // By this point PEAKLib.ModConfig's own "Mod Settings" button (if installed) is
        // guaranteed to already exist, so this just re-runs the same placement logic
        // MoveToSettings already did, now with an accurate picture of what's there
        private static void SettingsStartPostfix(PauseMenuSettingsMenuPage __instance)
        {
            try
            {
                if (!_cfg.MoveRebindControlsToSettings.Value) return;

                var mainPage = __instance.GetPageHandler<UIPageHandler>()?.GetPage<PauseMenuMainPage>() as PauseMenuMainPage;
                if (mainPage == null || !_state.TryGetValue(mainPage, out var state) || !state.Moved) return;

                var controlsButton = (Button)_controlsButtonField.GetValue(mainPage);
                if (controlsButton == null || __instance.backButton == null) return;

                var ctrlRect = (RectTransform)controlsButton.transform;
                var backRect = (RectTransform)__instance.backButton.transform;

                PositionBelowLowest(__instance.transform, ctrlRect, backRect, ExcludedSubtree(__instance));

                if (ctrlRect.parent is RectTransform parentRect)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(parentRect);

                _log.LogInfo("RebindControlsRelocationPatch: re-checked Rebind Controls position after Settings page init.");
            }
            catch (Exception e)
            {
                _log.LogError($"RebindControlsRelocationPatch.SettingsStartPostfix failed (non-fatal): {e}");
            }
        }

        private static void MoveToSettings(PauseMenuMainPage mainPage, Button controlsButton, RelocationState state)
        {
            var settingsPage = mainPage.GetPageHandler<UIPageHandler>()?.GetPage<PauseMenuSettingsMenuPage>() as PauseMenuSettingsMenuPage;
            if (settingsPage == null || settingsPage.backButton == null)
            {
                _log.LogWarning("RebindControlsRelocationPatch: Settings page/back button not found; "
                    + "leaving Rebind Controls where it is.");
                return;
            }

            var ctrlRect = (RectTransform)controlsButton.transform;

            // Remember exactly how/where this button sits today so it can be put back
            // byte-for-byte if the setting is ever turned off again mid-session
            state.OriginalParent = ctrlRect.parent;
            state.OriginalSiblingIndex = ctrlRect.GetSiblingIndex();
            state.OriginalAnchorMin = ctrlRect.anchorMin;
            state.OriginalAnchorMax = ctrlRect.anchorMax;
            state.OriginalPivot = ctrlRect.pivot;
            state.OriginalSizeDelta = ctrlRect.sizeDelta;
            state.OriginalAnchoredPosition = ctrlRect.anchoredPosition;

            var backRect = (RectTransform)settingsPage.backButton.transform;

            // Parented directly to the settings page's own ROOT transform, the exact
            // same transform PEAKLib.ModConfig itself parents "Mod Settings" to (see
            // PauseMenuSettingsMenuPageHooks.Prefix_Start passing `self.gameObject.
            // transform`), rather than Back's own immediate parent: Back may sit inside
            // a small container sized/masked just for the 1-2 buttons the vanilla page
            // ships with, and a 3rd button placed further down INSIDE that same
            // container could get silently clipped even though correctly positioned.
            // The page root is proven to render buttons fine at arbitrary Y (that's
            // where Mod Settings itself already renders correctly)
            Transform targetParent = settingsPage.transform;

            ctrlRect.SetParent(targetParent, worldPositionStays: false);

            // Match the Back button's own anchors/pivot for consistent positioning, but
            // deliberately NOT its sizeDelta: keep the button's own original width/height
            // from the pause menu (it already looked right there) rather than squeezing
            // it to Back's narrower size
            ctrlRect.anchorMin = backRect.anchorMin;
            ctrlRect.anchorMax = backRect.anchorMax;
            ctrlRect.pivot = backRect.pivot;

            PositionBelowLowest(settingsPage.transform, ctrlRect, backRect, ExcludedSubtree(settingsPage));

            if (targetParent is RectTransform parentRect)
                LayoutRebuilder.ForceRebuildLayoutImmediate(parentRect);

            state.Moved = true;
            _log.LogInfo("RebindControlsRelocationPatch: moved Rebind Controls into the Settings page.");
        }

        // The settings page's own scrollable list of actual settings (audio/graphics/
        // gameplay sliders, toggles, dropdowns) lives under sharedSettingsMenu, several
        // controls of which are themselves Buttons (dropdown headers, toggle switches,
        // etc.). Scanning the WHOLE page for "whichever Button sits lowest" without
        // excluding that subtree picks up something buried in that scrollable list
        // instead of a top-level page button, landing the relocated button somewhere
        // arbitrary (often off-screen or in an unrelated masked area, effectively
        // invisible) rather than actually below Back/Mod Settings
        private static Transform ExcludedSubtree(PauseMenuSettingsMenuPage settingsPage) =>
            settingsPage.sharedSettingsMenu != null ? settingsPage.sharedSettingsMenu.transform : null;

        // Places ctrlRect directly below whichever Button anywhere in the settings page
        // currently sits lowest on screen (Back itself, or another mod's own addition,
        // e.g. PEAKLib.ModConfig's "Mod Settings"). Compares WORLD-space bottom edges
        // rather than raw anchoredPosition: other mods' buttons aren't guaranteed to share
        // Back's own parent transform (ModConfig's "Mod Settings" doesn't, it turned out;
        // it's placed at its own hardcoded fixed anchoredPosition under a different
        // container entirely), and anchoredPosition values from two different parents
        // aren't comparable at all, only a common (world) space is
        private static void PositionBelowLowest(Transform settingsRoot, RectTransform ctrlRect, RectTransform fallback, Transform excludeSubtree)
        {
            RectTransform lowest = fallback;
            float lowestBottomWorldY = WorldBottomY(fallback);

            foreach (var btn in settingsRoot.GetComponentsInChildren<Button>(includeInactive: false))
            {
                var rt = (RectTransform)btn.transform;
                if (rt == ctrlRect) continue;
                if (excludeSubtree != null && rt.IsChildOf(excludeSubtree)) continue;
                float bottomY = WorldBottomY(rt);
                if (bottomY < lowestBottomWorldY)
                {
                    lowestBottomWorldY = bottomY;
                    lowest = rt;
                }
            }

            // Assign via Transform.position (WORLD space), not anchoredPosition: ctrlRect
            // and "lowest" aren't guaranteed to share a parent (or even a comparable
            // anchored-position convention), and a world-space position setter is
            // correct regardless of parent, sidestepping manual cross-parent coordinate
            // conversion entirely. lossyScale.y stands in for "this canvas's current
            // scale factor" (Canvas Scaler's Scale With Screen Size mode scales the
            // canvas itself, which every RectTransform under it inherits uniformly),
            // so both the button height and the fixed spacing convert consistently
            // Position ctrlRect's TOP edge `spacing` below the existing element's bottom
            // edge, then its own BOTTOM edge is heightWorld further down still (going
            // further down the screen means a SMALLER world Y, top-anchored UI): top =
            // lowestBottom - spacing; bottom = top - height; pivot = bottom + pivot.y *
            // height, which simplifies to top - height * (1 - pivot.y). The earlier
            // version added pivot.y * height to the wrong reference point (a "desired
            // bottom" that wasn't actually the bottom), which pushed the pivot UP above
            // the existing element's own bottom edge instead of below it, that's the
            // actual bug that put "Rebind Controls" back behind "Mod Settings"
            float scale = fallback.lossyScale.y;
            float heightWorld = ctrlRect.sizeDelta.y * scale;
            float desiredTopWorldY = lowestBottomWorldY - ButtonSpacing * scale;
            float desiredPivotWorldY = desiredTopWorldY - heightWorld * (1f - ctrlRect.pivot.y);

            Vector3 pos = ctrlRect.position;
            pos.y = desiredPivotWorldY;
            pos.x = fallback.position.x; // same on-screen column as Back
            ctrlRect.position = pos;

            _log.LogInfo($"RebindControlsRelocationPatch: positioned below '{lowest.name}' "
                + $"(world bottom {lowestBottomWorldY:F1}), final world pos {pos}, parent '{ctrlRect.parent?.name}', "
                + $"active-in-hierarchy={ctrlRect.gameObject.activeInHierarchy}.");
        }

        private static float WorldBottomY(RectTransform rt)
        {
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners); // 0 = bottom-left
            return corners[0].y;
        }

        private static void MoveBackToMainPage(Button controlsButton, RelocationState state)
        {
            var ctrlRect = (RectTransform)controlsButton.transform;
            ctrlRect.SetParent(state.OriginalParent, worldPositionStays: false);
            ctrlRect.SetSiblingIndex(state.OriginalSiblingIndex);
            ctrlRect.anchorMin = state.OriginalAnchorMin;
            ctrlRect.anchorMax = state.OriginalAnchorMax;
            ctrlRect.pivot = state.OriginalPivot;
            ctrlRect.sizeDelta = state.OriginalSizeDelta;
            ctrlRect.anchoredPosition = state.OriginalAnchoredPosition;

            if (state.OriginalParent is RectTransform parentRect)
                LayoutRebuilder.ForceRebuildLayoutImmediate(parentRect);

            state.Moved = false;
            _log.LogInfo("RebindControlsRelocationPatch: moved Rebind Controls back to the pause menu.");
        }
    }
}
