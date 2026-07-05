using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PEAKQuickResume
{
    /// <summary>
    /// A big, clearly-labeled, actually-looks-like-a-toggle stand-in for the checkpoint
    /// mod's own boarding-pass "use saved island / new island" checkbox: a plain small
    /// blue square with an even smaller green inner square, easy to miss entirely and
    /// easy to mis-click even once you know it's there (see ROADMAP.md). We never touch
    /// that checkbox's own behavior, only mirror + drive it: this button flips the
    /// checkpoint mod's own <c>Toggle.isOn</c> (see <see cref="CheckpointInterop.TryGetBoardingToggle"/>),
    /// which fires ITS OWN listener exactly like a real click on the tiny original would
    /// (persists its config, refreshes its own overlay text) - we add no save-toggling
    /// logic of our own, just a bigger, obvious, self-explaining door to the same knob
    ///
    /// Visibility/position/on-off state are all polled every frame from the checkpoint
    /// mod's own toggle GameObject rather than hooked via Harmony on its various
    /// OnOpen/OnClose/HideIt/StartGame call sites: several of those set
    /// <c>_boardingToggle.gameObject.SetActive(...)</c> as a follow-up statement AFTER
    /// calling ShowBoardingpassMessage (e.g. the "no savefile found" case hides the
    /// checkbox right after showing the message), so a postfix on ShowBoardingpassMessage
    /// alone would see stale visibility. Mirroring the real checkbox's own
    /// activeInHierarchy each frame sidesteps needing to replicate every one of those
    /// call sites correctly
    ///
    /// Parented directly under the checkpoint mod's own tiny checkbox's parent (its
    /// boarding-pass overlay Canvas, complete with ITS OWN CanvasScaler) rather than a
    /// separate raw ScreenSpaceOverlay canvas of our own: that scaler is what keeps the
    /// checkpoint mod's own message/checkbox a consistent size and position across
    /// resolutions, a plain unscaled canvas of ours drifted out of place against it.
    /// Anchored to the exact same anchor point the checkbox itself uses (copied off its
    /// own RectTransform, which is in turn copied from the message text's, see the
    /// checkpoint mod's own CreateBoardingPassCheckbox), then offset right + to that
    /// checkbox's own current height by <see cref="PluginConfig.IslandToggleOffsetX"/>/
    /// <see cref="PluginConfig.IslandToggleOffsetY"/> - tune those if it ever overlaps
    /// the message text (e.g. an unusually long campfire/level name)
    /// </summary>
    public class IslandToggleButton : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;
        private CheckpointInterop _checkpoint;
        private TMP_FontAsset _font;

        private GameObject _root;
        private RectTransform _panelRect;
        private Image _panelImage;
        private Image _trackImage;
        private RectTransform _knobRect;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _stateText;
        private HoverProbe _hoverProbe;

        private const float TrackWidth = 78f;
        private const float TrackHeight = 40f;
        private const float KnobDiameter = 34f;
        private const float KnobMargin = 3f;
        private const float KnobOffX = KnobMargin + KnobDiameter / 2f; // knob center when OFF
        private const float KnobOnX = TrackWidth - KnobMargin - KnobDiameter / 2f; // knob center when ON

        private static readonly Color TrackOffColor = new Color(0.38f, 0.38f, 0.42f, 0.95f);
        private static readonly Color TrackOnColor = new Color(0.22f, 0.62f, 0.36f, 0.95f);

        // #2742FC / #DDB92B, white foreground normally, black on hover (matching hover
        // background is light/gold, white text would disappear into it)
        private static readonly Color BackgroundNormalColor = new Color32(0x27, 0x42, 0xFC, 235);
        private static readonly Color BackgroundHoverColor = new Color32(0xDD, 0xB9, 0x2B, 245);
        private static readonly Color ForegroundNormalColor = Color.white;
        private static readonly Color ForegroundHoverColor = Color.black;

        private float _knobAnimX = KnobOffX;
        private Color _trackAnimColor = TrackOffColor;
        private Color _backgroundAnimColor = BackgroundNormalColor;
        private Color _foregroundAnimColor = ForegroundNormalColor;

        // Attached directly to the panel GameObject (not this component, which lives on
        // the persistent orchestrator GameObject) so the EventSystem's raycaster can
        // actually find it. Just a bool flag; IslandToggleButton.Update() reads it and
        // owns all the actual color animation, keeping that logic in one place alongside
        // the on/off knob-slide animation instead of splitting it across two components
        private class HoverProbe : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public bool Hovering;
            public void OnPointerEnter(PointerEventData eventData) => Hovering = true;
            public void OnPointerExit(PointerEventData eventData) => Hovering = false;
        }

        public void Init(ManualLogSource log, PluginConfig cfg, CheckpointInterop checkpoint)
        {
            _log = log;
            _cfg = cfg;
            _checkpoint = checkpoint;
        }

        private void Update()
        {
            if (_cfg == null || _checkpoint == null || !_cfg.ShowIslandToggleButton.Value)
            {
                SetVisible(false);
                return;
            }

            Toggle toggle;
            try { toggle = _checkpoint.TryGetBoardingToggle(); }
            catch { toggle = null; }

            if (toggle == null || !toggle.gameObject.activeInHierarchy)
            {
                SetVisible(false);
                return;
            }

            EnsureUi(toggle);
            SetVisible(true);
            Reposition(toggle);
            AnimateToward(toggle.isOn);
            RefreshLabels();
        }

        // Copies the real checkbox's own anchor point + current height every frame (it
        // can move, e.g. the checkpoint mod shifts it depending on whether a "PeakToBeach"
        // savefile flag is set), then applies our own configurable offset on top
        private void Reposition(Toggle toggle)
        {
            if (_panelRect == null) return;
            var toggleRect = toggle.GetComponent<RectTransform>();
            if (toggleRect == null) return;

            _panelRect.anchorMin = _panelRect.anchorMax = toggleRect.anchorMin;
            _panelRect.anchoredPosition = new Vector2(
                _cfg.IslandToggleOffsetX.Value,
                toggleRect.anchoredPosition.y + _cfg.IslandToggleOffsetY.Value);
        }

        private void SetVisible(bool visible)
        {
            if (_root == null) return;
            // Unity's EventSystem doesn't reliably fire OnPointerExit when a hovered
            // GameObject is deactivated (e.g. Escape closing the boarding pass while the
            // mouse never actually left the button) - so without this, reopening the
            // boarding pass could show the button stuck in its hovered yellow state even
            // though the mouse isn't over it. Reset immediately (not just the flag, the
            // already-animated colors too) whenever we hide, so it's never carried over
            if (!visible && _hoverProbe != null && _hoverProbe.Hovering)
            {
                _hoverProbe.Hovering = false;
                _backgroundAnimColor = BackgroundNormalColor;
                _foregroundAnimColor = ForegroundNormalColor;
                if (_panelImage != null) _panelImage.color = _backgroundAnimColor;
                if (_titleText != null) _titleText.color = _foregroundAnimColor;
                if (_stateText != null) _stateText.color = _foregroundAnimColor;
            }
            _root.SetActive(visible);
        }

        private void OnClicked()
        {
            try
            {
                Toggle toggle = _checkpoint?.TryGetBoardingToggle();
                if (toggle == null) return;
                // Setting isOn (not just flipping our own copy) fires the checkpoint
                // mod's own onValueChanged listener, exactly as if the tiny original
                // checkbox had been clicked directly
                toggle.isOn = !toggle.isOn;
            }
            catch (System.Exception e)
            {
                _log?.LogWarning($"IslandToggleButton.OnClicked failed (non-fatal): {e.Message}");
            }
        }

        private void AnimateToward(bool isOn)
        {
            float t = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);

            float targetKnobX = isOn ? KnobOnX : KnobOffX;
            Color targetTrackColor = isOn ? TrackOnColor : TrackOffColor;
            bool hovering = _hoverProbe != null && _hoverProbe.Hovering;
            Color targetBgColor = hovering ? BackgroundHoverColor : BackgroundNormalColor;
            Color targetFgColor = hovering ? ForegroundHoverColor : ForegroundNormalColor;

            _knobAnimX = Mathf.Lerp(_knobAnimX, targetKnobX, t);
            _trackAnimColor = Color.Lerp(_trackAnimColor, targetTrackColor, t);
            _backgroundAnimColor = Color.Lerp(_backgroundAnimColor, targetBgColor, t);
            _foregroundAnimColor = Color.Lerp(_foregroundAnimColor, targetFgColor, t);

            if (Mathf.Abs(_knobAnimX - targetKnobX) < 0.05f) _knobAnimX = targetKnobX;

            if (_knobRect != null) _knobRect.anchoredPosition = new Vector2(_knobAnimX, 0f);
            if (_trackImage != null) _trackImage.color = _trackAnimColor;
            if (_panelImage != null) _panelImage.color = _backgroundAnimColor;
            if (_titleText != null) _titleText.color = _foregroundAnimColor;
            if (_stateText != null) _stateText.color = _foregroundAnimColor;
        }

        private void RefreshLabels()
        {
            Toggle toggle = null;
            try { toggle = _checkpoint?.TryGetBoardingToggle(); } catch { /* ignore, keep last text */ }
            bool isOn = toggle != null && toggle.isOn;
            if (_stateText != null)
                _stateText.text = IslandToggleLocalization.Get(isOn ? IslandToggleKey.UsingSaved : IslandToggleKey.UsingNew);
        }

        private void EnsureUi(Toggle toggle)
        {
            if (_root != null) return;
            try
            {
                _font = SavePicker.FindGameFont();

                // Same parent the real checkbox itself lives under (the checkpoint
                // mod's own boarding-pass overlay Canvas + CanvasScaler), NOT a canvas
                // of our own - see the class doc comment for why
                Transform parent = toggle.transform.parent;

                var panelGo = new GameObject("PEAKQuickResume_IslandToggle", typeof(RectTransform));
                panelGo.transform.SetParent(parent, false);
                _root = panelGo;
                var panelRect = (RectTransform)panelGo.transform;
                _panelRect = panelRect;
                panelRect.pivot = new Vector2(0f, 0.5f);
                panelRect.sizeDelta = new Vector2(380f, 120f);

                // Baked plain white (not the actual brand color): Image.color is then the
                // ENTIRE final displayed color, not a multiplicative tint on top of a
                // fixed baked hue - required so hover can swap to a completely different
                // hue (blue -> gold) rather than just a brightness change, which is all a
                // multiplicative tint over a pre-colored texture could ever do
                _panelImage = panelGo.AddComponent<Image>();
                _panelImage.sprite = UiShapes.RoundedRect(380, 120, 18,
                    Color.white, new Color32(0, 0, 0, 255), 2f);
                _panelImage.color = BackgroundNormalColor;
                _panelImage.type = Image.Type.Simple;

                // The whole panel is the click target, not just the switch graphic -
                // the entire point of this button is a big, forgiving hitbox, the
                // opposite of the tiny original checkbox. Color transition is handled by
                // ourselves (HoverProbe + AnimateToward), not Selectable's own ColorTint,
                // since we need to drive several other elements (both texts) in sync too
                var button = panelGo.AddComponent<Button>();
                button.targetGraphic = _panelImage;
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(OnClicked);
                _hoverProbe = panelGo.AddComponent<HoverProbe>();

                _titleText = MakeText(panelGo.transform, "Title", 28, FontStyles.Normal,
                    ForegroundNormalColor, TextAlignmentOptions.Center);
                _titleText.text = IslandToggleLocalization.Get(IslandToggleKey.Title);
                var titleRect = (RectTransform)_titleText.transform;
                titleRect.anchorMin = new Vector2(0f, 1f);
                titleRect.anchorMax = new Vector2(1f, 1f);
                titleRect.pivot = new Vector2(0.5f, 1f);
                titleRect.sizeDelta = new Vector2(-16f, 34f);
                titleRect.anchoredPosition = new Vector2(0f, -12f);

                var trackGo = new GameObject("Track", typeof(RectTransform));
                trackGo.transform.SetParent(panelGo.transform, false);
                var trackRect = (RectTransform)trackGo.transform;
                trackRect.anchorMin = trackRect.anchorMax = new Vector2(0f, 0f);
                trackRect.pivot = new Vector2(0f, 0.5f);
                trackRect.sizeDelta = new Vector2(TrackWidth, TrackHeight);
                trackRect.anchoredPosition = new Vector2(18f, 34f);
                _trackImage = trackGo.AddComponent<Image>();
                _trackImage.sprite = UiShapes.RoundedRect((int)TrackWidth, (int)TrackHeight, TrackHeight / 2f,
                    Color.white, new Color32(140, 140, 140, 255), 2f);
                _trackImage.color = TrackOffColor;
                _trackImage.raycastTarget = false;

                var knobGo = new GameObject("Knob", typeof(RectTransform));
                knobGo.transform.SetParent(trackGo.transform, false);
                _knobRect = (RectTransform)knobGo.transform;
                _knobRect.anchorMin = _knobRect.anchorMax = new Vector2(0f, 0.5f);
                _knobRect.pivot = new Vector2(0.5f, 0.5f);
                _knobRect.sizeDelta = new Vector2(KnobDiameter, KnobDiameter);
                _knobRect.anchoredPosition = new Vector2(_knobAnimX, 0f);
                var knobImage = knobGo.AddComponent<Image>();
                knobImage.sprite = UiShapes.Circle((int)KnobDiameter, Color.white, new Color32(150, 150, 150, 255), 1.5f);
                knobImage.color = Color.white;
                knobImage.raycastTarget = false;

                _stateText = MakeText(panelGo.transform, "State", 34, FontStyles.Normal,
                    ForegroundNormalColor, TextAlignmentOptions.Left);
                var stateRect = (RectTransform)_stateText.transform;
                stateRect.anchorMin = stateRect.anchorMax = new Vector2(0f, 0f);
                stateRect.pivot = new Vector2(0f, 0.5f);
                stateRect.sizeDelta = new Vector2(256f, 40f);
                stateRect.anchoredPosition = new Vector2(18f + TrackWidth + 14f, 34f);

                SetVisible(false);
            }
            catch (System.Exception e)
            {
                _log?.LogError($"IslandToggleButton.EnsureUi failed (non-fatal): {e}");
            }
        }

        private TextMeshProUGUI MakeText(Transform parent, string name, int fontSize, FontStyles style,
            Color color, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) tmp.font = _font;
            tmp.fontSize = fontSize;
            tmp.fontStyle = style;
            tmp.color = color;
            tmp.alignment = alignment;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.raycastTarget = false;
            return tmp;
        }
    }
}
