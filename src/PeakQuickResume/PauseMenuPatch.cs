using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PEAKQuickResume
{
    /// <summary>
    /// Injects three in-game-styled buttons into the vanilla pause menu
    /// (<see cref="PauseMenuMainPage"/>) by cloning an existing button: "Restart"
    /// (mid-run, host only), "Return to Airport" (mid-run, host only), "Board Flight"
    /// (Airport only, any player - opens the gate-kiosk UI directly). Rebuilt on every
    /// fresh <see cref="PauseMenuMainPage"/> instance, since it isn't DontDestroyOnLoad.
    /// </summary>
    public static class PauseMenuPatch
    {
        private static ManualLogSource _log;
        private static PluginConfig _cfg;

        private static MethodInfo _windowOpen;
        private static MethodInfo _windowClose;
        private static FieldInfo _templateButtonField; // m_accoladesButton
        private static FieldInfo _quitButtonField;      // m_quitButton (sibling anchor)
        private static FieldInfo _confirmOkField;       // m_confirmOkButton
        private static FieldInfo _confirmCancelField;   // m_confirmCancelButton

        private class ButtonEntry
        {
            public GameObject GameObject;
            public TextMeshProUGUI Text;
            public Func<string> Localize; // re-evaluated every UpdateVisibility, tracks the current language
        }

        private class Buttons
        {
            public ButtonEntry Restart;
            public ButtonEntry ReturnToAirport;
            public ButtonEntry OpenKiosk;
        }

        // Keyed by PauseMenuMainPage instance so a fresh scene's instance starts clean
        private static readonly ConditionalWeakTable<object, Buttons> _built = new ConditionalWeakTable<object, Buttons>();

        public static void Apply(Harmony harmony, PluginConfig cfg, ManualLogSource log)
        {
            _log = log;
            _cfg = cfg;
            try
            {
                _windowOpen = AccessTools.Method(typeof(MenuWindow), "Open");
                _windowClose = AccessTools.Method(typeof(MenuWindow), "Close");
                _templateButtonField = AccessTools.Field(typeof(PauseMenuMainPage), "m_accoladesButton");
                _quitButtonField = AccessTools.Field(typeof(PauseMenuMainPage), "m_quitButton");
                _confirmOkField = AccessTools.Field(typeof(PauseMenuMainPage), "m_confirmOkButton");
                _confirmCancelField = AccessTools.Field(typeof(PauseMenuMainPage), "m_confirmCancelButton");

                if (_windowOpen == null || _windowClose == null || _templateButtonField == null
                    || _quitButtonField == null || _confirmOkField == null || _confirmCancelField == null)
                {
                    log.LogWarning("PauseMenuPatch: one or more pause menu members not found; "
                        + "Restart / Return to Airport / Board Flight buttons will not be added. "
                        + "The pause menu itself is unaffected.");
                    return;
                }

                var start = AccessTools.Method(typeof(PauseMenuMainPage), "Start");
                var onEnable = AccessTools.Method(typeof(PauseMenuMainPage), "OnEnable");
                harmony.Patch(start, postfix: new HarmonyMethod(typeof(PauseMenuPatch), nameof(StartPostfix)));
                harmony.Patch(onEnable, postfix: new HarmonyMethod(typeof(PauseMenuPatch), nameof(OnEnablePostfix)));
                log.LogInfo("PauseMenuPatch: patched PauseMenuMainPage.Start/OnEnable "
                    + "(Restart / Return to Airport / Board Flight buttons).");
            }
            catch (Exception e)
            {
                log.LogError($"PauseMenuPatch.Apply failed (non-fatal, pause menu unaffected): {e}");
            }
        }

        private static void StartPostfix(PauseMenuMainPage __instance)
        {
            try
            {
                if (_built.TryGetValue(__instance, out _)) return;

                var template = (Button)_templateButtonField.GetValue(__instance);
                var quitButton = (Button)_quitButtonField.GetValue(__instance);
                if (template == null || quitButton == null)
                {
                    _log.LogWarning("PauseMenuPatch: template/anchor button missing on this instance; skipping.");
                    return;
                }

                Transform parent = template.transform.parent;
                int insertIndex = quitButton.transform.GetSiblingIndex();

                var b = new Buttons
                {
                    // Crimson for Restart (the most severe action), teal for Return to
                    // Airport / Board Flight (calmer "just travel" actions).
                    Restart = MakeButton(template, parent, insertIndex++,
                        () => PauseMenuLocalization.Get(ButtonLabel.Restart),
                        () => OnRestartClicked(__instance), new Color(0.80f, 0.20f, 0.15f)),
                    ReturnToAirport = MakeButton(template, parent, insertIndex++,
                        () => PauseMenuLocalization.Get(ButtonLabel.ReturnToAirport),
                        () => OnReturnToAirportClicked(__instance), new Color(0.12f, 0.55f, 0.58f)),
                    // Reuses the game's own "BOARDFLIGHT" string, forced to all-caps to match
                    // the other buttons (ToUpperInvariant to sidestep Turkish "i" casing).
                    OpenKiosk = MakeButton(template, parent, insertIndex++,
                        () => LocalizedText.GetText("BOARDFLIGHT").ToUpperInvariant(),
                        () => OnOpenKioskClicked(__instance), new Color(0.12f, 0.55f, 0.58f)),
                };
                _built.Add(__instance, b);

                if (parent is RectTransform rt)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rt);

                UpdateVisibility(b);
            }
            catch (Exception e)
            {
                _log.LogError($"PauseMenuPatch.StartPostfix failed (non-fatal): {e}");
            }
        }

        private static void OnEnablePostfix(PauseMenuMainPage __instance)
        {
            try
            {
                if (_built.TryGetValue(__instance, out var b))
                    UpdateVisibility(b);
            }
            catch (Exception e)
            {
                _log.LogError($"PauseMenuPatch.OnEnablePostfix failed (non-fatal): {e}");
            }
        }

        private static void UpdateVisibility(Buttons b)
        {
            bool hostAction = RunLauncher.IsHost && RunLauncher.InLevel;
            SetActiveAndRefresh(b.Restart, hostAction && _cfg.ShowRestartButton.Value);
            SetActiveAndRefresh(b.ReturnToAirport, hostAction && _cfg.ShowReturnToAirportButton.Value);
            SetActiveAndRefresh(b.OpenKiosk, RunLauncher.InAirport && _cfg.ShowBoardFlightButton.Value);
        }

        // Re-reads the translation every call, so a language change in Settings takes effect
        // next time the player pauses, without a separate language-change subscription.
        private static void SetActiveAndRefresh(ButtonEntry entry, bool active)
        {
            entry.GameObject.SetActive(active);
            if (active && entry.Text != null) entry.Text.text = entry.Localize();
        }

        private static ButtonEntry MakeButton(Button template, Transform parent, int siblingIndex,
            Func<string> localize, UnityEngine.Events.UnityAction onClick, Color? bannerColor = null)
        {
            string label = localize();
            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
            clone.name = "PEAKQuickResume_" + label.Replace(" ", "");
            clone.transform.SetSiblingIndex(siblingIndex);

            Button btn = clone.GetComponent<Button>();
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(onClick);

            if (bannerColor.HasValue)
            {
                // Selectable.targetGraphic is the fill Image, not necessarily on the root
                // GameObject. The border Image is a shade variant of the fill, not a fixed
                // color, so its new shade is derived from the template's own fill/border ratio.
                Image fill = btn.targetGraphic as Image ?? clone.GetComponentInChildren<Image>(includeInactive: true);
                if (fill != null)
                {
                    Color origFill = fill.color;
                    List<Image> others = clone.GetComponentsInChildren<Image>(includeInactive: true)
                        .Where(i => i != fill).ToList();
                    var origOthers = others.Select(i => i.color).ToList();

                    fill.color = bannerColor.Value;

                    for (int i = 0; i < others.Count; i++)
                    {
                        Color orig = origOthers[i];
                        float rr = origFill.r > 0.001f ? orig.r / origFill.r : 1f;
                        float rg = origFill.g > 0.001f ? orig.g / origFill.g : 1f;
                        float rb = origFill.b > 0.001f ? orig.b / origFill.b : 1f;
                        others[i].color = new Color(
                            Mathf.Clamp01(bannerColor.Value.r * rr),
                            Mathf.Clamp01(bannerColor.Value.g * rg),
                            Mathf.Clamp01(bannerColor.Value.b * rb),
                            orig.a);
                    }
                }
            }

            LocalizedText loc = clone.GetComponentInChildren<LocalizedText>(includeInactive: true);
            TextMeshProUGUI tmp;
            if (loc != null)
            {
                tmp = loc.tmp as TextMeshProUGUI ?? clone.GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
                loc.SetText(label);
                // LocalizedText.OnEnable() re-derives its index from a serialized `row` field
                // (0 on a clone) and stomps our text with a "LOC: 0" placeholder on re-enable.
                // Disable it permanently instead; we drive tmp.text ourselves (see SetActiveAndRefresh).
                loc.enabled = false;
            }
            else
            {
                tmp = clone.GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
                if (tmp != null) tmp.text = label;
            }

            return new ButtonEntry { GameObject = clone, Text = tmp, Localize = localize };
        }

        private static void OnRestartClicked(PauseMenuMainPage instance)
        {
            if (!RunLauncher.IsHost) return;
            OpenConfirm(instance,
                PauseMenuLocalization.Get(ConfirmDialog.Restart),
                () =>
                {
                    ClosePauseMenu(instance);
                    Plugin.Instance?.RequestRestart();
                });
        }

        private static void OnReturnToAirportClicked(PauseMenuMainPage instance)
        {
            if (!RunLauncher.IsHost) return;
            OpenConfirm(instance,
                PauseMenuLocalization.Get(ConfirmDialog.ReturnToAirport),
                () =>
                {
                    ClosePauseMenu(instance);
                    Plugin.Instance?.RequestReturnToAirport();
                });
        }

        private static void OnOpenKioskClicked(PauseMenuMainPage instance)
        {
            ClosePauseMenu(instance);
            Plugin.Instance?.RequestOpenGateKiosk();
        }

        // Reuses the same confirm dialog + OK/Cancel buttons the vanilla "Leave Game" flow
        // uses, reconfiguring the OK listener and text each time.
        private static void OpenConfirm(PauseMenuMainPage instance, string text, Action onConfirm)
        {
            try
            {
                object confirmWindow = instance.confirmWindow;
                _windowOpen.Invoke(confirmWindow, null);
                instance.confirmText.SetText(text);

                var ok = (Button)_confirmOkField.GetValue(instance);
                var cancel = (Button)_confirmCancelField.GetValue(instance);

                ok.onClick.RemoveAllListeners();
                ok.onClick.AddListener(() =>
                {
                    _windowClose.Invoke(confirmWindow, null);
                    onConfirm();
                });
                cancel.Select();
            }
            catch (Exception e)
            {
                _log.LogError($"PauseMenuPatch.OpenConfirm failed (non-fatal): {e}");
            }
        }

        private static void ClosePauseMenu(PauseMenuMainPage instance)
        {
            try
            {
                PauseMenuHandler handler = instance.GetComponentInParent<PauseMenuHandler>();
                if (handler != null) handler.gameObject.SetActive(false);
            }
            catch (Exception e)
            {
                _log.LogError($"PauseMenuPatch.ClosePauseMenu failed (non-fatal): {e}");
            }
        }
    }
}
