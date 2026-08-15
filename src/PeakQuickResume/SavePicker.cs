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
    /// current network category, newest first. Arrow keys move the highlight, Delete removes
    /// a save (two-step), Escape closes. Rendered as a real UGUI Canvas (built once, lazily,
    /// then toggled/updated) rather than IMGUI, for a closer game-native look and cheaper
    /// per-frame cost. Shows at most <see cref="MaxVisibleRows"/> rows, scrolling to keep the
    /// selection in view.
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

        // A one-off transient warning (e.g. "unstar to delete"), shown in the same slot as the delete-confirm prompt.
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
        /// Opens the picker for the given category. If <paramref name="preferred"/> is set,
        /// selects the newest save of that difficulty; otherwise the newest overall. Returns
        /// false without opening if there are no saves for this category.
        /// </summary>
        public bool Open(bool offline, SaveTarget? preferred)
        {
            _offline = offline;
            _entries = SaveArchive.List(offline, _log);
            if (_entries.Count == 0)
            {
                _log.Trace($"[picker] No {(offline ? "offline" : "coop")} saves to show.");
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

            // First open this session: building the real menu is heavy enough to hitch, so
            // show a cheap loading indicator and build the real menu a frame later.
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

            _log.Trace($"[picker] Opened with {_entries.Count} {(offline ? "offline" : "coop")} save(s); selected #{_selected}.");
            return true;
        }

        // Delayed one frame so the loading text gets a chance to render before the heavy build.
        private IEnumerator WarmUpThenShow()
        {
            yield return null;
            // skipDimFade: the loading indicator's dim is already opaque and swaps out in the
            // same frame, so fading from 0 here would flash the dim away and back.
            ShowRealMenu(skipDimFade: true);
            _loadingRoot?.SetActive(false);
            _uiWarmedUp = true;
            _warmingUp = false;
        }

        private void ShowRealMenu(bool skipDimFade)
        {
            EnsureUi();
            ScrollToSelection();
            // Activate before rebuilding: Unity can't measure TMP text/layout on an inactive hierarchy.
            _root?.SetActive(true);
            RebuildUi();
            if (skipDimFade)
            {
                if (_dimImage != null) _dimImage.color = DimColor;
                _dimFadeElapsed = DimFadeDuration;
                return;
            }
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
            _log.Trace("[picker] Closed.");
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

            // Navigation (the resume key + Enter load live in Plugin). Shift jumps by JumpStep instead of 1.
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

            // Jagged-edge animation: cycles through 3 pre-built cached sprites. Skipped in
            // minimal mode (PluginConfig.MinimalPickerUi), whose flat sprites never change.
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

            // Starred saves must be unstarred first (see OnStarPressed); show a transient
            // warning instead of arming the delete confirm, which would just no-op.
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

        // Toggles the highlighted save's starred state and re-sorts in place, avoiding a redundant disk re-scan.
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
            // Past 10h, drop the minutes so this column's width stays bounded (see ComputeColumnLayout).
            if (t.TotalHours >= 10) return $"{(int)t.TotalHours}h {played}";
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m {played}" : $"{t.Minutes}m {played}";
        }

        // --- UGUI rendering ---

        private const int MaxVisibleRows = 10;
        private const float RowHeight = 40f;
        internal const float PanelPadding = 20f; // vertical margin; internal so HelpScreen matches this panel's proportions
        // Wider than PanelPadding: at PanelBorderThickness=11px the old margin left the
        // selected row's edge nearly touching the border.
        internal const float PanelPaddingHorizontal = 30f;
        internal const float TitleHeight = 42f;
        private const float ScrollHintHeight = 18f;
        private const float ScrollHintGap = 4f;
        private const float WarnHeight = 24f;
        internal const float FooterHeight = 34f;
        internal const float PanelWidth = 900f;

        // Row columns (difficulty/biome/date/playtime) are lined up at fixed x-positions
        // computed from the widest value across every archived save, so alignment doesn't
        // jitter while scrolling. If the 4-column layout doesn't fit, biome+date collapse
        // into one packed field (see ComputeColumnLayout).
        private const float RowTextInset = 10f;
        private const float RowColumnGap = 24f;
        private const string RowPackedMidSeparator = "   ";
        // Reserved on the right of every row so the last column doesn't shift when a row is starred.
        private const float RowStarReserve = RowStarIconSize + 10f;

        // Native widescreen support: the canvas is scaled to a constant reference height
        // (see CanvasScaler, matchWidthOrHeight = 1f) so only available width changes with aspect.
        internal const float ReferenceHeight = 1080f;
        internal static float CanvasWidthUnits => (float)Screen.width / Screen.height * ReferenceHeight;

        // Palette pulled from the game's own UI (boarding pass / map rotation panels).
        // internal: HelpScreen reuses this exact palette.
        internal static readonly Color DimColor = new Color(0f, 0f, 0f, 0.78f);
        internal static readonly Color PanelFillColor = new Color(0x34 / 255f, 0x54 / 255f, 0xD1 / 255f); // #3454D1
        internal static readonly Color PanelBorderColor = new Color(0x21 / 255f, 0x31 / 255f, 0x7E / 255f); // #21317E
        internal static readonly Color BadgeBorderColor = new Color(0x0A / 255f, 0x0D / 255f, 0x1A / 255f); // #0A0D1A
        internal static readonly Color TitleColor = new Color(0.98f, 0.99f, 1f);
        private static readonly Color RowColor = new Color(0.93f, 0.95f, 1f);
        private static readonly Color RowStripeColor = new Color(0f, 0f, 0f, 0.14f); // zebra striping
        private static readonly Color RowSelBarColor = new Color(1f, 0.82f, 0.22f, 0.97f); // solid bar, not a tint
        private static readonly Color RowSelTextColor = new Color(0.16f, 0.12f, 0.03f);
        internal static readonly Color FooterColor = new Color(0.85f, 0.9f, 1f);
        private static readonly Color WarnColor = new Color(1f, 0.6f, 0.55f);
        private static readonly Color ScrollHintColor = new Color(0.8f, 0.87f, 1f);
        internal static readonly Color KeyChipFillColor = new Color(0.10f, 0.16f, 0.44f);
        internal static readonly Color KeyTextColor = new Color(1f, 0.95f, 0.72f);
        private static readonly Color StarFillColor = new Color(0.97f, 0.62f, 0.10f); // richer amber than RowSelBarColor

        // One badge per footer hint: a rounded-rect Image (not a TMP <mark> tag, which
        // can't do rounded corners) behind a centered key label, then a plain-text label.
        private class FooterEntry
        {
            public TextMeshProUGUI KeyText;
            public TextMeshProUGUI LabelText;
        }

        internal const float PanelCornerRadius = 26f;
        // Was 7f; the extra thickness grows outward (PanelOuterMargin) rather than eating into content.
        internal const float PanelBorderThickness = 11f;
        internal const float PanelOuterMargin = PanelBorderThickness - 7f;
        private const float RowCapRadius = 14f;
        private const float RowStarIconSize = 22f;
        private const float RowSelOverflow = 8f; // selected row's bar sticks out this much past normal width

        private GameObject _root;
        private Image _dimImage;
        private float _dimFadeElapsed;
        private const float DimFadeDuration = 0.25f;

        // Shown instead of the real menu only once per session, while it's being built.
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
        // Row text is split into per-field pools so columns stay anchored; see ComputeColumnLayout/RebuildUi.
        private readonly List<TextMeshProUGUI> _rowDiffPool = new List<TextMeshProUGUI>();
        private readonly List<TextMeshProUGUI> _rowMidPool = new List<TextMeshProUGUI>();
        private readonly List<TextMeshProUGUI> _rowDatePool = new List<TextMeshProUGUI>();
        private readonly List<TextMeshProUGUI> _rowPlayPool = new List<TextMeshProUGUI>();
        private readonly List<Image> _rowStarPool = new List<Image>();
        // The selected row's look is drawn by one dedicated overlay repositioned on
        // selection change (see MakeFullCapSpriteWithGrain), not a per-row Mask: a Mask
        // toggled/repositioned per selection was found to only render correctly on row 0,
        // Unity's stencil-buffer bookkeeping doesn't reliably clean up.
        private GameObject _selOverlay;
        private RectTransform _selOverlayRect;
        private Image _selOverlayImage;
        private TMP_FontAsset _font;
        private static Sprite _panelInnerMaskSprite;
        private static Sprite _badgeSprite;
        private static Sprite _rowCapSprite;
        private static Texture2D _grainTexturePanel;

        // Pins the canvas to a constant 1080 reference-pixel height (see ReferenceHeight)
        // instead of the default width/height blend, which on ultrawide monitors would
        // shrink the canvas below the panel's own height and clip rows off-screen.
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
                // Rounded corners + outline baked into the sprite itself. Type.Simple, not
                // Sliced: 9-slicing stretches the straight edges and dilutes the jag effect
                // there (same bug already fixed for the selected row). Sprite assigned in
                // RebuildUi once the panel's actual size is known (see PanelSprite).
                _panelFillImage.type = Image.Type.Simple;
                _panelFillImage.color = Color.white;
                _panelRect = (RectTransform)panelGo.transform;
                _panelRect.anchorMin = _panelRect.anchorMax = new Vector2(0.5f, 0.5f);
                _panelRect.pivot = new Vector2(0.5f, 0.5f);

                // Separate invisible masking child inset by the border thickness. Putting
                // Mask directly on the panel's own Image (tried previously) swapped it onto
                // a stencil-only material and stopped the border from rendering.
                var maskGo = new GameObject("GrainMask", typeof(RectTransform));
                maskGo.transform.SetParent(panelGo.transform, false);
                var maskImage = maskGo.AddComponent<Image>();
                maskImage.sprite = PanelInnerMaskSprite();
                maskImage.type = Image.Type.Sliced;
                // Full-alpha white + showMaskGraphic=false: a Color.clear tint (tried first)
                // zeroed the stencil coverage entirely instead of just hiding the graphic.
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
                // Type.Simple, not Tiled: Tiled's on-screen tile size depends on the
                // canvas's effective PPU scale, which proved unpredictable to guess.
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
            // CampfireName is the deepest campfire/segment reached, not BiomesSummary; see SaveArchive.CampfireLabel.
            string biome = string.IsNullOrEmpty(e.CampfireName) ? "—" : SaveArchive.CampfireLabel(e.CampfireName);
            string date = string.IsNullOrEmpty(e.SaveDate) ? e.SortTime.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : e.SaveDate;
            // A stale save swaps the playtime text for its game version, reusing this slot
            // rather than adding a column/icon that could overflow the row layout.
            string playtime = e.IsStaleVersion
                ? (string.IsNullOrEmpty(e.DisplayGameVersion) ? GameVersionCompat.NoVersionDisplay : GameVersionCompat.Display(e.DisplayGameVersion))
                : FormatPlaytime(e.Playtime);
            // Co-op: tack the player list onto the last column since it's optional/co-op-only.
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

        // Decides column start x-positions, measured across every archived save (not just
        // the visible page) so they stay put while scrolling. Biome+date collapse into one
        // packed field if the full 4-column layout would overflow the row.
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
                // Width against the scaled canvas width (CanvasWidthUnits), not raw
                // Screen.width, since the widescreen scaler breaks 1:1 pixel measurement.
                float w = Mathf.Min(PanelWidth, CanvasWidthUnits - 80f) + 2f * PanelOuterMargin;
                float chrome = PanelPadding * 2f + TitleHeight + FooterHeight + WarnHeight
                    + 2f * ScrollHintHeight + 4f * ScrollHintGap;
                float h = Mathf.Min(chrome + visibleRows * RowHeight, ReferenceHeight - 80f) + 2f * PanelOuterMargin;

                bool minimalUi = MinimalUi;
                _panelRect.sizeDelta = new Vector2(w, h);
                _panelFillImage.sprite = PanelSprite(Mathf.RoundToInt(w), Mathf.RoundToInt(h), _jagFrame, minimalUi);

                // Read fresh every rebuild so a live Configuration Manager change applies
                // immediately. Grain overlay fades along with the fill (it's baked fully
                // opaque, see PanelGrainTexture) and is hidden entirely in minimal mode.
                float panelOpacity = _cfg != null ? Mathf.Clamp01(_cfg.PanelOpacity.Value) : 1f;
                _panelFillImage.color = new Color(1f, 1f, 1f, panelOpacity);
                if (_grainImage != null)
                {
                    _grainImage.gameObject.SetActive(!minimalUi);
                    _grainImage.color = new Color(1f, 1f, 1f, panelOpacity);
                }

                // Widened by RowSelOverflow so the selected row's bar can be drawn out to
                // fill the clipping bound; normal rows stay inset to PanelPaddingHorizontal.
                float rowMaskPadding = PanelPaddingHorizontal - RowSelOverflow;
                _rowsContainer.anchorMin = Vector2.zero;
                _rowsContainer.anchorMax = Vector2.one;
                _rowsContainer.offsetMin = new Vector2(rowMaskPadding,
                    PanelPadding + FooterHeight + WarnHeight + ScrollHintGap + ScrollHintHeight + ScrollHintGap);
                _rowsContainer.offsetMax = new Vector2(-rowMaskPadding,
                    -(PanelPadding + TitleHeight + ScrollHintGap + ScrollHintHeight + ScrollHintGap));

                _titleText.text = $"Quick Resume  {SavePickerLocalization.Get(PickerText.LoadSave)}  "
                    + $"({(_offline ? SavePickerLocalization.Get(PickerText.Solo) : SavePickerLocalization.Get(PickerText.Coop))})";

                // Same fix as RefreshFooter's badge sizing: a fresh TMP text's preferred
                // size is unreliable until its mesh is generated at least once.
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

                    // Striped by absolute entry index (not pool slot) so the pattern stays
                    // stable while scrolling. Plain/selected rows stay transparent so the
                    // panel background (or _selOverlay for the selected row) shows through.
                    bool striped = entryIndex % 2 == 0;
                    highlight.color = (!sel && striped) ? RowStripeColor : Color.clear;
                    highlight.sprite = (!sel && striped) ? RowCapSprite() : null;
                    highlight.type = (!sel && striped) ? Image.Type.Sliced : Image.Type.Simple;

                    // Rows stay inset to PanelPaddingHorizontal; the selection bulge lives on _selOverlay.
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
                ApplySelOverlaySprite(minimalUi); // refreshed every rebuild so a live minimal-ui toggle applies immediately

                RefreshFooter();
                RefreshWarn();
            }
            catch (Exception e)
            {
                _log?.LogError($"SavePicker.RebuildUi failed (non-fatal): {e}");
            }
        }

        // Cheap refresh for the footer row so it never shows a key that's been rebound.
        private void RefreshFooter()
        {
            if (_footerRow == null || _footerEntries.Count < 5) return;
            string key = _cfg != null ? _cfg.ResumeKey.Value.ToString() : "F7";
            bool keyLoads = _cfg != null && _cfg.ResumeKeyLoadsInsteadOfClosing.Value;
            string loadKeys = keyLoads ? $"{key} / Enter" : "Enter";
            string closeKeys = keyLoads ? "Esc" : $"{key} / Esc";
            string starKey = _cfg != null ? _cfg.StarKey.Value.ToString() : "B";
            bool starred = Selected != null && Selected.Starred;

            SetFooterEntry(_footerEntries[0], "↑/↓", SavePickerLocalization.Get(PickerText.Select));
            SetFooterEntry(_footerEntries[1], loadKeys, SavePickerLocalization.Get(PickerText.Load));
            SetFooterEntry(_footerEntries[2], starKey, SavePickerLocalization.Get(starred ? PickerText.Unstar : PickerText.Star));
            SetFooterEntry(_footerEntries[3], "Del", SavePickerLocalization.Get(PickerText.Delete));
            SetFooterEntry(_footerEntries[4], closeKeys, SavePickerLocalization.Get(PickerText.Close));

            // Badge widths follow ContentSizeFitter; force a layout pass so a rebound key
            // resizes correctly this frame. Canvas.ForceUpdateCanvases() works around a TMP
            // quirk where a fresh text's preferred size is unreliable until its mesh exists.
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_footerRow);
        }

        private static void SetFooterEntry(FooterEntry entry, string key, string label)
        {
            entry.KeyText.text = key;
            entry.LabelText.text = label;
        }

        // Builds the row of "[key badge] label" pairs, using a real rounded-rect Image
        // behind each key rather than a TMP <mark> tag (no rounded corners).
        private const float TitleIconSize = 30f;
        private const float TitleIconSpacing = 10f;
        // The flame sprite's art has more headroom above than below, so dead-center placement reads slightly high.
        private const float TitleIconVerticalNudge = 3f;
        private static Sprite _campfireIconSprite;

        // Sampled from the game's title text outline material (~59,58,55, warm dark gray, not pure black).
        internal static readonly Color ChromeOutlineColor = new Color(59f / 255f, 58f / 255f, 55f / 255f);

        // Title bracketed by the game's own campfire icon on both sides. Uses a
        // HorizontalLayoutGroup + ContentSizeFitter so icon+text+icon stay centered as one
        // group regardless of the localized title text's width.
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

        // How much bigger each backing silhouette copy (see AddTitleIcon) is drawn than the real icon.
        private const float TitleIconOutlineScale = 1.12f;
        // Diagonal offset between the two backing copies, px.
        private const float TitleIconOutlineOffset = 1.1f;

        private void AddTitleIcon(Transform parent, Sprite iconSprite)
        {
            if (iconSprite == null) return;

            // Images live as children of this slot, not directly on it, since the row's
            // layout group would undo any manual offset placed on a direct child.
            var slotGo = new GameObject("IconSlot", typeof(RectTransform));
            slotGo.transform.SetParent(parent, false);
            ((RectTransform)slotGo.transform).sizeDelta = new Vector2(TitleIconSize, TitleIconSize);

            // Outline via two slightly scaled-up, oppositely-offset silhouette copies.
            // UGUI's built-in Outline component ghosted into two distinct flames; a single
            // scaled copy alone had to be too big to read as a border. Splitting the scale
            // and offset between two copies keeps both small.
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

            // Drop shadow matching the title text's own SDF material weight (solid, not a soft blur).
            var shadow = iconGo.AddComponent<UnityEngine.UI.Shadow>();
            shadow.effectColor = new Color(ChromeOutlineColor.r, ChromeOutlineColor.g, ChromeOutlineColor.b, 0.85f);
            shadow.effectDistance = new Vector2(2.5f, -2.5f);
            shadow.useGraphicAlpha = true;
        }

        // One flat-colored, scaled-up, offset copy of the icon; two of these form the outline ring (see AddTitleIcon).
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

        // Pulled from the game's own HUD (StaminaBar.campfire), same reuse trick as
        // FindGameFont(). If no StaminaBar exists yet, retries next open instead of giving
        // up; the title just shows text-only until then.
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

            // Badge sized to its key text via a nested HorizontalLayoutGroup (as padding) + ContentSizeFitter.
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

        // No star icon exists in the game to reuse, so this is baked procedurally like the other shapes.
        internal static Sprite StarSprite() => _starSprite ??=
            MakeStarSprite(size: 24, fill: StarFillColor, border: BadgeBorderColor, borderThickness: 1.6f);

        // Filled 5-point star via a closed-form SDF (Inigo Quilez's sdStar5), which
        // anti-aliases the concave notches for free at this icon's small size.
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

        // Signed distance to a regular 5-pointed star: r = outer vertex radius, rf = inner/outer ratio.
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

        // Alpha-only shape matching the panel's fill area (inset by border thickness),
        // used as an invisible Mask host for the grain overlay.
        internal static Sprite PanelInnerMaskSprite() => _panelInnerMaskSprite ??=
            MakeCapSprite(Mathf.Max(1f, PanelCornerRadius - PanelBorderThickness));

        // Jag is on for the panel outline and selected-row edge, off for badges. One
        // shared "torn paper" scale for all of them. Frequency must stay well under
        // 1 cycle/pixel or the noise reads as random static instead of jagged (a past
        // attempt at freq=5.5 with 3 octaves washed out to invisible).
        private const float EdgeJagAmplitude = 5.0f;
        private const float EdgeJagFrequency = 1.2f;
        private const int EdgeJagOctaves = 2;
        private const float EdgeJagPersistence = 0.5f;
        private const float EdgeJagLacunarity = 2.44f;
        // Kept at 1.0 (not boosted) to match the panel's own amplitude/radius ratio;
        // a higher multiplier made the row's small corner radius look structurally broken.
        private const float RowJagAmplitudeMultiplier = 1.0f;

        // 3 pre-seeded variants cycled on a fixed interval, avoiding a full texture
        // regeneration per frame. internal: HelpScreen reuses these same timing constants.
        internal const int JagFrameCount = 3;
        internal const float JagFrameInterval = 0.5f;
        private static readonly float[] JagFrameSeedOffsets = { 0f, 173.2f, 401.7f };
        private int _jagFrame;
        private float _jagFrameTimer;

        // Keyed by (width, height): SavePicker and HelpScreen are usually different sizes,
        // so a single "most recent" cache would rebake on every switch. Never evicted; the
        // handful of distinct sizes per session is trivial memory.
        private static readonly Dictionary<(int width, int height), Sprite[]> _panelSpriteCache = new();

        // Minimal mode bakes edgeJag=0, identical every frame, so no per-frame array needed.
        private static readonly Dictionary<(int width, int height), Sprite> _panelSpriteFlatCache = new();

        // Baked at the panel's exact width/height, not a fixed guess: with Type.Simple the
        // whole texture stretches as one piece, and baking at the wrong height flattens the
        // round corners into ellipses.
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

        // Same shape/jag math as MakeRoundedSprite but bakes the whole shape directly (no
        // 9-slicing, which diluted the jag on straight edges). seedOffset varies each animation frame.
        private static Sprite MakeFullPanelSprite(int width, int height, float radius, float borderThickness,
            Color fill, Color border, float edgeJag, float jagFreq, float seedOffset)
        {
            var tex = new Texture2D(width, height, TextureFormat.ARGB32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            // SetPixels32 + one Apply(), not per-pixel SetPixel(), for a several-times speedup.
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

        // A 9-sliceable rounded-rect texture with the border baked into its pixels (not a
        // tint), reused for the main panel and footer badges at different scales.
        // edgeJag roughens both the outer silhouette and inner border/fill transition with
        // independently-offset noise, for a "crumpled paper" rather than clean vector look.
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
                    // fbm, not a single Perlin sample: stacked octaves give the small,
                    // irregular notches a torn/crumpled paper edge has.
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

        // Only the selected row gets the jagged edge; striped rows share one plain,
        // clean-cornered, no-jag sprite. Jag on every row wasn't worth the extra bakes.
        private static Sprite RowCapSprite() => _rowCapSprite ??= MakeCapSprite(RowCapRadius);

        // Baked near actual row proportions (not square) so it rarely needs to stretch,
        // keeping the corner radius and jag frequency accurate. Used with Image.Type.Simple
        // since 9-slicing can't show jag on long straight edges.
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

        // Minimal mode reuses the same clean-cornered cap sprite the zebra-striped rows use, tinted.
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

        // Bakes the whole shape directly (Image.Type.Simple) so jag noise survives
        // scaling, with grain baked into the same texture's RGB (see GenerateGrainTexture)
        // instead of a separate Image clipped by a Mask; see _selOverlay for why.
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
            // Flat array, not float[,], for faster indexing at this pixel count.
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

        // Alpha-only rounded-corner mask, tinted per row via Image.color. All 4 corners
        // always rounded, so rows read as individual chips, not one edge-rounded strip.
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

        // Baked opaque as lighter/darker variants of the panel's own color (not a neutral
        // gray blend, which would desaturate it) so the average color stays unshifted.
        // Stretched (Type.Simple) over the panel; higher resolution keeps grain crisp
        // rather than blurring into soft blobs.
        internal const int GrainTextureSize = 368;

        // Tuned interactively against a live HTML/JS port of this algorithm, side by side
        // with the boarding pass reference image.
        private const float GrainSeed = 1337f;
        private const float GrainEnvelopeFreq = 14.0f;
        private const int GrainOctaves = 6;
        private const float GrainPersistence = 0.76f;
        private const float GrainLacunarity = 2.98f;
        // Min > Max is intentional: collapses SmoothStepEdge's transition into a near-hard binary cutoff.
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

            // The fbm noise field itself is the cloud shape; SmoothStep pins most of it
            // flat to one of the two tones, with fbm's stacked octaves giving jagged
            // (not round) edges at the transition band.
            //
            // First pass: track actual min/max, since Unity's Mathf.PerlinNoise doesn't
            // necessarily match the JS implementation used to tune GrainSharpenMin/Max,
            // and a fixed 0..1 assumption could saturate SmoothStepEdge to a flat result.
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

        // Fractal Brownian motion: stacks octaves of Perlin noise at rising frequency,
        // falling amplitude, for organic irregular edges (see GenerateGrainTexture).
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

        // GLSL-style smoothstep(edge0, edge1, x). NOT Mathf.SmoothStep(from, to, t), which
        // interpolates between from/to rather than thresholding x against them.
        private static float SmoothStepEdge(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / Mathf.Max(0.0001f, edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        // Cheap refresh for just the warn line, no full rebuild. Delete-confirm takes
        // priority over a transient message if somehow both are active.
        private void RefreshWarn()
        {
            if (_warnText == null) return;
            bool showDelete = _pendingDeleteIndex >= 0 && _pendingDeleteIndex == _selected;
            bool showTransient = !showDelete && _transientWarnText != null;
            _warnText.gameObject.SetActive(showDelete || showTransient);
            if (showDelete) _warnText.text = SavePickerLocalization.Get(PickerText.DeleteConfirm);
            else if (showTransient) _warnText.text = _transientWarnText;
        }

        // Built once as the first child of rowsContainer (renders behind every pooled row),
        // then repositioned/toggled each rebuild. See _selOverlay's field comment.
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

            // One Image, one sprite baking fill+grain+jag together (see RowCapSelSprite),
            // no Mask: a toggled/repositioned Mask was found to only render correctly at row 0.
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

                // No Mask/grain here; that's the shared _selOverlay's job.
                var hl = rowGo.AddComponent<Image>();
                hl.color = Color.clear;

                // 4 columns, each its own TMP field so they can be independently x-positioned
                // by RebuildUi/ComputeColumnLayout.
                var diff = MakeText(rowGo.transform, "ColDiff", 21, FontStyles.Normal, RowColor, TextAlignmentOptions.MidlineLeft);
                StretchColumn(diff, RowTextInset);

                var mid = MakeText(rowGo.transform, "ColMid", 21, FontStyles.Normal, RowColor, TextAlignmentOptions.MidlineLeft);
                StretchColumn(mid, RowTextInset);

                var date = MakeText(rowGo.transform, "ColDate", 21, FontStyles.Normal, RowColor, TextAlignmentOptions.MidlineLeft);
                StretchColumn(date, RowTextInset);

                // Last column: right-aligned, inset by RowStarReserve on every row so the right edge stays consistent.
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

        // Uses whichever of the game's own already-loaded TMP fonts is available, so the
        // panel reads as part of the game's UI. Falls back to TMP's default if none found.
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

        // Borrows the game's own pre-baked outline+shadow TMP material instead of hand-
        // tuning our own. Retried on demand (not cached as "not found") since the native
        // UI may not have created an instance yet. Used only for chrome labels (loading
        // text, title, footer labels), never row text or key badges.
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

        // No-op if the material hasn't been found yet this session. See FindChromeOutlineMaterial.
        internal static void ApplyChromeTextStyle(TextMeshProUGUI tmp)
        {
            var mat = FindChromeOutlineMaterial();
            if (mat != null && tmp != null) tmp.fontSharedMaterial = mat;
        }
    }
}
