using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PEAKQuickResume
{
    /// <summary>
    /// The in-game F7 save picker: an overlay listing every archived checkpoint for the
    /// CURRENT network category (offline saves when solo, coop saves when in coop), newest
    /// first. Arrow keys move the highlight (holding one repeats it), Delete removes a save
    /// (two-step), Escape closes. The resume key itself (open / confirm-load) is driven by
    /// <see cref="Plugin"/> so a single key press never both opens and confirms
    ///
    /// The newest save is preselected, so "press F7, press F7 again" still loads the
    /// latest checkpoint exactly like before the picker existed (unless
    /// <see cref="PluginConfig.ResumeKeyAlsoConfirmsLoad"/> is disabled, in which case only
    /// Enter confirms)
    ///
    /// Rendered as a real UGUI Canvas (built once, lazily, then just toggled/updated)
    /// rather than IMGUI, both for a look that sits closer to the game's own menus (real
    /// TMP font pulled from the game's own loaded fonts, same trick the checkpoint mod
    /// uses for its loading screen) and because a Canvas that's only touched on state
    /// changes is cheaper than IMGUI's every-frame relayout while open
    ///
    /// The row list only ever shows <see cref="MaxVisibleRows"/> rows at once and scrolls
    /// to keep the selection in view, rather than growing the panel to fit an arbitrarily
    /// large archive
    /// </summary>
    public class SavePicker : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;

        private List<ArchivedSave> _entries = new List<ArchivedSave>();
        private int _selected;
        private int _scrollOffset;
        private bool _offline;

        // Two-step delete guard: first Delete arms, second within the window confirms
        private int _pendingDeleteIndex = -1;
        private float _pendingDeleteDeadline;

        // A one-off transient warning (e.g. "unstar to delete"), shown in the same
        // warn-line slot as the delete-confirm prompt above. The two never compete for
        // long: OnDeletePressed only ever sets one of them per press, and either one
        // being freshly set replaces whatever the other was showing
        private string _transientWarnText;
        private float _transientWarnDeadline;

        // Arrow-key hold-to-repeat
        private float _nextRepeatTime;
        private const float RepeatInitialDelay = 0.35f;
        private const float RepeatInterval = 0.08f;
        private const int JumpStep = 5;

        public bool IsOpen { get; private set; }

        public ArchivedSave Selected =>
            (IsOpen && _selected >= 0 && _selected < _entries.Count) ? _entries[_selected] : null;

        public void Init(ManualLogSource log, PluginConfig cfg)
        {
            _log = log;
            _cfg = cfg;
        }

        /// <summary>
        /// Open the picker for the given category. <paramref name="preferred"/>, if set,
        /// selects the newest save of that difficulty (used mid-run so the default matches
        /// the run you're in); otherwise the newest save overall is selected
        /// Returns false (and does not open) when there are no saves for this category
        /// </summary>
        public bool Open(bool offline, SaveTarget? preferred)
        {
            _offline = offline;
            _entries = SaveArchive.List(offline, _log);
            if (_entries.Count == 0)
            {
                _log?.LogInfo($"[picker] No {(offline ? "offline" : "coop")} saves to show.");
                return false;
            }

            _selected = 0;
            if (preferred.HasValue)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].Target.IsCustom == preferred.Value.IsCustom
                        && (preferred.Value.IsCustom || _entries[i].Target.Ascent == preferred.Value.Ascent))
                    {
                        _selected = i;
                        break;
                    }
                }
            }

            _scrollOffset = 0;
            ClearPendingDelete();
            IsOpen = true;

            // The very first time the picker is ever opened in a session, building the
            // real menu (baking every procedural sprite/texture from scratch) is heavy
            // enough to cause a visible hitch. Rather than the player pressing the key
            // and staring at nothing for a beat, show a cheap "Loading..." indicator
            // immediately and build the real menu a frame later; every subsequent open
            // this session skips straight to the instant path below
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
                ShowRealMenu(skipDimFade: false);
            }

            _log?.LogInfo($"[picker] Opened with {_entries.Count} {(offline ? "offline" : "coop")} save(s); selected #{_selected}.");
            return true;
        }

        // Builds the real menu (heavy, first time) and swaps the loading indicator out
        // for it. Delayed by one frame so the loading text actually gets a chance to
        // render before the heavy synchronous build work runs
        private IEnumerator WarmUpThenShow()
        {
            yield return null;
            // skipDimFade: the loading indicator's own dim is already at full DimColor,
            // deactivating it and activating the real root happen in this same frame
            // (no yield between them), so the real root's dim starts already-opaque
            // too, same color, same alpha, nothing to visually distinguish the swap.
            // Fading it in from 0 here (like a normal open) would instead cause the
            // dim to flash away to nothing for a frame and then re-fade in, since
            // deactivating the loading root removes the ONLY dim currently showing
            ShowRealMenu(skipDimFade: true);
            _loadingRoot?.SetActive(false);
            _uiWarmedUp = true;
            _warmingUp = false;
        }

        private void ShowRealMenu(bool skipDimFade)
        {
            EnsureUi();
            ScrollToSelection();
            // Activate BEFORE rebuilding: the footer badges size themselves via
            // ContentSizeFitter + a forced layout rebuild, and Unity can't correctly
            // measure TMP text (or run layout at all) on a still-inactive hierarchy, the
            // first-open badges would be measured wrong and only "snap" correct on the
            // next rebuild (e.g. the first arrow-key press)
            _root?.SetActive(true);
            RebuildUi();
            if (skipDimFade)
            {
                if (_dimImage != null) _dimImage.color = DimColor;
                _dimFadeElapsed = DimFadeDuration;
                return;
            }
            // Dim fades in from fully transparent each time the picker opens (rather
            // than snapping straight to DimColor), easier on the eyes than an instant
            // dark overlay slamming in
            if (_dimImage != null) _dimImage.color = new Color(DimColor.r, DimColor.g, DimColor.b, 0f);
            _dimFadeElapsed = 0f;
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            ClearPendingDelete();
            _root?.SetActive(false);
            _loadingRoot?.SetActive(false);
            _log?.LogInfo("[picker] Closed.");
        }

        private void Update()
        {
            if (!IsOpen) return;

            if (_dimImage != null && _dimFadeElapsed < DimFadeDuration)
            {
                _dimFadeElapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(_dimFadeElapsed / DimFadeDuration);
                _dimImage.color = new Color(DimColor.r, DimColor.g, DimColor.b, DimColor.a * t);
            }

            // Navigation (the resume key + Enter confirm live in Plugin). Holding an
            // arrow repeats it after an initial delay, like any normal menu list. Holding
            // Shift jumps by JumpStep entries instead of 1, both on the initial press and
            // on every repeat while held
            int step = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? JumpStep : 1;
            if (Input.GetKeyDown(KeyCode.UpArrow)) { Move(-step); _nextRepeatTime = Time.unscaledTime + RepeatInitialDelay; }
            else if (Input.GetKeyDown(KeyCode.DownArrow)) { Move(step); _nextRepeatTime = Time.unscaledTime + RepeatInitialDelay; }
            else if (Input.GetKey(KeyCode.UpArrow) && Time.unscaledTime >= _nextRepeatTime)
            { Move(-step); _nextRepeatTime = Time.unscaledTime + RepeatInterval; }
            else if (Input.GetKey(KeyCode.DownArrow) && Time.unscaledTime >= _nextRepeatTime)
            { Move(step); _nextRepeatTime = Time.unscaledTime + RepeatInterval; }
            else if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
                // See PauseSuppressPatch: stops the SAME Escape press from also opening
                // the vanilla pause menu right behind us
                PauseSuppressPatch.SuppressNextOpen();
            }
            else if (Input.GetKeyDown(KeyCode.Delete)) OnDeletePressed();
            else if (_cfg != null && Input.GetKeyDown(_cfg.StarKey.Value)) OnStarPressed();

            if (_pendingDeleteIndex >= 0 && Time.unscaledTime > _pendingDeleteDeadline)
            {
                ClearPendingDelete();
                RefreshWarn();
            }
            if (_transientWarnText != null && Time.unscaledTime > _transientWarnDeadline)
            {
                _transientWarnText = null;
                RefreshWarn();
            }

            // Jagged-edge animation: cycle through the 3 pre-built frames on a fixed
            // interval. Every frame is already a real, complete Sprite generated once
            // and cached (see PanelSprite/RowCapSelSprite), so ticking this is just
            // swapping which already-built texture two Images point at, not any kind
            // of per-frame regeneration. Skipped entirely in minimal mode (see
            // PluginConfig.MinimalPickerUi): the flat sprites baked for that mode are
            // identical every frame, so there is nothing to animate
            if (!MinimalUi)
            {
                _jagFrameTimer += Time.unscaledDeltaTime;
                if (_jagFrameTimer >= JagFrameInterval)
                {
                    _jagFrameTimer -= JagFrameInterval;
                    _jagFrame = (_jagFrame + 1) % JagFrameCount;
                    ApplyJagFrame();
                }
            }
        }

        private bool MinimalUi => _cfg != null && _cfg.MinimalPickerUi.Value;

        private void ApplyJagFrame()
        {
            if (_panelFillImage != null && _panelRect != null)
            {
                var size = _panelRect.sizeDelta;
                _panelFillImage.sprite = PanelSprite(Mathf.RoundToInt(size.x), Mathf.RoundToInt(size.y), _jagFrame, MinimalUi);
            }
            ApplySelOverlaySprite(MinimalUi);
        }

        private void Move(int delta)
        {
            ClearPendingDelete();
            if (_entries.Count == 0) return;
            _selected = Mathf.Clamp(_selected + delta, 0, _entries.Count - 1);
            ScrollToSelection();
            RebuildUi();
        }

        // Slides the visible window just enough to keep the selection in view (like any
        // normal scrolling list), rather than growing the panel to fit every entry
        private void ScrollToSelection()
        {
            if (_selected < _scrollOffset) _scrollOffset = _selected;
            else if (_selected >= _scrollOffset + MaxVisibleRows) _scrollOffset = _selected - MaxVisibleRows + 1;

            int maxOffset = Mathf.Max(0, _entries.Count - MaxVisibleRows);
            _scrollOffset = Mathf.Clamp(_scrollOffset, 0, maxOffset);
        }

        private void OnDeletePressed()
        {
            var target = Selected;
            if (target == null) return;

            // Starred saves can't be deleted at all, must be unstarred first (see
            // OnStarPressed) - shown as a transient warning instead of arming the
            // normal two-step delete confirm, which would otherwise silently no-op on
            // the second press (SaveArchive.Delete refuses starred saves defensively)
            if (target.Starred)
            {
                ClearPendingDelete();
                _transientWarnText = SavePickerLocalization.Get(PickerText.CannotDeleteStarred);
                _transientWarnDeadline = Time.unscaledTime + 3f;
                RefreshWarn();
                return;
            }

            if (_pendingDeleteIndex == _selected && Time.unscaledTime <= _pendingDeleteDeadline)
            {
                SaveArchive.Delete(target, _log);
                _entries.RemoveAt(_selected);
                ClearPendingDelete();
                if (_entries.Count == 0) { Close(); return; }
                _selected = Mathf.Clamp(_selected, 0, _entries.Count - 1);
                ScrollToSelection();
                RebuildUi();
            }
            else
            {
                _pendingDeleteIndex = _selected;
                _pendingDeleteDeadline = Time.unscaledTime + 3f;
                RefreshWarn();
            }
        }

        // Toggles the highlighted save's starred state (persisted immediately, see
        // SaveArchive.SetStarred) and re-sorts in place: starred saves float to the top
        // (newest first), same ordering SaveArchive.List() itself produces. Re-sorting
        // the already-loaded list rather than re-calling List() avoids a redundant
        // disk sync + re-parse of every archive file just to reflect one toggle
        private void OnStarPressed()
        {
            var target = Selected;
            if (target == null) return;

            ClearPendingDelete();
            SaveArchive.SetStarred(target, !target.Starred, _log);
            _entries.Sort(SaveArchive.CompareForDisplay);
            _selected = _entries.IndexOf(target);
            ScrollToSelection();
            RebuildUi();
        }

        private void ClearPendingDelete()
        {
            _pendingDeleteIndex = -1;
            _pendingDeleteDeadline = 0f;
            _transientWarnText = null;
        }

        private static string FormatPlaytime(float seconds)
        {
            if (seconds <= 0f) return "";
            var t = TimeSpan.FromSeconds(seconds);
            string played = SavePickerLocalization.Get(PickerText.Played);
            // Past 10h, drop the minutes: this is the last column, so its own width
            // factors into the row-overflow budget same as everything else, and an
            // unbounded "{hours}h {minutes}m" would grow without limit on a long-lived
            // save (unlike the difficulty column, there's no truncating an hour count).
            // Hours-only past this point is a one-character-wider-per-decade growth
            // instead, which the packed-column fallback (see ComputeColumnLayout) can
            // always absorb
            if (t.TotalHours >= 10) return $"{(int)t.TotalHours}h {played}";
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m {played}" : $"{t.Minutes}m {played}";
        }

        // --- UGUI rendering ---

        private const int MaxVisibleRows = 10;
        private const float RowHeight = 40f;
        // internal: reused by HelpScreen (the F1 screen) so it matches this panel's
        // proportions exactly rather than approximating them
        internal const float PanelPadding = 20f; // vertical margin (title/footer/etc.)
        // Wider than PanelPadding: with the border grown to 11px thick (see
        // PanelBorderThickness), the old horizontal margin left only ~1px between the
        // selected row's edge and the border, they read as touching. Only the
        // horizontal margin needs this, vertical spacing is untouched
        internal const float PanelPaddingHorizontal = 30f;
        internal const float TitleHeight = 42f;
        private const float ScrollHintHeight = 18f;
        private const float ScrollHintGap = 4f;
        private const float WarnHeight = 24f;
        internal const float FooterHeight = 34f;
        internal const float PanelWidth = 900f;

        // Row "column" layout: fields (difficulty / biome / date / playtime) are lined
        // up at fixed x-positions computed from the widest value each field takes across
        // every archived save, not just the visible page, so the alignment doesn't jitter
        // while scrolling. RowTextInset matches the 10f margin the row text used to have
        // on each side; RowColumnGap is the horizontal gap between adjacent columns. If
        // the 4-column layout doesn't fit the available row width (long biome/campfire
        // names, verbose languages, ...) the two middle fields (biome, date) collapse
        // into a single packed field instead of being clipped - see ComputeColumnLayout
        private const float RowTextInset = 10f;
        private const float RowColumnGap = 24f;
        private const string RowPackedMidSeparator = "   ";
        // Reserved on the right of every row (starred or not) so the last column never
        // sits where the star icon would be - keeps the right edge consistent instead of
        // the text shifting over only when a row happens to be starred. RowStarIconSize
        // is declared further down (with the star icon itself); const-to-const forward
        // references are resolved at compile time so the declaration order doesn't matter
        private const float RowStarReserve = RowStarIconSize + 10f;

        // Native widescreen support (same technique as the compass mod's PreviewMenu):
        // the canvas is scaled so it always measures exactly this many units tall
        // regardless of the monitor's actual resolution or aspect ratio (see the
        // CanvasScaler setup in EnsureUi/EnsureLoadingUi, matchWidthOrHeight = 1f).
        // Only the AVAILABLE WIDTH changes with aspect - wider (21:9, 32:9) monitors
        // just get more of it, narrower ones less - so panel height math against a
        // raw Screen.height never applies here, only width needs the live aspect ratio
        internal const float ReferenceHeight = 1080f;
        internal static float CanvasWidthUnits => (float)Screen.width / Screen.height * ReferenceHeight;

        // Palette pulled from the game's own UI (boarding pass / map rotation panels):
        // a vivid blue panel with a heavy near-black outline, rather than a dark navy
        // debug-overlay look. internal: HelpScreen (the F1 screen) reuses this exact
        // palette so it reads as the same menu system, not a different-looking one
        internal static readonly Color DimColor = new Color(0f, 0f, 0f, 0.78f);
        internal static readonly Color PanelFillColor = new Color(0x34 / 255f, 0x54 / 255f, 0xD1 / 255f); // #3454D1
        internal static readonly Color PanelBorderColor = new Color(0x21 / 255f, 0x31 / 255f, 0x7E / 255f); // #21317E
        // Everything else that was using the panel's border color (the key badges)
        // keeps the ORIGINAL shade, only the main panel's own outline changes
        internal static readonly Color BadgeBorderColor = new Color(0x0A / 255f, 0x0D / 255f, 0x1A / 255f); // #0A0D1A
        internal static readonly Color TitleColor = new Color(0.98f, 0.99f, 1f);
        private static readonly Color RowColor = new Color(0.93f, 0.95f, 1f);
        // Every other row gets a subtle darkening tint over the panel blue (zebra
        // striping, like the game's own Map Rotation table)
        private static readonly Color RowStripeColor = new Color(0f, 0f, 0f, 0.14f);
        // The selected row is a solid bar (not a translucent tint) with dark text, the
        // same "bright highlight, dark text" contrast the game's own menus use rather
        // than just recoloring the row text
        private static readonly Color RowSelBarColor = new Color(1f, 0.82f, 0.22f, 0.97f);
        private static readonly Color RowSelTextColor = new Color(0.16f, 0.12f, 0.03f);
        internal static readonly Color FooterColor = new Color(0.85f, 0.9f, 1f);
        private static readonly Color WarnColor = new Color(1f, 0.6f, 0.55f);
        private static readonly Color ScrollHintColor = new Color(0.8f, 0.87f, 1f);
        // Chips sit a shade darker than the panel so they read as distinct controls,
        // with the same near-black border style as the panel itself
        internal static readonly Color KeyChipFillColor = new Color(0.10f, 0.16f, 0.44f);
        internal static readonly Color KeyTextColor = new Color(1f, 0.95f, 0.72f);
        // A richer amber than the selected-row bar's own pale gold (RowSelBarColor), so
        // a starred AND selected row's icon still reads distinctly against that bar,
        // helped along by the same heavy dark outline the badges use
        private static readonly Color StarFillColor = new Color(0.97f, 0.62f, 0.10f);

        // One badge per footer hint: a small rounded-rect background (real UGUI Image,
        // not a TMP <mark> tag, which can't do rounded corners and baseline-centers its
        // background oddly) behind a centered key label, followed by a plain-text label
        private class FooterEntry
        {
            public TextMeshProUGUI KeyText;
            public TextMeshProUGUI LabelText;
        }

        internal const float PanelCornerRadius = 26f;
        // Was 7f; the extra thickness grows OUTWARD (see PanelOuterMargin, added to the
        // overall panel size in RebuildUi) rather than eating into the fill/content, so
        // the row list and its padding don't get any tighter for a thicker border
        internal const float PanelBorderThickness = 11f;
        internal const float PanelOuterMargin = PanelBorderThickness - 7f;
        private const float RowCapRadius = 14f;
        private const float RowStarIconSize = 22f;
        // How far the SELECTED row's bar sticks out past the normal row width on each
        // side, since it's a solid (non-translucent) highlight, popping it out a little
        // reads as more emphasized than just a same-width color swap
        private const float RowSelOverflow = 8f;

        private GameObject _root;
        private Image _dimImage;
        private float _dimFadeElapsed;
        private const float DimFadeDuration = 0.25f;

        // First-open loading indicator (item 2): shown instead of the real menu only
        // once per session, while the (heavy, one-time) real menu is being built
        private GameObject _loadingRoot;
        private bool _uiWarmedUp;
        private bool _warmingUp;

        private RectTransform _panelRect;
        private Image _panelFillImage;
        private Image _grainImage;
        private RectTransform _rowsContainer;
        private RectTransform _footerRow;
        private RectTransform _titleRow;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _warnText;
        private TextMeshProUGUI _scrollUpHint;
        private TextMeshProUGUI _scrollDownHint;
        private readonly List<FooterEntry> _footerEntries = new List<FooterEntry>();
        private readonly List<Image> _rowHighlightPool = new List<Image>();
        // Row text is split into per-field "columns" (difficulty / biome / date /
        // playtime) so they stack into readable, anchored positions instead of one
        // free-flowing string. See ComputeColumnLayout/RebuildUi for how the actual
        // x-positions are derived (and degrade to a packed fallback when they don't fit)
        private readonly List<TextMeshProUGUI> _rowDiffPool = new List<TextMeshProUGUI>();
        private readonly List<TextMeshProUGUI> _rowMidPool = new List<TextMeshProUGUI>();
        private readonly List<TextMeshProUGUI> _rowDatePool = new List<TextMeshProUGUI>();
        private readonly List<TextMeshProUGUI> _rowPlayPool = new List<TextMeshProUGUI>();
        private readonly List<Image> _rowStarPool = new List<Image>();
        // The selected row's gold/jagged/grained look is drawn by ONE dedicated overlay
        // (behind the row list, repositioned to whichever row is currently selected),
        // not by whichever pooled row Image happens to be selected, and its sprite
        // bakes fill+grain+jag together with no Mask involved at all (see
        // MakeFullCapSpriteWithGrain). A Mask that gets toggled active/repositioned
        // every time selection moves, tried twice now (once per pooled row, once on
        // this same shared overlay), is what was actually causing "only row 0 ever
        // shows it correctly": Unity's stencil-buffer bookkeeping for that combination
        // doesn't reliably clean up
        private GameObject _selOverlay;
        private RectTransform _selOverlayRect;
        private Image _selOverlayImage;
        private TMP_FontAsset _font;
        private static Sprite _panelInnerMaskSprite;
        private static Sprite _badgeSprite;
        private static Sprite _rowCapSprite;
        private static Texture2D _grainTexturePanel;

        // Deliberately minimal: a dim background + one line of TMP text, no procedural
        // sprite baking at all, so this is essentially free to build/show on the very
        // frame the key is pressed (unlike EnsureUi/RebuildUi, the actual expensive part)
        // Native widescreen support: pins the canvas to a constant 1080 reference-pixel
        // height (see ReferenceHeight/CanvasWidthUnits) instead of the default width/
        // height blend. At the blend's default (0.5), extra width on a 21:9/32:9
        // monitor drags the canvas's effective VERTICAL reference-pixel count down
        // with it, shrinking the canvas below this panel's own height and letting
        // rows - including the selected (yellow) one - render partly or fully off
        // screen with no way to scroll into view. Matching height alone means only
        // the available WIDTH changes with aspect, and it only ever gains on
        // anything 16:9 or wider, never loses
        internal static void ApplyWidescreenScaler(Canvas canvas)
        {
            var scaler = canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, ReferenceHeight);
            scaler.matchWidthOrHeight = 1f;
        }

        private void EnsureLoadingUi()
        {
            if (_loadingRoot != null) return;
            try
            {
                if (_font == null) _font = FindGameFont();

                _loadingRoot = new GameObject("PEAKQuickResume_SavePicker_Loading", typeof(RectTransform));
                _loadingRoot.transform.SetParent(transform, false);
                var canvas = _loadingRoot.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 30000;
                ApplyWidescreenScaler(canvas);

                var dimGo = new GameObject("Dim", typeof(RectTransform));
                dimGo.transform.SetParent(_loadingRoot.transform, false);
                var dim = dimGo.AddComponent<Image>();
                dim.color = DimColor;
                StretchFull((RectTransform)dimGo.transform);

                var text = MakeText(_loadingRoot.transform, "LoadingText", 30, FontStyles.Normal, TitleColor, TextAlignmentOptions.Center);
                ApplyChromeTextStyle(text);
                text.text = SavePickerLocalization.Get(PickerText.Loading);
                var textRect = (RectTransform)text.transform;
                StretchFull(textRect);

                _loadingRoot.SetActive(false);
            }
            catch (Exception e)
            {
                _log?.LogError($"SavePicker.EnsureLoadingUi failed (non-fatal): {e}");
            }
        }

        private void EnsureUi()
        {
            if (_root != null) return;
            try
            {
                _font = FindGameFont();

                _root = new GameObject("PEAKQuickResume_SavePicker", typeof(RectTransform));
                _root.transform.SetParent(transform, false);
                var canvas = _root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 30000; // well above the game's own in-world/HUD canvases
                ApplyWidescreenScaler(canvas);

                var dimGo = new GameObject("Dim", typeof(RectTransform));
                dimGo.transform.SetParent(_root.transform, false);
                _dimImage = dimGo.AddComponent<Image>();
                _dimImage.color = new Color(DimColor.r, DimColor.g, DimColor.b, 0f);
                StretchFull((RectTransform)dimGo.transform);

                var panelGo = new GameObject("Panel", typeof(RectTransform));
                panelGo.transform.SetParent(_root.transform, false);
                _panelFillImage = panelGo.AddComponent<Image>();
                // Rounded corners + a heavy near-black outline baked straight into the
                // sprite (not a separate GameObject behind it, that only ever gave a
                // plain rectangular ring), matching the boarding pass / map rotation
                // panels' look. Colors live in the texture itself, so the Image needs no
                // tint (white = "use the sprite's own baked colors as-is")
                //
                // Type.Simple (one full baked texture, stretched as a single piece),
                // NOT Sliced: 9-slicing stretches the straight-edge strips along their
                // long axis to fill the shape, same problem already fixed for the
                // selected row, diluting the edge jag there down to nothing everywhere
                // except the (correctly unstretched) corners. The panel had the exact
                // same bug, just less obviously since its corners looked fine on their
                // own and drew attention away from the flat straight edges. The sprite
                // itself is assigned in RebuildUi (baked at the panel's actual current
                // size, see PanelSprite(width, height)), not here, since that size
                // isn't known yet on the very first build
                _panelFillImage.type = Image.Type.Simple;
                _panelFillImage.color = Color.white;
                _panelRect = (RectTransform)panelGo.transform;
                _panelRect.anchorMin = _panelRect.anchorMax = new Vector2(0.5f, 0.5f);
                _panelRect.pivot = new Vector2(0.5f, 0.5f);

                // A separate, invisible masking child, inset by the border thickness so
                // it covers exactly the FILL area (not the border ring), with its own
                // matching-but-smaller-radius rounded shape. Putting Mask directly on
                // the panel's own visible Image (tried previously) made Unity swap that
                // Image onto its stencil-only mask material, which stopped the border
                // itself from rendering, hence a second dedicated, invisible mask host
                // instead: the panel's own Image is never touched by Mask at all, so its
                // border always renders exactly as authored, and the grain (a child of
                // THIS object, not the panel) is clipped to just the interior
                var maskGo = new GameObject("GrainMask", typeof(RectTransform));
                maskGo.transform.SetParent(panelGo.transform, false);
                var maskImage = maskGo.AddComponent<Image>();
                maskImage.sprite = PanelInnerMaskSprite();
                maskImage.type = Image.Type.Sliced;
                // Mask reads its shape from this Image's RENDERED alpha (texture alpha
                // x tint alpha), not just the sprite's own shape, a Color.clear tint
                // here was tried first ("shape only, don't actually draw it") and
                // instead zeroed the effective coverage everywhere, so the mask clipped
                // ALL children away completely, not just outside the rounded corners.
                // Full-alpha white + showMaskGraphic=false is the correct combination:
                // that flag hides the graphic's own output while leaving its alpha
                // shape intact for the stencil test
                maskImage.color = Color.white;
                var mask = maskGo.AddComponent<Mask>();
                mask.showMaskGraphic = false;
                var maskRect = (RectTransform)maskGo.transform;
                maskRect.anchorMin = Vector2.zero;
                maskRect.anchorMax = Vector2.one;
                maskRect.offsetMin = new Vector2(PanelBorderThickness, PanelBorderThickness);
                maskRect.offsetMax = new Vector2(-PanelBorderThickness, -PanelBorderThickness);

                var grainGo = new GameObject("Grain", typeof(RectTransform));
                grainGo.transform.SetParent(maskGo.transform, false);
                var grain = grainGo.AddComponent<Image>();
                // Type.Simple (just stretch this one texture to fill the rect), NOT
                // Tiled: getting Tiled's on-screen tile size right meant predicting the
                // canvas's actual effective scale via sprite pixelsPerUnit x
                // pixelsPerUnitMultiplier, and three rounds of guessing that number
                // (1, then 16) landed nowhere close, the true scale clearly isn't what
                // a plain "1 unit = 1 pixel" assumption says it is. Simple sidesteps
                // that entirely: the grain size is just "how many texels wide is the
                // texture", directly controlled by PanelGrainTexture()'s own
                // resolution, no guessing about canvas/PPU scale required at all
                grain.sprite = Sprite.Create(PanelGrainTexture(), new Rect(0, 0, GrainTextureSize, GrainTextureSize), new Vector2(0.5f, 0.5f), 100f);
                grain.type = Image.Type.Simple;
                grain.color = Color.white; // grain shade is baked into the texture itself (alpha applied in RebuildUi)
                grain.raycastTarget = false;
                StretchFull((RectTransform)grainGo.transform);
                _grainImage = grain;

                BuildTitleRow(panelGo.transform);

                _scrollUpHint = MakeText(panelGo.transform, "ScrollUp", 15, FontStyles.Normal, ScrollHintColor, TextAlignmentOptions.Center);
                _scrollUpHint.text = "▲";
                var upRect = (RectTransform)_scrollUpHint.transform;
                upRect.anchorMin = new Vector2(0f, 1f);
                upRect.anchorMax = new Vector2(1f, 1f);
                upRect.pivot = new Vector2(0.5f, 1f);
                upRect.sizeDelta = new Vector2(-2f * PanelPaddingHorizontal, ScrollHintHeight);
                upRect.anchoredPosition = new Vector2(0f, -(PanelPadding + TitleHeight + ScrollHintGap));

                var rowsGo = new GameObject("Rows", typeof(RectTransform));
                rowsGo.transform.SetParent(panelGo.transform, false);
                rowsGo.AddComponent<RectMask2D>();
                _rowsContainer = (RectTransform)rowsGo.transform;

                BuildSelectionOverlay(_rowsContainer);

                _scrollDownHint = MakeText(panelGo.transform, "ScrollDown", 15, FontStyles.Normal, ScrollHintColor, TextAlignmentOptions.Center);
                _scrollDownHint.text = "▼";
                var downRect = (RectTransform)_scrollDownHint.transform;
                downRect.anchorMin = new Vector2(0f, 0f);
                downRect.anchorMax = new Vector2(1f, 0f);
                downRect.pivot = new Vector2(0.5f, 0f);
                downRect.sizeDelta = new Vector2(-2f * PanelPaddingHorizontal, ScrollHintHeight);
                downRect.anchoredPosition = new Vector2(0f, PanelPadding + FooterHeight + WarnHeight + ScrollHintGap);

                _warnText = MakeText(panelGo.transform, "Warn", 16, FontStyles.Normal, WarnColor, TextAlignmentOptions.Center);
                var warnRect = (RectTransform)_warnText.transform;
                warnRect.anchorMin = new Vector2(0f, 0f);
                warnRect.anchorMax = new Vector2(1f, 0f);
                warnRect.pivot = new Vector2(0.5f, 0f);
                warnRect.sizeDelta = new Vector2(-2f * PanelPaddingHorizontal, WarnHeight);
                warnRect.anchoredPosition = new Vector2(0f, PanelPadding + FooterHeight);

                BuildFooterRow(panelGo.transform);

                EnsureRowPool(MaxVisibleRows);

                _root.SetActive(false);
            }
            catch (Exception e)
            {
                _log?.LogError($"SavePicker.EnsureUi failed (non-fatal, F7 picker will not render): {e}");
            }
        }

        // One archived save's row text, split into the 4 fields that get lined up as
        // "columns" (difficulty / last biome reached / date / playtime, the last
        // including the co-op player list when applicable)
        private readonly struct RowFields
        {
            public readonly string Difficulty;
            public readonly string Biome;
            public readonly string Date;
            public readonly string Playtime;

            public RowFields(string difficulty, string biome, string date, string playtime)
            {
                Difficulty = difficulty;
                Biome = biome;
                Date = date;
                Playtime = playtime;
            }
        }

        private RowFields GetRowFields(ArchivedSave e)
        {
            // CampfireName (despite its name) is the deepest campfire/segment actually
            // reached, not BiomesSummary - see the comment on SaveArchive.CampfireLabel
            string biome = string.IsNullOrEmpty(e.CampfireName) ? "—" : SaveArchive.CampfireLabel(e.CampfireName);
            string date = string.IsNullOrEmpty(e.SaveDate) ? e.SortTime.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : e.SaveDate;
            // A stale save (written under an older game version - map pool very likely
            // rotated since) swaps the usual "Xh Ym played" text for the version it was
            // actually written under instead, formatted the same "vX.Y.z" way as the
            // game's own top-left corner version label. No new UI element, no extra
            // column width to budget for any language - deliberately reuses this slot
            // instead of adding an icon precisely so it can't overflow the row layout
            string playtime = e.IsStaleVersion ? GameVersionCompat.Display(e.EffectiveGameVersion) : FormatPlaytime(e.Playtime);
            // Co-op: show everyone who played this run, tacked onto the last column
            // (rather than as its own column) since it's optional/co-op-only
            if (!_offline && !string.IsNullOrEmpty(e.Players))
                playtime += $"  ({e.Players})";
            return new RowFields(e.DifficultyLabel, biome, date, playtime);
        }

        private readonly struct ColumnLayout
        {
            public readonly bool UseColumns;
            public readonly float MidStartX;
            public readonly float DateStartX;

            public ColumnLayout(bool useColumns, float midStartX, float dateStartX)
            {
                UseColumns = useColumns;
                MidStartX = midStartX;
                DateStartX = dateStartX;
            }
        }

        // Decides where the difficulty/biome/date columns start, and whether biome+date
        // fit as two independently-aligned columns at all. Widths are measured across
        // EVERY archived save (not just the visible page) so column positions stay put
        // while scrolling. If the full 4-column layout would overflow the row (long
        // biome/campfire names, a verbose language, ...) biome and date collapse into a
        // single packed field instead of being clipped - the playtime column stays
        // right-aligned either way, and the difficulty column never moves, so this is
        // always enough slack to make everything fit (see the constants' comment)
        private ColumnLayout ComputeColumnLayout(float availableWidth)
        {
            var measure = _rowDiffPool.Count > 0 ? _rowDiffPool[0] : null;
            if (measure == null || _entries.Count == 0)
                return new ColumnLayout(true, RowTextInset, RowTextInset);

            float maxDiff = 0f, maxBiome = 0f, maxDate = 0f, maxPlay = 0f;
            foreach (var e in _entries)
            {
                RowFields f = GetRowFields(e);
                maxDiff = Mathf.Max(maxDiff, measure.GetPreferredValues(f.Difficulty).x);
                maxBiome = Mathf.Max(maxBiome, measure.GetPreferredValues(f.Biome).x);
                maxDate = Mathf.Max(maxDate, measure.GetPreferredValues(f.Date).x);
                maxPlay = Mathf.Max(maxPlay, measure.GetPreferredValues(f.Playtime).x);
            }

            float midStartX = RowTextInset + maxDiff + RowColumnGap;
            float dateStartX = midStartX + maxBiome + RowColumnGap;
            float columnsTotal = dateStartX + maxDate + RowColumnGap + maxPlay;
            if (columnsTotal <= availableWidth)
                return new ColumnLayout(true, midStartX, dateStartX);

            return new ColumnLayout(false, midStartX, midStartX);
        }

        // Rebuilds everything that can change while open: panel size, row content/
        // selection, footer/warning text. Only called on Open/Move/Delete, never per-frame
        private void RebuildUi()
        {
            if (_root == null) return;
            try
            {
                int visibleRows = Mathf.Min(_entries.Count, MaxVisibleRows);
                // Width against the SCALED canvas's own width (which grows with aspect
                // ratio, see CanvasWidthUnits), not raw Screen.width - the canvas no
                // longer measures 1:1 with physical screen pixels once the widescreen
                // scaler is applied. Height stays against the constant ReferenceHeight
                // for the same reason (the canvas is always exactly that tall)
                float w = Mathf.Min(PanelWidth, CanvasWidthUnits - 80f) + 2f * PanelOuterMargin;
                float chrome = PanelPadding * 2f + TitleHeight + FooterHeight + WarnHeight
                    + 2f * ScrollHintHeight + 4f * ScrollHintGap;
                float h = Mathf.Min(chrome + visibleRows * RowHeight, ReferenceHeight - 80f) + 2f * PanelOuterMargin;

                bool minimalUi = MinimalUi;
                _panelRect.sizeDelta = new Vector2(w, h);
                _panelFillImage.sprite = PanelSprite(Mathf.RoundToInt(w), Mathf.RoundToInt(h), _jagFrame, minimalUi);

                // Panel opacity is user-configurable (see PluginConfig.PanelOpacity) so
                // players can see through the menu's background if they want to. Read
                // fresh every rebuild (not just on open) so a change via Configuration
                // Manager while the picker happens to be open takes effect immediately.
                // The grain overlay is faded the same amount as the fill/border it sits
                // on top of (it's baked fully opaque, see PanelGrainTexture, so without
                // this it would keep hiding whatever the fill's own transparency reveals).
                // In minimal mode (see PluginConfig.MinimalPickerUi) the grain overlay is
                // hidden entirely, leaving a plain flat-colored panel
                float panelOpacity = _cfg != null ? Mathf.Clamp01(_cfg.PanelOpacity.Value) : 1f;
                _panelFillImage.color = new Color(1f, 1f, 1f, panelOpacity);
                if (_grainImage != null)
                {
                    _grainImage.gameObject.SetActive(!minimalUi);
                    _grainImage.color = new Color(1f, 1f, 1f, panelOpacity);
                }

                // Widened by RowSelOverflow on each side vs. PanelPadding: this is the
                // clipping bound for the row list, and the selected row's bar is
                // deliberately drawn out to fill it (see the per-row loop below), while
                // normal rows stay inset back to the "real" PanelPaddingHorizontal column so they
                // still line up with the header/footer
                float rowMaskPadding = PanelPaddingHorizontal - RowSelOverflow;
                _rowsContainer.anchorMin = Vector2.zero;
                _rowsContainer.anchorMax = Vector2.one;
                _rowsContainer.offsetMin = new Vector2(rowMaskPadding,
                    PanelPadding + FooterHeight + WarnHeight + ScrollHintGap + ScrollHintHeight + ScrollHintGap);
                _rowsContainer.offsetMax = new Vector2(-rowMaskPadding,
                    -(PanelPadding + TitleHeight + ScrollHintGap + ScrollHintHeight + ScrollHintGap));

                _titleText.text = $"Quick Resume  {SavePickerLocalization.Get(PickerText.LoadSave)}  "
                    + $"({(_offline ? SavePickerLocalization.Get(PickerText.Solo) : SavePickerLocalization.Get(PickerText.Coop))})";

                // Same fix as RefreshFooter's badge sizing: a freshly (re)built TMP
                // text's preferred size is unreliable until its mesh has actually been
                // generated at least once, which left the title icons visibly
                // misplaced on first open until any input forced a later rebuild.
                // Forcing the mesh + a layout pass here corrects it the same frame
                if (_titleRow != null)
                {
                    Canvas.ForceUpdateCanvases();
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_titleRow);
                }

                _scrollUpHint.gameObject.SetActive(_scrollOffset > 0);
                _scrollDownHint.gameObject.SetActive(_scrollOffset + visibleRows < _entries.Count);

                bool selectionVisible = false;

                float availableTextWidth = w - 2f * PanelPaddingHorizontal - 2f * RowTextInset - RowStarReserve;
                var layout = ComputeColumnLayout(availableTextWidth);

                for (int i = 0; i < _rowDiffPool.Count; i++)
                {
                    int entryIndex = _scrollOffset + i;
                    bool visible = entryIndex < _entries.Count;
                    Image highlight = _rowHighlightPool[i];
                    Image star = _rowStarPool[i];
                    highlight.gameObject.SetActive(visible);
                    if (!visible) { star.gameObject.SetActive(false); continue; }

                    var e = _entries[entryIndex];
                    bool sel = entryIndex == _selected;
                    RowFields f = GetRowFields(e);

                    var diffText = _rowDiffPool[i];
                    var midText = _rowMidPool[i];
                    var dateText = _rowDatePool[i];
                    var playText = _rowPlayPool[i];

                    diffText.text = f.Difficulty;
                    var diffRect = (RectTransform)diffText.transform;
                    diffRect.offsetMin = new Vector2(RowTextInset, diffRect.offsetMin.y);

                    if (layout.UseColumns)
                    {
                        midText.text = f.Biome;
                        var midRect = (RectTransform)midText.transform;
                        midRect.offsetMin = new Vector2(layout.MidStartX, midRect.offsetMin.y);

                        dateText.gameObject.SetActive(true);
                        dateText.text = f.Date;
                        var dateRect = (RectTransform)dateText.transform;
                        dateRect.offsetMin = new Vector2(layout.DateStartX, dateRect.offsetMin.y);
                    }
                    else
                    {
                        midText.text = f.Biome + RowPackedMidSeparator + f.Date;
                        var midRect = (RectTransform)midText.transform;
                        midRect.offsetMin = new Vector2(layout.MidStartX, midRect.offsetMin.y);

                        dateText.gameObject.SetActive(false);
                    }

                    playText.text = f.Playtime;

                    Color rowColor = sel ? RowSelTextColor : RowColor;
                    diffText.color = rowColor;
                    midText.color = rowColor;
                    dateText.color = rowColor;
                    playText.color = rowColor;

                    star.gameObject.SetActive(e.Starred);

                    // Zebra striping by ABSOLUTE entry index (not pool slot), so the
                    // stripe pattern stays stable as the list scrolls instead of flipping
                    // every time the window slides by one row. The "plain" rows are left
                    // exactly transparent (not a matching flat color), so the panel's own
                    // background (incl. its grain texture) shows straight through them.
                    // The SELECTED row is also left transparent here: its background is
                    // _selOverlay, drawn behind it, this Image must stay out of the way
                    bool striped = entryIndex % 2 == 0;
                    highlight.color = (!sel && striped) ? RowStripeColor : Color.clear;
                    highlight.sprite = (!sel && striped) ? RowCapSprite() : null;
                    highlight.type = (!sel && striped) ? Image.Type.Sliced : Image.Type.Simple;

                    // Rows always stay inset to the "real" PanelPaddingHorizontal
                    // column; the bulge-past-that-column look for the selection lives
                    // entirely on _selOverlay now (full rowsContainer width, see
                    // BuildSelectionOverlay), not on whichever pooled row is selected
                    var rowRect = (RectTransform)highlight.transform;
                    Vector2 om = rowRect.offsetMin; om.x = RowSelOverflow; rowRect.offsetMin = om;
                    Vector2 ox = rowRect.offsetMax; ox.x = -RowSelOverflow; rowRect.offsetMax = ox;

                    if (sel)
                    {
                        selectionVisible = true;
                        _selOverlayRect.anchoredPosition = new Vector2(0f, -(i * RowHeight));
                    }
                }

                _selOverlay.SetActive(selectionVisible);
                // Refreshed every rebuild (not just on the animation tick) so toggling
                // minimal-ui via Configuration Manager while the picker is open takes
                // effect immediately, same as panelOpacity above
                ApplySelOverlaySprite(minimalUi);

                RefreshFooter();
                RefreshWarn();
            }
            catch (Exception e)
            {
                _log?.LogError($"SavePicker.RebuildUi failed (non-fatal): {e}");
            }
        }

        // Cheap refresh for just the footer row: reflects the CURRENT resume key (in
        // case it was rebound) and whether it also confirms a load while open, so it
        // never shows a key that no longer does what it says
        private void RefreshFooter()
        {
            if (_footerRow == null || _footerEntries.Count < 5) return;
            string key = _cfg != null ? _cfg.ResumeKey.Value.ToString() : "F7";
            bool keyAlsoLoads = _cfg == null || _cfg.ResumeKeyAlsoConfirmsLoad.Value;
            string loadKeys = keyAlsoLoads ? $"{key} / Enter" : "Enter";
            string starKey = _cfg != null ? _cfg.StarKey.Value.ToString() : "B";
            bool starred = Selected != null && Selected.Starred;

            SetFooterEntry(_footerEntries[0], "↑/↓", SavePickerLocalization.Get(PickerText.Select));
            SetFooterEntry(_footerEntries[1], loadKeys, SavePickerLocalization.Get(PickerText.Load));
            SetFooterEntry(_footerEntries[2], starKey, SavePickerLocalization.Get(starred ? PickerText.Unstar : PickerText.Star));
            SetFooterEntry(_footerEntries[3], "Del", SavePickerLocalization.Get(PickerText.Delete));
            SetFooterEntry(_footerEntries[4], "Esc", SavePickerLocalization.Get(PickerText.Cancel));

            // Badge widths are driven by ContentSizeFitter off the key text's own
            // preferred size, force an immediate layout pass so a changed key (e.g. the
            // resume key got rebound to something longer than "F7") resizes correctly
            // this same frame instead of one frame late. A freshly created TMP text's
            // preferred size is unreliable until its mesh has actually been generated at
            // least once (a known TMP quirk), which is why the very first open of a
            // session showed the whole row mis-packed until any input forced a rebuild.
            // Canvas.ForceUpdateCanvases() generates that first mesh immediately so the
            // layout pass right after it sees correct sizes from frame one
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_footerRow);
        }

        private static void SetFooterEntry(FooterEntry entry, string key, string label)
        {
            entry.KeyText.text = key;
            entry.LabelText.text = label;
        }

        // Builds the row of "[key badge] label" pairs (↑/↓ Select, F7/Enter Load, Del
        // Delete, Esc Cancel). A real rounded-rect Image behind each key, not a TMP
        // <mark> tag (no rounded corners, and its background doesn't vertically center
        // against the text the way a normal layout does)
        private const float TitleIconSize = 30f;
        private const float TitleIconSpacing = 10f;
        // The flame sprite's own art has more headroom above the flame than below it
        // (the tip tapers to a point near the top of its bounding box), so placing it
        // dead-center in its box reads as sitting slightly HIGH next to the title
        // text's own baseline-centered glyphs. Nudged down by a few px to correct that
        private const float TitleIconVerticalNudge = 3f;
        private static Sprite _campfireIconSprite;

        // Sampled directly from the game's own "DarumaDropOne-Regular SDF Outline"
        // material (screenshotted title text outline pixels came out ~(59, 58, 55)),
        // a warm dark gray rather than pure black - used here so the campfire icons'
        // own outline reads as the exact same "chrome" color as the text beside them
        internal static readonly Color ChromeOutlineColor = new Color(59f / 255f, 58f / 255f, 55f / 255f);

        // Same "Quick Resume" title, now bracketed by the game's own campfire icon
        // (the small flame the vanilla HUD shows on StaminaBar next to the stamina bar
        // while the no-hunger buff is active) on both sides. A HorizontalLayoutGroup +
        // ContentSizeFitter on the text (same technique BuildFooterEntry already uses)
        // rather than fixed-position Images: that keeps icon+text+icon centered as one
        // compact group regardless of how wide the localized title text ends up being
        private void BuildTitleRow(Transform parent)
        {
            var rowGo = new GameObject("TitleRow", typeof(RectTransform));
            rowGo.transform.SetParent(parent, false);
            _titleRow = (RectTransform)rowGo.transform;
            _titleRow.anchorMin = new Vector2(0f, 1f);
            _titleRow.anchorMax = new Vector2(1f, 1f);
            _titleRow.pivot = new Vector2(0.5f, 1f);
            _titleRow.sizeDelta = new Vector2(-2f * PanelPaddingHorizontal, TitleHeight);
            _titleRow.anchoredPosition = new Vector2(0f, -PanelPadding);

            var rowLayout = rowGo.AddComponent<HorizontalLayoutGroup>();
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.spacing = TitleIconSpacing;
            rowLayout.childControlWidth = false;
            rowLayout.childControlHeight = false;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = false;

            var iconSprite = FindCampfireIcon();
            AddTitleIcon(rowGo.transform, iconSprite);

            _titleText = MakeText(rowGo.transform, "Title", 30, FontStyles.Normal, TitleColor, TextAlignmentOptions.Center);
            ApplyChromeTextStyle(_titleText);
            var titleFitter = _titleText.gameObject.AddComponent<ContentSizeFitter>();
            titleFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            titleFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            AddTitleIcon(rowGo.transform, iconSprite);
        }

        // How much bigger each backing silhouette copy (see AddTitleIcon) is drawn
        // than the real icon on top of it. Kept modest on its own - most of the
        // visible ring comes from the diagonal offset between the two copies below,
        // not from this alone (a single copy at a scale big enough to read on its own
        // was the "one big blob" attempt that came before this one)
        private const float TitleIconOutlineScale = 1.12f;
        // How far apart (in opposite diagonal directions) the two backing copies sit,
        // in px. Big enough that their offset does real work toward the ring's
        // thickness, small enough the two copies still overlap enough almost
        // everywhere around the shape that no gap or second silhouette becomes visible
        private const float TitleIconOutlineOffset = 1.1f;

        private void AddTitleIcon(Transform parent, Sprite iconSprite)
        {
            if (iconSprite == null) return;

            // A slot GameObject (sized/spaced by the row's HorizontalLayoutGroup) with
            // the actual images as its children, rather than living directly on the
            // layout-managed slot: the row's layout group re-centers/repositions its
            // direct children on every rebuild (RebuildUi runs on every open/move),
            // which would silently undo any manual offset placed on it. The vertical
            // nudge below lives on these nested, layout-untouched children instead
            var slotGo = new GameObject("IconSlot", typeof(RectTransform));
            slotGo.transform.SetParent(parent, false);
            ((RectTransform)slotGo.transform).sizeDelta = new Vector2(TitleIconSize, TitleIconSize);

            // Outline, take three. UGUI's built-in Outline component draws 4 diagonal
            // offset copies at once, which needed a big offset to read next to the
            // title's own heavy SDF outline and split into 2 distinct ghost flames
            // instead of a ring. A SINGLE scaled-up silhouette copy (the attempt right
            // before this one) avoided the ghosting but needed to be scaled up so much
            // to be noticeable that it read as one big blob rather than a slim border.
            // Splitting the difference: two silhouette copies, each just slightly
            // scaled up AND offset in opposite diagonal directions. Neither the scale
            // nor the offset alone has to do all the work, so both can stay small - the
            // two copies' silhouettes overlap almost everywhere except right at the
            // icon's own edge, which is exactly where the combined ring shows through
            AddIconSilhouette(slotGo.transform, iconSprite, new Vector2(TitleIconOutlineOffset, TitleIconOutlineOffset));
            AddIconSilhouette(slotGo.transform, iconSprite, new Vector2(-TitleIconOutlineOffset, -TitleIconOutlineOffset));

            var iconGo = new GameObject("Icon", typeof(RectTransform));
            iconGo.transform.SetParent(slotGo.transform, false);
            var iconImage = iconGo.AddComponent<Image>();
            iconImage.sprite = iconSprite;
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;
            var iconRect = (RectTransform)iconGo.transform;
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = Vector2.zero;
            iconRect.offsetMax = Vector2.zero;
            iconRect.anchoredPosition = new Vector2(0f, -TitleIconVerticalNudge);

            // Drop shadow, same direction and roughly the same visual weight as the
            // title text's own borrowed SDF material (its underlay reads as a solid,
            // fairly opaque dark patch bulking out the glyphs' bottom-right side, not
            // a faint soft blur) - down-and-right, same ChromeOutlineColor, high alpha
            var shadow = iconGo.AddComponent<UnityEngine.UI.Shadow>();
            shadow.effectColor = new Color(ChromeOutlineColor.r, ChromeOutlineColor.g, ChromeOutlineColor.b, 0.85f);
            shadow.effectDistance = new Vector2(2.5f, -2.5f);
            shadow.useGraphicAlpha = true;
        }

        // One flat ChromeOutlineColor copy of the icon sprite, slightly scaled up and
        // offset from center, stretched full over its parent slot. Two of these at
        // opposite offsets (see AddTitleIcon) are what actually forms the ring; see
        // that method's comment for why two small nudges beat one big one
        private static void AddIconSilhouette(Transform parent, Sprite iconSprite, Vector2 offset)
        {
            var go = new GameObject("IconOutline", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = iconSprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.color = ChromeOutlineColor;
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.anchoredPosition = new Vector2(offset.x, offset.y - TitleIconVerticalNudge);
            rect.localScale = new Vector3(TitleIconOutlineScale, TitleIconOutlineScale, 1f);
        }

        // The campfire icon isn't a bundled asset, it's pulled from the game's own
        // vanilla HUD (StaminaBar.campfire, the small icon shown while the no-hunger
        // buff is active), same "reuse the game's own art" approach FindGameFont()
        // uses for the title font. Cached once found (Sprite references stay valid for
        // the rest of the session, same reasoning as the cached TMP font); if no
        // StaminaBar exists yet (e.g. the very first open happens before any level's
        // HUD has ever loaded), this just tries again next open instead of giving up
        // permanently, the title row simply shows text-only until then
        private static Sprite FindCampfireIcon()
        {
            if (_campfireIconSprite != null) return _campfireIconSprite;
            try
            {
                var bar = UnityEngine.Object.FindObjectOfType<StaminaBar>();
                var icon = bar != null && bar.campfire != null
                    ? bar.campfire.GetComponentInChildren<Image>(true) ?? bar.campfire.GetComponent<Image>()
                    : null;
                if (icon != null && icon.sprite != null) _campfireIconSprite = icon.sprite;
            }
            catch { /* non-fatal: title just shows without the icon this open */ }
            return _campfireIconSprite;
        }

        private void BuildFooterRow(Transform parent)
        {
            var rowGo = new GameObject("Footer", typeof(RectTransform));
            rowGo.transform.SetParent(parent, false);
            _footerRow = (RectTransform)rowGo.transform;
            _footerRow.anchorMin = new Vector2(0f, 0f);
            _footerRow.anchorMax = new Vector2(1f, 0f);
            _footerRow.pivot = new Vector2(0.5f, 0f);
            _footerRow.sizeDelta = new Vector2(-2f * PanelPaddingHorizontal, FooterHeight);
            _footerRow.anchoredPosition = new Vector2(0f, PanelPadding);

            var rowLayout = rowGo.AddComponent<HorizontalLayoutGroup>();
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.spacing = 28f;
            rowLayout.childControlWidth = false;
            rowLayout.childControlHeight = false;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = false;

            _footerEntries.Clear();
            for (int i = 0; i < 5; i++)
                _footerEntries.Add(BuildFooterEntry(rowGo.transform));
        }

        private FooterEntry BuildFooterEntry(Transform parent)
        {
            var entryGo = new GameObject("Entry", typeof(RectTransform));
            entryGo.transform.SetParent(parent, false);
            var entryLayout = entryGo.AddComponent<HorizontalLayoutGroup>();
            entryLayout.childAlignment = TextAnchor.MiddleCenter;
            entryLayout.spacing = 8f;
            entryLayout.childControlWidth = false;
            entryLayout.childControlHeight = false;
            entryLayout.childForceExpandWidth = false;
            entryLayout.childForceExpandHeight = false;
            var entryFitter = entryGo.AddComponent<ContentSizeFitter>();
            entryFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            entryFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Badge: a rounded-rect Image sized to its own key text via a nested
            // HorizontalLayoutGroup (acting as internal padding) + ContentSizeFitter
            var badgeGo = new GameObject("Badge", typeof(RectTransform));
            badgeGo.transform.SetParent(entryGo.transform, false);
            var badgeImage = badgeGo.AddComponent<Image>();
            badgeImage.sprite = BadgeSprite();
            badgeImage.type = Image.Type.Sliced;
            badgeImage.color = Color.white; // colors are baked into the sprite, see BadgeSprite()
            var badgeLayout = badgeGo.AddComponent<HorizontalLayoutGroup>();
            badgeLayout.childAlignment = TextAnchor.MiddleCenter;
            badgeLayout.padding = new RectOffset(10, 10, 4, 4);
            badgeLayout.childControlWidth = true;
            badgeLayout.childControlHeight = true;
            var badgeFitter = badgeGo.AddComponent<ContentSizeFitter>();
            badgeFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            badgeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var keyText = MakeText(badgeGo.transform, "Key", 15, FontStyles.Normal, KeyTextColor, TextAlignmentOptions.Midline);

            var labelText = MakeText(entryGo.transform, "Label", 16, FontStyles.Normal, FooterColor, TextAlignmentOptions.Midline);
            ApplyChromeTextStyle(labelText);
            var labelFitter = labelText.gameObject.AddComponent<ContentSizeFitter>();
            labelFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            labelFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            return new FooterEntry { KeyText = keyText, LabelText = labelText };
        }

        internal static Sprite BadgeSprite() => _badgeSprite ??=
            MakeRoundedSprite(size: 32, radius: 10f, borderThickness: 3f, fill: KeyChipFillColor, border: BadgeBorderColor);

        private static Sprite _starSprite;

        // The starred-row indicator. The game itself has no star/favorite icon
        // anywhere to reuse (unlike FindCampfireIcon's HUD-icon trick), so this is
        // baked the same procedural way as every other shape in this file: a plain
        // analytic distance-field rasterization, cached once
        internal static Sprite StarSprite() => _starSprite ??=
            MakeStarSprite(size: 24, fill: StarFillColor, border: BadgeBorderColor, borderThickness: 1.6f);

        // A filled 5-point star via an exact closed-form signed distance function
        // (Inigo Quilez's sdStar5), not a rasterized polygon: at this icon's small size
        // a polygon approach would need its own point-in-polygon + per-edge nearest-
        // distance pass just to anti-alias the concave notches between points cleanly,
        // the closed-form distance gives that for free
        private static Sprite MakeStarSprite(int size, Color fill, Color border, float borderThickness)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            float half = size / 2f;
            float outerRadius = half - 2f; // margin for AA + the outline
            const float innerRatio = 0.5f; // inner/outer vertex radius ratio

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // NOT flipping Y here (texture rows run top-down already): SdStar5's
                    // single point faces the -Y direction of whatever space it's given,
                    // so feeding it row-major Y directly (without the usual top-down ->
                    // math-Y-up flip) is what actually points the star tip up on screen
                    var p = new Vector2(x + 0.5f - half, y + 0.5f - half);
                    float d = SdStar5(p, outerRadius, innerRatio);

                    float alpha = Mathf.Clamp01(0.5f - d);
                    Color c = d > -borderThickness ? border : fill;
                    c.a = alpha;
                    pixels[y * size + x] = c;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        }

        // Exact signed distance to a regular 5-pointed star: outer vertex radius r,
        // rf = inner/outer vertex radius ratio. Positive outside, negative inside
        private static float SdStar5(Vector2 p, float r, float rf)
        {
            var k1 = new Vector2(0.809016994375f, -0.587785252292f);
            var k2 = new Vector2(-k1.x, k1.y);
            p.x = Mathf.Abs(p.x);
            p -= 2f * Mathf.Max(Vector2.Dot(k1, p), 0f) * k1;
            p -= 2f * Mathf.Max(Vector2.Dot(k2, p), 0f) * k2;
            p.x = Mathf.Abs(p.x);
            p.y -= r;
            var ba = rf * new Vector2(-k1.y, k1.x) - new Vector2(0f, 1f);
            float h = Mathf.Clamp(Vector2.Dot(p, ba) / Vector2.Dot(ba, ba), 0f, r);
            return (p - ba * h).magnitude * Mathf.Sign(p.y * ba.x - p.x * ba.y);
        }

        // Alpha-only shape matching the panel's FILL area (inset by the border
        // thickness, so its own corner radius is correspondingly smaller, nested just
        // inside the border ring), used as an invisible Mask host for the grain overlay
        internal static Sprite PanelInnerMaskSprite() => _panelInnerMaskSprite ??=
            MakeCapSprite(Mathf.Max(1f, PanelCornerRadius - PanelBorderThickness));

        // Jag is on for the panel's own outline (both where it meets the fill AND
        // where it meets outside the panel, "inward and outward") and the selected
        // row's edge, off for badges, which stay clean-edged. One shared "torn paper"
        // scale for all of them, not per-element tuning, so it reads as the same
        // material everywhere.
        //
        // The previous attempt pushed jagFreq to 5.5 with 3 fbm octaves at lacunarity
        // 2.3, meaning the octaves sampled Perlin at ~5.5, ~12.6, ~29 cycles PER
        // TEXTURE PIXEL. That's nowhere close to a resolvable signal (need well under
        // 1 cycle/pixel), so it wasn't "more frequent jags", it was noise so far past
        // the sampling limit it comes back out as near-random static, which a modest
        // amplitude just washes out to invisible, "reverted the whole mechanic" was a
        // fair read of the actual result. Kept comfortably under that limit this time,
        // and dropped to 2 octaves (so even the highest octave, freq*lacunarity, still
        // resolves cleanly) rather than 3
        // Tuned against a live HTML/JS tool with native (unstretched, exact pixels) and
        // as-displayed (stretched, matching how Unity actually renders it) previews
        // side by side, after the earlier frequency was found to be past the noise's
        // actual sampling limit (see below)
        // "Scale" here means both dial together: a LARGER-looking notch is a lower
        // frequency (fewer, bigger bumps) paired with a bigger amplitude (each one
        // actually displaces further), not just one or the other, scaling only one
        // just makes it look diluted/sparse rather than bigger
        private const float EdgeJagAmplitude = 5.0f;
        private const float EdgeJagFrequency = 1.2f;
        private const int EdgeJagOctaves = 2;
        private const float EdgeJagPersistence = 0.5f;
        private const float EdgeJagLacunarity = 2.44f;
        // NOT boosted anymore: at 2.2x on top of the shared amplitude=5.0, the row's
        // jag amplitude (11.0) was actually LARGER relative to its own radius (14) than
        // the panel's is relative to ITS radius (26), amplitude/radius ratio ~0.79 vs.
        // ~0.19, several times more aggressive. That's what was actually distorting the
        // row's corners (and, since amplitude/frequency scaled together, likely made
        // the panel's own corners worse too): the notch size was comparable to the
        // whole corner, not textured, structurally broken. 1.0 keeps the row at the
        // same ratio as the panel instead of a wildly different one
        private const float RowJagAmplitudeMultiplier = 1.0f;

        // The jagged edges gently animate while a panel is open: 3 pre-seeded variants
        // of the same shape, cycled 1-2-3-1-2-3... on a fixed interval, rather than
        // re-rolling the noise continuously (which would mean regenerating a full
        // texture every frame, the whole reason this is baked ahead of time at all
        // instead of computed in a shader). Each variant is only ever generated once
        // and then just reused for the rest of the session, swapping Image.sprite
        // between 3 already-built textures each interval is effectively free.
        // internal: HelpScreen (the F1 screen) reuses these same timing constants for
        // its own independent jag animation
        internal const int JagFrameCount = 3;
        internal const float JagFrameInterval = 0.5f;
        private static readonly float[] JagFrameSeedOffsets = { 0f, 173.2f, 401.7f };
        private int _jagFrame;
        private float _jagFrameTimer;

        // Keyed by (width, height): SavePicker and HelpScreen are almost always
        // different sizes from each other (and SavePicker's own size varies with row
        // count), a single "most recently baked size" cache (the previous approach)
        // meant switching between them invalidated and rebaked the OTHER one's frames
        // every single time, a stutter every open instead of only the first ever open
        // per size. Never evicted: at most a handful of distinct sizes ever occur in a
        // session (this panel's few row-count buckets, HelpScreen's one content size),
        // trivial memory for the lifetime of the process
        private static readonly Dictionary<(int width, int height), Sprite[]> _panelSpriteCache = new();

        // Flat/minimal variant (see PluginConfig.MinimalPickerUi): edgeJag = 0, so
        // every "frame" would bake identical anyway - a single cached sprite per size,
        // no per-frame array needed
        private static readonly Dictionary<(int width, int height), Sprite> _panelSpriteFlatCache = new();

        // Baking at the panel's EXACT current width/height (not a fixed guess) rather
        // matters here: with Type.Simple the WHOLE texture stretches as one piece, and
        // the panel's height varies a lot (from ~2 rows to MaxVisibleRows, or whatever
        // HelpScreen's content needs). Baking at a fixed guessed height and letting a
        // much shorter actual panel squeeze it non-uniformly turned the round corners
        // into visibly flattened ellipses ("corners got skinnier"). Re-baking on demand
        // (cheap to skip via the cache above whenever this exact size was already seen)
        // keeps the corner radius and the jag scale correct at whatever size is needed
        internal static Sprite PanelSprite(int width, int height, int frame, bool minimal)
        {
            if (minimal)
            {
                var flatKey = (width, height);
                if (!_panelSpriteFlatCache.TryGetValue(flatKey, out Sprite flat))
                {
                    flat = MakeFullPanelSprite(width, height, PanelCornerRadius, PanelBorderThickness,
                        PanelFillColor, PanelBorderColor, 0f, EdgeJagFrequency, 0f);
                    _panelSpriteFlatCache[flatKey] = flat;
                }
                return flat;
            }

            var key = (width, height);
            if (!_panelSpriteCache.TryGetValue(key, out Sprite[] frames))
            {
                frames = new Sprite[JagFrameCount];
                _panelSpriteCache[key] = frames;
            }

            if (frames[frame] == null)
            {
                frames[frame] = MakeFullPanelSprite(width, height, PanelCornerRadius, PanelBorderThickness,
                    PanelFillColor, PanelBorderColor, EdgeJagAmplitude, EdgeJagFrequency, JagFrameSeedOffsets[frame]);
            }
            return frames[frame];
        }

        // Same shape/jag math as MakeRoundedSprite, but bakes the WHOLE width x height
        // shape directly with no 9-slice border metadata (meant for Image.Type.Simple):
        // 9-slicing stretches the straight-edge strips along their long axis to fill
        // the shape, which is exactly what was diluting the jag on the panel's own
        // straight edges down to nothing, only the (correctly unstretched) corners
        // ever showed it clearly. seedOffset shifts the noise sample so each animation
        // frame is a distinctly different (but same-looking-style) shape
        private static Sprite MakeFullPanelSprite(int width, int height, float radius, float borderThickness,
            Color fill, Color border, float edgeJag, float jagFreq, float seedOffset)
        {
            var tex = new Texture2D(width, height, TextureFormat.ARGB32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            // SetPixels32 + one Apply(), not SetPixel() in the loop: SetPixel's
            // per-call overhead (bounds/format checks on every single pixel) dominates
            // at these resolutions, building the array in managed memory first and
            // uploading it once is the standard several-times-over speedup for
            // procedural textures this size. Same change applied to every generator
            // below, it's the main lever for the open-picker stutter
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float jagOuter = edgeJag > 0f ? (Fbm(fx * jagFreq + 11.3f + seedOffset, fy * jagFreq + 11.3f + seedOffset, EdgeJagOctaves, EdgeJagPersistence, EdgeJagLacunarity) - 0.5f) * edgeJag : 0f;
                    float jagInner = edgeJag > 0f ? (Fbm(fx * jagFreq + 77.1f + seedOffset, fy * jagFreq + 41.9f + seedOffset, EdgeJagOctaves, EdgeJagPersistence, EdgeJagLacunarity) - 0.5f) * edgeJag : 0f;
                    float cx = Mathf.Clamp(fx, radius, width - radius);
                    float cy = Mathf.Clamp(fy, radius, height - radius);
                    float dist = Mathf.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
                    float shapeAlpha = Mathf.Clamp01(radius - dist + jagOuter + 0.5f);
                    float insideDist = radius - dist;
                    float fillT = Mathf.Clamp01(insideDist - borderThickness + jagInner + 0.5f);
                    Color c = Color.Lerp(border, fill, fillT);
                    c.a = shapeAlpha;
                    pixels[y * width + x] = c;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        // A 9-sliceable rounded-rect texture with a solid border baked straight into
        // its pixels (not a tint, so a border color and a DIFFERENT fill color can
        // coexist in one Image without a second GameObject behind it). Reused for both
        // the main panel and the footer key badges so they share one consistent "rounded
        // + outlined" style, just at different scales
        //
        // edgeJag (texture pixels) roughens BOTH transitions with noise instead of a
        // perfectly smooth arc/line: the outer silhouette (where the shape meets
        // whatever's behind it) and the inner one (where the border meets the fill),
        // each sampled with its own noise offset so the two edges don't wobble in
        // lockstep, a "crumpled paper" look rather than a clean vector outline
        private static Sprite MakeRoundedSprite(int size, float radius, float borderThickness, Color fill, Color border,
            float edgeJag = 0f, float jagFreq = 0.4f)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    // fbm (multi-octave), not a single Perlin sample: one smooth
                    // octave only ever gives slow, rounded "dents in metal" undulation.
                    // Stacking finer octaves on top is what breaks the edge up into the
                    // small, irregular, sharp-ish notches an actual torn/crumpled paper
                    // edge has
                    float jagOuter = edgeJag > 0f ? (Fbm(fx * jagFreq + 11.3f, fy * jagFreq + 11.3f, EdgeJagOctaves, EdgeJagPersistence, EdgeJagLacunarity) - 0.5f) * edgeJag : 0f;
                    float jagInner = edgeJag > 0f ? (Fbm(fx * jagFreq + 77.1f, fy * jagFreq + 41.9f, EdgeJagOctaves, EdgeJagPersistence, EdgeJagLacunarity) - 0.5f) * edgeJag : 0f;
                    float cx = Mathf.Clamp(fx, radius, size - radius);
                    float cy = Mathf.Clamp(fy, radius, size - radius);
                    float dist = Mathf.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
                    float shapeAlpha = Mathf.Clamp01(radius - dist + jagOuter + 0.5f); // ~1px soft edge AA
                    float insideDist = radius - dist; // how far inside the rounded boundary
                    float fillT = Mathf.Clamp01(insideDist - borderThickness + jagInner + 0.5f); // ~1px border/fill blend
                    Color c = Color.Lerp(border, fill, fillT);
                    c.a = shapeAlpha;
                    pixels[y * size + x] = c;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();

            var b = new Vector4(radius, radius, radius, radius);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, b);
        }

        // ONLY the selected row ever gets the jagged edge: striped rows go back to a
        // plain, clean-cornered, no-jag 9-sliced sprite (RowCapSprite, below), a single
        // cheap cached shape reused for however many striped rows are visible. Jag on
        // every row wasn't worth what it cost (barely noticeable at that scale, and up
        // to MaxVisibleRows extra full-size texture bakes the first time the picker
        // opens), and it's not needed for correctness either: this one shared sprite
        // for the selection works at ANY pool slot/scroll position already, since
        // which row gets it is decided by `sel` (entryIndex == _selected) each
        // rebuild, not by which GameObject/position it happens to render at, a Sprite
        // asset has no notion of "where" it's currently displayed
        private static Sprite RowCapSprite() => _rowCapSprite ??= MakeCapSprite(RowCapRadius);

        // Baked at roughly the row's own wide/short proportions (not square like
        // MakeCapSprite's shapes), close to PanelWidth/RowHeight so the actual row
        // rarely needs to stretch this by more than a small amount, keeping the corner
        // radius from visibly distorting into an ellipse and the jag frequency close
        // to what it actually looks like baked. Used with Image.Type.Simple, see the
        // comment where this is assigned for why: a 9-sliced texture can't show jag on
        // the long straight edges at all, only Simple (the whole texture, stretched as
        // one piece, no separately-stretched edge strips) keeps it visible everywhere
        private const int RowSelSpriteWidth = 900;
        private const int RowSelSpriteHeight = 44;
        private static readonly Sprite[] _rowCapSelSpriteFrames = new Sprite[JagFrameCount];

        private static Sprite RowCapSelSprite(int frame)
        {
            if (_rowCapSelSpriteFrames[frame] == null)
            {
                float seedOffset = JagFrameSeedOffsets[frame];
                _rowCapSelSpriteFrames[frame] = MakeFullCapSpriteWithGrain(RowSelSpriteWidth, RowSelSpriteHeight, RowCapRadius,
                    EdgeJagAmplitude * RowJagAmplitudeMultiplier, EdgeJagFrequency,
                    23.7f + seedOffset, 58.4f + seedOffset, RowSelBarColor);
            }
            return _rowCapSelSpriteFrames[frame];
        }

        // Flat/minimal variant (see PluginConfig.MinimalPickerUi): rather than baking a
        // whole separate grain-free/jag-free texture, this just reuses the same plain
        // clean-cornered, no-jag cap sprite the zebra-striped rows already use
        // (RowCapSprite), 9-sliced and tinted with the selection's own color - the exact
        // same "flat" treatment already applied to every other row
        private void ApplySelOverlaySprite(bool minimal)
        {
            if (_selOverlayImage == null) return;
            if (minimal)
            {
                _selOverlayImage.sprite = RowCapSprite();
                _selOverlayImage.type = Image.Type.Sliced;
                _selOverlayImage.color = RowSelBarColor;
            }
            else
            {
                _selOverlayImage.sprite = RowCapSelSprite(_jagFrame);
                _selOverlayImage.type = Image.Type.Simple;
                _selOverlayImage.color = Color.white;
            }
        }

        // Bakes the WHOLE width x height shape directly (no 9-slice border metadata,
        // meant for Image.Type.Simple) so edge detail like the jag noise survives
        // being scaled as a single piece. The grain is baked straight into this same
        // texture's RGB (same envelope/SmoothStepEdge technique as
        // GenerateGrainTexture, just reusing the SAME first-pass min/max-normalized
        // envelope this method already needs for the alpha shape's own edge jag would
        // be a further optimization, not done here since this only ever runs once and
        // is cached), rather than a separate grain Image clipped by a Mask, so this
        // needs no Mask at all, see the comment on _selOverlay for why
        private static Sprite MakeFullCapSpriteWithGrain(int width, int height, float radius, float edgeJag, float jagFreq,
            float phaseX, float phaseY, Color baseColor)
        {
            var tex = new Texture2D(width, height, TextureFormat.ARGB32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            Color dark = new Color(
                Mathf.Clamp01(baseColor.r * GrainDarkMul), Mathf.Clamp01(baseColor.g * GrainDarkMul), Mathf.Clamp01(baseColor.b * GrainDarkMul));
            Color light = new Color(
                Mathf.Clamp01(baseColor.r * GrainLightMul), Mathf.Clamp01(baseColor.g * GrainLightMul), Mathf.Clamp01(baseColor.b * GrainLightMul));

            // First pass: grain envelope min/max, same reasoning as GenerateGrainTexture.
            // Flat array, not float[,]: a 2D array's per-access bounds/stride math is
            // meaningfully slower than plain index arithmetic at this pixel count
            var envelopes = new float[width * height];
            float minEnvelope = float.MaxValue, maxEnvelope = float.MinValue;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float nx = x / (float)width, ny = y / (float)height;
                    float envelope = Fbm(nx * GrainEnvelopeFreq + GrainSeed * 0.001f, ny * GrainEnvelopeFreq + GrainSeed * 0.001f,
                        GrainOctaves, GrainPersistence, GrainLacunarity);
                    envelopes[y * width + x] = envelope;
                    if (envelope < minEnvelope) minEnvelope = envelope;
                    if (envelope > maxEnvelope) maxEnvelope = envelope;
                }
            }
            float envelopeRange = Mathf.Max(0.0001f, maxEnvelope - minEnvelope);

            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float jag = edgeJag > 0f ? (Fbm(fx * jagFreq + phaseX, fy * jagFreq + phaseY, EdgeJagOctaves, EdgeJagPersistence, EdgeJagLacunarity) - 0.5f) * edgeJag : 0f;
                    float cx = Mathf.Clamp(fx, radius, width - radius);
                    float cy = Mathf.Clamp(fy, radius, height - radius);
                    float dist = Mathf.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
                    float alpha = Mathf.Clamp01(radius - dist + jag + 0.5f);

                    float normalized = (envelopes[y * width + x] - minEnvelope) / envelopeRange;
                    float n = SmoothStepEdge(GrainSharpenMin, GrainSharpenMax, normalized);
                    Color c = Color.Lerp(dark, light, n);
                    c.a = alpha;
                    pixels[y * width + x] = c;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        // An alpha-only rounded-corner mask (plain white, tinted per row via Image.color
        // for zebra/selection), all 4 corners always rounded. Every filled row now gets
        // this regardless of its position in the list, they read as individual rounded
        // chips rather than one continuous strip with edge-only rounding
        private static Sprite MakeCapSprite(float radius, float edgeJag = 0f, float jagFreq = 0.4f)
        {
            const int size = 48;
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float jag = edgeJag > 0f ? (Fbm(fx * jagFreq + 23.7f, fy * jagFreq + 58.4f, EdgeJagOctaves, EdgeJagPersistence, EdgeJagLacunarity) - 0.5f) * edgeJag : 0f;
                    float cx = Mathf.Clamp(fx, radius, size - radius);
                    float cy = Mathf.Clamp(fy, radius, size - radius);
                    float dist = Mathf.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
                    float alpha = Mathf.Clamp01(radius - dist + jag + 0.5f);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();

            var b = new Vector4(radius, radius, radius, radius);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, b);
        }

        // A tileable grain texture, baked OPAQUE as lighter/darker variants of the
        // panel's OWN color (not a neutral gray blended on top): a gray overlay, even
        // one matched to the panel's brightness, still desaturates it on every blend,
        // there's no alpha value where "a bit of neutral gray" doesn't mute the blue.
        // Varying the panel's own hue up/down and drawing it fully opaque sidesteps
        // blending entirely, average color is exactly the panel's own, no shift, while
        // still being clearly visible since the light/dark swing isn't diluted by alpha
        // Stretched (not tiled) over the panel, so this directly controls grain
        // fineness: the panel is roughly PanelWidth px wide, so this many texels
        // across works out to a few screen pixels per fleck
        // Higher resolution than earlier attempts: stretched (Type.Simple) over an
        // ~860px-wide panel, this keeps the magnification low enough that the fine
        // grain stays crisp-ish rather than blurring into soft blobs
        internal const int GrainTextureSize = 368;

        // Tuned interactively against a live HTML/JS port of this exact algorithm
        // (sliders for every constant below, side by side with the boarding pass
        // reference image) rather than by round-tripping full game builds
        private const float GrainSeed = 1337f;
        private const float GrainEnvelopeFreq = 14.0f;
        private const int GrainOctaves = 6;
        private const float GrainPersistence = 0.76f;
        private const float GrainLacunarity = 2.98f;
        // Min > Max is intentional: SmoothStepEdge's denominator gets clamped to a
        // tiny epsilon in that case, collapsing the transition into a near-hard
        // binary cutoff rather than a gradient, that's what "sharp" edges needed
        private const float GrainSharpenMin = 0.61f;
        private const float GrainSharpenMax = 0.00f;
        private const float GrainLightMul = 1.03f;
        private const float GrainDarkMul = 1.00f;

        internal static Texture2D PanelGrainTexture() =>
            _grainTexturePanel != null ? _grainTexturePanel
                : (_grainTexturePanel = GenerateGrainTexture(PanelFillColor, GrainTextureSize, GrainTextureSize));

        private static Texture2D GenerateGrainTexture(Color baseColor, int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false)
            {
                wrapMode = TextureWrapMode.Clamp, // not tiled, no need to repeat
                filterMode = FilterMode.Bilinear,
            };
            Color dark = new Color(
                Mathf.Clamp01(baseColor.r * GrainDarkMul),
                Mathf.Clamp01(baseColor.g * GrainDarkMul),
                Mathf.Clamp01(baseColor.b * GrainDarkMul));
            Color light = new Color(
                Mathf.Clamp01(baseColor.r * GrainLightMul),
                Mathf.Clamp01(baseColor.g * GrainLightMul),
                Mathf.Clamp01(baseColor.b * GrainLightMul));

            // The fractal noise field itself IS the cloud shape (not a per-pixel
            // jitter modulator, that read as TV static no matter how it was tuned):
            // SmoothStep pins most of it flat to one of the two tones above, a solid-
            // colored blob interior like the reference, with only a narrow band right
            // at each blob's edge actually in transition. Stacking several octaves
            // (fbm) is what makes that edge jagged/irregular rather than a smooth
            // round arc, a single Perlin layer can only ever produce round blobs
            //
            // First pass: compute every pixel's envelope value and track its actual
            // min/max. Unity's Mathf.PerlinNoise and the JS Perlin implementation used
            // to tune GrainSharpenMin/Max against a live preview don't necessarily
            // produce numerically identical output ranges for the same octave/
            // frequency settings, especially after stacking several octaves, small
            // per-octave differences compound. A fixed 0..1 assumption can land the
            // real range entirely outside that narrow sharpen band, giving a
            // perfectly flat, texture-less result (SmoothStepEdge saturates to a
            // constant 0 or 1 everywhere), which happened on an earlier attempt.
            // Normalizing against the ACTUAL observed range keeps the thresholds
            // meaningful regardless of those implementation details
            var envelopes = new float[width * height];
            float minEnvelope = float.MaxValue, maxEnvelope = float.MinValue;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float nx = x / (float)width;
                    float ny = y / (float)height;
                    float ox = nx * GrainEnvelopeFreq + GrainSeed * 0.001f;
                    float oy = ny * GrainEnvelopeFreq + GrainSeed * 0.001f;
                    float envelope = Fbm(ox, oy, GrainOctaves, GrainPersistence, GrainLacunarity);
                    envelopes[y * width + x] = envelope;
                    if (envelope < minEnvelope) minEnvelope = envelope;
                    if (envelope > maxEnvelope) maxEnvelope = envelope;
                }
            }

            float envelopeRange = Mathf.Max(0.0001f, maxEnvelope - minEnvelope);
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float normalized = (envelopes[y * width + x] - minEnvelope) / envelopeRange;
                    float n = SmoothStepEdge(GrainSharpenMin, GrainSharpenMax, normalized);
                    pixels[y * width + x] = Color.Lerp(dark, light, n);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        // Stacks several octaves of Perlin noise at rising frequency and falling
        // amplitude (fractal Brownian motion). See GenerateGrainTexture() for why this
        // matters: it's what gives the cloud shapes irregular, organic edges
        private static float Fbm(float x, float y, int octaves, float persistence, float lacunarity)
        {
            float total = 0f, amplitude = 1f, frequency = 1f, max = 0f;
            for (int i = 0; i < octaves; i++)
            {
                total += Mathf.PerlinNoise(x * frequency, y * frequency) * amplitude;
                max += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }
            return total / max;
        }

        // The classic GLSL smoothstep(edge0, edge1, x): clamps x between the two
        // edges and returns a smoothed 0..1 based on where it fell. NOT the same as
        // Mathf.SmoothStep(from, to, t), which interpolates BETWEEN from/to using a
        // smoothed t and never actually thresholds x against them at all, using that
        // here (an earlier mistake) meant every "sharpen" attempt was really just
        // blending between two nearly-identical constants, not thresholding anything
        private static float SmoothStepEdge(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / Mathf.Max(0.0001f, edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        // Cheap refresh for just the warn line (delete-confirm armed/expired, or a
        // transient message like "unstar to delete"), no full rebuild. Delete-confirm
        // takes priority if somehow both are active at once (shouldn't happen, they're
        // set by mutually-exclusive branches of OnDeletePressed)
        private void RefreshWarn()
        {
            if (_warnText == null) return;
            bool showDelete = _pendingDeleteIndex >= 0 && _pendingDeleteIndex == _selected;
            bool showTransient = !showDelete && _transientWarnText != null;
            _warnText.gameObject.SetActive(showDelete || showTransient);
            if (showDelete) _warnText.text = SavePickerLocalization.Get(PickerText.DeleteConfirm);
            else if (showTransient) _warnText.text = _transientWarnText;
        }

        // Built ONCE as the first child of rowsContainer (so it always renders behind
        // every pooled row, including that row's own text on top of it), then just
        // repositioned/toggled each rebuild to sit over whichever row is selected. See
        // the field comment on _selOverlay for why this replaced per-row Mask toggling
        private void BuildSelectionOverlay(Transform rowsContainer)
        {
            _selOverlay = new GameObject("SelectionOverlay", typeof(RectTransform));
            _selOverlay.transform.SetParent(rowsContainer, false);
            _selOverlayRect = (RectTransform)_selOverlay.transform;
            _selOverlayRect.anchorMin = new Vector2(0f, 1f);
            _selOverlayRect.anchorMax = new Vector2(1f, 1f);
            _selOverlayRect.pivot = new Vector2(0.5f, 1f);
            _selOverlayRect.sizeDelta = new Vector2(0f, RowHeight);
            _selOverlayRect.offsetMin = new Vector2(0f, _selOverlayRect.offsetMin.y);
            _selOverlayRect.offsetMax = new Vector2(0f, _selOverlayRect.offsetMax.y);

            // ONE Image, ONE sprite that bakes fill+grain+jag together (see
            // RowCapSelSprite/MakeFullCapSpriteWithGrain), no Mask and no separate
            // grain child at all. A Mask here (tried twice: once per-row, once on this
            // same shared overlay) is what was actually causing "only ever shows
            // correctly at row 0": this object gets SetActive(false)/(true) and
            // repositioned every time selection moves, and Unity's stencil-buffer
            // bookkeeping for a Mask component that's toggled and moved like that
            // doesn't reliably clean up, leaving a stale clip footprint behind at
            // wherever it was FIRST positioned (row 0, since that's the default
            // selection on open) that then bled into whatever rendered there after.
            // Baking the grain directly into the sprite's own texture needs no
            // clipping at all, so there's no Mask left to misbehave
            _selOverlayImage = _selOverlay.AddComponent<Image>();
            ApplySelOverlaySprite(MinimalUi);

            _selOverlay.SetActive(false);
        }

        private void EnsureRowPool(int count)
        {
            while (_rowDiffPool.Count < count)
            {
                int i = _rowDiffPool.Count;
                var rowGo = new GameObject("Row" + i, typeof(RectTransform));
                rowGo.transform.SetParent(_rowsContainer, false);
                var rowRect = (RectTransform)rowGo.transform;
                rowRect.anchorMin = new Vector2(0f, 1f);
                rowRect.anchorMax = new Vector2(1f, 1f);
                rowRect.pivot = new Vector2(0.5f, 1f);
                rowRect.sizeDelta = new Vector2(0f, RowHeight);
                rowRect.anchoredPosition = new Vector2(0f, -(i * RowHeight));

                // No Mask/grain here anymore, that's the shared _selOverlay's job now,
                // this Image is only ever plain-striped or fully transparent
                var hl = rowGo.AddComponent<Image>();
                hl.color = Color.clear;

                // 4 "columns" (difficulty / biome / date / playtime), each its own TMP
                // field so they can be independently x-positioned. All stretch full
                // height and to the row's right inset; only offsetMin.x (the left start)
                // differs, and is set per-rebuild by RebuildUi/ComputeColumnLayout.
                // There's no per-field Mask, so a generous rect never clips anything -
                // only the shared rowsContainer's RectMask2D can ever clip text
                var diff = MakeText(rowGo.transform, "ColDiff", 21, FontStyles.Normal, RowColor, TextAlignmentOptions.MidlineLeft);
                StretchColumn(diff, RowTextInset);

                var mid = MakeText(rowGo.transform, "ColMid", 21, FontStyles.Normal, RowColor, TextAlignmentOptions.MidlineLeft);
                StretchColumn(mid, RowTextInset);

                var date = MakeText(rowGo.transform, "ColDate", 21, FontStyles.Normal, RowColor, TextAlignmentOptions.MidlineLeft);
                StretchColumn(date, RowTextInset);

                // Last column: right-aligned, but inset further than the others by
                // RowStarReserve so it never sits where the star icon goes - reserved on
                // EVERY row (not just starred ones) so the right edge stays consistent
                // instead of text shifting over only when a row happens to be starred
                var play = MakeText(rowGo.transform, "ColPlay", 21, FontStyles.Normal, RowColor, TextAlignmentOptions.MidlineRight);
                StretchColumn(play, RowTextInset, RowTextInset + RowStarReserve);

                var starGo = new GameObject("Star", typeof(RectTransform));
                starGo.transform.SetParent(rowGo.transform, false);
                var starImage = starGo.AddComponent<Image>();
                starImage.sprite = StarSprite();
                starImage.raycastTarget = false;
                var starRect = (RectTransform)starGo.transform;
                starRect.anchorMin = starRect.anchorMax = new Vector2(1f, 0.5f);
                starRect.pivot = new Vector2(1f, 0.5f);
                starRect.sizeDelta = new Vector2(RowStarIconSize, RowStarIconSize);
                starRect.anchoredPosition = new Vector2(-10f, 0f);
                starGo.SetActive(false);

                _rowHighlightPool.Add(hl);
                _rowDiffPool.Add(diff);
                _rowMidPool.Add(mid);
                _rowDatePool.Add(date);
                _rowPlayPool.Add(play);
                _rowStarPool.Add(starImage);
            }
        }

        private static void StretchColumn(TextMeshProUGUI text, float leftInset, float rightInset = RowTextInset)
        {
            var rect = (RectTransform)text.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(leftInset, 0f);
            rect.offsetMax = new Vector2(-rightInset, 0f);
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
            tmp.richText = true;
            tmp.alignment = alignment;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            return tmp;
        }

        internal static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // Same font-lookup trick the checkpoint mod uses for its own loading screen: use
        // whichever of the game's own TMP fonts is already loaded, so our panel reads as
        // part of the game's own UI rather than a generic debug overlay. No custom font
        // is bundled; if none of these are found (a very different game build), TMP's own
        // default font asset is used instead of throwing
        //
        // Same preference order as the checkpoint mod's own loading screen, so this
        // panel actually matches the game's own signature font rather than a generic
        // system sans. Legibility at this font's Regular weight (there's no real Bold
        // face loaded for it, so we never ask TMP to fake one, see FontStyles usage
        // above) is fine once the text is sized and spaced generously, which is why
        // this panel runs noticeably larger type than a typical debug overlay would
        private static readonly string[] PreferredFontNames =
        {
            "DarumaDropOne-Regular SDF", "Pangolin-Regular SDF", "Montserrat-Medium SDF", "LiberationSans SDF",
        };

        internal static TMP_FontAsset FindGameFont()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                foreach (string name in PreferredFontNames)
                    foreach (var f in all)
                        if (f != null && f.name == name) return f;
                return all.Length > 0 ? all[0] : null;
            }
            catch { return null; }
        }

        private static Material _chromeOutlineMaterial;

        // Borrows the game's own pre-baked outline+shadow TMP material (whichever
        // already-instantiated native UI text happens to be using it) instead of
        // hand-tuning our own outline/underlay values, same "reuse the game's own art"
        // approach FindGameFont()/FindCampfireIcon() already use. Not cached as
        // "not found" if the search comes up empty (retried on demand, like
        // FindCampfireIcon), since the native UI may not have created any instances of
        // it yet the first time this is called (e.g. right after a level loads)
        //
        // Deliberately used ONLY for this panel's own chrome labels (loading text,
        // the title, and the footer's action-description labels) - never for the
        // archived-save row text, help-screen body text, or the key badge text
        // itself, which all keep their original flat (no outline) style
        internal static Material FindChromeOutlineMaterial()
        {
            if (_chromeOutlineMaterial != null) return _chromeOutlineMaterial;
            try
            {
                var texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
                foreach (var t in texts)
                {
                    var mat = t != null ? t.materialForRendering : null;
                    if (mat != null && mat.name.Contains("DarumaDropOne-Regular SDF Outline"))
                    {
                        _chromeOutlineMaterial = mat;
                        break;
                    }
                }
            }
            catch { /* non-fatal: labels just render without the outline/shadow this open */ }
            return _chromeOutlineMaterial;
        }

        // Applies the borrowed outline+shadow material to one of this panel's chrome
        // labels (see FindChromeOutlineMaterial), a no-op if the material hasn't been
        // found yet this session
        internal static void ApplyChromeTextStyle(TextMeshProUGUI tmp)
        {
            var mat = FindChromeOutlineMaterial();
            if (mat != null && tmp != null) tmp.fontSharedMaterial = mat;
        }
    }
}
