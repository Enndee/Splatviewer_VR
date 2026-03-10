// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Cycles through .ply splat files in a folder using VR controller buttons.
///
/// VR Controls (right controller):
///   B (secondaryButton) → next splat file
///   A (primaryButton)   → previous splat file
///
/// Keyboard fallback:
///   PageDown / N → next
///   PageUp   / P → previous
/// </summary>
public class SplatCycler : MonoBehaviour
{
    [Header("Setup")]
    [Tooltip("The RuntimeSplatLoader to use for loading files.")]
    public RuntimeSplatLoader loader;

    [Tooltip("Folder to scan for .ply files. Leave empty to use a default path.")]
    public string splatFolder = "";

    [Tooltip("Auto-load the first file on start.")]
    public bool autoLoadFirst = true;

    [Header("Status (read-only)")]
    [SerializeField] string _currentFile = "(none)";
    [SerializeField] int _currentIndex = -1;
    [SerializeField] int _totalFiles;

    // Internal
    List<string> _files = new List<string>();
    bool _btnNextReady = true;
    bool _btnPrevReady = true;
    VRFileBrowser _browser;
    VRRig _rig;

    void Start()
    {
        if (loader == null)
            loader = GetComponent<RuntimeSplatLoader>();
        _browser = FindAnyObjectByType<VRFileBrowser>();
        _rig = FindAnyObjectByType<VRRig>();

        if (string.IsNullOrEmpty(splatFolder))
        {
            Debug.LogWarning("[SplatCycler] No splatFolder set. Assign a folder path in the Inspector.");
            return;
        }

        ScanFolder();

        if (autoLoadFirst && _files.Count > 0)
            LoadIndex(0);
    }

    void Update()
    {
        if (_files.Count == 0) return;

        if (XRSettings.isDeviceActive)
            HandleVRInput();
        else
            HandleKeyboardInput();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public string CurrentFileName => _currentFile;
    public int CurrentIndex => _currentIndex;
    public int TotalFiles => _totalFiles;
    public IReadOnlyList<string> Files => _files;

    /// <summary>Set the current index without loading (used by VRFileBrowser after direct load).</summary>
    public void SetIndex(int index)
    {
        if (index >= 0 && index < _files.Count)
        {
            _currentIndex = index;
            _currentFile = Path.GetFileName(_files[index]);
        }
    }

    public void LoadNext()
    {
        if (_files.Count == 0) return;
        int next = (_currentIndex + 1) % _files.Count;
        LoadIndex(next);
    }

    public void LoadPrevious()
    {
        if (_files.Count == 0) return;
        int prev = (_currentIndex - 1 + _files.Count) % _files.Count;
        LoadIndex(prev);
    }

    public void ScanFolder()
    {
        _files.Clear();
        _currentIndex = -1;
        _currentFile = "(none)";

        if (!Directory.Exists(splatFolder))
        {
            Debug.LogError($"[SplatCycler] Folder not found: {splatFolder}");
            _totalFiles = 0;
            return;
        }

        _files = Directory.GetFiles(splatFolder)
            .Where(f =>
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                return ext == ".ply" || ext == ".spz";
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _totalFiles = _files.Count;
        Debug.Log($"[SplatCycler] Found {_totalFiles} splat file(s) in: {splatFolder}");
    }

    public void LoadIndex(int index)
    {
        if (index < 0 || index >= _files.Count) return;

        string path = _files[index];
        Debug.Log($"[SplatCycler] Loading [{index + 1}/{_files.Count}]: {Path.GetFileName(path)}");

        if (loader.LoadFile(path))
        {
            _currentIndex = index;
            _currentFile = Path.GetFileName(path);
            if (_rig != null) _rig.ResetToSpawnPoint();
        }
    }

    // ── Input Handling ────────────────────────────────────────────────────────

    void HandleVRInput()
    {
        // Don't consume A/B when file browser is open (or just closed this frame)
        if (_browser != null && (_browser.IsOpen || _browser.WasOpenThisFrame)) return;

        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, devices);

        bool bPressed = false;
        bool aPressed = false;

        if (devices.Count > 0)
        {
            devices[0].TryGetFeatureValue(CommonUsages.secondaryButton, out bPressed); // B
            devices[0].TryGetFeatureValue(CommonUsages.primaryButton, out aPressed);   // A
        }

        // B → next (with debounce)
        if (bPressed && _btnNextReady)
        {
            LoadNext();
            _btnNextReady = false;
        }
        else if (!bPressed)
        {
            _btnNextReady = true;
        }

        // A → previous (with debounce)
        if (aPressed && _btnPrevReady)
        {
            LoadPrevious();
            _btnPrevReady = false;
        }
        else if (!aPressed)
        {
            _btnPrevReady = true;
        }
    }

    void HandleKeyboardInput()
    {
        if (Input.GetKeyDown(KeyCode.PageDown) || Input.GetKeyDown(KeyCode.N))
            LoadNext();
        if (Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.P))
            LoadPrevious();
    }
}
