using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace PEAKQuickResume
{
    /// <summary>
    /// The in-game F7 save picker: an overlay listing every archived checkpoint for the
    /// CURRENT network category (offline saves when solo, coop saves when in coop), newest
    /// first. Arrow keys move the highlight, Delete removes a save (two-step), Escape
    /// closes. The resume key itself (open / confirm-load) is driven by <see cref="Plugin"/>
    /// so a single key press never both opens and confirms
    ///
    /// The newest save is preselected, so "press F7, press F7 again" still loads the
    /// latest checkpoint exactly like before the picker existed
    /// </summary>
    public class SavePicker : MonoBehaviour
    {
        private ManualLogSource _log;
        private PluginConfig _cfg;

        private List<ArchivedSave> _entries = new List<ArchivedSave>();
        private int _selected;
        private bool _offline;

        // Two-step delete guard: first Delete arms, second within the window confirms
        private int _pendingDeleteIndex = -1;
        private float _pendingDeleteDeadline;

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

            ClearPendingDelete();
            IsOpen = true;
            _log?.LogInfo($"[picker] Opened with {_entries.Count} {(offline ? "offline" : "coop")} save(s); selected #{_selected}.");
            return true;
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            ClearPendingDelete();
            _log?.LogInfo("[picker] Closed.");
        }

        private void Update()
        {
            if (!IsOpen) return;

            // Navigation (the resume key + Enter confirm live in Plugin)
            if (Input.GetKeyDown(KeyCode.UpArrow)) Move(-1);
            else if (Input.GetKeyDown(KeyCode.DownArrow)) Move(1);
            else if (Input.GetKeyDown(KeyCode.Escape)) Close();
            else if (Input.GetKeyDown(KeyCode.Delete)) OnDeletePressed();

            if (_pendingDeleteIndex >= 0 && Time.unscaledTime > _pendingDeleteDeadline)
                ClearPendingDelete();
        }

        private void Move(int delta)
        {
            ClearPendingDelete();
            if (_entries.Count == 0) return;
            _selected = Mathf.Clamp(_selected + delta, 0, _entries.Count - 1);
        }

        private void OnDeletePressed()
        {
            var target = Selected;
            if (target == null) return;

            if (_pendingDeleteIndex == _selected && Time.unscaledTime <= _pendingDeleteDeadline)
            {
                SaveArchive.Delete(target, _log);
                _entries.RemoveAt(_selected);
                ClearPendingDelete();
                if (_entries.Count == 0) { Close(); return; }
                _selected = Mathf.Clamp(_selected, 0, _entries.Count - 1);
            }
            else
            {
                _pendingDeleteIndex = _selected;
                _pendingDeleteDeadline = Time.unscaledTime + 3f;
            }
        }

        private void ClearPendingDelete()
        {
            _pendingDeleteIndex = -1;
            _pendingDeleteDeadline = 0f;
        }

        // --- IMGUI rendering ---

        private GUIStyle _panel, _title, _row, _rowSel, _footer, _warn;

        private void EnsureStyles()
        {
            if (_panel != null) return;

            _panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(16, 16, 12, 12) };
            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.6f, 0.85f, 1f) }
            };
            _row = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16, alignment = TextAnchor.MiddleLeft, richText = true,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f) }
            };
            _rowSel = new GUIStyle(_row)
            {
                fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.95f, 0.4f) }
            };
            _footer = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.75f, 0.85f, 0.95f) }
            };
            _warn = new GUIStyle(_footer)
            {
                fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.5f, 0.5f) }
            };
        }

        private void OnGUI()
        {
            if (!IsOpen) return;
            EnsureStyles();

            float w = Mathf.Min(760f, Screen.width - 80f);
            float h = Mathf.Min(60f + _entries.Count * 30f + 70f, Screen.height - 80f);
            float x = (Screen.width - w) / 2f;
            float y = (Screen.height - h) / 2f;

            // Dim the screen behind the panel
            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = prev;

            GUILayout.BeginArea(new Rect(x, y, w, h), _panel);
            GUILayout.Label($"Quick Resume  Load Save  ({(_offline ? "Solo" : "Co-op")})", _title);
            GUILayout.Space(8f);

            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                bool sel = i == _selected;
                string marker = sel ? "▶ " : "   ";
                string campfire = string.IsNullOrEmpty(e.CampfireName) ? "—" : e.CampfireName;
                string date = string.IsNullOrEmpty(e.SaveDate) ? e.SortTime.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : e.SaveDate;
                string line = $"{marker}<b>{e.DifficultyLabel}</b>   {campfire}   {date}   {FormatPlaytime(e.Playtime)}";
                // Co-op: show everyone who played this run
                if (!_offline && !string.IsNullOrEmpty(e.Players))
                    line += $"   ({e.Players})";
                GUILayout.Label(line, sel ? _rowSel : _row);
            }

            GUILayout.FlexibleSpace();
            if (_pendingDeleteIndex >= 0 && _pendingDeleteIndex == _selected)
                GUILayout.Label("Press Delete again to permanently remove this save.", _warn);

            string key = _cfg != null ? _cfg.ResumeKey.Value.ToString() : "F7";
            GUILayout.Label($"↑/↓ Select     {key} / Enter  Load     Del  Delete     Esc  Cancel", _footer);
            GUILayout.EndArea();
        }

        private static string FormatPlaytime(float seconds)
        {
            if (seconds <= 0f) return "";
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m played" : $"{t.Minutes}m played";
        }
    }
}
