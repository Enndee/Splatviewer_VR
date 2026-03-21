// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.XR;

/// <summary>
/// World-space VR options menu to adjust Render Scale, SH Order and Sort Every Nth Frame at runtime.
///
/// VR Controls:
///   Right grip (hold) + Left Y (secondaryButton) → toggle menu
///   Left/Right stick up/down                     → navigate rows
///   Left/Right stick left/right                  → adjust value
///
/// Desktop fallback:
///   O                   → toggle menu
///   Up / Down           → navigate rows
///   Left / Right        → adjust value
/// </summary>
public class VROptionsMenu : MonoBehaviour
{
    // ── Layout constants ──────────────────────────────────────────────────────

    const int PW = 620, PH = 280;
    const float SCALE = 0.001f;
    const int PAD = 14;
    const int ROW_H = 44;
    const int FONT_LABEL = 22;
    const int FONT_TITLE = 24;
    const float SPAWN_DIST = 1.4f;

    // ── Colors ────────────────────────────────────────────────────────────────

    static readonly Color COL_BG       = new Color(0.06f, 0.06f, 0.08f, 0.94f);
    static readonly Color COL_SEL      = new Color(0.20f, 0.40f, 0.85f, 0.70f);
    static readonly Color COL_ROW      = new Color(1f, 1f, 1f, 0.03f);
    static readonly Color COL_CLEAR    = new Color(0f, 0f, 0f, 0f);
    static readonly Color COL_LABEL    = Color.white;
    static readonly Color COL_VALUE    = new Color(0.55f, 0.90f, 1.0f);
    static readonly Color COL_TITLE    = new Color(0.75f, 0.75f, 0.75f);
    static readonly Color COL_BAR_BG   = new Color(1f, 1f, 1f, 0.10f);
    static readonly Color COL_BAR_FILL = new Color(0.25f, 0.55f, 0.95f, 0.80f);

    // ── Option definitions ────────────────────────────────────────────────────

    struct OptionDef
    {
        public string label;
        public float min, max, step;
        public bool intOnly;
    }

    static readonly OptionDef[] Options =
    {
        new OptionDef { label = "Render Scale",    min = 0.50f, max = 1.00f, step = 0.05f, intOnly = false },
        new OptionDef { label = "SH Order",        min = 0,     max = 3,     step = 1,     intOnly = true  },
        new OptionDef { label = "Sort Every N Frames", min = 1, max = 30,    step = 1,     intOnly = true  },
    };

    const int OPT_RENDER_SCALE = 0;
    const int OPT_SH_ORDER     = 1;
    const int OPT_SORT_NTH     = 2;

    // ── PlayerPrefs keys ──────────────────────────────────────────────────────

    public const string PrefKeyRenderScale = "opt_renderScale";
    public const string PrefKeySHOrder     = "opt_shOrder";
    public const string PrefKeySortNth     = "opt_sortNth";

    /// <summary>Load a saved float from PlayerPrefs, returning defaultValue if not present.</summary>
    public static float LoadSavedFloat(string key, float defaultValue)
    {
        return PlayerPrefs.HasKey(key) ? PlayerPrefs.GetFloat(key) : defaultValue;
    }

    // ── State ─────────────────────────────────────────────────────────────────

    float[] _values;
    int _sel;
    bool _isOpen;
    float _stickCD;
    float _adjustCD;
    bool _toggleReady = true;

    // ── UI objects ────────────────────────────────────────────────────────────

    GameObject _root;
    Text[] _labelTexts;
    Text[] _valueTexts;
    Image[] _rowBgs;
    Image[] _barFills;
    static Font _font;

    // ── References ────────────────────────────────────────────────────────────

    VRFileBrowser _browser;

    /// <summary>True when the options menu is visible.</summary>
    public bool IsOpen => _isOpen;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Start()
    {
        _browser = FindAnyObjectByType<VRFileBrowser>();
        _values = new float[Options.Length];
        ReadCurrentValues();
        CacheFont();
        BuildUI();
        _root.SetActive(false);
    }

    void Update()
    {
        HandleToggle();
        if (!_isOpen) return;
        HandleNavigation();
        HandleAdjust();
    }

    // ── Read / Apply ──────────────────────────────────────────────────────────

    void ReadCurrentValues()
    {
        _values[OPT_RENDER_SCALE] = LoadSavedFloat(PrefKeyRenderScale, XRSettings.eyeTextureResolutionScale);
        if (_values[OPT_RENDER_SCALE] < 0.01f)
            _values[OPT_RENDER_SCALE] = 0.80f;

        var renderer = FindAnyObjectByType<GaussianSplatRenderer>();
        _values[OPT_SH_ORDER] = LoadSavedFloat(PrefKeySHOrder, renderer != null ? renderer.m_SHOrder : 1);
        _values[OPT_SORT_NTH] = LoadSavedFloat(PrefKeySortNth, renderer != null ? renderer.m_SortNthFrame : 12);
    }

    void ApplyRenderScale(float value)
    {
        float scale = Mathf.Clamp(value, Options[OPT_RENDER_SCALE].min, Options[OPT_RENDER_SCALE].max);
        XRSettings.eyeTextureResolutionScale = scale;

        // Also update the URP pipeline asset so everything stays in sync
        var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (pipeline != null)
            pipeline.renderScale = scale;

        PlayerPrefs.SetFloat(PrefKeyRenderScale, scale);
        PlayerPrefs.Save();
        Debug.Log($"[VROptionsMenu] Render scale → {scale:0.00}");
    }

    void ApplySHOrder(int order)
    {
        order = Mathf.Clamp(order, 0, 3);
        foreach (var r in FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None))
            r.m_SHOrder = order;
        PlayerPrefs.SetFloat(PrefKeySHOrder, order);
        PlayerPrefs.Save();
        Debug.Log($"[VROptionsMenu] SH order → {order}");
    }

    void ApplySortNthFrame(int n)
    {
        n = Mathf.Clamp(n, 1, 30);
        foreach (var r in FindObjectsByType<GaussianSplatRenderer>(FindObjectsSortMode.None))
            r.m_SortNthFrame = n;
        PlayerPrefs.SetFloat(PrefKeySortNth, n);
        PlayerPrefs.Save();
        Debug.Log($"[VROptionsMenu] Sort every Nth frame → {n}");
    }

    void ApplyValue(int optIndex, float newValue)
    {
        var def = Options[optIndex];
        newValue = Mathf.Clamp(newValue, def.min, def.max);
        if (def.intOnly)
            newValue = Mathf.Round(newValue);
        _values[optIndex] = newValue;

        switch (optIndex)
        {
            case OPT_RENDER_SCALE: ApplyRenderScale(newValue); break;
            case OPT_SH_ORDER:     ApplySHOrder((int)newValue); break;
            case OPT_SORT_NTH:     ApplySortNthFrame((int)newValue); break;
        }

        UpdateRow(optIndex);
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    void HandleToggle()
    {
        bool pressed = false;

        if (XRSettings.isDeviceActive)
        {
            // Right grip held + left Y to avoid clashing with browser's Y toggle
            float rightGrip = ReadAxis1D(XRNode.RightHand, CommonUsages.grip);
            bool yBtn = ReadButton(XRNode.LeftHand, CommonUsages.secondaryButton);
            pressed = rightGrip > 0.5f && yBtn;
        }
        else
        {
            pressed = Input.GetKeyDown(KeyCode.O);
        }

        if (pressed && _toggleReady)
        {
            _toggleReady = false;
            Toggle();
        }
        else if (!pressed)
        {
            _toggleReady = true;
        }
    }

    void Toggle()
    {
        // Close the file browser first if it's open
        if (!_isOpen && _browser != null && _browser.IsOpen)
            _browser.ToggleBrowser();

        _isOpen = !_isOpen;
        _root.SetActive(_isOpen);

        if (_isOpen)
        {
            ReadCurrentValues();
            UpdateAllRows();
            PositionInFront();
        }
    }

    void HandleNavigation()
    {
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
            _sel = Mathf.Clamp(_sel, 0, Options.Length - 1);
            UpdateAllRows();
            _stickCD = 0.20f;
        }
        else if (Mathf.Abs(ry) <= 0.3f)
        {
            _stickCD = 0f;
        }
    }

    void HandleAdjust()
    {
        float rx = 0f;
        if (XRSettings.isDeviceActive)
            rx = ReadNavigationX();
        else
        {
            if (Input.GetKey(KeyCode.RightArrow)) rx =  1f;
            if (Input.GetKey(KeyCode.LeftArrow))  rx = -1f;
        }

        _adjustCD -= Time.deltaTime;
        if (Mathf.Abs(rx) > 0.5f && _adjustCD <= 0f)
        {
            float delta = Options[_sel].step * Mathf.Sign(rx);
            ApplyValue(_sel, _values[_sel] + delta);
            _adjustCD = 0.15f;
        }
        else if (Mathf.Abs(rx) <= 0.3f)
        {
            _adjustCD = 0f;
        }
    }

    // ── UI Building ───────────────────────────────────────────────────────────

    void BuildUI()
    {
        _root = new GameObject("VROptionsMenu");
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        _root.AddComponent<CanvasScaler>();

        var crt = _root.GetComponent<RectTransform>();
        crt.sizeDelta = new Vector2(PW, PH);
        _root.transform.localScale = Vector3.one * SCALE;

        // Background
        var bg = MakeChild(_root.transform, "BG");
        bg.AddComponent<Image>().color = COL_BG;
        Stretch(bg);

        float y = -PAD;

        // Title
        MakeText(bg.transform, "Title", "OPTIONS", FONT_TITLE, COL_TITLE,
            PAD, y, PW - PAD * 2, 30, TextAnchor.MiddleCenter);
        y -= 36;

        // Separator
        var sep = MakeChild(bg.transform, "Sep");
        sep.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.15f);
        SetRect(sep, PAD, y, PW - PAD * 2, 1);
        y -= 6;

        int count = Options.Length;
        _labelTexts = new Text[count];
        _valueTexts = new Text[count];
        _rowBgs     = new Image[count];
        _barFills   = new Image[count];

        const int labelW = 220;
        const int valueW = 80;
        const int barH   = 10;
        int barW = PW - PAD * 2 - labelW - valueW - 24;

        for (int i = 0; i < count; i++)
        {
            // Row background
            var rowGo = MakeChild(bg.transform, $"RowBg{i}");
            _rowBgs[i] = rowGo.AddComponent<Image>();
            _rowBgs[i].color = COL_CLEAR;
            SetRect(rowGo, PAD, y, PW - PAD * 2, ROW_H);

            // Label
            _labelTexts[i] = MakeText(bg.transform, $"Label{i}", Options[i].label,
                FONT_LABEL, COL_LABEL, PAD + 8, y, labelW, ROW_H);

            // Bar background
            float barX = PAD + labelW + 8;
            var barBgGo = MakeChild(bg.transform, $"BarBg{i}");
            barBgGo.AddComponent<Image>().color = COL_BAR_BG;
            SetRect(barBgGo, barX, y - (ROW_H - barH) / 2, barW, barH);

            // Bar fill
            var barFillGo = MakeChild(bg.transform, $"BarFill{i}");
            _barFills[i] = barFillGo.AddComponent<Image>();
            _barFills[i].color = COL_BAR_FILL;
            SetRect(barFillGo, barX, y - (ROW_H - barH) / 2, 0, barH);

            // Value text
            float valX = barX + barW + 8;
            _valueTexts[i] = MakeText(bg.transform, $"Value{i}", "",
                FONT_LABEL, COL_VALUE, valX, y, valueW, ROW_H, TextAnchor.MiddleRight);

            y -= ROW_H;
        }

        // Hint
        y -= 4;
        string hint = XRSettings.isDeviceActive
            ? "[Stick \u2191\u2193] Select   [Stick \u2190\u2192] Adjust   [R-Grip + Y] Close"
            : "[Up/Down] Select   [Left/Right] Adjust   [O] Close";
        MakeText(bg.transform, "Hint", hint, 16, new Color(0.45f, 0.45f, 0.45f),
            PAD, y, PW - PAD * 2, 24, TextAnchor.MiddleCenter);

        UpdateAllRows();
    }

    // ── UI Update ─────────────────────────────────────────────────────────────

    void UpdateAllRows()
    {
        for (int i = 0; i < Options.Length; i++)
            UpdateRow(i);
    }

    void UpdateRow(int i)
    {
        var def = Options[i];
        float val = _values[i];
        float t = Mathf.InverseLerp(def.min, def.max, val);

        // Value text
        _valueTexts[i].text = def.intOnly ? val.ToString("0") : val.ToString("0.00");

        // Bar fill width
        const int labelW = 220;
        const int valueW = 80;
        int barW = PW - PAD * 2 - labelW - valueW - 24;
        float barX = PAD + labelW + 8;
        int fillW = Mathf.Max(2, Mathf.RoundToInt(barW * t));
        SetRect(_barFills[i].gameObject, barX, GetBarY(i), fillW, 10);

        // Row highlight
        _rowBgs[i].color = (i == _sel) ? COL_SEL : ((i % 2 == 1) ? COL_ROW : COL_CLEAR);
    }

    float GetBarY(int i)
    {
        float y = -PAD - 36 - 6; // title + sep
        y -= ROW_H * i;
        return y - (ROW_H - 10) / 2;
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

        _root.transform.position = cam.transform.position + fwd * SPAWN_DIST;
        _root.transform.rotation = Quaternion.LookRotation(fwd);
    }

    // ── UI Helpers (same style as VRFileBrowser) ──────────────────────────────

    static void CacheFont()
    {
        if (_font != null) return;
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (_font == null) _font = Font.CreateDynamicFontFromOSFont("Arial", 14);
    }

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

    // ── XR Input Helpers ──────────────────────────────────────────────────────

    static bool ReadButton(XRNode node, InputFeatureUsage<bool> usage)
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devs);
        if (devs.Count > 0 && devs[0].TryGetFeatureValue(usage, out bool v))
            return v;
        return false;
    }

    static float ReadAxis1D(XRNode node, InputFeatureUsage<float> usage)
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devs);
        if (devs.Count > 0 && devs[0].TryGetFeatureValue(usage, out float v))
            return v;
        return 0f;
    }

    static float ReadNavigationY()
    {
        float ly = ReadStickAxis(XRNode.LeftHand, 1);
        float ry = ReadStickAxis(XRNode.RightHand, 1);
        return Mathf.Abs(ly) >= Mathf.Abs(ry) ? ly : ry;
    }

    static float ReadNavigationX()
    {
        float lx = ReadStickAxis(XRNode.LeftHand, 0);
        float rx = ReadStickAxis(XRNode.RightHand, 0);
        return Mathf.Abs(lx) >= Mathf.Abs(rx) ? lx : rx;
    }

    static float ReadStickAxis(XRNode node, int axis)
    {
        var devs = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devs);
        if (devs.Count > 0 && devs[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v))
            return axis == 0 ? v.x : v.y;
        return 0f;
    }
}
