using System.Collections;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PEAKQuickResume
{
    /// <summary>
    /// The help screen: a real small menu built from the SAME visual primitives as
    /// the F7 save picker (<see cref="SavePicker"/>) - the rounded/bordered blue panel
    /// sprite (including its animated jagged-edge cycling), the game's own font, the
    /// gold key badges - rather than a plain screen-wide TMP text block. Opened/closed
    /// by the help-key listener in <see cref="Plugin"/>
    ///
    /// Deliberately simple compared to SavePicker: no row list/navigation, just a
    /// title, a word-wrapped body (auto-sized to its content height, so the panel
    /// grows vertically to fit rather than a fixed box), and a single "(F1) Close"
    /// footer entry. No bold anywhere (the game's own font has no real bold face, TMP
    /// faking it is what made the previous plain-text version unreadable), text color
    /// alone (matching the picker's own gold key / near-white title palette) does the
    /// section/emphasis work instead
    /// </summary>
    public class HelpScreen : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;

        public bool IsOpen { get; private set; }

        private GameObject _root;
        private Image _dimImage;
        private float _dimFadeElapsed;
        private const float DimFadeDuration = 0.25f;
        private Image _panelFillImage;
        private Image _grainImage;
        private RectTransform _panelRect;
        private RectTransform _bodyRect;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _bodyText;
        private TMP_FontAsset _font;

        // First-open loading indicator (same reasoning as SavePicker's): building this
        // panel (baking its rounded-rect sprite from scratch) takes ~300ms, heavy enough
        // to cause a visible hitch. Shown immediately, the real menu is built a frame
        // later; every subsequent open this session skips straight to the instant path
        private GameObject _loadingRoot;
        private bool _uiWarmedUp;
        private bool _warmingUp;

        // Jagged-edge animation, same cadence as SavePicker's own, but this screen's
        // own independent frame counter/timer (its panel is a different size, cached
        // separately, see SavePicker.PanelSprite)
        private int _jagFrame;
        private float _jagFrameTimer;
        private int _lastWidth, _lastHeight;

        private const float BodyFontSize = 20f;
        private const float TitleFontSize = 27f;
        private const float FooterGap = 14f;
        private const float TitleBodyGap = 10f;

        public void Init(ManualLogSource log, PluginConfig cfg)
        {
            _log = log;
            _cfg = cfg;
        }

        public void Open()
        {
            IsOpen = true;

            if (!_uiWarmedUp)
            {
                EnsureLoadingUi();
                _loadingRoot?.SetActive(true);
                if (!_warmingUp)
                {
                    _warmingUp = true;
                    StartCoroutine(WarmUpThenShow());
                }
            }
            else
            {
                ShowReal(skipDimFade: false);
            }
        }

        public void Close()
        {
            _root?.SetActive(false);
            _loadingRoot?.SetActive(false);
            IsOpen = false;
        }

        private IEnumerator WarmUpThenShow()
        {
            yield return null;
            // skipDimFade: the loading indicator's own dim is already at full DimColor
            // (see EnsureLoadingUi), deactivating it and activating the real root happen
            // in this same frame (no yield between them), so the real root's dim starts
            // already-opaque too, same color, same alpha, nothing to visually distinguish
            // the swap. Fading it in from 0 here (like a normal open) would instead cause
            // the dim to flash away to nothing for a frame and then re-fade in, since
            // deactivating the loading root removes the ONLY dim currently showing
            ShowReal(skipDimFade: true);
            _loadingRoot?.SetActive(false);
            _uiWarmedUp = true;
            _warmingUp = false;
        }

        private void ShowReal(bool skipDimFade)
        {
            EnsureUi();
            RebuildContent();
            _root?.SetActive(true);

            if (skipDimFade)
            {
                if (_dimImage != null) _dimImage.color = SavePicker.DimColor;
                _dimFadeElapsed = DimFadeDuration;
            }
            else
            {
                // Dim fades in from fully transparent each time the screen opens (rather
                // than snapping straight to DimColor), same treatment as the F7 picker
                if (_dimImage != null)
                    _dimImage.color = new Color(SavePicker.DimColor.r, SavePicker.DimColor.g, SavePicker.DimColor.b, 0f);
                _dimFadeElapsed = 0f;
            }
        }

        private void Update()
        {
            if (!IsOpen || _root == null || !_root.activeSelf) return;

            // Closable with Escape too, not just the tutorial key it was opened with -
            // deliberately: a player who's used to "same key closes it" would otherwise
            // try F7 to close the F7 picker, which does something else entirely there
            // (loads the highlighted save). Same Escape-suppression trick the F7 picker
            // already uses, so this doesn't also pop the vanilla pause menu open
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
                PauseSuppressPatch.SuppressNextOpen();
                return;
            }

            if (_dimImage != null && _dimFadeElapsed < DimFadeDuration)
            {
                _dimFadeElapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(_dimFadeElapsed / DimFadeDuration);
                _dimImage.color = new Color(SavePicker.DimColor.r, SavePicker.DimColor.g, SavePicker.DimColor.b, SavePicker.DimColor.a * t);
            }

            // Skipped entirely in minimal mode (see PluginConfig.MinimalPickerUi): the
            // flat sprite baked for that mode is identical every frame, nothing to animate
            if (MinimalUi) return;

            _jagFrameTimer += Time.unscaledDeltaTime;
            if (_jagFrameTimer >= SavePicker.JagFrameInterval)
            {
                _jagFrameTimer -= SavePicker.JagFrameInterval;
                _jagFrame = (_jagFrame + 1) % SavePicker.JagFrameCount;
                if (_panelFillImage != null)
                    _panelFillImage.sprite = SavePicker.PanelSprite(_lastWidth, _lastHeight, _jagFrame, false);
            }
        }

        private bool MinimalUi => _cfg != null && _cfg.MinimalPickerUi.Value;

        private void EnsureLoadingUi()
        {
            if (_loadingRoot != null) return;
            try
            {
                if (_font == null) _font = SavePicker.FindGameFont();

                _loadingRoot = new GameObject("PEAKQuickResume_HelpScreen_Loading", typeof(RectTransform));
                _loadingRoot.transform.SetParent(transform, false);
                var canvas = _loadingRoot.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 30001;
                SavePicker.ApplyWidescreenScaler(canvas);

                var dimGo = new GameObject("Dim", typeof(RectTransform));
                dimGo.transform.SetParent(_loadingRoot.transform, false);
                var dim = dimGo.AddComponent<Image>();
                dim.color = SavePicker.DimColor;
                SavePicker.StretchFull((RectTransform)dimGo.transform);

                var text = MakeText("LoadingText", TitleFontSize, TextAlignmentOptions.Center, SavePicker.TitleColor);
                text.transform.SetParent(_loadingRoot.transform, false);
                SavePicker.ApplyChromeTextStyle(text);
                text.text = SavePickerLocalization.Get(PickerText.Loading);
                SavePicker.StretchFull((RectTransform)text.transform);

                _loadingRoot.SetActive(false);
            }
            catch (System.Exception e)
            {
                _log?.LogError($"HelpScreen.EnsureLoadingUi failed (non-fatal): {e}");
            }
        }

        private void EnsureUi()
        {
            if (_root != null) return;
            try
            {
                _font = SavePicker.FindGameFont();

                _root = new GameObject("PEAKQuickResume_HelpScreen", typeof(RectTransform));
                _root.transform.SetParent(transform, false);
                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 30001; // above the F7 picker (30000)
                SavePicker.ApplyWidescreenScaler(canvas);

                var dimGo = new GameObject("Dim", typeof(RectTransform));
                dimGo.transform.SetParent(_root.transform, false);
                _dimImage = dimGo.AddComponent<Image>();
                _dimImage.color = new Color(SavePicker.DimColor.r, SavePicker.DimColor.g, SavePicker.DimColor.b, 0f);
                SavePicker.StretchFull((RectTransform)dimGo.transform);

                var panelGo = new GameObject("Panel", typeof(RectTransform));
                panelGo.transform.SetParent(_root.transform, false);
                _panelFillImage = panelGo.AddComponent<Image>();
                _panelFillImage.type = Image.Type.Simple;
                _panelFillImage.color = Color.white; // sprite bakes its own colors, see SavePicker.PanelSprite
                _panelRect = (RectTransform)panelGo.transform;
                _panelRect.anchorMin = _panelRect.anchorMax = new Vector2(0.5f, 0.5f);
                _panelRect.pivot = new Vector2(0.5f, 0.5f);

                // Same fractal-noise grain overlay as the F7 picker's panel, masked to
                // just the fill area (inset by the border thickness) so it never draws
                // over the border ring, see SavePicker's own EnsureUi for the full
                // reasoning (this is the exact same construction, reusing its sprites)
                var maskGo = new GameObject("GrainMask", typeof(RectTransform));
                maskGo.transform.SetParent(panelGo.transform, false);
                var maskImage = maskGo.AddComponent<Image>();
                maskImage.sprite = SavePicker.PanelInnerMaskSprite();
                maskImage.type = Image.Type.Sliced;
                maskImage.color = Color.white;
                var mask = maskGo.AddComponent<Mask>();
                mask.showMaskGraphic = false;
                var maskRect = (RectTransform)maskGo.transform;
                maskRect.anchorMin = Vector2.zero;
                maskRect.anchorMax = Vector2.one;
                maskRect.offsetMin = new Vector2(SavePicker.PanelBorderThickness, SavePicker.PanelBorderThickness);
                maskRect.offsetMax = new Vector2(-SavePicker.PanelBorderThickness, -SavePicker.PanelBorderThickness);

                var grainGo = new GameObject("Grain", typeof(RectTransform));
                grainGo.transform.SetParent(maskGo.transform, false);
                var grain = grainGo.AddComponent<Image>();
                grain.sprite = Sprite.Create(SavePicker.PanelGrainTexture(),
                    new Rect(0, 0, SavePicker.GrainTextureSize, SavePicker.GrainTextureSize), new Vector2(0.5f, 0.5f), 100f);
                grain.type = Image.Type.Simple;
                grain.color = Color.white; // grain shade is baked into the texture itself (alpha applied in RebuildContent)
                grain.raycastTarget = false;
                SavePicker.StretchFull((RectTransform)grainGo.transform);
                _grainImage = grain;

                _titleText = MakeText("Title", TitleFontSize, TextAlignmentOptions.Top, SavePicker.TitleColor);
                SavePicker.ApplyChromeTextStyle(_titleText);
                _titleText.textWrappingMode = TextWrappingModes.Normal;
                var titleRect = (RectTransform)_titleText.transform;
                titleRect.SetParent(panelGo.transform, false);
                titleRect.anchorMin = new Vector2(0f, 1f);
                titleRect.anchorMax = new Vector2(1f, 1f);
                titleRect.pivot = new Vector2(0.5f, 1f);
                titleRect.offsetMin = new Vector2(SavePicker.PanelPaddingHorizontal, titleRect.offsetMin.y);
                titleRect.offsetMax = new Vector2(-SavePicker.PanelPaddingHorizontal, titleRect.offsetMax.y);
                titleRect.anchoredPosition = new Vector2(0f, -SavePicker.PanelPadding);

                _bodyText = MakeText("Body", BodyFontSize, TextAlignmentOptions.TopLeft, SavePicker.FooterColor);
                _bodyText.textWrappingMode = TextWrappingModes.Normal;
                _bodyRect = (RectTransform)_bodyText.transform;
                _bodyRect.SetParent(panelGo.transform, false);
                _bodyRect.anchorMin = new Vector2(0f, 1f);
                _bodyRect.anchorMax = new Vector2(1f, 1f);
                _bodyRect.pivot = new Vector2(0.5f, 1f);
                _bodyRect.offsetMin = new Vector2(SavePicker.PanelPaddingHorizontal, _bodyRect.offsetMin.y);
                _bodyRect.offsetMax = new Vector2(-SavePicker.PanelPaddingHorizontal, _bodyRect.offsetMax.y);
                var bodyFitter = _bodyText.gameObject.AddComponent<ContentSizeFitter>();
                bodyFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                bodyFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                BuildFooter(panelGo.transform);

                _root.SetActive(false);
            }
            catch (System.Exception e)
            {
                _log?.LogError($"HelpScreen.EnsureUi failed (non-fatal, F1 screen will not render): {e}");
            }
        }

        private TextMeshProUGUI _footerKeyText;
        private TextMeshProUGUI _footerLabelText;
        private RectTransform _footerRow;

        private void BuildFooter(Transform parent)
        {
            var rowGo = new GameObject("Footer", typeof(RectTransform));
            rowGo.transform.SetParent(parent, false);
            _footerRow = (RectTransform)rowGo.transform;
            _footerRow.anchorMin = new Vector2(0.5f, 0f);
            _footerRow.anchorMax = new Vector2(0.5f, 0f);
            _footerRow.pivot = new Vector2(0.5f, 0f);
            _footerRow.sizeDelta = new Vector2(200f, SavePicker.FooterHeight);
            _footerRow.anchoredPosition = new Vector2(0f, SavePicker.PanelPadding);

            var rowLayout = rowGo.AddComponent<HorizontalLayoutGroup>();
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.spacing = 8f;
            rowLayout.childControlWidth = false;
            rowLayout.childControlHeight = false;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = false;
            var rowFitter = rowGo.AddComponent<ContentSizeFitter>();
            rowFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            rowFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var badgeGo = new GameObject("Badge", typeof(RectTransform));
            badgeGo.transform.SetParent(rowGo.transform, false);
            var badgeImage = badgeGo.AddComponent<Image>();
            badgeImage.sprite = SavePicker.BadgeSprite();
            badgeImage.type = Image.Type.Sliced;
            badgeImage.color = Color.white;
            var badgeLayout = badgeGo.AddComponent<HorizontalLayoutGroup>();
            badgeLayout.childAlignment = TextAnchor.MiddleCenter;
            badgeLayout.padding = new RectOffset(10, 10, 4, 4);
            badgeLayout.childControlWidth = true;
            badgeLayout.childControlHeight = true;
            var badgeFitter = badgeGo.AddComponent<ContentSizeFitter>();
            badgeFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            badgeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _footerKeyText = MakeText("Key", 16f, TextAlignmentOptions.Midline, SavePicker.KeyTextColor);
            _footerKeyText.transform.SetParent(badgeGo.transform, false);

            _footerLabelText = MakeText("Label", 17f, TextAlignmentOptions.Midline, SavePicker.FooterColor);
            _footerLabelText.transform.SetParent(rowGo.transform, false);
            SavePicker.ApplyChromeTextStyle(_footerLabelText);
            var labelFitter = _footerLabelText.gameObject.AddComponent<ContentSizeFitter>();
            labelFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            labelFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private TextMeshProUGUI MakeText(string name, float fontSize, TextAlignmentOptions alignment, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) tmp.font = _font;
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Normal; // never bold: this font has no real bold face, TMP faking it is unreadable
            tmp.color = color;
            tmp.richText = true;
            tmp.alignment = alignment;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            return tmp;
        }

        // Fills in the title/body/footer key text and re-sizes the panel to fit the
        // body's actual wrapped height, called every time the screen opens so it
        // always reflects live key bindings / config
        private void RebuildContent()
        {
            if (_root == null) return;
            try
            {
                string tutorialKey = _cfg?.HelpKey.Value.ToString() ?? "F4";

                _titleText.text = $"Quick Resume {HelpScreenLocalization.Get(HelpText.HelpTitleWord)}";
                _bodyText.text = HelpScreenContent.Build(_cfg);
                _footerKeyText.text = $"{tutorialKey} / Esc";
                _footerLabelText.text = HelpScreenLocalization.Get(HelpText.Close);

                // Same native-widescreen scaler as the F7 picker (see SavePicker.
                // ApplyWidescreenScaler): the canvas width scales with aspect ratio
                // rather than 1:1 with Screen.width
                float w = Mathf.Min(SavePicker.PanelWidth, SavePicker.CanvasWidthUnits - 80f) + 2f * SavePicker.PanelOuterMargin;

                // Force a layout pass at the final width so the body's ContentSizeFitter
                // reports the correct wrapped height for THIS width, not a stale one
                _panelRect.sizeDelta = new Vector2(w, 200f);
                _root.SetActive(true);
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(_bodyRect);

                float bodyHeight = _bodyText.preferredHeight;
                float chrome = SavePicker.PanelPadding * 2f + SavePicker.TitleHeight + TitleBodyGap
                    + FooterGap + SavePicker.FooterHeight;
                float h = Mathf.Min(chrome + bodyHeight, SavePicker.ReferenceHeight - 80f) + 2f * SavePicker.PanelOuterMargin;

                bool minimalUi = MinimalUi;
                _panelRect.sizeDelta = new Vector2(w, h);
                _lastWidth = Mathf.RoundToInt(w);
                _lastHeight = Mathf.RoundToInt(h);
                _panelFillImage.sprite = SavePicker.PanelSprite(_lastWidth, _lastHeight, _jagFrame, minimalUi);

                // Same PluginConfig.PanelOpacity the F7 picker's own panel respects, read
                // fresh every rebuild so a change while the screen happens to be open
                // takes effect immediately. The grain overlay is faded the same amount
                // (it's baked fully opaque, see SavePicker.PanelGrainTexture) or it would
                // keep hiding whatever the fill's own transparency reveals. In minimal
                // mode (see PluginConfig.MinimalPickerUi) the grain overlay is hidden
                // entirely, leaving a plain flat-colored panel, same as the F7 picker
                float panelOpacity = _cfg != null ? Mathf.Clamp01(_cfg.PanelOpacity.Value) : 1f;
                _panelFillImage.color = new Color(1f, 1f, 1f, panelOpacity);
                if (_grainImage != null)
                {
                    _grainImage.gameObject.SetActive(!minimalUi);
                    _grainImage.color = new Color(1f, 1f, 1f, panelOpacity);
                }

                var titleRect = (RectTransform)_titleText.transform;
                titleRect.offsetMin = new Vector2(SavePicker.PanelPaddingHorizontal, titleRect.offsetMin.y);
                titleRect.sizeDelta = new Vector2(titleRect.sizeDelta.x, SavePicker.TitleHeight);

                _bodyRect.anchoredPosition = new Vector2(0f, -(SavePicker.PanelPadding + SavePicker.TitleHeight + TitleBodyGap));

                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(_panelRect);
                LayoutRebuilder.ForceRebuildLayoutImmediate(_footerRow);
            }
            catch (System.Exception e)
            {
                _log?.LogWarning($"HelpScreen.RebuildContent failed (non-fatal): {e.Message}");
            }
        }
    }
}
