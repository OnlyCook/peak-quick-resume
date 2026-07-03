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
    /// Miscellaneous QoL: inject three in-game-styled buttons into the vanilla pause
    /// menu (<see cref="PauseMenuMainPage"/>), matching the screenshot's banner style
    /// by cloning an existing button rather than building new UI from scratch:
    ///
    ///   - "Restart"            mid-run only, host only. Confirms, then restarts the
    ///                          current run at the same difficulty (see <see cref="RestartOrchestrator"/>)
    ///   - "Return to Airport"  mid-run only, host only. Confirms, then sends
    ///                          everyone back to the Airport (no new run started)
    ///   - "Board Flight"       Airport only, any player. Opens the gate-kiosk UI
    ///                          directly, skipping the walk over to it
    ///
    /// <see cref="GUIManager"/>/<see cref="PauseMenuMainPage"/> are NOT DontDestroyOnLoad
    /// (each scene load, offline or Photon, is a full Single-mode scene swap), so a
    /// fresh instance is created every time and <c>Start()</c> runs again. We rebuild
    /// our buttons on every such instance rather than trying to make them survive
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
                    // Restart and Return to Airport are lobby-wide disruptive actions, so
                    // give them their own colors instead of inheriting the Accolades
                    // button's gold: a crimson (close kin to "Leave Game"'s red, since
                    // Restart is the more severe of the two, it skips straight into a
                    // fresh run) and a teal (a calmer "just travel, nothing is lost" cue
                    // for Return to Airport). Board Flight is non-destructive, so it
                    // keeps the cloned gold as-is
                    Restart = MakeButton(template, parent, insertIndex++,
                        () => PauseMenuLocalization.Get(ButtonLabel.Restart),
                        () => OnRestartClicked(__instance), new Color(0.80f, 0.20f, 0.15f)),
                    ReturnToAirport = MakeButton(template, parent, insertIndex++,
                        () => PauseMenuLocalization.Get(ButtonLabel.ReturnToAirport),
                        () => OnReturnToAirportClicked(__instance), new Color(0.12f, 0.55f, 0.58f)),
                    // Reuses the game's own official "BOARDFLIGHT" string (same text shown
                    // interacting with the kiosk directly), guaranteed to match/already
                    // translated into every language the game ships, no guesswork needed.
                    // That string comes back in normal case ("Board Flight" / "An Bord
                    // gehen" / ...), but every other pause menu button is all-caps, so
                    // force it to match. ToUpperInvariant (not culture-sensitive ToUpper)
                    // to sidestep the Turkish dotted/dotless "i" casing quirk
                    OpenKiosk = MakeButton(template, parent, insertIndex++,
                        () => LocalizedText.GetText("BOARDFLIGHT").ToUpperInvariant(),
                        () => OnOpenKioskClicked(__instance)),
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

        // Re-reads the label's translation every time this runs (Start + every pause
        // menu OnEnable), so a language change made in the game's own Settings takes
        // effect the next time the player pauses/returns to this page, no separate
        // language-change event subscription needed
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
                // Selectable.targetGraphic is whichever Image the button itself treats
                // as its banner fill (for hover/press tinting) - not necessarily on the
                // root GameObject. GetComponent<Image>() on the root came back null on
                // these buttons, which is why the first attempt at this silently did
                // nothing. There's also a separate Image for the dashed-stitch border,
                // which is a shade VARIANT of the fill color (not a fixed color), not
                // touching it left it looking like leftover Accolades gold. Rather than
                // hardcoding a second color per button, derive the border's new color
                // from the template's own fill/border ratio, so any custom fill color
                // automatically gets a matching border shade
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
                // loc.tmp is typed as the base TMP_Text; our buttons use TextMeshProUGUI
                tmp = loc.tmp as TextMeshProUGUI ?? clone.GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
                loc.SetText(label);
                // LocalizedText.OnEnable() re-derives `index` from its serialized `row`
                // field (0 on a clone) whenever the GameObject is re-enabled, and then
                // calls RefreshText(), stomping our text with the "LOC: 0" placeholder.
                // Our buttons get SetActive() toggled every time the pause menu
                // context changes, so disable the component permanently instead. We
                // drive `tmp.text` ourselves from here on (see SetActiveAndRefresh)
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

        // Reuses the SAME confirm dialog + OK/Cancel buttons the vanilla "Leave Game"
        // flow uses (PauseMenuMainPage.OpenQuitConfirmWindow), reconfiguring the OK
        // listener and text each time, exactly like the game's own code does
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
