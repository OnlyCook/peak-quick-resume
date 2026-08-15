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
    /// Optional QoL (<see cref="PluginConfig.MoveRebindControlsToSettings"/>, off by default):
    /// reparents the vanilla "Rebind Controls" button from the pause menu's main page into
    /// the Settings page, below whatever's already lowest there (Back, or another mod's own
    /// addition e.g. PEAKLib.ModConfig's "Mod Settings"). Frees a row on the 9-button-max
    /// pause menu for a button that's typically used once, if ever.
    ///
    /// Reparents (doesn't clone) so the button's existing click handler/closure keeps working.
    /// Two hooks cover both timing gaps: <c>PauseMenuMainPage.OnEnable()</c> does the move on
    /// every pause; <c>PauseMenuSettingsMenuPage.Start()</c> re-runs it once Settings has
    /// actually been opened, since other mods (e.g. ModConfig) inject their own Settings
    /// buttons via a hook on that same method and OnEnable can't see a button that doesn't
    /// exist yet.
    /// </summary>
    public static class RebindControlsRelocationPatch
    {
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

        // Keyed by instance so a fresh scene load starts with fresh state (see PauseMenuPatch).
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

                // Corrective second pass: other mods (e.g. ModConfig) inject their own
                // Settings buttons via a prefix on this same method, only the first time
                // Settings opens. This postfix re-runs placement once that's happened.
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

        // Re-runs MoveToSettings' placement now that other mods' Settings buttons exist.
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

                _log.Trace("RebindControlsRelocationPatch: re-checked Rebind Controls position after Settings page init.");
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

            // Remember original layout so it can be restored if the setting is turned off.
            state.OriginalParent = ctrlRect.parent;
            state.OriginalSiblingIndex = ctrlRect.GetSiblingIndex();
            state.OriginalAnchorMin = ctrlRect.anchorMin;
            state.OriginalAnchorMax = ctrlRect.anchorMax;
            state.OriginalPivot = ctrlRect.pivot;
            state.OriginalSizeDelta = ctrlRect.sizeDelta;
            state.OriginalAnchoredPosition = ctrlRect.anchoredPosition;

            var backRect = (RectTransform)settingsPage.backButton.transform;

            // Parent to the settings page's own root (same as ModConfig's "Mod Settings"),
            // not Back's immediate parent, which may be a small container that would clip
            // a button placed further down inside it.
            Transform targetParent = settingsPage.transform;

            ctrlRect.SetParent(targetParent, worldPositionStays: false);

            // Match Back's anchors/pivot, but keep our own sizeDelta (original width/height).
            ctrlRect.anchorMin = backRect.anchorMin;
            ctrlRect.anchorMax = backRect.anchorMax;
            ctrlRect.pivot = backRect.pivot;

            PositionBelowLowest(settingsPage.transform, ctrlRect, backRect, ExcludedSubtree(settingsPage));

            if (targetParent is RectTransform parentRect)
                LayoutRebuilder.ForceRebuildLayoutImmediate(parentRect);

            state.Moved = true;
            _log.Trace("RebindControlsRelocationPatch: moved Rebind Controls into the Settings page.");
        }

        // Excludes sharedSettingsMenu's own scrollable controls (dropdowns, toggles, etc.),
        // which are also Buttons and would otherwise get picked up as "lowest".
        private static Transform ExcludedSubtree(PauseMenuSettingsMenuPage settingsPage) =>
            settingsPage.sharedSettingsMenu != null ? settingsPage.sharedSettingsMenu.transform : null;

        // Places ctrlRect below whichever Button in the settings page sits lowest on screen,
        // comparing world-space bottom edges since other mods' buttons don't share Back's
        // parent transform and anchoredPosition values across parents aren't comparable.
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

            // Uses Transform.position (world space) since ctrlRect and "lowest" may not
            // share a parent. pivotY = topY - height * (1 - pivot.y), derived from
            // top = lowestBottom - spacing, bottom = top - height, pivot = bottom + pivot.y * height.
            float scale = fallback.lossyScale.y;
            float heightWorld = ctrlRect.sizeDelta.y * scale;
            float desiredTopWorldY = lowestBottomWorldY - ButtonSpacing * scale;
            float desiredPivotWorldY = desiredTopWorldY - heightWorld * (1f - ctrlRect.pivot.y);

            Vector3 pos = ctrlRect.position;
            pos.y = desiredPivotWorldY;
            pos.x = fallback.position.x; // same on-screen column as Back
            ctrlRect.position = pos;

            _log.Trace($"RebindControlsRelocationPatch: positioned below '{lowest.name}' "
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
            _log.Trace("RebindControlsRelocationPatch: moved Rebind Controls back to the pause menu.");
        }
    }
}
