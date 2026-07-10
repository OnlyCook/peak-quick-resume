using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PEAKQuickResume
{
    /// <summary>
    /// Our own top-of-screen transient message overlay. Every on-screen message this
    /// mod shows (resume status, errors, teleport-bug hints, save confirmations, etc.)
    /// goes through <see cref="Show"/>
    ///
    /// Deliberately simple: single text, no queue - a new call immediately replaces
    /// whatever's showing and resets its timer ("last call wins"). Several call sites
    /// rely on this, e.g. <see cref="TeleportWatchdog.ShowMessageResiliently"/>'s repeated
    /// re-shows to win a race against a later message
    ///
    /// Built from the same primitives as <see cref="SavePicker"/>/<see cref="HelpScreen"/>
    /// (own Canvas, <see cref="SavePicker.FindGameFont"/>), word-wrapped from the start
    /// </summary>
    public class OwnMessageOverlay : MonoBehaviour
    {
        private ManualLogSource _log;
        private TMP_FontAsset _font;

        private GameObject _root;
        private TextMeshProUGUI _text;
        private CanvasGroup _group;

        private float _hideAt = -1f;
        private float _fadeElapsed;
        private const float FadeDuration = 0.25f;
        private bool _fadingOut;

        public void Init(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>Shows (or replaces) the current message</summary>
        public void Show(string text, Color color, float duration = 4f)
        {
            try
            {
                EnsureUi();
                if (_text == null) return;

                _text.text = text;
                _text.color = color;
                _text.outlineColor = Darken(color);
                _hideAt = Time.unscaledTime + Mathf.Max(0.1f, duration);
                _fadingOut = false;
                _fadeElapsed = FadeDuration; // already fully visible if it was already showing
                if (_group != null) _group.alpha = 1f;
                _root.SetActive(true);
            }
            catch (System.Exception e)
            {
                _log?.LogWarning($"OwnMessageOverlay.Show failed (non-fatal): {e.Message}");
            }
        }

        private void Update()
        {
            if (_root == null || !_root.activeSelf || _hideAt < 0f) return;

            if (Time.unscaledTime >= _hideAt)
            {
                _fadingOut = true;
                _fadeElapsed -= Time.unscaledDeltaTime;
                if (_group != null) _group.alpha = Mathf.Clamp01(_fadeElapsed / FadeDuration);
                if (_fadeElapsed <= 0f)
                {
                    _root.SetActive(false);
                    _hideAt = -1f;
                    _fadingOut = false;
                }
            }
            else if (!_fadingOut && _fadeElapsed < FadeDuration)
            {
                // Fade-in on first show
                _fadeElapsed += Time.unscaledDeltaTime;
                if (_group != null) _group.alpha = Mathf.Clamp01(_fadeElapsed / FadeDuration);
            }
        }

        private void EnsureUi()
        {
            if (_root != null) return;

            _font = SavePicker.FindGameFont();

            _root = new GameObject("PEAKQuickResume_MessageOverlay", typeof(RectTransform));
            DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 31000; // above SavePicker/HelpScreen's own 30000

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _group = _root.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(_root.transform, false);
            _text = textGo.AddComponent<TextMeshProUGUI>();
            if (_font != null) _text.font = _font;
            _text.fontSize = 40f;
            _text.alignment = TextAlignmentOptions.Top;
            _text.textWrappingMode = TextWrappingModes.Normal;
            _text.color = Color.white;
            // Outline color is derived per-message from the text color (see Darken) to
            // match how the game's own UI does it throughout, rather than a fixed black
            _text.fontMaterial.EnableKeyword("OUTLINE_ON");
            _text.outlineWidth = 0.06f;
            _text.outlineColor = Darken(_text.color);

            var rect = (RectTransform)textGo.transform;
            rect.anchorMin = new Vector2(0.1f, 1f);
            rect.anchorMax = new Vector2(0.9f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -60f);
            rect.sizeDelta = new Vector2(0f, 200f);

            _root.SetActive(false);
        }

        /// <summary>
        /// Derives an outline color from a face color the same way the game's own text
        /// (e.g. player name labels) does throughout its UI: same hue/saturation, scaled-
        /// down brightness (HSV value), rather than a fixed black outline on every color
        /// </summary>
        private static Color Darken(Color c)
        {
            Color.RGBToHSV(c, out float h, out float s, out float v);
            Color outline = Color.HSVToRGB(h, s, v * 0.35f);
            outline.a = c.a;
            return outline;
        }
    }
}
