// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

/// <summary>
/// World-space VR file browser for browsing the file system and loading .ply/.spz/.sog/.spx splat files.
///
/// VR Controls:
///   Left Y (secondaryButton)  → toggle browser open/close
///   Left or right stick up/down → navigate list
///   Left or right trigger     → select (enter folder / load file)
///   Right B (secondaryButton) → go to parent directory
///
/// Desktop fallback:
///   Esc / Tab  → toggle browser
///   Arrow keys → navigate list
///   Enter      → select
///   Backspace  → go to parent
/// </summary>
public class VRFileBrowser : MonoBehaviour
{
    [Header("Setup")]
    [Tooltip("RuntimeSplatLoader to load selected files into.")]
    public RuntimeSplatLoader loader;

    [Tooltip("Optional SplatCycler — its folder will be updated when a file is loaded from the browser.")]
    public SplatCycler cycler;

    [Tooltip("Initial folder path. Leave empty to start at drive list.")]
    public string startPath = "";

    [Header("Placement")]
    [Tooltip("Distance in front of the user when the browser opens.")]
    public float spawnDistance = 1.5f;

    /// <summary>True when the browser panel is visible and consuming right-hand input.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Remains true for one extra frame after closing, to prevent button leak to other scripts.</summary>
    public bool WasOpenThisFrame { get; private set; }

    // ── Layout constants ──────────────────────────────────────────────────────

    const int CW = 900, CH = 650;
    const float SCALE = 0.001f;
    const int ROWS = 14;
    const int ROW_H = 38;
    const int PATH_H = 36;
    const int HINT_H = 30;
    const int PAD = 10;
    const int FONT_ENTRY = 22;
    const int FONT_PATH = 18;
    const int FONT_HINT = 16;

    // ── Colors ────────────────────────────────────────────────────────────────

    static readonly Color COL_BG      = new Color(0.08f, 0.08f, 0.10f, 0.96f);
    static readonly Color COL_SEL     = new Color(0.20f, 0.40f, 0.85f, 0.80f);
    static readonly Color COL_ROW_ALT = new Color(1f, 1f, 1f, 0.03f);
    static readonly Color COL_DIR     = new Color(1f, 0.88f, 0.40f);
    static readonly Color COL_FILE    = Color.white;
    static readonly Color COL_PATH    = new Color(0.65f, 0.65f, 0.65f);
    static readonly Color COL_HINT    = new Color(0.45f, 0.45f, 0.45f);
    static readonly Color COL_CLEAR   = new Color(0f, 0f, 0f, 0f);

    // ── State ─────────────────────────────────────────────────────────────────

    string _currentPath;
    readonly List<Entry> _entries = new List<Entry>();
    int _sel;
    int _scroll;

    // ── UI objects ────────────────────────────────────────────────────────────

    GameObject _root;
    Text _pathText;
    Text _hintText;
    Text _helpText;
    Text[] _rowTexts;
    Image[] _rowBgs;
    static Font _font;

    // ── Input state ───────────────────────────────────────────────────────────

    float _stickCD;
    bool _trigReady = true;
    bool _toggleReady = true;
    bool _backReady = true;

    struct Entry
    {
        public string name;
        public string path;
        public bool isDir;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Start()
    {
        if (loader == null) loader = FindAnyObjectByType<RuntimeSplatLoader>();
        if (cycler == null) cycler = FindAnyObjectByType<SplatCycler>();

        _currentPath = string.IsNullOrEmpty(startPath) ? null : startPath;

        // Cache a font reference
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (_font == null) _font = Font.CreateDynamicFontFromOSFont("Arial", 14);

        BuildUI();
        _root.SetActive(false);
    }

    void Update()
    {
        // Clear the one-frame guard from previous frame
        WasOpenThisFrame = IsOpen;

        // Toggle: left Y button or Esc/Tab key on desktop
        bool yBtn = ReadButton(XRNode.LeftHand, CommonUsages.secondaryButton);
        if (yBtn && _toggleReady) { ToggleBrowser(); _toggleReady = false; }
        else if (!yBtn) _toggleReady = true;

        if (!XRSettings.isDeviceActive
            && (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Tab)))
            ToggleBrowser();

        if (!IsOpen) return;

        HandleNavigation();
        HandleSelect();
        HandleBack();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void ToggleBrowser()
    {
        IsOpen = !IsOpen;
        _root.SetActive(IsOpen);
        if (IsOpen)
        {
            PositionInFront();
            Navigate(_currentPath);
        }

        if (!XRSettings.isDeviceActive)
        {
            Cursor.lockState = IsOpen ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = IsOpen;
        }
    }

    // ── Input Handling ────────────────────────────────────────────────────────

    void HandleNavigation()
    {
        if (_entries.Count == 0) return;

        // Left or right stick Y (VR) or arrow keys (desktop)
        float ry = 0f;
        if (XRSettings.isDeviceActive)
            ry = ReadNavigationY();
        else
        {
            if (Input.GetKey(KeyCode.UpArrow))   ry =  1f;
            if (Input.GetKey(KeyCode.DownArrow)) ry = -1f;
        }

        _stickCD -= Time.deltaTime;
        if (Mathf.Abs(ry) > 0.5f && _stickCD <= 0f)
        {
            _sel += (ry < 0f) ? 1 : -1;
            _sel = Mathf.Clamp(_sel, 0, _entries.Count - 1);
            EnsureVisible();
            UpdateRows();
            _stickCD = 0.18f;
        }
        else if (Mathf.Abs(ry) <= 0.3f)
        {
            _stickCD = 0f;
        }
    }

    void HandleSelect()
    {
        bool trig = false;
        if (XRSettings.isDeviceActive)
            trig = ReadTrigger(XRNode.LeftHand)
                || ReadTrigger(XRNode.RightHand)
                || ReadButton(XRNode.RightHand, CommonUsages.primaryButton);
        else
            trig = Input.GetKeyDown(KeyCode.Return);

        if (trig && _trigReady)
        {
            _trigReady = false;
            SelectCurrent();
        }
        else if (!trig)
        {
            _trigReady = true;
        }
    }

    void HandleBack()
    {
        bool b = false;
        if (XRSettings.isDeviceActive)
            b = ReadButton(XRNode.RightHand, CommonUsages.secondaryButton);
        else
            b = Input.GetKeyDown(KeyCode.Backspace);

        if (b && _backReady)
        {
            _backReady = false;
            GoUp();
        }
        else if (!b)
        {
            _backReady = true;
        }
    }

    // ── File System ───────────────────────────────────────────────────────────

    void Navigate(string path)
    {
        _currentPath = path;
        _entries.Clear();
        _sel = 0;
        _scroll = 0;

        if (string.IsNullOrEmpty(_currentPath))
        {
            // List drives
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (!d.IsReady) continue;
                    string label = "";
                    try { label = d.VolumeLabel; } catch { }
                    _entries.Add(new Entry
                    {
                        name = string.IsNullOrEmpty(label)
                            ? d.Name.TrimEnd('\\')
                            : $"{d.Name.TrimEnd('\\')}  ({label})",
                        path = d.RootDirectory.FullName,
                        isDir = true
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VRFileBrowser] Drive list error: {ex.Message}");
            }
        }
        else
        {
            // Parent entry
            var parent = Directory.GetParent(_currentPath);
            _entries.Add(new Entry { name = "..", path = parent?.FullName, isDir = true });

            try
            {
                foreach (var dir in Directory.GetDirectories(_currentPath)
                    .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                {
                    _entries.Add(new Entry
                    {
                        name = Path.GetFileName(dir),
                        path = dir,
                        isDir = true
                    });
                }

                foreach (var file in Directory.GetFiles(_currentPath)
                    .Where(f =>
                    {
                        return RuntimeSplatLoader.IsSupportedFileExtension(f);
                    })
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                {
                    _entries.Add(new Entry
                    {
                        name = Path.GetFileName(file),
                        path = file,
                        isDir = false
                    });
                }
            }
            catch (UnauthorizedAccessException) { /* skip protected dirs */ }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VRFileBrowser] Cannot list {_currentPath}: {ex.Message}");
            }
        }

        UpdatePath();
        UpdateRows();
    }

    void SelectCurrent()
    {
        if (_sel < 0 || _sel >= _entries.Count) return;
        var entry = _entries[_sel];

        if (entry.isDir)
        {
            Navigate(entry.path); // null path → drive list
        }
        else
        {
            if (loader != null)
            {
                bool ok = loader.LoadFile(entry.path);
                if (ok)
                {
                    // Sync SplatCycler to the loaded file so B/A cycling continues from here
                    if (cycler != null && !string.IsNullOrEmpty(_currentPath))
                    {
                        cycler.splatFolder = _currentPath;
                        cycler.ScanFolder();
                        // Find the loaded file's index in the cycler's list
                        string fileName = Path.GetFileName(entry.path);
                        for (int i = 0; i < cycler.Files.Count; i++)
                        {
                            if (Path.GetFileName(cycler.Files[i]).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                            {
                                cycler.SetIndex(i);
                                break;
                            }
                        }
                    }

                    // Reset camera to initial viewpoint
                    var rig = FindAnyObjectByType<VRRig>();
                    if (rig != null) rig.ResetToSpawnPoint(loader != null ? loader.targetRenderer : null);

                    ToggleBrowser(); // close after loading
                }
            }
        }
    }

    void GoUp()
    {
        if (string.IsNullOrEmpty(_currentPath)) return; // already at drives
        var parent = Directory.GetParent(_currentPath);
        Navigate(parent?.FullName);
    }

    // ── UI Building ───────────────────────────────────────────────────────────

    void BuildUI()
    {
        // World-space Canvas
        _root = new GameObject("VRFileBrowser");
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        _root.AddComponent<CanvasScaler>();

        var canvasRT = _root.GetComponent<RectTransform>();
        canvasRT.sizeDelta = new Vector2(CW, CH);
        _root.transform.localScale = Vector3.one * SCALE;

        // Background
        var bg = MakeChild(_root.transform, "BG");
        bg.AddComponent<Image>().color = COL_BG;
        Stretch(bg);

        // ── Layout from top down ──
        float y = -PAD;

        // Path bar
        _pathText = MakeText(bg.transform, "Path", "", FONT_PATH, COL_PATH,
            PAD + 4, y, CW - PAD * 2 - 8, PATH_H);
        y -= PATH_H;

        // Separator
        var sep = MakeChild(bg.transform, "Sep");
        sep.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.15f);
        SetRect(sep, PAD, y, CW - PAD * 2, 1);
        y -= 4;

        // Entry rows
        _rowBgs  = new Image[ROWS];
        _rowTexts = new Text[ROWS];
        for (int i = 0; i < ROWS; i++)
        {
            var rowBg = MakeChild(bg.transform, $"RowBg{i}");
            _rowBgs[i] = rowBg.AddComponent<Image>();
            _rowBgs[i].color = COL_CLEAR;
            SetRect(rowBg, PAD, y, CW - PAD * 2, ROW_H);

            _rowTexts[i] = MakeText(bg.transform, $"Row{i}", "", FONT_ENTRY, COL_FILE,
                PAD + 12, y, CW - PAD * 2 - 24, ROW_H);
            y -= ROW_H;
        }

        // Hint bar at bottom
        y -= 4;
        string vr   = "[L/R Stick] Navigate    [L/R Trigger or A] Select    [B] Back    [Y] Close";
        string desk  = "[Arrows] Navigate    [Enter] Select    [Backspace] Back    [Esc/Tab] Close";
        _hintText = MakeText(bg.transform, "Hint", XRSettings.isDeviceActive ? vr : desk,
            FONT_HINT, COL_HINT, PAD, y, CW - PAD * 2, HINT_H, TextAnchor.MiddleCenter);

        var helpPanel = MakeChild(_root.transform, "HelpPanel");
        var helpBg = helpPanel.AddComponent<Image>();
        helpBg.color = new Color(0.05f, 0.05f, 0.07f, 0.92f);
        SetRect(helpPanel, CW + 24, -PAD, 320, 260);

        _helpText = MakeText(helpPanel.transform, "Help", "", FONT_HINT, Color.white,
            16, -16, 288, 228, TextAnchor.UpperLeft);
        _helpText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _helpText.verticalOverflow = VerticalWrapMode.Overflow;
        UpdateHelpText();
    }

    // ── UI Helpers ────────────────────────────────────────────────────────────

    static GameObject MakeChild(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static void Stretch(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static void SetRect(GameObject go, float x, float y, float w, float h)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot     = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    static Text MakeText(Transform parent, string name, string text,
        int fontSize, Color color, float x, float y, float w, float h,
        TextAnchor align = TextAnchor.MiddleLeft)
    {
        var go = MakeChild(parent, name);
        SetRect(go, x, y, w, h);
        var t = go.AddComponent<Text>();
        t.text      = text;
        t.fontSize  = fontSize;
        t.color     = color;
        t.alignment = align;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow   = VerticalWrapMode.Truncate;
        if (_font != null) t.font = _font;
        return t;
    }

    // ── UI Update ─────────────────────────────────────────────────────────────

    void UpdatePath()
    {
        _pathText.text = string.IsNullOrEmpty(_currentPath)
            ? "Computer (Drives)"
            : TruncatePath(_currentPath, 70);
    }

    void UpdateRows()
    {
        for (int i = 0; i < ROWS; i++)
        {
            int idx = _scroll + i;
            if (idx < _entries.Count)
            {
                var e = _entries[idx];
                _rowTexts[i].text  = (e.isDir ? "\u25B6 " : "   ") + e.name;
                _rowTexts[i].color = e.isDir ? COL_DIR : COL_FILE;

                if (idx == _sel)
                    _rowBgs[i].color = COL_SEL;
                else if (i % 2 == 1)
                    _rowBgs[i].color = COL_ROW_ALT;
                else
                    _rowBgs[i].color = COL_CLEAR;
            }
            else
            {
                _rowTexts[i].text  = "";
                _rowBgs[i].color   = COL_CLEAR;
            }
        }

        // Update hint with item count
        int dirs  = _entries.Count(e => e.isDir) - (string.IsNullOrEmpty(_currentPath) ? 0 : 1);
        int files = _entries.Count(e => !e.isDir);
        string countInfo = $"{dirs} folder(s), {files} file(s)";
        if (_entries.Count > 0)
            countInfo += $"   [{_sel + 1}/{_entries.Count}]";

        string controls = XRSettings.isDeviceActive
            ? "[L/R Stick] Navigate    [L/R Trigger or A] Select    [B] Back    [Y] Close"
            : "[Arrows] Navigate    [Enter] Select    [Backspace] Back    [Esc/Tab] Close";
        _hintText.text = $"{countInfo}\n{controls}";
        UpdateHelpText();
    }

    void UpdateHelpText()
    {
        if (_helpText == null) return;

        _helpText.text = XRSettings.isDeviceActive
            ? "Browser\n"
            + "Y: open / close\n"
            + "Left or right stick: browse list\n"
            + "Left or right trigger: open / load\n"
            + "A: open / load\n"
            + "B: parent folder\n\n"
            + "Scene\n"
            + "Hold left grip + right stick: rotate splat\n"
            + "Hold left grip + X: flip\n"
            + "Hold left grip + A: reset rotation"
            : "Browser\n"
                + "Esc / Tab: open / close\n"
            + "Arrows: browse list\n"
            + "Enter: open / load\n"
            + "Backspace: parent folder\n\n"
            + "Scene\n"
                + "Mouse: look\n"
                + "WASD: move\n"
                + "Space / C: up / down\n"
                + "R / F: next / previous splat\n"
                + "Q / E: rotate splat\n"
                + "Left Click: capture mouse\n"
                + "Home: reset rotation\n"
                + "End: flip upside down";
    }

    void EnsureVisible()
    {
        if (_sel < _scroll)           _scroll = _sel;
        if (_sel >= _scroll + ROWS)   _scroll = _sel - ROWS + 1;
    }

    static string TruncatePath(string p, int maxLen)
    {
        if (p.Length <= maxLen) return p;
        return "..." + p.Substring(p.Length - maxLen + 3);
    }

    // ── Positioning ───────────────────────────────────────────────────────────

    void PositionInFront()
    {
        var cam = Camera.main;
        if (cam == null) return;

        Vector3 fwd = cam.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();

        Vector3 pos = cam.transform.position + fwd * spawnDistance;
        _root.transform.position = pos;
        // Canvas front faces toward the user
        _root.transform.rotation = Quaternion.LookRotation(fwd);
    }

    // ── XR Input Helpers ──────────────────────────────────────────────────────

    static bool ReadButton(XRNode node, InputFeatureUsage<bool> usage)
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devs);
        if (devs.Count > 0 && devs[0].TryGetFeatureValue(usage, out bool v))
            return v;
        return false;
    }

    static float ReadNavigationY()
    {
        float leftY = ReadStickY(XRNode.LeftHand);
        float rightY = ReadStickY(XRNode.RightHand);
        return Mathf.Abs(leftY) >= Mathf.Abs(rightY) ? leftY : rightY;
    }

    static float ReadStickY(XRNode node)
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devs);
        if (devs.Count > 0 && devs[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v))
            return v.y;
        return 0f;
    }

    static bool ReadTrigger(XRNode node)
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devs);
        if (devs.Count > 0 && devs[0].TryGetFeatureValue(CommonUsages.trigger, out float v))
            return v > 0.5f;
        return false;
    }
}
